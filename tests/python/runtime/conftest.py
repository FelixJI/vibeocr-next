"""Small staged product with a real interpreter and a synthetic three-model pack."""

import json
import os

import pytest


@pytest.fixture
def staged_runtime_product(tmp_path, monkeypatch):
    import shutil
    import sys
    from importlib.metadata import version
    from pathlib import Path

    root = Path(__file__).resolve().parents[3]
    monkeypatch.syspath_prepend(str(root))
    from scripts.build_product_binding import build_bindings
    from scripts.build_runtime_manifest import build_runtime_manifest
    from scripts.build_runtime_pack import write_runtime_env_manifest

    root = Path(__file__).resolve().parents[3]
    config = root / "config/runtime"
    product = tmp_path / "product"
    backend = product / "runtime/backend"
    tree = tmp_path / "interpreter"
    tree.mkdir()
    shutil.copyfile(sys._base_executable, tree / "python.exe")
    (tree / "python.exe").chmod(0o755)
    if os.name != "nt":
        shutil.copyfile(Path(sys.prefix) / "pyvenv.cfg", tree / "pyvenv.cfg")
    site = tree / (
        "Lib/site-packages"
        if os.name == "nt"
        else f"lib/python{sys.version_info.major}.{sys.version_info.minor}/site-packages"
    )
    site.mkdir(parents=True)
    import sysconfig

    (site / "fixture.pth").write_text(
        f"import site; site.addsitedir({sysconfig.get_path('purelib')!r})\n",
        encoding="utf-8",
    )
    if os.name == "nt":
        base = Path(sys.base_prefix)
        (tree / "python313._pth").write_text(
            "\n".join(
                (
                    str(base / "Lib"),
                    str(base / "DLLs"),
                    "Lib/site-packages",
                    sysconfig.get_path("purelib"),
                    "import site",
                )
            ),
            encoding="utf-8",
        )
    package = site / "vibeocr"
    shutil.copytree(
        root / "src/runtime/vibeocr/runtime",
        package / "runtime",
        ignore=shutil.ignore_patterns("__pycache__"),
    )
    shutil.copytree(
        root / "contracts/runtime/python/vibeocr/runtime_contracts",
        package / "runtime_contracts",
        ignore=shutil.ignore_patterns("__pycache__"),
    )
    if os.name == "nt":
        shutil.copyfile(Path(sys.base_prefix) / "python313.dll", tree / "python313.dll")
    else:
        (tree / "python313.dll").write_bytes(b"windows-layout-fixture")
    env_manifest = tmp_path / "runtime-env-manifest.json"
    import hashlib

    names = ("PP-OCRv6_small_det", "PP-OCRv6_small_rec", "PP-LCNet_x1_0_doc_ori")
    catalog: dict[str, dict] = {}
    models = tmp_path / "vibeocr-paddle-models-test.zip"
    import zipfile

    with zipfile.ZipFile(models, mode="w") as archive:
        for name in names:
            payload = f"payload:{name}".encode()
            entry = {
                # 共享缓存按 namespace/source/<owner>/<repo> 布置：每个
                # 模型一个仓库（与冻结清单一致），payload 内是成员文件。
                "repo_id": f"fixtures/{name}_onnx",
                "sources": {
                    source: {
                        "files": [
                            {
                                "path": "inference.onnx",
                                "size": len(payload),
                                "algorithm": "sha256",
                                "checksum": hashlib.sha256(payload).hexdigest(),
                            }
                        ]
                    }
                    for source in ("modelscope", "huggingface")
                },
            }
            catalog[name] = entry
            archive.writestr(f"{name}/inference.onnx", payload)
    (package / "runtime/environments/paddle_onnx_models.json").write_text(
        json.dumps({"models": catalog}), encoding="utf-8"
    )
    write_runtime_env_manifest(tree, env_manifest)
    runtime_version = version("vibeocr-next-runtime")
    pylock = json.loads((config / "python-runtime.lock.json").read_bytes())
    manifest = build_runtime_manifest(
        base_lock=config / "win-x64-base/requirements-win-x64-base.lock",
        paddle_onnx_locks={
            "directml": config
            / "win-x64-paddle-ort-directml/requirements-win-x64-paddle-ort-directml.lock"
        },
        paddle_model_pack=models,
        runtime_env_tree=tree,
        runtime_env_manifest=env_manifest,
        python_version=".".join(map(str, sys.version_info[:3])),
        python_source_url=pylock["source_url"],
        version=runtime_version,
        source_commit="a" * 40,
        build_workflow="synthetic",
        output_dir=backend,
    )
    binding = product / "app/metadata/component-lock.json"
    build_bindings(
        backend,
        version=runtime_version,
        source_sha="a" * 40,
        component_lock=binding,
        product_identity=binding.with_name("component-identities.json"),
    )
    runtime = product / "runtime/env/runtime-env"
    shutil.copytree(backend / "runtime-env", runtime)
    return product, runtime, manifest, binding, catalog
