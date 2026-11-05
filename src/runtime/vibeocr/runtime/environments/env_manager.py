"""预构建运行时树的解释器与安装根路径解析。

在线依赖安装与便携环境自举已随预构建运行时树退役（零安装），本模块
只保留 worker/服务进程定位当前解释器与安装根所需的路径助手。
"""

import os
import sys
from pathlib import Path

from vibeocr.runtime.environments.runtime_layout import resolve_app_paths


def _is_active_python_environment_root(project_root: Path) -> bool:
    """Return whether ``project_root`` is the interpreter running VibeOCR."""
    if getattr(sys, "frozen", False):
        return False
    try:
        return project_root.resolve() == Path(sys.prefix).resolve()
    except OSError:
        return False


def get_embedded_python_executable(project_root: Path) -> Path:
    """获取运行 Python 可执行文件路径

    优先使用 .venv 虚拟环境(开发模式),如果不存在则使用 python/ 目录(便携式部署)
    """
    if _is_active_python_environment_root(project_root):
        return Path(sys.executable).resolve()

    if os.name == "nt":  # Windows
        venv_python = project_root / ".venv" / "Scripts" / "python.exe"
        portable_python = project_root / "python" / "python.exe"
    else:
        venv_python = project_root / ".venv" / "bin" / "python"
        portable_python = project_root / "python" / "bin" / "python"

    # 优先使用虚拟环境
    if venv_python.exists():
        return venv_python
    return portable_python


def get_embedded_python(project_root: Path | None = None) -> Path:
    """获取运行 Python 可执行文件路径（兼容别名）

    Args:
        project_root: 项目根目录，如果为 None 则自动检测

    Returns:
        Python 可执行文件路径
    """
    if project_root is None:
        project_root = get_project_root()
    return get_embedded_python_executable(project_root)


def get_project_root() -> Path:
    """Return the install root while preserving the legacy interface."""

    return resolve_app_paths().install_root
