"""已退役原生后端安装命令的诊断兼容入口。"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from vibeocr.runtime.environments.env_manager import (
    install_backend_dependencies,
)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="原生 Paddle 后端安装已退役，请通过受管理环境准备 ONNX 或独立 MinerU 配方",
    )
    parser.add_argument(
        "--profile",
        choices=("auto", "cpu", "gpu-cu126"),
        default="auto",
    )
    parser.add_argument(
        "--network",
        choices=("international", "domestic"),
        default="international",
    )
    parser.add_argument(
        "--python",
        type=Path,
        default=Path(sys.executable),
        help="兼容旧参数；此命令只返回退役诊断，不修改目标环境",
    )
    args = parser.parse_args(argv)

    def report(stage: str, message: str) -> None:
        print(f"[{stage}] {message}", flush=True)

    success, message = install_backend_dependencies(
        args.python,
        profile=args.profile,
        network_type=args.network,
        progress_callback=report,
    )
    print(message, file=sys.stdout if success else sys.stderr)
    return 0 if success else 1


if __name__ == "__main__":  # pragma: no cover - module entry point
    raise SystemExit(main())
