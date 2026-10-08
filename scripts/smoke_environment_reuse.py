"""Real CPU/CUDA environment reuse smoke; creates only a fresh TEMP product.

Optionally seeds the fresh product's installer cache from an explicit
read-only synthetic ``InstallerCacheRoot``; only the new copy receives the
#196 ``ignore_installed`` resolve-input field. After the reuse and
failed-switch checks the larger CUDA configuration is really removed via
the production cleanup plan/run path while the CPU environment stays
active, healthy and guarded.
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import secrets
import subprocess
import tempfile
import threading
import time
from pathlib import Path
from uuid import uuid4

from smoke_environment_cleanup import seed_installer_cache
from vibeocr.runtime.environments import managed_environments, runtime_installer
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.managed_references import ManagedEnvironmentReferences


def main() -> int:
    parser = argparse.ArgumentParser()
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
    root = Path(tempfile.mkdtemp(prefix="af135-ac2-"))
    evidence = {"state": "running", "root": str(root), "installed": [], "switches": []}
    output = root / "evidence.json"

    def save() -> None:
        output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n")
        print(json.dumps(evidence, ensure_ascii=False), flush=True)

    save()
    try:
        if args.installer_cache_seed is not None:
            evidence["installer_cache_seed"] = seed_installer_cache(
                root / "product", args.installer_cache_seed.resolve()
            )
            save()
        manager = ManagedEnvironmentStore(
            product_root=root / "product",
            component_lock=args.component_lock,
            runtime_manifest=args.runtime_dir / "runtime-manifest.json",
        )
        evidence["source_sha"] = manager.manifest.source_commit
        initial_calls = 0
        default_install = manager._install_runner

        def install(*params):
            nonlocal initial_calls
            initial_calls += 1
            return default_install(*params)

        manager._install_runner = install
        records = []
        for recipe in ("paddleocr-cpu", "paddleocr-cuda"):
            print(f"Preparing real {recipe}", flush=True)
            created = manager.create(recipe)
            plan = manager.preview_install(created["id"], recipe, ("tuna-pypi",))
            installed = manager.install(
                plan["plan_id"], created["id"], recipe, ("tuna-pypi",)
            )
            assert installed["status"] == "installed"
            assert (
                manager.find_compatible(recipe)["selected"]["environment_id"]
                == created["id"]
            )
            record = manager._read()["environments"][created["id"]]
            records.append(record)
            launch = manager._launch(record, installed["python"])
            result = subprocess.run(
                [
                    installed["python"],
                    "-I",
                    "-B",
                    "-c",
                    "import json,paddle; print(json.dumps({'cuda_compiled':paddle.is_compiled_with_cuda(),"
                    "'cuda_devices':paddle.device.cuda.device_count(),'version':paddle.__version__}))",
                ],
                env={**os.environ, **launch["environment"]},
                capture_output=True,
                text=True,
                check=True,
                timeout=60,
            )
            device = json.loads(result.stdout)
            assert device["cuda_compiled"] is recipe.endswith("cuda"), device
            if recipe.endswith("cuda"):
                assert device["cuda_devices"] > 0, device
            evidence["installed"].append(
                {
                    "id": record["id"],
                    "recipe": recipe,
                    "python": installed["python"],
                    "recipe_lock": record["recipe_lock"],
                    "device": device,
                }
            )
            save()

        # Delete one empty environment while both real engine configurations remain.
        disposable = manager.create("共享资源验收")
        model_markers = [
            manager.paths.state_root / "model-cache" / "shared-smoke.txt",
            *[
                manager.paths.state_root
                / "environments"
                / record["id"]
                / "private-smoke.txt"
                for record in records
            ],
        ]
        for marker in model_markers:
            marker.parent.mkdir(parents=True, exist_ok=True)
            marker.write_text("retained-model", encoding="utf-8")
        artifacts = (
            manager.paths.state_root / "installer-cache" / "downloads" / "artifacts"
        )
        artifact_files = sorted(path.name for path in artifacts.iterdir())
        assert artifact_files, (
            "real CPU/CUDA installs left no shared download artifacts"
        )
        preview = manager.preview_cleanup()
        retained_groups = [
            item
            for item in preview["items"]
            if item["category"] == "dependency_cache"
            and not item["can_clean"]
            and "仍引用此下载工件" in item["reason"]
        ]
        assert retained_groups, preview["items"]
        item_id = f"environment:{disposable['id']}"
        removed = manager.run_cleanup(preview["plan_id"], [item_id])
        assert removed["items"][0]["state"] == "deleted", removed
        assert not Path(disposable["path"]).exists()
        assert set(manager._read()["environments"]) == {
            record["id"] for record in records
        }
        assert all((manager.root / record["path"]).is_dir() for record in records)
        assert sorted(path.name for path in artifacts.iterdir()) == artifact_files
        assert all(
            marker.read_text(encoding="utf-8") == "retained-model"
            for marker in model_markers
        )
        evidence["two_retained_environments_cleanup"] = {
            "removed_environment": disposable["id"],
            "retained_environments": [record["id"] for record in records],
            "retained_artifact_count": len(artifact_files),
            "retained_download_groups": retained_groups,
            "retained_model_markers": [str(marker) for marker in model_markers],
        }
        save()

        evidence["initial_install_calls"] = initial_calls
        blocked = {
            "installer_calls": 0,
            "dependency_download_calls": 0,
            "install_command_calls": 0,
        }

        def deny(key):
            def closed(*_args, **_kwargs):
                blocked[key] += 1
                raise AssertionError(f"switch entered closed preparation path: {key}")

            return closed

        # Close the real dependency preparation graph only after both real installs.
        # Health probes and the local Supervisor health HTTP remain unmodified.
        manager._install_runner = deny("installer_calls")
        managed_environments._prepare_online_artifacts = deny(
            "dependency_download_calls"
        )
        managed_environments._run_install_command = deny("install_command_calls")
        runtime_installer._download_resolved_artifacts = deny(
            "dependency_download_calls"
        )
        evidence["reuse"] = blocked
        processes = []

        def started(prepared):
            launch = prepared["launch"]
            token = secrets.token_urlsafe(32)
            command = [
                launch["python_executable"],
                "-I",
                "-B",
                "-m",
                launch["supervisor_module"],
            ]
            stderr = (root / f"supervisor-{len(processes)}.log").open(
                "w", encoding="utf-8"
            )
            proc = subprocess.Popen(
                command,
                cwd=launch["working_directory"],
                env={
                    **os.environ,
                    **launch["environment"],
                    "VIBEOCR_SUP_TOKEN": token,
                    "VIBEOCR_SUP_ROOT": str(root / "supervisor"),
                },
                stdout=subprocess.PIPE,
                stderr=stderr,
                text=True,
                encoding="utf-8",
            )
            processes.append(proc)
            lines = queue.Queue()
            threading.Thread(
                target=lambda: lines.put(proc.stdout.readline()), daemon=True
            ).start()
            ready = json.loads(lines.get(timeout=90))
            health = {"port": ready["port"], "instance_id": ready["instance_id"]}
            deadline = time.monotonic() + 30
            while not manager._verified_health(health):
                if proc.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError(
                        f"Supervisor failed health: {proc.returncode}; see {stderr.name}"
                    )
                time.sleep(0.1)
            return proc, health

        def stop(proc):
            proc.terminate()
            try:
                proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=10)

        try:
            for record in (records[0], records[1], records[0], records[1], records[0]):
                with manager.switch_reservation(record["id"]) as prepared:
                    proc, health = started(prepared)
                    try:
                        committed = manager.commit_switch(
                            prepared, started_health=health
                        )
                        assert committed["active_id"] == record["id"]
                        evidence["switches"].append(
                            {
                                "recipe": record["recipe"],
                                "active_id": committed["active_id"],
                                "active_revision": committed["active_revision"],
                                "health": health,
                            }
                        )
                        save()
                    finally:
                        stop(proc)
            active = manager._read()["active_id"]
            references = ManagedEnvironmentReferences(
                manager._registry,
                manager._lock,
                manager._references,
                active,
                records[0]["revision"],
            )
            job = str(uuid4())
            references.admit(job)
            try:
                try:
                    with manager.switch_reservation(records[1]["id"]):
                        pass
                except ManagedEnvironmentError:
                    pass
                else:
                    raise AssertionError("active job reference was ignored")
                assert manager._read()["active_id"] == active
                evidence["reference_protection"] = True
            finally:
                references.release(job)

            with manager.switch_reservation(records[1]["id"]) as prepared:
                proc, health = started(prepared)
                stop(proc)
                try:
                    manager.commit_switch(prepared, started_health=health)
                except ManagedEnvironmentError:
                    pass
                else:
                    raise AssertionError("dead target Supervisor was committed")
            assert manager._read()["active_id"] == active
            evidence["failed_switch_preserves_active"] = True
            assert blocked == dict.fromkeys(blocked, 0), blocked
            evidence["dependency_payload_bytes"] = 0
            evidence["byte_evidence"] = (
                "All dependency preparation/download paths closed; no path invoked."
            )

            # Real cleanup: remove the larger CUDA configuration while the
            # CPU environment stays active; shared caches/models stay guarded.
            preview = manager.preview_cleanup()
            items = {item["id"]: item for item in preview["items"]}
            cuda_item = items[f"environment:{records[1]['id']}"]
            assert cuda_item["can_clean"] is True, cuda_item
            retained_groups = [
                item
                for item in preview["items"]
                if item["category"] == "dependency_cache" and not item["can_clean"]
            ]
            assert retained_groups, preview["items"]
            assert all("仍引用此下载工件" in item["reason"] for item in retained_groups)
            removed = manager.run_cleanup(preview["plan_id"], [cuda_item["id"]])
            assert removed["items"][0]["state"] == "deleted", removed
            data = manager._read()
            assert records[1]["id"] not in data["environments"]
            assert data["active_id"] == records[0]["id"]
            assert not (manager.root / records[1]["path"]).exists()
            after = manager.preview_cleanup()
            assert after["last_result"]["items"][0]["state"] == "deleted"
            still_retained = [
                item
                for item in after["items"]
                if item["category"] == "dependency_cache" and not item["can_clean"]
            ]
            assert still_retained, after["items"]
            assert all("仍引用此下载工件" in item["reason"] for item in still_retained)
            protected = {
                item["category"] for item in after["items"] if not item["can_clean"]
            }
            assert {"models", "python_base", "cache", "legacy"} <= protected
            assert all(
                marker.read_text(encoding="utf-8") == "retained-model"
                for marker in model_markers
            )
            evidence["cleanup"] = {
                "removed_environment": records[1]["id"],
                "removed_logical_bytes": removed["items"][0]["removed_logical_bytes"],
                "retained_download_groups": [
                    {
                        "id": item["id"],
                        "path_count": item["path_count"],
                        "reason": item["reason"],
                    }
                    for item in still_retained
                ],
                "active_after": data["active_id"],
            }
            save()
            with manager.switch_reservation(records[0]["id"]) as prepared:
                proc, health = started(prepared)
                try:
                    committed = manager.commit_switch(prepared, started_health=health)
                    assert committed["active_id"] == records[0]["id"]
                finally:
                    stop(proc)
            assert blocked == dict.fromkeys(blocked, 0), blocked
            evidence["retained_cpu_healthy_after_cleanup"] = True
            evidence["state"] = "passed"
        finally:
            for proc in processes:
                if proc.poll() is None:
                    stop(proc)
    except Exception as exc:
        evidence["state"] = "failed"
        evidence["error"] = f"{type(exc).__name__}: {exc}"
        raise
    finally:
        save()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
