"""Generated release binding for the unified Paddle ONNX + MinerU closure."""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from test_product_manifest import _lock_text, _release
from vibeocr.runtime.environments.runtime_manifest import (
    ManifestError,
    load_runtime_manifest,
    validate_requirements_lock,
)


@pytest.mark.parametrize("device", ["directml"])
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


@pytest.mark.parametrize("device", ["directml"])
def test_paddle_host_lock_rejects_missing_windows_ocr_projection(tmp_path, device):
    profile = f"win-x64-paddle-ort-{device}"
    lock = tmp_path / "requirements.lock"
    lock.write_text(
        _lock_text(profile).replace("winrt-windows-media-ocr==", "missing-ocr=="),
        encoding="utf-8",
    )
    with pytest.raises(ManifestError, match="lacks its declared closure"):
        validate_requirements_lock(lock, profile=profile)


def test_generated_manifest_binds_directml_lock_and_rejects_incomplete_or_changed_closure(
    tmp_path,
):
    generated, _ = _release(tmp_path / "product")
    manifest = load_runtime_manifest(generated)
    assert set(manifest.paddle_onnx_environments) == {"paddleocr-ort-directml"}
    for recipe, scope in manifest.paddle_onnx_environments.items():
        validate_requirements_lock(
            scope.lock_path, profile=recipe.replace("paddleocr", "win-x64-paddle")
        )
    data = json.loads(generated.read_text(encoding="utf-8"))
    # CUDA 退役后绑定集合只接受空或统一 directml 闭包：换成历史 cpu 键
    # 整体拒绝，不允许部分/分设备残留。
    data["paddle_onnx_environments"]["cpu"] = data["paddle_onnx_environments"].pop(
        "directml"
    )
    generated.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(
        ManifestError,
        match="must bind the unified directml closure",
    ):
        load_runtime_manifest(generated)
    data["paddle_onnx_environments"] = {
        "directml": manifest.raw["paddle_onnx_environments"]["directml"]
    }
    generated.write_text(json.dumps(data), encoding="utf-8")
    manifest.paddle_onnx_environments["paddleocr-ort-directml"].lock_path.write_text(
        "corrupt", encoding="utf-8"
    )
    with pytest.raises(ManifestError, match="Paddle ONNX lock SHA-256 mismatch"):
        load_runtime_manifest(generated)
