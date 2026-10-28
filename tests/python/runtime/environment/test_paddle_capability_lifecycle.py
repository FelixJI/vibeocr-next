from __future__ import annotations

import sys
from pathlib import Path
from uuid import uuid4

import pytest
from test_runtime_installer import _pinned_versions, _release, _write_record
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json


def test_cuda_closure_serves_cpu_without_device_probe(tmp_path: Path, monkeypatch):
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    scope, _ = manager._recipe("paddleocr-ort-cuda")
    versions = _pinned_versions(manager, "paddleocr-ort-cuda")
    record = _write_record(
        manager,
        "paddle",
        status="installed",
        recipe="paddleocr-ort-cuda",
        recipe_lock=scope.sha256,
    )
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: {
            "healthy": True,
            "reason": None,
            "python": "python",
            "versions": versions,
            "platform": "win_amd64",
        },
    )
    assert (
        manager.find_compatible("paddleocr-ort-cpu")["selected"]["environment_id"]
        == record["id"]
    )
    data = manager._read()
    data["environments"][record["id"]]["recipe_lock"] = "f" * 64
    _atomic_json(manager._registry, data)
    assert manager.find_compatible("paddleocr-ort-cpu")["selected"] is None


def test_explicit_selected_cpu_is_frozen_for_cuda_closure(monkeypatch):
    from vibeocr.runtime.recognition.paddle_onnx import freeze_request

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cuda")
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_DEVICE", "cpu")
    assert freeze_request({"device": "auto"})["device"] == "cpu"
    assert freeze_request({"device": "cuda"})["device"] == "cuda"


def test_service_and_admission_gates_release_without_pid_liveness(
    tmp_path, monkeypatch
):
    from vibeocr.runtime.environments.managed_references import (
        ManagedEnvironmentReferences,
        admission_lock,
        environment_has_services,
        maintenance_lock,
    )
    from vibeocr.runtime.environments.runtime_lock import RuntimeLockTimeout

    registry = tmp_path / "registry.json"
    data = {
        "active_id": "paddle",
        "active_revision": 3,
        "environments": {"paddle": {"revision": 1}},
    }
    _atomic_json(registry, data)
    root = tmp_path / "references"
    for key, value in {
        "VIBEOCR_MANAGED_ENVIRONMENT_ID": "paddle",
        "VIBEOCR_MANAGED_ENVIRONMENT_REVISION": "1",
        "VIBEOCR_MANAGED_SELECTION_REVISION": "3",
        "VIBEOCR_MANAGED_REGISTRY_PATH": str(registry),
        "VIBEOCR_MANAGED_STORE_LOCK": str(tmp_path / "store.lock"),
        "VIBEOCR_MANAGED_REFERENCES_ROOT": str(root),
    }.items():
        monkeypatch.setenv(key, value)
    with maintenance_lock(root, "paddle"):
        with pytest.raises(RuntimeLockTimeout):
            ManagedEnvironmentReferences.from_environment()
    references = ManagedEnvironmentReferences.from_environment()
    assert references is not None and environment_has_services(root, "paddle")
    try:
        old_job = str(uuid4())
        references.admit(old_job)
        with admission_lock(root, "paddle"):
            with pytest.raises(RuntimeLockTimeout):
                references.admit(str(uuid4()))
        data["active_revision"] += 1
        _atomic_json(registry, data)
        with pytest.raises(RuntimeLockTimeout):
            references.admit(str(uuid4()))
        assert old_job in references._held
        references.release(old_job)
    finally:
        references._service.release()
    assert not environment_has_services(root, "paddle")
    assert not list((root / "paddle" / "services").glob("*.lock"))


def test_same_environment_selection_commit_changes_epoch_and_preserves_closure(
    tmp_path, monkeypatch
):
    from test_default_environment import _manager
    from vibeocr.runtime.environments.managed_environments import (
        ManagedEnvironmentError,
    )

    manager, _ = _manager(tmp_path, monkeypatch)
    item = manager.create("paddle")
    data = manager._read()
    data["active_id"] = item["id"]
    data["environments"][item["id"]].update(
        status="installed", recipe="paddleocr-ort-cuda", selected_device="cpu"
    )
    _atomic_json(manager._registry, data)
    monkeypatch.setattr(manager, "_verified_health", lambda _: True)
    with manager.switch_reservation(item["id"], "cuda") as prepared:
        assert manager._read()["environments"][item["id"]]["selected_device"] == "cpu"
        env = prepared["launch"]["environment"]
        assert env["VIBEOCR_MANAGED_ENVIRONMENT_DEVICE"] == "cuda"
        assert (
            int(env["VIBEOCR_MANAGED_SELECTION_REVISION"])
            == data["active_revision"] + 1
        )
        manager.commit_switch(prepared, started_health={})
    current = manager._read()
    assert current["active_revision"] == data["active_revision"] + 1
    assert current["environments"][item["id"]]["selected_device"] == "cuda"
    assert current["environments"][item["id"]]["revision"] == item["revision"]
    assert current["environments"][item["id"]]["recipe"] == "paddleocr-ort-cuda"
    before = manager._registry.read_bytes()
    monkeypatch.setattr(manager, "_verified_health", lambda _: False)
    with manager.switch_reservation(item["id"], "cpu") as prepared:
        with pytest.raises(ManagedEnvironmentError, match="not healthy"):
            manager.commit_switch(prepared, started_health={})
    assert manager._registry.read_bytes() == before


def test_active_cpu_upgrade_reuses_id_and_reports_only_delta(tmp_path, monkeypatch):
    from test_default_environment import _manager

    manager, _ = _manager(tmp_path, monkeypatch)
    item = manager.create("paddle")
    data = manager._read()
    data["active_id"] = item["id"]
    data["environments"][item["id"]].update(
        status="installed", recipe="paddleocr-ort-cpu"
    )
    _atomic_json(manager._registry, data)
    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.probe_nvidia_driver",
        lambda **_: {"status": "ok", "driver_version": "630.20"},
    )
    prepared = manager.prepare_target("paddleocr-ort-cuda")
    assert prepared["id"] == item["id"]
    plan = manager.preview_install(item["id"], "paddleocr-ort-cuda")
    assert plan["requires_service_stop"]
    assert len(manager._read()["environments"]) == 1
    assert plan["cuda_plugin_action"] == "prepare"
    assert plan["removed_dependencies"] == []
    assert plan["dependencies"]


def test_live_service_prevents_install_and_failed_cuda_keeps_healthy_cpu(
    tmp_path, monkeypatch
):
    from test_environment_store_layout import _manager
    from vibeocr.runtime.environments.managed_environments import (
        ManagedEnvironmentError,
    )
    from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock

    manager, calls = _manager(tmp_path, monkeypatch)
    item = manager.create("paddle")
    plan = manager.preview_install(item["id"], "paddleocr-ort-cpu")
    manager.install(plan["plan_id"], item["id"], plan["recipe"])
    data = manager._read()
    data["active_id"] = item["id"]
    data["environments"][item["id"]]["selected_device"] = "cpu"
    _atomic_json(manager._registry, data)
    old = dict(data["environments"][item["id"]])
    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.probe_nvidia_driver",
        lambda **_: {"status": "ok", "driver_version": "630.20"},
    )
    plan = manager.preview_install(item["id"], "paddleocr-ort-cuda")
    count = len(calls)
    with RuntimeStoreLock(manager._references / item["id"] / "services" / "live.lock"):
        with pytest.raises(ManagedEnvironmentError, match="service"):
            manager.install(plan["plan_id"], item["id"], plan["recipe"])
    assert len(calls) == count
    original = manager._probe

    def probe(record):
        result = original(record)
        if record.get("_device_probe") == "cuda" and record["path"] == item["id"]:
            return {**result, "healthy": False, "reason": "cuda_plugin_missing"}
        return result

    monkeypatch.setattr(manager, "_probe", probe)
    plan = manager.preview_install(item["id"], "paddleocr-ort-cuda")
    with manager.maintenance_reservation(item["id"]):
        with pytest.raises(ManagedEnvironmentError, match="cuda_plugin_missing"):
            manager.install(plan["plan_id"], item["id"], plan["recipe"])
    current = manager._read()["environments"][item["id"]]
    assert current["revision"] == old["revision"]
    assert current["recipe"] == old["recipe"]
    assert current["selected_device"] == "cpu"
    assert manager._probe(current)["healthy"]
    assert (
        manager.list()["environments"][0]["last_install_failure"]["reason_code"]
        == "cuda_plugin_missing"
    )


@pytest.mark.parametrize(
    "control,expected",
    [("", 0), ('{"action":"abort_switch"}\n', 0), ('{"action":"invalid"}\n', 1)],
)
def test_maintenance_stdin_reservation_releases_on_eof_abort_and_invalid_control(
    tmp_path, monkeypatch, capsys, control, expected
):
    import json

    from test_default_environment import _manager
    from vibeocr.runtime.environments.managed_references import admission_lock
    from vibeocr.runtime.environments.runtime_installer import main
    from vibeocr.runtime.environments.runtime_lock import RuntimeLockTimeout

    manager, _ = _manager(tmp_path, monkeypatch)
    item = manager.create("paddle")

    class Control:
        def readline(self, _limit):
            with pytest.raises(RuntimeLockTimeout):
                with admission_lock(manager._references, item["id"]):
                    pass
            return control

    monkeypatch.setattr(sys, "stdin", Control())
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "prepare_maintenance",
        "environment_id": item["id"],
        "product_root": str(manager.product_root),
        "component_lock": str(manager.component_lock_path),
        "runtime_manifest": str(manager.manifest.path),
    }
    assert (
        main(["--request-json", json.dumps(request), "--environment-switch-control"])
        == expected
    )
    assert '"action": "prepare_maintenance"' in capsys.readouterr().out
    with admission_lock(manager._references, item["id"]):
        pass


def test_selected_cpu_status_uses_cpu_subset_of_installed_cuda(tmp_path, monkeypatch):
    from test_default_environment import _manager

    manager, _ = _manager(tmp_path, monkeypatch)
    item = manager.create("paddle")
    data = manager._read()
    data["environments"][item["id"]].update(
        status="installed", recipe="paddleocr-ort-cuda", selected_device="cpu"
    )
    _atomic_json(manager._registry, data)
    for device, expected in (("cpu", "cpu"), ("cuda", "nvidia_cuda")):
        scope, closure, accelerator = manager.status_scope(
            item["id"],
            item["revision"],
            Path(item["path"]),
            "paddleocr-ort-cuda",
            device,
        )
        assert scope.scope_id == f"paddleocr-ort-{device}"
        assert closure is None and accelerator == expected
    assert manager._read()["environments"][item["id"]]["recipe"] == "paddleocr-ort-cuda"


def test_inactive_service_lease_protects_environment_from_cleanup(
    tmp_path, monkeypatch
):
    from test_environment_store_layout import _manager
    from vibeocr.runtime.environments.managed_environments import (
        ManagedEnvironmentError,
    )
    from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock

    manager, _ = _manager(tmp_path, monkeypatch)
    item = manager.create("paddle")
    with RuntimeStoreLock(manager._references / item["id"] / "services" / "live.lock"):
        plan = manager.preview_cleanup()
        entry = next(
            entry
            for entry in plan["items"]
            if entry["id"] == f"environment:{item['id']}"
        )
        assert not entry["can_clean"]
        with pytest.raises(ManagedEnvironmentError):
            manager.delete(item["id"])
    assert item["id"] in manager._read()["environments"]
    manager.delete(item["id"])
    assert item["id"] not in manager._read()["environments"]


def test_explicit_cuda_removal_rebuilds_cpu_without_changing_environment_identity(
    tmp_path, monkeypatch
):
    from test_environment_store_layout import _manager

    def install(python, scope, _endpoint):
        if scope.scope_id == "paddleocr-ort-cuda":
            (python.parent.parent / "synthetic-cuda.dll").write_bytes(b"owned fixture")

    manager, calls = _manager(tmp_path, monkeypatch, install=install)
    item = manager.create("paddle")

    def apply(recipe):
        plan = manager.preview_install(item["id"], recipe)
        return plan, manager.install(plan["plan_id"], item["id"], recipe)

    apply("paddleocr-ort-cpu")
    data = manager._read()
    data["active_id"] = item["id"]
    _atomic_json(manager._registry, data)
    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.probe_nvidia_driver",
        lambda **_: {"status": "ok", "driver_version": "630.20"},
    )
    marker = manager.paths.state_root / "model-cache" / "synthetic-model"
    marker.parent.mkdir(parents=True, exist_ok=True)
    marker.write_bytes(b"keep")
    with manager.maintenance_reservation(item["id"]):
        _, cuda = apply("paddleocr-ort-cuda")
    assert cuda["selected_device"] == "cpu"
    assert (Path(cuda["path"]) / "synthetic-cuda.dll").exists()
    count = len(calls)
    monkeypatch.setattr(manager, "_verified_health", lambda _: True)
    with manager.switch_reservation(item["id"], "cpu") as prepared:
        manager.commit_switch(prepared, started_health={})
    assert len(calls) == count
    assert (Path(cuda["path"]) / "synthetic-cuda.dll").exists()
    with manager.maintenance_reservation(item["id"]):
        plan, cpu = apply("paddleocr-ort-cpu")
    assert plan["cuda_plugin_action"] == "remove" and plan["removed_dependencies"]
    assert cpu["id"] == cuda["id"] == item["id"]
    assert cpu["selected_device"] == "cpu"
    assert not (Path(cpu["path"]) / "synthetic-cuda.dll").exists()
    assert marker.read_bytes() == b"keep"
    assert len(manager._read()["environments"]) == 1


@pytest.mark.parametrize(
    "condition", ["current", "old_lock", "missing_package", "changed_version"]
)
def test_cuda_preview_only_folds_verified_installed_cpu_pins(
    tmp_path, monkeypatch, condition
):
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    cpu, _ = manager._recipe("paddleocr-ort-cpu")
    cuda, _ = manager._recipe("paddleocr-ort-cuda")
    record = _write_record(
        manager,
        "paddle",
        status="installed",
        recipe="paddleocr-ort-cpu",
        recipe_lock=cpu.sha256 if condition != "old_lock" else "f" * 64,
    )
    versions = _pinned_versions(manager, "paddleocr-ort-cpu")
    if condition == "missing_package":
        versions.pop("paddlex")
    if condition == "changed_version":
        versions["paddlex"] = "0.0.0"
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda _: {
            "healthy": condition != "missing_package",
            "versions": versions,
            "reason": "engine_packages_missing"
            if condition == "missing_package"
            else None,
            "python": "python",
            "platform": "win_amd64",
        },
    )
    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.probe_nvidia_driver",
        lambda **_: {"status": "ok", "driver_version": "630.20"},
    )
    plan = manager.preview_install(record["id"], "paddleocr-ort-cuda")
    target = manager._scope_pins(cuda)
    if condition in {"old_lock", "missing_package"}:
        assert plan["dependencies"] == [display for _, display, _ in target]
    else:
        assert plan["dependencies"] == [
            display
            for name, display, version in target
            if versions.get(name) != version
        ]
        if condition == "current":
            assert plan["dependencies"]
            assert all(pin.startswith("nvidia-") for pin in plan["dependencies"])


def test_child_process_cancellation_records_terminal_state_and_keeps_cpu(
    tmp_path, monkeypatch
):
    import os

    from test_environment_store_layout import _manager
    from vibeocr.runtime.environments.managed_environments import (
        ManagedEnvironmentInstallCancelled,
    )
    from vibeocr.runtime.environments.runtime_installer import (
        _failure_envelope,
        _run_install_command,
    )

    manager, _ = _manager(tmp_path, monkeypatch)
    item = manager.create("paddle")
    plan = manager.preview_install(item["id"], "paddleocr-ort-cpu")
    manager.install(plan["plan_id"], item["id"], plan["recipe"])
    before = manager._read()
    before["active_id"] = item["id"]
    before["active_revision"] = 5
    before["environments"][item["id"]]["selected_device"] = "cpu"
    _atomic_json(manager._registry, before)
    old = dict(before["environments"][item["id"]])
    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.probe_nvidia_driver",
        lambda **_: {"status": "ok", "driver_version": "630.20"},
    )
    plan = manager.preview_install(item["id"], "paddleocr-ort-cuda")
    events, receipts = [], []

    def observe(event):
        events.append(event)
        if "cancel-ready" in event.get("log", {}).get("text", "") and not receipts:
            manager.cancel_install(receipts.append)

    manager._event_sink = observe

    def install(python, _scope, _endpoint):
        _run_install_command(
            [
                sys._base_executable,
                "-c",
                "import time; print('cancel-ready', flush=True); time.sleep(30)",
            ],
            timeout=15,
            env=dict(os.environ),
            reporter=manager._install_observer,
            heartbeat_code="runtime.install_profile",
            gate_python=python,
        )

    monkeypatch.setattr(manager, "_install_runner", install)
    with pytest.raises(ManagedEnvironmentInstallCancelled) as cancelled:
        manager.install(plan["plan_id"], item["id"], plan["recipe"])
    assert receipts == [True]
    failure = _failure_envelope(cancelled.value, operation=None, maintenance=None)
    assert failure["error"]["canonical_code"] == "CANCELLED"
    assert failure["error"]["category"] == "cancelled"
    after = manager._read()
    current = after["environments"][item["id"]]
    assert after["active_id"] == before["active_id"]
    assert after["active_revision"] == before["active_revision"]
    for key in (
        "recipe",
        "recipe_lock",
        "revision",
        "selected_device",
        "status",
        "source_ids",
    ):
        assert current[key] == old[key]
    assert manager._probe(current)["healthy"]
    operation = current["last_install_operation"]
    assert operation["phase"] == "failed"
    assert operation["reason_code"] == "install_interrupted"
    assert operation["next_action"] == "preview_again"
    assert operation["plan_id"] == plan["plan_id"]
    assert events[-1]["state"] == "cancelled"
    assert not any(event["state"] in ("failed", "succeeded") for event in events)
    manager.cancel_install(receipts.append)
    assert receipts == [True, False]


def test_cancelled_install_keeps_recoverable_marker_when_terminal_write_is_full(
    tmp_path, monkeypatch
):
    import errno

    from test_environment_store_layout import _manager
    from vibeocr.runtime.environments import managed_environments

    manager, _ = _manager(tmp_path, monkeypatch)
    item = manager.create("paddle")
    plan = manager.preview_install(item["id"], "paddleocr-ort-cpu")
    receipts, events, failed_writes = [], [], []
    manager._event_sink = events.append
    original_write = managed_environments._atomic_json

    def write(path, data):
        operation = (
            data.get("environments", {})
            .get(item["id"], {})
            .get("last_install_operation", {})
        )
        if path == manager._registry and operation.get("phase") == "failed":
            failed_writes.append(operation)
            raise OSError(errno.ENOSPC, "synthetic full disk")
        original_write(path, data)

    def install(*_args):
        manager.cancel_install(receipts.append)
        manager._check_install_cancelled()

    monkeypatch.setattr(managed_environments, "_atomic_json", write)
    monkeypatch.setattr(manager, "_install_runner", install)
    with pytest.raises(managed_environments.ManagedEnvironmentInstallCancelled):
        manager.install(plan["plan_id"], item["id"], plan["recipe"])
    assert failed_writes[0]["reason_code"] == "install_interrupted"
    assert (
        manager._read()["environments"][item["id"]]["last_install_operation"]["phase"]
        == "installing"
    )
    assert (
        manager.list()["environments"][0]["last_install_failure"]["reason_code"]
        == "install_interrupted"
    )
    assert events[-1]["state"] == "cancelled"
    manager.cancel_install(receipts.append)
    assert receipts == [True, False]
