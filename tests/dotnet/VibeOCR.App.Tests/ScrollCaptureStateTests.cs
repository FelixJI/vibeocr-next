using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ScrollCaptureStateTests
{
  [Fact]
  public async Task OverlappingMotionKeepsExactPixelsAndFinishRequiresStableSample()
  {
    using var state = new ScrollCaptureState(CancellationToken.None);
    state.Start(ScrollCaptureDirection.Down, false, 0, 0);
    CapturedFrame initial = Frame(0);
    state.ObserveFrame(initial);
    Assert.Equal(ScrollCapturePhase.WaitingForStability, state.Phase);
    Array.Clear(initial.Pixels);

    Assert.Equal(ScrollCaptureUpdate.FirstAccepted, state.ObserveFrame(Frame(0)).Update);
    Assert.Equal(ScrollCapturePhase.Capturing, state.Phase);
    Assert.Equal(ScrollCaptureUpdate.None, state.ObserveFrame(Frame(0)).Update);
    Assert.Equal(1, state.Stitcher!.FrameCount);
    Assert.Equal(ScrollCaptureUpdate.Accepted, state.ObserveFrame(Frame(40)).Update);
    Assert.Equal(ScrollCaptureUpdate.Accepted, state.ObserveFrame(Frame(80)).Update);
    Assert.True(state.CanPresentCaptureProgress);
    Assert.True(state.TryRequestFinish());
    Assert.False(state.CanPresentCaptureProgress);
    Assert.True(state.BeginFinishSampling());
    state.ObserveFrame(Frame(80));
    Assert.False(state.CanPublishFinishedSample);
    state.ObserveFrame(Frame(80));
    Assert.True(state.PublishFinishedSample(() => { }));
    Assert.False(state.PublishFinishedSample(() => { }));
    Assert.Equal(ScrollCapturePhase.Completed, state.Phase);
    Assert.Equal(Frame(0, height: 176).Pixels, Assert.IsType<CapturedFrame>(await state.Completion).Pixels);
  }

  [Fact]
  public async Task GapBlocksFinishUntilTheTimedSegmentIsExplicitlyConfirmed()
  {
    long now = 0;
    using var state = new ScrollCaptureState(CancellationToken.None, () => now);
    state.Start(ScrollCaptureDirection.Down, false, 0, 0);
    state.ObserveFrame(Frame(0));
    state.ObserveFrame(Frame(0));
    state.ObserveFrame(Frame(160));
    Assert.Equal(ScrollCaptureUpdate.OverlapMissing, state.ObserveFrame(Frame(160)).Update);
    Assert.False(state.TryRequestFinish());
    now = ScrollCaptureState.NoProgressTimeoutMs - 1;
    Assert.Equal(ScrollCaptureUpdate.None, state.EndNoProgress().Update);
    now++;
    ScrollCaptureChange change = state.EndNoProgress();
    Assert.Equal(ScrollCaptureUpdate.SegmentReview, change.Update);
    Assert.Equal(ScrollCapturePhase.ReviewingSegment, state.Phase);
    Assert.False(state.Completion.IsCompleted);
    Assert.False(state.ConfirmSegment(new ScrollCaptureReview(Segment: Frame(160))));
    Assert.True(state.ConfirmSegment(change.Review!));
    Assert.Equal(Frame(0).Pixels, Assert.IsType<CapturedFrame>(await state.Completion).Pixels);
  }

  [Fact]
  public void ReviewResumeRefreshesFiveSecondWindowAndRejectsOldReviewIdentity()
  {
    long now = 0;
    using var state = new ScrollCaptureState(CancellationToken.None, () => now);
    state.Start(ScrollCaptureDirection.Down, false, 0, 0);
    state.ObserveFrame(Frame(32));
    state.ObserveFrame(Frame(32));
    state.ObserveFrame(Frame(12));
    ScrollCaptureReview reverse = state.ObserveFrame(Frame(12)).Review!;
    now = ScrollCaptureState.NoProgressTimeoutMs + 1;
    Assert.False(state.NoProgressExpired);
    Assert.True(state.ResumeReview(reverse));
    ScrollCaptureReview later = state.OpenReview(ScrollCaptureUpdate.AutomaticReview).Review!;
    Assert.False(state.IsCurrentReview(reverse));
    Assert.False(state.ResumeReview(reverse));
    Assert.True(state.ResumeReview(later));
    Assert.Equal(now, state.ElapsedMilliseconds);
    Assert.Equal(ScrollCapturePhase.WaitingForStability, state.Phase);
    now += ScrollCaptureState.DynamicContentTimeoutMs;
    state.ObserveFrame(Frame(160));
    Assert.Null(state.Failure);
    now++;
    state.ObserveFrame(Frame(161));
    Assert.IsType<InvalidDataException>(state.Failure);
  }

  [Fact]
  public void CorrectionOwnsPendingPixelsAndCannotBeReused()
  {
    using var state = new ScrollCaptureState(CancellationToken.None);
    state.Start(ScrollCaptureDirection.Down, false, 0, 0);
    state.ObserveFrame(Frame(0, period: 8));
    state.ObserveFrame(Frame(0, period: 8));
    CapturedFrame pending = Frame(2, period: 8);
    state.ObserveFrame(pending);
    ScrollCaptureReview review = state.ObserveFrame(pending).Review!;
    Array.Clear(pending.Pixels);
    Assert.True(state.AuthorizeCorrection(review, 11));
    Assert.Equal(ScrollCaptureUpdate.CorrectionRejected, state.ApplyCorrection().Update);
    Assert.Equal(1, state.Stitcher!.FrameCount);
    Assert.True(state.AuthorizeCorrection(review, 10));
    Assert.Equal(ScrollCaptureUpdate.CorrectionAccepted, state.ApplyCorrection().Update);
    Assert.Equal(Frame(0, height: 106, period: 8).Pixels,
        state.Stitcher.BuildFrame(TestContext.Current.CancellationToken).Pixels);

    state.ObserveFrame(Frame(4, period: 8));
    ScrollCaptureReview next = state.ObserveFrame(Frame(4, period: 8)).Review!;
    Assert.False(state.AuthorizeCorrection(review, 2));
    Assert.Equal(ScrollCaptureUpdate.None, state.ApplyCorrection().Update);
    Assert.Same(next, state.Review);
    Assert.Equal(2, state.Stitcher.FrameCount);
  }

  [Fact]
  public async Task SuccessfulBuildCannotPublishWhileExternalCallbackIsDelayed()
  {
    using var external = new CancellationTokenSource();
    using var state = new ScrollCaptureState(external.Token);
    using CancellationTokenRegistration registration = external.Token.Register(state.Cancel);
    var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using CancellationTokenRegistration delay = external.Token.Register(() =>
    {
      blocked.TrySetResult();
      release.Task.GetAwaiter().GetResult();
    });
    state.Start(ScrollCaptureDirection.Down, false, 0, 0);
    state.ObserveFrame(Frame(0));
    state.ObserveFrame(Frame(0));
    Assert.True(state.TryRequestFinish());
    Assert.True(state.BeginFinishSampling());
    state.ObserveFrame(Frame(0));
    state.ObserveFrame(Frame(0));
    Task cancellation = Task.CompletedTask;
    try
    {
      Assert.False(state.PublishFinishedSample(() =>
      {
        cancellation = Task.Run(external.Cancel);
        Assert.True(blocked.Task.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(external.IsCancellationRequested);
        Assert.False(state.Completion.IsCompleted);
      }));
      Assert.Equal(ScrollCapturePhase.ExternalCancelled, state.Phase);
      OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.Completion);
      Assert.Equal(external.Token, error.CancellationToken);
    }
    finally
    {
      release.TrySetResult();
      await cancellation;
    }
  }

  [Fact]
  public async Task FirstFailureWinsOverExternalCancellationAndUserClose()
  {
    using var external = new CancellationTokenSource();
    using var state = new ScrollCaptureState(external.Token);
    var first = new InvalidDataException("source changed");
    state.Fail(first);
    state.Fail(new TimeoutException());
    external.Cancel();
    state.Cancel();
    state.Cancel();
    Assert.Equal(ScrollCapturePhase.Failed, state.Phase);
    Assert.Same(first, await Assert.ThrowsAsync<InvalidDataException>(() => state.Completion));
  }

  [Fact]
  public async Task AppendFailureIsRecordedBeforeTheStopNotification()
  {
    using var external = new CancellationTokenSource();
    using var state = new ScrollCaptureState(external.Token);
    state.Start(ScrollCaptureDirection.Down, false, 1, 0);
    state.ObserveFrame(Frame(0));
    state.ObserveFrame(Frame(0));
    CapturedFrame changed = Frame(0);
    changed.Pixels[0] ^= 0xFF;
    state.ObserveFrame(changed);
    Assert.Null(state.Failure);

    state.ObserveOwnedFrame(changed, beforeFailureNotification: () =>
    {
      Assert.False(state.Token.IsCancellationRequested);
      external.Cancel();
      state.Cancel();
    });

    Assert.Equal(ScrollCapturePhase.Failed, state.Phase);
    InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => state.Completion);
    Assert.Contains("固定边缘", error.Message);
  }

  [Fact]
  public void FinishDuringCursorPositioningSkipsWheelAndKeepsCaptureActive()
  {
    using var state = StartAutomaticCapture();
    var input = new ScrollCaptureScrollInput.SyntheticScrollInput();
    var center = new ScrollCaptureScrollInput.Point { X = 80, Y = 120 };
    ScrollCaptureDriver.AdvanceAutomaticScroll(state, 1, () => 1, _ => { }, () =>
        ScrollCaptureScrollInput.TrySend(input, center, 1, state.Direction, () =>
        {
          if (input.Moves == 1) Assert.True(state.TryRequestFinish());
          return state.CanScrollAutomatically;
        }));

    Assert.Equal(0, input.Sends);
    Assert.Equal(2, input.Moves);
    Assert.Equal(12, input.Position.X);
    Assert.True(state.FinishRequested);
    Assert.True(state.IsActive);
    Assert.Null(state.Failure);
  }

  [Theory]
  [InlineData(true, 0)]
  [InlineData(false, 1)]
  public void ControllerForegroundDefersAutomaticWheel(bool beforeSend, int expectedSends)
  {
    using var state = StartAutomaticCapture();
    nint foreground = beforeSend ? 2 : 1;
    int sends = 0;
    ScrollCaptureDriver.AdvanceAutomaticScroll(state, 1, () => foreground, window =>
    {
      Assert.True(window is 1 or 2);
    }, () =>
    {
      sends++;
      foreground = 2;
      return false;
    });

    Assert.Equal(expectedSends, sends);
    Assert.Equal(ScrollCapturePhase.WaitingForStability, state.Phase);
    Assert.True(state.Automatic);
    Assert.Null(state.Failure);
  }

  [Fact]
  public void RefusedAutomaticWheelStillFailsWithSourceForeground()
  {
    using var state = StartAutomaticCapture();
    int validations = 0;
    ScrollCaptureDriver.AdvanceAutomaticScroll(state, 1, () => 1, _ => validations++, () => false);

    Assert.Equal(2, validations);
    Assert.Equal(ScrollCapturePhase.Failed, state.Phase);
    Assert.Contains("滚轮输入被拒绝", Assert.IsType<InvalidOperationException>(state.Failure).Message);
  }

  [Fact]
  public void RefusedAutomaticWheelStillChecksForUnexpectedWindow()
  {
    using var state = StartAutomaticCapture();
    nint foreground = 1;
    var sourceChanged = new InvalidOperationException("源窗口已失去前台");
    InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
        ScrollCaptureDriver.AdvanceAutomaticScroll(state, 1, () => foreground, window =>
        {
          if (window != 1 && window != 2) throw sourceChanged;
        }, () =>
        {
          foreground = 3;
          return false;
        }));
    Assert.Same(sourceChanged, actual);
  }

  [Fact]
  public async Task SegmentReviewDoesNotExtendTheTotalDeadline()
  {
    long now = 0;
    using var state = new ScrollCaptureState(CancellationToken.None, () => now);
    state.Start(ScrollCaptureDirection.Down, false, 0, 0);
    ScrollCaptureReview review = state.OpenReview(ScrollCaptureUpdate.SegmentReview, segment: Frame(0)).Review!;
    now = ScrollCaptureState.TotalTimeoutMs;
    Assert.False(state.ConfirmSegment(review));
    Assert.Equal(ScrollCapturePhase.Failed, state.Phase);
    Assert.False(state.Completion.IsCompleted);
    state.Cancel();
    Assert.Equal(ScrollCaptureState.DeadlineMessage,
        (await Assert.ThrowsAsync<InvalidOperationException>(() => state.Completion)).Message);
  }

  private static ScrollCaptureState StartAutomaticCapture()
  {
    var state = new ScrollCaptureState(CancellationToken.None);
    state.Start(ScrollCaptureDirection.Down, true, 0, 0);
    state.ObserveFrame(Frame(0));
    state.ObserveFrame(Frame(0));
    return state;
  }

  private static CapturedFrame Frame(int top, int height = 96, int period = 0)
  {
    const int width = 48;
    byte[] pixels = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
    {
      int value = period > 0 ? (top + y) % period : top + y;
      for (int x = 0; x < width; x++)
      {
        int offset = (y * width + x) * 4;
        pixels[offset] = (byte)value;
        pixels[offset + 1] = (byte)(value >> 8);
      }
    }
    return new CapturedFrame(pixels, width, height, width * 4, "BGRA8");
  }
}
