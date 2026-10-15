using System.Diagnostics;
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
  public void ActiveWindowResolutionRejectsVanishedTargets()
  {
    var request = new ScreenshotCaptureRequest(ScreenshotCaptureKind.ActiveWindow);
    Assert.Throws<InvalidDataException>(() =>
        InputService.ResolveActiveWindow(new CapturePreferences(), request, owner: 0, foreground: 0));
    var vanished = request with { TargetWindow = unchecked((nint)0xDEAD) };
    Assert.Throws<InvalidDataException>(() =>
        InputService.ResolveActiveWindow(new CapturePreferences(), vanished, owner: 0, foreground: 0));
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
