using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Pins;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Services;
using VibeOCR.App.ViewModels;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App;

public sealed partial class App
{
  private sealed class PinSmokeHotkeys : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict) { conflict = null; return true; }
    public void Unregister() { }
  }
  private sealed class PinSmokeStartup : IStartupRegistrar
  {
    public bool SetEnabled(bool enabled) => true;
  }

  private async Task VerifyPinsUiAsync(PortableLayout layout)
  {
    layout = layout with { WebAssetsRoot = Path.Combine(AppContext.BaseDirectory, "WebAssets") };
    var decodeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var decodeReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var seed = new PinHistoryStore(layout.DataRoot);
    seed.SetEnabled(true);
    seed.RecordSuccessfulCapture("startup-probe", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
    seed.SetEnabled(false);
    MainWindow? main = null;
    main = new MainWindow(new DiagnosticsViewModel("pins-smoke", new PrerequisiteReport([])), layout,
      () => new RecognitionViewModel(_inferenceGateway, new InputService(() => WinRT.Interop.WindowNative.GetWindowHandle(main!))),
      static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(), () => new SettingsViewModel(_inferenceGateway),
      () => new ShellViewModel(new PinSmokeHotkeys(), new PinSmokeStartup()),
      static () => throw new InvalidOperationException(), new WindowLayoutStore(Path.Combine(layout.DataRoot, "layout.json")),
      inferenceAttached: () => false, supervisorInstanceId: () => "pins-smoke",
      preparePinnedTextLayer: (session, revision, path, token) => main!.VerifyMaskedPinOcrInputAsync(session, revision, path, token),
      pinHistoryFactory: dataRoot => new PinHistoryStore(dataRoot, validateLoadedImage: async png =>
      {
        decodeStarted.TrySetResult();
        await decodeReleased.Task;
        await PinSnapshotStore.ValidatePngAsync(png, CancellationToken.None);
        return true;
      }));
    main.Activate();
    try
    {
      await decodeStarted.Task;
      if (!main.AppWindow.IsVisible || !main.SmokeHistoryInitializing)
        throw new InvalidOperationException("Main window activation waited for history decoding.");
      decodeReleased.TrySetResult(true);
      await main.SmokeHistoryInitialization;
      await main.VerifyPinsSelfCheckAsync();
    }
    finally { decodeReleased.TrySetResult(true); main.Close(); await main.ShutdownCompletion; }
  }
}
