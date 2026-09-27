"""Bind the internal installer to one verified Next runtime candidate."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from vibeocr.runtime.environments.runtime_manifest import load_runtime_manifest

REQUIRED_CAPABILITIES = (
    "ocr.recognition.v2",
    "pdf.edit.v2",
    "qrcode.v2",
    "export.document.v1",
    "runtime.maintenance.v1",
    "runtime.settings.v2",
    "task.progress.v1",
    "ocr.engine-selection.v1",
    "ocr.recognition-modes.v1",
    "ocr.mineru-config.v1",
    "ocr.mineru-remote-api.v1",
    "runtime.download-sources.v1",
    "runtime.component-selection.v1",
)


def _write(path: Path, value: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def build_bindings(
    runtime_dir: Path,
    *,
    version: str,
    source_sha: str,
    component_lock: Path,
    product_identity: Path,
) -> None:
    manifest = load_runtime_manifest(runtime_dir / "runtime-manifest.json")
    if manifest.backend_version != version or manifest.source_commit != source_sha:
        raise ValueError("Runtime manifest does not match the Next checkout")
    missing = sorted(set(REQUIRED_CAPABILITIES).difference(manifest.capabilities))
    if missing:
        raise ValueError(f"Runtime is missing required capabilities: {missing}")
    product = {
        "component": "next",
        "repository": "FelixJI/vibeocr-next",
        "version": version,
        "source_sha": source_sha,
    }
    _write(
        component_lock,
        {
            "schema_version": 2,
            "product": {
                **product,
                "runtime_manifest_sha256": manifest.sha256,
                "accelerator": "cpu",
            },
            "required_capabilities": list(REQUIRED_CAPABILITIES),
        },
    )
    _write(product_identity, {"project": product})


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-dir", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--component-lock", type=Path, required=True)
    parser.add_argument("--product-identity", type=Path, required=True)
    args = parser.parse_args(argv)
    build_bindings(
        args.runtime_dir,
        version=args.version,
        source_sha=args.source_sha,
        component_lock=args.component_lock,
        product_identity=args.product_identity,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
