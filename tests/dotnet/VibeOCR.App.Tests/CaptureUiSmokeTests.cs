using System.Diagnostics;
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
  public Task ScrollCaptureAllowsNarrowHorizontalSelectionAndEnforcesAxisLimits() =>
      RunUiSmokeAsync(nameof(ScrollCaptureAllowsNarrowHorizontalSelectionAndEnforcesAxisLimits));

  [Fact]
  public Task ScrollCaptureReverseFrameCanBeDiscardedBeforeForwardResume() =>
      RunUiSmokeAsync(nameof(ScrollCaptureReverseFrameCanBeDiscardedBeforeForwardResume));

  [Fact]
  public Task ScrollCapturePendingPreviewBoundsBothDimensionsWithoutUpscaling() =>
      RunUiSmokeAsync(nameof(ScrollCapturePendingPreviewBoundsBothDimensionsWithoutUpscaling));

  [Fact]
  public Task ScrollCapturePreviewWaitsForDecodeAndRejectsPendingEncodingFailure() =>
      RunUiSmokeAsync(nameof(ScrollCapturePreviewWaitsForDecodeAndRejectsPendingEncodingFailure));

  [Fact]
  public Task ScrollCaptureAutoScrollRequiresPreviewAndReturnsConfirmedSegment() =>
      RunUiSmokeAsync(nameof(ScrollCaptureAutoScrollRequiresPreviewAndReturnsConfirmedSegment));

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
    using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start UI smoke.");
    try
    {
      await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(
          TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
      string result = Path.Combine(_root, "result.txt");
      string detail = File.Exists(result) ? File.ReadAllText(result) : "No UI smoke result.";
      Assert.True(child.ExitCode == 0, $"UI smoke exit={child.ExitCode}: {detail}");
      Assert.Equal("passed", detail);
    }
    finally
    {
      if (!child.HasExited)
      {
        child.Kill(entireProcessTree: true);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await child.WaitForExitAsync(cleanup.Token);
      }
    }
  }

  public void Dispose()
  {
    if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
  }
}
