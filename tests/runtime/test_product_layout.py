from __future__ import annotations

import hashlib
import json
import stat
import zipfile
from pathlib import Path
from types import SimpleNamespace

import pytest

from scripts.product_layout import (
    LAYOUT_RELATIVE_PATH,
    ProductLayoutError,
    load_product_layout,
    stage_product_layout,
)


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
    (tmp_path / "Velopack.dll").write_bytes(b"velopack")
    (tmp_path / "Microsoft.Web.WebView2.Core.dll").write_bytes(b"webview2-core")
    (tmp_path / "WebView2Loader.dll").write_bytes(b"webview2-loader")
    (tmp_path / "Newtonsoft.Json.dll").write_bytes(b"newtonsoft")
    (tmp_path / "VibeOCR.Bootstrapper.exe.config").write_text(
        "<configuration />", encoding="utf-8"
    )
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
    protocol_wheel = backend / "protocol.whl"
    protocol_wheel.write_bytes(b"protocol")
    protocol_manifest = backend / "protocol-release-manifest.json"
    protocol_manifest.write_text("{}", encoding="utf-8")
    python_archive = backend / "python.tar.gz"
    python_archive.write_bytes(b"python")
    base_lock = backend / "base.lock"
    base_lock.write_bytes(b"base-lock")
    cpu_lock = backend / "cpu.lock"
    cpu_lock.write_bytes(b"cpu-lock")
    cuda_lock = backend / "cuda.lock"
    cuda_lock.write_bytes(b"cuda-lock")
    cuda_gpu_lock = backend / "cuda-gpu.lock"
    cuda_gpu_lock.write_bytes(b"cuda-gpu-lock")
    base_pack = backend / "vibeocr-runtime-pack-win-x64-base.zip"
    base_pack.write_bytes(b"base-pack")
    installer_archive = backend / "installer.zip"
    with zipfile.ZipFile(installer_archive, "w") as archive:
        archive.writestr("runtime-installer/installer.exe", b"installer")
    (backend / "runtime-manifest.json").write_text(
        json.dumps(
            {
                "backend_wheel": backend_wheel.name,
                "backend_sha256": _sha(b"backend"),
                "protocol_manifest": protocol_manifest.name,
                "protocol_wheel": protocol_wheel.name,
                "python": {
                    "archive": python_archive.name,
                    "sha256": _sha(b"python"),
                },
                "installer": {
                    "archive": installer_archive.name,
                    "executable_path": "runtime-installer/installer.exe",
                    "executable_sha256": _sha(b"installer"),
                },
                "profiles": {
                    "win-x64-base": {
                        "lock": base_lock.name,
                        "runtime_pack": [base_pack.name],
                    },
                    "win-x64-cpu": {"lock": cpu_lock.name, "runtime_pack": None},
                    "win-x64-cu126": {
                        "lock": cuda_lock.name,
                        "runtime_pack": None,
                        "install_scopes": [
                            {"scope_id": "gpu-runtime", "lock": cuda_gpu_lock.name}
                        ],
                    },
                },
            }
        ),
        encoding="utf-8",
    )
    (backend / "release-manifest.json").write_text("{}", encoding="utf-8")
    (backend / "build-identity.json").write_text("{}", encoding="utf-8")
    return {
        "app_publish_root": app,
        "bootstrapper_executable": bootstrapper,
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
        "Velopack.dll",
        "Microsoft.Web.WebView2.Core.dll",
        "WebView2Loader.dll",
        "Newtonsoft.Json.dll",
        "LICENSE",
        "CHANGELOG.md",
        "app",
        "runtime",
    }
    assert layout.public_entry == product_root / "VibeOCR.exe"
    assert (product_root / "Velopack.dll").read_bytes() == b"velopack"
    assert (
        product_root / "Microsoft.Web.WebView2.Core.dll"
    ).read_bytes() == b"webview2-core"
    assert (product_root / "WebView2Loader.dll").read_bytes() == b"webview2-loader"
    assert layout.app_entry == product_root / "app" / "VibeOCR.WinUI.exe"
    assert (product_root / LAYOUT_RELATIVE_PATH).is_file()
    assert (product_root / "app/metadata/component-lock.json").is_file()
    assert (product_root / "app/metadata/component-identities.json").is_file()
    assert (product_root / "runtime/backend/runtime-manifest.json").is_file()
    assert (product_root / "runtime/installer/vibeocr-runtime-installer.exe").is_file()
    assert not list(product_root.rglob("*.pdb"))
    assert not list(product_root.rglob("*.exe.config"))
    with pytest.raises(ProductLayoutError, match="layout.missing-entry"):
        load_product_layout(product_root)


def test_stage_product_layout_embeds_only_the_release_bound_base_runtime_closure(
    tmp_path: Path,
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    runtime_manifest = json.loads(
        (backend / "runtime-manifest.json").read_text(encoding="utf-8")
    )
    original_base_pack = (
        backend / runtime_manifest["profiles"]["win-x64-base"]["runtime_pack"][0]
    )
    arbitrary_base_pack = backend / "offline-foundation.part-01.bundle"
    original_base_pack.replace(arbitrary_base_pack)
    runtime_manifest["profiles"]["win-x64-base"]["runtime_pack"] = [
        arbitrary_base_pack.name
    ]
    advanced_packs = {
        "future-win-x64-base-cpu-profile.pack": b"cpu-full",
        "future-win-x64-base-cu126-profile.pack": b"cuda-full",
        "vibeocr-runtime-pack-paddlex-future.zip": b"paddlex",
        "vibeocr-runtime-pack-mineru-future.zip": b"mineru",
    }
    for name, contents in advanced_packs.items():
        (backend / name).write_bytes(contents)
    runtime_manifest["profiles"]["win-x64-cpu"]["runtime_pack"] = [
        "future-win-x64-base-cpu-profile.pack"
    ]
    runtime_manifest["profiles"]["win-x64-cu126"]["runtime_pack"] = [
        "future-win-x64-base-cu126-profile.pack"
    ]
    runtime_manifest["profiles"]["win-x64-cpu"]["install_scopes"] = [
        {
            "scope_id": "document-parsing",
            "lock": "cpu.lock",
            "runtime_pack": [
                "vibeocr-runtime-pack-paddlex-future.zip",
                "vibeocr-runtime-pack-mineru-future.zip",
            ],
        }
    ]
    (backend / "runtime-manifest.json").write_text(
        json.dumps(runtime_manifest), encoding="utf-8"
    )
    (backend / "unrelated-training-corpus.bin").write_bytes(b"not-a-runtime-input")

    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **inputs)

    embedded = product_root / "runtime/backend"
    assert {path.name for path in embedded.iterdir()} == {
        "runtime-manifest.json",
        "release-manifest.json",
        "build-identity.json",
        "backend.whl",
        "protocol.whl",
        "protocol-release-manifest.json",
        "python.tar.gz",
        "installer.zip",
        "base.lock",
        "cpu.lock",
        "cuda.lock",
        "cuda-gpu.lock",
        "offline-foundation.part-01.bundle",
    }


@pytest.mark.parametrize(
    "filename",
    (
        "C:backend.whl",
        "/backend.whl",
        "backend:debug.whl",
        "NUL.whl",
        "NUL .whl",
        "COM1",
        "COM¹.txt",
        "backend.whl.",
        "backend.whl ",
    ),
)
def test_stage_product_layout_rejects_nonportable_windows_release_filenames(
    tmp_path: Path,
    filename: str,
) -> None:
    inputs = _release_inputs(tmp_path)
    manifest_path = inputs["backend_release_dir"] / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["backend_wheel"] = filename
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    with pytest.raises(
        ProductLayoutError,
        match="layout.invalid-descriptor: backend_wheel must be a release filename",
    ):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


@pytest.mark.parametrize(
    ("owner", "runtime_pack"),
    (
        ("profile", "advanced-pack.zip"),
        ("profile", ["../advanced-pack.zip"]),
        ("scope", {"filename": "scope-pack.zip"}),
        ("scope", ["C:scope-pack.zip"]),
    ),
)
def test_stage_product_layout_validates_every_declared_runtime_pack(
    tmp_path: Path,
    owner: str,
    runtime_pack: object,
) -> None:
    inputs = _release_inputs(tmp_path)
    manifest_path = inputs["backend_release_dir"] / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    profile = manifest["profiles"]["win-x64-cpu"]
    if owner == "profile":
        profile["runtime_pack"] = runtime_pack
    else:
        profile["install_scopes"] = [
            {
                "scope_id": "document-parsing",
                "lock": "cpu.lock",
                "runtime_pack": runtime_pack,
            }
        ]
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    with pytest.raises(
        ProductLayoutError,
        match=r"layout.invalid-descriptor: profiles\.win-x64-cpu",
    ):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


def test_stage_product_layout_rejects_reparse_point_closure_sources(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest = json.loads(
        (backend / "runtime-manifest.json").read_text(encoding="utf-8")
    )
    base_pack = backend / manifest["profiles"]["win-x64-base"]["runtime_pack"][0]
    original_lstat = Path.lstat

    def lstat_with_reparse_attribute(
        path: Path, *args: object, **kwargs: object
    ) -> object:
        result = original_lstat(path, *args, **kwargs)
        if path == base_pack:
            return SimpleNamespace(
                st_mode=result.st_mode,
                st_file_attributes=getattr(result, "st_file_attributes", 0)
                | getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
            )
        return result

    monkeypatch.setattr(Path, "lstat", lstat_with_reparse_attribute)

    with pytest.raises(
        ProductLayoutError,
        match=r"layout.invalid-path: backend product closure.*reparse",
    ):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


@pytest.mark.parametrize(
    ("source_kind", "error_code"),
    (
        ("missing", "layout.missing-entry"),
        ("directory", "layout.invalid-path"),
    ),
)
def test_stage_product_layout_rejects_non_file_closure_sources(
    tmp_path: Path,
    source_kind: str,
    error_code: str,
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest = json.loads(
        (backend / "runtime-manifest.json").read_text(encoding="utf-8")
    )
    base_pack = backend / manifest["profiles"]["win-x64-base"]["runtime_pack"][0]
    base_pack.unlink()
    if source_kind == "directory":
        base_pack.mkdir()

    with pytest.raises(ProductLayoutError, match=error_code):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


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


def test_load_product_layout_accepts_velopack_version_marker(tmp_path: Path) -> None:
    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **_release_inputs(tmp_path))
    release_manifest = product_root / "app/metadata/product-release-manifest.json"
    release_manifest.write_text("{}", encoding="utf-8")
    (product_root / "sq.version").write_text("0.3.1", encoding="utf-8")

    layout = load_product_layout(product_root)

    assert layout.product_root == product_root.resolve()


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
