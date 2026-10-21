"""运行环境存储固定路径、Velopack current 布局与全局来源统一的回归。

用户实测症状：
- Velopack 便携布局（bundle 根只有 current/packages/state，portable-layout.json
  注册 products.next.root='.'）下，默认环境安装/启动失败，失败证据为
  "product Runtime code is unavailable"——_launch 曾把 runtime-code 硬拼在
  product_root/runtime/backend，而真实产物随 manifest 位于 current/ 内。
- 环境路径不再使用 UUID/revisions/<N-hex>：新环境 venv 直接位于固定路径
  environments/<id>，升级前后不变；安装事务在 .staging/<id> 构建候选、
  提交时把旧 venv 移入 .retired/<id>，两个槽位每环境各一个，不随升级
  次数累积；历史 uuid/带后缀记录照常读取，重装自动迁移到固定路径。
- 新 UI 只有一个全局来源设置：显式保存全局默认必须清除所有环境同 kind
  旧 override，否则隐性遮蔽新全局默认。
"""

from __future__ import annotations

import json
import os
import shutil
import sys
from pathlib import Path

import pytest
from test_runtime_installer import _omit_unused_pip_bootstrap, _release
from vibeocr.runtime.environments import managed_cleanup
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.runtime_install_plan import RuntimeInstallPlanStale
from vibeocr.runtime.environments.runtime_installer import RuntimeInstallError
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json

_RUNTIME_CODE_FILES = (
    "vibeocr/runtime/__init__.py",
    "vibeocr/runtime/host/main.py",
    "vibeocr/runtime/recognition/paddle_worker.py",
    "vibeocr/runtime/documents/pdf_backend_process.py",
    "vibeocr/runtime/environments/dependency_profiles.json",
    "vibeocr/runtime_contracts/__init__.py",
)


def _manager(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    *,
    with_base_pack: bool = True,
    install=None,
):
    _omit_unused_pip_bootstrap(monkeypatch)
    (tmp_path / "release").parent.mkdir(parents=True, exist_ok=True)
    manifest, component = _release(tmp_path / "release", with_base_pack=with_base_pack)
    calls: list[tuple[str, tuple[str, ...], str]] = []

    def runner(python: Path, scope, endpoint: str):
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

    def probing_launch(record: dict) -> dict:
        if record["status"] != "installed":
            return probe(record)
        # 探针与真实 Supervisor 使用同一启动投影：_launch 失败即失败，
        # 不伪造 ready（开发 venv 无 rapidocr，引擎闭包来自离线 pack）。
        python = str(manager._venv_python(manager._safe_path(record)))
        manager._launch(record, python)
        return {"healthy": True, "reason": None, "python": python}

    monkeypatch.setattr(manager, "_probe", probing_launch)
    return manager, calls


def test_default_environment_reserves_readable_purpose_directory(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _calls = _manager(tmp_path, monkeypatch)
    listed = manager.initialize_default()
    environment = listed["environments"][0]
    assert environment["name"] == "默认环境"
    assert environment["recipe"] == "rapidocr-cpu"
    assert environment["id"] == "rapidocr-cpu"
    # 固定路径：venv 就是 environments/<id>，无 revisions 层级。
    assert environment["path"] == str(manager.root / "rapidocr-cpu")
    assert listed["active_id"] == "rapidocr-cpu"
    assert (manager.root / "rapidocr-cpu").is_dir()
    assert not (manager.root / "rapidocr-cpu" / "revisions").exists()
    marker = manager.paths.state_root / "default-environment.json"
    assert json.loads(marker.read_text(encoding="utf-8")) == {
        "environment_id": None,
        "created": True,
    }


def test_user_environments_use_readable_slug_directories_with_collisions(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _calls = _manager(tmp_path, monkeypatch, with_base_pack=False)
    work = manager.create("工作环境")
    assert work["id"] == "工作环境"
    assert (manager.root / "工作环境").is_dir()

    sanitized = manager.create("a:b")
    assert sanitized["id"] == "a-b"
    same_slug = manager.create("a-b")
    assert same_slug["id"] == "a-b-2"

    reserved = manager.create("python-base")
    assert reserved["id"] == "environment-python-base"
    legacy_named = manager.create("legacy")
    assert legacy_named["id"] == "environment-legacy"
    device = manager.create("CON")
    assert device["id"] == "environment-CON"
    underived = manager.create("...")
    assert underived["id"] == "environment"

    for record in (
        work,
        sanitized,
        same_slug,
        reserved,
        legacy_named,
        device,
        underived,
    ):
        assert (manager.root / record["id"]).is_dir()


def test_legacy_uuid_environment_records_stay_usable(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _calls = _manager(tmp_path, monkeypatch, with_base_pack=False)
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


def _velopack_bundle(tmp_path: Path) -> tuple[Path, Path, Path]:
    """构造用户实测的 Velopack 便携布局：bundle 根只有 current/packages/state。"""
    backend = tmp_path / "bundle" / "current" / "runtime" / "backend"
    backend.parent.mkdir(parents=True)
    manifest, component = _release(backend, with_base_pack=True)
    metadata = tmp_path / "bundle" / "current" / "app" / "metadata"
    metadata.mkdir(parents=True)
    component_copy = metadata / "component-lock.json"
    shutil.copyfile(component, component_copy)
    code_root = manifest.parent / "runtime-code"
    for relative in _RUNTIME_CODE_FILES:
        target = code_root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("", encoding="utf-8")
    dist_info = (
        code_root / f"vibeocr_next_runtime-{manager_version(manifest)}.dist-info"
    )
    dist_info.mkdir(parents=True, exist_ok=True)
    (dist_info / "METADATA").write_text(
        "Name: vibeocr-next-runtime\n", encoding="utf-8"
    )
    layout = tmp_path / "bundle" / "portable-layout.json"
    layout.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "shared_root": "state",
                "products": {
                    "next": {
                        "root": ".",
                        "component_lock": "current/app/metadata/component-lock.json",
                    }
                },
            }
        ),
        encoding="utf-8",
    )
    return manifest, component_copy, layout


def manager_version(manifest: Path) -> str:
    return json.loads(manifest.read_text(encoding="utf-8"))["product"]["version"]


def test_velopack_current_layout_resolves_product_code_root_from_manifest(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component, layout = _velopack_bundle(tmp_path)
    calls: list[tuple[str, tuple[str, ...], str]] = []

    def runner(python: Path, scope, endpoint: str) -> None:
        calls.append((scope.scope_id, scope.runtime_pack, endpoint))

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "bundle",
        component_lock=component,
        runtime_manifest=manifest,
        layout_manifest=layout,
        product_id="next",
        base_python=sys._base_executable,
        install_runner=runner,
    )
    probe = manager._probe

    def probing_launch(record: dict) -> dict:
        if record["status"] != "installed":
            return probe(record)
        python = str(manager._venv_python(manager._safe_path(record)))
        manager._launch(record, python)
        return {"healthy": True, "reason": None, "python": python}

    monkeypatch.setattr(manager, "_probe", probing_launch)

    listed = manager.initialize_default()
    environment = listed["environments"][0]
    assert environment["status"] == "installed"
    assert listed["active_id"] == environment["id"]
    assert calls and calls[0][1], "default environment installs from the offline pack"

    prepared = manager.prepare_switch(environment["id"])
    code_root = manifest.parent / "runtime-code"
    assert prepared["launch"]["environment"]["VIBEOCR_PRODUCT_CODE_ROOT"] == str(
        code_root
    )
    # 修复前 _launch 从 bundle 根硬拼 runtime/backend/runtime-code 并因
    # layout_manifest 存在强制要求其完整，导致 "product Runtime code is
    # unavailable"；修复后不得在 bundle 根创建该目录。
    assert not (tmp_path / "bundle" / "runtime").exists()


def test_default_environment_keeps_offline_pack_guard(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _calls = _manager(tmp_path, monkeypatch, with_base_pack=False)
    with pytest.raises(ManagedEnvironmentError, match="bundled offline base pack"):
        manager.initialize_default()
    listed = manager.list()
    assert len(listed["environments"]) == 1
    # 缺离线包是发布闭包错误：保留空环境与保留 id，下次可续装。
    assert listed["environments"][0]["status"] == "empty"
    assert listed["active_id"] is None


def test_default_install_probe_failure_is_not_committed_as_ready(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release", with_base_pack=True)

    def noop_runner(_python: Path, _scope, _endpoint: str) -> None:
        return None

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=noop_runner,
    )
    # 不 patch 探针：安装器 stub 未真正写入引擎包，真实探针必须判
    # 不健康并拒绝提交 installed（不能仅伪造 ready）。
    with pytest.raises(ManagedEnvironmentError, match="failed probe"):
        manager.initialize_default()
    listed = manager.list()
    environment = listed["environments"][0]
    assert environment["status"] == "empty"
    assert environment["dependency_state"] == "empty"
    assert environment["path"].endswith("rapidocr-cpu")
    failure = environment["last_install_failure"]
    assert failure["phase"] == "failed"
    assert failure["reason_code"] == "engine_packages_missing"
    registry = json.loads(manager._registry.read_text(encoding="utf-8"))
    assert registry["active_id"] is None


def test_cuda_combined_upgrade_uses_bound_cu126_lock(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _calls = _manager(tmp_path, monkeypatch, with_base_pack=False)
    from test_runtime_installer import _all_recipes_release

    standalone_manifest, standalone_component = _all_recipes_release(
        tmp_path / "all-recipes"
    )
    full = ManagedEnvironmentStore(
        product_root=tmp_path / "all-recipes-product",
        component_lock=standalone_component,
        runtime_manifest=standalone_manifest,
        base_python=sys._base_executable,
    )

    def write_installed(env_id: str, recipe: str) -> dict:
        with RuntimeStoreLock(full._lock):
            data = full._read()
            record = {
                "id": env_id,
                "name": f"env-{env_id}",
                "revision": 1,
                "kind": "venv",
                "path": str(Path(env_id) / "revisions" / "1"),
                "status": "installed",
                "recipe": recipe,
                "python_version": full.manifest.python.version,
                "abi": full.manifest.python.abi,
            }
            data["environments"][env_id] = record
            full._registry.parent.mkdir(parents=True, exist_ok=True)
            full._registry.write_text(json.dumps(data), encoding="utf-8")
            return record

    rapid = write_installed("a" * 32, "rapidocr-cpu")
    plan = full.preview_install(rapid["id"], "rapidocr+mineru-cuda")
    assert plan["requested_recipe"] == "rapidocr+mineru-cuda"
    scope, accelerator = full._recipe("rapidocr+mineru-cuda")
    assert accelerator == "nvidia_cuda"
    # 实际锁契约：升级计划绑定 manifest 的 cu126 mineru scope 锁与
    # torch CUDA 直接 URL 闭包，不是无锁的口头组合。
    assert plan["recipe_lock"] == scope.sha256
    assert "torch @ https://example.invalid/cu126/torch.whl" in plan["dependencies"]

    mineru = write_installed("b" * 32, "mineru-cpu")
    assert (
        full.preview_install(mineru["id"], "rapidocr+mineru-cuda")["recipe"]
        == "rapidocr+mineru-cuda"
    )

    with pytest.raises(ManagedEnvironmentError, match="compatible locked recipe"):
        full.preview_install(rapid["id"], "paddleocr-cpu")
    with pytest.raises(ManagedEnvironmentError, match="compatible locked recipe"):
        full.preview_install(rapid["id"], "paddleocr-cuda")


def test_global_source_save_clears_matching_environment_overrides(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _calls = _manager(tmp_path, monkeypatch, with_base_pack=False)

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

    a = write_env("a" * 32, "A", ["pypi", "paddleocr-bos", "mineru-modelscope"])
    b = write_env("b" * 32, "B", ["tuna-pypi", "mineru-huggingface"])

    # 显式保存全局模型来源（提交 package + 两个引擎 kinds）：所有环境的
    # 同 kind override 被同一原子提交清除，统一回新全局默认。
    listed = manager.set_sources(None, "tuna-pypi", "modelscope")
    assert listed["source_config_revision"] == 1
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    env_b = next(item for item in listed["environments"] if item["id"] == b["id"])
    assert env_a["override_source_ids"] == []
    assert env_b["override_source_ids"] == []
    resolved = env_b["resolved_sources"]
    package = next(item for item in resolved if item["kind"] == "package_index")
    assert package["id"] == "tuna-pypi" and package["origin"] == "global_default"
    model = next(
        item for item in resolved if item["kind"] == "paddleocr_model_registry"
    )
    assert model["id"] == "paddleocr-modelscope" and model["origin"] == "global_default"

    # 旧预览因 revision 递增而失效。
    plan = manager.preview_install(a["id"], "rapidocr-cpu")
    assert plan["source_config_revision"] == 1

    # 只提交 package kind 时不清除模型 override。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][a["id"]]["override_source_ids"] = [
            "paddleocr-bos",
            "gone-model",
        ]
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.set_sources(None, "pypi")
    assert listed["source_config_revision"] == 2
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    assert env_a["override_source_ids"] == ["paddleocr-bos", "gone-model"]
    resolved = env_a["resolved_sources"]
    package = next(item for item in resolved if item["kind"] == "package_index")
    assert package["id"] == "pypi" and package["origin"] == "global_default"

    # 历史共享模型 id 先展开再判 kind：保存全局模型来源时同样被清除，
    # 且剩余 override 以展开后的标准形式保存。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][a["id"]]["override_source_ids"] = [
            "modelscope",
            "pypi",
        ]
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.set_sources(None, "tuna-pypi", "huggingface")
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    # package 与两个引擎 kinds 均已提交：共享模型 id 与 pypi override 都被清除。
    assert env_a["override_source_ids"] == []
    resolved = env_a["resolved_sources"]
    package = next(item for item in resolved if item["kind"] == "package_index")
    assert package["id"] == "tuna-pypi" and package["origin"] == "global_default"
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
    listed = manager.set_sources(None, "tuna-pypi", "huggingface")
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    assert env_a["override_source_ids"] == ["gone-model"]
    assert env_a["unknown_source_ids"] == ["gone-model"]


def test_installs_keep_one_fixed_path_with_bounded_swap_slots(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 实测痛点：revisions/2-<16随机位>/Scripts/python.exe 过长；固定路径
    # environments/<id> 升级前后不变，.staging/.retired 每环境各一个槽位，
    # 不随升级次数累积（旧 uuid 后缀与纯数字 revisions 实现在此失败）。
    manager, _calls = _manager(
        tmp_path,
        monkeypatch,
        with_base_pack=False,
        install=lambda _python, _scope, _endpoint: None,
    )
    item = manager.create("短目录")
    assert item["path"] == str(manager.root / item["id"])
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("tuna-pypi",))
    installed = manager.install(
        plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",)
    )
    assert installed["status"] == "installed"
    assert installed["revision"] == 2
    assert installed["path"] == str(manager.root / item["id"])
    # 旧 venv 移入唯一退役槽位，候选槽位不残留。
    assert (manager.root / ".retired" / item["id"]).is_dir()
    assert not (manager.root / ".staging" / item["id"]).exists()

    # 同一环境再次安装：固定路径不变，退役槽位被替换而不是累积。
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("pypi",))
    upgraded = manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("pypi",))
    assert upgraded["revision"] == 3
    assert upgraded["path"] == str(manager.root / item["id"])
    assert (manager.root / ".retired" / item["id"]).is_dir()
    assert not (manager.root / ".staging" / item["id"]).exists()
    assert sorted(path.name for path in (manager.root / ".retired").iterdir()) == [
        item["id"]
    ]
    assert not (manager.root / item["id"] / "revisions").exists()


def test_failed_install_retry_keeps_old_env_and_reuses_staging_slot(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 失败候选落在固定 staging 槽位并由 ledger 保留：旧环境在固定路径原样
    # 可用；重试回收同槽位重建，修订号继续递增。
    attempts: list[int] = []

    def install(_python: Path, _scope, _endpoint: str) -> None:
        attempts.append(len(attempts))
        if len(attempts) == 1:
            raise ManagedEnvironmentError("synthetic install failure")

    manager, _calls = _manager(
        tmp_path, monkeypatch, with_base_pack=False, install=install
    )
    item = manager.create("重试")
    staging = manager.root / ".staging" / item["id"]
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(ManagedEnvironmentError, match="synthetic install failure"):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",))
    surviving = manager.list()["environments"][0]
    assert surviving["revision"] == 1
    assert surviving["status"] == "empty"
    assert surviving["path"] == str(manager.root / item["id"])
    assert staging.is_dir(), "失败候选 venv 已落盘"

    installed = manager.install(
        plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",)
    )
    assert installed["revision"] == 2
    assert installed["path"] == str(manager.root / item["id"])
    # 候选槽位被回收复用，不再残留；退役槽位保留旧 venv 交给 cleanup。
    assert not staging.exists()
    assert (manager.root / ".retired" / item["id"]).is_dir()
    ledger = managed_cleanup.read_ledger(manager)
    assert str(Path(item["id"])) in ledger["paths"]
    assert str(Path(".retired") / item["id"]) in ledger["paths"]


def test_repair_empty_rebuilds_at_fixed_path_and_retires_broken_venv(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manager, _calls = _manager(tmp_path, monkeypatch, with_base_pack=False)
    item = manager.create("修复")
    env_id = item["id"]
    registry = manager.paths.state_root / "environments.json"
    value = json.loads(registry.read_text(encoding="utf-8"))
    value["environments"][env_id]["python_version"] = "3.13.0"
    registry.write_text(json.dumps(value), encoding="utf-8")

    repaired = manager.repair_empty(env_id)
    assert repaired["revision"] == 2
    assert repaired["path"] == str(manager.root / env_id)
    assert (manager.root / env_id).is_dir()
    # 旧损坏 venv 移入唯一退役槽位保留，交给 cleanup；staging 不残留。
    assert (manager.root / ".retired" / env_id).is_dir()
    assert not (manager.root / ".staging" / env_id).exists()


def test_legacy_revision_suffix_paths_remain_readable_and_migrate_on_reinstall(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 旧 uuid 环境与 16/32 位后缀修订路径照常读取；重新安装迁移到固定路径
    # environments/<id>，旧 revisions 目录随退役槽位原样保留（不删除、不改名）。
    manager, _calls = _manager(
        tmp_path,
        monkeypatch,
        with_base_pack=False,
        install=lambda _python, _scope, _endpoint: None,
    )
    env_id = "f" * 8 + "8adbc0de" + "0" * 16
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][env_id] = {
            "id": env_id,
            "name": "旧环境",
            "revision": 1,
            "kind": "venv",
            "path": str(Path(env_id) / "revisions" / f"1-{'a' * 16}"),
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
        }
        manager._registry.parent.mkdir(parents=True, exist_ok=True)
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    assert manager.list()["environments"][0]["id"] == env_id

    revisions = manager.root / env_id / "revisions"
    revisions.mkdir(parents=True)
    (revisions / f"1-{'a' * 16}").mkdir()
    (revisions / f"2-{'b' * 32}").mkdir()
    # 未知 sibling（无 ledger 归属记录）：随目录迁移但不得获得可删除归属。
    unknown = revisions / "999-unknown"
    unknown.mkdir()
    (unknown / "private.txt").write_text("retain", encoding="utf-8")
    plan = manager.preview_install(env_id, "rapidocr-cpu", ("tuna-pypi",))
    installed = manager.install(plan["plan_id"], env_id, "rapidocr-cpu", ("tuna-pypi",))
    assert installed["revision"] == 2
    assert installed["path"] == str(manager.root / env_id)
    assert installed["status"] == "installed"
    # 旧带后缀目录与未知 sibling 原样保留在退役槽位，不删除、不改名。
    retired = manager.root / ".retired" / env_id / "revisions"
    assert (retired / f"1-{'a' * 16}").is_dir()
    assert (retired / f"2-{'b' * 32}").is_dir()
    assert (retired / "999-unknown" / "private.txt").read_text(encoding="utf-8") == (
        "retain"
    )
    assert not (manager.root / env_id / "revisions").exists()
    # 本测试手工写入记录、无 ledger 归属：退役槽位里全部是未登记数据，
    # 后续重装必须 fail closed 而不是删除未知内容。
    plan = manager.preview_install(env_id, "rapidocr-cpu", ("pypi",))
    with pytest.raises(RuntimeInstallError, match="退役槽位包含未记录数据"):
        manager.install(plan["plan_id"], env_id, "rapidocr-cpu", ("pypi",))
    assert (retired / "999-unknown" / "private.txt").exists()
    # 清理预览继续保护未知目录：退役槽位内未登记内容作为 unknown 受保护。
    items = manager.preview_cleanup()["items"]
    unknown_item = next(
        item
        for item in items
        if item["category"] == "unknown"
        and Path(item["paths"][0]).parts[-2:] == (".retired", env_id)
    )
    assert not unknown_item["can_clean"]


def _legacy_environment_with_unknown_sibling(manager: ManagedEnvironmentStore) -> str:
    """构造带真实归属记录与未知 sibling 的 legacy 布局环境。"""
    env_id = "c" * 8 + "8adbc0de" + "0" * 16
    legacy_root = manager.root / env_id / "revisions" / "1"
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
        _atomic_json(manager._registry, data)
        managed_cleanup.remember_path(manager, env_id, 1, legacy_root)
    legacy_root.mkdir(parents=True)
    (legacy_root / "pyvenv.cfg").write_text("", encoding="utf-8")
    unknown = manager.root / env_id / "revisions" / "999-unknown"
    unknown.mkdir()
    (unknown / "private.txt").write_text("retain", encoding="utf-8")
    return env_id


def test_legacy_migration_probe_failure_rollback_keeps_unknown_siblings(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # legacy 迁移换入后在最终路径复检失败：回滚把候选移回 staging、旧树
    # 复位。候选整树归属必须被移除、原归属恢复——否则旧树里的未知
    # sibling 凭候选 entry 获得可删除归属（旧实现在此失败）。
    manager, _calls = _manager(
        tmp_path,
        monkeypatch,
        with_base_pack=False,
        install=lambda _python, _scope, _endpoint: None,
    )
    original = manager._probe

    def probe(record: dict) -> dict:
        result = original(record)
        if record["status"] == "installed" and record["path"] == record["id"]:
            return {**result, "healthy": False, "reason": "engine_import_failed"}
        return result

    monkeypatch.setattr(manager, "_probe", probe)
    env_id = _legacy_environment_with_unknown_sibling(manager)
    unknown = manager.root / env_id / "revisions" / "999-unknown"

    plan = manager.preview_install(env_id, "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(ManagedEnvironmentError, match="engine_import_failed"):
        manager.install(plan["plan_id"], env_id, "rapidocr-cpu", ("tuna-pypi",))

    ledger = managed_cleanup.read_ledger(manager)
    assert str(Path(env_id)) not in ledger["paths"]
    assert str(Path(env_id) / "revisions" / "1") in ledger["paths"]
    assert (unknown / "private.txt").read_text(encoding="utf-8") == "retain"
    env_item = next(
        item
        for item in manager.preview_cleanup()["items"]
        if item["id"] == f"environment:{env_id}"
    )
    assert all("999-unknown" not in path for path in env_item["paths"])

    # 重装重试：换入→复检失败→回滚，不变量保持。
    plan = manager.preview_install(env_id, "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(ManagedEnvironmentError, match="engine_import_failed"):
        manager.install(plan["plan_id"], env_id, "rapidocr-cpu", ("tuna-pypi",))
    ledger = managed_cleanup.read_ledger(manager)
    assert str(Path(env_id)) not in ledger["paths"]
    assert (unknown / "private.txt").exists()


def test_rollback_ledger_restore_crash_reconverges(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 崩溃窗口：回滚的 retired→live 改名完成、归属恢复（restore_owned_paths）
    # 未执行时进程中断。重启后 list() 按注册表是否被外部修改分别回滚或
    # 前滚，两者都必须移除/不迁移候选整树归属，未知 sibling 不获得归属。
    manager, _calls = _manager(
        tmp_path,
        monkeypatch,
        with_base_pack=False,
        install=lambda _python, _scope, _endpoint: None,
    )
    env_id = _legacy_environment_with_unknown_sibling(manager)
    unknown = manager.root / env_id / "revisions" / "999-unknown"
    staging = manager._staging_path(env_id)
    retired = manager._retired_path(env_id)

    # 手工构造崩溃现场：换入改名与镜像、候选整树登记、回滚改名均完成，
    # restore_owned_paths 中断，journal 在盘。
    staging.mkdir(parents=True)
    (staging / "candidate.txt").write_text("candidate", encoding="utf-8")
    retired.parent.mkdir(parents=True, exist_ok=True)
    os.rename(manager.root / env_id, retired)
    managed_cleanup.retire_owned_paths(manager, env_id, 2)
    os.rename(staging, manager.root / env_id)
    managed_cleanup.remember_path(manager, env_id, 2, manager.root / env_id)
    with RuntimeStoreLock(manager._lock):
        expected = manager._read()["environments"][env_id]
    candidate = {
        key: value for key, value in expected.items() if key != "last_install_operation"
    }
    candidate.update(
        {
            "revision": 2,
            "path": env_id,
            "status": "installed",
            "recipe": "rapidocr-cpu",
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
    os.rename(manager.root / env_id, staging)
    os.rename(retired, manager.root / env_id)

    ledger = managed_cleanup.read_ledger(manager)
    assert str(Path(env_id)) in ledger["paths"], "崩溃现场：候选整树归属仍在"
    assert managed_cleanup.has_retired_mirrors(manager, env_id)

    # 分支一：注册表被外部修改 → 恢复按回滚收敛。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][env_id]["name"] = "改名后"
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.list()
    environment = listed["environments"][0]
    assert environment["name"] == "改名后"
    assert environment["path"] == str(manager.root / env_id / "revisions" / "1")
    assert not manager._journal_path(env_id).exists()
    ledger = managed_cleanup.read_ledger(manager)
    assert str(Path(env_id)) not in ledger["paths"]
    assert str(Path(env_id) / "revisions" / "1") in ledger["paths"]
    assert (unknown / "private.txt").read_text(encoding="utf-8") == "retain"

    # 分支二：注册表未变 → 恢复前滚完成安装；退役树只有镜像归属，
    # 后续重装遇到未知 sibling fail closed 而不是删除。
    os.rename(manager.root / env_id, retired)
    managed_cleanup.retire_owned_paths(manager, env_id, 1)
    os.rename(staging, manager.root / env_id)
    managed_cleanup.remember_path(manager, env_id, 2, manager.root / env_id)
    with RuntimeStoreLock(manager._lock):
        expected = manager._read()["environments"][env_id]
    candidate = {
        key: value for key, value in expected.items() if key != "last_install_operation"
    }
    candidate.update(
        {
            "revision": 2,
            "path": env_id,
            "status": "installed",
            "recipe": "rapidocr-cpu",
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
    os.rename(manager.root / env_id, staging)
    os.rename(retired, manager.root / env_id)

    listed = manager.list()
    environment = listed["environments"][0]
    assert environment["status"] == "installed"
    assert environment["revision"] == 2
    assert environment["path"] == str(manager.root / env_id)
    assert not manager._journal_path(env_id).exists()
    ledger = managed_cleanup.read_ledger(manager)
    assert str(Path(".retired") / env_id) not in ledger["paths"]
    assert str(Path(".retired") / env_id / "revisions" / "1") in ledger["paths"]
    assert (retired / "revisions" / "999-unknown" / "private.txt").exists()

    plan = manager.preview_install(env_id, "rapidocr-cpu", ("pypi",))
    with pytest.raises(RuntimeInstallError, match="退役槽位包含未记录数据"):
        manager.install(plan["plan_id"], env_id, "rapidocr-cpu", ("pypi",))
    assert (retired / "revisions" / "999-unknown" / "private.txt").exists()


def test_rollback_crash_without_mirrors_revokes_candidate_root(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # registry-only 旧环境（无历史 ledger 记录）：换入完成、候选整树归属已
    # 登记、回滚改名完成但归属恢复中断，且无退役镜像可判。恢复回滚必须
    # 按 candidate revision 撤销候选整树归属；原 fixed root 归属（新布局）
    # revision 更小，不得被误撤（旧实现在此遗留可删除整树归属）。
    manager, _calls = _manager(
        tmp_path,
        monkeypatch,
        with_base_pack=False,
        install=lambda _python, _scope, _endpoint: None,
    )
    env_id = "d" * 8 + "8adbc0de" + "0" * 16
    legacy_root = manager.root / env_id / "revisions" / "1"
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][env_id] = {
            "id": env_id,
            "name": "仅注册表",
            "revision": 1,
            "kind": "venv",
            "path": str(Path(env_id) / "revisions" / "1"),
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
        }
        _atomic_json(manager._registry, data)
    legacy_root.mkdir(parents=True)
    unknown = manager.root / env_id / "revisions" / "999-unknown"
    unknown.mkdir()
    (unknown / "private.txt").write_text("retain", encoding="utf-8")
    staging = manager._staging_path(env_id)
    retired = manager._retired_path(env_id)

    # 构造崩溃现场：换入完成（含候选根登记）、回滚改名完成、
    # restore 中断；ledger 无任何退役镜像。
    staging.mkdir(parents=True)
    (staging / "candidate.txt").write_text("candidate", encoding="utf-8")
    retired.parent.mkdir(parents=True, exist_ok=True)
    os.rename(manager.root / env_id, retired)
    managed_cleanup.retire_owned_paths(manager, env_id, 2)
    os.rename(staging, manager.root / env_id)
    managed_cleanup.remember_path(manager, env_id, 2, manager.root / env_id)
    with RuntimeStoreLock(manager._lock):
        expected = manager._read()["environments"][env_id]
    candidate = {
        key: value for key, value in expected.items() if key != "last_install_operation"
    }
    candidate.update({"revision": 2, "path": env_id})
    _atomic_json(
        manager._journal_path(env_id),
        {
            "schema_version": 1,
            "environment_id": env_id,
            "expected_record": expected,
            "candidate_record": candidate,
        },
    )
    os.rename(manager.root / env_id, staging)
    os.rename(retired, manager.root / env_id)

    ledger = managed_cleanup.read_ledger(manager)
    assert str(Path(env_id)) in ledger["paths"], "崩溃现场：候选整树归属仍在"
    assert not managed_cleanup.has_retired_mirrors(manager, env_id)

    # 注册表被外部修改 → 恢复按回滚收敛并撤销候选根归属。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][env_id]["name"] = "改名后"
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.list()
    assert listed["environments"][0]["path"] == str(
        manager.root / env_id / "revisions" / "1"
    )
    assert not manager._journal_path(env_id).exists()
    ledger = managed_cleanup.read_ledger(manager)
    assert str(Path(env_id)) not in ledger["paths"]
    assert (unknown / "private.txt").read_text(encoding="utf-8") == "retain"

    # 新布局环境：换入尚未开始（无 mirrors、无候选根），原 fixed root
    # 归属 revision 小于候选 revision，回滚收敛时不得被误撤。
    item = manager.create("新布局")
    staging = manager._staging_path(item["id"])
    staging.mkdir(parents=True)
    with RuntimeStoreLock(manager._lock):
        expected = manager._read()["environments"][item["id"]]
        candidate = {
            key: value
            for key, value in expected.items()
            if key != "last_install_operation"
        }
        candidate.update({"revision": expected["revision"] + 1, "path": item["id"]})
        _atomic_json(
            manager._journal_path(item["id"]),
            {
                "schema_version": 1,
                "environment_id": item["id"],
                "expected_record": expected,
                "candidate_record": candidate,
            },
        )
        expected_data = manager._read()
        expected_data["environments"][item["id"]]["name"] = "又改名"
        manager._registry.write_text(json.dumps(expected_data), encoding="utf-8")
    manager.list()
    assert not manager._journal_path(item["id"]).exists()
    ledger = managed_cleanup.read_ledger(manager)
    assert ledger["paths"][str(Path(item["id"]))]["revision"] == 1


def test_pending_swap_journal_protects_unique_old_copy_from_cleanup(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 可达故障：候选换入后回滚，retired→live 复位因目录句柄被占用而失败；
    # 恢复被吞后退役槽位里是唯一旧环境副本，不 busy、无引用。清理扫描
    # 必须凭 pending journal 保护该环境全部路径，且冻结证据随 journal
    # 变化失效，不能仅依赖先恢复。
    # 纯状态机验证：直接构造最小目录 + registry/ledger/journal 现场，
    # 不跑真实 venv 安装（真实安装/换入另行覆盖）。
    manager, _calls = _manager(
        tmp_path,
        monkeypatch,
        with_base_pack=False,
        install=lambda _python, _scope, _endpoint: None,
    )
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
            "recipe": "rapidocr-cpu",
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
                "recipe": "rapidocr-cpu",
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
    # 占住候选内部句柄：恢复的 staging→live 改名失败，journal 保留。
    lock_file = staging / "pyvenv.cfg"
    handle = lock_file.open("rb")
    try:
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
    finally:
        handle.close()
    # 阻塞解除后恢复前滚完成，journal 清除；旧预览因冻结证据变化失效。
    listed = manager.list()
    assert not manager._journal_path(env_id).exists()
    assert listed["environments"][0]["status"] == "installed"
    assert listed["environments"][0]["revision"] == 3
    with pytest.raises(RuntimeInstallPlanStale, match="preview again"):
        manager.run_cleanup(preview["plan_id"], [f"environment:{env_id}"])


def test_interrupted_fixed_path_swap_recovers_forward(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 崩溃发生在换入改名完成之后、注册表提交之前：journal 落盘，下一次
    # list() 前滚完成提交，环境落在固定路径且 journal 清除。
    manager, _calls = _manager(
        tmp_path,
        monkeypatch,
        with_base_pack=False,
        install=lambda _python, _scope, _endpoint: None,
    )
    item = manager.create("恢复")
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("tuna-pypi",))

    def crash_after_swap(env_id, expected, candidate, latest):
        from vibeocr.runtime.environments.runtime_maintenance import _atomic_json

        _atomic_json(
            manager._journal_path(env_id),
            {
                "schema_version": 1,
                "environment_id": env_id,
                "expected_record": expected,
                "candidate_record": candidate,
            },
        )
        manager._retired_path(env_id).parent.mkdir(parents=True, exist_ok=True)
        os.rename(manager.root / env_id, manager._retired_path(env_id))
        os.rename(manager._staging_path(env_id), manager.root / env_id)
        # KeyboardInterrupt 是 BaseException：绕过 install 的失败记录路径，
        # 忠实模拟进程在改名后、注册表提交前崩溃。
        raise KeyboardInterrupt("simulated crash after swap-in")

    monkeypatch.setattr(
        manager,
        "_swap_fixed_candidate",
        crash_after_swap,
    )
    original_swap = ManagedEnvironmentStore._swap_fixed_candidate
    with pytest.raises(KeyboardInterrupt):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",))
    monkeypatch.setattr(manager, "_swap_fixed_candidate", original_swap)
    assert manager._journal_path(item["id"]).is_file()

    listed = manager.list()
    environment = listed["environments"][0]
    assert environment["status"] == "installed"
    assert environment["revision"] == 2
    assert environment["path"] == str(manager.root / item["id"])
    assert not manager._journal_path(item["id"]).exists()
    assert (manager.root / ".retired" / item["id"]).is_dir()


def test_interrupted_fixed_path_swap_rolls_back_on_registry_divergence(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 直接构造换入后崩溃现场；真实 venv 的安装和迁移由相邻测试覆盖。
    manager, _calls = _manager(tmp_path, monkeypatch, with_base_pack=False)
    env_id = "回滚"
    live = manager.root / env_id
    retired = manager._retired_path(env_id)
    staging = manager._staging_path(env_id)
    live.mkdir(parents=True)
    retired.mkdir(parents=True)
    staging.parent.mkdir(parents=True)
    (live / "candidate.txt").write_text("new", encoding="utf-8")
    (retired / "original.txt").write_text("old", encoding="utf-8")
    expected = {
        "id": env_id,
        "name": "回滚",
        "revision": 1,
        "kind": "venv",
        "path": env_id,
        "status": "empty",
        "python_version": manager.manifest.python.version,
        "abi": manager.manifest.python.abi,
    }
    candidate = {**expected, "revision": 2, "status": "installed"}
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"][env_id] = {**expected, "name": "重命名后"}
        _atomic_json(manager._registry, data)
        managed_cleanup.remember_path(manager, env_id, 1, live)
        managed_cleanup.retire_owned_paths(manager, env_id, 2)
        managed_cleanup.remember_path(manager, env_id, 2, live)
        _atomic_json(
            manager._journal_path(env_id),
            {
                "schema_version": 1,
                "environment_id": env_id,
                "expected_record": expected,
                "candidate_record": candidate,
            },
        )

    manager.list()
    with RuntimeStoreLock(manager._lock):
        assert manager._read()["environments"][env_id] == {
            **expected,
            "name": "重命名后",
        }
    assert not manager._journal_path(env_id).exists()
    assert (live / "original.txt").read_text(encoding="utf-8") == "old"
    assert (staging / "candidate.txt").read_text(encoding="utf-8") == "new"
    assert not (live / "candidate.txt").exists()
    assert not retired.exists()
    ledger = managed_cleanup.read_ledger(manager)
    assert ledger["paths"][env_id]["revision"] == 1
