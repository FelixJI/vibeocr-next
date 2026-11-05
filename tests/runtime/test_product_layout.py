from __future__ import annotations

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
from scripts.release_smoke import _verify_runtime_code


def _packaged_release(root: Path) -> tuple[Path, Path]:
    """Use the current release generators and checked-in dependency locks."""
    from scripts.build_product_binding import build_bindings
    from scripts.build_runtime_manifest import build_runtime_manifest
    from scripts.build_runtime_pack import write_runtime_env_manifest

    config = Path(__file__).resolve().parents[2] / "config/runtime"
    work = root.parent / "synthetic-runtime-inputs"
    tree = work / "runtime-env"
    (tree / "Lib").mkdir(parents=True)
    (tree / "python.exe").write_bytes(b"python-bin")
    (tree / "python313.dll").write_bytes(b"python-dll")
    (tree / "Lib/os.py").write_bytes(b"os")
    descriptor = work / "runtime-env-manifest.json"
    write_runtime_env_manifest(tree, descriptor)
    models = work / "vibeocr-paddle-models-0.7.0.zip"
    with zipfile.ZipFile(models, "w") as archive:
        archive.writestr("seed.txt", b"synthetic")
    python = json.loads((config / "python-runtime.lock.json").read_bytes())
    manifest = build_runtime_manifest(
        base_lock=config / "win-x64-base/requirements-win-x64-base.lock",
        paddle_onnx_locks={
            "directml": config
            / "win-x64-paddle-ort-directml/requirements-win-x64-paddle-ort-directml.lock"
        },
        runtime_env_tree=tree,
        runtime_env_manifest=descriptor,
        paddle_model_pack=models,
        python_version=python["version"],
        python_source_url=python["source_url"],
        version="0.7.0",
        source_commit="0" * 40,
        build_workflow="test",
        output_dir=root,
    )
    lock = root / "component-lock.json"
    build_bindings(
        root,
        version="0.7.0",
        source_sha="0" * 40,
        component_lock=lock,
        product_identity=root / "component-identities.json",
    )
    return manifest, lock


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
    (tmp_path / "VibeOCR.Bootstrapper.exe.config").write_text(
        "<configuration />", encoding="utf-8"
    )
    backend = tmp_path / "backend-release"
    manifest_path, component_lock = _packaged_release(backend)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    identities = tmp_path / "component-identities.json"
    identities.write_text(
        json.dumps({"project": manifest["product"]}), encoding="utf-8"
    )
    license_file = tmp_path / "LICENSE"
    license_file.write_text("license", encoding="utf-8")
    changelog = tmp_path / "CHANGELOG.md"
    changelog.write_text("# Changes", encoding="utf-8")

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
        "LICENSE",
        "CHANGELOG.md",
        "app",
        "runtime",
    }
    assert layout.public_entry == product_root / "VibeOCR.exe"
    assert (product_root / "Velopack.dll").read_bytes() == b"velopack"
    assert layout.app_entry == product_root / "app" / "VibeOCR.WinUI.exe"
    assert (product_root / LAYOUT_RELATIVE_PATH).is_file()
    assert (product_root / "app/metadata/component-lock.json").is_file()
    assert (product_root / "app/metadata/component-identities.json").is_file()
    assert (product_root / "runtime/backend/runtime-manifest.json").is_file()
    assert layout.runtime_python.read_bytes() == b"python-bin"
    assert not list(product_root.rglob("vibeocr-runtime-installer*"))
    assert not list(product_root.rglob("*.pdb"))
    assert not list(product_root.rglob("*.exe.config"))
    with pytest.raises(ProductLayoutError, match="layout.missing-entry"):
        load_product_layout(product_root)


def test_stage_product_layout_accepts_relative_backend_source(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    inputs = _release_inputs(tmp_path)
    monkeypatch.chdir(tmp_path)
    inputs["backend_release_dir"] = Path("backend-release")
    product = tmp_path / "VibeOCR"

    stage_product_layout(product_root=product, **inputs)

    assert (product / "runtime/env/runtime-env/python.exe").is_file()


def test_stage_product_layout_embeds_only_the_release_bound_base_runtime_closure(
    tmp_path: Path,
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    runtime_manifest = json.loads(
        (backend / "runtime-manifest.json").read_text(encoding="utf-8")
    )
    for name in (
        "unrelated-training-corpus.bin",
        "vibeocr-runtime-installer.exe",
        "unbound.whl",
    ):
        (backend / name).write_bytes(b"unbound")
    product = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product, **inputs)
    assert {entry.name for entry in (product / "runtime/backend").iterdir()} == {
        "runtime-manifest.json",
        runtime_manifest["runtime_env"]["manifest"],
        runtime_manifest["profiles"]["win-x64-base"]["lock"],
        runtime_manifest["paddle_onnx_environments"]["directml"]["lock"],
        runtime_manifest["paddle_model_pack"]["archive"],
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
    manifest["runtime_env"]["manifest"] = filename
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    with pytest.raises(
        ProductLayoutError,
        match="layout.invalid-descriptor: runtime_env.manifest must be a release filename",
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
    if owner == "profile":
        manifest["profiles"]["win-x64-base"]["runtime_pack"] = runtime_pack
        pattern = r"layout.invalid-descriptor: profiles\.win-x64-base"
    else:
        manifest["paddle_onnx_environments"]["directml"]["runtime_pack"] = runtime_pack
        pattern = r"layout.invalid-descriptor: paddle_onnx_environments\.directml"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    with pytest.raises(ProductLayoutError, match=pattern):
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
    base_pack = backend / manifest["runtime_env"]["manifest"]
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
    base_pack = backend / manifest["runtime_env"]["manifest"]
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


@pytest.mark.parametrize(
    "binding", ["paddle_model_pack", "runtime_env", "paddle_onnx_environments"]
)
def test_current_candidate_requires_bound_inference_assets(tmp_path, binding):
    inputs = _release_inputs(tmp_path)
    manifest = inputs["backend_release_dir"] / "runtime-manifest.json"
    raw = json.loads(manifest.read_bytes())
    del raw[binding]
    manifest.write_text(json.dumps(raw), encoding="utf-8")
    with pytest.raises((ProductLayoutError, ValueError)):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


def test_release_smoke_rejects_missing_product_python_before_any_subprocess(
    tmp_path, monkeypatch
):
    def unexpected(*args, **kwargs):
        pytest.fail("attempted to run an unbound interpreter")

    monkeypatch.setattr("scripts.release_smoke.subprocess.run", unexpected)
    with pytest.raises(ValueError, match="python.exe"):
        _verify_runtime_code(tmp_path, "0.7.0")
