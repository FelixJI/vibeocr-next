from __future__ import annotations

import json
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest
from test_runtime_installer import _omit_unused_pip_bootstrap, _release
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.managed_references import ManagedEnvironmentReferences
from vibeocr.runtime.environments.runtime_install_plan import RuntimeInstallPlanStale


def _manager(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, install=None):
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(
        tmp_path / "release", with_base_pack=True, with_paddle_packs=True
    )
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
    assert environment["recipe"] == "paddleocr-ort-cpu"
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


def test_legacy_user_persists_registry_before_first_service_start(
    tmp_path, monkeypatch
):
    # 无注册表的旧 legacy 用户：候选 Supervisor 的启动租约必须读到注册表，
    # 而 commit_switch 要等目标健康后才首次落盘，因此 prepare_switch 要在
    # 返回 launch 前先把合成记录按 store 锁落盘。
    manager, _ = _manager(tmp_path, monkeypatch)
    legacy = manager.paths.runtime_root
    legacy.mkdir(parents=True, exist_ok=True)
    (legacy / "python.exe").write_bytes(b"test interpreter")
    (legacy / ".installed.json").write_text(
        json.dumps({"accelerator": "cpu"}), encoding="utf-8"
    )
    assert not manager._registry.is_file()

    assert manager.initialize_default()["active_id"] == "legacy"
    assert manager.list()["active_id"] == "legacy"
    assert not manager._registry.is_file()

    class Health(BaseHTTPRequestHandler):
        def do_GET(self):
            assert self.path == "/v2/health"
            self.send_response(200)
            self.end_headers()
            self.wfile.write(
                json.dumps(
                    {
                        "schema_version": 2,
                        "protocol_version": 2,
                        "ready": True,
                        "draining": False,
                        "instance_id": "legacy-instance",
                    }
                ).encode()
            )

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Health)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with manager.switch_reservation("legacy") as prepared:
            persisted = json.loads(manager._registry.read_text(encoding="utf-8"))
            assert persisted["active_id"] == "legacy"
            assert persisted["active_revision"] == 1
            assert persisted["environments"]["legacy"]["kind"] == "legacy"
            assert persisted["environments"]["legacy"]["revision"] == 1

            # 候选服务只用 launch 投影自举启动租约并准入任务。
            for key, value in prepared["launch"]["environment"].items():
                monkeypatch.setenv(key, value)
            references = ManagedEnvironmentReferences.from_environment()
            assert references is not None and references.env_id == "legacy"
            job_id = "0d1a7a85-c6de-4c60-9f36-0ea41d0dd5a0"
            try:
                references.admit(job_id)
            finally:
                references.release(job_id)
                references._service.release()

            committed = manager.commit_switch(
                prepared,
                started_health={
                    "port": server.server_port,
                    "instance_id": "legacy-instance",
                },
            )
            assert committed == {"active_id": "legacy", "active_revision": 1}
            # 健康提交后注册表保持 prepare 落盘的初值，不新增协议字段。
            assert (
                json.loads(manager._registry.read_text(encoding="utf-8")) == persisted
            )
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


def _retired_default(
    tmp_path, monkeypatch, install=None, *, environment_id="rapidocr-cpu"
):
    manager, calls = _manager(tmp_path, monkeypatch, install)
    old = manager._create("默认环境", as_default=True, environment_id=environment_id)
    registry = manager._read()
    registry["environments"][old["id"]].update(
        status="installed", recipe="rapidocr-cpu", revision=2
    )
    if len(environment_id) == 32:
        # 早期 UUID 默认环境没有 cleanup ledger，venv 位于修订子目录。
        source = Path(old["path"])
        temporary = source.with_name("legacy-fixture")
        source.rename(temporary)
        target = source / "revisions" / ("2-" + "a" * 16)
        target.parent.mkdir(parents=True)
        temporary.rename(target)
        registry["environments"][old["id"]]["path"] = str(
            target.relative_to(manager.root)
        )
        old["path"] = str(target)
        (manager.paths.state_root / "environment-cleanup.json").unlink()
    registry["active_id"] = old["id"]
    registry["active_revision"] = 1
    manager._registry.write_text(json.dumps(registry), encoding="utf-8")
    marker = manager.paths.state_root / "default-environment.json"
    marker.write_text(
        json.dumps({"environment_id": None, "created": True}), encoding="utf-8"
    )
    probe = manager._probe
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: (
            ManagedEnvironmentStore._probe(manager, record)
            if record["id"] == old["id"]
            else probe(record)
        ),
    )
    return manager, calls, old, registry


@pytest.mark.parametrize("unfinished_marker", [False, True])
@pytest.mark.parametrize(
    "environment_id", ["rapidocr-cpu", "0123456789abcdef0123456789abcdef"]
)
def test_upgrade_replaces_retired_default_without_removing_old_environment(
    tmp_path, monkeypatch, unfinished_marker, environment_id
):
    manager, calls, old, registry = _retired_default(
        tmp_path, monkeypatch, environment_id=environment_id
    )
    if unfinished_marker:
        (manager.paths.state_root / "default-environment.json").write_text(
            json.dumps({"environment_id": old["id"], "created": True}),
            encoding="utf-8",
        )

    listed = manager.initialize_default()
    # The real startup path prepares the selected environment after initialization.
    prepared = manager.prepare_switch(listed["active_id"])

    assert prepared["environment_id"] == "paddleocr-ort-cpu"
    assert prepared["requires_supervisor"]
    assert (
        manager._read()["environments"][old["id"]]
        == registry["environments"][old["id"]]
    )
    assert Path(old["path"]).is_dir()
    assert len(calls) == 1
    assert manager.initialize_default()["active_id"] == "paddleocr-ort-cpu"
    assert len(calls) == 1


@pytest.mark.parametrize(
    "environment_id", ["rapidocr-cpu", "0123456789abcdef0123456789abcdef"]
)
def test_retired_default_upgrade_failure_retries_without_changing_selection(
    tmp_path, monkeypatch, environment_id
):
    failed = True

    def install(_python, _scope, _source):
        if failed:
            raise RuntimeError("offline pack unavailable")

    manager, calls, old, registry = _retired_default(
        tmp_path, monkeypatch, install, environment_id=environment_id
    )
    with pytest.raises(RuntimeError, match="offline pack unavailable"):
        manager.initialize_default()
    assert manager._read()["active_id"] == old["id"]
    assert (
        manager._read()["environments"][old["id"]]
        == registry["environments"][old["id"]]
    )
    assert len(manager._read()["environments"]) == 2

    failed = False
    assert manager.initialize_default()["active_id"] == "paddleocr-ort-cpu"
    assert len(manager._read()["environments"]) == 2
    assert len(calls) == 2


@pytest.mark.parametrize("reserved", [False, True])
def test_migration_cleans_uuid_environment_only_after_healthy_switch(
    tmp_path, monkeypatch, reserved
):
    manager, _, old, _ = _retired_default(
        tmp_path, monkeypatch, environment_id="0123456789abcdef0123456789abcdef"
    )
    listed = manager.initialize_default()
    env_id = listed["active_id"]
    prepared = manager.prepare_switch(env_id)
    with pytest.raises(Exception, match="Supervisor is not healthy"):
        manager.commit_switch(prepared)
    assert Path(old["path"]).is_dir()
    monkeypatch.setattr(manager, "_verified_health", lambda evidence: True)
    if reserved:
        with manager.switch_reservation(env_id) as prepared:
            manager.commit_switch(prepared)
            assert Path(old["path"]).is_dir()
    else:
        manager.commit_switch(prepared)
    assert old["id"] not in manager._read()["environments"]
    assert not Path(old["path"]).exists()
    assert not (
        manager.paths.state_root / "default-environment-migration.json"
    ).exists()
    assert manager.initialize_default()["active_id"] == env_id


def test_migration_cleanup_failure_retries_after_restart(tmp_path, monkeypatch):
    import shutil

    manager, _, old, _ = _retired_default(tmp_path, monkeypatch)
    env_id = manager.initialize_default()["active_id"]
    monkeypatch.setattr(manager, "_verified_health", lambda evidence: True)
    original = shutil.rmtree

    def fail(path):
        raise PermissionError("fixture file in use")

    monkeypatch.setattr(shutil, "rmtree", fail)
    manager.commit_switch(manager.prepare_switch(env_id))
    assert manager._read()["active_id"] == env_id
    assert Path(old["path"]).exists()
    assert (manager.paths.state_root / "default-environment-migration.json").exists()
    monkeypatch.setattr(shutil, "rmtree", original)
    # A new manager has no in-memory migration state.
    reopened = ManagedEnvironmentStore(
        product_root=manager.product_root,
        component_lock=manager.component_lock_path,
        runtime_manifest=manager.manifest.path,
        base_python=sys._base_executable,
    )
    monkeypatch.setattr(reopened, "_probe", manager._probe)
    monkeypatch.setattr(reopened, "_verified_health", lambda evidence: True)
    reopened.commit_switch(reopened.prepare_switch(env_id))
    assert not Path(old["path"]).exists()


def test_custom_uuid_environment_is_not_a_default(tmp_path, monkeypatch):
    manager, calls, old, registry = _retired_default(
        tmp_path, monkeypatch, environment_id="0123456789abcdef0123456789abcdef"
    )
    registry["environments"][old["id"]]["name"] = "用户的 Rapid 环境"
    manager._registry.write_text(json.dumps(registry), encoding="utf-8")
    assert manager.initialize_default()["active_id"] == old["id"]
    assert calls == []


def test_switch_control_reports_success_after_migration_cleanup(tmp_path, monkeypatch):
    manager, _, old, _ = _retired_default(
        tmp_path, monkeypatch, environment_id="0123456789abcdef0123456789abcdef"
    )
    env_id = manager.initialize_default()["active_id"]
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "product_root": str(manager.product_root),
        "component_lock": str(manager.component_lock_path),
        "runtime_manifest": str(manager.manifest.path),
        "action": "prepare_switch",
        "environment_id": env_id,
    }
    harness = """
import sys, time
from pathlib import Path
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.runtime_installer import main
ManagedEnvironmentStore._expected_base_python = lambda self: Path(sys._base_executable)
ManagedEnvironmentStore._probe = lambda self, record: {
    'healthy': True, 'reason': None,
    'python': str(self._venv_python(self._safe_path(record))),
}
ManagedEnvironmentStore._verified_health = staticmethod(lambda evidence: True)
original = ManagedEnvironmentStore._finish_default_migration
def finish(self, env_id):
    time.sleep(0.5)
    original(self, env_id)
ManagedEnvironmentStore._finish_default_migration = finish
sys.exit(main(sys.argv[1:]))
"""
    child = subprocess.Popen(
        [
            sys.executable,
            "-c",
            harness,
            "--request-json",
            json.dumps(request),
            "--environment-switch-control",
        ],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    try:
        assert json.loads(child.stdout.readline())["action"] == "prepare_switch"
        child.stdin.write(
            json.dumps({"action": "commit_switch", "started_health": None}) + "\n"
        )
        child.stdin.flush()
        response = json.loads(child.stdout.readline())
        assert response["action"] == "commit_switch", response
        assert not Path(old["path"]).exists()
        child.wait(timeout=20)
        assert child.returncode == 0, child.stderr.read()
    finally:
        if child.poll() is None:
            child.kill()
        child.communicate(timeout=10)


def test_retired_default_upgrade_does_not_repurpose_existing_environment(
    tmp_path, monkeypatch
):
    manager, calls, old, _ = _retired_default(tmp_path, monkeypatch)
    existing = manager._create(
        "默认环境（PaddleOCR）", environment_id="paddleocr-ort-cpu"
    )
    before = manager._read()["environments"][existing["id"]]

    listed = manager.initialize_default()

    assert listed["active_id"] == "paddleocr-ort-cpu-2"
    assert manager._read()["environments"][existing["id"]] == before
    assert Path(old["path"]).is_dir()
    assert len(calls) == 1


def test_retired_default_upgrade_keeps_concurrent_user_selection(tmp_path, monkeypatch):
    def install(_python, _scope, _source):
        manager.commit_switch(manager.prepare_switch(selected["id"]))

    manager, _calls, _old, _ = _retired_default(tmp_path, monkeypatch, install)
    selected = manager.create("用户选择")

    # Installation rejects the changed revision; the user's selection survives.
    with pytest.raises(RuntimeInstallPlanStale):
        manager.initialize_default()
    assert manager._read()["active_id"] == selected["id"]
    assert manager.initialize_default()["active_id"] == selected["id"]


def test_retired_default_upgrade_reserves_id_against_concurrent_create(
    tmp_path, monkeypatch
):
    manager, _calls, _old, _ = _retired_default(tmp_path, monkeypatch)
    create = manager._create
    concurrent = []

    def create_after_user(name, **kwargs):
        if kwargs.get("environment_id") == "paddleocr-ort-cpu" and not concurrent:
            user = create("paddleocr-ort-cpu")
            concurrent.append(manager._read()["environments"][user["id"]])
        return create(name, **kwargs)

    monkeypatch.setattr(manager, "_create", create_after_user)

    listed = manager.initialize_default()

    assert concurrent[0]["id"] != listed["active_id"]
    assert manager._read()["environments"][concurrent[0]["id"]] == concurrent[0]


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


@pytest.mark.parametrize("reserved_id", ["b" * 32, "rapidocr-cpu"])
def test_interrupted_creation_reuses_reserved_environment_id(
    tmp_path, monkeypatch, reserved_id
):
    manager, calls = _manager(tmp_path, monkeypatch)
    marker = manager.paths.state_root / "default-environment.json"
    marker.parent.mkdir(parents=True, exist_ok=True)
    marker.write_text(
        json.dumps({"environment_id": reserved_id, "created": False}), encoding="utf-8"
    )
    listed = manager.initialize_default()
    expected_id = "paddleocr-ort-cpu" if reserved_id == "rapidocr-cpu" else reserved_id
    assert listed["active_id"] == expected_id
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
