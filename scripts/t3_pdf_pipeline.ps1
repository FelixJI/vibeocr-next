<#
.SYNOPSIS
#328 真实 Windows OCR 串行基线。默认短件、120 页长件和混合件各运行三次。
.DESCRIPTION
先走公开图片识别入口准备模型，每轮启动独立 App/Supervisor，再在同进程重开同一
PDF，分别测冷 worker 和稳态 worker。-Foreground 单独测长任务中的受控翻页。
退出码 0 通过，1 失败，3 环境受阻。所有尝试保留独立证据。
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
    [ValidateRange(1, 180)][int]$TimeoutMinutes = 40,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceSha,
    [switch]$Foreground,
    [switch]$SelfCheck
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts/t3_pipeline'
. (Join-Path $PSScriptRoot 'pdf_smoke_measurements.ps1')

function Get-T3RunPlan([string]$Root, [string[]]$Kinds, [int]$Runs) {
    foreach ($kind in ($Kinds | Select-Object -Unique)) {
        for ($number = 1; $number -le $Runs; $number++) {
            $id = "$kind-$number"
            [pscustomobject]@{
                id = $id; kind = $kind; run = $number
                monitor = Join-Path $Root "monitor/$id"
                health = Join-Path $Root "$id.json"
                fixture = Join-Path $Root "fixtures/t3-pipeline/$kind.pdf"
                fixture_before = Join-Path $Root "fixture-before/$id.pdf"
                export = Join-Path $Root "candidate/state/output/paddle-smoke/$id"
                webview = Join-Path $Root "webview2/$id"
            }
        }
    }
}

function Get-T3AppEnvironment($Plan, [bool]$Prepare) {
    @{
        VIBEOCR_SELF_TEST_SMOKE = 'paddle-modes-e2e'
        VIBEOCR_SELF_TEST_INSTANCE = [guid]::NewGuid().ToString('N')
        VIBEOCR_PADDLE_SMOKE_DEVICE = $Provider
        VIBEOCR_PADDLE_SMOKE_PRESET = $ModelPreset
        VIBEOCR_PADDLE_SMOKE_HEALTH = $Plan.health
        VIBEOCR_PADDLE_SMOKE_FIXTURE = $Plan.fixture
        VIBEOCR_PADDLE_SMOKE_PHASE = 'inputs'
        VIBEOCR_PADDLE_SMOKE_MODE = 'paddle_text'
        VIBEOCR_PADDLE_SMOKE_PIPELINE = 'OCR'
        VIBEOCR_PADDLE_SMOKE_INPUT_KIND = if ($Prepare) { 'file' } else { 'pdf_pipeline' }
        VIBEOCR_PADDLE_SMOKE_OPTION_NAME = 'use_textline_orientation'
        VIBEOCR_PADDLE_SMOKE_OPTION_VALUE = 'true'
        VIBEOCR_PADDLE_SMOKE_OPTION_KIND = 'bool'
        VIBEOCR_PADDLE_SMOKE_TOKENS = if ($Prepare) { 'GOAL103|1120|2244' } else { '' }
        VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS = ''
        VIBEOCR_PADDLE_SMOKE_EXPORT_DIR = $Plan.export
        VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES = "$TimeoutMinutes"
        VIBEOCR_T3_PIPELINE_PREPARE = if ($Prepare) { '1' } else { '0' }
        VIBEOCR_T3_PIPELINE_FOREGROUND = if ($Foreground -and -not $Prepare) { '1' } else { '0' }
        WEBVIEW2_USER_DATA_FOLDER = $Plan.webview
    }
}

function Invoke-T3App($Plan, [bool]$Prepare) {
    if (Test-Path -LiteralPath $Plan.health) { throw "Health exists: $($Plan.health)" }
    foreach ($entry in (Get-T3AppEnvironment $Plan $Prepare).GetEnumerator()) {
        if (-not $previous.ContainsKey($entry.Key)) {
            $previous[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key)
        }
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
    $capMinutes = if ($Prepare) { $TimeoutMinutes + 8 } else { 2 * $TimeoutMinutes + 20 }
    $monitor = $null
    $app = $null
    try {
        if (-not $Prepare) {
            New-Item -ItemType Directory -Path $Plan.monitor | Out-Null
            $monitorArgs = '-NoProfile -File "{0}" -OutputRoot "{1}" -TimeoutSeconds {2} -PipelineDetails' -f
                (Join-Path $PSScriptRoot 'runtime_demand_process_probe.ps1'), $Plan.monitor, (($capMinutes + 2) * 60)
            $monitor = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList $monitorArgs -WindowStyle Hidden -PassThru
            $ready = Join-Path $Plan.monitor 'monitor-ready'
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not (Test-Path -LiteralPath $ready)) {
                if ($monitor.HasExited -or [DateTime]::UtcNow -gt $deadline) {
                    throw 'Process monitor failed to become ready.'
                }
                Start-Sleep -Milliseconds 150
            }
        }

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $app = Start-Process -FilePath $executable -ArgumentList ('--profile production --install-root "{0}"' -f $candidate) `
            -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Hidden -PassThru
        $created = $app.StartTime.ToUniversalTime()
        $creationFileTime = [string]$created.ToFileTimeUtc()
        if ($monitor) {
            Set-Content -LiteralPath (Join-Path $Plan.monitor 'root-creation-filetime.txt') -Value $creationFileTime
            $pendingPid = Join-Path $Plan.monitor 'root-pid.pending'
            Set-Content -LiteralPath $pendingPid -Value $app.Id
            Move-Item -LiteralPath $pendingPid -Destination (Join-Path $Plan.monitor 'root-pid.txt')
        }

        $healthAt = $null
        while (-not $app.WaitForExit(500)) {
            if ($null -eq $healthAt -and (Test-Path -LiteralPath $Plan.health)) { $healthAt = $watch.Elapsed.TotalSeconds }
            if ($watch.Elapsed.TotalMinutes -ge $capMinutes -or
                ($null -ne $healthAt -and $watch.Elapsed.TotalSeconds - $healthAt -ge 60)) {
                throw "App exceeded phase cap or 60s exit grace: $($Plan.id)"
            }
        }
        if ($app.ExitCode -ne 0) { throw "App exit code $($app.ExitCode): $($Plan.id)" }
        [pscustomobject]@{
            pid = $app.Id; creation_filetime = $creationFileTime
            started = [DateTimeOffset]::new($created).ToUnixTimeMilliseconds()
            ended = [DateTimeOffset]::new($app.ExitTime.ToUniversalTime()).ToUnixTimeMilliseconds()
        }
    } finally {
        try {
            if ($app -and -not $app.HasExited) {
                $app.Kill($true)
                if (-not $app.WaitForExit(5000)) { throw 'App tree did not exit after termination.' }
            }
        } finally {
            if ($monitor) {
                Set-Content -LiteralPath (Join-Path $Plan.monitor 'stop-monitor') -Value 'stop'
                if (-not $monitor.WaitForExit(60000)) {
                    $monitor.Kill($true)
                    $null = $monitor.WaitForExit(5000)
                    throw 'Process monitor did not stop.'
                }
                if ($monitor.ExitCode -ne 0) { throw "Process monitor exit code $($monitor.ExitCode)." }
            }
        }
    }
}

function Get-T3WorkerEvidence($Evidence, $App, $Samples, $Starts, $LogLines, [string]$MonitorDir) {
    $passes = @($Evidence.passes)
    if ($passes.Count -ne 2) { throw 'Expected two OCR passes.' }
    $cold = $passes[0].utc_windows.ocr
    $steady = $passes[1].utc_windows.ocr
    if ($Evidence.process_pid -ne $App.pid -or $passes[0].label -ne 'cold' -or
        $passes[1].label -ne 'steady' -or !$passes[0].session -or !$passes[1].session -or
        $passes[0].session -eq $passes[1].session -or $cold.start -lt $App.started -or
        $cold.start -ge $cold.end -or $cold.end -ge $steady.start -or
        $steady.start -ge $steady.end -or $steady.end -gt $App.ended) {
        throw 'Health does not bind two distinct OCR sessions to this App lifecycle.'
    }
    $supervisors = @($Starts | Where-Object {
        $_.class -eq 'supervisor' -and $_.pid -eq $Evidence.supervisor_pid -and $_.ppid -eq $App.pid -and
            $_.started_ms -lt $cold.start
    })
    if ($supervisors.Count -ne 1) { throw 'Supervisor creation was not observed for this App.' }
    $workers = @($Starts | Where-Object { $_.paddle_worker -and $_.ppid -eq $supervisors[0].pid })
    if ($workers.Count -ne 1 -or $workers[0].started_ms -lt $cold.start -or $workers[0].started_ms -gt $cold.end) {
        throw 'Expected one Paddle worker created during first OCR.'
    }
    $worker = $workers[0]
    $phasePeaks = [ordered]@{}
    foreach ($pass in $passes) {
        $ocr = $pass.utc_windows.ocr
        $save = $pass.utc_windows.save
        $open = $pass.utc_windows.open
        $peak = @{ MonitorDir = $MonitorDir; RootPid = $App.pid; RootCreationFileTime = $App.creation_filetime; Samples = $Samples }
        $phasePeaks[$pass.label] = @{
            open = Read-PdfSmokeMonitorSamples @peak -StartMs $open.start -EndMs $open.end
            ocr = Read-PdfSmokeMonitorSamples @peak -StartMs $ocr.start -EndMs $ocr.end
            save = Read-PdfSmokeMonitorSamples @peak -StartMs $save.start -EndMs $save.end
        }
        $witnesses = @($Samples | Where-Object {
            $_.started_at_ms -ge $ocr.start -and $_.at_ms -le $ocr.end -and
                @($_.procs | Where-Object {
                    $_.pid -eq $App.pid -and $_.ws_bytes -gt 0 -and
                        [Math]::Abs([long]$_.creation_filetime - [long]$App.creation_filetime) -lt 10
                }).Count -eq 1 -and @($_.procs | Where-Object {
                $_.pid -eq $worker.pid -and $_.ws_bytes -gt 0 -and
                    [Math]::Abs([long]$_.creation_filetime - [long]$worker.creation_filetime) -lt 10
            }).Count -eq 1
        })
        if ($null -eq $phasePeaks[$pass.label].ocr -or $witnesses.Count -eq 0) {
            throw "Missing App/worker OCR samples for $($pass.label)."
        }
    }
    # 既有 worker 日志仅在核对各实际 ORT session 的首选 Provider 后产生。
    $init = @(foreach ($line in $LogLines) {
        if ($line -match '^\[([^\]]+)\].*Paddle ONNX request=.* sessions=(.*)$') {
            $at = [DateTimeOffset]::Parse($Matches[1]).ToUnixTimeMilliseconds()
            if ($at -ge $App.started -and $at -le $App.ended) {
                $sessions = @([regex]::Matches($Matches[2], "'model': '([^']+)'.*?'providers': \['([^']+)'") |
                    ForEach-Object {
                        @{ model = ($_.Groups[1].Value -split '[\\/]+')[-1]; primary_provider = $_.Groups[2].Value }
                    })
                [pscustomobject]@{ at_ms = $at; sessions = $sessions; line = $line }
            }
        }
    })
    $expected = if ($Provider -eq 'cpu') { 'CPUExecutionProvider' } else { 'DmlExecutionProvider' }
    if ($init.Count -ne 1 -or $init[0].at_ms -lt $cold.start -or $init[0].at_ms -gt $cold.end -or
        $init[0].sessions.Count -eq 0 -or @($init[0].sessions | Where-Object { $_.primary_provider -ne $expected }).Count) {
        throw "Missing proof of cold ORT session initialization and reuse for $expected."
    }
    $actualModels = @($init[0].sessions | ForEach-Object { $_.model } | Sort-Object -Unique)
    foreach ($pass in $passes) {
        if (@($pass.batches).Count -eq 0) { throw "Missing OCR jobs for $($pass.label)." }
        foreach ($batch in $pass.batches) {
            if (Compare-Object $actualModels @($batch.Paddle.Models | Sort-Object -Unique)) {
                throw "Actual model sessions differ from job $($batch.Job.JobId)."
            }
        }
    }
    [pscustomobject]@{ supervisor = $supervisors[0]; worker = $worker; phase_peaks = $phasePeaks; session_init = $init[0] }
}

function Get-T3RetainedStaging($Evidence) {
    # Runtime 的 staging 根带真实 Supervisor instance ID。只对已观测的 job 做事后盘点。
    $temp = Join-Path $candidate 'state/state/recognition/temp'
    if ($Evidence.supervisor -cnotmatch '^sup-[0-9a-f]{10}$' -or -not (Test-Path -LiteralPath $temp)) { return }
    $roots = @(Get-ChildItem -LiteralPath $temp -Directory -Filter "vibeocr-sup-$($Evidence.supervisor)-*")
    if ($roots.Count -ne 1) { return }
    foreach ($pass in $Evidence.passes) {
        foreach ($batch in $pass.batches) {
            $job = $batch.Job.JobId
            if ($job -cnotmatch '^[0-9a-f-]{36}$') { throw "Unexpected job ID: $job" }
            $dir = Join-Path $roots[0].FullName $job
            if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
            $inputs = @(Get-ChildItem -LiteralPath $dir -File)
            $resultDir = Join-Path $dir 'results'
            $assets = @(if (Test-Path -LiteralPath $resultDir -PathType Container) {
                Get-ChildItem -LiteralPath $resultDir -File
            })
            $linkedInputs = foreach ($inputItem in $batch.Inputs) {
                $item = @($batch.Job.Items | Where-Object ClientItemKey -eq $inputItem.ClientItemKey)
                if ($item.Count -ne 1 -or $item[0].ItemId -notmatch '^it-(\d+)$') { continue }
                $prefix = $item[0].ItemId.Substring(3) + '-'
                $file = @($inputs | Where-Object { $_.Name.StartsWith($prefix) -and $_.Length -eq $inputItem.Bytes })
                if ($file.Count -eq 1) {
                    @{ client_item_key = $inputItem.ClientItemKey; item_id = $item[0].ItemId
                        attachment = $inputItem.Attachment; path = $file[0].FullName; bytes = $file[0].Length }
                }
            }
            [pscustomobject]@{
                pass = $pass.label; job_id = $job; directory = $dir
                source = 'job staging directory after App exit'
                input_files = @($inputs | Select-Object Name, Length)
                linked_png_inputs = @($linkedInputs)
                result_assets = @($assets | Select-Object Name, Length)
            }
        }
    }
}

if ($SelfCheck) {
    $report = New-PdfSmokeRunReportDir $artifacts
    $plan = @(Get-T3RunPlan $report @('scan_long') 2)
    New-Item -ItemType Directory -Path (Split-Path -Parent $plan[0].fixture), (Split-Path -Parent $plan[0].fixture_before) | Out-Null
    Set-Content -LiteralPath $plan[0].fixture -Value 'synthetic content'
    foreach ($step in $plan) {
        if (Test-Path -LiteralPath $step.fixture_before) { throw 'Repeated run rewrites fixture copy.' }
        $appEnv = Get-T3AppEnvironment $step $false
        if ($appEnv.VIBEOCR_PADDLE_SMOKE_FIXTURE -ne $step.fixture -or
            $appEnv.VIBEOCR_PADDLE_SMOKE_HEALTH -ne $step.health -or
            $appEnv.VIBEOCR_PADDLE_SMOKE_INPUT_KIND -ne 'pdf_pipeline') {
            throw 'App environment is missing the actual fixture, health or input kind.'
        }
        Copy-Item -LiteralPath $step.fixture -Destination $step.fixture_before
        New-Item -ItemType Directory -Path $step.monitor, $step.export, $step.webview | Out-Null
        New-Item -ItemType File -Path $step.health | Out-Null
    }
    if ($plan.Count -ne 2) { throw 'Expected two independent run plans.' }
    $monitor = $plan[0].monitor
    Set-Content -LiteralPath (Join-Path $monitor 'process-samples.jsonl') -Value '{"started_at_ms":100,"at_ms":105,"procs":[]}'
    if ($null -ne (Read-PdfSmokeMonitorSamples $monitor -RootPid 99 -RootCreationFileTime 1000)) {
        throw 'An empty monitor sample was accepted.'
    }
    Add-Content -LiteralPath (Join-Path $monitor 'process-samples.jsonl') `
        -Value '{"started_at_ms":110,"at_ms":114,"procs":[{"pid":99,"creation_filetime":"1000","ws_bytes":512}]}'
    if ($null -ne (Read-PdfSmokeMonitorSamples $monitor -RootPid 99 -RootCreationFileTime 2000) -or
        $null -ne (Read-PdfSmokeMonitorSamples $monitor -RootPid 99 -RootCreationFileTime 1000 -StartMs 113) -or
        (Read-PdfSmokeMonitorSamples $monitor -RootPid 99 -RootCreationFileTime 1000).tree_ws_peak_bytes -ne 512) {
        throw 'Monitor identity, OCR window or peak binding failed.'
    }
    $fakeBatch = @{ Job = @{ JobId = 'job1' }; Paddle = @{ Models = @('test-model') } }
    $evidence = @{ process_pid = 99; supervisor_pid = 100; passes = @(
        @{ label = 'cold'; session = 'cold-doc'; batches = @($fakeBatch); utc_windows = @{
            open = @{ start = 10; end = 100 }; ocr = @{ start = 100; end = 200 }; save = @{ start = 200; end = 220 }
        } },
        @{ label = 'steady'; session = 'steady-doc'; batches = @($fakeBatch); utc_windows = @{
            open = @{ start = 220; end = 300 }; ocr = @{ start = 300; end = 400 }; save = @{ start = 400; end = 420 }
        } }
    ) }
    $app = @{ pid = 99; creation_filetime = '1000'; started = 1; ended = 500 }
    $starts = @(
        @{ class = 'supervisor'; pid = 100; ppid = 99; started_ms = 60 },
        @{ paddle_worker = $true; pid = 101; ppid = 100; started_ms = 105; creation_filetime = '2000' }
    )
    $procs = @(
        @{ pid = 99; creation_filetime = '1000'; ws_bytes = 512 },
        @{ pid = 101; creation_filetime = '2000'; ws_bytes = 1024 }
    )
    $samples = @(
        @{ started_at_ms = 110; at_ms = 115; procs = $procs },
        @{ started_at_ms = 310; at_ms = 315; procs = $procs }
    )
    $ep = if ($Provider -eq 'cpu') { 'CPUExecutionProvider' } else { 'DmlExecutionProvider' }
    $line = "[1970-01-01T00:00:00.120+00:00] [stderr] Paddle ONNX request={} available=[] sessions=[{'model': 'D:\cache\test-model', 'providers': ['$ep']}]"
    $workerProof = Get-T3WorkerEvidence $evidence $app $samples $starts @($line) $monitor
    if ($workerProof.worker.pid -ne 101 -or $workerProof.phase_peaks.steady.ocr.tree_ws_peak_bytes -ne 1536) {
        throw 'The sampled worker did not bind the cold and steady OCR phases.'
    }
    if ((Get-PdfSmokeExitCode @('failed', 'blocked')) -ne 1 -or (Get-PdfSmokeExitCode @('blocked', 'passed')) -ne 3) {
        throw 'Exit state precedence failed.'
    }
    Write-Host "SELFCHECK PASSED $report"
    exit 0
}

if (-not $ProductRoot -or -not $WorkRoot) { throw 'ProductRoot and WorkRoot required (or -SelfCheck).' }
if ($Foreground) { $Kinds = @('scan_long') }
$source = (Resolve-Path -LiteralPath $ProductRoot).Path.TrimEnd('\')
$work = (Resolve-Path -LiteralPath $WorkRoot).Path.TrimEnd('\')
if ($source.Equals($work, [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $work.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ProductRoot and WorkRoot must not nest.'
}
if (Test-Path -LiteralPath (Join-Path $source 'state')) { throw 'Source candidate already has state.' }
$identity = (Get-Content -LiteralPath (Join-Path $source 'app/metadata/component-identities.json') -Raw | ConvertFrom-Json).project
$runtime = Get-Content -LiteralPath (Join-Path $source 'runtime/backend/runtime-manifest.json') -Raw | ConvertFrom-Json
if ($identity.component -cne 'next' -or $identity.repository -cne 'FelixJI/vibeocr-next' -or
    $identity.source_sha -cnotmatch '^[0-9a-f]{40}$' -or ($ExpectedSourceSha -and $ExpectedSourceSha -cne $identity.source_sha) -or
    $runtime.product.source_sha -cne $identity.source_sha -or $runtime.product.version -cne $identity.version -or
    $runtime.product.repository -cne $identity.repository -or $runtime.product.component -cne $identity.component) {
    throw 'Candidate app and runtime identity must bind the same Next version and source SHA.'
}

$report = New-PdfSmokeRunReportDir $artifacts
$smokeRoot = Join-Path $work "t3-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
$candidate = Join-Path $smokeRoot 'candidate'
$fixtures = Join-Path $smokeRoot 'fixtures'
$executable = Join-Path $candidate 'app/VibeOCR.WinUI.exe'
$supervisorLog = Join-Path $candidate 'state/supervisor.log'
New-Item -ItemType Directory -Path $candidate, (Join-Path $smokeRoot 'fixture-before') | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $candidate -Recurse
Push-Location $repoRoot
try {
    uv run --frozen python scripts/paddle_smoke_fixtures.py --out $fixtures --pdf-pipeline *> (Join-Path $report 'fixture-generation.log')
    if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed: $report" }
} finally { Pop-Location }

$previous = @{}
try {
    $prepare = [pscustomobject]@{
        id = 'prepare-model'; health = Join-Path $smokeRoot 'prepare-model.json'
        fixture = Join-Path $fixtures 'text_zh_en.png'
        export = Join-Path $candidate 'state/output/paddle-smoke/prepare-model'
        webview = Join-Path $smokeRoot 'webview2/prepare-model'
    }
    $prepareRecord = [ordered]@{ plan = $prepare; state = 'failed'; candidate = $identity; provider = $Provider; preset = $ModelPreset }
    try {
        $prepareRecord['process'] = Invoke-T3App $prepare $true
        $health = Get-Content -LiteralPath $prepare.health -Raw | ConvertFrom-Json
        if ($health.state -in @('passed', 'blocked')) { $prepareRecord.state = $health.state }
        $prepareRecord['stage'] = $health.stage
        if ($health.state -eq 'passed') {
            $snapshot = @($health.evidence.job.events | Where-Object Stage -eq 'paddle_onnx_snapshot')
            if ($snapshot.Count -ne 1) { throw 'Model preparation lacks its Paddle execution snapshot.' }
            $detail = $snapshot[0].Detail
            $expected = if ($Provider -eq 'cpu') { 'CPUExecutionProvider' } else { 'DmlExecutionProvider' }
            if ($Provider -eq 'directml' -and $detail.auto_device_fallback -eq $true -and $detail.device -eq 'cpu') {
                $prepareRecord.state = 'blocked'
                $prepareRecord['reason'] = 'Requested DirectML hardware unavailable; preparation fell back to CPU.'
            } elseif ($detail.provider -ne $expected -or $detail.device -ne $Provider -or $detail.model_preset -ne $ModelPreset) {
                throw 'Preparation did not execute with the requested provider, device and preset.'
            }
        }
    } catch {
        $prepareRecord.state = 'failed'
        $prepareRecord['failure'] = $_.Exception.Message
    }
    $prepareRecord | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $report 'prepare-model.json')
    if ($prepareRecord.state -ne 'passed') {
        Write-Host "$($prepareRecord.state.ToUpper()) model/device preparation: $($prepare.health)"
        exit (Get-PdfSmokeExitCode @($prepareRecord.state))
    }

    $states = foreach ($step in (Get-T3RunPlan $smokeRoot $Kinds $Runs)) {
        $record = [ordered]@{
            plan = $step; state = 'failed'; candidate = $identity; provider = $Provider; preset = $ModelPreset
            runtime = @{ python = $runtime.python.version; capabilities = $runtime.capabilities }
            mode = if ($Foreground) { 'controlled_foreground' } else { 'unattended_ocr' }
        }
        try {
            if (Test-Path -LiteralPath $step.fixture_before) { throw "Fixture copy exists: $($step.fixture_before)" }
            Copy-Item -LiteralPath $step.fixture -Destination $step.fixture_before
            $app = Invoke-T3App $step $false
            $record['process'] = $app
            $health = Get-Content -LiteralPath $step.health -Raw | ConvertFrom-Json
            $record['health_stage'] = $health.stage
            $record['health_state'] = $health.state
            if ($health.state -eq 'blocked') { $record.state = 'blocked' }
            elseif ($health.state -eq 'passed') {
                $evidence = $health.evidence
                Push-Location $repoRoot
                try {
                    uv run --frozen python scripts/verify_pdf_pipeline_output.py --health $step.health `
                        --manifest (Join-Path $fixtures 'manifest.json') --fixture $step.fixture `
                        --fixture-copy $step.fixture_before --export-root $step.export *> (Join-Path $report "$($step.id)-verify.log")
                    $record['verify_exit'] = $LASTEXITCODE
                } finally { Pop-Location }
                $samples = @(Get-PdfSmokeMonitorRows $step.monitor 'process-samples.jsonl')
                $starts = @(Get-PdfSmokeMonitorRows $step.monitor 'process-starts.jsonl')
                $record['process_tree'] = Read-PdfSmokeMonitorSamples $step.monitor -RootPid $app.pid `
                    -RootCreationFileTime $app.creation_filetime -StartMs $app.started -EndMs $app.ended -Samples $samples
                if (-not $record.process_tree) { throw 'No process sample binds this App creation identity.' }
                $logLines = @(Select-String -LiteralPath $supervisorLog -SimpleMatch 'Paddle ONNX request=' | ForEach-Object Line)
                $record['residency'] = Get-T3WorkerEvidence $evidence $app $samples $starts $logLines $step.monitor
                $record['retained_staging'] = @(Get-T3RetainedStaging $evidence)
                $record['retained_staging_note'] = 'Files present after App exit, by observed job. Empty means no retained staging observed; transient staging peak and retained in-memory outcomes are unmeasured.'
                $record['performance_eligible'] = @($evidence.passes | Where-Object { $_.performance_eligible -ne $true }).Count -eq 0
                if ($record.verify_exit -eq 0) { $record.state = 'passed' }
            }
        } catch {
            $record['failure'] = $_.Exception.Message
        }
        $record | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $report "$($step.id).json")
        Add-Content -LiteralPath (Join-Path $artifacts 'run-index.jsonl') -Value (ConvertTo-Json $record -Depth 16 -Compress)
        Write-Host "$($record.state.ToUpper()) $($step.id): $($step.health)"
        $record.state
    }
    Write-Host "Evidence: $report (candidate, monitor, PDF files: $smokeRoot)"
    exit (Get-PdfSmokeExitCode @($states))
} finally {
    foreach ($entry in $previous.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
}
