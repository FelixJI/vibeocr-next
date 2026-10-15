using System.Runtime.InteropServices;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

/// <summary>
/// #236 截图目标几何契约：显示器枚举/虚拟桌面在真实 Windows 上的形状，
/// 无效句柄的显式拒绝，以及光标叠加确实把像素绘入 GDI 表面。
/// </summary>
public class ScreenTargetQueryTests
{
  [Fact]
  public void EnumerateMonitorsReportsAtLeastOnePrimaryAtOrigin()
  {
    IReadOnlyList<ScreenMonitorTarget> monitors = ScreenTargetQuery.EnumerateMonitors();

    Assert.NotEmpty(monitors);
    Assert.Contains(monitors, monitor => monitor.IsPrimary &&
        monitor.Bounds.X == 0 && monitor.Bounds.Y == 0 &&
        monitor.Bounds.Width > 0 && monitor.Bounds.Height > 0);
    Assert.All(monitors, monitor =>
    {
      Assert.False(string.IsNullOrWhiteSpace(monitor.DeviceName));
      Assert.True(monitor.Bounds.Width > 0 && monitor.Bounds.Height > 0,
              $"monitor {monitor.DeviceName} bounds must be positive");
    });
  }

  [Fact]
  public void VirtualDesktopCoversPrimaryOriginAndMatchesOverlayCoordinateSpace()
  {
    PhysicalRectangle desktop = ScreenTargetQuery.VirtualDesktop();
    PhysicalRectangle primary = ScreenTargetQuery.EnumerateMonitors()
        .Single(monitor => monitor.IsPrimary).Bounds;

    Assert.True(desktop.Width > 0 && desktop.Height > 0);
    Assert.InRange(primary.X, desktop.X, desktop.Right);
    Assert.InRange(primary.Y, desktop.Y, desktop.Bottom);
  }

  [Fact]
  public void InvalidWindowHandlesAreRejectedExplicitly()
  {
    Assert.Null(ScreenTargetQuery.TryGetVisibleFrameBounds(0));
    Assert.Null(ScreenTargetQuery.TryGetClientBounds(0));
    Assert.Null(ScreenTargetQuery.TryGetWindowBounds(0, ScreenTargetQuery.WindowBoundsClient));
    Assert.Null(ScreenTargetQuery.TryGetWindowBounds(0, ScreenTargetQuery.WindowBoundsFrame));
  }

  [Fact]
  public void LayoutComparisonPreservesNegativeCoordinatesAndRejectsChanges()
  {
    var left = new ScreenMonitorTarget(new PhysicalRectangle(-1920, -400, 1920, 1080), false, "left");
    var main = new ScreenMonitorTarget(new PhysicalRectangle(0, 0, 2560, 1440), true, "main");
    Assert.True(ScreenTargetQuery.LayoutsMatch([left, main], [main, left]));
    Assert.False(ScreenTargetQuery.LayoutsMatch([left, main], [main]));
    Assert.False(ScreenTargetQuery.LayoutsMatch([left, main],
        [left with { Bounds = new PhysicalRectangle(-1600, 0, 1600, 900) }, main]));
  }

  [Fact]
  public void CursorOverlayDrawsRealPixelsIntoCaptureSurface()
  {
    nint screen = GetDC(0);
    Assert.NotEqual(0, screen);
    nint memory = 0;
    nint bitmap = 0;
    nint previous = 0;
    try
    {
      memory = CreateCompatibleDC(screen);
      bitmap = CreateCompatibleBitmap(screen, 48, 48);
      Assert.NotEqual(0, memory);
      Assert.NotEqual(0, bitmap);
      previous = SelectObject(memory, bitmap);
      Assert.True(PatBlt(memory, 0, 0, 48, 48, Blackness));

      var bounds = new PhysicalRectangle(0, 0, 48, 48);
      nint arrow = LoadCursor(0, 32512 /* IDC_ARROW */);
      Assert.NotEqual(0, arrow);
      // 屏幕位置 24,24、热点 0,0 → 光标左上角落在捕获表面内。
      Assert.True(GdiScreenCursorOverlay.TryDrawCursor(
          memory, arrow, 0, 0, 24, 24, bounds));

      byte[] pixels = new byte[48 * 48 * 4];
      var info = new BitmapInfo
      {
        Header = new BitmapInfoHeader
        {
          Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
          Width = 48,
          Height = -48,
          Planes = 1,
          BitCount = 32,
          Compression = 0,
          ImageSize = (uint)pixels.Length,
        },
      };
      Assert.Equal(48, GetDIBits(memory, bitmap, 0, 48, pixels, ref info, 0));
      Assert.Contains(pixels, value => value != 0);
    }
    finally
    {
      if (previous != 0 && memory != 0) SelectObject(memory, previous);
      if (bitmap != 0) DeleteObject(bitmap);
      if (memory != 0) DeleteDC(memory);
      ReleaseDC(0, screen);
    }
  }

  private const uint Blackness = 0x00000042;

  [DllImport("user32.dll")]
  private static extern nint GetDC(nint window);

  [DllImport("user32.dll")]
  private static extern int ReleaseDC(nint window, nint deviceContext);

  [DllImport("user32.dll")]
  private static extern nint LoadCursor(nint module, int resource);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern nint CreateCompatibleDC(nint deviceContext);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern nint SelectObject(nint deviceContext, nint value);

  [DllImport("gdi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool PatBlt(
      nint deviceContext, int x, int y, int width, int height, uint operation);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern int GetDIBits(
      nint deviceContext, nint bitmap, uint start, uint lines,
      [Out] byte[] bits, ref BitmapInfo info, uint usage);

  [DllImport("gdi32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeleteObject(nint value);

  [DllImport("gdi32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeleteDC(nint deviceContext);

  [StructLayout(LayoutKind.Sequential)]
  private struct BitmapInfoHeader
  {
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint ImageSize;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ColorsUsed;
    public uint ColorsImportant;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct BitmapInfo
  {
    public BitmapInfoHeader Header;
    public uint Colors;
  }
}
