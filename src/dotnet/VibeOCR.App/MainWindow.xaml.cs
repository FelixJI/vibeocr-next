using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Text.Json;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
using VibeOCR.App.Services;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace VibeOCR.App;

public sealed partial class MainWindow : Window
{
  // 默认/最小尺寸均为逻辑像素（DIP），与前端 CSS 的 min-width/min-height 一致；
  // 写入 AppWindow/WM_GETMINMAXINFO 前由 WindowGeometryPolicy 按 DPI 换算为物理像素。
  private const int DefaultWidth = 1280;
  private const int DefaultHeight = 800;
  private const int MinWidth = 1024;
  private const int MinHeight = 720;

  [DllImport("user32.dll")]
  private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

  [DllImport("user32.dll")]
  private static extern bool IsIconic(IntPtr hWnd);

  [DllImport("user32.dll")]
  private static extern bool IsZoomed(IntPtr hWnd);

  [StructLayout(LayoutKind.Sequential)]
  private struct RECT
  {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
  }

  private readonly DiagnosticsViewModel diagnostics;
  private readonly PortableLayout layout;
  private readonly WindowLayoutStore layoutStore;
  private readonly WorkbenchApplication application;
  private readonly DesktopWorkbenchCommandHandler commandHandler;
  private readonly WebWorkbenchHost webHost;
  private readonly WorkbenchResourceBroker resourceBroker;
  private readonly string resourceRoot;
  private readonly List<PinnedImageWindow> pinnedImages = [];
  private readonly SyntheticScreenRegionPicker? screenshotSmokePicker;
  private readonly Func<string?>? supervisorInstanceId;
  private readonly Func<int>? smokeSubmitAttempts;
  private readonly Func<string?>? smokeLastJobId;
  private readonly Func<int>? smokeStartupEnsureAttempts;
  private readonly Func<bool>? smokeInferenceAttached;
  private bool screenshotSmokeStarted;
  private bool initialized;
  private WorkbenchRoute currentRoute = WorkbenchRoute.Recognition;

  public MainWindow(
    DiagnosticsViewModel diagnostics,
    PortableLayout layout,
    Func<RecognitionViewModel> recognitionFactory,
    Func<BatchViewModel> batchFactory,
    Func<QrCodeViewModel> qrCodeFactory,
    Func<PdfViewModel> pdfFactory,
    Func<SettingsViewModel> settingsFactory,
    Func<ShellViewModel> shellFactory,
    Func<UpdateViewModel> updateFactory,
    WindowLayoutStore layoutStore,
    Func<bool>? inferenceAttached = null,
    Func<string?>? supervisorInstanceId = null,
    IScreenRegionPicker? screenshotSmokePicker = null,
    Func<int>? smokeSubmitAttempts = null,
    Func<string?>? smokeLastJobId = null,
    Func<int>? smokeStartupEnsureAttempts = null)
  {
    this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
    ArgumentNullException.ThrowIfNull(recognitionFactory);
    ArgumentNullException.ThrowIfNull(batchFactory);
    ArgumentNullException.ThrowIfNull(qrCodeFactory);
    ArgumentNullException.ThrowIfNull(pdfFactory);
    ArgumentNullException.ThrowIfNull(settingsFactory);
    ArgumentNullException.ThrowIfNull(shellFactory);
    ArgumentNullException.ThrowIfNull(updateFactory);
    this.layoutStore = layoutStore ?? throw new ArgumentNullException(nameof(layoutStore));
    this.supervisorInstanceId = supervisorInstanceId;
    this.screenshotSmokePicker = screenshotSmokePicker as SyntheticScreenRegionPicker;
    this.smokeSubmitAttempts = smokeSubmitAttempts;
    this.smokeLastJobId = smokeLastJobId;
    this.smokeStartupEnsureAttempts = smokeStartupEnsureAttempts;
    smokeInferenceAttached = inferenceAttached;

    resourceRoot = Path.Combine(layout.DataRoot, "web-resources");
    Directory.CreateDirectory(resourceRoot);
    resourceBroker = new WorkbenchResourceBroker(resourceRoot);
    var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
    commandHandler = new DesktopWorkbenchCommandHandler(
      recognitionFactory,
      batchFactory,
      qrCodeFactory,
      pdfFactory,
      settingsFactory,
      shellFactory,
      updateFactory,
      diagnostics,
      resourceBroker,
      resourceRoot,
      () => WindowNative.GetWindowHandle(this),
      annotationStore,
      inferenceAttached: inferenceAttached,
      supervisorInstanceId: supervisorInstanceId,
      pinScreenshot: PinScreenshot);
    commandHandler.ScreenshotTextLayerChanged += layer =>
    {
      void UpdatePins()
      {
        foreach (PinnedImageWindow pinned in pinnedImages.ToArray())
        {
          pinned.Update(layer);
        }
      }
      if (DispatcherQueue.HasThreadAccess) UpdatePins();
      else DispatcherQueue.TryEnqueue(UpdatePins);
    };
    application = new WorkbenchApplication(
      DesktopWorkbenchCommandHandler.Capabilities,
      WorkbenchRoute.Recognition,
      commandHandler);
    webHost = new WebWorkbenchHost(
      application,
      resourceBroker,
      annotationStore);
    webHost.ProtocolViolation += OnProtocolViolation;
    webHost.RecoveryRequired += OnRecoveryRequired;
    webHost.StateChanged += OnHostStateChanged;

    InitializeComponent();
    Title = "VibeOCR";
    ApplyPersistedOrDefaultGeometry();
    Closed += OnWindowClosed;
  }

  private async void OnWorkbenchLoaded(object sender, RoutedEventArgs args)
  {
    if (initialized)
    {
      return;
    }
    initialized = true;
    try
    {
      await webHost.InitializeAsync(
        WorkbenchWebView,
        layout.WebAssetsRoot);
    }
    catch (Exception error) when (
      error is InvalidOperationException or DirectoryNotFoundException)
    {
      AppLog.Error("Web workbench initialization failed", error);
      ShowRecovery($"工作台初始化失败：{error.GetType().Name}");
    }
  }

  private void ApplyPersistedOrDefaultGeometry()
  {
    IntPtr hwnd = WindowNative.GetWindowHandle(this);
    WindowMinSizeEnforcer.Apply(hwnd, MinWidth, MinHeight);

    double scale = WindowGeometryPolicy.GetWindowScale(hwnd);
    int minWidth = WindowGeometryPolicy.ScaleToPhysical(MinWidth, scale);
    int minHeight = WindowGeometryPolicy.ScaleToPhysical(MinHeight, scale);
    DisplayArea area = DisplayArea.GetFromWindowId(
      AppWindow.Id,
      DisplayAreaFallback.Nearest);

    WindowGeometry? saved = layoutStore.Load();
    var presenter = (OverlappedPresenter)AppWindow.Presenter;
    if (saved is { } geometry)
    {
      WindowGeometry clamped = WindowGeometryPolicy.ClampRestored(
        geometry,
        area.WorkArea,
        minWidth,
        minHeight);
      AppWindow.MoveAndResize(new RectInt32(
        clamped.X,
        clamped.Y,
        clamped.Width,
        clamped.Height));
      if (geometry.IsMaximized)
      {
        presenter.Maximize();
      }
      return;
    }

    AppWindow.MoveAndResize(WindowGeometryPolicy.DefaultGeometry(
      area.WorkArea,
      scale,
      DefaultWidth,
      DefaultHeight));
  }

  internal WindowGeometry? CaptureGeometry()
  {
    IntPtr hwnd = WindowNative.GetWindowHandle(this);
    if (IsIconic(hwnd))
    {
      return null;
    }
    bool maximized = IsZoomed(hwnd);
    GetWindowRect(hwnd, out RECT rect);
    return new WindowGeometry(
      rect.Left,
      rect.Top,
      rect.Right - rect.Left,
      rect.Bottom - rect.Top,
      maximized);
  }

  internal void NavigateTo(string? destination)
  {
    WorkbenchRoute route = destination switch
    {
      "recognition" => WorkbenchRoute.Recognition,
      "batch" => WorkbenchRoute.Batch,
      "qrcode" => WorkbenchRoute.QrCode,
      "pdf" => WorkbenchRoute.Pdf,
      "settings" => WorkbenchRoute.Settings,
      "about" => WorkbenchRoute.About,
      "diagnostics" => WorkbenchRoute.Diagnostics,
      _ => WorkbenchRoute.Recognition,
    };
    if (destination is not null && route == WorkbenchRoute.Recognition &&
        destination != "recognition")
    {
      AppLog.Warn(
        $"Navigation destination '{destination}' is unavailable; falling back to recognition.");
    }
    currentRoute = route;
    _ = NavigateAsync(route);
  }

  private async Task NavigateAsync(WorkbenchRoute route)
  {
    WorkbenchCommandReceipt receipt = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        Guid.NewGuid(),
        new NavigateWorkbenchCommand(route)),
      CancellationToken.None);
    if (!receipt.Ok)
    {
      AppLog.Warn($"Workbench navigation failed: {receipt.Error?.Code}");
    }
  }

  internal void ShowAndNavigate(string? destination)
  {
    AppWindow.Show();
    Activate();
    NavigateTo(destination);
  }

  internal async Task RecognizeScreenshotAsync()
  {
    NavigateTo("recognition");
    await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        Guid.NewGuid(),
        new CaptureRecognitionScreenCommand()),
      CancellationToken.None);
  }

  private void OnDragOver(object sender, DragEventArgs args)
  {
    if (args.DataView.Contains(StandardDataFormats.StorageItems))
    {
      args.AcceptedOperation = DataPackageOperation.Copy;
    }
  }

  private async void OnDrop(object sender, DragEventArgs args)
  {
    try
    {
      if (!args.DataView.Contains(StandardDataFormats.StorageItems))
      {
        return;
      }
      IReadOnlyList<IStorageItem> items = await args.DataView.GetStorageItemsAsync();
      string[] paths = items.OfType<StorageFile>().Select(file => file.Path).ToArray();
      if (paths.Length == 0)
      {
        return;
      }
      WorkbenchCommand command = currentRoute switch
      {
        WorkbenchRoute.Batch => new AddDroppedBatchFilesCommand(paths),
        WorkbenchRoute.QrCode => new DecodeDroppedQrCodeCommand(paths[0]),
        WorkbenchRoute.Pdf => new OpenDroppedPdfCommand(paths[0]),
        _ => new RecognizeDroppedFileCommand(paths[0]),
      };
      await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), command),
        CancellationToken.None);
    }
    catch (Exception error) when (
      error is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
      AppLog.Error("Workbench file drop failed", error);
      RecoveryStatus.Text = "无法读取拖入的文件，请重新选择。";
    }
  }

  private void OnProtocolViolation(Exception error)
  {
    AppLog.Error("Workbench bridge protocol violation", error);
    RecoveryStatus.Text = "页面消息被安全拒绝；如果界面无响应，请重新加载。";
  }

  private void OnRecoveryRequired() =>
    DispatcherQueue.TryEnqueue(() => ShowRecovery(
      "WebView2 连续失败，已停止自动恢复以避免重载循环。"));

  private void OnHostStateChanged(string state)
  {
    if (state == "bridge-ready")
    {
      DispatcherQueue.TryEnqueue(() =>
      {
        RecoveryPanel.Visibility = Visibility.Collapsed;
        WorkbenchWebView.Visibility = Visibility.Visible;
      });
      _ = CompleteWebReadySmokeAsync();
      if (!screenshotSmokeStarted &&
          Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "screenshot-e2e")
      {
        screenshotSmokeStarted = true;
        _ = CompleteScreenshotE2eSmokeAsync();
      }
      if (!screenshotSmokeStarted &&
          Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "text-selection-e2e")
      {
        screenshotSmokeStarted = true;
        _ = CompleteTextSelectionE2eSmokeAsync();
      }
    }
    AppLog.Info($"Web workbench: {state}");
  }

  private async Task CompleteWebReadySmokeAsync()
  {
    if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") != "web-ready")
    {
      return;
    }
    string? healthFile = Environment.GetEnvironmentVariable(
      "VIBEOCR_WEB_READY_FILE");
    string relative = $"web-ready-{Guid.NewGuid():N}.txt";
    string path = Path.Combine(resourceRoot, relative);
    try
    {
      await File.WriteAllTextAsync(path, "web-resource-ok");
      WorkbenchResourceLease lease = resourceBroker.Lease(
        relative, "text/plain; charset=utf-8", TimeSpan.FromMinutes(1));
      const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
      string script = $$"""
        window.__vibeocrResourceSmoke = "pending";
        (async () => {
          try {
          const resource = await fetch({{JsonSerializer.Serialize(lease.Uri.AbsoluteUri)}},
            { cache: "no-store", credentials: "omit" });
          if (!resource.ok || await resource.text() !== "web-resource-ok")
            throw new Error("opaque resource GET failed");
          const bytes = Uint8Array.from(atob("{{png}}"), c => c.charCodeAt(0));
          const upload = await fetch("/__annotation", {
            method: "POST", headers: { "Content-Type": "image/png" }, body: bytes,
          });
          if (upload.status !== 201 ||
              typeof (await upload.json()).resourceUri !== "string")
            throw new Error("annotation POST failed");
          return "resource-and-annotation-ok";
          } catch (error) {
            return `failure: ${error instanceof Error ? error.message : String(error)}`;
          }
        })().then(result => { window.__vibeocrResourceSmoke = result; })
        """;
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(script);
      string result = "";
      for (int attempt = 0; attempt < 50; attempt++)
      {
        result = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
          "window.__vibeocrResourceSmoke");
        if (result != JsonSerializer.Serialize("pending")) break;
        await Task.Delay(100);
      }
      if (result != JsonSerializer.Serialize("resource-and-annotation-ok"))
      {
        throw new InvalidOperationException($"Web workbench resource smoke failed: {result}");
      }
      resourceBroker.Revoke(lease);
      if (!string.IsNullOrWhiteSpace(healthFile))
      {
        File.WriteAllText(healthFile,
          "{\"schema_version\":1,\"state\":\"bridge-ready\",\"resources\":\"verified\"}");
      }
      Environment.Exit(0);
    }
    catch (Exception error)
    {
      AppLog.Error("Web workbench resource smoke failed", error);
      if (!string.IsNullOrWhiteSpace(healthFile))
      {
        File.WriteAllText(healthFile, JsonSerializer.Serialize(new
        {
          schema_version = 1,
          state = "failed",
          error = error.Message,
        }));
      }
      Environment.Exit(1);
    }
    finally
    {
      File.Delete(path);
    }
  }

  private void ShowRecovery(string detail)
  {
    RecoveryDetail.Text = detail;
    WorkbenchWebView.Visibility = Visibility.Collapsed;
    RecoveryPanel.Visibility = Visibility.Visible;
  }

  private void OnReloadWorkbenchClicked(object sender, RoutedEventArgs args)
  {
    RecoveryStatus.Text = "正在重新加载工作台…";
    WorkbenchWebView.Visibility = Visibility.Visible;
    RecoveryPanel.Visibility = Visibility.Collapsed;
    try
    {
      webHost.Reload();
    }
    catch (InvalidOperationException error)
    {
      AppLog.Error("Workbench reload failed", error);
      ShowRecovery("工作台尚未完成初始化，无法重载。请导出诊断后退出。 ");
    }
  }

  private async void OnExportDiagnosticsClicked(object sender, RoutedEventArgs args)
  {
    string destination = Path.Combine(
      layout.DataRoot,
      $"vibeocr-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    try
    {
      await diagnostics.ExportAsync(destination, CancellationToken.None);
      RecoveryStatus.Text = $"诊断已导出：{destination}";
    }
    catch (IOException error)
    {
      AppLog.Error("Diagnostic export failed", error);
      RecoveryStatus.Text = "诊断导出失败，请检查数据目录权限。";
    }
  }

  private void OnExitClicked(object sender, RoutedEventArgs args) =>
    Application.Current.Exit();

  private async void OnWindowClosed(object sender, WindowEventArgs args)
  {
    Closed -= OnWindowClosed;
    foreach (PinnedImageWindow pinned in pinnedImages.ToArray()) pinned.Close();
    webHost.ProtocolViolation -= OnProtocolViolation;
    webHost.RecoveryRequired -= OnRecoveryRequired;
    webHost.StateChanged -= OnHostStateChanged;
    await webHost.DisposeAsync();
  }

  private void PinScreenshot(
    WorkbenchAnnotationFile image,
    Guid sessionId,
    long revision,
    RecognitionTextLayerState? layer)
  {
    if (pinnedImages.Count >= 4)
    {
      throw new InvalidOperationException("最多同时打开四张贴图，请先关闭一张。");
    }
    var pinned = new PinnedImageWindow(image, sessionId, revision, layer,
      () => commandHandler.PreparePinnedTextLayerAsync(sessionId, revision, image.Path));
    pinned.Closed += closed => pinnedImages.Remove(closed);
    pinnedImages.Add(pinned);
    _ = ShowPinnedAsync(pinned);
  }

  private static async Task ShowPinnedAsync(PinnedImageWindow pinned)
  {
    try { await pinned.ShowAsync(); }
    catch (Exception error)
    {
      AppLog.Error("Pinned image window failed to start", error);
      pinned.Close();
    }
  }
}
