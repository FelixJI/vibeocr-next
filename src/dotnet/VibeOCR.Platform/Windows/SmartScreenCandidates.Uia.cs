using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VibeOCR.Platform.Windows;

public sealed record SmartControlCandidate(PhysicalRectangle Bounds, int ControlType, int Depth)
{
    public static bool SamePathThrough(
        IReadOnlyList<SmartControlCandidate> before,
        IReadOnlyList<SmartControlCandidate> after, int selectedIndex)
    {
        if (selectedIndex < 0 || before.Count <= selectedIndex ||
            after.Count <= selectedIndex)
            return false;
        for (int index = 0; index <= selectedIndex; index++)
        {
            if (before[index] != after[index])
                return false;
        }
        return true;
    }
}

internal sealed record SmartControlQueryDiagnostic(
    bool PerMonitorDpiContextSet, int? CoInitializeHResult,
    string? ExceptionType, int? ExceptionHResult,
    PhysicalRectangle? RootBounds, string? RootBoundsValueType,
    bool? RootOffscreen, string? RootOffscreenValueType,
    bool? RootHasControlChild, int VisitedCount);

/// <summary>A single process-wide MTA worker. A stalled provider never creates another worker.</summary>
[SupportedOSPlatform("windows")]
public sealed class SmartControlQuery
{
    private const int MaximumNodes = 128;
    private const int MaximumDepth = 7;
    private static readonly Guid AutomationClsid = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    public static SmartControlQuery Shared { get; } = new();

    private readonly AutoResetEvent _signal = new(false);
    private readonly object _gate = new();
    private Request? _pending;

    private SmartControlQuery()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "VibeOCR UI Automation",
        };
        thread.Start();
    }

    public Task<IReadOnlyList<SmartControlCandidate>> QueryAsync(
        nint window, PhysicalPoint point, PhysicalRectangle windowBounds)
        => Enqueue(window, point, windowBounds, diagnostics: false).Result.Task;

    internal (Task<IReadOnlyList<SmartControlCandidate>> Result,
        Task<SmartControlQueryDiagnostic> Diagnostic) QueryWithDiagnosticsAsync(
        nint window, PhysicalPoint point, PhysicalRectangle windowBounds)
    {
        Request request = Enqueue(window, point, windowBounds, diagnostics: true);
        return (request.Result.Task, request.Diagnostic!.Task);
    }

    private Request Enqueue(nint window, PhysicalPoint point,
        PhysicalRectangle windowBounds, bool diagnostics)
    {
        var request = new Request(window, point, windowBounds, diagnostics);
        lock (_gate)
        {
            _pending?.Complete([]);
            _pending = request;
        }
        _signal.Set();
        return request;
    }

    private void Run()
    {
        // Thread has no windows and uses a COM multithreaded apartment.
        nint previousDpiContext = SetThreadDpiAwarenessContext((nint)(-4));
        int? initialized = previousDpiContext == 0 ? null : CoInitializeEx(0, 0);
        try
        {
            while (true)
            {
                _signal.WaitOne();
                Request? request;
                lock (_gate)
                {
                    request = _pending;
                    _pending = null;
                }
                if (request is null)
                    continue;
                request.PerMonitorDpiContextSet = previousDpiContext != 0;
                request.CoInitializeHResult = initialized;
                try
                {
                    request.Complete(initialized is >= 0 ? Query(request) : []);
                }
                catch (Exception error)
                {
                    // Inaccessible, disappearing and failing providers retain window/manual fallback.
                    request.ExceptionType = error.GetType().FullName;
                    request.ExceptionHResult = error.HResult;
                    request.Complete([]);
                }
            }
        }
        finally
        {
            if (initialized is >= 0)
                CoUninitialize();
            if (previousDpiContext != 0)
                SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }

    private static IReadOnlyList<SmartControlCandidate> Query(Request request)
    {
        IUIAutomation? automation = null;
        IUIAutomationElement? root = null;
        IUIAutomationTreeWalker? walker = null;
        try
        {
            automation = (IUIAutomation)Activator.CreateInstance(
                Type.GetTypeFromCLSID(AutomationClsid, throwOnError: true)!)!;
            automation.ElementFromHandle(request.Window, out root);
            automation.GetControlViewWalker(out walker);
            if (root is null || walker is null)
                return [];

            int visited = 0;
            var path = new List<SmartControlCandidate>();
            var best = new List<SmartControlCandidate>();
            try
            {
                Visit(root, walker, request.Point, request.WindowBounds, 0,
                    ref visited, path, best, request);
            }
            finally
            {
                request.VisitedCount = visited;
            }
            return best.ToArray();
        }
        finally
        {
            Release(walker);
            Release(root);
            Release(automation);
        }
    }

    private static void Visit(
        IUIAutomationElement element, IUIAutomationTreeWalker walker,
        PhysicalPoint point, PhysicalRectangle windowBounds, int depth,
        ref int visited, List<SmartControlCandidate> path,
        List<SmartControlCandidate> best, Request request)
    {
        if (++visited > MaximumNodes || depth > MaximumDepth)
            return;
        PhysicalRectangle? raw = ReadBounds(element, out string boundsValueType);
        if (depth == 0 && request.Diagnostic is not null)
        {
            request.RootBounds = raw;
            request.RootBoundsValueType = boundsValueType;
        }
        PhysicalRectangle? clipped = raw is { } rect
            ? SmartScreenCandidates.Intersect(rect, windowBounds)
            : null;
        bool diagnoseRoot = depth == 0 && request.Diagnostic is not null;
        bool rootOffscreen = false;
        if (diagnoseRoot)
        {
            rootOffscreen = ReadOffscreen(element, out string offscreenValueType);
            request.RootOffscreen = rootOffscreen;
            request.RootOffscreenValueType = offscreenValueType;
        }
        if (clipped is not { } candidate ||
            point.X < candidate.X || point.X >= (long)candidate.X + candidate.Width ||
            point.Y < candidate.Y || point.Y >= (long)candidate.Y + candidate.Height ||
            (diagnoseRoot ? rootOffscreen : ReadOffscreen(element, out _)))
            return;

        if (depth > 0)
        {
            path.Add(new SmartControlCandidate(candidate, ReadControlType(element), depth));
            if (path.Count > best.Count)
            {
                best.Clear();
                best.AddRange(path);
            }
        }
        if (depth < MaximumDepth && visited < MaximumNodes)
        {
            walker.GetFirstChildElement(element, out IUIAutomationElement? child);
            if (depth == 0 && request.Diagnostic is not null)
                request.RootHasControlChild = child is not null;
            while (child is not null && visited < MaximumNodes)
            {
                IUIAutomationElement? next = null;
                try
                {
                    walker.GetNextSiblingElement(child, out next);
                    Visit(child, walker, point, windowBounds, depth + 1,
                        ref visited, path, best, request);
                }
                catch
                {
                    Release(next);
                    throw;
                }
                finally
                {
                    Release(child);
                }
                child = next;
            }
            Release(child);
        }
        if (depth > 0)
            path.RemoveAt(path.Count - 1);
    }

    private static PhysicalRectangle? ReadBounds(
        IUIAutomationElement element, out string valueType)
    {
        element.GetCurrentPropertyValue(30001, out object value);
        valueType = value?.GetType().FullName ?? "null";
        if (value is not double[] { Length: 4 } coordinates ||
            coordinates.Any(number => !double.IsFinite(number)))
            return null;
        double left = Math.Floor(coordinates[0]);
        double top = Math.Floor(coordinates[1]);
        double right = Math.Ceiling(coordinates[0] + coordinates[2]);
        double bottom = Math.Ceiling(coordinates[1] + coordinates[3]);
        if (left < int.MinValue || top < int.MinValue ||
            right > int.MaxValue || bottom > int.MaxValue ||
            right - left > int.MaxValue || bottom - top > int.MaxValue ||
            right <= left || bottom <= top)
            return null;
        return new PhysicalRectangle((int)left, (int)top,
            (int)(right - left), (int)(bottom - top));
    }

    private static bool ReadOffscreen(IUIAutomationElement element,
        out string valueType)
    {
        element.GetCurrentPropertyValue(30022, out object value);
        valueType = value?.GetType().FullName ?? "null";
        return value is not false;
    }

    private static int ReadControlType(IUIAutomationElement element)
    {
        element.GetCurrentPropertyValue(30003, out object value);
        return value is int type ? type : 0;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private sealed class Request(
        nint window, PhysicalPoint point, PhysicalRectangle windowBounds,
        bool diagnostics)
    {
        public nint Window { get; } = window;
        public PhysicalPoint Point { get; } = point;
        public PhysicalRectangle WindowBounds { get; } = windowBounds;
        public TaskCompletionSource<IReadOnlyList<SmartControlCandidate>> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SmartControlQueryDiagnostic>? Diagnostic { get; } =
            diagnostics ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        public bool PerMonitorDpiContextSet { get; set; }
        public int? CoInitializeHResult { get; set; }
        public string? ExceptionType { get; set; }
        public int? ExceptionHResult { get; set; }
        public PhysicalRectangle? RootBounds { get; set; }
        public string? RootBoundsValueType { get; set; }
        public bool? RootOffscreen { get; set; }
        public string? RootOffscreenValueType { get; set; }
        public bool? RootHasControlChild { get; set; }
        public int VisitedCount { get; set; }
        public void Complete(IReadOnlyList<SmartControlCandidate> candidates)
        {
            Diagnostic?.TrySetResult(new SmartControlQueryDiagnostic(
                PerMonitorDpiContextSet, CoInitializeHResult,
                ExceptionType, ExceptionHResult,
                RootBounds, RootBoundsValueType, RootOffscreen,
                RootOffscreenValueType, RootHasControlChild, VisitedCount));
            Result.TrySetResult(candidates);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint mode);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    // Method order and signatures through the invoked methods follow UIAutomationClient.h
    // (Windows SDK 10.0.26100.0). Only property IDs for geometry/type/visibility are read.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(nint first, nint second, out int same);
        void CompareRuntimeIds(nint first, nint second, out int same);
        void GetRootElement(out IUIAutomationElement element);
        void ElementFromHandle(nint handle, out IUIAutomationElement element);
        void ElementFromPoint(NativePoint point, out IUIAutomationElement element);
        void GetFocusedElement(out IUIAutomationElement element);
        void GetRootElementBuildCache(nint cache, out IUIAutomationElement element);
        void ElementFromHandleBuildCache(nint handle, nint cache, out IUIAutomationElement element);
        void ElementFromPointBuildCache(NativePoint point, nint cache, out IUIAutomationElement element);
        void GetFocusedElementBuildCache(nint cache, out IUIAutomationElement element);
        void CreateTreeWalker(nint condition, out IUIAutomationTreeWalker walker);
        void GetControlViewWalker(out IUIAutomationTreeWalker walker);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out nint runtimeId);
        void FindFirst(int scope, nint condition, out IUIAutomationElement found);
        void FindAll(int scope, nint condition, out nint found);
        void FindFirstBuildCache(int scope, nint condition, nint cache, out IUIAutomationElement found);
        void FindAllBuildCache(int scope, nint condition, nint cache, out nint found);
        void BuildUpdatedCache(nint cache, out IUIAutomationElement element);
        void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTreeWalker
    {
        void GetParentElement(IUIAutomationElement element, out IUIAutomationElement parent);
        void GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement? child);
        void GetLastChildElement(IUIAutomationElement element, out IUIAutomationElement child);
        void GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement? next);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
}
