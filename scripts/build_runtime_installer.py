"""Build the standalone frozen Runtime Installer executable."""

from __future__ import annotations

import argparse
import subprocess
import sys
from importlib.metadata import distribution
from pathlib import Path

from vibeocr.runtime.environments.bundled_uv import UV_VERSION, uv_executable

ROOT = Path(__file__).resolve().parents[1]
INSTALLER_EXECUTABLE_NAME = "vibeocr-runtime-installer.exe"


def build_runtime_installer(
    *,
    output_dir: Path,
    work_dir: Path,
) -> Path:
    """Freeze the installer EXE and stage it once under ``output_dir``.

    产品只携带这一份可执行安装器（manifest 直接绑定其字节摘要）；
    历史“单成员 zip”中间层已随去重移除。
    """
    uv_binary = uv_executable()
    uv_licenses = distribution("uv").locate_file(f"uv-{UV_VERSION}.dist-info/licenses")
    if not uv_licenses.is_dir():
        raise ValueError("locked uv license files are missing")
    dist = work_dir / "dist"
    build = work_dir / "build"
    spec = work_dir / "spec"
    for directory in (dist, build, spec):
        directory.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            sys.executable,
            "-m",
            "PyInstaller",
            "--noconfirm",
            "--clean",
            "--onefile",
            "--add-binary",
            f"{uv_binary};uv",
            "--add-data",
            f"{uv_licenses};uv/licenses",
            "--name",
            "vibeocr-runtime-installer",
            "--paths",
            str(ROOT / "src" / "runtime"),
            "--paths",
            str(ROOT / "contracts" / "runtime" / "python"),
            "--copy-metadata",
            "vibeocr-next-runtime",
            "--collect-submodules",
            "vibeocr.runtime.environments",
            "--collect-submodules",
            "vibeocr.runtime_contracts",
            "--collect-data",
            "vibeocr.runtime_contracts",
            "--collect-data",
            "vibeocr.runtime.environments",
            "--distpath",
            str(dist),
            "--workpath",
            str(build),
            "--specpath",
            str(spec),
            str(ROOT / "scripts" / "runtime_installer_entry.py"),
        ],
        cwd=ROOT,
        check=True,
    )
    output_dir.mkdir(parents=True, exist_ok=True)
    staged = output_dir / INSTALLER_EXECUTABLE_NAME
    staged.write_bytes((dist / INSTALLER_EXECUTABLE_NAME).read_bytes())
    return staged


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--work-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    print(
        build_runtime_installer(
            output_dir=args.output_dir,
            work_dir=args.work_dir,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
