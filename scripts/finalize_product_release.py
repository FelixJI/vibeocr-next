"""Bind the verified Next runtime and finalize the Velopack pack directory."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from vibeocr.runtime.environments.runtime_manifest import load_runtime_manifest

if __package__:
    from .product_layout import load_staged_product_layout
else:
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
    runtime_dir: Path,
) -> Path:
    """Verify the product binding and write the product closure manifest."""

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
    runtime_manifest = load_runtime_manifest(runtime_dir / "runtime-manifest.json")
    product = lock.get("product")
    if not isinstance(product, dict) or lock.get("schema_version") != 2:
        raise ValueError("component lock must bind one Next product")
    if (
        product.get("component") != "next"
        or product.get("repository") != "FelixJI/vibeocr-next"
        or product.get("version") != frontend_version
        or product.get("source_sha") != source_commit
        or product.get("runtime_manifest_sha256") != runtime_manifest.sha256
        or product.get("accelerator") != "cpu"
        or runtime_manifest.backend_version != frontend_version
        or runtime_manifest.source_commit != source_commit
    ):
        raise ValueError("component lock does not bind the current Next runtime")
    if set(lock.get("required_capabilities", ())) - set(runtime_manifest.capabilities):
        raise ValueError("runtime is missing required capabilities")

    layout = load_staged_product_layout(product_root)
    embedded_lock = layout.component_lock
    if json.loads(embedded_lock.read_text(encoding="utf-8")) != lock:
        raise ValueError("staged component lock differs from verified releases")
    if layout.runtime_manifest.read_bytes() != runtime_manifest.path.read_bytes():
        raise ValueError("staged runtime manifest differs from verified runtime")
    if _sha256(layout.runtime_installer) != runtime_manifest.installer.executable_sha256:
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
    parser.add_argument("--runtime-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    print(
        finalize_product_release(
            product_root=args.product_root,
            frontend=args.frontend,
            frontend_version=args.frontend_version,
            source_commit=args.source_commit,
            component_lock=args.component_lock,
            runtime_dir=args.runtime_dir,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
