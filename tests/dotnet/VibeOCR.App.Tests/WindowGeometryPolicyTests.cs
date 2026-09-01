using VibeOCR.App.Services;
using Windows.Graphics;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WindowGeometryPolicyTests
{
    [Theory]
    [InlineData(1024, 1.0, 1024)]
    [InlineData(1024, 1.25, 1280)]
    [InlineData(1024, 1.5, 1536)]
    [InlineData(720, 1.75, 1260)]
    [InlineData(1024, 2.0, 2048)]
    public void ScaleToPhysicalConvertsLogicalFloorToPixels(
        int logical,
        double scale,
        int expected)
    {
        Assert.Equal(expected, WindowGeometryPolicy.ScaleToPhysical(logical, scale));
    }

    [Fact]
    public void DefaultGeometryScalesToPhysicalPixelsAndCentersInWorkArea()
    {
        RectInt32 workArea = new(0, 0, 3440, 1440);

        RectInt32 actual = WindowGeometryPolicy.DefaultGeometry(
            workArea,
            scale: 1.5,
            defaultWidth: 1280,
            defaultHeight: 800);

        // 1280x800 逻辑像素在 150% 缩放下必须是 1920x1200 物理像素，
        // 否则窗口逻辑尺寸会低于前端 1024x720 的布局下限。
        Assert.Equal(new RectInt32(760, 120, 1920, 1200), actual);
    }

    [Fact]
    public void DefaultGeometryClampsToSmallerWorkArea()
    {
        RectInt32 workArea = new(0, 0, 1024, 600);

        RectInt32 actual = WindowGeometryPolicy.DefaultGeometry(
            workArea,
            scale: 1.0,
            defaultWidth: 1280,
            defaultHeight: 800);

        Assert.Equal(new RectInt32(0, 0, 1024, 600), actual);
    }

    [Fact]
    public void ClampRestoredEnforcesScaledMinimumSize()
    {
        RectInt32 workArea = new(0, 0, 1920, 1080);

        WindowGeometry actual = WindowGeometryPolicy.ClampRestored(
            new WindowGeometry(10, 20, 900, 500, IsMaximized: false),
            workArea,
            minWidthPhysical: 1536,
            minHeightPhysical: 1080);

        Assert.Equal(new WindowGeometry(10, 20, 1536, 1080, false), actual);
    }

    [Fact]
    public void ClampRestoredPullsOffScreenPositionIntoWorkArea()
    {
        RectInt32 workArea = new(100, 50, 1920, 1080);

        WindowGeometry actual = WindowGeometryPolicy.ClampRestored(
            new WindowGeometry(-4000, 3000, 1280, 800, IsMaximized: false),
            workArea,
            minWidthPhysical: 1024,
            minHeightPhysical: 720);

        // 跨显示器/DPI 变化后恢复的窗口必须保留可拖拽的可见边缘。
        Assert.InRange(actual.X, workArea.X - actual.Width + 96, workArea.X + workArea.Width - 96);
        Assert.InRange(actual.Y, workArea.Y, workArea.Y + workArea.Height - 96);
    }

    [Fact]
    public void ClampRestoredKeepsReachableGeometryAndMaximizedFlag()
    {
        RectInt32 workArea = new(0, 0, 1920, 1080);
        WindowGeometry saved = new(64, 32, 1440, 900, IsMaximized: true);

        WindowGeometry actual = WindowGeometryPolicy.ClampRestored(
            saved,
            workArea,
            minWidthPhysical: 1024,
            minHeightPhysical: 720);

        Assert.Equal(saved, actual);
    }
}
