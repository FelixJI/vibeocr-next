"""Regenerate the standalone MinerU lock within the existing CPU pins."""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

from vibeocr.runtime.environments.runtime_manifest import validate_requirements_lock

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "config/runtime/win-x64-mineru-cpu/requirements.in"
CONSTRAINT = ROOT / "config/runtime/win-x64-cpu/requirements-win-x64-cpu.lock"
OUTPUT = ROOT / "config/runtime/win-x64-mineru-cpu/requirements-win-x64-mineru-cpu.lock"


def main() -> int:
    uv = shutil.which("uv")
    if uv is None:
        raise RuntimeError("uv is required to regenerate the MinerU lock")
    command = [
        uv,
        "pip",
        "compile",
        str(SOURCE.relative_to(ROOT)),
        "--no-config",
        "--no-sources",
        "--upgrade",
        "--python-version",
        "3.13",
        "--python-platform",
        "windows",
        "--generate-hashes",
        "--constraints",
        str(CONSTRAINT.relative_to(ROOT)),
        "--output-file",
        str(OUTPUT.relative_to(ROOT)),
    ]
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "MinerU lock resolution failed")
    validate_requirements_lock(OUTPUT, profile="win-x64-mineru-cpu")
    print(OUTPUT.relative_to(ROOT))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
