"""Bind verified component releases and finalize the Velopack pack directory."""

from __future__ import annotations

import argparse
import hashlib
import json
import tempfile
from pathlib import Path

if __package__:
    from .bind_component_releases import bind_product_releases
    from .product_layout import load_staged_product_layout
else:
    from bind_component_releases import bind_product_releases
    from product_layout import load_staged_product_layout

PROHIBITED_ROOTS = {".git", "apps", "contracts", "packages", "supervisor", "tests"}


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _canonical_json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def finalize_product_release(
    *,
    product_root: Path,
    frontend: str,
    frontend_version: str,
    source_commit: str,
    component_lock: Path,
    protocol_release_dir: Path,
    backend_release_dir: Path,
) -> Path:
    """Verify component bindings and write the product closure manifest."""

    product_root = product_root.resolve(strict=True)
    if not product_root.is_dir():
        raise ValueError("product_root must be a directory")
    prohibited = sorted(
        child.name
        for child in product_root.iterdir()
        if child.name.lower() in PROHIBITED_ROOTS
    )
    if prohibited:
        raise ValueError(f"prohibited source roots in product layout: {prohibited}")
    lock_path = component_lock.resolve(strict=True)
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    protocol = lock["protocol"]
    backend = lock["backend"]
    required_capabilities = tuple(lock["required_capabilities"])
    with tempfile.TemporaryDirectory(prefix="vibeocr-component-lock-") as temp:
        generated = Path(temp) / "component-lock.json"
        bind_product_releases(
            protocol_release_dir=protocol_release_dir,
            backend_release_dir=backend_release_dir,
            protocol_repository=str(protocol["repository"]),
            protocol_version=str(protocol["version"]),
            backend_repository=str(backend["repository"]),
            backend_version=str(backend["version"]),
            accelerator=str(backend["accelerator"]),
            required_capabilities=required_capabilities,
            output=generated,
        )
        if json.loads(generated.read_text(encoding="utf-8")) != lock:
            raise ValueError("committed component lock differs from verified releases")

    layout = load_staged_product_layout(product_root)
    embedded_lock = layout.component_lock
    if json.loads(embedded_lock.read_text(encoding="utf-8")) != lock:
        raise ValueError("staged component lock differs from verified releases")
    runtime_manifest = json.loads(layout.runtime_manifest.read_text(encoding="utf-8"))
    if (
        _sha256(layout.runtime_installer)
        != runtime_manifest["installer"]["executable_sha256"]
    ):
        raise ValueError("extracted Runtime Installer hash mismatch")

    files = sorted(
        path
        for path in product_root.rglob("*")
        if path.is_file() and path != layout.release_manifest
    )
    layout.release_manifest.write_text(
        _canonical_json(
            {
                "schema_version": 1,
                "frontend": frontend,
                "frontend_version": frontend_version,
                "source_commit": source_commit,
                "component_lock_sha256": _sha256(embedded_lock),
                "files": {
                    path.relative_to(product_root).as_posix(): {
                        "sha256": _sha256(path),
                        "size": path.stat().st_size,
                    }
                    for path in files
                },
            }
        ),
        encoding="utf-8",
        newline="\n",
    )
    return layout.release_manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--product-root", type=Path, required=True)
    parser.add_argument("--frontend", required=True)
    parser.add_argument("--frontend-version", required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--component-lock", type=Path, required=True)
    parser.add_argument("--protocol-release-dir", type=Path, required=True)
    parser.add_argument("--backend-release-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    print(
        finalize_product_release(
            product_root=args.product_root,
            frontend=args.frontend,
            frontend_version=args.frontend_version,
            source_commit=args.source_commit,
            component_lock=args.component_lock,
            protocol_release_dir=args.protocol_release_dir,
            backend_release_dir=args.backend_release_dir,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
