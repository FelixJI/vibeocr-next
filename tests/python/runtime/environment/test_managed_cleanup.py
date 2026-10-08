"""Cleanup contracts: explicit ownership, cross-process references and durable recovery."""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path
from uuid import uuid4

import pytest
from test_environment_store_layout import _manager
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.managed_references import ManagedEnvironmentReferences
from vibeocr.runtime.environments.runtime_install_plan import RuntimeInstallPlanStale
from vibeocr.runtime.environments.runtime_installer import (
    RuntimeInstallError,
    _lock_allowed_hashes,
)
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json


def _clean(store, *ids):
    plan = store.preview_cleanup()
    return store.run_cleanup(plan["plan_id"], list(ids))


def test_only_owned_environment_paths_are_removed(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    active = store.create("保留 CPU")
    store.commit_switch(store.prepare_switch(active["id"]))
    target = store.create("删除 GPU")
    unknown = store.root / target["id"] / "revisions" / "999-unknown"
    unknown.mkdir()
    (unknown / "private.txt").write_text("retain")
    assert (
        _clean(store, f"environment:{target['id']}")["items"][0]["state"] == "deleted"
    )
    assert target["id"] not in store._read()["environments"]
    assert Path(active["path"]).exists()
    assert (unknown / "private.txt").read_text() == "retain"
    assert any(
        item["category"] == "unknown" and not item["can_clean"]
        for item in store.preview_cleanup()["items"]
    )


def test_partial_failure_reopens_and_continues(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("待清理")
    original = shutil.rmtree

    def fail(path):
        (path / "pyvenv.cfg").unlink()
        raise PermissionError("fixture file in use")

    monkeypatch.setattr(shutil, "rmtree", fail)
    assert _clean(store, f"environment:{target['id']}")["items"][0]["state"] == "failed"
    assert target["id"] not in store._read()["environments"]
    assert Path(target["path"]).exists()
    monkeypatch.setattr(shutil, "rmtree", original)
    reopened = ManagedEnvironmentStore(
        product_root=store.product_root,
        component_lock=store.component_lock_path,
        runtime_manifest=store.manifest.path,
        base_python=sys._base_executable,
    )
    pending = next(
        item
        for item in reopened.preview_cleanup()["items"]
        if item["id"] == f"pending:{target['id']}"
    )
    assert "fixture file in use" in pending["last_error"]
    assert _clean(reopened, pending["id"])["items"][0]["state"] == "deleted"
    assert not Path(target["path"]).exists()


def test_pending_before_unregister_is_not_healthy(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("中断删除")
    relative = store._read()["environments"][target["id"]]["path"]
    path = store.paths.state_root / "environment-cleanup.json"
    ledger = json.loads(path.read_text(encoding="utf-8"))
    ledger["pending"][target["id"]] = {
        "name": target["name"],
        "paths": [relative],
        "state": "pending",
        "detail": "",
    }
    _atomic_json(path, ledger)
    assert (
        ManagedEnvironmentStore._probe(
            store, store._read()["environments"][target["id"]]
        )["reason"]
        == "cleanup_pending"
    )
    assert _clean(store, f"pending:{target['id']}")["items"][0]["state"] == "deleted"


def test_failed_install_candidate_is_recoverable(tmp_path, monkeypatch):
    def fail(_python, _scope, _endpoint):
        raise RuntimeInstallError("fixture install failure")

    store, _ = _manager(tmp_path, monkeypatch, install=fail)
    target = store.create("安装失败")
    plan = store.preview_install(target["id"], "rapidocr-cpu")
    with pytest.raises(RuntimeInstallError):
        store.install(plan["plan_id"], target["id"], "rapidocr-cpu")
    residual = next(
        item
        for item in store.preview_cleanup()["items"]
        if item["category"] == "residual"
    )
    assert residual["can_clean"]
    assert _clean(store, residual["id"])["items"][0]["state"] == "deleted"
    assert Path(target["path"]).exists()


def test_new_job_reference_invalidates_plan(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    active = store.create("活动")
    store.commit_switch(store.prepare_switch(active["id"]))
    target = store.create("闲置")
    plan = store.preview_cleanup()
    references = ManagedEnvironmentReferences(
        store._registry, store._lock, store._references, active["id"], 1
    )
    job = str(uuid4())
    references.admit(job)
    try:
        with pytest.raises(RuntimeInstallPlanStale):
            store.run_cleanup(plan["plan_id"], [f"environment:{target['id']}"])
    finally:
        references.release(job)
    assert Path(target["path"]).exists()


@pytest.mark.parametrize("lock_kind", ["install", "model", "switch"])
def test_new_operation_invalidates_plan(tmp_path, monkeypatch, lock_kind):
    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("目标")
    plan = store.preview_cleanup()
    if lock_kind == "model":
        guard = RuntimeStoreLock(
            store.paths.state_root / "model-cache" / "prepare.lock", timeout=0
        )
    elif lock_kind == "switch":
        guard = store.switch_reservation(target["id"])
    else:
        guard = store._target_operation(target["id"])
    with guard:
        with pytest.raises(RuntimeInstallPlanStale):
            store.run_cleanup(plan["plan_id"], [f"environment:{target['id']}"])
    assert Path(target["path"]).exists()


def _report(store, recipe, filename, name, digest):
    scope, _ = store._recipe(recipe)
    cache = store.paths.state_root / "installer-cache"
    report = cache / "resolve" / f"{scope.lock_path.stem}-report.json"
    report.parent.mkdir(parents=True, exist_ok=True)
    _atomic_json(
        report,
        {
            "install": [
                {
                    "metadata": {"name": name},
                    "download_info": {
                        "url": f"https://example.invalid/{filename}",
                        "archive_info": {"hashes": {"sha256": digest}},
                    },
                }
            ]
        },
    )
    _atomic_json(
        report.with_suffix(".inputs.json"),
        {
            "lock": scope.lock_path.read_text(),
            "endpoint": "https://example.invalid/simple",
            "ignore_installed": True,
        },
    )
    artifact = cache / "downloads" / "artifacts" / filename
    artifact.parent.mkdir(parents=True, exist_ok=True)
    artifact.write_bytes(b"known fixture bytes; cleanup does not hash them")
    return artifact


def test_shared_downloads_survive_unreferenced_cleanup(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    first, second, third = [
        store.create(name) for name in ("保留一", "保留二", "删除三")
    ]
    scope, _ = store._recipe("rapidocr-cpu")
    allowed = _lock_allowed_hashes(scope.lock_path)
    name, hashes = next(iter(allowed.items()))
    shared = _report(store, "rapidocr-cpu", "shared.whl", name, next(iter(hashes)))
    gpu_scope, _ = store._recipe("rapidocr+mineru-cuda")
    gpu_allowed = _lock_allowed_hashes(gpu_scope.lock_path)
    gpu_name = next(name for name in gpu_allowed if name not in allowed)
    unused = _report(
        store,
        "rapidocr+mineru-cuda",
        "unused.whl",
        gpu_name,
        next(iter(gpu_allowed[gpu_name])),
    )
    data = store._read()
    for item in (first, second):
        data["environments"][item["id"]].update(
            status="installed", recipe="rapidocr-cpu", recipe_lock=scope.sha256
        )
    _atomic_json(store._registry, data)
    assert _clean(store, f"environment:{third['id']}")["items"][0]["state"] == "deleted"
    plan = store.preview_cleanup()
    eligible = [
        item
        for item in plan["items"]
        if item["category"] == "dependency_cache" and item["can_clean"]
    ]
    protected = [
        item
        for item in plan["items"]
        if item["category"] == "dependency_cache" and not item["can_clean"]
    ]
    assert len(eligible) == 1 and any(
        "shared.whl" in path for item in protected for path in item["paths"]
    )
    assert (
        store.run_cleanup(plan["plan_id"], [eligible[0]["id"]])["items"][0][
            "removed_logical_bytes"
        ]
        > 0
    )
    assert shared.exists() and not unused.exists()
    assert Path(first["path"]).exists() and Path(second["path"]).exists()


def test_reparse_descendant_cannot_be_deleted(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("外部链接")
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "keep").write_text("private")
    child = Path(target["path"]) / "external"
    if sys.platform == "win32":
        subprocess.run(
            ["cmd", "/c", "mklink", "/J", str(child), str(outside)],
            capture_output=True,
            check=True,
        )
    else:
        child.symlink_to(outside, target_is_directory=True)
    try:
        item = next(
            item
            for item in store.preview_cleanup()["items"]
            if item["id"] == f"environment:{target['id']}"
        )
        assert not item["can_clean"]
        with pytest.raises(RuntimeInstallError):
            _clean(store, item["id"])
        assert (outside / "keep").read_text() == "private"
    finally:
        child.rmdir() if sys.platform == "win32" else child.unlink()


def test_cancel_before_delete_keeps_environment(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("取消")
    plan = store.preview_cleanup()
    receipts = []
    store.cancel_cleanup(receipts.append)
    result = store.run_cleanup(plan["plan_id"], [f"environment:{target['id']}"])
    assert receipts == [True]
    assert result["items"][0]["state"] == "cancelled"
    assert target["id"] in store._read()["environments"]


def test_stat_change_rejects_plan_without_hashing(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("目录变化")
    plan = store.preview_cleanup()
    (Path(target["path"]) / "new.txt").write_text("new evidence")
    with pytest.raises(RuntimeInstallPlanStale):
        store.run_cleanup(plan["plan_id"], [f"environment:{target['id']}"])


def test_guarded_host_holds_target_until_commit_and_unguarded_wire_fails(
    tmp_path, monkeypatch
):
    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("guarded")
    plan = store.preview_cleanup()
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "product_root": str(store.product_root),
        "component_lock": str(store.component_lock_path),
        "runtime_manifest": str(store.manifest.path),
        "action": "prepare_switch",
        "environment_id": target["id"],
    }
    command = [
        sys.executable,
        "-c",
        "from pathlib import Path; import sys; from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore; "
        "ManagedEnvironmentStore._expected_base_python=lambda self: Path(sys._base_executable); "
        "from vibeocr.runtime.environments.runtime_installer import main; sys.exit(main(sys.argv[1:]))",
        "--request-json",
        json.dumps(request),
    ]
    rejected = subprocess.run(
        command, capture_output=True, text=True, encoding="utf-8", timeout=20
    )
    assert rejected.returncode != 0 and "requires a held reservation" in rejected.stdout
    child = subprocess.Popen(
        command + ["--environment-switch-control"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    try:
        prepared = json.loads(child.stdout.readline())
        assert prepared.get("action") == "prepare_switch", prepared
        with pytest.raises(RuntimeInstallPlanStale):
            store.run_cleanup(plan["plan_id"], [f"environment:{target['id']}"])
        output, errors = child.communicate(
            json.dumps({"action": "commit_switch", "started_health": None}) + "\n",
            timeout=20,
        )
        assert child.returncode == 0, errors
        assert json.loads(output)["result"]["active_id"] == target["id"]
    finally:
        if child.poll() is None:
            child.kill()
            child.communicate(timeout=10)


def test_environment_and_residual_selection_does_not_double_count(
    tmp_path, monkeypatch
):
    from vibeocr.runtime.environments.managed_cleanup import remember_path

    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("重叠")
    residual = store.root / target["id"] / "revisions" / "2-aaaaaaaaaaaaaaaa"
    with RuntimeStoreLock(store._lock):
        remember_path(store, target["id"], 2, residual)
    residual.mkdir()
    (residual / "partial.bin").write_bytes(b"partial")
    plan = store.preview_cleanup()
    env = next(
        item for item in plan["items"] if item["id"] == f"environment:{target['id']}"
    )
    extra = next(item for item in plan["items"] if item["category"] == "residual")
    result = store.run_cleanup(plan["plan_id"], [extra["id"], env["id"]])
    assert all(item["state"] == "deleted" for item in result["items"])
    assert (
        sum(item["removed_logical_bytes"] for item in result["items"])
        == env["logical_bytes"]
    )
    assert store.preview_cleanup()["last_result"] == result


def test_artifact_filename_does_not_claim_directory_contents(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    scope, _ = store._recipe("rapidocr-cpu")
    name, hashes = next(iter(_lock_allowed_hashes(scope.lock_path).items()))
    path = _report(store, "rapidocr-cpu", "known.whl", name, next(iter(hashes)))
    path.unlink()
    path.mkdir()
    private = path / "unknown.txt"
    private.write_text("not a downloaded artifact")
    plan = store.preview_cleanup()
    item = next(
        item
        for item in plan["items"]
        if any("known.whl" in relative for relative in item["paths"])
    )
    assert not item["can_clean"]
    with pytest.raises(RuntimeInstallError):
        store.run_cleanup(plan["plan_id"], [item["id"]])
    assert private.read_text() == "not a downloaded artifact"


def test_new_preview_invalidates_previous_confirmation(tmp_path, monkeypatch):
    store, _ = _manager(tmp_path, monkeypatch)
    target = store.create("重新检查")
    old = store.preview_cleanup()
    current = store.preview_cleanup()
    selection = [f"environment:{target['id']}"]
    with pytest.raises(RuntimeInstallPlanStale):
        store.run_cleanup(old["plan_id"], selection)
    assert Path(target["path"]).exists()
    assert (
        store.run_cleanup(current["plan_id"], selection)["items"][0]["state"]
        == "deleted"
    )
