from __future__ import annotations

import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def test_legacy_winui_build_name_delegates_to_canonical_release() -> None:
    script = (ROOT / "scripts" / "build_winui_release.ps1").read_text(encoding="utf-8")

    assert "build-release.ps1" in script
    for prohibited in (
        "apps\\vibeocr-pyside",
        "packages\\vibeocr-backend",
        "packages\\vibeocr-contracts-py",
        "src\\dotnet\\VibeOCR.slnx",
        "build_release_metadata.py",
    ):
        assert prohibited not in script


def test_dotnet_lock_update_uses_internal_projects_and_isolated_caches() -> None:
    script = (ROOT / "scripts" / "update_dotnet_locks.ps1").read_text(encoding="utf-8")
    package_props = ET.parse(ROOT / "Directory.Packages.props").getroot()
    internal_versions = {
        node.attrib["Include"]
        for node in package_props.iter("PackageVersion")
        if node.attrib.get("Include", "").startswith("VibeOCR.Runtime.")
    }

    for required in (
        "--force-evaluate",
        "--locked-mode",
        "--no-cache",
        "NUGET_PACKAGES",
        "DOTNET_ROOT",
        "VibeOCR.Contracts.Tests.csproj",
        "VibeOCR.Runtime.Client.Tests.csproj",
    ):
        assert required in script
    assert not internal_versions
    assert "FelixJI/vibeocr-protocol" not in script
    assert ".release-input\\protocol-sdk" not in script


def test_windows_app_runtime_installer_tracks_central_windowsappsdk_pin() -> None:
    """The runtime installer must derive from the single central SDK pin."""
    script = (ROOT / "scripts" / "install_windows_app_runtime.ps1").read_text(
        encoding="utf-8"
    )
    package_props = ET.parse(ROOT / "Directory.Packages.props").getroot()
    sdk_version = next(
        node.attrib["Version"]
        for node in package_props.iter("PackageVersion")
        if node.attrib.get("Include") == "Microsoft.WindowsAppSDK"
    )
    band = ".".join(sdk_version.split(".")[:2])

    # 安装器从 central 单一 pin 派生官方 URL，不内嵌版本号；保留 Authenticode
    # 校验与有界下载超时。
    assert "windowsappsdk/$band/$sdkVersion/windowsappruntimeinstall-x64.exe" in script
    assert f"/{band}/{sdk_version}/" not in script
    assert "Get-AuthenticodeSignature" in script
    assert "-TimeoutSec 300" in script


def test_startup_benchmark_only_passes_supported_collector_arguments() -> None:
    wrapper = (ROOT / "scripts" / "benchmark_winui_startup.ps1").read_text(
        encoding="utf-8"
    )
    collector = (ROOT / "scripts" / "collect_startup_metrics.py").read_text(
        encoding="utf-8"
    )

    assert "--name" not in wrapper
    for argument in ("--target", "--runs", "--zip-bytes", "--output"):
        assert argument in wrapper
        assert f'"{argument}"' in collector
