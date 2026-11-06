import json
import os
from dataclasses import replace
from unittest.mock import patch
from uuid import uuid4

import pytest
from vibeocr.runtime.environments.product_job_leases import ProductJobLeases
from vibeocr.runtime.environments.product_runtime import _legacy_snapshot
from vibeocr.runtime.environments.settings_store import (
    RuntimeSettingsStore,
    RuntimeSettingsStoreError,
)
from vibeocr.runtime.host.composition import build_supervisor
from vibeocr.runtime.recognition import device_probe
from vibeocr.runtime_contracts import JobKind, JobPriority, JobState, SettingsSnapshot


@pytest.fixture(autouse=True)
def isolate_product_runtime_environment():
    with patch.dict(os.environ):
        yield


@pytest.mark.parametrize(
    ("profile", "recipe", "device", "expected"),
    [
        ("product", "paddleocr-ort-directml", "cpu", "cpu"),
        ("rapidocr-cpu", "rapidocr-cpu", None, "cpu"),
        ("old-cuda", "paddleocr-cuda", "cuda", "auto"),
        ("自己的旧目录", "custom", None, "auto"),
    ],
)
def test_legacy_import_only_reads_selected_settings(
    tmp_path, profile, recipe, device, expected
):
    state = tmp_path / "state"
    root = state / "environments" / profile
    root.mkdir(parents=True)
    old = root / "supervisor-settings.json"
    RuntimeSettingsStore(old).replace(SettingsSnapshot(extra={"keep": "value"}))
    old_bytes = old.read_bytes()
    project = root / "existing.vibp"
    project.write_bytes(b"project")
    registry = state / "environments.json"
    registry.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "active_id": profile,
                "default_source_ids": ["huggingface"],
                "environments": {
                    profile: {
                        "id": profile,
                        "kind": "venv",
                        "recipe": recipe,
                        "selected_device": device,
                        "path": "never-used-to-launch",
                        "override_source_ids": ["paddleocr-modelscope"],
                    }
                },
            }
        ),
        encoding="utf-8",
    )
    registry_bytes = registry.read_bytes()
    snapshot = _legacy_snapshot(state, tmp_path)
    assert _legacy_snapshot(state, tmp_path) == snapshot
    assert snapshot.recognition_device == expected
    assert snapshot.extra == {"keep": "value", "legacy_cache_profile": profile}
    assert set(snapshot.download_source_ids) == {
        "paddleocr-modelscope",
        "mineru-huggingface",
    }
    assert registry.read_bytes() == registry_bytes
    assert old.read_bytes() == old_bytes and project.read_bytes() == b"project"


def test_new_missing_corrupt_and_traversal_registry(tmp_path):
    state = tmp_path / "state"
    assert _legacy_snapshot(state, tmp_path).recognition_device == "auto"
    state.mkdir()
    registry = state / "environments.json"
    for payload in (
        "{",
        '{"schema_version":1,"active_id":"missing","environments":{}}',
        '{"schema_version":1,"active_id":"../other","environments":{}}',
    ):
        registry.write_text(payload, encoding="utf-8")
        with pytest.raises(ValueError, match="旧识别设置"):
            _legacy_snapshot(state, tmp_path)
        assert registry.read_text(encoding="utf-8") == payload


def test_concurrent_save_cannot_overwrite_import_or_settings(tmp_path):
    path = tmp_path / "recognition" / "supervisor-settings.json"
    first, second = RuntimeSettingsStore(path), RuntimeSettingsStore(path)
    default = first.load(SettingsSnapshot())
    assert second.load(SettingsSnapshot()) == default
    first.replace(replace(default, recognition_device="cpu"))
    with pytest.raises(RuntimeSettingsStoreError, match="另一进程"):
        second.replace(default)
    assert second.load(default).recognition_device == "cpu"


def test_two_lease_owners_block_device_change_until_release(tmp_path):
    jobs = ProductJobLeases(tmp_path)
    updater = ProductJobLeases(tmp_path)
    job_id = str(uuid4())
    jobs.admit(job_id)
    try:
        with (
            pytest.raises(ValueError, match="识别任务仍在进行"),
            updater.device_change(),
        ):
            pytest.fail("busy job lost its lease")
    finally:
        jobs.release(job_id)
    with updater.device_change():
        pass


def test_device_probe_failure_racing_job_and_stale_save_preserve_previous(
    tmp_path, monkeypatch
):
    from vibeocr.runtime.host.composition import _NullExecutor

    module, _ = build_supervisor(
        stager_root=tmp_path / "stager",
        executor=_NullExecutor(),
        settings_store=RuntimeSettingsStore(tmp_path / "settings.json"),
    )
    original = module.settings()
    target = replace(original, recognition_device="cpu")

    def fail_probe(_device):
        raise ValueError("provider unavailable")

    monkeypatch.setattr(device_probe, "prepare_device", fail_probe)
    with pytest.raises(ValueError, match="provider unavailable"):
        module.update_settings(target)
    assert module.settings() == original and not (tmp_path / "settings.json").exists()
    monkeypatch.setattr(device_probe, "prepare_device", lambda _device: None)
    prepared = module.prepare_recognition_device(target)
    job = module.registry.create(
        kind=JobKind.RECOGNITION,
        priority=JobPriority.INTERACTIVE,
        items=[],
        progress_total=0,
    )
    with pytest.raises(ValueError, match="任务"):
        module.update_settings(target, prepared=prepared)
    assert module.settings() == original
    job.transition(JobState.QUEUED)
    job.transition(JobState.CANCELLED)
    monkeypatch.delenv("VIBEOCR_RECOGNITION_DEVICE", raising=False)
    saved = module.update_settings(target)
    assert saved.recognition_device == "cpu" and saved.revision == 1
    assert module.registry.snapshot(job.job_id).state is JobState.CANCELLED
    with pytest.raises(ValueError, match="刷新"):
        module.update_settings(original)
    assert RuntimeSettingsStore(tmp_path / "settings.json").load(original) == saved


def test_submitted_paddle_jobs_keep_their_committed_device(tmp_path, monkeypatch):
    import threading

    from vibeocr.runtime.host.composition import _NullExecutor
    from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions
    from vibeocr.runtime_contracts import PipelineSelection

    entered = threading.Event()
    release = threading.Event()

    class Holding(_NullExecutor):
        def execute(self, record, staged):
            entered.set()
            assert release.wait(5)
            super().execute(record, staged)

    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")
    monkeypatch.setenv("VIBEOCR_RECOGNITION_DEVICE", "cpu")
    monkeypatch.setattr(device_probe, "prepare_device", lambda _device: None)
    monkeypatch.setattr(
        "vibeocr.runtime.recognition.directml_devices.hardware_adapters",
        lambda: [{"device_id": 0, "name": "synthetic"}],
    )
    module = SupervisorModule(
        options=SupervisorOptions(instance_id="device-snapshot"),
        stager_root=tmp_path,
        executor=Holding(),
    )
    first = module.update_settings(replace(module.settings(), recognition_device="cpu"))

    def submit():
        return module.submit(
            kind=JobKind.RECOGNITION,
            priority=JobPriority.INTERACTIVE,
            pipeline=PipelineSelection("OCR", engine="paddleocr"),
            uploads=[("page.png", "image/png", b"synthetic")],
        )

    try:
        old_job = submit()
        assert entered.wait(2)
        target = replace(first, recognition_device="directml")
        with pytest.raises(ValueError, match="任务"):
            module.update_settings(target)
        assert module.settings() == first
        release.set()
        from time import monotonic, sleep

        deadline = monotonic() + 5
        while module.registry.get(old_job.job_id).state in (
            JobState.QUEUED,
            JobState.RUNNING,
        ):
            assert monotonic() < deadline
            sleep(0.01)
        module.update_settings(target)
        new_job = submit()
        assert module.registry.get(old_job.job_id).paddle_snapshot["device"] == "cpu"
        assert (
            module.registry.get(new_job.job_id).paddle_snapshot["device"] == "directml"
        )
    finally:
        release.set()
        module.shutdown_now()


def _hold_job_in_child(root, job_id, channel):
    import os

    jobs = ProductJobLeases(root)
    jobs.admit(job_id)
    channel.send("held")
    if channel.recv() == "crash":
        os._exit(17)
    jobs.release(job_id)


@pytest.mark.parametrize("outcome", ["release", "crash"])
def test_independent_supervisor_lease_excludes_change_and_recovers_on_exit(
    tmp_path, outcome
):
    from multiprocessing import get_context

    context = get_context("spawn")
    parent, child = context.Pipe()
    process = context.Process(
        target=_hold_job_in_child, args=(tmp_path, str(uuid4()), child)
    )
    process.start()
    child.close()
    try:
        assert parent.poll(10) and parent.recv() == "held"
        with (
            pytest.raises(ValueError, match="识别任务仍在进行"),
            ProductJobLeases(tmp_path).device_change(),
        ):
            pytest.fail("independent worker lost an outstanding lease")
        parent.send(outcome)
        process.join(10)
        assert process.exitcode == (17 if outcome == "crash" else 0)
        with ProductJobLeases(tmp_path).device_change():
            pass
    finally:
        parent.close()
        if process.is_alive():
            process.terminate()
            process.join(5)
        process.close()


def test_device_stop_never_waits_for_operation_while_holding_short_settings_lock(
    tmp_path, monkeypatch
):
    from contextlib import contextmanager
    from threading import Event, Lock, Thread

    from vibeocr.runtime.host.composition import _NullExecutor
    from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions

    short_lock, operation_lock = Lock(), Lock()
    owns_operation, wait_for_short, idle_checked = Event(), Event(), Event()

    class Contended(_NullExecutor):
        @contextmanager
        def settings_guard(self):
            with short_lock:
                yield

        def release_idle(self, pipeline=None):
            wait_for_short.set()
            assert idle_checked.wait(2), (
                "stop waited for an operation while holding the short lock"
            )
            with operation_lock:
                return super().release_idle(pipeline)

    def idle_check():
        with operation_lock:
            owns_operation.set()
            if wait_for_short.wait(5):
                with short_lock:
                    idle_checked.set()

    monkeypatch.setattr(device_probe, "prepare_device", lambda _device: None)
    monkeypatch.delenv("VIBEOCR_RECOGNITION_DEVICE", raising=False)
    module = SupervisorModule(
        options=SupervisorOptions(instance_id="lock-order"),
        stager_root=tmp_path,
        executor=Contended(),
    )
    timer = Thread(target=idle_check)
    timer.start()
    try:
        assert owns_operation.wait(2)
        assert (
            module.update_settings(
                replace(module.settings(), recognition_device="cpu")
            ).recognition_device
            == "cpu"
        )
    finally:
        wait_for_short.set()
        timer.join(5)
        module.shutdown_now()


@pytest.mark.parametrize("legacy", [False, True])
@pytest.mark.parametrize("valid_connection", [False, True])
def test_product_preparation_preserves_restricted_remote_recovery(
    staged_runtime_product, monkeypatch, legacy, valid_connection
):
    import sys

    from vibeocr.runtime.environments import model_cache, product_runtime
    from vibeocr.runtime.host.composition import _NullExecutor
    from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions

    product, runtime, manifest, binding, catalog = staged_runtime_product
    for key, value in {
        "VIBEOCR_PRODUCT_ROOT": product,
        "VIBEOCR_RUNTIME_MANIFEST": manifest,
        "VIBEOCR_COMPONENT_LOCK": binding,
        "VIBEOCR_PRODUCT_RUNTIME": "1",
    }.items():
        monkeypatch.setenv(key, str(value))
    monkeypatch.delenv("VIBEOCR_LAYOUT_MANIFEST", raising=False)
    monkeypatch.setattr(sys, "executable", str(runtime / "python.exe"))
    monkeypatch.setattr(model_cache, "_paddle_onnx_catalog", lambda: catalog)
    state = product / "state"
    current = state / "recognition" / "supervisor-settings.json"
    old = state / "environments" / "product" / "supervisor-settings.json"
    path = old if legacy else current
    path.parent.mkdir(parents=True)
    connection = {
        "mode": "remote",
        "api_url": "https://example.invalid" if valid_connection else "file:///bad",
    }
    original = json.dumps(
        {
            "schema_version": 1,
            "settings": {
                "schema_version": 2,
                "paddle_idle_seconds": -1,
                "revision": 4,
                "recognition_device": "cpu",
                "extra": {"mineru_connection": connection},
                "download_source_ids": ["mineru-huggingface"],
            },
        }
    ).encode()
    path.write_bytes(original)
    store = product_runtime.prepare_product_runtime()
    module = SupervisorModule(
        options=SupervisorOptions(instance_id="recovery"),
        stager_root=state / "staging",
        executor=_NullExecutor(),
        settings_store=store,
    )
    try:
        snapshot = module.settings()
        assert snapshot.extra["worker_settings_error"]
        assert (
            snapshot.extra["worker_mineru_connection_unavailable"]
            is not valid_connection
        )
        assert snapshot.extra["mineru_connection"] == connection
        assert "mineru-huggingface" in snapshot.download_source_ids
        assert snapshot.revision == 4
        if legacy:
            assert snapshot.extra["legacy_cache_profile"] == "product"
            assert not current.exists()
        else:
            assert snapshot.recognition_device == "cpu"
        with pytest.raises(RuntimeSettingsStoreError, match="preserved"):
            store.replace(snapshot)
        assert path.read_bytes() == original
    finally:
        module.shutdown_now()


def test_product_prepare_imports_legacy_cache_and_fails_closed_on_wrong_interpreter(
    tmp_path, monkeypatch, staged_runtime_product
):
    import sys
    from importlib.metadata import version
    from pathlib import Path

    from vibeocr.runtime.environments import model_cache, product_runtime

    product, runtime, manifest, binding, catalog = staged_runtime_product
    runtime_version = version("vibeocr-next-runtime")
    names = tuple(catalog)
    monkeypatch.setattr(model_cache, "_paddle_onnx_catalog", lambda: catalog)
    state = product / "state"
    state.mkdir()
    registry = state / "environments.json"
    registry.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "active_id": "legacy",
                "environments": {
                    "legacy": {"id": "legacy", "kind": "legacy", "recipe": "old-cpu"}
                },
            }
        ),
        encoding="utf-8",
    )
    old = RuntimeSettingsStore(state / "supervisor-settings.json")
    old.replace(SettingsSnapshot(download_source_ids=("mineru-huggingface",)))
    old_bytes = old.path.read_bytes()
    for name, value in {
        "VIBEOCR_PRODUCT_RUNTIME": "1",
        "VIBEOCR_PRODUCT_ROOT": product,
        "VIBEOCR_RUNTIME_MANIFEST": manifest,
        "VIBEOCR_COMPONENT_LOCK": binding,
    }.items():
        monkeypatch.setenv(name, str(value))
    monkeypatch.delenv("VIBEOCR_LAYOUT_MANIFEST", raising=False)
    import os

    for key in (
        "VIBEOCR_RECOGNITION_DEVICE",
        "VIBEOCR_RUNTIME_ROOT",
        "VIBEOCR_RUNTIME_STATE_ROOT",
        "VIBEOCR_RUNTIME_ACCELERATOR",
        "VIBEOCR_SHARED_MODEL_CACHE",
        "HF_HOME",
        "MODELSCOPE_CACHE",
        "MODELSCOPE_HOME",
        "MODELSCOPE_CREDENTIALS_PATH",
        "PADDLE_PDX_CACHE_HOME",
        "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK",
        "MINERU_HOME",
        "TEMP",
        "TMP",
    ):
        monkeypatch.delenv(key, raising=False)
    original_lock = binding.read_bytes()
    for field in ("capabilities", "digest"):
        changed = json.loads(original_lock)
        if field == "capabilities":
            changed["required_capabilities"].append("unknown.feature")
        else:
            changed["product"]["runtime_manifest_sha256"] = "f" * 64
        binding.write_text(json.dumps(changed), encoding="utf-8")
        with pytest.raises(ValueError, match="版本清单不匹配"):
            product_runtime.prepare_product_runtime()
    binding.write_bytes(original_lock)
    with pytest.raises(ValueError, match="产品解释器"):
        product_runtime.prepare_product_runtime()
    assert not (state / "recognition").exists()
    monkeypatch.setattr(sys, "executable", str(runtime / "python.exe"))
    store = product_runtime.prepare_product_runtime()
    snapshot = store.load(SettingsSnapshot())
    assert snapshot.extra["legacy_cache_at_state_root"]
    assert "mineru-huggingface" in snapshot.download_source_ids
    assert Path(os.environ["HF_HOME"]) == state / "cache/huggingface"
    assert Path(os.environ["MINERU_HOME"]) == state / "mineru4"
    from vibeocr.runtime.environments.runtime_maintenance import (
        runtime_status_from_environment,
    )

    product_status = runtime_status_from_environment("synthetic-product", "ready")
    assert product_status["instance_id"] == "synthetic-product"
    # Binding and import probes have distinct responsibilities: a synthetic
    # tree with no installed distributions cannot report ready components.
    assert product_status["service_state"] == "degraded"
    assert any(
        c["actual_state"] != "ready" for c in product_status["profile"]["components"]
    )
    with monkeypatch.context() as other:
        other.delenv("VIBEOCR_PRODUCT_RUNTIME")
        with pytest.raises(RuntimeError, match="仅能读取"):
            runtime_status_from_environment("other", "ready")
    with monkeypatch.context() as other:
        other.setenv("VIBEOCR_RUNTIME_ROOT", str(tmp_path / "old-venv"))
        with pytest.raises(RuntimeError, match="不匹配"):
            runtime_status_from_environment("other", "ready")
    original_manifest = manifest.read_bytes()
    raw = json.loads(original_manifest)
    del raw["runtime_env"]
    raw.update(
        runtime_wheel=f"vibeocr_next_runtime-{runtime_version}-py3-none-any.whl",
        runtime_sha256="a" * 64,
    )
    raw["python"].update(archive="python.tar.gz", sha256="b" * 64)
    manifest.write_text(json.dumps(raw), encoding="utf-8")
    with pytest.raises(RuntimeError, match="预构建"):
        runtime_status_from_environment("other", "ready")
    manifest.write_bytes(original_manifest)
    cache = state / "model-cache/paddlex-onnx-3.7.2"
    paths = [
        cache / source / "fixtures" / f"{name}_onnx" / "payload/inference.onnx"
        for source in ("modelscope", "huggingface")
        for name in names
    ]
    timestamps = [path.stat().st_mtime_ns for path in paths]
    assert [path.read_bytes() for path in paths] == [
        f"payload:{name}".encode() for _ in range(2) for name in names
    ]
    assert (
        product_runtime.prepare_product_runtime().load(SettingsSnapshot()) == snapshot
    )
    assert [path.stat().st_mtime_ns for path in paths] == timestamps
    assert old.path.read_bytes() == old_bytes


def test_mineru_source_applies_to_owned_child_on_save_and_restart(
    tmp_path, monkeypatch
):
    from threading import Event

    from vibeocr.runtime.host.composition import _MinerUServiceLifecycle, _NullExecutor
    from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions
    from vibeocr.runtime.recognition.mineru_service import MinerUService

    monkeypatch.setenv("MINERU_MODEL_SOURCE", "parent-only")
    monkeypatch.setenv("MINERU_HOME", str(tmp_path / "mineru4"))
    monkeypatch.setattr(MinerUService, "_model_source", "modelscope")
    monkeypatch.setattr(MinerUService, "_startup_cancel", None)
    lifecycle = _MinerUServiceLifecycle()

    class SourceExecutor(_NullExecutor):
        def configure_settings(self, snapshot):
            lifecycle.set_model_sources(snapshot.download_source_ids)
            return super().configure_settings(snapshot)

    store = RuntimeSettingsStore(tmp_path / "settings.json")
    module = SupervisorModule(
        options=SupervisorOptions(instance_id="first"),
        stager_root=tmp_path / "a",
        executor=SourceExecutor(),
        settings_store=store,
    )
    old = module.settings()
    target = replace(old, download_source_ids=("mineru-huggingface",))
    pending = module.registry.create(
        kind=JobKind.RECOGNITION,
        priority=JobPriority.INTERACTIVE,
        items=[],
        progress_total=0,
    )
    pending.transition(JobState.QUEUED)
    with pytest.raises(ValueError, match="任务"):
        module.update_settings(target)
    assert module.settings() == old
    pending.transition(JobState.CANCELLED)
    module.update_settings(target)
    assert lifecycle._model_source == "huggingface"
    module.shutdown_now()
    from os import environ

    lifecycle = _MinerUServiceLifecycle()
    recovered = SupervisorModule(
        options=SupervisorOptions(instance_id="second"),
        stager_root=tmp_path / "b",
        executor=SourceExecutor(),
        settings_store=RuntimeSettingsStore(store.path),
    )
    try:
        assert recovered.settings().download_source_ids == ("mineru-huggingface",)
        MinerUService.bind_owner(Event(), model_source=lifecycle._model_source)
        assert (
            MinerUService._local_environment()["MINERU_MODEL_SOURCE"] == "huggingface"
        )
        assert environ["MINERU_MODEL_SOURCE"] == "parent-only"
    finally:
        recovered.shutdown_now()


def test_retired_installer_module_cannot_register_or_modify_environment(tmp_path):
    import subprocess
    import sys

    run = subprocess.run(
        [
            sys.executable,
            "-I",
            "-B",
            "-m",
            "vibeocr.runtime.environments.runtime_installer",
            "--request-json",
            json.dumps({"action": "initialize_default", "product_root": str(tmp_path)}),
        ],
        cwd=tmp_path,
        stdin=subprocess.DEVNULL,
        capture_output=True,
        timeout=10,
    )
    assert run.returncode != 0
    assert not list(tmp_path.iterdir())
