from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from zipfile import ZipFile

import pytest

from scripts import build_spdx_sbom as build_spdx_sbom_module
from scripts.build_spdx_sbom import build_spdx_sbom

_REPO = Path(__file__).resolve().parents[2]


def _artifacts(root: Path, *, reverse: bool = False) -> Path:
    root.mkdir()
    entries = [
        ("vibeocr_backend-0.7.0-py3-none-any.whl", b"backend-wheel"),
        ("runtime-manifest.json", b'{"schema_version":1}\n'),
        ("SHA256SUMS", b"checksums\n"),
    ]
    for name, content in reversed(entries) if reverse else entries:
        (root / name).write_bytes(content)
    return root


def test_spdx_sbom_is_deterministic_sorted_and_complete(tmp_path: Path) -> None:
    first = build_spdx_sbom(
        artifacts_dir=_artifacts(tmp_path / "first"),
        repository_name="FelixJI/vibeocr-backend",
        version="0.7.0",
    )
    second = build_spdx_sbom(
        artifacts_dir=_artifacts(tmp_path / "second", reverse=True),
        repository_name="FelixJI/vibeocr-backend",
        version="0.7.0",
    )

    assert first.read_bytes() == second.read_bytes()
    value = json.loads(first.read_text(encoding="utf-8"))
    assert value["spdxVersion"] == "SPDX-2.3"
    assert value["dataLicense"] == "CC0-1.0"
    assert value["packages"][0]["name"] == "FelixJI/vibeocr-backend"
    assert value["packages"][0]["versionInfo"] == "0.7.0"

    files = value["files"]
    names = [record["fileName"] for record in files]
    assert names == sorted(names)
    assert "SBOM.spdx.json" not in names
    for record in files:
        artifact = first.parent / record["fileName"]
        assert record["checksums"] == [
            {
                "algorithm": "SHA256",
                "checksumValue": hashlib.sha256(artifact.read_bytes()).hexdigest(),
            }
        ]
        assert record["comment"] == f"size={artifact.stat().st_size}"


def test_rebuild_excludes_existing_sbom_and_tracks_content_changes(
    tmp_path: Path,
) -> None:
    root = _artifacts(tmp_path / "artifacts")
    first = build_spdx_sbom(
        artifacts_dir=root,
        repository_name="vibeocr-protocol",
        version="2.0.0",
    )
    first_bytes = first.read_bytes()
    first_value = json.loads(first.read_text(encoding="utf-8"))

    rebuilt = build_spdx_sbom(
        artifacts_dir=root,
        repository_name="vibeocr-protocol",
        version="2.0.0",
    )
    assert rebuilt.read_bytes() == first_bytes
    assert all(
        record["fileName"] != "SBOM.spdx.json"
        for record in json.loads(rebuilt.read_text(encoding="utf-8"))["files"]
    )

    (root / "runtime-manifest.json").write_bytes(b"changed")
    changed = build_spdx_sbom(
        artifacts_dir=root,
        repository_name="vibeocr-protocol",
        version="2.0.0",
    )
    changed_value = json.loads(changed.read_text(encoding="utf-8"))
    assert changed_value["documentNamespace"] != first_value["documentNamespace"]


@pytest.mark.parametrize(
    ("repository_name", "version", "message"),
    [
        ("", "1.0.0", "repository_name"),
        ("vibeocr-backend", "", "version"),
    ],
)
def test_spdx_sbom_requires_repository_identity(
    tmp_path: Path,
    repository_name: str,
    version: str,
    message: str,
) -> None:
    with pytest.raises(ValueError, match=message):
        build_spdx_sbom(
            artifacts_dir=_artifacts(tmp_path / "artifacts"),
            repository_name=repository_name,
            version=version,
        )


def test_spdx_sbom_rejects_empty_artifacts_directory(tmp_path: Path) -> None:
    artifacts = tmp_path / "artifacts"
    artifacts.mkdir()
    with pytest.raises(ValueError, match="at least one"):
        build_spdx_sbom(
            artifacts_dir=artifacts,
            repository_name="vibeocr-backend",
            version="0.7.0",
        )


def _dist_info(site_packages: Path, name: str, version: str) -> None:
    normalized = re.sub(r"[-_.]+", "-", name).lower()
    target = site_packages / f"{normalized}-{version}.dist-info"
    target.mkdir(parents=True)
    (target / "METADATA").write_text(
        f"Metadata-Version: 2.1\nName: {name}\nVersion: {version}\n",
        encoding="utf-8",
    )
    (target / "RECORD").write_text(f"{target.name}/METADATA,,\n", encoding="utf-8")


@pytest.fixture(autouse=True)
def _web_dist(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    source = tmp_path / "web-source"
    monkeypatch.setattr(build_spdx_sbom_module, "_WEB_ASSETS", source)
    # Production lock includes the resolved transitive scheduler; vite is dev only.
    real = json.loads(
        (_REPO / "src/dotnet/VibeOCR.App/WebAssets/package-lock.json").read_text(
            encoding="utf-8"
        )
    )["packages"]
    dependencies = {"react-dom": "19.3.1"}
    lock = {"": {"dependencies": dependencies}}
    for name in ("react-dom", "react", "scheduler", "vite"):
        relative = f"node_modules/{name}"
        pin = real[relative]
        lock[relative] = pin
        installed = source / relative
        installed.mkdir(parents=True)
        (installed / "package.json").write_text(
            json.dumps({"name": name, "version": pin["version"]}), encoding="utf-8"
        )
    (source / "package-lock.json").write_text(
        json.dumps({"packages": lock}), encoding="utf-8"
    )
    (source / "package.json").write_text(
        json.dumps({"dependencies": dependencies}), encoding="utf-8"
    )
    dist = source / "dist"
    (dist / "assets").mkdir(parents=True)
    (dist / "index.html").write_text("<html></html>", encoding="utf-8")
    (dist / "assets/main.js").write_text("export {};", encoding="utf-8")
    return dist


def _product_tree(root: Path) -> Path:
    """受控完整占位产品树：WebView 文件数与锁一致、安装分发真实可读。"""
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    app = root / "app"
    (app / "metadata").mkdir(parents=True)
    boot_lock = json.loads(
        (_REPO / "src/dotnet/VibeOCR.Bootstrapper/packages.lock.json").read_text(
            encoding="utf-8"
        )
    )
    boot_pin = boot_lock["dependencies"][".NETFramework,Version=v4.7.2"]["Velopack"][
        "resolved"
    ]
    packages = root.parent / "nuget-cache"
    boot_asset = packages / f"velopack/{boot_pin}/lib/net472/Velopack.dll"
    boot_asset.parent.mkdir(parents=True)
    boot_asset.write_bytes(b"velopack net472")
    (root / "Velopack.dll").write_bytes(boot_asset.read_bytes())
    (root / "VibeOCR.exe").write_bytes(b"bootstrapper")
    (app / "WebView2Runtime").mkdir(parents=True)
    (app / "VibeOCR.WinUI.exe").write_bytes(b"exe")
    (app / "VibeOCR.WinUI.runtimeconfig.json").write_text(
        json.dumps(
            {
                "runtimeOptions": {
                    "tfm": "net10.0",
                    "includedFrameworks": [
                        {"name": "Microsoft.NETCore.App", "version": "10.0.15"}
                    ],
                }
            }
        ),
        encoding="utf-8",
    )
    # 实际 publish 的 NuGet 资产：deps.json 目标图绑定并落在产品 app/。
    (app / "WebView2Loader.dll").write_bytes(b"loader")
    (app / "Microsoft.Web.WebView2.Core.dll").write_bytes(b"core")
    (app / "Microsoft.Web.WebView2.Core.Projection.dll").write_bytes(b"projection")
    (app / "DirectML.dll").write_bytes(b"dml")
    (app / "onnxruntime.dll").write_bytes(b"ort")
    (app / "coreclr.dll").write_bytes(b"clr")
    (app / "VibeOCR.WinUI.dll").write_bytes(b"project")
    (app / "Microsoft.ui.xaml.dll").write_bytes(b"winui")
    target = ".NETCoreApp,Version=v10.0/win-x64"
    loader = f"Microsoft.Web.WebView2/{lock['loader_sdk_version']}"
    win_ml = "Microsoft.Windows.AI.MachineLearning/2.5.96"
    war = "Microsoft.WindowsAppSDK/2.7.0"
    clr = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.15"
    project = "VibeOCR.WinUI/0.9.0"
    (app / "VibeOCR.WinUI.deps.json").write_text(
        json.dumps(
            {
                "runtimeTarget": {"name": target},
                "libraries": {
                    loader: {"type": "package"},
                    win_ml: {"type": "package"},
                    war: {"type": "package"},
                    clr: {"type": "runtimepack"},
                    project: {"type": "project"},
                },
                "targets": {
                    ".NETCoreApp,Version=v10.0": {
                        loader: {
                            "native": {"runtimes/win-x86/native/wrong-rid.dll": {}}
                        },
                    },
                    target: {
                        loader: {
                            "runtime": {
                                "lib/net10.0/Microsoft.Web.WebView2.Core.dll": {}
                            },
                            "native": {
                                "runtimes/win-x64/native/WebView2Loader.dll": {}
                            },
                        },
                        win_ml: {
                            "native": {
                                "runtimes/win-x64/native/DirectML.dll": {},
                                "runtimes/win-x64/native/onnxruntime.dll": {},
                            },
                        },
                        war: {
                            "dependencies": {"Microsoft.WindowsAppSDK.WinUI": "2.7.0"}
                        },
                        clr: {"native": {"coreclr.dll": {}}},
                        project: {"runtime": {"VibeOCR.WinUI.dll": {}}},
                    },
                },
            }
        ),
        encoding="utf-8",
    )
    publish_items = root.parent / "app-publish-items.json"
    publish_items.write_text(
        json.dumps(
            {
                "Properties": {"NuGetPackageFolders": f"C:/nuget/packages;{packages}"},
                "Items": {
                    "ResolvedFileToPublish": [
                        {
                            "FullPath": "C:/nuget/packages/microsoft.web.webview2/1.0.4314.35/"
                            "runtimes/win-x64/native_uap/Microsoft.Web.WebView2.Core.dll",
                            "RelativePath": "Microsoft.Web.WebView2.Core.dll",
                        },
                        {
                            "FullPath": "C:/nuget/packages/microsoft.web.webview2/1.0.4314.35/"
                            "lib_manual/net8.0-windows10.0.17763.0/Microsoft.Web.WebView2.Core.Projection.dll",
                            "RelativePath": "Microsoft.Web.WebView2.Core.Projection.dll",
                        },
                    ],
                    "MicrosoftWindowsAppSDKFiles": [
                        {
                            "FullPath": "C:/nuget/packages/microsoft.windowsappsdk.winui/2.7.0/"
                            "runtimes-framework/win-x64/native/Microsoft.ui.xaml.dll",
                            "RecursiveDir": "",
                        }
                    ],
                    "MicrosoftWindowsAppSDKFilesRes": [],
                },
            }
        ),
        encoding="utf-8",
    )
    (app / "WebAssets/assets").mkdir(parents=True)
    (app / "WebAssets" / "index.html").write_text("<html></html>", encoding="utf-8")
    (app / "WebAssets/assets/main.js").write_text("export {};", encoding="utf-8")
    (app / "WebView2Runtime" / "msedgewebview2.exe").write_bytes(b"browser")
    for index in range(int(lock["file_count"]) - 1):
        relative = f"app/WebView2Runtime/vendor-{index:04d}.dat"
        (root / relative).write_bytes(b"v")
    (app / "metadata" / "product-layout.json").write_text(
        json.dumps({"schema_version": 1, "app": {"entry": "app/VibeOCR.WinUI.exe"}}),
        encoding="utf-8",
    )
    backend = root / "runtime/backend"
    backend.mkdir(parents=True)
    python_lock = json.loads(
        (_REPO / "config/runtime/python-runtime.lock.json").read_text(encoding="utf-8")
    )
    (backend / "runtime-env-manifest.json").write_text("{}", encoding="utf-8")
    (backend / "requirements-base.lock").write_text(
        "example-upstream==1.2.3\n", encoding="utf-8"
    )
    (backend / "requirements-directml.lock").write_text(
        "onnxruntime-directml==1.24.4\n", encoding="utf-8"
    )
    (backend / "runtime-manifest.json").write_text(
        json.dumps(
            {
                "python": python_lock,
                "profiles": {"win-x64-base": {"lock": "requirements-base.lock"}},
                "paddle_onnx_environments": {
                    "directml": {"lock": "requirements-directml.lock"}
                },
                "runtime_env": {
                    "root": "runtime-env",
                    "manifest": "runtime-env-manifest.json",
                },
                "product": {
                    "component": "next",
                    "repository": "FelixJI/vibeocr-next",
                    "version": "0.9.0",
                    "source_sha": "0" * 40,
                },
            }
        ),
        encoding="utf-8",
    )
    site_packages = root / "runtime/env/runtime-env/Lib/site-packages"
    site_packages.mkdir(parents=True)
    (site_packages.parents[1] / "python.exe").write_bytes(b"python")
    (site_packages.parents[1] / "python313.dll").write_bytes(b"python")
    _dist_info(site_packages, "vibeocr-next-runtime", "0.9.0")
    _dist_info(site_packages, "example-upstream", "1.2.3")
    _dist_info(site_packages, "onnxruntime-directml", "1.24.4")
    (site_packages / "ort-native.dll").write_bytes(b"onnxruntime")
    record = site_packages / "onnxruntime-directml-1.24.4.dist-info/RECORD"
    record.write_text(
        record.read_text(encoding="utf-8") + "ort-native.dll,,\n", encoding="utf-8"
    )
    files = {
        path.relative_to(root).as_posix(): {
            "sha256": "0" * 64,
            "size": path.stat().st_size,
        }
        for path in root.rglob("*")
        if path.is_file()
    }
    (app / "metadata/product-release-manifest.json").write_text(
        json.dumps({"schema_version": 1, "files": files}), encoding="utf-8"
    )
    return root


def _metadata(tmp_path: Path, *, version: str, file_count: int) -> Path:
    path = tmp_path / "webview2-runtime-metadata.json"
    path.write_text(
        json.dumps({"version": version, "file_count": file_count}),
        encoding="utf-8",
    )
    return path


def _locked_version() -> str:
    return json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )["version"]


def _build(
    tmp_path: Path,
    product: Path,
    metadata: Path | None = None,
    *,
    portable_updater: bool = True,
):
    import uuid

    artifacts = _artifacts(tmp_path / f"artifacts-{uuid.uuid4().hex[:8]}")
    with ZipFile(artifacts / "VibeOCRNext-v0.9.0-win-x64.zip", "w") as portable:
        portable.writestr(".portable", b"")
        portable.writestr("VibeOCR.exe", b"stub")
        if portable_updater:
            portable.writestr("Update.exe", b"updater")
        portable.writestr("current/sq.version", b"nuspec")
        for name in ("Velopack.dll", "VibeOCR.exe"):
            portable.write(product / name, f"current/{name}")
    with ZipFile(artifacts / "VibeOCRNext-0.9.0-full.nupkg", "w") as full:
        full.writestr("lib/app/sq.version", b"nuspec")
        for name in ("Velopack.dll", "VibeOCR.exe"):
            full.write(product / name, f"lib/app/{name}")
    return build_spdx_sbom(
        artifacts_dir=artifacts,
        repository_name="FelixJI/vibeocr-next",
        version="0.9.0",
        product_root=product,
        webview2_metadata=metadata,
        app_publish_items=product.parent / "app-publish-items.json",
    )


def test_spdx_sbom_declares_bundled_product_components(tmp_path: Path) -> None:
    """#321：声明版本与实际分发组件在 SPDX 中可追踪（旧实现缺失）。"""
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    app_lock = json.loads(
        (_REPO / "src/dotnet/VibeOCR.App/packages.lock.json").read_text(
            encoding="utf-8"
        )
    )
    resolved = {
        name: entry["resolved"]
        for target in app_lock["dependencies"].values()
        for name, entry in target.items()
        if isinstance(entry, dict) and "resolved" in entry
    }

    output = _build(
        tmp_path,
        _product_tree(tmp_path / "VibeOCR"),
        _metadata(
            tmp_path,
            version=lock["version"],
            file_count=int(lock["file_count"]),
        ),
    )

    value = json.loads(output.read_text(encoding="utf-8"))
    packages = {item["name"]: item for item in value["packages"]}
    assert packages["FelixJI/vibeocr-next-win-x64-portable"]["versionInfo"] == "0.9.0"
    assert packages["Microsoft.NETCore.App"]["versionInfo"] == "10.0.15"
    assert (
        packages["Microsoft.WindowsAppSDK"]["versionInfo"]
        == resolved["Microsoft.WindowsAppSDK"]
    )
    webview = packages["Microsoft.WebView2.FixedVersionRuntime"]
    assert webview["versionInfo"] == lock["version"]
    assert webview["downloadLocation"] == lock["source_url"]
    assert webview["checksums"] == [
        {"algorithm": "SHA256", "checksumValue": lock["sha256"]}
    ]
    assert packages["CPython"]["versionInfo"] == "3.13.16"
    assert packages["vibeocr-next-runtime"]["versionInfo"] == "0.9.0"
    assert packages["example-upstream"]["versionInfo"] == "1.2.3"
    # 实际 NuGet 资产与 Loader（从 deps.json 目标图与产品文件对应）。
    assert (
        packages["Microsoft.Web.WebView2"]["versionInfo"]
        == resolved["Microsoft.Web.WebView2"]
    )
    assert "app/WebView2Loader.dll" in packages["Microsoft.Web.WebView2"]["sourceInfo"]
    assert (
        "app/Microsoft.Web.WebView2.Core.Projection.dll"
        in packages["Microsoft.Web.WebView2"]["sourceInfo"]
    )
    native = packages["Microsoft.Windows.AI.MachineLearning"]["sourceInfo"]
    assert "app/DirectML.dll" in native
    assert "app/onnxruntime.dll" in native
    assert (
        "app/Microsoft.ui.xaml.dll"
        in packages["Microsoft.WindowsAppSDK.WinUI"]["sourceInfo"]
    )
    assert "ort-native.dll" in packages["onnxruntime-directml"]["sourceInfo"]
    assert "app/WebAssets/assets/main.js" in packages["VibeOCR.WebAssets"]["sourceInfo"]
    assert "lib/net472/Velopack.dll" in packages["Velopack"]["sourceInfo"]
    assert "Bootstrapper packages.lock.json" in packages["Velopack"]["sourceInfo"]
    portable_runtime = packages["Velopack Windows Portable runtime"]
    assert "vendor/update_x64.exe" in portable_runtime["sourceInfo"]
    assert "vendor/stub_x64.exe" in portable_runtime["sourceInfo"]
    assert "VibeOCR.WinUI" not in packages
    assert "runtimepack.Microsoft.NETCore.App.Runtime.win-x64" not in packages
    assert [item["name"] for item in value["packages"]].count(
        "Microsoft.WindowsAppSDK"
    ) == 1
    assert "vite" not in packages
    assert packages["scheduler"]["versionInfo"] != packages["react-dom"]["versionInfo"]
    assert packages["scheduler"]["externalRefs"][0]["referenceLocator"].startswith(
        "pkg:npm/scheduler@"
    )
    assert (
        "app/metadata/product-release-manifest.json"
        in packages["FelixJI/vibeocr-next-win-x64-portable"]["sourceInfo"]
    )
    relationships = {
        (item["spdxElementId"], item["relationshipType"], item["relatedSpdxElement"])
        for item in value["relationships"]
    }
    file_ids = {file["fileName"]: file["SPDXID"] for file in value["files"]}
    assert {
        (
            file_ids["VibeOCRNext-v0.9.0-win-x64.zip"],
            "CONTAINS",
            portable_runtime["SPDXID"],
        )
    } == {edge for edge in relationships if edge[2] == portable_runtime["SPDXID"]}
    assert {
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-WebView2"),
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-DotNet"),
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-WinAppSdk"),
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-CPython"),
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-InternalRuntime"),
        ("SPDXRef-Package-Product", "CONTAINS", packages["example-upstream"]["SPDXID"]),
        ("SPDXRef-DOCUMENT", "DESCRIBES", "SPDXRef-Package-Product"),
        (
            packages["scheduler"]["SPDXID"],
            "BUILD_DEPENDENCY_OF",
            "SPDXRef-Package-WebAssets",
        ),
    } <= relationships
    assert (
        "SPDXRef-Package-Product",
        "CONTAINS",
        packages["scheduler"]["SPDXID"],
    ) not in relationships


def test_spdx_sbom_requires_three_way_webview_file_count(tmp_path: Path) -> None:
    """manifest/实际展开 metadata/锁三方一致；伪造或缺一必须红。"""
    product = _product_tree(tmp_path / "VibeOCR")
    good = _build(
        tmp_path,
        product,
        _metadata(
            tmp_path,
            version=_locked_version(),
            file_count=int(
                json.loads(
                    (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
                        encoding="utf-8"
                    )
                )["file_count"]
            ),
        ),
    )
    assert good.is_file()

    # metadata 伪造计数
    with pytest.raises(ValueError, match="file count mismatch"):
        _build(
            tmp_path,
            product,
            _metadata(tmp_path, version=_locked_version(), file_count=1),
        )
    # manifest 缺一个 WebView 文件
    manifest_path = product / "app/metadata/product-release-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    webview = sorted(
        name for name in manifest["files"] if name.startswith("app/WebView2Runtime/")
    )
    manifest["files"].pop(webview[-1])
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ValueError, match="file count mismatch"):
        _build(
            tmp_path,
            product,
            _metadata(
                tmp_path,
                version=_locked_version(),
                file_count=int(
                    json.loads(
                        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
                            encoding="utf-8"
                        )
                    )["file_count"]
                ),
            ),
        )


def test_spdx_sbom_requires_exactly_one_matching_internal_wheel(
    tmp_path: Path,
) -> None:
    product = _product_tree(tmp_path / "VibeOCR")
    site_packages = product / "runtime/env/runtime-env/Lib/site-packages"
    (site_packages / "vibeocr-next-runtime-0.9.0.dist-info").rename(
        site_packages / "vibeocr-next-runtime-0.9.1.dist-info"
    )
    (site_packages / "vibeocr-next-runtime-0.9.1.dist-info" / "METADATA").write_text(
        "Metadata-Version: 2.1\nName: vibeocr-next-runtime\nVersion: 0.9.1\n",
        encoding="utf-8",
    )
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    with pytest.raises(ValueError, match="vibeocr-next-runtime"):
        _build(
            tmp_path,
            product,
            _metadata(
                tmp_path, version=lock["version"], file_count=int(lock["file_count"])
            ),
        )


def test_spdx_sbom_requires_loader_resolution_matching_the_lock(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    lock["loader_sdk_version"] = "0.0.0-mismatch"
    fake_lock = tmp_path / "webview2-runtime.lock.json"
    fake_lock.write_text(json.dumps(lock), encoding="utf-8")
    monkeypatch.setattr(build_spdx_sbom_module, "_WEBVIEW2_LOCK", fake_lock)
    product = _product_tree(tmp_path / "VibeOCR")
    with pytest.raises(ValueError, match="loader"):
        _build(
            tmp_path,
            product,
            _metadata(
                tmp_path, version=lock["version"], file_count=int(lock["file_count"])
            ),
        )


def test_spdx_sbom_requires_deps_json_and_loader_distribution(tmp_path: Path) -> None:
    """deps.json 缺失或 Loader 文件未分发时 SBOM fail closed。"""
    product = _product_tree(tmp_path / "VibeOCR")
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    good = _build(
        tmp_path,
        product,
        _metadata(
            tmp_path, version=lock["version"], file_count=int(lock["file_count"])
        ),
    )
    assert good.is_file()

    (product / "app/VibeOCR.WinUI.deps.json").unlink()
    with pytest.raises(ValueError, match="deps.json"):
        _build(
            tmp_path,
            product,
            _metadata(
                tmp_path, version=lock["version"], file_count=int(lock["file_count"])
            ),
        )


@pytest.mark.parametrize(
    "relative",
    [
        "app/WebView2Loader.dll",
        "app/DirectML.dll",
        "app/Microsoft.ui.xaml.dll",
        "app/Microsoft.Web.WebView2.Core.Projection.dll",
        "app/WebAssets/assets/main.js",
        "runtime/env/runtime-env/Lib/site-packages/ort-native.dll",
    ],
)
def test_spdx_sbom_rejects_declared_files_omitted_from_product(
    tmp_path: Path, relative: str
) -> None:
    """同包其他 DLL 仍在，不能把缺失原生 DLL 算作已分发。"""
    product = _product_tree(tmp_path / "VibeOCR")
    manifest_path = product / "app/metadata/product-release-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"].pop(relative)
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    (product / relative).unlink()
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    with pytest.raises(ValueError, match=re.escape(relative)):
        _build(
            tmp_path,
            product,
            _metadata(
                tmp_path, version=lock["version"], file_count=int(lock["file_count"])
            ),
        )


def test_spdx_sbom_rejects_production_web_input_outside_lock(tmp_path: Path) -> None:
    product = _product_tree(tmp_path / "VibeOCR")
    installed = tmp_path / "web-source/node_modules/scheduler/package.json"
    installed.write_text(
        json.dumps({"name": "scheduler", "version": "0.0.0"}), encoding="utf-8"
    )
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    with pytest.raises(ValueError, match="production input does not match the lock"):
        _build(
            tmp_path,
            product,
            _metadata(tmp_path, version=lock["version"], file_count=lock["file_count"]),
        )


@pytest.mark.parametrize("duplicate", [False, True], ids=["unowned", "ambiguous"])
def test_spdx_sbom_requires_unique_python_native_owner(
    tmp_path: Path, duplicate: bool
) -> None:
    product = _product_tree(tmp_path / "VibeOCR")
    site = product / "runtime/env/runtime-env/Lib/site-packages"
    manifest = product / "app/metadata/product-release-manifest.json"
    if duplicate:
        native = "ort-native.dll"
        record = site / "example-upstream-1.2.3.dist-info/RECORD"
        with record.open("a", encoding="utf-8") as stream:
            stream.write(f"{native},,\n")
    else:
        native = "orphan.pyd"
        orphan = site / native
        orphan.write_bytes(b"unowned")
        closure = json.loads(manifest.read_text(encoding="utf-8"))
        closure["files"][orphan.relative_to(product).as_posix()] = {"size": 7}
        manifest.write_text(json.dumps(closure), encoding="utf-8")
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    with pytest.raises(ValueError, match=re.escape(native)):
        _build(
            tmp_path,
            product,
            _metadata(tmp_path, version=lock["version"], file_count=lock["file_count"]),
        )


@pytest.mark.parametrize(
    ("wrong_dll", "message"),
    [
        (True, "root Velopack.dll differs"),
        (False, "unexpected Velopack Portable root files"),
    ],
)
def test_spdx_sbom_requires_locked_bootstrapper_dll_and_portable_updater(
    tmp_path: Path, wrong_dll: bool, message: str
) -> None:
    product = _product_tree(tmp_path / "VibeOCR")
    if wrong_dll:
        (product / "Velopack.dll").write_bytes(b"wrong framework build of Velopack")
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    with pytest.raises(ValueError, match=message):
        _build(
            tmp_path,
            product,
            _metadata(tmp_path, version=lock["version"], file_count=lock["file_count"]),
            portable_updater=wrong_dll,
        )


def test_spdx_sbom_rejects_stale_bundled_web_output(tmp_path: Path) -> None:
    product = _product_tree(tmp_path / "VibeOCR")
    (product / "app/WebAssets/assets/main.js").write_text(
        "export {}?", encoding="utf-8"
    )
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    with pytest.raises(
        ValueError, match="content differs from dist: app/WebAssets/assets/main.js"
    ):
        _build(
            tmp_path,
            product,
            _metadata(tmp_path, version=lock["version"], file_count=lock["file_count"]),
        )


def test_spdx_sbom_requires_the_product_release_manifest(tmp_path: Path) -> None:
    product = _product_tree(tmp_path / "VibeOCR")
    (product / "app/metadata/product-release-manifest.json").unlink()
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    with pytest.raises(ValueError, match="product-release-manifest"):
        _build(
            tmp_path,
            product,
            _metadata(
                tmp_path, version=lock["version"], file_count=int(lock["file_count"])
            ),
        )


def test_spdx_sbom_requires_expansion_metadata(tmp_path: Path) -> None:
    product = _product_tree(tmp_path / "VibeOCR")
    with pytest.raises(ValueError, match="webview2-metadata"):
        _build(tmp_path, product)


def test_spdx_sbom_requires_the_bundled_webview2_closure(tmp_path: Path) -> None:
    """#321：产品闭包未绑定随包 WebView2 时 SBOM fail closed。"""
    product = _product_tree(tmp_path / "VibeOCR")
    manifest_path = product / "app/metadata/product-release-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for name in [
        name for name in manifest["files"] if name.startswith("app/WebView2Runtime/")
    ]:
        manifest["files"].pop(name)
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    with pytest.raises(ValueError, match="bundled WebView2 runtime"):
        _build(
            tmp_path,
            product,
            _metadata(
                tmp_path, version=lock["version"], file_count=int(lock["file_count"])
            ),
        )
