"""Build a deterministic SPDX 2.3 JSON SBOM for release artifacts."""

from __future__ import annotations

import argparse
import csv
import filecmp
import hashlib
import json
import re
from dataclasses import dataclass
from importlib.metadata import distributions
from pathlib import Path, PurePosixPath, PureWindowsPath
from urllib.parse import quote

_OUTPUT_FILENAME = "SBOM.spdx.json"
_CREATED = "1970-01-01T00:00:00Z"
_CREATOR = "Tool: build_spdx_sbom.py"
_REPO_ROOT = Path(__file__).resolve().parents[1]
_WEBVIEW2_LOCK = _REPO_ROOT / "config" / "runtime" / "webview2-runtime.lock.json"
_PYTHON_LOCK = _REPO_ROOT / "config" / "runtime" / "python-runtime.lock.json"
_APP_LOCK = _REPO_ROOT / "src/dotnet/VibeOCR.App/packages.lock.json"
_WEB_ASSETS = _REPO_ROOT / "src/dotnet/VibeOCR.App/WebAssets"


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


def _json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _require_file(product: Path, closure: dict, relative: str) -> None:
    if relative not in closure or not (product / relative).is_file():
        raise ValueError(f"product closure is missing {relative}")


def _component(
    identifier: str,
    name: str,
    version: str,
    source: str,
    *,
    purl: str | None = None,
    download: str = "NOASSERTION",
) -> dict:
    package = {
        "SPDXID": f"SPDXRef-Package-{identifier}",
        "copyrightText": "NOASSERTION",
        "downloadLocation": download,
        "filesAnalyzed": False,
        "licenseConcluded": "NOASSERTION",
        "licenseDeclared": "NOASSERTION",
        "name": name,
        "versionInfo": version,
        "sourceInfo": source,
    }
    if purl:
        package["externalRefs"] = [
            {
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": purl,
            }
        ]
    return package


def _relationship(source: str, kind: str, target: str) -> dict:
    return {
        "spdxElementId": source,
        "relationshipType": kind,
        "relatedSpdxElement": target,
    }


def _python_name(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def _python_components(product: Path, closure: dict, version: str) -> list[dict]:
    backend = "runtime/backend"
    runtime_path = f"{backend}/runtime-manifest.json"
    _require_file(product, closure, runtime_path)
    runtime = _json(product / runtime_path)
    python = runtime["python"]
    python_lock = _json(_PYTHON_LOCK)
    if runtime["product"]["version"] != version or any(
        python[key] != python_lock[key] for key in ("version", "platform", "source_url")
    ):
        raise ValueError(
            "product runtime manifest does not match the candidate and CPython lock"
        )
    python_version = python["version"]
    tree = f"runtime/env/{runtime['runtime_env']['root']}"
    site = product / tree / "Lib/site-packages"
    _require_file(product, closure, f"{tree}/python.exe")
    python_abi = "".join(python_version.split(".")[:2])
    _require_file(product, closure, f"{tree}/python{python_abi}.dll")
    _require_file(product, closure, f"{backend}/{runtime['runtime_env']['manifest']}")
    native = sorted(
        relative
        for relative in closure
        if relative.startswith(f"{tree}/")
        and not relative.startswith(f"{tree}/Lib/site-packages/")
        and relative.lower().endswith((".dll", ".pyd"))
    )
    for relative in native:
        _require_file(product, closure, relative)
    locked = {}
    for entry in (
        *runtime["profiles"].values(),
        *runtime["paddle_onnx_environments"].values(),
    ):
        lock_path = f"{backend}/{entry['lock']}"
        _require_file(product, closure, lock_path)
        for name, pin in re.findall(
            r"(?m)^([A-Za-z0-9][A-Za-z0-9._-]*)==(\S+)",
            (product / lock_path).read_text(encoding="utf-8"),
        ):
            locked.setdefault((_python_name(name), pin), []).append(lock_path)
    if not locked:
        raise ValueError("bundled Python requirement locks contain no pins")
    packages = [
        _component(
            "CPython",
            "CPython",
            python_version,
            f"config/runtime/python-runtime.lock.json; installed at {tree}/; "
            f"native files: {', '.join(native)}",
            download=python_lock["source_url"],
        )
    ]
    installed = set()
    wheel = "vibeocr-next-runtime"
    for distribution in sorted(
        distributions(path=[str(site)]), key=lambda item: item.metadata["Name"] or ""
    ):
        name = distribution.metadata["Name"]
        pin = distribution.version
        if not name or not pin:
            raise ValueError(
                "bundled Python distribution lacks Name or Version metadata"
            )
        key = (_python_name(name), pin)
        if key in installed:
            raise ValueError(f"duplicate bundled Python distribution: {name}=={pin}")
        installed.add(key)
        if key[0] == wheel:
            if pin != version:
                raise ValueError(f"expected {wheel}=={version}; found {pin}")
            source = "internal wheel built from the candidate"
            identifier = "InternalRuntime"
        elif key in locked:
            source = ", ".join(sorted(locked[key]))
            identifier = f"Py-{_identifier(key[0])}-{_identifier(pin)}"
        elif key[0] == "pip":
            # The locked install-only CPython archive includes pip.
            source = "bundled by the locked CPython archive"
            identifier = f"Py-pip-{_identifier(pin)}"
        else:
            raise ValueError(
                f"installed Python distribution is not locked: {name}=={pin}"
            )
        metadata_paths = []
        native_paths = []
        record = distribution.read_text("RECORD")
        if not record:
            raise ValueError(f"bundled Python distribution lacks RECORD: {name}")
        # importlib.metadata.files drops missing files, which would hide damage.
        for relative_name, _, _ in csv.reader(record.splitlines()):
            path = PurePosixPath(relative_name)
            is_metadata = (
                len(path.parts) == 2
                and path.name == "METADATA"
                and path.parent.name.endswith(".dist-info")
            )
            if is_metadata or path.suffix.lower() in {".dll", ".pyd"}:
                relative = (
                    distribution.locate_file(path)
                    .resolve()
                    .relative_to(product)
                    .as_posix()
                )
                _require_file(product, closure, relative)
                (metadata_paths if is_metadata else native_paths).append(relative)
        if len(metadata_paths) != 1:
            raise ValueError(
                f"bundled Python RECORD does not identify METADATA: {name}"
            )
        packages.append(
            _component(
                identifier,
                name,
                pin,
                f"{source}; installed metadata: {metadata_paths[0]}; "
                f"native files from RECORD: {', '.join(sorted(native_paths)) or 'none'}",
                purl=f"pkg:pypi/{key[0]}@{quote(pin, safe='')}",
            )
        )
    if (wheel, version) not in installed:
        raise ValueError(f"bundled Python tree must contain {wheel}=={version}")
    if missing := locked.keys() - installed:
        raise ValueError(f"locked Python distributions are missing: {sorted(missing)}")
    return packages


def _web_components(
    product: Path, closure: dict, version: str
) -> tuple[list[dict], list[dict]]:
    prefix = "app/WebAssets/"
    dist = _WEB_ASSETS / "dist"
    built = {
        prefix + path.relative_to(dist).as_posix()
        for path in dist.rglob("*")
        if path.is_file()
    }
    if f"{prefix}index.html" not in built or not any(
        name.endswith(".js") for name in built
    ):
        raise ValueError(
            "WebAssets production dist must contain index.html and JavaScript"
        )
    manifest_files = {path for path in closure if path.startswith(prefix)}
    if built != manifest_files:
        raise ValueError(
            f"WebAssets production closure differs from dist: "
            f"missing={sorted(built - manifest_files)} extra={sorted(manifest_files - built)}"
        )
    for relative in sorted(built):
        _require_file(product, closure, relative)
        if not filecmp.cmp(
            dist / relative.removeprefix(prefix), product / relative, shallow=False
        ):
            raise ValueError(
                f"published WebAssets content differs from dist: {relative}"
            )
    web = _component(
        "WebAssets",
        "VibeOCR.WebAssets",
        version,
        "Static outputs of the production Vite build, bound by the product release manifest: "
        + ", ".join(sorted(built)),
    )
    package_lock = _json(_WEB_ASSETS / "package-lock.json")["packages"]
    manifest_dependencies = _json(_WEB_ASSETS / "package.json")["dependencies"]
    if (
        not manifest_dependencies
        or manifest_dependencies != package_lock[""]["dependencies"]
    ):
        raise ValueError("WebAssets production inputs disagree with package-lock.json")
    inputs = []
    for index, (path, pin) in enumerate(sorted(package_lock.items()), 1):
        if not path or pin.get("dev"):
            continue
        installed_manifest = _WEB_ASSETS / path / "package.json"
        if pin.get("optional") and not installed_manifest.is_file():
            continue
        if not installed_manifest.is_file():
            raise ValueError(f"locked WebAssets production input is missing: {path}")
        installed = _json(installed_manifest)
        name, resolved = installed["name"], pin["version"]
        if installed["version"] != resolved:
            raise ValueError(
                f"WebAssets production input does not match the lock: {path}"
            )
        inputs.append(
            _component(
                f"Npm-{index}",
                name,
                resolved,
                f"Installed production input from WebAssets/package-lock.json: {path}; "
                "Rollup may tree-shake unused input code",
                purl=f"pkg:npm/{quote(name, safe='/')}@{quote(resolved, safe='')}",
                download=pin["resolved"],
            )
        )
    if not inputs:
        raise ValueError("WebAssets lock resolves no installed production inputs")
    return [web, *inputs], [
        _relationship(item["SPDXID"], "BUILD_DEPENDENCY_OF", web["SPDXID"])
        for item in inputs
    ]


def _app_components(
    product: Path,
    closure: dict,
    entry: str,
    app_publish_items: Path | None,
    loader_version: str,
) -> list[dict]:
    deps_name = PurePosixPath(entry).with_suffix(".deps.json").as_posix()
    config_name = PurePosixPath(entry).with_suffix(".runtimeconfig.json").as_posix()
    for relative in (entry, deps_name, config_name):
        _require_file(product, closure, relative)
    deps = _json(product / deps_name)
    active = deps["runtimeTarget"]["name"]
    rid = active.rsplit("/", 1)[-1]
    target = deps["targets"][active]
    if rid != "win-x64":
        raise ValueError(
            f"expected the win-x64 published app deps.json target; found {active}"
        )

    lock = _json(_APP_LOCK)["dependencies"]
    base_target = next(name for name in lock if "/" not in name)
    resolved = {
        name.lower(): (name, item["resolved"])
        for name, item in (lock[base_target] | lock[f"{base_target}/{rid}"]).items()
        if "resolved" in item
    }
    app_prefix = PurePosixPath(entry).parent
    nuget_assets: dict[tuple[str, str], set[str]] = {}
    packages = []
    dotnet_key = f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}"
    runtime_options = _json(product / config_name)["runtimeOptions"]
    dotnet_version = next(
        (
            item["version"]
            for item in runtime_options.get("includedFrameworks", [])
            if item["name"] == "Microsoft.NETCore.App"
        ),
        None,
    )
    if not dotnet_version or f"{dotnet_key}/{dotnet_version}" not in target:
        raise ValueError(
            "product self-contained runtimeconfig and .NET runtime pack disagree"
        )
    loader = ("Microsoft.Web.WebView2", loader_version)
    loader_dll = (app_prefix / "WebView2Loader.dll").as_posix()
    loader_declared = False
    for key, payload in sorted(target.items()):
        name, pin = key.rsplit("/", 1)
        kind = deps["libraries"][key]["type"]
        if kind == "package":
            if resolved.get(name.lower(), (None, None))[1] != pin:
                raise ValueError(
                    f"published NuGet package does not match the RID lock: {key}"
                )
            name = resolved[name.lower()][0]
        assets = set()
        for section in ("runtime", "native", "resources"):
            for source, metadata in sorted(payload.get(section, {}).items()):
                leaf = PurePosixPath(source).name
                if leaf == "_._":
                    continue
                output = (
                    app_prefix / metadata["locale"]
                    if section == "resources"
                    else app_prefix
                )
                relative = (output / leaf).as_posix()
                _require_file(product, closure, relative)
                assets.add(relative)
                if kind == "package" and (name, pin) == loader and section == "native":
                    loader_declared |= relative == loader_dll
        if assets and kind == "package":
            nuget_assets[(name, pin)] = assets
        if assets and kind == "runtimepack":
            runtime_pack = name.removeprefix("runtimepack.")
            packages.append(
                _component(
                    "DotNet"
                    if name == dotnet_key
                    else f"RuntimePack-{_identifier(runtime_pack)}",
                    "Microsoft.NETCore.App" if name == dotnet_key else runtime_pack,
                    pin,
                    f"{deps_name} runtime pack {runtime_pack}: {', '.join(sorted(assets))}",
                )
            )
    if not loader_declared:
        raise ValueError(
            "published WebView2 loader does not declare the locked native WebView2Loader.dll"
        )
    if not any(item["SPDXID"] == "SPDXRef-Package-DotNet" for item in packages):
        raise ValueError("published .NET runtime pack declares no distributed files")

    # Windows App SDK copies its framework payload via MSBuild outside the deps.json graph.
    if app_publish_items is None:
        raise ValueError(
            "product_root requires --app-publish-items from the actual publish"
        )
    publish = _json(app_publish_items)
    roots = [
        PureWindowsPath(path)
        for path in publish["Properties"]["NuGetPackageFolders"].split(";")
        if path
    ]

    def nuget_origin(source: PureWindowsPath) -> PureWindowsPath | None:
        return next(
            (source.relative_to(root) for root in roots if source.is_relative_to(root)),
            None,
        )

    war_payload = set()
    for group in ("MicrosoftWindowsAppSDKFiles", "MicrosoftWindowsAppSDKFilesRes"):
        for item in publish["Items"][group]:
            source = PureWindowsPath(item["FullPath"])
            origin = nuget_origin(source)
            if origin is None or len(origin.parts) < 3:
                raise ValueError(
                    "Windows App SDK publish item is not from a resolved NuGet package"
                )
            package, pin = origin.parts[:2]
            resolved_item = resolved.get(package.lower())
            if resolved_item is None or resolved_item[1] != pin:
                raise ValueError(
                    f"Windows App SDK publish item does not match the lock: {package}/{pin}"
                )
            relative = (
                app_prefix
                / PureWindowsPath(item["RecursiveDir"]).as_posix()
                / source.name
            ).as_posix()
            _require_file(product, closure, relative)
            war_payload.add(relative)
            nuget_assets.setdefault(resolved_item, set()).add(relative)
    for item in publish["Items"]["ResolvedFileToPublish"]:
        # SDK targets can arrange NuGet copy-local files outside the deps graph.
        source = PureWindowsPath(item["FullPath"])
        origin = nuget_origin(source)
        if (
            origin is None
            or len(origin.parts) < 3
            or origin.parts[0].lower() not in resolved
        ):
            continue
        package, pin = origin.parts[:2]
        resolved_item = resolved[package.lower()]
        if resolved_item[1] != pin:
            raise ValueError(
                f"published NuGet copy-local item does not match the lock: {package}/{pin}"
            )
        relative = (
            app_prefix / PureWindowsPath(item["RelativePath"]).as_posix()
        ).as_posix()
        _require_file(product, closure, relative)
        nuget_assets.setdefault(resolved_item, set()).add(relative)
    if not war_payload:
        raise ValueError(
            "published Windows App SDK item list contains no distributed files"
        )
    war = resolved.get("microsoft.windowsappsdk")
    if war is None or f"{war[0]}/{war[1]}" not in target:
        raise ValueError("published Windows App SDK does not match the lock")
    packages.append(
        _component(
            "WinAppSdk",
            *war,
            "Self-contained Windows App SDK from the RID lock and actual publish items: "
            + ", ".join(sorted(war_payload | nuget_assets.pop(war, set()))),
            purl=f"pkg:nuget/{war[0]}@{war[1]}",
        )
    )
    packages.extend(
        _component(
            f"NuGet-{_identifier(name)}-{_identifier(pin)}",
            name,
            pin,
            "Locked NuGet package with files bound by the app deps.json or "
            f"MSBuild publish items: {', '.join(sorted(assets))}",
            purl=f"pkg:nuget/{name}@{pin}",
        )
        for (name, pin), assets in sorted(nuget_assets.items())
    )
    return packages


def _product_component_packages(
    *,
    repository_name: str,
    version: str,
    product_root: Path,
    webview2_metadata: Path | None,
    app_publish_items: Path | None,
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
    for relative in webview_files:
        _require_file(product_root, closure, relative)
    descriptor = _json(app_root / "metadata/product-layout.json")
    entry = descriptor.get("app", {}).get("entry")
    if not isinstance(entry, str):
        raise ValueError("product layout descriptor is missing app.entry")
    product = _component(
        "Product",
        f"{repository_name}-win-x64-portable",
        version,
        "Portable product closure bound by app/metadata/product-release-manifest.json "
        f"({len(closure)} files)",
    )
    webview = _component(
        "WebView2",
        "Microsoft.WebView2.FixedVersionRuntime",
        lock["version"],
        f"Expanded under app/WebView2Runtime ({len(webview_files)} files); "
        "actual expansion version/count and final manifest agree with "
        "config/runtime/webview2-runtime.lock.json",
        download=lock["source_url"],
    )
    webview["checksums"] = [{"algorithm": "SHA256", "checksumValue": lock["sha256"]}]
    web_packages, web_relationships = _web_components(product_root, closure, version)
    distributed = [
        webview,
        *_app_components(
            product_root, closure, entry, app_publish_items, lock["loader_sdk_version"]
        ),
        *_python_components(product_root, closure, version),
        web_packages[0],
    ]
    return [product, *distributed, *web_packages[1:]], [
        _relationship("SPDXRef-DOCUMENT", "DESCRIBES", product["SPDXID"]),
        *(
            _relationship(product["SPDXID"], "CONTAINS", item["SPDXID"])
            for item in distributed
        ),
        *web_relationships,
    ]


def build_spdx_sbom(
    *,
    artifacts_dir: Path,
    repository_name: str,
    version: str,
    product_root: Path | None = None,
    webview2_metadata: Path | None = None,
    app_publish_items: Path | None = None,
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
            app_publish_items=app_publish_items,
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
    parser.add_argument(
        "--app-publish-items",
        type=Path,
        help="Windows App SDK framework file items captured by the actual MSBuild publish",
    )
    args = parser.parse_args(argv)
    output = build_spdx_sbom(
        artifacts_dir=args.artifacts_dir,
        repository_name=args.repository_name,
        version=args.version,
        product_root=args.product_root,
        webview2_metadata=args.webview2_metadata,
        app_publish_items=args.app_publish_items,
    )
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
