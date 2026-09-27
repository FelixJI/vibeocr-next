"""Next 单一质量入口与候选门禁的静态回归测试。"""

import json
import os
import subprocess
from pathlib import Path

REPO_ROOT = Path(__file__).parents[2]


def test_ci_runs_the_unified_full_quality_gate() -> None:
    workflow = (REPO_ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    config = json.loads((REPO_ROOT / ".ci/project.json").read_text(encoding="utf-8"))
    script = (REPO_ROOT / "scripts/check_quality.py").read_text(encoding="utf-8")

    assert "--phase plan" in workflow
    assert "--phase finalize" in workflow
    assert "name: required" in workflow
    assert "python -m pip install" not in workflow
    assert config["ci"]["bootstrap"][0][-3:] == ["uv", "sync", "--frozen"]
    assert config["ci"]["quality"][0][-1] == "scripts/check_quality.py"
    assert {
        "tests/dotnet/VibeOCR.Contracts.Tests/VibeOCR.Contracts.Tests.csproj",
        "tests/dotnet/VibeOCR.Runtime.Client.Tests/VibeOCR.Runtime.Client.Tests.csproj",
    } <= {argument for command in config["ci"]["e2e"] for argument in command}
    for required in (
        "src/runtime",
        "contracts/runtime/python",
        "tests/python",
        "generate_runtime_protocol.py",
        "check_openapi_quality.py",
        "check_runtime_protocol_conformance.py",
    ):
        assert required in script


def test_release_verifies_runtime_candidate_after_build() -> None:
    workflow = (REPO_ROOT / ".github/workflows/cd.yml").read_text(encoding="utf-8")
    config = json.loads((REPO_ROOT / ".ci/project.json").read_text(encoding="utf-8"))
    build_script = (REPO_ROOT / "scripts/build-release.ps1").read_text(encoding="utf-8")

    download = workflow.index("name: Download exact CI candidate")
    stage = workflow.index("name: Stage release")
    publish = workflow.index("name: Publish and reconcile release")
    assert download < stage < publish
    assert config["ci"]["release_build"][0][-1] == "scripts/build-release.ps1"
    assert config["ci"]["release_smoke"][0][-1] == "scripts/release_smoke.py"
    assert config["release"]["identity_asset"] == "product-identity.json"
    assert "VibeOCRNext-v{version}-win-x64.zip" in config["release"]["required_assets"]
    assert all(
        "scripts/resolve_component_releases.py" not in command
        for command in config["ci"]["bootstrap"]
    )
    assert "scripts/build_internal_runtime.ps1" in build_script
    assert "New-Item -ItemType Directory -Path $artifacts -Force" in build_script
    assert "Remove-Item -LiteralPath $artifacts" not in build_script
    assert "VibeOCRNext" in build_script


def test_release_build_rejects_a_different_checkout_before_touching_outputs(
    tmp_path: Path,
) -> None:
    sibling = tmp_path / ".release-build" / "sibling.txt"
    sibling.parent.mkdir()
    sibling.write_text("preserve", encoding="utf-8")
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-File", str(REPO_ROOT / "scripts/build-release.ps1")],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
        env=os.environ | {"AUTOMATION_PROJECT_ROOT": str(tmp_path)},
    )
    assert result.returncode != 0
    assert "must use the checkout" in result.stderr
    assert sibling.read_text(encoding="utf-8") == "preserve"


def test_runtime_builder_rejects_output_outside_fixed_build_root(
    tmp_path: Path,
) -> None:
    sibling = tmp_path / "sibling.txt"
    sibling.write_text("preserve", encoding="utf-8")
    result = subprocess.run(
        [
            "pwsh",
            "-NoProfile",
            "-File",
            str(REPO_ROOT / "scripts/build_internal_runtime.ps1"),
            "-Version",
            "0.0.0",
            "-BuildRoot",
            str(tmp_path),
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    assert result.returncode != 0
    assert "must be the checkout .release-build directory" in result.stderr
    assert sibling.read_text(encoding="utf-8") == "preserve"
