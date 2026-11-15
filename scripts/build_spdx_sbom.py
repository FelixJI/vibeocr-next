"""Build a deterministic SPDX 2.3 JSON SBOM for release artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from dataclasses import dataclass
from pathlib import Path

_OUTPUT_FILENAME = "SBOM.spdx.json"
_CREATED = "1970-01-01T00:00:00Z"
_CREATOR = "Tool: build_spdx_sbom.py"
_REPO_ROOT = Path(__file__).resolve().parents[1]
_WEBVIEW2_LOCK = _REPO_ROOT / "config" / "runtime" / "webview2-runtime.lock.json"
_WIN_APP_SDK_PIN = _REPO_ROOT / "Directory.Packages.props"


@dataclass(frozen=True)
class _Artifact:
    name: str
    sha1: str
    sha256: str
    size: int


def _artifact(path: Path) -> _Artifact:
    # SPDX 2.3 mandates SHA-1 for the package verification code.
    sha1 = hashlib.sha1()
    sha256 = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            sha1.update(chunk)
            sha256.update(chunk)
            size += len(chunk)
    return _Artifact(path.name, sha1.hexdigest(), sha256.hexdigest(), size)


def _canonical_json(value: object) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    ).encode("utf-8")


def _identifier(value: str) -> str:
    identifier = re.sub(r"[^A-Za-z0-9.-]+", "-", value).strip("-.")
    return identifier or "release"


def _package_verification_code(artifacts: tuple[_Artifact, ...]) -> str:
    # SPDX 2.3 defines this as SHA-1 over the sorted per-file SHA-1 values.
    digest = hashlib.sha1()
    digest.update("".join(sorted(item.sha1 for item in artifacts)).encode("ascii"))
    return digest.hexdigest()


def _document_namespace(
    repository_name: str,
    version: str,
    artifacts: tuple[_Artifact, ...],
) -> str:
    identity = {
        "repository_name": repository_name,
        "version": version,
        "artifacts": [
            {"name": item.name, "sha256": item.sha256, "size": item.size}
            for item in artifacts
        ],
    }
    digest = hashlib.sha256(_canonical_json(identity)).hexdigest()
    return (
        "https://spdx.org/spdxdocs/"
        f"{_identifier(repository_name)}-{_identifier(version)}-{digest}"
    )


def _product_component_packages(
    *,
    repository_name: str,
    version: str,
    product_root: Path,
    webview2_metadata: Path | None,
) -> tuple[list[dict], list[dict]]:
    """Resolve the actually distributed runtime components of a product tree.

    The portable product closure itself stays bound by the existing
    per-file release manifest (single integrity contract); this only adds the
    declared component relationships: self-contained .NET runtime, Windows
    App SDK self-contained files and the locked WebView2 Fixed Version tree.
    """

    app_root = product_root / "app"
    manifest_path = app_root / "metadata" / "product-release-manifest.json"
    if not manifest_path.is_file():
        raise ValueError(
            "product_root requires app/metadata/product-release-manifest.json"
        )
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    closure = manifest.get("files")
    if not isinstance(closure, dict) or not closure:
        raise ValueError("product release manifest must bind a non-empty closure")
    webview_files = sorted(
        name for name in closure if name.startswith("app/WebView2Runtime/")
    )
    if not webview_files or "app/WebView2Runtime/msedgewebview2.exe" not in closure:
        raise ValueError("product closure does not bind the bundled WebView2 runtime")
    lock = json.loads(_WEBVIEW2_LOCK.read_text(encoding="utf-8"))
    for field in (
        "version",
        "source_url",
        "sha256",
        "file_count",
        "loader_sdk_version",
    ):
        if not lock.get(field):
            raise ValueError(f"webview2-runtime.lock.json is missing {field}")
    if webview2_metadata is None:
        raise ValueError(
            "product_root requires --webview2-metadata from the actual expansion"
        )
    metadata = json.loads(webview2_metadata.read_text(encoding="utf-8"))
    # 实际展开的 exe 版本/文件数必须与锁一致；最终 manifest 的 WebView 文件
    # 数也必须与两者相符（三方一致，缺一即 fail closed）。
    if metadata.get("version") != lock["version"]:
        raise ValueError(
            "expanded WebView2 runtime version does not match the lock: "
            f"{metadata.get('version')} != {lock['version']}"
        )
    if not (
        len(webview_files)
        == int(metadata.get("file_count", -1))
        == int(lock["file_count"])
    ):
        raise ValueError(
            "bundled WebView2 runtime file count mismatch: "
            f"manifest={len(webview_files)} "
            f"metadata={metadata.get('file_count')} lock={lock['file_count']}"
        )
    # NuGet 已解析的 Loader/WAR 版本从 App packages.lock 解析结果读取
    # （不是中央 pin 的正则推断）。
    app_lock = json.loads(
        (_REPO_ROOT / "src/dotnet/VibeOCR.App/packages.lock.json").read_text(
            encoding="utf-8"
        )
    )
    resolved = {
        name: entry["resolved"]
        for target in app_lock.get("dependencies", {}).values()
        for name, entry in target.items()
        if isinstance(entry, dict) and "resolved" in entry
    }
    resolved_loader = resolved.get("Microsoft.Web.WebView2")
    resolved_war = resolved.get("Microsoft.WindowsAppSDK")
    if resolved_loader != lock["loader_sdk_version"]:
        raise ValueError(
            "resolved Microsoft.Web.WebView2 loader does not match the lock: "
            f"{resolved_loader} != {lock['loader_sdk_version']}"
        )
    descriptor_path = app_root / "metadata" / "product-layout.json"
    if not descriptor_path.is_file():
        raise ValueError("product_root requires app/metadata/product-layout.json")
    descriptor = json.loads(descriptor_path.read_text(encoding="utf-8"))
    entry = descriptor.get("app", {}).get("entry")
    if not isinstance(entry, str):
        raise ValueError("product layout descriptor is missing app.entry")
    runtimeconfig = product_root / (entry[: -len(".exe")] + ".runtimeconfig.json")
    if not runtimeconfig.is_file():
        raise ValueError("product closure is missing the app runtimeconfig")
    runtime_options = json.loads(runtimeconfig.read_text(encoding="utf-8"))
    dotnet_version = next(
        (
            item["version"]
            for item in runtime_options.get("runtimeOptions", {}).get(
                "includedFrameworks", []
            )
            if isinstance(item, dict) and item.get("name") == "Microsoft.NETCore.App"
        ),
        None,
    )
    if not isinstance(dotnet_version, str):
        raise ValueError("product runtimeconfig is not self-contained")
    if resolved_war is None:
        raise ValueError(
            "App packages.lock.json does not resolve Microsoft.WindowsAppSDK"
        )
    runtime_manifest = json.loads(
        (product_root / "runtime/backend/runtime-manifest.json").read_text(
            encoding="utf-8"
        )
    )
    python_version = (runtime_manifest.get("python") or {}).get("version")
    backend_version = (runtime_manifest.get("product") or {}).get("version")
    if not isinstance(python_version, str) or backend_version != version:
        raise ValueError("product runtime manifest does not bind this candidate")
    # 实际安装的 Python 分发：从随包 site-packages 的 dist-info 读取
    # Name/Version（importlib.metadata），为每个实际安装分发建 SBOM 关系；
    # 不手写依赖列表、不裁上游包、不叠加全树 hash。
    import importlib.metadata as importlib_metadata

    site_packages = product_root / "runtime/env/runtime-env/Lib/site-packages"
    if not site_packages.is_dir():
        raise ValueError("product closure is missing the bundled site-packages")
    installed = sorted(
        (
            distribution.metadata["Name"],
            distribution.version,
        )
        for distribution in importlib_metadata.distributions(path=[str(site_packages)])
        if distribution.metadata["Name"]
    )
    if not installed:
        raise ValueError("bundled site-packages contains no installed distributions")
    internal = [item for item in installed if item[0] == "vibeocr-next-runtime"]
    if len(internal) != 1 or internal[0][1] != version:
        raise ValueError(
            "the bundled interpreter tree must contain exactly one "
            f"vibeocr-next-runtime=={version}; found {internal}"
        )

    def component(spdx_id: str, name: str, component_version: str, comment: str):
        return {
            "SPDXID": spdx_id,
            "copyrightText": "NOASSERTION",
            "downloadLocation": "NOASSERTION",
            "filesAnalyzed": False,
            "licenseConcluded": "NOASSERTION",
            "licenseDeclared": "NOASSERTION",
            "name": name,
            "versionInfo": component_version,
            "comment": comment,
        }

    product = component(
        "SPDXRef-Package-Product",
        f"{repository_name}-win-x64-portable",
        version,
        "portable product closure bound by app/metadata/product-release-manifest.json "
        f"({len(closure)} files with per-file sha256)",
    )
    dotnet = component(
        "SPDXRef-Package-DotNet",
        "Microsoft.NETCore.App",
        dotnet_version,
        "self-contained .NET runtime files distributed under app/",
    )
    win_app_sdk = component(
        "SPDXRef-Package-WinAppSdk",
        "Microsoft.WindowsAppSDK",
        resolved_war,
        "self-contained Windows App SDK runtime files distributed under app/; "
        "version from the resolved App packages.lock.json graph",
    )
    webview = component(
        "SPDXRef-Package-WebView2",
        "Microsoft.WebView2.FixedVersionRuntime",
        lock["version"],
        f"expanded under app/WebView2Runtime ({len(webview_files)} files; manifest "
        "count == expansion metadata == lock file_count); exe version from the "
        "actual expansion; CAB source locked by "
        "config/runtime/webview2-runtime.lock.json",
    )
    webview["downloadLocation"] = lock["source_url"]
    webview["checksums"] = [{"algorithm": "SHA256", "checksumValue": lock["sha256"]}]
    cpython = component(
        "SPDXRef-Package-CPython",
        "CPython",
        python_version,
        "bundled interpreter tree under runtime/env/runtime-env bound by the "
        "runtime-env tree manifest referenced from runtime/backend/runtime-manifest.json",
    )
    internal_wheel = component(
        "SPDXRef-Package-InternalRuntime",
        "vibeocr-next-runtime",
        internal[0][1],
        "the single internal wheel installed into the bundled interpreter tree; "
        "read from its installed dist-info metadata (not declared from manifest)",
    )
    installed_packages = [
        component(
            f"SPDXRef-Package-Py-{_identifier(name)}-{_identifier(item_version)}",
            name,
            item_version,
            "installed into the bundled interpreter site-packages; bound by the "
            "runtime-env tree manifest and the base/DirectML requirement locks "
            "referenced from the runtime manifest",
        )
        for name, item_version in installed
        if name != "vibeocr-next-runtime"
    ]
    packages = [
        product,
        dotnet,
        win_app_sdk,
        webview,
        cpython,
        internal_wheel,
        *installed_packages,
    ]
    relationships = [
        {
            "relatedSpdxElement": "SPDXRef-Package-Product",
            "relationshipType": "DESCRIBES",
            "spdxElementId": "SPDXRef-DOCUMENT",
        },
        *[
            {
                "relatedSpdxElement": item["SPDXID"],
                "relationshipType": "CONTAINS",
                "spdxElementId": "SPDXRef-Package-Product",
            }
            for item in (
                dotnet,
                win_app_sdk,
                webview,
                cpython,
                internal_wheel,
                *installed_packages,
            )
        ],
    ]
    return packages, relationships


def build_spdx_sbom(
    *,
    artifacts_dir: Path,
    repository_name: str,
    version: str,
    product_root: Path | None = None,
    webview2_metadata: Path | None = None,
) -> Path:
    """Write ``SBOM.spdx.json`` for the regular files in ``artifacts_dir``."""

    if not repository_name.strip():
        raise ValueError("repository_name is required")
    if not version.strip():
        raise ValueError("version is required")

    root = artifacts_dir.resolve(strict=True)
    if not root.is_dir():
        raise ValueError("artifacts_dir must be a directory")

    paths = sorted(
        (
            path
            for path in root.iterdir()
            if path.is_file() and path.name != _OUTPUT_FILENAME
        ),
        key=lambda path: path.name,
    )
    if not paths:
        raise ValueError("artifacts_dir must contain at least one artifact")

    artifacts = tuple(_artifact(path) for path in paths)
    extra_packages: list[dict] = []
    extra_relationships: list[dict] = []
    if product_root is not None:
        extra_packages, extra_relationships = _product_component_packages(
            repository_name=repository_name,
            version=version,
            product_root=product_root.resolve(strict=True),
            webview2_metadata=webview2_metadata,
        )
    files = [
        {
            "SPDXID": f"SPDXRef-File-{index:04d}",
            "checksums": [
                {
                    "algorithm": "SHA256",
                    "checksumValue": artifact.sha256,
                }
            ],
            "comment": f"size={artifact.size}",
            "copyrightText": "NOASSERTION",
            "fileName": artifact.name,
            "licenseConcluded": "NOASSERTION",
            "licenseInfoInFiles": ["NOASSERTION"],
        }
        for index, artifact in enumerate(artifacts, start=1)
    ]
    package_id = "SPDXRef-Package"
    document = {
        "SPDXID": "SPDXRef-DOCUMENT",
        "creationInfo": {
            "created": _CREATED,
            "creators": [_CREATOR],
        },
        "dataLicense": "CC0-1.0",
        "documentNamespace": _document_namespace(
            repository_name,
            version,
            artifacts,
        ),
        "files": files,
        "name": f"{repository_name}-{version}-release-artifacts",
        "packages": [
            {
                "SPDXID": package_id,
                "copyrightText": "NOASSERTION",
                "downloadLocation": "NOASSERTION",
                "filesAnalyzed": True,
                "licenseConcluded": "NOASSERTION",
                "licenseDeclared": "NOASSERTION",
                "name": repository_name,
                "packageVerificationCode": {
                    "packageVerificationCodeValue": (
                        _package_verification_code(artifacts)
                    )
                },
                "versionInfo": version,
            },
            *extra_packages,
        ],
        "relationships": [
            {
                "relatedSpdxElement": package_id,
                "relationshipType": "DESCRIBES",
                "spdxElementId": "SPDXRef-DOCUMENT",
            },
            *extra_relationships,
            *[
                {
                    "relatedSpdxElement": file["SPDXID"],
                    "relationshipType": "CONTAINS",
                    "spdxElementId": package_id,
                }
                for file in files
            ],
        ],
        "spdxVersion": "SPDX-2.3",
    }
    output = root / _OUTPUT_FILENAME
    output.write_bytes(_canonical_json(document))
    return output


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifacts-dir", type=Path, required=True)
    parser.add_argument("--repository-name", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument(
        "--product-root",
        type=Path,
        default=None,
        help="Staged product tree whose distributed runtime components are "
        "declared alongside the release artifacts",
    )
    parser.add_argument(
        "--webview2-metadata",
        type=Path,
        default=None,
        help="JSON with version/file_count captured from the actual expanded "
        "WebView2 runtime tree during release build",
    )
    args = parser.parse_args(argv)
    output = build_spdx_sbom(
        artifacts_dir=args.artifacts_dir,
        repository_name=args.repository_name,
        version=args.version,
        product_root=args.product_root,
        webview2_metadata=args.webview2_metadata,
    )
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
