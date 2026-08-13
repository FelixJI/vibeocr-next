"""Build and resolve the versioned VibeOCR product layout."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import uuid
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath

LAYOUT_RELATIVE_PATH = Path("app/metadata/product-layout.json")
ROOT_ALLOWLIST = frozenset(
    {"VibeOCR.exe", "Velopack.dll", "LICENSE", "CHANGELOG.md", "app", "runtime"}
)
CANONICAL_PATHS = {
    "public_entry": "VibeOCR.exe",
    "roots.app": "app",
    "roots.runtime": "runtime",
    "roots.metadata": "app/metadata",
    "app.entry": "app/VibeOCR.WinUI.exe",
    "app.web_assets": "app/WebAssets",
    "app.updater": "app/tools/updater.exe",
    "runtime.manifest": "runtime/backend/runtime-manifest.json",
    "runtime.installer": "runtime/installer/vibeocr-runtime-installer.exe",
    "metadata.component_lock": "app/metadata/component-lock.json",
    "metadata.component_identities": "app/metadata/component-identities.json",
    "metadata.release_manifest": "app/metadata/product-release-manifest.json",
}


class ProductLayoutError(ValueError):
    """A stable product-layout contract violation."""


@dataclass(frozen=True)
class ProductLayout:
    product_root: Path
    public_entry: Path
    app_root: Path
    app_entry: Path
    web_assets: Path
    updater: Path
    runtime_root: Path
    runtime_manifest: Path
    runtime_installer: Path
    metadata_root: Path
    component_lock: Path
    component_identities: Path
    release_manifest: Path


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _canonical_json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def _resolved_relative(product_root: Path, value: object, field: str) -> Path:
    if not isinstance(value, str) or not value:
        raise ProductLayoutError(
            f"layout.invalid-path: {field} must be a relative path"
        )
    if "\\" in value:
        raise ProductLayoutError(
            f"layout.invalid-path: {field} must use forward slashes"
        )
    relative = PurePosixPath(value)
    if relative.is_absolute() or any(
        part in {"", ".", ".."} for part in relative.parts
    ):
        raise ProductLayoutError(
            f"layout.invalid-path: {field} escapes the product root"
        )
    candidate = product_root.joinpath(*relative.parts).resolve(strict=False)
    try:
        candidate.relative_to(product_root)
    except ValueError as error:
        raise ProductLayoutError(
            f"layout.invalid-path: {field} escapes the product root"
        ) from error
    return candidate


def _require_mapping(value: object, field: str) -> dict[str, object]:
    if not isinstance(value, dict):
        raise ProductLayoutError(
            f"layout.invalid-descriptor: {field} must be an object"
        )
    return value


def _require_canonical(value: object, field: str) -> str:
    expected = CANONICAL_PATHS[field]
    if value != expected:
        raise ProductLayoutError(f"layout.invalid-path: {field} must be {expected}")
    return expected


def _read_json_object(path: Path, field: str) -> dict[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ProductLayoutError(f"layout.closure-mismatch: invalid {field}") from error
    return _require_mapping(value, field)


def _load_product_layout(
    product_root: Path, *, require_release_manifest: bool
) -> ProductLayout:

    root = product_root.resolve(strict=True)
    if not root.is_dir():
        raise ProductLayoutError(
            "layout.missing-entry: product root is not a directory"
        )
    actual_root = {path.name for path in root.iterdir()}
    if actual_root != ROOT_ALLOWLIST:
        unexpected = sorted(actual_root - ROOT_ALLOWLIST)
        missing = sorted(ROOT_ALLOWLIST - actual_root)
        raise ProductLayoutError(
            f"layout.root-conflict: unexpected={unexpected}, missing={missing}"
        )
    descriptor_path = root / LAYOUT_RELATIVE_PATH
    try:
        descriptor = json.loads(descriptor_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ProductLayoutError(
            "layout.missing-entry: product layout descriptor is unavailable"
        ) from error
    if descriptor.get("schema_version") != 1:
        raise ProductLayoutError("layout.unsupported-schema: expected schema_version 1")
    if descriptor.get("product_id") != "vibeocr":
        raise ProductLayoutError("layout.product-mismatch: expected vibeocr")

    roots = _require_mapping(descriptor.get("roots"), "roots")
    app = _require_mapping(descriptor.get("app"), "app")
    runtime = _require_mapping(descriptor.get("runtime"), "runtime")
    metadata = _require_mapping(descriptor.get("metadata"), "metadata")
    user_data = _require_mapping(descriptor.get("user_data"), "user_data")
    if user_data != {"known_folder": "LocalApplicationData", "relative": "VibeOCR"}:
        raise ProductLayoutError("layout.invalid-descriptor: invalid user_data policy")
    canonical = {
        "public_entry": descriptor.get("public_entry"),
        "roots.app": roots.get("app"),
        "roots.runtime": roots.get("runtime"),
        "roots.metadata": roots.get("metadata"),
        "app.entry": app.get("entry"),
        "app.web_assets": app.get("web_assets"),
        "app.updater": app.get("updater"),
        "runtime.manifest": runtime.get("manifest"),
        "runtime.installer": runtime.get("installer"),
        "metadata.component_lock": metadata.get("component_lock"),
        "metadata.component_identities": metadata.get("component_identities"),
        "metadata.release_manifest": metadata.get("release_manifest"),
    }
    for field, value in canonical.items():
        _require_canonical(value, field)

    layout = ProductLayout(
        product_root=root,
        public_entry=_resolved_relative(
            root, descriptor.get("public_entry"), "public_entry"
        ),
        app_root=_resolved_relative(root, roots.get("app"), "roots.app"),
        app_entry=_resolved_relative(root, app.get("entry"), "app.entry"),
        web_assets=_resolved_relative(root, app.get("web_assets"), "app.web_assets"),
        updater=_resolved_relative(root, app.get("updater"), "app.updater"),
        runtime_root=_resolved_relative(root, roots.get("runtime"), "roots.runtime"),
        runtime_manifest=_resolved_relative(
            root, runtime.get("manifest"), "runtime.manifest"
        ),
        runtime_installer=_resolved_relative(
            root, runtime.get("installer"), "runtime.installer"
        ),
        metadata_root=_resolved_relative(root, roots.get("metadata"), "roots.metadata"),
        component_lock=_resolved_relative(
            root, metadata.get("component_lock"), "metadata.component_lock"
        ),
        component_identities=_resolved_relative(
            root,
            metadata.get("component_identities"),
            "metadata.component_identities",
        ),
        release_manifest=_resolved_relative(
            root, metadata.get("release_manifest"), "metadata.release_manifest"
        ),
    )
    required = (
        layout.public_entry,
        layout.app_entry,
        layout.app_root / "VibeOCR.WinUI.dll",
        layout.app_root / "VibeOCR.WinUI.pri",
        layout.app_root / "App.xbf",
        layout.app_root / "MainWindow.xbf",
        layout.web_assets / "index.html",
        layout.updater,
        layout.runtime_manifest,
        layout.runtime_installer,
        layout.component_lock,
        layout.component_identities,
        root / "LICENSE",
        root / "CHANGELOG.md",
    )
    if require_release_manifest:
        required = (*required, layout.release_manifest)
    missing = [str(path.relative_to(root)) for path in required if not path.is_file()]
    if missing:
        raise ProductLayoutError(f"layout.missing-entry: {sorted(missing)}")
    for path in root.rglob("*"):
        if path.is_symlink():
            raise ProductLayoutError(
                f"layout.invalid-path: symbolic link is not allowed: {path.relative_to(root)}"
            )
    return layout


def load_staged_product_layout(product_root: Path) -> ProductLayout:
    """Resolve the package builder's pre-manifest staging tree."""

    return _load_product_layout(product_root, require_release_manifest=False)


def load_product_layout(product_root: Path) -> ProductLayout:
    """Resolve a complete installed or released product tree."""

    return _load_product_layout(product_root, require_release_manifest=True)


def verify_product_release(product_root: Path) -> ProductLayout:
    """Validate the complete release closure consumed by updater candidates."""

    layout = load_product_layout(product_root)
    if not layout.release_manifest.is_file():
        raise ProductLayoutError("layout.missing-entry: product release manifest")
    manifest = _read_json_object(layout.release_manifest, "product release manifest")
    if manifest.get("schema_version") != 1 or manifest.get("frontend") != "next":
        raise ProductLayoutError("layout.closure-mismatch: invalid release identity")
    records = _require_mapping(manifest.get("files"), "release files")
    actual_files = {
        path.relative_to(layout.product_root).as_posix(): path
        for path in layout.product_root.rglob("*")
        if path.is_file() and path != layout.release_manifest
    }
    if set(records) != set(actual_files):
        raise ProductLayoutError("layout.closure-mismatch: release file set")
    for relative, path in actual_files.items():
        record = _require_mapping(records[relative], f"release file {relative}")
        if (
            record.get("sha256") != _sha256(path)
            or record.get("size") != path.stat().st_size
        ):
            raise ProductLayoutError(f"layout.closure-mismatch: {relative}")

    lock_hash = _sha256(layout.component_lock)
    if manifest.get("component_lock_sha256") != lock_hash:
        raise ProductLayoutError("layout.closure-mismatch: component lock")
    lock = _read_json_object(layout.component_lock, "component lock")
    identities = _read_json_object(layout.component_identities, "component identities")
    runtime = _read_json_object(layout.runtime_manifest, "runtime manifest")
    backend_lock = _require_mapping(lock.get("backend"), "component lock backend")
    backend_identity = _require_mapping(
        identities.get("backend"), "component identity backend"
    )
    runtime_hash = _sha256(layout.runtime_manifest)
    if (
        identities.get("component_lock_sha256") != lock_hash
        or backend_lock.get("runtime_manifest_sha256") != runtime_hash
        or backend_identity.get("runtime_manifest_sha256") != runtime_hash
    ):
        raise ProductLayoutError("layout.closure-mismatch: runtime binding")

    protocol_lock = _require_mapping(lock.get("protocol"), "component lock protocol")
    protocol_identity = _require_mapping(
        identities.get("protocol"), "component identity protocol"
    )
    protocol_manifest = (
        layout.runtime_manifest.parent / "protocol-release-manifest.json"
    )
    backend_manifest = layout.runtime_manifest.parent / "release-manifest.json"
    if (
        not protocol_manifest.is_file()
        or protocol_lock.get("manifest_sha256") != _sha256(protocol_manifest)
        or protocol_identity.get("release_manifest_sha256")
        != _sha256(protocol_manifest)
        or not backend_manifest.is_file()
        or backend_identity.get("release_manifest_sha256") != _sha256(backend_manifest)
    ):
        raise ProductLayoutError("layout.closure-mismatch: component identity")

    installer = _require_mapping(runtime.get("installer"), "runtime installer")
    if installer.get("executable_sha256") != _sha256(layout.runtime_installer):
        raise ProductLayoutError("layout.closure-mismatch: runtime installer")
    return layout


def _copy_publish_tree(source: Path, destination: Path) -> None:
    source = source.resolve(strict=True)
    if not source.is_dir():
        raise ProductLayoutError(
            "layout.missing-entry: app publish root is not a directory"
        )
    for path in sorted(source.rglob("*")):
        if path.is_symlink():
            raise ProductLayoutError(
                f"layout.invalid-path: publish symbolic link is not allowed: {path}"
            )
        relative = path.relative_to(source)
        target = destination / relative
        if path.is_dir():
            target.mkdir(parents=True, exist_ok=True)
        elif path.suffix.lower() not in {".pdb"} and not path.name.lower().endswith(
            ".exe.config"
        ):
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, target)


def stage_product_layout(
    *,
    product_root: Path,
    app_publish_root: Path,
    bootstrapper_executable: Path,
    updater_executable: Path,
    component_lock: Path,
    component_identities: Path,
    backend_release_dir: Path,
    license_file: Path,
    changelog_file: Path,
) -> ProductLayout:
    """Assemble the complete product tree without exposing copy details to callers."""

    output = product_root.resolve(strict=False)
    if output.exists():
        raise ProductLayoutError("layout.root-conflict: product root already exists")
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = output.parent / f".{output.name}.staging-{uuid.uuid4().hex}"
    try:
        app_root = staging / "app"
        metadata_root = app_root / "metadata"
        tools_root = app_root / "tools"
        backend_root = staging / "runtime" / "backend"
        installer_root = staging / "runtime" / "installer"
        _copy_publish_tree(app_publish_root, app_root)
        metadata_root.mkdir(parents=True)
        tools_root.mkdir(parents=True)
        backend_root.mkdir(parents=True)
        installer_root.mkdir(parents=True)

        shutil.copyfile(
            bootstrapper_executable.resolve(strict=True), staging / "VibeOCR.exe"
        )
        shutil.copyfile(
            bootstrapper_executable.with_name("Velopack.dll").resolve(strict=True),
            staging / "Velopack.dll",
        )
        shutil.copyfile(
            updater_executable.resolve(strict=True), tools_root / "updater.exe"
        )
        shutil.copyfile(
            component_lock.resolve(strict=True), metadata_root / "component-lock.json"
        )
        shutil.copyfile(
            component_identities.resolve(strict=True),
            metadata_root / "component-identities.json",
        )
        shutil.copyfile(license_file.resolve(strict=True), staging / "LICENSE")
        shutil.copyfile(changelog_file.resolve(strict=True), staging / "CHANGELOG.md")

        backend_source = backend_release_dir.resolve(strict=True)
        for path in sorted(backend_source.iterdir()):
            if path.is_symlink() or not path.is_file():
                raise ProductLayoutError(
                    f"layout.invalid-path: backend release entry is not a regular file: {path}"
                )
            shutil.copyfile(path, backend_root / path.name)
        runtime_manifest = json.loads(
            (backend_root / "runtime-manifest.json").read_text(encoding="utf-8")
        )
        installer = _require_mapping(runtime_manifest.get("installer"), "installer")
        archive_name = installer.get("archive")
        executable_name = installer.get("executable_path")
        if not isinstance(archive_name, str) or not isinstance(executable_name, str):
            raise ProductLayoutError(
                "layout.invalid-descriptor: invalid runtime installer"
            )
        with zipfile.ZipFile(backend_root / archive_name) as archive:
            executable = archive.read(executable_name)
        installer_path = installer_root / "vibeocr-runtime-installer.exe"
        installer_path.write_bytes(executable)
        expected_hash = installer.get("executable_sha256")
        if (
            not isinstance(expected_hash, str)
            or _sha256(installer_path) != expected_hash
        ):
            raise ProductLayoutError("layout.closure-mismatch: runtime installer hash")

        descriptor = {
            "schema_version": 1,
            "product_id": "vibeocr",
            "public_entry": "VibeOCR.exe",
            "roots": {
                "app": "app",
                "runtime": "runtime",
                "metadata": "app/metadata",
            },
            "app": {
                "entry": "app/VibeOCR.WinUI.exe",
                "web_assets": "app/WebAssets",
                "updater": "app/tools/updater.exe",
            },
            "runtime": {
                "manifest": "runtime/backend/runtime-manifest.json",
                "installer": "runtime/installer/vibeocr-runtime-installer.exe",
            },
            "metadata": {
                "component_lock": "app/metadata/component-lock.json",
                "component_identities": "app/metadata/component-identities.json",
                "release_manifest": "app/metadata/product-release-manifest.json",
            },
            "user_data": {
                "known_folder": "LocalApplicationData",
                "relative": "VibeOCR",
            },
        }
        (staging / LAYOUT_RELATIVE_PATH).write_text(
            _canonical_json(descriptor), encoding="utf-8", newline="\n"
        )
        staging.replace(output)
        return load_staged_product_layout(output)
    except Exception:
        if staging.exists():
            shutil.rmtree(staging)
        raise


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    stage = subparsers.add_parser("stage")
    stage.add_argument("--product-root", type=Path, required=True)
    stage.add_argument("--app-publish-root", type=Path, required=True)
    stage.add_argument("--bootstrapper-executable", type=Path, required=True)
    stage.add_argument("--updater-executable", type=Path, required=True)
    stage.add_argument("--component-lock", type=Path, required=True)
    stage.add_argument("--component-identities", type=Path, required=True)
    stage.add_argument("--backend-release-dir", type=Path, required=True)
    stage.add_argument("--license-file", type=Path, required=True)
    stage.add_argument("--changelog-file", type=Path, required=True)
    inspect = subparsers.add_parser("inspect")
    inspect.add_argument("--product-root", type=Path, required=True)
    verify = subparsers.add_parser("verify")
    verify.add_argument("--product-root", type=Path, required=True)
    args = parser.parse_args(argv)
    if args.command == "stage":
        layout = stage_product_layout(
            product_root=args.product_root,
            app_publish_root=args.app_publish_root,
            bootstrapper_executable=args.bootstrapper_executable,
            updater_executable=args.updater_executable,
            component_lock=args.component_lock,
            component_identities=args.component_identities,
            backend_release_dir=args.backend_release_dir,
            license_file=args.license_file,
            changelog_file=args.changelog_file,
        )
    elif args.command == "inspect":
        layout = load_product_layout(args.product_root)
    else:
        layout = verify_product_release(args.product_root)
    print(layout.product_root)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
