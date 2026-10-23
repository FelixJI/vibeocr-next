"""ORT ownership and admission snapshots; native outputs are checked by smoke."""

from __future__ import annotations

import json
import threading
import time
from types import SimpleNamespace

import pytest
from vibeocr.runtime.recognition import paddle_onnx as onnx


def test_admission_snapshot_keeps_source_models_environment_and_device(monkeypatch):
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_ID", "first")
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_REVISION", "4")
    snapshot = onnx.freeze_request(
        {"model_preset": "tiny", "use_doc_orientation_classify": False},
        ("paddleocr-modelscope",),
    )
    monkeypatch.setenv("PADDLE_PDX_MODEL_SOURCE", "huggingface")
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_REVISION", "5")
    with onnx.request_snapshot(snapshot):
        current = onnx.current_snapshot({"model_preset": "medium"})
        assert current["model_preset"] == "tiny"
        assert current["source"] == "modelscope"
        assert current["environment_revision"] == "4"
        assert set(current["models"]) == {"PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec"}
        assert current["provider"] == "CPUExecutionProvider"
    assert onnx.current_snapshot()["environment_revision"] == "5"


def test_directml_uses_dxgi_hardware_ordinal_without_cuda(monkeypatch):
    from vibeocr.runtime.recognition import directml_devices

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-directml")
    monkeypatch.setattr(
        directml_devices,
        "hardware_adapters",
        lambda: [{"device_id": 2, "name": "hardware"}],
    )
    snapshot = onnx.freeze_request({})
    assert snapshot["device_id"] == 2
    assert snapshot["provider"] == "DmlExecutionProvider"
    assert snapshot["adapter"]["name"] == "hardware"


@pytest.mark.parametrize(
    "options,sources",
    [({"device": "cuda"}, ()), ({"model_preset": "v5"}, ()), ({}, ("paddleocr-bos",))],
)
def test_wrong_closure_or_source_fails_before_model_preparation(
    monkeypatch, options, sources
):
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    with pytest.raises(onnx.PaddleOnnxError):
        onnx.freeze_request(options, sources)


def test_constructor_signature_changes_with_real_model_and_auxiliary_selection(
    monkeypatch,
):
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    tiny = onnx.constructor_kwargs(
        SimpleNamespace(model_preset="tiny", use_doc_orientation_classify=False)
    )
    medium = onnx.constructor_kwargs(
        SimpleNamespace(model_preset="medium", use_doc_orientation_classify=True)
    )
    assert tiny != medium
    assert len(json.loads(tiny["_onnx_request"])["models"]) == 2
    assert len(json.loads(medium["_onnx_request"])["models"]) == 3


class Session:
    def __init__(self, provider="DmlExecutionProvider"):
        self.provider = provider
        self.fallback_disabled = False

    def get_providers(self):
        return [self.provider, "CPUExecutionProvider"]

    def get_provider_options(self):
        return {self.provider: {"device_id": "2"}}

    def get_session_options(self):
        return SimpleNamespace(enable_mem_pattern=False, execution_mode="sequential")

    def disable_fallback(self):
        self.fallback_disabled = True


def make_pipeline(provider="DmlExecutionProvider", count=5):
    runners = [
        SimpleNamespace(session=Session(provider), model_dir=f"model-{i}")
        for i in range(count)
    ]
    node = SimpleNamespace(
        text_det_model=SimpleNamespace(runner=runners[0]),
        text_rec_model=SimpleNamespace(runner=runners[1]),
    )
    if count == 5:
        node.textline_orientation_model = SimpleNamespace(runner=runners[2])
        node.doc_preprocessor_pipeline = SimpleNamespace(
            doc_ori_classify_model=SimpleNamespace(runner=runners[3]),
            doc_unwarping_model=SimpleNamespace(runner=runners[4]),
        )
    native = SimpleNamespace(
        paddlex_pipeline=SimpleNamespace(_pipeline=node), close=lambda: None
    )
    return native, runners


def test_all_auxiliary_sessions_disable_fallback_and_release(monkeypatch):
    monkeypatch.setitem(
        __import__("sys").modules,
        "onnxruntime",
        SimpleNamespace(
            get_available_providers=lambda: ["DmlExecutionProvider"],
            ExecutionMode=SimpleNamespace(ORT_SEQUENTIAL="sequential"),
        ),
    )
    native, runners = make_pipeline()
    pipeline = onnx.PaddleOnnxPipeline(
        native, {"provider": "DmlExecutionProvider", "models": dict.fromkeys(range(5))}
    )
    assert len(pipeline.sessions) == 5
    assert all(runner.session.fallback_disabled for runner in runners)
    pipeline.close()
    pipeline.close()
    assert all(runner.session is None for runner in runners)


def test_entirely_cpu_session_cannot_masquerade_as_gpu():
    native, runners = make_pipeline("CPUExecutionProvider")
    with pytest.raises(onnx.PaddleOnnxError, match="not activated"):
        onnx.PaddleOnnxPipeline(
            native,
            {"provider": "DmlExecutionProvider", "models": dict.fromkeys(range(5))},
        )
    assert all(runner.session is None for runner in runners)


def test_dml_run_is_serialized_until_generator_is_consumed(monkeypatch):
    monkeypatch.setitem(
        __import__("sys").modules,
        "onnxruntime",
        SimpleNamespace(
            get_available_providers=lambda: ["DmlExecutionProvider"],
            ExecutionMode=SimpleNamespace(ORT_SEQUENTIAL="sequential"),
        ),
    )
    native, _ = make_pipeline(count=2)
    active = 0
    maximum = 0

    def predict():
        nonlocal active, maximum
        active += 1
        maximum = max(maximum, active)
        time.sleep(0.02)
        yield "text"
        active -= 1

    native.predict = predict
    pipeline = onnx.PaddleOnnxPipeline(
        native, {"provider": "DmlExecutionProvider", "models": {"det": {}, "rec": {}}}
    )
    workers = [
        threading.Thread(target=lambda: list(pipeline.predict())) for _ in range(3)
    ]
    for worker in workers:
        worker.start()
    for worker in workers:
        worker.join(timeout=2)
        assert not worker.is_alive()
    assert maximum == 1
    pipeline.close()


@pytest.mark.parametrize(
    "variable,value",
    [
        ("VIBEOCR_MANAGED_ENVIRONMENT_ID", "replaced"),
        ("VIBEOCR_MANAGED_ENVIRONMENT_REVISION", "5"),
    ],
)
def test_changed_environment_rejects_before_preparing_models(
    monkeypatch, variable, value
):
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_ID", "original")
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_REVISION", "4")
    snapshot = onnx.freeze_request({})
    monkeypatch.setenv(variable, value)
    monkeypatch.setattr(
        onnx,
        "prepare_models",
        lambda *_args, **_kwargs: pytest.fail("must reject before preparation"),
    )
    with pytest.raises(onnx.PaddleOnnxError, match="environment snapshot changed"):
        onnx.create_pipeline(json.dumps(snapshot))


def test_local_only_onnx_missing_model_never_downloads_or_replaces_view(
    tmp_path, monkeypatch
):
    from vibeocr.runtime.environments import model_cache

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path / "private"))
    monkeypatch.setenv("VIBEOCR_SHARED_MODEL_CACHE", str(tmp_path / "shared"))
    snapshot = onnx.freeze_request({"use_doc_orientation_classify": False})
    current = tmp_path / "private/onnx/small" / snapshot["source"] / "current.json"
    current.parent.mkdir(parents=True)
    current.write_text('{"generation":"old-view"}', encoding="utf-8")
    monkeypatch.setattr(
        model_cache,
        "_download",
        lambda *_a, **_kw: pytest.fail("local-only must not download"),
    )
    with pytest.raises(model_cache.LocalModelsNotPrepared):
        onnx.create_pipeline(json.dumps(snapshot), local_models_only=True)
    assert current.read_text(encoding="utf-8") == '{"generation":"old-view"}'


def test_auxiliary_constructor_settings_follow_admission_snapshot(monkeypatch):
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    snapshot = onnx.freeze_request(
        {"use_doc_orientation_classify": False, "use_doc_unwarping": True}
    )
    with onnx.request_snapshot(snapshot):
        kwargs = onnx.constructor_kwargs(
            SimpleNamespace(use_doc_orientation_classify=True, use_doc_unwarping=False)
        )
    assert not kwargs["use_doc_orientation_classify"]
    assert kwargs["use_doc_unwarping"]
    monkeypatch.setattr(
        onnx,
        "prepare_models",
        lambda *_a, **_kw: pytest.fail("must not prepare changed auxiliary models"),
    )
    with pytest.raises(
        onnx.PaddleOnnxError, match="auxiliary selection snapshot changed"
    ):
        onnx.create_pipeline(json.dumps(snapshot), use_doc_orientation_classify=True)


def test_factory_explicitly_disables_unselected_auxiliary_models(tmp_path, monkeypatch):
    import sys

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path))
    snapshot = onnx.freeze_request({"use_doc_orientation_classify": False})
    monkeypatch.setattr(
        onnx, "prepare_models", lambda *_a, **_kw: SimpleNamespace(root=tmp_path)
    )
    monkeypatch.setitem(
        sys.modules,
        "onnxruntime",
        SimpleNamespace(get_available_providers=lambda: ["CPUExecutionProvider"]),
    )
    native, _ = make_pipeline("CPUExecutionProvider", count=2)
    observed = {}

    def factory(**kwargs):
        observed.update(kwargs)
        return native

    monkeypatch.setitem(sys.modules, "paddleocr", SimpleNamespace(PaddleOCR=factory))
    pipeline = onnx.create_pipeline(json.dumps(snapshot))
    assert observed["engine"] == "onnxruntime"
    assert not observed["use_doc_orientation_classify"]
    assert not observed["use_doc_unwarping"]
    assert not observed["use_textline_orientation"]
    assert observed["engine_config"]["providers"] == ["CPUExecutionProvider"]
    pipeline.close()
