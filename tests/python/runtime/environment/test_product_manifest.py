"""Current product binding and read-only historical manifest contracts."""

import hashlib
import io
import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest
from vibeocr.runtime.environments import runtime_maintenance
from vibeocr.runtime.environments.runtime_layout import (
    LayoutError,
    resolve_runtime_store,
)
from vibeocr.runtime.environments.runtime_lock import (
    RuntimeLockTimeout,
    RuntimeStoreLock,
)
from vibeocr.runtime.environments.runtime_maintenance import probe_runtime_components
from vibeocr.runtime.environments.runtime_manifest import (
    ManifestError,
    load_runtime_manifest,
    validate_requirements_lock,
)

from tests.runtime.test_product_layout import _packaged_release


def _release(product_root):
    manifest, lock = _packaged_release(product_root / "runtime/backend")
    shutil.copytree(
        manifest.parent / "runtime-env", product_root / "runtime/env/runtime-env"
    )
    return manifest, lock


def _legacy_release(root):
    manifest, lock = _packaged_release(root)
    value = json.loads(manifest.read_bytes())
    del value["runtime_env"]
    wheel = root / "vibeocr_next_runtime-0.7.0-py3-none-any.whl"
    archive = root / "cpython-3.13.16-win_amd64-install_only.tar.gz"
    wheel.write_bytes(b"historical-wheel-fixture")
    archive.write_bytes(b"historical-python-fixture")
    value.update(runtime_wheel=wheel.name, runtime_sha256=_sha(wheel.read_bytes()))
    value["python"].update(archive=archive.name, sha256=_sha(archive.read_bytes()))
    manifest.write_text(json.dumps(value), encoding="utf-8")
    return manifest, lock


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _lock_text(profile: str) -> str:
    base = "fastapi==1.0.0 \\\n    --hash=sha256:" + "1" * 64 + "\n"
    if profile == "win-x64-base":
        return base + (
            "winrt-runtime==3.2.1 \\\n    --hash=sha256:" + "6" * 64 + "\n"
            "winrt-windows-foundation==3.2.1 \\\n    --hash=sha256:" + "6" * 64 + "\n"
            "winrt-windows-foundation-collections==3.2.1 \\\n"
            "    --hash=sha256:" + "6" * 64 + "\n"
        )
    if profile.startswith("win-x64-paddle-ort-"):
        base += "".join(
            package + "==3.2.1 \\\n    --hash=sha256:" + "6" * 64 + "\n"
            for package in (
                "winrt-runtime",
                "winrt-windows-foundation",
                "winrt-windows-foundation-collections",
                "winrt-windows-globalization",
                "winrt-windows-graphics-imaging",
                "winrt-windows-media-ocr",
                "winrt-windows-storage-streams",
            )
        )
        ort = (
            "onnxruntime-directml==1.24.4"
            if profile.endswith("directml")
            else "onnxruntime==1.30.0"
        )
        # directml 是统一推理闭包：随包携带 MinerU（ONNX 线）与其模型格式
        # 依赖 onnx；cpu 历史锁不含 MinerU/onnx。CUDA 锁已随档位退役删除。
        unified = (
            "mineru==4.1.3 \\\n    --hash=sha256:" + "2" * 64 + "\n"
            "onnx==1.23.2 \\\n    --hash=sha256:" + "2" * 64 + "\n"
            if profile.endswith("directml")
            else ""
        )
        return base + (
            "paddleocr==3.7.0 \\\n    --hash=sha256:" + "7" * 64 + "\n"
            "paddlex==3.7.2 \\\n    --hash=sha256:" + "7" * 64 + "\n"
            "opencv-contrib-python==5.0.0.93 \\\n    --hash=sha256:"
            + "7" * 64
            + "\n"
            + ort
            + " \\\n    --hash=sha256:"
            + "7" * 64
            + "\n"
            + unified
        )
    raise AssertionError(f"unsupported fixture profile: {profile}")


def test_prebuilt_manifest_binds_runtime_env_tree(tmp_path: Path) -> None:
    """预构建形态：runtime_env 绑定真实树与确定性树清单，无 wheel/archive。"""
    product = tmp_path / "product"
    manifest_path, _ = _release(product)
    release = manifest_path.parent
    manifest = load_runtime_manifest(manifest_path)
    assert manifest.runtime_wheel is None
    assert manifest.runtime_sha256 is None
    assert manifest.python.archive_path is None
    assert manifest.python.sha256 is None
    binding = manifest.runtime_env
    assert binding is not None
    assert binding.root == "runtime-env"
    assert binding.manifest_name == "runtime-env-manifest.json"
    assert binding.manifest_path == release / "runtime-env-manifest.json"
    assert binding.manifest_sha256 == _sha(binding.manifest_path.read_bytes())
    tree = release.parent / "env" / "runtime-env"
    assert (tree / "python.exe").is_file()
    assert manifest.backend_version == "0.7.0"
    assert manifest.sha256 == _sha(manifest_path.read_bytes())
    assert set(manifest.profiles) == {"win-x64-base"}
    assert set(manifest.paddle_onnx_environments) == {"paddleocr-ort-directml"}
    # 全量校验（verify_artifacts=True）核对树清单摘要。
    load_runtime_manifest(manifest_path)


def test_prebuilt_manifest_rejects_tampered_tree_manifest(tmp_path: Path) -> None:
    product = tmp_path / "product"
    manifest_path, _ = _release(product)
    release = manifest_path.parent
    tree_manifest = release / "runtime-env-manifest.json"
    tree_manifest.write_bytes(b"tampered")
    with pytest.raises(ManifestError, match="runtime env manifest"):
        load_runtime_manifest(manifest_path).verify_artifacts()


def test_manifest_rejects_tampered_lock(tmp_path: Path) -> None:
    product = tmp_path / "product"
    manifest_path, _ = _release(product)
    release = manifest_path.parent
    (release / "requirements-win-x64-base.lock").write_text(
        "tampered", encoding="utf-8"
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
    lock = tmp_path / "requirements-win-x64-base.lock"
    lock.write_text(f"{directive}\n{_lock_text('win-x64-base')}", encoding="utf-8")

    with pytest.raises(ManifestError, match="source-neutral"):
        validate_requirements_lock(lock, profile="win-x64-base")


def test_manifest_rejects_old_schema(tmp_path: Path) -> None:
    product = tmp_path / "product"
    manifest_path, _ = _release(product)
    payload = json.loads(manifest_path.read_text(encoding="utf-8"))
    payload["schema_version"] = 1
    manifest_path.write_text(json.dumps(payload), encoding="utf-8")
    with pytest.raises(ManifestError, match="schema_version must be 2"):
        load_runtime_manifest(manifest_path)


def test_legacy_wheel_manifest_still_verifies_bound_artifacts(tmp_path: Path) -> None:
    """loader 历史兼容：wheel + python archive 绑定照常校验（只读面）。"""
    release = tmp_path / "release"
    manifest_path, _ = _legacy_release(release)
    manifest = load_runtime_manifest(manifest_path)
    assert manifest.runtime_wheel == "vibeocr_next_runtime-0.7.0-py3-none-any.whl"
    assert manifest.runtime_env is None

    (release / "vibeocr_next_runtime-0.7.0-py3-none-any.whl").write_bytes(b"tampered")
    with pytest.raises(ManifestError, match="Runtime wheel SHA-256 mismatch"):
        load_runtime_manifest(manifest_path)


def test_legacy_wheel_manifest_rejects_tampered_python_archive(tmp_path: Path) -> None:
    release = tmp_path / "release"
    manifest_path, _ = _legacy_release(release)
    (release / "cpython-3.13.16-win_amd64-install_only.tar.gz").write_bytes(b"tampered")
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


def test_base_component_probe_uses_runtime_host_binding(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    runtime_root = tmp_path / "runtime"
    python = runtime_root / "python.exe"
    python.parent.mkdir(parents=True)
    python.write_bytes(b"python")

    def run(command, **_kwargs):  # type: ignore[no-untyped-def]
        modules = json.loads(command[-1])
        result = {
            component_id: module == "fastapi"
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
        ("runtime_host",),
        profile_id="win-x64-base",
    ) == {"runtime_host": True}


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
