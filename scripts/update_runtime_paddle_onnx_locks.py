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
        # #270：旧原生 paddle-cpu 锁已随产品退役删除。约束改用各设备现有的
        # ORT 锁自身：已锁版本全部保留（不因重生成顺带升级），仅在新增
        # 依赖时重新解析；生成仍由 uv pip compile 完成，不手改锁。
        constraints = ["--constraints", str(output)] if output.is_file() else []
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
                *constraints,
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
