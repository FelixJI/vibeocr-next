using System.Runtime.InteropServices;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

/// <summary>
/// #236 边框语义真实 Windows 契约：普通有边框窗口上 client ⊆ 可见边框
/// （extended frame bounds）⊆ GetWindowRect；隐藏/最小化窗口显式拒绝，
/// 不输出错误区域。
/// </summary>
public class ScreenTargetQueryWindowContractTests
{
  [Fact]
  public void StyledWindowBoundsNestClientInsideVisibleFrameInsideWindowRect()
  {
    nint previousDpi = SetThreadDpiAwarenessContext(-4); // 与生产 App 的 PMv2 manifest 一致。
    nint window = CreateStyledWindow();
    try
    {
      ShowWindow(window, 5 /* SW_SHOW */);
      Assert.True(IsWindowVisible(window));

      PhysicalRectangle? visible = ScreenTargetQuery.TryGetVisibleFrameBounds(window);
      PhysicalRectangle? client = ScreenTargetQuery.TryGetClientBounds(window);
      Assert.NotNull(visible);
      Assert.NotNull(client);

      Assert.True(client!.Value.X >= visible!.Value.X && client.Value.Y >= visible.Value.Y &&
          client.Value.Right <= visible.Value.Right && client.Value.Bottom <= visible.Value.Bottom,
          $"client {client} must stay inside visible frame {visible}");

      Assert.True(GetWindowRect(window, out NativeRect rect));
      long windowWidth = (long)rect.Right - rect.Left;
      long windowHeight = (long)rect.Bottom - rect.Top;
      Assert.True(visible.Value.Width <= windowWidth && visible.Value.Height <= windowHeight,
          "visible frame bounds must not exceed GetWindowRect");
    }
    finally
    {
      DestroyWindow(window);
      SetThreadDpiAwarenessContext(previousDpi);
    }
  }

  [Fact]
  public void HiddenAndMinimizedWindowsAreRejectedNotClamped()
  {
    nint previousDpi = SetThreadDpiAwarenessContext(-4); // 与生产 App 的 PMv2 manifest 一致。
    nint window = CreateStyledWindow();
    try
    {
      Assert.Null(ScreenTargetQuery.TryGetVisibleFrameBounds(window));

      ShowWindow(window, 5);
      ShowWindow(window, 6 /* SW_MINIMIZE */);
      Assert.True(IsIconic(window));
      Assert.Null(ScreenTargetQuery.TryGetVisibleFrameBounds(window));
      Assert.Null(ScreenTargetQuery.TryGetClientBounds(window));
    }
    finally
    {
      DestroyWindow(window);
      SetThreadDpiAwarenessContext(previousDpi);
    }
  }

  [Fact]
  public async Task CaptureHonorsCursorFlagExactlyOnceWhenEnabled()
  {
    var overlay = new FakeCursorOverlay();
    await using var service = new ScreenCaptureService(Guid.NewGuid(), cursorOverlay: overlay);
    var bounds = new PhysicalRectangle(0, 0, 4, 4);

    CapturedFrame frame = service.Capture(bounds, TimeSpan.FromMinutes(1));
    Assert.Equal(0, overlay.Draws);


    _ = service.Capture(bounds, TimeSpan.FromMinutes(1), includeCursor: true);
    Assert.Equal(1, overlay.Draws);

  }

  private sealed class FakeCursorOverlay : IScreenCursorOverlay
  {
    public int Draws;

    public bool TryDraw(nint memoryDc, PhysicalRectangle bounds)
    {
      Draws++;
      return true;
    }
  }

  [DllImport("user32.dll")]
  private static extern nint SetThreadDpiAwarenessContext(nint context);

  private static nint CreateStyledWindow()
  {
    nint window = CreateWindowExW(
        0, "STATIC", "vibeocr-capture-contract", 0x00CF0000 /* WS_OVERLAPPEDWINDOW */,
        40, 40, 480, 320, 0, 0, 0, 0);
    Assert.NotEqual(0, window);
    return window;
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern nint CreateWindowExW(
      uint extendedStyle, string className, string windowName, uint style,
      int x, int y, int width, int height, nint parent, nint menu, nint module, nint parameter);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ShowWindow(nint window, int command);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DestroyWindow(nint window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindowVisible(nint window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsIconic(nint window);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetWindowRect(nint window, out NativeRect rect);

  [StructLayout(LayoutKind.Sequential)]
  private struct NativeRect
  {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
  }
}
