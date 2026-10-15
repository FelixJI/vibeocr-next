using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App;

public sealed partial class App
{
  // 独立应用进程具有 Program.Main 的 STA/XAML 初始化；不在 xunit 宿主内启动第二个 Application。
  private async Task RunCaptureUiSmokeAsync()
  {
    string root = Environment.GetEnvironmentVariable("VIBEOCR_CAPTURE_UI_ROOT")
        ?? throw new InvalidOperationException("Capture UI smoke requires its isolated root.");
    string result = Path.Combine(root, "result.txt");
    try
    {
      string scenario = Environment.GetEnvironmentVariable("VIBEOCR_CAPTURE_UI_SCENARIO") ?? "";
      var layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "production");
      layout.EnsurePortableState();
      var store = new CapturePreferencesStore(layout);
      switch (scenario)
      {
        case "OptionsDialogSavesMergedOptionsAndKeepsFreshLastRegion":
          VerifyCaptureOptionsMerge(store);
          break;
        case "OptionsDialogShowsSaveErrorsAndFallsBackForInvalidInput":
          VerifyCaptureOptionsErrors(store);
          break;
        case "DelayedOverlayCancelStopsCountdownAndReturnsFalse":
          await VerifyDelayedCaptureCancellationAsync();
          break;
        case "DelayedOverlayCompletesAfterConfiguredSeconds":
          Check(await DelayedCaptureOverlay.RunAsync(1, CancellationToken.None),
              "Countdown must complete after its configured delay.");
          break;
        default:
          throw new InvalidOperationException("Unknown capture UI smoke scenario.");
      }
      File.WriteAllText(result, "passed");
      Environment.Exit(0);
    }
    catch (Exception error)
    {
      File.WriteAllText(result, error.ToString());
      Environment.Exit(1);
    }
  }

  private static void VerifyCaptureOptionsMerge(CapturePreferencesStore store)
  {
    store.SaveLastRegion(new PhysicalRectangle(5, 6, 100, 80));
    var dialog = new CaptureOptionsDialog(store);
    try
    {
      dialog.Show(0);
      store.SaveLastRegion(new PhysicalRectangle(9, 9, 42, 24));
      dialog.DelayInput.Value = 8;
      dialog.CursorInput.IsOn = true;
      dialog.BoundsInput.SelectedIndex = 1;
      dialog.FixedXInput.Value = -12;
      dialog.FixedYInput.Value = -34;
      dialog.FixedWInput.Value = 320;
      dialog.FixedHInput.Value = 240;
      Check(dialog.TrySave() is null, "Valid options must save.");
      CapturePreferences loaded = store.Load();
      Check(loaded.EffectiveDelaySeconds == 8, "Delay must round-trip.");
      Check(loaded.IncludeCursor, "Cursor option must round-trip.");
      Check(loaded.EffectiveWindowBoundsMode == ScreenTargetQuery.WindowBoundsClient,
          "Client bounds option must round-trip.");
      Check(loaded.FixedRegion == new PhysicalRectangle(-12, -34, 320, 240),
          "Fixed region must retain negative coordinates.");
      Check(loaded.LastRegion?.Bounds == new PhysicalRectangle(9, 9, 42, 24),
          "Saving options must preserve a newer last region.");
    }
    finally { CloseSmokeDialog(dialog); }
  }

  private static void VerifyCaptureOptionsErrors(CapturePreferencesStore store)
  {
    var failing = new CaptureOptionsDialog(new CaptureSmokeFailingStore());
    var invalid = new CaptureOptionsDialog(store);
    var nan = new CaptureOptionsDialog(store);
    try
    {
      failing.Show(0);
      failing.DelayInput.Value = 9;
      Check(failing.TrySave() is not null, "Storage failure must be reported.");
      Check(failing.ErrorText.Visibility == Visibility.Visible, "Storage error must be visible.");
      Check(failing.Window is not null && failing.DelayInput.Value == 9,
          "Failed save must preserve window and pending input.");

      invalid.Show(0);
      invalid.DelayInput.Value = double.NaN;
      invalid.FixedWInput.Value = -5;
      Check(invalid.TrySave() is not null && invalid.ErrorText.Text.Contains("正数", StringComparison.Ordinal),
          "Invalid region dimensions must be rejected visibly.");
      invalid.FixedWInput.Value = 100;
      invalid.FixedHInput.Value = 50;
      Check(invalid.TrySave() is null, "Corrected region must save.");

      nan.Show(0);
      nan.DelayInput.Value = double.NaN;
      Check(nan.TrySave() is null, "NaN delay must safely fall back.");
      Check(store.Load().EffectiveDelaySeconds == 3, "NaN delay must use the default.");
    }
    finally
    {
      CloseSmokeDialog(nan);
      CloseSmokeDialog(invalid);
      CloseSmokeDialog(failing);
    }
  }

  private static async Task VerifyDelayedCaptureCancellationAsync()
  {
    var owner = new Window { Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "capture smoke owner" } };
    owner.Activate();
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
    try
    {
      await WaitForCaptureUiAsync(() => ScreenTargetQuery.GetForegroundWindowHandle() == handle);
      var input = new InputService(() => handle);
      try
      {
        await input.CaptureAreaAsync(new ScreenshotCaptureRequest(
            ScreenshotCaptureKind.ActiveWindow, TargetWindow: unchecked((nint)0xDEAD)),
            CancellationToken.None);
        throw new InvalidOperationException("Vanished capture target was accepted.");
      }
      catch (InvalidDataException)
      {
        Check(IsWindowVisible(handle), "Direct capture failure must restore owner visibility.");
        Check(ScreenTargetQuery.GetForegroundWindowHandle() == handle,
            "Direct capture failure must restore owned foreground.");
      }
      using (var stopped = new CancellationTokenSource())
      {
        stopped.Cancel();
        try
        {
          await input.CaptureAreaAsync(new ScreenshotCaptureRequest(ScreenshotCaptureKind.Fullscreen),
              stopped.Token);
          throw new InvalidOperationException("Cancelled capture was accepted.");
        }
        catch (OperationCanceledException)
        {
          Check(IsWindowVisible(handle), "Cancelled direct capture must restore owner visibility.");
        }
      }
      var overlay = new DelayedCaptureOverlay(30);
      Task<bool> pending = overlay.ShowAsync(CancellationToken.None);
      await WaitForCaptureUiAsync(() => overlay.CancelButton.IsLoaded);
      var peer = new ButtonAutomationPeer(overlay.CancelButton);
      ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
      Check(!await pending, "Cancel button must stop countdown.");
      Check(ScreenTargetQuery.GetForegroundWindowHandle() == handle, "Cancel must restore owned foreground.");

      var closed = new DelayedCaptureOverlay(30);
      Task<bool> closing = closed.ShowAsync(CancellationToken.None);
      closed.Window.Close();
      Check(!await closing, "Closing the countdown window must cancel.");
      Check(ScreenTargetQuery.GetForegroundWindowHandle() == handle, "Close must restore owned foreground.");

      using var token = new CancellationTokenSource();
      Task<bool> cancelled = DelayedCaptureOverlay.RunAsync(30, token.Token);
      token.Cancel();
      Check(!await cancelled, "Cancellation token must stop countdown.");
      Check(ScreenTargetQuery.GetForegroundWindowHandle() == handle, "Token cancellation must restore foreground.");
    }
    finally { owner.Close(); }
  }

  private static async Task WaitForCaptureUiAsync(Func<bool> ready)
  {
    for (int attempt = 0; attempt < 40; attempt++)
    {
      if (ready()) return;
      await Task.Delay(25);
    }
    throw new InvalidOperationException("Capture UI did not reach its expected state.");
  }

  private static void CloseSmokeDialog(CaptureOptionsDialog dialog)
  {
    try { dialog.Window?.Close(); } catch (COMException) { }
  }

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindowVisible(nint window);

  private static void Check(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }

  private sealed class CaptureSmokeFailingStore : ICapturePreferencesStore
  {
    public CapturePreferences Load() => new();
    public void Save(CapturePreferences preferences) => throw new IOException("simulated disk full");
    public void SaveLastRegion(PhysicalRectangle bounds) =>
        throw new InvalidOperationException("Options dialog must not save last region.");
  }
}
