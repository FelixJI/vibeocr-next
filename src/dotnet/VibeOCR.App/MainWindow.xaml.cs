using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
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
  private readonly WebWorkbenchHost webHost;
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
    WindowLayoutStore layoutStore)
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

    string resourceRoot = Path.Combine(layout.DataRoot, "web-resources");
    Directory.CreateDirectory(resourceRoot);
    var resourceBroker = new WorkbenchResourceBroker(resourceRoot);
    var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
    var commandHandler = new DesktopWorkbenchCommandHandler(
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
      annotationStore);
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

    WindowGeometry? saved = layoutStore.Load();
    var presenter = (OverlappedPresenter)AppWindow.Presenter;
    if (saved is { } geometry)
    {
      AppWindow.MoveAndResize(new RectInt32(
        geometry.X,
        geometry.Y,
        geometry.Width,
        geometry.Height));
      if (geometry.IsMaximized)
      {
        presenter.Maximize();
      }
      return;
    }

    DisplayArea area = DisplayArea.GetFromWindowId(
      AppWindow.Id,
      DisplayAreaFallback.Nearest);
    RectInt32 work = area.WorkArea;
    int x = work.X + Math.Max(0, (work.Width - DefaultWidth) / 2);
    int y = work.Y + Math.Max(0, (work.Height - DefaultHeight) / 2);
    AppWindow.MoveAndResize(new RectInt32(x, y, DefaultWidth, DefaultHeight));
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
      CompleteWebReadySmoke();
    }
    AppLog.Info($"Web workbench: {state}");
  }

  private static void CompleteWebReadySmoke()
  {
    if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") != "web-ready")
    {
      return;
    }
    string? healthFile = Environment.GetEnvironmentVariable(
      "VIBEOCR_WEB_READY_FILE");
    if (!string.IsNullOrWhiteSpace(healthFile))
    {
      File.WriteAllText(
        healthFile,
        "{\"schema_version\":1,\"state\":\"bridge-ready\"}");
    }
    Environment.Exit(0);
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
    webHost.ProtocolViolation -= OnProtocolViolation;
    webHost.RecoveryRequired -= OnRecoveryRequired;
    webHost.StateChanged -= OnHostStateChanged;
    await webHost.DisposeAsync();
  }
}
