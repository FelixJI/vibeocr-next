from __future__ import annotations

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

    for required in (
        "FelixJI/vibeocr-protocol",
        "gh attestation verify",
        "--force-evaluate",
        "--locked-mode",
        "--no-cache",
        "NUGET_PACKAGES",
    ):
        assert required in script


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
