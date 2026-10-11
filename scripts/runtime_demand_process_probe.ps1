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
'@
if ($SelfCheck) {
    $parsed = [DemandProbeArgv]::Split('installer.exe --request-json "{\"action\":\"initialize_default\"}"')
    if ($parsed.Count -ne 3 -or ($parsed[2] | ConvertFrom-Json).action -ne 'initialize_default') {
        throw 'Windows argv JSON parsing failed.'
    }
    Write-Output 'Windows argv JSON parser: passed'
    return
}
$rootFile = Join-Path $OutputRoot 'root-pid.txt'
$stopFile = Join-Path $OutputRoot 'stop-monitor'
$startsFile = Join-Path $OutputRoot 'process-starts.jsonl'
$samplesFile = Join-Path $OutputRoot 'process-samples.jsonl'
$owned = @{}
@{ source = 'CIM snapshots'; interval_ms = 250; complete_start_trace = $false
    evidence_gap = 'Win32_ProcessStartTrace denied by current Windows account; short-lived processes/calls can be missed' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'monitor-info.json')
Set-Content -LiteralPath (Join-Path $OutputRoot 'monitor-ready') -Value 'ready'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while (-not (Test-Path -LiteralPath $stopFile)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Process monitor deadline exceeded.' }
    if (Test-Path -LiteralPath $rootFile) {
        $rootPid = [int](Get-Content -LiteralPath $rootFile -Raw)
        $all = @(Get-CimInstance Win32_Process | Sort-Object CreationDate)
        $liveById = @{}
        foreach ($process in $all) { $liveById[[int]$process.ProcessId] = $process }
        $live = [Collections.Generic.List[object]]::new()
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
                $record = [ordered]@{
                    pid = $processId; ppid = [int]$process.ParentProcessID; name = [string]$process.Name
                    started_ms = [DateTimeOffset]::new($process.CreationDate.ToUniversalTime()).ToUnixTimeMilliseconds()
                    observed_ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                    creation_filetime = $created
                    class = $class; action = $action; evidence_gap = $gap
                }
                $owned[$processId] = $record
                $record | ConvertTo-Json -Compress | Add-Content -LiteralPath $startsFile
            }
            $live.Add(@{ pid = $processId; name = $process.Name; ws_bytes = [long]$process.WorkingSetSize })
        }
        @{ at_ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); procs = @($live.ToArray()) } |
            ConvertTo-Json -Depth 4 -Compress | Add-Content -LiteralPath $samplesFile
    }
    Start-Sleep -Milliseconds 250
}
