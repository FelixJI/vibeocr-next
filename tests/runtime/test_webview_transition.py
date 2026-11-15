from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from scripts import verify_velopack_portable_delta_e2e as portable


def test_rollback_preserves_forward_packages_and_uses_the_original_payload(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = tmp_path / "installed-old"
    packages = root / "packages"
    packages.mkdir(parents=True)
    (packages / "new-full.nupkg").write_bytes(b"new")
    old = tmp_path / "original-full.nupkg"
    old.write_bytes(b"original")
    calls = []

    def run(arguments: list[str], **kwargs: object) -> subprocess.CompletedProcess:
        calls.append((arguments, kwargs))
        assert not packages.exists()
        return subprocess.CompletedProcess(arguments, 0, b"applied", b"")

    monkeypatch.setattr(portable.subprocess, "run", run)
    environment = {"LOCALAPPDATA": str(tmp_path / "external")}
    portable._rollback_portable(root, tmp_path, old, environment)
    assert (tmp_path / "forward-packages/new-full.nupkg").read_bytes() == b"new"
    assert old.read_bytes() == b"original"
    assert calls == [
        (
            [
                str(root / "Update.exe"),
                "apply",
                "--norestart",
                "--package",
                str(tmp_path / "rollback-full.nupkg"),
            ],
            {"cwd": root, "env": environment, "capture_output": True, "timeout": 600},
        )
    ]


def test_rollback_rejects_an_unowned_installation_before_moving_files(
    tmp_path: Path,
) -> None:
    other = tmp_path / "user-installation"
    packages = other / "packages"
    packages.mkdir(parents=True)
    marker = packages / "keep"
    marker.write_bytes(b"keep")
    with pytest.raises(RuntimeError, match="harness-owned"):
        portable._rollback_portable(other, tmp_path, tmp_path / "old.nupkg", {})
    assert marker.read_bytes() == b"keep"


def test_rollback_propagates_updater_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = tmp_path / "installed-old"
    root.mkdir()
    old = tmp_path / "original-full.nupkg"
    old.write_bytes(b"historical baseline")

    def delete_input_and_fail(
        arguments: list[str], **kwargs: object
    ) -> subprocess.CompletedProcess:
        Path(arguments[-1]).unlink()
        return subprocess.CompletedProcess(arguments, 1, b"", b"failed")

    monkeypatch.setattr(portable.subprocess, "run", delete_input_and_fail)
    with pytest.raises(subprocess.CalledProcessError):
        portable._rollback_portable(root, tmp_path, old, {})
    assert old.read_bytes() == b"historical baseline"
    assert (tmp_path / "rollback-updater.log").read_bytes() == b"failed"
