"""Paddle worker failures cannot replay inference or return stale responses."""

import subprocess
import sys
from pathlib import Path
from unittest.mock import MagicMock

import pytest
from vibeocr.runtime.recognition.paddle_process_adapter import (
    PaddleProcessAdapter,
    paddle_python,
)


def test_worker_timeout_terminates_process_and_discards_late_reply():
    adapter = PaddleProcessAdapter(Path(sys.executable))
    process = MagicMock()
    process.poll.return_value = None
    adapter._process = process
    with pytest.raises(TimeoutError, match="deadline"):
        adapter._exchange({"operation": "status"}, timeout=0.001)
    process.stdin.close.assert_called_once()
    assert adapter._process is None


def test_worker_exit_is_failure_not_replayed():
    adapter = PaddleProcessAdapter(Path(sys.executable))
    process = MagicMock()
    process.poll.return_value = 1
    adapter._process = process
    adapter._responses.put(None)
    with pytest.raises(RuntimeError, match="exited"):
        adapter._exchange({"operation": "recognize"})
    process.stdin.write.assert_called_once()
    assert adapter._process is None


def test_worker_model_error_is_not_reported_as_payload():
    adapter = PaddleProcessAdapter(Path(sys.executable))
    process = MagicMock()
    adapter._process = process
    adapter._responses.put('{"error":"model unavailable","error_type":"RuntimeError"}')
    with pytest.raises(RuntimeError, match="model unavailable"):
        adapter._exchange({"operation": "recognize"})
    process.stdin.write.assert_called_once()


def test_worker_preserves_local_model_failure_for_deterministic_job_feedback():
    from vibeocr.runtime.environments.model_cache import LocalModelsNotPrepared

    adapter = PaddleProcessAdapter(Path(sys.executable))
    process = MagicMock()
    adapter._process = process
    adapter._responses.put(
        '{"error":"not locally prepared","error_type":"LocalModelsNotPrepared"}'
    )
    with pytest.raises(LocalModelsNotPrepared):
        adapter._exchange({"operation": "recognize"})
    process.stdin.write.assert_called_once()


def test_shutdown_escalates_only_when_worker_does_not_exit():
    adapter = PaddleProcessAdapter(Path(sys.executable))
    process = MagicMock()
    process.poll.return_value = None
    process.wait.side_effect = [subprocess.TimeoutExpired("worker", 5), 0]
    adapter._process = process
    adapter.close()
    process.terminate.assert_called_once()
    process.kill.assert_not_called()
    assert adapter._process is None


def test_routing_batch_argument_reaches_isolated_worker(monkeypatch):
    adapter = PaddleProcessAdapter(Path(sys.executable))
    request = MagicMock(return_value=[])
    monkeypatch.setattr(adapter, "_request", request)
    assert adapter.recognize_many([], compute_batch=4) == []
    assert request.call_args.args == ("recognize",)


def test_broken_worker_pipe_discards_process():
    adapter = PaddleProcessAdapter(Path(sys.executable))
    process = MagicMock()
    process.poll.return_value = 1
    process.stdin.write.side_effect = BrokenPipeError()
    adapter._process = process
    with pytest.raises(BrokenPipeError):
        adapter._exchange({"operation": "recognize"})
    assert adapter._process is None


@pytest.mark.parametrize(
    "recipe", ["paddleocr-ort-cpu", "paddleocr-ort-cuda", "paddleocr-ort-directml"]
)
def test_managed_paddle_worker_uses_current_code_and_same_venv(monkeypatch, recipe):
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", recipe)
    monkeypatch.setenv("VIBEOCR_PRODUCT_CODE_ROOT", "C:\\product\\runtime-code")
    monkeypatch.setenv("PYTHONPATH", "C:\\untrusted")
    assert paddle_python() == Path(sys.executable)
    observed = {}

    def stop_before_start(args, **kwargs):
        observed.update(args=args, env=kwargs["env"])
        raise RuntimeError("captured")

    monkeypatch.setattr(
        "vibeocr.runtime.recognition.paddle_process_adapter.subprocess.Popen",
        stop_before_start,
    )
    with pytest.raises(RuntimeError, match="captured"):
        PaddleProcessAdapter(Path(sys.executable))._start()
    assert observed["args"][:4] == [sys.executable, "-I", "-B", "-c"]
    assert "vibeocr.runtime.recognition.paddle_worker" in observed["args"][4]
    assert "PYTHONPATH" not in observed["env"]


@pytest.mark.parametrize(
    "reply",
    [None, "not-json", '{"error":"out of memory","error_type":"PaddleOnnxError"}'],
)
def test_onnx_worker_native_or_wire_failure_is_typed_and_discards_worker(
    monkeypatch, reply
):
    from vibeocr.runtime.recognition.paddle_onnx import PaddleOnnxError

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    adapter = PaddleProcessAdapter(Path(sys.executable))
    process = MagicMock()
    process.poll.return_value = None
    adapter._process = process
    adapter._responses.put(reply)
    with pytest.raises(PaddleOnnxError):
        adapter._exchange({"operation": "recognize"})
    process.stdin.write.assert_called_once()
    assert adapter._process is None


def test_onnx_cancellation_stops_worker_and_ignores_late_reply(monkeypatch):
    from vibeocr.runtime.recognition.paddle_onnx import (
        PaddleOnnxCancelled,
        request_snapshot,
    )

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    adapter = PaddleProcessAdapter(Path(sys.executable))
    process = MagicMock()
    process.poll.return_value = None
    adapter._process = process
    adapter._responses.put('{"result": "late"}')
    with (
        request_snapshot(None, cancelled=lambda: True),
        pytest.raises(PaddleOnnxCancelled),
    ):
        adapter._exchange({"operation": "recognize"})
    assert adapter._process is None
    assert adapter._responses.empty()


@pytest.mark.parametrize("mode", ["TABLE_RECOGNITION", "PP-StructureV3"])
@pytest.mark.parametrize("frozen_job", [False, True])
def test_retained_worker_request_preserves_pipeline_id_and_frozen_selection(
    monkeypatch, mode, frozen_job
):
    from contextlib import nullcontext

    from vibeocr.runtime.recognition.paddle_onnx import freeze_request, request_snapshot
    from vibeocr.runtime_contracts import PipelineSelection

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    monkeypatch.setenv("PADDLE_PDX_MODEL_SOURCE", "huggingface")
    values = {"model_preset": "tiny", "use_doc_unwarping": True}
    selection = PipelineSelection(mode, options=values)
    frozen = freeze_request(values, pipeline_id=mode)
    adapter = PaddleProcessAdapter(Path(sys.executable))
    request = MagicMock(return_value=[])
    monkeypatch.setattr(adapter, "_request", request)
    with request_snapshot(frozen) if frozen_job else nullcontext():
        assert adapter.recognize_many([], options=selection) == []
    sent = request.call_args.kwargs
    assert sent["snapshot"]["pipeline_id"] == mode
    assert sent["snapshot"]["selections"] == frozen["selections"]
    assert sent["snapshot"]["models"] == frozen["models"]
    assert sent["options"] == selection.to_payload()
    assert "pipeline" not in values
    if frozen_job:
        assert sent["snapshot"] is frozen


def test_worker_request_rejects_a_different_pipeline_than_frozen_job(monkeypatch):
    from vibeocr.runtime.recognition.paddle_onnx import (
        PaddleOnnxError,
        freeze_request,
        request_snapshot,
    )
    from vibeocr.runtime_contracts import PipelineSelection

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    adapter = PaddleProcessAdapter(Path(sys.executable))
    request = MagicMock(return_value=[])
    monkeypatch.setattr(adapter, "_request", request)
    with request_snapshot(freeze_request({}, pipeline_id="TABLE_RECOGNITION")):
        with pytest.raises(PaddleOnnxError, match="snapshot changed"):
            adapter.recognize_many([], options=PipelineSelection("PP-StructureV3"))
    request.assert_not_called()


@pytest.mark.parametrize("recipe", ["paddleocr-cpu", "paddleocr-cuda", "rapidocr-cpu"])
def test_native_or_other_recipes_do_not_select_paddle_worker(monkeypatch, recipe):
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", recipe)
    assert paddle_python() is None
