from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path

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


def _product_tree(root: Path) -> Path:
    """受控完整占位产品树：WebView 文件数与锁一致、安装分发真实可读。"""
    lock = json.loads(
        (_REPO / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    app = root / "app"
    (app / "metadata").mkdir(parents=True)
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
    files = {
        "app/VibeOCR.WinUI.exe": {"sha256": "0" * 64, "size": 3},
    }
    webview = ["app/WebView2Runtime/msedgewebview2.exe"]
    (app / "WebView2Runtime" / "msedgewebview2.exe").write_bytes(b"browser")
    for index in range(int(lock["file_count"]) - 1):
        relative = f"app/WebView2Runtime/vendor-{index:04d}.dat"
        webview.append(relative)
        (root / relative).write_bytes(b"v")
    for relative in webview:
        files[relative] = {"sha256": "0" * 64, "size": 1}
    (app / "metadata" / "product-layout.json").write_text(
        json.dumps({"schema_version": 1, "app": {"entry": "app/VibeOCR.WinUI.exe"}}),
        encoding="utf-8",
    )
    (app / "metadata" / "product-release-manifest.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "frontend": "next",
                "frontend_version": "0.9.0",
                "source_commit": "0" * 40,
                "component_lock_sha256": "0" * 64,
                "files": files,
            }
        ),
        encoding="utf-8",
    )
    (root / "runtime/backend").mkdir(parents=True)
    (root / "runtime/backend/runtime-manifest.json").write_text(
        json.dumps(
            {
                "python": {"version": "3.13.16"},
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
    _dist_info(site_packages, "vibeocr-next-runtime", "0.9.0")
    _dist_info(site_packages, "example-upstream", "1.2.3")
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


def _build(tmp_path: Path, product: Path, metadata: Path | None = None):
    import uuid

    return build_spdx_sbom(
        artifacts_dir=_artifacts(tmp_path / f"artifacts-{uuid.uuid4().hex[:8]}"),
        repository_name="FelixJI/vibeocr-next",
        version="0.9.0",
        product_root=product,
        webview2_metadata=metadata,
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
    assert (
        "app/metadata/product-release-manifest.json"
        in packages["FelixJI/vibeocr-next-win-x64-portable"]["comment"]
    )
    relationships = {
        (item["spdxElementId"], item["relationshipType"], item["relatedSpdxElement"])
        for item in value["relationships"]
    }
    assert {
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-WebView2"),
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-DotNet"),
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-WinAppSdk"),
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-CPython"),
        ("SPDXRef-Package-Product", "CONTAINS", "SPDXRef-Package-InternalRuntime"),
        ("SPDXRef-Package-Product", "CONTAINS", packages["example-upstream"]["SPDXID"]),
        ("SPDXRef-DOCUMENT", "DESCRIBES", "SPDXRef-Package-Product"),
    } <= relationships


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
