from __future__ import annotations

import sys
from pathlib import Path
from uuid import uuid4

import pytest
from test_runtime_installer import _write_venv_record
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock
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

    manager = _manager(tmp_path, monkeypatch)
    item = _write_venv_record(
        manager,
        "paddle",
        status="installed",
        recipe="paddleocr-ort-directml",
        selected_device="cpu",
    )
    data = manager._read()
    data["active_id"] = item["id"]
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
def test_switch_control_stdin_reservation_releases_on_eof_abort_and_invalid_control(
    tmp_path, monkeypatch, capsys, control, expected
):
    import json

    from test_default_environment import _manager
    from test_runtime_installer import _materialize_venv
    from vibeocr.runtime.environments.managed_environments import (
        ManagedEnvironmentStore,
    )
    from vibeocr.runtime.environments.managed_references import admission_lock
    from vibeocr.runtime.environments.runtime_installer import main
    from vibeocr.runtime.environments.runtime_lock import RuntimeLockTimeout

    manager = _manager(tmp_path, monkeypatch)
    item = _write_venv_record(manager, "paddle")
    _materialize_venv(manager, "paddle", bare=True)
    # main() 内部重建 store：base 解释器门按类级补丁对历史 venv 记录放行。
    monkeypatch.setattr(
        ManagedEnvironmentStore,
        "_expected_base_python",
        lambda self: Path(sys._base_executable),
    )

    class Control:
        def readline(self, _limit):
            # 预留期间目标环境操作锁被持有：同环境的其它操作 fail closed。
            reservation = RuntimeStoreLock(
                manager.paths.locks_root
                / "environment-operations"
                / f"{item['id']}.lock",
                timeout=0,
            )
            with pytest.raises(RuntimeLockTimeout):
                with reservation:
                    pass
            return control

    monkeypatch.setattr(sys, "stdin", Control())
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "prepare_switch",
        "environment_id": item["id"],
        "product_root": str(manager.product_root),
        "component_lock": str(manager.component_lock_path),
        "runtime_manifest": str(manager.manifest.path),
    }
    assert (
        main(["--request-json", json.dumps(request), "--environment-switch-control"])
        == expected
    )
    assert '"action": "prepare_switch"' in capsys.readouterr().out
    with admission_lock(manager._references, item["id"]):
        pass


def test_inactive_service_lease_protects_environment_from_cleanup(
    tmp_path, monkeypatch
):
    from test_environment_store_layout import _manager
    from vibeocr.runtime.environments.managed_environments import (
        ManagedEnvironmentError,
    )

    manager = _manager(tmp_path, monkeypatch)
    item = _write_venv_record(manager, "paddle")
    (manager.root / "paddle").mkdir(parents=True, exist_ok=True)
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


def test_registry_record_python_resolves_record_not_path(tmp_path, monkeypatch) -> None:
    """_record_python 吃记录：公共探针/启动投影不得把 Path 传进去。"""
    from test_default_environment import _manager

    manager = _manager(tmp_path, monkeypatch)
    item = _write_venv_record(manager, "paddle", status="installed")
    root = manager._safe_path(item)
    assert manager._record_python(item) == manager._venv_python(root)
    assert isinstance(root, Path)
