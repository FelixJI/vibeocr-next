"""Update host/base profile dependencies without upgrading unrelated pins.

Rapid 专属锁（win-x64-rapid-cu13）已随引擎退役删除；本脚本覆盖 base
（Runtime 服务环境）、cpu/cu126（MinerU full 闭包）与 cu126-gpu（torch
运行时闭包）四个 host 域锁。uv 优先复用现有输出中的 pin，仅在输入
约束变化时重新解析；生成仍由 uv pip compile 完成，不手改锁。
"""

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
        output = directory / f"requirements-{profile}.lock"
        # uv 默认优先保留输出文件里的 pin；把旧锁再作为硬约束会阻止显式改版。
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
                "--custom-compile-command",
                "uv run python scripts/update_runtime_host_locks.py",
                "--output-file",
                str(output),
            ],
            cwd=ROOT,
            check=True,
            stdout=subprocess.DEVNULL,
        )
        validate_requirements_lock(
            ROOT / output,
            profile="win-x64-cu126" if profile == "win-x64-cu126-gpu" else profile,
            paddle_isolated=True,
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
