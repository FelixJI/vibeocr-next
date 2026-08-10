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
    for command in (
        [sys.executable, "scripts/generate_brand_assets.py", "--check"],
        [sys.executable, "-m", "ruff", "check", "scripts", "tests/runtime"],
        [sys.executable, "-m", "ruff", "format", "--check", "scripts", "tests/runtime"],
        [sys.executable, "-m", "pytest", "tests/runtime"],
        [npm, "run", "format:check", "--prefix", WEB_ASSETS],
        [npm, "run", "lint", "--prefix", WEB_ASSETS],
        [npm, "run", "typecheck", "--prefix", WEB_ASSETS],
        [npm, "run", "test", "--prefix", WEB_ASSETS],
        [npm, "run", "test:visual", "--prefix", WEB_ASSETS],
        [npm, "run", "build", "--prefix", WEB_ASSETS],
    ):
        subprocess.run(command, cwd=ROOT, check=True)
    verify_web_assets(ROOT / WEB_ASSETS / "dist")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
