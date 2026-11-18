<#
.SYNOPSIS
Goal #325/#328 T3 串行阶段测量基线（真实产品 OCR 管线）。

.DESCRIPTION
单一隔离 candidate 副本复用于所有进程；每种合成 fixture（t3-pipeline 的
scan_short / scan_long / mixed）各 N 次独立 App 进程（process-cold：新进程
+ 独立 WebView2 用户数据目录；非 OS/disk cold）。每次运行：
  - 复用 scripts/runtime_demand_process_probe.ps1 以 250ms 采样 App 进程树
    working-set（仅 WS；private bytes/CPU 未采样即如实标注，不冒充设备等待）；
  - 复用 paddle smoke 入口（phase=inputs、INPUT_KIND=pdf_pipeline），App 内
    通过公开产品操作（打开 PDF→选择引擎/页面→添加选中页文字层→另存为）
    完成两遍测量：第一遍保留冷 worker 成本，关闭重开同一 fixture 后第二遍
    为稳态；每遍记录 presence/render/submit/observe/write/save 阶段、真实
    传输 bytes 与 PNG IHDR 像素、job 归属证明、前台帧尾与宿主 working set；
  - 运行前保存 fixture 字节副本；health passed 后由
    scripts/verify_pdf_pipeline_output.py 校验保存 PDF 的可搜索文字、页序
    标记、原生层保留、扫描栅格与源 fixture 未被写入；
  - health/monitor/reports 永不覆盖：已存在即 fail closed。
报告写入 artifacts/t3_pipeline/<runid>/（预检查不覆盖），全局索引 append。
退出码：0 全部 passed；1 任一 failed（优先）；3 仅 blocked。

.EXAMPLE
pwsh -File scripts/t3_pdf_pipeline.ps1 -ProductRoot .release-build\publish -WorkRoot D:\goal325-t3-pipeline
#>
[CmdletBinding()]
param(
    [string]$ProductRoot,
    [string]$WorkRoot,
    [ValidateSet('scan_short', 'scan_long', 'mixed')]
    [string[]]$Kinds = @('scan_short', 'scan_long', 'mixed'),
    [ValidateRange(1, 9)][int]$Runs = 3,
    [ValidateSet('cpu', 'directml')][string]$Provider = 'cpu',
    [ValidateSet('tiny', 'small', 'medium')][string]$ModelPreset = 'small',
    [int]$TimeoutMinutes = 40,
    # 页面选择（App 内 VIBEOCR_T3_PIPELINE_PAGES）：all / first:N / pages:a,b,c。
    # 默认按 kind：scan_short=all(6)，scan_long=first:8（2 批，跨批共享资源），
    # mixed=first:10（仅扫描页；原生/空白页不参与文字层添加）。
    [string]$Pages = '',
    # 最小自检：不启动 App/monitor，只验证退出码优先级、报告目录隔离与
    # verifier 参数校验。
    [switch]$SelfCheck
)
$ErrorActionPreference = 'Stop'
if ($TimeoutMinutes -le 0) { throw 'TimeoutMinutes must be positive' }
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repoRoot 'artifacts\t3_pipeline'

function Get-T3ExitCode {
    param([Parameter(Mandatory)][string[]]$States)
    if ($States -contains 'failed') { return 1 }
    if ($States -contains 'blocked') { return 3 }
    return 0
}

function New-T3RunReportDir {
    param([Parameter(Mandatory)][string]$Base)
    do {
        $runid = "run-{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'),
            ([guid]::NewGuid().ToString('N').Substring(0, 6))
        $dir = Join-Path $Base $runid
    } while (Test-Path -LiteralPath $dir)
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Get-T3DefaultPages {
    param([Parameter(Mandatory)][string]$Kind)
    switch ($Kind) {
        'scan_short' { 'all' }
        'scan_long' { 'first:8' }
        'mixed' { 'first:10' }
        default { throw "Unknown kind $Kind" }
    }
}

if ($SelfCheck) {
    if ((Get-T3ExitCode @('blocked', 'failed', 'passed')) -ne 1) { throw 'SelfCheck: failed must win over blocked' }
    if ((Get-T3ExitCode @('passed', 'blocked')) -ne 3) { throw 'SelfCheck: blocked must map to 3 without failed' }
    if ((Get-T3ExitCode @('passed')) -ne 0) { throw 'SelfCheck: all passed must be 0' }
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $a = New-T3RunReportDir $artifacts
    $b = New-T3RunReportDir $artifacts
    if ($a -eq $b -or (Test-Path -LiteralPath (Join-Path $b 'x.json'))) { throw 'SelfCheck: report dirs must be unique' }
    Set-Content -LiteralPath (Join-Path $a 'x.json') -Value '{}'
    if (-not (Test-Path -LiteralPath (Join-Path $a 'x.json'))) { throw 'SelfCheck: report write failed' }
    # verifier 必须拒绝缺参调用（fail-closed 而非空通过）。
    Push-Location $repoRoot
    try {
        uv run --frozen python scripts/verify_pdf_pipeline_output.py *> $null
        if ($LASTEXITCODE -eq 0) { throw 'SelfCheck: verifier must fail without required args' }
    } finally { Pop-Location }
    Write-Host "SELFCHECK PASSED exit-priority + isolation + verifier fail-closed ($a)"
    exit 0
}

if (-not $ProductRoot -or -not $WorkRoot) { throw 'ProductRoot + WorkRoot required (or -SelfCheck)' }
$source = (Resolve-Path -LiteralPath $ProductRoot).Path.TrimEnd('\')
$work = (Resolve-Path -LiteralPath $WorkRoot).Path.TrimEnd('\')
if ($source.Equals($work, [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $work.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ProductRoot and WorkRoot must not nest'
}
foreach ($marker in @(
    'app\VibeOCR.WinUI.exe', 'app\metadata\product-layout.json',
    'runtime\backend\runtime-manifest.json', 'runtime\env\runtime-env\python.exe'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $marker) -PathType Leaf)) {
        throw "Candidate marker missing: $marker"
    }
}
if (Test-Path -LiteralPath (Join-Path $source 'state')) {
    throw 'Source candidate must not contain user or previous smoke state'
}
$reportDir = New-T3RunReportDir $artifacts
$smokeRoot = Join-Path $work "t3-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
$candidate = Join-Path $smokeRoot 'candidate'
$fixtures = Join-Path $smokeRoot 'fixtures'
New-Item -ItemType Directory -Path $candidate | Out-Null
Get-ChildItem -LiteralPath $source -Force |
    Copy-Item -Destination $candidate -Recurse -Force

Push-Location $repoRoot
try {
    uv run --frozen python (Join-Path $repoRoot 'scripts\paddle_smoke_fixtures.py') `
        --out $fixtures --pdf-pipeline *> (Join-Path $reportDir 'fixture-generation.log')
    if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed (exit $LASTEXITCODE; log in report dir)" }
} finally { Pop-Location }
foreach ($kind in $Kinds) {
    if (-not (Test-Path -LiteralPath (Join-Path $fixtures "t3-pipeline\$kind.pdf") -PathType Leaf)) {
        throw "Pipeline fixture missing: t3-pipeline\$kind.pdf"
    }
}

$previous = @{}
foreach ($name in @(
    'VIBEOCR_SELF_TEST_SMOKE', 'VIBEOCR_SELF_TEST_INSTANCE',
    'VIBEOCR_PADDLE_SMOKE_DEVICE', 'VIBEOCR_PADDLE_SMOKE_PRESET',
    'VIBEOCR_PADDLE_SMOKE_HEALTH', 'VIBEOCR_PADDLE_SMOKE_PHASE',
    'VIBEOCR_PADDLE_SMOKE_MODE', 'VIBEOCR_PADDLE_SMOKE_PIPELINE',
    'VIBEOCR_PADDLE_SMOKE_INPUT_KIND', 'VIBEOCR_PADDLE_SMOKE_OPTION_NAME',
    'VIBEOCR_PADDLE_SMOKE_OPTION_VALUE', 'VIBEOCR_PADDLE_SMOKE_OPTION_KIND',
    'VIBEOCR_PADDLE_SMOKE_TOKENS', 'VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS',
    'VIBEOCR_PADDLE_SMOKE_EXPORT_DIR', 'VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES',
    'WEBVIEW2_USER_DATA_FOLDER', 'VIBEOCR_T3_PIPELINE_PAGES'
)) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }

$results = @()
$states = @()
try {
    foreach ($kind in $Kinds) {
        for ($run = 1; $run -le $Runs; $run++) {
            $phaseName = "t3-$kind-cold-$run"
            $healthPath = Join-Path $smokeRoot "$phaseName.json"
            if (Test-Path -LiteralPath $healthPath -PathType Leaf) {
                throw "$phaseName health already exists; refusing to overwrite historical evidence"
            }
            $fixture = Join-Path $fixtures "t3-pipeline\$kind.pdf"
            $fixtureCopy = "$fixture.pre"
            if (Test-Path -LiteralPath $fixtureCopy -PathType Leaf) {
                throw "fixture pre-run copy already exists: $fixtureCopy"
            }
            Copy-Item -LiteralPath $fixture -Destination $fixtureCopy
            $monitorDir = Join-Path $smokeRoot "monitor\$phaseName"
            New-Item -ItemType Directory -Path $monitorDir -Force | Out-Null
            $monitorArgs = '-NoProfile -File "{0}" -OutputRoot "{1}" -TimeoutSeconds {2}' -f `
                (Join-Path $repoRoot 'scripts\runtime_demand_process_probe.ps1'),
                $monitorDir, (($TimeoutMinutes + 20) * 60)
            $monitor = Start-Process -FilePath (Get-Command pwsh).Source `
                -ArgumentList $monitorArgs -WindowStyle Hidden -PassThru
            try {
                $ready = Join-Path $monitorDir 'monitor-ready'
                $deadline = [DateTime]::UtcNow.AddSeconds(30)
                while (-not (Test-Path -LiteralPath $ready)) {
                    if ($monitor.HasExited) { throw "Process monitor exited early: $($monitor.ExitCode)" }
                    if ([DateTime]::UtcNow -gt $deadline) { throw 'Process monitor not ready in 30s' }
                    Start-Sleep -Milliseconds 200
                }
                $env:VIBEOCR_SELF_TEST_SMOKE = 'paddle-modes-e2e'
                $env:VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
                $env:VIBEOCR_PADDLE_SMOKE_HEALTH = $healthPath
                $env:VIBEOCR_PADDLE_SMOKE_DEVICE = $Provider
                $env:VIBEOCR_PADDLE_SMOKE_PRESET = $ModelPreset
                $env:VIBEOCR_PADDLE_SMOKE_PHASE = 'inputs'
                $env:VIBEOCR_PADDLE_SMOKE_MODE = 'paddle_text'
                $env:VIBEOCR_PADDLE_SMOKE_PIPELINE = 'OCR'
                $env:VIBEOCR_PADDLE_SMOKE_INPUT_KIND = 'pdf_pipeline'
                $env:VIBEOCR_T3_PIPELINE_PAGES =
                    if ($Pages) { $Pages } else { Get-T3DefaultPages $kind }
                $env:VIBEOCR_PADDLE_SMOKE_OPTION_NAME = 'use_textline_orientation'
                $env:VIBEOCR_PADDLE_SMOKE_OPTION_VALUE = 'true'
                $env:VIBEOCR_PADDLE_SMOKE_OPTION_KIND = 'bool'
                $env:VIBEOCR_PADDLE_SMOKE_TOKENS = ''
                $env:VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS = ''
                $env:VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES = "$TimeoutMinutes"
                $env:VIBEOCR_PADDLE_SMOKE_EXPORT_DIR =
                    Join-Path $candidate "state\output\paddle-smoke\pipeline-$phaseName"
                $env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $smokeRoot "webview2\$phaseName"
                $executable = Join-Path $candidate 'app\VibeOCR.WinUI.exe'
                $app = Start-Process -FilePath $executable `
                    -ArgumentList "--profile production --install-root `"$candidate`"" `
                    -WorkingDirectory (Split-Path -Parent $executable) `
                    -WindowStyle Hidden -PassThru
                Set-Content -LiteralPath (Join-Path $monitorDir 'root-pid.txt') -Value $app.Id
                $overallCapMs = ($TimeoutMinutes + 20) * 60000
                $exitGraceMs = 60000
                $exitFailure = $null
                $healthSeen = $null
                $watch = [System.Diagnostics.Stopwatch]::StartNew()
                while (-not $app.WaitForExit(500)) {
                    if ($null -eq $healthSeen -and (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
                        $healthSeen = [DateTime]::UtcNow
                    }
                    if ($null -ne $healthSeen -and
                        ([DateTime]::UtcNow - $healthSeen).TotalMilliseconds -ge $exitGraceMs) {
                        $exitFailure = "app pid $($app.Id) did not exit within 60s grace after health file appeared"
                        $app.Kill($true)
                        if (-not $app.WaitForExit(5000)) {
                            throw "$phaseName app tree did not exit after forced termination"
                        }
                        break
                    }
                    if ($watch.ElapsedMilliseconds -ge $overallCapMs) {
                        $app.Kill($true)
                        if (-not $app.WaitForExit(5000)) {
                            throw "$phaseName app tree did not exit after forced termination"
                        }
                        throw "$phaseName timed out（现场与 health/monitor 证据已保留: $smokeRoot）"
                    }
                }
            } finally {
                Set-Content -LiteralPath (Join-Path $monitorDir 'stop-monitor') -Value 'stop' -ErrorAction SilentlyContinue
                if (-not $monitor.WaitForExit(60000)) { $monitor.Kill($true) }
            }
            if (-not (Test-Path -LiteralPath $healthPath -PathType Leaf)) {
                throw "$phaseName exited without health evidence (exit $($app.ExitCode))"
            }
            $health = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
            $monitorInfoPath = Join-Path $monitorDir 'monitor-info.json'
            $monitorValid = ($monitor.ExitCode -eq 0) -and
                (Test-Path -LiteralPath $monitorInfoPath -PathType Leaf) -and
                (Test-Path -LiteralPath (Join-Path $monitorDir 'process-samples.jsonl') -PathType Leaf)
            $state = 'failed'
            $verifyExit = $null
            if ($null -eq $exitFailure -and $app.ExitCode -eq 0 -and
                $health.state -eq 'passed' -and $monitorValid) {
                # 真实输出最终校验：可搜索文字/页序/原生层/栅格/源未写。
                Push-Location $repoRoot
                try {
                    uv run --frozen python scripts/verify_pdf_pipeline_output.py `
                        --health $healthPath --manifest (Join-Path $fixtures 'manifest.json') `
                        --fixture $fixture --fixture-copy $fixtureCopy `
                        *> (Join-Path $reportDir "$phaseName-verify.log")
                    $verifyExit = $LASTEXITCODE
                } finally { Pop-Location }
                if ($verifyExit -eq 0) { $state = 'passed' }
            } elseif ($null -eq $exitFailure -and $health.state -eq 'blocked') {
                $state = 'blocked'
            }
            if ($state -eq 'passed') {
                $receiptDir = Join-Path $smokeRoot 'receipts'
                New-Item -ItemType Directory -Path $receiptDir -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $receiptDir "$phaseName.json") `
                    -Value (ConvertTo-Json ([pscustomobject]@{
                        phase = $phaseName; exit_code = $app.ExitCode; verify_exit = $verifyExit
                    })) -Encoding utf8
            }
            # 事后磁盘留存（粗粒度边界披露：非峰值、逐成分不拆分）。
            $stateBytes = $null
            if (Test-Path -LiteralPath (Join-Path $candidate 'state')) {
                $stateBytes = (Get-ChildItem -LiteralPath (Join-Path $candidate 'state') -Recurse -File |
                    Measure-Object -Property Length -Sum).Sum
            }
            $states += $state
            $record = [pscustomobject]@{
                phase = $phaseName; kind = $kind; run = $run; state = $state
                app_exit_code = $app.ExitCode; app_pid = $app.Id
                exit_failure = $exitFailure
                pages_spec = $env:VIBEOCR_T3_PIPELINE_PAGES
                verify_exit = $verifyExit
                monitor_exit_code = $monitor.ExitCode; monitor_valid = $monitorValid
                health_stage = $health.stage; health_state = $health.state
                health_path = $healthPath; monitor_dir = $monitorDir
                fixture = $fixture; fixture_pre_copy = $fixtureCopy
                candidate_state_bytes_post_run = $stateBytes
                candidate_state_note = 'post-run on-disk bytes under candidate state tree; NOT a peak measure; per-component staging/terminal retention NOT_MEASURED'
                process_cold = 'new app process + fresh per-run WebView2 user data folder; not OS/disk cold'
                candidate_reused = $true
                provider = $Provider; model_preset = $ModelPreset
            }
            $results += $record
            Add-Content -LiteralPath (Join-Path $artifacts 'run-index.jsonl') -Value (ConvertTo-Json $record -Depth 8 -Compress)
            $summaryPath = Join-Path $reportDir "$phaseName.json"
            if (Test-Path -LiteralPath $summaryPath) { throw "Refusing to overwrite $summaryPath" }
            Set-Content -LiteralPath $summaryPath -Value (ConvertTo-Json $record -Depth 8) -Encoding utf8
            Write-Host "$($state.ToUpper()) $phaseName stage=$($health.stage) monitor_valid=$monitorValid verify_exit=$verifyExit"
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

$exitCode = Get-T3ExitCode $states
Write-Host ''
if ($results.Count -gt 0) {
    Write-Host "==== T3 串行阶段基线观测汇总：$($results.Count) 次采样===="
} else {
    Write-Host '==== T3 串行阶段基线观测：尚未采样===='
}
$results | Format-Table phase, kind, run, state, app_exit_code, verify_exit, monitor_valid -AutoSize | Out-String | Write-Host
Write-Host "隔离证据（候选/fixtures/health/monitor/receipts）保留在: $smokeRoot"
Write-Host "本次报告目录: $reportDir （全局索引 artifacts\t3_pipeline\run-index.jsonl append-only）"
exit $exitCode
