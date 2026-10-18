using VibeOCR.App.Features.Pins;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class FormulaCaptureBoundsTests
{
  [Theory]
  [InlineData(1, 800, 600)]
  [InlineData(1.25, 1000, 750)]
  [InlineData(1.5, 1200, 900)]
  [InlineData(2, 1600, 1200)]
  public void CssRectangleUsesActualDecodedRaster(double ratio, uint width, uint height)
  {
    var bounds = FormulaCaptureBounds.Compute(10, 12, 100, 40, 800, 600, ratio, width, height);
    Assert.Equal((uint)(10 * ratio), bounds.X);
    Assert.Equal((uint)(12 * ratio), bounds.Y);
    Assert.Equal((uint)(Math.Ceiling(110 * ratio) - Math.Floor(10 * ratio)), bounds.Width);
    Assert.Equal((uint)(40 * ratio), bounds.Height);
  }

  [Theory]
  [InlineData(-1, 0, 20, 20, 1, 800)]
  [InlineData(799, 0, 20, 20, 1, 800)]
  [InlineData(0, 0, 0, 20, 1, 800)]
  [InlineData(0, 0, 20, 20, 2, 800)]
  [InlineData(double.NaN, 0, 20, 20, 1, 800)]
  public void InvalidOrMismatchedGeometryNeverProducesACrop(double x, double y, double w, double h, double ratio, uint bitmapWidth)
  {
    Assert.Throws<InvalidOperationException>(() => FormulaCaptureBounds.Compute(x, y, w, h, 800, 600, ratio, bitmapWidth, 600));
  }
}
