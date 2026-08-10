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

        for (int pass = 0; pass < MaxTreeEnrollmentPasses; pass++)
        {
            bool enrolledProcess = false;
            foreach (int processId in CaptureDescendantProcessIds(rootProcessId))
            {
                enrolledProcess |= TryAssignExistingProcess(processId);
            }
            if (!enrolledProcess)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "Supervisor process tree did not stabilize during Job Object enrollment.");
    }

    private bool TryAssignExistingProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited || IsProcessAssigned(process))
            {
                return false;
            }
            if (AssignProcessToJobObject(_handle, process.SafeHandle))
            {
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            if (process.HasExited)
            {
                return false;
            }
            throw new Win32Exception(error);
        }
        catch (ArgumentException)
        {
            // The process exited after the snapshot was captured.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited while its handle or state was queried.
            return false;
        }
    }

    private bool IsProcessAssigned(Process process)
    {
        if (!IsProcessInJob(process.SafeHandle, _handle, out bool assigned))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return assigned;
    }

    private static IReadOnlyList<int> CaptureDescendantProcessIds(int rootProcessId)
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
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
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var processTree = new HashSet<int> { rootProcessId };
        bool changed;
        do
        {
            changed = false;
            foreach (ProcessEntry candidate in entries)
            {
                int processId = checked((int)candidate.ProcessId);
                int parentProcessId = checked((int)candidate.ParentProcessId);
                if (processId != rootProcessId
                    && processTree.Contains(parentProcessId)
                    && processTree.Add(processId))
                {
                    changed = true;
                }
            }
        }
        while (changed);
        processTree.Remove(rootProcessId);
        return processTree.ToArray();
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
