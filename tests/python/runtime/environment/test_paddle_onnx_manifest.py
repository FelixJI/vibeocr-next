"""Generated release binding for the three isolated Paddle ONNX closures."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

import pytest
from test_runtime_installer import _lock_text, _release
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.runtime_manifest import (
    ManifestError,
    load_runtime_manifest,
    validate_requirements_lock,
)

from scripts.build_runtime_manifest import build_runtime_manifest


@pytest.mark.parametrize("device", ["cpu", "cuda", "directml"])
def test_paddle_host_locks_include_windows_ocr_projection(device):
    checkout = Path(__file__).resolve().parents[4]
    profile = f"win-x64-paddle-ort-{device}"
    lock = checkout / f"config/runtime/{profile}/requirements-{profile}.lock"
    declarations = lock.read_text(encoding="utf-8").splitlines()
    for package in (
        "winrt-runtime",
        "winrt-windows-foundation",
        "winrt-windows-foundation-collections",
        "winrt-windows-globalization",
        "winrt-windows-graphics-imaging",
        "winrt-windows-media-ocr",
        "winrt-windows-storage-streams",
    ):
        assert any(line.startswith(f"{package}==") for line in declarations), package


@pytest.mark.parametrize("device", ["cpu", "cuda", "directml"])
def test_paddle_host_lock_rejects_missing_windows_ocr_projection(tmp_path, device):
    profile = f"win-x64-paddle-ort-{device}"
    lock = tmp_path / "requirements.lock"
    lock.write_text(
        _lock_text(profile).replace("winrt-windows-media-ocr==", "missing-ocr=="),
        encoding="utf-8",
    )
    with pytest.raises(ManifestError, match="lacks its declared closure"):
        validate_requirements_lock(lock, profile=profile)


def test_generated_manifest_binds_three_locks_and_rejects_incomplete_or_changed_closure(
    tmp_path,
):
    manifest_path, _ = _release(tmp_path / "release")
    value = json.loads(manifest_path.read_text(encoding="utf-8"))
    root = manifest_path.parent
    checkout = Path(__file__).resolve().parents[4]
    locks = {
        device: checkout
        / f"config/runtime/win-x64-paddle-ort-{device}/requirements-win-x64-paddle-ort-{device}.lock"
        for device in ("cpu", "cuda", "directml")
    }
    generated = build_runtime_manifest(
        runtime_wheel=root / value["runtime_wheel"],
        base_lock=root / value["profiles"]["win-x64-base"]["lock"],
        cpu_lock=root / value["profiles"]["win-x64-cpu"]["lock"],
        cu126_lock=root / value["profiles"]["win-x64-cu126"]["lock"],
        cu126_gpu_lock=root / "requirements-win-x64-cu126-gpu.lock",
        python_archive=root / value["python"]["archive"],
        python_version=value["python"]["version"],
        python_source_url=value["python"]["source_url"],
        installer_executable=root / value["installer"]["executable"],
        version="0.7.0",
        source_commit="0" * 40,
        build_workflow="tests/runtime",
        output_dir=tmp_path / "output",
        paddle_onnx_locks=locks,
    )
    manifest = load_runtime_manifest(generated)
    assert set(manifest.paddle_onnx_environments) == {
        f"paddleocr-ort-{device}" for device in locks
    }
    for recipe, scope in manifest.paddle_onnx_environments.items():
        validate_requirements_lock(
            scope.lock_path, profile=recipe.replace("paddleocr", "win-x64-paddle")
        )
        assert ManagedEnvironmentStore._recipe_modes(recipe) == [
            "text",
            "table",
            "structure",
        ]
    data = json.loads(generated.read_text(encoding="utf-8"))
    del data["paddle_onnx_environments"]["directml"]
    generated.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ManifestError, match="all three providers"):
        load_runtime_manifest(generated)
    data["paddle_onnx_environments"]["directml"] = manifest.raw[
        "paddle_onnx_environments"
    ]["directml"]
    generated.write_text(json.dumps(data), encoding="utf-8")
    manifest.paddle_onnx_environments["paddleocr-ort-cpu"].lock_path.write_text(
        "corrupt", encoding="utf-8"
    )
    with pytest.raises(ManifestError, match="Paddle ONNX lock SHA-256 mismatch"):
        load_runtime_manifest(generated)


@pytest.mark.parametrize(
    "recipe", ["paddleocr-ort-cpu", "paddleocr-ort-cuda", "paddleocr-ort-directml"]
)
def test_managed_onnx_import_probe_disables_unfrozen_upstream_source_checks_only_in_child(
    tmp_path, recipe, monkeypatch
):
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("ort")
    record = manager._read()["environments"][item["id"]]
    record.update(status="installed", recipe=recipe)
    monkeypatch.delenv("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", raising=False)
    launch = manager._launch(record, sys.executable)
    assert launch["environment"]["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] == "True"
    assert "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK" not in os.environ
    record["recipe"] = "paddleocr-cpu"
    assert (
        "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"
        not in manager._launch(record, sys.executable)["environment"]
    )
