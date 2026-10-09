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
using VibeOCR.Platform.Inference;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace VibeOCR.App;

public sealed partial class MainWindow : Window
{
  // 默认/最小尺寸均为逻辑像素（DIP）；Web 布局还需适配扣除窗口边框后的可用尺寸。
  // 写入 AppWindow/WM_GETMINMAXINFO 前由 WindowGeometryPolicy 按 DPI 换算为物理像素。
  private const int DefaultWidth = 1280;
  private const int DefaultHeight = 800;
  private const int MinWidth = 640;
  private const int MinHeight = 480;

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
  private readonly WorkbenchAnnotationStore annotationStore;
  private ImageEditWindow? imageEditor;
  private bool shuttingDown;
  internal Microsoft.UI.Xaml.Controls.WebView2? SceneWebView => imageEditor?.WebView;
  internal Guid? SceneSessionId => imageEditor?.SessionId;
  private Microsoft.UI.Xaml.Controls.WebView2 SmokeEditorWebView => SceneWebView ?? WorkbenchWebView;
  private readonly List<PinnedImageWindow> pinnedImages = [];
  private readonly Dictionary<(Guid SessionId, long Revision),
    (Task<RecognitionTextLayerState?> Task, CancellationTokenSource Cancellation)> pinTextTasks = [];
  private readonly SyntheticScreenRegionPicker? screenshotSmokePicker;
  private readonly Func<string?>? supervisorInstanceId;
  private readonly Func<int>? smokeSubmitAttempts;
  private readonly Func<string?>? smokeLastJobId;
  private readonly Func<int>? smokeStartupEnsureAttempts;
  private readonly Func<bool>? smokeInferenceAttached;
  private readonly Func<ManagedEnvironmentList?>? smokeEnvironmentSnapshot;
  private readonly Func<ManagedEnvironmentSession?>? smokeManagedSession;
  private readonly Func<int>? smokeInstallAttempts;
  private bool screenshotSmokeStarted;
  private bool managedEnvironmentSmokeStarted;
  private bool paddleModesSmokeStarted;
  private bool initialized;

  internal MainWindow(
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
    Func<int>? smokeStartupEnsureAttempts = null,
    Func<ManagedEnvironmentList?>? smokeEnvironmentSnapshot = null,
    Func<ManagedEnvironmentSession?>? smokeManagedSession = null,
    Func<int>? smokeInstallAttempts = null,
    ShellActionDispatcher? shellActions = null)
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
    this.smokeEnvironmentSnapshot = smokeEnvironmentSnapshot;
    this.smokeManagedSession = smokeManagedSession;
    this.smokeInstallAttempts = smokeInstallAttempts;
    smokeInferenceAttached = inferenceAttached;

    resourceRoot = Path.Combine(layout.DataRoot, "web-resources");
    Directory.CreateDirectory(resourceRoot);
    resourceBroker = new WorkbenchResourceBroker(resourceRoot);
    annotationStore = new WorkbenchAnnotationStore(resourceRoot);
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
      () => imageEditor is { } editor ? editor.Handle : WindowNative.GetWindowHandle(this),
      annotationStore,
      inferenceAttached: inferenceAttached,
      supervisorInstanceId: supervisorInstanceId,
      pinScreenshot: PinScreenshot,
      shellActions: shellActions,
      optionsLayout: layout, confirmPdfClose: ConfirmPdfCloseAsync,
      flushPdfPreview: FlushPdfPreviewAsync);
    commandHandler.ScreenshotSessionReady += ShowImageEditor;
    commandHandler.ScreenshotCaptureStarting += () => imageEditor?.HideForCapture();
    commandHandler.ScreenshotCaptureFinished += () => imageEditor?.RestoreAfterCapture();
    commandHandler.ScreenshotTextLayerChanged += layer =>
    {
      void UpdatePins()
      {
        foreach (PinnedImageWindow pinned in pinnedImages.ToArray())
          pinned.Update(layer);
      }
      if (DispatcherQueue.HasThreadAccess) UpdatePins();
      else DispatcherQueue.TryEnqueue(UpdatePins);
    };
    commandHandler.ScreenshotTextLayerInvalidated += (sessionId, revision) =>
    {
      void InvalidatePins()
      {
        RemovePinTextTask((sessionId, revision));
        foreach (PinnedImageWindow pinned in pinnedImages.ToArray())
          if (pinned.SessionId == sessionId && pinned.Revision == revision)
            pinned.Update(null);
      }
      if (DispatcherQueue.HasThreadAccess) InvalidatePins();
      else DispatcherQueue.TryEnqueue(InvalidatePins);
    };
    commandHandler.ScreenshotSessionDetached += (sessionId, revision) =>
    {
      void MarkPins()
      {
        if (imageEditor is { } editor && editor.SessionId == sessionId)
        {
          editor.TransferOwnerRestorationTo(commandHandler.PendingScreenshotCaptureScene);
          imageEditor = null;
          editor.Close();
        }
        foreach (PinnedImageWindow pinned in pinnedImages.ToArray())
          if (pinned.SessionId == sessionId && pinned.Revision == revision)
            pinned.MarkOldSnapshot();
      }
      if (DispatcherQueue.HasThreadAccess) MarkPins();
      else DispatcherQueue.TryEnqueue(MarkPins);
    };
    commandHandler.PinnedTextEnvironmentChanged += InvalidatePinnedTextLayers;
    commandHandler.ScreenshotSceneRecognitionHandoff += sessionId =>
    {
      void Handoff()
      {
        // scene 显式识别交接：先解除引用再关窗，避免 Closed 处理器把
        // 会话关闭而取消刚提交的任务；随后主窗口切到识别承载面。
        if (imageEditor is { } editor && editor.SessionId == sessionId)
        {
          editor.TransferOwnerRestorationTo(commandHandler.PendingScreenshotCaptureScene);
          imageEditor = null;
          editor.Close();
        }
        ShowAndNavigate("recognition");
      }
      if (DispatcherQueue.HasThreadAccess) Handoff();
      else DispatcherQueue.TryEnqueue(Handoff);
    };
    commandHandler.ScreenshotSelectionRecognized += () =>
    {
      // 普通截图动作栏显式识别终态：显示主窗并切到识别承载面（明确分支，
      // 不贯穿所有动作无条件激活）。
      void Show()
      {
        ShowAndNavigate("recognition");
      }
      if (DispatcherQueue.HasThreadAccess) Show();
      else DispatcherQueue.TryEnqueue(Show);
    };
    commandHandler.ScreenshotSelectionSettingsRequested += () =>
    {
      // 动作栏“打开设置”：放弃本次截图并显式进入现有设置页，不自动安装。
      void OpenSettings()
      {
        ShowAndNavigate("settings");
      }
      if (DispatcherQueue.HasThreadAccess) OpenSettings();
      else DispatcherQueue.TryEnqueue(OpenSettings);
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
    Activated += OnWindowActivated;
  }

  private async void OnWorkbenchLoaded(object sender, RoutedEventArgs args)
  {
    if (initialized)
    {
      return;
    }
    initialized = true;
    WebReadySmokeStatus.Stage("webview-initializing");
    try
    {
      await webHost.InitializeAsync(
        WorkbenchWebView,
        layout.WebAssetsRoot);
    }
    catch (Exception error) when (
      WebReadySmokeStatus.Enabled || error is InvalidOperationException or DirectoryNotFoundException)
    {
      AppLog.Error("Web workbench initialization failed", error);
      WebReadySmokeStatus.Fail("webview-initializing", error);
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

  private void ShowImageEditor(Guid sessionId, VibeOCR.Platform.Windows.PhysicalRectangle? bounds)
  {
    if (shuttingDown || commandHandler.CurrentImageSessionId != sessionId) return;
    ScreenshotCaptureScene? captureScene = commandHandler.TakeScreenshotCaptureScene(sessionId);
    if (imageEditor is { } previous)
    {
      previous.TransferOwnerRestorationTo(captureScene);
      imageEditor = null;
      previous.Close();
    }
    ImageEditWindow editor;
    try
    {
      editor = new ImageEditWindow(sessionId, application, resourceBroker, annotationStore,
        layout.WebAssetsRoot, bounds, captureScene);
    }
    catch
    {
      captureScene?.Dispose();
      throw;
    }
    imageEditor = editor;
    editor.Closed += async (_, _) =>
    {
      if (!ReferenceEquals(imageEditor, editor)) return;
      imageEditor = null;
      if (!shuttingDown && commandHandler.CurrentImageSessionId == sessionId)
        await application.ExecuteAsync(new WorkbenchCommandEnvelope(Guid.NewGuid(),
          new CloseScreenshotSessionCommand()), CancellationToken.None);
    };
    editor.Activate();
  }

  internal void NavigateTo(string? destination)
  {
    WorkbenchRoute route = destination switch
    {
      "recognition" => WorkbenchRoute.Recognition,
      "imageEdit" => WorkbenchRoute.ImageEdit,
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
    commandHandler.EndHotkeyRecording();
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

  internal Task RefreshRecognitionCatalogAsync(CancellationToken cancellationToken) =>
    commandHandler.RefreshRecognitionCatalogAsync(cancellationToken);

  internal async Task RecognizeScreenshotAsync()
  {
    NavigateTo("recognition");
    await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        Guid.NewGuid(),
        new CaptureRecognitionScreenCommand()),
      CancellationToken.None);
  }

  /// <summary>纯截图编辑入口：只截取并进入编辑会话，不提交任何识别请求。</summary>
  internal async Task CaptureScreenshotForEditAsync()
  {
    await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        Guid.NewGuid(),
        new CaptureScreenshotSessionCommand()),
      CancellationToken.None);
  }

  /// <summary>剪贴板识别入口：与页面“读取剪贴板”同一命令。</summary>
  internal async Task RecognizeClipboardAsync()
  {
    NavigateTo("recognition");
    await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        Guid.NewGuid(),
        new ReadRecognitionClipboardCommand()),
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
      WorkbenchCommand command = application.CurrentRoute switch
      {
        WorkbenchRoute.ImageEdit => new OpenDroppedImageEditFileCommand(paths[0]),
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
    WebReadySmokeStatus.Fail("bridge-protocol", error);
    RecoveryStatus.Text = "页面消息被安全拒绝；如果界面无响应，请重新加载。";
  }

  private void OnRecoveryRequired() =>
    DispatcherQueue.TryEnqueue(() => ShowRecovery(
      "WebView2 连续失败，已停止自动恢复以避免重载循环。"));

  private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
  {
    if (args.WindowActivationState == WindowActivationState.Deactivated)
      commandHandler.EndHotkeyRecording();
  }

  private void OnHostStateChanged(string state)
  {
    WebReadySmokeStatus.Stage(state);
    if (state == "navigation-starting" ||
        state.StartsWith("process-failed:", StringComparison.Ordinal) ||
        state.StartsWith("bridge-command-failed:", StringComparison.Ordinal))
      commandHandler.EndHotkeyRecording();
    if (state == "bridge-ready")
    {
      DispatcherQueue.TryEnqueue(async () =>
      {
        RecoveryPanel.Visibility = Visibility.Collapsed;
        WorkbenchWebView.Visibility = Visibility.Visible;
        await CompleteWebReadySmokeAsync();
      });
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
      if (!managedEnvironmentSmokeStarted &&
          Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "managed-environment-e2e")
      {
        managedEnvironmentSmokeStarted = true;
        _ = CompleteManagedEnvironmentE2eSmokeAsync();
      }
      if (!paddleModesSmokeStarted &&
          Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "paddle-modes-e2e")
      {
        paddleModesSmokeStarted = true;
        _ = CompletePaddleModesSmokeAsync();
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
    string smokeStage = "resource-verification";
    try
    {
      WebReadySmokeStatus.Stage(smokeStage);
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
      // Exercise the packaged bundle in the real WebView2 client area. A ready
      // bridge alone cannot detect fixed-width content or a blank React root.
      foreach (SizeInt32 size in new[] { new SizeInt32(640, 480), new SizeInt32(1280, 800) })
      {
        smokeStage = $"layout-verification-{size.Width}x{size.Height}";
        WebReadySmokeStatus.Stage(smokeStage);
        double scale = WindowGeometryPolicy.GetWindowScale(WindowNative.GetWindowHandle(this));
        AppWindow.Resize(new SizeInt32(
          WindowGeometryPolicy.ScaleToPhysical(size.Width, scale),
          WindowGeometryPolicy.ScaleToPhysical(size.Height, scale)));
        await Task.Delay(100);
        bool ready = false;
        for (int attempt = 0; attempt < 50; attempt++)
        {
          string layoutResult = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("""
            (() => {
              const main = document.querySelector('main');
              const heading = main?.querySelector('h1');
              const root = document.querySelector('#root > .fluent-root');
              return !!heading && !!root && main.clientWidth > 0 && main.clientHeight > 0 &&
                Math.abs(root.getBoundingClientRect().height - innerHeight) <= 1 &&
                root.getBoundingClientRect().height <= innerHeight + 1 &&
                document.documentElement.scrollWidth <= innerWidth + 1 &&
                main.scrollWidth <= main.clientWidth + 1;
            })()
            """);
          if (layoutResult == "true") { ready = true; break; }
          await Task.Delay(100);
        }
        if (!ready) throw new InvalidOperationException("Packaged workbench layout did not fit its client area.");
      }
      // #187 AC5：窗口原状态恢复自检——真实 WinUI 窗口（自检自建）的可见/
      // 最小化/隐藏三态经生产 RestoreOwnerState 实际恢复；不采样用户桌面，
      // 恢复方法前台传 0 不恢复/检查外部窗口前台（自检初次显示会激活其
      // 自建窗口；前台物理一致性 UNVERIFIED）。无需 Runtime/Supervisor。
      smokeStage = "owner-restore-verification";
      WebReadySmokeStatus.Stage(smokeStage);
      await ScreenRegionPicker.VerifyOwnerRestoreSelfCheckAsync(CancellationToken.None);
      // 只用合成像素验证覆盖层首帧等待与冻结背景交接，不采样用户桌面。
      smokeStage = "capture-scene-verification";
      WebReadySmokeStatus.Stage(smokeStage);
      using (ScreenshotCaptureScene scene = await ScreenRegionPicker.CreateSyntheticSceneAsync(
        new VibeOCR.Platform.Windows.PhysicalRectangle(0, 0, 240, 160), new byte[240 * 160 * 4], 240 * 4, CancellationToken.None))
      {
        if (scene.TakeBackground() is not { Length: 153654 })
          throw new InvalidOperationException("Capture scene lost its frozen background.");
      }
      resourceBroker.Revoke(lease);
      if (!string.IsNullOrWhiteSpace(healthFile))
      {
        File.WriteAllText(healthFile,
          "{\"schema_version\":1,\"state\":\"bridge-ready\",\"resources\":\"verified\"," +
          "\"layout_sizes_verified\":2,\"owner_restore\":\"verified\"}");
      }
      Environment.Exit(0);
    }
    catch (Exception error)
    {
      AppLog.Error("Web workbench resource smoke failed", error);
      WebReadySmokeStatus.Fail(smokeStage, error);
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

  private Task<bool> FlushPdfPreviewAsync(string documentId) => webHost.FlushPdfPreviewAsync(documentId);

  public Task<bool> PreparePdfExitAsync() => commandHandler.RequestCloseAllPdfAsync();
  private Microsoft.UI.Xaml.Controls.ContentDialog? activePdfCloseDialog;
  private async Task<PdfCloseDecision> ConfirmPdfCloseAsync(PdfDocumentEntry entry)
  {
    var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
    {
      XamlRoot = (Content as FrameworkElement)?.XamlRoot,
      Title = $"保存 {Path.GetFileName(entry.Model.FilePath)} 的修改？",
      Content = "保存提交当前文档；放弃会丢弃未保存修改；取消保留文档。",
      PrimaryButtonText = "保存", SecondaryButtonText = "放弃修改", CloseButtonText = "取消",
      DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
    };
    activePdfCloseDialog = dialog;
    try
    {
      return await dialog.ShowAsync() switch
      {
        Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary => PdfCloseDecision.Save,
        Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary => PdfCloseDecision.Discard,
        _ => PdfCloseDecision.Cancel,
      };
    }
    finally { if (ReferenceEquals(activePdfCloseDialog, dialog)) activePdfCloseDialog = null; }
  }
  private void OnExitClicked(object sender, RoutedEventArgs args) =>
    Close();

  private async void OnWindowClosed(object sender, WindowEventArgs args)
  {
    Closed -= OnWindowClosed;
    Activated -= OnWindowActivated;
    commandHandler.EndHotkeyRecording();
    shuttingDown = true;
    commandHandler.ScreenshotSessionReady -= ShowImageEditor;
    if (imageEditor is { } editor)
    {
      imageEditor = null;
      await editor.DisposeAsync();
      editor.Close();
    }
    ClearPinTextTasks();
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
    RecognitionTextLayerState? layer,
    IReadOnlyList<WorkbenchExclusionBox> excludeBoxes)
  {
    if (pinnedImages.Count >= 4)
    {
      throw new InvalidOperationException("最多同时打开四张贴图，请先关闭一张。");
    }
    var pinned = new PinnedImageWindow(image, sessionId, revision, layer, excludeBoxes,
      () => PreparePinTextAsync(sessionId, revision, image.Path, excludeBoxes),
      () => supervisorInstanceId?.Invoke());
    pinned.Closed += closed =>
    {
      pinnedImages.Remove(closed);
      if (!pinnedImages.Any(other => other.SessionId == sessionId && other.Revision == revision))
        RemovePinTextTask((sessionId, revision));
    };
    pinnedImages.Add(pinned);
    _ = ShowPinnedAsync(pinned);
  }

  private async Task<RecognitionTextLayerState?> PreparePinTextAsync(
    Guid sessionId, long revision, string imagePath,
    IReadOnlyList<WorkbenchExclusionBox> excludeBoxes)
  {
    var key = (sessionId, revision);
    if (!pinTextTasks.TryGetValue(key, out var entry))
    {
      var cancellation = new CancellationTokenSource();
      entry = (PrepareMaskedPinTextAsync(sessionId, revision, imagePath, excludeBoxes, cancellation.Token), cancellation);
      pinTextTasks.Add(key, entry);
    }
    try
    {
      RecognitionTextLayerState? result = await entry.Task;
      if (result is not null &&
          result.ServiceInstance != (supervisorInstanceId?.Invoke() ?? string.Empty))
      {
        RemovePinTextTask(key);
        throw new PinnedTextPreparationException("本地识别服务已变化，请重新取字。");
      }
      return result;
    }
    catch
    {
      if (pinTextTasks.TryGetValue(key, out var current) &&
          ReferenceEquals(current.Task, entry.Task)) RemovePinTextTask(key);
      throw;
    }
  }

  /// <summary>
  /// 贴图取字输入按需烘焙：临时副本只含白色遮罩像素，识别结束即删除；
  /// 贴图自身的显示/复制/保存像素不受影响。整图被遮罩时拒绝取字而非拒绝贴图。
  /// </summary>
  private async Task<RecognitionTextLayerState?> PrepareMaskedPinTextAsync(
    Guid sessionId, long revision, string imagePath,
    IReadOnlyList<WorkbenchExclusionBox> excludeBoxes,
    CancellationToken cancellationToken)
  {
    if (excludeBoxes.Count == 0)
      return await commandHandler.PreparePinnedTextLayerAsync(
        sessionId, revision, imagePath, cancellationToken);
    if (PinnedTextMask.CoversEntireImage(excludeBoxes))
      throw new PinnedTextPreparationException(
        "整张图都在屏蔽区内：没有可取文字；贴图本身保持不变。");
    string masked = await PinnedTextMask.CreateMaskedPngAsync(
      imagePath, excludeBoxes, cancellationToken);
    try
    {
      return await commandHandler.PreparePinnedTextLayerAsync(
        sessionId, revision, masked, cancellationToken);
    }
    finally
    {
      try { File.Delete(masked); }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }

  private void RemovePinTextTask((Guid SessionId, long Revision) key)
  {
    if (!pinTextTasks.Remove(key, out var entry)) return;
    entry.Cancellation.Cancel();
    entry.Cancellation.Dispose();
  }

  private void ClearPinTextTasks()
  {
    foreach (var key in pinTextTasks.Keys.ToArray()) RemovePinTextTask(key);
  }

  internal void InvalidatePinnedTextLayers()
  {
    void Invalidate()
    {
      ClearPinTextTasks();
      foreach (PinnedImageWindow pinned in pinnedImages.ToArray()) pinned.Update(null);
    }
    if (DispatcherQueue.HasThreadAccess) Invalidate();
    else DispatcherQueue.TryEnqueue(Invalidate);
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
