using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class SmartScreenCandidatesTests
{
    [Fact]
    public void HitUsesFrozenFrontToBackOrderAndNegativeDesktopCoordinates()
    {
        var desktop = new PhysicalRectangle(-1600, -200, 3520, 1280);
        var front = new SmartScreenCandidates.Window(2, new(-500, -100, 300, 200));
        var back = new SmartScreenCandidates.Window(1, new(-600, -150, 500, 300));
        var snapshot = new SmartScreenCandidates(desktop, [front, back]);

        Assert.Same(front, snapshot.Hit(new(-450, -50)));
        Assert.Same(back, snapshot.Hit(new(-550, -120)));
        Assert.Null(snapshot.Hit(new(1900, 1000)));
    }

    [Fact]
    public void ClipAndProjectUsePhysicalPixelsWithoutDpiScaling()
    {
        var desktop = new PhysicalRectangle(-1600, -200, 3520, 1280);
        var window = new SmartScreenCandidates.Window(5, new(-100, -50, 300, 200));
        var snapshot = new SmartScreenCandidates(desktop, [window]);

        PhysicalRectangle? clipped = snapshot.ClipToCapture(new(-150, -80, 260, 180), window);
        Assert.Equal(new PhysicalRectangle(-100, -50, 210, 150), clipped);
        Assert.Equal(new PhysicalRectangle(1500, 150, 210, 150),
            snapshot.ToLocal(clipped!.Value));
        Assert.Null(snapshot.ClipToCapture(new(300, 300, 30, 30), window));
    }

    [Fact]
    public void MovedOrChangedDesktopInvalidatesFrozenWindow()
    {
        var desktop = new PhysicalRectangle(-1600, 0, 3520, 1080);
        var window = new SmartScreenCandidates.Window(7, new(-400, 100, 500, 400));
        var snapshot = new SmartScreenCandidates(desktop, [window]);

        Assert.True(snapshot.IsCurrent(window, desktop, _ => window.Bounds));
        Assert.False(snapshot.IsCurrent(window, desktop,
            _ => window.Bounds with { X = -399 }));
        Assert.False(snapshot.IsCurrent(window, desktop with { Width = 3519 },
            _ => window.Bounds));
        Assert.False(snapshot.IsCurrent(window, desktop, _ => null));
    }

    [Fact]
    public void ConfirmationRejectsDisappearedOrMovedControlAndChangedParentPath()
    {
        SmartControlCandidate parent = new(new(-400, 100, 500, 400), 50033, 1);
        SmartControlCandidate child = new(new(-300, 200, 100, 30), 50000, 2);
        Assert.True(SmartControlCandidate.SamePathThrough(
            [parent, child], [parent, child], 1));
        Assert.False(SmartControlCandidate.SamePathThrough(
            [parent, child], [parent], 1));
        Assert.False(SmartControlCandidate.SamePathThrough(
            [parent, child], [parent, child with { Bounds = child.Bounds with { X = -299 } }], 1));
        Assert.False(SmartControlCandidate.SamePathThrough(
            [parent, child], [parent with { Bounds = parent.Bounds with { Width = 499 } }, child], 1));
    }

    [Fact]
    public void RectangleHitTestingRejectsTransparentOrUnknownLayeredWindows()
    {
        const long layered = 0x00080000;
        const long transparent = 0x00000020;
        const uint alpha = 0x00000002;
        const uint colorKey = 0x00000001;

        Assert.True(SmartScreenCandidates.CanUseRectangularHitTest(0, false, 0, 0));
        Assert.True(SmartScreenCandidates.CanUseRectangularHitTest(layered, true, 255, alpha));
        Assert.False(SmartScreenCandidates.CanUseRectangularHitTest(layered, false, 0, 0));
        Assert.False(SmartScreenCandidates.CanUseRectangularHitTest(layered, true, 0, alpha));
        Assert.False(SmartScreenCandidates.CanUseRectangularHitTest(layered, true, 128, alpha));
        Assert.False(SmartScreenCandidates.CanUseRectangularHitTest(layered, true, 255,
            alpha | colorKey));
        Assert.False(SmartScreenCandidates.CanUseRectangularHitTest(layered | transparent,
            true, 255, alpha));
    }
}
