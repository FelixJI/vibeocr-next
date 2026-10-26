"""Shared ONNX asset contracts (CUDA plugin, pinned downloads, retired recipes)."""

from __future__ import annotations

import hashlib
import io
import json
import sys
import zipfile

import pytest
from test_runtime_installer import _omit_unused_pip_bootstrap, _release
from vibeocr.runtime.environments import model_cache, onnx_assets
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentStore
from vibeocr.runtime.environments.runtime_installer import probe_nvidia_driver


def test_external_download_rejects_corruption_and_removes_partial(
    tmp_path, monkeypatch
):
    monkeypatch.setattr(onnx_assets, "urlopen", lambda *_a, **_k: io.BytesIO(b"bad"))
    target = tmp_path / "plugin.zip"
    with pytest.raises(ValueError, match="digest mismatch"):
        onnx_assets.pinned_download(
            "https://example.invalid", target, "0" * 64, lambda: None
        )
    assert not target.exists()
    assert not target.with_suffix(".zip.partial").exists()


def test_external_download_cancellation_does_not_replace_verified_cache(
    tmp_path, monkeypatch
):
    monkeypatch.setattr(onnx_assets, "urlopen", lambda *_a, **_k: io.BytesIO(b"new"))
    target = tmp_path / "plugin.zip"
    target.write_bytes(b"old")
    calls = 0

    def cancel():
        nonlocal calls
        calls += 1
        if calls > 1:
            raise RuntimeError("cancelled")

    with pytest.raises(RuntimeError, match="cancelled"):
        onnx_assets.pinned_download("https://example.invalid", target, "0" * 64, cancel)
    assert target.read_bytes() == b"old"
    assert not target.with_suffix(".zip.partial").exists()


@pytest.mark.parametrize("unsafe", [False, True])
def test_plugin_extracts_only_exact_official_inventory(tmp_path, monkeypatch, unsafe):
    artifact = tmp_path / "plugin.zip"
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr(onnx_assets.CUDA_PLUGIN_DLL, b"dll")
        archive.writestr("version.json", b"{}")
        if unsafe:
            archive.writestr("../escaped.dll", b"bad")
    monkeypatch.setattr(onnx_assets, "pinned_download", lambda *_a: artifact)
    target = tmp_path / "candidate"
    if unsafe:
        with pytest.raises(ValueError, match="unexpected members"):
            onnx_assets.install_cuda_plugin(target, tmp_path / "cache", lambda: None)
        assert not (tmp_path / "escaped.dll").exists()
    else:
        onnx_assets.install_cuda_plugin(target, tmp_path / "cache", lambda: None)
        assert (
            target / "providers/cuda" / onnx_assets.CUDA_PLUGIN_DLL
        ).read_bytes() == b"dll"


@pytest.mark.parametrize(
    "version,expected", [("579.99", "unsupported"), ("580.88", "ok")]
)
def test_cuda13_does_not_reuse_cuda12_driver_floor(monkeypatch, version, expected):
    from types import SimpleNamespace

    monkeypatch.setattr(
        "vibeocr.runtime.environments.runtime_installer.subprocess.run",
        lambda *_a, **_k: SimpleNamespace(returncode=0, stdout=version + "\n"),
    )
    assert probe_nvidia_driver()["status"] == "ok"
    assert probe_nvidia_driver(minimum_version=(580, 0))["status"] == expected


def _install_retired_rapid_record(
    manager: ManagedEnvironmentStore, recipe: str
) -> dict:
    item = manager.create("legacy rapid environment")
    registry = manager._registry
    data = json.loads(registry.read_text(encoding="utf-8"))
    record = data["environments"][item["id"]]
    record.update(status="installed", recipe=recipe)
    registry.write_text(json.dumps(data), encoding="utf-8")
    return manager._read()["environments"][item["id"]]


@pytest.mark.parametrize(
    "recipe",
    ["rapidocr-cpu", "rapidocr-cuda", "rapidocr+mineru-cpu", "rapidocr+mineru-cuda"],
)
def test_legacy_rapid_records_stay_listed_but_are_retired_and_uninstallable(
    tmp_path, monkeypatch, recipe
):
    """旧 Rapid 记录保留可读：不再加载引擎库做探针，直接投影不可用。"""
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest_path, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest_path,
        base_python=sys._base_executable,
        install_runner=lambda python, _scope, _endpoint: None,
    )
    record = _install_retired_rapid_record(manager, recipe)
    listed = manager.list()["environments"][0]
    assert listed["recipe"] == recipe
    assert listed["configured_recognition_types"] == []
    assert listed["dependency_state"] == "unavailable"
    assert listed["reason"] == "engine_recipe_retired"
    with pytest.raises(Exception, match="retired"):
        manager.preview_install(record["id"], recipe)
    with pytest.raises(Exception, match="unhealthy"):
        manager.prepare_switch(record["id"])
    # 数据保护：退役记录不被删除，目录仍在。
    assert (manager.root / record["path"]).exists()


def _synthetic_catalog() -> dict:
    payloads = {
        "PP-OCRv6_small_det": {
            "inference.onnx": b"det-model-bytes",
            "inference.yml": b"det: true\n",
        },
        "PP-OCRv6_small_rec": {
            "inference.onnx": b"rec-model-bytes",
            "inference.yml": b"rec: true\n",
        },
        "PP-LCNet_x1_0_doc_ori": {
            "inference.onnx": b"ori-model-bytes",
            "inference.yml": b"ori: true\n",
        },
    }
    models: dict[str, dict] = {}
    for name, files in payloads.items():
        entries = {
            source: {
                "revision": f"{source}-rev",
                "files": [
                    {
                        "path": path,
                        "size": len(payload),
                        "algorithm": "sha256",
                        "checksum": hashlib.sha256(payload).hexdigest(),
                        "revision": f"{source}-file-rev",
                    }
                    for path, payload in files.items()
                ],
            }
            for source in ("modelscope", "huggingface")
        }
        models[name] = {
            "repo_id": f"PaddlePaddle/{name}_onnx",
            "sources": entries,
        }
    return {"models": models}


def test_bundled_models_seed_into_shared_cache_consumed_by_prepare_models(
    tmp_path, monkeypatch
):
    """随包模型必须布置到 prepare_models 的共享资产缓存可消费位置。"""
    catalog = _synthetic_catalog()["models"]
    payloads = {
        "PP-OCRv6_small_det": {
            "inference.onnx": b"det-model-bytes",
            "inference.yml": b"det: true\n",
        },
        "PP-OCRv6_small_rec": {
            "inference.onnx": b"rec-model-bytes",
            "inference.yml": b"rec: true\n",
        },
        "PP-LCNet_x1_0_doc_ori": {
            "inference.onnx": b"ori-model-bytes",
            "inference.yml": b"ori: true\n",
        },
    }
    monkeypatch.setattr(model_cache, "_paddle_onnx_catalog", lambda: catalog)
    archive = tmp_path / "vibeocr-paddle-models-0.7.0.zip"
    with zipfile.ZipFile(archive, "w") as bundle:
        for name, files in payloads.items():
            for path, payload in files.items():
                bundle.writestr(f"{name}/{path}", payload)

    class Manifest:
        paddle_model_pack = type("Pack", (), {"archive_path": archive})()

    shared = tmp_path / "shared" / "model-cache"
    model_cache.seed_bundled_paddle_onnx_models(Manifest(), shared, lambda: None)
    # 布置目标即共享缓存布局：prepare_models（每来源一个快照，与生产
    # 单来源模型请求一致）直接复用共享 payload，不触发下载。
    for source in ("modelscope", "huggingface"):
        assets = [
            model_cache.ModelAsset(
                "paddlex-onnx-3.7.2",
                source,
                record["repo_id"],
                name,
                tuple(entry["path"] for entry in record["sources"][source]["files"]),
                record["sources"][source]["revision"],
                tuple(
                    model_cache.ModelFile(**entry)
                    for entry in record["sources"][source]["files"]
                ),
            )
            for name, record in catalog.items()
        ]
        prepared = model_cache.prepare_models(
            assets, tmp_path / "private" / source, shared_root=shared
        )
        assert prepared.downloaded_bytes == 0
        for name, files in payloads.items():
            for path, payload in files.items():
                assert (prepared.root / name / path).read_bytes() == payload


def test_bundled_models_reject_pack_bytes_that_mismatch_the_frozen_listing(
    tmp_path, monkeypatch
):
    catalog = _synthetic_catalog()["models"]
    monkeypatch.setattr(model_cache, "_paddle_onnx_catalog", lambda: catalog)
    archive = tmp_path / "vibeocr-paddle-models-0.7.0.zip"
    with zipfile.ZipFile(archive, "w") as bundle:
        bundle.writestr("PP-OCRv6_small_det/inference.onnx", b"tampered")

    class Manifest:
        paddle_model_pack = type("Pack", (), {"archive_path": archive})()

    with pytest.raises(
        model_cache.ModelCacheError, match="unexpected members|frozen listing"
    ):
        model_cache.seed_bundled_paddle_onnx_models(
            Manifest(), tmp_path / "shared", lambda: None
        )
    assert not (tmp_path / "shared" / "paddlex-onnx-3.7.2").exists()
