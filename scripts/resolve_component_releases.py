"""解析最新正式 Backend Release 及其绑定 Protocol，写入可审计组件 identity。"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import urllib.request
from pathlib import Path
from typing import Any
from xml.etree import ElementTree

if __package__:
    from .bind_component_releases import (
        bind_product_releases,
        protocol_manifest_version,
    )
else:
    from bind_component_releases import bind_product_releases, protocol_manifest_version

ROOT = Path(__file__).resolve().parents[1]
SEMVER = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)$")


def _api(repository: str, path: str) -> Any:
    token = os.environ.get("GITHUB_TOKEN", "")
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repository}{path}",
        headers={
            "Accept": "application/vnd.github+json",
            **({"Authorization": f"Bearer {token}"} if token else {}),
        },
    )
    with urllib.request.urlopen(request) as response:  # noqa: S310
        return json.loads(response.read())


def _sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _version(value: str) -> tuple[int, int, int]:
    match = SEMVER.fullmatch(value)
    if match is None:
        raise ValueError(f"stable SemVer tag required: {value}")
    return tuple(map(int, match.groups()))


def assert_protocol_compatible(version: str, compatibility: dict[str, Any]) -> None:
    major, _, _ = _version(version)
    if not compatibility.get("minor_compatible") or major not in compatibility.get(
        "supported_majors", []
    ):
        raise ValueError(f"unsupported Protocol {version}; no fallback is allowed")


def compile_protocol_version(root: Path) -> str:
    """Return the single pinned Protocol SDK version used for compilation."""
    versions = {
        element.attrib["Version"].removeprefix("[").removesuffix("]")
        for element in ElementTree.parse(root / "Directory.Packages.props").iter(
            "PackageVersion"
        )
        if element.attrib.get("Include", "").startswith("VibeOCR.Runtime.")
    }
    if len(versions) != 1:
        raise ValueError(
            f"one pinned Protocol SDK version required: {sorted(versions)}"
        )
    version = versions.pop()
    _version(version)
    return version


def _download(release: dict[str, Any], destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    token = os.environ.get("GITHUB_TOKEN", "")
    for asset in release["assets"]:
        request = urllib.request.Request(
            asset["browser_download_url"],
            headers={
                "Accept": "application/octet-stream",
                **({"Authorization": f"Bearer {token}"} if token else {}),
            },
        )
        with urllib.request.urlopen(request) as response:  # noqa: S310
            (destination / asset["name"]).write_bytes(response.read())


def _source_sha(repository: str, tag: str, release: dict[str, Any]) -> str:
    candidate = str(release.get("target_commitish", ""))
    if re.fullmatch(r"[0-9a-f]{40}", candidate):
        return candidate
    ref = _api(repository, f"/git/ref/tags/{tag}")["object"]
    if ref["type"] == "tag":
        ref = _api(repository, f"/git/tags/{ref['sha']}")["object"]
    if ref["type"] != "commit" or not re.fullmatch(r"[0-9a-f]{40}", ref["sha"]):
        raise ValueError(f"cannot resolve source SHA for {repository}@{tag}")
    return ref["sha"]


def bound_protocol_version(backend_release_dir: Path) -> str:
    """Read and validate the Protocol identity bundled by Backend."""
    manifest = json.loads(
        (backend_release_dir / "protocol-release-manifest.json").read_text(
            encoding="utf-8"
        )
    )
    if not isinstance(manifest, dict):
        raise ValueError("Backend bound Protocol manifest must be an object")
    version = protocol_manifest_version(manifest)
    _version(version)
    return version


def resolve(root: Path = ROOT) -> Path:
    config = json.loads((root / ".ci/project.json").read_text(encoding="utf-8"))
    artifacts = Path(os.environ["AUTOMATION_ARTIFACTS_DIR"]).resolve()
    work = root / ".release-input"
    if work.exists():
        shutil.rmtree(work)
    backend_repo, protocol_repo = "FelixJI/vibeocr-backend", "FelixJI/vibeocr-protocol"
    backend = _api(backend_repo, "/releases/latest")
    if backend.get("prerelease") or backend.get("draft"):
        raise ValueError("latest Backend release is not formal")
    backend_version = backend["tag_name"].removeprefix("v")
    _version(backend_version)
    _download(backend, work / "backend")
    protocol_version = bound_protocol_version(work / "backend")
    assert_protocol_compatible(
        protocol_version, config["project"]["protocol_compatibility"]
    )
    protocol = _api(protocol_repo, f"/releases/tags/v{protocol_version}")
    if protocol.get("prerelease") or protocol.get("draft"):
        raise ValueError("bound Protocol release is not formal")
    _download(protocol, work / "protocol")
    sdk_version = compile_protocol_version(root)
    assert_protocol_compatible(sdk_version, config["project"]["protocol_compatibility"])
    if _version(sdk_version) > _version(protocol_version):
        raise ValueError(
            f"Protocol SDK {sdk_version} is newer than bound runtime {protocol_version}"
        )
    sdk_release = (
        protocol
        if sdk_version == protocol_version
        else _api(protocol_repo, f"/releases/tags/v{sdk_version}")
    )
    if sdk_release.get("prerelease") or sdk_release.get("draft"):
        raise ValueError("Protocol SDK release is not formal")
    _download(sdk_release, work / "protocol-sdk")
    lock = artifacts / "component-lock.json"
    bind_product_releases(
        protocol_release_dir=work / "protocol",
        backend_release_dir=work / "backend",
        protocol_repository=protocol_repo,
        protocol_version=protocol_version,
        backend_repository=backend_repo,
        backend_version=backend_version,
        accelerator="cpu",
        required_capabilities=(
            "ocr.recognition.v2",
            "pdf.edit.v2",
            "qrcode.v2",
            "export.document.v1",
            "runtime.maintenance.v1",
            "runtime.settings.v2",
            "task.progress.v1",
        ),
        output=lock,
    )
    backend_identity = {
        "repository": backend_repo,
        "version": backend_version,
        "source_sha": _source_sha(backend_repo, backend["tag_name"], backend),
        "runtime_manifest_sha256": _sha(work / "backend" / "runtime-manifest.json"),
    }
    backend_release_manifest = work / "backend" / "release-manifest.json"
    if backend_release_manifest.is_file():
        backend_identity["release_manifest_sha256"] = _sha(backend_release_manifest)
    identity = {
        "schema_version": 1,
        "backend": backend_identity,
        "protocol": {
            "repository": protocol_repo,
            "version": protocol_version,
            "source_sha": _source_sha(protocol_repo, protocol["tag_name"], protocol),
            "release_manifest_sha256": _sha(
                work / "protocol" / "release-manifest.json"
            ),
        },
        "component_lock_sha256": _sha(lock),
    }
    output = artifacts / "component-identities.json"
    output.write_text(
        json.dumps(identity, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    return output


if __name__ == "__main__":
    print(resolve())
