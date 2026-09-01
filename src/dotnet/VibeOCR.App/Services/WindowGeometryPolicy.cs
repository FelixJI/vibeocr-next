using System.Runtime.InteropServices;
using Windows.Graphics;

namespace VibeOCR.App.Services;

/// <summary>
/// 窗口几何策略：前端布局以逻辑像素（DIP）为基准，而 AppWindow/WM_GETMINMAXINFO
/// 使用物理像素；这里统一做 DPI 换算，并把恢复的几何约束在当前工作区内，
/// 避免高 DPI 缩放下默认窗口小于布局下限、或跨显示器恢复后窗口不可达。
/// </summary>
internal static class WindowGeometryPolicy
{
  /// <summary>标题栏至少保留的物理像素宽度，确保恢复位置始终可见可拖拽。</summary>
  private const int VisibleEdgePx = 96;

  [DllImport("user32.dll")]
  private static extern uint GetDpiForWindow(IntPtr hWnd);

  public static double GetWindowScale(IntPtr hWnd)
  {
    uint dpi = hWnd == IntPtr.Zero ? 0 : GetDpiForWindow(hWnd);
    return dpi > 0 ? dpi / 96.0 : 1.0;
  }

  public static int ScaleToPhysical(int logical, double scale) =>
    scale <= 1 ? logical : (int)Math.Ceiling(logical * scale);

  public static RectInt32 DefaultGeometry(
    RectInt32 workArea,
    double scale,
    int defaultWidth,
    int defaultHeight)
  {
    int width = Math.Min(ScaleToPhysical(defaultWidth, scale), workArea.Width);
    int height = Math.Min(ScaleToPhysical(defaultHeight, scale), workArea.Height);
    int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
    int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
    return new RectInt32(x, y, width, height);
  }

  public static WindowGeometry ClampRestored(
    WindowGeometry geometry,
    RectInt32 workArea,
    int minWidthPhysical,
    int minHeightPhysical)
  {
    int width = Math.Clamp(
      geometry.Width,
      minWidthPhysical,
      Math.Max(minWidthPhysical, workArea.Width));
    int height = Math.Clamp(
      geometry.Height,
      minHeightPhysical,
      Math.Max(minHeightPhysical, workArea.Height));
    int x = Math.Clamp(
      geometry.X,
      workArea.X - width + VisibleEdgePx,
      workArea.X + workArea.Width - VisibleEdgePx);
    int y = Math.Clamp(
      geometry.Y,
      workArea.Y,
      workArea.Y + workArea.Height - VisibleEdgePx);
    return geometry with { X = x, Y = y, Width = width, Height = height };
  }
}
