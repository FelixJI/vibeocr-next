"""运行环境存储的保留面回归：历史记录只读、全局来源统一、删除与清理。

预构建运行时树（PR2）零安装后，本文件只覆盖仍然存在的语义：

- 历史 uuid/revisions 记录照常读取（_safe_path 兼容旧布局）；
- 全局来源保存清除环境 override 的统一提交语义；
- 删除环境时已登记空父容器回收、未知 sibling 保留、公共槽位容器不删；
- 清理扫描对槽位容器/历史残留/换入 journal 的保护投影；
- registry 校验对非法 path 的结构化失败。
"""

from __future__ import annotations

import json
import shutil
from pathlib import Path

import pytest
from test_runtime_installer import _release
from vibeocr.runtime.environments import managed_cleanup
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.runtime_installer import RuntimeInstallError
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json


def _manager(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    # 历史 venv 记录的探针与启动共用真实解释器路径；本文件用例只断言
    # 注册表/目录/清理投影，不依赖引擎健康。
    probe = manager._probe

    def probing_launch(record: dict) -> dict:
        if record["status"] != "installed":
            return probe(record)
        python = str(manager._venv_python(manager._safe_path(record)))
        manager._launch(record, python)
        return {"healthy": True, "reason": None, "python": python}

    monkeypatch.setattr(manager, "_probe", probing_launch)
    return manager


def test_legacy_uuid_environment_records_stay_usable(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager = _manager(tmp_path, monkeypatch)
    env_id = "f" * 8 + "8adbc0de" + "0" * 16
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][env_id] = {
            "id": env_id,
            "name": "旧环境",
            "revision": 1,
            "kind": "venv",
            "path": str(Path(env_id) / "revisions" / "1"),
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
        }
        manager._registry.parent.mkdir(parents=True, exist_ok=True)
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.list()
    assert [item["id"] for item in listed["environments"]] == [env_id]
    with manager._target_operation(env_id):
        pass


def test_global_source_save_clears_matching_environment_overrides(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager = _manager(tmp_path, monkeypatch)

    def write_env(env_id: str, name: str, overrides: list[str]) -> dict:
        with RuntimeStoreLock(manager._lock):
            data = manager._read()
            record = {
                "id": env_id,
                "name": name,
                "revision": 1,
                "kind": "venv",
                "path": str(Path(env_id) / "revisions" / "1"),
                "status": "empty",
                "python_version": manager.manifest.python.version,
                "abi": manager.manifest.python.abi,
                "override_source_ids": overrides,
            }
            data["environments"][env_id] = record
            manager._registry.parent.mkdir(parents=True, exist_ok=True)
            manager._registry.write_text(json.dumps(data), encoding="utf-8")
            return record

    a = write_env("a" * 32, "A", ["paddleocr-bos", "mineru-modelscope"])
    b = write_env("b" * 32, "B", ["paddleocr-huggingface", "mineru-huggingface"])

    # 显式保存全局模型来源（两个引擎 kinds）：所有环境的同 kind override
    # 被同一原子提交清除，统一回新全局默认。
    listed = manager.set_sources(None, "modelscope")
    assert listed["source_config_revision"] == 1
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    env_b = next(item for item in listed["environments"] if item["id"] == b["id"])
    assert env_a["override_source_ids"] == []
    assert env_b["override_source_ids"] == []
    resolved = env_b["resolved_sources"]
    model = next(
        item for item in resolved if item["kind"] == "paddleocr_model_registry"
    )
    assert model["id"] == "paddleocr-modelscope" and model["origin"] == "global_default"

    # 只提交单引擎 kind 时不清除另一引擎 override。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][a["id"]]["override_source_ids"] = [
            "paddleocr-bos",
            "gone-model",
        ]
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.set_sources(None, paddleocr_model_source_id="paddleocr-modelscope")
    assert listed["source_config_revision"] == 2
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    assert env_a["override_source_ids"] == ["gone-model"]
    resolved = env_a["resolved_sources"]
    model = next(
        item for item in resolved if item["kind"] == "paddleocr_model_registry"
    )
    assert model["id"] == "paddleocr-modelscope" and model["origin"] == "global_default"

    # 历史共享模型 id 先展开再判 kind：保存全局模型来源时同样被清除，
    # 且剩余 override 以展开后的标准形式保存。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][a["id"]]["override_source_ids"] = [
            "modelscope",
            "gone-model",
        ]
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.set_sources(None, "huggingface")
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    # 两个引擎 kinds 均已提交：共享模型 id 展开后被清除。
    assert env_a["override_source_ids"] == ["gone-model"]
    resolved = env_a["resolved_sources"]
    model = next(item for item in resolved if item["kind"] == "mineru_model_registry")
    assert model["id"] == "mineru-huggingface" and model["origin"] == "global_default"

    # 未知来源 id 无法判定 kind：提交模型 kinds 时保留不删。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][a["id"]]["override_source_ids"] = [
            "paddleocr-bos",
            "gone-model",
        ]
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.set_sources(None, "huggingface")
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    assert env_a["override_source_ids"] == ["gone-model"]
    assert env_a["unknown_source_ids"] == ["gone-model"]


def test_registry_non_string_paths_fail_structured(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # path 为 null/数字时必须保持结构化 invalid 契约，而不是在
    # Path(record["path"]) 上抛 TypeError（旧实现在此抛 TypeError）。
    manager = _manager(tmp_path, monkeypatch)
    for bad in (None, 123):
        with RuntimeStoreLock(manager._lock):
            data = manager._read()
            data["environments"][str(bad)] = {
                "id": str(bad),
                "name": f"坏路径-{bad}",
                "revision": 1,
                "kind": "venv",
                "path": bad,
                "status": "empty",
                "python_version": manager.manifest.python.version,
                "abi": manager.manifest.python.abi,
            }
            manager._registry.parent.mkdir(parents=True, exist_ok=True)
            manager._registry.write_text(json.dumps(data), encoding="utf-8")
        with pytest.raises(
            ManagedEnvironmentError, match="environment record is invalid"
        ):
            manager.list()
        # 迭代间清理坏记录，避免 setup 阶段的 _read 提前抛错。
        with RuntimeStoreLock(manager._lock):
            raw = json.loads(manager._registry.read_text(encoding="utf-8"))
            del raw["environments"][str(bad)]
            manager._registry.write_text(json.dumps(raw), encoding="utf-8")


def test_unknown_retired_recovery_failure_does_not_block_listing_and_cleanup(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 结构非法的历史 journal 残留：不能阻塞 list/preview_cleanup/run_cleanup；
    # 失败按环境隔离，journal 保留且该环境全部路径继续受保护。
    manager = _manager(tmp_path, monkeypatch)
    env_id = "未知退役"
    live = manager.root / env_id
    live.mkdir(parents=True)
    (live / "pyvenv.cfg").write_text("", encoding="utf-8")
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        expected = {
            "id": env_id,
            "name": "未知退役",
            "revision": 1,
            "kind": "venv",
            "path": env_id,
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
        }
        data["environments"][env_id] = expected
        _atomic_json(manager._registry, data)
        # 结构非法的 journal（候选 revision 不满足 expected+1）：清理扫描
        # fail closed，不得阻塞其余环境。
        _atomic_json(
            manager._journal_path(env_id),
            {
                "schema_version": 1,
                "environment_id": env_id,
                "expected_record": expected,
                "candidate_record": expected,
            },
        )

    listed = manager.list()
    assert listed["environments"][0]["id"] == env_id
    assert manager._journal_path(env_id).is_file(), "journal 保留"
    preview = manager.preview_cleanup()
    env_item = next(
        item for item in preview["items"] if item["id"] == f"environment:{env_id}"
    )
    assert not env_item["can_clean"]
    assert "换入事务未收敛" in env_item["reason"]
    # 同一预览可用：受 journal 保护的环境项拒绝被选中删除，而不是让
    # run_cleanup 因恢复异常报错。
    with pytest.raises(RuntimeInstallError, match="protected resources"):
        manager.run_cleanup(preview["plan_id"], [f"environment:{env_id}"])
    assert (live / "pyvenv.cfg").exists(), "环境数据不受影响"


def test_reclaim_empty_parent_rmdir_failure_keeps_evidence_for_retry(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 已知空父容器 rmdir 因占用失败：ledger 必须保留子路径证据（用于
    # 推导父目录归属），不得先 pop 落盘；解除占用后重入即可完成回收。
    manager = _manager(tmp_path, monkeypatch)
    env_id = "f" * 8 + "8adbc0de" + "0" * 16
    retired = manager._retired_path(env_id)
    owned = retired / "revisions" / "1"
    owned.mkdir(parents=True)
    (owned / "pyvenv.cfg").write_text("", encoding="utf-8")
    with RuntimeStoreLock(manager._lock):
        managed_cleanup.remember_path(manager, env_id, 1, owned)

    revisions = retired / "revisions"
    original_rmdir = Path.rmdir

    def busy_rmdir(self: Path) -> None:
        if Path(self) == revisions:
            raise PermissionError("parent in use")
        return original_rmdir(self)

    monkeypatch.setattr(Path, "rmdir", busy_rmdir)
    with RuntimeStoreLock(manager._lock):
        with pytest.raises(PermissionError, match="parent in use"):
            managed_cleanup.reclaim_retired(manager, env_id)
        # 证据保留：子路径仍在 ledger，未因部分清理而丢失。
        assert (
            str(Path(".retired") / env_id / "revisions" / "1")
            in (managed_cleanup.read_ledger(manager)["paths"])
        )
    monkeypatch.setattr(Path, "rmdir", original_rmdir)
    with RuntimeStoreLock(manager._lock):
        managed_cleanup.reclaim_retired(manager, env_id)
    assert not retired.exists()
    assert (
        str(Path(".retired") / env_id)
        not in managed_cleanup.read_ledger(manager)["paths"]
    )


@pytest.mark.parametrize("scenario", ["unknown_empty_sibling", "empty_root"])
def test_reclaim_retired_preserves_unknown_empty_siblings(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, scenario: str
) -> None:
    # reclaim 只回收已登记子树及其可证明归属的空父容器：legacy 树里的
    # 未知空目录必须保留并阻止槽位复用；无任何归属证据时空槽位根也
    # fail closed 保留；全归属场景空父容器正常回收。
    manager = _manager(tmp_path, monkeypatch)
    env_id = "e" * 8 + "8adbc0de" + "0" * 16
    retired = manager._retired_path(env_id)
    owned = retired / "revisions" / "1"
    unknown_empty = retired / "revisions" / "999-unknown"
    if scenario == "unknown_empty_sibling":
        owned.mkdir(parents=True)
        (owned / "pyvenv.cfg").write_text("", encoding="utf-8")
        unknown_empty.mkdir()
        with RuntimeStoreLock(manager._lock):
            managed_cleanup.remember_path(manager, env_id, 1, owned)
    else:
        retired.mkdir(parents=True)
    with RuntimeStoreLock(manager._lock):
        with pytest.raises(RuntimeInstallError, match="未记录数据"):
            managed_cleanup.reclaim_retired(manager, env_id)
    if scenario == "unknown_empty_sibling":
        # 已登记子树被删除；未知空目录与其父容器保留，阻止槽位复用。
        assert not owned.exists()
        assert unknown_empty.is_dir(), "未知空目录不得被回收"
        assert (retired / "revisions").is_dir()
    else:
        # 无归属证据的空槽位根同样保留。
        assert retired.is_dir(), "空槽位根不得被回收"

    # 全归属场景：已登记子树与其空父容器正常回收，槽位可用。
    shutil.rmtree(retired)
    owned.mkdir(parents=True)
    (owned / "pyvenv.cfg").write_text("", encoding="utf-8")
    with RuntimeStoreLock(manager._lock):
        managed_cleanup.remember_path(manager, env_id, 1, retired / "revisions" / "1")
        managed_cleanup.reclaim_retired(manager, env_id)
    assert not retired.exists()


def test_delete_environment_recycles_owned_empty_parents(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 删除 legacy 迁移环境：已登记叶证明的空父容器应被回收；未知空
    # sibling 必须保留。
    manager = _manager(tmp_path, monkeypatch)

    def legacy(env_id: str, unknown: bool) -> None:
        root = manager.root / env_id / "revisions" / "1"
        root.mkdir(parents=True)
        (root / "pyvenv.cfg").write_text("", encoding="utf-8")
        if unknown:
            empty = manager.root / env_id / "revisions" / "999-unknown"
            empty.mkdir()
        with RuntimeStoreLock(manager._lock):
            data = manager._read()
            data["environments"][env_id] = {
                "id": env_id,
                "name": env_id,
                "revision": 1,
                "kind": "venv",
                "path": str(Path(env_id) / "revisions" / "1"),
                "status": "empty",
                "python_version": manager.manifest.python.version,
                "abi": manager.manifest.python.abi,
            }
            _atomic_json(manager._registry, data)
            managed_cleanup.remember_path(
                manager, env_id, 1, manager.root / env_id / "revisions" / "1"
            )

    clean_id = "g" * 8 + "8adbc0de" + "0" * 16
    legacy(clean_id, unknown=False)
    manager.delete(clean_id)
    assert not (manager.root / clean_id).exists(), "全归属环境目录应被完整回收"

    sibling_id = "h" * 8 + "8adbc0de" + "0" * 16
    legacy(sibling_id, unknown=True)
    manager.delete(sibling_id)
    assert (manager.root / sibling_id / "revisions" / "999-unknown").is_dir(), (
        "未知空 sibling 保留"
    )
    assert (manager.root / sibling_id / "revisions").is_dir()


def test_delete_prune_keeps_shared_slot_containers(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 环境删除的空父回收不得删除公共 .staging/.retired 容器（旧实现会
    # rmdir 空容器）；历史退役槽位照常一并回收。
    manager = _manager(tmp_path, monkeypatch)
    env_id = "i" * 8 + "8adbc0de" + "0" * 16
    legacy_root = manager.root / env_id / "revisions" / "1"
    legacy_root.mkdir(parents=True)
    (legacy_root / "pyvenv.cfg").write_text("", encoding="utf-8")
    retired = manager.root / ".retired" / env_id
    retired.mkdir(parents=True)
    (retired / "pyvenv.cfg").write_text("", encoding="utf-8")
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][env_id] = {
            "id": env_id,
            "name": env_id,
            "revision": 1,
            "kind": "venv",
            "path": str(Path(env_id) / "revisions" / "1"),
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
        }
        _atomic_json(manager._registry, data)
        managed_cleanup.remember_path(manager, env_id, 1, legacy_root)
        managed_cleanup.remember_path(manager, env_id, 1, retired)
    manager.delete(env_id)
    assert not (manager.root / env_id).exists(), "环境目录完整回收"
    assert not retired.exists(), "已登记退役槽位回收"
    assert (manager.root / ".retired").is_dir(), "公共槽位容器不得被删除"


def test_delete_empty_parent_occupied_keeps_evidence_then_completes(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 删除收尾的空父 rmdir 被占用：pending/paths 证据保留，解除后重删
    # 即完成回收。
    manager = _manager(tmp_path, monkeypatch)
    env_id = "j" * 8 + "8adbc0de" + "0" * 16
    legacy_root = manager.root / env_id / "revisions" / "1"
    legacy_root.mkdir(parents=True)
    (legacy_root / "pyvenv.cfg").write_text("", encoding="utf-8")
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][env_id] = {
            "id": env_id,
            "name": env_id,
            "revision": 1,
            "kind": "venv",
            "path": str(Path(env_id) / "revisions" / "1"),
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
        }
        _atomic_json(manager._registry, data)
        managed_cleanup.remember_path(manager, env_id, 1, legacy_root)
    revisions = manager.root / env_id / "revisions"
    original_rmdir = Path.rmdir

    def busy_rmdir(self: Path) -> None:
        if Path(self) == revisions:
            raise PermissionError("parent in use")
        return original_rmdir(self)

    monkeypatch.setattr(Path, "rmdir", busy_rmdir)
    with pytest.raises(RuntimeInstallError):
        manager.delete(env_id)
    ledger = managed_cleanup.read_ledger(manager)
    assert env_id in ledger["pending"], "证据保留"
    monkeypatch.setattr(Path, "rmdir", original_rmdir)
    manager.delete(env_id)
    assert not (manager.root / env_id).exists()
    assert env_id not in managed_cleanup.read_ledger(manager)["pending"]


def test_scan_treats_slot_container_regular_file_as_protected_unknown(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # .staging 为普通文件时预览不得对文件 iterdir 抛错，应作为受保护
    # unknown 返回（旧实现 NotADirectoryError）。
    manager = _manager(tmp_path, monkeypatch)
    manager.root.mkdir(parents=True, exist_ok=True)
    (manager.root / ".staging").write_text("not a directory", encoding="utf-8")
    preview = manager.preview_cleanup()
    item = next(
        item
        for item in preview["items"]
        if item["category"] == "unknown"
        and any(path.endswith(".staging") for path in item["paths"])
    )
    assert not item["can_clean"]
    assert (manager.root / ".staging").is_file()


def test_pending_swap_journal_protects_unique_old_copy_from_cleanup(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 可达故障残留：历史换入事务中断后退役槽位里是唯一旧环境副本，
    # 不 busy、无引用。清理扫描必须凭 pending journal 保护该环境全部
    # 路径。纯状态机验证：直接构造最小目录 + registry/ledger/journal
    # 现场，不跑真实 venv 安装。
    manager = _manager(tmp_path, monkeypatch)
    env_id = "保护"
    staging = manager._staging_path(env_id)
    retired = manager._retired_path(env_id)
    staging.mkdir(parents=True)
    (staging / "pyvenv.cfg").write_text("", encoding="utf-8")
    retired.mkdir(parents=True)
    (retired / "Scripts").mkdir()
    (retired / "Scripts" / "python.exe").write_text("", encoding="utf-8")
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        expected = {
            "id": env_id,
            "name": "保护",
            "revision": 2,
            "kind": "venv",
            "path": env_id,
            "status": "installed",
            "recipe": "paddleocr-ort-cpu",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
        }
        data["environments"][env_id] = expected
        _atomic_json(manager._registry, data)
        # 换入完成后的归属：固定路径属当前修订，退役槽位持有旧副本。
        managed_cleanup.remember_path(manager, env_id, 2, manager.root / env_id)
        managed_cleanup.remember_path(manager, env_id, 1, retired)
        candidate = {
            key: value
            for key, value in expected.items()
            if key != "last_install_operation"
        }
        candidate.update(
            {
                "revision": expected["revision"] + 1,
                "path": env_id,
                "status": "installed",
                "recipe": "paddleocr-ort-cpu",
            }
        )
        _atomic_json(
            manager._journal_path(env_id),
            {
                "schema_version": 1,
                "environment_id": env_id,
                "expected_record": expected,
                "candidate_record": candidate,
            },
        )
    preview = manager.preview_cleanup()
    assert manager._journal_path(env_id).is_file()
    touched = [
        item
        for item in preview["items"]
        if item["environment_id"] == env_id
        or any(env_id in path for path in item["paths"])
    ]
    assert touched, "该环境至少出现在 environment/residual 项中"
    assert all(not item["can_clean"] for item in touched)
    assert any("换入事务未收敛" in item["reason"] for item in touched)
    with pytest.raises(RuntimeInstallError, match="protected resources"):
        manager.run_cleanup(preview["plan_id"], [f"environment:{env_id}"])
    # 唯一旧副本仍在退役槽位。
    assert (manager._venv_python(retired)).is_file()
