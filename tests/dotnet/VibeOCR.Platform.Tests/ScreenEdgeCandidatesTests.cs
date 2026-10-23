using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class ScreenEdgeCandidatesTests
{
  private const int Width = 360, Height = 240, Stride = Width * 4 + 16;

  private static void Paint(byte[] pixels, PhysicalRectangle rect, byte value, bool outline = false)
  {
    for (int y = rect.Y; y < rect.Bottom; y++)
      for (int x = rect.X; x < rect.Right; x++)
      {
        if (outline && x != rect.X && x != rect.Right - 1 && y != rect.Y && y != rect.Bottom - 1) continue;
        int at = y * Stride + x * 4;
        pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
      }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void DetectsFilledAndOutlinedRegionsWithoutWindowMetadata(bool outline)
  {
    byte[] pixels = new byte[Stride * Height];
    var region = new PhysicalRectangle(40, 30, 240, 160);
    Paint(pixels, region, 220, outline);
    IReadOnlyList<PhysicalRectangle> found = ScreenEdgeCandidates.Find(pixels, Width, Height, Stride, new(150, 100));
    Assert.Equal(region, found.First());
    Assert.Equal(region, ScreenEdgeCandidates.Find(pixels, Width, Height, Stride, new(43, 33)).First());
  }

  [Fact]
  public void InteriorShortLinesDoNotHideTheEnclosingBoundary()
  {
    byte[] pixels = new byte[Stride * Height];
    var region = new PhysicalRectangle(40, 30, 280, 180);
    Paint(pixels, region, 220);
    for (int y = 60; y < 180; y += 20) Paint(pixels, new(80, y, 200, 2), 40);
    Assert.Contains(region, ScreenEdgeCandidates.Find(pixels, Width, Height, Stride, new(150, 130)));
  }
  [Fact]
  public void PrefersInnerRegionAndRejectsOpenLinesAndFlatBackground()
  {
    byte[] pixels = new byte[Stride * Height];
    Assert.Empty(ScreenEdgeCandidates.Find(pixels, Width, Height, Stride, new(150, 100)));
    Paint(pixels, new(20, 20, 300, 200), 180);
    Paint(pixels, new(80, 60, 160, 100), 40);
    Assert.Equal(new PhysicalRectangle(80, 60, 160, 100),
      ScreenEdgeCandidates.Find(pixels, Width, Height, Stride, new(150, 100))[0]);
    Array.Clear(pixels);
    Paint(pixels, new(40, 30, 240, 1), 220);
    Paint(pixels, new(40, 190, 240, 1), 220);
    Assert.Empty(ScreenEdgeCandidates.Find(pixels, Width, Height, Stride, new(150, 100)));
    Assert.Empty(ScreenEdgeCandidates.Find(pixels, Width, Height, Stride, new(-1, 100)));
    Assert.Empty(ScreenEdgeCandidates.Find([0], Width, Height, Stride, new(150, 100)));
  }
}
