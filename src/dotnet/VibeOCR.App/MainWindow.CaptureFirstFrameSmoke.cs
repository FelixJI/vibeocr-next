using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Workbench;
using VibeOCR.App.Services;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  private sealed record CaptureResourceCounts(
    int Subscriptions, int Resources, int AnnotationEntries,
    int AnnotationReservations, int AnnotationOwnedFiles, int ResourceFiles,
    int EditorWebViews, int TotalWebViews);

  private CaptureResourceCounts CaptureCounts()
  {
    var annotations = annotationStore.ActiveCounts;
    int editors = ImageEditWindow.ActiveWebViewCount;
    return new(application.ActiveSubscriptionCount, resourceBroker.ActiveResourceCount,
      annotations.Entries, annotations.Reservations, annotations.OwnedFiles,
      Directory.GetFiles(resourceRoot, "*", SearchOption.AllDirectories).Length,
      editors, editors + (webHost.HasWebViewBindings ? 1 : 0));
  }

  private async Task CompleteCaptureFirstFrameSmokeAsync()
  {
    string? path = Environment.GetEnvironmentVariable("VIBEOCR_CAPTURE_FIRST_FRAME_HEALTH");
    string expected = Path.Combine(Directory.GetParent(layout.InstallRoot)!.FullName,
      "capture-first-frame-health.json");
    if (path is null || !string.Equals(Path.GetFullPath(path), expected,
      StringComparison.OrdinalIgnoreCase)) { Close(); return; }
    List<object> rows = [];
    CaptureDispatchProbe? probe = null;
    string? failure = null;
    CaptureResourceCounts? baseline = null;
    int callbacksAfterStop = -1;
    try
    {
      int cycles = Environment.GetEnvironmentVariable("VIBEOCR_CAPTURE_FIRST_FRAME_CYCLES") switch
      { "20" => 20, "1" => 1, _ => throw new InvalidOperationException("Expected 20 or 1 capture cycles.") };
      if (screenshotSmokePicker is null || smokeSubmitAttempts is null ||
          smokeStartupEnsureAttempts is null || smokeInferenceAttached is null || smokeInstallAttempts is null)
        throw new InvalidOperationException("Capture measurement dependencies are missing.");
      await Task.Yield();
      baseline = CaptureCounts();
      if (baseline.Subscriptions != 1 || baseline.EditorWebViews != 0 || baseline.TotalWebViews != 1)
        throw new InvalidOperationException("Expected one main bridge before capture.");
      probe = new CaptureDispatchProbe(DispatcherQueue);
      for (int cycle = 1; cycle <= cycles; cycle++)
      {
        CaptureResourceCounts before = CaptureCounts();
        if (before != baseline) throw new InvalidOperationException("Previous capture did not return to baseline.");
        long commandStarted = Stopwatch.GetTimestamp();
        long commandAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await CaptureScreenshotForEditAsync();
        RecognitionWorkbenchState state = await WaitForScreenshotStateAsync(
          item => !item.IsBusy && item.ScreenshotSession is not null, TimeSpan.FromSeconds(30));
        Guid sessionId = Guid.ParseExact(state.ScreenshotSession!.SessionId, "N");
        await WaitForImageEditorRequestAsync(sessionId).WaitAsync(TimeSpan.FromSeconds(10));
        await imageEditorCreation.WaitAsync(TimeSpan.FromSeconds(10));
        ImageEditWindow editor = imageEditor ?? throw new InvalidOperationException("Real capture editor missing.");
        long presented = await editor.Presented.WaitAsync(TimeSpan.FromSeconds(10));
        if (editor.SessionId.ToString("N") != state.ScreenshotSession?.SessionId || !editor.ReusesCaptureOverlay)
          throw new InvalidOperationException("Capture overlay/session handoff mismatch.");
        CaptureResourceCounts ready = CaptureCounts();
        if (ready.Subscriptions != baseline.Subscriptions + 1 || ready.EditorWebViews != 1 ||
            ready.TotalWebViews != 2 || ready.Resources != baseline.Resources + 1)
          throw new InvalidOperationException("Real editor resource bounds exceeded.");
        ScreenshotCaptureScene scene = editor.CaptureScene ??
          throw new InvalidOperationException("Capture scene missing.");
        object geometry = await VerifyCaptureFrameAsync(editor, scene);
        if (cycle == 1)
        {
          string previewPath = Path.Combine(Path.GetDirectoryName(path)!, "capture-first-frame-preview.png");
          File.Create(previewPath).Dispose();
          StorageFile previewFile = await StorageFile.GetFileFromPathAsync(previewPath);
          using IRandomAccessStream preview = await previewFile.OpenAsync(FileAccessMode.ReadWrite);
          await editor.WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
          await preview.FlushAsync();
        }
        long closeStarted = Stopwatch.GetTimestamp();
        editor.Close();
        await editor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForScreenshotStateAsync(item => item.ScreenshotSession is null,
          TimeSpan.FromSeconds(5));
        long disposed = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(closeStarted, disposed) > TimeSpan.FromSeconds(5) ||
            editor.Host.HasBridgeBinding || editor.Host.HasWebViewBindings || editor.Host.HasSubscription ||
            editor.Host.CaptureBackground is not null || imageEditor is not null ||
            CaptureCounts() != baseline || !webHost.HasBridgeBinding)
          throw new InvalidOperationException("Closed editor retained callbacks/resources or destroyed the main bridge.");
        if (smokeSubmitAttempts() != 0 || smokeStartupEnsureAttempts() != 0 ||
            smokeInstallAttempts() != 0 || smokeInferenceAttached())
          throw new InvalidOperationException("Pure capture created Runtime/OCR/Installer demand.");
        long closedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Task.Delay(1000);
        long settledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SyntheticScreenRegionPicker.CaptureEvidence capture = screenshotSmokePicker.Evidence ??
          throw new InvalidOperationException("Synthetic capture evidence missing.");
        rows.Add(new
        {
          cycle, temperature = cycle == 1 ? "cold" : "warm", command_at_ms = commandAt,
          closed_at_ms = closedAt, settled_at_ms = settledAt,
          synthetic_capture_ms = Stopwatch.GetElapsedTime(screenshotSmokePicker.CaptureStartedTimestamp, screenshotSmokePicker.CaptureCompletedTimestamp).TotalMilliseconds,
          capture_to_presented_ms = Stopwatch.GetElapsedTime(screenshotSmokePicker.CaptureStartedTimestamp, presented).TotalMilliseconds,
          capture_complete_to_presented_ms = Stopwatch.GetElapsedTime(screenshotSmokePicker.CaptureCompletedTimestamp, presented).TotalMilliseconds,
          editor_to_presented_ms = Stopwatch.GetElapsedTime(editor.CreatedTimestamp, presented).TotalMilliseconds,
          command_to_presented_ms = Stopwatch.GetElapsedTime(commandStarted, presented).TotalMilliseconds,
          dispose_ms = Stopwatch.GetElapsedTime(closeStarted, disposed).TotalMilliseconds,
          before, ready, closed = CaptureCounts(), capture, geometry,
          dispatch_lag_ms = probe.Drain(),
        });
      }
    }
    catch (Exception error) { failure = error.ToString(); }
    finally
    {
      if (probe is not null)
      {
        try { await probe.StopAsync(); callbacksAfterStop = probe.PendingCallbacks; }
        catch (Exception error) { failure = (failure is null ? "" : failure + "\n") + error; }
      }
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1, smoke = "capture-first-frame-e2e",
        state = failure is null ? "passed" : "failed", error = failure,
        baseline, rows, dispatch_tail_ms = probe?.Drain(), callbacks_after_stop = callbacksAfterStop,
        submit_attempts = smokeSubmitAttempts?.Invoke(),
        startup_ensure_attempts = smokeStartupEnsureAttempts?.Invoke(),
        installer_attempts = smokeInstallAttempts?.Invoke(),
        inference_attached = smokeInferenceAttached?.Invoke(),
        dpi = WindowGeometryPolicy.GetWindowScale(WindowNative.GetWindowHandle(this)) * 96,
        timing_endpoint = "source canvas/background ready; native WebView Opacity=1",
        limitations = new[] {
          "Synthetic owned WinUI capture and automatic selection; no human hotkey/selection duration.",
          "Cold editor after main bridge-ready in a fresh App/UDF; warm cycles still create a new editor.",
          "Dispatcher post latency is a 16ms single-flight sampled lower bound, not function CPU duration.",
          "WebView PNG corroborates content; physical display scanout is not timed.",
        },
      }));
      Close();
    }
  }

  private static async Task<object> VerifyCaptureFrameAsync(ImageEditWindow editor, ScreenshotCaptureScene scene)
  {
    string json = await editor.WebView.CoreWebView2.ExecuteScriptAsync("""
      (() => {
        const canvas=document.querySelector('canvas[aria-label="图片检查画布"]');
        const backdrop=document.querySelector('.capture-scene-backdrop img');
        if (!canvas || !backdrop?.complete || !backdrop.naturalWidth) return null;
        const r=canvas.getBoundingClientRect();
        const data=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data;
        let white=0,dark=0;
        for(let i=0;i<data.length;i+=4) {
          if(data[i]>245&&data[i+1]>245&&data[i+2]>245) white++;
          if(data[i]<40&&data[i+1]<40&&data[i+2]<40) dark++;
        }
        return {x:r.x,y:r.y,width:r.width,height:r.height,viewportWidth:innerWidth,
          viewportHeight:innerHeight,pixelWidth:canvas.width,pixelHeight:canvas.height,white,dark};
      })()
      """);
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement frame = document.RootElement;
    if (frame.ValueKind != JsonValueKind.Object || frame.GetProperty("white").GetInt32() < 10000 ||
        frame.GetProperty("dark").GetInt32() < 100)
      throw new InvalidOperationException("Real source canvas pixels are missing.");
    double sx = frame.GetProperty("viewportWidth").GetDouble() / scene.Desktop.Width;
    double sy = frame.GetProperty("viewportHeight").GetDouble() / scene.Desktop.Height;
    (string Name, double Expected)[] dimensions = [
      ("x", (scene.Bounds.X - scene.Desktop.X) * sx),
      ("y", (scene.Bounds.Y - scene.Desktop.Y) * sy),
      ("width", scene.Bounds.Width * sx), ("height", scene.Bounds.Height * sy),
    ];
    foreach (var dimension in dimensions)
      if (Math.Abs(frame.GetProperty(dimension.Name).GetDouble() - dimension.Expected) > 1)
        throw new InvalidOperationException("Real capture canvas does not match physical scene geometry.");
    return frame.Clone();
  }

  private sealed class CaptureDispatchProbe
  {
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<double> samples = [];
    private readonly Task worker;
    private int pendingCallbacks;
    internal int PendingCallbacks => Volatile.Read(ref pendingCallbacks);
    internal CaptureDispatchProbe(DispatcherQueue queue)
    {
      worker = Task.Run(async () =>
      {
        while (!cancellation.IsCancellationRequested)
        {
          long posted = Stopwatch.GetTimestamp();
          var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
          Interlocked.Increment(ref pendingCallbacks);
          if (!queue.TryEnqueue(() =>
          {
            Interlocked.Decrement(ref pendingCallbacks);
            completion.TrySetResult(Stopwatch.GetTimestamp());
          }))
          {
            Interlocked.Decrement(ref pendingCallbacks);
            throw new InvalidOperationException("UI dispatcher rejected the measurement callback.");
          }
          long handled = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
          lock (samples)
          {
            if (samples.Count >= 8192) throw new InvalidOperationException("Dispatch sample bound exceeded.");
            samples.Add(Stopwatch.GetElapsedTime(posted, handled).TotalMilliseconds);
          }
          try { await Task.Delay(16, cancellation.Token); }
          catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
        }
      });
    }
    internal double[] Drain()
    {
      lock (samples) { double[] result = [.. samples]; samples.Clear(); return result; }
    }
    internal async Task StopAsync()
    {
      cancellation.Cancel();
      try { await worker.WaitAsync(TimeSpan.FromSeconds(5)); }
      finally { cancellation.Dispose(); }
    }
  }
}
