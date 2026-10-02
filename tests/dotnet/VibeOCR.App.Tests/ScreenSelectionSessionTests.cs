using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ScreenSelectionSessionTests
{
  private sealed class StitchedRegionPicker : IScreenRegionPicker
  {
    public Task<ScreenRegionSelection?> PickAsync(CancellationToken cancellationToken) =>
      Task.FromResult<ScreenRegionSelection?>(new ScreenRegionSelection(
        new PhysicalRectangle(10, 20, 2, 2), Enumerable.Range(0, 40).Select(i => (byte)i).ToArray(), 8, 5));
  }

  [Fact]
  public async Task ScrollingInputEncodesTheWholeStitchedHeight()
  {
    var inputService = new InputService(static () => 0, scrollingRegionPicker: new StitchedRegionPicker());
    RecognitionInput input = Assert.IsType<RecognitionInput>(
      await inputService.CaptureScrollingScreenAsync(TestContext.Current.CancellationToken));
    Assert.Equal("scrolling-screenshot", input.Origin);
    Assert.Equal("image/bmp", input.MediaType);
    Assert.Equal(94, input.Data.Length);
    Assert.Equal(-5, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(input.Data.AsSpan(22)));
    Assert.Equal(Enumerable.Range(0, 40).Select(i => (byte)i), input.Data.Skip(54));
  }

  [Fact]
  public void ReleaseKeepsSelectionForConfirmationAndBackAllowsReselectionBeforeExit()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(10, 10));
    session.Move(new(50, 40));
    Assert.False(session.CanConfirm);
    session.End(new(50, 40));
    Assert.True(session.CanConfirm);
    Assert.Equal(new PhysicalRectangle(10, 10, 40, 30), session.Selection);
    Assert.False(session.Back());
    Assert.Null(session.Selection);
    Assert.False(session.CanConfirm);
    Assert.True(session.Back());
  }

  [Fact]
  public void DragMoveAndResizeAreSeparateUndoableOperations()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(10, 10));
    session.End(new(50, 40));
    session.Begin(new(30, 25));
    session.Move(new(35, 30));
    session.End(new(40, 35));
    Assert.Equal(new PhysicalRectangle(20, 20, 40, 30), session.Selection);
    session.Begin(new(60, 50));
    session.End(new(80, 70));
    Assert.Equal(new PhysicalRectangle(20, 20, 60, 50), session.Selection);
    session.Undo();
    Assert.Equal(new PhysicalRectangle(20, 20, 40, 30), session.Selection);
    session.Undo();
    Assert.Equal(new PhysicalRectangle(10, 10, 40, 30), session.Selection);
    session.Redo();
    Assert.Equal(new PhysicalRectangle(20, 20, 40, 30), session.Selection);
  }

  [Fact]
  public void BackDuringDragRollsBackWithoutExitingOrAddingHistory()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(10, 10));
    session.End(new(50, 40));
    session.Begin(new(30, 25));
    session.Move(new(80, 80));
    Assert.False(session.Back());
    Assert.False(session.IsDragging);
    Assert.Equal(new PhysicalRectangle(10, 10, 40, 30), session.Selection);
    session.Undo();
    Assert.Null(session.Selection);
  }

  [Fact]
  public void CaptureLossRestoresPreviousSelectionAndCannotConfirmActiveDrag()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(10, 10));
    session.Move(new(50, 40));
    Assert.False(session.CanConfirm);
    session.CancelDrag();
    Assert.Null(session.Selection);
    Assert.False(session.CanUndo);
  }

  [Fact]
  public void ReselectCanBeUndoneAndNewEditDiscardsRedo()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(10, 10));
    session.End(new(50, 40));
    session.Back();
    session.Undo();
    Assert.True(session.CanConfirm);
    session.Adjust(1, 0, false);
    Assert.False(session.CanRedo);
    Assert.Equal(11, session.Selection!.Value.X);
  }

  [Fact]
  public void ReverseDragAndAdjustmentsStayInsidePhysicalDesktop()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(50, 40));
    session.End(new(-20, -10));
    Assert.Equal(new PhysicalRectangle(0, 0, 50, 40), session.Selection);
    session.Adjust(500, 500, false);
    Assert.Equal(new PhysicalRectangle(150, 60, 50, 40), session.Selection);
    session.Adjust(-500, -500, true);
    Assert.Equal(new PhysicalRectangle(150, 60, 1, 1), session.Selection);
    session.Adjust(500, 500, true);
    Assert.Equal(new PhysicalRectangle(150, 60, 50, 40), session.Selection);
  }

  [Fact]
  public void ClickWithoutAreaCannotBeSubmitted()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(10, 10));
    session.End(new(10, 10));
    Assert.False(session.CanConfirm);
    Assert.False(session.CanUndo);
  }

  [Fact]
  public void HoverPreviewCyclesWithoutHistoryAndManualDragOverridesIt()
  {
    var session = new ScreenSelectionSession(300, 200);
    session.SetPreview([new(10, 10, 200, 100), new(20, 20, 60, 40)]);
    Assert.Equal(new PhysicalRectangle(10, 10, 200, 100), session.ActiveSelection);
    session.CyclePreview();
    Assert.Equal(new PhysicalRectangle(20, 20, 60, 40), session.ActiveSelection);
    Assert.True(session.CanConfirm);
    Assert.False(session.CanUndo);

    session.Begin(new(150, 150));
    session.End(new(200, 180));
    Assert.Equal(new PhysicalRectangle(150, 150, 50, 30), session.ActiveSelection);
    Assert.True(session.CanUndo);
    session.Undo();
    Assert.Null(session.ActiveSelection);
  }

  [Fact]
  public void BackDropsPreviewBeforeExiting()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.SetPreview([new(10, 10, 50, 40)]);
    Assert.False(session.Back());
    Assert.Null(session.ActiveSelection);
    Assert.True(session.ManualOnly);
    session.SetPreview([new(20, 20, 30, 20)]);
    Assert.Null(session.ActiveSelection);
    Assert.True(session.Back());
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void ClickConfirmsTheSameSelectionOnceWithoutEditingHistory(bool preview)
  {
    var session = new ScreenSelectionSession(200, 100);
    var rect = new PhysicalRectangle(10, 10, 80, 60);
    if (preview) session.SetPreview([rect]);
    else { session.Begin(new(10, 10)); session.End(new(90, 70)); }
    bool undoBefore = session.CanUndo;
    session.Begin(new(40, 40));
    session.Move(new(43, 42));
    Assert.True(session.IsPointerActive);
    Assert.False(session.IsDragging);
    Assert.False(session.CanConfirm);
    Assert.Equal(rect, session.ActiveSelection);
    Assert.True(session.End(new(43, 42)));
    Assert.False(session.End(new(43, 42)));
    Assert.Equal(rect, session.ActiveSelection);
    Assert.Equal(undoBefore, session.CanUndo);
    Assert.Equal(preview, session.Selection is null);
  }

  [Theory]
  [InlineData(1.0)]
  [InlineData(1.25)]
  [InlineData(1.5)]
  [InlineData(2.0)]
  public void DipThresholdUsesUnroundedPointerAndFirstDragNeverConfirms(double scale)
  {
    var session = new ScreenSelectionSession(400, 300);
    session.Begin(new(20, 20), new(20 / scale, 20 / scale), scale, scale);
    session.Move(new((int)Math.Round(20 + 4 * scale), 20), new(20 / scale + 4, 20 / scale));
    Assert.False(session.IsDragging);
    Assert.Null(session.Selection);
    session.Move(new((int)Math.Round(20 + 4.01 * scale), 20), new(20 / scale + 4.01, 20 / scale));
    Assert.True(session.IsDragging);
    Assert.False(session.End(new(20, 20), new(20 / scale, 20 / scale)));
    Assert.False(session.CanConfirm);
    Assert.False(session.CanUndo);
  }

  [Fact]
  public void DiagonalThresholdAndManualMoveRemainACompletedEditOnRelease()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(10, 10));
    session.Move(new(13, 13));
    Assert.True(session.IsDragging);
    Assert.False(session.End(new(90, 70)));
    session.Begin(new(40, 40));
    session.Move(new(45, 40));
    Assert.True(session.IsDragging);
    Assert.False(session.End(new(40, 40)));
    Assert.Equal(new PhysicalRectangle(10, 10, 80, 60), session.Selection);
    session.Undo();
    Assert.Null(session.Selection);
  }

  [Fact]
  public void HandleClickAndOutsideClickCannotConfirmAndOffsetResizeDoesNotJump()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(20, 20));
    session.End(new(100, 80));
    session.Begin(new(103, 83));
    Assert.Equal(7, session.ActiveHandle);
    Assert.False(session.End(new(103, 83)));
    session.Begin(new(103, 83));
    session.Move(new(113, 93));
    Assert.Equal(new PhysicalRectangle(20, 20, 90, 70), session.Selection);
    Assert.Equal(new PhysicalPoint(109, 89), session.SamplePoint(new(113, 93), -1));
    Assert.False(session.End(new(113, 93)));
    session.Begin(new(150, 50));
    Assert.False(session.End(new(150, 50)));
    Assert.Equal(new PhysicalRectangle(20, 20, 90, 70), session.Selection);
  }

  [Theory]
  [InlineData(1.0, 1.0)]
  [InlineData(1.25, 1.5)]
  [InlineData(2.0, 2.0)]
  public void EightHandlesUseNearestDipDistanceAndSameExclusiveSampling(double sx, double sy)
  {
    var session = new ScreenSelectionSession(200, 100);
    var rect = new PhysicalRectangle(20, 20, 80, 60);
    session.SetPreview([rect]);
    (double X, double Y)[] anchors = ScreenSelectionSession.HandlePoints(rect);
    PhysicalPoint[] samples = [new(20, 20), new(60, 20), new(99, 20), new(20, 50),
      new(99, 50), new(20, 79), new(60, 79), new(99, 79)];
    for (int i = 0; i < anchors.Length; i++)
    {
      var pointer = new LogicalPoint(anchors[i].X / sx + 3, anchors[i].Y / sy + 3);
      int hit = session.HitHandle(pointer, sx, sy);
      Assert.Equal(i, hit);
      Assert.Equal(samples[i], session.SamplePoint(new(0, 0), hit));
    }
    Assert.Equal(-1, session.HitHandle(new(120 / sx, 50 / sy), sx, sy));
    Assert.Equal(new PhysicalPoint(120, 50), session.SamplePoint(new(120, 50), -1));
    Assert.Equal(rect, session.ActiveSelection);
  }

  [Fact]
  public void OverlappingTinyHandlesChooseNearestThenStableOrderAndClampPixels()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.SetPreview([new(199, 99, 1, 1)]);
    Assert.Equal(0, session.HitHandle(new(199.25, 99.25)));
    Assert.Equal(1, session.HitHandle(new(199.5, 99)));
    Assert.Equal(7, session.HitHandle(new(200, 100)));
    for (int i = 0; i < 8; i++)
      Assert.Equal(new PhysicalPoint(199, 99), session.SamplePoint(new(200, 100), i));
    Assert.Equal(new PhysicalPoint(199, 0), session.SamplePoint(new(300, -5), -1));
  }

  [Fact]
  public void PendingCandidateFreezesCyclingAndCaptureLossCannotRequestConfirmation()
  {
    var session = new ScreenSelectionSession(200, 100);
    var first = new PhysicalRectangle(10, 10, 80, 60);
    session.SetPreview([first, new(20, 20, 40, 30)]);
    session.Begin(new(40, 40));
    session.CyclePreview();
    session.SetPreview([new(0, 0, 200, 100)]);
    Assert.Equal(first, session.ActiveSelection);
    session.CancelDrag();
    Assert.False(session.IsPointerActive);
    Assert.Equal(first, session.ActiveSelection);
    Assert.False(session.End(new(40, 40)));
    Assert.False(session.CanUndo);
    session.Begin(new(90, 70));
    session.Move(new(100, 80));
    Assert.Equal(new PhysicalRectangle(10, 10, 90, 70), session.Selection);
    session.CancelDrag();
    Assert.Null(session.ActiveSelection);
    Assert.False(session.CanUndo);
  }
}
