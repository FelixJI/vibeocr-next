using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Workbench;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  private async Task CompleteTextSelectionE2eSmokeAsync()
  {
    string? path = Environment.GetEnvironmentVariable("VIBEOCR_SCREENSHOT_E2E_HEALTH");
    string expected = Path.Combine(Directory.GetParent(layout.InstallRoot)!.FullName,
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
          smokeStartupEnsureAttempts is null || smokeInferenceAttached is null)
        throw new InvalidOperationException("Text selection smoke dependencies are missing.");
      for (int attempt = 0; !smokeInferenceAttached(); attempt++)
      {
        if (attempt >= 21000)
          throw new TimeoutException("Real Supervisor did not attach within 35 minutes.");
        await Task.Delay(100);
      }
      if (smokeSubmitAttempts() != 0)
        throw new InvalidOperationException("OCR was submitted before capture.");
      int ensuresBefore = smokeStartupEnsureAttempts();

      await ClickSmokeButtonAsync("纯截图");
      RecognitionWorkbenchState captured = await WaitForScreenshotStateAsync(
        state => !state.IsBusy && state.ScreenshotSession is not null,
        TimeSpan.FromSeconds(30));
      SyntheticScreenRegionPicker.CaptureEvidence capture =
        screenshotSmokePicker.Evidence ??
        throw new InvalidOperationException("Synthetic capture has no evidence.");
      if (captured.Result is not null || captured.Input is null ||
          smokeSubmitAttempts() != 0 ||
          smokeStartupEnsureAttempts() != ensuresBefore)
        throw new InvalidOperationException("Capture submitted OCR or started installation.");
      await WaitForCanvasAsync();
      await ClickSmokeButtonAsync("贴图");
      await WaitForPinCountAsync(1);
      PinnedImageWindow purePin = pinnedImages.Single();
      string purePath = purePin.SmokeImagePath;
      if (!File.Exists(purePath) || smokeSubmitAttempts() != 0)
        throw new InvalidOperationException("Pure pin submitted OCR or lost its PNG.");
      purePin.Close();
      await WaitForPinCountAsync(0);
      if (!purePin.SmokeDisposed || File.Exists(purePath))
        throw new InvalidOperationException("Pure pin did not release its PNG lease.");

      await ClickSmokeButtonAsync("截图取字");
      captured = await WaitForScreenshotStateAsync(
        state => !state.IsBusy && state.ScreenshotSession?.TextSelectionRequested == true,
        TimeSpan.FromSeconds(30));
      capture = screenshotSmokePicker.Evidence ??
        throw new InvalidOperationException("Text capture has no synthetic evidence.");
      RecognitionTextLayerState firstLayer = await WaitForReadyTextLayerAsync(0);
      if (smokeSubmitAttempts() != 1 ||
          smokeStartupEnsureAttempts() != ensuresBefore ||
          firstLayer.Lines is not { Count: > 0 } ||
          !firstLayer.Lines.Any(line => line.Text.Any(c => c is >= '一' and <= '龥')) ||
          !firstLayer.Lines.Any(line => line.Text.Contains("Vibe", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Real local OCR did not produce bilingual text once.");
      await ClickSmokeButtonAsync("取字");
      await WaitForWebConditionAsync("document.querySelectorAll('.image-text-glyphs').length > 0");
      string evidenceRoot = Path.GetDirectoryName(path)!;
      string editorPreview = Path.Combine(evidenceRoot, "text-selection-editor.png");
      string firstPinPreview = Path.Combine(evidenceRoot, "text-selection-pin-1.png");
      string secondPinPreview = Path.Combine(evidenceRoot, "text-selection-pin-2.png");
      string latin = await SelectMainSubstringAndCopyAsync(
        chinese: false, previewPath: editorPreview);
      string chinese = await SelectMainSubstringAndCopyAsync(chinese: true);
      if (string.IsNullOrWhiteSpace(latin) || string.IsNullOrWhiteSpace(chinese))
        throw new InvalidOperationException("Partial Latin/Chinese selection was empty.");

      await ClickSmokeButtonAsync("贴图");
      await WaitForPinCountAsync(1);
      await ClickSmokeButtonAsync("贴图");
      await WaitForPinCountAsync(2);
      PinnedImageWindow[] oldPins = pinnedImages.ToArray();
      string[] pinnedPaths = oldPins.Select(pin => pin.SmokeImagePath).ToArray();
      if (pinnedPaths.Distinct().Count() != 2 ||
          pinnedPaths.Any(path => !File.Exists(path)) ||
          smokeSubmitAttempts() != 1 ||
          smokeStartupEnsureAttempts() != ensuresBefore)
        throw new InvalidOperationException("Text pins did not reuse the OCR layer and independent PNG leases.");

      foreach (PinnedImageWindow pin in oldPins)
      {
        await WaitForPinTextAsync(pin, present: true);
      }
      string pinnedSelection = await CopyPinSubstringAsync(oldPins[0]);
      await oldPins[0].SmokeCapturePreviewAsync(firstPinPreview);
      await oldPins[1].SmokeCapturePreviewAsync(secondPinPreview);
      if (smokeSubmitAttempts() != 1)
        throw new InvalidOperationException("Two pins caused duplicate OCR submissions.");

      await ClickSmokeButtonAsync("马赛克");
      string canvasBox = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "(() => { const c=document.querySelector('canvas[aria-label=\"图片检查画布\"]'); " +
        "c.scrollIntoView({block:'center'}); const r=c.getBoundingClientRect(); " +
        "return {x:r.x,y:r.y,width:r.width,height:r.height}; })()");
      using JsonDocument box = JsonDocument.Parse(canvasBox);
      JsonElement rect = box.RootElement;
      double x = rect.GetProperty("x").GetDouble();
      double y = rect.GetProperty("y").GetDouble();
      double width = rect.GetProperty("width").GetDouble();
      double height = rect.GetProperty("height").GetDouble();
      await DispatchMouseAsync("mouseMoved", x + width * 0.1, y + height * 0.1, 0);
      await DispatchMouseAsync("mousePressed", x + width * 0.1, y + height * 0.1, 1);
      await DispatchMouseAsync("mouseMoved", x + width * 0.4, y + height * 0.3, 1);
      await DispatchMouseAsync("mouseReleased", x + width * 0.4, y + height * 0.3, 0);
      RecognitionWorkbenchState edited = await WaitForScreenshotStateAsync(
        state => state.ScreenshotSession?.Revision == 1,
        TimeSpan.FromSeconds(15));
      await WaitForWebConditionAsync("document.querySelectorAll('.image-text-glyphs').length === 0");
      foreach (PinnedImageWindow pin in oldPins) await WaitForPinTextAsync(pin, present: false);
      RecognitionTextLayerState secondLayer = await WaitForReadyTextLayerAsync(1);
      RecognitionScreenshotSessionState secondBinding = secondLayer.Binding ??
        throw new InvalidOperationException("Edited text layer has no revision binding.");
      if (secondBinding.SessionId != firstLayer.Binding?.SessionId ||
          secondBinding.Revision != edited.ScreenshotSession?.Revision ||
          smokeSubmitAttempts() != 2 ||
          smokeStartupEnsureAttempts() != ensuresBefore)
        throw new InvalidOperationException("Edited PNG did not get one new bound OCR layer.");
      foreach (PinnedImageWindow pin in oldPins) await WaitForPinTextAsync(pin, present: false);

      foreach (PinnedImageWindow pin in oldPins) pin.Close();
      await WaitForPinCountAsync(0);
      if (oldPins.Any(pin => !pin.SmokeDisposed) ||
          pinnedPaths.Any(File.Exists))
        throw new InvalidOperationException("Closing pins retained WebView or PNG leases.");
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = "passed",
        capture,
        session_id = secondBinding.SessionId,
        revision = secondBinding.Revision,
        pins = 2,
        submit_attempts_after_pure_pins = 0,
        submit_attempts_after_first_layer = 1,
        submit_attempts_after_edit = smokeSubmitAttempts(),
        startup_ensure_attempts_before_capture = ensuresBefore,
        startup_ensure_attempts = smokeStartupEnsureAttempts(),
        latin_selection = latin,
        chinese_selection = chinese,
        pinned_selection = pinnedSelection,
        closed_leases = 2,
        ui_previews = new[] {
          Path.GetFileName(editorPreview),
          Path.GetFileName(firstPinPreview),
          Path.GetFileName(secondPinPreview),
        },
      }));
      Close();
    }
    catch (Exception error)
    {
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = "failed",
        error = error.ToString(),
      }));
      Close();
    }
  }

  private async Task<RecognitionTextLayerState> WaitForReadyTextLayerAsync(long revision)
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(35));
    while (!timeout.IsCancellationRequested)
    {
      RecognitionWorkbenchState state = await WaitForScreenshotStateAsync(
        _ => true, TimeSpan.FromSeconds(5));
      RecognitionTextLayerState? layer = state.TextLayer;
      if (layer?.Binding?.Revision == revision && layer.Status == "textlayer.ready")
        return layer;
      if (layer?.Binding?.Revision == revision &&
          layer.Status is "textlayer.failed" or "textlayer.unavailable" or "textlayer.cancelled")
        throw new InvalidOperationException($"Text layer failed: {layer.Status}/{layer.Reason}");
      await Task.Delay(100, timeout.Token);
    }
    throw new TimeoutException("Text layer did not become ready.");
  }

  private async Task WaitForWebConditionAsync(string expression)
  {
    for (int attempt = 0; attempt < 100; attempt++)
    {
      if (await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(expression) == "true")
        return;
      await Task.Delay(100);
    }
    throw new TimeoutException($"WebView2 condition did not hold: {expression}");
  }

  private async Task WaitForPinCountAsync(int count)
  {
    for (int attempt = 0; attempt < 100; attempt++)
    {
      if (pinnedImages.Count == count) return;
      await Task.Delay(100);
    }
    throw new TimeoutException($"Expected {count} native pin windows, got {pinnedImages.Count}.");
  }

  private static async Task WaitForPinTextAsync(PinnedImageWindow pin, bool present)
  {
    for (int attempt = 0; attempt < 100; attempt++)
    {
      try
      {
        bool hasText = await pin.SmokeEvaluateAsync(
          "document.querySelectorAll('.line span').length > 0") == "true";
        if (hasText == present) return;
      }
      catch (InvalidOperationException) { }
      await Task.Delay(100);
    }
    throw new TimeoutException("Pinned WebView2 text state did not update.");
  }

  private async Task<string> SelectMainSubstringAndCopyAsync(
    bool chinese, string? previewPath = null)
  {
    string selectedJson = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync($$"""
      (() => {
        const element = [...document.querySelectorAll('.image-text-glyphs')]
          .find(node => {{(chinese ? "/[\\u4e00-\\u9fff]/.test(node.textContent)" : "/[A-Za-z]{4}/.test(node.textContent)")}});
        if (!element?.firstChild) return '';
        const text = element.firstChild.textContent;
        const start = {{(chinese ? "text.search(/[\\u4e00-\\u9fff]/)" : "Math.min(1, text.length - 1)")}};
        const end = Math.min(text.length, start + {{(chinese ? 1 : 3)}});
        const range = document.createRange();
        range.setStart(element.firstChild, start); range.setEnd(element.firstChild, end);
        const selection = window.getSelection();
        selection.removeAllRanges(); selection.addRange(range);
        return selection.toString();
      })()
      """);
    string selected = JsonSerializer.Deserialize<string>(selectedJson) ?? string.Empty;
    if (selected.Length == 0)
      throw new InvalidOperationException("Native DOM substring selection failed.");
    await WaitForWebConditionAsync("[...document.querySelectorAll('button')].some(b => b.textContent?.trim() === '复制所选')");
    if (previewPath is not null)
    {
      File.Create(previewPath).Dispose();
      StorageFile file = await StorageFile.GetFileFromPathAsync(previewPath);
      using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
      await WorkbenchWebView.CoreWebView2.CapturePreviewAsync(
        CoreWebView2CapturePreviewImageFormat.Png, stream);
      await stream.FlushAsync();
    }
    await ClickSmokeButtonAsync("复制所选");
    for (int attempt = 0; attempt < 100; attempt++)
    {
      DataPackageView content = Clipboard.GetContent();
      if (content.Contains(StandardDataFormats.Text) &&
          await content.GetTextAsync() == selected) return selected;
      await Task.Delay(100);
    }
    throw new InvalidOperationException("Clipboard did not contain only the selected substring.");
  }

  private static async Task<string> CopyPinSubstringAsync(PinnedImageWindow pin)
  {
    string selectedJson = await pin.SmokeEvaluateAsync("""
      (() => {
        const span = [...document.querySelectorAll('.line span')]
          .find(node => node.firstChild?.textContent.length >= 4);
        if (!span) return '';
        const range = document.createRange();
        range.setStart(span.firstChild, 1); range.setEnd(span.firstChild, 4);
        const selection = window.getSelection();
        selection.removeAllRanges(); selection.addRange(range);
        return selection.toString();
      })()
      """);
    string selected = JsonSerializer.Deserialize<string>(selectedJson) ?? string.Empty;
    if (selected.Length != 3)
      throw new InvalidOperationException("Pinned WebView2 substring selection failed.");
    await pin.SmokeCopySelectionAsync();
    DataPackageView content = Clipboard.GetContent();
    if (!content.Contains(StandardDataFormats.Text) ||
        await content.GetTextAsync() != selected)
      throw new InvalidOperationException("Pinned copy did not preserve the selected substring.");
    return selected;
  }
}
