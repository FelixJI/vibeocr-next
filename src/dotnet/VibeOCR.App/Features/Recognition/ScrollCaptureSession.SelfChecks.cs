using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using VibeOCR.App.Services;
using VibeOCR.Platform.Windows;
using Windows.Graphics;

namespace VibeOCR.App.Features.Recognition;

internal static partial class ScrollCaptureSession
{
  internal sealed partial class Session
  {
    private sealed class SelfCheckClock
    {
      public long Now { get; set; }
    }

    private SelfCheckClock? _selfCheckClock;

    private static Session CreateSelfCheckSession(int width = 48, int height = 96)
    {
      var clock = new SelfCheckClock();
      var session = new Session(1, new PhysicalRectangle(0, 0, width, height), CancellationToken.None, clock: () => clock.Now)
      {
        _selfCheckClock = clock,
        _window = new Window(),
        _statusText = new TextBlock(),
        _errorText = new TextBlock(),
        _previewImage = new Image(),
        _pendingImage = new Image(),
        _offsetBox = new NumberBox(),
        _finishButton = new Button(),
        _cancelButton = new Button(),
        _confirmButton = new Button { IsEnabled = true },
      };
      session._window.Content = session._previewImage;
      return session;
    }

    private void AdvanceSelfCheckClock(int milliseconds) => _selfCheckClock!.Now += milliseconds;

    private Task ObserveFrameAsync(CapturedFrame frame)
    {
      _state.Start(ScrollCaptureDirection.Down, false, 0, 0);
      PresentChange(_state.ObserveFrame(frame), frame);
      PresentFailure();
      return Task.CompletedTask;
    }

    private void PauseForReview(string message, CapturedFrame? pending)
    {
      if (_state.OpenReview(ScrollCaptureUpdate.OffsetReview, pending).Review is { } review)
        PresentReview(review, message, pending is null ? "确认预览并继续" : "确认位移并校验拼接");
    }

    private void ApplyConfirmedOffset(Action? afterClaimForTest = null, Action? afterMatchForTest = null)
    {
      ScrollCaptureChange correction = _state.ApplyCorrection(afterClaimForTest, afterMatchForTest);
      PresentChange(correction, correction.Review?.Pending);
    }

    private void HandleNoProgressBoundary()
    {
      PresentChange(_state.EndNoProgress(), null);
      PresentFailure();
    }

    // 此自检使用真实 WinUI 控件与解码，阻塞上一张预览来验证确认门控，不需要桌面前台权限。
    internal static async Task VerifyPreviewGateSelfCheckAsync()
    {
      ScrollCaptureScrollInput.VerifyInputBoundaries();
      static void Check(bool condition, string message)
      {
        if (!condition) throw new InvalidOperationException(message);
      }
      byte[] pixels = new byte[48 * 96 * 4];
      for (int y = 0; y < 96; y++) pixels[y * 48 * 4] = (byte)y;
      var frame = new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      var final = CreateSelfCheckSession();
      var pending = CreateSelfCheckSession();
      var first = CreateSelfCheckSession();
      var manual = CreateSelfCheckSession();
      var witness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      try
      {
        final._previewTask = witness.Task;
        ScrollCaptureReview segment = final._state.OpenReview(ScrollCaptureUpdate.SegmentReview,
            segment: new VerticalScrollStitcher(frame).BuildFrame()).Review!;
        final.PresentSegmentReview(segment);
        Check(final._confirmButton.Visibility == Visibility.Visible && !final._confirmButton.IsEnabled,
            "Final confirmation must disable immediately, even if the preceding preview enabled it.");
        final.OnConfirm(final._confirmButton, new RoutedEventArgs());
        Check(!final._state.Completion.IsCompleted, "An unfinished preview must not publish its frame.");
        witness.SetResult();
        await final._previewTask;
        Check(final._previewImage.Source is not null && final._confirmButton.IsEnabled,
            "Only the decoded final preview may enable confirmation.");
        final.OnConfirm(final._confirmButton, new RoutedEventArgs());
        Check(final._state.Failure is not null && !final._state.Completion.IsCompletedSuccessfully,
            "Even a decoded final preview must reject confirmation when its source window no longer exists.");

        await manual.ObserveFrameAsync(frame);
        await manual.ObserveFrameAsync(frame);
        await manual._previewTask;
        Check(manual._state.Review is null && manual._finishButton.IsEnabled && manual._state.Stitcher?.FrameCount == 1,
            "The first stable frame must be accepted directly without an initial preview review.");
        await manual.ObserveFrameAsync(frame);
        await manual.ObserveFrameAsync(frame);
        Check(manual._finishButton.IsEnabled && manual._state.Stitcher?.FrameCount == 1,
            "Duplicate stable frames must retain single-frame completion without increasing the frame count.");
        Task.Run(() => manual.PresentChange(new(ScrollCaptureUpdate.FirstAccepted), frame)).GetAwaiter().GetResult();
        manual.OnFinish(manual._finishButton, new RoutedEventArgs());
        Check(manual._state.FinishRequested, "The enabled finish action must request final sampling.");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Check(manual._window.DispatcherQueue.TryEnqueue(() => drained.SetResult()), "Could not drain queued progress updates.");
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(manual._statusText.Text == "请停止滚动，正在收尾…" && !manual._finishButton.IsEnabled,
            "Queued frame progress must not replace the finishing status or re-enable finish.");
        var invalid = new CapturedFrame([], 48, 96, 48 * 4, "BGRA8");
        first.ReportAccepted(new VerticalScrollStitcher(frame), invalid);
        first.PauseForReview("synthetic initial frame", null);
        await first._previewTask;
        Check(first._state.Failure is not null && first._errorText.Visibility == Visibility.Visible &&
            !first._confirmButton.IsEnabled && first._confirmButton.Visibility == Visibility.Collapsed,
            "An initial preview encoding failure must not reopen confirmation.");

        pending.PauseForReview("synthetic pending frame", invalid);
        await pending._previewTask;
        Check(pending._state.Failure is not null && pending._errorText.Visibility == Visibility.Visible &&
            pending._errorText.Text.Contains("无法生成待拼预览", StringComparison.Ordinal),
            "A pending-frame encoding failure must produce a visible failure.");
        Check(!pending._confirmButton.IsEnabled && pending._confirmButton.Visibility == Visibility.Collapsed,
            "A failed pending preview must keep confirmation closed.");
        pending.OnConfirm(pending._confirmButton, new RoutedEventArgs());
        Check(!pending._state.Completion.IsCompleted && pending._state.HasCorrection == false,
            "A failed preview must neither publish output nor accept a correction.");
      }
      finally
      {
        witness.TrySetResult();
        foreach (Session session in new[] { final, pending, first, manual })
        {
          session._state.Stop();
          await session._previewTask;
          session._previewImage.Source = null;
          session._window.Close();
          session._state.Dispose();
        }
      }
    }
    internal static async Task VerifyAcceptedPreviewAllocationSelfCheckAsync()
    {
      const int side = 2048;
      byte[] pixels = new byte[side * side * 4];
      for (int y = 0; y < side; y++) pixels[y * side * 4] = (byte)y;
      var frame = new CapturedFrame(pixels, side, side, side * 4, "BGRA8");
      var stitcher = new VerticalScrollStitcher(frame);
      var session = CreateSelfCheckSession(side, side);
      var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      try
      {
        session._previewTask = busy.Task;
        long before = GC.GetAllocatedBytesForCurrentThread();
        session.ReportAccepted(stitcher, frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        AppLog.Info($"scroll-capture busy preview allocation self-check: {allocated} bytes");
        if (allocated > 65536 || !ReferenceEquals(session._previewTask, busy.Task))
          throw new InvalidOperationException($"A busy renderer must skip preview generation before allocation; allocated={allocated} bytes.");
        busy.SetResult();
        Task.Run(() => session.ReportAccepted(stitcher, frame)).GetAwaiter().GetResult();
        if (!session._acceptedPreviewQueued) throw new InvalidOperationException("The worker preview must be queued until the UI can present it.");
        before = GC.GetAllocatedBytesForCurrentThread();
        session.ReportAccepted(stitcher, frame);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated > 65536) throw new InvalidOperationException("A queued preview must also block further generation.");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session._window.DispatcherQueue.TryEnqueue(() => drained.SetResult()))
          throw new InvalidOperationException("Could not drain the accepted preview callback.");
        await drained.Task;
        await session._previewTask;
        CapturedFrame unmarked = stitcher.BuildPreview(PreviewMaxDecodePx);
        foreach (ScrollCaptureDirection direction in new[] { ScrollCaptureDirection.Down, ScrollCaptureDirection.Right })
        {
          CapturedFrame marked = MarkFixedEdges(unmarked, side, side, direction, 256, 128, session._state.Token);
          int end = direction == ScrollCaptureDirection.Down ? (marked.Height - 1) * marked.Stride : (marked.Width - 1) * 4;
          int middle = marked.Height / 2 * marked.Stride + marked.Width / 2 * 4;
          if (marked.Pixels[2] == 0 || marked.Pixels[end + 2] == 0 || unmarked.Pixels[2] != 0 ||
              !marked.Pixels.AsSpan(middle, 4).SequenceEqual(unmarked.Pixels.AsSpan(middle, 4)))
            throw new InvalidOperationException("Thumbnail fixed-edge marks must follow physical source coordinates without changing body/source pixels.");
        }
        before = GC.GetAllocatedBytesForCurrentThread();
        session.ReportAccepted(stitcher, frame);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated > 3 * PreviewMaxDecodePx * PreviewMaxDecodePx * 4 + 65536)
          throw new InvalidOperationException($"Accepted preview allocation must depend on the thumbnail bound, not source area; allocated={allocated} bytes.");
        await session._previewTask;
        if (session._previewImage.Source is not BitmapImage bitmap ||
            bitmap.PixelWidth > PreviewMaxDecodePx || bitmap.PixelHeight > PreviewMaxDecodePx)
          throw new InvalidOperationException("The encoded accepted preview itself must be bounded, not only its decode request.");
      }
      finally
      {
        busy.TrySetResult();
        session._state.Stop();
        await session._previewTask;
        session._previewImage.Source = null;
        session._window.Close();
        session._state.Dispose();
      }
    }

    internal static async Task VerifyPreviewResumeStableWindowSelfCheckAsync()
    {
      static CapturedFrame Frame(int top)
      {
        byte[] pixels = new byte[48 * 96 * 4];
        for (int y = 0; y < 96; y++)
          for (int x = 0; x < 48; x++) pixels[(y * 48 + x) * 4] = (byte)(top + y);
        return new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      }
      static CapturedFrame RepaintingFrame(int top)
      {
        CapturedFrame frame = Frame(top);
        frame.Pixels[0] ^= 0xA5; // 重绘像素破坏真实重叠，不能被当作滚动进展接收。
        return frame;
      }
      var sessions = new[] { CreateSelfCheckSession(), CreateSelfCheckSession() };
      try
      {
        foreach (Session session in sessions)
        {
          await session.ObserveFrameAsync(Frame(32));
          await session.ObserveFrameAsync(Frame(32));
          await session._previewTask;
          if (session._state.Failure is not null || session._state.Review is not null || session._state.Stitcher is null ||
              !session._finishButton.IsEnabled)
            throw new InvalidOperationException("The first stable frame must enter the stitcher directly.", session._state.Failure);
          if (ReferenceEquals(session, sessions[1]))
          {
            // 反向帧复核是现存的恢复点：确认后继续必须拿到全新的动态窗口。
            await session.ObserveFrameAsync(Frame(12));
            await session.ObserveFrameAsync(Frame(12));
            await session._previewTask;
            if (!Equals(session._confirmButton.Content, "丢弃反向帧并继续"))
              throw new InvalidOperationException("The recovery case must pause on a real reverse-frame review.");
          }
          else
          {
            // 自动滚动未前进等暂停复核与首帧直达采集共存：确认继续同样要重置动态窗口。
            session.PauseForReview("synthetic pause review", null);
            await session._previewTask;
          }
          session.AdvanceSelfCheckClock(ScrollCaptureState.NoProgressTimeoutMs + 1000);
          long elapsedBefore = session._state.ElapsedMilliseconds;
          session.OnConfirm(session, new RoutedEventArgs());
          if (session._state.Review is not null)
            throw new InvalidOperationException("Confirmation must resume capture from the review pause.");
          await session.ObserveFrameAsync(RepaintingFrame(52));
          if (session._state.Failure is not null)
            throw new InvalidOperationException("A changed frame immediately after a long review must get a fresh dynamic window.", session._state.Failure);
          if (session._state.Phase != ScrollCapturePhase.WaitingForStability ||
              session._state.ElapsedMilliseconds != elapsedBefore || session._state.NoProgressExpired)
            throw new InvalidOperationException("Review resume must restart the no-progress and stability budgets while preserving total elapsed time.");
          session.AdvanceSelfCheckClock(ScrollCaptureState.DynamicContentTimeoutMs - 1000);
          await session.ObserveFrameAsync(RepaintingFrame(53));
          if (session._state.Failure is not null)
            throw new InvalidOperationException("Changing content inside the fresh five-second window must be allowed.");
          session.AdvanceSelfCheckClock(1001);
          await session.ObserveFrameAsync(RepaintingFrame(54));
          if (session._state.Failure is not InvalidDataException)
            throw new InvalidOperationException("Changing content beyond the same five-second window must still stop.");
        }
      }
      finally
      {
        foreach (Session session in sessions)
        {
          session._state.Stop();
          await session._previewTask;
          session._previewImage.Source = null;
          session._window.Close();
          session._state.Dispose();
        }
      }
    }

    internal static async Task VerifyPreviewPlacementSelfCheckAsync()
    {
      PhysicalRectangle work = GetWorkArea(DesktopScreenQuery.GetPrimaryMonitor());
      byte[] pixels = new byte[48 * 96 * 4];
      for (int y = 0; y < 96; y++) pixels[y * 48 * 4] = (byte)y;
      var frame = new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      byte[] bmp = EncodePreviewBmp(frame) ?? throw new InvalidOperationException("Preview fixture encoding failed.");
      string[] stages = ["paused", "pending", "final", "confirm"];
      Session[] sessions = stages.Select(_ => CreateSelfCheckSession()).ToArray();
      try
      {
        foreach ((string stage, Session session) in stages.Zip(sessions))
        {
          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.begin");
          session._window.Content = new Grid();
          ConfigureOverlayWindow(session._window);
          session._controlHandle = WinRT.Interop.WindowNative.GetWindowHandle(session._window);
          session._window.AppWindow.MoveAndResize(new RectInt32(work.X + 20, work.Y + 20, 200, 60));
          session._source = session._source with { Dpi = ScrollCaptureDriver.GetDpiForWindow(session._controlHandle) };
          session._bounds = new PhysicalRectangle(work.X + work.Width / 4, work.Y + work.Height / 4, 200, 200);
          var previews = new StackPanel();
          previews.Children.Add(session._previewImage);
          previews.Children.Add(session._pendingImage);
          session._previewWindow = new Window { Content = previews };
          ConfigureOverlayWindow(session._previewWindow);
          session._previewHandle = WinRT.Interop.WindowNative.GetWindowHandle(session._previewWindow);
          ScrollCaptureReview review = session._state.OpenReview(ScrollCaptureUpdate.AutomaticReview).Review!;
          session._confirmButton.IsEnabled = false;
          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.decode");
          await session.RenderPreviewAsync(bmp, frame.Width, frame.Height);
          await session.RenderReviewPreviewAsync(Task.CompletedTask, null, review);
          if (session._state.Failure is not null || !IsWindowVisible(session._previewHandle) ||
              session._previewImage.Source is null || !session._confirmButton.IsEnabled)
            throw new InvalidOperationException("A decoded preview with legal real HWND geometry must enable paused review confirmation.");

          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.visible");
          // 工具栏仍有自己的矩形，但工作区已无任何可放预览的选区外空间。
          session._bounds = work;
          session._confirmButton.IsEnabled = false;
          if (stage == "paused") await session.RenderReviewPreviewAsync(Task.CompletedTask, null, review);
          else if (stage == "pending")
          {
            ScrollCaptureReview pending = session._state.OpenReview(ScrollCaptureUpdate.OffsetReview, pending: frame).Review!;
            session._pendingImage.Visibility = Visibility.Visible;
            await session.RenderReviewPreviewAsync(Task.CompletedTask, bmp, pending);
          }
          else
          {
            ScrollCaptureReview final = session._state.OpenReview(ScrollCaptureUpdate.SegmentReview, segment: frame).Review!;
            if (stage == "final") await session.RenderFinalPreviewAsync(Task.CompletedTask, bmp, final);
            else
            {
              session._confirmButton.IsEnabled = true;
              session.OnConfirm(session, new RoutedEventArgs());
            }
          }
          if (session._state.Failure is null || !session._errorText.Text.Contains("预览空间", StringComparison.Ordinal) ||
              session._confirmButton.IsEnabled || session._finishButton.IsEnabled ||
              IsWindowVisible(session._previewHandle) || session._state.Completion.IsCompleted || session._state.HasCorrection)
            throw new InvalidOperationException($"An invisible {stage} preview must fail closed with shrink-selection guidance.");
          session.OnConfirm(session, new RoutedEventArgs());
          if (session._state.Completion.IsCompleted) throw new InvalidOperationException("Hidden preview confirmation returned output.");
          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.rejected");
        }
      }
      finally
      {
        foreach ((string stage, Session session) in stages.Zip(sessions))
        {
          session._state.Stop();
          await session._previewTask;
          session._previewWindow?.Close();
          session._window.Close();
          session._state.Dispose();
          CaptureUiSmokeTrace.Stage($"scroll-preview-geometry.{stage}.closed");
        }
      }
    }

    // 回归：30 秒无新增不再把已收合法选段连同错误一起丢弃（原报错“30 秒没有新增有效内容…未生成长图”）。
    internal static async Task VerifyNoProgressBoundarySelfCheckAsync()
    {
      static CapturedFrame Frame(int top)
      {
        byte[] pixels = new byte[48 * 96 * 4];
        for (int y = 0; y < 96; y++)
          for (int x = 0; x < 48; x++) pixels[(y * 48 + x) * 4] = (byte)(top + y);
        return new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      }
      var sessions = new[] { CreateSelfCheckSession(), CreateSelfCheckSession(), CreateSelfCheckSession(), CreateSelfCheckSession() };
      try
      {
        Session boundary = sessions[0];
        await boundary.ObserveFrameAsync(Frame(32));
        await boundary.ObserveFrameAsync(Frame(32));
        await boundary._previewTask;
        if (boundary._state.Failure is not null || boundary._state.Review is not null || boundary._state.Stitcher?.FrameCount != 1)
          throw new InvalidOperationException("The boundary regression must start from an accepted single-frame segment.", boundary._state.Failure);
        boundary.AdvanceSelfCheckClock(ScrollCaptureState.NoProgressTimeoutMs + 1);
        boundary.HandleNoProgressBoundary();
        if (boundary._state.Failure is not null || boundary._state.Review?.Segment is not null || boundary._state.Review is not null ||
            !boundary._state.Completion.IsCompletedSuccessfully)
          throw new InvalidOperationException(
              "The clean no-progress boundary must return the accepted segment without another preview confirmation.",
              boundary._state.Failure);
        CapturedFrame? output = await boundary._state.Completion;
        if (output?.Pixels.SequenceEqual(Frame(32).Pixels) != true)
          throw new InvalidOperationException("The confirmed boundary segment must return exactly the accepted pixels.");

        Session idle = sessions[1];
        idle._state.Start(ScrollCaptureDirection.Down, false, 0, 0);
        idle.AdvanceSelfCheckClock(ScrollCaptureState.NoProgressTimeoutMs + 1);
        idle.HandleNoProgressBoundary();
        if (idle._state.Failure is null || !idle._errorText.Text.Contains("首帧", StringComparison.Ordinal) ||
            idle._state.Review?.Segment is not null)
          throw new InvalidOperationException("Without any accepted frame the boundary stop must still report a real first-frame failure.");

        Session reviewing = sessions[2];
        await reviewing.ObserveFrameAsync(Frame(32));
        await reviewing.ObserveFrameAsync(Frame(32));
        await reviewing._previewTask;
        reviewing.PauseForReview("synthetic review wait", null);
        reviewing.AdvanceSelfCheckClock(ScrollCaptureState.NoProgressTimeoutMs + 1);
        if (reviewing._state.Failure is not null)
          throw new InvalidOperationException("The synthetic review pause must succeed.", reviewing._state.Failure);
        if (reviewing._state.NoProgressExpired)
          throw new InvalidOperationException("Waiting for an explicit user decision must pause the no-progress boundary.");

        Session missing = sessions[3];
        await missing.ObserveFrameAsync(Frame(32));
        await missing.ObserveFrameAsync(Frame(32));
        await missing._previewTask;
        await missing.ObserveFrameAsync(Frame(160));
        await missing.ObserveFrameAsync(Frame(160));
        missing.AdvanceSelfCheckClock(ScrollCaptureState.NoProgressTimeoutMs + 1);
        missing.HandleNoProgressBoundary();
        await missing._previewTask;
        if (missing._state.Failure is not null || missing._state.Review?.Segment is null || missing._state.Review is null ||
            missing._state.Completion.IsCompleted || !missing._confirmButton.IsEnabled)
          throw new InvalidOperationException("A missing overlap must require explicit confirmation before returning the accepted segment.", missing._state.Failure);
        missing.OnConfirm(missing._confirmButton, new RoutedEventArgs());
        if (missing._state.Failure is null || missing._state.Completion.IsCompletedSuccessfully)
          throw new InvalidOperationException("Final confirmation must recheck the source after the capture loop has stopped.");
      }
      finally
      {
        foreach (Session session in sessions)
        {
          session._state.Stop();
          await session._previewTask;
          session._previewImage.Source = null;
          session._window.Close();
          session._state.Dispose();
        }
      }
    }

    internal static void VerifySelectionGeometrySelfCheck()
    {
      ScrollCaptureMask.VerifySelfCheck();
      var boundary = new Session(1, new PhysicalRectangle(-350, -150, 300, 300), CancellationToken.None)
      {
        _source = new ScrollCaptureDriver.Source(0, new PhysicalRectangle(-500, -300, 1200, 900), 0,
            new PhysicalRectangle(-400, -200, 800, 600)),
        _widthBox = new NumberBox { Value = 800 },
        _heightBox = new NumberBox { Value = 300 },
        _errorText = new TextBlock(),
      };
      try
      {
        PhysicalRectangle accepted = boundary._bounds;
        if (!boundary.SelectionFitsCaptureArea(accepted)) throw new InvalidOperationException("Valid negative-coordinate selections must remain supported.");
        boundary.ApplySelectionDimensions();
        if (boundary._bounds != accepted || boundary._errorText.Visibility != Visibility.Visible)
          throw new InvalidOperationException("Resizing into a source window's off-desktop area must be rejected before any capture/window mutation.");
        foreach (PhysicalRectangle outside in new[]
        {
          new PhysicalRectangle(-350, -150, 800, 300),
          new PhysicalRectangle(-350, -150, 300, 600),
          new PhysicalRectangle(-450, -150, 100, 100),
          new PhysicalRectangle(-350, -250, 100, 100),
        })
          if (boundary.SelectionFitsCaptureArea(outside)) throw new InvalidOperationException("Every desktop edge must bound resizing even when the source window extends beyond it.");
        boundary._source = boundary._source with { WindowBounds = new PhysicalRectangle(-300, -100, 400, 300) };
        if (boundary.SelectionFitsCaptureArea(accepted)) throw new InvalidOperationException("The source window boundary must also remain enforced.");
      }
      finally { boundary._state.Dispose(); }
      foreach ((int width, int height, ScrollCaptureDirection direction) in new[]
          { (200, 40, ScrollCaptureDirection.Right), (40, 200, ScrollCaptureDirection.Down) })
      {
        var session = new Session(1, new PhysicalRectangle(0, 0, width, height), CancellationToken.None);
        try
        {
          session.ValidateSelectionSize();
          byte[] pixels = new byte[width * height * 4];
          for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixels[(y * width + x) * 4] = (byte)(x + y);
          _ = new VerticalScrollStitcher(new CapturedFrame(pixels, width, height, width * 4, "BGRA8"), direction);
          session._directionSegments = session.CreateDirectionSegments();
          session._leadingBox = new NumberBox { Value = 0 };
          session._trailingBox = new NumberBox { Value = 0 };
          session._errorText = new TextBlock();
          // 分段方向与拼接轴垂直：短轴不足 64px 必须在启动前被拒绝。
          session.StartWithDirection(direction == ScrollCaptureDirection.Right ? 0 : 2);
          if (session._loop is not null || !session._errorText.Text.Contains("64", StringComparison.Ordinal))
            throw new InvalidOperationException("The selected scroll axis must still leave at least 64 pixels before capture starts.");
        }
        finally { session._state.Dispose(); }
      }
      foreach ((int width, int height) in new[] { (31, 200), (200, 31), (32769, 32), (4000, 2001) })
      {
        var session = new Session(1, new PhysicalRectangle(0, 0, width, height), CancellationToken.None);
        try
        {
          try { session.ValidateSelectionSize(); }
          catch (InvalidOperationException) { continue; }
          throw new InvalidOperationException("Common minimum side and resource limits must remain enforced.");
        }
        finally { session._state.Dispose(); }
      }
    }

    internal static async Task VerifyReverseRecoverySelfCheckAsync()
    {
      static CapturedFrame Frame(int top)
      {
        byte[] pixels = new byte[48 * 96 * 4];
        for (int y = 0; y < 96; y++)
          for (int x = 0; x < 48; x++) pixels[(y * 48 + x) * 4] = (byte)(top + y);
        return new CapturedFrame(pixels, 48, 96, 48 * 4, "BGRA8");
      }
      var session = CreateSelfCheckSession();
      try
      {
        session._state.Start(ScrollCaptureDirection.Down, true, 0, 0);
        await session.ObserveFrameAsync(Frame(32));
        await session.ObserveFrameAsync(Frame(32));
        await session._previewTask;
        CapturedFrame reverse = Frame(12);
        await session.ObserveFrameAsync(reverse);
        await session.ObserveFrameAsync(reverse);
        await session._previewTask;
        if (session._state.Review?.Pending is not null || session._offsetBox.Visibility != Visibility.Collapsed ||
            !Equals(session._confirmButton.Content, "丢弃反向帧并继续") || session._state.Review is null || session._state.Automatic)
          throw new InvalidOperationException("A reverse frame must offer explicit discard/continue, never impossible positive correction.");
        if (session._state.Stitcher?.FrameCount != 1 || !session._state.Stitcher.BuildFrame().Pixels.SequenceEqual(Frame(32).Pixels))
          throw new InvalidOperationException("Reverse review must not change the accepted segment.");
        session.OnConfirm(session, new RoutedEventArgs());
        await session.ObserveFrameAsync(reverse);
        await session.ObserveFrameAsync(reverse);
        if (session._state.Review is not null || session._state.Review?.Pending is not null || session._state.Completion.IsCompleted)
          throw new InvalidOperationException("Discarding a reverse frame must resume manual capture without re-prompting on the same stable frame.");
        await session.ObserveFrameAsync(Frame(52));
        await session.ObserveFrameAsync(Frame(52));
        await session._previewTask;
        if (session._state.Stitcher.FrameCount != 2 || session._state.Stitcher.Height != 116)
          throw new InvalidOperationException("Forward scrolling after discard must append to the retained accepted segment.");
      }
      finally
      {
        session._state.Stop();
        await session._previewTask;
        session._window.Close();
        session._state.Dispose();
      }
    }

    internal static async Task VerifyPendingPreviewBoundsSelfCheckAsync()
    {
      // 所有用例先创建窗口，避免关闭最后窗口后在下一次异步解码期间退出 App。
      var sessions = new[] { CreateSelfCheckSession(32, 2160), CreateSelfCheckSession(2160, 32), CreateSelfCheckSession(32, 96) };
      try
      {
        foreach (Session session in sessions)
        {
          int width = session._bounds.Width, height = session._bounds.Height;
          var frame = new CapturedFrame(new byte[width * height * 4], width, height, width * 4, "BGRA8");
          session.PauseForReview("synthetic pending preview bounds", frame);
          await session._previewTask;
          BitmapImage? bitmap = session._pendingImage.Source as BitmapImage;
          // PixelWidth/Height 在此报告源尺寸；解码约束由 DecodePixelWidth/Height 表达。
          if (session._state.Failure is not null || bitmap is null ||
              bitmap.DecodePixelWidth < 1 || bitmap.DecodePixelWidth > Math.Min(width, PreviewMaxDecodePx) ||
              bitmap.DecodePixelHeight < 1 || bitmap.DecodePixelHeight > Math.Min(height, PreviewMaxDecodePx) ||
              bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            throw new InvalidOperationException(
                $"Pending preview must bound both decode dimensions by the longest edge without enlarging small frames. " +
                $"source={width}x{height}, decode={bitmap?.DecodePixelWidth}x{bitmap?.DecodePixelHeight}, " +
                $"pixels={bitmap?.PixelWidth}x{bitmap?.PixelHeight}, failure={session._state.Failure?.Message}");
        }
      }
      finally
      {
        foreach (Session session in sessions)
        {
          session._state.Stop();
          await session._previewTask;
          session._pendingImage.Source = null;
          session._window.Close();
          session._state.Dispose();
        }
      }
    }

    internal static async Task VerifyContinuousScrollRecoverySelfCheckAsync()
    {
      static CapturedFrame Frame(int top, int height = 96)
      {
        byte[] pixels = new byte[48 * height * 4];
        for (int y = 0; y < height; y++)
        {
          pixels[y * 48 * 4] = (byte)(top + y);
          pixels[y * 48 * 4 + 1] = (byte)((top + y) >> 8);
        }
        return new CapturedFrame(pixels, 48, height, 48 * 4, "BGRA8");
      }
      var session = CreateSelfCheckSession();
      try
      {
        await session.ObserveFrameAsync(Frame(0));
        await session.ObserveFrameAsync(Frame(0));
        await session._previewTask;
        // 连续滚动没有两张相同帧，但每步有经完整校验的重叠。
        foreach (int top in new[] { 40, 80, 120 }) await session.ObserveFrameAsync(Frame(top));
        await session._previewTask;
        if (session._state.Stitcher?.FrameCount != 4 ||
            !session._state.Stitcher.BuildFrame().Pixels.SequenceEqual(Frame(0, 216).Pixels))
          throw new InvalidOperationException("Continuous overlapping scroll frames were skipped before settling.");
        CapturedFrame accepted = session._state.Stitcher.BuildFrame();
        await session.ObserveFrameAsync(Frame(280));
        await session.ObserveFrameAsync(Frame(280));
        await session._previewTask;
        if (session._state.Failure is not null || session._finishButton.IsEnabled ||
            !session._state.Stitcher.BuildFrame().Pixels.SequenceEqual(accepted.Pixels))
          throw new InvalidOperationException("A gap must remain recoverable without changing accepted pixels or allowing completion.");
        session.OnFinish(session, new RoutedEventArgs());
        if (session._state.FinishRequested || session._state.Completion.IsCompleted)
          throw new InvalidOperationException("An unresolved gap must never publish an incomplete long image.");
        // 回滚到与最后接收位置 120 重叠的位置，再向前连接；280 没有被伪造加入。
        await session.ObserveFrameAsync(Frame(160));
        await session.ObserveFrameAsync(Frame(200));
        await session.ObserveFrameAsync(Frame(240));
        await session.ObserveFrameAsync(Frame(280));
        await session._previewTask;
        if (session._state.Failure is not null || !session._finishButton.IsEnabled ||
            !session._state.Stitcher.BuildFrame().Pixels.SequenceEqual(Frame(0, 376).Pixels))
          throw new InvalidOperationException("Returning to an overlapping frame must safely resume and fill every missing strip.");
        session.CancelFromUi();
        if (await session._state.Completion is not null)
          throw new InvalidOperationException("Cancellation must discard recovered output.");
      }
      finally
      {
        session._state.Stop();
        await session._previewTask;
        session._window.Close();
        session._state.Dispose();
      }
    }

    internal static async Task VerifyFirstFailureSelfCheckAsync()
    {
      var session = CreateSelfCheckSession();
      try
      {
        var original = new InvalidOperationException("源窗口已关闭，已停止采集。");
        session.Fail(original);
        session.Fail(new InvalidOperationException("已到 3 分钟总时限（包含预览与纠偏等待），本次未完成，未生成长图。"));
        if (!ReferenceEquals(session._state.Failure, original) || session._errorText.Text != original.Message)
          throw new InvalidOperationException("A late deadline must not overwrite the first capture failure.");
        await Task.Run(() => Parallel.For(0, 16, index =>
            session.Fail(new InvalidOperationException($"concurrent late failure {index}"))));
        session.RunOnUi(() =>
        {
          session._errorText.Text = "late queued correction message";
          session._finishButton.IsEnabled = true;
        });
        if (!ReferenceEquals(session._state.Failure, original) || session._errorText.Text != original.Message)
          throw new InvalidOperationException("Concurrent later failures must retain the first failure and its visible explanation.");
        session.OnConfirm(session, new RoutedEventArgs());
        session.OnFinish(session, new RoutedEventArgs());
        if (session._state.Completion.IsCompleted || session._finishButton.IsEnabled || session._confirmButton.IsEnabled)
          throw new InvalidOperationException("Terminal failure must keep output actions closed.");
        session.CancelFromUi();
        try { await session._state.Completion; throw new InvalidOperationException("Failure returned output."); }
        catch (Exception error) when (ReferenceEquals(error, original)) { }
      }
      finally
      {
        session._state.Stop();
        await session._previewTask;
        session._window.Close();
        session._state.Dispose();
      }
    }

    internal static async Task VerifyAmbiguousCorrectionsSelfCheckAsync()
    {
      static CapturedFrame Periodic(int width, int height, int offset, bool horizontal)
      {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
          for (int x = 0; x < width; x++) pixels[(y * width + x) * 4] = (byte)(((horizontal ? x : y) + offset) % 8);
        return new CapturedFrame(pixels, width, height, width * 4, "BGRA8");
      }
      // 所有测试窗口先创建、统一最后关闭，避免两次 await 之间关闭最后窗口而退出 App。
      var sessions = new[] { CreateSelfCheckSession(), CreateSelfCheckSession(32764, 32) };
      try
      {
        for (int index = 0; index < sessions.Length; index++)
        {
          bool horizontal = index == 1;
          int width = horizontal ? 32764 : 48, height = horizontal ? 32 : 96;
          int acceptedOffset = horizontal ? 2 : 10, rejectedOffset = horizontal ? 10 : 11;
          Session session = sessions[index];
          CapturedFrame first = Periodic(width, height, 0, horizontal);
          CapturedFrame pending = Periodic(width, height, 2, horizontal);
          session._state.Start(horizontal ? ScrollCaptureDirection.Right : ScrollCaptureDirection.Down, false, 0, 0);
          await session.ObserveFrameAsync(first);
          await session.ObserveFrameAsync(first);
          await session._previewTask;
          await session.ObserveFrameAsync(pending);
          await session.ObserveFrameAsync(pending);
          await session._previewTask;
          if (session._state.Review?.Pending?.Pixels.SequenceEqual(pending.Pixels) != true ||
              session._offsetBox.Visibility != Visibility.Visible || session._finishButton.IsEnabled)
            throw new InvalidOperationException("Feasible ambiguous matches must expose only the explicit verified correction path.");
          session._offsetBox.Value = 0;
          session.OnConfirm(session, new RoutedEventArgs());
          if (session._state.HasCorrection) throw new InvalidOperationException("Invalid offsets must not reach the matcher.");
          session._offsetBox.Value = rejectedOffset;
          session.OnConfirm(session, new RoutedEventArgs());
          session.ApplyConfirmedOffset();
          if (session._state.Failure is not null || session._state.Review is null || session._state.Stitcher?.FrameCount != 1 ||
              session._state.Review?.Pending?.Pixels.SequenceEqual(pending.Pixels) != true ||
              !session._state.Stitcher.BuildFrame().Pixels.SequenceEqual(first.Pixels))
            throw new InvalidOperationException("A wrong or oversized choice must preserve ambiguous review and accepted pixels.");
          session._offsetBox.Value = acceptedOffset;
          session.OnConfirm(session, new RoutedEventArgs());
          session.ApplyConfirmedOffset();
          await session._previewTask;
          if (session._state.Failure is not null || session._state.Review is not null || session._state.Review?.Pending is not null ||
              session._state.Stitcher.FrameCount != 2 || !session._finishButton.IsEnabled || session._state.Completion.IsCompleted)
            throw new InvalidOperationException("Another feasible correction must resume capture without publishing unconfirmed output.");
        }
      }
      finally
      {
        foreach (Session session in sessions)
        {
          session._state.Stop();
          await session._previewTask;
          session._window.Close();
          session._state.Dispose();
        }
      }
    }

    internal static async Task VerifyCorrectionAuthorizationSelfCheckAsync()
    {
      static CapturedFrame Periodic(int height, int phase)
      {
        byte[] pixels = new byte[48 * height * 4];
        for (int y = 0; y < height; y++)
          for (int x = 0; x < 48; x++) pixels[(y * 48 + x) * 4] = (byte)((y + phase) % 8);
        return new CapturedFrame(pixels, 48, height, 48 * 4, "BGRA8");
      }
      static async Task DrainUiAsync(Session session)
      {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session._window.DispatcherQueue.TryEnqueue(() => drained.SetResult()))
          throw new InvalidOperationException("Could not drain correction UI callbacks.");
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session._previewTask;
      }
      var sessions = new[] { CreateSelfCheckSession(), CreateSelfCheckSession(), CreateSelfCheckSession() };
      Session session = sessions[0];
      var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Task worker = Task.CompletedTask;
      try
      {
        await session.ObserveFrameAsync(Periodic(96, 0));
        await session.ObserveFrameAsync(Periodic(96, 0));
        await session._previewTask;
        await session.ObserveFrameAsync(Periodic(96, 2));
        await session.ObserveFrameAsync(Periodic(96, 2));
        await session._previewTask;
        session._offsetBox.Value = 10;
        session.OnConfirm(session, new RoutedEventArgs());
        worker = Task.Run(() => session.ApplyConfirmedOffset(afterClaimForTest: () =>
        {
          claimed.SetResult();
          release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }));
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.OnConfirm(session, new RoutedEventArgs()); // 匹配过程中重复点击同一个确认。
        release.SetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        await DrainUiAsync(session);
        CapturedFrame accepted = session._state.Stitcher!.BuildFrame();
        CapturedFrame next = Periodic(96, 4);
        await session.ObserveFrameAsync(next);
        await session.ObserveFrameAsync(next);
        await session._previewTask;
        session.ApplyConfirmedOffset(); // B 尚未确认，不能消费 A 的重复点击。
        CapturedFrame unconfirmed = session._state.Stitcher.BuildFrame();
        if (session._state.Stitcher.FrameCount != 2 || unconfirmed.Height != 106 ||
            !unconfirmed.Pixels.SequenceEqual(accepted.Pixels) || session._state.Review is null ||
            session._state.Review?.Pending?.Pixels.SequenceEqual(next.Pixels) != true || !session._confirmButton.IsEnabled ||
            session._state.Completion.IsCompleted)
          throw new InvalidOperationException($"A repeated confirmation leaked to the next frame: height={unconfirmed.Height}, frames={session._state.Stitcher.FrameCount}.");
        session._offsetBox.Value = 2;
        session.OnConfirm(session, new RoutedEventArgs());
        session.ApplyConfirmedOffset();
        await session._previewTask;
        CapturedFrame confirmed = session._state.Stitcher.BuildFrame();
        if (session._state.Stitcher.FrameCount != 3 || confirmed.Height != 108 ||
            !confirmed.Pixels.SequenceEqual(Periodic(108, 0).Pixels) || session._state.Review is not null ||
            session._state.Review?.Pending is not null || session._state.Completion.IsCompleted)
          throw new InvalidOperationException("Only B's own confirmed offset may extend the accepted segment to 108 pixels.");
        for (int index = 1; index < sessions.Length; index++)
        {
          session = sessions[index];
          await session.ObserveFrameAsync(Periodic(96, 0));
          await session.ObserveFrameAsync(Periodic(96, 0));
          await session._previewTask;
          await session.ObserveFrameAsync(Periodic(96, 2));
          await session.ObserveFrameAsync(Periodic(96, 2));
          await session._previewTask;
          session._offsetBox.Value = 10;
          session.OnConfirm(session, new RoutedEventArgs());
          claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          worker = Task.Run(() => session.ApplyConfirmedOffset(afterMatchForTest: () =>
          {
            claimed.SetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
          }));
          await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
          var original = new InvalidOperationException("source disappeared while correction was matching");
          if (index == 1) session.CancelFromUi();
          else session.Fail(original);
          release.SetResult();
          await worker.WaitAsync(TimeSpan.FromSeconds(5));
          await DrainUiAsync(session);
          if (session._state.HasCorrection || session._confirmButton.IsEnabled ||
              session._finishButton.IsEnabled || session._state.Review is null || session._state.Review?.Pending is null)
            throw new InvalidOperationException("A stopped correction must not resume capture or enable stale output actions.");
          if (index == 1)
          {
            if (await session._state.Completion is not null)
              throw new InvalidOperationException("Cancellation while matching must discard all captured output.");
          }
          else
          {
            if (!ReferenceEquals(session._state.Failure, original) || session._errorText.Text != original.Message)
              throw new InvalidOperationException("A matching completion must retain the original visible failure.");
            session.CancelFromUi();
            try { await session._state.Completion; throw new InvalidOperationException("Failed correction returned output."); }
            catch (Exception error) when (ReferenceEquals(error, original)) { }
          }
        }
      }
      finally
      {
        release.TrySetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (Session owned in sessions)
        {
          owned._state.Stop();
          await owned._previewTask;
          owned._window.Close();
          owned._state.Dispose();
        }
      }
    }

    // 无窗口自检：关闭队列仅记录/拒绝操作，不创建 WinUI 控件或占用桌面。
    internal static async Task VerifyExternalCancellationSelfCheckAsync(bool? acceptsQueue, bool closeBeforeCallback, bool failFirst)
    {
      static async Task Verify(bool? acceptsQueue, bool closeBeforeCallback = false, Exception? failure = null)
      {
        using var external = new CancellationTokenSource();
        var session = new Session(1, new PhysicalRectangle(0, 0, 48, 96), external.Token);
        Action? queuedClose = null;
        bool EnqueueClose(Action close)
        {
          if (acceptsQueue == true) queuedClose = close;
          return acceptsQueue == true;
        }
        if (failure is not null) session.Fail(failure);
        using CancellationTokenRegistration registration = external.Token.Register(() =>
        {
          if (closeBeforeCallback) session.CancelFromUi();
          if (acceptsQueue is null) session.OnExternalCancelled();
          else session.OnExternalCancelled(EnqueueClose);
        });
        try
        {
          external.Cancel();
          // 即使 UI 先处理关闭或永远不执行排队操作，终态也必须已经确定。
          if (acceptsQueue == true) session.CancelFromUi();
          if (!session._state.Completion.IsCompleted)
            throw new InvalidOperationException("External cancellation waited for the UI close queue.");
          if (failure is not null)
          {
            try { await session._state.Completion; }
            catch (Exception error) when (ReferenceEquals(error, failure)) { return; }
            throw new InvalidOperationException("External cancellation lost the first failure.");
          }
          if (!session._state.Completion.IsCanceled)
            throw new InvalidOperationException("External cancellation became a null user cancellation.");
          queuedClose?.Invoke();
          try { await session._state.Completion; }
          catch (OperationCanceledException error) when (error.CancellationToken == external.Token) { return; }
          throw new InvalidOperationException("External cancellation lost its token.");
        }
        finally { session._state.Dispose(); }
      }
      await Verify(acceptsQueue, closeBeforeCallback, failFirst ? new InvalidOperationException("first failure") : null);
      var user = new Session(1, new PhysicalRectangle(0, 0, 48, 96), CancellationToken.None);
      try
      {
        user.CancelFromUi();
        if (await user._state.Completion is not null)
          throw new InvalidOperationException("User cancellation returned partial output.");
      }
      finally { user._state.Dispose(); }
    }

  }
}
