"""运行环境存储可读目录、Velopack current 布局与全局来源统一的回归。

用户实测症状：
- Velopack 便携布局（bundle 根只有 current/packages/state，portable-layout.json
  注册 products.next.root='.'）下，默认环境安装/启动失败，失败证据为
  "product Runtime code is unavailable"——_launch 曾把 runtime-code 硬拼在
  product_root/runtime/backend，而真实产物随 manifest 位于 current/ 内。
- 环境路径 state/environments/<uuid>/revisions/1 不再需要 UUID：新环境目录
  用名称/用途派生的可读 slug。
- 新 UI 只有一个全局来源设置：显式保存全局默认必须清除所有环境同 kind
  旧 override，否则隐性遮蔽新全局默认。
"""

from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path

import pytest
from test_runtime_installer import _omit_unused_pip_bootstrap, _release
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock

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
    assert Path(environment["path"]).parent.parent == (manager.root / "rapidocr-cpu")
    assert listed["active_id"] == "rapidocr-cpu"
    assert (manager.root / "rapidocr-cpu" / "revisions").is_dir()
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
    assert (manager.root / "工作环境" / "revisions" / "1").is_dir()

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
        assert (manager.root / record["id"] / "revisions" / "1").is_dir()


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
    assert environment["path"].endswith(str(Path("rapidocr-cpu") / "revisions" / "1"))
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
