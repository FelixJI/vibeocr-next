"""Build and deterministically package the standalone Runtime Installer EXE."""

from __future__ import annotations

import argparse
import subprocess
import sys
import zipfile
from importlib.metadata import distribution
from pathlib import Path

from vibeocr.runtime.environments.bundled_uv import UV_VERSION, uv_executable

ROOT = Path(__file__).resolve().parents[1]
FIXED_ZIP_TIME = (1980, 1, 1, 0, 0, 0)


def package_runtime_installer(
    executable: Path,
    output_dir: Path,
    *,
    version: str,
) -> Path:
    executable = executable.resolve(strict=True)
    output_dir.mkdir(parents=True, exist_ok=True)
    target = output_dir / f"vibeocr-runtime-installer-v{version}-win-x64.zip"
    info = zipfile.ZipInfo(
        "runtime-installer/vibeocr-runtime-installer.exe",
        date_time=FIXED_ZIP_TIME,
    )
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o755 << 16
    with zipfile.ZipFile(target, mode="w", compresslevel=9) as archive:
        archive.writestr(info, executable.read_bytes())
    return target


def build_runtime_installer(
    *,
    output_dir: Path,
    work_dir: Path,
    version: str,
) -> tuple[Path, Path]:
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
    executable = dist / "vibeocr-runtime-installer.exe"
    archive = package_runtime_installer(
        executable,
        output_dir,
        version=version,
    )
    return executable, archive


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args(argv)
    executable, archive = build_runtime_installer(
        output_dir=args.output_dir,
        work_dir=args.work_dir,
        version=args.version,
    )
    for path in (executable, archive):
        print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
