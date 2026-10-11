from __future__ import annotations

import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
import threading
import zipfile
from pathlib import Path
from uuid import uuid4

import httpx
import pytest
from vibeocr.runtime.environments import runtime_maintenance
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.managed_references import ManagedEnvironmentReferences
from vibeocr.runtime.environments.runtime_control import RuntimeControl
from vibeocr.runtime.environments.runtime_installer import (
    RuntimeInstaller,
    RuntimeInstallError,
    _extract_python_archive,
    _run_install_command,
    main,
    probe_nvidia_driver,
)
from vibeocr.runtime.environments.runtime_layout import (
    LayoutError,
    resolve_runtime_store,
)
from vibeocr.runtime.environments.runtime_lock import (
    RuntimeLockTimeout,
    RuntimeStoreLock,
)
from vibeocr.runtime.environments.runtime_maintenance import (
    RuntimeInstallFailure,
    RuntimeMaintenanceReporter,
    probe_runtime_components,
    profile_descriptor,
    runtime_profile_status,
    runtime_status_from_environment,
)
from vibeocr.runtime.environments.runtime_manifest import (
    ManifestError,
    load_runtime_manifest,
    runtime_component_binding,
    validate_requirements_lock,
)
from vibeocr.runtime.environments.runtime_selection import BoundDownloadSource

from scripts.build_runtime_manifest import (
    _extract_product_runtime_code,
    build_runtime_manifest,
)


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _lock_text(profile: str) -> str:
    base = "fastapi==1.0.0 \\\n    --hash=sha256:" + "1" * 64 + "\n"
    if profile == "win-x64-base":
        return base + (
            "rapidocr==3.9.2 \\\n    --hash=sha256:" + "6" * 64 + "\n"
            "onnxruntime==1.28.0 \\\n    --hash=sha256:" + "6" * 64 + "\n"
            "winrt-runtime==3.2.1 \\\n    --hash=sha256:" + "6" * 64 + "\n"
            "winrt-windows-foundation==3.2.1 \\\n    --hash=sha256:" + "6" * 64 + "\n"
            "winrt-windows-foundation-collections==3.2.1 \\\n"
            "    --hash=sha256:" + "6" * 64 + "\n"
            "opencv-python==5.0.0.93 \\\n    --hash=sha256:" + "6" * 64 + "\n"
        )
    if profile == "win-x64-cpu":
        return base + ("paddlepaddle==3.3.1 \\\n    --hash=sha256:" + "2" * 64 + "\n")
    return base + (
        "paddlepaddle-gpu @ https://example.invalid/cu126/paddle.whl \\\n"
        "    --hash=sha256:" + "3" * 64 + "\n"
        "torch @ https://example.invalid/cu126/torch.whl \\\n"
        "    --hash=sha256:" + "4" * 64 + "\n"
        "torchvision @ https://example.invalid/cu126/torchvision.whl \\\n"
        "    --hash=sha256:" + "5" * 64 + "\n"
    )


def _release(
    root: Path,
    *,
    with_base_pack: bool = False,
) -> tuple[Path, Path]:
    root.mkdir()
    wheel = root / "vibeocr_next_runtime-0.7.0-py3-none-any.whl"
    with zipfile.ZipFile(wheel, mode="w") as archive:
        for module in (
            "vibeocr/runtime/__init__.py",
            "vibeocr/runtime/host/main.py",
            "vibeocr/runtime/environments/env_config.py",
            "vibeocr/runtime/recognition/paddle_worker.py",
            "vibeocr/runtime/documents/pdf_backend_process.py",
            "vibeocr/runtime_contracts/__init__.py",
        ):
            archive.writestr(module, "")
        archive.writestr("vibeocr/runtime/environments/dependency_profiles.json", "{}")
        archive.writestr(
            "vibeocr_next_runtime-0.7.0.dist-info/METADATA",
            "Name: vibeocr-next-runtime\nVersion: 0.7.0\n",
        )
    _extract_product_runtime_code(wheel, root, "0.7.0")
    python_archive = root / "cpython-3.13.16-win_amd64-install_only.tar.gz"
    python_archive.write_bytes(b"python-archive")
    installer_archive = root / "vibeocr-runtime-installer-v0.7.0-win-x64.zip"
    with zipfile.ZipFile(installer_archive, mode="w") as archive:
        archive.writestr(
            "runtime-installer/vibeocr-runtime-installer.exe",
            b"installer",
        )
    profiles = {}
    for profile in ("win-x64-base", "win-x64-cpu", "win-x64-cu126"):
        lock = root / f"requirements-{profile}.lock"
        lock.write_text(_lock_text(profile), encoding="utf-8")
        profiles[profile] = {
            "lock": lock.name,
            "sha256": _sha(lock.read_bytes()),
            "runtime_pack": None,
        }
    cu126_gpu_lock = root / "requirements-win-x64-cu126-gpu.lock"
    cu126_gpu_lock.write_text(_lock_text("win-x64-cu126"), encoding="utf-8")
    profiles["win-x64-cu126"]["install_scopes"] = [
        {
            "scope_id": "gpu-runtime",
            "component_ids": [
                "rapidocr-base",
                "runtime_host",
                "gpu_runtime",
            ],
            "lock": cu126_gpu_lock.name,
            "sha256": _sha(cu126_gpu_lock.read_bytes()),
            "runtime_pack": None,
        }
    ]
    base_ids = ["rapidocr-base", "runtime_host"]
    for profile, suffix in [("win-x64-cpu", "cpu"), ("win-x64-cu126", "cuda")]:
        for engine in ["paddleocr", "mineru"]:
            host = (
                profiles["win-x64-base"] if engine == "paddleocr" else profiles[profile]
            )
            profiles[profile].setdefault("install_scopes", []).append(
                {
                    "scope_id": engine,
                    "component_ids": [
                        *base_ids,
                        f"{engine}-{suffix}",
                        *(
                            ["gpu_runtime"]
                            if engine == "mineru" and suffix == "cuda"
                            else []
                        ),
                    ],
                    "lock": host["lock"],
                    "sha256": host["sha256"],
                    "runtime_pack": None,
                }
            )
    if with_base_pack:
        pack = root / "vibeocr-runtime-pack-win-x64-base-0.7.0.zip"
        with zipfile.ZipFile(pack, mode="w") as archive:
            archive.writestr("pack-requirements.txt", "rapidocr==3.9.2")
            archive.writestr("rapidocr-3.9.2-py3-none-any.whl", b"rapidocr-wheel")
            archive.writestr(
                "onnxruntime-1.28.0-cp313-cp313-win_amd64.whl", b"ort-wheel"
            )
        profiles["win-x64-base"]["runtime_pack"] = [pack.name]
        profiles["win-x64-base"]["runtime_pack_sha256"] = [_sha(pack.read_bytes())]
    manifest = {
        "schema_version": 2,
        "product": {
            "component": "next",
            "repository": "FelixJI/vibeocr-next",
            "version": "0.7.0",
            "source_sha": "0" * 40,
        },
        "runtime_wheel": wheel.name,
        "runtime_sha256": _sha(wheel.read_bytes()),
        "python": {
            "version": "3.13.16",
            "abi": "cp313",
            "platform": "win_amd64",
            "source_url": (
                "https://github.com/astral-sh/python-build-standalone/releases/"
                "download/20261001/"
                "cpython-3.13.16+20261001-x86_64-pc-windows-msvc"
                "-install_only.tar.gz"
            ),
            "archive": python_archive.name,
            "sha256": _sha(python_archive.read_bytes()),
        },
        "installer": {
            "archive": installer_archive.name,
            "sha256": _sha(installer_archive.read_bytes()),
            "executable_path": "runtime-installer/vibeocr-runtime-installer.exe",
            "executable_sha256": _sha(b"installer"),
        },
        "profiles": profiles,
        "capabilities": ["ocr.recognition.v2"],
        "build_workflow": "tests/runtime",
    }
    manifest_path = root / "runtime-manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    component = {
        "schema_version": 2,
        "product": {
            "component": "next",
            "repository": "FelixJI/vibeocr-next",
            "version": "0.7.0",
            "source_sha": "0" * 40,
            "runtime_manifest_sha256": _sha(manifest_path.read_bytes()),
            "accelerator": "cpu",
        },
        "required_capabilities": ["ocr.recognition.v2"],
    }
    component_path = root / "component-lock.json"
    component_path.write_text(
        json.dumps(component, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return manifest_path, component_path


def _fake_install(partial: Path, _manifest, _profile: str) -> Path:
    python = partial / "Scripts" / "python.exe"
    python.parent.mkdir(parents=True)
    python.write_bytes(b"python")
    return python


def _pypi_source() -> tuple[BoundDownloadSource, ...]:
    return (
        BoundDownloadSource(
            kind="package_index",
            source_id="pypi",
            endpoint="https://pypi.org/simple",
        ),
    )


def test_legacy_probe_reuses_frontend_update_but_rejects_runtime_or_abi_change(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    product = tmp_path / "product"
    runtime = product / "runtime"
    runtime.mkdir(parents=True)
    (runtime / "python.exe").write_bytes(b"synthetic interpreter")
    marker_path = runtime / ".installed.json"
    original_manifest = load_runtime_manifest(manifest)
    marker = {
        "schema_version": 1,
        "backend_version": original_manifest.backend_version,
        "manifest_sha256": original_manifest.sha256,
        "accelerator": "cpu",
        "component_ids": ["rapidocr-base", "runtime_host"],
    }
    marker_path.write_text(json.dumps(marker), encoding="utf-8")
    actual_version = [3, 13, 16]

    def probe(_args: list[str], **_kwargs: object) -> subprocess.CompletedProcess[str]:
        return subprocess.CompletedProcess(
            _args,
            0,
            stdout=json.dumps(
                {
                    "prefix": str(runtime),
                    "base_prefix": str(runtime),
                    "version": actual_version,
                    "packages": ["fastapi"],
                    "runtime_version": "0.7.0",
                }
            ),
        )

    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.subprocess.run", probe
    )
    current = ManagedEnvironmentStore(
        product_root=product, component_lock=component, runtime_manifest=manifest
    )
    assert current.list()["environments"][0]["python_state"] == "ready"

    document = json.loads(manifest.read_text(encoding="utf-8"))
    document["product"]["source_sha"] = "a" * 40
    document["product"]["version"] = "0.8.0"
    document["runtime_wheel"] = "vibeocr_next_runtime-0.8.0-py3-none-any.whl"
    (manifest.parent / document["runtime_wheel"]).write_bytes(b"runtime-wheel")
    manifest.write_text(json.dumps(document, sort_keys=True) + "\n", encoding="utf-8")
    binding = json.loads(component.read_text(encoding="utf-8"))
    binding["product"]["source_sha"] = "a" * 40
    binding["product"]["version"] = "0.8.0"
    binding["product"]["runtime_manifest_sha256"] = _sha(manifest.read_bytes())
    component.write_text(json.dumps(binding, sort_keys=True) + "\n", encoding="utf-8")
    updated = ManagedEnvironmentStore(
        product_root=product, component_lock=component, runtime_manifest=manifest
    )
    assert updated.list()["environments"][0]["python_state"] == "ready"

    marker["backend_version"] = "0.8.0"
    marker_path.write_text(json.dumps(marker), encoding="utf-8")
    incompatible = updated.list()["environments"][0]
    assert incompatible["reason"] == "legacy_runtime_marker_invalid"
    assert incompatible["python_state"] == "unavailable"
    assert incompatible["python_version"] == "3.13.16"

    marker["backend_version"] = original_manifest.backend_version
    marker_path.write_text(json.dumps(marker), encoding="utf-8")
    actual_version[:] = [3, 12, 9]
    changed_abi = updated.list()["environments"][0]
    assert changed_abi["reason"] == "base_or_abi_changed"
    assert changed_abi["python_version"] == "3.12.9"
    assert changed_abi["abi"] == "cp312"

    def timeout(
        _args: list[str], **_kwargs: object
    ) -> subprocess.CompletedProcess[str]:
        raise subprocess.TimeoutExpired(_args, 20)

    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.subprocess.run", timeout
    )
    stalled = updated.list()["environments"][0]
    assert stalled["reason"] == "python_probe_failed"
    assert stalled["python_state"] == "unavailable"


def test_managed_status_uses_installed_scope_without_legacy_marker(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    created = manager.create("RapidOCR")
    registry = json.loads(manager._registry.read_text(encoding="utf-8"))
    record = registry["environments"][created["id"]]
    record.update(status="installed", recipe="rapidocr-cpu")
    manager._registry.write_text(json.dumps(registry), encoding="utf-8")
    root = manager._safe_path(record)
    assert not (root / ".installed.json").exists()
    for key, value in manager._launch(record, created["python"])["environment"].items():
        monkeypatch.setenv(key, value)
    monkeypatch.setattr(
        runtime_maintenance,
        "_distribution_versions",
        lambda _root: {"vibeocr-next-runtime": "0.7.0"},
    )
    monkeypatch.setattr(
        runtime_maintenance,
        "probe_runtime_components",
        lambda _root, component_ids, **_kwargs: dict.fromkeys(component_ids, True),
    )
    runtime_maintenance._component_probe_cache.clear()
    status = runtime_status_from_environment("named", "ready")
    assert status["service_state"] == "ready", status
    assert status["profile"]["profile_id"] == "win-x64-base"


def test_legacy_cuda_launch_preserves_marker_accelerator(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    runtime = tmp_path / "product" / "runtime"
    runtime.mkdir(parents=True)
    (runtime / "python.exe").write_bytes(b"synthetic interpreter")
    marker = {
        "schema_version": 1,
        "backend_version": "0.7.0",
        "manifest_sha256": load_runtime_manifest(manifest).sha256,
        "accelerator": "nvidia_cuda",
        "component_ids": ["rapidocr-base", "runtime_host", "mineru-cuda"],
    }
    (runtime / ".installed.json").write_text(json.dumps(marker), encoding="utf-8")
    monkeypatch.setattr(
        "vibeocr.runtime.environments.managed_environments.subprocess.run",
        lambda args, **_kwargs: subprocess.CompletedProcess(
            args,
            0,
            stdout=json.dumps(
                {
                    "prefix": str(runtime),
                    "base_prefix": str(runtime),
                    "version": [3, 13, 16],
                    "packages": ["fastapi"],
                    "runtime_version": "0.7.0",
                }
            ),
        ),
    )
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    launch = manager.prepare_switch("legacy")["launch"]
    assert launch["environment"]["VIBEOCR_RUNTIME_ACCELERATOR"] == "nvidia_cuda"
    assert launch["environment"]["VIBEOCR_USE_GPU"] == "true"


def test_legacy_status_reuses_current_product_code_after_frontend_update(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest_path, _ = _release(tmp_path / "release")
    document = json.loads(manifest_path.read_text(encoding="utf-8"))
    document["product"]["version"] = "0.8.0"
    document["product"]["source_sha"] = "a" * 40
    document["runtime_wheel"] = "vibeocr_next_runtime-0.8.0-py3-none-any.whl"
    (manifest_path.parent / document["runtime_wheel"]).write_bytes(b"current wheel")
    manifest_path.write_text(json.dumps(document, sort_keys=True), encoding="utf-8")
    manifest = load_runtime_manifest(manifest_path, verify_artifacts=False)
    root = tmp_path / "old-runtime"
    root.mkdir()
    (root / "python.exe").write_bytes(b"synthetic interpreter")
    (root / ".installed.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "backend_version": "0.7.0",
                "manifest_sha256": "1" * 64,
                "accelerator": "cpu",
                "component_ids": ["rapidocr-base", "runtime_host"],
            }
        ),
        encoding="utf-8",
    )
    old_metadata = root / "Lib/site-packages/vibeocr_next_runtime-0.7.0.dist-info"
    old_metadata.mkdir(parents=True)
    (old_metadata / "METADATA").write_text(
        "Name: vibeocr-next-runtime\nVersion: 0.7.0\n", encoding="utf-8"
    )
    code_root = tmp_path / "current-product-code"
    current_metadata = code_root / "vibeocr_next_runtime-0.8.0.dist-info"
    current_metadata.mkdir(parents=True)
    (current_metadata / "METADATA").write_text(
        "Name: vibeocr-next-runtime\nVersion: 0.8.0\n", encoding="utf-8"
    )
    module_file = code_root / "vibeocr/runtime/environments/runtime_maintenance.py"
    module_file.parent.mkdir(parents=True)
    module_file.write_text("", encoding="utf-8")
    monkeypatch.setattr(runtime_maintenance, "__file__", str(module_file))
    monkeypatch.setenv("VIBEOCR_PRODUCT_CODE_ROOT", str(code_root))
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_ID", "legacy")
    monkeypatch.setattr(runtime_maintenance.sys, "prefix", str(root))
    monkeypatch.setattr(runtime_maintenance.sys, "version_info", (3, 13, 16))
    current = runtime_profile_status(
        manifest,
        accelerator="cpu",
        runtime_root=root,
        probe_results={"rapidocr-base": True, "runtime_host": True},
    )
    assert all(
        component["actual_state"] == "ready" for component in current["components"]
    )
    monkeypatch.setenv("VIBEOCR_RUNTIME_MANIFEST", str(manifest_path))
    monkeypatch.setenv("VIBEOCR_RUNTIME_ROOT", str(root))
    monkeypatch.setenv("VIBEOCR_RUNTIME_ACCELERATOR", "cpu")
    monkeypatch.setenv("VIBEOCR_RUNTIME_STATE_ROOT", str(tmp_path / "state"))
    monkeypatch.setattr(
        runtime_maintenance,
        "probe_runtime_components",
        lambda _root, component_ids, **_kwargs: dict.fromkeys(component_ids, True),
    )
    runtime_maintenance._component_probe_cache.clear()
    assert (
        runtime_status_from_environment("legacy", "ready")["service_state"] == "ready"
    )
    (old_metadata / "METADATA").write_text(
        "Name: vibeocr-next-runtime\nVersion: 0.6.0\n", encoding="utf-8"
    )
    drifted = runtime_profile_status(
        manifest,
        accelerator="cpu",
        runtime_root=root,
        probe_results={"rapidocr-base": True, "runtime_host": True},
    )
    assert any(
        component["drift_reason"] == "identity_mismatch"
        for component in drifted["components"]
    )
    runtime_maintenance._component_probe_cache.clear()
    assert (
        runtime_status_from_environment("legacy", "ready")["service_state"]
        == "degraded"
    )


def test_product_code_bootstrap_keeps_environment_packages_and_no_bytecode(
    tmp_path: Path,
) -> None:
    environment = tmp_path / "environment"
    subprocess.run(
        [sys._base_executable, "-m", "venv", "--without-pip", str(environment)],
        check=True,
    )
    python = environment / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    site_packages = environment / (
        "Lib/site-packages"
        if os.name == "nt"
        else f"lib/python{sys.version_info.major}.{sys.version_info.minor}/site-packages"
    )
    site_packages.mkdir(parents=True, exist_ok=True)
    (site_packages / "engine_only.py").write_text("VALUE = 'environment'\n")
    old_metadata = site_packages / "vibeocr_next_runtime-0.7.0.dist-info"
    old_metadata.mkdir()
    (old_metadata / "METADATA").write_text(
        "Name: vibeocr-next-runtime\nVersion: 0.7.0\n", encoding="utf-8"
    )
    code_root = tmp_path / "product" / "runtime-code"
    current_metadata = code_root / "vibeocr_next_runtime-0.8.0.dist-info"
    current_metadata.mkdir(parents=True)
    (current_metadata / "METADATA").write_text(
        "Name: vibeocr-next-runtime\nVersion: 0.8.0\n", encoding="utf-8"
    )
    modules = (
        "vibeocr.runtime.host.main",
        "vibeocr.runtime.recognition.paddle_worker",
        "vibeocr.runtime.documents.pdf_backend_process",
    )
    script = (
        "import importlib.metadata,json,sys,engine_only;"
        "print(json.dumps({'file':__file__,'prefix':sys.prefix,"
        "'runtime':importlib.metadata.version('vibeocr-next-runtime'),"
        "'engine':engine_only.VALUE}))"
    )
    for module in modules:
        source = code_root.joinpath(*module.split(".")).with_suffix(".py")
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_text(script, encoding="utf-8")
        command = (
            "import os,runpy,sys;"
            "sys.path.insert(0,os.environ['VIBEOCR_PRODUCT_CODE_ROOT']);"
            f"runpy.run_module('{module}',run_name='__main__')"
        )
        result = subprocess.run(
            [str(python), "-I", "-B", "-c", command],
            check=True,
            capture_output=True,
            text=True,
            env={**os.environ, "VIBEOCR_PRODUCT_CODE_ROOT": str(code_root)},
        )
        body = json.loads(result.stdout)
        assert Path(body["file"]) == source
        assert Path(body["prefix"]) == environment
        assert body["runtime"] == "0.8.0"
        assert body["engine"] == "environment"
    assert not list(code_root.rglob("__pycache__"))


def test_named_environments_are_real_empty_venvs_and_switch_is_cas(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    first = manager.create("空环境一")
    second = manager.create("空环境二")
    assert first["python_state"] == second["python_state"] == "ready"
    assert first["dependency_state"] == "empty"
    assert first["engine_state"] == "unavailable"
    assert first["id"] != second["id"]
    assert not set(first["packages"]).intersection(
        {"fastapi", "rapidocr", "paddleocr", "mineru", "torch", "onnxruntime"}
    )
    assert Path(first["python"]).is_file()
    assert (
        len(
            ManagedEnvironmentStore(
                product_root=tmp_path / "product",
                component_lock=component,
                runtime_manifest=manifest,
                base_python=sys._base_executable,
            ).list()["environments"]
        )
        == 2
    )
    prepared = manager.prepare_switch(first["id"])
    assert prepared["requires_supervisor"] is False
    manager.commit_switch(prepared)
    plan = manager.preview_install(first["id"], "rapidocr-cpu")
    assert plan["environment_id"] == first["id"]
    assert plan["source_ids"] == [
        "tuna-pypi",
        "paddleocr-modelscope",
        "mineru-modelscope",
    ]
    assert plan["dependencies"]
    with pytest.raises(ManagedEnvironmentError, match="stale"):
        manager.commit_switch(prepared)
    manager.commit_switch(manager.prepare_switch(second["id"]))
    assert manager.list()["active_id"] == second["id"]


def test_named_environment_registry_rejects_external_python(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("isolated")
    registry = manager.paths.state_root / "environments.json"
    value = json.loads(registry.read_text(encoding="utf-8"))
    value["environments"][item["id"]]["path"] = str(Path(sys._base_executable))
    registry.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(ManagedEnvironmentError, match="outside managed store"):
        manager.list()


def test_active_job_reference_blocks_switch_install_and_delete(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    first = manager.create("running")
    second = manager.create("target")
    manager.commit_switch(manager.prepare_switch(first["id"]))
    references = ManagedEnvironmentReferences(
        manager._registry,
        manager._lock,
        manager._references,
        first["id"],
        first["revision"],
    )
    prepared = manager.prepare_switch(second["id"])
    job_id = str(uuid4())
    references.admit(job_id)
    try:
        with pytest.raises(ManagedEnvironmentError, match="active jobs"):
            manager.commit_switch(prepared)
        assert manager.list()["active_id"] == first["id"]
        with pytest.raises(ManagedEnvironmentError, match="active jobs"):
            manager.prepare_switch(second["id"])
        assert manager.preview_install(second["id"], "rapidocr-cpu")
    finally:
        references.release(job_id)
    assert not (references.reference_root / f"{job_id}.lock").exists()
    manager.commit_switch(prepared)
    with pytest.raises(RuntimeLockTimeout, match="no longer active"):
        references.admit(str(uuid4()))


def _omit_unused_pip_bootstrap(monkeypatch: pytest.MonkeyPatch) -> None:
    # These tests inject the installer: only the real venv/lock/probe paths
    # matter, and the injected runners never invoke pip. Bootstrapping an
    # unused pip costs seconds of ensurepip per candidate venv, which made CI
    # time out before it reached the concurrent operations whose one-second
    # deadline we verify.
    run = subprocess.run

    def without_pip(args: list[str], **kwargs):
        if args[1:4] == ["-I", "-m", "venv"] and "--without-pip" not in args:
            args = [*args[:-1], "--without-pip", args[-1]]
        return run(args, **kwargs)

    monkeypatch.setattr(subprocess, "run", without_pip)


def test_inactive_install_does_not_block_active_job_admission_or_target_conflict(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")
    started = threading.Event()
    finish = threading.Event()

    def install(_python: Path, _scope, _source: str) -> None:
        started.set()
        assert finish.wait(10)

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=install,
    )
    active = manager.create("active")
    target = manager.create("target")
    manager.commit_switch(manager.prepare_switch(active["id"]))
    plan = manager.preview_install(target["id"], "rapidocr-cpu", ("tuna-pypi",))
    original_probe = manager._probe
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: (
            {
                "healthy": True,
                "reason": None,
                "python": str(manager._venv_python(manager._safe_path(record))),
            }
            if record["status"] == "installed"
            else original_probe(record)
        ),
    )
    installed: list[object] = []
    worker = threading.Thread(
        target=lambda: installed.append(
            manager.install(
                plan["plan_id"], target["id"], "rapidocr-cpu", ("tuna-pypi",)
            )
        ),
        daemon=True,
    )
    worker.start()
    admitted = threading.Event()
    admission_errors: list[Exception] = []
    references = ManagedEnvironmentReferences(
        manager._registry,
        manager._lock,
        manager._references,
        active["id"],
        active["revision"],
    )

    def admit() -> None:
        job_id = str(uuid4())
        try:
            references.admit(job_id)
            admitted.set()
            references.release(job_id)
        except Exception as error:
            admission_errors.append(error)

    admission = threading.Thread(target=admit, daemon=True)
    conflict_finished = threading.Event()
    conflict_errors: list[Exception] = []

    def delete_target() -> None:
        try:
            manager.delete(target["id"])
        except Exception as error:
            conflict_errors.append(error)
        finally:
            conflict_finished.set()

    conflict = threading.Thread(target=delete_target, daemon=True)
    try:
        assert started.wait(5)
        running = next(
            item
            for item in manager.list()["environments"]
            if item["id"] == target["id"]
        )
        assert running["last_install_failure"]["reason_code"] == "install_in_progress"
        assert running["last_install_failure"]["plan_id"] == plan["plan_id"]
        admission.start()
        conflict.start()
        assert admitted.wait(1), "A job admission waited on B's package installation"
        assert not admission_errors
        assert conflict_finished.wait(1), "B delete waited on B's installation"
        assert len(conflict_errors) == 1
        assert isinstance(conflict_errors[0], ManagedEnvironmentError)
    finally:
        finish.set()
        worker.join(10)
        if admission.ident is not None:
            admission.join(10)
        if conflict.ident is not None:
            conflict.join(10)
    assert len(installed) == 1
    assert manager.list()["active_id"] == active["id"]
    assert (
        next(
            item
            for item in manager.list()["environments"]
            if item["id"] == target["id"]
        )["last_install_failure"]
        is None
    )


def test_inactive_install_commit_rejects_active_pointer_drift(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")
    started = threading.Event()
    finish = threading.Event()

    def install(_python: Path, _scope, _source: str) -> None:
        started.set()
        assert finish.wait(10)

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=install,
    )
    active = manager.create("active")
    target = manager.create("target")
    replacement = manager.create("replacement")
    manager.commit_switch(manager.prepare_switch(active["id"]))
    plan = manager.preview_install(target["id"], "rapidocr-cpu", ("tuna-pypi",))
    # The deadline measures lock contention, not interpreter startup for replacement.
    original_probe = manager._probe
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: (
            {
                "healthy": True,
                "reason": None,
                "python": str(manager._venv_python(manager._safe_path(record))),
            }
            if record["status"] == "installed" or record["id"] == replacement["id"]
            else original_probe(record)
        ),
    )
    install_errors: list[Exception] = []

    def run_install() -> None:
        try:
            manager.install(
                plan["plan_id"], target["id"], "rapidocr-cpu", ("tuna-pypi",)
            )
        except Exception as error:
            install_errors.append(error)

    worker = threading.Thread(target=run_install, daemon=True)
    worker.start()
    switched = threading.Event()
    switch_errors: list[Exception] = []

    def switch() -> None:
        try:
            manager.commit_switch(manager.prepare_switch(replacement["id"]))
            switched.set()
        except Exception as error:
            switch_errors.append(error)

    switch_worker = threading.Thread(target=switch, daemon=True)
    try:
        assert started.wait(5)
        switch_worker.start()
        assert switched.wait(1), "Unrelated active-pointer switch waited on B install"
    finally:
        finish.set()
        worker.join(10)
        if switch_worker.ident is not None:
            switch_worker.join(10)
    assert not switch_errors
    assert len(install_errors) == 1
    assert isinstance(install_errors[0], runtime_maintenance.RuntimeInstallPlanStale)
    registry = manager.list()
    assert registry["active_id"] == replacement["id"]
    assert (
        next(item for item in registry["environments"] if item["id"] == target["id"])[
            "status"
        ]
        == "empty"
    )


def test_installed_switch_rejects_unverified_supervisor_health(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("installed")
    registry = manager.paths.state_root / "environments.json"
    value = json.loads(registry.read_text(encoding="utf-8"))
    value["environments"][item["id"]].update(
        {"status": "installed", "recipe": "rapidocr-cpu"}
    )
    registry.write_text(json.dumps(value), encoding="utf-8")
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: {"healthy": True, "python": item["python"], "reason": None},
    )
    prepared = manager.prepare_switch(item["id"])
    assert prepared["launch"]["python_executable"] == item["python"]
    with pytest.raises(ManagedEnvironmentError, match="not healthy"):
        manager.commit_switch(
            prepared, started_health={"port": 1, "instance_id": "not-running"}
        )
    assert manager.list()["active_id"] is None


def test_empty_environment_repairs_changed_python_binding(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("portable")
    registry = manager.paths.state_root / "environments.json"
    value = json.loads(registry.read_text(encoding="utf-8"))
    value["environments"][item["id"]]["python_version"] = "3.13.0"
    registry.write_text(json.dumps(value), encoding="utf-8")
    assert manager.list()["environments"][0]["reason"] == "base_or_abi_changed"
    repaired = manager.repair_empty(item["id"])
    assert repaired["revision"] == 2
    assert repaired["python_state"] == "ready"
    assert repaired["packages"] == []


def test_repair_after_failed_install_clears_stale_revision_failure_only_on_success(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")

    def fail_install(_python: Path, _scope, _endpoint: str) -> None:
        raise ManagedEnvironmentError("synthetic install failure")

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=fail_install,
    )
    item = manager.create("failed then repaired")
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(ManagedEnvironmentError, match="synthetic install failure"):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",))
    failure = manager.list()["environments"][0]["last_install_failure"]
    assert failure["environment_revision"] == 1

    registry = manager.paths.state_root / "environments.json"
    value = json.loads(registry.read_text(encoding="utf-8"))
    value["environments"][item["id"]]["python_version"] = "3.13.0"
    registry.write_text(json.dumps(value), encoding="utf-8")

    def fail_repair() -> Path:
        raise ManagedEnvironmentError("synthetic repair failure")

    original_base_python = manager._base_python
    monkeypatch.setattr(manager, "_base_python", fail_repair)
    with pytest.raises(ManagedEnvironmentError, match="synthetic repair failure"):
        manager.repair_empty(item["id"])
    assert manager.list()["environments"][0]["last_install_failure"] == failure

    monkeypatch.setattr(manager, "_base_python", original_base_python)
    repaired = manager.repair_empty(item["id"])
    assert repaired["revision"] == 2
    assert repaired["last_install_failure"] is None
    assert (
        ManagedEnvironmentStore(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest,
            base_python=sys._base_executable,
        ).list()["environments"][0]["last_install_failure"]
        is None
    )
    stored = json.loads(registry.read_text(encoding="utf-8"))
    assert "last_install_operation" not in stored["environments"][item["id"]]


def test_named_environment_probe_does_not_extract_missing_base(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    product = tmp_path / "product"
    creator = ManagedEnvironmentStore(
        product_root=product,
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = creator.create("portable")
    reader = ManagedEnvironmentStore(
        product_root=product,
        component_lock=component,
        runtime_manifest=manifest,
    )
    base = reader.root / "python-base"
    assert not base.exists()
    assert reader.list()["environments"][0]["reason"] == "base_or_abi_changed"
    assert not base.exists()
    with pytest.raises(ManagedEnvironmentError, match="base_or_abi_changed"):
        reader.prepare_switch(item["id"])
    assert not base.exists()


def test_failed_named_install_preserves_empty_revision(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")

    def fail_install(_python: Path, _scope, _endpoint: str) -> None:
        raise ManagedEnvironmentError("synthetic install failure")

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=fail_install,
    )
    item = manager.create("target")
    manager.commit_switch(manager.prepare_switch(item["id"]))
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(runtime_maintenance.RuntimeInstallPlanStale):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("pypi",))
    with pytest.raises(ManagedEnvironmentError, match="synthetic"):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(ManagedEnvironmentError, match="synthetic"):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",))
    surviving = manager.list()["environments"][0]
    assert surviving["revision"] == 1
    assert surviving["status"] == "empty"
    assert surviving["python_state"] == "ready"
    assert manager.list()["active_id"] == item["id"]
    failure = surviving["last_install_failure"]
    assert failure["reason_code"] == "unknown"
    assert failure["phase"] == "failed"
    assert failure["recipe"] == "rapidocr-cpu"
    assert failure["environment_revision"] == 1
    stored = json.loads(manager._registry.read_text(encoding="utf-8"))
    assert (
        stored["environments"][item["id"]]["last_install_operation"]["plan_id"]
        == plan["plan_id"]
    )
    assert (
        ManagedEnvironmentStore(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest,
            base_python=sys._base_executable,
        ).list()["environments"][0]["last_install_failure"]
        == failure
    )


def test_named_install_failure_redacts_diagnostics_and_interruption_is_durable(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")

    def fail(_python: Path, _scope, _source: str) -> None:
        raise RuntimeInstallError(
            "https://user:secret@example.invalid/private C:/private/work "
            "token=secretvalue",
            reason_code="network_error",
            next_action="check_source_and_retry",
        )

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=fail,
    )
    first = manager.create("failed")
    other = manager.create("unaffected")
    plan = manager.preview_install(first["id"], "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(RuntimeInstallError):
        manager.install(plan["plan_id"], first["id"], "rapidocr-cpu", ("tuna-pypi",))
    registry_text = manager._registry.read_text(encoding="utf-8")
    assert "secretvalue" not in registry_text
    assert "example.invalid" not in registry_text
    assert "C:/private/work" not in registry_text
    records = {item["id"]: item for item in manager.list()["environments"]}
    failure = records[first["id"]]["last_install_failure"]
    assert failure["reason_code"] == "network_error"
    assert failure["next_action"] == "check_source_and_retry"
    assert failure["plan_id"] == plan["plan_id"]
    assert records[other["id"]]["last_install_failure"] is None

    def interrupted(_python: Path, _scope, _source: str) -> None:
        raise SystemExit(1)

    manager._install_runner = interrupted
    with pytest.raises(SystemExit):
        manager.install(plan["plan_id"], first["id"], "rapidocr-cpu", ("tuna-pypi",))
    recovered = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    ).list()["environments"]
    by_id = {item["id"]: item for item in recovered}
    assert by_id[first["id"]]["status"] == "empty"
    assert (
        by_id[first["id"]]["last_install_failure"]["reason_code"]
        == "install_interrupted"
    )
    # 中断投影也携带 plan_id：调用方据此把终态归属回本次安装计划。
    assert by_id[first["id"]]["last_install_failure"]["plan_id"] == plan["plan_id"]
    assert by_id[other["id"]]["last_install_failure"] is None


def test_new_environment_preview_supersedes_old_plan(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")
    installed_sources: list[str] = []
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=lambda _python, _scope, endpoint: installed_sources.append(
            endpoint
        ),
    )
    current = manager.create("current")
    target = manager.create("target")
    manager.commit_switch(manager.prepare_switch(current["id"]))
    old = manager.preview_install(target["id"], "rapidocr-cpu", ("tuna-pypi",))
    fresh = manager.preview_install(target["id"], "rapidocr-cpu", ("pypi",))

    with pytest.raises(
        runtime_maintenance.RuntimeInstallPlanStale, match="preview again"
    ):
        manager.install(old["plan_id"], target["id"], "rapidocr-cpu", ("tuna-pypi",))
    assert manager.list()["active_id"] == current["id"]
    assert manager.list()["environments"][1]["status"] == "empty"

    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: {
            "healthy": True,
            "python": str(manager._venv_python(manager._safe_path(record))),
            "reason": None,
        },
    )
    installed = manager.install(
        fresh["plan_id"], target["id"], "rapidocr-cpu", ("pypi",)
    )
    assert installed["revision"] == 2
    assert installed["status"] == "installed"
    assert installed["last_install_failure"] is None
    assert installed_sources == ["https://pypi.org/simple"]
    assert manager.list()["active_id"] == current["id"]


def test_named_recipe_addition_preserves_compatible_engine(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("engines")
    registry = manager.paths.state_root / "environments.json"
    value = json.loads(registry.read_text(encoding="utf-8"))
    value["environments"][item["id"]].update(
        {"status": "installed", "recipe": "rapidocr-cpu"}
    )
    registry.write_text(json.dumps(value), encoding="utf-8")
    plan = manager.preview_install(item["id"], "mineru-cpu")
    assert plan["requested_recipe"] == "mineru-cpu"
    assert plan["recipe"] == "rapidocr+mineru-cpu"
    explicit = manager.preview_install(item["id"], "rapidocr+mineru-cpu")
    assert explicit["requested_recipe"] == "rapidocr+mineru-cpu"
    assert explicit["recipe"] == "rapidocr+mineru-cpu"
    with pytest.raises(ManagedEnvironmentError, match="compatible locked recipe"):
        manager.preview_install(item["id"], "paddleocr-cpu")


def test_named_preview_includes_direct_url_dependencies(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("cuda")
    dependencies = manager.preview_install(item["id"], "rapidocr+mineru-cuda")[
        "dependencies"
    ]
    assert "paddlepaddle-gpu @ https://example.invalid/cu126/paddle.whl" in dependencies
    assert "torch @ https://example.invalid/cu126/torch.whl" in dependencies


def _all_recipes_release(root: Path) -> tuple[Path, Path]:
    """默认 release + mineru-standalone scope + Paddle 隔离锁：六配方全部可解析。"""
    manifest, component = _release(root)
    lock = manifest.parent / "mineru-standalone.lock"
    lock.write_text(
        "fastapi==1.0.0 \\\n    --hash=sha256:" + "1" * 64 + "\n"
        "mineru==4.0.2 \\\n    --hash=sha256:" + "2" * 64 + "\n",
        encoding="utf-8",
    )
    value = json.loads(manifest.read_text(encoding="utf-8"))
    value["profiles"]["win-x64-cpu"]["install_scopes"].append(
        {
            "scope_id": "mineru-standalone",
            "component_ids": ["runtime_host", "mineru-cpu"],
            "lock": lock.name,
            "sha256": _sha(lock.read_bytes()),
            "runtime_pack": None,
        }
    )
    paddle_cpu = manifest.parent / "requirements-win-x64-paddle-cpu.lock"
    paddle_cpu.write_text(
        "paddlepaddle==3.3.1 \\\n    --hash=sha256:" + "7" * 64 + "\n",
        encoding="utf-8",
    )
    paddle_cu126 = manifest.parent / "requirements-win-x64-paddle-cu126.lock"
    paddle_cu126.write_text(
        "paddlepaddle-gpu @ https://example.invalid/cu126/paddle.whl \\\n"
        "    --hash=sha256:" + "8" * 64 + "\n",
        encoding="utf-8",
    )
    value["profiles"]["win-x64-cpu"]["paddle_environment"] = {
        "lock": paddle_cpu.name,
        "sha256": _sha(paddle_cpu.read_bytes()),
    }
    value["profiles"]["win-x64-cu126"]["paddle_environment"] = {
        "lock": paddle_cu126.name,
        "sha256": _sha(paddle_cu126.read_bytes()),
    }
    manifest.write_text(json.dumps(value, sort_keys=True) + "\n", encoding="utf-8")
    binding = json.loads(component.read_text(encoding="utf-8"))
    binding["product"]["runtime_manifest_sha256"] = _sha(manifest.read_bytes())
    component.write_text(json.dumps(binding, sort_keys=True) + "\n", encoding="utf-8")
    return manifest, component


def _write_record(manager: ManagedEnvironmentStore, env_id: str, **overrides) -> dict:
    """直接写入注册表记录：不建真实 venv，兼容查询只关心身份与证据。"""
    from vibeocr.runtime.environments.runtime_maintenance import _atomic_json

    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        record = {
            "id": env_id,
            "name": f"env-{env_id[:4]}",
            "revision": 1,
            "kind": "venv",
            "path": str(Path(env_id) / "revisions" / "1"),
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
            **overrides,
        }
        data["environments"][env_id] = record
        _atomic_json(manager._registry, data)
        return record


def _pinned_versions(manager: ManagedEnvironmentStore, recipe: str) -> dict[str, str]:
    scope, _accelerator = manager._recipe(recipe)
    return {
        name: version
        for name, _display, version in ManagedEnvironmentStore._scope_pins(scope)
        if version is not None
    }


def test_recipe_catalog_projects_manifest_bound_scopes(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    # 未绑定 standalone scope/Paddle 隔离锁的 manifest 不提供对应配方（与 preview 同源）。
    assert [entry["id"] for entry in manager.recipe_catalog()] == [
        "rapidocr-cpu",
        "rapidocr+mineru-cpu",
        "rapidocr+mineru-cuda",
    ]

    standalone_manifest, standalone_component = _all_recipes_release(
        tmp_path / "standalone"
    )
    full = ManagedEnvironmentStore(
        product_root=tmp_path / "standalone-product",
        component_lock=standalone_component,
        runtime_manifest=standalone_manifest,
        base_python=sys._base_executable,
    )
    catalog = full.recipe_catalog()
    assert full.list()["recipes"] == catalog
    assert [entry["id"] for entry in catalog] == [
        "rapidocr-cpu",
        "paddleocr-cpu",
        "paddleocr-cuda",
        "mineru-cpu",
        "rapidocr+mineru-cpu",
        "rapidocr+mineru-cuda",
    ]
    entry = {item["id"]: item for item in catalog}
    assert entry["rapidocr-cpu"]["display_name"] == "RapidOCR · CPU"
    assert entry["rapidocr-cpu"]["configured_recognition_types"] == ["text"]
    assert entry["rapidocr-cpu"]["accelerator"] == "cpu"
    assert entry["rapidocr-cpu"]["target_device"] == "cpu"
    assert entry["rapidocr-cpu"]["python_version"] == "3.13.16"
    assert entry["rapidocr-cpu"]["abi"] == "cp313"
    assert entry["rapidocr-cpu"]["platform"] == "win_amd64"
    assert entry["paddleocr-cuda"]["display_name"] == "PaddleOCR · NVIDIA CUDA"
    assert entry["paddleocr-cuda"]["configured_recognition_types"] == [
        "text",
        "table",
        "formula",
        "structure",
        "document_vl",
    ]
    assert entry["paddleocr-cuda"]["accelerator"] == "nvidia_cuda"
    assert entry["paddleocr-cuda"]["target_device"] == "cuda"
    assert entry["mineru-cpu"]["configured_recognition_types"] == ["document"]
    assert entry["mineru-cpu"]["scope_id"] == "mineru-standalone"
    assert "paddlepaddle==3.3.1" in entry["rapidocr+mineru-cpu"]["dependencies"]
    assert (
        "paddlepaddle-gpu @ https://example.invalid/cu126/paddle.whl"
        in entry["rapidocr+mineru-cuda"]["dependencies"]
    )

    # 目录与 preview 同源：锁、pin 与来源分类完全一致，前台不得另算。
    target = _write_record(full, "a" * 32)
    for recipe in ("rapidocr-cpu", "rapidocr+mineru-cuda", "mineru-cpu"):
        plan = full.preview_install(target["id"], recipe)
        assert entry[recipe]["recipe_lock"] == plan["recipe_lock"]
        assert entry[recipe]["dependencies"] == plan["dependencies"]
        assert entry[recipe]["dependency_origin"] == plan["dependency_origin"]
        assert entry[recipe]["python_origin"] == plan["python_origin"]


@pytest.mark.parametrize(
    "stdout,returncode,raises,status,reason,version",
    [
        ("552.44\n", 0, None, "ok", None, "552.44"),
        ("610.88\n545.92\n", 0, None, "ok", None, "610.88"),
        ("527.00\n", 0, None, "unsupported", "nvidia_driver_incompatible", "527.00"),
        ("no devices\n", 1, None, "unsupported", "nvidia_driver_unavailable", None),
        ("garbage\n", 0, None, "unsupported", "nvidia_driver_unavailable", None),
        (
            "",
            0,
            FileNotFoundError("nvidia-smi"),
            "unsupported",
            "nvidia_driver_unavailable",
            None,
        ),
        (
            "",
            0,
            subprocess.TimeoutExpired(cmd="nvidia-smi", timeout=10),
            "unknown",
            None,
            None,
        ),
    ],
)
def test_probe_nvidia_driver_reports_honest_hardware_truth(
    monkeypatch: pytest.MonkeyPatch,
    stdout: str,
    returncode: int,
    raises: Exception | None,
    status: str,
    reason: str | None,
    version: str | None,
) -> None:
    from types import SimpleNamespace

    def fake_run(*_args: object, **_kwargs: object) -> object:
        if raises is not None:
            raise raises
        return SimpleNamespace(returncode=returncode, stdout=stdout)

    monkeypatch.setattr(
        "vibeocr.runtime.environments.runtime_installer.subprocess.run", fake_run
    )
    assert probe_nvidia_driver() == {
        "status": status,
        "reason_code": reason,
        "driver_version": version,
    }


def test_managed_environment_list_projects_runtime_hardware_truth(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )

    def missing_driver(*_args: object, **_kwargs: object) -> object:
        raise FileNotFoundError("nvidia-smi")

    monkeypatch.setattr(
        "vibeocr.runtime.environments.runtime_installer.subprocess.run",
        missing_driver,
    )
    assert manager.list()["hardware"] == {
        "nvidia_driver": {
            "status": "unsupported",
            "reason_code": "nvidia_driver_unavailable",
            "driver_version": None,
        }
    }


def test_find_compatible_prefers_active_then_stable_id_and_reports_reasons(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    scope, _accelerator = manager._recipe("rapidocr-cpu")
    pinned = _pinned_versions(manager, "rapidocr-cpu")

    def healthy_probe(record: dict) -> dict:
        return {
            "healthy": True,
            "reason": None,
            "python": str(manager._venv_python(manager._safe_path(record))),
            "versions": dict(pinned),
            "platform": "win_amd64",
        }

    monkeypatch.setattr(manager, "_probe", healthy_probe)
    a = _write_record(
        manager,
        "a" * 32,
        status="installed",
        recipe="rapidocr-cpu",
        recipe_lock=scope.sha256,
    )
    b = _write_record(
        manager,
        "b" * 32,
        status="installed",
        recipe="rapidocr-cpu",
        recipe_lock=scope.sha256,
    )
    empty = _write_record(manager, "c" * 32)
    legacy_no_evidence = _write_record(
        manager, "d" * 32, status="installed", recipe="rapidocr-cpu"
    )
    _write_record(
        manager,
        "e" * 32,
        status="installed",
        recipe="paddleocr-cpu",
        recipe_lock="f" * 64,
    )
    from vibeocr.runtime.environments.runtime_maintenance import _atomic_json

    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["environments"]["legacy"] = {
            "id": "legacy",
            "name": "原有环境",
            "revision": 1,
            "kind": "legacy",
            "path": "runtime",
            "status": "installed",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
        }
        _atomic_json(manager._registry, data)

    before = manager._registry.read_bytes()
    result = manager.find_compatible("rapidocr-cpu")
    assert manager._registry.read_bytes() == before
    assert result["recipe"] == "rapidocr-cpu"
    assert result["recipe_lock"] == scope.sha256
    assert result["selected"] == {
        "environment_id": a["id"],
        "environment_revision": 1,
        "recipe": "rapidocr-cpu",
        "selection_reason": "deterministic_id_order",
    }
    assert [item["environment_id"] for item in result["environments"]] == [
        a["id"],
        b["id"],
        empty["id"],
        legacy_no_evidence["id"],
        "e" * 32,
        "legacy",
    ]
    assert {
        item["environment_id"]: item["reason_code"] for item in result["environments"]
    } == {
        a["id"]: "selected_compatible_environment",
        b["id"]: "compatible_lower_priority",
        empty["id"]: "environment_empty",
        legacy_no_evidence["id"]: "lock_evidence_missing",
        "e" * 32: "recipe_mismatch",
        "legacy": "legacy_environment",
    }

    # 活动环境优先；同配方低优先级环境如实标记。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["active_id"] = b["id"]
        data["active_revision"] += 1
        _atomic_json(manager._registry, data)
    result = manager.find_compatible("rapidocr-cpu")
    assert result["selected"]["environment_id"] == b["id"]
    assert result["selected"]["selection_reason"] == "active_environment"
    by_id = {item["environment_id"]: item for item in result["environments"]}
    assert by_id[b["id"]]["reason_code"] == "selected_active_environment"
    assert by_id[b["id"]]["active"] is True
    assert by_id[a["id"]]["reason_code"] == "compatible_lower_priority"
    assert by_id[a["id"]]["active"] is False

    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: {**healthy_probe(record), "platform": "win_arm64"},
    )
    wrong_platform = manager.find_compatible("rapidocr-cpu")
    assert wrong_platform["selected"] is None
    assert {
        item["reason_code"]
        for item in wrong_platform["environments"]
        if item["environment_id"] in {a["id"], b["id"]}
    } == {"platform_mismatch"}

    # 安装后版本漂移：探针健康但 pin 不匹配，不得选中。
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: {
            **healthy_probe(record),
            "versions": {**pinned, "fastapi": "2.0.0"},
        },
    )
    drifted = manager.find_compatible("rapidocr-cpu")
    assert drifted["selected"] is None
    assert {
        item["reason_code"]
        for item in drifted["environments"]
        if item["environment_id"] in {a["id"], b["id"]}
    } == {"locked_version_mismatch"}

    # 探针不健康：原因原样透传，不选中。
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: {
            "healthy": False,
            "reason": "engine_import_failed",
            "python": str(manager._venv_python(manager._safe_path(record))),
        },
    )
    broken = manager.find_compatible("rapidocr-cpu")
    assert broken["selected"] is None
    assert {
        item["reason_code"]
        for item in broken["environments"]
        if item["environment_id"] in {a["id"], b["id"]}
    } == {"engine_import_failed"}

    # 无锁证据的旧环境：query 不验证，但 list 与手工切换保持可用。
    monkeypatch.setattr(manager, "_probe", healthy_probe)
    prepared = manager.prepare_switch(legacy_no_evidence["id"])
    assert prepared["environment_id"] == legacy_no_evidence["id"]
    assert (
        next(
            item
            for item in manager.list()["environments"]
            if item["id"] == legacy_no_evidence["id"]
        )["status"]
        == "installed"
    )

    # 未知配方拒绝。
    with pytest.raises(ManagedEnvironmentError, match="unknown engine recipe"):
        manager.find_compatible("not-a-recipe")


@pytest.mark.parametrize(
    ("url", "expected_version", "installed_version", "reason"),
    [
        (
            "https://example.invalid/Paddle_GPU-3.3.1-cp313-cp313-win_amd64.whl",
            "3.3.1",
            "3.3.1",
            "selected_compatible_environment",
        ),
        (
            "https://example.invalid/Paddle_GPU-3.3.1-cp313-cp313-win_amd64.whl",
            "3.3.1",
            "3.3.0",
            "locked_version_mismatch",
        ),
        ("https://example.invalid/paddle.whl", None, "3.3.1", "locked_version_unknown"),
        (
            "https://example.invalid/other-3.3.1-cp313-cp313-win_amd64.whl",
            None,
            "3.3.1",
            "locked_version_unknown",
        ),
    ],
)
def test_compatible_query_checks_wheel_version_and_normalized_distribution_name(
    tmp_path, monkeypatch, url, expected_version, installed_version, reason
):
    from dataclasses import replace

    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    scope, accelerator = manager._recipe("rapidocr-cpu")
    lock = tmp_path / "query.lock"
    lock.write_text(f"Paddle.GPU @ {url}\n", encoding="utf-8")
    scope = replace(scope, lock_path=lock, sha256=_sha(lock.read_bytes()))
    monkeypatch.setattr(manager, "_recipe", lambda _recipe: (scope, accelerator))
    _write_record(
        manager,
        "a" * 32,
        status="installed",
        recipe="rapidocr-cpu",
        recipe_lock=scope.sha256,
    )
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: {
            "healthy": True,
            "reason": None,
            "platform": "win_amd64",
            "versions": {"paddle-gpu": installed_version},
        },
    )
    assert manager._scope_pins(scope) == [
        ("paddle-gpu", f"Paddle.GPU @ {url}", expected_version)
    ]
    result = manager.find_compatible("rapidocr-cpu")
    assert result["environments"][0]["reason_code"] == reason
    assert (result["selected"] is not None) == (
        reason == "selected_compatible_environment"
    )


def test_install_freezes_recipe_lock_and_query_round_trips_cli(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=lambda _python, _scope, _endpoint: None,
    )
    pinned = _pinned_versions(manager, "rapidocr-cpu")
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda record: (
            {
                "healthy": True,
                "reason": None,
                "python": str(manager._venv_python(manager._safe_path(record))),
                "versions": dict(pinned),
                "platform": "win_amd64",
            }
            if record["status"] == "installed"
            else ManagedEnvironmentStore._probe(manager, record)
        ),
    )
    item = manager.create("reusable")
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("tuna-pypi",))
    installed = manager.install(
        plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",)
    )
    assert installed["status"] == "installed"
    record = manager._read()["environments"][item["id"]]
    assert record["recipe_lock"] == plan["recipe_lock"]

    # 重启后按锁证据命中同一环境。
    reopened = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    monkeypatch.setattr(
        reopened,
        "_probe",
        lambda record: {
            "healthy": True,
            "reason": None,
            "python": str(reopened._venv_python(reopened._safe_path(record))),
            "versions": dict(pinned),
            "platform": "win_amd64",
        },
    )
    assert (
        reopened.find_compatible("rapidocr-cpu")["selected"]["environment_id"]
        == (item["id"])
    )

    base = {
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
    }
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "find_compatible",
        "recipe": "rapidocr-cpu",
        **base,
    }
    # CLI 在新 store 实例上运行真实探针；本测试的合成 venv 无包，
    # 用类级 stub 保持与上方同一健康投影。
    monkeypatch.setattr(
        ManagedEnvironmentStore,
        "_probe",
        lambda self, record: {
            "healthy": True,
            "reason": None,
            "python": str(self._venv_python(self._safe_path(record))),
            "versions": dict(pinned),
            "platform": "win_amd64",
        },
    )
    assert main(["--request-json", json.dumps(request)]) == 0
    envelope = json.loads(capsys.readouterr().out)
    assert envelope["response_kind"] == "environment"
    assert envelope["action"] == "find_compatible"
    assert envelope["result"]["selected"]["environment_id"] == item["id"]
    assert [recipe["id"] for recipe in envelope["result"]["recipes"]][0] == (
        "rapidocr-cpu"
    )

    unknown = {**request, "recipe": "not-a-recipe"}
    assert main(["--request-json", json.dumps(unknown)]) == 1
    failure = json.loads(capsys.readouterr().out)
    assert failure["ok"] is False
    assert "unknown engine recipe" in failure["error"]["message"]


def test_named_install_rejects_native_import_failure_without_committing(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")

    def install(python: Path, _scope, _source: str) -> None:
        site = python.parent.parent / "Lib" / "site-packages"
        for name in ("fastapi", "rapidocr", "onnxruntime"):
            metadata = site / f"{name}-1.0.dist-info"
            metadata.mkdir(parents=True)
            (metadata / "METADATA").write_text(
                f"Name: {name}\nVersion: 1.0\n", encoding="utf-8"
            )
        (site / "onnxruntime").mkdir()
        (site / "onnxruntime" / "__init__.py").write_text("", encoding="utf-8")
        (site / "pyclipper").mkdir()
        (site / "pyclipper" / "__init__.py").write_text(
            "raise ImportError('native extension cannot load')\n", encoding="utf-8"
        )

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=install,
    )
    item = manager.create("native")
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(ManagedEnvironmentError, match="engine_import_failed"):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",))
    surviving = manager.list()["environments"][0]
    assert surviving["status"] == "empty"
    assert surviving["revision"] == 1
    assert surviving["last_install_failure"]["reason_code"] == "engine_import_failed"
    candidate = next((manager.root / item["id"] / "revisions").glob("2-*"))
    assert len(candidate.name.split("-", 1)[1]) == 16
    registry = json.loads(manager._registry.read_text(encoding="utf-8"))
    registry["environments"][item["id"]].pop("last_install_operation")
    registry["environments"][item["id"]].update(
        {
            "revision": 2,
            "path": f"{item['id']}/revisions/{candidate.name}",
            "status": "installed",
            "recipe": "rapidocr-cpu",
            "source_ids": ["tuna-pypi"],
        }
    )
    manager._registry.write_text(json.dumps(registry), encoding="utf-8")
    unavailable = manager.list()["environments"][0]
    assert unavailable["python_state"] == "ready"
    assert unavailable["dependency_state"] == "unavailable"
    assert unavailable["engine_state"] == "unavailable"
    assert "engine_import_failed" in unavailable["reason"]
    assert "shorter Portable location" in unavailable["reason"]


def test_named_install_rapidocr_probe_imports_real_transitive_closure(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # 回归 #104：rapidocr 的 __init__ 惰性解析 RapidOCR，旧探针只
    # import pyclipper/onnxruntime（甚至裸 import rapidocr）都能通过，
    # 而 Supervisor 实际执行的 from rapidocr import RapidOCR 会在
    # ch_ppocr_det → shapely.lib 的原生传递依赖上失败（长路径 DLL load
    # failed）。探针必须运行同一导入闭包，在 install 提交与
    # prepare_switch 之前 fail closed，且不实例化引擎、不下载模型。
    _omit_unused_pip_bootstrap(monkeypatch)
    manifest, component = _release(tmp_path / "release")

    def install(python: Path, _scope, _source: str) -> None:
        site = python.parent.parent / "Lib" / "site-packages"
        for name in ("fastapi", "rapidocr", "onnxruntime", "shapely"):
            metadata = site / f"{name}-1.0.dist-info"
            metadata.mkdir(parents=True)
            (metadata / "METADATA").write_text(
                f"Name: {name}\nVersion: 1.0\n", encoding="utf-8"
            )
        # 旧探针的叶子依赖必须健康：契约只在 rapidocr 真实导入闭包上失败。
        for name in ("pyclipper", "onnxruntime"):
            (site / name).mkdir()
            (site / name / "__init__.py").write_text("", encoding="utf-8")
        (site / "rapidocr").mkdir()
        (site / "rapidocr" / "__init__.py").write_text(
            "from importlib import import_module\n"
            "_LAZY_IMPORTS = {'RapidOCR': 'rapidocr.main'}\n"
            "def __getattr__(name):\n"
            "    if name in _LAZY_IMPORTS:\n"
            "        return getattr(import_module(_LAZY_IMPORTS[name]), name)\n"
            "    raise AttributeError(name)\n",
            encoding="utf-8",
        )
        (site / "rapidocr" / "main.py").write_text(
            "from rapidocr.ch_ppocr_det import TextDetector\n"
            "class RapidOCR:\n"
            "    def __init__(self, *args, **kwargs):\n"
            "        raise AssertionError('probe must not instantiate the engine')\n",
            encoding="utf-8",
        )
        det = site / "rapidocr" / "ch_ppocr_det"
        det.mkdir()
        (det / "__init__.py").write_text(
            "from .main import TextDetector\n", encoding="utf-8"
        )
        (det / "main.py").write_text(
            "from .utils import DBPostProcess\nclass TextDetector:\n    pass\n",
            encoding="utf-8",
        )
        (det / "utils.py").write_text(
            "from shapely.geometry import Polygon\nclass DBPostProcess:\n    pass\n",
            encoding="utf-8",
        )
        (site / "shapely").mkdir()
        (site / "shapely" / "__init__.py").write_text(
            "from shapely.lib import GEOSException\n", encoding="utf-8"
        )
        (site / "shapely" / "lib.py").write_text(
            "raise ImportError('DLL load failed while importing lib')\n",
            encoding="utf-8",
        )
        (site / "shapely" / "geometry.py").write_text("", encoding="utf-8")

    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
        install_runner=install,
    )
    item = manager.create("closure")
    manager.commit_switch(manager.prepare_switch(item["id"]))
    plan = manager.preview_install(item["id"], "rapidocr-cpu", ("tuna-pypi",))
    with pytest.raises(ManagedEnvironmentError, match="engine_import_failed"):
        manager.install(plan["plan_id"], item["id"], "rapidocr-cpu", ("tuna-pypi",))
    surviving = manager.list()["environments"][0]
    assert surviving["status"] == "empty"
    assert surviving["revision"] == 1
    assert surviving["python_state"] == "ready"
    assert manager.list()["active_id"] == item["id"]
    failure = surviving["last_install_failure"]
    assert failure["reason_code"] == "engine_import_failed"
    assert failure["phase"] == "failed"
    assert failure["next_action"] == "repair_environment"
    assert failure["environment_revision"] == 1

    # 若探针未覆盖真实闭包（旧实现），候选修订会被提交为已安装环境；
    # 模拟该状态时，list/prepare_switch 也必须按同一探针 fail closed。
    candidate = next((manager.root / item["id"] / "revisions").glob("2-*"))
    registry = json.loads(manager._registry.read_text(encoding="utf-8"))
    registry["environments"][item["id"]].pop("last_install_operation")
    registry["environments"][item["id"]].update(
        {
            "revision": 2,
            "path": f"{item['id']}/revisions/{candidate.name}",
            "status": "installed",
            "recipe": "rapidocr-cpu",
            "source_ids": ["tuna-pypi"],
        }
    )
    manager._registry.write_text(json.dumps(registry), encoding="utf-8")
    unavailable = manager.list()["environments"][0]
    assert unavailable["python_state"] == "ready"
    assert unavailable["dependency_state"] == "unavailable"
    assert "engine_import_failed" in unavailable["reason"]
    with pytest.raises(ManagedEnvironmentError, match="engine_import_failed"):
        manager.prepare_switch(item["id"])


def test_mineru_probe_imports_server_binding_without_starting_or_downloading(
    tmp_path, monkeypatch
):
    manifest, component = _all_recipes_release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("mineru")
    record = manager._read()["environments"][item["id"]]
    record.update(status="installed", recipe="mineru-cpu")
    site = (
        manager._venv_python(manager._safe_path(record)).parent.parent
        / "Lib/site-packages"
    )
    for name in ("fastapi", "mineru"):
        metadata = site / f"{name}-1.0.dist-info"
        metadata.mkdir(parents=True)
        (metadata / "METADATA").write_text(f"Name: {name}\nVersion: 1.0\n")
    parser = site / "mineru/parser"
    parser.mkdir(parents=True)
    (parser.parent / "__init__.py").write_text("")
    (parser / "__init__.py").write_text("")
    server = parser / "api_server.py"
    private_home = manager.paths.state_root / "environments" / item["id"] / "mineru4"
    monkeypatch.setenv("MINERU_HOME", str(tmp_path / "unrelated"))
    server.write_text(
        "import os\n"
        f"assert os.environ['MINERU_HOME'] == {str(private_home)!r}\n"
        "def create_app(*args, **kwargs):\n"
        "    raise AssertionError('probe must not initialize models or a server')\n"
        "if __name__ == '__main__':\n"
        "    raise AssertionError('probe must not run the server')\n"
    )
    assert manager._probe(record)["healthy"] is True
    # 包级导入仍成功，但真实 server 闭包损坏必须报告失败。
    server.write_text("raise ImportError('server dependency missing')\n")
    assert manager._probe(record)["reason"] == "engine_import_failed"
    assert not private_home.exists()


def test_named_revision_path_accepts_old_and_new_random_tails(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("portable")
    for suffix in ("a" * 16, "a" * 32):
        record = {
            **item,
            "revision": 2,
            "path": f"{item['id']}/revisions/2-{suffix}",
        }
        assert manager._safe_path(record) == manager.root / record["path"]


def test_standalone_mineru_recipe_excludes_rapidocr(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    lock = manifest.parent / "mineru-standalone.lock"
    lock.write_text(
        "fastapi==1.0.0 \\\n    --hash=sha256:" + "1" * 64 + "\n"
        "mineru==4.0.2 \\\n    --hash=sha256:" + "2" * 64 + "\n",
        encoding="utf-8",
    )
    value = json.loads(manifest.read_text(encoding="utf-8"))
    value["profiles"]["win-x64-cpu"]["install_scopes"].append(
        {
            "scope_id": "mineru-standalone",
            "component_ids": ["runtime_host", "mineru-cpu"],
            "lock": lock.name,
            "sha256": _sha(lock.read_bytes()),
            "runtime_pack": None,
        }
    )
    manifest.write_text(json.dumps(value, sort_keys=True) + "\n", encoding="utf-8")
    binding = json.loads(component.read_text(encoding="utf-8"))
    binding["product"]["runtime_manifest_sha256"] = _sha(manifest.read_bytes())
    component.write_text(json.dumps(binding), encoding="utf-8")
    assert (
        load_runtime_manifest(manifest).profiles["win-x64-cpu"].scopes[-1].scope_id
        == "mineru-standalone"
    )
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
    )
    item = manager.create("mineru")
    assert manager.preview_install(item["id"], "mineru-cpu")["recipe"] == "mineru-cpu"
    generated = build_runtime_manifest(
        runtime_wheel=manifest.parent / value["runtime_wheel"],
        base_lock=manifest.parent / value["profiles"]["win-x64-base"]["lock"],
        cpu_lock=manifest.parent / value["profiles"]["win-x64-cpu"]["lock"],
        cu126_lock=manifest.parent / value["profiles"]["win-x64-cu126"]["lock"],
        cu126_gpu_lock=manifest.parent / "requirements-win-x64-cu126-gpu.lock",
        python_archive=manifest.parent / value["python"]["archive"],
        python_version=value["python"]["version"],
        python_source_url=value["python"]["source_url"],
        installer_archive=manifest.parent / value["installer"]["archive"],
        version="0.7.0",
        source_commit="0" * 40,
        build_workflow="tests/runtime",
        output_dir=tmp_path / "output",
        capabilities=("ocr.recognition.v2",),
        mineru_cpu_lock=lock,
    )
    assert (generated.parent / "runtime-code/vibeocr/runtime/host/main.py").is_file()
    assert any(
        scope.scope_id == "mineru-standalone"
        for scope in load_runtime_manifest(generated).profiles["win-x64-cpu"].scopes
    )


def test_frozen_manager_exposes_named_environment_list(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")

    def nvidia_ok(*_args: object, **_kwargs: object) -> object:
        from types import SimpleNamespace

        return SimpleNamespace(returncode=0, stdout="552.44\n")

    monkeypatch.setattr(
        "vibeocr.runtime.environments.runtime_installer.subprocess.run", nvidia_ok
    )
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "list",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
    }
    assert main(["--request-json", json.dumps(request)]) == 0
    response = json.loads(capsys.readouterr().out)
    assert response["response_kind"] == "environment"
    assert [entry["id"] for entry in response["result"].pop("recipes")] == [
        "rapidocr-cpu",
        "rapidocr+mineru-cpu",
        "rapidocr+mineru-cuda",
    ]
    # 硬件投影与安装预检同一真相源：只读探测，不安装、不缓存。
    assert response["result"].pop("hardware") == {
        "nvidia_driver": {
            "status": "ok",
            "reason_code": None,
            "driver_version": "552.44",
        }
    }
    assert response["result"] == {
        "capabilities": [
            "environment.install_progress.v1",
            "environment.cleanup.v1",
            "environment.switch-reservation.v1",
        ],
        "active_id": None,
        "active_revision": 0,
        "sources": [
            {
                "id": source_id,
                "kind": kind,
                "display_name": display_name,
                "endpoint": endpoint,
                "is_default": is_default,
            }
            for source_id, kind, display_name, endpoint, is_default in (
                (
                    "tuna-pypi",
                    "package_index",
                    "TUNA PyPI 镜像",
                    "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
                    True,
                ),
                (
                    "pypi",
                    "package_index",
                    "PyPI 官方源",
                    "https://pypi.org/simple",
                    False,
                ),
                (
                    "huggingface",
                    "model_registry",
                    "Hugging Face",
                    "https://huggingface.co",
                    False,
                ),
                (
                    "modelscope",
                    "model_registry",
                    "ModelScope",
                    "https://www.modelscope.cn",
                    False,
                ),
                (
                    "paddleocr-huggingface",
                    "paddleocr_model_registry",
                    "Hugging Face",
                    "https://huggingface.co",
                    False,
                ),
                (
                    "paddleocr-modelscope",
                    "paddleocr_model_registry",
                    "ModelScope",
                    "https://www.modelscope.cn",
                    True,
                ),
                (
                    "paddleocr-bos",
                    "paddleocr_model_registry",
                    "百度 BOS",
                    "https://paddle-model-ecology.bj.bcebos.com",
                    False,
                ),
                (
                    "mineru-huggingface",
                    "mineru_model_registry",
                    "Hugging Face",
                    "https://huggingface.co",
                    False,
                ),
                (
                    "mineru-modelscope",
                    "mineru_model_registry",
                    "ModelScope",
                    "https://www.modelscope.cn",
                    True,
                ),
            )
        ],
        "resolved_default_sources": [
            {
                "kind": "package_index",
                "id": "tuna-pypi",
                "display_name": "TUNA PyPI 镜像",
                "origin": "product_default",
            },
            {
                "kind": "paddleocr_model_registry",
                "id": "paddleocr-modelscope",
                "display_name": "ModelScope",
                "origin": "product_default",
            },
            {
                "kind": "mineru_model_registry",
                "id": "mineru-modelscope",
                "display_name": "ModelScope",
                "origin": "product_default",
            },
        ],
        "default_source_ids": [],
        "unknown_default_source_ids": [],
        "source_config_revision": 0,
        "package_source_ids": ["tuna-pypi", "pypi"],
        "environments": [],
    }


def test_named_environment_error_envelope_redacts_private_details(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")

    def fail(_manager: ManagedEnvironmentStore) -> dict:
        raise RuntimeInstallError(
            "https://user:secret@example.invalid/private C:/private/work "
            "token=secretvalue"
        )

    monkeypatch.setattr(ManagedEnvironmentStore, "list", fail)
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "list",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
    }
    assert main(["--request-json", json.dumps(request)]) == 1
    output = capsys.readouterr().out
    assert "secretvalue" not in output
    assert "example.invalid" not in output
    assert "C:/private/work" not in output
    assert "[url]" in output


def _tar_with(path: Path, member_name: str, content: bytes = b"python") -> None:
    with tarfile.open(path, mode="w:gz") as archive:
        info = tarfile.TarInfo(member_name)
        info.size = len(content)
        archive.addfile(info, io.BytesIO(content))


def test_python_archive_extracts_only_stripped_python_root(tmp_path: Path) -> None:
    archive = tmp_path / "python.tar.gz"
    _tar_with(archive, "python/python.exe")
    destination = tmp_path / "runtime"
    destination.mkdir()
    _extract_python_archive(archive, destination)
    assert (destination / "python.exe").read_bytes() == b"python"


def test_python_archive_reports_actual_uncompressed_bytes(tmp_path: Path) -> None:
    archive = tmp_path / "python.tar.gz"
    _tar_with(archive, "python/python.exe", b"python")
    destination = tmp_path / "runtime"
    destination.mkdir()
    samples: list[tuple[int, int]] = []

    _extract_python_archive(
        archive,
        destination,
        progress=lambda current, total: samples.append((current, total)),
    )

    assert samples == [(6, 6)]


def test_python_archive_coalesces_dense_progress_without_losing_completion(
    tmp_path: Path,
) -> None:
    archive = tmp_path / "python.tar.gz"
    with tarfile.open(archive, mode="w:gz") as stream:
        for index in range(103):
            info = tarfile.TarInfo(f"python/Lib/module-{index}.py")
            info.size = 1
            stream.addfile(info, io.BytesIO(b"x"))
    destination = tmp_path / "runtime"
    destination.mkdir()
    samples: list[tuple[int, int]] = []

    _extract_python_archive(
        archive,
        destination,
        progress=lambda current, total: samples.append((current, total)),
    )

    assert samples[-1] == (103, 103)
    assert len(samples) <= 102


def test_python_archive_does_not_repeat_completion_for_empty_tail_files(
    tmp_path: Path,
) -> None:
    archive = tmp_path / "python.tar.gz"
    with tarfile.open(archive, mode="w:gz") as stream:
        content = tarfile.TarInfo("python/python.exe")
        content.size = 1
        stream.addfile(content, io.BytesIO(b"x"))
        for index in range(3):
            empty = tarfile.TarInfo(f"python/empty-{index}.txt")
            empty.size = 0
            stream.addfile(empty, io.BytesIO())
    destination = tmp_path / "runtime"
    destination.mkdir()
    samples: list[tuple[int, int]] = []

    _extract_python_archive(
        archive,
        destination,
        progress=lambda current, total: samples.append((current, total)),
    )

    assert samples == [(1, 1)]


def test_python_archive_rejects_traversal(tmp_path: Path) -> None:
    archive = tmp_path / "python.tar.gz"
    _tar_with(archive, "python/../escape.exe")
    destination = tmp_path / "runtime"
    destination.mkdir()
    with pytest.raises(RuntimeInstallError, match="unsafe"):
        _extract_python_archive(archive, destination)
    assert not (tmp_path / "escape.exe").exists()


def test_manifest_verifies_raw_hash_and_bound_artifacts(tmp_path: Path) -> None:
    manifest_path, _ = _release(tmp_path / "release")
    manifest = load_runtime_manifest(manifest_path)
    assert manifest.backend_version == "0.7.0"
    assert manifest.runtime_wheel == "vibeocr_next_runtime-0.7.0-py3-none-any.whl"
    assert manifest.sha256 == _sha(manifest_path.read_bytes())
    assert set(manifest.profiles) == {"win-x64-base", "win-x64-cpu", "win-x64-cu126"}


def test_manifest_rejects_tampered_lock(tmp_path: Path) -> None:
    manifest_path, _ = _release(tmp_path / "release")
    (manifest_path.parent / "requirements-win-x64-cpu.lock").write_text(
        "tampered",
        encoding="utf-8",
    )
    with pytest.raises(ManifestError, match="SHA-256 mismatch"):
        load_runtime_manifest(manifest_path)


@pytest.mark.parametrize(
    "directive",
    ["--index-url https://pypi.org/simple", "--extra-index-url https://extra.invalid"],
)
def test_runtime_lock_rejects_embedded_source_directives(
    tmp_path: Path,
    directive: str,
) -> None:
    lock = tmp_path / "requirements-win-x64-cpu.lock"
    lock.write_text(f"{directive}\n{_lock_text('win-x64-cpu')}", encoding="utf-8")

    with pytest.raises(ManifestError, match="source-neutral"):
        validate_requirements_lock(lock, profile="win-x64-cpu")


def test_manifest_rejects_tampered_runtime_wheel(tmp_path: Path) -> None:
    manifest_path, _ = _release(tmp_path / "release")
    (manifest_path.parent / "vibeocr_next_runtime-0.7.0-py3-none-any.whl").write_bytes(
        b"tampered"
    )
    with pytest.raises(ManifestError, match="Runtime wheel SHA-256 mismatch"):
        load_runtime_manifest(manifest_path)


def test_manifest_rejects_old_schema(tmp_path: Path) -> None:
    manifest_path, _ = _release(tmp_path / "release")
    payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    payload["schema_version"] = 1
    manifest_path.write_text(json.dumps(payload), encoding="utf-8")
    with pytest.raises(ManifestError, match="schema_version must be 2"):
        load_runtime_manifest(manifest_path)


def test_manifest_rejects_tampered_python_archive(tmp_path: Path) -> None:
    manifest_path, _ = _release(tmp_path / "release")
    (
        manifest_path.parent / "cpython-3.13.16-win_amd64-install_only.tar.gz"
    ).write_bytes(b"tampered")
    with pytest.raises(ManifestError, match="Python archive SHA-256 mismatch"):
        load_runtime_manifest(manifest_path)


def test_shared_layout_requires_explicit_valid_registration(tmp_path: Path) -> None:
    bundle = tmp_path / "bundle"
    product = bundle / "classic"
    product.mkdir(parents=True)
    marker = bundle / "portable-layout.json"
    marker.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "shared_root": "shared",
                "products": {
                    "classic": {
                        "root": "classic",
                        "component_lock": "component-lock.json",
                    }
                },
            }
        ),
        encoding="utf-8",
    )
    digest = "a" * 64
    local = resolve_runtime_store(
        product,
        manifest_sha256=digest,
    )
    assert local.store_root == product.resolve()
    shared = resolve_runtime_store(
        product,
        manifest_sha256=digest,
        layout_manifest=marker,
        product_id="classic",
    )
    assert shared.store_root == (bundle / "shared").resolve()
    assert shared.runtime_root == (bundle / "shared" / "runtime").resolve()


def test_shared_layout_rejects_traversal(tmp_path: Path) -> None:
    bundle = tmp_path / "bundle"
    product = bundle / "classic"
    product.mkdir(parents=True)
    marker = bundle / "portable-layout.json"
    marker.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "shared_root": "../escape",
                "products": {"classic": {"root": "classic"}},
            }
        ),
        encoding="utf-8",
    )
    with pytest.raises(LayoutError):
        resolve_runtime_store(
            product,
            manifest_sha256="a" * 64,
            layout_manifest=marker,
            product_id="classic",
        )


def test_ensure_is_atomic_and_idempotent(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    calls: list[Path] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        calls.append(partial)
        return _fake_install(partial, runtime_manifest, profile)

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=(),
    )
    first = installer.ensure()
    second = installer.ensure()
    assert first == second
    assert len(calls) == 1
    assert calls == [installer.paths.runtime_root.with_name("runtime.installing")]
    assert Path(first.python_executable).is_file()
    assert not installer.paths.runtime_root.with_name("runtime.installing").exists()
    assert installer.inspect().integrity == "verified"
    assert installer.inspect().accelerator == "cpu"
    portable_path_keys = {
        "VIBEOCR_PRODUCT_ROOT",
        "VIBEOCR_RUNTIME_ROOT",
        "PIP_CACHE_DIR",
        "UV_CACHE_DIR",
        "HF_HOME",
        "MODELSCOPE_CACHE",
        "PADDLE_PDX_CACHE_HOME",
        "TEMP",
        "TMP",
    }
    assert all(
        Path(first.environment[key]).is_relative_to(installer.paths.store_root)
        for key in portable_path_keys
    )
    assert first.environment["PIP_CONFIG_FILE"] == os.devnull
    assert first.environment["PYTHONNOUSERSITE"] == "1"
    assert Path(first.environment["MINERU_HOME"]).is_dir()
    assert first.environment["VIBEOCR_RUNTIME_ACCELERATOR"] == "cpu"
    assert first.environment["VIBEOCR_USE_GPU"] == "false"


def test_gpu_launch_environment_is_derived_from_installer_profile(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        install_runner=_fake_install,
        install_component_ids=(),
    )

    launch = installer.ensure()

    assert launch is not None
    assert launch.environment["VIBEOCR_RUNTIME_ACCELERATOR"] == "nvidia_cuda"
    assert launch.environment["VIBEOCR_USE_GPU"] == "true"


def test_failed_install_leaves_no_partial_or_final(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")

    def fail(_partial: Path, _manifest, _profile: str) -> Path:
        raise RuntimeInstallError("boom")

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=fail,
    )
    with pytest.raises(RuntimeInstallError, match="boom"):
        installer.ensure()
    assert not installer.paths.runtime_root.exists()
    assert not installer.paths.runtime_root.with_name("runtime.installing").exists()
    assert installer.maintenance_snapshot()["operation_state"] == "failed"


@pytest.mark.parametrize("resolver_timeout", [False, True])
def test_failed_repair_preserves_previous_runtime_until_verified_commit(
    tmp_path: Path,
    resolver_timeout: bool,
) -> None:
    manifest, component = _release(tmp_path / "release")
    initial = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
    )
    launch = initial.ensure()
    assert launch is not None
    preserved = initial.paths.runtime_root / "preserved.txt"
    preserved.write_text("previous-runtime", encoding="utf-8")
    marker_before = (initial.paths.runtime_root / ".installed.json").read_bytes()

    def fail(_partial: Path, _manifest, _profile: str) -> Path:
        if resolver_timeout:
            _run_install_command(
                [sys.executable, "-c", "import time; time.sleep(30)"],
                timeout=0.3,
                env=dict(os.environ),
                reporter=repair._reporter,
                heartbeat_code="runtime.resolve_packages",
            )
        raise RuntimeInstallError("repair failed")

    repair = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=fail,
        component_probe=lambda _root, component_ids, _profile_id: {
            component_id: component_id != "rapidocr-base"
            for component_id in component_ids
        },
        operation_id="failed-repair",
    )

    reason = "reason=total_timeout" if resolver_timeout else "repair failed"
    with pytest.raises(RuntimeInstallError, match=reason):
        repair.repair()

    assert preserved.read_text(encoding="utf-8") == "previous-runtime"
    assert (
        initial.paths.runtime_root / ".installed.json"
    ).read_bytes() == marker_before
    assert Path(launch.python_executable).is_file()
    assert not initial.paths.runtime_root.with_name("runtime.installing").exists()
    assert not initial.paths.runtime_root.with_name("runtime.rollback").exists()

    assert repair._reporter.snapshot["operation_state"] == "failed"
    if resolver_timeout:
        with pytest.raises(
            RuntimeInstallFailure, match="operation=failed-repair"
        ) as replayed:
            repair._reporter._store.raise_replayed_failure("failed-repair")
        assert "reason=total_timeout" in str(replayed.value)


def test_component_import_probe_reports_integrity_failed_drift(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    initial = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
    )
    initial.ensure()
    inspected = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
        component_probe=lambda _root, component_ids, _profile_id: {
            component_id: component_id != "rapidocr-base"
            for component_id in component_ids
        },
    )

    component_status = inspected.profile_payload()["components"][0]
    inspection = inspected.inspect(emit=False)

    assert component_status["actual_state"] == "drifted"
    assert component_status["drift_reason"] == "integrity_failed"
    assert component_status["repairable"] is True
    assert inspection.status == "missing"
    assert inspection.integrity == "not-installed"


def test_base_component_probe_uses_rapidocr_binding(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    runtime_root = tmp_path / "runtime"
    python = runtime_root / "python.exe"
    python.parent.mkdir(parents=True)
    python.write_bytes(b"python")

    def run(command, **_kwargs):  # type: ignore[no-untyped-def]
        modules = json.loads(command[-1])
        result = {
            component_id: module == "rapidocr"
            for component_id, module in modules.items()
        }
        return subprocess.CompletedProcess(
            command,
            0,
            stdout="VIBEOCR_COMPONENT_PROBE=" + json.dumps(result) + "\n",
            stderr="",
        )

    monkeypatch.setattr(runtime_maintenance.subprocess, "run", run)

    assert probe_runtime_components(
        runtime_root,
        ("rapidocr-base",),
        profile_id="win-x64-base",
    ) == {"rapidocr-base": True}


def test_runtime_control_inspect_probes_components_once(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    initial = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
    )
    initial.ensure()
    probe_calls: list[tuple[Path, tuple[str, ...]]] = []

    def probe(
        runtime_root: Path, component_ids: tuple[str, ...], _profile_id: str
    ) -> dict[str, bool]:
        probe_calls.append((runtime_root, component_ids))
        return {component_id: True for component_id in component_ids}

    inspected = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        component_probe=probe,
        operation_id="inspect-once",
    )
    control = object.__new__(RuntimeControl)
    control._installer_factory = lambda **_kwargs: inspected
    control._active_snapshot = None

    result = control.execute_with_result(operation="inspect")

    assert result.state.integrity == "verified"
    # base-only 安装后，展示投影按已安装闭包的覆盖 profile 回报
    # win-x64-base，而不是 accelerator plan 的 cpu 视角。
    assert result.profile["profile_id"] == "win-x64-base"
    assert len(probe_calls) == 1
    assert probe_calls[0][0] == inspected.paths.runtime_root
    assert all(
        component["actual_state"] == "ready"
        for component in result.profile["components"]
    )


def test_ensure_ready_runtime_probes_components_once(tmp_path: Path) -> None:
    # 回归：就绪运行时的启动 ensure 在 ready 判定与 _launch 校验之间
    # 复用同一次组件导入探测；重复探测会让每次产品启动都多付一遍
    # 已安装组件的冷导入成本。
    manifest, component = _release(tmp_path / "release")
    initial = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
    )
    initial.ensure()
    probe_calls: list[Path] = []

    def probe(
        runtime_root: Path, component_ids: tuple[str, ...], _profile_id: str
    ) -> dict[str, bool]:
        probe_calls.append(runtime_root)
        return {component_id: True for component_id in component_ids}

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
        component_probe=probe,
        operation_id="ensure-once",
    )

    launch = installer.ensure()

    assert launch is not None
    assert Path(launch.python_executable).is_file()
    assert len(probe_calls) == 1
    assert probe_calls[0] == installer.paths.runtime_root


def test_inspect_and_ensure_report_covering_profile_after_base_only_install(
    tmp_path: Path,
) -> None:
    # 回归：base-only 安装后，inspect 与 ensure 信封的 profile 投影必须
    # 回报已安装闭包的覆盖 profile（win-x64-base），而不是 accelerator
    # plan（win-x64-cpu）——plan 视角的组件集与绑定并不描述已安装的
    # 运行时，还会把闭包外组件谎报成 desired ready。
    manifest, component = _release(tmp_path / "release")

    def probe(
        _runtime_root: Path, component_ids: tuple[str, ...], _profile_id: str
    ) -> dict[str, bool]:
        return {component_id: True for component_id in component_ids}

    product = tmp_path / "product"
    control = RuntimeControl.from_installer_factory(
        lambda **kwargs: RuntimeInstaller(
            product_root=product,
            component_lock=component,
            runtime_manifest=manifest,
            accelerator="cpu",
            install_runner=_fake_install,
            component_probe=probe,
            **kwargs,
        )
    )
    control.execute_with_result(
        operation="ensure", install_component_ids=(), download_source_ids=("pypi",)
    )

    inspected = control.execute_with_result(operation="inspect")
    ensured = control.execute_with_result(
        operation="ensure", install_component_ids=(), download_source_ids=("pypi",)
    )

    for result in (inspected, ensured):
        assert result.state.status == "ready"
        assert result.profile["profile_id"] == "win-x64-base"
        assert result.profile["accelerator"] == "cpu"
        assert [item["component_id"] for item in result.profile["components"]] == [
            "rapidocr-base",
            "runtime_host",
        ]
        # 覆盖 profile 投影内全部组件都属于已安装闭包，desired ready 如实。
        assert all(
            item["desired_state"] == "ready" for item in result.profile["components"]
        )
        assert all(
            item["actual_state"] == "ready" for item in result.profile["components"]
        )


def test_component_desired_state_not_required_outside_installed_closure(
    tmp_path: Path,
) -> None:
    # 回归：cu126 的 gpu_runtime 精确 scope 不含 Paddle/MinerU；覆盖
    # profile（cu126 plan）投影它们时必须标记 desired not_required，
    # 不得谎报 ready / failed / 可修复。
    manifest, component = _release(tmp_path / "release")
    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        install_component_ids=("gpu_runtime",),
        install_runner=_fake_install,
        component_probe=lambda _root, ids, _profile_id: dict.fromkeys(ids, True),
    )
    installer.ensure()

    inspection = installer.inspect_snapshot(emit=False)

    assert inspection.state.status == "ready"
    assert inspection.profile["profile_id"] == "win-x64-cu126"
    statuses = {item["component_id"]: item for item in inspection.profile["components"]}
    for component_id in ("paddleocr-cuda", "mineru-cuda"):
        assert statuses[component_id]["desired_state"] == "not_required"
        assert statuses[component_id]["state"] == "not_required"
        assert statuses[component_id]["drift_reason"] == "none"
        assert statuses[component_id]["repairable"] is False
    for component_id in ("rapidocr-base", "gpu_runtime", "runtime_host"):
        assert statuses[component_id]["desired_state"] == "ready"
        assert statuses[component_id]["actual_state"] == "ready"


def test_runtime_status_ready_for_healthy_base_only_runtime(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # 回归：健康的 base-only 运行时不得被 /v2/runtime/status 判成
    # degraded——plan 投影下闭包外组件显示 missing 是展示层
    # 谎报，状态投影必须按已安装闭包的覆盖 profile 判定。
    manifest, component = _release(tmp_path / "release")
    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_component_ids=(),
        install_runner=_fake_install,
        component_probe=lambda _root, ids, _profile_id: dict.fromkeys(ids, True),
    )
    launch = installer.ensure()
    assert launch is not None
    for key, value in launch.environment.items():
        monkeypatch.setenv(key, value)
    monkeypatch.setattr(
        runtime_maintenance,
        "_cached_runtime_component_probe",
        lambda _root, component_ids, **_kwargs: dict.fromkeys(component_ids, True),
    )
    runtime_maintenance._component_probe_cache.clear()

    status = runtime_status_from_environment("instance-test", "ready")

    assert status["service_state"] == "ready"
    assert status["profile"]["profile_id"] == "win-x64-base"
    statuses = {item["component_id"]: item for item in status["profile"]["components"]}
    assert statuses["rapidocr-base"]["actual_state"] == "ready"
    assert all(
        item["desired_state"] == "ready" for item in status["profile"]["components"]
    )


def test_repair_of_in_sync_component_succeeds_without_claiming_global_ready(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    calls: list[Path] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        calls.append(partial)
        return _fake_install(partial, runtime_manifest, profile)

    initial = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=(),
    )
    initial.ensure()
    repair = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=(),
        component_probe=lambda _root, component_ids, _profile_id: {
            component_id: component_id != "rapidocr-base"
            for component_id in component_ids
        },
        component_ids=("runtime_host",),
        operation_id="repair-ready-component",
    )

    launch = repair.repair()

    assert launch is None
    assert len(calls) == 1
    snapshot = repair.maintenance_snapshot()
    assert snapshot is not None
    assert snapshot["operation_state"] == "succeeded"
    assert snapshot["requested_component_ids"] == ["runtime_host"]
    assert "effective_component_ids" not in snapshot
    assert repair.profile_payload()["components"][0]["actual_state"] == "drifted"


def test_component_lock_capability_mismatch_is_rejected(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    data = json.loads(component.read_text(encoding="utf-8"))
    data["required_capabilities"].append("unknown.feature")
    component.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(RuntimeInstallError, match="missing required"):
        RuntimeInstaller(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest,
            accelerator="cpu",
            install_runner=_fake_install,
        )


def test_component_lock_runtime_manifest_mismatch_is_rejected(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    data = json.loads(component.read_text(encoding="utf-8"))
    data["product"]["runtime_manifest_sha256"] = "f" * 64
    component.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(
        RuntimeInstallError, match="component lock runtime manifest mismatch"
    ):
        RuntimeInstaller(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest,
            accelerator="cpu",
            install_runner=_fake_install,
        )


def test_store_lock_is_cross_handle_exclusive(tmp_path: Path) -> None:
    first = RuntimeStoreLock(tmp_path / "locks" / "store.lock", timeout=0)
    second = RuntimeStoreLock(tmp_path / "locks" / "store.lock", timeout=0)
    first.acquire()
    try:
        with pytest.raises(RuntimeLockTimeout):
            second.acquire()
    finally:
        first.release()
    second.acquire()
    second.release()


def test_repair_is_idempotent_when_runtime_has_no_drift(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    calls: list[Path] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        calls.append(partial)
        return _fake_install(partial, runtime_manifest, profile)

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=(),
    )
    installer.ensure()
    original_marker = installer.paths.runtime_root / "original.txt"
    original_marker.write_text("old", encoding="utf-8")

    installer.repair()

    assert calls == [installer.paths.runtime_root.with_name("runtime.installing")]
    assert original_marker.read_text(encoding="utf-8") == "old"


def test_failed_operation_id_replays_failure_without_reexecuting(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")

    def fail_install(partial: Path, runtime_manifest, profile: str) -> Path:
        del partial, runtime_manifest, profile
        raise RuntimeInstallError("expected failure")

    failed = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=fail_install,
        operation_id="stable-operation",
    )
    with pytest.raises(RuntimeInstallError, match="expected failure"):
        failed.ensure()
    calls: list[Path] = []

    def unexpected_install(partial: Path, runtime_manifest, profile: str) -> Path:
        calls.append(partial)
        return _fake_install(partial, runtime_manifest, profile)

    replay = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=unexpected_install,
        operation_id="stable-operation",
    )

    with pytest.raises(RuntimeInstallFailure, match="expected failure"):
        replay.ensure()
    assert calls == []
    assert replay.maintenance_snapshot() == failed.maintenance_snapshot()


def test_component_repair_reports_requested_and_effective_scope(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    calls: list[Path] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        calls.append(partial)
        return _fake_install(partial, runtime_manifest, profile)

    initial = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
    )
    initial.ensure()
    Path(initial._launch().python_executable).unlink()

    repair = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        operation_id="repair-1",
        component_ids=("rapidocr-base",),
    )
    repair.repair()

    assert len(calls) == 2
    snapshot = repair.maintenance_snapshot()
    assert snapshot is not None
    assert snapshot["requested_component_ids"] == ["rapidocr-base"]
    assert set(snapshot["effective_component_ids"]) == {
        component.component_id
        for component in repair.manifest.profiles[repair.plan].components
    }
    assert repair.paths.runtime_root.is_dir()


def test_component_drift_uses_installed_distribution_and_selected_repair(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest, component_lock = _release(tmp_path / "release")
    manifest_payload = json.loads(manifest.read_text(encoding="utf-8"))
    manifest_payload["profiles"]["win-x64-cpu"]["components"] = [
        {
            "component_id": "rapidocr-base",
            "display_name": "RapidOCR base inference",
        },
        {
            "component_id": "paddleocr-cpu",
            "display_name": "PaddleOCR CPU inference",
            "version": "3.7.0",
        },
        *[
            {"component_id": component_id, "display_name": display_name}
            for component_id, display_name in (
                ("mineru-cpu", "MinerU CPU document parsing"),
                ("runtime_host", "Runtime HTTP host"),
            )
        ],
    ]
    manifest.write_text(json.dumps(manifest_payload) + "\n", encoding="utf-8")
    lock_payload = json.loads(component_lock.read_text(encoding="utf-8"))
    lock_payload["product"]["runtime_manifest_sha256"] = _sha(manifest.read_bytes())
    component_lock.write_text(json.dumps(lock_payload) + "\n", encoding="utf-8")
    calls: list[Path] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        calls.append(partial)
        python = _fake_install(partial, runtime_manifest, profile)
        metadata = partial / "Lib" / "site-packages" / "paddleocr-3.7.0.dist-info"
        metadata.mkdir(parents=True)
        (metadata / "METADATA").write_text(
            "Metadata-Version: 2.1\nName: paddleocr\nVersion: 3.7.0\n",
            encoding="utf-8",
        )
        return python

    initial = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component_lock,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
    )
    initial.ensure()
    metadata = (
        initial.paths.runtime_root
        / "Lib"
        / "site-packages"
        / "paddleocr-3.7.0.dist-info"
        / "METADATA"
    )
    metadata.write_text(
        "Metadata-Version: 2.1\nName: paddleocr\nVersion: 3.6.0\n",
        encoding="utf-8",
    )
    drifted = next(
        item
        for item in initial.profile_payload()["components"]
        if item["component_id"] == "paddleocr-cpu"
    )
    assert drifted["actual_state"] == "drifted"
    assert drifted["actual_version"] == "3.6.0"
    assert drifted["drift_reason"] == "version_mismatch"
    metadata.unlink()
    missing = next(
        item
        for item in initial.profile_payload()["components"]
        if item["component_id"] == "paddleocr-cpu"
    )
    assert missing["actual_state"] == "missing"

    repair = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component_lock,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        operation_id="repair-component",
        component_ids=("paddleocr-cpu",),
    )
    repair.repair()

    assert len(calls) == 2
    repaired = next(
        item
        for item in repair.profile_payload()["components"]
        if item["component_id"] == "paddleocr-cpu"
    )
    assert repaired["actual_state"] == "ready"


def test_runtime_host_json_contract_selects_and_persists_accelerator(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    manifest, component = _release(tmp_path / "release")

    request = {
        "protocol_version": 2,
        "operation": "inspect",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
        "accelerator": "nvidia_cuda",
    }
    assert main(["--request-json", json.dumps(request)]) == 0
    envelope = json.loads(capsys.readouterr().out)
    assert envelope["protocol_version"] == 2
    assert envelope["ok"] is True
    assert envelope["operation"] == "inspect"
    assert envelope["state"]["accelerator"] == "nvidia_cuda"
    assert envelope["state"]["runtime_root"].endswith("runtime")
    assert "profile" not in envelope["state"]
    assert envelope["profile"]["profile_id"] == "win-x64-cu126"
    assert envelope["profile"]["components"][-1]["component_id"] == "gpu_runtime"
    assert envelope["maintenance"]["operation_state"] == "succeeded"
    descriptors = {item["name"]: item for item in envelope["capability_descriptors"]}
    recognition = descriptors["ocr.recognition.v2"]
    assert recognition == {
        "name": "ocr.recognition.v2",
        "lifecycle": "active",
        "introduced_in": "2.0.0",
        "deprecated_in": None,
        "sunset_at": None,
        "replacement": None,
    }


def test_runtime_host_emits_unicode_paths_as_utf8(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    output = io.BytesIO()
    stdout = io.TextIOWrapper(output, encoding="cp1252")
    monkeypatch.setattr(sys, "stdout", stdout)
    moved_root = tmp_path / "移动后"
    request = {
        "protocol_version": 2,
        "operation": "inspect",
        "product_root": str(moved_root),
        "component_lock": str(moved_root / "component-lock.json"),
        "runtime_manifest": str(moved_root / "runtime-manifest.json"),
    }

    assert main(["--request-json", json.dumps(request, ensure_ascii=False)]) == 1

    stdout.flush()
    envelope = json.loads(output.getvalue().decode("utf-8"))
    assert envelope["ok"] is False
    assert moved_root.name in envelope["error"]["message"]


def test_runtime_host_allows_standard_streams_without_reconfigure(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    stdout = io.StringIO()
    monkeypatch.setattr(sys, "stdout", stdout)
    monkeypatch.setattr(sys, "stderr", None)

    assert main(["--request-json", "not-json"]) == 1

    assert json.loads(stdout.getvalue())["error"]["code"] == "invalid_request"


def test_runtime_host_does_not_swallow_stdout_write_errors(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class FailingWriter:
        def write(self, _value: str) -> None:
            raise OSError("stdout unavailable")

    monkeypatch.setattr(sys, "stdout", FailingWriter())
    monkeypatch.setattr(sys, "stderr", None)

    with pytest.raises(OSError, match="stdout unavailable"):
        main(["--request-json", "not-json"])


def test_runtime_host_emits_opt_in_ndjson_progress(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    manifest, component = _release(tmp_path / "release")
    request = {
        "protocol_version": 2,
        "operation": "inspect",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
        "accelerator": "cpu",
        "accepted_event_streams": ["ndjson.v1"],
    }

    assert main(["--request-json", json.dumps(request)]) == 0

    messages = [json.loads(line) for line in capsys.readouterr().out.splitlines()]
    events = messages[:-1]
    response = messages[-1]
    assert [event["event_type"] for event in events] == ["progress", "snapshot"]
    assert [event["snapshot"]["sequence"] for event in events] == [1, 2]
    assert all(event["operation"] == "inspect" for event in events)
    assert response["ok"] is True
    assert response["maintenance"] == events[-1]["snapshot"]


def test_runtime_host_rejects_unknown_event_stream(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    manifest, component = _release(tmp_path / "release")
    request = {
        "protocol_version": 2,
        "operation": "inspect",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
        "accepted_event_streams": ["sse.v1"],
    }

    assert main(["--request-json", json.dumps(request)]) == 1
    envelope = json.loads(capsys.readouterr().out)
    assert envelope["error"]["code"] == "invalid_request"


def test_install_progress_and_http_status_share_the_persisted_snapshot(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest, component = _release(tmp_path / "release")
    events: list[dict] = []
    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
        event_sink=events.append,
    )

    launch = installer.ensure()

    assert [event["snapshot"]["phase"] for event in events] == [
        "validate_binding",
        "wait_for_lock",
        "prepare_runtime",
        "install_profile",
        "verify_runtime",
        "commit_runtime",
        "commit_runtime",
    ]
    assert events[-1]["snapshot"]["operation_state"] == "succeeded"
    for key, value in launch.environment.items():
        monkeypatch.setenv(key, value)
    monkeypatch.setattr(
        "vibeocr.runtime.environments.runtime_maintenance.probe_runtime_components",
        lambda _root, component_ids, **_kwargs: {
            component_id: True for component_id in component_ids
        },
    )
    runtime_maintenance._component_probe_cache.clear()
    status = runtime_status_from_environment("instance-test", "ready")
    assert status["maintenance"]["sequence"] == events[-1]["snapshot"]["sequence"]
    assert status["maintenance"]["message_code"] == "runtime.ensure_complete"
    assert status["profile"]["components"][0] == {
        "component_id": "rapidocr-base",
        "display_name": "RapidOCR 基础识别",
        "state": "ready",
        "desired_state": "ready",
        "desired_version": None,
        "actual_state": "ready",
        "actual_version": None,
        "drift_reason": "none",
        "repairable": False,
    }
    assert status["source"]["backend_source_sha"] == "0" * 40

    monkeypatch.setattr(
        "vibeocr.runtime.environments.runtime_maintenance.probe_runtime_components",
        lambda _root, component_ids, **_kwargs: {
            component_id: component_id != "rapidocr-base"
            for component_id in component_ids
        },
    )
    runtime_maintenance._component_probe_cache.clear()
    degraded = runtime_status_from_environment("instance-test", "ready")
    assert degraded["service_state"] == "degraded"
    assert degraded["profile"]["components"][0]["drift_reason"] == ("integrity_failed")


def test_long_install_command_emits_heartbeat(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest_path, _ = _release(tmp_path / "release")
    manifest = load_runtime_manifest(manifest_path)
    events: list[dict] = []
    reporter = RuntimeMaintenanceReporter(
        state_root=tmp_path / "state",
        profile=profile_descriptor(
            manifest.profiles["win-x64-cpu"],
            accelerator="cpu",
        ),
        event_sink=events.append,
    )
    reporter.start("ensure", total_steps=7)
    reporter.advance(
        phase="install_profile",
        current=4,
        total=7,
        message_code="runtime.install_profile",
    )
    events_before_cancel_checks = list(events)
    for _ in range(100):
        reporter.check_cancelled()
    assert events == events_before_cancel_checks

    from vibeocr.runtime.environments import runtime_installer as installer_module

    monkeypatch.setattr(
        installer_module,
        "_CHILD_HEARTBEAT_INTERVAL_SECONDS",
        0.2,
    )
    command = [
        sys.executable,
        "-c",
        (
            "import sys, time\n"
            "print('Collecting torch-2.7.0', flush=True)\n"
            "time.sleep(0.6)\n"
            "print('noise line without status prefix', flush=True)\n"
            "time.sleep(0.6)\n"
            "print('Downloading torch-2.7.0-cp313 (2.5 GB)', flush=True)\n"
        ),
    ]

    _run_install_command(
        command,
        timeout=60,
        env={**os.environ},
        reporter=reporter,
        heartbeat_code="runtime.install_profile",
    )

    heartbeats = [event for event in events if event["event_type"] == "heartbeat"]
    assert heartbeats
    assert heartbeats[0]["snapshot"]["phase"] == "install_profile"
    assert heartbeats[0]["message_code"] == "runtime.install_profile"
    # 状态行明细按变化回报：无前缀的噪声行不产生事件
    detail_events = [event for event in events if event.get("fallback_message")]
    assert "reason=started" in detail_events[0]["fallback_message"]
    assert [event["fallback_message"] for event in detail_events[1:]] == [
        "Collecting torch-2.7.0",
        "Downloading torch-2.7.0-cp313 (2.5 GB)",
    ]
    for event in detail_events:
        assert event["event_type"] == "progress"
        assert event["snapshot"]["phase"] == "install_profile"


def test_child_status_detail_only_matches_known_prefixes() -> None:
    from vibeocr.runtime.environments.runtime_installer import _child_status_detail

    assert (
        _child_status_detail("Collecting numpy==2.3.1\n") == "Collecting numpy==2.3.1"
    )
    assert (
        _child_status_detail("Downloading torch-2.7.0-cp313 (2.5 GB)\r\n")
        == "Downloading torch-2.7.0-cp313 (2.5 GB)"
    )
    assert _child_status_detail("Using cached scipy-1.16.0.whl") is not None
    assert _child_status_detail("noise line") is None
    assert _child_status_detail("   ") is None
    # 超长行（如 Installing collected packages 全量列表）有界截断
    assert len(_child_status_detail("Collecting " + "x" * 500)) == 160


def test_run_install_command_failure_keeps_stderr_tail() -> None:
    """pip 失败时错误信息必须携带子进程输出尾部，不能再只剩 exit code。"""

    command = [
        sys.executable,
        "-c",
        (
            "import sys\n"
            "print('Collecting big-wheel')\n"
            "print('ERROR: Could not find a version for req-x', file=sys.stderr)\n"
            "sys.exit(1)\n"
        ),
    ]
    with pytest.raises(RuntimeInstallError) as excinfo:
        _run_install_command(
            command,
            timeout=60,
            env={**os.environ},
            reporter=None,
            heartbeat_code="runtime.install_profile",
        )
    message = str(excinfo.value)
    assert "runtime.install_profile failed with exit code 1" in message
    assert "ERROR: Could not find a version for req-x" in message


def test_run_install_command_failure_tail_is_bounded_and_prefers_stderr() -> None:
    command = [
        sys.executable,
        "-c",
        (
            "import sys\n"
            "for index in range(60):\n"
            "    print(f'noise-{index}')\n"
            "print('ERROR: hash mismatch for wheel-y', file=sys.stderr)\n"
            "sys.exit(1)\n"
        ),
    ]
    with pytest.raises(RuntimeInstallError) as excinfo:
        _run_install_command(
            command,
            timeout=60,
            env={**os.environ},
            reporter=None,
            heartbeat_code="runtime.install_profile",
        )
    message = str(excinfo.value)
    assert "hash mismatch for wheel-y" in message
    # stderr 优先且整条消息有界：stdout 噪声不应全部进入错误信息
    assert "noise-0\n" not in message


def test_runtime_host_rejects_legacy_profile_field(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    manifest, component = _release(tmp_path / "release")
    request = {
        "protocol_version": 2,
        "operation": "inspect",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
        "profile": "win-x64-cpu",
    }
    assert main(["--request-json", json.dumps(request)]) == 1
    envelope = json.loads(capsys.readouterr().out)
    assert envelope["ok"] is False
    assert envelope["error"]["code"] == "invalid_request"


# ---------------------------------------------------------------------------
# base-offline 离线安装路径（计划 §4.2）
# ---------------------------------------------------------------------------


def _run_default_installer(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    *,
    with_base_pack: bool,
    profile: str = "win-x64-base",
) -> tuple[list[list[str]], Path, Path]:
    """Run ``_default_install_runner`` with captured uv commands.

    Returns (captured_commands, partial_root, manifest_path). The Python
    archive extraction and every child process are faked so the test only
    asserts command construction and pack handling.
    """
    from vibeocr.runtime.environments import runtime_installer as installer

    manifest_path, _ = _release(tmp_path / "release", with_base_pack=with_base_pack)
    manifest = load_runtime_manifest(manifest_path)
    partial_root = tmp_path / "runtimes" / "runtime-0" / "partial"
    partial_root.mkdir(parents=True)
    commands: list[list[str]] = []

    def fake_run(command, *, timeout, env, reporter, heartbeat_code, gate_python=None):  # type: ignore[no-untyped-def]
        commands.append(list(command))

    def fake_extract_python(archive_path, destination, *, progress=None):  # type: ignore[no-untyped-def]
        (destination / "python.exe").write_bytes(b"python")

    def fake_prepare(_python, _lock, _endpoint, _cache, _reporter, _env):  # type: ignore[no-untyped-def]
        downloads = tmp_path / "downloads"
        downloads.mkdir(exist_ok=True)
        return downloads

    monkeypatch.setattr(installer, "_run_install_command", fake_run)
    monkeypatch.setattr(installer, "_prepare_online_artifacts", fake_prepare)
    monkeypatch.setattr(
        installer,
        "_local_install_requirements",
        lambda lock, *args: lock.with_suffix(".local.txt"),
    )
    monkeypatch.setattr(installer, "_extract_python_archive", fake_extract_python)
    installer._default_install_runner(
        partial_root,
        manifest,
        manifest.profiles[profile].scopes[0],
        _pypi_source(),
    )
    return commands, partial_root, manifest_path


class TestOfflineRuntimePack:
    def test_bound_pack_installs_with_no_index_and_find_links(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        commands, partial_root, manifest_path = _run_default_installer(
            tmp_path, monkeypatch, with_base_pack=True
        )
        profile_install = commands[0]
        assert "--no-index" in profile_install
        # 离线路径不做逐件 --require-hashes:pack 完整性由 manifest 绑定。
        assert "--require-hashes" not in profile_install
        # pack 是纯 wheel 闭包，离线安装永不触发本机构建。
        assert "--only-binary=:all:" in profile_install
        find_links = profile_install[
            profile_install.index("--find-links") + 1  # type: ignore[arg-type]
        ]
        pack_dir = Path(find_links)
        # pack 已解压到 state 缓存且包含全部 wheel。
        assert (pack_dir / "rapidocr-3.9.2-py3-none-any.whl").is_file()
        assert (pack_dir / "onnxruntime-1.28.0-cp313-cp313-win_amd64.whl").is_file()
        assert (pack_dir / ".complete").is_file()
        # 完整标记存在时重复安装幂等复用，不重复解压。
        marker_before = (pack_dir / ".complete").stat().st_mtime_ns
        # 同一 partial 目录重复执行：完整标记存在时直接复用解压结果。
        from vibeocr.runtime.environments import runtime_installer as installer

        manifest = load_runtime_manifest(manifest_path)
        commands.clear()
        installer._default_install_runner(
            partial_root,
            manifest,
            manifest.profiles["win-x64-base"].scopes[0],
            _pypi_source(),
        )
        assert commands[0][commands[0].index("--find-links") + 1] == find_links
        assert (pack_dir / ".complete").stat().st_mtime_ns == marker_before

    def test_same_version_new_manifest_pack_rebuilds_cached_wheels(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        commands, partial_root, manifest_path = _run_default_installer(
            tmp_path, monkeypatch, with_base_pack=True
        )
        first_command = commands[0]
        pack_dir = Path(first_command[first_command.index("--find-links") + 1])
        assert (pack_dir / "rapidocr-3.9.2-py3-none-any.whl").is_file()

        document = json.loads(manifest_path.read_text(encoding="utf-8"))
        profile = document["profiles"]["win-x64-base"]
        pack_path = manifest_path.parent / profile["runtime_pack"][0]
        with zipfile.ZipFile(pack_path, mode="w") as archive:
            archive.writestr("pack-requirements.txt", "rapidocr==3.9.2\n")
            archive.writestr("rapidocr-3.9.2-new-py3-none-any.whl", b"new-wheel")
        profile["runtime_pack_sha256"] = [_sha(pack_path.read_bytes())]
        manifest_path.write_text(json.dumps(document), encoding="utf-8")
        manifest = load_runtime_manifest(manifest_path)

        from vibeocr.runtime.environments import runtime_installer as installer

        installer._default_install_runner(
            partial_root,
            manifest,
            manifest.profiles["win-x64-base"].scopes[0],
            _pypi_source(),
        )
        assert not (pack_dir / "rapidocr-3.9.2-py3-none-any.whl").exists()
        assert (pack_dir / "rapidocr-3.9.2-new-py3-none-any.whl").read_bytes() == (
            b"new-wheel"
        )

    def test_without_pack_installs_online_without_no_index(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        commands, _, _ = _run_default_installer(
            tmp_path, monkeypatch, with_base_pack=False
        )
        assert "--no-index" not in commands[0]
        # 所选索引只供隔离构建；目标闭包绑定为本地输入，禁止重选远端。
        assert "--no-deps" in commands[0]
        assert commands[0][commands[0].index("--find-links") + 1] == str(
            tmp_path / "downloads"
        )
        assert "--require-hashes" in commands[0]
        # lock 的哈希行覆盖 sdist-only 工件（antlr4-python3-runtime==4.9.3），
        # 在线路径禁止 sdist 会令完整 profile 无法解析。
        assert "--only-binary=:all:" not in commands[0]
        assert commands[0][-2:] == [
            "-r",
            str(tmp_path / "release" / "requirements-win-x64-base.local.txt"),
        ]

    def test_missing_bound_pack_fails_closed(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from vibeocr.runtime.environments import runtime_installer as installer

        manifest_path, _ = _release(tmp_path / "release", with_base_pack=True)
        (tmp_path / "release" / "vibeocr-runtime-pack-win-x64-base-0.7.0.zip").unlink()
        partial_root = tmp_path / "runtimes" / "runtime-0" / "partial"
        partial_root.mkdir(parents=True)
        manifest = load_runtime_manifest(manifest_path, verify_artifacts=False)

        def fake_run(
            command, *, timeout, env, reporter, heartbeat_code, gate_python=None
        ):  # type: ignore[no-untyped-def]
            raise AssertionError("install must not run when the pack is missing")

        monkeypatch.setattr(installer, "_run_install_command", fake_run)

        def fake_extract_python(archive_path, destination, *, progress=None):  # type: ignore[no-untyped-def]
            (destination / "python.exe").write_bytes(b"python")

        monkeypatch.setattr(installer, "_extract_python_archive", fake_extract_python)
        with pytest.raises(RuntimeInstallError, match="runtime pack is missing"):
            installer._default_install_runner(
                partial_root,
                manifest,
                manifest.profiles["win-x64-base"].scopes[0],
                _pypi_source(),
            )


@pytest.mark.parametrize(
    ("source_ids", "expected_endpoint"),
    [
        (None, "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/"),
        (("pypi",), "https://pypi.org/simple"),
    ],
)
def test_online_install_uses_selected_source_and_isolates_parent_config(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    source_ids: tuple[str, ...] | None,
    expected_endpoint: str,
) -> None:
    from vibeocr.runtime.environments import runtime_installer as installer_module

    manifest, component = _release(tmp_path / "release")
    captured: list[tuple[list[str], dict[str, str]]] = []

    def fake_run(command, *, timeout, env, reporter, heartbeat_code, gate_python=None):  # type: ignore[no-untyped-def]
        captured.append((list(command), dict(env)))

    def fake_extract_python(archive_path, destination, *, progress=None):  # type: ignore[no-untyped-def]
        (destination / "python.exe").write_bytes(b"python")

    resolved_endpoints: list[str] = []

    def fake_prepare(_python, _lock, endpoint, _cache, _reporter, _env):  # type: ignore[no-untyped-def]
        resolved_endpoints.append(endpoint)
        downloads = tmp_path / "downloads"
        downloads.mkdir(exist_ok=True)
        return downloads

    monkeypatch.setattr(installer_module, "_run_install_command", fake_run)
    monkeypatch.setattr(installer_module, "_prepare_online_artifacts", fake_prepare)
    monkeypatch.setattr(
        installer_module,
        "_local_install_requirements",
        lambda lock, *args: lock.with_suffix(".local.txt"),
    )
    monkeypatch.setattr(
        installer_module,
        "_extract_python_archive",
        fake_extract_python,
    )
    monkeypatch.setenv("PIP_EXTRA_INDEX_URL", "https://extra.invalid/simple")
    monkeypatch.setenv("PIP_FIND_LINKS", "https://links.invalid")
    monkeypatch.setenv("PIP_NO_INDEX", "1")
    monkeypatch.setenv("UV_INDEX", "https://uv.invalid/simple")

    runtime = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        component_probe=lambda _root, ids, _profile_id: dict.fromkeys(ids, True),
        download_source_ids=source_ids,
        install_component_ids=(),
    )
    launch = runtime.ensure()

    profile_command, child_env = captured[0]
    assert resolved_endpoints == [expected_endpoint]
    assert profile_command[profile_command.index("--default-index") + 1] == (
        expected_endpoint
    )
    assert "--require-hashes" in profile_command
    assert "--no-deps" in profile_command
    assert profile_command[-1].endswith(".local.txt")
    assert "PIP_EXTRA_INDEX_URL" not in child_env
    assert "PIP_FIND_LINKS" not in child_env
    assert "PIP_NO_INDEX" not in child_env
    assert "UV_INDEX" not in child_env
    assert launch is not None
    assert launch.environment["PIP_INDEX_URL"] == expected_endpoint


def test_cuda_gpu_only_selection_uses_exact_install_scope(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from vibeocr.runtime.environments import runtime_installer as installer_module

    manifest, component = _release(tmp_path / "release")
    captured: list[list[str]] = []

    def fake_run(command, *, timeout, env, reporter, heartbeat_code, gate_python=None):  # type: ignore[no-untyped-def]
        captured.append(list(command))

    def fake_extract_python(archive_path, destination, *, progress=None):  # type: ignore[no-untyped-def]
        (destination / "python.exe").write_bytes(b"python")

    def fake_prepare(_python, _lock, _endpoint, _cache, _reporter, _env):  # type: ignore[no-untyped-def]
        downloads = tmp_path / "downloads"
        downloads.mkdir(exist_ok=True)
        return downloads

    monkeypatch.setattr(installer_module, "_run_install_command", fake_run)
    monkeypatch.setattr(installer_module, "_prepare_online_artifacts", fake_prepare)
    monkeypatch.setattr(
        installer_module,
        "_local_install_requirements",
        lambda lock, *args: lock.with_suffix(".local.txt"),
    )
    monkeypatch.setattr(
        installer_module,
        "_extract_python_archive",
        fake_extract_python,
    )

    runtime = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        component_probe=lambda _root, ids, _profile_id: dict.fromkeys(ids, True),
        install_component_ids=("gpu_runtime",),
    )
    runtime.ensure()

    assert captured[0][-1] == str(
        tmp_path / "release" / "requirements-win-x64-cu126-gpu.local.txt"
    )
    marker = json.loads(
        (runtime.paths.runtime_root / ".installed.json").read_text(encoding="utf-8")
    )
    assert marker["component_ids"] == [
        "rapidocr-base",
        "runtime_host",
        "gpu_runtime",
    ]


def test_control_installer_store_composition_retries_durable_selection(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    product_root = tmp_path / "product"
    constructed: list[dict[str, object]] = []

    def fail_install(_partial: Path, _manifest, _profile: str) -> Path:
        raise RuntimeInstallError("synthetic install failure")

    def factory(**kwargs):  # type: ignore[no-untyped-def]
        constructed.append(dict(kwargs))
        install_runner = (
            fail_install
            if kwargs.get("operation_id") == "composition-failed"
            else _fake_install
        )
        return RuntimeInstaller(
            product_root=product_root,
            component_lock=component,
            runtime_manifest=manifest,
            accelerator="cpu",
            install_runner=install_runner,
            **kwargs,
        )

    control = RuntimeControl.from_installer_factory(factory)
    with pytest.raises(RuntimeInstallError, match="synthetic install failure"):
        control.execute(
            operation="ensure",
            operation_id="composition-failed",
            install_component_ids=("document_parsing",),
            download_source_ids=("huggingface",),
        )

    restarted = RuntimeControl.from_installer_factory(factory)
    receipt = restarted.command(
        command_id="composition-retry-command",
        command="retry",
        target_operation_id="composition-failed",
        new_operation_id="composition-retried",
    )
    assert receipt["snapshot"]["operation_state"] == "succeeded"
    assert receipt["snapshot"]["requested_download_source_ids"] == ["huggingface"]
    assert receipt["snapshot"]["effective_download_source_ids"] == [
        "tuna-pypi",
        "huggingface",
    ]
    retried = next(
        item
        for item in constructed
        if item.get("operation_id") == "composition-retried"
    )
    assert retried["install_component_ids"] == ("mineru-cpu",)
    assert retried["download_source_ids"] == ("huggingface",)

    store = runtime_maintenance.RuntimeOperationStore(restarted.state_root)
    failed_intent = store.intent("composition-failed")
    assert failed_intent["requested_download_source_ids"] == ["huggingface"]
    assert failed_intent["download_source_ids"] == ["huggingface", "tuna-pypi"]
    intent = store.intent("composition-retried")
    assert intent["install_component_ids"] == ["mineru-cpu"]
    assert intent["requested_download_source_ids"] == ["huggingface"]
    assert intent["download_source_ids"] == ["huggingface", "tuna-pypi"]
    observation = restarted.observe("composition-retried")
    assert observation["snapshot"]["operation_state"] == "succeeded"
    assert observation["snapshot"]["requested_download_source_ids"] == ["huggingface"]
    assert observation["snapshot"]["effective_download_source_ids"] == [
        "tuna-pypi",
        "huggingface",
    ]
    assert observation["events"][-1]["snapshot"]["operation_state"] == "succeeded"

    default_receipt = restarted.execute(
        operation="ensure",
        operation_id="composition-default-source",
        install_component_ids=(),
    )
    assert default_receipt["snapshot"]["effective_download_source_ids"] == ["tuna-pypi"]
    default_intent = runtime_maintenance.RuntimeOperationStore(
        restarted.state_root
    ).intent("composition-default-source")
    assert default_intent["requested_download_source_ids"] is None
    assert default_intent["download_source_ids"] == ["tuna-pypi"]


def test_extract_runtime_pack_rejects_unsafe_members(tmp_path: Path) -> None:
    from vibeocr.runtime.environments import runtime_installer as installer

    pack = tmp_path / "pack.zip"
    with zipfile.ZipFile(pack, mode="w") as archive:
        archive.writestr("../evil.whl", b"evil")
    with pytest.raises(RuntimeInstallError, match="unsafe runtime pack member"):
        installer._extract_runtime_pack(
            [pack], tmp_path / "cache", expected_sha256=(_sha(pack.read_bytes()),)
        )

    pack2 = tmp_path / "pack2.zip"
    with zipfile.ZipFile(pack2, mode="w") as archive:
        archive.writestr("payload.txt", b"not a wheel")
    with pytest.raises(RuntimeInstallError, match="unsafe runtime pack member"):
        installer._extract_runtime_pack(
            [pack2], tmp_path / "cache2", expected_sha256=(_sha(pack2.read_bytes()),)
        )


def test_base_accelerator_maps_to_base_profile() -> None:
    from vibeocr.runtime.environments.runtime_installer import ACCELERATOR_TO_PLAN

    assert ACCELERATOR_TO_PLAN["base"] == "win-x64-base"


def test_extract_runtime_pack_requires_pack_requirements(tmp_path: Path) -> None:
    from vibeocr.runtime.environments import runtime_installer as installer

    pack = tmp_path / "pack.zip"
    with zipfile.ZipFile(pack, mode="w") as archive:
        archive.writestr("rapidocr-3.9.2-py3-none-any.whl", b"wheel")
    with pytest.raises(RuntimeInstallError, match="lacks pack-requirements.txt"):
        installer._extract_runtime_pack(
            [pack], tmp_path / "cache", expected_sha256=(_sha(pack.read_bytes()),)
        )


def test_full_profile_without_pack_falls_back_online(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """full 闭包绑定的 pack 未下载到位时回退在线安装,不 fail closed。"""
    from vibeocr.runtime.environments import runtime_installer as installer

    manifest_path, _ = _release(tmp_path / "release")
    raw = json.loads(manifest_path.read_text(encoding="utf-8"))
    raw["profiles"]["win-x64-cpu"]["runtime_pack"] = ["not-yet-downloaded.zip"]
    raw["profiles"]["win-x64-cpu"]["runtime_pack_sha256"] = ["0" * 64]
    manifest_path.write_text(json.dumps(raw), encoding="utf-8")
    # 绑定的 pack 文件不存在:component lock 的 manifest 哈希已失配,直接
    # 构造 manifest 对象驱动 runner(绕过 _validate_binding)。
    manifest = load_runtime_manifest(manifest_path, verify_artifacts=False)
    partial_root = tmp_path / "runtimes" / "runtime-0" / "partial"
    partial_root.mkdir(parents=True)
    commands: list[list[str]] = []

    def fake_run(command, *, timeout, env, reporter, heartbeat_code, gate_python=None):  # type: ignore[no-untyped-def]
        commands.append(list(command))

    def fake_extract_python(archive_path, destination, *, progress=None):  # type: ignore[no-untyped-def]
        (destination / "python.exe").write_bytes(b"python")

    def fake_prepare(_python, _lock, _endpoint, _cache, _reporter, _env):  # type: ignore[no-untyped-def]
        downloads = tmp_path / "downloads"
        downloads.mkdir(exist_ok=True)
        return downloads

    original_run = installer._run_install_command
    original_extract = installer._extract_python_archive
    original_prepare = installer._prepare_online_artifacts
    installer._run_install_command = fake_run  # type: ignore[assignment]
    installer._extract_python_archive = fake_extract_python  # type: ignore[assignment]
    installer._prepare_online_artifacts = fake_prepare  # type: ignore[assignment]
    monkeypatch.setattr(
        installer,
        "_local_install_requirements",
        lambda lock, *args: lock.with_suffix(".local.txt"),
    )
    try:
        installer._default_install_runner(
            partial_root,
            manifest,
            manifest.profiles["win-x64-cpu"].scopes[0],
            _pypi_source(),
        )
    finally:
        installer._run_install_command = original_run  # type: ignore[assignment]
        installer._extract_python_archive = original_extract  # type: ignore[assignment]
        installer._prepare_online_artifacts = original_prepare  # type: ignore[assignment]
    # 回退在线:require-hashes + lock + 已验证工件的 find-links,而非 --no-index。
    assert "--require-hashes" in commands[0]
    assert "--no-index" not in commands[0]
    assert "--find-links" in commands[0]
    assert commands[0][-1].endswith("requirements-win-x64-cpu.local.txt")
    assert "--no-deps" in commands[0]


def test_multi_part_pack_extracts_into_one_directory(tmp_path: Path) -> None:
    from vibeocr.runtime.environments import runtime_installer as installer

    part1 = tmp_path / "vibeocr-runtime-pack-win-x64-cpu-0.7.0.part01.zip"
    part2 = tmp_path / "vibeocr-runtime-pack-win-x64-cpu-0.7.0.part02.zip"
    with zipfile.ZipFile(part1, mode="w") as archive:
        archive.writestr("pack-requirements.txt", "rapidocr==3.9.2\n")
        archive.writestr("rapidocr-3.9.2-py3-none-any.whl", b"rapidocr-wheel")
    with zipfile.ZipFile(part2, mode="w") as archive:
        archive.writestr("onnxruntime-1.28.0-cp313-cp313-win_amd64.whl", b"ort")

    binding = (_sha(part1.read_bytes()), _sha(part2.read_bytes()))
    pack_dir = installer._extract_runtime_pack(
        [part1, part2], tmp_path / "cache", expected_sha256=binding
    )
    assert pack_dir.name == "vibeocr-runtime-pack-win-x64-cpu-0.7.0"
    assert (pack_dir / "pack-requirements.txt").is_file()
    assert (pack_dir / "rapidocr-3.9.2-py3-none-any.whl").is_file()
    assert (pack_dir / "onnxruntime-1.28.0-cp313-cp313-win_amd64.whl").is_file()
    assert (pack_dir / ".complete").is_file()
    # 幂等:完整标记存在时直接复用。
    again = installer._extract_runtime_pack(
        [part1, part2], tmp_path / "cache", expected_sha256=binding
    )
    assert again == pack_dir


def test_old_runtime_pack_marker_rebuilds_only_its_cache_directory(
    tmp_path: Path,
) -> None:
    from vibeocr.runtime.environments import runtime_installer as installer

    pack = tmp_path / "vibeocr-runtime-pack-win-x64-base-0.7.0.zip"
    with zipfile.ZipFile(pack, mode="w") as archive:
        archive.writestr("pack-requirements.txt", "rapidocr==3.9.2\n")
        archive.writestr("rapidocr-3.9.2-py3-none-any.whl", b"fresh")
    cache = tmp_path / "cache"
    pack_dir = cache / pack.stem
    pack_dir.mkdir(parents=True)
    (pack_dir / ".complete").write_text("ok\n", encoding="utf-8")
    (pack_dir / "stale.whl").write_bytes(b"stale")
    sibling = cache / "other-pack"
    sibling.mkdir()
    (sibling / "keep.txt").write_text("preserve", encoding="utf-8")

    extracted = installer._extract_runtime_pack(
        [pack], cache, expected_sha256=(_sha(pack.read_bytes()),)
    )

    assert extracted == pack_dir
    assert not (pack_dir / "stale.whl").exists()
    assert (pack_dir / "rapidocr-3.9.2-py3-none-any.whl").read_bytes() == b"fresh"
    assert (pack_dir / ".complete").read_text(encoding="utf-8") != "ok\n"
    assert (sibling / "keep.txt").read_text(encoding="utf-8") == "preserve"


def test_runtime_pack_cache_rejects_escaping_stem_and_reparse_directory(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from vibeocr.runtime.environments import runtime_installer as installer

    cache = tmp_path / "cache"
    cache.mkdir()
    sibling = tmp_path / "keep.txt"
    sibling.write_text("preserve", encoding="utf-8")
    escaping = tmp_path / "...zip"
    escaping.write_bytes(b"zip")
    with pytest.raises(RuntimeInstallError, match="cache name is unsafe"):
        installer._extract_runtime_pack(
            [escaping], cache, expected_sha256=(_sha(escaping.read_bytes()),)
        )

    pack = tmp_path / "pack.zip"
    pack.write_bytes(b"zip")
    destination = cache / "pack"
    destination.mkdir()
    original_is_junction = Path.is_junction
    monkeypatch.setattr(
        Path,
        "is_junction",
        lambda path: path == destination or original_is_junction(path),
    )
    with pytest.raises(RuntimeInstallError, match="reparse point"):
        installer._extract_runtime_pack(
            [pack], cache, expected_sha256=(_sha(pack.read_bytes()),)
        )
    assert destination.is_dir()
    assert sibling.read_text(encoding="utf-8") == "preserve"


def test_ensure_with_explicit_base_only_scope_installs_base_lock(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    seen_profiles: list[str] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        seen_profiles.append(profile)
        return _fake_install(partial, runtime_manifest, profile)

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=(),
    )
    installer.ensure()

    assert seen_profiles == ["win-x64-base"]
    marker = json.loads(
        (installer.paths.runtime_root / ".installed.json").read_text(encoding="utf-8")
    )
    assert "document_parsing" not in marker["component_ids"]
    snapshot = installer.maintenance_snapshot()
    # 显式空集回显 requested=[]（base-only），effective 为 base 闭包。
    assert snapshot["requested_component_ids"] == []
    assert "document_parsing" not in snapshot["effective_component_ids"]
    # base-only 缺 full 可选组件不算漂移：inspect 仍 ready。
    assert installer.inspect().integrity == "verified"


@pytest.mark.parametrize(
    ("install_component_ids", "expected_profile", "expected_ocr_import"),
    [
        ((), "win-x64-base", "rapidocr"),
        (("document_parsing",), "win-x64-cpu", "rapidocr"),
    ],
)
def test_install_probe_uses_actual_install_scope_profile(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    install_component_ids: tuple[str, ...],
    expected_profile: str,
    expected_ocr_import: str,
) -> None:
    manifest, component = _release(tmp_path / "release")

    def probe(
        runtime_root: Path,
        component_ids: tuple[str, ...],
        profile_id: str,
    ) -> dict[str, bool]:
        if runtime_root.name != "runtime.installing":
            return dict.fromkeys(component_ids, True)
        return {
            component_id: (
                runtime_component_binding(profile_id, component_id).import_name
                == expected_ocr_import
                if component_id == "rapidocr-base"
                else profile_id == expected_profile
            )
            for component_id in component_ids
        }

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        component_probe=probe,
        install_component_ids=install_component_ids,
    )

    assert installer.ensure() is not None


def test_repair_preserves_trusted_base_only_closure_when_runtime_is_incomplete(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    installed_profiles: list[str] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        installed_profiles.append(profile)
        return _fake_install(partial, runtime_manifest, profile)

    base_only = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=(),
    )
    base_only.ensure()
    Path(base_only._launch().python_executable).unlink()

    repair = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
    )
    repair.repair()

    assert installed_profiles == ["win-x64-base", "win-x64-base"]
    marker = json.loads(
        (repair.paths.runtime_root / ".installed.json").read_text(encoding="utf-8")
    )
    assert "document_parsing" not in marker["component_ids"]


def test_repair_fails_closed_when_installed_marker_is_untrusted(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    initial = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
    )
    initial.ensure()
    marker_path = initial.paths.runtime_root / ".installed.json"
    marker = json.loads(marker_path.read_text(encoding="utf-8"))
    marker["manifest_sha256"] = "f" * 64
    marker_path.write_text(json.dumps(marker), encoding="utf-8")

    repair = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
    )

    with pytest.raises(RuntimeInstallError, match="untrusted installed marker"):
        repair.repair()


def test_ensure_with_optional_components_reports_independent_closure(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest, component = _release(tmp_path / "release")
    seen_profiles: list[str] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        seen_profiles.append(profile)
        return _fake_install(partial, runtime_manifest, profile)

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=("document_parsing",),
    )
    installer.ensure()

    # 独立 MinerU scope 不再因为旧 full lock 强装 Paddle。
    assert seen_profiles == ["win-x64-cpu"]
    snapshot = installer.maintenance_snapshot()
    assert snapshot["requested_component_ids"] == ["mineru-cpu"]
    assert snapshot["effective_component_ids"] == [
        "rapidocr-base",
        "mineru-cpu",
        "runtime_host",
    ]


def test_ensure_reinstalls_when_scope_changes(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    calls: list[Path] = []

    def install(partial: Path, runtime_manifest, profile: str) -> Path:
        calls.append(partial)
        return _fake_install(partial, runtime_manifest, profile)

    base_only = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=(),
    )
    base_only.ensure()

    # 范围从 base-only 扩到缺省全量：marker 闭包不同 → 重装一次。
    default_scope = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=install,
        install_component_ids=("paddleocr-cpu", "mineru-cpu"),
    )
    default_scope.ensure()
    assert len(calls) == 2

    # 幂等：同范围再次 ensure 不重装。
    default_scope.ensure()
    assert len(calls) == 2


def test_startup_selection_is_base_only_without_an_installed_marker(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        install_runner=_fake_install,
    )

    inspection = installer.inspect_snapshot(emit=False)

    assert inspection.state.startup_install_component_ids == ()
    assert inspection.state.integrity == "not-installed"


def test_startup_selection_preserves_paddle_across_manifest_change(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    product = tmp_path / "product"

    def probe(_root: Path, ids: tuple[str, ...], _profile: str) -> dict[str, bool]:
        return dict.fromkeys(ids, True)

    previous = RuntimeInstaller(
        product_root=product,
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        install_component_ids=("paddleocr-cuda",),
        install_runner=_fake_install,
        component_probe=probe,
    )
    previous.ensure()
    old_marker = json.loads(previous._marker().read_text(encoding="utf-8"))

    updated_manifest = json.loads(manifest.read_text(encoding="utf-8"))
    updated_manifest["product"]["source_sha"] = "1" * 40
    manifest.write_text(json.dumps(updated_manifest), encoding="utf-8")
    updated_lock = json.loads(component.read_text(encoding="utf-8"))
    updated_lock["product"]["source_sha"] = "1" * 40
    updated_lock["product"]["runtime_manifest_sha256"] = _sha(manifest.read_bytes())
    component.write_text(json.dumps(updated_lock), encoding="utf-8")

    current = RuntimeInstaller(
        product_root=product,
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        install_runner=_fake_install,
        component_probe=probe,
    )
    inspection = current.inspect_snapshot(emit=False)

    assert inspection.state.integrity == "not-installed"
    assert inspection.state.startup_install_component_ids == ("paddleocr-cuda",)
    selected = RuntimeInstaller(
        product_root=product,
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        install_component_ids=inspection.state.startup_install_component_ids,
        install_runner=_fake_install,
        component_probe=probe,
    )
    selected.ensure()
    marker = json.loads(selected._marker().read_text(encoding="utf-8"))
    assert marker["requested_component_ids"] == ["paddleocr-cuda"]
    assert "paddleocr-cuda" in marker["component_ids"]
    assert marker["manifest_sha256"] != old_marker["manifest_sha256"]
    rollback = json.loads(
        (product / "runtime.rollback" / ".installed.json").read_text(encoding="utf-8")
    )
    assert rollback["requested_component_ids"] == ["paddleocr-cuda"]


@pytest.mark.parametrize("invalid_marker", ["malformed", "unsupported_component"])
def test_startup_selection_rejects_unusable_prior_intent(
    tmp_path: Path, invalid_marker: str
) -> None:
    manifest, component = _release(tmp_path / "release")
    previous = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        install_component_ids=("paddleocr-cuda",),
        install_runner=_fake_install,
        component_probe=lambda _root, ids, _profile: dict.fromkeys(ids, True),
    )
    previous.ensure()
    marker_path = previous._marker()
    marker = json.loads(marker_path.read_text(encoding="utf-8"))
    if invalid_marker == "malformed":
        marker["component_ids"] = "paddleocr-cuda"
    else:
        marker["requested_component_ids"] = ["retired-cuda"]
    marker_path.write_text(json.dumps(marker), encoding="utf-8")

    current = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="nvidia_cuda",
        install_runner=_fake_install,
    )
    inspection = current.inspect_snapshot(emit=False)
    assert inspection.state.integrity == (
        "not-installed" if invalid_marker == "malformed" else "verified"
    )
    assert inspection.state.startup_install_component_ids is None
    with pytest.raises(RuntimeInstallError, match="installed marker|startup selection"):
        current.ensure()


def test_unknown_install_component_fails_closed(tmp_path: Path) -> None:
    from vibeocr.runtime.environments.runtime_selection import RuntimeSelectionError
    from vibeocr.runtime_contracts import ErrorCode

    manifest, component = _release(tmp_path / "release")
    with pytest.raises(RuntimeSelectionError) as excinfo:
        RuntimeInstaller(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest,
            accelerator="cpu",
            install_runner=_fake_install,
            install_component_ids=("not-a-component",),
        )
    assert excinfo.value.code is ErrorCode.RUNTIME_COMPONENT_UNKNOWN

    # gpu_runtime 只属于 nvidia_cuda 档位：component selection 不得隐式换档。
    with pytest.raises(RuntimeSelectionError):
        RuntimeInstaller(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest,
            accelerator="cpu",
            install_runner=_fake_install,
            install_component_ids=("gpu_runtime",),
        )


def test_maintenance_snapshot_echoes_download_source_intent(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    explicit = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        download_source_ids=("pypi",),
        install_component_ids=(),
    )
    explicit.ensure()
    snapshot = explicit.maintenance_snapshot()
    assert snapshot["requested_download_source_ids"] == ["pypi"]
    assert snapshot["effective_download_source_ids"] == ["pypi"]

    omitted = RuntimeInstaller(
        product_root=tmp_path / "product2",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_runner=_fake_install,
        install_component_ids=(),
    )
    omitted.ensure()
    snapshot = omitted.maintenance_snapshot()
    # 省略：requested 不出现，effective 解析为 Backend 缺省源。
    assert "requested_download_source_ids" not in snapshot
    assert snapshot["effective_download_source_ids"] == ["tuna-pypi"]


def test_base_only_ensure_probes_with_base_binding_not_plan_binding(
    tmp_path: Path,
) -> None:
    """base-only 安装的漂移探测必须用 base profile 的 import 绑定。

    回归：accelerator=cpu + 显式空安装范围时，已安装 ocr_engine 的绑定是
    RapidOCR；旧实现按 cpu plan 绑定 PaddleOCR 探测，会把刚装好的 base
    闭包整体判为漂移，ensure 最终以 "did not verify" 失败。
    """

    manifest, component = _release(tmp_path / "release")
    probe_profiles: list[str] = []

    def probe(
        _runtime_root: Path, component_ids: tuple[str, ...], profile_id: str
    ) -> dict[str, bool]:
        probe_profiles.append(profile_id)
        # 只按“绑定是否来自 base profile”判定：base 绑定（rapidocr）已装，
        # cpu plan 绑定（paddleocr）在 base-only 安装中不存在。
        return {
            component_id: profile_id == "win-x64-base" for component_id in component_ids
        }

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_component_ids=(),
        install_runner=_fake_install,
        component_probe=probe,
        operation_id="base-only-ensure",
    )

    launch = installer.ensure()

    assert launch is not None
    assert Path(launch.python_executable).is_file()
    assert probe_profiles
    # 漂移探测（非展示 payload）必须以 base 覆盖 profile 探测
    assert "win-x64-base" in probe_profiles
    assert installer._drifted_component_ids() == ()
    # 幂等复跑：base 闭包 ready，不触发重装
    assert installer.ensure() is not None


def test_full_scope_drift_still_probes_with_plan_binding(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """full（含可选组件）安装的漂移探测仍使用 accelerator 的 plan 绑定。"""

    manifest, component = _release(tmp_path / "release")
    probe_profiles: list[str] = []

    def probe(
        _runtime_root: Path, component_ids: tuple[str, ...], profile_id: str
    ) -> dict[str, bool]:
        probe_profiles.append(profile_id)
        return dict.fromkeys(component_ids, True)

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        accelerator="cpu",
        install_component_ids=("document_parsing",),
        install_runner=_fake_install,
        component_probe=probe,
        operation_id="full-ensure",
    )
    installer.ensure()

    assert installer._drifted_component_ids() == ()
    assert "win-x64-cpu" in probe_profiles


def test_drift_projection_uses_covering_profile_declared_versions(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 单测没有真实 Python runtime：组件 import 探测统一视为通过，
    # 使断言聚焦在“声明版本/分布绑定按哪个 profile 投影”。
    monkeypatch.setattr(
        runtime_maintenance,
        "_cached_runtime_component_probe",
        lambda _root, component_ids, **_kwargs: dict.fromkeys(component_ids, True),
    )
    """base-only 安装的版本比对必须使用稳定的 rapidocr-base 身份。"""

    manifest_path, component = _release(tmp_path / "release")
    document = json.loads(manifest_path.read_text(encoding="utf-8"))
    # 声明组件必须与 loader 派生的稳定 id 顺序一致，仅注入版本差异
    document["profiles"]["win-x64-base"]["components"] = [
        {
            "component_id": "rapidocr-base",
            "display_name": "RapidOCR base inference",
            "version": "3.9.2",
        },
        {"component_id": "runtime_host", "display_name": "Runtime HTTP host"},
    ]
    document["profiles"]["win-x64-cpu"]["components"] = [
        {
            "component_id": "rapidocr-base",
            "display_name": "RapidOCR base inference",
            "version": "3.9.2",
        },
        {
            "component_id": "paddleocr-cpu",
            "display_name": "PaddleOCR CPU inference",
            "version": "3.7.0",
        },
        {
            "component_id": "mineru-cpu",
            "display_name": "MinerU CPU document parsing",
        },
        {"component_id": "runtime_host", "display_name": "Runtime HTTP host"},
    ]
    manifest_path.write_text(
        json.dumps(document, sort_keys=True) + "\n", encoding="utf-8"
    )
    # 组件锁的 manifest 摘要必须跟随改写后的字节，保持绑定校验成立
    lock_document = json.loads(component.read_text(encoding="utf-8"))
    lock_document["product"]["runtime_manifest_sha256"] = _sha(
        manifest_path.read_bytes()
    )
    component.write_text(
        json.dumps(lock_document, sort_keys=True) + "\n", encoding="utf-8"
    )
    loaded = load_runtime_manifest(manifest_path)

    runtime_root = tmp_path / "product" / "runtime"
    dist_info = runtime_root / "Lib" / "site-packages" / "rapidocr-3.9.2.dist-info"
    dist_info.mkdir(parents=True)
    (dist_info / "METADATA").write_text(
        "Metadata-Version: 2.1\nName: rapidocr\nVersion: 3.9.2\n",
        encoding="utf-8",
    )
    (runtime_root / "python.exe").write_bytes(b"python")
    (runtime_root / ".installed.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "backend_version": loaded.backend_version,
                "manifest_sha256": loaded.sha256,
                "accelerator": "cpu",
                "component_ids": [
                    "rapidocr-base",
                    "runtime_host",
                ],
            }
        )
        + "\n",
        encoding="utf-8",
    )

    base_view = runtime_profile_status(
        loaded,
        accelerator="cpu",
        runtime_root=runtime_root,
        profile_id="win-x64-base",
    )
    # plan 视角必须显式请求：省略 profile_id 的默认投影按已安装闭包的
    # 覆盖 profile（此处为 win-x64-base）选择组件集与绑定。
    cpu_view = runtime_profile_status(
        loaded,
        accelerator="cpu",
        runtime_root=runtime_root,
        profile_id="win-x64-cpu",
    )
    default_view = runtime_profile_status(
        loaded,
        accelerator="cpu",
        runtime_root=runtime_root,
    )
    assert default_view["profile_id"] == "win-x64-base"
    base_states = {
        entry["component_id"]: entry["actual_state"]
        for entry in base_view["components"]
    }
    cpu_states = {
        entry["component_id"]: entry["actual_state"] for entry in cpu_view["components"]
    }

    assert base_states["rapidocr-base"] == "ready"
    assert base_states["runtime_host"] == "ready"
    assert cpu_states["rapidocr-base"] == "ready"
    assert cpu_states["paddleocr-cpu"] == "missing"

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest_path,
        accelerator="cpu",
        install_component_ids=(),
        install_runner=_fake_install,
        component_probe=lambda _root, ids, _profile: dict.fromkeys(ids, True),
    )
    assert installer._drifted_component_ids() == ()


@pytest.mark.parametrize(
    ("model_source_id", "expected_source_environment"),
    [
        (
            "huggingface",
            {
                "PADDLE_PDX_MODEL_SOURCE": "huggingface",
                "MINERU_MODEL_SOURCE": "huggingface",
            },
        ),
        (
            "modelscope",
            {
                "PADDLE_PDX_MODEL_SOURCE": "modelscope",
                "MINERU_MODEL_SOURCE": "modelscope",
            },
        ),
    ],
)
def test_document_parsing_ensure_passes_selected_model_source_to_native_clients(
    tmp_path: Path,
    model_source_id: str,
    expected_source_environment: dict[str, str],
) -> None:
    """Runtime control 仅把选择投影为上游 downloader 的环境变量。"""
    manifest, component = _release(tmp_path / "release")
    product = tmp_path / "product"
    control = RuntimeControl.from_installer_factory(
        lambda **kwargs: RuntimeInstaller(
            product_root=product,
            component_lock=component,
            runtime_manifest=manifest,
            accelerator="cpu",
            component_probe=lambda _root, ids, _profile_id: dict.fromkeys(ids, True),
            install_runner=_fake_install,
            **kwargs,
        )
    )

    result = control.execute_with_result(
        operation="ensure",
        install_component_ids=("document_parsing",),
        download_source_ids=(model_source_id,),
    )
    launch = result.launch

    assert launch is not None
    snapshot = result.receipt["snapshot"]
    assert snapshot["requested_download_source_ids"] == [model_source_id]
    assert snapshot["effective_download_source_ids"] == [
        "tuna-pypi",
        model_source_id,
    ]
    state_root = Path(launch.environment["VIBEOCR_RUNTIME_STATE_ROOT"])
    assert Path(launch.model_root) == state_root / "models"
    assert Path(launch.model_root).is_dir()
    assert Path(launch.environment["HF_HOME"]).is_relative_to(state_root)
    assert Path(launch.environment["MODELSCOPE_CACHE"]).is_relative_to(state_root)
    assert Path(launch.environment["MODELSCOPE_HOME"]).is_relative_to(state_root)
    assert (
        launch.environment["MODELSCOPE_CREDENTIALS_PATH"]
        == (launch.environment["MODELSCOPE_HOME"])
    )
    shared_models = Path(launch.environment["VIBEOCR_SHARED_MODEL_CACHE"])
    assert shared_models == state_root / "model-cache"
    assert Path(launch.environment["PADDLE_PDX_CACHE_HOME"]).is_relative_to(state_root)
    assert launch.environment["PIP_INDEX_URL"] == (
        "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/"
    )
    mineru_config = Path(launch.environment["MINERU_HOME"]) / "config.yaml"
    assert mineru_config == state_root / "mineru4" / "config.yaml"
    assert mineru_config.parent.is_dir()
    assert not mineru_config.exists()
    assert {
        key: launch.environment[key] for key in expected_source_environment
    } == expected_source_environment
    assert "VIBEOCR_MODEL_ROOT" not in launch.environment
    assert "VIBEOCR_RESOLVED_MODELS" not in launch.environment


class _MeasuredRecorder:
    def __init__(self) -> None:
        self.calls: list[tuple[str, int, int | None]] = []
        self.codes: list[str] = []

    def advance_measured(
        self,
        *,
        phase: str,
        unit: str,
        current: int,
        total: int | None,
        message_code: str,
        component_id: str | None = None,
        estimated_remaining_seconds: int | None = None,
        message_args: dict[str, str] | None = None,
    ) -> None:
        self.calls.append((unit, current, total))

        self.codes.append(message_code)

    def check_cancelled(self) -> None:
        pass

    def record_activity(self) -> None:
        pass

    def heartbeat(self, **kwargs) -> None:
        pass


def _mock_downloads(monkeypatch, payloads, *, known_length=True, opened=None):
    from vibeocr.runtime.environments import runtime_installer as installer

    def respond(request):
        filename = request.url.path.rsplit("/", 1)[-1]
        payload = payloads[filename]
        headers = {"Content-Length": str(len(payload))} if known_length else {}
        if request.method == "HEAD":
            return httpx.Response(200, headers=headers)
        if opened is not None:
            opened.append(filename)

        class Body(httpx.AsyncByteStream):
            async def __aiter__(self):
                yield payload

        return httpx.Response(200, headers=headers, stream=Body())

    monkeypatch.setattr(
        installer,
        "_download_client",
        lambda: httpx.AsyncClient(transport=httpx.MockTransport(respond)),
    )


class TestOnlineArtifactDownload:
    def test_lock_allowed_hashes_parses_equals_and_direct_url_declarations(
        self, tmp_path: Path
    ) -> None:
        from vibeocr.runtime.environments import runtime_installer as installer

        lock = tmp_path / "requirements.lock"
        lock.write_text(
            "antlr4-python3-runtime==4.9.3 \\n"
            "    --hash=sha256:" + "a" * 64 + " \\n"
            "    --hash=sha256:" + "b" * 64 + "\n"
            "paddlepaddle-gpu @ https://example.invalid/paddle.whl \\n"
            "    --hash=sha256:" + "c" * 64 + "\n",
            encoding="utf-8",
        )
        allowed = installer._lock_allowed_hashes(lock)
        assert allowed["antlr4-python3-runtime"] == {"a" * 64, "b" * 64}
        assert allowed["paddlepaddle-gpu"] == {"c" * 64}

    def test_download_reports_byte_progress_and_verifies_hashes(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from vibeocr.runtime.environments import runtime_installer as installer

        payloads = {
            "alpha-1.0-py3-none-any.whl": b"a" * (5 * 1024 * 1024),
            "beta-2.0.tar.gz": b"b" * 1024,
        }
        artifacts = []
        for filename, payload in payloads.items():
            digest = hashlib.sha256(payload).hexdigest()
            artifacts.append(
                installer._ResolvedArtifact(
                    name=filename.split("-")[0],
                    url=f"https://example.invalid/{filename}",
                    sha256=digest,
                    filename=filename,
                )
            )
        allowed = {
            artifact.name: {hashlib.sha256(payloads[artifact.filename]).hexdigest()}
            for artifact in artifacts
        }
        opened: list[str] = []

        _mock_downloads(monkeypatch, payloads, opened=opened)
        reporter = _MeasuredRecorder()
        root = installer._download_resolved_artifacts(
            tuple(artifacts), allowed, tmp_path / "downloads", reporter
        )

        assert sorted(path.name for path in root.iterdir()) == sorted(payloads)
        assert ("bytes", 0, sum(map(len, payloads.values()))) in reporter.calls
        assert reporter.calls[-1][0] == "bytes"
        assert reporter.calls[-1][2] == sum(map(len, payloads.values()))
        # 已验证文件直接复用：第二次调用不再发起网络请求。
        opened.clear()
        reporter2 = _MeasuredRecorder()
        installer._download_resolved_artifacts(
            tuple(artifacts), allowed, root, reporter2
        )
        assert opened == []
        assert all(current == 0 for _, current, _ in reporter2.calls)
        assert reporter2.codes.count("runtime.download_cache_hit") == 2

    def test_download_fails_closed_on_hash_mismatch(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from vibeocr.runtime.environments import runtime_installer as installer

        payload = b"tampered-payload"
        artifact = installer._ResolvedArtifact(
            name="alpha",
            url="https://example.invalid/alpha-1.0.whl",
            sha256=None,
            filename="alpha-1.0.whl",
        )
        _mock_downloads(monkeypatch, {artifact.filename: payload}, known_length=False)
        with pytest.raises(RuntimeInstallError, match="hash mismatch"):
            installer._download_resolved_artifacts(
                (artifact,), {"alpha": {"0" * 64}}, tmp_path / "downloads", None
            )
        assert not list((tmp_path / "downloads").glob("*.part"))

    def test_download_omits_total_without_sizes(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from vibeocr.runtime.environments import runtime_installer as installer

        payload = b"payload"
        artifact = installer._ResolvedArtifact(
            name="alpha",
            url="https://example.invalid/alpha-1.0.whl",
            sha256=hashlib.sha256(payload).hexdigest(),
            filename="alpha-1.0.whl",
        )
        _mock_downloads(monkeypatch, {artifact.filename: payload}, known_length=False)
        reporter = _MeasuredRecorder()
        installer._download_resolved_artifacts(
            (artifact,),
            {"alpha": {hashlib.sha256(payload).hexdigest()}},
            tmp_path / "downloads",
            reporter,
        )
        assert reporter.calls[-1] == ("bytes", len(payload), None)
        assert all(
            unit == "bytes" and total is None for unit, _, total in reporter.calls
        )


def test_paddle_environment_installs_in_separate_interpreter_and_shared_cache(
    tmp_path, monkeypatch
):
    from dataclasses import replace

    from vibeocr.runtime.environments import runtime_installer as installer
    from vibeocr.runtime.environments.runtime_manifest import PaddleEnvironment

    manifest_path, _ = _release(tmp_path / "release", with_base_pack=False)
    manifest = load_runtime_manifest(manifest_path)
    paddle_lock = tmp_path / "paddle.lock"
    paddle_lock.write_text("paddlepaddle==3.3.1")
    scope = replace(
        manifest.profiles["win-x64-cpu"].scopes[0],
        paddle_environment=PaddleEnvironment(paddle_lock, "a" * 64),
    )
    root = tmp_path / "candidate"
    calls = []
    caches = []

    def extract(_archive, destination, **kwargs):
        destination.mkdir(parents=True, exist_ok=True)
        (destination / "python.exe").write_bytes(b"python")

    def run(command, **kwargs):
        calls.append(command)

    def prepare(python, lock, endpoint, cache, reporter, env):
        caches.append(cache)
        return tmp_path

    monkeypatch.setattr(installer, "_extract_python_archive", extract)
    monkeypatch.setattr(installer, "_run_install_command", run)
    monkeypatch.setattr(installer, "_prepare_online_artifacts", prepare)
    monkeypatch.setattr(
        installer,
        "_local_install_requirements",
        lambda lock, *args: lock.with_suffix(".local.txt"),
    )
    installer._default_install_runner(root, manifest, scope, _pypi_source())
    checks = [c for c in calls if "check" in c and "pip" in c]
    assert [c[c.index("--python") + 1] for c in checks] == [
        str(root / "python.exe"),
        str(root / "engines/paddle/python.exe"),
    ]
    assert caches[0] == caches[1]
    paddle_install = next(
        c for c in calls if str(paddle_lock.with_suffix(".local.txt")) in c
    )
    assert paddle_install[paddle_install.index("--python") + 1] == str(
        root / "engines/paddle/python.exe"
    )
    assert str(scope.lock_path) not in paddle_install


def test_different_scope_locks_reuse_verified_download_without_second_request(
    tmp_path, monkeypatch
):
    from vibeocr.runtime.environments import runtime_installer as installer

    payload = b"same-artifact-used-by-both-environments"
    digest = hashlib.sha256(payload).hexdigest()
    artifact = installer._ResolvedArtifact(
        "shared",
        "https://example.invalid/shared.whl",
        digest,
        "shared-1.0-py3-none-any.whl",
    )
    locks = [tmp_path / "paddle.lock", tmp_path / "mineru.lock"]
    for lock in locks:
        lock.write_text(f"shared==1.0 --hash=sha256:{digest}\n")
    monkeypatch.setattr(
        installer, "_resolve_online_report", lambda *args: tmp_path / "report.json"
    )
    monkeypatch.setattr(installer, "_parse_resolve_report", lambda *args: (artifact,))
    requests = []
    _mock_downloads(monkeypatch, {"shared.whl": payload}, opened=requests)
    roots = [
        installer._prepare_online_artifacts(
            Path("python"),
            lock,
            "https://example.invalid",
            tmp_path / "cache",
            None,
            {},
        )
        for lock in locks
    ]
    assert roots[0] == roots[1]
    assert len(requests) == 1
    (roots[0] / artifact.filename).write_bytes(b"corrupt")
    installer._prepare_online_artifacts(
        Path("python"),
        locks[1],
        "https://example.invalid",
        tmp_path / "cache",
        None,
        {},
    )
    assert len(requests) == 2


def test_resolve_report_accepts_only_files_in_bound_download_cache(tmp_path):
    from vibeocr.runtime.environments import runtime_installer as installer

    cache = tmp_path / "cache"
    cache.mkdir()
    wheel = cache / "shared-1-py3-none-any.whl"
    wheel.write_bytes(b"artifact")
    report = tmp_path / "report.json"
    report.write_text(
        json.dumps(
            {
                "install": [
                    {
                        "metadata": {"name": "shared"},
                        "download_info": {"url": wheel.as_uri()},
                    }
                ]
            }
        )
    )
    assert installer._parse_resolve_report(report, cache)[0].filename == wheel.name
    with pytest.raises(RuntimeInstallError, match="outside download cache"):
        installer._parse_resolve_report(report, tmp_path / "other")
    with pytest.raises(RuntimeInstallError, match="outside download cache"):
        installer._parse_resolve_report(report)


@pytest.mark.parametrize("component", ["paddleocr-cuda", "gpu_runtime", "mineru-cuda"])
def test_gpu_probe_fails_when_import_works_but_device_is_unavailable(
    tmp_path, monkeypatch, component
):
    import contextlib
    import importlib
    import io
    import sys
    from types import SimpleNamespace

    module = SimpleNamespace(
        device=SimpleNamespace(is_compiled_with_cuda=lambda: False),
        cuda=SimpleNamespace(is_available=lambda: False),
    )

    def run(command, **kwargs):
        output = io.StringIO()
        with monkeypatch.context() as patch, contextlib.redirect_stdout(output):
            patch.setattr(importlib, "import_module", lambda name: module)
            patch.setattr(sys, "argv", ["-c", command[-1]])
            exec(command[-2], {})
        return subprocess.CompletedProcess(
            command, 0, stdout=output.getvalue(), stderr=""
        )

    monkeypatch.setattr(runtime_maintenance.subprocess, "run", run)
    assert probe_runtime_components(
        tmp_path, (component,), profile_id="win-x64-cu126"
    ) == {component: False}


def test_paddle_only_cuda_status_uses_base_host_lock(tmp_path: Path) -> None:
    manifest_path, component_lock = _release(tmp_path / "release")
    payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    payload["capabilities"].append("runtime.install-plan.v1")
    payload["profiles"]["win-x64-cu126"]["components"] = [
        {
            **item.to_payload(),
            **({"version": "0.141.0"} if item.component_id == "runtime_host" else {}),
        }
        for item in load_runtime_manifest(manifest_path)
        .profiles["win-x64-cu126"]
        .components
    ]
    manifest_path.write_text(json.dumps(payload), encoding="utf-8")
    binding = json.loads(component_lock.read_text(encoding="utf-8"))
    binding["product"]["runtime_manifest_sha256"] = _sha(manifest_path.read_bytes())
    component_lock.write_text(json.dumps(binding), encoding="utf-8")

    def install(partial, manifest, profile):
        python = _fake_install(partial, manifest, profile)
        metadata = partial / "Lib/site-packages/fastapi-1.0.0.dist-info"
        metadata.mkdir(parents=True)
        (metadata / "METADATA").write_text(
            "Name: fastapi\nVersion: 1.0.0\n", encoding="utf-8"
        )
        return python

    installer = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component_lock,
        runtime_manifest=manifest_path,
        accelerator="nvidia_cuda",
        install_component_ids=("paddleocr-cuda",),
        install_runner=install,
    )
    installer.ensure()
    host = next(
        item
        for item in installer.profile_payload()["components"]
        if item["component_id"] == "runtime_host"
    )
    assert host["desired_version"] == "1.0.0"
    assert host["actual_state"] == "ready"
    metadata = (
        installer.paths.runtime_root
        / "Lib/site-packages/fastapi-1.0.0.dist-info/METADATA"
    )
    marker = installer._marker().read_bytes()
    replacement = RuntimeInstaller(
        product_root=tmp_path / "product",
        component_lock=component_lock,
        runtime_manifest=manifest_path,
        accelerator="nvidia_cuda",
        install_component_ids=("paddleocr-cuda", "mineru-cuda"),
        required_capabilities=("runtime.install-plan.v1",),
        install_runner=install,
    )
    with RuntimeStoreLock(replacement.paths.locks_root / "runtime-store.lock"):
        preview = replacement._preview_install_plan_locked()
    host_change = next(
        item for item in preview["components"] if item["component_id"] == "runtime_host"
    )
    assert host_change["action"] == "replace"
    assert host_change["dependency_state"] == "pending"
    with pytest.raises(RuntimeInstallError, match="candidate Runtime did not verify"):
        replacement.ensure()
    assert installer._marker().read_bytes() == marker
    assert not (tmp_path / "product/runtime.rollback").exists()
    metadata.write_text("Name: fastapi\nVersion: 0.141.0\n", encoding="utf-8")
    assert installer._drifted_component_ids() == ("runtime_host",)


@pytest.mark.parametrize("llama_available", [True, False])
def test_mineru_cpu_probe_uses_mineru4_llama_binding(
    tmp_path, monkeypatch, llama_available
):
    import contextlib
    import importlib
    from types import SimpleNamespace

    def import_module(name):
        if name in {"mineru", "onnxruntime"} or (
            name == "mineru_llama_cpp" and llama_available
        ):
            return SimpleNamespace()
        raise ImportError(name)

    def run(command, **kwargs):
        output = io.StringIO()
        with monkeypatch.context() as patch, contextlib.redirect_stdout(output):
            patch.setattr(sys, "argv", ["probe", command[-1]])
            patch.setattr(importlib, "import_module", import_module)
            exec(command[-2], {})
        return subprocess.CompletedProcess(
            command, 0, stdout=output.getvalue(), stderr=""
        )

    monkeypatch.setattr(runtime_maintenance.subprocess, "run", run)
    assert (
        probe_runtime_components(tmp_path, ("mineru-cpu",))["mineru-cpu"]
        is llama_available
    )
