"""Explicit, recoverable cleanup inside one Runtime store; no inferred ownership."""

from __future__ import annotations

import json
import os
import re
import shutil
from contextlib import ExitStack
from pathlib import Path
from typing import TYPE_CHECKING
from uuid import uuid4

from vibeocr.runtime.environments.managed_references import environment_has_references
from vibeocr.runtime.environments.runtime_install_plan import RuntimeInstallPlanStale
from vibeocr.runtime.environments.runtime_installer import (
    RuntimeInstallError,
    _lock_allowed_hashes,
    _normalize_dist_name,
    _parse_resolve_report,
)
from vibeocr.runtime.environments.runtime_lock import (
    RuntimeLockTimeout,
    RuntimeStoreLock,
)
from vibeocr.runtime.environments.runtime_maintenance import (
    _atomic_json,
    safe_runtime_detail,
)

if TYPE_CHECKING:
    from vibeocr.runtime.environments.managed_environments import (
        ManagedEnvironmentStore,
    )

CAPABILITY = "environment.cleanup.v1"
SWITCH_CAPABILITY = "environment.switch-reservation.v1"


def _ledger_path(store: ManagedEnvironmentStore) -> Path:
    return store.paths.state_root / "environment-cleanup.json"


def _owned_path(store: ManagedEnvironmentStore, relative: str, owner: dict) -> Path:
    if (
        not isinstance(owner, dict)
        or set(owner) != {"environment_id", "revision"}
        or not isinstance(owner["environment_id"], str)
        or type(owner["revision"]) is not int
        or owner["revision"] < 1
        or not isinstance(relative, str)
    ):
        raise RuntimeInstallError("cleanup ownership record is invalid")
    from vibeocr.runtime.environments.managed_environments import (
        _is_valid_environment_id,
    )

    if owner["environment_id"] == "legacy" or not _is_valid_environment_id(
        owner["environment_id"]
    ):
        raise RuntimeInstallError("cleanup owner is invalid")
    return store._safe_path(
        {
            "id": owner["environment_id"],
            "kind": "venv",
            "path": relative,
            "revision": owner["revision"],
        }
    )


def _pending_paths(ledger: dict) -> set[str]:
    """待完成删除仍引用的相对路径集合。"""
    return {
        relative
        for pending in ledger["pending"].values()
        for relative in pending["paths"]
    }


def _prune_owned_empty_parents(store: ManagedEnvironmentStore, keys) -> None:
    """按已登记叶路径回收可证明归属的空父容器。

    仅清理 keys 的祖先目录且仅在为空时 rmdir：未知内容（含空 sibling）
    的容器保留；公共槽位容器 .staging/.retired 不属于任何单环境，永不
    清理。rmdir/reparse 失败上抛，保留归属证据供重试。
    """
    from vibeocr.runtime.environments.managed_environments import (
        _SWAP_RETIRED_ROOT,
        _SWAP_STAGING_ROOT,
    )

    ancestors: set[Path] = set()
    for key in keys:
        parts = Path(key).parts
        for depth in range(1, len(parts)):
            ancestors.add(store.root.joinpath(*parts[:depth]))
    for directory in sorted(ancestors, key=lambda path: len(path.parts), reverse=True):
        if len(directory.relative_to(store.root).parts) == 1 and directory.name in (
            _SWAP_STAGING_ROOT,
            _SWAP_RETIRED_ROOT,
        ):
            continue
        if store._is_reparse(directory):
            raise RuntimeInstallError("路径包含符号链接或 reparse point，受保护")
        if directory.is_dir() and not any(directory.iterdir()):
            directory.rmdir()


def reclaim_slot(store: ManagedEnvironmentStore, env_id: str, path: Path) -> None:
    """安装事务复用固定换入槽位（.staging/.retired 下每环境一个）。

    仅回收 ledger 中由该环境拥有且 _tree 判定安全的路径，避免固定槽位
    无界累积；未知占用者 fail closed（提示走空间清理或人工检查），
    不做推断归属。调用方持有 store 锁。
    """
    relative = str(path.relative_to(store.root))
    ledger = read_ledger(store)
    owner = ledger["paths"].get(relative)
    if relative in _pending_paths(ledger):
        # 待完成删除仍引用该槽位：回收会道成 pending 孤儿引用并破坏
        # ledger 校验，fail closed。
        raise RuntimeInstallError("候选槽位仍被待完成删除引用；请先完成空间清理")
    if owner is None:
        if path.exists() or path.is_symlink():
            raise RuntimeInstallError(
                "候选槽位被未记录数据占用；请先运行空间清理或手动检查该目录"
            )
        return
    if path.exists() or path.is_symlink():
        _entries, unsafe = _tree(store, path)
        if unsafe:
            raise RuntimeInstallError(unsafe)
        shutil.rmtree(path)
    ledger["paths"].pop(relative, None)
    _atomic_json(_ledger_path(store), ledger)


def _retired_prefix(store: ManagedEnvironmentStore, env_id: str) -> Path:
    from vibeocr.runtime.environments.managed_environments import (
        _SWAP_RETIRED_ROOT,
    )

    return Path(_SWAP_RETIRED_ROOT) / env_id


def has_retired_mirrors(store: ManagedEnvironmentStore, env_id: str) -> bool:
    """ledger 中是否已有该环境的退役镜像记录。

    用于区分“换入尚未开始”与“回滚改名已完成但归属恢复被中断”：前者
    原归属仍在环境目录名下，后者镜像已存在而目录已复位。
    """
    retired = _retired_prefix(store, env_id)
    return any(
        owner["environment_id"] == env_id
        and len(Path(relative).parts) >= len(retired.parts)
        and Path(relative).parts[: len(retired.parts)] == tuple(retired.parts)
        for relative, owner in read_ledger(store)["paths"].items()
    )


def retire_owned_paths(
    store: ManagedEnvironmentStore, env_id: str, candidate_revision: int
) -> None:
    """环境目录已整体移入退役槽位后，镜像既有归属记录。

    只迁移 ledger 已登记的原归属路径（<id> 本身或 <id>/... 子路径）；
    中断回滚遗留的候选整树归属（revision == candidate_revision）被
    丢弃而不是映射，避免 legacy 树（含未知 sibling）获得整树可删除
    归属。未知/未登记数据随目录移动但不获得归属。调用方持有 store 锁。
    """
    ledger = read_ledger(store)
    retired = _retired_prefix(store, env_id)
    moved = False
    for relative in sorted(ledger["paths"], key=len, reverse=True):
        owner = ledger["paths"][relative]
        if owner["environment_id"] != env_id:
            continue
        parts = Path(relative).parts
        if parts == (env_id,) and owner["revision"] == candidate_revision:
            # 回滚中断遗留的候选整树归属：候选树在 staging，不随旧树
            # 迁入退役槽位，换入完成后会重新登记。
            ledger["paths"].pop(relative)
            moved = True
            continue
        if parts == (env_id,):
            mapped = retired
        elif len(parts) > 1 and parts[0] == env_id:
            mapped = retired.joinpath(*parts[1:])
        else:
            continue
        ledger["paths"].pop(relative)
        ledger["paths"][str(mapped)] = owner
        moved = True
    if moved:
        _atomic_json(_ledger_path(store), ledger)


def restore_owned_paths(
    store: ManagedEnvironmentStore, env_id: str, candidate_revision: int
) -> None:
    """退役树已移回环境目录后，把镜像归属记录逆映射回原路径。

    先移除换入成功时登记的候选整树归属（revision == candidate_revision），
    再恢复镜像的原归属；两者均可重入：中断后重跑同样收敛。
    """
    ledger = read_ledger(store)
    live_relative = str(Path(env_id))
    candidate_root = ledger["paths"].get(live_relative)
    if (
        candidate_root is not None
        and candidate_root["environment_id"] == env_id
        and candidate_root["revision"] == candidate_revision
    ):
        ledger["paths"].pop(live_relative)
        _atomic_json(_ledger_path(store), ledger)
    retired = _retired_prefix(store, env_id)
    moved = False
    for relative in sorted(ledger["paths"], key=len, reverse=True):
        owner = ledger["paths"][relative]
        if owner["environment_id"] != env_id:
            continue
        parts = Path(relative).parts
        if parts == tuple(retired.parts):
            mapped = Path(env_id)
        elif len(parts) > len(retired.parts) and parts[: len(retired.parts)] == tuple(
            retired.parts
        ):
            mapped = Path(env_id).joinpath(*parts[len(retired.parts) :])
        else:
            continue
        ledger["paths"].pop(relative)
        ledger["paths"][str(mapped)] = owner
        moved = True
    if moved:
        _atomic_json(_ledger_path(store), ledger)


def reclaim_retired(store: ManagedEnvironmentStore, env_id: str) -> None:
    """回收退役槽位：只删除已登记归属的子树，未知数据 fail closed。

    新布局环境的退役槽位拥有整树归属，直接回收；legacy 迁移的退役
    槽位逐个删除已镜像的子路径，剩余未登记内容保留并拒绝复用，
    由空间清理预览展示或人工检查。调用方持有 store 锁。
    """
    retired = store._retired_path(env_id)
    if not (retired.exists() or retired.is_symlink()):
        return
    relative = str(retired.relative_to(store.root))
    ledger = read_ledger(store)
    pending_paths = _pending_paths(ledger)
    blanket = ledger["paths"].get(relative)
    if blanket is not None:
        if relative in pending_paths:
            raise RuntimeInstallError("退役槽位仍被待完成删除引用；请先完成空间清理")
        _entries, unsafe = _tree(store, retired)
        if unsafe:
            raise RuntimeInstallError(unsafe)
        shutil.rmtree(retired)
        ledger["paths"].pop(relative, None)
        _atomic_json(_ledger_path(store), ledger)
        return
    retired_parts = Path(relative).parts
    owned = [
        key
        for key, owner in ledger["paths"].items()
        if owner["environment_id"] == env_id
        and len(Path(key).parts) > len(retired_parts)
        and Path(key).parts[: len(retired_parts)] == retired_parts
    ]
    if not owned:
        # 无任何归属证据：即使槽位为空也保留并阻止复用，与未知空目录
        # 同类边界；只有 owned 路径能证明父容器归属时才可清理。
        raise RuntimeInstallError(
            "退役槽位包含未记录数据；请先运行空间清理或手动检查该目录"
        )
    # 先删已登记子树；任何失败都不落 ledger，保留推导父容器归属的子路径
    # 证据，解除占用后重入按磁盘现状继续。
    for key in sorted(owned, key=len, reverse=True):
        if key in pending_paths:
            raise RuntimeInstallError("退役槽位仍被待完成删除引用；请先完成空间清理")
        child = store.root / key
        if child.exists() or child.is_symlink():
            _entries, unsafe = _tree(store, child)
            if unsafe:
                raise RuntimeInstallError(unsafe)
            shutil.rmtree(child)
    _prune_owned_empty_parents(store, owned)
    if retired.exists() or retired.is_symlink():
        if any(retired.iterdir()):
            raise RuntimeInstallError(
                "退役槽位包含未记录数据；请先运行空间清理或手动检查该目录"
            )
        retired.rmdir()
    # 全部清理成功后原子落 ledger；失败路径均保留原登记。
    for key in owned:
        ledger["paths"].pop(key, None)
    _atomic_json(_ledger_path(store), ledger)


def read_ledger(store: ManagedEnvironmentStore) -> dict:
    path = _ledger_path(store)
    if not path.exists():
        return {"schema_version": 1, "paths": {}, "pending": {}}
    data = json.loads(path.read_text(encoding="utf-8"))
    if (
        not isinstance(data, dict)
        or set(data) != {"schema_version", "paths", "pending"}
        or data["schema_version"] != 1
        or not isinstance(data["paths"], dict)
        or not isinstance(data["pending"], dict)
    ):
        raise RuntimeInstallError("cleanup journal is invalid")
    for relative, owner in data["paths"].items():
        _owned_path(store, relative, owner)
    for env_id, pending in data["pending"].items():
        if (
            not isinstance(pending, dict)
            or set(pending) != {"name", "paths", "state", "detail"}
            or not isinstance(pending["name"], str)
            or not isinstance(pending["paths"], list)
            or not pending["paths"]
            or len(set(pending["paths"])) != len(pending["paths"])
            or pending["state"] not in {"pending", "failed"}
            or not isinstance(pending["detail"], str)
            or len(pending["detail"]) > 4000
            or any(
                relative not in data["paths"]
                or data["paths"][relative]["environment_id"] != env_id
                for relative in pending["paths"]
            )
        ):
            raise RuntimeInstallError("pending environment cleanup is invalid")
    return data


def remember_path(
    store: ManagedEnvironmentStore, env_id: str, revision: int, path: Path
) -> None:
    """Called under the store lock, before any candidate files are created."""
    relative = str(path.relative_to(store.root))
    owner = {"environment_id": env_id, "revision": revision}
    _owned_path(store, relative, owner)
    ledger = read_ledger(store)
    ledger["paths"][relative] = owner
    _atomic_json(_ledger_path(store), ledger)


def _tree(store: ManagedEnvironmentStore, path: Path) -> tuple[list, str | None]:
    """Logical bytes and stat evidence; never follow a reparse point."""
    current = path
    boundary = store.paths.store_root
    while current != boundary:
        if current == current.parent or not current.is_relative_to(boundary):
            return [], "路径超出本产品管理范围"
        if store._is_reparse(current):
            return [], "路径包含符号链接或 reparse point，受保护"
        current = current.parent
    if not path.exists():
        return [], None
    entries = []
    try:
        candidates = [path]
        if path.is_dir():
            for directory, children, files in os.walk(path, followlinks=False):
                parent = Path(directory)
                for name in children + files:
                    child = parent / name
                    if store._is_reparse(child):
                        return [], "目录内包含符号链接或 reparse point，受保护"
                    candidates.append(child)
        for child in candidates:
            stat = child.stat()
            entries.append(
                [
                    str(child.relative_to(path)),
                    child.is_dir(),
                    stat.st_size if child.is_file() else 0,
                    stat.st_mtime_ns,
                ]
            )
    except OSError:
        return [], "无法完整读取目录，受保护"
    return sorted(entries), None


def _item(
    store: ManagedEnvironmentStore,
    item_id: str,
    category: str,
    label: str,
    paths: list[Path],
    reason: str | None = None,
    env_id: str | None = None,
) -> dict:
    evidence = []
    total = 0
    for path in paths if reason is None else []:
        entries, unsafe = _tree(store, path)
        evidence.append([str(path.relative_to(store.paths.store_root)), entries])
        if unsafe:
            reason = unsafe
        total += sum(entry[2] for entry in entries)
    return {
        "id": item_id,
        "category": category,
        "label": label,
        "environment_id": env_id,
        "logical_bytes": total if not reason else None,
        "can_clean": reason is None,
        "reason": reason or "本产品明确拥有且无保留引用；清理后需要时可重新准备",
        "paths": [str(path.relative_to(store.paths.store_root)) for path in paths],
        "evidence": evidence,
    }


def _busy(store: ManagedEnvironmentStore, env_id: str) -> bool:
    lock = RuntimeStoreLock(
        store.paths.locks_root / "environment-operations" / f"{env_id}.lock", timeout=0
    )
    try:
        lock.acquire()
    except RuntimeLockTimeout:
        return True
    lock.release()
    return False


def _model_busy(store: ManagedEnvironmentStore) -> bool:
    lock = RuntimeStoreLock(
        store.paths.state_root / "model-cache" / "prepare.lock", timeout=0
    )
    try:
        lock.acquire()
    except RuntimeLockTimeout:
        return True
    lock.release()
    return False


def _scan(store: ManagedEnvironmentStore, *, held: bool = False) -> dict:
    data = store._read()
    ledger = read_ledger(store)
    environments = data["environments"]
    swap_root = store._swap_journal_root
    # 待收敛的换入事务：即使进程已不在（不 busy、无引用），该环境的
    # 全部路径也必须受保护——退役槽位可能持有唯一旧环境副本。
    swapping = (
        {path.stem for path in swap_root.iterdir() if path.is_file()}
        if swap_root.is_dir()
        else set()
    )
    owners = {owner["environment_id"] for owner in ledger["paths"].values()} | set(
        environments
    )
    busy = {
        env_id: False if held else _busy(store, env_id) for env_id in sorted(owners)
    }
    references = {
        env_id: environment_has_references(store._references, env_id)
        for env_id in sorted(owners)
    }
    model_busy = False if held else _model_busy(store)
    shared_busy = any(busy.values()) or model_busy
    items = []
    claimed: set[Path] = set()
    for env_id, record in environments.items():
        if env_id in ledger["pending"]:
            continue
        path = store._safe_path(record)
        owned = [
            store.root / relative
            for relative, owner in ledger["paths"].items()
            if owner["environment_id"] == env_id
        ]
        paths = sorted(set(owned + [path])) if record["kind"] == "venv" else [path]
        claimed.update(paths)
        reason = (
            "环境换入事务未收敛，受保护"
            if env_id in swapping
            else "原有 legacy 环境与回滚资源受保护"
            if record["kind"] == "legacy"
            else "当前环境受保护；先切换到需要保留的配置"
            if env_id == data["active_id"]
            else "环境被任务引用，受保护"
            if references[env_id]
            else "环境正在安装、修复或切换，受保护"
            if busy[env_id]
            else None
        )
        items.append(
            _item(
                store,
                f"environment:{env_id}",
                "environment",
                record["name"],
                paths,
                reason,
                env_id,
            )
        )
    for env_id, pending in ledger["pending"].items():
        paths = [store.root / relative for relative in pending["paths"]]
        claimed.update(paths)
        reason = (
            "环境换入事务未收敛，受保护"
            if env_id in swapping
            else "环境仍在操作或被引用，受保护"
            if busy.get(env_id) or references.get(env_id)
            else None
        )
        if env_id in environments and env_id == data["active_id"]:
            reason = "当前环境受保护"
        item = _item(
            store,
            f"pending:{env_id}",
            "residual",
            f"待完成移除：{pending['name']}",
            paths,
            reason,
            env_id,
        )
        item["last_error"] = pending["detail"]
        items.append(item)
    current_paths = {store._safe_path(record) for record in environments.values()}
    pending_paths = {
        store.root / relative
        for pending in ledger["pending"].values()
        for relative in pending["paths"]
    }
    for relative, owner in ledger["paths"].items():
        path = store.root / relative
        claimed.add(path)
        if path in current_paths or path in pending_paths or not path.exists():
            continue
        env_id = owner["environment_id"]
        reason = (
            "环境换入事务未收敛，受保护"
            if env_id in swapping
            else "环境操作或任务仍在使用此目录"
            if busy.get(env_id) or references.get(env_id)
            else None
        )
        items.append(
            _item(
                store,
                f"residual:{relative}",
                "residual",
                "已记录的准备/安装/修复残留",
                [path],
                reason,
                env_id,
            )
        )
    # Unknown siblings are visible but their names never grant ownership.
    if store.root.is_dir():
        from vibeocr.runtime.environments.managed_environments import (
            _SWAP_RETIRED_ROOT,
            _SWAP_STAGING_ROOT,
        )

        for parent in store.root.iterdir():
            if parent.name == "python-base":
                continue
            if store._is_reparse(parent):
                items.append(
                    _item(
                        store,
                        f"unknown:{parent.name}",
                        "unknown",
                        parent.name,
                        [parent],
                        "未知或外部目录受保护",
                    )
                )
                continue
            if parent.name in (_SWAP_STAGING_ROOT, _SWAP_RETIRED_ROOT):
                if not parent.is_dir():
                    # 槽位容器是普通文件：作为受保护 unknown 展示，不得
                    # 对文件 iterdir。
                    items.append(
                        _item(
                            store,
                            f"unknown:{parent.name}",
                            "unknown",
                            parent.name,
                            [parent],
                            "未知或外部文件受保护",
                        )
                    )
                    continue
                # 换入槽位容器：逐子目录展示，未记录的占用者不授予所有权。
                for path in parent.iterdir():
                    if path not in claimed:
                        items.append(
                            _item(
                                store,
                                f"unknown:{parent.name}/{path.name}",
                                "unknown",
                                "未记录的旧目录",
                                [path],
                                "没有生产归属记录，保留",
                            )
                        )
                continue
            revisions = parent / "revisions"
            if revisions.is_dir() and not store._is_reparse(revisions):
                for path in revisions.iterdir():
                    if path not in claimed:
                        items.append(
                            _item(
                                store,
                                f"unknown:{parent.name}/{path.name}",
                                "unknown",
                                "未记录的旧目录",
                                [path],
                                "没有生产归属记录，保留",
                            )
                        )
            elif parent not in claimed:
                items.append(
                    _item(
                        store,
                        f"unknown:{parent.name}",
                        "unknown",
                        parent.name,
                        [parent],
                        "没有生产归属记录，保留",
                    )
                )
    cache = store.paths.state_root / "installer-cache"
    downloads = cache / "downloads" / "artifacts"
    reports = []
    proven: dict[str, set[str]] = {}
    retained: set[str] = set()
    unknown_reference = False
    for record in environments.values():
        if record["status"] != "installed":
            continue
        try:
            scope, _ = store._recipe(record["recipe"])
            if record.get("recipe_lock") != scope.sha256:
                unknown_reference = True
                continue
            retained.update(
                value
                for hashes in _lock_allowed_hashes(scope.lock_path).values()
                for value in hashes
            )
        except (KeyError, RuntimeInstallError):
            unknown_reference = True
    for recipe in store.recipe_catalog():
        scope, _ = store._recipe(recipe["id"])
        report = cache / "resolve" / f"{scope.lock_path.stem}-report.json"
        inputs = report.with_suffix(".inputs.json")
        if not report.is_file() or not inputs.is_file():
            continue
        try:
            if _tree(store, report)[1] or _tree(store, inputs)[1]:
                continue
            raw_inputs = json.loads(inputs.read_text(encoding="utf-8"))
            if (
                not isinstance(raw_inputs, dict)
                or set(raw_inputs)
                not in (
                    {"lock", "endpoint", "ignore_installed"},
                    {"lock", "endpoint", "ignore_installed", "executor", "target"},
                )
                or raw_inputs["lock"] != scope.lock_path.read_text(encoding="utf-8")
                or not isinstance(raw_inputs["endpoint"], str)
                or raw_inputs["ignore_installed"] is not True
            ):
                continue
            artifacts = _parse_resolve_report(report, downloads)
            allowed = _lock_allowed_hashes(scope.lock_path)
            if any(
                not artifact.sha256
                or artifact.sha256
                not in allowed.get(_normalize_dist_name(artifact.name), set())
                for artifact in artifacts
            ):
                continue
            reports.append(
                [
                    str(report.relative_to(store.paths.store_root)),
                    raw_inputs,
                    json.loads(report.read_text(encoding="utf-8")),
                ]
            )
            for artifact in artifacts:
                proven.setdefault(artifact.filename, set()).add(artifact.sha256)
        except (OSError, ValueError, RuntimeInstallError):
            continue
    if downloads.is_dir() and not store._is_reparse(downloads):
        for path in sorted(downloads.iterdir()):
            name = path.name.removesuffix(".part")
            digests = proven.get(name)
            reason = (
                "同名目录或特殊文件没有下载工件归属证明，保留"
                if not path.is_file()
                else "安装、切换或模型准备进行中"
                if shared_busy
                else "保留环境的旧配方引用无法完整证明，保留共享工件"
                if unknown_reference
                else "没有匹配 manifest/resolve report 的归属证据，保留"
                if not digests
                else "保留环境仍引用此下载工件；维持离线复用"
                if digests & retained
                else None
            )
            items.append(
                _item(
                    store,
                    f"download:{path.name}",
                    "dependency_cache",
                    path.name,
                    [path],
                    reason,
                )
            )
    for category, label, path in (
        ("python_base", "共享 Python 基础解释器", store.root / "python-base"),
        ("models", "共享模型及提供方缓存", store.paths.state_root / "model-cache"),
        (
            "models",
            "环境私有模型、任务数据与原生缓存",
            store.paths.state_root / "environments",
        ),
        ("cache", "安装器通用缓存与离线 runtime packs", cache),
        ("legacy", "原有 Runtime / rollback / previous", store.paths.runtime_root),
    ):
        items.append(
            _item(
                store,
                f"protected:{category}:{label}",
                category,
                label,
                [path],
                "模型/基础资源/原生缓存及旧数据的引用边界不完整，保留",
            )
        )
    groups: dict[str, dict] = {}
    ordinary = []
    for item in items:
        if item["category"] != "dependency_cache":
            ordinary.append(item)
            continue
        key = item["reason"]
        if key not in groups:
            groups[key] = {
                **item,
                "id": f"download-group:{len(groups)}",
                "label": "依赖下载工件",
                "paths": [],
                "evidence": [],
                "logical_bytes": 0 if item["can_clean"] else None,
            }
        group = groups[key]
        group["paths"].extend(item["paths"])
        group["evidence"].extend(item["evidence"])
        if group["logical_bytes"] is not None:
            group["logical_bytes"] += item["logical_bytes"] or 0
    for group in groups.values():
        group["label"] += f"（{len(group['paths'])} 项）"
    items = ordinary + list(groups.values())
    return {
        "registry": data,
        "ledger": ledger,
        "busy": busy,
        "references": references,
        "model_busy": model_busy,
        "pending_swaps": sorted(swapping),
        "reports": reports,
        "items": items,
    }


def _public(items: list[dict]) -> list[dict]:
    return [
        {
            **{
                key: value
                for key, value in item.items()
                if key not in {"evidence", "paths"}
            },
            "paths": item["paths"][:8],
            "path_count": len(item["paths"]),
        }
        for item in items
    ]


def _last_result(store: ManagedEnvironmentStore) -> dict | None:
    path = store.paths.state_root / "cleanup-results.json"
    if not path.exists():
        return None
    if _tree(store, path)[1]:
        raise RuntimeInstallError("cleanup results path is unsafe")
    result = json.loads(path.read_text(encoding="utf-8"))
    if (
        not isinstance(result, dict)
        or set(result) != {"plan_id", "items", "size_kind"}
        or not isinstance(result["plan_id"], str)
        or not re.fullmatch(r"[0-9a-f]{32}", result["plan_id"])
        or result["size_kind"] != "logical_bytes"
        or not isinstance(result["items"], list)
        or len(result["items"]) > 2048
    ):
        raise RuntimeInstallError("cleanup results record is invalid")
    for item in result["items"]:
        if (
            not isinstance(item, dict)
            or set(item) != {"id", "state", "detail", "removed_logical_bytes"}
            or not isinstance(item["id"], str)
            or not 1 <= len(item["id"]) <= 1024
            or item["state"] not in {"deleted", "failed", "cancelled"}
            or not isinstance(item["detail"], str)
            or len(item["detail"]) > 4100
            or type(item["removed_logical_bytes"]) is not int
            or item["removed_logical_bytes"] < 0
        ):
            raise RuntimeInstallError("cleanup item result is invalid")
    return result


def preview_cleanup(store: ManagedEnvironmentStore) -> dict:
    with RuntimeStoreLock(store._lock):
        scan = _scan(store)
        plan_id = uuid4().hex
        _atomic_json(
            store.paths.state_root / "cleanup-plan.json",
            {"plan_id": plan_id, "scan": scan},
        )
        return {
            "plan_id": plan_id,
            "items": _public(scan["items"]),
            "size_kind": "logical_bytes",
            "last_result": _last_result(store),
            "warning": "仅统计逻辑字节，不保证物理磁盘释放；删除环境不可撤销，清理缓存可能需要重新下载。",
        }


def _unregister(
    store: ManagedEnvironmentStore, env_id: str, data: dict, ledger: dict
) -> list[Path]:
    record = data["environments"].get(env_id)
    if record is not None:
        if (
            env_id == data["active_id"]
            or record["kind"] != "venv"
            or environment_has_references(store._references, env_id)
        ):
            raise RuntimeInstallError(
                "active, referenced or legacy environment cannot be deleted"
            )
        path = store._safe_path(record)
        relative = str(path.relative_to(store.root))
        ledger["paths"][relative] = {
            "environment_id": env_id,
            "revision": record["revision"],
        }
        paths = sorted(
            relative
            for relative, owner in ledger["paths"].items()
            if owner["environment_id"] == env_id
        )
        ledger["pending"][env_id] = {
            "name": record["name"],
            "paths": paths,
            "state": "pending",
            "detail": "",
        }
        _atomic_json(_ledger_path(store), ledger)
        # 删除即终结该对象的旧计划生命周期：随注册表记录一并撤销落盘
        # 安装计划与准备归属，同 id 同名重建的新对象不会被旧 plan 的
        # revision/CAS 冒认；失败时记录仍在注册表，删除重试会重做。
        store._forget_install_plan(env_id)
        targets = data.get("prepare_targets")
        if targets is not None:
            for recipe in [
                recipe
                for recipe, target in targets.items()
                if target["environment_id"] == env_id
            ]:
                del targets[recipe]
        del data["environments"][env_id]
        _atomic_json(store._registry, data)
    pending = ledger["pending"].get(env_id)
    if pending is None:
        raise RuntimeInstallError("no recoverable deletion record")
    return [store.root / relative for relative in pending["paths"]]


def run_cleanup(
    store: ManagedEnvironmentStore, plan_id: str, item_ids: list[str]
) -> dict:
    if (
        not re.fullmatch(r"[0-9a-f]{32}", plan_id)
        or not isinstance(item_ids, list)
        or not item_ids
        or len(item_ids) > 2048
        or any(not isinstance(item, str) for item in item_ids)
        or len(set(item_ids)) != len(item_ids)
    ):
        raise RuntimeInstallError("cleanup selection is invalid")
    with ExitStack() as locks:
        locks.enter_context(RuntimeStoreLock(store._lock, timeout=0))
        try:
            frozen = json.loads(
                (store.paths.state_root / "cleanup-plan.json").read_text(
                    encoding="utf-8"
                )
            )
        except (OSError, ValueError) as exc:
            raise RuntimeInstallPlanStale(
                "cleanup plan unavailable; preview again"
            ) from exc
        scan = _scan(store)
        if frozen != {"plan_id": plan_id, "scan": scan}:
            raise RuntimeInstallPlanStale(
                "cleanup resources or references changed; preview again"
            )
        chosen = {item["id"]: item for item in scan["items"]}
        if any(
            item not in chosen or not chosen[item]["can_clean"] for item in item_ids
        ):
            raise RuntimeInstallError("cleanup selection contains protected resources")
        for env_id in scan["busy"]:
            locks.enter_context(store._target_operation(env_id))
        locks.enter_context(
            RuntimeStoreLock(
                store.paths.state_root / "model-cache" / "prepare.lock", timeout=0
            )
        )
        # Admission uses the same store lock. Acquired target locks also cover installers and switch reservations.
        if (
            any(scan["references"].values())
            or any(scan["busy"].values())
            or scan["model_busy"]
        ):
            raise RuntimeInstallPlanStale(
                "environment/model operation in progress; preview again"
            )
        ledger = scan["ledger"]
        data = scan["registry"]
        results = []
        covered: dict[str, dict] = {}
        ordered = sorted(
            item_ids,
            key=lambda key: (
                0
                if chosen[key]["category"] == "environment"
                or key.startswith("pending:")
                else 1
            ),
        )
        for item_id in ordered:
            item = chosen[item_id]
            if item["environment_id"] in covered:
                previous = covered[item["environment_id"]]
                results.append(
                    {
                        "id": item_id,
                        "state": previous["state"],
                        "detail": "此残留包含在所选环境移除范围；逻辑字节计入环境项。"
                        + previous["detail"],
                        "removed_logical_bytes": 0,
                    }
                )
                continue
            if store._cleanup_cancelled.is_set():
                results.append(
                    {
                        "id": item_id,
                        "state": "cancelled",
                        "detail": "已取消；已记录的残留可重新检查并继续",
                        "removed_logical_bytes": 0,
                    }
                )
                continue
            paths = [store.paths.store_root / relative for relative in item["paths"]]
            removed = 0
            deleted_keys: list[str] = []
            deferred = False
            try:
                if item["category"] == "environment" or item_id.startswith("pending:"):
                    paths = _unregister(store, item["environment_id"], data, ledger)
                    deleted_keys = [str(path.relative_to(store.root)) for path in paths]
                    # 环境删除收尾：延迟落 ledger，先证明空父容器收尾成功，
                    # 失败保留 pending/paths 归属证据供重试。
                    deferred = True
                for path in paths:
                    before, unsafe = _tree(store, path)
                    if unsafe:
                        raise RuntimeInstallError(unsafe)
                    if store._cleanup_cancelled.is_set():
                        raise RuntimeInstallError(
                            "清理已取消；剩余项目可重新检查并继续"
                        )
                    try:
                        if path.is_dir():
                            shutil.rmtree(path)
                        elif path.exists():
                            path.unlink()
                    finally:
                        after, _ = _tree(store, path)
                        removed += max(
                            0,
                            sum(entry[2] for entry in before)
                            - sum(entry[2] for entry in after),
                        )
                    if path.is_relative_to(store.root):
                        relative = str(path.relative_to(store.root))
                        if not deferred:
                            ledger["paths"].pop(relative, None)
                            _atomic_json(_ledger_path(store), ledger)
                if deferred:
                    # 先回收已登记叶证明的空父容器（失败保留证据并上抛，
                    # 重入按磁盘现状继续）；成功后才一次性提交 ledger 与
                    # pending 移除。未知非空/未知空 sibling 保留。
                    _prune_owned_empty_parents(store, deleted_keys)
                    for relative in deleted_keys:
                        ledger["paths"].pop(relative, None)
                        pending = ledger["pending"].get(item["environment_id"])
                        if pending and relative in pending["paths"]:
                            pending["paths"].remove(relative)
                            if not pending["paths"]:
                                del ledger["pending"][item["environment_id"]]
                    _atomic_json(_ledger_path(store), ledger)
                results.append(
                    {
                        "id": item_id,
                        "state": "deleted",
                        "detail": "所选明确归属路径已移除；模型/未知目录保留",
                        "removed_logical_bytes": removed,
                    }
                )
            except (OSError, RuntimeInstallError) as exc:
                detail = safe_runtime_detail(str(exc))
                pending = ledger["pending"].get(item["environment_id"])
                if pending:
                    pending.update(state="failed", detail=detail)
                    _atomic_json(_ledger_path(store), ledger)
                results.append(
                    {
                        "id": item_id,
                        "state": "cancelled"
                        if store._cleanup_cancelled.is_set()
                        else "failed",
                        "detail": detail,
                        "removed_logical_bytes": removed,
                    }
                )
            if item["category"] == "environment" or item_id.startswith("pending:"):
                covered[item["environment_id"]] = results[-1]
        by_id = {item["id"]: item for item in results}
        results = [by_id[item_id] for item_id in item_ids]
        for result in results:
            result["detail"] = safe_runtime_detail(
                f"{chosen[result['id']]['label']}：{result['detail']}"
            )
        with store._cleanup_cancel_gate:
            store._cleanup_terminal = True
        _atomic_json(
            store.paths.state_root / "cleanup-results.json",
            {"plan_id": plan_id, "items": results, "size_kind": "logical_bytes"},
        )
        return {"plan_id": plan_id, "items": results, "size_kind": "logical_bytes"}


def delete_environment(store: ManagedEnvironmentStore, env_id: str) -> None:
    preview = preview_cleanup(store)
    items = {item["id"]: item for item in preview["items"]}
    # 与预览同源选择：优先注册表环境项，首轮失败后只剩待完成删除项时
    # 续用 pending 项；保持 can_clean 门禁，不绕过保护。
    chosen = next(
        (
            items[candidate_id]
            for candidate_id in (f"environment:{env_id}", f"pending:{env_id}")
            if candidate_id in items and items[candidate_id]["can_clean"]
        ),
        None,
    )
    if chosen is None:
        raise RuntimeInstallError("环境暂不可删除；请先完成空间清理或解除占用")
    result = run_cleanup(store, preview["plan_id"], [chosen["id"]])
    if result["items"][0]["state"] != "deleted":
        raise RuntimeInstallError(result["items"][0]["detail"])
