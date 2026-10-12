using System.Runtime.InteropServices;
using System.Text.Json;
using VibeOCR.App.Workbench;
using VibeOCR.App.Web;
using WinRT.Interop;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindow(nint window);

  private async Task<Guid> CaptureReplacementRequestAsync()
  {
    Guid? previous = commandHandler.CurrentImageSessionId;
    await CaptureScreenshotForEditAsync();
    RecognitionWorkbenchState state = await WaitForScreenshotStateAsync(
      item => !item.IsBusy && item.ScreenshotSession is not null &&
        item.ScreenshotSession.SessionId != previous?.ToString("N"), TimeSpan.FromSeconds(30));
    Guid session = Guid.ParseExact(state.ScreenshotSession!.SessionId, "N");
    await WaitForImageEditorRequestAsync(session).WaitAsync(TimeSpan.FromSeconds(10));
    return session;
  }

  private async Task<ImageEditWindow> CaptureReplacementEditorAsync()
  {
    Guid session = await CaptureReplacementRequestAsync();
    await imageEditorCreation.WaitAsync(TimeSpan.FromSeconds(10));
    ImageEditWindow editor = imageEditor ?? throw new InvalidOperationException("Real editor missing.");
    if (editor.SessionId != session) throw new InvalidOperationException("Wrong editor session.");
    await editor.Presented.WaitAsync(TimeSpan.FromSeconds(10));
    return editor;
  }

  private static async Task ReloadCaptureHostAsync(WebWorkbenchHost host, string ready)
  {
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void Observe(string state) { if (state == ready) completed.TrySetResult(); }
    host.StateChanged += Observe;
    try { host.Reload(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
    finally { host.StateChanged -= Observe; }
  }

  private async Task CompleteCaptureReplacementSmokeAsync()
  {
    string? path = Environment.GetEnvironmentVariable("VIBEOCR_CAPTURE_REPLACEMENT_HEALTH");
    string expected = Path.Combine(Directory.GetParent(layout.InstallRoot)!.FullName,
      "capture-replacement-health.json");
    if (path is null || !string.Equals(Path.GetFullPath(path), expected,
        StringComparison.OrdinalIgnoreCase)) { Close(); return; }
    List<object> rows = [];
    CaptureResourceCounts? baseline = null;
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    bool recorded = false;
    void WriteHealth(string state, string? error)
    {
      recorded = true;
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1, smoke = "capture-replacement-e2e", state, error, baseline, rows,
        submit_attempts = smokeSubmitAttempts?.Invoke(),
        startup_ensure_attempts = smokeStartupEnsureAttempts?.Invoke(),
        installer_attempts = smokeInstallAttempts?.Invoke(),
        inference_attached = smokeInferenceAttached?.Invoke(),
        renderer_failure = "UNVERIFIED: Reload is real; no ProcessFailed was induced by this smoke.",
      }));
    }
    void Require(bool condition, string message)
    {
      if (!condition) throw new InvalidOperationException(message);
    }
    void RequireHeld(ImageEditWindow retired)
    {
      CaptureResourceCounts held = CaptureCounts();
      Require(imageEditor is null && !imageEditorCreation.IsCompleted &&
        held.EditorWebViews == 1 && held.TotalWebViews == 2 &&
        held.Subscriptions == baseline!.Subscriptions &&
        !retired.Host.HasSubscription && !retired.Host.HasBridgeBinding && !retired.Host.HasWebViewBindings,
        "Replacement created a control before the held native disposal drained.");
    }
    async Task CloseCurrentAsync(ImageEditWindow editor)
    {
      editor.Close();
      await editor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
      await WaitForScreenshotStateAsync(item => item.ScreenshotSession is null, TimeSpan.FromSeconds(5));
      Require(CaptureCounts() == baseline, "Close did not restore real resource counts.");
    }
    try
    {
      Require(screenshotSmokePicker is not null && smokeSubmitAttempts is not null &&
        smokeStartupEnsureAttempts is not null && smokeInstallAttempts is not null && smokeInferenceAttached is not null,
        "Replacement dependencies missing.");
      await Task.Yield();
      baseline = CaptureCounts();
      Require(baseline.Subscriptions == 1 && baseline.EditorWebViews == 0 && baseline.TotalWebViews == 1,
        "Expected one main WebView.");
      await ReloadCaptureHostAsync(webHost, "bridge-ready");
      Require(CaptureCounts() == baseline, "Main Reload changed resource ownership.");
      rows.Add(new { phase = "main-reload", counts = CaptureCounts() });

      ImageEditWindow old = await CaptureReplacementEditorAsync();
      int restorations = 0;
      old.CaptureScene!.RestoreOwnerOnClose(() => restorations++);
      Task held = old.HoldDisposalForSmoke(release.Task);
      Guid intermediate = await CaptureReplacementRequestAsync();
      await held.WaitAsync(TimeSpan.FromSeconds(5));
      RequireHeld(old);
      nint intermediateWindow = WindowNative.GetWindowHandle(pendingImageEditor!.Scene!.Window);
      Guid latest = await CaptureReplacementRequestAsync();
      RequireHeld(old);
      Require(!IsWindow(intermediateWindow) && restorations == 0 &&
        pendingImageEditor?.SessionId == latest && latest != intermediate,
        "Superseded pending scene retained a native window or restored the owner early.");
      rows.Add(new { phase = "two-replacements-held", intermediate, latest, restorations, counts = CaptureCounts() });
      release.TrySetResult();
      await imageEditorCreation.WaitAsync(TimeSpan.FromSeconds(5));
      ImageEditWindow current = imageEditor ?? throw new InvalidOperationException("Latest editor missing.");
      await current.Presented.WaitAsync(TimeSpan.FromSeconds(10));
      Require(current.SessionId == latest && old.SessionId != latest && ImageEditWindow.ActiveWebViewCount == 1,
        "Old closure removed or overlapped the latest session.");
      await VerifyCaptureFrameAsync(current, current.CaptureScene!);
      await ReloadCaptureHostAsync(current.Host, "capture-pixel-ready");
      await VerifyCaptureFrameAsync(current, current.CaptureScene!);
      rows.Add(new { phase = "latest-editor-and-reload", latest, counts = CaptureCounts() });
      await CloseCurrentAsync(current);
      Require(restorations == 1, "Transferred owner was not restored exactly once.");

      // Cancellation while native disposal is still held must discard the pending
      // scene, then let the same drain finish without creating an obsolete editor.
      old = await CaptureReplacementEditorAsync();
      int cancelledRestorations = 0;
      old.CaptureScene!.RestoreOwnerOnClose(() => cancelledRestorations++);
      release = new(TaskCreationOptions.RunContinuationsAsynchronously);
      held = old.HoldDisposalForSmoke(release.Task);
      await CaptureReplacementRequestAsync();
      await held.WaitAsync(TimeSpan.FromSeconds(5));
      RequireHeld(old);
      nint cancelledWindow = WindowNative.GetWindowHandle(pendingImageEditor!.Scene!.Window);
      pendingImageEditor!.Scene!.Window.Close();
      await WaitForScreenshotStateAsync(item => item.ScreenshotSession is null, TimeSpan.FromSeconds(5));
      Require(pendingImageEditor is null && !IsWindow(cancelledWindow) && cancelledRestorations == 1,
        "Cancelled pending scene or owner responsibility survived.");
      release.TrySetResult();
      await editorRetirement.Drain.WaitAsync(TimeSpan.FromSeconds(5));
      await imageEditorCreation.WaitAsync(TimeSpan.FromSeconds(5));
      Require(imageEditor is null && CaptureCounts() == baseline, "Cancel created a stale editor or retained resources.");
      rows.Add(new { phase = "pending-cancelled", restorations = cancelledRestorations, counts = CaptureCounts() });

      old = await CaptureReplacementEditorAsync();
      int shutdownRestorations = 0;
      old.CaptureScene!.RestoreOwnerOnClose(() => shutdownRestorations++);
      release = new(TaskCreationOptions.RunContinuationsAsynchronously);
      held = old.HoldDisposalForSmoke(release.Task);
      await CaptureReplacementRequestAsync();
      await held.WaitAsync(TimeSpan.FromSeconds(5));
      RequireHeld(old);
      nint shutdownWindow = WindowNative.GetWindowHandle(pendingImageEditor!.Scene!.Window);
      captureReplacementShutdownVerification = () =>
      {
        try
        {
          CaptureResourceCounts closed = CaptureCounts();
          Require(closed.Subscriptions == 0 && closed.Resources == 0 && closed.AnnotationEntries == 0 &&
            closed.AnnotationReservations == 0 && closed.AnnotationOwnedFiles == 0 &&
            closed.ResourceFiles == baseline!.ResourceFiles && closed.EditorWebViews == 0 && closed.TotalWebViews == 0 &&
            !IsWindow(shutdownWindow) && shutdownRestorations == 1 && pendingImageEditor is null,
            "Shutdown retained a window, resource, subscription or owner callback.");
          Require(smokeSubmitAttempts!() == 0 && smokeStartupEnsureAttempts!() == 0 &&
            smokeInstallAttempts!() == 0 && !smokeInferenceAttached!(), "Pure lifecycle created Runtime demand.");
          rows.Add(new { phase = "shutdown-drained", restorations = shutdownRestorations, counts = closed });
          WriteHealth("passed", null);
        }
        catch (Exception error) { WriteHealth("failed", error.ToString()); throw; }
      };
      Close();
      await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
      Require(pendingImageEditor is null && !IsWindow(shutdownWindow) &&
        !ShutdownCompletion.IsCompleted && ImageEditWindow.ActiveWebViewCount == 1,
        "Shutdown skipped the shared native drain.");
      rows.Add(new { phase = "shutdown-held", restorations = shutdownRestorations, counts = CaptureCounts() });
      release.TrySetResult();
      await ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (Exception error) { WriteHealth("failed", error.ToString()); }
    finally
    {
      release.TrySetResult();
      if (!recorded) WriteHealth("failed", "Lifecycle did not produce final evidence.");
      if (!shuttingDown) { captureReplacementShutdownVerification = null; Close(); }
    }
  }
}
