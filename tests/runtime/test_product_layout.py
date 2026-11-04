from __future__ import annotations

import hashlib
import json
import stat
import zipfile
from pathlib import Path
from types import SimpleNamespace

import pytest

from scripts.product_layout import (
    LAYOUT_RELATIVE_PATH,
    ProductLayoutError,
    load_product_layout,
    stage_product_layout,
)
from scripts.release_smoke import _verify_runtime_code
from tests.python.runtime.environment.test_runtime_installer import _release

# 历史隔离 host 锁（不含 Paddle/MinerU）：统一闭包后 cpu 档位锁已删除，
# 复用现存 base 锁内容满足同一 paddle_isolated 校验形态。
CPU_HOST_LOCK = (
    Path(__file__).parents[2]
    / "config/runtime/win-x64-base/requirements-win-x64-base.lock"
)


def _legacy_paddle_cpu_lock_text() -> str:
    """#270：仓库已删除旧原生 paddle 锁；历史 manifest 兼容路径用临时合成旧锁覆盖。"""
    return (
        "\n".join(
            [
                "opencv-contrib-python==4.12.0.88 " + chr(92),
                "    --hash=sha256:" + "7" * 64,
                "paddlepaddle==3.3.1 " + chr(92),
                "    --hash=sha256:" + "8" * 64,
            ]
        )
        + "\n"
    )


def _release_inputs(tmp_path: Path) -> dict[str, Path]:
    app = tmp_path / "app-publish"
    (app / "WebAssets").mkdir(parents=True)
    (app / "runtimes" / "win-x64" / "native").mkdir(parents=True)
    for relative in (
        "VibeOCR.WinUI.exe",
        "VibeOCR.WinUI.dll",
        "VibeOCR.WinUI.pri",
        "App.xbf",
        "MainWindow.xbf",
        "WebAssets/index.html",
        "runtimes/win-x64/native/WebView2Loader.dll",
    ):
        path = app / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(relative.encode())
    (app / "VibeOCR.WinUI.pdb").write_bytes(b"symbols")

    bootstrapper = tmp_path / "VibeOCR.Bootstrapper.exe"
    bootstrapper.write_bytes(b"launcher")
    (tmp_path / "Velopack.dll").write_bytes(b"velopack")
    (tmp_path / "VibeOCR.Bootstrapper.exe.config").write_text(
        "<configuration />", encoding="utf-8"
    )
    backend = tmp_path / "backend-release"
    manifest_path, component_lock = _release(
        backend,
        with_base_pack=True,
        with_paddle_packs=True,
        with_paddle_model_pack=True,
    )
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    identities = tmp_path / "component-identities.json"
    identities.write_text(
        json.dumps({"project": manifest["product"]}), encoding="utf-8"
    )
    license_file = tmp_path / "LICENSE"
    license_file.write_text("license", encoding="utf-8")
    changelog = tmp_path / "CHANGELOG.md"
    changelog.write_text("# Changes", encoding="utf-8")

    return {
        "app_publish_root": app,
        "bootstrapper_executable": bootstrapper,
        "component_lock": component_lock,
        "component_identities": identities,
        "backend_release_dir": backend,
        "license_file": license_file,
        "changelog_file": changelog,
    }


def test_stage_product_layout_builds_the_strict_public_tree(tmp_path: Path) -> None:
    product_root = tmp_path / "VibeOCR"

    layout = stage_product_layout(
        product_root=product_root, **_release_inputs(tmp_path)
    )

    assert {path.name for path in product_root.iterdir()} == {
        "VibeOCR.exe",
        "Velopack.dll",
        "LICENSE",
        "CHANGELOG.md",
        "app",
        "runtime",
    }
    assert layout.public_entry == product_root / "VibeOCR.exe"
    assert (product_root / "Velopack.dll").read_bytes() == b"velopack"
    assert layout.app_entry == product_root / "app" / "VibeOCR.WinUI.exe"
    assert (product_root / LAYOUT_RELATIVE_PATH).is_file()
    assert (product_root / "app/metadata/component-lock.json").is_file()
    assert (product_root / "app/metadata/component-identities.json").is_file()
    assert (product_root / "runtime/backend/runtime-manifest.json").is_file()
    assert (
        product_root / "runtime/backend/runtime-code/vibeocr/runtime/host/main.py"
    ).is_file()
    assert (product_root / "runtime/installer/vibeocr-runtime-installer.exe").is_file()
    # 产品只携带一份可执行安装器：backend 目录不再携带 zip/exe 双副本。
    assert (
        product_root / "runtime/installer/vibeocr-runtime-installer.exe"
    ).read_bytes() == b"installer"
    assert not list(
        (product_root / "runtime/backend").glob("vibeocr-runtime-installer*")
    )
    assert len(list(product_root.rglob("vibeocr-runtime-installer*"))) == 1
    _verify_runtime_code(product_root, "0.7.0")
    assert not list(product_root.rglob("*.pdb"))
    assert not list(product_root.rglob("*.exe.config"))
    with pytest.raises(ProductLayoutError, match="layout.missing-entry"):
        load_product_layout(product_root)


def test_stage_product_layout_accepts_relative_backend_source(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    inputs = _release_inputs(tmp_path)
    monkeypatch.chdir(tmp_path)
    inputs["backend_release_dir"] = Path("backend-release")
    product = tmp_path / "VibeOCR"

    stage_product_layout(product_root=product, **inputs)

    assert (
        product / "runtime/backend/runtime-code/vibeocr/runtime/host/main.py"
    ).is_file()


def test_stage_product_layout_embeds_only_the_release_bound_base_runtime_closure(
    tmp_path: Path,
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    runtime_manifest = json.loads(
        (backend / "runtime-manifest.json").read_text(encoding="utf-8")
    )
    original_base_pack = (
        backend / runtime_manifest["profiles"]["win-x64-base"]["runtime_pack"][0]
    )
    arbitrary_base_pack = backend / "offline-foundation.part-01.bundle"
    original_base_pack.replace(arbitrary_base_pack)
    runtime_manifest["profiles"]["win-x64-base"]["runtime_pack"] = [
        arbitrary_base_pack.name
    ]
    advanced_packs = {
        "future-win-x64-base-cpu-profile.pack": b"cpu-full",
        "vibeocr-runtime-pack-paddlex-future.zip": b"paddlex",
        "vibeocr-runtime-pack-mineru-future.zip": b"mineru",
    }
    for name, contents in advanced_packs.items():
        (backend / name).write_bytes(contents)
    runtime_manifest["profiles"]["win-x64-cpu"]["runtime_pack"] = [
        "future-win-x64-base-cpu-profile.pack"
    ]
    runtime_manifest["profiles"]["win-x64-cpu"]["runtime_pack_sha256"] = [
        hashlib.sha256(b"cpu-full").hexdigest()
    ]
    runtime_manifest["paddle_onnx_environments"]["directml"]["runtime_pack"] = [
        *runtime_manifest["paddle_onnx_environments"]["directml"]["runtime_pack"],
        "vibeocr-runtime-pack-paddlex-future.zip",
    ]
    runtime_manifest["paddle_onnx_environments"]["directml"]["runtime_pack_sha256"] = [
        *runtime_manifest["paddle_onnx_environments"]["directml"][
            "runtime_pack_sha256"
        ],
        hashlib.sha256(b"paddlex").hexdigest(),
    ]
    (backend / "runtime-manifest.json").write_text(
        json.dumps(runtime_manifest), encoding="utf-8"
    )
    (backend / "unrelated-training-corpus.bin").write_bytes(b"not-a-runtime-input")

    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **inputs)

    embedded = product_root / "runtime/backend"
    assert {path.name for path in embedded.iterdir()} == {
        "runtime-manifest.json",
        runtime_manifest["runtime_wheel"],
        runtime_manifest["python"]["archive"],
        runtime_manifest["profiles"]["win-x64-base"]["lock"],
        runtime_manifest["profiles"]["win-x64-cpu"]["lock"],
        runtime_manifest["paddle_onnx_environments"]["directml"]["lock"],
        *runtime_manifest["paddle_onnx_environments"]["directml"]["runtime_pack"],
        runtime_manifest["paddle_model_pack"]["archive"],
        "offline-foundation.part-01.bundle",
        "runtime-code",
    }


@pytest.mark.parametrize(
    "filename",
    (
        "C:backend.whl",
        "/backend.whl",
        "backend:debug.whl",
        "NUL.whl",
        "NUL .whl",
        "COM1",
        "COM¹.txt",
        "backend.whl.",
        "backend.whl ",
    ),
)
def test_stage_product_layout_rejects_nonportable_windows_release_filenames(
    tmp_path: Path,
    filename: str,
) -> None:
    inputs = _release_inputs(tmp_path)
    manifest_path = inputs["backend_release_dir"] / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["runtime_wheel"] = filename
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    with pytest.raises(
        ProductLayoutError,
        match="layout.invalid-descriptor: runtime_wheel must be a release filename",
    ):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


@pytest.mark.parametrize(
    ("owner", "runtime_pack"),
    (
        ("profile", "advanced-pack.zip"),
        ("profile", ["../advanced-pack.zip"]),
        ("scope", {"filename": "scope-pack.zip"}),
        ("scope", ["C:scope-pack.zip"]),
    ),
)
def test_stage_product_layout_validates_every_declared_runtime_pack(
    tmp_path: Path,
    owner: str,
    runtime_pack: object,
) -> None:
    inputs = _release_inputs(tmp_path)
    manifest_path = inputs["backend_release_dir"] / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if owner == "profile":
        manifest["profiles"]["win-x64-cpu"]["runtime_pack"] = runtime_pack
        pattern = r"layout.invalid-descriptor: profiles\.win-x64-cpu"
    else:
        manifest["paddle_onnx_environments"]["directml"]["runtime_pack"] = runtime_pack
        pattern = r"layout.invalid-descriptor: paddle_onnx_environments\.directml"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    with pytest.raises(ProductLayoutError, match=pattern):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


def test_stage_product_layout_rejects_reparse_point_closure_sources(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest = json.loads(
        (backend / "runtime-manifest.json").read_text(encoding="utf-8")
    )
    base_pack = backend / manifest["profiles"]["win-x64-base"]["runtime_pack"][0]
    original_lstat = Path.lstat

    def lstat_with_reparse_attribute(
        path: Path, *args: object, **kwargs: object
    ) -> object:
        result = original_lstat(path, *args, **kwargs)
        if path == base_pack:
            return SimpleNamespace(
                st_mode=result.st_mode,
                st_file_attributes=getattr(result, "st_file_attributes", 0)
                | getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
            )
        return result

    monkeypatch.setattr(Path, "lstat", lstat_with_reparse_attribute)

    with pytest.raises(
        ProductLayoutError,
        match=r"layout.invalid-path: backend product closure.*reparse",
    ):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


@pytest.mark.parametrize(
    ("source_kind", "error_code"),
    (
        ("missing", "layout.missing-entry"),
        ("directory", "layout.invalid-path"),
    ),
)
def test_stage_product_layout_rejects_non_file_closure_sources(
    tmp_path: Path,
    source_kind: str,
    error_code: str,
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest = json.loads(
        (backend / "runtime-manifest.json").read_text(encoding="utf-8")
    )
    base_pack = backend / manifest["profiles"]["win-x64-base"]["runtime_pack"][0]
    base_pack.unlink()
    if source_kind == "directory":
        base_pack.mkdir()

    with pytest.raises(ProductLayoutError, match=error_code):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


def test_load_product_layout_rejects_escape_and_root_clutter(tmp_path: Path) -> None:
    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **_release_inputs(tmp_path))
    descriptor = product_root / LAYOUT_RELATIVE_PATH
    value = json.loads(descriptor.read_text(encoding="utf-8"))
    value["app"]["entry"] = "../outside.exe"
    descriptor.write_text(json.dumps(value), encoding="utf-8")

    with pytest.raises(ProductLayoutError, match="layout.invalid-path"):
        load_product_layout(product_root)

    cluttered_root = tmp_path / "ClutteredVibeOCR"
    cluttered_inputs = tmp_path / "cluttered-inputs"
    cluttered_inputs.mkdir()
    stage_product_layout(
        product_root=cluttered_root,
        **_release_inputs(cluttered_inputs),
    )
    (cluttered_root / "unexpected.dll").write_bytes(b"clutter")
    with pytest.raises(ProductLayoutError, match="layout.root-conflict"):
        load_product_layout(cluttered_root)


def test_load_product_layout_accepts_velopack_version_marker(tmp_path: Path) -> None:
    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **_release_inputs(tmp_path))
    release_manifest = product_root / "app/metadata/product-release-manifest.json"
    release_manifest.write_text("{}", encoding="utf-8")
    (product_root / "sq.version").write_text("0.3.1", encoding="utf-8")

    layout = load_product_layout(product_root)

    assert layout.product_root == product_root.resolve()


def test_load_product_layout_rejects_safe_but_noncanonical_paths(
    tmp_path: Path,
) -> None:
    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **_release_inputs(tmp_path))
    descriptor = product_root / LAYOUT_RELATIVE_PATH
    value = json.loads(descriptor.read_text(encoding="utf-8"))
    alternate = product_root / "app/Alternate.exe"
    alternate.write_bytes((product_root / "app/VibeOCR.WinUI.exe").read_bytes())
    value["app"]["entry"] = "app/Alternate.exe"
    descriptor.write_text(json.dumps(value), encoding="utf-8")

    with pytest.raises(ProductLayoutError, match="layout.invalid-path: app.entry"):
        load_product_layout(product_root)


@pytest.mark.parametrize("present", [True, False])
@pytest.mark.parametrize("device", ["directml"])
def test_stage_requires_declared_paddle_onnx_pack_and_lock(
    tmp_path: Path, present: bool, device: str
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest_path = backend / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    binding = manifest["paddle_onnx_environments"][device]
    lock = backend / binding["lock"]
    pack = backend / binding["runtime_pack"][0]
    if not present:
        lock.unlink()
        with pytest.raises(ProductLayoutError, match="closure file is unavailable"):
            stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)
        return
    stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)
    assert (tmp_path / "VibeOCR/runtime/backend" / pack.name).is_file()


@pytest.mark.parametrize("device", ["directml"])
def test_stage_requires_offline_paddle_onnx_pack_binding(
    tmp_path: Path, device: str
) -> None:
    """统一推理闭包的 DirectML 离线 wheel 闭包必须随产品：缺失 pack 绑定
    fail closed（分设备 cpu/cuda pack 已退役，历史条目仅按声明通用校验）。"""
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest_path = backend / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["paddle_onnx_environments"][device].pop("runtime_pack")
    manifest["paddle_onnx_environments"][device].pop("runtime_pack_sha256")
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ProductLayoutError, match="runtime_pack must contain"):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


def test_stage_accepts_unified_directml_only_binding(tmp_path: Path) -> None:
    """正式新产物形态：paddle_onnx_environments 只绑定 directml 时 staging
    成功，不再要求退役的 cpu 分设备 pack。"""
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest_path = backend / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    ort = manifest["paddle_onnx_environments"]
    # CUDA 档位退役后 _release 只产 directml 绑定：默认形态即统一闭包。
    assert set(ort) == {"directml"}
    stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)
    staged = tmp_path / "VibeOCR/runtime/backend"
    assert (staged / ort["directml"]["lock"]).is_file()
    assert (staged / ort["directml"]["runtime_pack"][0]).is_file()


def test_stage_requires_bundled_paddle_model_pack(tmp_path: Path) -> None:
    """默认 Paddle small 检测/识别 + 文档方向模型必须随产品携带。"""
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest_path = backend / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest.pop("paddle_model_pack")
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ProductLayoutError, match="paddle_model_pack"):
        stage_product_layout(product_root=tmp_path / "VibeOCR", **inputs)


@pytest.mark.parametrize("present", [True, False])
def test_stage_requires_declared_paddle_environment_lock(
    tmp_path: Path, present: bool
) -> None:
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest_path = backend / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    # 历史兼容 fixture：仓库 paddle 专用锁已删除，这里合成一份满足旧
    # win-x64-paddle-cpu 锁校验的临时旧锁，验证新 staging 仍按声明
    # 携带历史 manifest 的旧字段（新 manifest 不再产生该绑定）。
    # 历史隔离 host 锁不含 Paddle：用真实 cpu host 锁替换合成锁并同步
    # sha，使 loader 的 paddle_isolated 校验与历史 manifest 形态一致。
    host_lock = backend / manifest["profiles"]["win-x64-cpu"]["lock"]
    host_lock.write_bytes(CPU_HOST_LOCK.read_bytes())
    manifest["profiles"]["win-x64-cpu"]["sha256"] = hashlib.sha256(
        host_lock.read_bytes()
    ).hexdigest()
    legacy_lock = backend / "paddle-cpu.lock"
    legacy_lock.write_text(_legacy_paddle_cpu_lock_text(), encoding="utf-8")
    manifest["profiles"]["win-x64-cpu"]["paddle_environment"] = {
        "lock": legacy_lock.name,
        "sha256": hashlib.sha256(legacy_lock.read_bytes()).hexdigest(),
    }
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    product = tmp_path / "VibeOCR"
    if not present:
        legacy_lock.unlink()
        with pytest.raises(ProductLayoutError, match="closure file is unavailable"):
            stage_product_layout(product_root=product, **inputs)
        return
    stage_product_layout(product_root=product, **inputs)
    assert (product / "runtime/backend/paddle-cpu.lock").read_text(
        encoding="utf-8"
    ) == _legacy_paddle_cpu_lock_text()


def test_stage_product_layout_still_carries_legacy_zip_bound_installer(
    tmp_path: Path,
) -> None:
    """历史 zip 绑定 manifest 的产品 staging 兼容：backend 携带 zip、提取 exe。"""
    inputs = _release_inputs(tmp_path)
    backend = inputs["backend_release_dir"]
    manifest_path = backend / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    (backend / manifest["installer"]["executable"]).unlink()
    legacy_zip = backend / "vibeocr-runtime-installer-v0.7.0-win-x64.zip"
    with zipfile.ZipFile(legacy_zip, mode="w") as archive:
        archive.writestr(
            "runtime-installer/vibeocr-runtime-installer.exe", b"installer"
        )
    manifest["installer"] = {
        "archive": legacy_zip.name,
        "sha256": hashlib.sha256(legacy_zip.read_bytes()).hexdigest(),
        "executable_path": "runtime-installer/vibeocr-runtime-installer.exe",
        "executable_sha256": hashlib.sha256(b"installer").hexdigest(),
    }
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

    product_root = tmp_path / "VibeOCR"
    stage_product_layout(product_root=product_root, **inputs)

    installer = product_root / "runtime/installer/vibeocr-runtime-installer.exe"
    assert installer.read_bytes() == b"installer"
    # 历史产品形态保留：backend 目录携带绑定的 zip（新 manifest 不再产生）。
    assert (product_root / "runtime/backend" / legacy_zip.name).is_file()


def _prebuilt_release_inputs(tmp_path: Path) -> dict[str, Path]:
    """预构建运行时树形态的最小 bundle 输入（PR2）。"""
    from scripts.build_runtime_pack import write_runtime_env_manifest
    from scripts.build_runtime_manifest import build_runtime_manifest

    tree = tmp_path / "runtime-env"
    (tree / "Lib").mkdir(parents=True)
    (tree / "python.exe").write_bytes(b"python-bin")
    (tree / "Lib" / "os.py").write_bytes(b"os")
    env_manifest = tmp_path / "runtime-env-manifest.json"
    write_runtime_env_manifest(tree, env_manifest)

    installer = tmp_path / "vibeocr-runtime-installer.exe"
    installer.write_bytes(b"installer")
    model_pack = tmp_path / "vibeocr-paddle-models-9.9.9.zip"
    with zipfile.ZipFile(model_pack, "w") as archive:
        archive.writestr("seed.txt", "model")
    base_lock = tmp_path / "requirements-win-x64-base.lock"
    base_lock.write_text(
        "fastapi==1.0.0 \\n  --hash=sha256:" + "1" * 64 + "\n"
        "winrt-runtime==3.2.1 \\n  --hash=sha256:" + "6" * 64 + "\n"
        "winrt-windows-foundation==3.2.1 \\n  --hash=sha256:" + "6" * 64 + "\n"
        "winrt-windows-foundation-collections==3.2.1 \\n  --hash=sha256:"
        + "6" * 64
        + "\n",
        encoding="utf-8",
    )
    dml_lock = tmp_path / "requirements-win-x64-paddle-ort-directml.lock"
    dml_lock.write_text(
        base_lock.read_text(encoding="utf-8")
        + "paddleocr==3.7.0 \\n  --hash=sha256:" + "7" * 64 + "\n"
        "paddlex==3.7.2 \\n  --hash=sha256:" + "7" * 64 + "\n"
        "opencv-contrib-python==5.0.0.93 \\n  --hash=sha256:" + "7" * 64 + "\n"
        "fastapi==1.0.0 \\n  --hash=sha256:" + "1" * 64 + "\n"
        "winrt-runtime==3.2.1 \\n  --hash=sha256:" + "6" * 64 + "\n"
        "winrt-windows-foundation==3.2.1 \\n  --hash=sha256:" + "6" * 64 + "\n"
        "winrt-windows-foundation-collections==3.2.1 \\n  --hash=sha256:"
        + "6" * 64
        + "\n"
        "winrt-windows-globalization==3.2.1 \\n  --hash=sha256:" + "6" * 64 + "\n"
        "winrt-windows-graphics-imaging==3.2.1 \\n  --hash=sha256:" + "6" * 64 + "\n"
        "winrt-windows-media-ocr==3.2.1 \\n  --hash=sha256:" + "6" * 64 + "\n"
        "winrt-windows-storage-streams==3.2.1 \\n  --hash=sha256:" + "6" * 64 + "\n"
        "onnxruntime-directml==1.24.4 \\n  --hash=sha256:" + "7" * 64 + "\n"
        "mineru==4.1.3 \\n  --hash=sha256:" + "2" * 64 + "\n"
        "onnx==1.23.2 \\n  --hash=sha256:" + "2" * 64 + "\n",
        encoding="utf-8",
    )
    output_dir = tmp_path / "runtime-bundle"
    build_runtime_manifest(
        runtime_wheel=None,
        base_lock=base_lock,
        python_archive=None,
        python_version="3.13.16",
        python_source_url=(
            "https://github.com/astral-sh/python-build-standalone/releases/"
            "download/20261001/"
            "cpython-3.13.16+20261001-x86_64-pc-windows-msvc"
            "-install_only.tar.gz"
        ),
        installer_executable=installer,
        version="9.9.9",
        source_commit="a" * 40,
        build_workflow="test",
        output_dir=output_dir,
        paddle_onnx_locks={"directml": dml_lock},
        paddle_model_pack=model_pack,
        runtime_env_tree=tree,
        runtime_env_manifest=env_manifest,
    )

    inputs = _release_inputs(tmp_path)
    # 重建 app/bootstrapper 等输入被 build_runtime_manifest 共用 tmp_path；
    # backend 指向新 bundle。
    inputs["backend_release_dir"] = output_dir
    return inputs


def test_stage_product_layout_ships_prebuilt_runtime_env_tree(
    tmp_path: Path,
) -> None:
    inputs = _prebuilt_release_inputs(tmp_path)
    product_root = tmp_path / "VibeOCR"

    stage_product_layout(product_root=product_root, **inputs)

    env_root = product_root / "runtime" / "env" / "runtime-env"
    assert (env_root / "python.exe").read_bytes() == b"python-bin"
    assert (env_root / "Lib" / "os.py").is_file()
    backend = product_root / "runtime" / "backend"
    assert (backend / "runtime-env-manifest.json").is_file()
    # 预构建形态：backend 不再携带 wheel/python 归档/runtime-code/pack。
    assert not list(backend.glob("*.whl"))
    assert not list(backend.glob("*.tar.gz"))
    assert not (backend / "runtime-code").exists()
    assert not list(backend.glob("vibeocr-runtime-pack*.zip"))
    assert not list(backend.glob("vibeocr-runtime-pool*.zip"))
    manifest = json.loads(
        (backend / "runtime-manifest.json").read_text(encoding="utf-8")
    )
    assert manifest["runtime_env"]["root"] == "runtime-env"
    assert manifest["runtime_wheel"] is None
    assert "archive" not in manifest["python"]
