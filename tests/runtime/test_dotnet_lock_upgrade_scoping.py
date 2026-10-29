"""update_dotnet_locks.ps1 选择性升级入口的行为合同测试。

两层 seam，均不触碰真实仓库文件、不发起真实网络、不运行真实 restore：

1. 函数级：沿用 smoke_paddle_modes 的 PowerShell AST 提取——从真实脚本
   提取 Update-CentralPackageVersions 原函数，在隔离夹具目录以替身
   Invoke-RestMethod 模拟 nuget.org flatcontainer 元数据，验证真实选择
   行为（目标升级 / 未选保留且零元数据请求 / 未知 id fail closed /
   省略选择集保持历史全量模式 / 已知 id 集只认 PackageVersion 元素）。
2. 脚本级：把真实脚本复制进临时 fixture 仓库，假 DOTNET_ROOT 只放
   不可执行哨兵 dotnet.exe（任何意外 restore 试图运行它都会以与门禁
   不同的错误失败），wrapper 定义 Invoke-RestMethod 拒绝网络。回归只会
   触及 fixture，不可能修改工作树或启动真实 restore。
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

from scripts.check_quality import resolve_executable

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "scripts" / "update_dotnet_locks.ps1"

FIXTURE_PROPS = """<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Velopack" Version="1.2.161" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
    <PackageVersion Include="xunit.v3" Version="4.0.1" />
  </ItemGroup>
</Project>
"""

# 带"可升级"假新版的未选包，用于证明未选包未被触碰。
METADATA = {
    "xunit.runner.visualstudio": ["3.1.5", "4.0.0", "4.0.1"],
    "xunit.v3": ["4.0.0", "4.0.1", "4.0.2"],
    "velopack": ["1.2.161", "9.9.9"],
}

HARNESS = r"""
$ErrorActionPreference = 'Stop'
$repo = $env:FIXTURE_ROOT
$metadata = (Get-Content -LiteralPath $env:METADATA_JSON -Raw |
    ConvertFrom-Json -AsHashtable)
$script:metadataCalls = [System.Collections.Generic.List[string]]::new()

function Invoke-RestMethod {
    param([string]$Uri)
    if ($Uri -notmatch 'api\.nuget\.org/v3-flatcontainer/([^/]+)/index\.json') {
        throw "unexpected metadata url: $Uri"
    }
    $id = $Matches[1]
    $script:metadataCalls.Add($id)
    if (-not $metadata.ContainsKey($id)) {
        throw "metadata requested for unknown fixture package: $id"
    }
    [pscustomobject]@{ versions = $metadata[$id] }
}

$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $env:TARGET_SCRIPT, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw "script parse failed: $($errors[0].Message)" }
$fn = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Update-CentralPackageVersions' }, $true))
if ($fn.Count -ne 1) {
    throw "Update-CentralPackageVersions extraction count: $($fn.Count)"
}
Invoke-Expression $fn[0].Extent.Text

$outcome = @{ threw = $false; message = ''; calls = @() }
try {
    if ($env:SELECTED) {
        $selected = @($env:SELECTED -split ';')
        Update-CentralPackageVersions $selected
    } else {
        Update-CentralPackageVersions
    }
} catch {
    $outcome.threw = $true
    $outcome.message = $_.Exception.Message
}
$outcome.calls = $script:metadataCalls
[pscustomobject]$outcome | ConvertTo-Json -Compress
"""

# 脚本级 wrapper：拒网替身优先于 cmdlet；转发参数运行 fixture 副本脚本。
WRAPPER = r"""
$ErrorActionPreference = 'Stop'
function Invoke-RestMethod {
    param([string]$Uri)
    throw "network rejected by fixture: $Uri"
}
& (Join-Path $PSScriptRoot 'scripts' 'update_dotnet_locks.ps1') @args
exit $LASTEXITCODE
"""


def _run_harness(
    tmp_path: Path,
    *,
    selected: list[str] | None,
    props: str = FIXTURE_PROPS,
) -> dict[str, object]:
    fixture_root = tmp_path / "fixture-repo"
    fixture_root.mkdir()
    (fixture_root / "Directory.Packages.props").write_text(props, newline="\n")
    metadata_json = tmp_path / "metadata.json"
    metadata_json.write_text(json.dumps(METADATA), encoding="utf-8")
    harness = tmp_path / "harness.ps1"
    harness.write_text(HARNESS, encoding="utf-8", newline="\n")
    env = os.environ | {
        "FIXTURE_ROOT": str(fixture_root),
        "METADATA_JSON": str(metadata_json),
        "TARGET_SCRIPT": str(SCRIPT),
    }
    if selected is not None:
        env["SELECTED"] = ";".join(selected)
    else:
        env.pop("SELECTED", None)
    completed = subprocess.run(
        [resolve_executable("pwsh"), "-NoProfile", "-File", str(harness)],
        check=False,
        timeout=60,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
    )
    assert completed.returncode == 0, (
        f"harness failed: rc={completed.returncode}\n"
        f"stdout={completed.stdout}\nstderr={completed.stderr}"
    )
    result: dict[str, object] = json.loads(completed.stdout.strip().splitlines()[-1])
    result["_props"] = (fixture_root / "Directory.Packages.props").read_text()
    return result


def _pin(props: str, package_id: str) -> str:
    for line in props.splitlines():
        if f'Include="{package_id}"' in line:
            return line.strip()
    raise AssertionError(f"{package_id} missing from props:\n{props}")


def test_selected_packages_upgrade_and_unselected_are_untouched(
    tmp_path: Path,
) -> None:
    result = _run_harness(
        tmp_path,
        selected=["xunit.runner.visualstudio", "xunit.v3"],
    )
    assert not result["threw"], result["message"]
    props = str(result["_props"])
    assert (
        '<PackageVersion Include="xunit.runner.visualstudio" Version="4.0.1" />'
        in props
    )
    assert '<PackageVersion Include="xunit.v3" Version="4.0.2" />' in props
    # 未选包即使元数据里存在 9.9.9 假新版也逐字节保留，且从未被查询。
    assert _pin(props, "Velopack").endswith('Version="1.2.161" />')
    assert "9.9.9" not in props
    assert sorted(result["calls"]) == ["xunit.runner.visualstudio", "xunit.v3"]


def test_unknown_selection_fails_closed_without_writes_or_metadata(
    tmp_path: Path,
) -> None:
    result = _run_harness(tmp_path, selected=["Not.Pinned", "xunit.v3"])
    assert result["threw"]
    assert "Not.Pinned" in str(result["message"])
    assert result["calls"] == [], result["calls"]
    assert result["_props"] == FIXTURE_PROPS


def test_known_ids_come_only_from_package_version_elements(
    tmp_path: Path,
) -> None:
    # 非 PackageVersion 元素上的同名 Include 属性不能混入已知 id 集合。
    props = FIXTURE_PROPS.replace(
        "  </ItemGroup>",
        '    <ProjectReference Include="Not.Pinned" />\n  </ItemGroup>',
    )
    result = _run_harness(tmp_path, selected=["Not.Pinned"], props=props)
    assert result["threw"]
    assert "Not.Pinned" in str(result["message"])
    assert result["calls"] == [], result["calls"]
    assert result["_props"] == props


def test_omitted_selection_keeps_historical_full_upgrade_mode(
    tmp_path: Path,
) -> None:
    result = _run_harness(tmp_path, selected=None)
    assert not result["threw"], result["message"]
    props = str(result["_props"])
    assert (
        '<PackageVersion Include="xunit.runner.visualstudio" Version="4.0.1" />'
        in props
    )
    assert '<PackageVersion Include="xunit.v3" Version="4.0.2" />' in props
    # 全量模式下未点名包同样前进到（模拟的）最新稳定版。
    assert '<PackageVersion Include="Velopack" Version="9.9.9" />' in props
    assert sorted(result["calls"]) == [
        "velopack",
        "xunit.runner.visualstudio",
        "xunit.v3",
    ]


def _fixture_repo(tmp_path: Path) -> tuple[Path, Path, Path]:
    """复制真实脚本与 central 到临时仓库，DOTNET_ROOT 只放哨兵。"""
    fixture = tmp_path / "fixture-repo"
    (fixture / "scripts").mkdir(parents=True)
    shutil.copy2(SCRIPT, fixture / "scripts" / "update_dotnet_locks.ps1")
    central = fixture / "Directory.Packages.props"
    central.write_text(FIXTURE_PROPS, newline="\n")
    dotnet_root = tmp_path / "fake-dotnet-root"
    dotnet_root.mkdir()
    sentinel = dotnet_root / "dotnet.exe"
    sentinel.write_bytes(b"sentinel-not-an-executable")
    wrapper = fixture / "wrapper.ps1"
    wrapper.write_text(WRAPPER, encoding="utf-8", newline="\n")
    return fixture, central, sentinel


def _run_fixture_script(
    tmp_path: Path, *script_args: str
) -> tuple[subprocess.CompletedProcess[str], Path, Path]:
    fixture, central, sentinel = _fixture_repo(tmp_path)
    env = {k: v for k, v in os.environ.items() if k != "DOTNET_ROOT"} | {
        "DOTNET_ROOT": str(sentinel.parent),
    }
    completed = subprocess.run(
        [
            resolve_executable("pwsh"),
            "-NoProfile",
            "-File",
            str(fixture / "wrapper.ps1"),
            *script_args,
        ],
        check=False,
        timeout=120,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
    )
    return completed, central, sentinel


def assert_gate(
    completed: subprocess.CompletedProcess[str],
    central: Path,
    sentinel: Path,
    message: str,
) -> None:
    combined = completed.stderr + completed.stdout
    assert completed.returncode != 0, combined
    assert message in combined, f"missing {message!r} in: {combined}"
    # 门禁失败不得触碰 fixture central，也不得运行（或读取改变）哨兵。
    assert central.read_text() == FIXTURE_PROPS
    assert sentinel.read_bytes() == b"sentinel-not-an-executable"


def test_gate_rejects_internal_only_combination_before_restore(
    tmp_path: Path,
) -> None:
    completed, central, sentinel = _run_fixture_script(
        tmp_path, "-InternalOnly", "-UpgradePackages", "xunit.v3"
    )
    assert_gate(completed, central, sentinel, "cannot be combined with -InternalOnly")
    # 证明未进入 InternalOnly 的 restore 路径：哨兵从未被当作进程启动。
    assert "not a valid application" not in (completed.stderr + completed.stdout)


def test_gate_rejects_add_combination_before_central_write(tmp_path: Path) -> None:
    completed, central, sentinel = _run_fixture_script(
        tmp_path, "-Add", "New.Pkg=1.0.0", "-UpgradePackages", "xunit.v3"
    )
    assert_gate(completed, central, sentinel, "cannot be combined with -Add")
    assert "New.Pkg" not in central.read_text()


def test_gate_rejects_selection_without_upgrade(tmp_path: Path) -> None:
    completed, central, sentinel = _run_fixture_script(
        tmp_path, "-UpgradePackages", "xunit.v3"
    )
    assert_gate(completed, central, sentinel, "-UpgradePackages requires -Upgrade")


def test_gate_rejects_empty_selection(tmp_path: Path) -> None:
    completed, central, sentinel = _run_fixture_script(
        tmp_path, "-Upgrade", "-UpgradePackages", ","
    )
    assert_gate(completed, central, sentinel, "contained no package id")


def test_gate_rejects_unknown_selection_in_fixture_repo(tmp_path: Path) -> None:
    completed, central, sentinel = _run_fixture_script(
        tmp_path, "-Upgrade", "-UpgradePackages", "Not.Pinned"
    )
    assert_gate(completed, central, sentinel, "not pinned centrally")
    # 未知 id 拒绝必须先于任何元数据请求（wrapper 的拒网替身未被触发）。
    assert "network rejected by fixture" not in (completed.stderr + completed.stdout)
