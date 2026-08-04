from __future__ import annotations

import hashlib
import json
import subprocess
import zipfile
from pathlib import Path

import pytest

from scripts.automation_core import CommandRunner
from scripts.check_quality import resolve_executable
from scripts.release_smoke import verify
from scripts.resolve_component_releases import (
    assert_protocol_compatible,
    compile_protocol_version,
)
from scripts.sync_version import sync_version


def test_protocol_compatibility_requires_declared_major_and_minor() -> None:
    compatibility = {"supported_majors": [2], "minor_compatible": True}
    assert_protocol_compatible("2.0.0", compatibility)
    assert_protocol_compatible("2.99.0", compatibility)
    with pytest.raises(ValueError, match="no fallback"):
        assert_protocol_compatible("3.0.0", compatibility)


def test_compile_sdk_version_is_pinned_independently_from_runtime(
    tmp_path: Path,
) -> None:
    (tmp_path / "Directory.Packages.props").write_text(
        """<Project><ItemGroup>
        <PackageVersion Include="VibeOCR.Runtime.Contracts" Version="[2.0.0]" />
        <PackageVersion Include="VibeOCR.Runtime.Client" Version="[2.0.0]" />
        </ItemGroup></Project>""",
        encoding="utf-8",
    )

    assert compile_protocol_version(tmp_path) == "2.0.0"


def test_command_runner_resolves_platform_command_shims(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    calls: list[list[str]] = []

    monkeypatch.setattr(
        "scripts.automation_core.shutil.which",
        lambda command, **kwargs: "C:/node/npm.cmd" if command == "npm" else None,
    )

    def fake_run(
        command: list[str], **kwargs: object
    ) -> subprocess.CompletedProcess[str]:
        calls.append(command)
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setattr("scripts.automation_core.subprocess.run", fake_run)
    CommandRunner(tmp_path).run(["npm", "ci"])

    assert calls == [["C:/node/npm.cmd", "ci"]]


def test_quality_script_resolves_platform_command_shims(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(
        "scripts.check_quality.shutil.which",
        lambda command: "C:/node/npm.cmd" if command == "npm" else None,
    )

    assert resolve_executable("npm") == "C:/node/npm.cmd"


def test_project_config_declares_minor_compatible_protocol_and_single_identity_asset() -> (
    None
):
    root = Path(__file__).parents[2]
    config = json.loads((root / ".ci/project.json").read_text(encoding="utf-8"))
    assert config["project"]["protocol_compatibility"] == {
        "supported_majors": [2],
        "minor_compatible": True,
    }
    assert config["release"]["identity_asset"] == "component-identities.json"
    assert "component-lock.json" in config["release"]["required_assets"]
    bootstrap = config["ci"]["bootstrap"]
    assert ["pwsh", "-File", "scripts/install_windows_app_runtime.ps1"] in bootstrap
    resolver_index = bootstrap.index(
        ["python", "scripts/resolve_component_releases.py"]
    )
    restore_indexes = [
        index
        for index, command in enumerate(bootstrap)
        if command[:2] == ["dotnet", "restore"]
    ]
    assert restore_indexes and resolver_index < min(restore_indexes)
    assert ["python", "scripts/resolve_component_releases.py"] not in config["ci"][
        "e2e"
    ]
    build_script = (root / "scripts/build-release.ps1").read_text(encoding="utf-8")
    assert build_script.count("build_release_checksums.py") == 1
    resolver = (root / "scripts/resolve_component_releases.py").read_text(
        encoding="utf-8"
    )
    assert 'work = root / ".release-input"' in resolver
    assert "$inputs = Join-Path $root '.release-input'" in build_script
    nuget = (root / "NuGet.Config").read_text(encoding="utf-8")
    assert 'value=".release-input/protocol-sdk"' in nuget


def test_backend_identity_hashes_runtime_and_optional_release_manifests() -> None:
    root = Path(__file__).parents[2]
    resolver = (root / "scripts/resolve_component_releases.py").read_text(
        encoding="utf-8"
    )

    assert (
        '"runtime_manifest_sha256": _sha(work / "backend" / "runtime-manifest.json")'
        in resolver
    )
    assert (
        '"release_manifest_sha256": _sha(work / "backend" / "release-manifest.json")'
        not in resolver
    )
    assert 'backend_identity["release_manifest_sha256"] = _sha(' in resolver
    assert "if backend_release_manifest.is_file():" in resolver


def test_sync_version_updates_repository_and_desktop_project(tmp_path: Path) -> None:
    (tmp_path / "src/dotnet/VibeOCR.App").mkdir(parents=True)
    (tmp_path / "repository.json").write_text(
        '{"version":"0.1.0-preview.1"}', encoding="utf-8"
    )
    project = tmp_path / "src/dotnet/VibeOCR.App/VibeOCR.App.csproj"
    project.write_text(
        "<Project><PropertyGroup><Version>0.1.0-preview.1</Version></PropertyGroup></Project>",
        encoding="utf-8",
    )
    sync_version(tmp_path, "0.2.0")
    assert (
        json.loads((tmp_path / "repository.json").read_text(encoding="utf-8"))[
            "version"
        ]
        == "0.2.0"
    )
    assert "<Version>0.2.0</Version>" in project.read_text(encoding="utf-8")


def test_release_smoke_binds_real_archive_and_component_identity(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    archive = tmp_path / "VibeOCR-Next-v0.2.0-win64.zip"
    with zipfile.ZipFile(archive, "w") as package:
        package.writestr("VibeOCR.Next/VibeOCR.WinUI.exe", b"desktop")
    (tmp_path / "component-lock.json").write_text("{}", encoding="utf-8")
    (tmp_path / "component-identities.json").write_text(
        json.dumps(
            {
                "backend": {"version": "1.0.0", "source_sha": "a" * 40},
                "protocol": {"version": "2.0.0", "source_sha": "b" * 40},
            }
        ),
        encoding="utf-8",
    )
    (tmp_path / "SBOM.spdx.json").write_text("{}", encoding="utf-8")
    sidecar = archive.with_name(archive.name + ".sha256")
    sidecar.write_text(
        f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n",
        encoding="utf-8",
    )
    monkeypatch.setattr(
        "scripts.release_smoke.subprocess.run", lambda *args, **kwargs: None
    )
    verify(tmp_path)

    (tmp_path / "unexpected.txt").write_text("unexpected", encoding="utf-8")
    with pytest.raises(ValueError, match="release asset set mismatch"):
        verify(tmp_path)


def test_only_canonical_workflows_remain() -> None:
    root = Path(__file__).parents[2]
    assert {path.name for path in (root / ".github/workflows").glob("*.yml")} == {
        "ci.yml",
        "cd.yml",
    }


def test_publish_checkout_keeps_job_token_for_git_tag_push() -> None:
    root = Path(__file__).parents[2]
    workflow = (root / ".github/workflows/cd.yml").read_text(encoding="utf-8")
    publish_job = workflow.split("\n  publish:\n", maxsplit=1)[1]
    permissions = publish_job.split("\n    permissions:\n", maxsplit=1)[1].split(
        "\n    steps:\n", maxsplit=1
    )[0]
    checkout = publish_job.split("- uses: actions/checkout@", maxsplit=1)[1].split(
        "- uses: actions/setup-python@", maxsplit=1
    )[0]

    assert {line.strip() for line in permissions.splitlines() if line.strip()} == {
        "actions: read",
        "attestations: write",
        "contents: write",
        "id-token: write",
    }
    assert "persist-credentials: true" in checkout
    assert "persist-credentials: false" not in checkout
    assert "token:" not in checkout
