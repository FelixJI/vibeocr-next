"""契约：运行依赖三处声明保持单一事实源结构。

根 ``pyproject.toml`` [project] dependencies（wheel 元数据，无法引用
片段）与 ``config/runtime/shared/requirements-base.in``（闭包基座）必须
逐项一致；两个 profile 的 ``requirements.in`` 只允许以 ``-r`` 引用基座
并追加精确 pin（``==``）。漂移在本文件失败，防止三处复制重新发散。
"""

from __future__ import annotations

import tomllib
from pathlib import Path

from packaging.requirements import Requirement

_REPO = Path(__file__).resolve().parents[2]
_PYPROJECT = _REPO / "pyproject.toml"
_SHARED = _REPO / "config" / "runtime" / "shared" / "requirements-base.in"
_BASE = _REPO / "config" / "runtime" / "win-x64-base" / "requirements.in"
_DIRECTML = (
    _REPO / "config" / "runtime" / "win-x64-paddle-ort-directml" / "requirements.in"
)

# 基座携带、但根 wheel 元数据不声明（Windows 平台投影，仅闭包分发）。
_SHARED_ONLY_WINRT = {
    "winrt-runtime",
    "winrt-windows.foundation",
    "winrt-windows.foundation.collections",
    "winrt-windows.media.ocr",
    "winrt-windows.globalization",
    "winrt-windows.graphics.imaging",
    "winrt-windows.storage.streams",
}
# profile 允许追加的精确 pin 名称（版本不在此锁定，随闭包升级演进）。
_BASE_APPEND_NAMES = {"numpy"}
_DIRECTML_APPEND_NAMES = {
    "numpy",
    "paddleocr",
    "paddlex",
    "modelscope-hub",
    "onnxruntime-directml",
    "mineru",
}


def _parse_requirements(path: Path) -> dict[str, Requirement]:
    requirements: dict[str, Requirement] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or line.startswith("-r"):
            continue
        requirement = Requirement(line)
        requirements[requirement.name.lower()] = requirement
    return requirements


def _profile_parts(path: Path) -> tuple[list[str], dict[str, Requirement]]:
    """返回 (引用行, 追加依赖)；引用行必须恰好一条且指向共享基座。"""

    references: list[str] = []
    requirements: dict[str, Requirement] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if line.startswith("-r"):
            references.append(line)
        elif line and not line.startswith("#"):
            requirement = Requirement(line)
            requirements[requirement.name.lower()] = requirement
    return references, requirements


def test_root_dependencies_match_shared_base() -> None:
    dependencies = tomllib.loads(_PYPROJECT.read_text(encoding="utf-8"))["project"][
        "dependencies"
    ]
    root = {Requirement(item).name.lower(): Requirement(item) for item in dependencies}
    shared = _parse_requirements(_SHARED)

    assert set(shared) - set(root) == _SHARED_ONLY_WINRT
    for name, root_requirement in root.items():
        assert name in shared, f"{name} missing from shared requirements-base.in"
        assert str(shared[name].specifier) == str(root_requirement.specifier), (
            f"{name} specifier drift: pyproject={root_requirement.specifier} "
            f"shared={shared[name].specifier}"
        )


def test_profiles_reference_shared_base_with_exact_pins_only() -> None:
    for path, allowed_names in (
        (_BASE, _BASE_APPEND_NAMES),
        (_DIRECTML, _DIRECTML_APPEND_NAMES),
    ):
        references, appended = _profile_parts(path)
        assert references == ["-r ../shared/requirements-base.in"], path
        assert set(appended) <= allowed_names, path
        for name, requirement in appended.items():
            assert requirement.specifier and all(
                spec.operator == "==" for spec in requirement.specifier
            ), f"{path}: appended pin {name} must be exact (==), got {requirement}"


def test_numpy_bounds_align_across_root_and_closure_pins() -> None:
    shared = _parse_requirements(_SHARED)
    numpy_specifier = shared["numpy"].specifier
    assert any(
        spec.operator == "<" and spec.version == "2.4" for spec in numpy_specifier
    ), "shared numpy must keep the PaddleX <2.4 upper bound"
    for path in (_BASE, _DIRECTML):
        _, appended = _profile_parts(path)
        pinned = appended["numpy"].specifier
        pinned_version = next(iter(pinned)).version
        assert numpy_specifier.contains(pinned_version), (
            f"{path}: numpy pin {pinned_version} violates shared bounds "
            f"{numpy_specifier}"
        )
