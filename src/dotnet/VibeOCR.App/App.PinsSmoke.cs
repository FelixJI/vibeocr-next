using VibeOCR.App.Features.Recognition;
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
    MainWindow? main = null;
    main = new MainWindow(new DiagnosticsViewModel("pins-smoke", new PrerequisiteReport([])), layout,
      () => new RecognitionViewModel(_inferenceGateway, new InputService(() => WinRT.Interop.WindowNative.GetWindowHandle(main!))),
      static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(), () => new SettingsViewModel(_inferenceGateway),
      () => new ShellViewModel(new PinSmokeHotkeys(), new PinSmokeStartup()),
      static () => throw new InvalidOperationException(), new WindowLayoutStore(Path.Combine(layout.DataRoot, "layout.json")),
      inferenceAttached: () => false);
    main.Activate();
    try { await main.VerifyPinsSelfCheckAsync(); }
    finally { main.Close(); await main.ShutdownCompletion; }
  }
}
