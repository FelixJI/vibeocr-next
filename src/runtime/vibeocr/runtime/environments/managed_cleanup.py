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
            "原有 legacy 环境与回滚资源受保护"
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
            "环境仍在操作或被引用，受保护"
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
            "环境操作或任务仍在使用此目录"
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
                or set(raw_inputs) != {"lock", "endpoint", "ignore_installed"}
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
                "安装、切换或模型准备进行中"
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
            store.paths.state_root / "cleanup-plans" / f"{plan_id}.json",
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
                (
                    store.paths.state_root / "cleanup-plans" / f"{plan_id}.json"
                ).read_text(encoding="utf-8")
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
            try:
                if item["category"] == "environment" or item_id.startswith("pending:"):
                    paths = _unregister(store, item["environment_id"], data, ledger)
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
        with store._cleanup_cancel_gate:
            store._cleanup_terminal = True
        _atomic_json(
            store.paths.state_root / "cleanup-results.json",
            {"plan_id": plan_id, "items": results, "size_kind": "logical_bytes"},
        )
        return {"plan_id": plan_id, "items": results, "size_kind": "logical_bytes"}


def delete_environment(store: ManagedEnvironmentStore, env_id: str) -> None:
    preview = preview_cleanup(store)
    result = run_cleanup(store, preview["plan_id"], [f"environment:{env_id}"])
    if result["items"][0]["state"] != "deleted":
        raise RuntimeInstallError(result["items"][0]["detail"])
