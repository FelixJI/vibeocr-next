using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Workbench;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  private async Task CompleteScreenshotE2eSmokeAsync()
  {
    string? path = Environment.GetEnvironmentVariable("VIBEOCR_SCREENSHOT_E2E_HEALTH");
    string expected = Path.Combine(
      Directory.GetParent(layout.InstallRoot)!.FullName,
      "screenshot-e2e-health.json");
    if (path is null || !string.Equals(Path.GetFullPath(path), expected,
      StringComparison.OrdinalIgnoreCase))
    {
      Close();
      return;
    }

    try
    {
      if (screenshotSmokePicker is null || smokeSubmitAttempts is null ||
          smokeLastJobId is null || smokeStartupEnsureAttempts is null ||
          smokeInferenceAttached is null)
      {
        throw new InvalidOperationException("Screenshot smoke dependencies are missing.");
      }
      for (int attempt = 0; !smokeInferenceAttached(); attempt++)
      {
        if (attempt >= 21000)
          throw new TimeoutException("Real Supervisor did not attach within 35 minutes.");
        await Task.Delay(100);
      }
      int submitsBefore = smokeSubmitAttempts();
      if (submitsBefore != 0)
        throw new InvalidOperationException("OCR was submitted before screenshot capture.");
      int ensureBeforeCapture = smokeStartupEnsureAttempts();
      await ClickSmokeButtonAsync("纯截图");
      RecognitionWorkbenchState captured = await WaitForScreenshotStateAsync(
        state => !state.IsBusy && state.ScreenshotSession is not null,
        TimeSpan.FromSeconds(30));
      SyntheticScreenRegionPicker.CaptureEvidence capture =
        screenshotSmokePicker.Evidence ??
        throw new InvalidOperationException("GDI synthetic capture has no evidence.");
      if (captured.Result is not null || captured.Input is null ||
          smokeSubmitAttempts() != 0 ||
          smokeStartupEnsureAttempts() != ensureBeforeCapture)
      {
        throw new InvalidOperationException("Pure screenshot started OCR or lacks an input.");
      }
      int ensureAfterCapture = smokeStartupEnsureAttempts();
      int orangeBefore = await WaitForCanvasAsync();
      if (imageEditor?.ReusesCaptureOverlay != true)
        throw new InvalidOperationException("Screenshot editor did not reuse the selection overlay.");
      string geometryJson = await SmokeEditorWebView.CoreWebView2.ExecuteScriptAsync(
        "(() => { const c=document.querySelector('canvas[aria-label=\"图片检查画布\"]');" +
        "const r=c.getBoundingClientRect(); const s=window.vibeocrCaptureScene;" +
        "return {x:r.x,y:r.y,width:r.width,height:r.height," +
        "expectedX:s.x*innerWidth/s.desktopWidth,expectedY:s.y*innerHeight/s.desktopHeight," +
        "expectedWidth:s.width*innerWidth/s.desktopWidth,expectedHeight:s.height*innerHeight/s.desktopHeight};})()");
      using JsonDocument geometry = JsonDocument.Parse(geometryJson);
      foreach (string dimension in new[] { "x", "y", "width", "height" })
      {
        string expectedDimension = "expected" + char.ToUpperInvariant(dimension[0]) + dimension[1..];
        if (Math.Abs(geometry.RootElement.GetProperty(dimension).GetDouble() -
          geometry.RootElement.GetProperty(expectedDimension).GetDouble()) > 1)
          throw new InvalidOperationException("Screenshot canvas left the original selection bounds.");
      }

      await ClickSmokeButtonAsync("矩形");
      string canvasBox = await SmokeEditorWebView.CoreWebView2.ExecuteScriptAsync(
        "(() => { const c = document.querySelector('canvas[aria-label=\"图片检查画布\"]'); " +
        "c.scrollIntoView({block:'center'}); const r=c.getBoundingClientRect(); " +
        "return {x:r.x,y:r.y,width:r.width,height:r.height}; })()");
      using JsonDocument box = JsonDocument.Parse(canvasBox);
      JsonElement rect = box.RootElement;
      double x = rect.GetProperty("x").GetDouble();
      double y = rect.GetProperty("y").GetDouble();
      double width = rect.GetProperty("width").GetDouble();
      double height = rect.GetProperty("height").GetDouble();
      double x1 = x + width * 100 / 900;
      double y1 = y + height * 0.05;
      double x2 = x + width * 260 / 900;
      double y2 = y + height * 0.20;
      await DispatchMouseAsync("mouseMoved", x1, y1, 0);
      await DispatchMouseAsync("mousePressed", x1, y1, 1);
      await DispatchMouseAsync("mouseMoved", x2, y2, 1);
      await DispatchMouseAsync("mouseReleased", x2, y2, 0);
      RecognitionWorkbenchState edited = await WaitForScreenshotStateAsync(
        state => state.ScreenshotSession?.Revision > 0 && !state.IsBusy,
        TimeSpan.FromSeconds(15));
      RecognitionScreenshotSessionState editedSession = edited.ScreenshotSession ??
        throw new InvalidOperationException("Edited screenshot session disappeared.");
      int orangeAfter = await WaitForOrangePixelsAsync(orangeBefore);
      if (edited.ScreenshotSession?.SessionId != captured.ScreenshotSession?.SessionId ||
          smokeSubmitAttempts() != 0 ||
          smokeStartupEnsureAttempts() != ensureAfterCapture)
      {
        throw new InvalidOperationException("Editing changed session or started OCR/install.");
      }

      await ClickSmokeButtonAsync("识别当前图");
      RecognitionWorkbenchState recognized = await WaitForScreenshotStateAsync(
        state => !state.IsBusy && state.Result is not null,
        TimeSpan.FromMinutes(35));
      RecognitionScreenshotSessionState recognizedSession = recognized.ScreenshotSession ??
        throw new InvalidOperationException("Recognized screenshot session disappeared.");
      if (recognizedSession.SessionId != editedSession.SessionId ||
          recognizedSession.Revision != editedSession.Revision ||
          smokeSubmitAttempts() != 1 ||
          smokeStartupEnsureAttempts() != ensureAfterCapture ||
          string.IsNullOrWhiteSpace(smokeLastJobId()))
      {
        throw new InvalidOperationException("Explicit OCR session or operation counts mismatch.");
      }
      bool ocrVisible = false;
      for (int attempt = 0; attempt < 100; attempt++)
      {
        string result = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
          "(() => { const t=document.querySelector('.result-document')?.textContent ?? ''; " +
          "return t.includes('VibeOCR') && t.includes('123'); })()");
        if (result == "true") { ocrVisible = true; break; }
        await Task.Delay(100);
      }
      if (!ocrVisible)
        throw new InvalidOperationException("Synthetic OCR text was not visible in WebView2.");

      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = "passed",
        capture,
        canvas = new { orange_before = orangeBefore, orange_after = orangeAfter },
        capture_overlay_reused = true,
        capture_geometry = geometry.RootElement,
        session_id = recognizedSession.SessionId,
        revision = recognizedSession.Revision,
        task_id = smokeLastJobId(),
        submit_attempts_after_capture = 0,
        submit_attempts_after_recognition = smokeSubmitAttempts(),
        startup_ensure_attempts_before_capture = ensureBeforeCapture,
        startup_ensure_attempts_after_capture = ensureAfterCapture,
        startup_ensure_attempts_after_recognition = smokeStartupEnsureAttempts(),
        ocr_visible = true,
      }));
      Close();
    }
    catch (Exception error)
    {
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = "failed",
        error = error.Message,
      }));
      Close();
    }
  }

  private async Task ClickSmokeButtonAsync(string label)
  {
    bool capture = label is "纯截图";
    var surface = capture ? WorkbenchWebView : SmokeEditorWebView;
    static string ClickScript(string name) =>
      "(() => { const b=Array.from(document.querySelectorAll('button'))" +
      ".find(b => b.textContent?.trim() === " + JsonSerializer.Serialize(name) +
      "); if (!b || b.disabled || b.getAttribute('aria-disabled')==='true') return false; b.click(); return true; })()";
    if (capture)
    {
      // 右上角统一截图按钮单击直接开始普通截图，不再经过菜单。
      if (await surface.CoreWebView2.ExecuteScriptAsync(ClickScript("截图")) != "true")
        throw new InvalidOperationException("Screenshot button unavailable.");
      return;
    }
    if (await surface.CoreWebView2.ExecuteScriptAsync(ClickScript(label)) != "true")
      throw new InvalidOperationException($"Screenshot smoke button unavailable: {label}");
  }
  private async Task<RecognitionWorkbenchState> WaitForScreenshotStateAsync(
    Func<RecognitionWorkbenchState, bool> matches, TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
      while (true)
      {
        WorkbenchBootstrap bootstrap = await application.BootstrapAsync(cancellation.Token);
        RecognitionWorkbenchState state = bootstrap.States
          .Select(item => item.State)
          .OfType<RecognitionWorkbenchState>()
          .Single();
        if (state.StatusCode == "recognition.failed")
        {
          throw new InvalidOperationException(
            $"Screenshot smoke recognition failed: {screenshotSmokePicker?.Failure ?? state.StatusCode}");
        }
        if (matches(state)) return state;
        await Task.Delay(100, cancellation.Token);
      }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
      throw new TimeoutException("Screenshot state did not reach the expected phase.");
    }
  }

  private async Task<int> WaitForCanvasAsync()
  {
    for (int attempt = 0; attempt < 100; attempt++)
    {
      int pixels = await CountOrangePixelsAsync(requireImage: true);
      if (pixels >= 0) return pixels;
      await Task.Delay(100);
    }
    throw new TimeoutException("Screenshot canvas did not render.");
  }

  private async Task<int> WaitForOrangePixelsAsync(int before)
  {
    for (int attempt = 0; attempt < 100; attempt++)
    {
      int pixels = await CountOrangePixelsAsync();
      if (pixels > before + 50) return pixels;
      await Task.Delay(100);
    }
    throw new InvalidOperationException("Real WebView2 canvas pixels did not change after edit.");
  }

  private async Task<int> CountOrangePixelsAsync(bool requireImage = false)
  {
    if (SmokeEditorWebView.CoreWebView2 is null) return -1;
    string result = await SmokeEditorWebView.CoreWebView2.ExecuteScriptAsync(
      "(() => { const c=document.querySelector('canvas[aria-label=\"图片检查画布\"]'); " +
      "if(!c) return -1; const d=c.getContext('2d').getImageData(0,0,c.width,c.height).data; " +
      "let n=0,w=0,k=0; for(let i=0;i<d.length;i+=4) { " +
      "if(d[i]>230 && d[i+1]>115 && d[i+1]<165 && d[i+2]>35 && d[i+2]<85) n++; " +
      "if(d[i]>245 && d[i+1]>245 && d[i+2]>245) w++; " +
      "if(d[i]<40 && d[i+1]<40 && d[i+2]<40) k++; } " +
      $"return {(requireImage ? "true" : "false")} && (w<10000 || k<100) ? -1 : n; }})()");
    return int.Parse(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  private async Task DispatchMouseAsync(string type, double x, double y, int buttons) =>
    await SmokeEditorWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
      "Input.dispatchMouseEvent",
      JsonSerializer.Serialize(new
      {
        type,
        x,
        y,
        button = "left",
        buttons,
        clickCount = 1,
      }));
}
