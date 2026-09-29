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

.PARAMETER InputKinds
额外真实输入入口：file/batch 使用表格模式，pdf 使用文档结构模式。
默认不运行；可与 Modes 空数组组合单独执行。

.PARAMETER ResumeRoot
恢复模式：复用本脚本已创建的隔离根继续未完成的模式；与 ProductRoot/
WorkRoot 互斥。已有 health 证据不覆盖（passed 保留跳过，failed/blocked
保留并计入退出码）；恢复前校验候选 markers、fixture manifest 生成器
与既有 paddle 证据。

.EXAMPLE
pwsh -File scripts/smoke_paddle_modes.ps1 -ProductRoot .release-build\publish -WorkRoot D:\goal103-smoke

.EXAMPLE
pwsh -File scripts/smoke_paddle_modes.ps1 -ResumeRoot D:\goal103-smoke\pm-abc123 -Modes paddle_formula
#>
[CmdletBinding()]
param(
    [string]$ProductRoot,
    [string]$WorkRoot,
    # 恢复模式：复用本脚本已创建的隔离根（与 ProductRoot/WorkRoot 互斥）。
    # 已有 health 证据不覆盖：passed 保留跳过，failed/blocked 保留并计入退出码。
    [string]$ResumeRoot,
    [string[]]$Modes = @(
        'paddle_text', 'paddle_table', 'paddle_formula',
        'paddle_structure', 'paddle_document_vl'
    ),
    [ValidateSet("file", "batch", "pdf")]
    [string[]]$InputKinds = @(),
    [int]$ModeTimeoutMinutes = 40,
    [int]$InstallTimeoutMinutes = 60,
    [switch]$IncludeWireless
)

$ErrorActionPreference = 'Stop'
if ($ModeTimeoutMinutes -le 0 -or $InstallTimeoutMinutes -le 0) {
    throw 'All timeout parameters must be positive'
}
if ($ResumeRoot) {
    if ($ProductRoot -or $WorkRoot) {
        throw 'ResumeRoot 与 ProductRoot/WorkRoot 互斥，只能二选一'
    }
} elseif (-not $ProductRoot -or -not $WorkRoot) {
    throw '需要 ProductRoot + WorkRoot（全新隔离）或 ResumeRoot（恢复）'
}
$candidateMarkers = @(
    'app\VibeOCR.WinUI.exe',
    'app\metadata\product-layout.json',
    'runtime\backend\runtime-manifest.json',
    'runtime\installer\vibeocr-runtime-installer.exe'
)
$isResume = [bool]$ResumeRoot
if ($isResume) {
    # 恢复模式：严格校验这是本脚本创建的隔离根 —— 候选 markers、我们
    # 的 fixture manifest（生成器标识）、至少一份 paddle health 证据；
    # 不接受真实用户目录（真实产品目录不会有这些本脚本产物）。
    $smokeRoot = (Resolve-Path -LiteralPath $ResumeRoot).Path.TrimEnd('\')
    $candidate = Join-Path $smokeRoot 'candidate'
    $fixtures = Join-Path $smokeRoot 'fixtures'
    $exports = Join-Path $smokeRoot 'ui-exports'
    $webViewData = Join-Path $smokeRoot 'webview2'
    foreach ($marker in $candidateMarkers) {
        if (-not (Test-Path -LiteralPath (Join-Path $candidate $marker) -PathType Leaf)) {
            throw "ResumeRoot 候选 marker 缺失: $marker"
        }
    }
    $manifestPath = Join-Path $fixtures 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'ResumeRoot 缺少 fixtures\manifest.json（非本脚本创建的隔离根）'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.generator -ne 'scripts/paddle_smoke_fixtures.py') {
        throw "ResumeRoot manifest 生成器不匹配: $($manifest.generator)"
    }
    $knownHealth = Get-ChildItem -LiteralPath $smokeRoot -Filter 'paddle-*.json' -File `
        -ErrorAction SilentlyContinue
    if ($knownHealth.Count -eq 0) {
        throw 'ResumeRoot 内无任何 paddle-*.json 证据（无法证明为本脚本隔离根）'
    }
    New-Item -ItemType Directory -Path $exports -Force | Out-Null
    # 恢复模式不重新生成 fixture（Python 入口只读）；所需文件存在性在
    # modeSpec 定义之后统一校验。
} else {
    $source = (Resolve-Path -LiteralPath $ProductRoot).Path.TrimEnd('\')
    $work = (Resolve-Path -LiteralPath $WorkRoot).Path.TrimEnd('\')
    if ($source.Equals($work, [StringComparison]::OrdinalIgnoreCase) -or
        $source.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $work.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ProductRoot and WorkRoot must not nest'
    }
    foreach ($marker in $candidateMarkers) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $marker) -PathType Leaf)) {
            throw "Candidate marker missing: $marker"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $source 'state')) {
        throw 'Source candidate must not contain user or previous smoke state'
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
}

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
        Fixture = 'table_merged_zh_en.png'; Pipeline = 'TABLE_RECOGNITION'
        Option = 'use_table_orientation_classify'; Value = 'false'; Kind = 'bool'
        Tokens = 'GOAL103|1120'; Export = '导出 Excel|导出 Markdown'
    }
    'paddle_formula' = @{
        Fixture = 'formulas_multi.png'; Pipeline = 'FORMULA_RECOGNITION'
        Option = 'formula_recognition_model_name'; Value = 'PP-FormulaNet_plus-L'; Kind = 'enum'
        Tokens = ''; Export = '导出 Markdown'
    }
    'paddle_structure' = @{
        Fixture = 'document_mixed.png'; Pipeline = 'PP-StructureV3'
        Option = 'use_seal_recognition'; Value = 'true'; Kind = 'bool'
        Tokens = 'GOAL103|1120'; Export = '导出 Markdown|导出 Excel'
    }
    'paddle_document_vl' = @{
        Fixture = 'document_mixed.png'; Pipeline = 'PaddleOCR-VL'
        Option = 'vl_use_chart_recognition'; Value = 'true'; Kind = 'bool'
        Tokens = 'GOAL103|1120'; Export = '导出 Markdown'
    }
}
foreach ($mode in $Modes) {
    if (-not $modeSpec.ContainsKey($mode)) { throw "Unknown smoke mode: $mode" }
}
if ($isResume) {
    foreach ($mode in $Modes) {
        $needed = @($modeSpec[$mode].Fixture)
        if ($mode -eq 'paddle_table' -and $IncludeWireless) {
            $needed += 'table_wireless.png'
        }
        foreach ($fixtureName in $needed) {
            if (-not (Test-Path -LiteralPath (Join-Path $fixtures $fixtureName) -PathType Leaf)) {
                throw "ResumeRoot fixture 缺失: $fixtureName"
            }
        }
    }
}
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

# fixture 生成统一走仓库锁定环境（uv run --frozen python），不接入任意
# 系统 Python，也不新增 runtime 依赖；恢复模式不重新生成，复用并按
# manifest 已验证的既有 fixtures。
if (-not $isResume) {
    Push-Location $repoRoot
    try {
        uv run --frozen python (Join-Path $repoRoot 'scripts\paddle_smoke_fixtures.py') --out $fixtures
        if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed' }
    } finally { Pop-Location }
    $manifest = Get-Content -LiteralPath (Join-Path $fixtures 'manifest.json') -Raw | ConvertFrom-Json
}

$previous = @{}
foreach ($name in @(
    'VIBEOCR_SELF_TEST_SMOKE', 'VIBEOCR_SELF_TEST_INSTANCE',
    'VIBEOCR_PADDLE_SMOKE_HEALTH', 'VIBEOCR_PADDLE_SMOKE_PHASE',
    'VIBEOCR_PADDLE_SMOKE_MODE', 'VIBEOCR_PADDLE_SMOKE_PIPELINE',
    'VIBEOCR_PADDLE_SMOKE_INPUT_KIND', 'VIBEOCR_PADDLE_SMOKE_SECOND_FIXTURE',
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
function Set-PaddleOutcome([string]$state) {
    # 退出码优先级：failed(1) 永远不被后续 blocked(3) 覆盖。
    if ($state -eq 'failed') { $script:exitCode = 1 }
    elseif ($state -eq 'blocked' -and $script:exitCode -eq 0) { $script:exitCode = 3 }
}
try {
    $installHealthPath = Join-Path $smokeRoot 'paddle-install.json'
    $installReused = $false
    if (Test-Path -LiteralPath $installHealthPath -PathType Leaf) {
        # 已有 install 证据不覆盖：passed 直接复用；非 passed 拒绝恢复
        # （重跑需要覆盖 health，与固定输出不可覆盖规则冲突）。
        $kept = Get-Content -LiteralPath $installHealthPath -Raw | ConvertFrom-Json
        if ($kept.state -ne 'passed') {
            throw "paddle-install 既有证据非 passed（$($kept.state)）且不可覆盖，拒绝恢复"
        }
        $installReused = $true
        Write-Host "Install evidence reused (kept): $installHealthPath"
    }
    if (-not $installReused) {
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
            $healthPath = Join-Path $smokeRoot "$phaseName.json"
            if (Test-Path -LiteralPath $healthPath -PathType Leaf) {
                # 已有证据不覆盖：passed 保留跳过；failed/blocked 保留并计入退出码。
                $kept = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
                $results += [pscustomobject]@{
                    phase = $phaseName; mode = $mode; fixture = $run.Fixture
                    state = "kept:$($kept.state)"; stage = $kept.stage
                    error = $kept.error
                }
                Write-Host "KEPT    $phaseName state=$($kept.state)（证据已保留，不重跑不覆盖）"
                Set-PaddleOutcome $kept.state
                continue
            }
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
                'VIBEOCR_PADDLE_SMOKE_EXPORT_DIR' = (Join-Path $exports $phaseName)
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
                Set-PaddleOutcome 'blocked'
            } else {
                Write-Host "FAILED  $phaseName stage=$($health.stage) $($health.error)"
                Set-PaddleOutcome 'failed'
            }
        }
    }
    foreach ($inputKind in $InputKinds) {
        $inputMode = if ($inputKind -eq 'pdf') { 'paddle_structure' } else { 'paddle_table' }
        $spec = $modeSpec[$inputMode]
        $fixtureName = if ($inputKind -eq 'pdf') { 'document_mixed.pdf' } else { $spec.Fixture }
        $phaseName = "paddle-input-$inputKind"
        $healthPath = Join-Path $smokeRoot "$phaseName.json"
        if (Test-Path -LiteralPath $healthPath -PathType Leaf) {
            $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
            Write-Host "KEPT $phaseName state=$($health.state)"
        } else {
            foreach ($needed in @($fixtureName, 'table_wireless.png')) {
                if (-not (Test-Path -LiteralPath (Join-Path $fixtures $needed) -PathType Leaf)) {
                    throw "Input fixture missing: $needed"
                }
            }
            $inputExports = Join-Path $exports "input-$inputKind"
            New-Item -ItemType Directory -Path $inputExports -Force | Out-Null
            $health = Invoke-PaddlePhase @{
                'VIBEOCR_PADDLE_SMOKE_PHASE' = 'inputs'
                'VIBEOCR_PADDLE_SMOKE_INPUT_KIND' = $inputKind
                'VIBEOCR_PADDLE_SMOKE_MODE' = $inputMode
                'VIBEOCR_PADDLE_SMOKE_PIPELINE' = $spec.Pipeline
                'VIBEOCR_PADDLE_SMOKE_FIXTURE' = (Join-Path $fixtures $fixtureName)
                'VIBEOCR_PADDLE_SMOKE_SECOND_FIXTURE' = (Join-Path $fixtures 'table_wireless.png')
                'VIBEOCR_PADDLE_SMOKE_OPTION_NAME' = $spec.Option
                'VIBEOCR_PADDLE_SMOKE_OPTION_VALUE' = $spec.Value
                'VIBEOCR_PADDLE_SMOKE_OPTION_KIND' = $spec.Kind
                'VIBEOCR_PADDLE_SMOKE_TOKENS' = $spec.Tokens
                'VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS' = $spec.Export
                'VIBEOCR_PADDLE_SMOKE_EXPORT_DIR' = $inputExports
                'VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES' = "$ModeTimeoutMinutes"
            } $phaseName ($ModeTimeoutMinutes + 10)
        }
        $results += [pscustomobject]@{
            phase = $phaseName; mode = $inputMode; fixture = $fixtureName
            state = $health.state; stage = $health.stage; error = $health.error
        }
        Write-Host "$($health.state) $phaseName stage=$($health.stage) $($health.error)"
        Set-PaddleOutcome $health.state
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
