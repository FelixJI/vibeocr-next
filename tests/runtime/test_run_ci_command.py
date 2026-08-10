from __future__ import annotations

import subprocess

from scripts.run_ci_command import main


class _FakeProcess:
    def __init__(self, *, returncode: int = 0, times_out: bool = False) -> None:
        self.pid = 4321
        self.returncode = returncode
        self.times_out = times_out

    def wait(self, timeout: int) -> int:
        if self.times_out:
            raise subprocess.TimeoutExpired(["fake"], timeout)
        return self.returncode


def test_returns_the_child_exit_code(monkeypatch) -> None:
    monkeypatch.setattr(
        "scripts.run_ci_command.subprocess.Popen",
        lambda command: _FakeProcess(returncode=17),
    )

    assert main(["--label", "quality", "--timeout-seconds", "30", "--", "fake"]) == 17


def test_timeout_terminates_the_process_tree(monkeypatch) -> None:
    process = _FakeProcess(times_out=True)
    terminated: list[int] = []
    monkeypatch.setattr(
        "scripts.run_ci_command.subprocess.Popen", lambda command: process
    )
    monkeypatch.setattr(
        "scripts.run_ci_command.terminate_process_tree",
        lambda target: terminated.append(target.pid),
    )

    assert main(["--label", "platform", "--timeout-seconds", "30", "--", "fake"]) == 124
    assert terminated == [4321]
