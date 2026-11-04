"""装配 MinerU SDK 合同测试专用 venv（``build/mineru-sdk-contract-venv``）。

从统一推理 lock（pip-compile 格式）提取全部 ``name==version`` 作为
constraints，创建 Python 3.13 venv 并安装：

- ``mineru==<lock 版本> --no-deps``（版本只由 lock 决定）；
- facade 与真实 ``mineru.parser.api_server`` 的直接依赖（版本由
  constraints 钉死，传递依赖在 constraints 内解析）；
- ``pytest``（宽松版本，测试框架不进 constraints 体系）；
- 本项目 wheel ``--no-deps``（构建安装 ``vibeocr-next-runtime``）。

幂等：venv 可用、mineru 版本与 lock 一致且关键导入成功时跳过重建；
``--force`` 强制重建。只在固定输出目录 ``build/mineru-sdk-contract-venv``
内写文件。
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOCK_PATH = (
    ROOT
    / "config"
    / "runtime"
    / "win-x64-paddle-ort-directml"
    / "requirements-win-x64-paddle-ort-directml.lock"
)
VENV_DIR = ROOT / "build" / "mineru-sdk-contract-venv"

# facade（mineru.parser.api_client）与真实 api_server 子进程的直接依赖；
# 版本全部由 lock constraints 钉死，传递依赖在 constraints 内解析。
# json-repair/openai：mineru.backend.postprocess.llm_* 在 api_server 解析
# 路径顶层导入（flash 原生抽取也会走到该导入）。
DIRECT_DEPENDENCIES = [
    "docvortex",
    "PyYAML",
    "filelock",
    "httpx",
    "pydantic",
    "loguru",
    "fastapi",
    "fastapi-offline",
    "uvicorn",
    "python-docx",
    "lxml",
    "json-repair",
    "openai",
]

# pip-compile 输出行：`name==version \`（续行是 --hash/注释）。
_REQUIREMENT = re.compile(r"^([A-Za-z0-9][A-Za-z0-9._-]*)==([^\s\\]+)")


def parse_lock_constraints() -> tuple[dict[str, str], str]:
    """提取 lock 中的全部 name==version；返回 (映射, mineru 版本)。"""

    constraints: dict[str, str] = {}
    for line in LOCK_PATH.read_text(encoding="utf-8").splitlines():
        match = _REQUIREMENT.match(line)
        if match is not None:
            constraints[match.group(1)] = match.group(2)
    if not constraints:
        raise SystemExit(f"no requirements parsed from {LOCK_PATH}")
    try:
        mineru_version = constraints["mineru"]
    except KeyError:  # pragma: no cover - lock 结构变化时立即失败
        raise SystemExit("lock does not pin 'mineru'; refusing to assemble") from None
    return constraints, mineru_version


def venv_python() -> Path:
    return VENV_DIR / (
        "Scripts/python.exe" if sys.platform == "win32" else "bin/python"
    )


def run(command: list[str]) -> subprocess.CompletedProcess:
    printable = " ".join(str(part) for part in command)
    print(f"[prepare_mineru_sdk_tests] {printable}", flush=True)
    return subprocess.run(command, check=True)


def probe_imports(python: Path) -> bool:
    probe = (
        "import mineru.parser.api_client, "
        "vibeocr.runtime.recognition.mineru_api, pytest"
    )
    return (
        subprocess.run([str(python), "-c", probe], capture_output=True).returncode == 0
    )


def installed_version(python: Path, distribution: str) -> str | None:
    code = f"from importlib import metadata;print(metadata.version({distribution!r}))"
    result = subprocess.run([str(python), "-c", code], capture_output=True, text=True)
    return result.stdout.strip() if result.returncode == 0 else None


def assemble(constraints: dict[str, str], mineru_version: str, *, force: bool) -> None:
    python = venv_python()
    if (
        not force
        and python.is_file()
        and installed_version(python, "mineru") == mineru_version
        and probe_imports(python)
    ):
        print(
            f"[prepare_mineru_sdk_tests] venv already ready "
            f"(mineru=={mineru_version}); reuse it",
            flush=True,
        )
        return

    uv = shutil.which("uv")
    if uv is None:
        raise SystemExit(
            "uv is required to assemble the MinerU SDK contract venv "
            "(install it or run via the repository dev environment)"
        )

    # 走到重建分支（复用条件不满足或 --force）：先移除陈旧/半装配目录，
    # 再创建干净 venv，保证脚本可重复执行。
    if VENV_DIR.exists():
        shutil.rmtree(VENV_DIR)
    run([uv, "venv", "--python", "3.13", str(VENV_DIR)])
    constraints_file = VENV_DIR / "constraints.txt"
    constraints_file.write_text(
        "".join(
            f"{name}=={version}\n" for name, version in sorted(constraints.items())
        ),
        encoding="utf-8",
    )

    common = [uv, "pip", "install", "--python", str(python)]
    pinned = ["--constraints", str(constraints_file)]
    run([*common, *pinned, f"mineru=={mineru_version}", "--no-deps"])
    run([*common, *pinned, *DIRECT_DEPENDENCIES])
    run([*common, "pytest"])
    run([*common, "--no-deps", str(ROOT)])

    actual = installed_version(python, "mineru")
    if actual != mineru_version:
        raise SystemExit(
            f"assembled mineru=={actual} but lock pins mineru=={mineru_version}"
        )
    if not probe_imports(python):
        raise SystemExit(
            "assembled venv cannot import mineru.parser.api_client, "
            "vibeocr.runtime.recognition.mineru_api and pytest"
        )
    print(f"[prepare_mineru_sdk_tests] assembled mineru=={actual}", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--force", action="store_true", help="重建 venv 而不是复用已装配环境"
    )
    args = parser.parse_args()
    constraints, mineru_version = parse_lock_constraints()
    assemble(constraints, mineru_version, force=args.force)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
