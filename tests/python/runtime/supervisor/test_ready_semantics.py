"""Ready envelope must mean the supervisor is actually serving.

Regressions for the startup ordering bug where ``host.main`` emitted the ready
envelope before importing uvicorn, building the FastAPI app and starting to
listen: the parent (``InferenceSupervisorProcess.StartAsync`` followed by the
switch coordinator's health check) then blocked on a dropped SYN for the whole
import window. The contracts pinned here, through the real production spawn
arguments (``python -I -B -c runpy vibeocr.runtime.host.main``):

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
import sys
import threading
import urllib.error
import urllib.request
from pathlib import Path

import pytest
import vibeocr.runtime

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


def _spawn_supervisor(token: str, sup_root: str) -> subprocess.Popen[str]:
    code_root = Path(vibeocr.runtime.__file__).resolve().parents[1]
    argv = [
        sys.executable,
        "-I",
        "-B",
        "-c",
        "import os,runpy,sys;"
        "sys.path.insert(0,os.environ['VIBEOCR_PRODUCT_CODE_ROOT']);"
        "runpy.run_module('vibeocr.runtime.host.main',run_name='__main__')",
    ]
    env = dict(os.environ)
    env.update(
        {
            "VIBEOCR_SUP_TOKEN": token,
            "VIBEOCR_PRODUCT_CODE_ROOT": str(code_root),
            "VIBEOCR_SUP_ROOT": sup_root,
            "PYTHONIOENCODING": "utf-8",
        }
    )
    for noisy in (
        "VIBEOCR_SUPERVISOR_SOAK_CRASH_AFTER_READY",
        "VIBEOCR_SELF_TEST_SMOKE",
        "VIBEOCR_SELF_TEST_RESULT",
        "VIBEOCR_SUPERVISOR_SETTINGS",
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
) -> None:
    token = "r" * 64
    with _spawn_supervisor(token, str(tmp_path / "sup-root")) as proc:
        try:
            ready = json.loads(_read_ready_line(proc))
            with socket.create_connection(
                ("127.0.0.1", ready["port"]), timeout=CONNECT_BUDGET_SECONDS
            ):
                pass
            health = _get_health(ready["port"], token)
            assert health["ready"] is True
            assert health["instance_id"] == ready["instance_id"]
        finally:
            proc.kill()
            proc.wait(timeout=10)


def test_real_startup_failure_exits_without_ready_envelope(
    tmp_path: Path,
) -> None:
    # A real assembly failure (stager root cannot be created because the
    # configured VIBEOCR_SUP_ROOT is an existing file) must exit non-zero
    # without emitting any ready envelope: the parent treats "first stdout
    # line" as the serving contract, so a dead server must stay silent.
    stager_root = tmp_path / "sup-root-is-a-file"
    stager_root.write_text("not a directory", encoding="utf-8")
    with _spawn_supervisor("f" * 64, str(stager_root)) as proc:
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
