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
    assert "runtime.component-selection.v1" not in required
    for capability in (
        "ocr.engine-selection.v1",
        "runtime.download-sources.v1",
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
    assert commands[-5:] == [
        ["C:/node/npm.cmd", "run", "format:check", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "lint", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "typecheck", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "test", "--prefix", web_prefix],
        ["C:/node/npm.cmd", "run", "test:visual", "--prefix", web_prefix],
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
    assert "::notice title=Quality stage::web-visual completed" in output


def _run_release_build_fixture(
    tmp_path: Path,
    *,
    fail_stage: str,
    explicit_version: bool = True,
    repository_version: str = "1.2.3",
) -> tuple[subprocess.CompletedProcess[str], list[str]]:
    root = Path(__file__).parents[2]
    project = tmp_path / "src/dotnet/VibeOCR.App/VibeOCR.App.csproj"
    project.parent.mkdir(parents=True)
    project.write_text(
        "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup>"
        "<PropertyGroup Condition=\"'$(VibeOCRProductWebView2)' == 'true'\">"
        "<AppHostDotNetSearch>AppLocal</AppHostDotNetSearch></PropertyGroup></Project>",
        encoding="utf-8",
    )
    (tmp_path / "repository.json").write_text(
        json.dumps({"version": repository_version}), encoding="utf-8"
    )
    for relative in (".release-input/protocol", ".release-input/backend", "artifacts"):
        (tmp_path / relative).mkdir(parents=True, exist_ok=True)
    for name in ("component-lock.json", "component-identities.json"):
        (tmp_path / "artifacts" / name).write_text("{}", encoding="utf-8")

    scripts = tmp_path / "scripts"
    scripts.mkdir()
    build_script = scripts / "build-release.ps1"
    shutil.copyfile(root / "scripts/build-release.ps1", build_script)
    call_log = tmp_path / "calls.log"
    (scripts / "build_internal_runtime.ps1").write_text(
        """param([string]$Version, [string]$BuildRoot)
Add-Content -LiteralPath $env:CALL_LOG -Value "runtime|[$Version]"
if ($Version -cne '1.2.3') { throw "Runtime received wrong version: [$Version]" }
throw 'fixture reached exact Runtime version'
""",
        encoding="utf-8",
    )
    (scripts / "smoke_web_workbench.ps1").write_text(
        """param([string]$ProductRoot)
Add-Content -LiteralPath $env:CALL_LOG -Value "smoke|$ProductRoot"
$global:LASTEXITCODE = 0
""",
        encoding="utf-8",
    )
    # #321：随包 WebView2 展开步骤在夹具中以桩记录顺序，不接触真实 CAB。
    (scripts / "expand_webview2_runtime.ps1").write_text(
        """param([string]$AppPublishRoot, [string]$LockFile, [string]$CacheRoot)
Add-Content -LiteralPath $env:CALL_LOG -Value "expand|$AppPublishRoot"
New-Item -ItemType Directory -Path (Join-Path $AppPublishRoot 'WebView2Runtime') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $AppPublishRoot 'WebView2Runtime/msedgewebview2.exe') -Value 'stub'
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
if ($env:EXPLICIT_VERSION -eq '1') { & $env:BUILD_SCRIPT -Version '1.2.3' }
else { & $env:BUILD_SCRIPT }
""",
        encoding="utf-8",
    )
    environment = os.environ | {
        "AUTOMATION_PROJECT_ROOT": str(tmp_path),
        "AUTOMATION_ARTIFACTS_DIR": str(tmp_path / "artifacts"),
        "BUILD_SCRIPT": str(build_script),
        "CALL_LOG": str(call_log),
        "FAIL_STAGE": fail_stage,
        "EXPLICIT_VERSION": "1" if explicit_version else "0",
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
    calls = (
        call_log.read_text(encoding="utf-8-sig").splitlines()
        if call_log.exists()
        else []
    )
    return completed, calls


@pytest.mark.parametrize("explicit_version", [False, True])
def test_release_build_preserves_exact_version_with_multiple_property_groups(
    tmp_path: Path, explicit_version: bool
) -> None:
    completed, calls = _run_release_build_fixture(
        tmp_path, fail_stage="runtime-version", explicit_version=explicit_version
    )
    assert "fixture reached exact Runtime version" in completed.stderr
    assert calls[-1] == "runtime|[1.2.3]"


def test_release_build_rejects_stale_app_version_before_building(
    tmp_path: Path,
) -> None:
    completed, calls = _run_release_build_fixture(
        tmp_path, fail_stage="runtime-version", repository_version="1.2.4"
    )
    assert completed.returncode != 0
    assert "App must have one Version matching repository.json" in completed.stderr
    assert calls == []


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
    webview2_expand = next(
        index for index, call in enumerate(calls) if call.startswith("expand|")
    )
    smoke = next(index for index, call in enumerate(calls) if call.startswith("smoke|"))
    bootstrap_publish = next(
        index
        for index, call in enumerate(calls)
        if call.startswith("dotnet|publish") and "VibeOCR.Bootstrapper" in call
    )
    assert verifier < app_publish < webview2_expand < smoke < bootstrap_publish
    assert not any(call.startswith("python|-m PyInstaller") for call in calls)


# 与 product_layout.py stage 写出的 canonical 布局 descriptor 一致。
_PRODUCT_LAYOUT_DESCRIPTOR = {
    "schema_version": 1,
    "product_id": "vibeocr",
    "public_entry": "VibeOCR.exe",
    "roots": {"app": "app", "runtime": "runtime", "metadata": "app/metadata"},
    "app": {
        "entry": "app/VibeOCR.WinUI.exe",
        "web_assets": "app/WebAssets",
        "webview2_runtime": "app/WebView2Runtime",
    },
    "runtime": {
        "manifest": "runtime/backend/runtime-manifest.json",
        "python": "runtime/env/runtime-env/python.exe",
    },
    "metadata": {
        "component_lock": "app/metadata/component-lock.json",
        "component_identities": "app/metadata/component-identities.json",
        "release_manifest": "app/metadata/product-release-manifest.json",
    },
    "user_data": {"relative": "state"},
    "required": [],
}

# #321：smoke 验证随包 WebView2 版本时读取仓库锁（不硬编码版本）。
_LOCKED_WEBVIEW2_VERSION = json.loads(
    (Path(__file__).parents[2] / "config/runtime/webview2-runtime.lock.json").read_text(
        encoding="utf-8"
    )
)["version"]


def _write_installed_product_fixture(product: Path) -> None:
    metadata = product / "app/metadata"
    metadata.mkdir(parents=True)
    (metadata / "product-layout.json").write_text(
        json.dumps(_PRODUCT_LAYOUT_DESCRIPTOR), encoding="utf-8"
    )
    for name in ("VibeOCR.exe", "Velopack.dll", "LICENSE", "CHANGELOG.md"):
        (product / name).write_bytes(b"placeholder")
    for relative in (
        "app/VibeOCR.WinUI.exe",
        "app/VibeOCR.WinUI.dll",
        "app/VibeOCR.WinUI.pri",
        "app/App.xbf",
        "app/MainWindow.xbf",
        "app/WebAssets/index.html",
        "app/WebView2Runtime/msedgewebview2.exe",
        "app/WebView2Runtime/icudtl.dat",
        "app/metadata/component-lock.json",
        "app/metadata/component-identities.json",
        "app/metadata/product-release-manifest.json",
        "runtime/backend/runtime-manifest.json",
        "runtime/env/runtime-env/python.exe",
    ):
        path = product / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"placeholder")
    # W17-2：随包启动库写入最小自洽 x64 DLL 映像（映像校验正例）。
    for relative in (
        "app/WebView2Runtime/msedge.dll",
        "app/WebView2Runtime/EBWebView/x64/EmbeddedBrowserWebView.dll",
    ):
        path = product / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(_minimal_x64_dll_image())


def _minimal_x64_dll_image() -> bytes:
    """最小自洽 Windows x64 DLL PE 头样本。自洽指：COFF 声明的可选头
    （0xF0）与节表（1×40）都落在实际文件长度内；仅头部/声明范围语义。"""
    image = bytearray(0x80 + 24 + 0xF0 + 40)
    image[0:2] = b"MZ"
    image[0x3C:0x40] = (0x80).to_bytes(4, "little")
    image[0x80:0x84] = b"PE\x00\x00"
    image[0x84:0x86] = (0x8664).to_bytes(2, "little")
    image[0x86:0x88] = (1).to_bytes(2, "little")
    image[0x94:0x96] = (0xF0).to_bytes(2, "little")
    image[0x96:0x98] = (0x2022).to_bytes(2, "little")
    image[0x98:0x9A] = (0x20B).to_bytes(2, "little")
    return bytes(image)


def _configure_release_smoke_fixture(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    *,
    fail_smoke: bool,
    include_delta: bool = False,
    fail_bootstrapper: bool = False,
    fail_runtime_code: bool = False,
) -> tuple[Path, list[list[str]], list[Path]]:
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
        # canonical Portable 布局:顶层入口、更新工具与唯一 current 产品树。
        package.writestr(".portable", b"velopack-portable-marker")
        package.writestr("Update.exe", b"velopack-update")
        package.writestr("VibeOCR.exe", b"velopack-entry")
        package.writestr("current/VibeOCR.exe", b"bootstrapper")
        package.writestr("current/Velopack.dll", b"velopack-runtime")
        package.writestr("current/LICENSE", b"license")
        package.writestr("current/CHANGELOG.md", b"changelog")
        package.writestr("current/app/VibeOCR.WinUI.exe", b"desktop")
        package.writestr("current/app/Microsoft.WindowsAppRuntime.dll", b"winappsdk")
        package.writestr("current/app/Microsoft.UI.Xaml.dll", b"winappsdk-xaml")
        package.writestr(
            "current/app/VibeOCR.WinUI.runtimeconfig.json",
            json.dumps(
                {
                    "runtimeOptions": {
                        "includedFrameworks": [
                            {"name": "Microsoft.NETCore.App", "version": "10.0.0"}
                        ]
                    }
                }
            ),
        )
        package.writestr("current/app/System.Private.CoreLib.dll", b"clr")
        package.writestr(
            "current/app/WebView2Runtime/msedgewebview2.exe", b"locked-browser"
        )
        package.writestr(
            "current/app/WebView2Runtime/msedge.dll", _minimal_x64_dll_image()
        )
        package.writestr(
            "current/app/WebView2Runtime/EBWebView/x64/EmbeddedBrowserWebView.dll",
            _minimal_x64_dll_image(),
        )
        package.writestr("current/runtime/env/runtime-env/python.exe", b"runtime")
        package.writestr(
            "current/app/metadata/component-identities.json",
            json.dumps(identity),
        )
        package.writestr(
            "current/app/metadata/product-layout.json",
            json.dumps(_PRODUCT_LAYOUT_DESCRIPTOR),
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
    unpack_roots: list[Path] = []
    monkeypatch.setattr(
        "scripts.release_smoke.verify_release_assets",
        lambda *args, **kwargs: names,
    )

    def fake_run(
        command: list[str], **kwargs: object
    ) -> subprocess.CompletedProcess[bytes]:
        calls.append(command)
        if Path(command[0]).name == "python.exe":
            assert command[1:4] == ["-I", "-B", "-c"]
            assert (
                Path(command[0])
                == Path(str(kwargs["cwd"])) / "runtime/env/runtime-env/python.exe"
            )
            assert command[-1] == "1.2.3"
            assert kwargs["timeout"] == 120
            unpack_roots.append(Path(str(kwargs["cwd"])))
            if fail_runtime_code:
                raise subprocess.CalledProcessError(32, command)
        elif command[0].endswith("VibeOCR.exe"):
            assert command[1:] == ["--self-test-prerequisites"]
            assert kwargs["timeout"] == 30
            assert kwargs["cwd"] == Path(command[0]).parent
            product_root = Path(command[0]).parent
            unpack_roots.append(product_root)
            velopack = product_root / "Velopack.dll"
            browser = product_root / "app/WebView2Runtime/msedgewebview2.exe"
            if kwargs.get("capture_output"):
                if (
                    product_root / "app/VibeOCR.WinUI.exe"
                ).read_bytes() == b"invalid app image":
                    return subprocess.CompletedProcess(
                        command, 3, stderr=b"app.invalid-image"
                    )
                if (
                    not velopack.is_file()
                    or velopack.read_bytes() == b"invalid managed assembly"
                ):
                    return subprocess.CompletedProcess(
                        command, 5, stderr=b"Velopack.dll"
                    )
                runtime_config = product_root / "app/VibeOCR.WinUI.runtimeconfig.json"
                if not (product_root / "app/System.Private.CoreLib.dll").is_file():
                    # 随包 .NET 闭包：CoreLib 缺失 → exit 2。
                    return subprocess.CompletedProcess(
                        command, 2, stderr=b"System.Private.CoreLib.dll"
                    )
                if not runtime_config.is_file() or runtime_config.read_bytes() == b"{":
                    # 随包 .NET 闭包：runtimeconfig 缺失/损坏 → exit 2。
                    return subprocess.CompletedProcess(
                        command, 2, stderr=b"VibeOCR.WinUI.runtimeconfig.json"
                    )
                options = json.loads(runtime_config.read_text())["runtimeOptions"]
                if "framework" in options or "frameworks" in options:
                    return subprocess.CompletedProcess(
                        command, 2, stderr=b"VibeOCR.WinUI.runtimeconfig.json"
                    )
                if not (product_root / "app/Microsoft.UI.Xaml.dll").is_file():
                    # 随包 Windows App SDK 启动闭包：UI.Xaml 缺失 → exit 2。
                    return subprocess.CompletedProcess(
                        command, 2, stderr=b"Microsoft.UI.Xaml.dll"
                    )
                webview2_root = product_root / "app/WebView2Runtime"
                msedge_dll = webview2_root / "msedge.dll"
                if not msedge_dll.is_file():
                    # 随包 WebView2 原生启动闭包：msedge.dll 缺失 → exit 5。
                    return subprocess.CompletedProcess(command, 5, stderr=b"msedge.dll")
                if not msedge_dll.read_bytes().startswith(b"MZ"):
                    # W17-2：msedge.dll 在位但非 PE 映像（如 67 字节无效映像）
                    # → exit 5 报修，不得放行。
                    return subprocess.CompletedProcess(
                        command,
                        5,
                        stderr=b"webview2.invalid-image: app\\WebView2Runtime\\msedge.dll",
                    )
                if not (
                    webview2_root / "EBWebView/x64/EmbeddedBrowserWebView.dll"
                ).is_file():
                    # 嵌套架构子目录内的运行库 DLL 缺失 → exit 5。
                    return subprocess.CompletedProcess(
                        command, 5, stderr=b"EmbeddedBrowserWebView.dll"
                    )
                reason = (
                    b"webview2.version-mismatch"
                    if browser.read_bytes() == velopack.read_bytes()
                    else b"webview2.invalid-version"
                )
                return subprocess.CompletedProcess(command, 2, stderr=reason)
            if fail_bootstrapper:
                raise subprocess.CalledProcessError(30, command)
        elif "smoke_web_workbench.ps1" in command[2]:
            assert kwargs["timeout"] == 300
            assert "-ReuseUnpackedProduct" in command
            product_root = Path(command[command.index("-ProductRoot") + 1])
            assert product_root.name == "current"
            assert product_root.parent.name.startswith("vibeocr-release-smoke-")
            assert (product_root / "app/VibeOCR.WinUI.exe").read_bytes() == b"desktop"
            assert (product_root / "Velopack.dll").read_bytes() == b"velopack-runtime"
            assert (
                product_root / "app/WebView2Runtime/msedgewebview2.exe"
            ).read_bytes() == b"locked-browser"
            environment = kwargs["env"]
            assert environment is not None
            assert Path(environment["TEMP"]) == product_root.parent.parent
            assert Path(environment["TMP"]) == product_root.parent.parent
            unpack_roots.append(product_root)
            if fail_smoke:
                raise subprocess.CalledProcessError(31, command)
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setattr("scripts.release_smoke.subprocess.run", fake_run)
    return artifacts, calls, unpack_roots


def test_release_smoke_executes_the_extracted_product_handshake(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, calls, unpack_roots = _configure_release_smoke_fixture(
        tmp_path,
        monkeypatch,
        fail_smoke=False,
    )

    verify(artifacts, "1.2.3")

    assert len(calls) == 17
    assert Path(calls[0][0]).parts[-4:] == (
        "runtime",
        "env",
        "runtime-env",
        "python.exe",
    )
    assert calls[1][0].endswith("VibeOCR.exe")
    assert calls[1][1:] == ["--self-test-prerequisites"]
    assert calls[-1][2].endswith("smoke_web_workbench.ps1")
    # 子进程共享同一个独占解包根,并在结束后由 Python 清理。
    assert len(unpack_roots) == 17
    assert len({str(path) for path in unpack_roots}) == 1
    reused = unpack_roots[0]
    assert reused.name == "current"
    assert reused.parent.name.startswith("vibeocr-release-smoke-")
    assert not reused.exists()


def test_release_smoke_stops_when_product_runtime_code_cannot_import(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, calls, _unpack_roots = _configure_release_smoke_fixture(
        tmp_path, monkeypatch, fail_smoke=False, fail_runtime_code=True
    )

    with pytest.raises(subprocess.CalledProcessError) as failure:
        verify(artifacts, "1.2.3")

    assert failure.value.returncode == 32
    assert len(calls) == 1
    assert Path(calls[0][0]).parts[-4:] == (
        "runtime",
        "env",
        "runtime-env",
        "python.exe",
    )


def test_release_smoke_propagates_a_failed_public_entry_self_test(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, calls, _unpack_roots = _configure_release_smoke_fixture(
        tmp_path,
        monkeypatch,
        fail_smoke=False,
        fail_bootstrapper=True,
    )

    with pytest.raises(subprocess.CalledProcessError):
        verify(artifacts, "1.2.3")

    assert len(calls) == 2


def test_release_smoke_accepts_and_binds_one_current_delta(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    artifacts, _calls, _unpack_roots = _configure_release_smoke_fixture(
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
    artifacts, _calls, _unpack_roots = _configure_release_smoke_fixture(
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
    artifacts, calls, unpack_roots = _configure_release_smoke_fixture(
        tmp_path,
        monkeypatch,
        fail_smoke=True,
    )

    with pytest.raises(subprocess.CalledProcessError):
        verify(artifacts, "1.2.3")

    assert len(calls) == 17
    # handshake 失败后复用解包树仍由 Python 的 TemporaryDirectory 清理。
    assert not unpack_roots[0].exists()


@pytest.mark.parametrize("installed_layout", [False, True])
@pytest.mark.parametrize("layout_verified", [False, True])
def test_web_ready_smoke_runs_an_isolated_production_profile(
    tmp_path: Path,
    installed_layout: bool,
    layout_verified: bool,
) -> None:
    root = Path(__file__).parents[2]
    product = tmp_path / "product"
    product.mkdir()
    executable = product / (
        "app/VibeOCR.WinUI.exe" if installed_layout else "VibeOCR.WinUI.exe"
    )
    if installed_layout:
        _write_installed_product_fixture(product)
    else:
        executable.parent.mkdir(parents=True, exist_ok=True)
        executable.write_bytes(b"placeholder")
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
    if ($LiteralPath -like '*vibeocr-web-smoke-*') {
        $global:webViewCleanupAttempts += 1
        if ($global:webViewCleanupAttempts -eq 1) {
            throw 'simulated WebView2 file handle is still active'
        }
    }
    Microsoft.PowerShell.Management\\Remove-Item @PSBoundParameters
}
function Get-CimInstance { param($Filter) @() }
function Start-Process {
    param(
        [string]$FilePath,
        [object[]]$ArgumentList,
        [string]$WorkingDirectory,
        [string]$WindowStyle,
        [switch]$PassThru
    )
    $exeDir = Split-Path -Parent $FilePath
    $script:productRoot = if (Test-Path (Join-Path $Product 'app/metadata/product-layout.json')) { Split-Path -Parent $exeDir } else { $exeDir }
    $script:userData = [IO.Path]::GetFullPath((Join-Path (Join-Path $script:productRoot 'state') (Join-Path 'webview2-fixed' '__LOCKED_VERSION__')))
    $script:browserExe = [IO.Path]::GetFullPath((Join-Path (Join-Path $exeDir 'WebView2Runtime') 'msedgewebview2.exe'))
    New-Item -ItemType Directory -Path $script:userData -Force | Out-Null
    @{
        file = $FilePath
        arguments = @($ArgumentList)
        working_directory = $WorkingDirectory
        user_data = $script:userData
        instance_scope = $env:VIBEOCR_SELF_TEST_INSTANCE
    } | ConvertTo-Json | Set-Content -LiteralPath $Launch
    $script:launchedRoot = Split-Path -Parent $FilePath
    @{
        schema_version = 1; state = 'bridge-ready'; resources = 'verified';
        layout_sizes_verified = 2; owner_restore = 'verified'
        webview2 = @{
            browser_process_id = 4242
            browser_version = '__LOCKED_VERSION__'
            user_data_folder = $script:userData
            browser_executable_path = $script:browserExe
        }
    } | ConvertTo-Json -Compress |
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
""".replace("__LOCKED_VERSION__", _LOCKED_WEBVIEW2_VERSION),
        encoding="utf-8",
    )

    if not layout_verified:
        wrapper.write_text(
            wrapper.read_text(encoding="utf-8").replace(
                "layout_sizes_verified = 2", "layout_sizes_verified = $null"
            ),
            encoding="utf-8",
        )

    result = subprocess.run(
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
        capture_output=True,
        text=True,
    )

    if not layout_verified:
        assert result.returncode != 0
        assert "Web workbench health signal is invalid" in result.stderr
        return
    assert result.returncode == 0, result.stderr

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
    user_data = Path(launched["user_data"])
    # #321：随包 Fixed 模式 userData 必须是隔离树内并列版本化目录。
    assert user_data.name == _LOCKED_WEBVIEW2_VERSION
    assert user_data.parent.name == "webview2-fixed"
    assert user_data.parents[1].name == "state"
    assert user_data.parents[2] == isolated_product
    assert user_data != Path(launched["working_directory"])
    assert cleanup.read_text(encoding="utf-8-sig").strip() == "2"
    assert not user_data.exists()
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


_REUSE_SMOKE_WRAPPER = r"""param(
    [string]$Smoke,
    [string]$Product,
    [string]$TempBase,
    [string]$Launch,
    [string]$Cleanup,
    [string]$Removals,
    [string]$BrowserOutcome
)
$ErrorActionPreference = 'Stop'
$env:TEMP = $TempBase
$env:TMP = $TempBase
$global:webViewCleanupAttempts = 0
$global:browserOutcome = $BrowserOutcome
$global:browserCensuses = 0
$global:exitedBrowsers = [Collections.Generic.HashSet[int]]::new()
$global:browserFixtureStart = [datetime]'2026-11-18T00:00:00Z'
$global:browserFixtureExe = Join-Path $Product 'app/WebView2Runtime/msedgewebview2.exe'
$global:outsideBrowserExe = Join-Path "$Product-sibling" 'app/WebView2Runtime/msedgewebview2.exe'
function Copy-Item {
    throw 'reuse path must not copy the unpacked product'
}
function Remove-Item {
    [CmdletBinding()]
    param(
        [string]$LiteralPath,
        [switch]$Recurse,
        [switch]$Force
    )
    Add-Content -LiteralPath $Removals -Value $LiteralPath
    if ($LiteralPath -like '*vibeocr-web-smoke-*') {
        $global:webViewCleanupAttempts += 1
        if ($global:webViewCleanupAttempts -eq 1) {
            throw 'simulated WebView2 file handle is still active'
        }
    }
    Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
}
function Get-CimInstance {
    param($Filter)
    $global:browserCensuses++
    [pscustomobject]@{
        ProcessId = 4243; ExecutablePath = $global:outsideBrowserExe; CommandLine = "$Product\cache"
        CreationDate = $global:browserFixtureStart
    }
    [pscustomobject]@{ ProcessId = 4245; ExecutablePath = $null; CreationDate = $global:browserFixtureStart }
    if ($global:browserCensuses -eq 1) {
        [pscustomobject]@{ ProcessId = 4242; ExecutablePath = $global:browserFixtureExe; CreationDate = $global:browserFixtureStart }
        [pscustomobject]@{
            ProcessId = 4244; ExecutablePath = $global:browserFixtureExe; CreationDate = $global:browserFixtureStart.AddSeconds(-1)
        }
    } elseif ($global:exitedBrowsers.Contains(4242) -and -not $global:exitedBrowsers.Contains(4246)) {
        [pscustomobject]@{ ProcessId = 4246; ExecutablePath = $global:browserFixtureExe; CreationDate = $global:browserFixtureStart }
    }
}
function Get-Process {
    [CmdletBinding()]
    param([int]$Id)
    if ($Id -eq 4243) { throw 'unowned browser was selected' }
    $browser = [pscustomobject]@{
        Id = $Id; HasExited = $false; StopRequested = $false
        StartTime = if ($Id -ne 4242 -or $global:browserOutcome -ne 'no-start-time') { $global:browserFixtureStart }
        SafeHandle = if (($Id -ne 4242 -or $global:browserOutcome -ne 'no-handle') -and
            ($Id -ne 4245 -or $global:browserOutcome -ne 'unreadable-external')) {
            [pscustomobject]@{ IsClosed = $false; IsInvalid = $false }
        }
        Path = if ($Id -eq 4245 -and $global:browserOutcome -ne 'unreadable-external') { $global:outsideBrowserExe }
    }
    $browser | Add-Member ScriptMethod Kill {
        if ($this.Id -notin @(4242, 4246) -or $null -eq $this.SafeHandle -or $null -eq $this.StartTime) {
            throw 'termination attempted without a pinned owned browser'
        }
        $this.StopRequested = $true
    }
    $browser | Add-Member ScriptMethod WaitForExit {
        param([int]$Milliseconds)
        if ($Milliseconds -le 0 -or $Milliseconds -gt 5000) { throw 'invalid browser exit deadline' }
        if ($this.Id -eq 4245) { throw 'waited for a browser without ownership evidence' }
        if (-not $this.StopRequested) { return $false }
        $this.HasExited = $true
        $null = $global:exitedBrowsers.Add($this.Id)
        return $true
    }
    $browser | Add-Member ScriptMethod Dispose {}
    return $browser
}
function Stop-Process {
    [CmdletBinding()]
    param([int]$Id, [switch]$Force)
}
function Start-Process {
    param(
        [string]$FilePath,
        [object[]]$ArgumentList,
        [string]$WorkingDirectory,
        [string]$WindowStyle,
        [switch]$PassThru
    )
    $exeDir = Split-Path -Parent $FilePath
    $script:productRoot = if (Test-Path -LiteralPath (Join-Path $Product 'app/metadata/product-layout.json')) { Split-Path -Parent $exeDir } else { $exeDir }
    $script:userData = [IO.Path]::GetFullPath((Join-Path (Join-Path $script:productRoot 'state') (Join-Path 'webview2-fixed' '__LOCKED_VERSION__')))
    $script:browserExe = [IO.Path]::GetFullPath((Join-Path (Join-Path $exeDir 'WebView2Runtime') 'msedgewebview2.exe'))
    New-Item -ItemType Directory -Path $script:userData -Force | Out-Null
    @{
        file = $FilePath
        arguments = @($ArgumentList)
        working_directory = $WorkingDirectory
        user_data = $script:userData
        instance_scope = $env:VIBEOCR_SELF_TEST_INSTANCE
        health = $env:VIBEOCR_WEB_READY_FILE
    } | ConvertTo-Json | Set-Content -LiteralPath $Launch
    $script:launchedRoot = Split-Path -Parent $FilePath
    @{
        schema_version = 1; state = 'bridge-ready'; resources = 'verified';
        layout_sizes_verified = 2; owner_restore = 'verified'
        webview2 = @{
            browser_process_id = 4242
            browser_version = '__LOCKED_VERSION__'
            user_data_folder = $script:userData
            browser_executable_path = $script:browserExe
        }
    } | ConvertTo-Json -Compress |
        Set-Content -LiteralPath $env:VIBEOCR_WEB_READY_FILE
    $process = [pscustomobject]@{ ExitCode = 0 }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int]$Milliseconds)
        return $true
    }
    return $process
}
$env:VIBEOCR_SELF_TEST_INSTANCE = 'outer-test-scope'
& $Smoke -ProductRoot $Product -ReuseUnpackedProduct
if (-not $global:exitedBrowsers.SetEquals([int[]]@(4242, 4246)) -or $global:browserCensuses -lt 3) {
    throw 'unpack owner resumed before all owned browsers, including a late helper, exited'
}
if ($env:VIBEOCR_SELF_TEST_INSTANCE -ne 'outer-test-scope') {
    throw 'smoke did not restore the caller instance scope'
}
$global:webViewCleanupAttempts | Set-Content -LiteralPath $Cleanup
"""


def _run_reuse_smoke(
    tmp_path: Path, product: Path, temp_base: Path, *, browser_outcome: str = "success"
) -> subprocess.CompletedProcess[str]:
    root = Path(__file__).parents[2]
    launch = tmp_path / "launch.json"
    cleanup = tmp_path / "cleanup.txt"
    removals = tmp_path / "removals.log"
    wrapper = tmp_path / "run-reuse-smoke.ps1"
    wrapper.write_text(
        _REUSE_SMOKE_WRAPPER.replace("__LOCKED_VERSION__", _LOCKED_WEBVIEW2_VERSION),
        encoding="utf-8",
    )
    return subprocess.run(
        [
            resolve_executable("pwsh"),
            "-NoProfile",
            "-File",
            str(wrapper),
            str(root / "scripts/smoke_web_workbench.ps1"),
            str(product),
            str(temp_base),
            str(launch),
            str(cleanup),
            str(removals),
            browser_outcome,
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=120,
    )


@pytest.mark.parametrize(
    ("browser_outcome", "reason"),
    [
        ("no-handle", "unable to pin a valid process handle"),
        ("no-start-time", "process creation time is unavailable"),
    ],
)
def test_web_ready_smoke_refuses_to_kill_an_unverified_browser(
    tmp_path: Path, browser_outcome: str, reason: str
) -> None:
    temp_base = tmp_path / "temp"
    current = temp_base / "vibeocr-release-smoke-0a1b2c3d" / "current"
    current.mkdir(parents=True)
    _write_installed_product_fixture(current)

    result = _run_reuse_smoke(
        tmp_path, current, temp_base, browser_outcome=browser_outcome
    )

    assert result.returncode != 0
    assert "Cannot establish identity or confirm exit" in result.stderr
    assert reason in result.stderr
    assert "termination attempted without a pinned owned browser" not in result.stderr
    assert (current / "app/VibeOCR.WinUI.exe").is_file()


@pytest.mark.parametrize("browser_outcome", ["success", "unreadable-external"])
def test_web_ready_smoke_reuses_the_release_smoke_unpack_tree_in_place(
    tmp_path: Path, browser_outcome: str
) -> None:
    temp_base = tmp_path / "temp[test]"
    holder = temp_base / "vibeocr-release-smoke-0a1b2c3d"
    current = holder / "current"
    current.mkdir(parents=True)
    _write_installed_product_fixture(current)

    result = _run_reuse_smoke(
        tmp_path, current, temp_base, browser_outcome=browser_outcome
    )

    assert result.returncode == 0, result.stderr
    launch = json.loads((tmp_path / "launch.json").read_text(encoding="utf-8-sig"))
    # 运行根仍是原 current 解包树:executable/install-root 不变,未复制。
    assert Path(launch["file"]) == current / "app/VibeOCR.WinUI.exe"
    assert Path(str(launch["working_directory"])) == current / "app"
    assert launch["arguments"] == [
        "--shell-only",
        "--profile",
        "production",
        "--install-root",
        str(current),
    ]
    user_data = Path(launch["user_data"])
    # #321：随包 Fixed userData 是复用解包树内的并列版本化目录，
    # 本 smoke 不删除它（真实清理由 release_smoke 的临时目录负责）。
    assert user_data.name == _LOCKED_WEBVIEW2_VERSION
    assert user_data.parent.name == "webview2-fixed"
    assert user_data.parents[1].name == "state"
    assert user_data.parents[2] == current
    assert user_data != Path(launch["working_directory"])
    # 健康信号清理仍由 PowerShell 完成,且从不触碰原解包树。
    assert not Path(launch["health"]).exists()
    assert user_data.is_dir()
    assert (tmp_path / "cleanup.txt").read_text(encoding="utf-8-sig").strip() == "0"
    removed = (tmp_path / "removals.log").read_text(encoding="utf-8-sig").splitlines()
    assert any("vibeocr-web-ready-" in line for line in removed)
    assert not any("webview2-fixed" in line for line in removed)
    assert not any(Path(line) == current for line in removed)
    assert {path.name for path in current.iterdir()} == {
        "VibeOCR.exe",
        "Velopack.dll",
        "LICENSE",
        "CHANGELOG.md",
        "app",
        "runtime",
        "state",
    }
    assert (current / "runtime/env/runtime-env/python.exe").read_bytes() == (
        b"placeholder"
    )
    assert {path.name for path in temp_base.iterdir()} == {holder.name}


@pytest.mark.parametrize(
    "case",
    [
        "external-root",
        "plain-temp-child",
        "holder-outside-temp",
        "existing-state",
        "existing-portable-layout",
        "dangling-state-link",
        "reparse-current",
        "reparse-holder",
    ],
)
def test_web_ready_smoke_reuse_rejects_unsafe_product_roots(
    tmp_path: Path, case: str
) -> None:
    import _winapi

    temp_base = tmp_path / "temp"
    temp_base.mkdir()
    holder_name = "vibeocr-release-smoke-9z8y7x6w"
    product = temp_base / holder_name / "current"
    if case == "external-root":
        product = tmp_path / "elsewhere" / "product"
        product.mkdir(parents=True)
        expected = "named 'current'"
    elif case == "plain-temp-child":
        product = temp_base / "current"
        product.mkdir()
        expected = "dedicated vibeocr-release-smoke-"
    elif case == "holder-outside-temp":
        product = tmp_path / "elsewhere" / holder_name / "current"
        product.mkdir(parents=True)
        expected = "direct child of the current TEMP"
    elif case == "existing-state":
        product.mkdir(parents=True)
        (product / "state").mkdir()
        (product / "state" / "user.db").write_bytes(b"user-data")
        expected = "pristine unpacked product"
    elif case == "existing-portable-layout":
        product.mkdir(parents=True)
        (product / "portable-layout.json").write_text("{}", encoding="utf-8")
        expected = "pristine unpacked product"
    elif case == "dangling-state-link":
        product.mkdir(parents=True)
        target = temp_base / "vanishing-target"
        target.mkdir()
        _winapi.CreateJunction(str(target), str(product / "state"))
        shutil.rmtree(target)
        expected = "pristine unpacked product"
    elif case == "reparse-current":
        product.parent.mkdir(parents=True)
        target = temp_base / "real-current"
        target.mkdir()
        _winapi.CreateJunction(str(target), str(product))
        expected = "reparse points"
    else:
        real_holder = temp_base / "real-holder"
        (real_holder / "current").mkdir(parents=True)
        _winapi.CreateJunction(str(real_holder), str(temp_base / holder_name))
        expected = "reparse points"
    marker = product / "marker.txt"
    marker.write_text("keep", encoding="utf-8")

    result = _run_reuse_smoke(tmp_path, product, temp_base)

    assert result.returncode != 0
    assert expected in result.stderr
    # 拒绝发生在读取 descriptor/复制/启动之前,既有数据原样保留。
    assert marker.read_text(encoding="utf-8") == "keep"
    assert not (tmp_path / "launch.json").exists()
    if case == "existing-state":
        assert (product / "state" / "user.db").read_bytes() == b"user-data"
    if case == "reparse-holder":
        assert (temp_base / holder_name).exists()


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

    assert platform_test[-5:] == [
        "--hangdump",
        "--hangdump-timeout",
        "2m",
        "--hangdump-type",
        "None",
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
    assert (
        program.index("LegacyVelopackStateMigration.Resume(")
        < program.index("if (!File.Exists(velopackDll))")
        < program.index("RunVelopack(productRoot);")
        < program.index("return Launch(args,")
    )
    assert program.index("LegacyVelopackStateMigration.Resume(") < program.index(
        "InitializeLog(productRoot);"
    )
    assert "[MethodImpl(MethodImplOptions.NoInlining)]" in program
    assert '<PackageReference Include="Velopack" />' in project
    child_program = (root / "src/dotnet/VibeOCR.App/Program.cs").read_text(
        encoding="utf-8"
    )
    assert child_program.index("VelopackApp.Build().Run();") < child_program.index(
        "Application.Start"
    )


def test_bootstrapper_probes_the_bundled_runtime_payload() -> None:
    root = Path(__file__).parents[2]
    program = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )
    project = (
        root / "src/dotnet/VibeOCR.Bootstrapper/VibeOCR.Bootstrapper.csproj"
    ).read_text(encoding="utf-8")

    # #321：Bootstrapper 只检查随包载荷，不再探测已安装系统运行库。
    assert "using Microsoft.Web.WebView2.Core;" not in program
    assert '<PackageReference Include="Microsoft.Web.WebView2" />' not in project
    assert "DotNetRuntimeProbe" not in program
    assert "PackageManager" not in program
    assert "GetAvailableCoreWebView2BrowserVersionString" not in program
    assert "BundledRuntimeConfig.MissingClrStartupFiles" in program
    assert "BundledRuntimeConfig.FindNetCoreAppVersion" in program
    assert 'Path.ChangeExtension(layout.AppEntry, ".runtimeconfig.json")' in program
    assert "MissingWindowsAppRuntimeFiles(layout.AppRoot)" in program
    assert 'layout.WebView2RuntimeRoot, "msedgewebview2.exe"' in program
    assert "Repair with the in-app update or a fresh complete download." in program


def test_bundled_windows_app_runtime_startup_closure_is_shared_and_covers_xaml() -> (
    None
):
    root = Path(__file__).parents[2]
    shared = (
        root / "src/dotnet/VibeOCR.ProductLayout.Shared/ProductLayout.cs"
    ).read_text(encoding="utf-8")
    bootstrapper = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )
    detector = (
        root / "src/dotnet/VibeOCR.Platform/Bootstrap/PrerequisiteDetector.cs"
    ).read_text(encoding="utf-8")

    # #321：UI.Xaml 是真实启动关键；闭包两侧共用。
    closure = shared[shared.index("WindowsAppRuntimeStartupFiles") :]
    assert "Microsoft.WindowsAppRuntime.dll" in closure
    assert "Microsoft.UI.Xaml.dll" in closure
    assert "MissingWindowsAppRuntimeFiles(layout.AppRoot)" in bootstrapper
    assert "MissingWindowsAppRuntimeFiles(appRoot)" in detector


def test_bundled_dotnet_startup_closure_and_runtimeconfig_semantics_are_shared() -> (
    None
):
    root = Path(__file__).parents[2]
    shared = (
        root / "src/dotnet/VibeOCR.ProductLayout.Shared/DotNetRuntimeProbe.cs"
    ).read_text(encoding="utf-8")
    bootstrapper = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )
    detector = (
        root / "src/dotnet/VibeOCR.Platform/Bootstrap/PrerequisiteDetector.cs"
    ).read_text(encoding="utf-8")

    # #321：CoreLib 属 CLR 启动必需；runtimeconfig 语义两侧复用同一解析。
    closure = shared[shared.index("ClrStartupFiles") :]
    assert "System.Private.CoreLib.dll" in closure
    assert "DataContractJsonSerializer" in shared
    assert "BundledRuntimeConfig.FindNetCoreAppVersion" in bootstrapper
    assert "BundledRuntimeConfig" in detector
    assert "System.Text.Json" not in detector


def test_windows_app_runtime_identity_checks_stay_in_the_dev_detector() -> None:
    root = Path(__file__).parents[2]
    program = (root / "src/dotnet/VibeOCR.Bootstrapper/Program.cs").read_text(
        encoding="utf-8"
    )
    detector = (
        root / "src/dotnet/VibeOCR.Platform/Bootstrap/PrerequisiteDetector.cs"
    ).read_text(encoding="utf-8")

    # 随包 Bootstrapper 不再接受任何系统包身份；winui-dev 探测保留。
    assert "Microsoft.WindowsAppRuntime.CBS" not in program
    assert 'Microsoft.WindowsAppRuntime.CBS.2"' in detector


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
    artifacts, _calls, _unpack_roots = _configure_release_smoke_fixture(
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


@pytest.mark.parametrize(
    "outcome", ["failed", "pending", "ready_without_exit", "diagnostic_io_failure"]
)
def test_web_smoke_preserves_failure_stage_before_cleanup(
    tmp_path: Path, outcome: str
) -> None:
    root = Path(__file__).parents[2]
    product = tmp_path / "product"
    product.mkdir()
    (product / "VibeOCR.WinUI.exe").write_bytes(b"test process fixture")
    diagnostics = tmp_path / "diagnostics"
    if outcome == "diagnostic_io_failure":
        diagnostics.write_text(
            "file blocks diagnostic directory creation", encoding="utf-8"
        )
    wrapper = tmp_path / "run-smoke.ps1"
    wrapper.write_text(
        r"""param($Smoke, $Product, $Diagnostics, $Outcome)
$ErrorActionPreference = 'Stop'
$global:outcome = $Outcome
function Get-CimInstance { param($Filter) @() }
function Start-Process {
    param($FilePath, $ArgumentList, $WorkingDirectory, $WindowStyle, [switch]$PassThru)
    $state = if ($global:outcome -eq 'pending') { 'starting' } elseif ($global:outcome -eq 'ready_without_exit') { 'bridge-ready' } else { 'failed' }
    @{
        schema_version=1; state=$state; stage='webview-initializing'
        error_type='InvalidOperationException'; error_code=-2146233079
        private_detail='fixture-secret-must-not-survive'
    } | ConvertTo-Json | Set-Content -LiteralPath $env:VIBEOCR_WEB_READY_FILE
    $logs = Join-Path $WorkingDirectory 'state/logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    @(
        '01:02:03.004 [INFO ] OnLaunched: profile=production shellOnly=True',
        '01:02:03.005 [INFO ] Web workbench: webview-initialized',
        '01:02:03.006 [ERROR] unrelated fixture-secret-must-not-survive'
    ) | Set-Content -LiteralPath (Join-Path $logs 'winui-dev-fixture.log')
    $process = [pscustomobject]@{ ExitCode=1; HasExited=$false; Waits=0 }
    $process | Add-Member ScriptMethod WaitForExit {
        param($Milliseconds)
        $this.Waits++
        if ($global:outcome -in @('pending', 'ready_without_exit') -and $this.Waits -eq 1) { return $false }
        $this.HasExited=$true
        return $true
    }
    $process | Add-Member ScriptMethod Kill { param($EntireTree) $this.HasExited=$true; $this.ExitCode=-1 }
    return $process
}
& $Smoke -ProductRoot $Product -TimeoutSeconds 1 -DiagnosticsRoot $Diagnostics
""",
        encoding="utf-8",
    )
    result = subprocess.run(
        [
            resolve_executable("pwsh"),
            "-NoProfile",
            "-File",
            str(wrapper),
            str(root / "scripts/smoke_web_workbench.ps1"),
            str(product),
            str(diagnostics),
            outcome,
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=20,
    )
    assert result.returncode != 0
    if outcome == "diagnostic_io_failure":
        assert "diagnostic capture failed" in result.stdout
        assert "smoke exited with code 1" in result.stderr
        return
    reports = list(diagnostics.glob("*/failure.json"))
    assert len(reports) == 1
    raw = reports[0].read_text(encoding="utf-8-sig")
    assert "fixture-secret" not in raw
    report = json.loads(raw)
    assert report["process_exited"] is True
    assert report["stage"] == "webview-initializing"
    assert report["error_type"] == "InvalidOperationException"
    assert len(report["app_signals"]) == 2
    if outcome == "failed":
        assert report["failure_kind"] == "process-or-health-failure"
        assert report["state"] == "failed"
    else:
        assert report["failure_kind"] == "deadline-exceeded"
        assert report["state"] == (
            "starting" if outcome == "pending" else "bridge-ready"
        )
    assert "Web workbench diagnostic evidence:" in result.stdout
