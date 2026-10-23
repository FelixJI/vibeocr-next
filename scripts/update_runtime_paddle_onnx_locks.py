"""Generate the three minimal Paddle ONNX closures without changing legacy pins."""

from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path

from vibeocr.runtime.environments.runtime_manifest import validate_requirements_lock

ROOT = Path(__file__).resolve().parents[1]
DEVICES = ("cpu", "cuda", "directml")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", choices=DEVICES, action="append")
    args = parser.parse_args()
    uv = shutil.which("uv")
    if uv is None:
        raise RuntimeError("uv is required to regenerate Runtime locks")
    for device in args.device or DEVICES:
        profile = f"win-x64-paddle-ort-{device}"
        directory = Path("config/runtime") / profile
        output = directory / f"requirements-{profile}.lock"
        subprocess.run(
            [
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
                "--constraints",
                "config/runtime/win-x64-paddle-cpu/requirements-win-x64-paddle-cpu.lock",
                "--custom-compile-command",
                "uv run python scripts/update_runtime_paddle_onnx_locks.py",
                "--output-file",
                str(output),
            ],
            cwd=ROOT,
            check=True,
            stdout=subprocess.DEVNULL,
        )
        validate_requirements_lock(ROOT / output, profile=profile)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
