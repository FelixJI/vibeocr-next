using System.ComponentModel;
using System.Runtime.InteropServices;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

/// <summary>原生分块遮罩让选区保持真实透空；滚轮、拖动和焦点仍属于源窗口。</summary>
internal sealed class ScrollCaptureMask : IDisposable
{
  private readonly List<nint> _windows = [];
  internal bool Contains(nint handle) => _windows.Contains(handle);

  internal ScrollCaptureMask(PhysicalRectangle desktop, PhysicalRectangle bounds)
  {
    try
    {
      Add(new(desktop.X, desktop.Y, desktop.Width, bounds.Y - desktop.Y), false);
      Add(new(desktop.X, bounds.Bottom, desktop.Width, desktop.Bottom - bounds.Bottom), false);
      Add(new(desktop.X, bounds.Y, bounds.X - desktop.X, bounds.Height), false);
      Add(new(bounds.Right, bounds.Y, desktop.Right - bounds.Right, bounds.Height), false);
      Add(new(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, 1), true);
      Add(new(bounds.X - 1, bounds.Bottom, bounds.Width + 2, 1), true);
      Add(new(bounds.X - 1, bounds.Y, 1, bounds.Height), true);
      Add(new(bounds.Right, bounds.Y, 1, bounds.Height), true);
    }
    catch
    {
      Dispose();
      throw;
    }
  }

  private void Add(PhysicalRectangle rect, bool border)
  {
    if (rect.Width <= 0 || rect.Height <= 0) return;
    // Layered + transparent + noactivate: shades never intercept source input.
    nint handle = CreateWindowExW(0x080800A0, "Static", "", 0x80000000u | (border ? 6u : 4u),
        rect.X, rect.Y, rect.Width, rect.Height, 0, 0, 0, 0);
    if (handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法创建滚动截图遮罩。");
    _windows.Add(handle);
    if (!SetWindowDisplayAffinity(handle, 0x11) ||
        !SetLayeredWindowAttributes(handle, 0, border ? (byte)255 : (byte)80, 2) ||
        !SetWindowPos(handle, -1, rect.X, rect.Y, rect.Width, rect.Height, 0x0050))
      throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法安全显示滚动截图遮罩。");
  }

  public void Dispose()
  {
    foreach (nint handle in _windows) DestroyWindow(handle);
    _windows.Clear();
  }

  internal static void VerifySelfCheck()
  {
    nint foreground = GetForegroundWindow();
    var mask = new ScrollCaptureMask(new(-500, -500, 300, 300), new(-450, -450, 100, 100));
    nint[] handles = mask._windows.ToArray();
    try
    {
      if (handles.Length != 8 || GetForegroundWindow() != foreground)
        throw new InvalidOperationException("Scroll shades must preserve foreground and include all four edges.");
      var selection = new PhysicalRectangle(-450, -450, 100, 100);
      foreach (nint handle in handles)
      {
        if (!GetWindowDisplayAffinity(handle, out uint affinity) || affinity != 0x11 ||
            ((long)GetWindowLongPtrW(handle, -20) & 0x080800A0) != 0x080800A0 ||
            !GetWindowRect(handle, out NativeRect rect) ||
            SmartScreenCandidates.Intersect(selection, new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)) is not null)
          throw new InvalidOperationException("Negative-coordinate shades must be input-transparent, excluded from capture and outside the selection.");
      }
    }
    finally { mask.Dispose(); }
    mask.Dispose();
    if (handles.Any(IsWindow)) throw new InvalidOperationException("Scroll shade HWNDs leaked after disposal.");
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct NativeRect { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();
  [DllImport("user32.dll")]
  private static extern nint GetWindowLongPtrW(nint handle, int index);
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetWindowRect(nint handle, out NativeRect rect);
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindow(nint handle);

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern nint CreateWindowExW(uint extendedStyle, string className, string title,
      uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetLayeredWindowAttributes(nint handle, uint key, byte alpha, uint flags);
  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DestroyWindow(nint handle);
}
