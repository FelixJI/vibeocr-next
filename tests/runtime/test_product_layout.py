from __future__ import annotations

import hashlib
import json
import zipfile
from typing import TYPE_CHECKING

import pytest

from scripts.product_layout import (
    LAYOUT_RELATIVE_PATH,
    ProductLayoutError,
    load_product_layout,
    stage_product_layout,
)

if TYPE_CHECKING:
    from pathlib import Path


def _sha(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _release_inputs(tmp_path: Path) -> dict[str, Path]:
    app = tmp_path / "app-publish"
    (app / "WebAssets").mkdir(parents=True)
    (app / "runtimes" / "win-x64" / "native").mkdir(parents=True)
    for relative in (
        "VibeOCR.WinUI.exe",
        "VibeOCR.WinUI.dll",
        "VibeOCR.WinUI.pri",
        "App.xbf",
        "MainWindow.xbf",
        "WebAssets/index.html",
        "runtimes/win-x64/native/WebView2Loader.dll",
    ):
        path = app / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(relative.encode())
    (app / "VibeOCR.WinUI.pdb").write_bytes(b"symbols")

    bootstrapper = tmp_path / "VibeOCR.Bootstrapper.exe"
    bootstrapper.write_bytes(b"launcher")
    (tmp_path / "VibeOCR.Bootstrapper.exe.config").write_text(
        "<configuration />", encoding="utf-8"
    )
    updater = tmp_path / "updater.exe"
    updater.write_bytes(b"updater")
    component_lock = tmp_path / "component-lock.json"
    component_lock.write_text("{}", encoding="utf-8")
    identities = tmp_path / "component-identities.json"
    identities.write_text('{"product":"vibeocr"}', encoding="utf-8")
    license_file = tmp_path / "LICENSE"
    license_file.write_text("license", encoding="utf-8")
    changelog = tmp_path / "CHANGELOG.md"
    changelog.write_text("# Changes", encoding="utf-8")

    backend = tmp_path / "backend-release"
    backend.mkdir()
    backend_wheel = backend / "backend.whl"
    backend_wheel.write_bytes(b"backend")
    installer_archive = backend / "installer.zip"
    with zipfile.ZipFile(installer_archive, "w") as archive:
        archive.writestr("runtime-installer/installer.exe", b"installer")
    (backend / "runtime-manifest.json").write_text(
        json.dumps(
            {
                "backend_wheel": backend_wheel.name,
                "backend_sha256": _sha(b"backend"),
                "installer": {
                    "archive": installer_archive.name,
                    "executable_path": "runtime-installer/installer.exe",
                    "executable_sha256": _sha(b"installer"),
                },
            }
        ),
        encoding="utf-8",
    )
    return {
        "app_publish_root": app,
        "bootstrapper_executable": bootstrapper,
        "updater_executable": updater,
        "component_lock": component_lock,
        "component_identities": identities,
        "backend_release_dir": backend,
        "license_file": license_file,
        "changelog_file": changelog,
    }


def test_stage_product_layout_builds_the_strict_public_tree(tmp_path: Path) -> None:
    product_root = tmp_path / "VibeOCR"

    layout = stage_product_layout(
        product_root=product_root, **_release_inputs(tmp_path)
    )

    assert {path.name for path in product_root.iterdir()} == {
        "VibeOCR.exe",
        "LICENSE",
        "CHANGELOG.md",
        "app",
        "runtime",
    }
    assert layout.public_entry == product_root / "VibeOCR.exe"
    assert layout.app_entry == product_root / "app" / "VibeOCR.WinUI.exe"
    assert (product_root / LAYOUT_RELATIVE_PATH).is_file()
    assert (product_root / "app/tools/updater.exe").is_file()
    assert (product_root / "app/metadata/component-lock.json").is_file()
    assert (product_root / "app/metadata/component-identities.json").is_file()
    assert (product_root / "runtime/backend/runtime-manifest.json").is_file()
    assert (product_root / "runtime/installer/vibeocr-runtime-installer.exe").is_file()
    assert not list(product_root.rglob("*.pdb"))
    assert not list(product_root.rglob("*.exe.config"))
    with pytest.raises(ProductLayoutError, match="layout.missing-entry"):
        load_product_layout(product_root)


def test_load_product_layout_rejects_escape_and_root_clutter(tmp_path: Path) -> None:
    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **_release_inputs(tmp_path))
    descriptor = product_root / LAYOUT_RELATIVE_PATH
    value = json.loads(descriptor.read_text(encoding="utf-8"))
    value["app"]["entry"] = "../outside.exe"
    descriptor.write_text(json.dumps(value), encoding="utf-8")

    with pytest.raises(ProductLayoutError, match="layout.invalid-path"):
        load_product_layout(product_root)

    cluttered_root = tmp_path / "ClutteredVibeOCR"
    cluttered_inputs = tmp_path / "cluttered-inputs"
    cluttered_inputs.mkdir()
    stage_product_layout(
        product_root=cluttered_root,
        **_release_inputs(cluttered_inputs),
    )
    (cluttered_root / "unexpected.dll").write_bytes(b"clutter")
    with pytest.raises(ProductLayoutError, match="layout.root-conflict"):
        load_product_layout(cluttered_root)


def test_load_product_layout_rejects_safe_but_noncanonical_paths(
    tmp_path: Path,
) -> None:
    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **_release_inputs(tmp_path))
    descriptor = product_root / LAYOUT_RELATIVE_PATH
    value = json.loads(descriptor.read_text(encoding="utf-8"))
    alternate = product_root / "app/Alternate.exe"
    alternate.write_bytes((product_root / "app/VibeOCR.WinUI.exe").read_bytes())
    value["app"]["entry"] = "app/Alternate.exe"
    descriptor.write_text(json.dumps(value), encoding="utf-8")

    with pytest.raises(ProductLayoutError, match="layout.invalid-path: app.entry"):
        load_product_layout(product_root)
