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


def test_dotnet_lock_update_uses_published_packages_and_isolated_caches() -> None:
    script = (ROOT / "scripts" / "update_dotnet_locks.ps1").read_text(encoding="utf-8")
    package_props = ET.parse(ROOT / "Directory.Packages.props").getroot()
    protocol_versions = {
        node.attrib["Version"]
        for node in package_props.iter("PackageVersion")
        if node.attrib.get("Include", "").startswith("VibeOCR.Runtime.")
    }

    for required in (
        "FelixJI/vibeocr-protocol",
        "gh attestation verify",
        "--force-evaluate",
        "--locked-mode",
        "--no-cache",
        "NUGET_PACKAGES",
        "DOTNET_ROOT",
        ".release-input\\protocol-sdk",
        "Directory.Packages.props",
        "$protocolVersion",
        "$protocolPackagePattern",
    ):
        assert required in script
    assert protocol_versions == {"[2.7.1]"}
    assert "v2.7.1" not in script


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
