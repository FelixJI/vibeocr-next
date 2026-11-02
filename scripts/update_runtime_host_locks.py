"""Update host/base profile dependencies without upgrading unrelated pins.

统一推理闭包后仅剩 base（Runtime 服务环境）一个 host 域锁；Paddle ONNX
与 MinerU 由 paddle-ort-directml 统一闭包承载（见
update_runtime_paddle_onnx_locks.py）。uv 优先复用现有输出中的 pin，仅在
输入约束变化时重新解析；生成仍由 uv pip compile 完成，不手改锁。
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path

from vibeocr.runtime.environments.runtime_manifest import validate_requirements_lock

ROOT = Path(__file__).resolve().parents[1]
PROFILES = ("win-x64-base",)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", choices=PROFILES, action="append")
    parser.add_argument(
        "--upgrade",
        action="store_true",
        help=(
            "allow upgrading every pin to its latest compatible version "
            "(otherwise uv prefers the pins already present in the output)"
        ),
    )
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
                *(["--upgrade"] if args.upgrade else []),
                "--custom-compile-command",
                "uv run python scripts/update_runtime_host_locks.py",
                "--output-file",
                str(output),
            ],
            cwd=ROOT,
            check=True,
            stdout=subprocess.DEVNULL,
        )
        validate_requirements_lock(ROOT / output, profile=profile, paddle_isolated=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
