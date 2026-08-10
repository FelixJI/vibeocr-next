from __future__ import annotations

import ast
import hashlib
import inspect
import json
import os
import shutil
import subprocess
import zipfile
from pathlib import Path

import pytest

from scripts.automation_core import CommandRunner
from scripts.bind_component_releases import bind_product_releases
from scripts.check_quality import main as run_quality
from scripts.check_quality import resolve_executable
from scripts.release_smoke import verify
from scripts.resolve_component_releases import (
    _api,
    assert_protocol_compatible,
    bound_protocol_version,
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


def test_newer_sdk_and_older_bound_runtime_are_minor_compatible() -> None:
    compatibility = {"supported_majors": [2], "minor_compatible": True}

    assert_protocol_compatible("2.3.0", compatibility)
    assert_protocol_compatible("2.0.0", compatibility)


def test_reads_backend_bound_protocol_v2_identity(tmp_path: Path) -> None:
    (tmp_path / "protocol-release-manifest.json").write_text(
        json.dumps(
            {
                "schema_version": 2,
                "project": {"component": "protocol"},
                "protocol": {"version": "2.1.0"},
                "release": {"version": "2.1.0", "tag": "v2.1.0"},
            }
        ),
        encoding="utf-8",
    )

    assert bound_protocol_version(tmp_path) == "2.1.0"


def test_resolver_binding_keywords_match_the_binding_api() -> None:
    root = Path(__file__).parents[2]
    tree = ast.parse(
        (root / "scripts/resolve_component_releases.py").read_text(encoding="utf-8")
    )
    calls = [
        node
        for node in ast.walk(tree)
        if isinstance(node, ast.Call)
        and isinstance(node.func, ast.Name)
        and node.func.id == "bind_product_releases"
    ]

    assert len(calls) == 1
    passed_keywords = {keyword.arg for keyword in calls[0].keywords}
    accepted_keywords = set(inspect.signature(bind_product_releases).parameters)
    assert passed_keywords <= accepted_keywords


def test_component_resolver_authenticates_api_with_ci_gh_token(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class Response:
        def __enter__(self) -> Response:
            return self

        def __exit__(self, *args: object) -> None:
            return None

        def read(self) -> bytes:
            return b'{"tag_name":"v1.0.0"}'

    requests: list[object] = []
    monkeypatch.setenv("GH_TOKEN", "ci-token")
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)

    def fake_urlopen(request: object) -> Response:
        requests.append(request)
        return Response()

    monkeypatch.setattr(
        "scripts.resolve_component_releases.urllib.request.urlopen", fake_urlopen
    )

    assert _api("FelixJI/vibeocr-backend", "/releases/latest") == {"tag_name": "v1.0.0"}
    assert len(requests) == 1
    assert requests[0].get_header("Authorization") == "Bearer ci-token"


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
    monkeypatch: pytest.MonkeyPatch,
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
    assert verified == [
        Path(__file__).parents[2] / "src/dotnet/VibeOCR.App/WebAssets/dist"
    ]


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
        ("npm-ci", ["uv", "npm"]),
        ("npm-build", ["uv", "npm", "npm"]),
        ("web-verify", ["uv", "npm", "npm", "uv"]),
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
) -> tuple[Path, list[list[str]]]:
    artifacts = tmp_path / "artifacts"
    artifacts.mkdir()
    archive = artifacts / "VibeOCR-v1.2.3-win64.zip"
    with zipfile.ZipFile(archive, "w") as package:
        package.writestr("VibeOCR.Next/VibeOCR.WinUI.exe", b"placeholder")
    names = {
        archive.name,
        f"{archive.name}.sha256",
        "component-lock.json",
        "component-identities.json",
        "SBOM.spdx.json",
    }
    (artifacts / "component-identities.json").write_text(
        json.dumps(
            {
                component: {
                    "version": "2.0.0",
                    "source_sha": "a" * 40,
                    **(
                        {"release_manifest_sha256": "b" * 64}
                        if component != "backend"
                        else {}
                    ),
                }
                for component in ("backend", "protocol", "protocol_sdk")
            }
        ),
        encoding="utf-8",
    )
    calls: list[list[str]] = []
    monkeypatch.setattr(
        "scripts.release_smoke.verify_release_assets",
        lambda *args, **kwargs: names,
    )

    def fake_run(
        command: list[str], **kwargs: object
    ) -> subprocess.CompletedProcess[str]:
        assert kwargs["timeout"] == 120
        calls.append(command)
        if "smoke_web_workbench.ps1" in command[2]:
            product_root = Path(command[command.index("-ProductRoot") + 1])
            assert (product_root / "VibeOCR.WinUI.exe").is_file()
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

    verify(artifacts)

    assert len(calls) == 2
    assert calls[1][2].endswith("smoke_web_workbench.ps1")


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
        verify(artifacts)

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
                        "updater": "app/tools/updater.exe",
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
                    "user_data": {
                        "known_folder": "LocalApplicationData",
                        "relative": "VibeOCR",
                    },
                    "required": [],
                }
            ),
            encoding="utf-8",
        )
        for name in ("VibeOCR.exe", "LICENSE", "CHANGELOG.md"):
            (product / name).write_bytes(b"placeholder")
        for relative in (
            "app/VibeOCR.WinUI.dll",
            "app/VibeOCR.WinUI.pri",
            "app/App.xbf",
            "app/MainWindow.xbf",
            "app/WebAssets/index.html",
            "app/tools/updater.exe",
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
    '{"schema_version":1,"state":"bridge-ready"}' |
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

    for stage in ("quality", "e2e", "release_build", "release_smoke"):
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
        "updater-pyinstaller",
        "product-package",
        "artifact-verify",
    ):
        assert f"Write-CiStage '{stage}'" in build


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
    assert '"protocol_sdk": {' in resolver
    assert "Protocol SDK {sdk_version} is newer than bound runtime" not in resolver
    assert (
        'verify_protocol_release(work / "protocol-sdk", version=sdk_version)'
        in resolver
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


def test_release_smoke_binds_real_archive_and_component_identity(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    archive = tmp_path / "VibeOCR-v0.2.0-win64.zip"
    with zipfile.ZipFile(archive, "w") as package:
        package.writestr("VibeOCR.Next/VibeOCR.WinUI.exe", b"desktop")
    (tmp_path / "component-lock.json").write_text("{}", encoding="utf-8")
    (tmp_path / "component-identities.json").write_text(
        json.dumps(
            {
                "backend": {"version": "1.0.0", "source_sha": "a" * 40},
                "protocol": {
                    "version": "2.0.0",
                    "source_sha": "b" * 40,
                    "release_manifest_sha256": "c" * 64,
                },
                "protocol_sdk": {
                    "version": "2.3.0",
                    "source_sha": "d" * 40,
                    "release_manifest_sha256": "e" * 64,
                },
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

    identity = json.loads(
        (tmp_path / "component-identities.json").read_text(encoding="utf-8")
    )
    del identity["protocol_sdk"]
    (tmp_path / "component-identities.json").write_text(
        json.dumps(identity), encoding="utf-8"
    )
    with pytest.raises(ValueError, match="protocol_sdk"):
        verify(tmp_path)
    identity["protocol_sdk"] = {
        "version": "2.3.0",
        "source_sha": "d" * 40,
        "release_manifest_sha256": "e" * 64,
    }
    (tmp_path / "component-identities.json").write_text(
        json.dumps(identity), encoding="utf-8"
    )

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
