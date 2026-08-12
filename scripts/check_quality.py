"""运行不依赖 workflow 的 Python 与 Web 质量门。"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

try:
    from scripts.verify_web_assets import verify_web_assets
except ModuleNotFoundError:  # 直接执行 scripts/check_quality.py
    from verify_web_assets import verify_web_assets

ROOT = Path(__file__).resolve().parents[1]
WEB_ASSETS = "src/dotnet/VibeOCR.App/WebAssets"


def resolve_executable(command: str) -> str:
    """Resolve Windows command shims such as npm.cmd without invoking a shell."""
    return shutil.which(command) or command


def main() -> int:
    npm = resolve_executable("npm")
    for label, command in (
        (
            "ruff-check",
            [sys.executable, "-m", "ruff", "check", "scripts", "tests/runtime"],
        ),
        (
            "ruff-format",
            [
                sys.executable,
                "-m",
                "ruff",
                "format",
                "--check",
                "scripts",
                "tests/runtime",
            ],
        ),
        ("pytest", [sys.executable, "-m", "pytest", "tests/runtime"]),
        ("web-format", [npm, "run", "format:check", "--prefix", WEB_ASSETS]),
        ("web-lint", [npm, "run", "lint", "--prefix", WEB_ASSETS]),
        ("web-typecheck", [npm, "run", "typecheck", "--prefix", WEB_ASSETS]),
        ("web-test", [npm, "run", "test", "--prefix", WEB_ASSETS]),
        ("web-visual", [npm, "run", "test:visual", "--prefix", WEB_ASSETS]),
        ("web-build", [npm, "run", "build", "--prefix", WEB_ASSETS]),
    ):
        print(f"::notice title=Quality stage::{label} started", flush=True)
        subprocess.run(command, cwd=ROOT, check=True)
        print(f"::notice title=Quality stage::{label} completed", flush=True)
    verify_web_assets(ROOT / WEB_ASSETS / "dist")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
