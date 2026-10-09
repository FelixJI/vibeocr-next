"""Fixed installer tool: frozen binary or the locked development distribution."""

from __future__ import annotations

import os
import re
import subprocess
import sys
from importlib.metadata import PackageNotFoundError, distribution
from pathlib import Path

UV_VERSION = "0.12.22"


def uv_executable() -> Path:
    if getattr(sys, "frozen", False):
        root = getattr(sys, "_MEIPASS", None)
        if root is None:
            raise ValueError("bundled uv extraction directory is unavailable")
        executable = Path(root) / "uv" / "uv.exe"
    else:
        try:
            package = distribution("uv")
        except PackageNotFoundError:
            raise ValueError(
                "locked uv is missing; run uv sync --frozen --group build"
            ) from None
        if package.version != UV_VERSION:
            raise ValueError(
                f"installer requires uv {UV_VERSION}; restore the locked tools"
            )
        # Only the binary recorded by this distribution is eligible, including in development.
        binary_name = "uv.exe" if os.name == "nt" else "uv"
        binaries = [entry for entry in package.files or () if entry.name == binary_name]
        if len(binaries) != 1:
            raise ValueError(
                "locked uv executable is missing; restore the locked tools"
            )
        executable = Path(package.locate_file(binaries[0])).resolve()
    if not executable.is_absolute() or not executable.is_file():
        raise ValueError("bundled uv is missing; restore the Portable product")
    try:
        result = subprocess.run(
            [str(executable), "--no-config", "--version"],
            env=uv_environment(dict(os.environ)),
            stdin=subprocess.DEVNULL,
            capture_output=True,
            text=True,
            timeout=10,
        )
    except (OSError, subprocess.TimeoutExpired):
        raise ValueError(
            "bundled uv cannot start; restore the Portable product"
        ) from None
    if result.returncode or not re.fullmatch(
        r"uv " + re.escape(UV_VERSION) + r"(?: \([^\r\n]+\))?", result.stdout.strip()
    ):
        raise ValueError("bundled uv version is invalid; restore the Portable product")
    return executable


def uv_environment(environment: dict[str, str]) -> dict[str, str]:
    return {
        key: value
        for key, value in environment.items()
        if not key.upper().startswith(("PIP_", "UV_", "CONDA_"))
        and key.upper() not in {"VIRTUAL_ENV", "PYTHONHOME", "PYTHONPATH"}
    }


def uv_pip_command(python: Path, action: str) -> list[str]:
    if not python.is_absolute() or not python.is_file():
        raise ValueError("installer target Python must be an existing absolute path")
    return [
        str(uv_executable()),
        "--no-config",
        "--color",
        "never",
        "--no-progress",
        "--no-python-downloads",
        "pip",
        action,
        "--python",
        str(python),
    ]
