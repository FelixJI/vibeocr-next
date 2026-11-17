<#
.SYNOPSIS
Goal #325/#327 T2 优化前原生 PDF 基线观测驱动（只观测，不优化）。

.DESCRIPTION
单一隔离 candidate 副本复用于所有进程；每种合成 fixture（scan_short /
scan_long / mixed）各 N 次独立 App 进程（process-cold：新进程 + 独立
WebView2 用户数据目录；非 OS/disk cold）。每次运行：
  - 复用 scripts/runtime_demand_process_probe.ps1 以 250ms 采样 App 进程树
    working-set（monitor-info 如实披露 complete_start_trace=false 短进程可
    遗漏；要求正常退出+非空有效采样，不无限阻塞、不声称完整事件追踪）；
  - 复用 paddle smoke 入口（phase=inputs、INPUT_KIND=pdf_baseline），App 内
    完成首图/检测/warm/reopen/跨文档观测并写 health JSON；
  - health/monitor/reports 永不覆盖：已存在即 fail closed。
报告写入 artifacts/t2_baseline/<runid>/（预检查不覆盖），全局索引 append。
进程树峰值按同时刻采样求和（不拿逐进程峰值之和冒充）；探针仅采样
working-set，private bytes 未采样即如实标注，不记 0。
退出码：0 全部 passed；1 任一 failed（优先）；3 仅 blocked。

.EXAMPLE
pwsh -File scripts/t2_pdf_baseline.ps1 -ProductRoot .release-build\publish -WorkRoot D:\goal325-t2-baseline
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
    # 最小自检：不启动 App/monitor，只验证退出码优先级与报告目录隔离逻辑。
    [switch]$SelfCheck
)
$ErrorActionPreference = 'Stop'
if ($TimeoutMinutes -le 0) { throw 'TimeoutMinutes must be positive' }
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repoRoot 'artifacts\t2_baseline'

function Get-T2ExitCode {
    # failed(1) 永远优先；blocked(3) 只在无 failed 时生效。
    param([Parameter(Mandatory)][string[]]$States)
    if ($States -contains 'failed') { return 1 }
    if ($States -contains 'blocked') { return 3 }
    return 0
}

function New-T2RunReportDir {
    param([Parameter(Mandatory)][string]$Base)
    do {
        $runid = "run-{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'),
            ([guid]::NewGuid().ToString('N').Substring(0, 6))
        $dir = Join-Path $Base $runid
    } while (Test-Path -LiteralPath $dir)
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Read-T2MonitorSamples {
    # 至少一个样本须包含 root PID 且 ws>0；全空 procs/缺文件返回 $null（invalid），
    # 不返回 0。峰值口径：同一采样窗口（started_at_ms..at_ms）内求和，非严格同一时刻。
    param(
        [Parameter(Mandatory)][string]$MonitorDir,
        [int]$RootPid = 0
    )
    $samplesFile = Join-Path $MonitorDir 'process-samples.jsonl'
    if (-not (Test-Path -LiteralPath $samplesFile -PathType Leaf)) { return $null }
    $lines = @(Get-Content -LiteralPath $samplesFile | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -eq 0) { return $null }
    $samples = @($lines | ForEach-Object { $_ | ConvertFrom-Json })
    if ($RootPid -le 0) {
        $rootFile = Join-Path $MonitorDir 'root-pid.txt'
        if (Test-Path -LiteralPath $rootFile -PathType Leaf) {
            try { $RootPid = [int](Get-Content -LiteralPath $rootFile -Raw) } catch { $RootPid = 0 }
        }
    }
    $treePeak = 0L; $procPeaks = @{}; $times = @(); $hasRootSample = $false
    foreach ($sample in $samples) {
        $times += [long]$sample.at_ms
        $sum = 0L
        foreach ($proc in @($sample.procs)) {
            $bytes = [long]$proc.ws_bytes
            if ($bytes -le 0) { continue }
            $sum += $bytes
            if (-not $procPeaks.ContainsKey([string]$proc.pid) -or $procPeaks[[string]$proc.pid] -lt $bytes) {
                $procPeaks[[string]$proc.pid] = $bytes
            }
            if ($RootPid -gt 0 -and [int]$proc.pid -eq $RootPid) { $hasRootSample = $true }
        }
        if ($sum -gt $treePeak) { $treePeak = $sum }
    }
    if (-not $hasRootSample -or $treePeak -le 0) { return $null }
    $sorted = $times | Sort-Object
    $gaps = @(for ($i = 1; $i -lt $sorted.Count; $i++) { $sorted[$i] - $sorted[$i - 1] })
    $windows = @($samples | ForEach-Object { [long]$_.at_ms - [long]$_.started_at_ms })
    [pscustomobject]@{
        sample_count = $samples.Count
        root_pid = $RootPid
        tree_ws_peak_bytes = $treePeak
        per_process_ws_peak_bytes = $procPeaks
        sample_gap_ms = if ($gaps.Count -gt 0) {
            @{ min = [long](($gaps | Measure-Object -Minimum).Minimum); max = [long](($gaps | Measure-Object -Maximum).Maximum) }
        } else { $null }
        sample_window_ms = if ($windows.Count -gt 0) {
            @{ min = [long](($windows | Measure-Object -Minimum).Minimum); max = [long](($windows | Measure-Object -Maximum).Maximum) }
        } else { $null }
        private_bytes = 'not_sampled_by_probe (probe records ws_bytes only; recorded as absent, not 0)'
        peak_definition = 'tree peak = max over sampling windows (started_at_ms..at_ms) of window-summed ws; sums share one window, not a strict single instant'
    }
}

if ($SelfCheck) {
    if ((Get-T2ExitCode @('blocked', 'failed', 'passed')) -ne 1) { throw 'SelfCheck: failed must win over blocked' }
    if ((Get-T2ExitCode @('passed', 'blocked')) -ne 3) { throw 'SelfCheck: blocked must map to 3 without failed' }
    if ((Get-T2ExitCode @('passed')) -ne 0) { throw 'SelfCheck: all passed must be 0' }
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $a = New-T2RunReportDir $artifacts
    $b = New-T2RunReportDir $artifacts
    if ($a -eq $b -or (Test-Path -LiteralPath (Join-Path $a 'x.json'))) { throw 'SelfCheck: report dirs must be unique' }
    Set-Content -LiteralPath (Join-Path $a 'x.json') -Value '{}'
    if (-not (Test-Path -LiteralPath (Join-Path $a 'x.json'))) { throw 'SelfCheck: report write failed' }
    $monitorEmpty = Join-Path $a 'monitor-empty'
    New-Item -ItemType Directory -Path $monitorEmpty -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $monitorEmpty 'process-samples.jsonl') `
        -Value '{"started_at_ms":1,"at_ms":2,"procs":[]}'
    if ($null -ne (Read-T2MonitorSamples $monitorEmpty)) {
        throw 'SelfCheck: all-empty procs samples must be invalid (null), never peak 0'
    }
    $monitorValid = Join-Path $a 'monitor-valid'
    New-Item -ItemType Directory -Path $monitorValid -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $monitorValid 'root-pid.txt') -Value '4242'
    $validSamples = @(
        [pscustomobject]@{
            started_at_ms = 10; at_ms = 40
            procs = @(
                [pscustomobject]@{ pid = 4242; ws_bytes = 1000 }
                [pscustomobject]@{ pid = 99; ws_bytes = 500 }
            )
        },
        [pscustomobject]@{
            started_at_ms = 50; at_ms = 80
            procs = @([pscustomobject]@{ pid = 4242; ws_bytes = 1200 })
        }
    )
    Set-Content -LiteralPath (Join-Path $monitorValid 'process-samples.jsonl') `
        -Value ($validSamples | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 4 })
    $aggregate = Read-T2MonitorSamples $monitorValid
    if ($null -eq $aggregate) { throw 'SelfCheck: valid samples must aggregate' }
    if ($aggregate.tree_ws_peak_bytes -ne 1500 -or $aggregate.sample_count -ne 2 -or
        $aggregate.root_pid -ne 4242) {
        throw "SelfCheck: unexpected aggregate $($aggregate | ConvertTo-Json -Compress)"
    }
    $roundTrip = $aggregate | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    if ($roundTrip.per_process_ws_peak_bytes.'4242' -ne 1200) { throw 'SelfCheck: serialized process peak missing' }
    Write-Host "SELFCHECK PASSED exit-priority + isolation + monitor evidence ($a)"
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
$reportDir = New-T2RunReportDir $artifacts
$smokeRoot = Join-Path $work "t2-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
$candidate = Join-Path $smokeRoot 'candidate'
$fixtures = Join-Path $smokeRoot 'fixtures'
New-Item -ItemType Directory -Path $candidate | Out-Null
Get-ChildItem -LiteralPath $source -Force |
    Copy-Item -Destination $candidate -Recurse -Force

Push-Location $repoRoot
try {
    uv run --frozen python (Join-Path $repoRoot 'scripts\paddle_smoke_fixtures.py') `
        --out $fixtures --pdf-baseline *> (Join-Path $reportDir 'fixture-generation.log')
    if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed (exit $LASTEXITCODE; log in report dir)" }
} finally { Pop-Location }
foreach ($kind in $Kinds) {
    if (-not (Test-Path -LiteralPath (Join-Path $fixtures "t2-baseline\$kind.pdf") -PathType Leaf)) {
        throw "Baseline fixture missing: t2-baseline\$kind.pdf"
    }
}

$previous = @{}
foreach ($name in @(
    'VIBEOCR_SELF_TEST_SMOKE', 'VIBEOCR_SELF_TEST_INSTANCE',
    'VIBEOCR_PADDLE_SMOKE_DEVICE', 'VIBEOCR_PADDLE_SMOKE_PRESET',
    'VIBEOCR_PADDLE_SMOKE_HEALTH', 'VIBEOCR_PADDLE_SMOKE_PHASE',
    'VIBEOCR_PADDLE_SMOKE_MODE', 'VIBEOCR_PADDLE_SMOKE_PIPELINE',
    'VIBEOCR_PADDLE_SMOKE_INPUT_KIND',
    'VIBEOCR_PADDLE_SMOKE_PDF_TEXT_LAYER', 'VIBEOCR_PADDLE_SMOKE_PDF_EDITING',
    'VIBEOCR_PADDLE_SMOKE_PDF_WORKSPACE', 'VIBEOCR_PADDLE_SMOKE_FIXTURE',
    'VIBEOCR_PADDLE_SMOKE_SECOND_FIXTURE', 'VIBEOCR_PADDLE_SMOKE_OPTION_NAME',
    'VIBEOCR_PADDLE_SMOKE_OPTION_VALUE', 'VIBEOCR_PADDLE_SMOKE_OPTION_KIND',
    'VIBEOCR_PADDLE_SMOKE_TOKENS', 'VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS',
    'VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES', 'WEBVIEW2_USER_DATA_FOLDER'
)) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }

$results = @()
$states = @()
try {
    foreach ($kind in $Kinds) {
        for ($run = 1; $run -le $Runs; $run++) {
            $phaseName = "t2-$kind-cold-$run"
            $healthPath = Join-Path $smokeRoot "$phaseName.json"
            if (Test-Path -LiteralPath $healthPath -PathType Leaf) {
                throw "$phaseName health already exists; refusing to overwrite historical evidence"
            }
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
                $env:VIBEOCR_PADDLE_SMOKE_INPUT_KIND = 'pdf_baseline'
                $env:VIBEOCR_PADDLE_SMOKE_PDF_TEXT_LAYER = '0'
                $env:VIBEOCR_PADDLE_SMOKE_PDF_EDITING = '0'
                $env:VIBEOCR_PADDLE_SMOKE_PDF_WORKSPACE = '0'
                $env:VIBEOCR_PADDLE_SMOKE_FIXTURE = (Join-Path $fixtures "t2-baseline\$kind.pdf")
                $secondKind = if ($kind -eq 'mixed') { 'scan_short' } else { 'mixed' }
                $env:VIBEOCR_PADDLE_SMOKE_SECOND_FIXTURE = (Join-Path $fixtures "t2-baseline\$secondKind.pdf")
                $env:VIBEOCR_PADDLE_SMOKE_OPTION_NAME = 'use_textline_orientation'
                $env:VIBEOCR_PADDLE_SMOKE_OPTION_VALUE = 'true'
                $env:VIBEOCR_PADDLE_SMOKE_OPTION_KIND = 'bool'
                $env:VIBEOCR_PADDLE_SMOKE_TOKENS = ''
                $env:VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS = ''
                $env:VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES = "$TimeoutMinutes"
                $env:WEBVIEW2_USER_DATA_FOLDER = Join-Path $smokeRoot "webview2\$phaseName"
                $executable = Join-Path $candidate 'app\VibeOCR.WinUI.exe'
                $app = Start-Process -FilePath $executable `
                    -ArgumentList "--profile production --install-root `"$candidate`"" `
                    -WorkingDirectory (Split-Path -Parent $executable) `
                    -WindowStyle Hidden -PassThru
                Set-Content -LiteralPath (Join-Path $monitorDir 'root-pid.txt') -Value $app.Id
                if (-not $app.WaitForExit(($TimeoutMinutes + 20) * 60000)) {
                    $app.Kill($true)
                    if (-not $app.WaitForExit(5000)) {
                        throw "$phaseName app tree did not exit after forced termination"
                    }
                    throw "$phaseName timed out（现场与 health/monitor 证据已保留: $smokeRoot）"
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
                ($null -ne (Read-T2MonitorSamples $monitorDir))
            $monitorEvidenceGap = $null
            if (Test-Path -LiteralPath $monitorInfoPath -PathType Leaf) {
                $info = Get-Content -LiteralPath $monitorInfoPath -Raw | ConvertFrom-Json
                if ($info.complete_start_trace -eq $false) {
                    $monitorEvidenceGap = 'complete_start_trace=false：短进程/启动事件可遗漏；本报告只要求正常退出+非空采样'
                }
            }
            $state = 'failed'
            if ($app.ExitCode -eq 0 -and $health.state -eq 'passed' -and $monitorValid) { $state = 'passed' }
            elseif ($health.state -eq 'blocked') { $state = 'blocked' }
            if ($state -eq 'passed') {
                $receiptDir = Join-Path $smokeRoot 'receipts'
                New-Item -ItemType Directory -Path $receiptDir -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $receiptDir "$phaseName.json") `
                    -Value (ConvertTo-Json ([pscustomobject]@{ phase = $phaseName; exit_code = $app.ExitCode })) -Encoding utf8
            }
            $states += $state
            $record = [pscustomobject]@{
                phase = $phaseName; kind = $kind; run = $run; state = $state
                app_exit_code = $app.ExitCode; app_pid = $app.Id
                monitor_exit_code = $monitor.ExitCode; monitor_valid = $monitorValid
                monitor_evidence_gap = $monitorEvidenceGap
                health_stage = $health.stage; health_state = $health.state
                health_path = $healthPath; monitor_dir = $monitorDir
                process_tree = Read-T2MonitorSamples $monitorDir
                process_cold = 'new app process + fresh per-run WebView2 user data folder; not OS/disk cold'
                candidate_reused = $true
            }
            $results += $record
            Add-Content -LiteralPath (Join-Path $artifacts 'run-index.jsonl') -Value (ConvertTo-Json $record -Depth 8 -Compress)
            $summaryPath = Join-Path $reportDir "$phaseName.json"
            if (Test-Path -LiteralPath $summaryPath) { throw "Refusing to overwrite $summaryPath" }
            Set-Content -LiteralPath $summaryPath -Value (ConvertTo-Json $record -Depth 8) -Encoding utf8
            Write-Host "$($state.ToUpper()) $phaseName stage=$($health.stage) monitor_valid=$monitorValid"
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

$exitCode = Get-T2ExitCode $states
Write-Host ''
if ($results.Count -gt 0) {
    Write-Host "==== T2 原生基线观测汇总：$($results.Count) 次采样（本工具只观测；优化未启动）===="
} else {
    Write-Host '==== T2 原生基线观测：尚未采样（优化未启动）===='
}
$results | Format-Table phase, kind, run, state, app_exit_code, monitor_valid -AutoSize | Out-String | Write-Host
Write-Host "隔离证据（候选/fixtures/health/monitor/receipts）保留在: $smokeRoot"
Write-Host "本次报告目录: $reportDir （全局索引 artifacts\t2_baseline\run-index.jsonl append-only）"
exit $exitCode
