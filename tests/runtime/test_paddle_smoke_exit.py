"""smoke_paddle_modes.ps1 Invoke-PaddlePhase 退出码门禁回归。

复现形态：候选 App 写完 passed health 后进程崩溃（如 wetype_tip.dll
0xC0000409），旧实现只检查 health 文件存在，仍打印 Install phase OK。
本回归用 PowerShell AST 从真实脚本提取原函数（不复制实现、不做源码
字符串断言），以同名 Start-Process 函数替身（函数优先于 cmdlet）模拟
"写 passed health + 指定退出码"，验证 passed health 不能掩盖非零退出；同时保留退出码 0 时 passed/blocked 的原有返回语义。
整改：每 phase 另持久化 wrapper exit0 receipt（不改 App 原始 health），
三个 Resume 复用分支（install/mode/input）无有效 receipt 一律 fail closed，
不得默认 exit0 兼容历史无 receipt 的根；显式 -Attempt 或新隔离根才能重验。
"""

from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

import pytest

from scripts.check_quality import resolve_executable

REPO_ROOT = Path(__file__).resolve().parents[2]

# 默认取仓库当前脚本；可用 PADDLE_SMOKE_SCRIPT 指向历史副本（如
# git show 导出的旧实现）复现本回归在旧实现上的失败。
PADDLE_SMOKE_SCRIPT = Path(
    os.environ.get(
        "PADDLE_SMOKE_SCRIPT", str(REPO_ROOT / "scripts" / "smoke_paddle_modes.ps1")
    )
)

# 替身进程环境：函数所需的脚本级变量 + 模拟 App 的 Start-Process。
HARNESS = r"""
$ErrorActionPreference = 'Stop'
$smokeRoot = $env:SMOKE_ROOT
$candidate = Join-Path $smokeRoot 'candidate'
$webViewData = Join-Path $smokeRoot 'webview2'
$Provider = 'cpu'
$ModelPreset = 'small'
$script:mockExitCode = [int]$env:MOCK_EXIT_CODE
$script:launchCount = 0

# 模拟候选应用：先落盘 health（MOCK_STATE），再按 MOCK_EXIT_CODE 退出。
function Start-Process {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory,
        [string]$WindowStyle, [switch]$PassThru)
    $script:launchCount += 1
    $health = @{
        schema_version = 1; state = $env:MOCK_STATE; phase = $env:MOCK_PHASE
        stage = 'app'; error = $null; evidence = @{}
    }
    Set-Content -LiteralPath $env:VIBEOCR_PADDLE_SMOKE_HEALTH `
        -Value (ConvertTo-Json $health -Depth 5) -Encoding utf8
    $process = [pscustomobject] @{ ExitCode = $script:mockExitCode }
    Add-Member -InputObject $process -MemberType ScriptMethod -Name WaitForExit `
        -Value { param($milliseconds) $true }
    Add-Member -InputObject $process -MemberType ScriptMethod -Name Kill -Value { }
    return $process
}

# PowerShell AST 提取真实脚本中的原函数定义。
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $env:PADDLE_SMOKE_SCRIPT, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw "脚本解析失败: $($errors[0].Message)" }
$fn = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Invoke-PaddlePhase' }, $true))
if ($fn.Count -ne 1) { throw "Invoke-PaddlePhase 提取数异常: $($fn.Count)" }
Invoke-Expression $fn[0].Extent.Text

$outcome = @{ threw = $false; returnedState = $null; message = '' }
try {
    $health = Invoke-PaddlePhase @{ 'VIBEOCR_PADDLE_SMOKE_PHASE' = $env:MOCK_PHASE } `
        "paddle-$($env:MOCK_PHASE)" 1
    $outcome.returnedState = $health.state
} catch {
    $outcome.threw = $true
    $outcome.message = $_.Exception.Message
}
[pscustomobject]@{
    threw = $outcome.threw
    returnedState = $outcome.returnedState
    message = $outcome.message
    launchCount = $script:launchCount
    healthKept = (Test-Path -LiteralPath (Join-Path $smokeRoot "paddle-$($env:MOCK_PHASE).json") -PathType Leaf)
    receiptExists = (Test-Path -LiteralPath (Join-Path $smokeRoot "receipts/paddle-$($env:MOCK_PHASE).json") -PathType Leaf)
} | ConvertTo-Json -Compress
"""


def _run_phase(
    tmp_path: Path,
    *,
    exit_code: int,
    state: str,
    phase: str = "install",
    root: Path | None = None,
) -> dict[str, object]:
    smoke_root = root if root is not None else tmp_path / "smoke-root"
    smoke_root.mkdir(parents=True, exist_ok=True)
    harness = tmp_path / "harness.ps1"
    harness.write_text(HARNESS, encoding="utf-8", newline="\n")
    completed = subprocess.run(
        [
            resolve_executable("pwsh"),
            "-NoProfile",
            "-File",
            str(harness),
        ],
        check=False,
        timeout=30,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=os.environ
        | {
            "PADDLE_SMOKE_SCRIPT": str(PADDLE_SMOKE_SCRIPT),
            "SMOKE_ROOT": str(smoke_root),
            "MOCK_EXIT_CODE": str(exit_code),
            "MOCK_STATE": state,
            "MOCK_PHASE": phase,
        },
    )
    assert completed.returncode == 0, completed.stdout + completed.stderr
    payload = [line for line in completed.stdout.splitlines() if line.startswith("{")]
    assert payload, completed.stdout + completed.stderr
    outcome: dict[str, object] = json.loads(payload[-1])
    assert outcome["launchCount"] == 1
    return outcome


def test_passed_health_cannot_mask_nonzero_exit(tmp_path: Path) -> None:
    # 0xC0000409 崩溃（Int32 视图）且已落盘 passed health：必须明确失败，
    # 失败信息点名退出码，health 与现场保留。旧实现在此不抛错即失败本断言。
    outcome = _run_phase(tmp_path, exit_code=-1073740791, state="passed")
    assert outcome["threw"] is True
    assert "-1073740791" in str(outcome["message"])
    assert outcome["healthKept"] is True
    # 崩溃路径不写 receipt：复用门禁的持久化前提。
    assert outcome["receiptExists"] is False


@pytest.mark.parametrize("state", ["passed", "blocked"])
def test_zero_exit_preserves_health_state_semantics(tmp_path: Path, state: str) -> None:
    # 退出码 0：passed 正常返回，blocked 语义保留（由调用方解释，不抛错）。
    outcome = _run_phase(tmp_path, exit_code=0, state=state)
    assert outcome["threw"] is False
    assert outcome["returnedState"] == state
    assert outcome["healthKept"] is True
    # 正常完成才落盘 exit0 receipt，供 Resume 复用分支验收。
    assert outcome["receiptExists"] is True


# ResumeRoot 端到端回归：真实脚本 -File 执行，断言退出码与现场保留。
RESUME_MARKERS = (
    "candidate/app/VibeOCR.WinUI.exe",
    "candidate/app/metadata/product-layout.json",
    "candidate/runtime/backend/runtime-manifest.json",
    "candidate/runtime/installer/vibeocr-runtime-installer.exe",
    "fixtures/text_zh_en.png",
    "fixtures/table_merged_zh_en.png",
    "fixtures/document_mixed.png",
    "fixtures/table_wireless.png",
)


def _fabricate_resume_root(root: Path) -> None:
    # ResumeRoot 预检所需的最小隔离根结构（候选 markers + fixture manifest）。
    for relative in RESUME_MARKERS:
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"smoke-exit dummy\n")
    (root / "fixtures/manifest.json").write_text(
        json.dumps({"generator": "scripts/paddle_smoke_fixtures.py"}),
        encoding="utf-8",
    )


def _write_health(root: Path, phase: str, state: str) -> None:
    # 直接落盘历史 health（无 receipt 的“旧根”形态）。
    (root / f"{phase}.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "state": state,
                "stage": "app",
                "error": None,
                "evidence": {},
            }
        ),
        encoding="utf-8",
    )


def _run_resume(root: Path, *script_args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            resolve_executable("pwsh"),
            "-NoProfile",
            "-File",
            str(PADDLE_SMOKE_SCRIPT),
            "-ResumeRoot",
            str(root),
            *script_args,
        ],
        check=False,
        timeout=30,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def test_fresh_crash_leaves_no_receipt_and_resume_rejects(tmp_path: Path) -> None:
    # fresh 非零退出 throw：health 仍 passed 但无 receipt；ResumeRoot
    # 复用分支必须 fail closed（非 0 退出），原始 health 现场保留。
    root = tmp_path / "resume-root"
    _fabricate_resume_root(root)
    outcome = _run_phase(
        tmp_path, exit_code=-1073740791, state="passed", phase="install", root=root
    )
    assert outcome["threw"] is True
    assert outcome["receiptExists"] is False
    completed = _run_resume(root, "-Modes", "paddle_text")
    assert completed.returncode != 0
    assert "receipt" in completed.stderr
    assert (root / "paddle-install.json").exists()


def test_resume_reuses_phases_with_exit0_receipts(tmp_path: Path) -> None:
    # exit0 完成才写 receipt；带有效 receipt 的 passed health 可复用，exit 0。
    root = tmp_path / "resume-root"
    _fabricate_resume_root(root)
    _run_phase(tmp_path, exit_code=0, state="passed", phase="install", root=root)
    _run_phase(tmp_path, exit_code=0, state="passed", phase="paddle_text", root=root)
    completed = _run_resume(root, "-Modes", "paddle_text")
    assert completed.returncode == 0
    assert "All requested modes PASSED." in completed.stdout


def test_resume_rejects_historical_health_without_receipt(tmp_path: Path) -> None:
    # 历史根只有 health、无 receipt：不得默认 exit0 兼容，install 复用即拒绝。
    root = tmp_path / "resume-root"
    _fabricate_resume_root(root)
    _write_health(root, "paddle-install", "passed")
    _write_health(root, "paddle-paddle_text", "passed")
    completed = _run_resume(root, "-Modes", "paddle_text")
    assert completed.returncode != 0
    assert "receipt" in completed.stderr
    assert (root / "paddle-install.json").exists()
    assert (root / "paddle-paddle_text.json").exists()


def test_resume_rejects_mode_health_without_receipt(tmp_path: Path) -> None:
    # install 复用合法，但 mode health 缺 receipt：mode 复用分支同样 fail closed。
    root = tmp_path / "resume-root"
    _fabricate_resume_root(root)
    _run_phase(tmp_path, exit_code=0, state="passed", phase="install", root=root)
    _write_health(root, "paddle-paddle_text", "passed")
    completed = _run_resume(root, "-Modes", "paddle_text")
    assert completed.returncode != 0
    assert "receipt" in completed.stderr
    assert (root / "paddle-paddle_text.json").exists()


def test_resume_rejects_input_health_without_receipt(tmp_path: Path) -> None:
    # input 复用分支：缺 receipt 的既有 input health 拒绝复用。
    root = tmp_path / "resume-root"
    _fabricate_resume_root(root)
    _run_phase(tmp_path, exit_code=0, state="passed", phase="install", root=root)
    _run_phase(tmp_path, exit_code=0, state="passed", phase="paddle_table", root=root)
    _write_health(root, "paddle-input-file", "passed")
    completed = _run_resume(root, "-Modes", "paddle_table", "-InputKinds", "file")
    assert completed.returncode != 0
    assert "receipt" in completed.stderr
    assert (root / "paddle-input-file.json").exists()


def test_resume_kept_blocked_with_receipt_keeps_blocked_exit(tmp_path: Path) -> None:
    # blocked + 有效 receipt：原 blocked 语义保留（exit 3，不放行不升级）。
    root = tmp_path / "resume-root"
    _fabricate_resume_root(root)
    _run_phase(tmp_path, exit_code=0, state="passed", phase="install", root=root)
    _run_phase(tmp_path, exit_code=0, state="blocked", phase="paddle_text", root=root)
    completed = _run_resume(root, "-Modes", "paddle_text")
    assert completed.returncode == 3
