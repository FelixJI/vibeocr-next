"""The installer and Portable candidate share one verified Next identity."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from scripts.build_product_binding import REQUIRED_CAPABILITIES, build_bindings
from scripts.finalize_product_release import finalize_product_release
from scripts.product_layout import stage_product_layout, verify_product_release
from tests.runtime.test_product_layout import _release_inputs


def _candidate(tmp_path: Path) -> tuple[dict[str, Path], Path]:
    inputs = _release_inputs(tmp_path)
    runtime_dir = inputs["backend_release_dir"]
    manifest_path = runtime_dir / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["capabilities"] = list(REQUIRED_CAPABILITIES)
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    build_bindings(
        runtime_dir,
        version="0.7.0",
        source_sha="0" * 40,
        component_lock=inputs["component_lock"],
        product_identity=inputs["component_identities"],
    )
    return inputs, manifest_path


def test_finalize_binds_one_next_product_and_verified_runtime(tmp_path: Path) -> None:
    inputs, manifest_path = _candidate(tmp_path)
    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **inputs)
    output = finalize_product_release(
        product_root=product_root,
        frontend="next",
        frontend_version="0.7.0",
        source_commit="0" * 40,
        component_lock=inputs["component_lock"],
        runtime_dir=manifest_path.parent,
    )
    assert output.is_file()
    assert verify_product_release(product_root).runtime_manifest.is_file()
    assert json.loads(inputs["component_identities"].read_text())["project"] == {
        "component": "next",
        "repository": "FelixJI/vibeocr-next",
        "version": "0.7.0",
        "source_sha": "0" * 40,
    }


def test_binding_rejects_wrong_checkout_and_missing_capability(tmp_path: Path) -> None:
    inputs, manifest_path = _candidate(tmp_path)
    with pytest.raises(ValueError, match="does not match the Next checkout"):
        build_bindings(
            manifest_path.parent,
            version="0.7.0",
            source_sha="1" * 40,
            component_lock=inputs["component_lock"],
            product_identity=inputs["component_identities"],
        )
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["capabilities"].remove("ocr.recognition.v2")
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ValueError, match="missing required capabilities"):
        build_bindings(
            manifest_path.parent,
            version="0.7.0",
            source_sha="0" * 40,
            component_lock=inputs["component_lock"],
            product_identity=inputs["component_identities"],
        )


def test_binding_rejects_tampered_or_missing_runtime_wheel(tmp_path: Path) -> None:
    inputs, manifest_path = _candidate(tmp_path)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    wheel = manifest_path.parent / manifest["runtime_wheel"]
    wheel.write_bytes(b"tampered")
    with pytest.raises(ValueError, match="Runtime wheel SHA-256 mismatch"):
        build_bindings(
            manifest_path.parent,
            version="0.7.0",
            source_sha="0" * 40,
            component_lock=inputs["component_lock"],
            product_identity=inputs["component_identities"],
        )
    wheel.unlink()
    with pytest.raises(OSError):
        build_bindings(
            manifest_path.parent,
            version="0.7.0",
            source_sha="0" * 40,
            component_lock=inputs["component_lock"],
            product_identity=inputs["component_identities"],
        )
