using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class ScreenEdgeGeometryTests
{
    private static readonly PhysicalRectangle Monitor = new(100, 200, 1920, 1080);

    [Fact]
    public void SensorRectangleSpansEachEdge()
    {
        Assert.Equal(
            new PhysicalRectangle(100, 200, 1920, 2),
            ScreenEdgeGeometry.GetSensorRectangle(Monitor, ScreenEdge.Top));
        Assert.Equal(
            new PhysicalRectangle(100, 1278, 1920, 2),
            ScreenEdgeGeometry.GetSensorRectangle(Monitor, ScreenEdge.Bottom));
        Assert.Equal(
            new PhysicalRectangle(100, 200, 2, 1080),
            ScreenEdgeGeometry.GetSensorRectangle(Monitor, ScreenEdge.Left));
        Assert.Equal(
            new PhysicalRectangle(2018, 200, 2, 1080),
            ScreenEdgeGeometry.GetSensorRectangle(Monitor, ScreenEdge.Right));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SensorRectangleRejectsNonPositiveThickness(int thickness)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScreenEdgeGeometry.GetSensorRectangle(Monitor, ScreenEdge.Top, thickness));
    }

    [Fact]
    public void SensorRectangleClampsThicknessToMonitor()
    {
        var tiny = new PhysicalRectangle(0, 0, 1, 1);
        Assert.Equal(
            new PhysicalRectangle(0, 0, 1, 1),
            ScreenEdgeGeometry.GetSensorRectangle(tiny, ScreenEdge.Top, thickness: 8));
        Assert.Equal(
            new PhysicalRectangle(0, 0, 1, 1),
            ScreenEdgeGeometry.GetSensorRectangle(tiny, ScreenEdge.Left, thickness: 8));
    }

    [Fact]
    public void DockedRectangleCentersAgainstEachEdge()
    {
        Assert.Equal(
            new PhysicalRectangle(960, 200, 200, 40),
            ScreenEdgeGeometry.GetDockedToolbarRectangle(Monitor, ScreenEdge.Top, 200, 40));
        Assert.Equal(
            new PhysicalRectangle(960, 1240, 200, 40),
            ScreenEdgeGeometry.GetDockedToolbarRectangle(Monitor, ScreenEdge.Bottom, 200, 40));
        Assert.Equal(
            new PhysicalRectangle(100, 720, 200, 40),
            ScreenEdgeGeometry.GetDockedToolbarRectangle(Monitor, ScreenEdge.Left, 200, 40));
        Assert.Equal(
            new PhysicalRectangle(1820, 720, 200, 40),
            ScreenEdgeGeometry.GetDockedToolbarRectangle(Monitor, ScreenEdge.Right, 200, 40));
    }

    [Fact]
    public void DockedRectangleClampsOversizedToolbarIntoMonitor()
    {
        Assert.Equal(
            Monitor,
            ScreenEdgeGeometry.GetDockedToolbarRectangle(Monitor, ScreenEdge.Top, 5000, 2000));
    }

    [Fact]
    public void SnapEdgePicksNearestEdgeWithinThreshold()
    {
        var nearTop = new PhysicalRectangle(500, 204, 200, 40);
        Assert.Equal(
            ScreenEdge.Top,
            ScreenEdgeGeometry.FindSnapEdge(nearTop, Monitor, threshold: 24));

        var nearBottom = new PhysicalRectangle(500, 1216, 200, 40);
        Assert.Equal(
            ScreenEdge.Bottom,
            ScreenEdgeGeometry.FindSnapEdge(nearBottom, Monitor, threshold: 24));

        var nearRight = new PhysicalRectangle(1804, 500, 200, 40);
        Assert.Equal(
            ScreenEdge.Right,
            ScreenEdgeGeometry.FindSnapEdge(nearRight, Monitor, threshold: 24));
    }

    [Fact]
    public void SnapEdgeReturnsNullWhenAllEdgesBeyondThreshold()
    {
        var floating = new PhysicalRectangle(700, 500, 200, 40);
        Assert.Null(ScreenEdgeGeometry.FindSnapEdge(floating, Monitor, threshold: 24));
    }

    [Fact]
    public void SnapEdgeTreatsOvershootAsZeroGap()
    {
        // 越过边的窗口（拖动到屏幕外）仍吸附到最近边。
        var overshoot = new PhysicalRectangle(500, 90, 200, 40);
        Assert.Equal(
            ScreenEdge.Top,
            ScreenEdgeGeometry.FindSnapEdge(overshoot, Monitor, threshold: 24));
    }

    [Fact]
    public void SnapEdgeRejectsNegativeThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScreenEdgeGeometry.FindSnapEdge(Monitor, Monitor, threshold: -1));
    }
}
