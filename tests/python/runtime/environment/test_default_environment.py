from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest
from test_runtime_installer import _omit_unused_pip_bootstrap, _release
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore


def _manager(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, install=None):
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release", with_base_pack=True)
    calls = []

    def runner(python, scope, endpoint):
        calls.append((scope.scope_id, scope.runtime_pack, endpoint))
        if install is not None:
            install(python, scope, endpoint)

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=runner,
    )
    probe = manager._probe
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: (
            {
                "healthy": True,
                "reason": None,
                "python": str(manager._venv_python(manager._safe_path(record))),
            }
            if record["status"] == "installed"
            else probe(record)
        ),
    )
    return manager, calls


def test_first_launch_installs_and_selects_default_once(tmp_path, monkeypatch):
    manager, calls = _manager(tmp_path, monkeypatch)
    listed = manager.initialize_default()
    assert len(listed["environments"]) == 1
    environment = listed["environments"][0]
    assert environment["name"] == "默认环境"
    assert environment["recipe"] == "rapidocr-cpu"
    assert listed["active_id"] == environment["id"]
    assert listed["active_revision"] == 1
    assert len(calls) == 1 and calls[0][1]
    assert manager.initialize_default() == listed
    assert len(calls) == 1
    registry = json.loads(manager._registry.read_text(encoding="utf-8"))
    assert "default_environment_id" not in registry
    marker = manager.paths.state_root / "default-environment.json"
    assert json.loads(marker.read_text(encoding="utf-8")) == {
        "environment_id": None,
        "created": True,
    }


def test_upgrade_from_empty_registry_gets_default(tmp_path, monkeypatch):
    manager, calls = _manager(tmp_path, monkeypatch)
    manager._registry.parent.mkdir(parents=True, exist_ok=True)
    manager._registry.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "active_id": None,
                "active_revision": 0,
                "environments": {},
            }
        ),
        encoding="utf-8",
    )
    listed = manager.initialize_default()
    assert listed["active_id"] is not None
    assert len(calls) == 1


def test_upgrade_keeps_existing_environment_and_active_selection(tmp_path, monkeypatch):
    manager, calls = _manager(tmp_path, monkeypatch)
    existing = manager.create("用户环境")
    manager.commit_switch(manager.prepare_switch(existing["id"]))
    before = manager.list()
    assert manager.initialize_default() == before
    assert calls == []


def test_upgrade_keeps_legacy_environment(tmp_path, monkeypatch):
    manager, calls = _manager(tmp_path, monkeypatch)
    legacy = manager.paths.runtime_root
    legacy.mkdir(parents=True, exist_ok=True)
    (legacy / "python.exe").write_bytes(b"test interpreter")
    (legacy / ".installed.json").write_text("{}", encoding="utf-8")
    listed = manager.initialize_default()
    assert listed["active_id"] == "legacy"
    assert [item["id"] for item in listed["environments"]] == ["legacy"]
    assert calls == []


def test_failed_default_install_retries_same_environment(tmp_path, monkeypatch):
    failed = True

    def install(_python, _scope, _endpoint):
        if failed:
            raise RuntimeError("offline pack unavailable")

    manager, calls = _manager(tmp_path, monkeypatch, install)
    with pytest.raises(RuntimeError, match="offline pack unavailable"):
        manager.initialize_default()
    before = manager.list()
    assert len(before["environments"]) == 1
    environment_id = before["environments"][0]["id"]
    assert before["active_id"] is None
    assert before["environments"][0]["last_install_failure"] is not None
    failed = False
    listed = manager.initialize_default()
    assert len(listed["environments"]) == 1
    assert listed["active_id"] == environment_id
    assert len(calls) == 2


def test_deleting_initialized_environment_does_not_recreate(tmp_path, monkeypatch):
    manager, calls = _manager(tmp_path, monkeypatch)
    listed = manager.initialize_default()
    # Clearing the active selection models the normal switch before deletion.
    registry = json.loads(manager._registry.read_text(encoding="utf-8"))
    registry["active_id"] = None
    manager._registry.write_text(json.dumps(registry), encoding="utf-8")
    manager.delete(listed["active_id"])
    assert manager.initialize_default()["environments"] == []
    assert len(calls) == 1


def test_interrupted_creation_reuses_reserved_environment_id(tmp_path, monkeypatch):
    manager, calls = _manager(tmp_path, monkeypatch)
    marker = manager.paths.state_root / "default-environment.json"
    marker.parent.mkdir(parents=True, exist_ok=True)
    reserved_id = "b" * 32
    marker.write_text(
        json.dumps({"environment_id": reserved_id, "created": False}), encoding="utf-8"
    )
    listed = manager.initialize_default()
    assert listed["active_id"] == reserved_id
    assert len(listed["environments"]) == 1 and len(calls) == 1


def test_deleting_failed_default_does_not_recreate(tmp_path, monkeypatch):
    def install(_python, _scope, _endpoint):
        raise RuntimeError("offline pack unavailable")

    manager, calls = _manager(tmp_path, monkeypatch, install)
    with pytest.raises(RuntimeError, match="offline pack unavailable"):
        manager.initialize_default()
    manager.delete(manager.list()["environments"][0]["id"])
    assert manager.initialize_default()["environments"] == []
    assert len(calls) == 1


def test_empty_environment_tools_leave_live_installer_control_pipe_available(tmp_path):
    import os
    import subprocess

    manifest, component = _release(tmp_path / "release")
    harness = r"""
import json
import subprocess
import sys
import threading
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.runtime_installer import _listen_environment_cancel, _environment_cancel_receipt

manager = ManagedEnvironmentStore(
    product_root=sys.argv[1], component_lock=sys.argv[2], runtime_manifest=sys.argv[3],
    base_python=sys._base_executable,
)
# Bound a regression failure without changing production timeouts. subprocess.run
# still launches and collects the real venv/probe child and owns timeout cleanup.
real_run = subprocess.run
def bounded_run(*args, **kwargs):
    if args[0][0] == 'nvidia-smi':
        args = ([sys.executable, '-c', "print('575.64')"], *args[1:])
    kwargs['timeout'] = 5
    return real_run(*args, **kwargs)
subprocess.run = bounded_run
received = threading.Event()
def cancel():
    manager.cancel_install(_environment_cancel_receipt)
    received.set()
threading.Thread(target=_listen_environment_cancel, args=(cancel,), daemon=True).start()
created = manager.create('live-control-pipe')
hardware = manager.list()['hardware']['nvidia_driver']
print(json.dumps({'status': created['status'], 'python_state': created['python_state'], 'driver_status': hardware['status']}), flush=True)
assert received.wait(10), 'installer control pipe stopped receiving commands'
"""
    with subprocess.Popen(
        [
            sys.executable,
            "-c",
            harness,
            str(tmp_path / "product"),
            str(component),
            str(manifest),
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        env={**os.environ, "PYTHONPATH": os.pathsep.join(sys.path)},
    ) as parent:
        assert parent.stdin is not None
        assert parent.stdout is not None
        assert parent.stderr is not None
        created = parent.stdout.readline()
        if not created:
            parent.wait(timeout=15)
            pytest.fail(parent.stderr.read())
        assert json.loads(created) == {
            "status": "empty",
            "python_state": "ready",
            "driver_status": "ok",
        }
        # Keep the installer input open throughout creation/probing, then use the
        # same channel to prove no external tool consumed or closed it.
        parent.stdin.write("cancel\n")
        parent.stdin.flush()
        parent.wait(timeout=15)
        assert parent.returncode == 0, parent.stderr.read()
        assert json.loads(parent.stdout.readline()) == {
            "environment_cancel": "accepted"
        }
