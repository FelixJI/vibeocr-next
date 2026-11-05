"""预构建默认环境（PR2）：幂等注册、随包模型布置与退役默认迁移。

initialize_default 的新语义：预构建树随产品分发、零安装——注册只是把
固定 product 记录写入注册表；active 为 None 或旧退役默认匹配先前读数时
切换；随包默认模型幂等布置；旧退役默认环境的迁移只登记归属并切换
active，健康使用 product 环境后由 _finish_default_migration 回收。
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest
from test_runtime_installer import (
    _materialize_venv,
    _release,
    _sha,
    _write_venv_record,
)
from vibeocr.runtime.environments import managed_environments
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.managed_references import ManagedEnvironmentReferences
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json

_ENV_ID = "paddleocr-ort-directml"
# product 环境的注册表 ID 独立于配方名（避免与历史默认 venv 的 slug 冲突）。
_PRODUCT_ID = "product"


def _manager(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    *,
    paddle_model_pack: Path | None = None,
) -> ManagedEnvironmentStore:
    manifest, component = _release(
        tmp_path / "product", paddle_model_pack=paddle_model_pack
    )
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    # 合成环境里 installed 记录（历史 venv 与 product）统一按本机解释器
    # 放行：本组用例聚焦注册/迁移/active 语义，不依赖真实树探针。
    monkeypatch.setattr(
        manager, "_expected_base_python", lambda: Path(sys._base_executable)
    )
    probe = manager._probe

    def healthy_synthetic(record: dict) -> dict:
        if record["status"] == "installed":
            return {
                "healthy": True,
                "reason": None,
                "python": str(manager._record_python(record)),
            }
        return probe(record)

    monkeypatch.setattr(manager, "_probe", healthy_synthetic)
    return manager


def test_first_launch_registers_default_once(tmp_path: Path, monkeypatch) -> None:
    manager = _manager(tmp_path, monkeypatch)
    listed = manager.initialize_default()
    assert len(listed["environments"]) == 1
    environment = listed["environments"][0]
    assert environment["name"] == "默认环境"
    assert environment["kind"] == "product"
    assert environment["recipe"] == _ENV_ID
    assert listed["active_id"] == _PRODUCT_ID
    assert listed["active_revision"] == 1
    again = manager.initialize_default()
    assert again["active_revision"] == 1
    assert again["environments"] == listed["environments"]
    registry = json.loads(manager._registry.read_text(encoding="utf-8"))
    assert "default_environment_id" not in registry
    # 旧初始化 marker（default-environment.json）不再读写。
    assert not (manager.paths.state_root / "default-environment.json").exists()


def test_upgrade_from_empty_registry_gets_default(tmp_path, monkeypatch) -> None:
    manager = _manager(tmp_path, monkeypatch)
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
    assert listed["active_id"] == _PRODUCT_ID


def test_upgrade_keeps_existing_environment_and_active_selection(
    tmp_path, monkeypatch
) -> None:
    manager = _manager(tmp_path, monkeypatch)
    user = _write_venv_record(manager, "user-env")
    _materialize_venv(manager, "user-env", bare=True)
    manager.commit_switch(manager.prepare_switch(user["id"]))
    before = manager.list()
    listed = manager.initialize_default()
    # 历史 venv 在预构建产品形态下不可运行：active 一律迁移到 product
    # 环境（venv 记录与目录按数据保护保留，可由空间清理回收）。
    assert listed["active_id"] == _PRODUCT_ID
    assert listed["active_revision"] == before["active_revision"] + 1
    assert {item["id"] for item in listed["environments"]} == {user["id"], _PRODUCT_ID}


def test_upgrade_keeps_legacy_environment(tmp_path, monkeypatch) -> None:
    manager = _manager(tmp_path, monkeypatch)
    # legacy 记录的 runtime 根就是 product/runtime：与预构建布局的
    # backend/env 子目录共存（旧安装原地升级后的真实形态）。
    legacy = manager.paths.runtime_root
    legacy.mkdir(parents=True, exist_ok=True)
    (legacy / "python.exe").write_bytes(b"test interpreter")
    (legacy / ".installed.json").write_text("{}", encoding="utf-8")
    listed = manager.initialize_default()
    # legacy 活动环境与 venv 同理：旧 runtime 根的代码读不了预构建
    # manifest，active 一律迁移到产品树；记录与目录保留只读。
    assert listed["active_id"] == _PRODUCT_ID
    assert {item["id"] for item in listed["environments"]} == {"legacy", _PRODUCT_ID}
    # legacy 记录不是退役 venv 默认：不产生迁移标记。
    assert not (
        manager.paths.state_root / "default-environment-migration.json"
    ).exists()


def test_legacy_user_persists_registry_before_first_service_start(
    tmp_path, monkeypatch
) -> None:
    # 无注册表的旧 legacy 用户：候选 Supervisor 的启动租约必须读到注册表，
    # 而 commit_switch 要等目标健康后才首次落盘，因此 prepare_switch 要在
    # 返回 launch 前先把合成记录按 store 锁落盘。
    manager = _manager(tmp_path, monkeypatch)
    legacy = manager.paths.runtime_root
    legacy.mkdir(parents=True, exist_ok=True)
    (legacy / "python.exe").write_bytes(b"test interpreter")
    (legacy / ".installed.json").write_text(
        json.dumps({"accelerator": "cpu"}), encoding="utf-8"
    )
    assert not manager._registry.is_file()

    # 初始化即迁移 active 到产品树并首次落盘注册表（legacy 记录保留）。
    assert manager.initialize_default()["active_id"] == _PRODUCT_ID
    assert manager.list()["active_id"] == _PRODUCT_ID
    # 回到"无注册表"状态验证 prepare_switch 的首次落盘防御分支：候选
    # Supervisor 的启动租约必须读到注册表，而 commit_switch 要等目标
    # 健康后才落盘，prepare 需先把合成记录按 store 锁写回磁盘。
    manager._registry.unlink()

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
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    *,
    environment_id: str = "rapidocr-cpu",
    revision_path: bool = True,
) -> tuple[ManagedEnvironmentStore, dict]:
    """构造旧退役默认环境：venv 记录 + 真实目录 + active 指向。"""
    manager = _manager(tmp_path, monkeypatch)
    path = (
        environment_id
        if not revision_path
        else f"{environment_id}/revisions/2-{'a' * 16}"
    )
    record = _write_venv_record(
        manager,
        environment_id,
        name="默认环境",
        revision=2,
        path=path,
        status="installed",
        recipe="rapidocr-cpu",
    )
    root = manager.root / path
    if not root.exists():
        fixture = _materialize_venv(manager, f".fixture-{environment_id}")
        root.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(fixture), str(root))
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["active_id"] = environment_id
        data["active_revision"] = 1
        _atomic_json(manager._registry, data)
    return manager, record


@pytest.mark.parametrize(
    "environment_id", ["rapidocr-cpu", "0123456789abcdef0123456789abcdef"]
)
def test_upgrade_migrates_retired_default_and_reclaims_old_tree(
    tmp_path, monkeypatch, environment_id
) -> None:
    manager, old = _retired_default(
        tmp_path, monkeypatch, environment_id=environment_id
    )
    old_root = manager.root / old["path"]
    assert old_root.is_dir()

    listed = manager.initialize_default()
    marker = manager.paths.state_root / "default-environment-migration.json"

    # 旧退役默认匹配先前读数：active 切到 product 环境；初始化只登记
    # 迁移 marker，旧树保留到 Supervisor 健康切换提交后回收（回滚语义）。
    assert listed["active_id"] == _PRODUCT_ID
    assert old["id"] in manager._read()["environments"]
    assert old_root.exists()
    assert json.loads(marker.read_text(encoding="utf-8"))["retired_id"] == old["id"]
    # 启动激活链等价模拟：prepare + 健康证据 commit 提交成功后，迁移
    # 回收由 switch_reservation 的收尾收敛。
    monkeypatch.setattr(
        ManagedEnvironmentStore,
        "_verified_health",
        staticmethod(lambda evidence: True),
    )
    with manager.switch_reservation(_PRODUCT_ID) as prepared:
        manager.commit_switch(prepared)
    assert old["id"] not in manager._read()["environments"]
    assert not old_root.exists()
    assert not marker.exists()
    # 幂等重跑：注册与迁移都不再产生变化。
    assert manager.initialize_default()["active_id"] == _PRODUCT_ID


@pytest.mark.parametrize(
    "environment_id", ["rapidocr-cpu", "0123456789abcdef0123456789abcdef"]
)
def test_retired_default_migration_marker_binds_prior_reading(
    tmp_path, monkeypatch, environment_id
) -> None:
    """迁移回收在健康切换提交后执行；清理失败保留证据供重试。"""
    manager, old = _retired_default(
        tmp_path, monkeypatch, environment_id=environment_id
    )
    old_root = manager.root / old["path"]
    marker = manager.paths.state_root / "default-environment-migration.json"
    monkeypatch.setattr(
        ManagedEnvironmentStore,
        "_verified_health",
        staticmethod(lambda evidence: True),
    )

    listed = manager.initialize_default()
    assert listed["active_id"] == _PRODUCT_ID
    # 初始化只登记 marker：旧环境保留，回收延后到健康提交。
    assert old["id"] in manager._read()["environments"]
    assert old_root.exists()
    assert json.loads(marker.read_text(encoding="utf-8")) == {
        "retired_id": old["id"],
        "retired_revision": old["revision"],
        "replacement_id": _PRODUCT_ID,
    }

    # 健康切换提交时清理失败：记录注销先行，旧树作为 pending 残留与
    # marker 一起保留证据，切换本身不受影响；重试按磁盘现状收敛。
    def fail(_path):
        raise PermissionError("fixture file in use")

    original_remove = shutil.rmtree
    monkeypatch.setattr(shutil, "rmtree", fail)
    try:
        with manager.switch_reservation(_PRODUCT_ID) as prepared:
            manager.commit_switch(prepared)
    finally:
        monkeypatch.setattr(shutil, "rmtree", original_remove)
    assert old["id"] not in manager._read()["environments"]
    assert old_root.exists()
    assert marker.exists()

    # 重启后新的启动激活链重试收敛迁移。
    reopened = ManagedEnvironmentStore(
        product_root=manager.product_root,
        component_lock=manager.component_lock_path,
        runtime_manifest=manager.manifest.path,
    )
    monkeypatch.setattr(
        reopened, "_expected_base_python", lambda: Path(sys._base_executable)
    )
    reopened_probe = reopened._probe

    def reopened_synthetic(record: dict) -> dict:
        if record["status"] == "installed":
            return {
                "healthy": True,
                "reason": None,
                "python": str(reopened._record_python(record)),
            }
        return reopened_probe(record)

    monkeypatch.setattr(reopened, "_probe", reopened_synthetic)
    assert reopened.initialize_default()["active_id"] == _PRODUCT_ID
    with reopened.switch_reservation(_PRODUCT_ID) as prepared:
        reopened.commit_switch(prepared)
    assert not old_root.exists()
    assert not marker.exists()


def test_custom_named_uuid_environment_is_not_a_default(tmp_path, monkeypatch) -> None:
    manager, old = _retired_default(
        tmp_path, monkeypatch, environment_id="0123456789abcdef0123456789abcdef"
    )
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][old["id"]]["name"] = "用户的 Rapid 环境"
        _atomic_json(manager._registry, data)
    listed = manager.initialize_default()
    # 非默认命名的退役环境不登记迁移回收：active 仍按 venv 迁移语义
    # 切到 product，旧记录与目录保留（无 marker，交由空间清理回收）。
    assert listed["active_id"] == _PRODUCT_ID
    assert old["id"] in manager._read()["environments"]
    assert (manager.root / old["path"]).is_dir()
    assert not (
        manager.paths.state_root / "default-environment-migration.json"
    ).exists()


def test_retired_default_upgrade_keeps_concurrent_user_selection(
    tmp_path, monkeypatch
) -> None:
    """注册与切换之间的并发用户选择不被覆盖：切换按先前读数 CAS。"""
    manager, old = _retired_default(tmp_path, monkeypatch)
    selected = _write_venv_record(manager, "user-choice")

    real_seed = managed_environments.seed_bundled_paddle_onnx_models

    def racing_seed(manifest, shared_root, check_cancelled, **kwargs):
        # 模拟注册落盘后、active 切换前的并发用户切换。
        with RuntimeStoreLock(manager._lock):
            data = manager._read()
            data["active_id"] = selected["id"]
            data["active_revision"] += 1
            _atomic_json(manager._registry, data)
        return real_seed(manifest, shared_root, check_cancelled, **kwargs)

    monkeypatch.setattr(
        managed_environments, "seed_bundled_paddle_onnx_models", racing_seed
    )
    listed = manager.initialize_default()
    assert listed["active_id"] == selected["id"]
    assert old["id"] in manager._read()["environments"]
    assert (manager.root / old["path"]).is_dir()


def test_legacy_default_marker_file_is_not_read(tmp_path, monkeypatch) -> None:
    """旧 default-environment.json marker 只是历史文件：不读、不写、不改。"""
    manager = _manager(tmp_path, monkeypatch)
    marker = manager.paths.state_root / "default-environment.json"
    marker.parent.mkdir(parents=True, exist_ok=True)
    stale = json.dumps({"environment_id": "rapidocr-cpu", "created": False})
    marker.write_text(stale, encoding="utf-8")

    listed = manager.initialize_default()

    assert listed["active_id"] == _PRODUCT_ID
    assert marker.read_text(encoding="utf-8") == stale


def test_initialize_default_seeds_bundled_models_idempotently(
    tmp_path, monkeypatch
) -> None:
    calls: list[tuple[object, Path]] = []
    real_seed = managed_environments.seed_bundled_paddle_onnx_models

    def recording_seed(manifest, shared_root, check_cancelled, **kwargs):
        calls.append((manifest, shared_root))
        assert callable(check_cancelled)
        return real_seed(manifest, shared_root, check_cancelled, **kwargs)

    monkeypatch.setattr(
        managed_environments, "seed_bundled_paddle_onnx_models", recording_seed
    )
    manager = _manager(tmp_path, monkeypatch)
    manager.initialize_default()
    manager.initialize_default()
    assert (
        calls
        == [
            (manager.manifest, manager.paths.state_root / "model-cache"),
        ]
        * 2
    )


def test_initialize_default_seeds_model_pack_payload(tmp_path, monkeypatch) -> None:
    """随包 model pack 的三模型双源 payload 布置进共享模型缓存。"""
    from vibeocr.runtime.environments import model_cache

    models = ("PP-OCRv6_small_det", "PP-OCRv6_small_rec", "PP-LCNet_x1_0_doc_ori")
    catalog: dict[str, dict] = {}
    pack = tmp_path / "models.zip"
    import zipfile

    with zipfile.ZipFile(pack, mode="w") as archive:
        for name in models:
            payload = f"payload:{name}".encode()
            entry = {
                # 共享缓存按 namespace/source/<owner>/<repo> 布置：每个
                # 模型一个仓库（与冻结清单一致），payload 内是成员文件。
                "repo_id": f"fixtures/{name}_onnx",
                "sources": {
                    source: {
                        "files": [
                            {
                                "path": "inference.onnx",
                                "size": len(payload),
                                "algorithm": "sha256",
                                "checksum": _sha(payload),
                            }
                        ]
                    }
                    for source in ("modelscope", "huggingface")
                },
            }
            catalog[name] = entry
            archive.writestr(f"{name}/inference.onnx", payload)
    monkeypatch.setattr(model_cache, "_paddle_onnx_catalog", lambda: catalog)

    manager = _manager(tmp_path, monkeypatch, paddle_model_pack=pack)
    manager.initialize_default()

    shared = manager.paths.state_root / "model-cache"
    for name in models:
        for source in ("modelscope", "huggingface"):
            payload = (
                shared
                / "paddlex-onnx-3.7.2"
                / source
                / "fixtures"
                / f"{name}_onnx"
                / "payload"
                / "inference.onnx"
            )
            assert payload.is_file(), payload
            assert payload.read_bytes() == f"payload:{name}".encode()


def test_product_environment_cannot_be_deleted(tmp_path, monkeypatch) -> None:
    manager = _manager(tmp_path, monkeypatch)
    manager.initialize_default()
    # active 状态：预览即受保护。
    with pytest.raises(Exception, match="暂不可删除"):
        manager.delete(_PRODUCT_ID)
    # 非 active 也不可删：product 树随产品分发，不属于可清理对象。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["active_id"] = None
        _atomic_json(manager._registry, data)
    with pytest.raises(Exception):
        manager.delete(_PRODUCT_ID)
    assert _PRODUCT_ID in manager._read()["environments"]


def test_switch_control_reports_success_after_migration_cleanup(tmp_path, monkeypatch):
    manager, old = _retired_default(
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
    'python': str(self._record_python(record)),
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


def test_status_scope_resolves_product_environment(tmp_path: Path, monkeypatch) -> None:
    # 回归：status_scope 的存活校验必须接受 kind=product（预构建树随
    # 产品分发）；/v2/runtime/status 的 supervisor 状态解析依赖它，
    # 误判 stale 会让状态路由 500 并阻断启动切换链。
    manager = _manager(tmp_path, monkeypatch)
    manager.initialize_default()
    record = manager._read()["environments"][_PRODUCT_ID]
    scope, closure, accelerator = manager.status_scope(
        _PRODUCT_ID,
        record["revision"],
        manager._safe_path(record),
        record["recipe"],
        record["selected_device"],
    )
    assert scope is not None and scope.sha256 == record["recipe_lock"]
    assert closure is None
    assert accelerator == "cpu"


def test_product_record_refreshes_runtime_binding_after_update(
    tmp_path, monkeypatch
) -> None:
    # 产品更新（CPython 补丁版本变化）后：已有 product 记录随包绑定刷新，
    # 用户偏好保留，revision 递增使旧会话失效，probe 不再判版本漂移。
    manager = _manager(tmp_path, monkeypatch)
    manager.initialize_default()
    scope, _accelerator = manager._recipe("paddleocr-ort-directml")
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        stale = data["environments"][_PRODUCT_ID]
        stale.update(
            python_version="3.13.15",
            abi="cp313",
            recipe_lock="0" * 64,
            selected_device="cpu",
            override_source_ids=["paddleocr-modelscope"],
        )
        _atomic_json(manager._registry, data)

    manager.initialize_default()
    record = manager._read()["environments"][_PRODUCT_ID]
    assert record["python_version"] == manager.manifest.python.version
    assert record["recipe_lock"] == scope.sha256
    assert record["revision"] == 2
    # 用户偏好不随绑定刷新丢失。
    assert record["selected_device"] == "cpu"
    assert record["override_source_ids"] == ["paddleocr-modelscope"]


def test_active_migration_carries_supervisor_settings(tmp_path, monkeypatch) -> None:
    # 迁移 active 到 product 时把原环境的 supervisor-settings.json 安全
    # 迁入（旧文件保留，product 已有设置不覆盖）：MinerU 远程连接等
    # 用户配置不得在新环境回落默认本地模式。
    manager, old = _retired_default(tmp_path, monkeypatch)
    environments_root = manager.paths.state_root / "environments"
    legacy_settings = environments_root / old["id"] / "supervisor-settings.json"
    legacy_settings.parent.mkdir(parents=True, exist_ok=True)
    legacy_settings.write_text('{"mineru": "remote"}', encoding="utf-8")

    manager.initialize_default()

    product_settings = environments_root / _PRODUCT_ID / "supervisor-settings.json"
    assert product_settings.read_text(encoding="utf-8") == '{"mineru": "remote"}'
    assert legacy_settings.read_text(encoding="utf-8") == '{"mineru": "remote"}'

    # product 侧已有设置时迁移不覆盖（重放防护）。
    product_settings.write_text('{"mineru": "local"}', encoding="utf-8")
    legacy_settings.write_text('{"mineru": "changed"}', encoding="utf-8")
    manager._migrate_supervisor_settings(old["id"], _PRODUCT_ID)
    assert product_settings.read_text(encoding="utf-8") == '{"mineru": "local"}'
