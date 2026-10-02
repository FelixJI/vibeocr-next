using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeOCR.Platform.Inference;

/// <summary>
/// Windows Job Object with KILL_ON_JOB_CLOSE. If the frontend crashes, closing
/// its kernel handles terminates the Supervisor and every descendant process.
/// </summary>
internal sealed class WindowsJobObject : IDisposable
{
    private const uint ExtendedLimitInformationClass = 9;
    private const uint BasicAccountingInformationClass = 1;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint SupervisorTerminationExitCode = 1;
    private const uint SnapshotProcesses = 0x00000002;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;
    private const int MaxTreeEnrollmentPasses = 8;
    private readonly SafeFileHandle _handle;

    public WindowsJobObject()
    {
        _handle = CreateJobObjectW(nint.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(
                    _handle,
                    ExtendedLimitInformationClass,
                    buffer,
                    (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void AssignProcessTree(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        int rootProcessId = process.Id;
        Assign(process);

        // The root handle belongs to the caller's Process instance: keep it
        // pinned for the whole traversal but never dispose it here.
        long rootCreationTime = ReadProcessTreeRootCreationTime(process, rootProcessId);
        var verifiedProcesses = new Dictionary<int, VerifiedProcess>
        {
            [rootProcessId] = new VerifiedProcess(
                process.SafeHandle, rootCreationTime, rootProcessId, ownsHandle: false),
        };
        try
        {
            // The outer passes follow process-tree changes between snapshots;
            // each snapshot is expanded to a fixed point so neither entry
            // ordering nor intermediate depth consumes tree-change passes.
            for (int pass = 0; pass < MaxTreeEnrollmentPasses; pass++)
            {
                bool assignedProcess = false;
                List<ProcessEntry> entries = CaptureProcessEntries(
                    rootProcessId,
                    candidateProcessId: null,
                    parentProcessId: null,
                    pass,
                    "capture-pass-snapshot");
                var attemptedProcessIds = new HashSet<int>();
                bool expanded;
                do
                {
                    expanded = false;
                    foreach (ProcessEntry entry in entries)
                    {
                        int processId = checked((int)entry.ProcessId);
                        if (verifiedProcesses.ContainsKey(processId)
                            || attemptedProcessIds.Contains(processId))
                        {
                            continue;
                        }
                        if (!verifiedProcesses.TryGetValue(
                                checked((int)entry.ParentProcessId),
                                out VerifiedProcess? parent))
                        {
                            continue;
                        }
                        CandidateEnrollmentResult result = TryAssignVerifiedProcess(
                            verifiedProcesses, processId, parent, rootProcessId, pass);
                        if (result == CandidateEnrollmentResult.NotEnrolled)
                        {
                            attemptedProcessIds.Add(processId);
                            continue;
                        }
                        expanded = true;
                        assignedProcess |= result == CandidateEnrollmentResult.Assigned;
                    }
                }
                while (expanded);
                if (!assignedProcess)
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                "Supervisor process tree did not stabilize during Job Object enrollment.");
        }
        finally
        {
            foreach (VerifiedProcess verified in verifiedProcesses.Values)
            {
                if (verified.OwnsHandle)
                {
                    verified.Handle.Dispose();
                }
            }
        }
    }

    private CandidateEnrollmentResult TryAssignVerifiedProcess(
        Dictionary<int, VerifiedProcess> verifiedProcesses,
        int processId,
        VerifiedProcess parent,
        int rootProcessId,
        int pass)
    {
        Win32Exception Failure(string stage, uint? requestedAccess, int error)
            => CreateEnrollmentException(
                rootProcessId,
                processId,
                parent.ProcessId,
                pass,
                stage,
                requestedAccess,
                error);

        // A query handle pins the exact process instance: while it stays open
        // the PID cannot be reused, so every identity fact gathered below
        // belongs to the candidate observed in the traversal snapshot.
        SafeProcessHandle identityHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            (uint)processId);
        if (identityHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            identityHandle.Dispose();
            if (error == ErrorInvalidParameter)
            {
                // The candidate exited before its instance could be pinned.
                return CandidateEnrollmentResult.NotEnrolled;
            }
            throw Failure(
                "open-candidate-identity", ProcessQueryLimitedInformation, error);
        }

        bool handleTransferred = false;
        try
        {
            if (!GetProcessTimes(
                    identityHandle,
                    out long creationTime,
                    out _,
                    out _,
                    out _))
            {
                throw Failure(
                    "read-candidate-creation-time",
                    null,
                    Marshal.GetLastWin32Error());
            }

            // Refresh the snapshot while the identity handle is open so the
            // reported parent is checked against the current PID owner.
            ProcessEntry? currentEntry = FindProcessEntry(
                CaptureProcessEntries(
                    rootProcessId,
                    processId,
                    parent.ProcessId,
                    pass,
                    "confirm-candidate-parent"),
                processId);
            if (!IsVerifiedDescendantInstance(
                    parent.CreationTime,
                    creationTime,
                    currentEntry?.ParentProcessId,
                    parent.ProcessId))
            {
                // Stale edge or reused PID: exclude it and never expand it.
                return CandidateEnrollmentResult.NotEnrolled;
            }

            if (!IsProcessInJob(identityHandle, _handle, out bool inJob))
            {
                throw Failure("query-job-membership", null, Marshal.GetLastWin32Error());
            }
            if (!inJob)
            {
                // Only processes still missing from the job need the stronger
                // control handle; job members keep the identity handle only.
                using SafeProcessHandle controlHandle = OpenProcess(
                    ProcessTerminate | ProcessSetQuota,
                    inheritHandle: false,
                    (uint)processId);
                if (controlHandle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorInvalidParameter)
                    {
                        return CandidateEnrollmentResult.NotEnrolled;
                    }
                    throw Failure(
                        "open-candidate-control", ProcessTerminate | ProcessSetQuota, error);
                }
                if (!AssignProcessToJobObject(_handle, controlHandle))
                {
                    int assignmentError = Marshal.GetLastWin32Error();
                    if (assignmentError == ErrorInvalidParameter)
                    {
                        return CandidateEnrollmentResult.NotEnrolled;
                    }
                    throw Failure(
                        "assign-candidate", ProcessTerminate | ProcessSetQuota, assignmentError);
                }
            }

            verifiedProcesses.Add(
                processId,
                new VerifiedProcess(
                    identityHandle, creationTime, processId, ownsHandle: true));
            handleTransferred = true;
            return inJob
                ? CandidateEnrollmentResult.AlreadyInJob
                : CandidateEnrollmentResult.Assigned;
        }
        finally
        {
            if (!handleTransferred)
            {
                identityHandle.Dispose();
            }
        }
    }

    /// <summary>
    /// Judges whether a snapshot edge identifies a descendant of the pinned
    /// parent instance: the candidate must postdate the parent instance and a
    /// refreshed snapshot must still attribute the candidate PID to that same
    /// parent. Stale PPID edges left behind by PID reuse fail one of the two
    /// checks and must not be enrolled.
    /// </summary>
    internal static bool IsVerifiedDescendantInstance(
        long parentCreationTime,
        long candidateCreationTime,
        uint? currentParentProcessId,
        int verifiedParentProcessId)
    {
        if (candidateCreationTime < parentCreationTime)
        {
            return false;
        }
        return currentParentProcessId == (uint)verifiedParentProcessId;
    }

    private static long ReadProcessTreeRootCreationTime(Process process, int rootProcessId)
    {
        SafeProcessHandle rootHandle;
        try
        {
            rootHandle = process.SafeHandle;
        }
        catch (Win32Exception exception)
        {
            throw CreateEnrollmentException(
                rootProcessId,
                candidateProcessId: rootProcessId,
                parentProcessId: null,
                pass: 0,
                "open-root-identity",
                null,
                exception.NativeErrorCode);
        }

        if (!GetProcessTimes(rootHandle, out long creationTime, out _, out _, out _))
        {
            throw CreateEnrollmentException(
                rootProcessId,
                candidateProcessId: rootProcessId,
                parentProcessId: null,
                pass: 0,
                "read-root-creation-time",
                null,
                Marshal.GetLastWin32Error());
        }
        return creationTime;
    }

    private static Win32Exception CreateEnrollmentException(
        int rootProcessId,
        int? candidateProcessId,
        int? parentProcessId,
        int pass,
        string stage,
        uint? requestedAccess,
        int error)
    {
        string accessText = requestedAccess is null
            ? "inherited"
            : $"0x{requestedAccess.Value:X4}";
        return new Win32Exception(
            error,
            $"Job Object process tree enrollment failed: root process {rootProcessId}, candidate {candidateProcessId?.ToString() ?? "none"}, parent {parentProcessId?.ToString() ?? "none"}, pass {pass}, stage {stage}, access {accessText}, win32 error {error}.");
    }

    private static List<ProcessEntry> CaptureProcessEntries(
        int rootProcessId,
        int? candidateProcessId,
        int? parentProcessId,
        int pass,
        string stage)
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            throw CreateEnrollmentException(
                rootProcessId,
                candidateProcessId,
                parentProcessId,
                pass,
                stage,
                requestedAccess: null,
                Marshal.GetLastWin32Error());
        }

        var entries = new List<ProcessEntry>();
        var entry = new ProcessEntry
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry>(),
        };
        if (Process32FirstW(snapshot, ref entry))
        {
            do
            {
                entries.Add(entry);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        else
        {
            throw CreateEnrollmentException(
                rootProcessId,
                candidateProcessId,
                parentProcessId,
                pass,
                stage,
                requestedAccess: null,
                Marshal.GetLastWin32Error());
        }
        return entries;
    }

    private static ProcessEntry? FindProcessEntry(List<ProcessEntry> entries, int processId)
    {
        foreach (ProcessEntry entry in entries)
        {
            if (checked((int)entry.ProcessId) == processId)
            {
                return entry;
            }
        }
        return null;
    }

    private enum CandidateEnrollmentResult
    {
        NotEnrolled,
        AlreadyInJob,
        Assigned,
    }

    private sealed class VerifiedProcess(
        SafeProcessHandle handle,
        long creationTime,
        int processId,
        bool ownsHandle)
    {
        public SafeProcessHandle Handle { get; } = handle;
        public long CreationTime { get; } = creationTime;
        public int ProcessId { get; } = processId;
        public bool OwnsHandle { get; } = ownsHandle;
    }

    public bool TerminateAndWait(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if (!TerminateJobObject(_handle, SupervisorTerminationExitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var elapsed = Stopwatch.StartNew();
        while (GetActiveProcessCount() != 0)
        {
            TimeSpan remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            Thread.Sleep(remaining < TimeSpan.FromMilliseconds(10)
                ? remaining
                : TimeSpan.FromMilliseconds(10));
        }
        return true;
    }

    private uint GetActiveProcessCount()
    {
        uint size = (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>();
        if (!QueryInformationJobObject(
                _handle,
                BasicAccountingInformationClass,
                out JobObjectBasicAccountingInformation information,
                size,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return information.ActiveProcesses;
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(
        nint jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        nint information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeProcessHandle process,
        SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(
        uint flags,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(
        SafeFileHandle snapshot,
        ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(
        SafeFileHandle snapshot,
        ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(
        SafeFileHandle job,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        uint informationClass,
        out JobObjectBasicAccountingInformation jobObjectInformation,
        uint jobObjectInformationLength,
        out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }
}
