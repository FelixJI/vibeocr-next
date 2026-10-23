from __future__ import annotations

import hashlib
import io
import json
import sys
import zipfile
from dataclasses import replace
from pathlib import Path

import pytest
from test_runtime_installer import _omit_unused_pip_bootstrap, _release
from vibeocr.runtime.environments import rapid_assets
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.runtime_installer import (
    RuntimeInstallError,
    _install_rapidtable_model,
    probe_nvidia_driver,
)
from vibeocr.runtime.environments.runtime_manifest import load_runtime_manifest

from scripts.build_runtime_manifest import build_runtime_manifest


def test_external_download_rejects_corruption_and_removes_partial(
    tmp_path, monkeypatch
):
    monkeypatch.setattr(rapid_assets, "urlopen", lambda *_a, **_k: io.BytesIO(b"bad"))
    target = tmp_path / "model.onnx"
    with pytest.raises(ValueError, match="digest mismatch"):
        rapid_assets.pinned_download(
            "https://example.invalid", target, "0" * 64, lambda: None
        )
    assert not target.exists()
    assert not target.with_suffix(".onnx.partial").exists()


def test_external_download_cancellation_does_not_replace_verified_cache(
    tmp_path, monkeypatch
):
    monkeypatch.setattr(rapid_assets, "urlopen", lambda *_a, **_k: io.BytesIO(b"new"))
    target = tmp_path / "model.onnx"
    target.write_bytes(b"old")
    calls = 0

    def cancel():
        nonlocal calls
        calls += 1
        if calls > 1:
            raise RuntimeError("cancelled")

    with pytest.raises(RuntimeError, match="cancelled"):
        rapid_assets.pinned_download(
            "https://example.invalid", target, "0" * 64, cancel
        )
    assert target.read_bytes() == b"old"
    assert not target.with_suffix(".onnx.partial").exists()


@pytest.mark.parametrize("unsafe", [False, True])
def test_plugin_extracts_only_exact_official_inventory(tmp_path, monkeypatch, unsafe):
    artifact = tmp_path / "plugin.zip"
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr(rapid_assets.CUDA_PLUGIN_DLL, b"dll")
        archive.writestr("version.json", b"{}")
        if unsafe:
            archive.writestr("../escaped.dll", b"bad")
    monkeypatch.setattr(rapid_assets, "pinned_download", lambda *_a: artifact)
    target = tmp_path / "candidate"
    if unsafe:
        with pytest.raises(ValueError, match="unexpected members"):
            rapid_assets.install_cuda_plugin(target, tmp_path / "cache", lambda: None)
        assert not (tmp_path / "escaped.dll").exists()
    else:
        rapid_assets.install_cuda_plugin(target, tmp_path / "cache", lambda: None)
        assert (
            target / "providers/cuda" / rapid_assets.CUDA_PLUGIN_DLL
        ).read_bytes() == b"dll"


def test_rapidtable_install_copies_model_from_bound_pack_without_network(
    tmp_path, monkeypatch
):
    manifest_path, _ = _release(tmp_path / "release", with_base_pack=True)
    manifest = load_runtime_manifest(manifest_path)
    base = manifest.profiles["win-x64-base"]
    with base.lock_path.open("a", encoding="utf-8") as lock:
        lock.write("rapid-table==3.0.2\n")
    pack = manifest.path.parent / base.runtime_pack[0]
    with zipfile.ZipFile(pack, "a") as archive:
        archive.writestr("slanet-plus.onnx", b"bundled model")
    base = replace(
        base, runtime_pack_sha256=(hashlib.sha256(pack.read_bytes()).hexdigest(),)
    )
    manifest = replace(manifest, profiles={**manifest.profiles, "win-x64-base": base})
    monkeypatch.setattr(
        rapid_assets, "urlopen", lambda *_a, **_k: pytest.fail("must stay offline")
    )
    target = tmp_path / "candidate"
    _install_rapidtable_model(manifest, target, tmp_path / "cache")
    assert (
        target / "models/rapidtable/slanet-plus.onnx"
    ).read_bytes() == b"bundled model"


def test_rapidtable_install_fails_when_bound_pack_omits_model(tmp_path):
    manifest_path, _ = _release(tmp_path / "release", with_base_pack=True)
    manifest = load_runtime_manifest(manifest_path)
    with manifest.profiles["win-x64-base"].lock_path.open(
        "a", encoding="utf-8"
    ) as lock:
        lock.write("rapid-table==3.0.2\n")
    with pytest.raises(RuntimeInstallError, match="lacks the RapidTable model"):
        _install_rapidtable_model(manifest, tmp_path / "candidate", tmp_path / "cache")


@pytest.mark.parametrize(
    "version,expected", [("579.99", "unsupported"), ("580.88", "ok")]
)
def test_rapid_cuda13_does_not_reuse_cuda12_driver_floor(
    monkeypatch, version, expected
):
    from types import SimpleNamespace

    monkeypatch.setattr(
        "vibeocr.runtime.environments.runtime_installer.subprocess.run",
        lambda *_a, **_k: SimpleNamespace(returncode=0, stdout=version + "\n"),
    )
    assert probe_nvidia_driver()["status"] == "ok"
    assert probe_nvidia_driver(minimum_version=(580, 0))["status"] == expected


def test_generated_manifest_binds_rapid_cuda_lock_and_rejects_mutation(tmp_path):
    from vibeocr.runtime.environments.runtime_manifest import ManifestError

    manifest_path, _ = _release(tmp_path / "release")
    value = json.loads(manifest_path.read_text(encoding="utf-8"))
    root = manifest_path.parent
    lock = Path(__file__).resolve().parents[4] / (
        "config/runtime/win-x64-rapid-cu13/requirements-win-x64-rapid-cu13.lock"
    )
    generated = build_runtime_manifest(
        runtime_wheel=root / value["runtime_wheel"],
        base_lock=root / value["profiles"]["win-x64-base"]["lock"],
        cpu_lock=root / value["profiles"]["win-x64-cpu"]["lock"],
        cu126_lock=root / value["profiles"]["win-x64-cu126"]["lock"],
        cu126_gpu_lock=root / "requirements-win-x64-cu126-gpu.lock",
        python_archive=root / value["python"]["archive"],
        python_version=value["python"]["version"],
        python_source_url=value["python"]["source_url"],
        installer_archive=root / value["installer"]["archive"],
        version="0.7.0",
        source_commit="0" * 40,
        build_workflow="tests/runtime",
        output_dir=tmp_path / "output",
        rapid_cuda_lock=lock,
    )
    manifest = load_runtime_manifest(generated)
    assert set(manifest.profiles) == {"win-x64-base", "win-x64-cpu", "win-x64-cu126"}
    scope = manifest.rapid_cuda_environment
    assert scope is not None
    assert "onnxruntime==1.30.0" in scope.lock_path.read_text(encoding="utf-8")
    scope.lock_path.write_text("corrupted\n", encoding="utf-8")
    with pytest.raises(ManifestError, match="Rapid CUDA lock SHA-256 mismatch"):
        load_runtime_manifest(generated)


def _install_old_rapid_packages(python: Path, *, table_package: bool) -> None:
    site = python.parent.parent / "Lib/site-packages"
    names = [
        "fastapi",
        "rapidocr",
        "onnxruntime",
        "mineru",
        "torch",
        "nvidia-cudnn-cu13",
    ]
    if table_package:
        names.append("rapid-table")
    for name in names:
        metadata = site / f"{name}-1.0.dist-info"
        metadata.mkdir(parents=True)
        (metadata / "METADATA").write_text(
            f"Name: {name}\nVersion: 1.0\n", encoding="utf-8"
        )
    (site / "rapidocr").mkdir()
    (site / "rapidocr/__init__.py").write_text(
        "class RapidOCR: pass\n", encoding="utf-8"
    )
    (site / "mineru/parser").mkdir(parents=True)
    (site / "mineru/parser/api_server.py").write_text(
        "def create_app(): pass\n", encoding="utf-8"
    )


@pytest.mark.parametrize(
    "recipe", ["rapidocr-cpu", "rapidocr+mineru-cpu", "rapidocr+mineru-cuda"]
)
@pytest.mark.parametrize("table_package", [False, True])
def test_existing_rapid_environment_can_start_without_new_table_model(
    tmp_path, monkeypatch, recipe, table_package
):
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest_path, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest_path,
        base_python=sys._base_executable,
        install_runner=lambda python, _scope, _endpoint: _install_old_rapid_packages(
            python, table_package=table_package
        ),
    )
    item = manager.create("old rapid environment")
    plan = manager.preview_install(item["id"], recipe)
    installed = manager.install(plan["plan_id"], item["id"], recipe)
    assert installed["dependency_state"] == "installed"
    assert not (Path(installed["path"]) / "models/rapidtable/slanet-plus.onnx").exists()
    assert manager.prepare_switch(item["id"])["requires_supervisor"] is True


@pytest.mark.parametrize(
    "table_package,reason",
    [(False, "engine_packages_missing"), (True, "engine_model_missing")],
)
def test_new_cuda_recipe_still_requires_table_package_and_model(
    tmp_path, monkeypatch, table_package, reason
):
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest_path, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest_path,
        base_python=sys._base_executable,
        install_runner=lambda python, _scope, _endpoint: _install_old_rapid_packages(
            python, table_package=table_package
        ),
    )
    manager.manifest = replace(
        manager.manifest,
        rapid_cuda_environment=replace(
            manager.manifest.profiles["win-x64-base"].scopes[0],
            scope_id="rapidocr-cuda",
        ),
    )
    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.probe_nvidia_driver",
        lambda **_kwargs: {
            "status": "ok",
            "reason_code": None,
            "driver_version": "580.88",
        },
    )
    item = manager.create("new cuda environment")
    plan = manager.preview_install(item["id"], "rapidocr-cuda")
    with pytest.raises(ManagedEnvironmentError, match=reason):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cuda")
    assert manager.list()["environments"][0]["status"] == "empty"
