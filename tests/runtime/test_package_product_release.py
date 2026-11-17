from __future__ import annotations

import json
from typing import TYPE_CHECKING

from scripts.finalize_product_release import finalize_product_release
from scripts.product_layout import stage_product_layout
from tests.runtime.test_internal_product_binding import _candidate

if TYPE_CHECKING:
    from pathlib import Path


def _minimal_x64_dll_image() -> bytes:
    """最小自洽 Windows x64 DLL PE 头样本。自洽指：COFF 声明的可选头
    （0xF0）与节表（1×40）都落在实际文件长度内；仅头部/声明范围语义。"""
    image = bytearray(0x80 + 24 + 0xF0 + 40)
    image[0:2] = b"MZ"
    image[0x3C:0x40] = (0x80).to_bytes(4, "little")
    image[0x80:0x84] = b"PE\x00\x00"
    image[0x84:0x86] = (0x8664).to_bytes(2, "little")
    image[0x86:0x88] = (1).to_bytes(2, "little")
    image[0x94:0x96] = (0xF0).to_bytes(2, "little")
    image[0x96:0x98] = (0x2022).to_bytes(2, "little")
    image[0x98:0x9A] = (0x20B).to_bytes(2, "little")
    return bytes(image)


def _stage_product(
    tmp_path: Path, name: str, backend: Path, component_lock: Path
) -> Path:
    app = tmp_path / f"{name}-app"
    for relative in (
        "VibeOCR.WinUI.exe",
        "VibeOCR.WinUI.dll",
        "VibeOCR.WinUI.pri",
        "App.xbf",
        "MainWindow.xbf",
        "WebAssets/index.html",
        "WebView2Runtime/msedgewebview2.exe",
        "WebView2Runtime/icudtl.dat",
    ):
        path = app / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(relative.encode())
    # W17-2：随包启动库写入最小自洽 x64 DLL 映像（映像校验正例）。
    for relative in (
        "WebView2Runtime/msedge.dll",
        "WebView2Runtime/EBWebView/x64/EmbeddedBrowserWebView.dll",
    ):
        path = app / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(_minimal_x64_dll_image())
    bootstrapper = tmp_path / f"{name}-bootstrapper.exe"
    bootstrapper.write_bytes(b"bootstrapper")
    for dependency in (
        "Velopack.dll",
        "Microsoft.Web.WebView2.Core.dll",
        "WebView2Loader.dll",
        "Newtonsoft.Json.dll",
    ):
        (tmp_path / dependency).write_bytes(dependency.encode())
    identities = tmp_path / f"{name}-identities.json"
    identities.write_text(
        json.dumps(
            {
                "project": json.loads(
                    (backend / "runtime-manifest.json").read_text(encoding="utf-8")
                )["product"]
            }
        ),
        encoding="utf-8",
    )
    license_file = tmp_path / f"{name}-LICENSE"
    license_file.write_text("license", encoding="utf-8")
    changelog = tmp_path / f"{name}-CHANGELOG.md"
    changelog.write_text("changes", encoding="utf-8")
    product = tmp_path / name / "VibeOCR"
    stage_product_layout(
        product_root=product,
        app_publish_root=app,
        bootstrapper_executable=bootstrapper,
        component_lock=component_lock,
        component_identities=identities,
        backend_release_dir=backend,
        license_file=license_file,
        changelog_file=changelog,
    )
    return product


def test_product_finalize_is_deterministic_and_binds_runtime(tmp_path: Path) -> None:
    inputs, runtime_manifest = _candidate(tmp_path)
    backend = runtime_manifest.parent
    component_lock = inputs["component_lock"]
    manifests = []
    for name in ("first", "second"):
        product = _stage_product(tmp_path, name, backend, component_lock)
        manifests.append(
            finalize_product_release(
                product_root=product,
                frontend="next",
                frontend_version="0.7.0",
                source_commit="0" * 40,
                component_lock=component_lock,
                runtime_dir=backend,
            )
        )
    assert manifests[0].read_bytes() == manifests[1].read_bytes()
    records = json.loads(manifests[0].read_text(encoding="utf-8"))["files"]
    assert "app/metadata/component-lock.json" in records
    assert "runtime/env/runtime-env/python.exe" in records
    assert "runtime/env/runtime-env/python313.dll" in records
    assert not any("runtime-installer" in path for path in records)
    assert "runtime/backend/runtime-manifest.json" in records


def test_product_finalize_accepts_equivalent_crlf_component_lock(
    tmp_path: Path,
) -> None:
    inputs, runtime_manifest = _candidate(tmp_path)
    backend = runtime_manifest.parent
    component_lock = inputs["component_lock"]
    component_lock.write_bytes(
        component_lock.read_text(encoding="utf-8").replace("\n", "\r\n").encode()
    )
    product = _stage_product(tmp_path, "product", backend, component_lock)

    output = finalize_product_release(
        product_root=product,
        frontend="next",
        frontend_version="0.7.0",
        source_commit="0" * 40,
        component_lock=component_lock,
        runtime_dir=backend,
    )

    assert output.is_file()
