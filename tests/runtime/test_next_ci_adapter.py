from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import zipfile
from pathlib import Path

import pytest

from scripts.automation_core import CommandRunner
from scripts.build_product_binding import REQUIRED_CAPABILITIES
from scripts.check_quality import main as run_quality
from scripts.check_quality import resolve_executable
from scripts.release_smoke import verify
from scripts.sync_version import sync_version


def _write_velopack_feed(artifacts: Path, version: str) -> None:
    assets: list[dict[str, object]] = []
    for kind in ("Full", "Delta"):
        path = artifacts / f"VibeOCRNext-{version}-{kind.casefold()}.nupkg"
        if not path.is_file():
            continue
        assets.append(
            {
                "PackageId": "VibeOCRNext",
                "Version": version,
                "Type": kind,
                "FileName": path.name,
                "SHA1": hashlib.sha1(path.read_bytes()).hexdigest().upper(),
                "SHA256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
                "Size": path.stat().st_size,
            }
        )
    (artifacts / "releases.win.json").write_text(
        json.dumps({"Assets": assets}), encoding="utf-8"
    )


def test_product_binding_requires_selection_and_mineru_capabilities() -> None:
    required = REQUIRED_CAPABILITIES
    for capability in (
        "ocr.engine-selection.v1",
        "runtime.download-sources.v1",
        "runtime.component-selection.v1",
        "ocr.mineru-config.v1",
        "ocr.mineru-remote-api.v1",
    ):
        assert capability in required


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


def test_quality_runs_web_gates_once_and_verifies_production_dist(
    monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    commands: list[list[str]] = []
    verified: list[Path] = []

    monkeypatch.setattr(
        "scripts.check_quality.shutil.which",
        lambda command: "C:/node/npm.cmd" if command == "npm" else None,
    )
    monkeypatch.setattr(
        "scripts.check_quality.subprocess.run",
        lambda command, **kwargs: commands.append(command),
    )
    monkeypatch.setattr(
        "scripts.check_quality.verify_web_assets", lambda path: verified.append(path)
    )

    assert run_quality() == 0

    web_prefix = "src/dotnet/VibeOCR.App/WebAssets"
    assert commands[-6:] == [
        ["C:/node/npm.cmd", "run", "format:check", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "lint", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "typecheck", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "test", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "test:visual", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "build", "--prefix", web_prefix],
    ]
    assert all("test:legacy" not in command for command in commands)
    assert not any(
        "generate_brand_assets.py" in argument
        for command in commands
        for argument in command
    )
    assert verified == [
        Path(__file__).parents[2] / "src/dotnet/VibeOCR.App/WebAssets/dist"
    ]
    output = capsys.readouterr().out
    assert "brand-assets" not in output
    assert "::notice title=Quality stage::web-build completed" in output


def _run_release_build_fixture(
    tmp_path: Path,
    *,
    fail_stage: str,
) -> tuple[subprocess.CompletedProcess[str], list[str]]:
    root = Path(__file__).parents[2]
    project = tmp_path / "src/dotnet/VibeOCR.App/VibeOCR.App.csproj"
    project.parent.mkdir(parents=True)
    project.write_text(
        "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>",
        encoding="utf-8",
    )
    for relative in (".release-input/protocol", ".release-input/backend", "artifacts"):
        (tmp_path / relative).mkdir(parents=True, exist_ok=True)
    for name in ("component-lock.json", "component-identities.json"):
        (tmp_path / "artifacts" / name).write_text("{}", encoding="utf-8")

    scripts = tmp_path / "scripts"
    scripts.mkdir()
    call_log = tmp_path / "calls.log"
    (scripts / "smoke_web_workbench.ps1").write_text(
        """param([string]$ProductRoot)
Add-Content -LiteralPath $env:CALL_LOG -Value "smoke|$ProductRoot"
$global:LASTEXITCODE = 0
""",
        encoding="utf-8",
    )
    wrapper = tmp_path / "run-build.ps1"
    wrapper.write_text(
        """$ErrorActionPreference = 'Stop'
function Write-Call {
    param([string]$Name, [object[]]$Arguments)
    Add-Content -LiteralPath $env:CALL_LOG -Value "$Name|$($Arguments -join ' ')"
}
function npm {
    Write-Call 'npm' $args
    if (($env:FAIL_STAGE -eq 'npm-ci' -and $args[0] -eq 'ci') -or
        ($env:FAIL_STAGE -eq 'npm-build' -and $args[0] -eq 'run')) {
        $global:LASTEXITCODE = 21
    } else { $global:LASTEXITCODE = 0 }
}
function uv {
    Write-Call 'uv' $args
    if ($env:FAIL_STAGE -eq 'web-verify' -and
        ($args -join ' ') -like '*verify_web_assets.py*') {
        $global:LASTEXITCODE = 22
    } else { $global:LASTEXITCODE = 0 }
}
function python { Write-Call 'python' $args; $global:LASTEXITCODE = 0 }
function git {
    Write-Call 'git' $args
    $global:LASTEXITCODE = 0
    '0000000000000000000000000000000000000000'
}
function dotnet {
    Write-Call 'dotnet' $args
    if ($args[0] -eq 'publish') {
        for ($index = 0; $index -lt $args.Count - 1; $index++) {
            if ($args[$index] -eq '-o') {
                New-Item -ItemType Directory -Path $args[$index + 1] -Force | Out-Null
            }
        }
    }
    if ($env:FAIL_STAGE -eq 'bootstrap-publish' -and
        $args[0] -eq 'publish' -and $args[1] -like '*Bootstrapper*') {
        $global:LASTEXITCODE = 23
    } else { $global:LASTEXITCODE = 0 }
}
& $env:BUILD_SCRIPT -Version '1.2.3'
""",
        encoding="utf-8",
    )
    environment = os.environ | {
        "AUTOMATION_PROJECT_ROOT": str(tmp_path),
        "AUTOMATION_ARTIFACTS_DIR": str(tmp_path / "artifacts"),
        "BUILD_SCRIPT": str(root / "scripts/build-release.ps1"),
        "CALL_LOG": str(call_log),
        "FAIL_STAGE": fail_stage,
    }
    completed = subprocess.run(
        [resolve_executable("pwsh"), "-NoProfile", "-File", str(wrapper)],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=environment,
    )
    calls = call_log.read_text(encoding="utf-8-sig").splitlines()
    return completed, calls


@pytest.mark.parametrize(
    ("fail_stage", "expected_commands"),
    [
        ("npm-ci", ["npm"]),
        ("npm-build", ["npm", "npm"]),
        ("web-verify", ["npm", "npm", "uv"]),
    ],
)
def test_release_build_web_gates_fail_closed_before_publish(
    tmp_path: Path,
    fail_stage: str,
    expected_commands: list[str],
) -> None:
    completed, calls = _run_release_build_fixture(tmp_path, fail_stage=fail_stage)

    assert completed.returncode != 0
    assert [call.partition("|")[0] for call in calls] == expected_commands


def test_release_build_runs_verified_web_bundle_before_packaged_smoke(
    tmp_path: Path,
) -> None:
    completed, calls = _run_release_build_fixture(
        tmp_path,
        fail_stage="bootstrap-publish",
    )

    assert completed.returncode != 0
    verifier = next(
        index
        for index, call in enumerate(calls)
        if call.startswith("uv|") and "verify_web_assets.py" in call
    )
    app_publish = next(
        index
        for index, call in enumerate(calls)
        if call.startswith("dotnet|publish") and "VibeOCR.App.csproj" in call
    )
    smoke = next(index for index, call in enumerate(calls) if call.startswith("smoke|"))
    bootstrap_publish = next(
        index
        for index, call in enumerate(calls)
        if call.startswith("dotnet|publish") and "VibeOCR.Bootstrapper" in call
    )
    assert verifier < app_publish < smoke < bootstrap_publish
    assert not any(call.startswith("python|-m PyInstaller") for call in calls)


def _configure_release_smoke_fixture(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    *,
    fail_smoke: bool,
    include_delta: bool = False,
    fail_bootstrapper: bool = False,
) -> tuple[Path, list[list[str]]]:
    artifacts = tmp_path / "artifacts"
    artifacts.mkdir()
    full = artifacts / "VibeOCRNext-1.2.3-full.nupkg"
    full.write_bytes(b"velopack-package")
    if include_delta:
        (artifacts / "VibeOCRNext-1.2.3-delta.nupkg").write_bytes(b"delta")
    _write_velopack_feed(artifacts, "1.2.3")
    identity = {
        "project": {
            "component": "next",
            "repository": "FelixJI/vibeocr-next",
            "version": "1.2.3",
            "source_sha": "a" * 40,
        }
    }
    with zipfile.ZipFile(artifacts / "VibeOCRNext-v1.2.3-win-x64.zip", "w") as package:
        package.writestr("VibeOCR/VibeOCR.exe", b"bootstrapper")
        package.writestr("VibeOCR/app/VibeOCR.WinUI.exe", b"desktop")
        package.writestr(
            "VibeOCR/app/metadata/component-identities.json", json.dumps(identity)
        )
    names = {
        full.name,
        "VibeOCRNext-v1.2.3-win-x64.zip",
        "releases.win.json",
        "product-identity.json",
        "SBOM.spdx.json",
    }
    if include_delta:
        names.add("VibeOCRNext-1.2.3-delta.nupkg")
    (artifacts / "product-identity.json").write_text(
        json.dumps(identity), encoding="utf-8"
    )
    calls: list[list[str]] = []
    monkeypatch.setattr(
        "scripts.release_smoke.verify_release_assets",
        lambda *args, **kwargs: names,
    )

    def fake_run(
        command: list[str], **kwargs: object
    ) -> subprocess.CompletedProcess[str]:
        calls.append(command)
        if command[0].endswith("VibeOCR.exe"):
            assert command[1:] == ["--self-test-prerequisites"]
            assert kwargs["timeout"] == 30
            assert kwargs["cwd"] == Path(command[0]).parent
            if fail_bootstrapper:
                raise subprocess.CalledProcessError(30, command)
        elif "smoke_web_workbench.ps1" in command[2]:
            assert kwargs["timeout"] == 120
            product_root = Path(command[command.index("-ProductRoot") + 1])
            assert (product_root / "app/VibeOCR.WinUI.exe").is_file()
            if fail_smoke:
                raise subprocess.CalledProcessError(31, command)
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setattr("scripts.release_smoke.subprocess.run", fake_run)
    return artifacts, calls


def test_release_smoke_executes_the_extracted_product_handshake(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, calls = _configure_release_smoke_fixture(
        tmp_path,
        monkeypatch,
        fail_smoke=False,
    )

    verify(artifacts, "1.2.3")

    assert len(calls) == 2
    assert calls[0][0].endswith("VibeOCR.exe")
    assert calls[0][1:] == ["--self-test-prerequisites"]
    assert calls[1][2].endswith("smoke_web_workbench.ps1")


def test_release_smoke_propagates_a_failed_public_entry_self_test(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, calls = _configure_release_smoke_fixture(
        tmp_path,
        monkeypatch,
        fail_smoke=False,
        fail_bootstrapper=True,
    )

    with pytest.raises(subprocess.CalledProcessError):
        verify(artifacts, "1.2.3")

    assert len(calls) == 1


def test_release_smoke_accepts_and_binds_one_current_delta(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, _calls = _configure_release_smoke_fixture(
        tmp_path,
        monkeypatch,
        fail_smoke=False,
        include_delta=True,
    )

    verify(artifacts, "1.2.3")

    (artifacts / "VibeOCRNext-1.2.3-delta.nupkg").write_bytes(b"tampered")
    with pytest.raises(ValueError, match="Delta"):
        verify(artifacts, "1.2.3")


def test_release_smoke_rejects_future_historical_full(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, _calls = _configure_release_smoke_fixture(
        tmp_path,
        monkeypatch,
        fail_smoke=False,
        include_delta=True,
    )
    feed_path = artifacts / "releases.win.json"
    feed = json.loads(feed_path.read_text(encoding="utf-8"))
    feed["Assets"].append(
        {
            "PackageId": "VibeOCRNext",
            "Version": "1.2.4",
            "Type": "Full",
            "FileName": "VibeOCRNext-1.2.4-full.nupkg",
            "SHA1": "A" * 40,
            "SHA256": "B" * 64,
            "Size": 123,
        }
    )
    feed_path.write_text(json.dumps(feed), encoding="utf-8")

    with pytest.raises(ValueError, match="historical"):
        verify(artifacts, "1.2.3")


def test_release_smoke_propagates_a_failed_web_ready_handshake(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, calls = _configure_release_smoke_fixture(
        tmp_path,
        monkeypatch,
        fail_smoke=True,
    )

    with pytest.raises(subprocess.CalledProcessError):
        verify(artifacts, "1.2.3")

    assert len(calls) == 2


@pytest.mark.parametrize("installed_layout", [False, True])
def test_web_ready_smoke_runs_an_isolated_production_profile(
    tmp_path: Path,
    installed_layout: bool,
) -> None:
    root = Path(__file__).parents[2]
    product = tmp_path / "product"
    product.mkdir()
    executable = product / (
        "app/VibeOCR.WinUI.exe" if installed_layout else "VibeOCR.WinUI.exe"
    )
    executable.parent.mkdir(parents=True, exist_ok=True)
    executable.write_bytes(b"placeholder")
    if installed_layout:
        metadata = product / "app/metadata"
        metadata.mkdir(parents=True)
        (metadata / "product-layout.json").write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "product_id": "vibeocr",
                    "public_entry": "VibeOCR.exe",
                    "roots": {
                        "app": "app",
                        "runtime": "runtime",
                        "metadata": "app/metadata",
                    },
                    "app": {
                        "entry": "app/VibeOCR.WinUI.exe",
                        "web_assets": "app/WebAssets",
                    },
                    "runtime": {
                        "manifest": "runtime/backend/runtime-manifest.json",
                        "installer": "runtime/installer/vibeocr-runtime-installer.exe",
                    },
                    "metadata": {
                        "component_lock": "app/metadata/component-lock.json",
                        "component_identities": "app/metadata/component-identities.json",
                        "release_manifest": "app/metadata/product-release-manifest.json",
                    },
                    "user_data": {"relative": "state"},
                    "required": [],
                }
            ),
            encoding="utf-8",
        )
        for name in (
            "VibeOCR.exe",
            "Velopack.dll",
            "LICENSE",
            "CHANGELOG.md",
        ):
            (product / name).write_bytes(b"placeholder")
        for relative in (
            "app/VibeOCR.WinUI.dll",
            "app/VibeOCR.WinUI.pri",
            "app/App.xbf",
            "app/MainWindow.xbf",
            "app/WebAssets/index.html",
            "app/metadata/component-lock.json",
            "app/metadata/component-identities.json",
            "app/metadata/product-release-manifest.json",
            "runtime/backend/runtime-manifest.json",
            "runtime/installer/vibeocr-runtime-installer.exe",
        ):
            path = product / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"placeholder")
    launch = tmp_path / "launch.json"
    cleanup = tmp_path / "cleanup.txt"
    wrapper = tmp_path / "run-smoke.ps1"
    wrapper.write_text(
        """param(
    [string]$Smoke,
    [string]$Product,
    [string]$Launch,
    [string]$Cleanup
)
$global:webViewCleanupAttempts = 0
function Remove-Item {
    [CmdletBinding()]
    param(
        [string]$LiteralPath,
        [switch]$Recurse,
        [switch]$Force
    )
    if ($LiteralPath -like '*vibeocr-webview-smoke-*') {
        $global:webViewCleanupAttempts += 1
        if ($global:webViewCleanupAttempts -eq 1) {
            throw 'simulated WebView2 file handle is still active'
        }
    }
    Microsoft.PowerShell.Management\\Remove-Item @PSBoundParameters
}
function Start-Process {
    param(
        [string]$FilePath,
        [object[]]$ArgumentList,
        [string]$WorkingDirectory,
        [string]$WindowStyle,
        [switch]$PassThru
    )
    @{
        file = $FilePath
        arguments = @($ArgumentList)
        working_directory = $WorkingDirectory
        user_data = $env:WEBVIEW2_USER_DATA_FOLDER
        instance_scope = $env:VIBEOCR_SELF_TEST_INSTANCE
    } | ConvertTo-Json | Set-Content -LiteralPath $Launch
    New-Item -ItemType Directory -Path $env:WEBVIEW2_USER_DATA_FOLDER |
        Out-Null
    '{"schema_version":1,"state":"bridge-ready","resources":"verified"}' |
        Set-Content -LiteralPath $env:VIBEOCR_WEB_READY_FILE
    $process = [pscustomobject]@{ ExitCode = 0 }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int]$Milliseconds)
        return $true
    }
    return $process
}
$env:VIBEOCR_SELF_TEST_INSTANCE = 'outer-test-scope'
& $Smoke -ProductRoot $Product
if ($env:VIBEOCR_SELF_TEST_INSTANCE -ne 'outer-test-scope') {
    throw 'smoke did not restore the caller instance scope'
}
$global:webViewCleanupAttempts | Set-Content -LiteralPath $Cleanup
""",
        encoding="utf-8",
    )

    subprocess.run(
        [
            resolve_executable("pwsh"),
            "-NoProfile",
            "-File",
            str(wrapper),
            str(root / "scripts/smoke_web_workbench.ps1"),
            str(product),
            str(launch),
            str(cleanup),
        ],
        check=True,
    )

    launched = json.loads(launch.read_text(encoding="utf-8-sig"))
    assert Path(launched["file"]).parent != product
    assert launched["working_directory"] == str(Path(launched["file"]).parent)
    expected_arguments = ["--shell-only", "--profile", "production"]
    if installed_layout:
        expected_arguments += ["--install-root", str(Path(launched["file"]).parents[1])]
    assert launched["arguments"] == expected_arguments
    assert len(launched["instance_scope"]) == 32
    assert all(
        character in "0123456789abcdef" for character in launched["instance_scope"]
    )
    isolated_product = (
        Path(launched["file"]).parents[1]
        if installed_layout
        else Path(launched["file"]).parent
    )
    assert Path(launched["user_data"]).parent == isolated_product.parent
    assert Path(launched["user_data"]) != Path(launched["working_directory"])
    assert cleanup.read_text(encoding="utf-8-sig").strip() == "2"
    assert not Path(launched["user_data"]).exists()
    assert not Path(launched["working_directory"]).exists()
    if installed_layout:
        assert {path.name for path in product.iterdir()} == {
            "VibeOCR.exe",
            "Velopack.dll",
            "LICENSE",
            "CHANGELOG.md",
            "app",
            "runtime",
        }
    else:
        assert list(product.iterdir()) == [product / "VibeOCR.WinUI.exe"]


def _run_app_ci_fixture(
    tmp_path: Path,
    *,
    total: int,
    passed: int,
    failed: int,
    not_executed: int,
) -> subprocess.CompletedProcess[str]:
    root = Path(__file__).parents[2]
    scripts = tmp_path / "scripts"
    scripts.mkdir()
    script = scripts / "test_app_ci.ps1"
    shutil.copyfile(root / "scripts/test_app_ci.ps1", script)
    wrapper = tmp_path / "run-app-tests.ps1"
    wrapper.write_text(
        """$ErrorActionPreference = 'Stop'
function dotnet {
    $resultIndex = [Array]::IndexOf($args, '--results-directory')
    $resultRoot = $args[$resultIndex + 1]
    New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
    $xml = @"
<TestRun><ResultSummary outcome="Completed"><Counters total="$env:TRX_TOTAL"
passed="$env:TRX_PASSED" failed="$env:TRX_FAILED"
notExecuted="$env:TRX_NOT_EXECUTED" /></ResultSummary></TestRun>
"@
    Set-Content -LiteralPath (Join-Path $resultRoot 'app-tests.trx') -Value $xml
    $global:LASTEXITCODE = 0
}
& $env:APP_TEST_SCRIPT
""",
        encoding="utf-8",
    )
    return subprocess.run(
        [resolve_executable("pwsh"), "-NoProfile", "-File", str(wrapper)],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=os.environ
        | {
            "APP_TEST_SCRIPT": str(script),
            "TRX_TOTAL": str(total),
            "TRX_PASSED": str(passed),
            "TRX_FAILED": str(failed),
            "TRX_NOT_EXECUTED": str(not_executed),
        },
    )


def test_app_ci_accepts_any_nonempty_all_passed_trx_count(tmp_path: Path) -> None:
    completed = _run_app_ci_fixture(
        tmp_path,
        total=2,
        passed=2,
        failed=0,
        not_executed=0,
    )

    assert completed.returncode == 0, completed.stderr


@pytest.mark.parametrize(
    ("total", "passed", "failed", "not_executed"),
    [
        (0, 0, 0, 0),
        (2, 1, 1, 0),
        (2, 1, 0, 1),
    ],
)
def test_app_ci_rejects_empty_failed_or_incomplete_trx(
    tmp_path: Path,
    total: int,
    passed: int,
    failed: int,
    not_executed: int,
) -> None:
    completed = _run_app_ci_fixture(
        tmp_path,
        total=total,
        passed=passed,
        failed=failed,
        not_executed=not_executed,
    )

    assert completed.returncode != 0
    assert "App test result is incomplete" in completed.stderr


def test_project_config_declares_one_next_product_identity_asset() -> None:
    root = Path(__file__).parents[2]
    config = json.loads((root / ".ci/project.json").read_text(encoding="utf-8"))
    assert "protocol_compatibility" not in config["project"]
    assert config["release"]["identity_asset"] == "product-identity.json"
    assert config["release"]["required_assets"] == [
        "VibeOCRNext-*-full.nupkg",
        "VibeOCRNext-*-delta.nupkg",
        "VibeOCRNext-v{version}-win-x64.zip",
        "releases.win.json",
        "product-identity.json",
        "SBOM.spdx.json",
    ]
    bootstrap = config["ci"]["bootstrap"]
    assert any(
        command[-3:] == ["pwsh", "-File", "scripts/install_windows_app_runtime.ps1"]
        for command in bootstrap
    )
    assert all("component-resolve" not in command for command in bootstrap)
    build_script = (root / "scripts/build-release.ps1").read_text(encoding="utf-8")
    assert "prepare_velopack_delta.py" in build_script
    assert "normalize_velopack_feed.py" in build_script
    assert "--reproduce-published-delta" in build_script
    assert "AUTOMATION_SOURCE_SHA" in build_script
    assert "--delta $deltaMode" in build_script
    assert "verify_velopack_portable_delta_e2e.py" in build_script
    assert "--old-package" in build_script
    assert "--require-package-type full" in build_script
    assert "--require-package-type delta" in build_script
    assert "--legacy-state-layout" in build_script
    assert "velopack-full-e2e-old" in build_script
    delta_e2e = (root / "scripts/verify_velopack_portable_delta_e2e.py").read_text(
        encoding="utf-8"
    )
    program = (root / "src/dotnet/VibeOCR.App/Program.cs").read_text(encoding="utf-8")
    assert 'f"-{require_package_type}.nupkg"' in delta_e2e
    assert "Velopack apply lost state marker" in delta_e2e
    assert "_wait_for_evidence_writer_exit(evidence" in delta_e2e
    assert "requested the target full package after delta" in delta_e2e
    assert '"LOCALAPPDATA"' in delta_e2e
    assert '"USERPROFILE"' in delta_e2e
    assert '"TEMP"' in delta_e2e
    assert "allowed_external" in delta_e2e
    assert "wrote unexpected data outside the Portable root" in delta_e2e
    assert "VelopackUpdateSelfTest.Run()" in program
    bootstrapper = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )
    assert "OnAfterUpdateFastCallback" in bootstrapper
    assert "LegacyVelopackStateMigration.Migrate" in bootstrapper
    assert build_script.count("build_release_checksums.py") == 1
    assert "dotnet tool run vpk pack" in build_script
    assert "scripts/build_internal_runtime.ps1" in build_script
    nuget = (root / "NuGet.Config").read_text(encoding="utf-8")
    assert ".release-input/protocol-sdk" not in nuget


def test_platform_e2e_has_a_bounded_hang_diagnostic() -> None:
    root = Path(__file__).parents[2]
    config = json.loads((root / ".ci/project.json").read_text(encoding="utf-8"))
    platform_test = next(
        command for command in config["ci"]["e2e"] if "platform-tests" in command
    )

    assert platform_test[-7:] == [
        "--blame-hang",
        "--blame-hang-timeout",
        "2m",
        "--blame-hang-dump-type",
        "none",
        "--logger",
        "console;verbosity=detailed",
    ]


def test_long_running_ci_commands_have_outer_process_tree_timeouts() -> None:
    root = Path(__file__).parents[2]
    config = json.loads((root / ".ci/project.json").read_text(encoding="utf-8"))

    for stage in ("bootstrap", "quality", "e2e", "release_build", "release_smoke"):
        for command in config["ci"][stage]:
            assert command[:2] == ["python", "scripts/run_ci_command.py"]
            assert "--timeout-seconds" in command


def test_web_ready_smoke_never_waits_unbounded_after_forced_termination() -> None:
    root = Path(__file__).parents[2]
    smoke = (root / "scripts/smoke_web_workbench.ps1").read_text(encoding="utf-8")

    assert "$process.WaitForExit()" not in smoke
    assert "$process.WaitForExit(5000)" in smoke


def test_release_build_emits_actionable_stage_annotations() -> None:
    root = Path(__file__).parents[2]
    build = (root / "scripts/build-release.ps1").read_text(encoding="utf-8")

    assert "::notice title=Release build stage::" in build
    for stage in (
        "app-publish",
        "app-webview-smoke",
        "product-finalize",
        "velopack-package",
        "artifact-verify",
    ):
        assert f"Write-CiStage '{stage}'" in build


def test_velopack_startup_hook_runs_in_the_packaged_root_entrypoint() -> None:
    root = Path(__file__).parents[2]
    program = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )
    project = (
        root / "src/dotnet/VibeOCR.Bootstrapper/VibeOCR.Bootstrapper.csproj"
    ).read_text(encoding="utf-8")

    assert (
        ".OnAfterUpdateFastCallback(_ => LegacyVelopackStateMigration.Migrate("
        in program
    )
    assert program.index("LegacyVelopackStateMigration.Resume(") < program.index(
        "VelopackApp.Build()"
    )
    assert program.index("VelopackApp.Build()") < program.index(
        "PortableProductRoots roots"
    )
    assert '<PackageReference Include="Velopack" />' in project
    child_program = (root / "src/dotnet/VibeOCR.App/Program.cs").read_text(
        encoding="utf-8"
    )
    assert child_program.index("VelopackApp.Build().Run();") < child_program.index(
        "Application.Start"
    )


def test_bootstrapper_webview_probe_uses_the_packaged_native_loader() -> None:
    root = Path(__file__).parents[2]
    program = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )
    project = (
        root / "src/dotnet/VibeOCR.Bootstrapper/VibeOCR.Bootstrapper.csproj"
    ).read_text(encoding="utf-8")

    assert "using Microsoft.Web.WebView2.Core;" not in program
    assert '<PackageReference Include="Microsoft.Web.WebView2" />' not in project
    assert 'Path.Combine(layout.AppRoot, "WebView2Loader.dll")' in program
    assert "GetAvailableCoreWebView2BrowserVersionString" in program

    version_call = program.index("int result = getVersion")
    cleanup_try = program.index("try", version_call)
    failed_result = program.index(
        "if (result < 0 || versionInfo == IntPtr.Zero)", version_call
    )
    cleanup_finally = program.index("finally", failed_result)
    release_version = program.index("Marshal.FreeCoTaskMem(versionInfo)", failed_result)
    assert (
        version_call < cleanup_try < failed_result < cleanup_finally < release_version
    )


def test_bootstrapper_accepts_historical_and_cbs_windows_app_runtime_identities() -> (
    None
):
    root = Path(__file__).parents[2]
    program = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )

    assert 'name.Equals("Microsoft.WindowsAppRuntime.2",' in program
    assert 'name.Equals("Microsoft.WindowsAppRuntime.CBS.2",' in program
    assert "Microsoft.WindowsAppRuntime.2.2" not in program


def test_bootstrapper_prerequisite_self_test_never_launches_the_app() -> None:
    root = Path(__file__).parents[2]
    program = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )

    assert program.index("args.Contains(SelfTestPrerequisites") < program.index(
        "Process.Start(startInfo)"
    )


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


def test_release_smoke_binds_native_portable_and_product_identity(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    artifacts, _calls = _configure_release_smoke_fixture(
        tmp_path, monkeypatch, fail_smoke=False
    )
    verify(artifacts, "1.2.3")

    identity = json.loads(
        (artifacts / "product-identity.json").read_text(encoding="utf-8")
    )
    identity["project"]["source_sha"] = "b" * 40
    (artifacts / "product-identity.json").write_text(
        json.dumps(identity), encoding="utf-8"
    )
    with pytest.raises(ValueError, match="differs from release asset"):
        verify(artifacts, "1.2.3")

    (artifacts / "unexpected.txt").write_text("unexpected", encoding="utf-8")
    monkeypatch.setattr(
        "scripts.release_smoke.verify_release_assets",
        lambda *args, **kwargs: {
            "VibeOCRNext-1.2.3-full.nupkg",
            "VibeOCRNext-v1.2.3-win-x64.zip",
            "releases.win.json",
            "product-identity.json",
            "SBOM.spdx.json",
            "unexpected.txt",
        },
    )
    with pytest.raises(ValueError, match="release asset set mismatch"):
        verify(artifacts, "1.2.3")


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
