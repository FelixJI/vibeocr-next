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
      await WaitForEditorSessionAsync(captured.ScreenshotSession!.SessionId);
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
      await WaitForEditorSessionAsync(captured.ScreenshotSession!.SessionId);
      await WaitForCanvasAsync();
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
      string latin;
      string chinese;
      try
      {
        latin = await SelectMainSubstringAndCopyAsync(
          chinese: false, previewPath: editorPreview);
      }
      catch (Exception error)
      {
        throw new InvalidOperationException("Latin Ctrl+C selection smoke failed.", error);
      }
      try { chinese = await SelectMainSubstringAndCopyAsync(chinese: true); }
      catch (Exception error)
      {
        throw new InvalidOperationException("Chinese context-menu selection smoke failed.", error);
      }
      await ExerciseWordAndCrossLineSelectionAsync();
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
      if (oldPins.Any(pin => !pin.SmokeAlwaysOnTop))
        throw new InvalidOperationException("A pin is not a native topmost window.");
      var pinPosition = oldPins[0].SmokePosition;
      await oldPins[0].SmokeDragTitleBarAsync(40, 30);
      if (oldPins[0].SmokePosition == pinPosition)
        throw new InvalidOperationException("Native pin did not move by titlebar drag.");
      oldPins[0].SmokeInvokeZoomIn();
      if (oldPins[0].SmokeZoom <= 1)
        throw new InvalidOperationException("Pin zoom control did not change content scale.");
      oldPins[0].SmokeSetOpacity(0.65);
      if (oldPins[0].SmokeNativeAlpha >= 255)
        throw new InvalidOperationException("Pin opacity did not change native window alpha.");
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

      await ClickSmokeButtonAsync("结束会话");
      await WaitForScreenshotStateAsync(
        state => state.ScreenshotSession is null, TimeSpan.FromSeconds(15));
      oldPins[0].SmokeInvokePrepareText();
      await WaitForPinTextAsync(oldPins[0], present: true);
      if (smokeSubmitAttempts() != 3 ||
          (await WaitForScreenshotStateAsync(_ => true, TimeSpan.FromSeconds(5))).TextLayer is not null)
        throw new InvalidOperationException(
          "Detached old pin did not OCR only its frozen PNG after editor close.");
      await WaitForPinTextAsync(oldPins[1], present: false);
      await ClickSmokeButtonAsync("纯截图");
      await WaitForScreenshotStateAsync(
        state => !state.IsBusy && state.ScreenshotSession is not null,
        TimeSpan.FromSeconds(30));
      await WaitForPinTextAsync(oldPins[0], present: true);
      if (smokeSubmitAttempts() != 3)
        throw new InvalidOperationException("New pure screenshot re-submitted old pin OCR.");

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
        submit_attempts_after_edit = 2,
        submit_attempts_after_detached_pin = smokeSubmitAttempts(),
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
          layer.Status is "textlayer.failed" or "textlayer.empty" or
            "textlayer.unavailable" or "textlayer.cancelled")
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

  private Task WaitForEditorSessionAsync(string sessionId) =>
    WaitForWebConditionAsync(
      "document.querySelector('.canvas-editor')?.dataset.screenshotSession === " +
      JsonSerializer.Serialize(sessionId));

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
    // Range is used only to measure glyph caret coordinates. The actual DOM
    // selection is made by WebView2 mouse input, as it is for a user.
    string geometryJson = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync($$"""
      (() => {
        const element = [...document.querySelectorAll('.image-text-glyphs')]
          .find(node => {{(chinese ? "/[\\u4e00-\\u9fff]/.test(node.textContent)" : "/[A-Za-z]{4}/.test(node.textContent)")}});
        if (!element?.firstChild) return null;
        const text = element.firstChild.textContent;
        const start = {{(chinese ? "text.search(/[\\u4e00-\\u9fff]/)" : "Math.min(1, text.length - 1)")}};
        const end = Math.min(text.length, start + {{(chinese ? 1 : 3)}});
        const point = offset => {
          const range = document.createRange();
          range.setStart(element.firstChild, offset); range.collapse(true);
          const box = range.getBoundingClientRect();
          return {x: box.x, y: box.y + box.height / 2};
        };
        element.scrollIntoView({block:'center'});
        return {expected:text.slice(start,end), start:point(start), end:point(end)};
      })()
      """);
    using JsonDocument geometry = JsonDocument.Parse(geometryJson);
    JsonElement root = geometry.RootElement;
    string expected = root.GetProperty("expected").GetString() ?? string.Empty;
    JsonElement startPoint = root.GetProperty(chinese ? "end" : "start");
    JsonElement endPoint = root.GetProperty(chinese ? "start" : "end");
    double sx = startPoint.GetProperty("x").GetDouble();
    double sy = startPoint.GetProperty("y").GetDouble();
    double ex = endPoint.GetProperty("x").GetDouble();
    double ey = endPoint.GetProperty("y").GetDouble();
    await DispatchMouseAsync("mouseMoved", sx, sy, 0);
    await DispatchMouseAsync("mousePressed", sx, sy, 1);
    await DispatchMouseAsync("mouseMoved", ex, ey, 1);
    await DispatchMouseAsync("mouseReleased", ex, ey, 0);
    string selected = JsonSerializer.Deserialize<string>(
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "window.getSelection()?.toString() ?? ''")) ?? string.Empty;
    if (selected != expected || selected.Length == 0)
      throw new InvalidOperationException($"WebView2 mouse selection mismatch: {selected} / {expected}.");
    await WaitForWebConditionAsync("[...document.querySelectorAll('button')].some(b => b.textContent?.trim() === '复制所选' && !b.disabled)");
    if (previewPath is not null)
    {
      File.Create(previewPath).Dispose();
      StorageFile file = await StorageFile.GetFileFromPathAsync(previewPath);
      using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
      await WorkbenchWebView.CoreWebView2.CapturePreviewAsync(
        CoreWebView2CapturePreviewImageFormat.Png, stream);
      await stream.FlushAsync();
    }
    if (chinese)
    {
      double contextX = (sx + ex) / 2;
      double contextY = (sy + ey) / 2;
      await WorkbenchWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
        "Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
          type = "mousePressed", x = contextX, y = contextY, button = "right", buttons = 2, clickCount = 1,
        }));
      await WorkbenchWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
        "Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
          type = "mouseReleased", x = contextX, y = contextY, button = "right", buttons = 0, clickCount = 1,
        }));
      await WaitForWebConditionAsync(
        "[...document.querySelectorAll('button')].filter(b => b.textContent?.trim() === '复制所选').length >= 2");
      await ClickTextCopyButtonByMouseAsync();
    }
    else
    {
      await WorkbenchWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
        "Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Control\",\"windowsVirtualKeyCode\":17,\"modifiers\":2}");
      await WorkbenchWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
        "Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"c\",\"code\":\"KeyC\",\"windowsVirtualKeyCode\":67,\"modifiers\":2}");
      await WorkbenchWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
        "Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"c\",\"code\":\"KeyC\",\"windowsVirtualKeyCode\":67,\"modifiers\":2}");
      await WorkbenchWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
        "Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Control\",\"windowsVirtualKeyCode\":17}");
    }
    string? lastClipboard = null;
    for (int attempt = 0; attempt < 100; attempt++)
    {
      DataPackageView content = Clipboard.GetContent();
      lastClipboard = content.Contains(StandardDataFormats.Text)
        ? await content.GetTextAsync() : null;
      if (lastClipboard == selected) return selected;
      await Task.Delay(100);
    }
    throw new InvalidOperationException(
      $"{(chinese ? "Chinese context-menu" : "Latin Ctrl+C")} clipboard mismatch: " +
      $"expected={JsonSerializer.Serialize(selected)}, actual={JsonSerializer.Serialize(lastClipboard)}.");
  }

  private async Task ClickTextCopyButtonByMouseAsync()
  {
    string boxJson = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("""
      (() => {
        const button = [...document.querySelectorAll('button')]
          .filter(node => node.textContent?.trim() === '复制所选').at(-1);
        if (!button) return null;
        const box = button.getBoundingClientRect();
        return {x:box.x+box.width/2,y:box.y+box.height/2};
      })()
      """);
    using JsonDocument box = JsonDocument.Parse(boxJson);
    double x = box.RootElement.GetProperty("x").GetDouble();
    double y = box.RootElement.GetProperty("y").GetDouble();
    await DispatchMouseAsync("mouseMoved", x, y, 0);
    await DispatchMouseAsync("mousePressed", x, y, 1);
    await DispatchMouseAsync("mouseReleased", x, y, 0);
  }

  private async Task ExerciseWordAndCrossLineSelectionAsync()
  {
    string geometryJson = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("""
      (() => {
        const spans = [...document.querySelectorAll('.image-text-glyphs')]
          .filter(node => node.firstChild?.textContent.length > 0);
        if (spans.length < 2) return null;
        const first = spans.find(node => /[A-Za-z]{2}/.test(node.firstChild.textContent));
        if (!first) return null;
        first.scrollIntoView({block:'center'});
        const point = (node, offset) => {
          const range = document.createRange();
          range.setStart(node.firstChild, offset); range.collapse(true);
          const box = range.getBoundingClientRect();
          return {x:box.x,y:box.y+box.height/2};
        };
        const firstBox = first.getBoundingClientRect();
        const second = spans.find(node =>
          Math.abs(node.getBoundingClientRect().y-firstBox.y) > firstBox.height/2);
        if (!second) return null;
        const start = Math.max(0, first.firstChild.textContent.length-3);
        const range = document.createRange();
        range.setStart(first.firstChild,start);
        range.setEnd(second.firstChild,Math.min(3,second.firstChild.textContent.length));
        return {word:point(first,1),start:point(first,start),
          end:point(second,Math.min(3,second.firstChild.textContent.length)),
          cross:range.toString(), firstY:firstBox.y,
          secondY:second.getBoundingClientRect().y};
      })()
      """);
    using JsonDocument geometry = JsonDocument.Parse(geometryJson);
    JsonElement root = geometry.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
      throw new InvalidOperationException("Synthetic OCR did not render two distinct text rows.");
    if (Math.Abs(root.GetProperty("firstY").GetDouble() -
          root.GetProperty("secondY").GetDouble()) < 2)
      throw new InvalidOperationException("Cross-line smoke selected glyphs on one visual row.");
    JsonElement word = root.GetProperty("word");
    double wx = word.GetProperty("x").GetDouble();
    double wy = word.GetProperty("y").GetDouble();
    await WorkbenchWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
      "Input.dispatchMouseEvent", JsonSerializer.Serialize(new
      {
        type = "mousePressed", x = wx, y = wy, button = "left", buttons = 1, clickCount = 2,
      }));
    await WorkbenchWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
      "Input.dispatchMouseEvent", JsonSerializer.Serialize(new
      {
        type = "mouseReleased", x = wx, y = wy, button = "left", buttons = 0, clickCount = 2,
      }));
    string wordSelection = JsonSerializer.Deserialize<string>(
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "window.getSelection()?.toString() ?? ''")) ?? string.Empty;
    if (string.IsNullOrWhiteSpace(wordSelection) || wordSelection.Contains('\n'))
      throw new InvalidOperationException("WebView2 double-click did not select one word.");

    string expected = root.GetProperty("cross").GetString() ?? string.Empty;
    JsonElement startPoint = root.GetProperty("start");
    JsonElement endPoint = root.GetProperty("end");
    foreach (bool reverse in new[] { false, true })
    {
      JsonElement from = reverse ? endPoint : startPoint;
      JsonElement to = reverse ? startPoint : endPoint;
      double sx = from.GetProperty("x").GetDouble();
      double sy = from.GetProperty("y").GetDouble();
      double ex = to.GetProperty("x").GetDouble();
      double ey = to.GetProperty("y").GetDouble();
      await DispatchMouseAsync("mouseMoved", sx, sy, 0);
      await DispatchMouseAsync("mousePressed", sx, sy, 1);
      await DispatchMouseAsync("mouseMoved", ex, ey, 1);
      await DispatchMouseAsync("mouseReleased", ex, ey, 0);
      string selected = JsonSerializer.Deserialize<string>(
        await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
          "window.getSelection()?.toString() ?? ''")) ?? string.Empty;
      if (selected != expected || string.IsNullOrWhiteSpace(selected))
        throw new InvalidOperationException("WebView2 cross-line mouse selection mismatch.");
    }
  }

  private static async Task<string> CopyPinSubstringAsync(PinnedImageWindow pin)
  {
    string geometryJson = await pin.SmokeEvaluateAsync("""
      (() => {
        const span = [...document.querySelectorAll('.line span')]
          .find(node => node.firstChild?.textContent.length >= 4);
        if (!span) return null;
        const point = offset => {
          const range = document.createRange();
          range.setStart(span.firstChild, offset); range.collapse(true);
          const box = range.getBoundingClientRect();
          return {x:box.x,y:box.y+box.height/2};
        };
        return {expected:span.firstChild.textContent.slice(1,4),start:point(1),end:point(4)};
      })()
      """);
    using JsonDocument geometry = JsonDocument.Parse(geometryJson);
    JsonElement root = geometry.RootElement;
    string expected = root.GetProperty("expected").GetString() ?? string.Empty;
    JsonElement start = root.GetProperty("start");
    JsonElement end = root.GetProperty("end");
    double sx = start.GetProperty("x").GetDouble();
    double sy = start.GetProperty("y").GetDouble();
    double ex = end.GetProperty("x").GetDouble();
    double ey = end.GetProperty("y").GetDouble();
    await pin.SmokeDispatchMouseAsync("mouseMoved", sx, sy, 0);
    await pin.SmokeDispatchMouseAsync("mousePressed", sx, sy, 1);
    await pin.SmokeDispatchMouseAsync("mouseMoved", ex, ey, 1);
    await pin.SmokeDispatchMouseAsync("mouseReleased", ex, ey, 0);
    string selected = JsonSerializer.Deserialize<string>(
      await pin.SmokeEvaluateAsync("window.getSelection()?.toString() ?? ''")) ?? string.Empty;
    if (selected != expected || selected.Length != 3)
      throw new InvalidOperationException("Pinned WebView2 mouse selection failed.");
    pin.SmokeInvokeCopySelection();
    for (int attempt = 0; attempt < 100; attempt++)
    {
      DataPackageView pending = Clipboard.GetContent();
      if (pending.Contains(StandardDataFormats.Text) &&
          await pending.GetTextAsync() == selected) return selected;
      await Task.Delay(100);
    }
    DataPackageView content = Clipboard.GetContent();
    throw new InvalidOperationException($"Pinned UIAutomation copy failed: {content.Contains(StandardDataFormats.Text)}.");
  }
}
