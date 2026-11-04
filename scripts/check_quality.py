"""运行不依赖 workflow 的 Python 与 Web 质量门。"""

from __future__ import annotations

import os
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
PYTHON_SOURCES = [
    "scripts",
    "src/runtime",
    "contracts/runtime/python",
    "tests/runtime",
    "tests/python",
]
MINERU_SDK_CONTRACTS = "tests/python/runtime/services/mineru_sdk_contract"


def resolve_executable(command: str) -> str:
    """Resolve Windows command shims such as npm.cmd without invoking a shell."""
    return shutil.which(command) or command


def run_mineru_sdk_contract_tests() -> None:
    """在装配的专用 venv 中运行真实 MinerU SDK 合同测试。

    ``VIBEOCR_SKIP_MINERU_SDK_CONTRACTS=1`` 显式跳过（离线开发）；装配或
    测试失败（uv 不可用、网络失败、用例失败）都直接失败，不静默降级。
    """
    label = "mineru-sdk-contracts"
    if os.environ.get("VIBEOCR_SKIP_MINERU_SDK_CONTRACTS") == "1":
        print(
            "::warning title=Quality stage::SKIPPED: MinerU SDK contract tests "
            "(VIBEOCR_SKIP_MINERU_SDK_CONTRACTS=1)",
            flush=True,
        )
        return
    print(f"::notice title=Quality stage::{label} started", flush=True)
    subprocess.run(
        [sys.executable, "scripts/prepare_mineru_sdk_tests.py"], cwd=ROOT, check=True
    )
    venv_python = (
        ROOT
        / "build"
        / "mineru-sdk-contract-venv"
        / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
    )
    environment = {**os.environ, "VIBEOCR_MINERU_SDK_CONTRACTS": "1"}
    subprocess.run(
        [
            str(venv_python),
            "-m",
            "pytest",
            MINERU_SDK_CONTRACTS,
            "-p",
            "no:cacheprovider",
        ],
        cwd=ROOT,
        check=True,
        env=environment,
    )
    print(f"::notice title=Quality stage::{label} completed", flush=True)


def main() -> int:
    npm = resolve_executable("npm")
    stages: list[tuple[str, list[str]]] = [
        (
            "ruff-check",
            [sys.executable, "-m", "ruff", "check", *PYTHON_SOURCES],
        ),
        (
            "ruff-format",
            [
                sys.executable,
                "-m",
                "ruff",
                "format",
                "--check",
                *PYTHON_SOURCES,
            ],
        ),
        (
            "protocol-generate",
            [sys.executable, "scripts/generate_runtime_protocol.py", "--check"],
        ),
        ("openapi", [sys.executable, "scripts/check_openapi_quality.py"]),
        (
            "toolbar-icons",
            [sys.executable, "scripts/generate_toolbar_icons.py", "--check"],
        ),
        (
            "protocol-conformance",
            [sys.executable, "scripts/check_runtime_protocol_conformance.py"],
        ),
        ("pytest", [sys.executable, "-m", "pytest", "tests/runtime", "tests/python"]),
    ]
    for label, command in stages:
        print(f"::notice title=Quality stage::{label} started", flush=True)
        subprocess.run(command, cwd=ROOT, check=True)
        print(f"::notice title=Quality stage::{label} completed", flush=True)
    # 根 pytest 之后：真实 MinerU SDK 合同在专用 venv 中运行。
    run_mineru_sdk_contract_tests()
    for label, command in (
        ("web-format", [npm, "run", "format:check", "--prefix", WEB_ASSETS]),
        ("web-lint", [npm, "run", "lint", "--prefix", WEB_ASSETS]),
        ("web-typecheck", [npm, "run", "typecheck", "--prefix", WEB_ASSETS]),
        ("web-test", [npm, "run", "test", "--prefix", WEB_ASSETS]),
        ("web-visual", [npm, "run", "test:visual", "--prefix", WEB_ASSETS]),
    ):
        print(f"::notice title=Quality stage::{label} started", flush=True)
        subprocess.run(command, cwd=ROOT, check=True)
        print(f"::notice title=Quality stage::{label} completed", flush=True)
    # test:visual 已构建并测试这份生产资产，直接校验它而不重新构建。
    verify_web_assets(ROOT / WEB_ASSETS / "dist")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
