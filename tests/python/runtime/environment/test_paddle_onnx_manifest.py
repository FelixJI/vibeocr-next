"""Generated release binding for the unified Paddle ONNX + MinerU closure."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

import pytest
from test_runtime_installer import (
    _legacy_release,
    _lock_text,
    _release,
    _write_venv_record,
)
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.runtime_manifest import (
    ManifestError,
    load_runtime_manifest,
    validate_requirements_lock,
)

from scripts.build_runtime_manifest import build_runtime_manifest


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
    """统一推理闭包：生成 manifest 只绑 directml 锁（含 MinerU/onnx 声明）。

    不完整绑定（残留部分历史设备键）与被篡改的锁内容都必须 fail closed。
    空绑定（paddle_onnx_environments 为 {}）是合法的 base-only manifest，
    由 loader 的历史容忍规则接受，不在本测试的拒绝范围。
    """
    # build 侧锁绑定校验与形态无关：用历史 wheel+archive 形态构造
    # 完整 manifest 载荷（预构建形态的 wheel/archive 均为 None）。
    manifest_path, _ = _legacy_release(tmp_path / "release")
    value = json.loads(manifest_path.read_text(encoding="utf-8"))
    root = manifest_path.parent
    lock = tmp_path / "requirements-win-x64-paddle-ort-directml.lock"
    lock.write_text(_lock_text("win-x64-paddle-ort-directml"), encoding="utf-8")
    generated = build_runtime_manifest(
        runtime_wheel=root / value["runtime_wheel"],
        base_lock=root / value["profiles"]["win-x64-base"]["lock"],
        python_archive=root / value["python"]["archive"],
        python_version=value["python"]["version"],
        python_source_url=value["python"]["source_url"],
        installer_executable=root / value["installer"]["executable"],
        version="0.7.0",
        source_commit="0" * 40,
        build_workflow="tests/runtime",
        output_dir=tmp_path / "output",
        paddle_onnx_locks={"directml": lock},
    )
    manifest = load_runtime_manifest(generated)
    assert set(manifest.paddle_onnx_environments) == {"paddleocr-ort-directml"}
    for recipe, scope in manifest.paddle_onnx_environments.items():
        validate_requirements_lock(
            scope.lock_path, profile=recipe.replace("paddleocr", "win-x64-paddle")
        )
        # 统一闭包随包 MinerU：document 与 text/table/structure 同环境投影。
        assert ManagedEnvironmentStore._recipe_modes(recipe) == [
            "text",
            "table",
            "structure",
            "document",
        ]
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


@pytest.mark.parametrize("recipe", ["paddleocr-ort-directml"])
def test_managed_onnx_import_probe_disables_unfrozen_upstream_source_checks_only_in_child(
    tmp_path, recipe, monkeypatch
):
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    record = _write_venv_record(manager, "ort", status="installed", recipe=recipe)
    monkeypatch.delenv("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", raising=False)
    launch = manager._launch(record, sys.executable)
    assert launch["environment"]["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] == "True"
    assert "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK" not in os.environ
    record["recipe"] = "paddleocr-cpu"
    assert (
        "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"
        not in manager._launch(record, sys.executable)["environment"]
    )
