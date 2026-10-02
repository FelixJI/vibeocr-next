"""Real CPU/CUDA environment reuse smoke; creates only a fresh TEMP product."""

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

from vibeocr.runtime.environments import managed_environments, runtime_installer
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.managed_references import ManagedEnvironmentReferences


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-dir", type=Path, required=True)
    parser.add_argument("--component-lock", type=Path, required=True)
    args = parser.parse_args()
    root = Path(tempfile.mkdtemp(prefix="af135-ac2-"))
    evidence = {"state": "running", "root": str(root), "installed": [], "switches": []}
    output = root / "evidence.json"

    def save() -> None:
        output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n")
        print(json.dumps(evidence, ensure_ascii=False), flush=True)

    save()
    try:
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
                prepared = manager.prepare_switch(record["id"])
                proc, health = started(prepared)
                try:
                    committed = manager.commit_switch(prepared, started_health=health)
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
                    manager.prepare_switch(records[1]["id"])
                except ManagedEnvironmentError:
                    pass
                else:
                    raise AssertionError("active job reference was ignored")
                assert manager._read()["active_id"] == active
                evidence["reference_protection"] = True
            finally:
                references.release(job)

            prepared = manager.prepare_switch(records[1]["id"])
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
