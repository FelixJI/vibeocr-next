"""ORT ownership and admission snapshots; native outputs are checked by smoke."""

from __future__ import annotations

import json
import threading
import time
from types import SimpleNamespace

import pytest
from vibeocr.runtime.recognition import paddle_onnx as onnx


@pytest.fixture(autouse=True)
def runner_constructor_seam(monkeypatch):
    import sys

    class Runner:
        def __init__(self):
            pass

    monkeypatch.setitem(
        sys.modules,
        "paddlex.inference.models.runners.onnxruntime_runner",
        SimpleNamespace(ONNXRuntimeRunner=Runner),
    )


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


@pytest.mark.parametrize(
    "pipeline_id,wireless",
    [("TABLE_RECOGNITION", "SLANeXt_wireless"), ("PP-StructureV3", "SLANeXt_wireless")],
)
def test_retained_graph_prepares_every_role_without_retired_assets(
    monkeypatch, pipeline_id, wireless
):
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    snapshot = onnx.freeze_request(
        {
            "use_doc_orientation_classify": False,
            "use_textline_orientation": pipeline_id == "PP-StructureV3",
        },
        pipeline_id=pipeline_id,
    )
    assert {
        "PP-DocLayout_plus-L",
        "PP-LCNet_x1_0_doc_ori",
        "PP-LCNet_x1_0_table_cls",
        "SLANeXt_wired",
        wireless,
        "RT-DETR-L_wired_table_cell_det",
        "RT-DETR-L_wireless_table_cell_det",
    } <= snapshot["models"].keys()
    if pipeline_id == "PP-StructureV3":
        assert {"PP-DocBlockLayout", "PP-LCNet_x1_0_textline_ori"} <= snapshot[
            "models"
        ].keys()
    assert not any(
        "seal" in name.lower() or "formula" in name.lower() or "chart" in name.lower()
        for name in snapshot["models"]
    )
    with onnx.request_snapshot(snapshot):
        kwargs = onnx.constructor_kwargs(SimpleNamespace(pipeline=pipeline_id))
    assert kwargs["use_doc_orientation_classify"] is False
    assert "PP-LCNet_x1_0_doc_ori" in json.loads(kwargs["_onnx_request"])["models"]


def test_failed_partial_constructor_releases_only_its_thread_owned_sessions(
    tmp_path, monkeypatch
):
    import sys

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path))
    monkeypatch.setattr(
        onnx, "prepare_models", lambda *_a, **_kw: SimpleNamespace(root=tmp_path)
    )
    monkeypatch.setitem(
        sys.modules,
        "onnxruntime",
        SimpleNamespace(get_available_providers=lambda: ["CPUExecutionProvider"]),
    )

    class Runner:
        def __init__(self):
            self.session = object()

    monkeypatch.setitem(
        sys.modules,
        "paddlex.inference.models.runners.onnxruntime_runner",
        SimpleNamespace(ONNXRuntimeRunner=Runner),
    )
    own, foreign = [], []

    def factory(**_kwargs):
        own.append(Runner())
        thread = threading.Thread(target=lambda: foreign.append(Runner()))
        thread.start()
        thread.join(timeout=2)
        assert not thread.is_alive()
        raise MemoryError("partial graph allocation")

    monkeypatch.setitem(sys.modules, "paddleocr", SimpleNamespace(PaddleOCR=factory))
    original = Runner.__init__
    with pytest.raises(onnx.PaddleOnnxError, match="partial graph"):
        onnx.create_pipeline(json.dumps(onnx.freeze_request({})))
    assert own[0].session is None
    assert foreign[0].session is not None
    assert Runner.__init__ is original


def test_graph_validation_counts_duplicate_model_sessions_and_closes_all_on_mismatch(
    monkeypatch,
):
    import sys

    monkeypatch.setitem(
        sys.modules,
        "onnxruntime",
        SimpleNamespace(get_available_providers=lambda: ["CPUExecutionProvider"]),
    )
    native, runners = make_pipeline("CPUExecutionProvider", count=2)
    runners[0].model_dir = runners[1].model_dir = "shared-asset"
    owned = onnx.PaddleOnnxPipeline(
        native,
        {"models": {"shared-asset": {}}, "provider": "CPUExecutionProvider"},
        ["shared-asset", "shared-asset"],
    )
    assert len(owned.sessions) == 2
    owned.close()
    assert all(r.session is None for r in runners)
    native, runners = make_pipeline("CPUExecutionProvider", count=2)
    with pytest.raises(onnx.PaddleOnnxError, match="count mismatch"):
        onnx.PaddleOnnxPipeline(
            native, {"models": {}, "provider": "CPUExecutionProvider"}, ["model-0"]
        )
    assert all(r.session is None for r in runners)


def test_missing_table_layout_keeps_baseline_table_and_non_table_outputs():
    import numpy as np

    table = {"cls_id": 8, "score": 0.9, "coordinate": [10, 20, 90, 80]}
    image = {"cls_id": 1, "score": 0.8, "coordinate": [0, 90, 100, 150]}
    original = [[table, image]]
    calls = []

    def upstream(*args, **kwargs):
        calls.append(kwargs)
        return original

    wrapper = onnx._MissingTableLayoutPostprocess(upstream)
    assert wrapper([{"boxes": np.array([[8, 0.1, 0, 0, 100, 200]])}], [{}]) is original
    assert len(calls) == 1


def test_missing_table_layout_preserves_non_table_and_only_uses_model_candidates():
    import numpy as np

    text = {"cls_id": 2, "score": 0.6, "coordinate": [0, 0, 10, 10]}
    table = {"cls_id": 8, "score": 0.15, "coordinate": [0, 0, 10, 10]}
    calls = []

    def upstream(outputs, datas, **kwargs):
        calls.append(kwargs)
        return [[text]] if len(calls) == 1 else [[table]]

    raw = np.array([[2, 0.6, 0, 0, 10, 10], [8, 0.15, 0, 0, 10, 10]])
    saved = raw.copy()
    result = onnx._MissingTableLayoutPostprocess(upstream)(
        [{"boxes": raw}], [{}], threshold={2: 0.4, 8: 0.5}, layout_nms=True
    )
    assert result == [[text, table]] and result[0][0] is text
    assert calls[0]["threshold"] == {2: 0.4, 8: 0.5}
    assert calls[1]["layout_merge_bboxes_mode"] == "large"
    np.testing.assert_array_equal(raw, saved)


def test_table_materialization_activates_predictor_nms_despite_upstream_config_gap():
    layout = SimpleNamespace(
        post_op=lambda *args, **kwargs: [],
        layout_nms=None,
        layout_merge_bboxes_mode=None,
    )
    root = SimpleNamespace(
        layout_det_model=layout,
        trans_cells_det_results_to_html=lambda cells: cells,
        general_ocr_pipeline=object(),
    )
    onnx._materialize_table_nodes(
        SimpleNamespace(paddlex_pipeline=root),
        {
            "pipeline_id": "TABLE_RECOGNITION",
            "selections": {"use_table_orientation_classify": False},
        },
    )
    assert layout.layout_nms is True
    assert layout.layout_merge_bboxes_mode == "union"


@pytest.mark.parametrize("angle", [0, 90, 180, 270, 13])
def test_doc_rotation_is_lossless_scoped_and_keeps_single_preprocessing(
    monkeypatch, angle
):
    import functools
    import sys

    import numpy as np

    calls = []

    def upstream_rotate(image, rotation):
        calls.append(("upstream_rotate", rotation))
        return image + 1

    def orientation(value):
        calls.append(("orientation", value))
        return value

    def unwarp(image):
        calls.append(("unwarp", image.shape))
        return image

    namespace = {
        "rotate_image": upstream_rotate,
        "orientation": orientation,
        "unwarp": unwarp,
    }
    exec(
        "class Preprocessor:\n"
        " def predict(self, image, angle=0, *, source='original'):\n"
        "  angle = orientation(angle)\n"
        "  rotated = rotate_image(image, angle)\n"
        "  yield {'angle': angle, 'input_img': image, 'output_img': unwarp(rotated), 'source': source}\n",
        namespace,
    )
    cls = namespace["Preprocessor"]
    wrapped = []

    def timeit(function):
        wrapped.append(function)

        @functools.wraps(function)
        def measured(*args, **kwargs):
            calls.append(("timer", True))
            yield from function(*args, **kwargs)

        return measured

    cls.predict = timeit(cls.predict)
    method_before = cls.predict
    monkeypatch.setitem(
        sys.modules,
        "paddlex.inference.pipelines.doc_preprocessor.pipeline",
        SimpleNamespace(_DocPreprocessorPipeline=cls, rotate_image=upstream_rotate),
    )
    monkeypatch.setitem(
        sys.modules,
        "paddlex.inference.utils.benchmark",
        SimpleNamespace(benchmark=SimpleNamespace(timeit=timeit)),
    )
    owned, other = cls(), cls()
    onnx._install_lossless_doc_rotation(owned)
    image = np.arange(2 * 5 * 3, dtype=np.uint8).reshape(2, 5, 3)
    result = next(owned.predict(image, angle, source="page-2"))
    assert result["source"] == "page-2" and result["angle"] == angle
    assert result["input_img"] is image
    expected = np.rot90(image, angle // 90) if angle % 90 == 0 else image + 1
    np.testing.assert_array_equal(result["output_img"], expected)
    assert result["output_img"].flags.c_contiguous
    assert [name for name, _ in calls].count("orientation") == 1
    assert [name for name, _ in calls].count("unwarp") == 1
    assert [name for name, _ in calls].count("timer") == 1
    assert len(wrapped) == 2
    assert namespace["rotate_image"] is upstream_rotate
    assert cls.predict is method_before and other.predict.__func__ is method_before
    np.testing.assert_array_equal(
        next(other.predict(image, angle))["output_img"], image + 1
    )
    with pytest.raises(onnx.PaddleOnnxError, match="Unsupported PaddleX"):
        onnx._install_lossless_doc_rotation(SimpleNamespace(predict=lambda: None))


def test_structure_config_replaces_wireless_weight_before_model_admission(
    monkeypatch, tmp_path
):
    import sys

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    snapshot = onnx.freeze_request({}, pipeline_id="PP-StructureV3")
    monkeypatch.setitem(
        sys.modules,
        "paddlex.inference",
        SimpleNamespace(
            load_pipeline_config=lambda _name: {
                "pipeline_name": "PP-StructureV3",
                "SubModules": {
                    "WirelessTableStructureRecognition": {"model_name": "SLANet_plus"},
                    "WiredTableStructureRecognition": {"model_name": "SLANeXt_wired"},
                },
            }
        ),
    )
    config, expected = onnx._structure_config(snapshot, tmp_path)
    assert expected == ["SLANeXt_wireless", "SLANeXt_wired"]
    assert config["SubModules"]["WirelessTableStructureRecognition"][
        "model_dir"
    ] == str(tmp_path / "SLANeXt_wireless")
    assert "SLANet_plus" not in snapshot["models"]
    assert "SLANet_plus" not in onnx.model_catalog()
    assert len(onnx.model_catalog()) == 17


def test_structure_predict_uses_both_official_wireless_models_by_default(monkeypatch):
    import sys

    monkeypatch.setitem(
        sys.modules,
        "onnxruntime",
        SimpleNamespace(get_available_providers=lambda: ["CPUExecutionProvider"]),
    )
    native, _runners = make_pipeline("CPUExecutionProvider", count=2)
    native.predict = lambda **kwargs: iter((kwargs,))
    pipeline = onnx.PaddleOnnxPipeline(
        native,
        {
            "provider": "CPUExecutionProvider",
            "models": {"det": {}, "rec": {}},
            "pipeline_id": "PP-StructureV3",
        },
    )
    try:
        options = next(pipeline.predict(input="fixture.png"))
        assert options["use_e2e_wireless_table_rec_model"] is False
        assert options["use_wireless_table_cells_trans_to_html"] is False
    finally:
        pipeline.close()
