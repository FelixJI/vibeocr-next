using System.Runtime.InteropServices;

namespace VibeOCR.Platform.Windows;

/// <summary>
/// 一块物理显示器的目标描述：DeviceName 是 EnumDisplayMonitors 的稳定
/// 设备名（\\.\DISPLAY1），Bounds 为物理像素矩形（进程按 PMv2 感知）。
/// </summary>
public sealed record ScreenMonitorTarget(
    PhysicalRectangle Bounds,
    bool IsPrimary,
    string DeviceName);

/// <summary>
/// 截图目标几何查询，统一使用物理像素：frame 取 DWM 可见窗口边界
/// （不包含 GetWindowRect 的不可见 resize 边框），client 取客户区。
/// 不向窗口外扩展阴影；桌面/显示器/区域按合成桌面采样，保留区域内
/// 的阴影像素。BitBlt 本身不删除阴影，也不能分离自绘阴影。
/// DWM 边界不可得时明确拒绝，避免用不同边界语义静默替代。
/// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowrect
/// </summary>
public static class ScreenTargetQuery
{
  public const string WindowBoundsFrame = "frame";
  public const string WindowBoundsClient = "client";

  private const int VirtualScreenX = 76;
  private const int VirtualScreenY = 77;
  private const int VirtualScreenWidth = 78;
  private const int VirtualScreenHeight = 79;
  private const uint DwmwaExtendedFrameBounds = 9;

  /// <summary>全部显示器（物理像素）。主显示器原点恒为 (0,0)。</summary>
  public static IReadOnlyList<ScreenMonitorTarget> EnumerateMonitors()
  {
    var monitors = new List<ScreenMonitorTarget>();
    EnumDisplayMonitors(0, 0,
        (monitor, _, _, _) =>
        {
          var info = new MonitorInfoExW
          {
            CbSize = (uint)Marshal.SizeOf<MonitorInfoExW>(),
          };
          if (GetMonitorInfoW(monitor, ref info))
          {
            monitors.Add(new ScreenMonitorTarget(
                    new PhysicalRectangle(
                        info.RcMonitor.Left,
                        info.RcMonitor.Top,
                        info.RcMonitor.Right - info.RcMonitor.Left,
                        info.RcMonitor.Bottom - info.RcMonitor.Top),
                    (info.DwFlags & 1u) != 0,
                    info.DeviceName));
          }

          return true;
        }, 0);
    return monitors;
  }

  /// <summary>虚拟桌面物理矩形（与截图选区 overlay 同一坐标系）。</summary>
  public static PhysicalRectangle VirtualDesktop() => new(
      GetSystemMetrics(VirtualScreenX),
      GetSystemMetrics(VirtualScreenY),
      GetSystemMetrics(VirtualScreenWidth),
      GetSystemMetrics(VirtualScreenHeight));

  public static nint GetForegroundWindowHandle() => GetForegroundWindow();

  public static bool LayoutsMatch(
      IReadOnlyList<ScreenMonitorTarget> previous, IReadOnlyList<ScreenMonitorTarget> current) =>
      previous.OrderBy(monitor => monitor.DeviceName, StringComparer.Ordinal)
          .SequenceEqual(current.OrderBy(monitor => monitor.DeviceName, StringComparer.Ordinal));

  /// <summary>
  /// 窗口可见边框矩形（DWM extended frame bounds）。
  /// 窗口无效/不可见时返回 null。
  /// </summary>
  public static PhysicalRectangle? TryGetVisibleFrameBounds(nint window) =>
      TryGetBounds(window);

  /// <summary>窗口客户区屏幕矩形。窗口无效时返回 null。</summary>
  public static PhysicalRectangle? TryGetClientBounds(nint window)
  {
    if (window == 0 || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window))
    {
      return null;
    }

    if (!GetClientRect(window, out NativeRect client))
    {
      return null;
    }

    var origin = new NativePoint();
    if (!ClientToScreen(window, ref origin))
    {
      return null;
    }

    int width = client.Right - client.Left;
    int height = client.Bottom - client.Top;
    return width > 0 && height > 0
        ? new PhysicalRectangle(origin.X, origin.Y, width, height)
        : null;
  }

  /// <summary>按设置返回窗口矩形；windowBoundsMode 仅接受 frame/client。</summary>
  public static PhysicalRectangle? TryGetWindowBounds(
      nint window, string windowBoundsMode) =>
      string.Equals(windowBoundsMode, WindowBoundsClient, StringComparison.Ordinal)
          ? TryGetClientBounds(window)
          : TryGetVisibleFrameBounds(window);

  private static PhysicalRectangle? TryGetBounds(nint window)
  {
    if (window == 0 || !IsWindow(window) || !IsWindowVisible(window) || IsIconic(window))
    {
      return null;
    }

    var rect = new NativeRect();
    if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) != 0)
    {
      return null;
    }

    int width = rect.Right - rect.Left;
    int height = rect.Bottom - rect.Top;
    return width > 0 && height > 0
        ? new PhysicalRectangle(rect.Left, rect.Top, width, height)
        : null;
  }

  // MONITORENUMPROC 的第 3 参是 LPRECT 指针；lambda 不能声明 ref 参数，
  // 因此按指针传递并显式解引用。
  private delegate bool MonitorEnumProc(
      nint monitor, nint deviceContext, nint bounds, nint data);

  [DllImport("user32.dll")]
  private static extern int GetSystemMetrics(int index);

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool EnumDisplayMonitors(
      nint deviceContext, nint clip, MonitorEnumProc callback, nint data);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoExW info);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindow(nint window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindowVisible(nint window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsIconic(nint window);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetWindowRect(nint window, out NativeRect rect);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetClientRect(nint window, out NativeRect rect);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ClientToScreen(nint window, ref NativePoint point);

  [DllImport("dwmapi.dll")]
  private static extern int DwmGetWindowAttribute(
      nint window, uint attribute, out NativeRect value, int size);

  [StructLayout(LayoutKind.Sequential)]
  private struct NativePoint { public int X; public int Y; }

  [StructLayout(LayoutKind.Sequential)]
  private struct NativeRect
  {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct MonitorInfoExW
  {
    public uint CbSize;
    public NativeRect RcMonitor;
    public NativeRect RcWork;
    public uint DwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
  }
}
