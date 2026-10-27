"""Contract tests for the Paddle ONNX lock generator CLI constraints."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

from scripts import update_runtime_paddle_onnx_locks as generator

DEVICES = ("cpu", "cuda", "directml")


def _capture_uv_commands(monkeypatch: pytest.MonkeyPatch) -> list[list[str]]:
    commands: list[list[str]] = []

    def fake_run(command: list[str], **_kwargs: object) -> None:
        commands.append(list(command))

    monkeypatch.setattr(generator.shutil, "which", lambda _: "uv")
    monkeypatch.setattr(generator.subprocess, "run", fake_run)
    return commands


def _uv_command(command: list[str]) -> list[str]:
    return command[command.index("pip") + 2 :]


@pytest.mark.parametrize("refresh", [False, True])
def test_refresh_pins_only_drops_constraints_and_never_upgrades(
    monkeypatch: pytest.MonkeyPatch, refresh: bool
) -> None:
    commands = _capture_uv_commands(monkeypatch)
    argv = ["update-runtime-paddle-onnx-locks"] + (
        ["--refresh-pins"] if refresh else []
    )
    monkeypatch.setattr(sys, "argv", argv)
    assert generator.main() == 0
    assert len(commands) == len(DEVICES)
    for command, device in zip(commands, DEVICES, strict=True):
        profile = f"win-x64-paddle-ort-{device}"
        directory = Path("config/runtime") / profile
        uv = _uv_command(command)
        assert str(directory / "requirements.in") in uv
        assert any(
            part == str(directory / f"requirements-{profile}.lock") for part in uv
        )
        # 无隐式 --upgrade：版本演进只能来自 requirements.in 收紧。
        assert "--upgrade" not in uv
        # 生成不变量保持不变。
        assert "--no-config" in uv
        assert "--generate-hashes" in uv
        if refresh:
            assert "--constraints" not in uv
        else:
            constraints_index = uv.index("--constraints")
            assert (
                Path(uv[constraints_index + 1])
                == directory / f"requirements-{profile}.lock"
            )


def test_refresh_pins_defaults_to_constraints_when_lock_missing(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """--constraints 只在锁存在时加入：缺锁（首次生成）必须同样无 constraints。"""
    commands = _capture_uv_commands(monkeypatch)
    real_is_file = generator.Path.is_file

    def missing_lock_is_file(self: Path) -> bool:
        if self.match("config/runtime/win-x64-paddle-ort-*/requirements-*.lock"):
            return False
        return real_is_file(self)

    monkeypatch.setattr(generator.Path, "is_file", missing_lock_is_file)
    monkeypatch.setattr(sys, "argv", ["update-runtime-paddle-onnx-locks"])
    assert generator.main() == 0
    for command in commands:
        assert "--constraints" not in _uv_command(command)
