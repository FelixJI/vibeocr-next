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
    public DemandWorkingSetSampler(string path) {
        worker = Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var live = new List<object>();
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
                    } catch (ArgumentException) { // Process exited before opening.
                    } catch (InvalidOperationException) { // Process exited during the read.
                    } catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 87) {
                        // The process vanished between enumeration and opening.
                    }
                }
                long completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                File.AppendAllText(path, JsonSerializer.Serialize(new {
                    started_at_ms = started, at_ms = completed, procs = live }) + "\n");
                await Task.Delay(250, stop.Token);
            }
        });
    }
    public void Observe(int pid, long created, string name, string role) =>
        owned[pid] = (created, name, role);
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
    $created = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
    $sampler = [DemandWorkingSetSampler]::new($samplePath)
    try {
        $sampler.Observe($PID, $created + 10, 'pwsh.exe', $null)
        Start-Sleep -Milliseconds 350
        if (@(Get-Content -LiteralPath $samplePath | ForEach-Object { ($_ | ConvertFrom-Json).procs }).Count) {
            throw 'Sampler adopted a reused PID'
        }
        $sampler.Observe($PID, $created, 'pwsh.exe', $null)
        $started = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        # Simulate blocked CIM discovery without blocking the owned-process sampler.
        Start-Sleep -Milliseconds 1200
        $completed = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    } finally { $sampler.Dispose(); $process.Dispose() }
    $length = (Get-Item -LiteralPath $samplePath).Length
    Start-Sleep -Milliseconds 300
    if ((Get-Item -LiteralPath $samplePath).Length -ne $length) { throw 'Sampler wrote after disposal' }
    $samples = @(Get-Content -LiteralPath $samplePath | ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.started_at_ms -ge $started -and $_.at_ms -le $completed -and $_.procs.Count -eq 1 })
    if ($samples.Count -lt 3 -or @($samples | Where-Object {
        $_.at_ms -lt $_.started_at_ms -or $_.procs[0].pid -ne $PID -or $_.procs[0].ws_bytes -le 0
    }).Count) { throw 'Independent real working-set samples are missing or invalid' }
    Write-Output "Windows argv and independent owned working-set sampler: passed ($($samples.Count) samples)"
    return
}
$rootFile = Join-Path $OutputRoot 'root-pid.txt'
$stopFile = Join-Path $OutputRoot 'stop-monitor'
$startsFile = Join-Path $OutputRoot 'process-starts.jsonl'
$samplesFile = Join-Path $OutputRoot 'process-samples.jsonl'
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
            $all = @(Get-CimInstance Win32_Process | Sort-Object CreationDate)
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
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
} finally { $sampler.Dispose() }
