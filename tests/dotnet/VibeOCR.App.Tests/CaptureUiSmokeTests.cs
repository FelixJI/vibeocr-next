using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using VibeOCR.App.Inference;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.App.Features.Recognition;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>在独立真实 WinUI App 进程执行 UI 断言；testhost 不创建 XAML Application。</summary>
public sealed class CaptureUiSmokeTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), $"vibeocr-capture-ui-{Guid.NewGuid():N}");

  [Fact]
  public Task OptionsDialogSavesMergedOptionsAndKeepsFreshLastRegion() =>
      RunUiSmokeAsync(nameof(OptionsDialogSavesMergedOptionsAndKeepsFreshLastRegion));

  [Fact]
  public Task OptionsDialogShowsSaveErrorsAndFallsBackForInvalidInput() =>
      RunUiSmokeAsync(nameof(OptionsDialogShowsSaveErrorsAndFallsBackForInvalidInput));

  [Fact]
  public Task DelayedOverlayCancelStopsCountdownAndReturnsFalse() =>
      RunUiSmokeAsync(nameof(DelayedOverlayCancelStopsCountdownAndReturnsFalse));

  [Fact]
  public Task DelayedOverlayCompletesAfterConfiguredSeconds() =>
      RunUiSmokeAsync(nameof(DelayedOverlayCompletesAfterConfiguredSeconds));

  [Fact]
  public Task RegionPickerRejectsChangedMonitorLayoutWithSameDesktopBounds() =>
      RunUiSmokeAsync(nameof(RegionPickerRejectsChangedMonitorLayoutWithSameDesktopBounds));

  [Fact]
  public Task ScrollCaptureCorrectionAuthorizationStaysWithItsPendingFrame() =>
      RunUiSmokeAsync(nameof(ScrollCaptureCorrectionAuthorizationStaysWithItsPendingFrame));

  [Fact]
  public Task ScrollCaptureAmbiguousChoicesPreserveStateAndAllowFeasibleCorrection() =>
      RunUiSmokeAsync(nameof(ScrollCaptureAmbiguousChoicesPreserveStateAndAllowFeasibleCorrection));

  [Fact]
  public Task ScrollCaptureNoOverlapStopsWithoutImpossibleCorrection() =>
      RunUiSmokeAsync(nameof(ScrollCaptureNoOverlapStopsWithoutImpossibleCorrection));

  [Fact]
  public Task ScrollCaptureKeepsFirstFailureAcrossDeadlineAndConcurrentStops() =>
      RunUiSmokeAsync(nameof(ScrollCaptureKeepsFirstFailureAcrossDeadlineAndConcurrentStops));

  [Fact]
  public Task ScrollCaptureAllowsNarrowHorizontalSelectionAndEnforcesAxisLimits() =>
      RunUiSmokeAsync(nameof(ScrollCaptureAllowsNarrowHorizontalSelectionAndEnforcesAxisLimits));

  [Fact]
  public Task ScrollCaptureReverseFrameCanBeDiscardedBeforeForwardResume() =>
      RunUiSmokeAsync(nameof(ScrollCaptureReverseFrameCanBeDiscardedBeforeForwardResume));

  [Fact]
  public Task ScrollCapturePendingPreviewBoundsBothDimensionsWithoutUpscaling() =>
      RunUiSmokeAsync(nameof(ScrollCapturePendingPreviewBoundsBothDimensionsWithoutUpscaling));

  [Fact]
  public Task ScrollCaptureAcceptedPreviewSkipsBusyRendererAndBoundsAllocation() =>
      RunUiSmokeAsync(nameof(ScrollCaptureAcceptedPreviewSkipsBusyRendererAndBoundsAllocation));

  [Fact]
  public Task ScrollCapturePreviewResumeGetsFreshDynamicWindowWithoutResettingBudgets() =>
      RunUiSmokeAsync(nameof(ScrollCapturePreviewResumeGetsFreshDynamicWindowWithoutResettingBudgets));

  [Fact]
  public Task ScrollCapturePreviewWaitsForDecodeAndRejectsPendingEncodingFailure() =>
      RunUiSmokeAsync(nameof(ScrollCapturePreviewWaitsForDecodeAndRejectsPendingEncodingFailure));

  [Fact]
  public Task ScrollCaptureAutoScrollRequiresPreviewAndReturnsConfirmedSegment() =>
      RunUiSmokeAsync(nameof(ScrollCaptureAutoScrollRequiresPreviewAndReturnsConfirmedSegment));

  [Theory]
  [InlineData(null, false, false)] // DispatcherQueue 不存在。
  [InlineData(false, false, false)] // DispatcherQueue 拒绝入队。
  [InlineData(true, false, false)] // UI 关闭先于已入队但尚未执行的关闭操作。
  [InlineData(true, true, false)] // token 已取消，UI 关闭先于注册回调。
  [InlineData(false, false, true)] // 已发生的首次 failure 优先。
  public Task ScrollCaptureCancellationCompletesBeforeUiDispatchAndKeepsFirstFailure(
      bool? acceptsQueue, bool closeBeforeCallback, bool failFirst) =>
      ScrollCaptureSession.Session.VerifyExternalCancellationSelfCheckAsync(acceptsQueue, closeBeforeCallback, failFirst);

  [Fact]
  public Task ScrollCaptureCancellationDiscardsFramesAndClosesController() =>
      RunUiSmokeAsync(nameof(ScrollCaptureCancellationDiscardsFramesAndClosesController));

  [Fact]
  public Task ScrollCaptureVanishedSourceFailsWithoutReturningPartialFrame() =>
      RunUiSmokeAsync(nameof(ScrollCaptureVanishedSourceFailsWithoutReturningPartialFrame));
  [Fact]
  public void ActiveWindowResolutionRejectsVanishedTargets()
  {
    var request = new ScreenshotCaptureRequest(ScreenshotCaptureKind.ActiveWindow);
    CaptureValidationException vanishedEntry = Assert.Throws<CaptureValidationException>(() =>
        InputService.ResolveActiveWindow(new CapturePreferences(), request, owner: 0, foreground: 0));
    Assert.Equal(CaptureRefusal.WindowUnavailable, vanishedEntry.ReasonCode);
    var vanished = request with { TargetWindow = unchecked((nint)0xDEAD) };
    CaptureValidationException vanishedTarget = Assert.Throws<CaptureValidationException>(() =>
        InputService.ResolveActiveWindow(new CapturePreferences(), vanished, owner: 0, foreground: 0));
    Assert.Equal(CaptureRefusal.WindowUnavailable, vanishedTarget.ReasonCode);
  }

  [Fact]
  public async Task DelayedOverlayEscKeyCancelsAndRestoresForeground() =>
      await RunUiSmokeAsync(nameof(DelayedOverlayEscKeyCancelsAndRestoresForeground));

  /// <summary>
  /// #245 C3 可见状态链：本地截图校验拒绝不再落入 Supervisor 断开处理器，
  /// 而是以稳定 captureRefused 状态码与既有中文指引呈现。
  /// </summary>
  [Fact]
  public async Task CaptureRefusalSurfacesStableStatusCodeNotSupervisorBreak()
  {
    var inputs = new RefusingInputService();
    var viewModel = new RecognitionViewModel(new DeferredInferenceClient(), inputs);

    await viewModel.CaptureScreenshotSessionAsync(
        null,
        new ScreenshotCaptureRequest(ScreenshotCaptureKind.FixedRegion),
        TestContext.Current.CancellationToken);

    Assert.Equal(JobState.Failed, viewModel.TerminalState);
    Assert.Equal(
        "recognition.captureRefused." + CaptureRefusal.MissingFixedRegion,
        viewModel.CaptureRefusalStatusCode);
    Assert.Equal("尚未设置固定截图区域，请先在截图选项中设置。", viewModel.Status);
  }

  private sealed class RefusingInputService : IInputService
  {
    public Task<RecognitionInput?> CaptureAreaAsync(
        ScreenshotCaptureRequest request, CancellationToken cancellationToken) =>
        throw new CaptureValidationException(
            CaptureRefusal.MissingFixedRegion, "尚未设置固定截图区域，请先在截图选项中设置。");

    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken)
        => Task.FromResult<RecognitionInput?>(new RecognitionInput([1], "image/png", "file.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken)
        => PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken)
        => PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken)
        => PickFileAsync(cancellationToken);
  }

  private async Task RunUiSmokeAsync(string scenario)
  {
    Directory.CreateDirectory(_root);
    var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "VibeOCR.WinUI.exe"))
    {
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    start.Environment["VIBEOCR_SELF_TEST_SMOKE"] = "capture-ui";
    start.Environment["VIBEOCR_CAPTURE_UI_ROOT"] = _root;
    start.Environment["VIBEOCR_CAPTURE_UI_SCENARIO"] = scenario;
    var elapsed = Stopwatch.StartNew();
    using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start UI smoke.");
    Task exited = child.WaitForExitAsync(TestContext.Current.CancellationToken);
    await CompleteUiSmokeAsync(async () =>
    {
      await exited.WaitAsync(
          TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
      string result = Path.Combine(_root, "result.txt");
      string detail = File.Exists(result) ? File.ReadAllText(result) : "No UI smoke result.";
      Assert.True(child.ExitCode == 0, $"UI smoke exit={child.ExitCode}: {detail}");
      Assert.Equal("passed", detail);
      TestContext.Current.TestOutputHelper!.WriteLine(ReadUiSmokeDiagnostics(child, exited, _root));
    }, async () =>
    {
      if (!child.HasExited)
      {
        child.Kill(entireProcessTree: true);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await child.WaitForExitAsync(cleanup.Token);
      }
    }, error => TestContext.Current.TestOutputHelper!.WriteLine(
        $"UI smoke scenario={scenario}, failure={error.GetType().Name}, elapsedMs={elapsed.ElapsedMilliseconds}\n" +
        ReadUiSmokeDiagnostics(child, exited, _root)));
  }

  private static async Task CompleteUiSmokeAsync(
      Func<Task> body, Func<Task> cleanup, Action<Exception> reportFailure)
  {
    Exception? failure = null;
    try { await body(); }
    catch (Exception error)
    {
      failure = error;
      try { reportFailure(error); }
      catch (Exception diagnosticError)
      {
        failure = new AggregateException("UI smoke failure diagnostics also failed.", error, diagnosticError);
      }
    }
    try { await cleanup(); }
    catch (Exception cleanupError)
    {
      if (failure is not null)
        throw new AggregateException("UI smoke and process cleanup both failed.", failure, cleanupError);
      throw;
    }
    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
  }

  private static string ReadUiSmokeDiagnostics(Process child, Task exited, string root)
  {
    string state;
    try
    {
      bool hasExited = child.HasExited;
      state = $"pid={child.Id}, hasExited={hasExited}, exitCode={(hasExited ? child.ExitCode.ToString() : "<running>")}";
    }
    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
      state = $"processStateUnavailable={error.GetType().Name}";
    }
    return $"{state}, exitWait={exited.Status}\n" +
        $"stages (tail):\n{ReadDiagnosticFile(Path.Combine(root, "stages.txt"), tail: true)}\n" +
        $"result (head):\n{ReadDiagnosticFile(Path.Combine(root, "result.txt"), tail: false)}";
  }

  private static string ReadDiagnosticFile(string path, bool tail)
  {
    const int limit = 4096;
    try
    {
      using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
          FileShare.ReadWrite | FileShare.Delete);
      bool truncated = file.Length > limit;
      if (tail && truncated) file.Seek(-limit, SeekOrigin.End);
      byte[] bytes = new byte[limit];
      int count = file.Read(bytes);
      return Encoding.UTF8.GetString(bytes, 0, count) + (truncated ? "\n<truncated>" : "");
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
      return $"<unavailable: {error.GetType().Name}>";
    }
  }

  [Fact]
  public async Task UiSmokeTimeoutIsReportedBeforeCleanupWithoutReplacingOriginalError()
  {
    var expected = new TimeoutException("UI smoke did not exit");
    var events = new List<string>();
    TimeoutException actual = await Assert.ThrowsAsync<TimeoutException>(() => CompleteUiSmokeAsync(
        () => Task.FromException(expected),
        () => { events.Add("cleanup"); return Task.CompletedTask; },
        error => { Assert.Same(expected, error); events.Add("diagnostics"); }));
    Assert.Same(expected, actual);
    Assert.Equal(new[] { "diagnostics", "cleanup" }, events);
  }

  [Fact]
  public async Task UiSmokePreservesTimeoutAndCleanupFailure()
  {
    var expected = new TimeoutException("UI smoke did not exit");
    var cleanup = new IOException("process cleanup failed");
    AggregateException actual = await Assert.ThrowsAsync<AggregateException>(() => CompleteUiSmokeAsync(
        () => Task.FromException(expected), () => Task.FromException(cleanup), _ => { }));
    Assert.Equal(new Exception[] { expected, cleanup }, actual.InnerExceptions);
  }

  [Fact]
  public void UiSmokeDiagnosticsIncludeBoundedStageTailResultHeadAndProcessState()
  {
    Directory.CreateDirectory(_root);
    File.WriteAllText(Path.Combine(_root, "stages.txt"), new string('s', 8192) + "last-stage");
    File.WriteAllText(Path.Combine(_root, "result.txt"), "original-result" + new string('r', 8192));
    using Process current = Process.GetCurrentProcess();
    string diagnostics = ReadUiSmokeDiagnostics(current, Task.CompletedTask, _root);
    Assert.Contains($"pid={current.Id}, hasExited=False", diagnostics);
    Assert.Contains("exitWait=RanToCompletion", diagnostics);
    Assert.Contains("last-stage", diagnostics);
    Assert.Contains("original-result", diagnostics);
    Assert.Contains("<truncated>", diagnostics);
    Assert.True(diagnostics.Length < 8500);
  }

  public void Dispose()
  {
    if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
  }
}
