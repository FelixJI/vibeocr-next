"""Ready envelope must mean the supervisor is actually serving.

Regressions for the startup ordering bug where ``host.main`` emitted the ready
envelope before importing uvicorn, building the FastAPI app and starting to
listen: the parent (``InferenceSupervisorProcess.StartAsync`` followed by the
switch coordinator's health check) then blocked on a dropped SYN for the whole
import window. The contracts pinned here, through the real production spawn
arguments (``python -I -B -m vibeocr.runtime.host.main``):

* once the first stdout line (the ready envelope) is readable, the loopback
  socket must already be listening (connect succeeds within a short budget);
* the authorized ``GET /v2/health`` then reports a serving instance matching
  the envelope;
* a real assembly failure exits non-zero without emitting any ready line.
"""

from __future__ import annotations

import json
import os
import socket
import subprocess
import threading
import urllib.error
import urllib.request
from pathlib import Path

import pytest

# The old implementation starts importing uvicorn only after emitting ready, so
# a connect issued right after the envelope hits a port that is bound but not
# listening: on Windows the SYN is dropped and only retried after the initial
# 1s RTO, and the import window itself measured >=0.67s, so old code can never
# accept within 0.5s. The fixed implementation emits ready from inside uvicorn's
# startup, after listening began, so connect completes in milliseconds. The
# budget therefore has an order of magnitude of headroom on both sides and does
# not depend on the machine being quiet for the health round trip itself.
CONNECT_BUDGET_SECONDS = 0.5
STARTUP_DEADLINE_SECONDS = 60.0
HEALTH_TIMEOUT_SECONDS = 5.0


def _spawn_supervisor(token: str, product_fixture) -> subprocess.Popen[str]:
    product, runtime, manifest, binding, _ = product_fixture
    argv = [str(runtime / "python.exe"), "-I", "-B", "-m", "vibeocr.runtime.host.main"]
    env = dict(os.environ)
    env.update(
        {
            "VIBEOCR_SUP_TOKEN": token,
            "VIBEOCR_PRODUCT_RUNTIME": "1",
            "VIBEOCR_PRODUCT_ROOT": str(product),
            "VIBEOCR_RUNTIME_MANIFEST": str(manifest),
            "VIBEOCR_COMPONENT_LOCK": str(binding),
            "PYTHONIOENCODING": "utf-8",
        }
    )
    for noisy in (
        "VIBEOCR_SUPERVISOR_SOAK_CRASH_AFTER_READY",
        "VIBEOCR_SELF_TEST_SMOKE",
        "VIBEOCR_SELF_TEST_RESULT",
        "VIBEOCR_SUPERVISOR_SETTINGS",
        "VIBEOCR_LAYOUT_MANIFEST",
    ):
        env.pop(noisy, None)
    return subprocess.Popen(
        argv,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def _read_ready_line(proc: subprocess.Popen[str]) -> str:
    lines: list[str] = []

    def read() -> None:
        lines.append(proc.stdout.readline())

    reader = threading.Thread(target=read, daemon=True)
    reader.start()
    reader.join(STARTUP_DEADLINE_SECONDS)
    if not lines:
        proc.kill()
        proc.wait(timeout=10)
        raise AssertionError(
            f"supervisor did not emit a ready envelope within "
            f"{STARTUP_DEADLINE_SECONDS}s"
        )
    line = lines[0]
    if not line.strip():
        _, stderr = proc.communicate(timeout=10)
        raise AssertionError(
            "supervisor exited before the ready envelope; stderr tail: "
            + (stderr or "")[-2000:]
        )
    return line


def _get_health(port: int, token: str) -> dict:
    request = urllib.request.Request(f"http://127.0.0.1:{port}/v2/health")
    request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(
            request, timeout=HEALTH_TIMEOUT_SECONDS
        ) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        raise AssertionError(f"/v2/health returned HTTP {error.code}") from error


def test_ready_envelope_means_listening_and_health_serves(
    tmp_path: Path,
    staged_runtime_product,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    token = "r" * 64
    outside_stager = tmp_path / "inherited-stager-root"
    monkeypatch.setenv("VIBEOCR_SUP_ROOT", str(outside_stager))
    product, _, _, _, _ = staged_runtime_product
    with _spawn_supervisor(token, staged_runtime_product) as proc:
        try:
            ready = json.loads(_read_ready_line(proc))
            staging = product / "state" / "vibeocr-next-supervisor-staging"
            roots = [
                path
                for path in staging.glob(f"instance-{ready['instance_id']}-*")
                if path.is_dir()
            ]
            assert len(roots) == 1 and roots[0].with_suffix(".lease").is_file()
            assert not outside_stager.exists()
            with socket.create_connection(
                ("127.0.0.1", ready["port"]), timeout=CONNECT_BUDGET_SECONDS
            ):
                pass
            health = _get_health(ready["port"], token)
            assert "runtime.component-selection.v1" not in ready["capabilities"]
            assert set(health["capabilities"]) == set(ready["capabilities"])
            assert {"ocr.engine-selection.v1", "runtime.download-sources.v1"} <= set(
                ready["capabilities"]
            )
            assert health["ready"] is True
            assert health["instance_id"] == ready["instance_id"]
        finally:
            proc.kill()
            proc.wait(timeout=10)


def test_real_startup_failure_exits_without_ready_envelope(
    staged_runtime_product,
) -> None:
    # The bound temporary directory must be usable before publishing ready.
    product, _, _, _, _ = staged_runtime_product
    blocked_temp = product / "state" / "recognition" / "temp"
    blocked_temp.parent.mkdir(parents=True)
    blocked_temp.write_text("not a directory", encoding="utf-8")
    with _spawn_supervisor("f" * 64, staged_runtime_product) as proc:
        try:
            stdout, _ = proc.communicate(timeout=STARTUP_DEADLINE_SECONDS)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=10)
            pytest.fail("failing supervisor did not exit")
        assert proc.returncode != 0
        assert not stdout.strip(), (
            "startup failure must not emit a ready envelope; "
            f"stdout was: {stdout[:200]!r}"
        )


def test_managed_staging_preserves_live_process_and_reclaims_killed_process(
    staged_runtime_product,
) -> None:
    product = staged_runtime_product[0]
    parent = product / "state" / "vibeocr-next-supervisor-staging"
    processes: list[subprocess.Popen[str]] = []

    def start() -> Path:
        proc = _spawn_supervisor("staging-lifecycle-test-token", staged_runtime_product)
        processes.append(proc)
        ready = json.loads(_read_ready_line(proc))
        roots = [
            path
            for path in parent.glob(f"instance-{ready['instance_id']}-*")
            if path.is_dir()
        ]
        assert len(roots) == 1
        return roots[0]

    try:
        first_root = start()
        first_asset = first_root / "job" / "results" / "synthetic.bin"
        first_asset.parent.mkdir(parents=True)
        first_asset.write_bytes(b"first owned result")
        second_root = start()
        second_asset = second_root / "job" / "results" / "synthetic.bin"
        second_asset.parent.mkdir(parents=True)
        second_asset.write_bytes(b"live owned result")
        assert first_asset.read_bytes() == b"first owned result"

        processes[0].kill()
        processes[0].wait(timeout=10)
        third_root = start()

        assert not first_root.exists()
        assert not first_root.with_suffix(".lease").exists()
        assert second_asset.read_bytes() == b"live owned result"
        assert second_root.with_suffix(".lease").is_file()
        assert third_root.with_suffix(".lease").is_file()
    finally:
        for proc in processes:
            if proc.poll() is None:
                proc.kill()
            proc.communicate(timeout=10)
