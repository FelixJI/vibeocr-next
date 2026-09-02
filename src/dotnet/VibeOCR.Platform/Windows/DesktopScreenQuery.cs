using System.Runtime.InteropServices;

namespace VibeOCR.Platform.Windows;

/// <summary>
/// 桌面环境查询：显示器矩形、任务栏占用边与前台全屏判定。悬浮工具栏用其
/// 计算停靠几何并做全屏/任务栏退避；全部基于 physical pixel。
/// </summary>
public static class DesktopScreenQuery
{
    private const int FullscreenTolerancePx = 1;
    private const uint AbmGetTaskbarPos = 5;
    private const uint MonitorDefaultToNearest = 2;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    /// <summary>主显示器矩形（原点恒为 (0,0)）。</summary>
    public static PhysicalRectangle GetPrimaryMonitor() => new(
        0,
        0,
        GetSystemMetrics(SmCxScreen),
        GetSystemMetrics(SmCyScreen));

    /// <summary>距给定矩形最近的显示器矩形（显示器热插拔后仍可用）。</summary>
    public static PhysicalRectangle GetMonitorContaining(PhysicalRectangle bounds)
    {
        var area = new RectL
        {
            Left = bounds.X,
            Top = bounds.Y,
            Right = bounds.Right,
            Bottom = bounds.Bottom,
        };
        nint monitor = MonitorFromRect(ref area, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return GetPrimaryMonitor();
        }

        var info = new MonitorInfo
        {
            CbSize = (uint)Marshal.SizeOf<MonitorInfo>(),
        };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return GetPrimaryMonitor();
        }

        return new PhysicalRectangle(
            info.RcMonitor.Left,
            info.RcMonitor.Top,
            info.RcMonitor.Right - info.RcMonitor.Left,
            info.RcMonitor.Bottom - info.RcMonitor.Top);
    }

    /// <summary>
    /// 任务栏（含副屏任务栏）占用的屏幕边集合。停靠吸附时避开这些边，
    /// 防止感应条与任务栏抢边缘命中区。
    /// </summary>
    public static IReadOnlySet<ScreenEdge> GetTaskbarOccupiedEdges()
    {
        var edges = new HashSet<ScreenEdge>();
        nint taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != 0)
        {
            AddTaskbarEdge(edges, taskbar);
        }

        nint secondary = 0;
        while ((secondary = FindWindowEx(0, secondary, "Shell_SecondaryTrayWnd", null)) != 0)
        {
            AddTaskbarEdge(edges, secondary);
        }

        return edges;
    }

    /// <summary>
    /// 前台窗口是否恰好覆盖给定显示器（无边框全屏）。最大化的有边框窗口
    /// 会越出显示器约 7px，不会被误判。
    /// </summary>
    public static bool IsForegroundWindowFullscreen(PhysicalRectangle monitorBounds)
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }

        if (!GetWindowRect(foreground, out RectL rect))
        {
            return false;
        }

        return Math.Abs(rect.Left - monitorBounds.X) <= FullscreenTolerancePx
            && Math.Abs(rect.Top - monitorBounds.Y) <= FullscreenTolerancePx
            && Math.Abs(rect.Right - monitorBounds.Right) <= FullscreenTolerancePx
            && Math.Abs(rect.Bottom - monitorBounds.Bottom) <= FullscreenTolerancePx;
    }

    private static void AddTaskbarEdge(HashSet<ScreenEdge> edges, nint taskbar)
    {
        var data = new AppBarData
        {
            CbSize = (uint)Marshal.SizeOf<AppBarData>(),
            HWnd = taskbar,
        };
        if (SHAppBarMessage(AbmGetTaskbarPos, ref data) == 0)
        {
            return;
        }

        // ABE_LEFT=0, ABE_TOP=1, ABE_RIGHT=2, ABE_BOTTOM=3。
        edges.Add(data.UEdge switch
        {
            0 => ScreenEdge.Left,
            1 => ScreenEdge.Top,
            2 => ScreenEdge.Right,
            _ => ScreenEdge.Bottom,
        });
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out RectL rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref RectL rect, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(
        nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern nint SHAppBarMessage(uint message, ref AppBarData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint CbSize;
        public RectL RcMonitor;
        public RectL RcWork;
        public uint DwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint CbSize;
        public nint HWnd;
        public uint UCallbackMessage;
        public uint UEdge;
        public RectL Rc;
        public nint LParam;
    }
}
