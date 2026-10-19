using System.Diagnostics;
using System.Text.Json;
using VibeOCR.App.Features.Pins;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  private static async Task WaitForPinCheckAsync(Func<Task<bool>> condition, string failure)
  {
    var elapsed = Stopwatch.StartNew();
    while (!await condition())
    {
      if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(failure);
      await Task.Delay(25);
    }
  }

  internal bool SmokeHistoryInitializing => pinHistoryStore?.Initialization.IsCompleted == false;
  internal Task SmokeHistoryInitialization => pinHistoryStore!.Initialization;

  internal async Task VerifyPinsSelfCheckAsync()
  {
    CaptureUiSmokeTrace.Stage("pins.main-ready");
    await WaitForPinCheckAsync(() => Task.FromResult(webHost.HasSubscription), "Main bridge was not ready.");
    if (commandHandler.InitialStates.OfType<PinEditWorkbenchState>().Single().History is not { Enabled: false })
      throw new InvalidOperationException("History must be available and disabled without an editing session.");
    await application.ExecuteAsync(new(Guid.NewGuid(), new NavigateWorkbenchCommand(WorkbenchRoute.ImageEdit)), CancellationToken.None);
    await WaitForPinCheckAsync(async () => await WorkbenchWebView.ExecuteScriptAsync(
      "!!document.querySelector('[aria-label=\"截图历史\"]')") == "true", "Main history entry was absent.");

    using var encoded = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
    byte[] pixels = new byte[64 * 32 * 4];
    for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 50; pixels[i + 1] = 140; pixels[i + 2] = 220; pixels[i + 3] = 255; }
    // Two real raster H glyphs: one in the initially excluded left quarter, one visible.
    foreach (int startX in new[] { 4, 44 })
      for (int y = 6; y < 25; y++)
        for (int x = startX; x < startX + 10; x++)
          if (x < startX + 2 || x >= startX + 8 || y is >= 14 and < 17)
          { int at = (y * 64 + x) * 4; pixels[at] = pixels[at + 1] = pixels[at + 2] = 0; }
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 64, 32, 96, 96, pixels);
    await encoder.FlushAsync();
    encoded.Seek(0);
    using var reader = new DataReader(encoded.GetInputStreamAt(0));
    await reader.LoadAsync(checked((uint)encoded.Size));
    byte[] png = new byte[checked((int)encoded.Size)];
    reader.ReadBytes(png);
    string initialImage = Path.Combine(resourceRoot, "pins-smoke-source.png");
    await File.WriteAllBytesAsync(initialImage, png);
    await application.ExecuteAsync(new(Guid.NewGuid(), new OpenDroppedImageEditFileCommand(initialImage)), CancellationToken.None);
    await WaitForPinCheckAsync(async () => await WorkbenchWebView.ExecuteScriptAsync(
      "document.body.textContent.includes('原图 64×32') && !document.querySelector('button[aria-label=\"贴图\"]')?.disabled") == "true",
      "Main image editor did not load the source.");
    await WorkbenchWebView.ExecuteScriptAsync("document.querySelector('button[aria-label=\"屏蔽\"]').click()");
    await WorkbenchWebView.ExecuteScriptAsync("""
      (()=>{const c=document.querySelector('canvas[aria-label="图片检查画布"]');const r=c.getBoundingClientRect();
      const e=(type,x,y,buttons)=>c.dispatchEvent(new PointerEvent(type,{bubbles:true,pointerId:1,pointerType:'mouse',isPrimary:true,button:0,buttons,clientX:r.left+r.width*x/900,clientY:r.top+r.height*y/600}));
      e('pointerdown',1,76,1);e('pointermove',224,524,1);e('pointerup',224,524,0);
      const select=document.querySelector('select[aria-label="输出格式"]');select.value='image/jpeg';select.dispatchEvent(new Event('change',{bubbles:true}));})()
      """);
    string pinRoot = PinSnapshotStore.RootFor(layout.DataRoot);
    int windowCount = CountSmokeAppWindows();
    File.WriteAllText(pinRoot, "owned persistence blocker");
    try
    {
      for (int attempt = 0; attempt < 2; attempt++)
      {
        await WorkbenchWebView.ExecuteScriptAsync("document.querySelector('button[aria-label=\"贴图\"]').click()");
        await WaitForPinCheckAsync(async () => await WorkbenchWebView.ExecuteScriptAsync(
          "document.body.textContent.includes('贴图未创建') && !document.querySelector('button[aria-label=\"贴图\"]')?.disabled") == "true",
          "Failed first pin did not return a recoverable receipt.");
        await WaitForPinCheckAsync(() => Task.FromResult(CountSmokeAppWindows() == windowCount), "Failed pin leaked an unregistered native window.");
        if (pinnedImages.Count != 0) throw new InvalidOperationException("Failed first pin was registered.");
      }
    }
    finally { File.Delete(pinRoot); }
    await WorkbenchWebView.ExecuteScriptAsync("document.querySelector('button[aria-label=\"贴图\"]').click()");
    await WaitForPinCheckAsync(() => Task.FromResult(pinnedImages.Count == 1), "JPEG selection prevented first pin creation.");
    if (pinSnapshotStore!.ReadSnapshotPng(pinnedImages[0].PinId) is not { Width: 64, Height: 32 })
      throw new InvalidOperationException("First pin was not a decodable owned PNG.");
    if (await WorkbenchWebView.ExecuteScriptAsync("document.querySelector('select[aria-label=\"输出格式\"]').value === 'image/jpeg'") != "true")
      throw new InvalidOperationException("First pin changed the ordinary output format.");
    WorkbenchAnnotationLease secondLease = await annotationStore.UploadPngAsync(new MemoryStream(png), CancellationToken.None);
    PinScreenshot(annotationStore.Take(secondLease.ResourceUri), Guid.NewGuid(), 0, null, []);
    PinnedImageWindow first = pinnedImages[0], second = pinnedImages[1];
    await WaitForPinCheckAsync(() => Task.FromResult(first.SmokeDocumentReady && second.SmokeDocumentReady), "Static pins not ready.");
    var observedScopes = new List<string>();
    commandHandler.StateChanged += state => observedScopes.Add(state.Scope);
    try
    {
      CaptureUiSmokeTrace.Stage("pins.first-edit");
      var oldTextIdentity = (first.SessionId, first.Revision);
      WorkbenchAnnotationLease sharedLease = await annotationStore.UploadPngAsync(new MemoryStream(png), CancellationToken.None);
      PinScreenshot(annotationStore.Take(sharedLease.ResourceUri), first.SessionId, first.Revision, null, first.CurrentExcludeBoxes);
      PinnedImageWindow shared = pinnedImages.Last();
      await VerifySmokePinTextAsync(first);
      await VerifySmokePinTextAsync(shared);
      if (pinTextTasks.Count != 1) throw new InvalidOperationException("Shared pin text was not cached once.");
      first.SmokeSnapshotDisplayStep = () => Task.FromException(new IOException("Injected static display swap failure."));
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      await RotateSmokePinAsync(first);
      CaptureUiSmokeTrace.Stage("pins.switch-save");
      await EnterPinEditAsync(second);
      await WaitForPinEditorAsync(second);
      if (first.Editing || !second.Editing || pinSnapshotStore!.GetSnapshotProjectPath(first.PinId) is null ||
        first.SmokeImageSize != (32, 64)) throw new InvalidOperationException($"Switch did not commit: firstEditing={first.Editing}, secondEditing={second.Editing}, size={first.SmokeImageSize}, project={pinSnapshotStore!.GetSnapshotProjectPath(first.PinId)}, body={await first.SmokeEditorEvaluateAsync("document.body.textContent")}");

      if (pinSnapshotSwap.IsFaulted || !first.SmokeStatus.Contains("显示更新失败") || !pinTextTasks.ContainsKey(oldTextIdentity))
        throw new InvalidOperationException("Committed display failure lost the slot, saved image, or shared OCR task.");
      shared.ForceClose();
      if (pinTextTasks.ContainsKey(oldTextIdentity)) throw new InvalidOperationException("Last shared pin did not release old OCR task.");
      await RotateSmokePinAsync(second);
      string blocker = Path.Combine(PinSnapshotStore.RootFor(layout.DataRoot), second.PinId.ToString("N"), "current.json");
      Directory.CreateDirectory(blocker);
      CaptureUiSmokeTrace.Stage("pins.failed-save-retains-editor");
      await EnterPinEditAsync(first);
      if (!second.Editing || first.Editing || second.SmokeImageSize != (64, 32))
        throw new InvalidOperationException("Failed save discarded the editor or replaced its static image.");
      Directory.Delete(blocker);
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      await WaitForPinCheckAsync(async () => (await first.SmokeEditorEvaluateAsync(
        "document.querySelector('.canvas-operation-message')?.textContent?.includes('工程已恢复') ?? false")) == "true" ||
        (await first.SmokeEditorEvaluateAsync("document.body.textContent.includes('工程已恢复')")) == "true",
        "Saved project did not restore its editable state.");
      CaptureUiSmokeTrace.Stage("pins.restore-editable-project");
      await RotateSmokePinAsync(first);
      if (!await first.SaveEditAsync()) throw new InvalidOperationException("Restored project could not save.");
      if (first.SmokeImageSize != (64, 32)) throw new InvalidOperationException("Restored rotation was lost or flattened.");
      for (int cycle = 0; cycle < 3; cycle++)
      {
        await VerifySmokePinTextAsync(first);
        if (pinTextTasks.Count != 1) throw new InvalidOperationException("OCR cache accumulated before rebind.");
        await EnterPinEditAsync(first); await WaitForPinEditorAsync(first);
        await RotateSmokePinAsync(first); await RotateSmokePinAsync(first);
        if (!await first.SaveEditAsync() || pinEditing is not null || pinTextTasks.Count != 0)
          throw new InvalidOperationException("Repeated text/edit/save leaked its old OCR task or editor slot.");
      }
      await VerifySmokePinTextAsync(first);
      if (observedScopes.Any(scope => scope is "recognition" or "imageEdit"))
        throw new InvalidOperationException("Pin editing polluted a main image channel.");

      TogglePinGroup(first); TogglePinGroup(second);
      var firstBefore = first.SmokePosition; var secondBefore = second.SmokePosition;
      MovePinGroup(first, 30, -20);
      if (first.SmokePosition != firstBefore || second.SmokePosition.X != secondBefore.X + 30 ||
        second.SmokePosition.Y != secondBefore.Y - 20) throw new InvalidOperationException("Group movement did not use physical deltas.");
      first.SetPassThrough(true); ShowPinManager(); first.SetPassThrough(false);
      first.SetCollapsed(true); first.SetCollapsed(false);

      CaptureUiSmokeTrace.Stage("pins.formula-render");
      var formula = CreateTypedPin(TypedPinWindow.ContentKind.Formula);
      formula.SetText("\\frac{x^2+1}{2}");
      await formula.RenderFormulaAsync();
      if (!formula.HasFormulaImage) throw new InvalidOperationException(formula.StatusText);
      formula.SetText("\\unknownCommand{");
      await formula.RenderFormulaAsync();
      if (formula.HasFormulaImage || !formula.RecoveryState().Content.Contains("unknownCommand"))
        throw new InvalidOperationException("Failed formula inserted an image or lost its source.");
      formula.ForceClose();

      CaptureUiSmokeTrace.Stage("pins.close-cancel-discard");
      await EnterPinEditAsync(second);
      await WaitForPinEditorAsync(second);
      await RotateSmokePinAsync(second);
      second.Close();
      await WaitForPinCheckAsync(() => Task.FromResult(second.SmokeInvokeEditDecision("取消")), "Close decision was absent.");
      if (second.Disposed || !second.Editing) throw new InvalidOperationException("Cancel closed a dirty pin.");
      await WaitForPinCheckAsync(() => Task.FromResult(!second.SmokeEditDecisionPending), "Cancel decision did not finish.");
      second.Close();
      await WaitForPinCheckAsync(() => Task.FromResult(second.SmokeInvokeEditDecision("放弃更改")), "Discard decision was absent.");
      await WaitForPinCheckAsync(() => Task.FromResult(second.Disposed), "Discard did not close the pin.");
      if (pinSnapshotStore!.GetSnapshotPngPath(second.PinId) is not null)
        throw new InvalidOperationException("Closing a single pin did not delete its owned recovery record.");

      CaptureUiSmokeTrace.Stage("pins.exit-recovery");
      var expanded = first.SmokeClientSize;
      first.SetCollapsed(true);
      if (!await PreparePinsExitAsync()) throw new InvalidOperationException("Exit recovery could not persist pins.");
      var recovered = pinSnapshotStore.LoadRecovery();
      if (recovered.Count != 1 || !recovered.Any(item => item.Id == first.PinId && item.X == first.SmokePosition.X))
        throw new InvalidOperationException("Pin recovery did not retain content and physical position.");
      // Simulate the same shutdown disposal and fresh recovery path, preserving owned records.
      shuttingDown = true;
      first.ForceClose();
      shuttingDown = false;
      RestorePins();
      first = pinnedImages.Single();
      await WaitForPinCheckAsync(() => Task.FromResult(first.SmokeDocumentReady), "Recovered static pin not ready.");
      if (!first.Collapsed) throw new InvalidOperationException("Recovery lost the collapsed state.");
      first.SetCollapsed(false);
      if (first.SmokeClientSize.Width != expanded.Width || first.SmokeClientSize.Height != expanded.Height)
        throw new InvalidOperationException($"Recovery lost expanded size: expected={expanded.Width}x{expanded.Height}, actual={first.SmokeClientSize.Width}x{first.SmokeClientSize.Height}, record={JsonSerializer.Serialize(recovered.Single())}");
      await VerifySmokePinTextAsync(first);
      first.Close(); second.Close();
      if (pinSnapshotStore.LoadRecovery().Count != 0) throw new InvalidOperationException("Closing a pin kept its owned recovery entry.");
    }
    finally
    {
      if (pinEditing is { } slot) await ExitPinEditAsync(slot.Window);
      foreach (var pin in pinnedImages.ToArray()) pin.ForceClose();
      foreach (var pin in typedPins.ToArray()) pin.ForceClose();
      pinManager?.ForceClose();
    }
  }

  private static async Task VerifySmokePinTextAsync(PinnedImageWindow pin)
  {
    if (pin.CurrentExcludeBoxes.Count == 0) throw new InvalidOperationException("Pin lost its OCR exclusions.");
    await pin.SmokePrepareTextAsync();
    if (pin.SmokeLayerTexts.Count != 1 || pin.SmokeLayerTexts[0] != "visible")
      throw new InvalidOperationException("Pin text layer resurrected an excluded line.");
  }

  internal async Task<RecognitionTextLayerState?> VerifyMaskedPinOcrInputAsync(
    Guid session, long revision, string input, CancellationToken token)
  {
    PinnedImageWindow pin = pinnedImages.First(item => item.SessionId == session);
    if (input == pin.CurrentImagePath) throw new InvalidOperationException("OCR received the ordinary unmasked pin.");
    using var memory = new MemoryStream(await File.ReadAllBytesAsync(input, token));
    using var random = memory.AsRandomAccessStream();
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random);
    byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
      new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    WorkbenchExclusionBox box = pin.CurrentExcludeBoxes[0];
    double centerX = box.X + box.Width / 2, centerY = box.Y + box.Height / 2;
    int x = (int)(centerX / 1000 * decoder.PixelWidth), y = (int)(centerY / 1000 * decoder.PixelHeight);
    int offset = (y * (int)decoder.PixelWidth + x) * 4;
    if (!pixels.AsSpan(offset, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 }))
      throw new InvalidOperationException("Excluded glyph pixels reached OCR.");
    using var ordinary = new MemoryStream(await File.ReadAllBytesAsync(pin.CurrentImagePath, token));
    using var ordinaryRandom = ordinary.AsRandomAccessStream();
    BitmapDecoder ordinaryDecoder = await BitmapDecoder.CreateAsync(ordinaryRandom);
    byte[] ordinaryPixels = (await ordinaryDecoder.GetPixelDataAsync()).DetachPixelData();
    if (ordinaryPixels.AsSpan(offset, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 }))
      throw new InvalidOperationException("OCR masking modified the ordinary display/copy/save image.");
    double safeX = centerX < 500 ? 800 : 100, safeY = centerY < 500 ? 800 : 100;
    return new RecognitionTextLayerState("textlayer.ready", null, new(session.ToString("N"), revision),
      "rapid_text", "pins-smoke", null,
      [new("hidden", centerX - 1, centerY - 1, centerX + 1, centerY + 1),
       new("visible", safeX, safeY, safeX + 5, safeY + 5)]);
  }

  private int CountSmokeAppWindows()
  {
    int count = 0;
    string windowClass = PaddleSmokeNative.GetWindowClassName(WinRT.Interop.WindowNative.GetWindowHandle(this));
    var probe = new PaddleSmokeNative.EnumWindowsProc((hwnd, _) =>
    {
      PaddleSmokeNative.GetWindowThreadProcessId(hwnd, out uint processId);
      if (processId == Environment.ProcessId && PaddleSmokeNative.GetWindowClassName(hwnd) == windowClass) count++;
      return true;
    });
    PaddleSmokeNative.EnumWindows(probe, nint.Zero);
    GC.KeepAlive(probe);
    return count;
  }

  private static Task WaitForPinEditorAsync(PinnedImageWindow pin) => WaitForPinCheckAsync(
    async () => (await pin.SmokeEditorEvaluateAsync(
      "!!document.querySelector('.canvas-editor[data-screenshot-session]') && !!document.querySelector('canvas[aria-label=\"图片检查画布\"]') && /原图 [0-9]+×[0-9]+/.test(document.body.textContent) && !Array.from(document.querySelectorAll('button')).find(b=>b.getAttribute('aria-label')==='旋转 90°')?.disabled")) == "true",
    "Pin editor did not decode its input.");

  private static async Task RotateSmokePinAsync(PinnedImageWindow pin)
  {
    string result = await pin.SmokeEditorEvaluateAsync(
      "(()=>{const b=Array.from(document.querySelectorAll('button')).find(b=>b.getAttribute('aria-label')==='旋转 90°');if(!b||b.disabled)return false;b.click();return true})()");
    if (result != "true") throw new InvalidOperationException("Pin rotation entry is unavailable.");
    await Task.Delay(50);
  }
}
