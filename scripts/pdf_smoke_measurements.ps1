function Get-PdfSmokeExitCode {
    # failed(1) 永远优先；blocked(3) 只在无 failed 时生效。
    param([Parameter(Mandatory)][string[]]$States)
    if ($States -contains 'failed') { return 1 }
    if ($States -contains 'blocked') { return 3 }
    return 0
}

function New-PdfSmokeRunReportDir {
    param([Parameter(Mandatory)][string]$Base)
    do {
        $runid = "run-{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'),
            ([guid]::NewGuid().ToString('N').Substring(0, 6))
        $dir = Join-Path $Base $runid
    } while (Test-Path -LiteralPath $dir)
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Read-PdfSmokeMonitorSamples {
    # 至少一个样本须包含 root PID 且 ws>0；全空 procs/缺文件返回 $null（invalid），
    # 不返回 0。峰值口径：同一采样窗口（started_at_ms..at_ms）内求和，非严格同一时刻。
    param(
        [Parameter(Mandatory)][string]$MonitorDir,
        [int]$RootPid = 0,
        [string]$RootCreationFileTime,
        [long]$StartMs = 0,
        [long]$EndMs = [long]::MaxValue,
        [object[]]$Samples
    )
    if ($null -eq $Samples) { $Samples = @(Get-PdfSmokeMonitorRows $MonitorDir 'process-samples.jsonl') }
    $samples = @($Samples | Where-Object { $_.started_at_ms -ge $StartMs -and $_.at_ms -le $EndMs })
    if ($RootCreationFileTime) {
        $samples = @($samples | Where-Object {
            $sample = $_
            $sample.at_ms -ge $sample.started_at_ms -and @($sample.procs | Where-Object {
                $_.pid -eq $RootPid -and $_.ws_bytes -gt 0 -and
                    [Math]::Abs([long]$_.creation_filetime - [long]$RootCreationFileTime) -lt 10
            }).Count -eq 1
        })
    }
    if ($samples.Count -eq 0) { return $null }
    if ($RootPid -le 0) {
        $rootFile = Join-Path $MonitorDir 'root-pid.txt'
        if (Test-Path -LiteralPath $rootFile -PathType Leaf) {
            try { $RootPid = [int](Get-Content -LiteralPath $rootFile -Raw) } catch { $RootPid = 0 }
        }
    }
    $treePrivatePeak = $null; $cpu = @{}
    $treePeak = 0L; $procPeaks = @{}; $times = @(); $hasRootSample = $false
    foreach ($sample in $samples) {
        $times += [long]$sample.at_ms
        $sum = 0L; $privateSum = 0L; $privateCount = 0
        foreach ($proc in @($sample.procs)) {
            $bytes = [long]$proc.ws_bytes
            if ($bytes -le 0) { continue }
            $sum += $bytes
            if ($null -ne $proc.private_bytes) { $privateSum += [long]$proc.private_bytes; $privateCount++ }
            if ($null -ne $proc.cpu_ms) {
                $id = "$($proc.pid):$($proc.creation_filetime)"
                if (-not $cpu.ContainsKey($id)) { $cpu[$id] = @{ first_ms = $proc.cpu_ms; last_ms = $proc.cpu_ms } }
                else { $cpu[$id].last_ms = $proc.cpu_ms }
            }
            if (-not $procPeaks.ContainsKey([string]$proc.pid) -or $procPeaks[[string]$proc.pid] -lt $bytes) {
                $procPeaks[[string]$proc.pid] = $bytes
            }
            if ($RootPid -gt 0 -and [int]$proc.pid -eq $RootPid) { $hasRootSample = $true }
        }
        if ($privateCount -gt 0 -and $privateCount -eq $sample.procs.Count -and
            ($null -eq $treePrivatePeak -or $privateSum -gt $treePrivatePeak)) { $treePrivatePeak = $privateSum }
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
        private_bytes = if ($null -ne $treePrivatePeak) { $treePrivatePeak }
            else { 'not_sampled_by_probe (probe records ws_bytes only; recorded as absent, not 0)' }
        process_cpu_time_counters_ms = if ($cpu.Count -gt 0) { $cpu } else { $null }
        peak_definition = 'tree peak = max over sampling windows (started_at_ms..at_ms) of window-summed ws; sums share one window, not a strict single instant'
    }
}

function Get-PdfSmokeMonitorRows([string]$MonitorDir, [string]$Name) {
    $path = Join-Path $MonitorDir $Name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Get-Content -LiteralPath $path | Where-Object { $_.Trim().Length -gt 0 } |
            ForEach-Object { $_ | ConvertFrom-Json }
    }
}
