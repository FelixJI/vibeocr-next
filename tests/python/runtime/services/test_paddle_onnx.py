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


@pytest.mark.parametrize(
    "recipe,device",
    [("cpu", "cpu"), ("cuda", "cuda"), ("cuda", "cpu"), ("directml", "directml")],
)
def test_factory_cuda_layout_is_shared_by_all_models_and_isolated_from_other_devices(
    tmp_path, monkeypatch, recipe, device
):
    import sys

    from vibeocr.runtime.recognition import directml_devices, rapid_devices

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", f"paddleocr-ort-{recipe}")
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path))
    monkeypatch.setattr(
        directml_devices, "hardware_adapters", lambda: [{"device_id": 2}]
    )
    monkeypatch.setattr(rapid_devices, "setup_cuda_plugin", lambda: device == "cuda")
    snapshot = onnx.freeze_request(
        {"device": device, "use_doc_unwarping": True, "use_textline_orientation": True}
    )
    provider = snapshot["provider"]
    monkeypatch.setattr(
        onnx, "prepare_models", lambda *_a, **_kw: SimpleNamespace(root=tmp_path)
    )
    monkeypatch.setitem(
        sys.modules,
        "onnxruntime",
        SimpleNamespace(
            get_available_providers=lambda: [provider],
            ExecutionMode=SimpleNamespace(ORT_SEQUENTIAL="sequential"),
        ),
    )
    native, runners = make_pipeline(provider)
    observed = {}

    def factory(**kwargs):
        observed.update(kwargs)
        return native

    monkeypatch.setitem(sys.modules, "paddleocr", SimpleNamespace(PaddleOCR=factory))
    pipeline = onnx.create_pipeline(json.dumps(snapshot))
    config = observed["engine_config"]
    expected = {} if device == "cpu" else {"device_id": str(snapshot["device_id"])}
    if device == "cuda":
        expected["prefer_nhwc"] = "1"
    assert config["provider_options"] == [expected]
    assert len(pipeline.sessions) == 5
    assert all(runner.session.fallback_disabled for runner in runners)
    if device == "directml":
        assert config["enable_mem_pattern"] is False
        assert config["execution_mode"] == "sequential"
    pipeline.close()
    assert all(runner.session is None for runner in runners)


@pytest.mark.parametrize("pinned", [False, True])
@pytest.mark.parametrize(
    "changed", [dict(model_preset="medium"), dict(use_textline_orientation=True)]
)
def test_real_cache_replacement_closes_old_sessions_and_preserves_current_lease(
    monkeypatch, pinned, changed
):
    from dataclasses import replace

    from vibeocr.runtime.recognition import core
    from vibeocr.runtime.recognition.core.pipelines import get_registry
    from vibeocr.runtime.recognition.core.pipelines.registry import PipelineRegistry
    from vibeocr.runtime.recognition.models.ocr_options import OCROptions
    from vibeocr.runtime.recognition.ocr_service import OCRService
    from vibeocr.runtime.recognition.pipeline_cache_manager import PipelineCacheManager

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    monkeypatch.setitem(
        __import__("sys").modules,
        "onnxruntime",
        SimpleNamespace(get_available_providers=lambda: ["CPUExecutionProvider"]),
    )
    service = object.__new__(OCRService)
    service._pipelines = {}
    service._pipeline_signatures = {}
    service._lock = threading.Lock()
    manager = PipelineCacheManager(service, {"OCR": 1}, max_heavy=1, tick_interval=0.01)
    service._cache_manager = manager
    if pinned:
        manager.configure_residency(
            default_ttl_seconds=1,
            pipelines=[SimpleNamespace(name="OCR", pinned=True, ttl_seconds=1)],
        )
    created = []
    previous_runners = []
    predicting = threading.Event()
    finish = threading.Event()

    def factory(_device, **kwargs):
        if previous_runners:
            assert all(runner.session is None for runner in previous_runners[-1])
        assert manager.status()["pinned_pipelines"] == (["OCR"] if pinned else [])
        assert manager._active_counts == {"OCR": 1}
        snapshot = json.loads(kwargs["_onnx_request"])
        native, runners = make_pipeline("CPUExecutionProvider", count=2)
        # The current snapshots use 2/3/4 runners, so model exactly their selected owners.
        nodes = native.paddlex_pipeline._pipeline
        if "PP-LCNet_x1_0_doc_ori" in snapshot["models"]:
            runner = SimpleNamespace(
                session=Session("CPUExecutionProvider"), model_dir="docori"
            )
            runners.append(runner)
            nodes.doc_preprocessor_pipeline = SimpleNamespace(
                doc_ori_classify_model=SimpleNamespace(runner=runner)
            )
        if "PP-LCNet_x0_25_textline_ori" in snapshot["models"]:
            runner = SimpleNamespace(
                session=Session("CPUExecutionProvider"), model_dir="lineori"
            )
            runners.append(runner)
            nodes.textline_orientation_model = SimpleNamespace(runner=runner)

        def predict():
            predicting.set()
            assert finish.wait(2)
            yield "text"

        native.predict = predict
        wrapped = onnx.PaddleOnnxPipeline(native, snapshot)
        created.append(wrapped)
        previous_runners.append(runners)
        return wrapped

    registry = PipelineRegistry()
    registry.register(replace(get_registry().get("OCR"), create_pipeline=factory))
    monkeypatch.setattr(core.pipelines, "get_registry", lambda: registry)
    try:
        with manager.lease("OCR"):
            service.get_or_create_pipeline(
                "OCR", OCROptions(use_doc_orientation_classify=False)
            )
        with manager.lease("OCR"):
            options = OCROptions(use_doc_orientation_classify=False, **changed)
            current = service.get_or_create_pipeline("OCR", options)
            assert created[0].pipeline is None
            assert manager.status()["active_counts"] == {"OCR": 1}
            # Force an expired idle timestamp while the actual generator holds its run lock.
            manager.touch("OCR", now=time.time() - 60)
            worker = threading.Thread(target=lambda: list(current.predict()))
            worker.start()
            assert predicting.wait(1)
            evicted = []
            eviction = threading.Thread(
                target=lambda: evicted.extend(manager.evict_idle(now=time.time() + 60))
            )
            eviction.start()
            eviction.join(timeout=0.5)
            assert not eviction.is_alive(), "TTL attempted to close an active session"
            status = []
            query = threading.Thread(target=lambda: status.append(manager.status()))
            query.start()
            query.join(timeout=0.5)
            assert not query.is_alive(), (
                "status blocked behind active prediction disposal"
            )
            assert status[0]["active_counts"] == {"OCR": 1}
            assert not evicted
            assert current.pipeline is not None
            finish.set()
            worker.join(timeout=1)
            assert not worker.is_alive()
        completed_at = manager.get_last_used("OCR")
        assert completed_at is not None and completed_at >= time.time() - 1
        assert manager.evict_idle(now=completed_at + 0.5) == []
        if pinned:
            assert manager.evict_idle(now=completed_at + 2) == []
            assert manager.status()["pinned_pipelines"] == ["OCR"]
        else:
            assert manager.evict_idle(now=completed_at + 2) == ["OCR"]
    finally:
        finish.set()
        manager.shutdown()
        manager.release(heavy_only=False, force=True)


@pytest.mark.parametrize("failure", ["missing", "rejected"])
def test_cuda_plugin_failure_is_typed_before_any_factory_or_session(
    tmp_path, monkeypatch, failure
):
    import sys

    from vibeocr.runtime.recognition import rapid_devices

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cuda")
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path))
    plugin = tmp_path / "incompatible.dll"
    if failure == "rejected":
        plugin.write_bytes(b"")
    monkeypatch.setenv("VIBEOCR_ORT_CUDA_EP_DLL", str(plugin))
    monkeypatch.delenv("VIBEOCR_RUNTIME_ROOT", raising=False)
    monkeypatch.setattr(rapid_devices, "_state", {})
    monkeypatch.setenv("PATH", rapid_devices.os.environ.get("PATH", ""))
    # The native loader is exercised separately; this unit seam needs no CUDA DLLs.
    monkeypatch.setattr(
        rapid_devices,
        "_nvidia_dll_dirs",
        lambda: ({"unused.dll": tmp_path / "unused.dll"}, []),
    )
    monkeypatch.setattr(
        onnx, "prepare_models", lambda *_a, **_kw: SimpleNamespace(root=tmp_path)
    )
    calls = {"register": 0, "factory": 0, "session": 0}

    def register(_name, path):
        calls["register"] += 1
        assert path == str(plugin)
        raise RuntimeError("incompatible binary fixture")

    def factory(**_kwargs):
        calls["factory"] += 1
        pytest.fail("rejected CUDA must not construct a CPU or Paddle pipeline")

    def session(*_args, **_kwargs):
        calls["session"] += 1
        pytest.fail("rejected CUDA must not construct any ORT session")

    monkeypatch.setitem(
        sys.modules,
        "onnxruntime",
        SimpleNamespace(
            register_execution_provider_library=register,
            get_available_providers=lambda: ["CPUExecutionProvider"],
            InferenceSession=session,
        ),
    )
    monkeypatch.setitem(sys.modules, "paddleocr", SimpleNamespace(PaddleOCR=factory))
    snapshot = onnx.freeze_request(
        {"device": "cuda", "use_doc_orientation_classify": False}
    )
    with pytest.raises(onnx.PaddleOnnxError, match="CUDA DLL/plugin unavailable"):
        onnx.create_pipeline(json.dumps(snapshot), local_models_only=True)
    assert calls == {
        "register": int(failure == "rejected"),
        "factory": 0,
        "session": 0,
    }
    assert rapid_devices._state == {"failed": True}


def test_directml_absent_hardware_rejects_admission_before_model_preparation(
    monkeypatch,
):
    from vibeocr.runtime.recognition import directml_devices

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-directml")
    monkeypatch.setattr(directml_devices, "hardware_adapters", lambda: [])
    monkeypatch.setattr(
        onnx,
        "prepare_models",
        lambda *_a, **_kw: pytest.fail("missing hardware must not prepare models"),
    )
    with pytest.raises(onnx.PaddleOnnxError, match="hardware adapter unavailable"):
        onnx.freeze_request({"device": "directml"})
