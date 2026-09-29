<#
.SYNOPSIS
Goal #110 隔离候选专用 Paddle 五模式实机冒烟。

.DESCRIPTION
只复制全新候选到新的隔离根（不删除/复用任何已有 state，不读取真实
桌面内容），用仓库 Python 生成合成 fixture，然后按阶段启动候选应用：
  install   —— 创建具名空环境 "Paddle 冒烟"，公开来源预览 paddleocr-cpu
               配方并确认安装（与 managed-environment 冒烟同一公共 UI）。
  recognize —— 每模式一次独立进程：切换环境、公共 UI 选模式、设置真实
               非默认选项并保存、纯截图（合成 fixture 选区器）+显式
               "识别当前图"、校验 job/环境/管线/选项归属、结构化预览、
               复制与 UI 保存导出。提交被拒 = blocked，真实 job 失败 =
               failed；两者都不是 passed，不盲重试。
证据保留在隔离根；GPU 保持 UNVERIFIED。

.PARAMETER ProductRoot
候选目录（含 app\runtime\installer 布局，且不含 state/）。

.PARAMETER WorkRoot
隔离工作根（不得与 ProductRoot 相互嵌套）。

.PARAMETER Modes
默认五种 paddle 模式；可用子集逐个交付。

.EXAMPLE
pwsh -File scripts/smoke_paddle_modes.ps1 -ProductRoot .release-build\publish -WorkRoot D:\goal103-smoke
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProductRoot,
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [string[]]$Modes = @(
        'paddle_text', 'paddle_table', 'paddle_formula',
        'paddle_structure', 'paddle_document_vl'
    ),
    [int]$ModeTimeoutMinutes = 40,
    [int]$InstallTimeoutMinutes = 60,
    [switch]$SkipInstall,
    [switch]$IncludeWireless
)

$ErrorActionPreference = 'Stop'
if ($ModeTimeoutMinutes -le 0 -or $InstallTimeoutMinutes -le 0) {
    throw 'All timeout parameters must be positive'
}
$source = (Resolve-Path -LiteralPath $ProductRoot).Path.TrimEnd('\')
$work = (Resolve-Path -LiteralPath $WorkRoot).Path.TrimEnd('\')
if ($source.Equals($work, [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $work.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ProductRoot and WorkRoot must not nest'
}
foreach ($marker in @(
    'app\VibeOCR.WinUI.exe',
    'app\metadata\product-layout.json',
    'runtime\backend\runtime-manifest.json',
    'runtime\installer\vibeocr-runtime-installer.exe'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $marker) -PathType Leaf)) {
        throw "Candidate marker missing: $marker"
    }
}
if (Test-Path -LiteralPath (Join-Path $source 'state')) {
    throw 'Source candidate must not contain user or previous smoke state'
}
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

# 模式 → fixture / 非默认选项 / 期望管线 / 结果 token / 公共 UI 导出按钮。
# 选项均为真实构造/预测参数默认值之外的有效取值（默认值见
# src/runtime/.../core/pipelines/*.py）；formula 用本地可信缓存已有的
# PP-FormulaNet_plus-L 以减少下载。
$modeSpec = @{
    'paddle_text' = @{
        Fixture = 'text_zh_en.png'; Pipeline = 'OCR'
        Option = 'use_textline_orientation'; Value = 'true'; Kind = 'bool'
        Tokens = 'GOAL103|1120|2244'; Export = '导出 Markdown'
    }
    'paddle_table' = @{
        Fixture = 'table_merged_zh_en.png'; Pipeline = 'TABLERECOGNITION'
        Option = 'use_table_orientation_classify'; Value = 'false'; Kind = 'bool'
        Tokens = 'GOAL103|1120'; Export = '导出 XLSX|导出 Markdown'
    }
    'paddle_formula' = @{
        Fixture = 'formulas_multi.png'; Pipeline = 'FORMULARECOGNITION'
        Option = 'formula_recognition_model_name'; Value = 'PP-FormulaNet_plus-L'; Kind = 'enum'
        Tokens = ''; Export = '导出 Markdown'
    }
    'paddle_structure' = @{
        Fixture = 'document_mixed.png'; Pipeline = 'PPStructureV3'
        Option = 'use_seal_recognition'; Value = 'true'; Kind = 'bool'
        Tokens = 'GOAL103|1120'; Export = '导出 Markdown|导出 XLSX'
    }
    'paddle_document_vl' = @{
        Fixture = 'document_mixed.png'; Pipeline = 'PaddleOCRVL'
        Option = 'vl_use_layout_detection'; Value = 'false'; Kind = 'bool'
        Tokens = 'GOAL103|1120'; Export = '导出 Markdown'
    }
}
foreach ($mode in $Modes) {
    if (-not $modeSpec.ContainsKey($mode)) { throw "Unknown smoke mode: $mode" }
}

$smokeRoot = Join-Path $work "pm-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
$candidate = Join-Path $smokeRoot 'candidate'
$fixtures = Join-Path $smokeRoot 'fixtures'
$exports = Join-Path $smokeRoot 'ui-exports'
$webViewData = Join-Path $smokeRoot 'webview2'
New-Item -ItemType Directory -Path $candidate | Out-Null
Get-ChildItem -LiteralPath $source -Force |
    Copy-Item -Destination $candidate -Recurse -Force
New-Item -ItemType Directory -Path $exports | Out-Null

# fixture 生成统一走仓库锁定环境（uv run --frozen python），不接入任意
# 系统 Python，也不新增 runtime 依赖。
Push-Location $repoRoot
try {
    uv run --frozen python (Join-Path $repoRoot 'scripts\paddle_smoke_fixtures.py') --out $fixtures
    if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed' }
} finally { Pop-Location }
$manifest = Get-Content -LiteralPath (Join-Path $fixtures 'manifest.json') -Raw | ConvertFrom-Json

$previous = @{}
foreach ($name in @(
    'VIBEOCR_SELF_TEST_SMOKE', 'VIBEOCR_SELF_TEST_INSTANCE',
    'VIBEOCR_PADDLE_SMOKE_HEALTH', 'VIBEOCR_PADDLE_SMOKE_PHASE',
    'VIBEOCR_PADDLE_SMOKE_MODE', 'VIBEOCR_PADDLE_SMOKE_PIPELINE',
    'VIBEOCR_PADDLE_SMOKE_FIXTURE', 'VIBEOCR_PADDLE_SMOKE_OPTION_NAME',
    'VIBEOCR_PADDLE_SMOKE_OPTION_VALUE', 'VIBEOCR_PADDLE_SMOKE_OPTION_KIND',
    'VIBEOCR_PADDLE_SMOKE_TOKENS', 'VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS',
    'VIBEOCR_PADDLE_SMOKE_EXPORT_DIR', 'VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES',
    'VIBEOCR_PADDLE_SMOKE_INSTALL_TIMEOUT_MINUTES', 'WEBVIEW2_USER_DATA_FOLDER'
)) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }

function Invoke-PaddlePhase([hashtable]$extra, [string]$phaseName, [int]$capMinutes) {
    $healthPath = Join-Path $smokeRoot "$phaseName.json"
    $env:VIBEOCR_SELF_TEST_SMOKE = 'paddle-modes-e2e'
    $env:VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
    $env:VIBEOCR_PADDLE_SMOKE_HEALTH = $healthPath
    $env:WEBVIEW2_USER_DATA_FOLDER = $webViewData
    foreach ($key in $extra.Keys) {
        Set-Item -Path ("Env:\" + $key) -Value $extra[$key]
    }
    $executable = Join-Path $candidate 'app\VibeOCR.WinUI.exe'
    $process = Start-Process -FilePath $executable `
        -ArgumentList "--profile production --install-root `"$candidate`"" `
        -WorkingDirectory (Split-Path -Parent $executable) `
        -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit($capMinutes * 60000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) {
                throw "$phaseName process tree did not exit after forced termination"
            }
            throw "$phaseName timed out after $capMinutes minutes（超时前证据见 $smokeRoot 与 health 文件）"
        }
    } finally {
        foreach ($key in $extra.Keys) { Remove-Item -Path ("Env:\" + $key) -ErrorAction SilentlyContinue }
    }
    if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
        throw "$phaseName exited without health evidence (exit $($process.ExitCode))"
    }
    return Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
}

$results = @()
$exitCode = 0
try {
    if (-not $SkipInstall) {
        $install = Invoke-PaddlePhase @{
            'VIBEOCR_PADDLE_SMOKE_PHASE' = 'install'
            'VIBEOCR_PADDLE_SMOKE_INSTALL_TIMEOUT_MINUTES' = "$InstallTimeoutMinutes"
        } 'paddle-install' ([Math]::Max($InstallTimeoutMinutes + 15, 30))
        if ($install.state -ne 'passed') {
            throw "paddleocr-cpu install failed: $($install.error)"
        }
        Write-Host "Install phase OK: environment=$($install.evidence.environment.name) recipe=$($install.evidence.environment.recipe)"
    }

    foreach ($mode in $Modes) {
        $spec = $modeSpec[$mode]
        $runs = @(@{ Fixture = $spec.Fixture; Suffix = '' })
        if ($mode -eq 'paddle_table' -and $IncludeWireless) {
            $runs += @{ Fixture = 'table_wireless.png'; Suffix = '-wireless' }
        }
        foreach ($run in $runs) {
            $phaseName = "paddle-$mode$($run.Suffix)"
            $health = Invoke-PaddlePhase @{
                'VIBEOCR_PADDLE_SMOKE_PHASE' = 'recognize'
                'VIBEOCR_PADDLE_SMOKE_MODE' = $mode
                'VIBEOCR_PADDLE_SMOKE_PIPELINE' = $spec.Pipeline
                'VIBEOCR_PADDLE_SMOKE_FIXTURE' = (Join-Path $fixtures $run.Fixture)
                'VIBEOCR_PADDLE_SMOKE_OPTION_NAME' = $spec.Option
                'VIBEOCR_PADDLE_SMOKE_OPTION_VALUE' = $spec.Value
                'VIBEOCR_PADDLE_SMOKE_OPTION_KIND' = $spec.Kind
                'VIBEOCR_PADDLE_SMOKE_TOKENS' = $spec.Tokens
                'VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS' = $spec.Export
                'VIBEOCR_PADDLE_SMOKE_EXPORT_DIR' = $exports
                'VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES' = "$ModeTimeoutMinutes"
            } $phaseName ($ModeTimeoutMinutes + 10)
            $results += [pscustomobject]@{
                phase = $phaseName; mode = $mode; fixture = $run.Fixture
                state = $health.state; stage = $health.stage
                error = $health.error
            }
            if ($health.state -eq 'passed') {
                Write-Host "PASSED $phaseName (task=$($health.evidence.job.task_id))"
            } elseif ($health.state -eq 'blocked') {
                Write-Host "BLOCKED $phaseName stage=$($health.stage)（提交被环境/目录显式拒绝，证据见 $smokeRoot）"
                $exitCode = 3
            } else {
                Write-Host "FAILED  $phaseName stage=$($health.stage) $($health.error)"
                $exitCode = 1
            }
        }
    }
} finally {
    foreach ($entry in $previous.GetEnumerator()) {
        if ($null -ne $entry.Value) {
            Set-Item -Path ("Env:\" + $entry.Key) -Value $entry.Value
        } else {
            Remove-Item -Path ("Env:\" + $entry.Key) -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ''
Write-Host '==== Paddle 五模式冒烟汇总 ===='
$results | Format-Table phase, mode, state, stage -AutoSize | Out-String | Write-Host
Write-Host "隔离证据保留在: $smokeRoot （候选副本、fixtures、health JSON、UI 导出文件）"
Write-Host 'GPU 状态: UNVERIFIED（本冒烟固定 CPU recipe；GPU 需另行真实槽位验证）'
if ($exitCode -eq 0 -and $results.Count -gt 0) { Write-Host 'All requested modes PASSED.' }
exit $exitCode
