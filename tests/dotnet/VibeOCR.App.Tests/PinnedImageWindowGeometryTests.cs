using VibeOCR.App.Features.Recognition;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 钉图窗口纯几何策略：原大小客户区换算（1:1 像素 + 工作区钳制）、
/// 缩放钳制。均为无 UI 纯函数，窗口行为与 1:1 像素比由
/// text-selection-e2e 自检在真实 WinUI 窗口上验证。
/// </summary>
public sealed class PinnedImageWindowGeometryTests
{
  [Theory]
  [InlineData(800, 600, 1920, 1040, 800, 600)]
  [InlineData(1, 1, 1920, 1040, 1, 1)]
  [InlineData(120, 80, 1920, 1040, 120, 80)]
  [InlineData(3000, 2000, 1920, 1040, 1920, 1040)]
  [InlineData(3000, 200, 1920, 1040, 1920, 200)]
  [InlineData(300, 2000, 1920, 1040, 300, 1040)]
  [InlineData(0, 0, 1920, 1040, 1, 1)]
  [InlineData(800, 600, 0, 0, 1, 1)]
  public void OriginalClientSizeMatchesImagePixelsOrWorkArea(
    int imageWidth, int imageHeight, int maxWidth, int maxHeight,
    int expectedWidth, int expectedHeight)
  {
    (int width, int height) = PinnedImageWindow.ComputeOriginalClientSize(
      imageWidth, imageHeight, maxWidth, maxHeight);
    Assert.Equal(expectedWidth, width);
    Assert.Equal(expectedHeight, height);
  }

  [Theory]
  [InlineData(0.25, 0.25)]
  [InlineData(0.95, 0.95)]
  [InlineData(1, 1)]
  [InlineData(1.1, 1.1)]
  [InlineData(4, 4)]
  [InlineData(0.1, 0.25)]
  [InlineData(9, 4)]
  public void ZoomStaysWithinPinnedRange(double input, double expected)
  {
    Assert.Equal(expected, PinnedImageWindow.ClampZoom(input));
  }

}
