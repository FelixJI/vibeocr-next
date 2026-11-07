# Read-only process evidence for the explicitly spawned Task #214 candidate.
# Only owned lineage is persisted; full argv, tokens and request payloads are not.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [int]$TimeoutSeconds = 2400,
    [switch]$SelfCheck
)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
public static class DemandProbeArgv {
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
    public static string[] Split(string command) {
        int count; var pointer = CommandLineToArgvW(command, out count);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("CommandLineToArgvW failed");
        try {
            var args = new string[count];
            for (int i=0; i<count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i*IntPtr.Size));
            return args;
        } finally { LocalFree(pointer); }
    }
}
// CIM discovery can exceed one second on a busy host. Sample only its verified
// ownership records independently; every value is read inside the recorded interval.
public sealed class DemandWorkingSetSampler : IDisposable {
    readonly ConcurrentDictionary<int, (long Created, string Name, string Role)> owned = new();
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    readonly object receiptGate = new();
    (int Pid, long Created, string Path)? pendingReceipt;
    public DemandWorkingSetSampler(string path) {
        worker = Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var live = new List<object>();
                // Identities actually verified inside THIS sample (per-tick live
                // StartTime check on the microsecond grid); the owned table can be
                // re-registered later, so receipts must match these tuples.
                var verified = new List<(int Pid, long Created)>();
                foreach (var item in owned) {
                    try {
                        using var process = Process.GetProcessById(item.Key);
                        // CIM CreationDate has microsecond precision; native FILETIME has 100 ns ticks.
                        if (process.StartTime.ToUniversalTime().ToFileTimeUtc() / 10 != item.Value.Created / 10) continue;
                        process.Refresh();
                        long bytes = process.WorkingSet64;
                        if (process.HasExited) continue;
                        live.Add(new { pid = item.Key, name = item.Value.Name,
                            ws_bytes = bytes, webview_role = item.Value.Role });
                        verified.Add((item.Key, item.Value.Created));
                    } catch (ArgumentException) { // Process exited before opening.
                    } catch (InvalidOperationException) { // Process exited during the read.
                    } catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 87) {
                        // The process vanished between enumeration and opening.
                    }
                }
                long completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                File.AppendAllText(path, JsonSerializer.Serialize(new {
                    started_at_ms = started, at_ms = completed, procs = live }) + "\n");
                (int Pid, long Created, string Path)? pending;
                lock (receiptGate) pending = pendingReceipt;
                // Root first-sample receipt (capture-first-frame handshake): written
                // ATOMICALLY only after a COMPLETE sample whose actually-verified
                // (pid, creation) tuple matches the request on the established
                // microsecond grid has been flushed to disk; the receipt attests the
                // sample-side verified value. A pid-only match, a creation on another
                // grid cell, or a never-sampled identity never receives a receipt.
                if (pending is { } request) {
                    int confirmed = verified.FindIndex(identity =>
                        identity.Pid == request.Pid && identity.Created / 10 == request.Created / 10);
                    if (confirmed >= 0) {
                        lock (receiptGate) pendingReceipt = null;
                        var identity = verified[confirmed];
                        string temp = request.Path + ".tmp";
                        File.WriteAllText(temp, JsonSerializer.Serialize(new {
                            pid = identity.Pid, creation_filetime = identity.Created.ToString(),
                            sample_started_at_ms = started, sample_at_ms = completed,
                            issued_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
                        File.Move(temp, request.Path, true);
                    }
                }
                await Task.Delay(250, stop.Token);
            }
        });
    }
    public void Observe(int pid, long created, string name, string role) =>
        owned[pid] = (created, name, role);
    public void IssueReceiptWhenRootSampled(int pid, long created, string receiptPath) {
        lock (receiptGate) pendingReceipt = (pid, created, receiptPath);
    }
    public void Dispose() {
        stop.Cancel();
        try { worker.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { stop.Dispose(); }
    }
}
'@
if ($SelfCheck) {
    $parsed = [DemandProbeArgv]::Split('installer.exe --request-json "{\"action\":\"initialize_default\"}"')
    if ($parsed.Count -ne 3 -or ($parsed[2] | ConvertFrom-Json).action -ne 'initialize_default') {
        throw 'Windows argv JSON parsing failed.'
    }
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    $samplePath = Join-Path $OutputRoot 'selfcheck-samples.jsonl'
    if (Test-Path -LiteralPath $samplePath) { throw 'Self-check output must be fresh' }
    $process = [Diagnostics.Process]::GetCurrentProcess()
    $created = [string]$process.StartTime.ToUniversalTime().ToFileTimeUtc()
    $wrongReceipt = Join-Path $OutputRoot 'selfcheck-wrong-receipt.json'
    $goodReceipt = Join-Path $OutputRoot 'selfcheck-root-receipt.json'
    $sampler = [DemandWorkingSetSampler]::new($samplePath)
    try {
        $sampler.Observe($PID, [long]($created) + 10, 'pwsh.exe', $null)
        Start-Sleep -Milliseconds 350
        if (@(Get-Content -LiteralPath $samplePath | ForEach-Object { ($_ | ConvertFrom-Json).procs }).Count) {
            throw 'Sampler adopted a reused PID'
        }
        # 握手回归：错误身份（创建时间不匹配）登记后不得回执。
        $sampler.IssueReceiptWhenRootSampled($PID, [long]($created) + 10, $wrongReceipt)
        Start-Sleep -Milliseconds 300
        if (Test-Path -LiteralPath $wrongReceipt) { throw 'Receipt issued for a wrong identity' }
        # 延迟登记：root 只在此刻注册；回执必须等待含 root 的完整样本落盘。
        $sampler.Observe($PID, [long]$created, 'pwsh.exe', $null)
        # 根审反例：pid 正确但 creation 跨微秒格的请求——即使后续样本实际含该
        # pid，也不得按 pid-only 匹配签发错误 creation 回执。
        $crossReceipt = Join-Path $OutputRoot 'selfcheck-cross-receipt.json'
        $sampler.IssueReceiptWhenRootSampled($PID, [long]($created) + 10, $crossReceipt)
        Start-Sleep -Milliseconds 600
        if (Test-Path -LiteralPath $crossReceipt) {
            throw 'Receipt signed from a pid-only match with a wrong-grid creation'
        }
        $sampler.IssueReceiptWhenRootSampled($PID, [long]$created, $goodReceipt)
        $started = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        # Simulate blocked CIM discovery without blocking the owned-process sampler.
        Start-Sleep -Milliseconds 1200
        $completed = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $receiptDeadline = [DateTimeOffset]::UtcNow.AddSeconds(3)
        while (-not (Test-Path -LiteralPath $goodReceipt) -and [DateTimeOffset]::UtcNow -lt $receiptDeadline) {
            Start-Sleep -Milliseconds 50
        }
        if (-not (Test-Path -LiteralPath $goodReceipt)) { throw 'Root first-sample receipt was not issued' }
    } finally { $sampler.Dispose(); $process.Dispose() }
    $length = (Get-Item -LiteralPath $samplePath).Length
    Start-Sleep -Milliseconds 300
    if ((Get-Item -LiteralPath $samplePath).Length -ne $length) { throw 'Sampler wrote after disposal' }
    if (Test-Path -LiteralPath $wrongReceipt) { throw 'Wrong-identity receipt appeared after disposal' }
    if (Test-Path -LiteralPath $crossReceipt) { throw 'Wrong-grid creation receipt appeared after disposal' }
    if (Test-Path -LiteralPath "$goodReceipt.tmp") { throw 'Receipt temp file leaked' }
    $receipt = Get-Content -LiteralPath $goodReceipt -Raw | ConvertFrom-Json
    if ($receipt.pid -ne $PID -or $receipt.creation_filetime -ne $created) {
        throw 'Receipt identity mismatch'
    }
    if ($receipt.sample_at_ms -lt $receipt.sample_started_at_ms -or $receipt.issued_at_ms -lt $receipt.sample_at_ms) {
        throw 'Receipt timestamps are not ordered'
    }
    $sampleLines = @(Get-Content -LiteralPath $samplePath | ForEach-Object { $_ | ConvertFrom-Json })
    if (-not @($sampleLines | Where-Object { $_.at_ms -le $receipt.sample_at_ms -and
        @($_.procs | Where-Object { $_.pid -eq $PID }).Count -gt 0 }).Count) {
        throw 'Receipt was not preceded by a complete root-containing sample on disk'
    }
    $samples = @($sampleLines |
        Where-Object { $_.started_at_ms -ge $started -and $_.at_ms -le $completed -and $_.procs.Count -eq 1 })
    if ($samples.Count -lt 3 -or @($samples | Where-Object {
        $_.at_ms -lt $_.started_at_ms -or $_.procs[0].pid -ne $PID -or $_.procs[0].ws_bytes -le 0
    }).Count) { throw 'Independent real working-set samples are missing or invalid' }
    Write-Output "Windows argv and independent owned working-set sampler: passed ($($samples.Count) samples)"
    Write-Output 'Root first-sample receipt handshake: passed (delayed registration, wrong identity refused, wrong-grid creation refused even with the pid sampled, receipt only after a root-containing sample, nothing written after disposal)'
    return
}
$rootFile = Join-Path $OutputRoot 'root-pid.txt'
$stopFile = Join-Path $OutputRoot 'stop-monitor'
$startsFile = Join-Path $OutputRoot 'process-starts.jsonl'
$samplesFile = Join-Path $OutputRoot 'process-samples.jsonl'
# capture-first-frame 握手：仅当冒烟显式放置 marker 时，root 登记后向采样器
# 请求首样本回执（其他 monitor 证据根不出现任何新文件/行为）。
$receiptMarker = Join-Path $OutputRoot 'expect-root-receipt'
$receiptFile = Join-Path $OutputRoot 'root-first-sample.json'
$receiptRequested = $false
$owned = @{}
@{ source = 'CIM ownership discovery + independent .NET working-set reads'; interval_ms = 250; complete_start_trace = $false
    sample_interval = 'started_at_ms through at_ms; both bound the actual working-set reads'
    evidence_gap = 'Win32_ProcessStartTrace denied by current Windows account; short-lived processes/calls can be missed' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'monitor-info.json')
Set-Content -LiteralPath (Join-Path $OutputRoot 'monitor-ready') -Value 'ready'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$sampler = [DemandWorkingSetSampler]::new($samplesFile)
try {
    while (-not (Test-Path -LiteralPath $stopFile)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Process monitor deadline exceeded.' }
        if (Test-Path -LiteralPath $rootFile) {
            $rootPid = [int](Get-Content -LiteralPath $rootFile -Raw)
            if ($owned.ContainsKey($rootPid) -or -not (Test-Path -LiteralPath $receiptMarker)) {
                $all = @(Get-CimInstance Win32_Process | Sort-Object CreationDate)
            } else {
                # 仅握手模式（marker 存在）在 root 未登记时用单 PID 定向查询缩短
                # App 建窗前等待；其他模式保留原首次全量枚举，不改变首批子进程发现时序。
                $all = @(Get-CimInstance Win32_Process -Filter "ProcessId = $rootPid")
            }
            $liveById = @{}
            foreach ($process in $all) { $liveById[[int]$process.ProcessId] = $process }
            foreach ($process in $all) {
                $processId = [int]$process.ProcessId
                if ($processId -ne $rootPid -and -not $owned.ContainsKey([int]$process.ParentProcessID) -and -not $owned.ContainsKey($processId)) { continue }
                $created = [string]$process.CreationDate.ToUniversalTime().ToFileTimeUtc()
                if ($owned.ContainsKey($processId) -and $owned[$processId].creation_filetime -ne $created) { continue }
                if (-not $owned.ContainsKey($processId)) {
                    if ($processId -ne $rootPid) {
                        # A stale parent PID must not adopt an unrelated process after reuse.
                        $parentId = [int]$process.ParentProcessID
                        $parent = $liveById[$parentId]
                        if (-not $parent -or -not $owned.ContainsKey($parentId) -or
                            [string]$parent.CreationDate.ToUniversalTime().ToFileTimeUtc() -ne $owned[$parentId].creation_filetime) { continue }
                    }
                    $class = 'other'
                    $action = $null
                    $gap = $null
                    if ([string]$process.Name -match 'runtime-installer') {
                        # Frozen bootloader children repeat argv; count only the invocation root.
                        $class = if ($owned[[int]$process.ParentProcessID].name -match 'runtime-installer') { 'installer-child' } else { 'installer' }
                        if ($process.CommandLine) {
                            $argv = [DemandProbeArgv]::Split($process.CommandLine)
                            $requestIndex = [Array]::IndexOf($argv, '--request-json')
                            if ($requestIndex -ge 0 -and $requestIndex + 1 -lt $argv.Count) {
                                $request = $argv[$requestIndex + 1] | ConvertFrom-Json
                                $action = @($request.action, $request.operation, $request.command, $request.request_kind) |
                                    Where-Object { $_ } | Select-Object -First 1
                                if ($action -and $action -notmatch '^[a-z_]+$') { throw 'Unexpected Installer action shape.' }
                            }
                        }
                        if (-not $action) { $gap = 'installer_request_not_observed' }
                    } elseif ([string]$process.Name -match 'python') {
                        $class = if ($process.CommandLine -match 'vibeocr.runtime.host.main') { 'supervisor' } else { 'python-child' }
                        if (-not $process.CommandLine) { $gap = 'python_role_not_observed' }
                    }
                    $webViewRole = $null
                    if ([string]$process.Name -ieq 'msedgewebview2.exe') {
                        $webViewRole = 'unknown'
                        if ($process.CommandLine) {
                            $webViewRole = 'browser'
                            if ($process.CommandLine -match '--type=([^\s"]+)') {
                                $webViewRole = if ($Matches[1] -in @('renderer', 'utility', 'gpu-process')) { $Matches[1] } else { 'other' }
                            }
                        }
                    }
                    $record = [ordered]@{
                        pid = $processId; ppid = [int]$process.ParentProcessID; name = [string]$process.Name
                        started_ms = [DateTimeOffset]::new($process.CreationDate.ToUniversalTime()).ToUnixTimeMilliseconds()
                        observed_ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                        creation_filetime = $created
                        class = $class; action = $action; evidence_gap = $gap; webview_role = $webViewRole
                    }
                    $owned[$processId] = $record
                    $sampler.Observe($processId, [long]$created, [string]$process.Name, $webViewRole)
                    $record | ConvertTo-Json -Compress | Add-Content -LiteralPath $startsFile
                    if ($processId -eq $rootPid -and -not $receiptRequested -and
                        (Test-Path -LiteralPath $receiptMarker)) {
                        $receiptRequested = $true
                        # 回执仅在含本 root（创建时间已核对）的完整样本写盘后原子写出，
                        # 供 capture-first-frame App 在任何主窗/WebView 创建前有界等待。
                        $sampler.IssueReceiptWhenRootSampled($rootPid, [long]$created, $receiptFile)
                    }
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
} finally { $sampler.Dispose() }
