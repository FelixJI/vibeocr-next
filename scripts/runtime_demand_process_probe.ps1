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
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
// Toolhelp provides a cheap PID/PPID census without waiting for CIM command lines.
// Open candidates under the root to verify their identities. Unrelated snapshot
// rows stay in memory and are never persisted.
static class DemandProcessCensus {
    const uint SnapshotProcesses = 0x00000002;
    const uint QueryLimitedInformation = 0x00001000;
    const uint Synchronize = 0x00100000;
    const uint WaitSignaled = 0;
    const uint WaitTimeout = 258;
    const int NoMoreFiles = 18;
    const int InvalidParameter = 87;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry {
        public uint Size, Usage, Pid;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", ExactSpelling = true, SetLastError = true)]
    static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", ExactSpelling = true, SetLastError = true)]
    static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(SafeProcessHandle process, out long created,
        out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    static SafeProcessHandle OpenLiveProcess(int pid, out long created) {
        created = 0;
        var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, pid);
        try {
            if (handle.IsInvalid) {
                int error = Marshal.GetLastWin32Error();
                if (error == InvalidParameter) return null;
                throw new Win32Exception(error);
            }
            uint state = WaitForSingleObject(handle, 0);
            if (state == WaitSignaled) return null;
            if (state != WaitTimeout || !GetProcessTimes(handle, out created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var live = handle;
            handle = null;
            return live;
        } finally { handle?.Dispose(); }
    }

    public static void VisitKnown(int pid, long expectedCreated, string name,
        Action<int, long, string> visit) {
        using var handle = OpenLiveProcess(pid, out long created);
        if (handle != null && created / 10 == expectedCreated / 10) visit(pid, created, name);
    }

    static IEnumerable<ProcessEntry> Snapshot() {
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do { yield return entry; } while (Process32Next(snapshot, ref entry));
        int error = Marshal.GetLastWin32Error();
        if (error != NoMoreFiles) throw new Win32Exception(error);
    }

    public static void VisitOwned(int rootPid, long rootCreated, long censusStarted,
        Action<int, long, string> visit) {
        ProcessEntry? root = null;
        var children = new Dictionary<int, List<ProcessEntry>>();
        foreach (var entry in Snapshot()) {
            if (entry.Pid == rootPid) { root = entry; continue; }
            int parentPid = (int)entry.ParentPid;
            if (!children.TryGetValue(parentPid, out var siblings))
                children.Add(parentPid, siblings = new List<ProcessEntry>());
            siblings.Add(entry);
        }
        if (root is not { } candidate) return;

        var pinned = new Dictionary<int, (SafeProcessHandle Handle, long Created, ProcessEntry Entry)>();
        var confirmed = new HashSet<int>();
        try {
            Pin(candidate, parentCreated: null);
            if (!pinned.ContainsKey(rootPid)) return;
            confirmed.Add(rootPid);
            // Verify PPID/name again with candidate identities pinned, so a
            // reused PID cannot inherit an old snapshot's parent or role.
            if (pinned.Count > 1)
                foreach (var entry in Snapshot())
                    if (pinned.TryGetValue((int)entry.Pid, out var item) &&
                        item.Entry.ParentPid == entry.ParentPid && item.Entry.Name == entry.Name)
                        confirmed.Add((int)entry.Pid);
            Read(candidate);
        } finally {
            foreach (var item in pinned.Values) item.Handle.Dispose();
        }

        void Pin(ProcessEntry entry, long? parentCreated) {
            int pid = (int)entry.Pid;
            var handle = OpenLiveProcess(pid, out long created);
            // CIM supplies the root on a microsecond grid.
            if (handle == null || created > censusStarted ||
                (parentCreated is { } parent ? created < parent : created / 10 != rootCreated / 10)) {
                handle?.Dispose();
                return;
            }
            pinned.Add(pid, (handle, created, entry));
            if (children.TryGetValue(pid, out var descendants))
                foreach (var child in descendants) Pin(child, created);
        }

        void Read(ProcessEntry entry) {
            int pid = (int)entry.Pid;
            if (!confirmed.Contains(pid)) return;
            visit(pid, pinned[pid].Created, entry.Name);
            if (children.TryGetValue(pid, out var descendants))
                foreach (var child in descendants) Read(child);
        }
    }
}

// In capture-first-frame mode, enumerate the rooted tree inside each sample;
// CIM continues to supply durable lineage, roles and installer arguments.
public sealed class DemandWorkingSetSampler : IDisposable {
    readonly ConcurrentDictionary<int, (long Created, string Name, string Role)> owned = new();
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    readonly object receiptGate = new();
    readonly bool useCensus;
    (int Pid, long Created, string Path)? pendingReceipt;
    (int Pid, long Created)? censusRoot;
    public DemandWorkingSetSampler(string path) : this(path, false) { }
    public DemandWorkingSetSampler(string path, bool useCensus) {
        this.useCensus = useCensus;
        worker = Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                DateTimeOffset started = DateTimeOffset.UtcNow;
                var live = new List<object>();
                // Identities actually verified inside this sample, including
                // any descendants discovered before CIM has their command lines.
                var verified = new Dictionary<int, long>();
                (int Pid, long Created)? root;
                lock (receiptGate) root = censusRoot;
                if (root is { } identity)
                    DemandProcessCensus.VisitOwned(identity.Pid, identity.Created, started.ToFileTime(), (pid, created, name) => {
                        string role = name.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) ? "unknown" : "";
                        if (owned.TryGetValue(pid, out var known) && known.Created / 10 == created / 10)
                            role = known.Role;
                        ReadWorkingSet(pid, created, name, role, identityVerified: true);
                    });
                foreach (var item in owned) {
                    if (verified.ContainsKey(item.Key)) continue;
                    if (root.HasValue)
                        // Preserve CIM-verified descendants if their parent exited.
                        DemandProcessCensus.VisitKnown(item.Key, item.Value.Created, item.Value.Name,
                            (pid, created, name) => ReadWorkingSet(pid, created, name, item.Value.Role, identityVerified: true));
                    else
                        ReadWorkingSet(item.Key, item.Value.Created, item.Value.Name, item.Value.Role, identityVerified: false);
                }

                void ReadWorkingSet(int pid, long created, string name, string role, bool identityVerified) {
                    if (verified.ContainsKey(pid)) return;
                    try {
                        using var process = Process.GetProcessById(pid);
                        if (!identityVerified) {
                            // CIM has microsecond precision. Persist the actual live FILETIME.
                            long actual = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                            if (actual / 10 != created / 10) return;
                            created = actual;
                        }
                        process.Refresh();
                        long bytes = process.WorkingSet64;
                        if (process.HasExited) return;
                        live.Add(new { pid, name, ws_bytes = bytes, webview_role = role,
                            creation_filetime = created.ToString() });
                        verified.Add(pid, created);
                    } catch (ArgumentException) { // Process exited before opening.
                    } catch (InvalidOperationException) { // Process exited during the read.
                    } catch (Win32Exception error) when (error.NativeErrorCode == 87) {
                        // The process vanished between enumeration and opening.
                    }
                }
                long completed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                File.AppendAllText(path, JsonSerializer.Serialize(new {
                    started_at_ms = started.ToUnixTimeMilliseconds(), at_ms = completed, procs = live }) + "\n");
                (int Pid, long Created, string Path)? pending;
                lock (receiptGate) pending = pendingReceipt;
                // Root first-sample receipt (capture-first-frame handshake): written
                // ATOMICALLY only after a COMPLETE sample whose actually-verified
                // (pid, creation) tuple matches the request on the established
                // microsecond grid has been flushed to disk; the receipt attests the
                // sample-side verified value. A pid-only match, a creation on another
                // grid cell, or a never-sampled identity never receives a receipt.
                if (pending is { } request) {
                    if (verified.TryGetValue(request.Pid, out long created) && created / 10 == request.Created / 10) {
                        lock (receiptGate) pendingReceipt = null;
                        string temp = request.Path + ".tmp";
                        File.WriteAllText(temp, JsonSerializer.Serialize(new {
                            pid = request.Pid, creation_filetime = created.ToString(),
                            sample_started_at_ms = started.ToUnixTimeMilliseconds(), sample_at_ms = completed,
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
        lock (receiptGate) {
            pendingReceipt = (pid, created, receiptPath);
            if (useCensus) censusRoot = (pid, created);
        }
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

    # The child is deliberately never registered through CIM or Observe().
    $censusPath = Join-Path $OutputRoot 'selfcheck-census-samples.jsonl'
    $badCensusReceipt = Join-Path $OutputRoot 'selfcheck-census-wrong-root.json'
    $censusReceipt = Join-Path $OutputRoot 'selfcheck-census-root.json'
    $childInfo = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
    $childInfo.UseShellExecute = $false
    $childInfo.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30')) {
        $childInfo.ArgumentList.Add($argument)
    }
    $fixture = [Diagnostics.Process]::Start($childInfo)
    try {
        $censusSampler = [DemandWorkingSetSampler]::new($censusPath, $true)
        try {
            $censusSampler.Observe($PID, [long]$created, 'pwsh.exe', $null)
            $censusSampler.IssueReceiptWhenRootSampled($PID, [long]$created + 10, $badCensusReceipt)
            $wait = [Diagnostics.Stopwatch]::StartNew()
            do {
                if ($wait.Elapsed.TotalSeconds -ge 5) { throw 'No sample with root for census identity check' }
                Start-Sleep -Milliseconds 50
                if (-not (Test-Path -LiteralPath $censusPath)) { continue }
                $sample = Get-Content -LiteralPath $censusPath | Select-Object -Last 1 | ConvertFrom-Json
            } until ($sample.procs.pid -contains $PID)
            if ($sample.procs.pid -contains $fixture.Id -or (Test-Path -LiteralPath $badCensusReceipt)) {
                throw 'Census adopted a child of an invalid root identity'
            }
            $censusSampler.IssueReceiptWhenRootSampled($PID, [long]$created, $censusReceipt)
            $wait.Restart()
            do {
                if ($wait.Elapsed.TotalSeconds -ge 5) { throw 'Census missed a live child before CIM registration' }
                Start-Sleep -Milliseconds 50
                $sample = Get-Content -LiteralPath $censusPath | Select-Object -Last 1 | ConvertFrom-Json
                $childSample = @($sample.procs | Where-Object { $_.pid -eq $fixture.Id })
            } until ((Test-Path -LiteralPath $censusReceipt) -and $childSample.Count -gt 0)
            $fixtureCreation = $fixture.StartTime.ToUniversalTime().ToFileTimeUtc().ToString()
            if ($childSample.Count -ne 1 -or $childSample[0].ws_bytes -le 0 -or $sample.procs.pid -notcontains $PID -or
                $childSample[0].creation_filetime -isnot [string] -or $childSample[0].creation_filetime -cne $fixtureCreation) {
                throw 'Census child sample is invalid'
            }
            $fixture.Kill()
            if (-not $fixture.WaitForExit(3000)) { throw 'Census fixture did not exit' }
            $exitedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            $wait.Restart()
            do {
                if ($wait.Elapsed.TotalSeconds -ge 5) { throw 'No sample after census fixture exit' }
                Start-Sleep -Milliseconds 50
                $sample = Get-Content -LiteralPath $censusPath | Select-Object -Last 1 | ConvertFrom-Json
            } until ($sample.started_at_ms -ge $exitedAt)
            if ($sample.procs.pid -contains $fixture.Id) { throw 'Census included an exited child' }
        } finally { $censusSampler.Dispose() }
    } finally {
        try {
            if (-not $fixture.HasExited) {
                $fixture.Kill()
                if (-not $fixture.WaitForExit(3000)) { throw 'Census fixture cleanup timed out' }
            }
        } finally { $fixture.Dispose() }
    }
    Write-Output 'Owned per-sample census: passed (invalid root refused, real child sampled without CIM, exited child absent)'
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
$useCensus = Test-Path -LiteralPath $receiptMarker
$owned = @{}
@{ source = if ($useCensus) { 'CIM lineage + per-sample Toolhelp ownership census + independent .NET working-set reads' }
    else { 'CIM ownership discovery + independent .NET working-set reads' }
    interval_ms = 250; complete_start_trace = $false
    sample_interval = 'started_at_ms through at_ms; both bound the actual working-set reads'
    evidence_gap = 'Win32_ProcessStartTrace denied by current Windows account; short-lived processes/calls can be missed' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'monitor-info.json')
Set-Content -LiteralPath (Join-Path $OutputRoot 'monitor-ready') -Value 'ready'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$sampler = [DemandWorkingSetSampler]::new($samplesFile, $useCensus)
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
                            [string]$parent.CreationDate.ToUniversalTime().ToFileTimeUtc() -ne $owned[$parentId].creation_filetime -or
                            [long]$created -lt [long]$owned[$parentId].creation_filetime) { continue }
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
