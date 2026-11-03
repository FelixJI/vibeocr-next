from __future__ import annotations

import sys
from uuid import uuid4

import pytest
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json


def test_explicit_selected_cpu_is_frozen_for_directml_closure(monkeypatch):
    from vibeocr.runtime.recognition.paddle_onnx import freeze_request

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-directml")
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_DEVICE", "cpu")
    assert freeze_request({"device": "auto"})["device"] == "cpu"
    assert freeze_request({"device": "directml"})["device"] == "directml"


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
        status="installed", recipe="paddleocr-ort-directml", selected_device="cpu"
    )
    _atomic_json(manager._registry, data)
    monkeypatch.setattr(manager, "_verified_health", lambda _: True)
    with manager.switch_reservation(item["id"], "directml") as prepared:
        assert manager._read()["environments"][item["id"]]["selected_device"] == "cpu"
        env = prepared["launch"]["environment"]
        assert env["VIBEOCR_MANAGED_ENVIRONMENT_DEVICE"] == "directml"
        assert (
            int(env["VIBEOCR_MANAGED_SELECTION_REVISION"])
            == data["active_revision"] + 1
        )
        manager.commit_switch(prepared, started_health={})
    current = manager._read()
    assert current["active_revision"] == data["active_revision"] + 1
    assert current["environments"][item["id"]]["selected_device"] == "directml"
    assert current["environments"][item["id"]]["revision"] == item["revision"]
    assert current["environments"][item["id"]]["recipe"] == "paddleocr-ort-directml"
    before = manager._registry.read_bytes()
    monkeypatch.setattr(manager, "_verified_health", lambda _: False)
    with manager.switch_reservation(item["id"], "cpu") as prepared:
        with pytest.raises(ManagedEnvironmentError, match="not healthy"):
            manager.commit_switch(prepared, started_health={})
    assert manager._registry.read_bytes() == before


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
    plan = manager.preview_install(item["id"], "paddleocr-ort-directml")
    manager.install(plan["plan_id"], item["id"], plan["recipe"])
    before = manager._read()
    before["active_id"] = item["id"]
    before["active_revision"] = 5
    before["environments"][item["id"]]["selected_device"] = "cpu"
    _atomic_json(manager._registry, before)
    old = dict(before["environments"][item["id"]])
    # 统一推理闭包内没有跨设备重装；取消语义用同 recipe 重装表达。
    plan = manager.preview_install(item["id"], "paddleocr-ort-directml")
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
    plan = manager.preview_install(item["id"], "paddleocr-ort-directml")
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
