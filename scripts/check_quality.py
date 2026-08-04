"""运行不依赖 workflow 的 Python 与 Web 质量门。"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def resolve_executable(command: str) -> str:
    """Resolve Windows command shims such as npm.cmd without invoking a shell."""
    return shutil.which(command) or command


def main() -> int:
    for command in (
        [sys.executable, "-m", "ruff", "check", "scripts", "tests/runtime"],
        [sys.executable, "-m", "ruff", "format", "--check", "scripts", "tests/runtime"],
        [sys.executable, "-m", "pytest", "tests/runtime"],
        [
            resolve_executable("npm"),
            "test",
            "--prefix",
            "src/dotnet/VibeOCR.App/WebAssets",
        ],
    ):
        subprocess.run(command, cwd=ROOT, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
