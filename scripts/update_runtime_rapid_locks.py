"""Update Rapid model dependencies without upgrading unrelated profile pins."""

from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path

from vibeocr.runtime.environments.runtime_manifest import validate_requirements_lock

ROOT = Path(__file__).resolve().parents[1]
PROFILES = (
    "win-x64-base",
    "win-x64-cpu",
    "win-x64-cu126",
    "win-x64-cu126-gpu",
    "win-x64-rapid-cu13",
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", choices=PROFILES, action="append")
    args = parser.parse_args()
    uv = shutil.which("uv")
    if uv is None:
        raise RuntimeError("uv is required to regenerate Runtime locks")
    for profile in args.profile or PROFILES:
        directory = Path("config/runtime") / profile
        command = [
            uv,
            "pip",
            "compile",
            str(directory / "requirements.in"),
            "--no-config",
            "--no-sources",
            "--python-version",
            "3.13",
            "--python-platform",
            "windows",
            "--generate-hashes",
            "--custom-compile-command",
            "uv run python scripts/update_runtime_rapid_locks.py",
            "--output-file",
            str(directory / f"requirements-{profile}.lock"),
        ]
        if profile == "win-x64-rapid-cu13":
            command += [
                "--constraints",
                "config/runtime/win-x64-base/requirements-win-x64-base.lock",
            ]
        subprocess.run(command, cwd=ROOT, check=True)
        validate_requirements_lock(
            ROOT / directory / f"requirements-{profile}.lock",
            profile="win-x64-cu126" if profile == "win-x64-cu126-gpu" else profile,
            paddle_isolated=True,
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
