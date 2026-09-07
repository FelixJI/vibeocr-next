using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ScreenSelectionSessionTests
{
  [Fact]
  public void ReleaseKeepsSelectionForConfirmationAndBackAllowsReselectionBeforeExit()
  {
    var session = new ScreenSelectionSession(200, 100);
    session.Begin(new(10, 10), 2);
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
    session.Begin(new(10, 10), 2);
    session.End(new(50, 40));
    session.Begin(new(30, 25), 2);
    session.Move(new(35, 30));
    session.End(new(40, 35));
    Assert.Equal(new PhysicalRectangle(20, 20, 40, 30), session.Selection);
    session.Begin(new(60, 50), 2);
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
    session.Begin(new(10, 10), 2);
    session.End(new(50, 40));
    session.Begin(new(30, 25), 2);
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
    session.Begin(new(10, 10), 2);
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
    session.Begin(new(10, 10), 2);
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
    session.Begin(new(50, 40), 2);
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
    session.Begin(new(10, 10), 2);
    session.End(new(10, 10));
    Assert.False(session.CanConfirm);
    Assert.False(session.CanUndo);
  }
}
