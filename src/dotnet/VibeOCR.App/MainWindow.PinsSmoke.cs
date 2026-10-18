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
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 64, 32, 96, 96, pixels);
    await encoder.FlushAsync();
    encoded.Seek(0);
    using var reader = new DataReader(encoded.GetInputStreamAt(0));
    await reader.LoadAsync(checked((uint)encoded.Size));
    byte[] png = new byte[checked((int)encoded.Size)];
    reader.ReadBytes(png);
    for (int i = 0; i < 2; i++)
    {
      WorkbenchAnnotationLease lease = await annotationStore.UploadPngAsync(new MemoryStream(png), CancellationToken.None);
      PinScreenshot(annotationStore.Take(lease.ResourceUri), Guid.NewGuid(), 0, null, []);
    }
    PinnedImageWindow first = pinnedImages[0], second = pinnedImages[1];
    await WaitForPinCheckAsync(() => Task.FromResult(first.SmokeDocumentReady && second.SmokeDocumentReady), "Static pins not ready.");
    var observedScopes = new List<string>();
    commandHandler.StateChanged += state => observedScopes.Add(state.Scope);
    try
    {
      CaptureUiSmokeTrace.Stage("pins.first-edit");
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      await RotateSmokePinAsync(first);
      CaptureUiSmokeTrace.Stage("pins.switch-save");
      await EnterPinEditAsync(second);
      await WaitForPinEditorAsync(second);
      if (first.Editing || !second.Editing || pinSnapshotStore!.GetSnapshotProjectPath(first.PinId) is null ||
        first.SmokeImageSize != (32, 64)) throw new InvalidOperationException("Switch did not commit the first pin before opening the second.");

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
      if (!await PreparePinsExitAsync()) throw new InvalidOperationException("Exit recovery could not persist pins.");
      var recovered = pinSnapshotStore.LoadRecovery();
      if (recovered.Count != 1 || !recovered.Any(item => item.Id == first.PinId && item.X == first.SmokePosition.X))
        throw new InvalidOperationException("Pin recovery did not retain content and physical position.");
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

  private static Task WaitForPinEditorAsync(PinnedImageWindow pin) => WaitForPinCheckAsync(
    async () => (await pin.SmokeEditorEvaluateAsync(
      "!!document.querySelector('.canvas-editor[data-screenshot-session]') && !!document.querySelector('canvas[aria-label=\"图片检查画布\"]') && !Array.from(document.querySelectorAll('button')).find(b=>b.getAttribute('aria-label')==='旋转 90°')?.disabled")) == "true",
    "Pin editor did not decode its input.");

  private static async Task RotateSmokePinAsync(PinnedImageWindow pin)
  {
    string result = await pin.SmokeEditorEvaluateAsync(
      "(()=>{const b=Array.from(document.querySelectorAll('button')).find(b=>b.getAttribute('aria-label')==='旋转 90°');if(!b||b.disabled)return false;b.click();return true})()");
    if (result != "true") throw new InvalidOperationException("Pin rotation entry is unavailable.");
    await Task.Delay(50);
  }
}
