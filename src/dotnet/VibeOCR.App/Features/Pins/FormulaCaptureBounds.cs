using Windows.Graphics.Imaging;

namespace VibeOCR.App.Features.Pins;

internal static class FormulaCaptureBounds
{
  internal static BitmapBounds Compute(double x, double y, double width, double height,
    double viewportWidth, double viewportHeight, double ratio, uint bitmapWidth, uint bitmapHeight)
  {
    double[] values = [x, y, width, height, viewportWidth, viewportHeight, ratio];
    if (values.Any(value => !double.IsFinite(value)) || x < 0 || y < 0 || width <= 0 || height <= 0 ||
      viewportWidth <= 0 || viewportHeight <= 0 || ratio is < 0.5 or > 8 ||
      x + width > viewportWidth || y + height > viewportHeight || bitmapWidth == 0 || bitmapHeight == 0 ||
      (long)bitmapWidth * bitmapHeight > 16_000_000 ||
      Math.Abs(bitmapWidth - viewportWidth * ratio) > 1 ||
      Math.Abs(bitmapHeight - viewportHeight * ratio) > 1)
      throw new InvalidOperationException("Formula capture geometry does not match the decoded bitmap.");
    // Calibrate with the actual decoded raster, never the renderer's DPR alone.
    double sx = bitmapWidth / viewportWidth, sy = bitmapHeight / viewportHeight;
    uint left = checked((uint)Math.Floor(x * sx)), top = checked((uint)Math.Floor(y * sy));
    uint right = checked((uint)Math.Ceiling((x + width) * sx));
    uint bottom = checked((uint)Math.Ceiling((y + height) * sy));
    if (right <= left || bottom <= top || right > bitmapWidth || bottom > bitmapHeight)
      throw new InvalidOperationException("Formula crop is outside the captured bitmap.");
    return new BitmapBounds { X = left, Y = top, Width = right - left, Height = bottom - top };
  }
}
