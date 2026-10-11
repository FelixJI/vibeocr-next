"""Real isolated cleanup smoke; creates only a fresh TEMP product.

Exercises the production ``preview_cleanup``/``run_cleanup`` plan and run
paths of one fresh Runtime store against real Windows files:

* production ``create`` builds real empty venvs and production
  ``remember_path`` records candidate revisions exactly like an install
  that crashed before committing (real candidate venv bytes on disk);
* the recorded residual and the seed cache's proven-but-unreferenced
  download artifacts are really deleted, while unknown directories,
  unproven artifacts and shared model/base caches stay protected;
* the frozen installer is cancelled through stdin and forcibly terminated
  after pending ownership is durable and deletion has actually started;
* genuine Windows open-file handles make deletion partially fail; item
  results stay queryable, the pending ledger survives, and a rebuilt
  manager previews again and finishes the interrupted removals.

Retained-environment Supervisor health and the synthetic OCR round trip
are proven by ``scripts/smoke_environment_reuse.py`` and the WinUI native
smoke; this script intentionally does not repeat them.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import tempfile
import time
from pathlib import Path

from run_ci_command import terminate_process_tree
from vibeocr.runtime.environments import managed_cleanup
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.runtime_installer import _parse_resolve_report
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock


def seed_installer_cache(product_root: Path, source: Path) -> dict:
    """Copy an existing installer cache; patch only the new copy.

    Legacy inputs gain ``ignore_installed`` only in the copy. Report file URIs
    belonging to the explicit source artifact directory move to the copied
    directory; network sources and digests stay unchanged. The source is read-only.
    """
    cache = product_root / "state" / "installer-cache"
    shutil.copytree(source, cache)
    relocated = 0
    for report_path in sorted((cache / "resolve").glob("*-report.json")):
        artifacts = _parse_resolve_report(
            report_path, source / "downloads" / "artifacts"
        )
        document = json.loads(report_path.read_text(encoding="utf-8"))
        changed = False
        for item, artifact in zip(document["install"], artifacts, strict=True):
            if artifact.url.startswith("file://"):
                copied = cache / "downloads" / "artifacts" / artifact.filename
                if not copied.is_file():
                    raise ValueError(
                        "synthetic cache report references a missing copied artifact"
                    )
                item["download_info"]["url"] = copied.resolve().as_uri()
                relocated += 1
                changed = True
        if changed:
            report_path.write_text(
                json.dumps(document, sort_keys=True), encoding="utf-8"
            )
    patched = 0
    for inputs_path in sorted((cache / "resolve").glob("*.inputs.json")):
        document = json.loads(inputs_path.read_text(encoding="utf-8"))
        if isinstance(document, dict) and "ignore_installed" not in document:
            document["ignore_installed"] = True
            inputs_path.write_text(
                json.dumps(document, sort_keys=True), encoding="utf-8"
            )
            patched += 1
    return {
        "source": str(source),
        "patched_ignore_installed": patched,
        "relocated_file_artifacts": relocated,
    }


def _create_candidate_venv(manager: ManagedEnvironmentStore, destination: Path) -> None:
    """Build the same real candidate revision bytes production install uses."""
    result = subprocess.run(
        [
            str(manager._base_python()),
            "-I",
            "-m",
            "venv",
            "--copies",
            "--without-pip",
            str(destination),
        ],
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        timeout=180,
    )
    if result.returncode:
        raise RuntimeError(
            f"candidate venv failed (exit {result.returncode}): {result.stderr}"
        )


def interrupt_cleanup(
    manager: ManagedEnvironmentStore, installer: Path, mode: str, log_root: Path
) -> dict:
    """Interrupt real frozen deletion only after observing durable ownership."""
    assert mode in {"cancel", "terminate"}
    assert manager.product_root == (log_root / "product").resolve()
    created = manager.create(f"中断验收-{mode}")
    env_id = created["id"]
    current = Path(created["path"])
    owned = [current]
    # Multiple owned revisions leave a real continuation boundary after rmtree.
    for revision in range(2, 9):
        path = manager.root / env_id / "revisions" / f"{revision}-0123456789abcdef"
        with RuntimeStoreLock(manager._lock):
            managed_cleanup.remember_path(manager, env_id, revision, path)
        owned.append(path)
    for path in owned:
        files = path / "interrupt-files"
        files.mkdir(parents=True)
        for index in range(1024):
            (files / f"{index:04d}.tmp").write_bytes(b"synthetic-cleanup-data")
    first_file = current / "interrupt-files" / "0000.tmp"
    last_file = owned[-1] / "interrupt-files" / "1023.tmp"
    unknown = manager.root / env_id / "revisions" / "99-fedcba9876543210"
    unknown.mkdir()
    marker = unknown / "keep.txt"
    marker.write_text("unowned", encoding="utf-8")
    preview = manager.preview_cleanup()
    item_id = f"environment:{env_id}"
    item = next(item for item in preview["items"] if item["id"] == item_id)
    assert item["can_clean"] is True, item
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "run_cleanup",
        "product_root": str(manager.product_root),
        "component_lock": str(manager.component_lock_path),
        "runtime_manifest": str(manager.manifest.path),
        "plan_id": preview["plan_id"],
        "item_ids": [item_id],
    }
    stdout_path = log_root / f"{mode}-installer.stdout.jsonl"
    stderr_path = log_root / f"{mode}-installer.stderr.log"
    with stdout_path.open("wb") as stdout, stderr_path.open("wb") as stderr:
        process = subprocess.Popen(
            [
                str(installer),
                "--environment-cancel-control",
                "--request-json",
                json.dumps(request, ensure_ascii=False),
            ],
            stdin=subprocess.PIPE,
            stdout=stdout,
            stderr=stderr,
            cwd=manager.product_root,
        )
        try:
            deadline = time.monotonic() + 120
            while True:
                pending = managed_cleanup.read_ledger(manager)["pending"].get(env_id)
                if (
                    pending is not None
                    and not first_file.exists()
                    and last_file.is_file()
                    and process.poll() is None
                ):
                    break
                assert process.poll() is None, (
                    f"{mode}: installer exited before interruption; see {stdout_path}"
                )
                assert time.monotonic() < deadline, (
                    f"{mode}: durable, live deletion window was not captured"
                )
                time.sleep(0.005)
            observed = {"state": pending["state"], "paths": list(pending["paths"])}
            if mode == "cancel":
                assert process.stdin is not None
                process.stdin.write(b"cancel\n")
                process.stdin.flush()
                process.stdin.close()
                assert process.wait(timeout=120) == 0, stderr_path
            else:
                terminate_process_tree(process)
                assert process.returncode != 0, (
                    "terminated installer exited successfully"
                )
        finally:
            terminate_process_tree(process)
            if process.stdin is not None and not process.stdin.closed:
                process.stdin.close()
    envelopes = [
        json.loads(line)
        for line in stdout_path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if mode == "cancel":
        assert {"environment_cancel": "accepted"} in envelopes, envelopes
        completed = next(
            envelope
            for envelope in envelopes
            if envelope.get("action") == "run_cleanup"
        )
        assert completed["result"]["items"][0]["state"] == "cancelled", completed
    rebuilt = ManagedEnvironmentStore(
        product_root=manager.product_root,
        component_lock=manager.component_lock_path,
        runtime_manifest=manager.manifest.path,
    )
    pending = managed_cleanup.read_ledger(rebuilt)["pending"].get(env_id)
    assert pending is not None and any(path.exists() for path in owned), pending
    assert env_id not in rebuilt._read()["environments"]
    assert marker.read_text(encoding="utf-8") == "unowned"
    preview = rebuilt.preview_cleanup()
    pending_id = f"pending:{env_id}"
    item = next(item for item in preview["items"] if item["id"] == pending_id)
    assert item["can_clean"] is True, item
    result = rebuilt.run_cleanup(preview["plan_id"], [pending_id])
    assert result["items"][0]["state"] == "deleted", result
    assert all(not path.exists() for path in owned)
    assert env_id not in managed_cleanup.read_ledger(rebuilt)["pending"]
    assert marker.read_text(encoding="utf-8") == "unowned"
    return {
        "pid": process.pid,
        "exit_code": process.returncode,
        "pending_observed": observed,
        "deletion_started_before_interrupt": True,
        "stdout": str(stdout_path),
        "stderr": str(stderr_path),
        "envelopes": envelopes,
        "resumed_result": result,
        "unknown_revision_preserved": str(unknown),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-dir", type=Path, required=True)
    parser.add_argument(
        "--component-lock",
        type=Path,
        required=True,
        help="component lock bound to this candidate runtime manifest, not the cache seed",
    )
    parser.add_argument(
        "--installer-cache-seed",
        type=Path,
        default=None,
        help=(
            "explicit synthetic installer-cache root; copied read-only into "
            "the fresh TEMP product, only the copy gains the #196 "
            "ignore_installed resolve-input field"
        ),
    )
    args = parser.parse_args()
    if (
        args.installer_cache_seed is not None
        and not (args.installer_cache_seed / "resolve").is_dir()
    ):
        raise SystemExit(
            "--installer-cache-seed must be an installer-cache root with resolve/"
        )
    args.runtime_dir = args.runtime_dir.resolve()
    args.component_lock = args.component_lock.resolve()
    installer = args.runtime_dir.parent / "installer" / "vibeocr-runtime-installer.exe"
    if not installer.is_file():
        raise SystemExit(f"candidate frozen installer missing: {installer}")
    root = Path(tempfile.mkdtemp(prefix="af135-c2-cleanup-"))
    product = root / "product"
    evidence = {"state": "running", "root": str(root), "phases": {}}
    output = root / "evidence.json"

    def save() -> None:
        output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n")
        print(json.dumps(evidence, ensure_ascii=False), flush=True)

    def record(phase: str, **payload) -> None:
        evidence["phases"].setdefault(phase, {}).update(payload)
        save()

    def build_manager() -> ManagedEnvironmentStore:
        return ManagedEnvironmentStore(
            product_root=product,
            component_lock=args.component_lock,
            runtime_manifest=args.runtime_dir / "runtime-manifest.json",
        )

    def env_relative(manager: ManagedEnvironmentStore, path: Path) -> str:
        return str(path.relative_to(manager.root))

    save()
    try:
        if args.installer_cache_seed is not None:
            evidence["installer_cache_seed"] = seed_installer_cache(
                product, args.installer_cache_seed.resolve()
            )
            save()
        manager = build_manager()
        evidence["source_sha"] = manager.manifest.source_commit

        # Real product state: a retained empty environment plus the durable
        # candidate records a crashed install would leave behind.
        retained = manager.create("保留环境")
        retained_id = retained["id"]
        residual_kept = manager.root / retained_id / "revisions" / "2-0123456789abcdef"
        residual_held = manager.root / retained_id / "revisions" / "3-0123456789abcdef"
        for revision, residual in ((2, residual_kept), (3, residual_held)):
            # Production order: remember_path under the store lock first,
            # then real candidate venv bytes; the revision is never committed.
            with RuntimeStoreLock(manager._lock):
                managed_cleanup.remember_path(manager, retained_id, revision, residual)
            _create_candidate_venv(manager, residual)

        # Unknown siblings never gain ownership from names alone.
        unknown_root = manager.root / "外部目录"
        unknown_root.mkdir()
        (unknown_root / "keep.txt").write_text("external", encoding="utf-8")
        unknown_revision = (
            manager.root / retained_id / "revisions" / "9-fedcba9876543210"
        )
        unknown_revision.mkdir()
        (unknown_revision / "keep.txt").write_text("old", encoding="utf-8")
        downloads = (
            manager.paths.state_root / "installer-cache" / "downloads" / "artifacts"
        )
        downloads.mkdir(parents=True, exist_ok=True)
        unproven = downloads / "unproven-0.0.0-py3-none-any.whl"
        unproven.write_bytes(b"not-a-real-wheel")
        record(
            "state",
            retained_environment=retained_id,
            residual_kept=env_relative(manager, residual_kept),
            residual_held=env_relative(manager, residual_held),
            unknown_paths=[
                env_relative(manager, unknown_root),
                env_relative(manager, unknown_revision),
            ],
        )

        preview = manager.preview_cleanup()
        items = {item["id"]: item for item in preview["items"]}
        residual_item = items[f"residual:{env_relative(manager, residual_kept)}"]
        assert residual_item["can_clean"] is True, residual_item
        cleanable_cache = [
            item
            for item in preview["items"]
            if item["category"] == "dependency_cache" and item["can_clean"]
        ]
        if args.installer_cache_seed is None:
            assert not cleanable_cache, preview["items"]
            record(
                "preview",
                plan_id=preview["plan_id"],
                dependency_cache_proof=(
                    "skipped: no --installer-cache-seed, no proven artifacts"
                ),
            )
        else:
            assert cleanable_cache, preview["items"]
            assert cleanable_cache[0]["path_count"] > 1, cleanable_cache[0]
        protected_cache = [
            item
            for item in preview["items"]
            if item["category"] == "dependency_cache" and not item["can_clean"]
        ]
        assert protected_cache, preview["items"]
        assert all(item["can_clean"] is False for item in protected_cache)
        unknown_items = [
            item for item in preview["items"] if item["category"] == "unknown"
        ]
        assert len(unknown_items) >= 2, preview["items"]
        assert all(item["can_clean"] is False for item in unknown_items)
        protected = {
            item["category"] for item in preview["items"] if not item["can_clean"]
        }
        assert {"models", "python_base", "cache", "legacy"} <= protected, protected
        record(
            "preview",
            plan_id=preview["plan_id"],
            cleanable_cache_group=(
                None
                if not cleanable_cache
                else {
                    "id": cleanable_cache[0]["id"],
                    "path_count": cleanable_cache[0]["path_count"],
                    "logical_bytes": cleanable_cache[0]["logical_bytes"],
                }
            ),
            protected_cache_reasons=[item["reason"] for item in protected_cache],
        )

        selected = [residual_item["id"]] + [item["id"] for item in cleanable_cache[:1]]
        result = manager.run_cleanup(preview["plan_id"], selected)
        outcome = {item["id"]: item for item in result["items"]}
        assert outcome[residual_item["id"]]["state"] == "deleted", result
        assert not residual_kept.exists()
        if cleanable_cache:
            assert outcome[cleanable_cache[0]["id"]]["state"] == "deleted", result
            # Disk truth: every proven-unreferenced artifact is really gone
            # while the unproven sibling stays protected on disk.
            assert sorted(path.name for path in downloads.iterdir()) == [unproven.name]
            assert any(
                (manager.paths.state_root / "installer-cache" / "resolve").glob(
                    "*-report.json"
                )
            )
        assert unknown_root.is_dir() and unknown_revision.is_dir()
        assert (manager.root / "python-base").is_dir()
        ledger = managed_cleanup.read_ledger(manager)
        assert retained_id not in ledger["pending"]
        assert env_relative(manager, residual_kept) not in ledger["paths"]
        assert env_relative(manager, residual_held) in ledger["paths"]
        record(
            "clean",
            removed_residual_bytes=outcome[residual_item["id"]][
                "removed_logical_bytes"
            ],
            removed_cache_bytes=(
                outcome[cleanable_cache[0]["id"]]["removed_logical_bytes"]
                if cleanable_cache
                else None
            ),
        )

        # Genuine Windows file occupation: open handles defeat deletion.
        held_residual = (residual_held / "pyvenv.cfg").open("r+b")
        doomed = manager.create("待删除环境")
        doomed_path = Path(doomed["path"])
        held_environment = (doomed_path / "pyvenv.cfg").open("r+b")
        try:
            preview = manager.preview_cleanup()
            items = {item["id"]: item for item in preview["items"]}
            held_item = items[f"residual:{env_relative(manager, residual_held)}"]
            assert held_item["can_clean"] is True, held_item
            environment_item = items[f"environment:{doomed['id']}"]
            assert environment_item["can_clean"] is True, environment_item
            result = manager.run_cleanup(
                preview["plan_id"], [environment_item["id"], held_item["id"]]
            )
            outcome = {item["id"]: item for item in result["items"]}
            assert outcome[environment_item["id"]]["state"] == "failed", result
            assert outcome[held_item["id"]]["state"] == "failed", result
            data = manager._read()
            assert doomed["id"] not in data["environments"]
            assert retained_id in data["environments"]
            ledger = managed_cleanup.read_ledger(manager)
            assert ledger["pending"][doomed["id"]]["state"] == "failed"
            assert doomed_path.exists() and residual_held.exists()
            record(
                "occupy",
                environment_result=outcome[environment_item["id"]],
                residual_result=outcome[held_item["id"]],
                pending_state=ledger["pending"][doomed["id"]]["state"],
            )
        finally:
            held_residual.close()
            held_environment.close()

        # Restart: a rebuilt manager re-previews and finishes the removal.
        manager = build_manager()
        preview = manager.preview_cleanup()
        assert preview["last_result"] is not None
        assert any(
            item["state"] == "failed" for item in preview["last_result"]["items"]
        )
        items = {item["id"]: item for item in preview["items"]}
        pending_item = items[f"pending:{doomed['id']}"]
        assert pending_item["can_clean"] is True, pending_item
        held_item = items[f"residual:{env_relative(manager, residual_held)}"]
        assert held_item["can_clean"] is True, held_item
        result = manager.run_cleanup(
            preview["plan_id"], [pending_item["id"], held_item["id"]]
        )
        outcome = {item["id"]: item for item in result["items"]}
        assert outcome[pending_item["id"]]["state"] == "deleted", result
        assert outcome[held_item["id"]]["state"] == "deleted", result
        assert not doomed_path.exists() and not residual_held.exists()
        ledger = managed_cleanup.read_ledger(manager)
        assert not ledger["pending"]
        assert set(ledger["paths"]) == {
            env_relative(manager, manager.root / retained_id / "revisions" / "1")
        }
        listing = manager.list()
        assert [entry["id"] for entry in listing["environments"]] == [retained_id]
        # Unknown directories, the unproven artifact and the shared Python
        # base all survive the finished cleanup; with a seed the resolve
        # reports stay on disk as well.
        assert unknown_root.is_dir() and unknown_revision.is_dir()
        assert unproven.is_file()
        assert (manager.root / "python-base").is_dir()
        if args.installer_cache_seed is not None:
            assert any(
                (manager.paths.state_root / "installer-cache" / "resolve").glob(
                    "*-report.json"
                )
            )
        record(
            "resume",
            plan_id=preview["plan_id"],
            last_result=preview["last_result"],
            environments=[entry["id"] for entry in listing["environments"]],
        )
        retained_path = Path(retained["path"])
        retained_config = (retained_path / "pyvenv.cfg").read_bytes()
        model_markers = [
            manager.paths.state_root / "model-cache" / "shared-smoke.txt",
            manager.paths.state_root
            / "environments"
            / retained_id
            / "private-smoke.txt",
        ]
        for marker in model_markers:
            marker.parent.mkdir(parents=True, exist_ok=True)
            marker.write_text("retained-model", encoding="utf-8")
        for mode in ("cancel", "terminate"):
            record(mode, **interrupt_cleanup(build_manager(), installer, mode, root))
            manager = build_manager()
            assert retained_id in manager._read()["environments"]
            assert (retained_path / "pyvenv.cfg").read_bytes() == retained_config
            assert all(
                marker.read_text(encoding="utf-8") == "retained-model"
                for marker in model_markers
            )
            assert unknown_root.is_dir() and unknown_revision.is_dir()
            assert unproven.is_file()
            assert (manager.root / "python-base").is_dir()
        evidence["not_covered_here"] = [
            "retained-environment Supervisor health and synthetic OCR: proven by "
            "scripts/smoke_environment_reuse.py and the WinUI native smoke",
            "retained-environment download-artifact retention reason: proven by "
            "scripts/smoke_environment_reuse.py after its CUDA cleanup",
            "host wire/bridge/WinUI cleanup surfaces: covered by repository "
            "tests and the native managed-environments smoke",
        ]
        evidence["state"] = "passed"
    except Exception as exc:
        evidence["state"] = "failed"
        evidence["error"] = f"{type(exc).__name__}: {exc}"
        raise
    finally:
        save()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
