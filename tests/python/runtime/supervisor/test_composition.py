"""Tests for the composition root: build_supervisor + adapter builders.

The composition root wires the SupervisorModule with real or null executors.
Most ``_build_*`` helpers lazily import heavy services (OCRService,
MinerUService, PdfBackendClient); we mock those modules so the wiring logic
is covered without pulling paddle/torch/mineru.
"""

from __future__ import annotations

import stat
import sys
import types
from typing import TYPE_CHECKING, Any

import pytest
from vibeocr.runtime.host import composition
from vibeocr.runtime.host.composition import (
    _mineru_available,
    _MinerUServiceLifecycle,
    _NullExecutor,
    _paddle_available,
    build_supervisor,
)
from vibeocr.runtime.jobs.staging import ManagedStaging

if TYPE_CHECKING:
    from os import stat_result
    from pathlib import Path

    from vibeocr.runtime_contracts import ResidencyStatus, SettingsSnapshot


# ---------------------------------------------------------------------------
# _NullExecutor
# ---------------------------------------------------------------------------


def _record_factory(monkeypatch, *, state) -> Any:
    """Build a minimal record-like object exposing the surface _NullExecutor uses."""
    calls: list[str] = []

    class _Rec:
        def __init__(self) -> None:
            self.state = state

        def transition(self, target):  # type: ignore[no-untyped-def]
            calls.append(
                f"transition:{target.value if hasattr(target, 'value') else target}"
            )
            self.state = target

        def append_event(self, stage, *, detail=None):  # type: ignore[no-untyped-def]
            calls.append(f"event:{stage}")

    return _Rec(), calls


def test_null_executor_fails_non_terminal_job() -> None:
    from vibeocr.runtime_contracts import JobState

    rec, calls = _record_factory(None, state=JobState.QUEUED)
    _NullExecutor().execute(rec, [])
    assert any("transition:failed" in c for c in calls)
    assert any("event:no_backend" in c for c in calls)


def test_null_executor_skips_already_terminal_job() -> None:
    from vibeocr.runtime_contracts import JobState

    rec, calls = _record_factory(None, state=JobState.COMPLETED)
    _NullExecutor().execute(rec, [])
    assert calls == []


def test_null_executor_passthrough_methods_return_defaults() -> None:
    from vibeocr.runtime_contracts import CancelMode, ResidencyStatus, SettingsSnapshot

    ex = _NullExecutor()
    assert ex.cancel_mode_for(None) is CancelMode.COOPERATIVE
    assert ex.residency_status() == ResidencyStatus()
    assert ex.release_idle() == ResidencyStatus()
    assert ex.release_idle("OCR") == ResidencyStatus()
    assert ex.preload(("OCR",)) == ResidencyStatus()
    snap = SettingsSnapshot(default_ttl_seconds=600)
    configured = ex.configure_settings(snap)
    assert configured.default_ttl_seconds == 600
    ex.close()  # no-op, must not raise


def test_null_executor_swallows_transition_error(monkeypatch) -> None:
    """The defensive except in execute() must not leak a transition failure."""
    from vibeocr.runtime_contracts import JobState

    class _BrokenRec:
        state = JobState.QUEUED

        def transition(self, target):  # type: ignore[no-untyped-def]
            raise RuntimeError("boom")

        def append_event(self, *a, **k):  # type: ignore[no-untyped-def]
            raise RuntimeError("boom")

    # Must not raise.
    _NullExecutor().execute(_BrokenRec(), [])


# ---------------------------------------------------------------------------
# build_supervisor — null path
# ---------------------------------------------------------------------------


def test_default_staging_cleans_owned_crashes_and_preserves_live_instances(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")
    monkeypatch.setenv("VIBEOCR_RUNTIME_STATE_ROOT", str(tmp_path))
    live, _ = build_supervisor(instance_id="sup-123456789a", executor=_NullExecutor())
    root = live.stager.root
    assert root.parent == tmp_path / "vibeocr-next-supervisor-staging"
    assert root.name.startswith("instance-sup-123456789a-")
    asset = live.stager.stage_job("live", [("input.pdf", None, b"pdf")])[0].path
    crashed = ManagedStaging.create(root.parent)
    results = crashed.root / "job" / "results"
    results.mkdir(parents=True)
    (results / "image.png").write_bytes(b"retained result")
    crashed._lease.release()  # OS releases the lease even when shutdown is skipped.
    unknown = root.parent / ("instance-" + "f" * 32)
    unknown.mkdir()
    legacy = tmp_path / "vibeocr-sup-old"
    legacy.mkdir()

    next_module, _ = build_supervisor(executor=_NullExecutor())
    try:
        assert asset.read_bytes() == b"pdf"
        assert not crashed.root.exists()
        assert not crashed.root.with_suffix(".lease").exists()
        assert unknown.is_dir()
        assert legacy.is_dir()
    finally:
        next_module.shutdown_now()
        live.shutdown_now()
    assert not root.exists()
    assert not root.with_suffix(".lease").exists()


def test_default_staging_releases_ownership_when_module_initialization_fails(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    class BrokenExecutor(_NullExecutor):
        def configure_settings(self, snapshot: SettingsSnapshot) -> ResidencyStatus:
            raise RuntimeError("configure failed")

    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")
    monkeypatch.setenv("VIBEOCR_RUNTIME_STATE_ROOT", str(tmp_path))
    with pytest.raises(RuntimeError, match="configure failed"):
        build_supervisor(executor=BrokenExecutor())

    parent = tmp_path / "vibeocr-next-supervisor-staging"
    assert {entry.name for entry in parent.iterdir()} == {"maintenance.lock"}


def test_managed_staging_refuses_a_redirected_ancestor(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    host = tmp_path / "host"
    host.mkdir()
    _mark_as_reparse_point(host, monkeypatch)

    with pytest.raises(ValueError, match="Redirected supervisor staging path"):
        ManagedStaging.create(host / "managed")
    assert list(host.iterdir()) == []


def test_managed_staging_retains_lease_when_stale_deletion_is_unsafe(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    caplog: pytest.LogCaptureFixture,
) -> None:
    parent = tmp_path / "managed"
    crashed = ManagedStaging.create(parent)
    redirected = crashed.root / "results"
    redirected.mkdir()

    with monkeypatch.context() as patch:
        _mark_as_reparse_point(redirected, patch)
        with pytest.raises(ValueError, match="Redirected supervisor staging path"):
            crashed.close()
        recovered = ManagedStaging.create(parent)
        try:
            assert redirected.is_dir()
            assert crashed.root.with_suffix(".lease").is_file()
            assert "Could not reclaim supervisor staging" in caplog.text
        finally:
            recovered.close()

    recovered = ManagedStaging.create(parent)
    try:
        assert not crashed.root.exists()
        assert not crashed.root.with_suffix(".lease").exists()
    finally:
        recovered.close()


def _mark_as_reparse_point(path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    lstat = type(path).lstat

    def with_reparse_point(candidate: Path) -> stat_result | types.SimpleNamespace:
        info = lstat(candidate)
        if candidate == path:
            return types.SimpleNamespace(
                st_mode=info.st_mode,
                st_file_attributes=stat.FILE_ATTRIBUTE_REPARSE_POINT,
            )
        return info

    monkeypatch.setattr(type(path), "lstat", with_reparse_point)


def test_build_supervisor_keeps_base_ocr_path_without_optional_backends(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from vibeocr.runtime.recognition.ocr_engines import (
        EngineAvailability,
        EngineDescriptor,
        OcrEngineRegistry,
    )
    from vibeocr.runtime_contracts import ResidencyStatus
    from vibeocr.runtime_contracts.dtos import OcrEngine

    class _BaseOcrEngine:
        engine_id = OcrEngine.PADDLEOCR

        def descriptor(self) -> EngineDescriptor:
            return EngineDescriptor(
                engine_id=self.engine_id,
                availability=EngineAvailability.READY,
                included_in_base=True,
            )

        def preload(self, pipelines):  # type: ignore[no-untyped-def]
            return ResidencyStatus()

        def residency_status(self) -> ResidencyStatus:
            return ResidencyStatus()

        def release_idle(self, pipeline=None):  # type: ignore[no-untyped-def]
            return ResidencyStatus()

        def configure_settings(self, snapshot):  # type: ignore[no-untyped-def]
            return None

        def close(self) -> None:
            return None

    monkeypatch.setattr(composition, "_paddle_available", lambda: False)
    monkeypatch.setattr(composition, "_mineru_available", lambda: False)
    registry = OcrEngineRegistry([_BaseOcrEngine()])
    module, handle = build_supervisor(
        instance_id="comp-test",
        stager_root=tmp_path / "stage",
        engine_registry=registry,
    )
    # 目录只列活跃 id：首项是未就绪的 windows（rapidocr 已移除）。
    assert module.engine_resolver.catalog_payload()["engines"][0]["id"] == "windows"
    from vibeocr.runtime.recognition.recognition_modes import (
        RecognitionModeError,
    )

    with pytest.raises(RecognitionModeError):
        module.preload(("OCR",))
    module.release_idle("OCR")
    assert handle.token  # bootstrap handle should now carry a token


def test_build_supervisor_loads_the_injected_settings_store(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from vibeocr.runtime.environments.settings_store import RuntimeSettingsStore
    from vibeocr.runtime_contracts import SettingsSnapshot

    monkeypatch.setattr(composition, "_paddle_available", lambda: False)
    monkeypatch.setattr(composition, "_mineru_available", lambda: False)
    store = RuntimeSettingsStore(tmp_path / "supervisor-settings.json")
    store.replace(SettingsSnapshot(default_ttl_seconds=600))

    module, _ = build_supervisor(
        instance_id="comp-test",
        stager_root=tmp_path / "stage",
        settings_store=store,
    )

    assert module.settings().default_ttl_seconds == 600


def test_build_supervisor_keeps_existing_bootstrap_token(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from vibeocr.runtime.host.bootstrap import BootstrapHandle

    monkeypatch.setattr(composition, "_paddle_available", lambda: False)
    monkeypatch.setattr(composition, "_mineru_available", lambda: False)
    pre_handle = BootstrapHandle("pre-existing-token")
    _, handle = build_supervisor(
        instance_id="comp-test",
        stager_root=tmp_path / "stage",
        bootstrap_handle=pre_handle,
    )
    assert handle.token == "pre-existing-token"


def test_build_supervisor_explicit_executor_wins(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    class _Sentinel:
        def close(self) -> None:
            return

    sentinel = _Sentinel()
    monkeypatch.setattr(composition, "_paddle_available", lambda: True)
    module, _ = build_supervisor(
        instance_id="comp-test",
        stager_root=tmp_path / "stage",
        executor=sentinel,
    )
    assert module._executor is sentinel


def test_build_supervisor_picks_composite_when_paddle_available(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """缺省（use_mineru=None）总是惰性构建 MinerU executor，即使未安装。

    远程自部署连接不依赖本地 mineru 包；显式 opt-out 见下个测试。
    """
    captured: dict[str, Any] = {}

    def fake_composite(
        *,
        use_paddle,
        use_mineru,
        engine_registry=None,  # type: ignore[no-untyped-def]
        engine_resolver=None,  # type: ignore[no-untyped-def]
    ):
        captured.update(use_paddle=use_paddle, use_mineru=use_mineru)
        return _NullExecutor()

    monkeypatch.setattr(composition, "_paddle_available", lambda: True)
    monkeypatch.setattr(composition, "_mineru_available", lambda: False)
    monkeypatch.setattr(composition, "_build_composite_executor", fake_composite)
    module, _ = build_supervisor(
        instance_id="comp-test", stager_root=tmp_path / "stage"
    )
    assert captured == {"use_paddle": True, "use_mineru": True}
    # paddle 存在时引擎目录随 module 暴露。
    assert module.engine_registry is not None


def test_build_supervisor_explicit_mineru_optout_keeps_old_gate(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """显式 use_mineru=False 保留测试 opt-out：不构建 MinerU executor。"""
    captured: dict[str, Any] = {}

    def fake_composite(
        *,
        use_paddle,
        use_mineru,
        engine_registry=None,  # type: ignore[no-untyped-def]
        engine_resolver=None,  # type: ignore[no-untyped-def]
    ):
        captured.update(use_paddle=use_paddle, use_mineru=use_mineru)
        return _NullExecutor()

    monkeypatch.setattr(composition, "_paddle_available", lambda: True)
    monkeypatch.setattr(composition, "_build_composite_executor", fake_composite)
    build_supervisor(
        instance_id="comp-test", stager_root=tmp_path / "stage", use_mineru=False
    )
    assert captured == {"use_paddle": True, "use_mineru": False}


def test_build_supervisor_picks_composite_when_mineru_available(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    captured: dict[str, Any] = {}

    def fake_composite(
        *,
        use_paddle,
        use_mineru,
        engine_registry=None,  # type: ignore[no-untyped-def]
        engine_resolver=None,  # type: ignore[no-untyped-def]
    ):
        captured.update(use_paddle=use_paddle, use_mineru=use_mineru)
        return _NullExecutor()

    monkeypatch.setattr(composition, "_paddle_available", lambda: False)
    monkeypatch.setattr(composition, "_mineru_available", lambda: True)
    monkeypatch.setattr(composition, "_build_composite_executor", fake_composite)
    module, _ = build_supervisor(
        instance_id="comp-test",
        stager_root=tmp_path / "stage",
        use_real_paddle=False,
    )
    assert captured == {"use_paddle": False, "use_mineru": True}
    # MinerU 是可选子执行器；base OCR 目录始终存在。
    assert module.engine_registry is not None


def test_build_supervisor_force_paddle_overrides_availability(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """use_real_paddle=True forces the composite even if paddle is unavailable."""
    captured: dict[str, Any] = {}

    def fake_composite(
        *,
        use_paddle,
        use_mineru,
        engine_registry=None,  # type: ignore[no-untyped-def]
        engine_resolver=None,  # type: ignore[no-untyped-def]
    ):
        captured.update(use_paddle=use_paddle, use_mineru=use_mineru)
        return _NullExecutor()

    monkeypatch.setattr(composition, "_paddle_available", lambda: False)
    monkeypatch.setattr(composition, "_mineru_available", lambda: False)
    monkeypatch.setattr(composition, "_build_composite_executor", fake_composite)
    build_supervisor(
        instance_id="comp-test",
        stager_root=tmp_path / "stage",
        use_real_paddle=True,
        use_mineru=False,
    )
    assert captured == {"use_paddle": True, "use_mineru": False}


# ---------------------------------------------------------------------------
# build_supervisor — pdf adapter wiring
# ---------------------------------------------------------------------------


def test_build_supervisor_attaches_pdf_adapter_when_requested(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    sentinel = object()

    def fake_pdf_adapter():  # type: ignore[no-untyped-def]
        return sentinel

    monkeypatch.setattr(composition, "_paddle_available", lambda: False)
    monkeypatch.setattr(composition, "_mineru_available", lambda: False)
    monkeypatch.setattr(composition, "_build_pdf_adapter", fake_pdf_adapter)
    module, _ = build_supervisor(
        instance_id="comp-test",
        stager_root=tmp_path / "stage",
        with_pdf_adapter=True,
    )
    assert module.pdf_adapter is sentinel


def test_build_pdf_adapter_wraps_lazy_client(monkeypatch: pytest.MonkeyPatch) -> None:
    """``_build_pdf_adapter`` returns an adapter whose ``child_factory`` defers to PdfBackendClient."""
    from vibeocr.runtime.documents.adapter import PdfProcessAdapter

    sentinel_client = object()

    fake_client_module = types.ModuleType(
        "vibeocr.runtime.documents.pdf_backend_client"
    )
    fake_client_module.PdfBackendClient = type(
        "PdfBackendClient",
        (),
        {"instance": staticmethod(lambda: sentinel_client)},
    )
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.documents.pdf_backend_client", fake_client_module
    )

    adapter = composition._build_pdf_adapter()
    assert isinstance(adapter, PdfProcessAdapter)
    assert adapter.child_factory() is sentinel_client


# ---------------------------------------------------------------------------
# _build_paddle_executor / _build_mineru_executor — lazy import path
# ---------------------------------------------------------------------------


def test_build_paddle_executor_wires_factory_and_clear_cache(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Cover the paddle executor builder and its inner factory + clear_cache."""
    sentinel_service = object()

    fake_ocr_module = types.ModuleType("vibeocr.runtime.recognition.ocr_service")
    fake_ocr_module.OCRService = lambda: sentinel_service  # type: ignore[assignment]
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.ocr_service", fake_ocr_module
    )

    executor = composition._build_paddle_executor()
    # Calling adapter_factory() drives the lazy OCRService import + adapter wrap.
    adapter = executor._adapter_factory()
    assert adapter is not None
    # ``clear_cache`` is stored as a plain callable on the executor. It must
    # not raise regardless of whether paddle is importable / CUDA-compiled.
    assert callable(executor._clear_cache)
    executor._clear_cache()


# ---------------------------------------------------------------------------
# OCR engine registry wiring（计划 §3.2/§4.1）
# ---------------------------------------------------------------------------


def test_build_ocr_engine_registry_registers_all_stable_engines() -> None:
    from vibeocr.runtime.recognition.ocr_engines import (
        LazyEngineHandle,
        OcrEngineResolver,
    )
    from vibeocr.runtime_contracts.dtos import OcrEngine

    registry = composition._build_ocr_engine_registry(lambda: object())
    descriptors = OcrEngineResolver(registry).probe_descriptors()
    # #317：目录只覆盖活跃 id；rapidocr 已从能力目录移除，仅保留解析后
    # fail closed 的结构化拒绝（见 resolver 测试）。
    assert [d.engine_id for d in descriptors] == [
        OcrEngine.WINDOWS,
        OcrEngine.PADDLEOCR,
    ]
    # Paddle 必须以惰性句柄注册（导入成本推迟）。
    paddle_engine = registry.get(OcrEngine.PADDLEOCR)
    assert isinstance(paddle_engine, LazyEngineHandle)


def test_recognition_mode_catalog_uses_provisioning_specific_components() -> None:
    from vibeocr.runtime.recognition.ocr_engines import (
        EngineAvailability,
        EngineDescriptor,
        OcrEngineRegistry,
        OcrEngineResolver,
    )
    from vibeocr.runtime_contracts.dtos import OcrEngine

    class _Engine:
        def __init__(
            self, engine_id: OcrEngine, availability: EngineAvailability
        ) -> None:
            self.engine_id = engine_id
            self.availability = availability

        def descriptor(self) -> EngineDescriptor:
            return EngineDescriptor(
                engine_id=self.engine_id,
                availability=self.availability,
                included_in_base=self.engine_id is OcrEngine.PADDLEOCR,
                reason_code=(
                    None
                    if self.availability is EngineAvailability.READY
                    else "engine_not_installed"
                ),
                required_component="legacy-ambiguous-component",
            )

    windows = _Engine(OcrEngine.WINDOWS, EngineAvailability.UNAVAILABLE)
    paddle = _Engine(OcrEngine.PADDLEOCR, EngineAvailability.PREPARATION_REQUIRED)
    resolver = OcrEngineResolver(OcrEngineRegistry([windows, paddle]))
    modes = composition._build_recognition_mode_registry(
        engine_resolver=resolver,
        use_paddle=False,
        use_mineru=False,
    )

    entries = {item["id"]: item for item in modes.catalog_payload()["modes"]}
    assert entries["windows_text"]["availability"] == "unavailable"
    assert entries["windows_text"]["required_component"] is None
    # #270：非 ONNX 环境中保留 Paddle 模式不再指向旧原生配方，而是提示
    # 准备对应 Paddle ONNX 环境。
    assert entries["paddle_text"]["required_component"] == "paddleocr-ort-directml"
    assert entries["paddle_text"]["reason_code"] == (
        "mode_requires_paddle_onnx_environment"
    )

    paddle.availability = EngineAvailability.READY
    resolver.invalidate_probe_cache()
    refreshed = {item["id"]: item for item in modes.catalog_payload()["modes"]}
    assert refreshed["paddle_text"]["availability"] == "preparation_required"
    assert refreshed["paddle_text"]["required_component"] == "paddleocr-ort-directml"


def test_build_paddle_executor_with_registry_wraps_router(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from vibeocr.runtime.recognition.ocr_engine_router import (
        OcrEngineRoutingAdapter,
    )

    fake_ocr_module = types.ModuleType("vibeocr.runtime.recognition.ocr_service")
    fake_ocr_module.OCRService = lambda: object()  # type: ignore[assignment]
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.ocr_service", fake_ocr_module
    )

    registry = composition._build_ocr_engine_registry(
        composition._paddle_adapter_factory
    )
    executor = composition._build_paddle_executor(engine_registry=registry)
    adapter = executor._adapter_factory()
    assert isinstance(adapter, OcrEngineRoutingAdapter)


def test_build_supervisor_base_path_exposes_engine_registry(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(composition, "_paddle_available", lambda: False)
    monkeypatch.setattr(composition, "_mineru_available", lambda: False)
    module, _ = build_supervisor(
        instance_id="comp-test", stager_root=tmp_path / "stage"
    )
    assert module.engine_registry is not None


def test_paddle_clear_cache_does_not_call_native_allocator(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Retained ORT pipelines must not call the retired native Paddle allocator."""
    empty_calls: list[str] = []

    fake_paddle = types.ModuleType("paddle")
    fake_device = types.ModuleType("paddle.device")
    fake_cuda = types.ModuleType("paddle.device.cuda")

    fake_device.is_compiled_with_cuda = lambda: True
    fake_cuda.empty_cache = lambda: empty_calls.append("called")
    fake_device.cuda = fake_cuda
    fake_paddle.device = fake_device
    monkeypatch.setitem(sys.modules, "paddle", fake_paddle)
    monkeypatch.setitem(sys.modules, "paddle.device", fake_device)
    monkeypatch.setitem(sys.modules, "paddle.device.cuda", fake_cuda)

    sentinel_service = object()
    fake_ocr_module = types.ModuleType("vibeocr.runtime.recognition.ocr_service")
    fake_ocr_module.OCRService = lambda: sentinel_service  # type: ignore[assignment]
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.ocr_service", fake_ocr_module
    )

    executor = composition._build_paddle_executor()
    executor._clear_cache()
    assert empty_calls == []


def test_paddle_clear_cache_swallows_paddle_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The except branch must swallow any error from the paddle import."""
    import builtins

    real_import = builtins.__import__

    def raise_importerror(name, globals=None, locals=None, fromlist=(), level=0):  # type: ignore[no-untyped-def]  # noqa: A002 - mirrors builtins.__import__ signature
        if name == "paddle":
            raise ImportError("simulated paddle absence")
        return real_import(name, globals, locals, fromlist, level)

    monkeypatch.setattr(builtins, "__import__", raise_importerror)

    sentinel_service = object()
    fake_ocr_module = types.ModuleType("vibeocr.runtime.recognition.ocr_service")
    fake_ocr_module.OCRService = lambda: sentinel_service  # type: ignore[assignment]
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.ocr_service", fake_ocr_module
    )

    executor = composition._build_paddle_executor()
    # Must not raise even though the paddle import fails.
    executor._clear_cache()


def test_paddle_clear_cache_skips_empty_cache_when_not_cuda_compiled(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The False branch of ``is_compiled_with_cuda`` must skip empty_cache."""
    empty_calls: list[str] = []

    fake_paddle = types.ModuleType("paddle")
    fake_device = types.ModuleType("paddle.device")
    fake_cuda = types.ModuleType("paddle.device.cuda")

    fake_device.is_compiled_with_cuda = lambda: False
    fake_cuda.empty_cache = lambda: empty_calls.append("should-not-run")
    fake_device.cuda = fake_cuda
    fake_paddle.device = fake_device
    monkeypatch.setitem(sys.modules, "paddle", fake_paddle)
    monkeypatch.setitem(sys.modules, "paddle.device", fake_device)
    monkeypatch.setitem(sys.modules, "paddle.device.cuda", fake_cuda)

    fake_ocr_module = types.ModuleType("vibeocr.runtime.recognition.ocr_service")
    fake_ocr_module.OCRService = lambda: object()
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.ocr_service", fake_ocr_module
    )

    executor = composition._build_paddle_executor()
    executor._clear_cache()
    assert empty_calls == []


def test_build_mineru_executor_wires_adapter_factory(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Cover the MinerU executor builder and its inner adapter_factory."""
    sentinel_service = object()

    fake_mineru_module = types.ModuleType("vibeocr.runtime.recognition.mineru_service")
    fake_mineru_module.MinerUService = lambda: sentinel_service
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.mineru_service", fake_mineru_module
    )

    executor = composition._build_mineru_executor()
    # Calling adapter_factory() drives the lazy MinerUService import + adapter wrap.
    adapter = executor._adapter_factory()
    assert adapter is not None


def test_build_composite_executor_routes_to_each_backend(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Composite builder assembles paddle+mineru children sharing one scheduler."""
    paddle_exec = _NullExecutor()
    mineru_exec = _NullExecutor()

    def fake_paddle(  # type: ignore[no-untyped-def]
        *,
        scheduler=None,
        engine_registry=None,
        engine_resolver=None,
        paddle_fallback=True,
    ):
        return paddle_exec

    def fake_mineru(*, scheduler=None):  # type: ignore[no-untyped-def]
        return mineru_exec

    monkeypatch.setattr(composition, "_build_paddle_executor", fake_paddle)
    monkeypatch.setattr(composition, "_build_mineru_executor", fake_mineru)
    composite = composition._build_composite_executor(use_paddle=True, use_mineru=True)
    children = composite._children
    assert {id(c.executor) for c in children} == {id(paddle_exec), id(mineru_exec)}


def test_build_composite_executor_paddle_only(monkeypatch: pytest.MonkeyPatch) -> None:
    paddle_exec = _NullExecutor()

    def fake_paddle(  # type: ignore[no-untyped-def]
        *,
        scheduler=None,
        engine_registry=None,
        engine_resolver=None,
        paddle_fallback=True,
    ):
        return paddle_exec

    monkeypatch.setattr(
        composition,
        "_build_paddle_executor",
        fake_paddle,
    )
    composite = composition._build_composite_executor(use_paddle=True, use_mineru=False)
    assert len(composite._children) == 1


def test_build_composite_executor_mineru_only(monkeypatch: pytest.MonkeyPatch) -> None:
    mineru_exec = _NullExecutor()
    monkeypatch.setattr(
        composition, "_build_mineru_executor", lambda *, scheduler=None: mineru_exec
    )
    composite = composition._build_composite_executor(use_paddle=False, use_mineru=True)
    assert len(composite._children) == 2


# ---------------------------------------------------------------------------
# _MinerUServiceLifecycle
# ---------------------------------------------------------------------------


def test_mineru_lifecycle_start_invokes_singleton(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    calls: list[str] = []

    fake_module = types.ModuleType("vibeocr.runtime.recognition.mineru_service")
    fake_module.MinerUService = lambda: (
        calls.append("constructor")
        or types.SimpleNamespace(_ensure_api_running=lambda: calls.append("ensure"))
    )
    fake_module.MinerUService.bind_owner = lambda owner, *, model_source: None
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.mineru_service", fake_module
    )

    _MinerUServiceLifecycle().start()
    assert calls == ["constructor", "ensure"]


def test_mineru_lifecycle_stop_invokes_shutdown(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    calls: list[str] = []

    class _Svc:
        @classmethod
        def shutdown_owned(cls, **_kwargs):  # type: ignore[no-untyped-def]
            calls.append("shutdown")

    fake_module = types.ModuleType("vibeocr.runtime.recognition.mineru_service")
    fake_module.MinerUService = _Svc
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.mineru_service", fake_module
    )

    _MinerUServiceLifecycle().stop()
    assert calls == ["shutdown"]


def test_mineru_lifecycle_stop_swallows_shutdown_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class _BrokenSvc:
        @classmethod
        def shutdown_owned(cls, **_kwargs):  # type: ignore[no-untyped-def]
            raise RuntimeError("wedged")

    fake_module = types.ModuleType("vibeocr.runtime.recognition.mineru_service")
    fake_module.MinerUService = _BrokenSvc
    monkeypatch.setitem(
        sys.modules, "vibeocr.runtime.recognition.mineru_service", fake_module
    )

    with pytest.raises(RuntimeError, match="wedged"):
        _MinerUServiceLifecycle().stop()


# ---------------------------------------------------------------------------
# Availability probes
# ---------------------------------------------------------------------------


def test_paddle_available_detects_importable_module() -> None:
    """When paddle imports cleanly, the probe returns True."""
    # paddle may or may not be installed in the test environment; the probe's
    # contract is "True iff importable", so just assert it matches reality.
    try:
        __import__("paddle")
        expected = True
    except Exception:
        expected = False
    assert _paddle_available() is expected


def test_paddle_available_returns_false_on_import_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The except branch returns False when the import raises."""
    import builtins

    real_import = builtins.__import__

    def raise_importerror(name, globals=None, locals=None, fromlist=(), level=0):  # type: ignore[no-untyped-def]  # noqa: A002 - mirrors builtins.__import__ signature
        if name == "paddle":
            raise ImportError("no paddle")
        return real_import(name, globals, locals, fromlist, level)

    monkeypatch.setattr(builtins, "__import__", raise_importerror)
    assert _paddle_available() is False


def test_mineru_available_detects_importable_module() -> None:
    # 4.1 起可用性以官方 SDK 客户端（mineru.parser.api_client）可导入为准。
    try:
        import mineru.parser.api_client  # type: ignore  # noqa: F401

        expected = True
    except Exception:
        expected = False
    assert _mineru_available() is expected


def test_mineru_available_returns_false_on_import_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The except branch returns False when the SDK client import raises."""
    import builtins

    real_import = builtins.__import__

    def raise_importerror(name, globals=None, locals=None, fromlist=(), level=0):  # type: ignore[no-untyped-def]  # noqa: A002 - mirrors builtins.__import__ signature
        if name == "mineru.parser.api_client":
            raise ImportError("no mineru sdk client")
        return real_import(name, globals, locals, fromlist, level)

    monkeypatch.setattr(builtins, "__import__", raise_importerror)
    assert _mineru_available() is False
