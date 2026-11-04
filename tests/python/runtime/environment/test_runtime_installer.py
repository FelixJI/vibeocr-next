"""预构建运行时树（PR2）形态的 Runtime installer / ManagedEnvironmentStore 契约。

构建期装配的展开解释器+依赖闭包树随产品分发：运行时零 venv 创建、零 uv、
零安装事务。本文件覆盖：

- manifest 装载（runtime_env 绑定、installer 绑定、loader 历史兼容）；
- ManagedEnvironmentStore 的 product 环境注册/健康门/launch 投影；
- 历史 venv/legacy 记录的只读探测与切换语义；
- runtime-installer CLI 的 environment 白名单与错误信封。
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import zipfile
from pathlib import Path
from uuid import uuid4

import pytest
from vibeocr.runtime.environments import runtime_maintenance
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.managed_references import ManagedEnvironmentReferences
from vibeocr.runtime.environments.runtime_installer import (
    RuntimeInstallError,
    main,
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
    _atomic_json,
    probe_runtime_components,
    runtime_status_from_environment,
)
from vibeocr.runtime.environments.runtime_manifest import (
    ManifestError,
    load_runtime_manifest,
    validate_requirements_lock,
)

from scripts.build_runtime_manifest import build_runtime_manifest
from scripts.build_runtime_pack import write_runtime_env_manifest


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


_PYTHON_VERSION = "3.13.16"
_PYTHON_SOURCE_URL = (
    "https://github.com/astral-sh/python-build-standalone/releases/"
    "download/20261001/"
    "cpython-3.13.16+20261001-x86_64-pc-windows-msvc"
    "-install_only.tar.gz"
)


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


# ---------------------------------------------------------------------------
# 可执行引擎树模板：一次构建，多个 release 复制。
# 树内是真实 venv 解释器（-I 下 sys.prefix==树根）+ 统一闭包 dist-info 与
# 引擎导入桩，product 记录的真实探针（distributions + import probe）可
# 直接通过；需要失败注入的用例在复制出的树上改写桩文件。
# ---------------------------------------------------------------------------

_ENGINE_PACKAGES = (
    "fastapi",
    "paddleocr",
    "paddlex",
    "onnxruntime-directml",
    "mineru",
    "vibeocr-next-runtime",
)

_TEMPLATE_LOCK = threading.Lock()
_TEMPLATE_TREE: Path | None = None
_TEMPLATE_TREE_MANIFEST: Path | None = None


def _seed_engine_stubs(site: Path, *, hardware_adapters: str = "True") -> None:
    for name in _ENGINE_PACKAGES:
        metadata = site / f"{name}-1.0.dist-info"
        metadata.mkdir(parents=True, exist_ok=True)
        (metadata / "METADATA").write_text(
            f"Name: {name}\nVersion: 1.0\n", encoding="utf-8"
        )
    (site / "paddleocr").mkdir(parents=True, exist_ok=True)
    (site / "paddleocr" / "__init__.py").write_text(
        "class PaddleOCR:\n    pass\n", encoding="utf-8"
    )
    (site / "onnxruntime").mkdir(parents=True, exist_ok=True)
    (site / "onnxruntime" / "__init__.py").write_text(
        "def get_available_providers():\n"
        "    return ['DmlExecutionProvider', 'CPUExecutionProvider']\n",
        encoding="utf-8",
    )
    parser = site / "mineru" / "parser"
    parser.mkdir(parents=True, exist_ok=True)
    for package in (site / "mineru", parser):
        (package / "__init__.py").write_text("", encoding="utf-8")
    (parser / "api_server.py").write_text(
        "def create_app(*args, **kwargs):\n"
        "    raise AssertionError('probe must not initialize a server')\n",
        encoding="utf-8",
    )
    recognition = site / "vibeocr" / "runtime" / "recognition"
    recognition.mkdir(parents=True, exist_ok=True)
    for package in (
        site / "vibeocr",
        site / "vibeocr" / "runtime",
        recognition,
    ):
        (package / "__init__.py").write_text("", encoding="utf-8")
    (recognition / "directml_devices.py").write_text(
        f"def hardware_adapters():\n    return {hardware_adapters}\n",
        encoding="utf-8",
    )


def _engine_tree_template() -> tuple[Path, Path]:
    """返回 (模板树, 模板树清单)：进程内单例，测试只读复制。"""
    global _TEMPLATE_TREE, _TEMPLATE_TREE_MANIFEST
    with _TEMPLATE_LOCK:
        if _TEMPLATE_TREE is None or _TEMPLATE_TREE_MANIFEST is None:
            root = Path(tempfile.mkdtemp(prefix="vibeocr-engine-tree-"))
            tree = root / "runtime-env"
            subprocess.run(
                [sys._base_executable, "-m", "venv", "--without-pip", str(tree)],
                check=True,
            )
            # 预构建树契约：python.exe 位于树根（构建校验与产品布局都锚定它）。
            shutil.copyfile(
                tree / "Scripts" / "python.exe"
                if os.name == "nt"
                else tree / "bin" / "python",
                tree / "python.exe",
            )
            site = tree / "Lib" / "site-packages"
            _seed_engine_stubs(site)
            manifest = root / "runtime-env-manifest.json"
            write_runtime_env_manifest(tree, manifest)
            _TEMPLATE_TREE = tree
            _TEMPLATE_TREE_MANIFEST = manifest
        return _TEMPLATE_TREE, _TEMPLATE_TREE_MANIFEST


def _component_lock_for(manifest_path: Path) -> Path:
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
    component_path = manifest_path.parent / "component-lock.json"
    component_path.write_text(
        json.dumps(component, sort_keys=True) + "\n", encoding="utf-8"
    )
    return component_path


def _release(
    product_root: Path, *, paddle_model_pack: Path | None = None
) -> tuple[Path, Path]:
    """预构建运行时树形态的最小产品（PR2 默认形态）。

    manifest 以 runtime_env 绑定装配：backend 位于
    <product_root>/runtime/backend，树复制到 <product_root>/runtime/env/
    runtime-env（与 product_layout 的产品布局一致），树清单留在 backend
    侧；无 runtime wheel、无 python archive、无安装事务。
    """
    backend = product_root / "runtime" / "backend"
    backend.mkdir(parents=True)
    base_lock = backend / "requirements-win-x64-base.lock"
    base_lock.write_text(_lock_text("win-x64-base"), encoding="utf-8")
    paddle_lock = backend / "requirements-win-x64-paddle-ort-directml.lock"
    paddle_lock.write_text(_lock_text("win-x64-paddle-ort-directml"), encoding="utf-8")
    installer_executable = backend / "vibeocr-runtime-installer.exe"
    installer_executable.write_bytes(b"installer")
    tree, tree_manifest = _engine_tree_template()
    manifest_path = build_runtime_manifest(
        runtime_wheel=None,
        base_lock=base_lock,
        python_archive=None,
        python_version=_PYTHON_VERSION,
        python_source_url=_PYTHON_SOURCE_URL,
        installer_executable=installer_executable,
        version="0.7.0",
        source_commit="0" * 40,
        build_workflow="tests/python",
        output_dir=backend,
        capabilities=("ocr.recognition.v2",),
        paddle_onnx_locks={"directml": paddle_lock},
        runtime_env_tree=tree,
        runtime_env_manifest=tree_manifest,
        paddle_model_pack=paddle_model_pack,
    )
    # build 把树复制到 output_dir/runtime-env；产品布局把它放到 backend
    # 的兄弟 env 目录（manifest.path.parent.parent/"env"/binding.root）。
    product_env = product_root / "runtime" / "env" / "runtime-env"
    product_env.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(str(backend / "runtime-env"), str(product_env))
    return manifest_path, _component_lock_for(manifest_path)


def _legacy_release(root: Path) -> tuple[Path, Path]:
    """历史 wheel + python archive 形态：loader 只读兼容面（loader 校验用）。"""
    root.mkdir(parents=True)
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
    python_archive = root / "cpython-3.13.16-win_amd64-install_only.tar.gz"
    python_archive.write_bytes(b"python-archive")
    installer_executable = root / "vibeocr-runtime-installer.exe"
    installer_executable.write_bytes(b"installer")
    base_lock = root / "requirements-win-x64-base.lock"
    base_lock.write_text(_lock_text("win-x64-base"), encoding="utf-8")
    paddle_lock = root / "requirements-win-x64-paddle-ort-directml.lock"
    paddle_lock.write_text(_lock_text("win-x64-paddle-ort-directml"), encoding="utf-8")
    manifest_path = build_runtime_manifest(
        runtime_wheel=wheel,
        base_lock=base_lock,
        python_archive=python_archive,
        python_version=_PYTHON_VERSION,
        python_source_url=_PYTHON_SOURCE_URL,
        installer_executable=installer_executable,
        version="0.7.0",
        source_commit="0" * 40,
        build_workflow="tests/python",
        output_dir=root,
        capabilities=("ocr.recognition.v2",),
        paddle_onnx_locks={"directml": paddle_lock},
    )
    return manifest_path, _component_lock_for(manifest_path)


def _rewrite_installer_binding(release: Path, binding: dict[str, str]) -> Path:
    manifest_path = release / "runtime-manifest.json"
    document = json.loads(manifest_path.read_text(encoding="utf-8"))
    document["installer"] = binding
    manifest_path.write_text(
        json.dumps(document, sort_keys=True) + "\n", encoding="utf-8"
    )
    return manifest_path


def _write_venv_record(
    manager: ManagedEnvironmentStore, env_id: str, **overrides
) -> dict:
    """直接写入固定路径 venv 记录；配合 _materialize_venv 提供真实解释器。"""
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        record = {
            "id": env_id,
            "name": f"env-{env_id[:4]}",
            "revision": 1,
            "kind": "venv",
            "path": env_id,
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
            **overrides,
        }
        data["environments"][env_id] = record
        _atomic_json(manager._registry, data)
        return record


def _materialize_venv(
    manager: ManagedEnvironmentStore, env_id: str, *, bare: bool = False
) -> Path:
    """把引擎树模板复制为 environments/<id> 的历史 venv 布局（真实解释器）。

    ``bare=True`` 清空 site-packages：空环境（status=empty）记录的探针要求
    不含任何引擎/服务分发包。
    """
    tree, _manifest = _engine_tree_template()
    destination = manager.root / env_id
    shutil.copytree(tree, destination)
    if bare:
        shutil.rmtree(destination / "Lib" / "site-packages")
        (destination / "Lib" / "site-packages").mkdir()
    return destination


def _patch_venv_base_python(
    manager: ManagedEnvironmentStore, monkeypatch: pytest.MonkeyPatch
) -> None:
    """历史 venv 记录的 base 解释器门：模板 venv 的 base_prefix 是本机解释器。"""
    monkeypatch.setattr(
        manager, "_expected_base_python", lambda: Path(sys._base_executable)
    )


# ---------------------------------------------------------------------------
# installer 绑定与 manifest 装载
# ---------------------------------------------------------------------------


def test_installer_binding_resolves_bundle_sibling_and_product_layout(
    tmp_path: Path,
) -> None:
    """新形态直接绑定可执行文件：扁平 bundle 与规范产品布局都能校验。"""
    product = tmp_path / "product"
    manifest_path, _ = _release(product)
    release = manifest_path.parent
    loaded = load_runtime_manifest(manifest_path)
    assert loaded.installer.executable_name == "vibeocr-runtime-installer.exe"
    assert loaded.installer.archive_path is None
    assert loaded.installer.executable_sha256 == _sha(b"installer")

    relocated = tmp_path / "relocated"
    backend = relocated / "runtime" / "backend"
    backend.parent.mkdir(parents=True)
    shutil.copytree(release, backend)
    shutil.copytree(product / "runtime" / "env", relocated / "runtime" / "env")
    (backend / "vibeocr-runtime-installer.exe").unlink()
    installer_root = relocated / "runtime" / "installer"
    installer_root.mkdir()
    (installer_root / "vibeocr-runtime-installer.exe").write_bytes(b"installer")
    assert load_runtime_manifest(
        backend / "runtime-manifest.json"
    ).installer.executable_sha256 == _sha(b"installer")


@pytest.mark.parametrize("kind", ["missing", "tampered"])
def test_installer_binding_fails_closed_on_missing_or_tampered_executable(
    tmp_path: Path, kind: str
) -> None:
    product = tmp_path / "product"
    manifest_path, _ = _release(product)
    release = manifest_path.parent
    executable = release / "vibeocr-runtime-installer.exe"
    if kind == "missing":
        executable.unlink()
        expected = "Installer executable is missing"
    else:
        executable.write_bytes(b"tampered")
        expected = "Installer executable SHA-256 mismatch"
    with pytest.raises(ManifestError, match=expected):
        load_runtime_manifest(manifest_path)


def test_installer_binding_fails_closed_when_any_present_copy_is_tampered(
    tmp_path: Path,
) -> None:
    """双候选位中任一存在的副本被篡改都必须 fail closed，不得被另一个遮蔽。"""
    product = tmp_path / "product"
    release = _release(product)[0].parent
    relocated = tmp_path / "relocated"
    backend = relocated / "runtime" / "backend"
    backend.parent.mkdir(parents=True)
    shutil.copytree(release, backend)
    installer_root = relocated / "runtime" / "installer"
    installer_root.mkdir()
    (installer_root / "vibeocr-runtime-installer.exe").write_bytes(b"tampered")
    with pytest.raises(ManifestError, match="Installer executable SHA-256 mismatch"):
        load_runtime_manifest(backend / "runtime-manifest.json")


def test_installer_binding_rejects_mixed_shapes(tmp_path: Path) -> None:
    product = tmp_path / "product"
    manifest_path, _ = _release(product)
    release = manifest_path.parent
    _rewrite_installer_binding(
        release,
        {
            "executable": "vibeocr-runtime-installer.exe",
            "executable_sha256": _sha(b"installer"),
            "archive": "vibeocr-runtime-installer-v0.7.0-win-x64.zip",
        },
    )
    with pytest.raises(ManifestError, match="must not mix"):
        load_runtime_manifest(manifest_path)


def test_legacy_zip_installer_manifest_still_loads_and_fails_closed(
    tmp_path: Path,
) -> None:
    """历史单成员 zip 绑定继续可读可验：旧 manifest 的离线修复/回滚兼容。"""
    product = tmp_path / "product"
    manifest_path, _ = _release(product)
    release = manifest_path.parent
    (release / "vibeocr-runtime-installer.exe").unlink()
    legacy_archive = release / "vibeocr-runtime-installer-v0.7.0-win-x64.zip"
    with zipfile.ZipFile(legacy_archive, mode="w") as archive:
        archive.writestr(
            "runtime-installer/vibeocr-runtime-installer.exe", b"installer"
        )
    _rewrite_installer_binding(
        release,
        {
            "archive": legacy_archive.name,
            "sha256": _sha(legacy_archive.read_bytes()),
            "executable_path": "runtime-installer/vibeocr-runtime-installer.exe",
            "executable_sha256": _sha(b"installer"),
        },
    )
    loaded = load_runtime_manifest(manifest_path)
    assert loaded.installer.archive_path == legacy_archive

    # zip 内容被替换且 zip 摘要同步更新：可执行文件摘要不匹配仍必须失败。
    with zipfile.ZipFile(legacy_archive, mode="w") as archive:
        archive.writestr("runtime-installer/vibeocr-runtime-installer.exe", b"tampered")
    document = json.loads(manifest_path.read_text(encoding="utf-8"))
    document["installer"]["sha256"] = _sha(legacy_archive.read_bytes())
    manifest_path.write_text(
        json.dumps(document, sort_keys=True) + "\n", encoding="utf-8"
    )
    with pytest.raises(ManifestError, match="Installer executable SHA-256 mismatch"):
        load_runtime_manifest(manifest_path)


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
    assert release / "runtime-code" / "vibeocr" / "runtime" / "host" / "main.py"

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


def test_component_lock_capability_mismatch_is_rejected(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    data = json.loads(component.read_text(encoding="utf-8"))
    data["required_capabilities"].append("unknown.feature")
    component.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ManagedEnvironmentError, match="component lock"):
        ManagedEnvironmentStore(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest,
        )


def test_component_lock_runtime_manifest_mismatch_is_rejected(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    data = json.loads(component.read_text(encoding="utf-8"))
    data["product"]["runtime_manifest_sha256"] = "f" * 64
    component.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ManagedEnvironmentError, match="component lock"):
        ManagedEnvironmentStore(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest,
        )


# ---------------------------------------------------------------------------
# product 环境注册 / 健康门 / launch 投影
# ---------------------------------------------------------------------------


def _product_tree(manager: ManagedEnvironmentStore) -> Path:
    binding = manager.manifest.runtime_env
    assert binding is not None
    return manager.manifest.path.parent.parent / "env" / binding.root


def test_initialize_default_registers_bundled_product_environment(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    listed = manager.initialize_default()
    environment = listed["environments"][0]
    assert environment["id"] == "paddleocr-ort-directml"
    assert environment["name"] == "默认环境"
    assert environment["kind"] == "product"
    assert environment["status"] == "installed"
    assert environment["recipe"] == "paddleocr-ort-directml"
    assert environment["selected_device"] == "directml"
    assert environment["path"] == str(_product_tree(manager))
    assert listed["active_id"] == "paddleocr-ort-directml"
    assert listed["active_revision"] == 1
    # 真实探针：树内解释器 + dist-info/import 桩全部通过。
    assert environment["python_state"] == "ready"
    assert environment["dependency_state"] == "installed"
    assert environment["engine_state"] == "unverified"
    assert environment["operation_blocks"] == {
        "switch": None,
        "delete": "product_environment",
    }
    record = manager._read()["environments"]["paddleocr-ort-directml"]
    scope, _accelerator = manager._recipe("paddleocr-ort-directml")
    assert record["recipe_lock"] == scope.sha256

    # 幂等：重复注册不递增 active_revision，也不重复布置模型。
    again = manager.initialize_default()
    assert again["active_revision"] == 1
    assert len(again["environments"]) == 1


def test_initialize_default_fails_closed_without_runtime_env_binding(
    tmp_path: Path,
) -> None:
    """manifest 无 runtime_env 绑定（历史形态）时注册 fail closed，不重建。"""
    manifest, component = _legacy_release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    with pytest.raises(ManagedEnvironmentError, match="not bound by manifest"):
        manager.initialize_default()
    assert manager._read()["environments"] == {}


def test_initialize_default_fails_closed_when_tree_python_missing(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    tree = _product_tree(manager)
    (tree / "python.exe").unlink()
    (tree / "Scripts" / "python.exe").unlink()
    with pytest.raises(ManagedEnvironmentError, match="tree is missing"):
        manager.initialize_default()
    assert manager._read()["environments"] == {}


def test_product_probe_fails_closed_on_missing_tree_or_manifest(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    manager.initialize_default()
    record = manager._read()["environments"]["paddleocr-ort-directml"]
    tree = _product_tree(manager)

    binding = manager.manifest.runtime_env
    assert binding is not None
    binding.manifest_path.unlink()
    missing_manifest = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )._probe(record)
    assert missing_manifest["healthy"] is False
    assert missing_manifest["reason"] == "product_runtime_env_missing"

    tree_manifest = manifest.parent / "runtime-env-manifest.json"
    tree_manifest.write_bytes(b"{}")
    shutil.move(str(tree), str(tree.with_name("runtime-env-moved")))
    missing_tree = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )._probe(record)
    assert missing_tree["healthy"] is False
    assert missing_tree["reason"] == "product_runtime_env_missing"


_LEGACY_OPERATION = {
    "environment_revision": 1,
    "plan_id": "a" * 32,
    "recipe": "paddleocr-ort-directml",
    "phase": "failed",
    "reason_code": "unknown",
    "next_action": "preview_again",
    "detail": "fixture",
}


def test_product_record_registry_validation_is_strict(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    scope, _accelerator = manager._recipe("paddleocr-ort-directml")
    product_record = {
        "schema_version": 1,
        "active_id": "paddleocr-ort-directml",
        "active_revision": 1,
        "environments": {
            "paddleocr-ort-directml": {
                "id": "paddleocr-ort-directml",
                "name": "默认环境",
                "revision": 1,
                "kind": "product",
                "path": "env/runtime-env",
                "status": "installed",
                "python_version": manager.manifest.python.version,
                "abi": manager.manifest.python.abi,
                "recipe": "paddleocr-ort-directml",
                "recipe_lock": scope.sha256,
                "selected_device": "directml",
            }
        },
    }

    def rewrite(mutator) -> None:
        data = json.loads(json.dumps(product_record))
        mutator(data)
        manager._registry.parent.mkdir(parents=True, exist_ok=True)
        manager._registry.write_text(json.dumps(data), encoding="utf-8")

    # 固定形态记录可读。
    rewrite(lambda data: None)
    assert manager._read()["active_id"] == "paddleocr-ort-directml"

    # path 标记被改动 → 拒绝。
    rewrite(
        lambda data: data["environments"]["paddleocr-ort-directml"].update(
            {"path": "runtime-env"}
        )
    )
    with pytest.raises(ManagedEnvironmentError, match="product environment record"):
        manager._read()

    # recipe 偏离固定 slug → 拒绝。
    rewrite(
        lambda data: data["environments"]["paddleocr-ort-directml"].update(
            {"recipe": "paddleocr-ort-cpu"}
        )
    )
    with pytest.raises(ManagedEnvironmentError, match="product environment record"):
        manager._read()

    # product 记录携带安装证据 → 拒绝（安装证据只属于历史 venv 记录）。
    rewrite(
        lambda data: data["environments"]["paddleocr-ort-directml"].update(
            {"last_install_operation": _LEGACY_OPERATION}
        )
    )
    with pytest.raises(ManagedEnvironmentError, match="product environment record"):
        manager._read()

    # 非法 kind → 拒绝。
    rewrite(
        lambda data: data["environments"]["paddleocr-ort-directml"].update(
            {"kind": "bundled"}
        )
    )
    with pytest.raises(ManagedEnvironmentError, match="environment record"):
        manager._read()


def test_product_launch_projects_recipe_and_device_without_code_root(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    manager.initialize_default()
    record = manager._read()["environments"]["paddleocr-ort-directml"]
    tree = _product_tree(manager)
    python = str(manager._interpreter_python(tree))
    launch = manager._launch(record, python)
    environment = launch["environment"]
    assert launch["supervisor_module"] == "vibeocr.runtime.host.main"
    assert launch["python_executable"] == python
    assert environment["VIBEOCR_RUNTIME_ROOT"] == str(tree)
    assert environment["VIBEOCR_MANAGED_ENVIRONMENT_RECIPE"] == "paddleocr-ort-directml"
    assert environment["VIBEOCR_MANAGED_ENVIRONMENT_DEVICE"] == "directml"
    assert environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] == "True"
    # 预构建树自带完整 site-packages：入口统一 python -m，不再注入产品代码根。
    assert "VIBEOCR_PRODUCT_CODE_ROOT" not in environment


def test_product_tree_is_never_a_cleanup_target(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    manager.initialize_default()
    plan = manager.preview_cleanup()
    product_items = [
        item
        for item in plan["items"]
        if item.get("environment_id") == "paddleocr-ort-directml"
    ]
    assert product_items
    assert all(not item["can_clean"] for item in product_items)
    assert _product_tree(manager).is_dir()


# ---------------------------------------------------------------------------
# 历史 venv / legacy 记录的只读探测
# ---------------------------------------------------------------------------


def test_venv_record_probe_gates_fail_closed(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    record = _write_venv_record(manager, "a" * 32)
    # 无解释器：python_missing。
    probe = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    assert probe._probe(record)["reason"] == "python_missing"
    # 版本漂移：base_or_abi_changed，且不产生任何解释器解包副作用。
    drifted = _write_venv_record(manager, "b" * 32, python_version="3.13.0")
    _materialize_venv(manager, "b" * 32)
    assert manager._probe(drifted)["reason"] == "base_or_abi_changed"
    assert not (manager.root / "python-base").exists()


def test_legacy_probe_reuses_frontend_update_but_rejects_runtime_or_abi_change(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # legacy 记录的 runtime 根是 product_root/runtime：与预构建产品的
    # runtime/backend 布局分属不同产品目录。
    manifest, component = _release(tmp_path / "bundle")
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


def test_legacy_cuda_launch_preserves_marker_accelerator(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "bundle")
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
    manifest_path, _ = _legacy_release(tmp_path / "product")
    document = json.loads(manifest_path.read_text(encoding="utf-8"))
    document["product"]["version"] = "0.8.0"
    document["product"]["source_sha"] = "a" * 40
    document["runtime_wheel"] = "vibeocr_next_runtime-0.8.0-py3-none-any.whl"
    (manifest_path.parent / document["runtime_wheel"]).write_bytes(b"current wheel")
    manifest_path.write_text(json.dumps(document), encoding="utf-8")
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
    fastapi_metadata = root / "Lib/site-packages/fastapi-1.0.0.dist-info"
    fastapi_metadata.mkdir(parents=True)
    (fastapi_metadata / "METADATA").write_text(
        "Metadata-Version: 2.1\nName: fastapi\nVersion: 1.0.0\n", encoding="utf-8"
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
    from vibeocr.runtime.environments.runtime_maintenance import runtime_profile_status

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


def test_venv_record_registry_rejects_external_python(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    _write_venv_record(manager, "a" * 32)
    registry = manager.paths.state_root / "environments.json"
    value = json.loads(registry.read_text(encoding="utf-8"))
    value["environments"]["a" * 32]["path"] = str(Path(sys._base_executable))
    registry.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(ManagedEnvironmentError, match="outside managed store"):
        manager.list()


def test_named_revision_path_accepts_old_and_new_random_tails(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    base = _write_venv_record(manager, "a" * 32)
    for suffix in ("b" * 16, "b" * 32):
        record = {
            **base,
            "revision": 2,
            "path": f"{base['id']}/revisions/2-{suffix}",
        }
        assert manager._safe_path(record) == manager.root / record["path"]


def test_installed_switch_rejects_unverified_supervisor_health(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    record = _write_venv_record(
        manager, "installed", status="installed", recipe="paddleocr-ort-cpu"
    )
    python = str(manager._venv_python(manager._safe_path(record)))
    monkeypatch.setattr(
        manager,
        "_probe",
        lambda _record: {"healthy": True, "python": python, "reason": None},
    )
    prepared = manager.prepare_switch(record["id"])
    assert prepared["launch"]["python_executable"] == python
    with pytest.raises(ManagedEnvironmentError, match="not healthy"):
        manager.commit_switch(
            prepared, started_health={"port": 1, "instance_id": "not-running"}
        )
    assert manager.list()["active_id"] is None


def test_active_job_reference_blocks_switch_and_delete(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    first = _write_venv_record(manager, "running")
    second = _write_venv_record(manager, "target")
    python = str(manager._venv_python(manager._safe_path(first)))
    manager._probe = lambda record: {  # noqa: B010 - 测试内替换实例探针
        "healthy": True,
        "reason": None,
        "python": python,
    }
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
    finally:
        references.release(job_id)
    assert not (references.reference_root / f"{job_id}.lock").exists()
    manager.commit_switch(prepared)
    with pytest.raises(RuntimeLockTimeout, match="no longer active"):
        references.admit(str(uuid4()))


# ---------------------------------------------------------------------------
# 引擎探针（历史 venv 记录；product 记录同源复用）
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "failure,recipe,timeout",
    [
        ("CalledProcessError", "paddleocr-ort-directml", 60),
        ("TimeoutExpired", "paddleocr-ort-directml", 60),
    ],
)
def test_engine_import_probe_preserves_failure_kind_elapsed_and_stderr_tail(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    failure: str,
    recipe: str,
    timeout: int,
) -> None:
    from types import SimpleNamespace

    from vibeocr.runtime.environments import managed_environments

    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    root = _materialize_venv(manager, "probe-diagnostic")
    record = _write_venv_record(
        manager, "probe-diagnostic", status="installed", recipe=recipe
    )
    stderr = (
        "discarded diagnostic prefix" + "x" * 2200 + "\nnative import failed: 诊断尾部"
    )
    ticks = iter((10.0, 10.0 + timeout + 0.25))
    monkeypatch.setattr(
        managed_environments, "time", SimpleNamespace(monotonic=lambda: next(ticks))
    )

    def run(command, **kwargs):
        if "-B" in command:
            assert kwargs["timeout"] == timeout
            if failure == "TimeoutExpired":
                raise subprocess.TimeoutExpired(
                    command, timeout, stderr=stderr.encode()
                )
            raise subprocess.CalledProcessError(1, command, stderr=stderr)
        assert kwargs["timeout"] == 20
        return subprocess.CompletedProcess(
            command,
            0,
            stdout=json.dumps(
                {
                    "prefix": str(root),
                    "base_prefix": str(manager._expected_base_python().parent),
                    "version": [
                        int(part) for part in manager.manifest.python.version.split(".")
                    ],
                    "packages": [
                        "fastapi",
                        "paddleocr",
                        "paddlex",
                        "onnxruntime-directml",
                        "mineru",
                    ],
                }
            ),
        )

    monkeypatch.setattr(managed_environments.subprocess, "run", run)
    result = manager._probe(record)
    assert result["healthy"] is False
    assert result["reason"] == "engine_import_failed"
    assert result["detail"].startswith(f"{failure} after {timeout}.2s:")
    assert result["detail"].endswith("\n" + stderr[-2000:])
    assert "discarded diagnostic prefix" not in result["detail"]


def test_venv_engine_probe_fails_closed_on_broken_native_import(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """已安装记录的引擎 import 闭包损坏：list/prepare_switch 同一探针 fail closed。"""
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    _patch_venv_base_python(manager, monkeypatch)
    root = _materialize_venv(manager, "native")
    record = _write_venv_record(
        manager, "native", status="installed", recipe="paddleocr-ort-directml"
    )
    (root / "Lib" / "site-packages" / "onnxruntime" / "__init__.py").write_text(
        "raise ImportError('native extension cannot load')\n", encoding="utf-8"
    )
    probe = manager._probe(record)
    assert probe["healthy"] is False
    assert probe["reason"] == "engine_import_failed"
    assert "native extension cannot load" in probe["detail"]
    unavailable = next(
        item for item in manager.list()["environments"] if item["id"] == record["id"]
    )
    assert unavailable["python_state"] == "ready"
    assert unavailable["dependency_state"] == "installed"
    assert unavailable["engine_state"] == "unavailable"
    assert "engine_import_failed" in unavailable["reason"]
    assert "shorter Portable location" in unavailable["reason"]
    with pytest.raises(ManagedEnvironmentError, match="engine_import_failed"):
        manager.prepare_switch(record["id"])


def test_venv_engine_probe_imports_real_transitive_closure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Paddle 惰性导入的真实符号闭包失败必须被探针发现；不实例化引擎。"""
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    _patch_venv_base_python(manager, monkeypatch)
    root = _materialize_venv(manager, "closure")
    record = _write_venv_record(
        manager, "closure", status="installed", recipe="paddleocr-ort-directml"
    )
    site = root / "Lib" / "site-packages"
    for name in ("shapely",):
        metadata = site / f"{name}-1.0.dist-info"
        metadata.mkdir(parents=True, exist_ok=True)
        (metadata / "METADATA").write_text(
            f"Name: {name}\nVersion: 1.0\n", encoding="utf-8"
        )
    # 旧探针的叶子依赖必须健康：契约只在 paddleocr 真实导入闭包上失败。
    for name in ("pyclipper",):
        (site / name).mkdir(parents=True, exist_ok=True)
        (site / name / "__init__.py").write_text("", encoding="utf-8")
    (site / "paddleocr" / "__init__.py").write_text(
        "from importlib import import_module\n"
        "_LAZY_IMPORTS = {'PaddleOCR': 'paddleocr.main'}\n"
        "def __getattr__(name):\n"
        "    if name in _LAZY_IMPORTS:\n"
        "        return getattr(import_module(_LAZY_IMPORTS[name]), name)\n"
        "    raise AttributeError(name)\n",
        encoding="utf-8",
    )
    (site / "paddleocr" / "main.py").write_text(
        "from paddleocr.ch_ppocr_det import TextDetector\n"
        "class PaddleOCR:\n"
        "    def __init__(self, *args, **kwargs):\n"
        "        raise AssertionError('probe must not instantiate the engine')\n",
        encoding="utf-8",
    )
    det = site / "paddleocr" / "ch_ppocr_det"
    det.mkdir(parents=True, exist_ok=True)
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
    (site / "shapely").mkdir(parents=True, exist_ok=True)
    (site / "shapely" / "__init__.py").write_text(
        "from shapely.lib import GEOSException\n", encoding="utf-8"
    )
    (site / "shapely" / "lib.py").write_text(
        "raise ImportError('DLL load failed while importing lib')\n",
        encoding="utf-8",
    )
    (site / "shapely" / "geometry.py").write_text("", encoding="utf-8")

    result = manager._probe(record)
    assert result["healthy"] is False
    assert result["reason"] == "engine_import_failed"
    assert "DLL load failed" in result["detail"]
    with pytest.raises(ManagedEnvironmentError, match="engine_import_failed"):
        manager.prepare_switch(record["id"])


def test_mineru_probe_imports_server_binding_without_starting_or_downloading(
    tmp_path, monkeypatch
):
    """统一闭包探针覆盖随包 MinerU 的 server 绑定：不启动、不下载。"""
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    _patch_venv_base_python(manager, monkeypatch)
    root = _materialize_venv(manager, "mineru")
    record = _write_venv_record(
        manager, "mineru", status="installed", recipe="paddleocr-ort-directml"
    )
    site = root / "Lib" / "site-packages"
    private_home = manager.paths.state_root / "environments" / record["id"] / "mineru4"
    monkeypatch.setenv("MINERU_HOME", str(tmp_path / "unrelated"))
    (site / "mineru" / "parser" / "api_server.py").write_text(
        "import os\n"
        f"assert os.environ['MINERU_HOME'] == {str(private_home)!r}\n"
        "def create_app(*args, **kwargs):\n"
        "    raise AssertionError('probe must not initialize models or a server')\n"
        "if __name__ == '__main__':\n"
        "    raise AssertionError('probe must not run the server')\n",
        encoding="utf-8",
    )
    assert manager._probe(record)["healthy"] is True
    # 包级导入仍成功，但真实 server 闭包损坏必须报告失败。
    (site / "mineru" / "parser" / "api_server.py").write_text(
        "raise ImportError('server dependency missing')\n", encoding="utf-8"
    )
    assert manager._probe(record)["reason"] == "engine_import_failed"
    assert not private_home.exists()


def test_unified_probe_cpu_device_does_not_require_gpu_adapter(tmp_path, monkeypatch):
    """统一闭包选 CPU 设备时健康探针不得要求 DXGI 适配器。

    无 D3D12 适配器的 Windows 虚拟机必须能以 CPU 设备使用闭包内的
    CPUExecutionProvider；GPU 硬件检查只在选择 directml 设备时执行。"""
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    _patch_venv_base_python(manager, monkeypatch)
    root = _materialize_venv(manager, "cpu-only")
    record = _write_venv_record(
        manager,
        "cpu-only",
        status="installed",
        recipe="paddleocr-ort-directml",
        selected_device="cpu",
    )
    site = root / "Lib" / "site-packages"
    # 无 DXGI 适配器的机器（Windows 虚拟机等）。
    (site / "vibeocr" / "runtime" / "recognition" / "directml_devices.py").write_text(
        "def hardware_adapters():\n    return []\n", encoding="utf-8"
    )

    # 旧实现无条件断言 hardware_adapters() 非空：无适配器机器上 CPU 设备
    # 也会被误判 engine_import_failed。
    assert manager._probe(record)["healthy"] is True
    # 同一闭包切回 directml 设备仍要求真实适配器，fail closed。
    assert manager._probe_device(record, "directml")["reason"] == "engine_import_failed"


def test_missing_model_does_not_erase_installed_dependencies(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    record = _write_venv_record(
        manager, "model-missing", status="installed", recipe="paddleocr-ort-directml"
    )
    result = manager._public_record(
        record, {"healthy": False, "reason": "engine_model_missing", "python": "python"}
    )
    assert result["dependency_state"] == "installed"
    assert result["model_state"] == "missing"
    assert result["engine_state"] == "unverified"
    assert result["service_state"] == "not_started"
    assert result["target_device"] == "directml" and result["actual_device"] is None


# ---------------------------------------------------------------------------
# 配方目录 / 列表投影 / CLI
# ---------------------------------------------------------------------------


def test_recipe_catalog_projects_manifest_bound_scopes(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    catalog = manager.recipe_catalog()
    assert manager.list()["recipes"] == catalog
    # 统一推理闭包：directml 是唯一可安装配方；历史原生 Paddle 与分设备
    # （CPU/CUDA/独立 MinerU）配方均已从可安装目录退役（记录保留）。
    assert [entry["id"] for entry in catalog] == ["paddleocr-ort-directml"]
    directml = catalog[0]
    assert directml["engine"] == "paddle"
    assert directml["family"] == "paddle-onnx"
    assert directml["provider"] == "DmlExecutionProvider"
    assert directml["configured_recognition_types"] == [
        "text",
        "table",
        "structure",
        "document",
    ]
    # 统一闭包是唯一可安装配方，也是远程 MinerU 模式的推荐宿主。
    assert [item["id"] for item in catalog if item["default_remote_host"]] == [
        "paddleocr-ort-directml"
    ]
    assert all(item["supports_remote_host"] for item in catalog)
    assert directml["display_name"] == "PaddleOCR ONNX + MinerU · Windows DirectML"
    assert directml["accelerator"] == "cpu"
    assert directml["target_device"] == "directml"
    # 目录同时投影设备全集：统一闭包可在同闭包 directml↔cpu 切换。
    assert directml["supported_devices"] == ["directml", "cpu"]
    assert directml["python_version"] == _PYTHON_VERSION
    assert directml["abi"] == "cp313"
    assert directml["platform"] == "win_amd64"
    assert directml["scope_id"] == "paddleocr-ort-directml"
    assert "paddleocr==3.7.0" in directml["dependencies"]
    assert "onnxruntime-directml==1.24.4" in directml["dependencies"]
    assert "mineru==4.1.3" in directml["dependencies"]
    assert directml["python_origin"] == "product_bundle"
    assert directml["runtime_wheel_origin"] == "product_bundle"
    # 退役配方不再宣称任何识别类型，且不可再解析安装 scope。
    for recipe in ("paddleocr-cuda", "paddleocr-ort-cpu", "mineru-cuda"):
        assert manager._recipe_modes(recipe) == []
        with pytest.raises(ManagedEnvironmentError, match="engine recipe is retired"):
            manager._recipe(recipe)


def test_managed_environment_list_projects_runtime_hardware_truth(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "product")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    # CUDA 支持退役后列表不再探测 NVIDIA 驱动（runtime_installer 已不
    # import subprocess）：list() 不得携带硬件/package 来源投影。
    import vibeocr.runtime.environments.runtime_installer as installer_module

    assert not hasattr(installer_module, "subprocess")
    payload = manager.list()
    assert "hardware" not in payload
    assert "package_source_ids" not in payload
    # 预构建树后 capabilities 不再包含安装进度。
    assert payload["capabilities"] == [
        "environment.cleanup.v1",
        "environment.switch-reservation.v1",
    ]


def test_frozen_manager_exposes_named_environment_list(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    manifest, component = _release(tmp_path / "product")
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
        "paddleocr-ort-directml",
    ]
    # CUDA 支持已退役：list 不再携带硬件投影；零安装后 capabilities 只剩
    # 清理与切换预留。
    assert response["result"] == {
        "capabilities": [
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
                "provider": {
                    "huggingface": "huggingface",
                    "modelscope": "modelscope",
                    "paddleocr-huggingface": "huggingface",
                    "paddleocr-modelscope": "modelscope",
                    "paddleocr-bos": "bos",
                    "mineru-huggingface": "huggingface",
                    "mineru-modelscope": "modelscope",
                }.get(source_id),
            }
            for source_id, kind, display_name, endpoint, is_default in (
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
        "environments": [],
    }


def test_cli_initialize_default_registers_product_environment(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    manifest, component = _release(tmp_path / "product")
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "initialize_default",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
    }
    assert main(["--request-json", json.dumps(request)]) == 0
    envelope = json.loads(capsys.readouterr().out)
    assert envelope["action"] == "initialize_default"
    assert envelope["result"]["active_id"] == "paddleocr-ort-directml"
    environment = envelope["result"]["environments"][0]
    assert environment["kind"] == "product"
    assert environment["python_state"] == "ready"


def test_cli_rejects_retired_request_kinds_and_actions(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    manifest, component = _release(tmp_path / "product")
    base = {
        "protocol_version": 2,
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
    }
    # request_kind 白名单：在线安装/maintenance 的 kind 已随预构建树退役。
    for request in (
        {"request_kind": "install_plan", "operation": "ensure", **base},
        {"request_kind": "start", "operation": "ensure", **base},
        {"request_kind": "environment", "action": "install", **base},
        {"request_kind": "environment", "action": "preview_install", **base},
        {"request_kind": "environment", "action": "prepare_target", **base},
        {"request_kind": "environment", "action": "find_compatible", **base},
        {"request_kind": "environment", "action": "repair_empty", **base},
    ):
        assert main(["--request-json", json.dumps(request)]) == 1, request
        envelope = json.loads(capsys.readouterr().out)
        assert envelope["ok"] is False
        assert envelope["error"]["code"] == "invalid_request"


def test_cli_rejects_retired_request_fields(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    manifest, component = _release(tmp_path / "product")
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "list",
        "name": "新环境",
        "recipe": "paddleocr-ort-directml",
        "accepted_event_streams": ["ndjson.v1"],
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
    }
    assert main(["--request-json", json.dumps(request)]) == 1
    envelope = json.loads(capsys.readouterr().out)
    assert envelope["ok"] is False
    assert envelope["error"]["code"] == "invalid_request"


def test_named_environment_error_envelope_redacts_private_details(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "product")

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
        "request_kind": "environment",
        "action": "list",
        "product_root": str(moved_root),
        "component_lock": str(moved_root / "component-lock.json"),
        "runtime_manifest": str(moved_root / "runtime-manifest.json"),
    }

    assert main(["--request-json", json.dumps(request, ensure_ascii=False)]) == 1

    stdout.flush()
    # stdout 被强制重配为 UTF-8：JSON 可解码，且本地路径按 [path] 脱敏。
    envelope = json.loads(output.getvalue().decode("utf-8"))
    assert envelope["ok"] is False
    assert "[path]" in envelope["error"]["message"]


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


# ---------------------------------------------------------------------------
# 组件探测（runtime_maintenance 保留面）
# ---------------------------------------------------------------------------


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


def test_runtime_status_ready_for_healthy_base_only_runtime(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """legacy 记录的启动投影：健康 base 闭包不得被 /v2/runtime/status 判 degraded。"""
    manifest, component = _release(tmp_path / "bundle")
    manager = ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
    )
    runtime = manager.paths.runtime_root
    runtime.mkdir(parents=True)
    (runtime / "python.exe").write_bytes(b"python")
    runtime_metadata = (
        runtime / "Lib" / "site-packages" / "vibeocr_next_runtime-0.7.0.dist-info"
    )
    runtime_metadata.mkdir(parents=True)
    (runtime_metadata / "METADATA").write_text(
        "Name: vibeocr-next-runtime\nVersion: 0.7.0\n", encoding="utf-8"
    )
    fastapi_metadata = runtime / "Lib" / "site-packages" / "fastapi-1.0.0.dist-info"
    fastapi_metadata.mkdir(parents=True)
    (fastapi_metadata / "METADATA").write_text(
        "Metadata-Version: 2.1\nName: fastapi\nVersion: 1.0.0\n", encoding="utf-8"
    )
    (runtime / ".installed.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "backend_version": manager.manifest.backend_version,
                "manifest_sha256": manager.manifest.sha256,
                "accelerator": "cpu",
                "component_ids": ["runtime_host"],
            }
        ),
        encoding="utf-8",
    )
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
        data["active_id"] = "legacy"
        data["active_revision"] = 1
        _atomic_json(manager._registry, data)
    launch = manager._launch(
        manager._read()["environments"]["legacy"], str(runtime / "python.exe")
    )
    for key, value in launch["environment"].items():
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
    assert statuses["runtime_host"]["actual_state"] == "ready"
    assert all(
        item["desired_state"] == "ready" for item in status["profile"]["components"]
    )


def test_drift_projection_uses_covering_profile_declared_versions(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """base-only 安装按 Runtime host 绑定比较版本，不能把 full MinerU 视为已安装。"""
    # 单测没有真实 Python runtime：组件 import 探测统一视为通过，
    # 使断言聚焦在“声明版本/分布绑定按哪个 profile 投影”。
    monkeypatch.setattr(
        runtime_maintenance,
        "_cached_runtime_component_probe",
        lambda _root, component_ids, **_kwargs: dict.fromkeys(component_ids, True),
    )
    from vibeocr.runtime.environments.runtime_maintenance import runtime_profile_status

    manifest_path, component = _legacy_release(tmp_path / "product")
    document = json.loads(manifest_path.read_text(encoding="utf-8"))
    # 声明组件必须与 loader 派生的稳定 id 顺序一致，仅注入版本差异
    document["profiles"]["win-x64-base"]["components"] = [
        {
            "component_id": "runtime_host",
            "display_name": "Runtime HTTP host",
            "version": "1.0.0",
        },
    ]
    legacy_profile = {
        "lock": document["profiles"]["win-x64-base"]["lock"],
        "sha256": document["profiles"]["win-x64-base"]["sha256"],
        "components": [
            {
                "component_id": "runtime_host",
                "display_name": "Runtime HTTP host",
                "version": "1.0.0",
            },
            {
                "component_id": "mineru-cpu",
                "display_name": "MinerU CPU document parsing",
                "version": "4.0.10",
            },
        ],
    }
    document["profiles"]["win-x64-cpu"] = legacy_profile
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
    dist_info = runtime_root / "Lib" / "site-packages" / "fastapi-1.0.0.dist-info"
    dist_info.mkdir(parents=True)
    (dist_info / "METADATA").write_text(
        "Metadata-Version: 2.1\nName: fastapi\nVersion: 1.0.0\n",
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

    assert base_states["runtime_host"] == "ready"
    assert cpu_states["runtime_host"] == "ready"
    assert cpu_states["mineru-cpu"] == "missing"


# ---------------------------------------------------------------------------
# 解析报告 / 加锁哈希（保留的下载校验工具）
# ---------------------------------------------------------------------------


def test_lock_allowed_hashes_parses_equals_and_direct_url_declarations(
    tmp_path: Path,
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
