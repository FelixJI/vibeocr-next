using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool SetWindowPos(
    IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

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
  /// <summary>#238 pin owned 快照与截图历史存储（DataRoot 下固定 owned 目录）。</summary>
  private readonly Features.Pins.PinSnapshotStore? pinSnapshotStore;
  private readonly Features.Pins.PinHistoryStore? pinHistoryStore;
  private readonly VibeOCR.App.Web.WorkbenchProjectStore projectStore;
  private ImageEditWindow? imageEditor;
  private WorkbenchTheme workbenchTheme = WorkbenchTheme.System;
  private readonly Microsoft.UI.System.ThemeSettings themeSettings;
  private readonly ImageEditorRetirement editorRetirement = new();
  private sealed record PendingImageEditor(long Generation, Guid SessionId,
    VibeOCR.Platform.Windows.PhysicalRectangle? Bounds, ScreenshotCaptureScene? Scene)
  {
    internal Action? DetachClosed { get; set; }
  }
  private PendingImageEditor? pendingImageEditor;
  private Task imageEditorCreation = Task.CompletedTask;
  private TaskCompletionSource imageEditorRequestChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private TaskCompletionSource? captureFinished;
  private bool pendingCaptureFocus;
  private bool shuttingDown;
  private TaskCompletionSource? shutdownCompletion;
  private readonly TaskCompletionSource shutdownStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal Task ShutdownCompletion => shutdownCompletion?.Task ?? Task.CompletedTask;
  private Action? captureReplacementShutdownVerification;
  internal Microsoft.UI.Xaml.Controls.WebView2? SceneWebView => imageEditor?.WebView;
  internal Guid? SceneSessionId => imageEditor?.SessionId;
  private Microsoft.UI.Xaml.Controls.WebView2 SmokeEditorWebView => SceneWebView ?? WorkbenchWebView;
  private readonly List<PinnedImageWindow> pinnedImages = [];
  /// <summary>#238 类型化贴图（文字/颜色/文件预览）；与图片贴图共用 4 张上限。</summary>
  private readonly List<Features.Pins.TypedPinWindow> typedPins = [];
  private readonly Dictionary<(Guid SessionId, long Revision),
    (Task<RecognitionTextLayerState?> Task, CancellationTokenSource Cancellation)> pinTextTasks = [];
  private readonly SyntheticScreenRegionPicker? screenshotSmokePicker;
  private readonly Func<string?>? supervisorInstanceId;
  private readonly Func<Guid, long, string, CancellationToken, Task<RecognitionTextLayerState?>> preparePinnedTextLayer;
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
    ShellActionDispatcher? shellActions = null,
    Func<CancellationToken, Task>? ensureRuntimeReady = null,
    ISystemImportReader? systemImportReader = null,
    Func<Guid, long, string, CancellationToken, Task<RecognitionTextLayerState?>>? preparePinnedTextLayer = null)
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
    if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "paddle-modes-e2e")
      paddleSmokePriorResourceFiles.UnionWith(Directory.GetFiles(resourceRoot, "*", SearchOption.AllDirectories));
    resourceBroker = new WorkbenchResourceBroker(resourceRoot);
    annotationStore = new WorkbenchAnnotationStore(resourceRoot);
    pinSnapshotStore = new Features.Pins.PinSnapshotStore(layout.DataRoot);
    pinHistoryStore = new Features.Pins.PinHistoryStore(layout.DataRoot);
    // #233 同一 store 同时供命令（保存消费）与 /__project 上传（暂存）共享。
    projectStore = new VibeOCR.App.Web.WorkbenchProjectStore();
    Func<nint> activeWindowHandle = () => imageEditor is { } editor ? editor.Handle : pendingImageEditor?.Scene is { } scene ?
      WindowNative.GetWindowHandle(scene.Window) : WindowNative.GetWindowHandle(this);
    var projectPicker = new VibeOCR.App.Features.Recognition.ProjectFilePicker(activeWindowHandle);
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
      () => imageEditor is { } editor ? editor.Handle : pendingImageEditor?.Scene is { } scene ?
        WindowNative.GetWindowHandle(scene.Window) : WindowNative.GetWindowHandle(this),
      annotationStore,
      inferenceAttached: inferenceAttached,
      supervisorInstanceId: supervisorInstanceId,
      pinScreenshot: PinScreenshot,
      shellActions: shellActions,
      optionsLayout: layout, confirmPdfClose: ConfirmPdfCloseAsync,
      flushPdfPreview: FlushPdfPreviewAsync,
      ensureRuntimeReady: ensureRuntimeReady,
      systemImportReader: systemImportReader,
      projectStore: projectStore,
      pinSnapshotWriter: pinSnapshotStore,
      pinHistory: pinHistoryStore,
      projectSavePicker: projectPicker.PickSavePathAsync,
      projectOpenPicker: projectPicker.PickOpenPathAsync);
    this.preparePinnedTextLayer = preparePinnedTextLayer ?? commandHandler.PreparePinnedTextLayerAsync;
    commandHandler.ScreenshotSessionReady += ShowImageEditor;
    commandHandler.PinSnapshotApplied += OnPinSnapshotApplied;
    commandHandler.PinEditSessionClosed += OnPinEditSessionClosed;
    commandHandler.PinHistoryReopenRequested += OnPinHistoryReopenRequested;
    commandHandler.PinManagerRequested += ShowPinManagerFromTray;
    commandHandler.ScreenshotCaptureStarting += OnScreenshotCaptureStarting;
    commandHandler.ScreenshotCaptureFinished += OnScreenshotCaptureFinished;
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
          RetireImageEditor(editor, commandHandler.PendingScreenshotCaptureScene);
        }
        if (pendingImageEditor?.SessionId == sessionId)
          CancelPendingImageEditor(commandHandler.PendingScreenshotCaptureScene);
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
          RetireImageEditor(editor, commandHandler.PendingScreenshotCaptureScene);
        }
        if (pendingImageEditor?.SessionId == sessionId) CancelPendingImageEditor(null);
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
      annotationStore,
      projectStore: projectStore);
    webHost.ProtocolViolation += OnProtocolViolation;
    webHost.RecoveryRequired += OnRecoveryRequired;
    webHost.StateChanged += OnHostStateChanged;
    webHost.ThemeObserved += OnWorkbenchThemeObserved;
    themeSettings = Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id);
    themeSettings.Changed += OnSystemHighContrastChanged;

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
    RestorePins();
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
    // Take ownership synchronously: the capture producer disposes any unclaimed scene
    // as soon as ScreenshotSessionReady returns.
    ScreenshotCaptureScene? scene = commandHandler.TakeScreenshotCaptureScene(sessionId);
    CancelPendingImageEditor(scene);
    if (imageEditor is { } previous) RetireImageEditor(previous, scene);
    var pending = new PendingImageEditor(editorRetirement.BeginReplacement(), sessionId, bounds, scene);
    pendingImageEditor = pending;
    if (scene is not null)
    {
      void OnPendingClosed(object sender, WindowEventArgs args) => OnPendingImageEditorClosed(pending);
      scene.Window.Closed += OnPendingClosed;
      pending.DetachClosed = () => scene.Window.Closed -= OnPendingClosed;
    }
    imageEditorCreation = CreateImageEditorAsync(pending);
    TaskCompletionSource changed = imageEditorRequestChanged;
    imageEditorRequestChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    changed.TrySetResult();
    ObserveImageEditorCreation(imageEditorCreation);
  }

  private static async void ObserveImageEditorCreation(Task creation)
  {
    // Keep the original task faulted for smoke/shutdown callers, while observing
    // failures when the production event caller does not await creation.
    try { await creation; }
    catch (Exception error) { AppLog.Error("Image editor creation did not complete", error); }
  }

  private async Task WaitForImageEditorRequestAsync(Guid session)
  {
    while (imageEditor?.SessionId != session && pendingImageEditor?.SessionId != session)
    {
      if (shuttingDown || commandHandler.CurrentImageSessionId != session)
        throw new InvalidOperationException("Capture editor request was superseded.");
      await imageEditorRequestChanged.Task;
    }
  }

  private void CancelPendingImageEditor(ScreenshotCaptureScene? successor)
  {
    editorRetirement.CancelReplacement();
    PendingImageEditor? previous = pendingImageEditor;
    pendingImageEditor = null;
    previous?.DetachClosed?.Invoke();
    if (previous is not null) previous.DetachClosed = null;
    if (previous?.Scene is not { } scene) return;
    if (successor is not null) scene.TransferOwnerRestorationTo(successor);
    scene.Dispose();
  }

  private async void OnPendingImageEditorClosed(PendingImageEditor pending)
  {
    if (!ReferenceEquals(pendingImageEditor, pending)) return;
    CancelPendingImageEditor(null);
    if (!shuttingDown && commandHandler.CurrentImageSessionId == pending.SessionId)
      await application.ExecuteAsync(new WorkbenchCommandEnvelope(Guid.NewGuid(),
        new CloseScreenshotSessionCommand()), CancellationToken.None);
  }

  private void RetireImageEditor(ImageEditWindow editor, ScreenshotCaptureScene? successor)
  {
    editor.TransferOwnerRestorationTo(successor);
    if (ReferenceEquals(imageEditor, editor)) imageEditor = null;
    // Closed also registers user-driven closure in the same drain. Close is first
    // so no asynchronous Dispose can recursively close an unregistered window.
    editor.Close();
    editorRetirement.Track(editor.DisposeAsync().AsTask());
  }

  private async Task CreateImageEditorAsync(PendingImageEditor pending)
  {
    bool claimed = false;
    try
    {
      await editorRetirement.Drain;
      while (captureFinished is { } capture) await capture.Task;
      if (!editorRetirement.IsCurrent(pending.Generation, pending.SessionId,
          commandHandler.CurrentImageSessionId, shuttingDown) ||
          !ReferenceEquals(pendingImageEditor, pending)) return;
      pending.DetachClosed?.Invoke();
      pending.DetachClosed = null;
      var editor = new ImageEditWindow(pending.SessionId, application, resourceBroker, annotationStore,
        layout.WebAssetsRoot, pending.Bounds, pending.Scene,
        projectStore: projectStore);
      claimed = true;
      pendingImageEditor = null;
      imageEditor = editor;
      editor.ApplyWorkbenchTheme(workbenchTheme);
      editor.Closed += OnImageEditorClosed;
      editor.Activate();
    }
    catch (Exception error)
    {
      AppLog.Error("Image editor replacement failed", error);
      if (editorRetirement.IsCurrent(pending.Generation, pending.SessionId,
          commandHandler.CurrentImageSessionId, shuttingDown))
        await application.ExecuteAsync(new WorkbenchCommandEnvelope(Guid.NewGuid(),
          new CloseScreenshotSessionCommand()), CancellationToken.None);
      throw;
    }
    finally
    {
      if (ReferenceEquals(pendingImageEditor, pending)) pendingImageEditor = null;
      if (!claimed)
      {
        pending.DetachClosed?.Invoke();
        pending.DetachClosed = null;
        pending.Scene?.Dispose();
      }
    }
  }

  private async void OnImageEditorClosed(object? sender, WindowEventArgs args)
  {
    if (sender is not ImageEditWindow editor) return;
    editorRetirement.Track(editor.DisposeAsync().AsTask());
    if (!ReferenceEquals(imageEditor, editor)) return;
    imageEditor = null;
    if (!shuttingDown && commandHandler.CurrentImageSessionId == editor.SessionId)
      await application.ExecuteAsync(new WorkbenchCommandEnvelope(Guid.NewGuid(),
        new CloseScreenshotSessionCommand()), CancellationToken.None);
  }

  private void OnScreenshotCaptureStarting()
  {
    captureFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    imageEditor?.HideForCapture();
    if (pendingImageEditor?.Scene is not { } scene) return;
    pendingCaptureFocus = GetForegroundWindow() == WindowNative.GetWindowHandle(scene.Window);
    scene.Window.AppWindow.Hide();
  }

  private void OnScreenshotCaptureFinished()
  {
    imageEditor?.RestoreAfterCapture();
    if (pendingImageEditor?.Scene is { } scene)
    {
      scene.Window.AppWindow.Show(false);
      if (pendingCaptureFocus) SetForegroundWindow(WindowNative.GetWindowHandle(scene.Window));
    }
    pendingCaptureFocus = false;
    TaskCompletionSource? finished = captureFinished;
    captureFinished = null;
    finished?.TrySetResult();
  }

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetForegroundWindow(nint window);
  internal void NavigateTo(string? destination)
  {
    WorkbenchRoute route = destination switch
    {
      "recognition" => WorkbenchRoute.Recognition,
      "imageEdit" or "pinEdit" => WorkbenchRoute.ImageEdit,
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

  /// <summary>
  /// #236 直接目标截图入口：与纯截图同一命令、会话状态机与现场编辑宿主，
  /// 不在 Shell 层绕过；失败经既有 recognition.failed 终态呈现。
  /// </summary>
  internal async Task CaptureScreenshotAreaAsync(ScreenshotCaptureRequest request)
  {
    await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        Guid.NewGuid(),
        new CaptureScreenshotSessionCommand(request)),
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
    // #232：文本拖放仅对图片编辑承载面有意义；其余路由维持既有文件语义。
    if (args.DataView.Contains(StandardDataFormats.StorageItems) ||
      ((args.DataView.Contains(StandardDataFormats.Text) ||
        args.DataView.Contains(StandardDataFormats.Bitmap)) &&
        application.CurrentRoute is WorkbenchRoute.ImageEdit or WorkbenchRoute.Recognition))
    {
      args.AcceptedOperation = DataPackageOperation.Copy;
    }
  }

  private async void OnDrop(object sender, DragEventArgs args)
  {
    var deferral = args.GetDeferral();
    try
    {
      // 活动编辑承载面：DataPackage 整体交给宿主导入命令（StorageItems/Text
      // 优先级与授权由 C# 读取）；其余路由维持既有文件路径路由。
      WorkbenchCommand command;
      if (application.CurrentRoute is WorkbenchRoute.ImageEdit or WorkbenchRoute.Recognition)
      {
        await webHost.ImportDroppedContentAsync(args.DataView, application.CurrentRoute);
        return;
      }
      else
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
        command = application.CurrentRoute switch
        {
          WorkbenchRoute.Batch => new AddDroppedBatchFilesCommand(paths),
          WorkbenchRoute.QrCode => new DecodeDroppedQrCodeCommand(paths[0]),
          WorkbenchRoute.Pdf => new OpenDroppedPdfCommand(paths[0]),
          _ => new RecognizeDroppedFileCommand(paths[0]),
        };
      }
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
    finally
    {
      deferral.Complete();
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

  /// <summary>既有 settings 主题真值驱动原生根主题：高对比优先回落 Default
  /// （系统可见资源），System/Light/Dark 由 WorkbenchThemePresenter 唯一解析；
  /// 系统高对比切换经 ThemeSettings 封送 UI 线程重解析。</summary>
  private void OnWorkbenchThemeObserved(WorkbenchTheme theme)
  {
    workbenchTheme = theme;
    DispatcherQueue.TryEnqueue(ApplyResolvedTheme);
  }

  private void ApplyResolvedTheme()
  {
    // 入队后窗口可能已进入 shutdown：复用既有 shuttingDown 状态，
    // 避免与 teardown（#216 生命周期收口）竞争触碰根元素。
    if (shuttingDown) return;
    RootLayout.RequestedTheme = WorkbenchThemePresenter.ResolveRequestedTheme(
      workbenchTheme, themeSettings.HighContrast);
    // RequestedTheme 不包含标准标题栏。
    AppWindow.TitleBar.PreferredTheme = ImageEditWindow.ResolveTitleBarTheme(
      workbenchTheme, themeSettings.HighContrast);
    // 恢复面已停止 WebView 订阅，仍由主窗的主题真值驱动。
    imageEditor?.ApplyWorkbenchTheme(workbenchTheme);
  }

  private void OnSystemHighContrastChanged(Microsoft.UI.System.ThemeSettings sender, object args) =>
    DispatcherQueue.TryEnqueue(ApplyResolvedTheme);

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
          Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "capture-first-frame-e2e")
      {
        screenshotSmokeStarted = true;
        _ = CompleteCaptureFirstFrameSmokeAsync();
      }
      if (!screenshotSmokeStarted &&
          Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "capture-replacement-e2e")
      {
        screenshotSmokeStarted = true;
        _ = CompleteCaptureReplacementSmokeAsync();
      }
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
      // XAML 图标解析需真实 WinUI UI 线程；普通单元测试宿主未初始化 Application。
      smokeStage = "capture-icons-verification";
      WebReadySmokeStatus.Stage(smokeStage);
      foreach (string key in ScreenshotToolbarIcons.Keys)
      {
        smokeStage = $"capture-icon-{key}";
        ContentControl icon = ScreenshotToolbarIcons.CreateIcon(key);
        ScreenshotToolbarIcons.EffectLayers.TryGetValue(key, out var layers);
        if (icon.Content is not Canvas iconCanvas ||
            iconCanvas.Children.Count != (layers?.Length ?? 1))
          throw new InvalidOperationException($"Toolbar icon failed to load: {key}");
        var foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        smokeStage = $"capture-icon-color-{key}";
        icon.Foreground = foreground;
        for (int index = 0; index < iconCanvas.Children.Count; index++)
        {
          if (iconCanvas.Children[index] is not Microsoft.UI.Xaml.Shapes.Path iconPath ||
              iconPath.Data is null ||
              (iconPath.Data.Bounds.Width <= 0 && iconPath.Data.Bounds.Height <= 0))
            throw new InvalidOperationException($"Toolbar icon has invalid geometry: {key}");
          bool stroked = layers is null || layers[index].Stroke;
          if (stroked &&
              Math.Abs(iconPath.StrokeThickness - ScreenshotToolbarIcons.StrokeWidth) > 0.001)
            throw new InvalidOperationException($"Toolbar icon has incorrect stroke width: {key}");
          if ((stroked ? iconPath.Stroke : iconPath.Fill) is not
              Microsoft.UI.Xaml.Media.SolidColorBrush brush || brush.Color != Microsoft.UI.Colors.White)
            throw new InvalidOperationException($"Toolbar icon failed to follow foreground: {key}");
        }
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
    bool draft = entry.HasUnsubmittedDraft;
    var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
    {
      XamlRoot = (Content as FrameworkElement)?.XamlRoot,
      Title = draft ? $"{entry.DisplayName} 有未提交草稿" : $"保存 {entry.DisplayName} 的修改？",
      Content = draft ? "草稿尚未写入 PDF。返回继续编辑，或明确放弃草稿后继续关闭；随后仍会确认已提交文档的未保存修改。"
        : "保存提交当前文档；放弃会丢弃未保存修改；取消保留文档。",
      PrimaryButtonText = draft ? "返回继续编辑" : "保存", SecondaryButtonText = draft ? "放弃草稿并继续" : "放弃修改", CloseButtonText = "取消",
      DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
    };
    activePdfCloseDialog = dialog;
    try
    {
      return await dialog.ShowAsync() switch
      {
        Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary => draft ? PdfCloseDecision.Cancel : PdfCloseDecision.Save,
        Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary => PdfCloseDecision.Discard,
        _ => PdfCloseDecision.Cancel,
      };
    }
    finally { if (ReferenceEquals(activePdfCloseDialog, dialog)) activePdfCloseDialog = null; }
  }
  internal event Action? ExitRequested;

  public new void Close()
  {
    if (ExitRequested is { } request) request();
    else base.Close();
  }

  internal void CloseAfterPreparation() => base.Close();

  private void OnExitClicked(object sender, RoutedEventArgs args) =>
    Close();

  private void OnWindowClosed(object sender, WindowEventArgs args)
  {
    Closed -= OnWindowClosed;
    Activated -= OnWindowActivated;
    // Assign the shared completion before invoking any async cleanup; App's
    // later Closed subscriber can always await this exact task.
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    shutdownCompletion = completion;
    _ = CompleteWindowShutdownAsync(completion);
  }

  private async Task CompleteWindowShutdownAsync(TaskCompletionSource completion)
  {
    try { await ShutdownWindowAsync(); completion.TrySetResult(); }
    catch (Exception error) { completion.TrySetException(error); }
  }

  private async Task ShutdownWindowAsync()
  {
    commandHandler.EndHotkeyRecording();
    shuttingDown = true;
    commandHandler.ScreenshotSessionReady -= ShowImageEditor;
    commandHandler.ScreenshotCaptureStarting -= OnScreenshotCaptureStarting;
    commandHandler.ScreenshotCaptureFinished -= OnScreenshotCaptureFinished;
    CancelPendingImageEditor(null);
    TaskCompletionSource? finished = captureFinished;
    captureFinished = null;
    finished?.TrySetResult();
    imageEditorRequestChanged.TrySetResult();
    if (imageEditor is { } editor) RetireImageEditor(editor, null);
    shutdownStarted.TrySetResult();
    try
    {
      await editorRetirement.Drain;
      await imageEditorCreation;
    }
    finally
    {
      ClearPinTextTasks();
      foreach (PinnedImageWindow pinned in pinnedImages.ToArray()) pinned.ForceClose();
      foreach (Features.Pins.TypedPinWindow typed in typedPins.ToArray()) typed.ForceClose();
      pinManager?.ForceClose();
      webHost.ProtocolViolation -= OnProtocolViolation;
      webHost.RecoveryRequired -= OnRecoveryRequired;
      webHost.StateChanged -= OnHostStateChanged;
      webHost.ThemeObserved -= OnWorkbenchThemeObserved;
      themeSettings.Changed -= OnSystemHighContrastChanged;
      await webHost.DisposeAsync();
    }
    captureReplacementShutdownVerification?.Invoke();
    captureReplacementShutdownVerification = null;
  }

  private void PinScreenshot(
    WorkbenchAnnotationFile image,
    Guid sessionId,
    long revision,
    RecognitionTextLayerState? layer,
    IReadOnlyList<WorkbenchExclusionBox> excludeBoxes)
  {
    if (pinnedImages.Count + typedPins.Count >= 4)
    {
      // C6：专属回执文案；宿主仍以此处为最终防竞态防线。
      throw new PinLimitReachedException();
    }
    PinnedImageWindow? pending = null;
    var pinned = new PinnedImageWindow(image, sessionId, revision, layer, excludeBoxes,
      () => PreparePinTextAsync(
        pending!.SessionId, pending!.Revision, pending!.CurrentImagePath,
        pending!.CurrentExcludeBoxes),
      () => supervisorInstanceId?.Invoke());
    pending = pinned;
    pinSnapshotStore?.SaveRecovery(pinned.RecoveryState(), File.ReadAllBytes(image.Path));
    WirePinWindow(pinned);
  }

  /// <summary>贴图窗口统一接线：编辑槽事件、交互事件、关闭清理与注册。</summary>
  private void WirePinWindow(PinnedImageWindow pinned)
  {
    pinned.EditRequested += EnterPinEditAsync;
    pinned.SnapshotConfirmed += async target =>
    {
      await pinSnapshotSwap;
      if (pinEditing?.Window == target) pinEditing = null;
      await target.ExitEditModeAsync();
    };
    pinned.ExitEditRequested += ExitPinEditAsync;
    pinned.MovedDelta += (dx, dy) => MovePinGroup(pinned, dx, dy);
    pinned.CollapseToggleRequested += pin => pin.SetCollapsed(!pin.Collapsed);
    pinned.PassThroughToggleRequested += pin => pin.SetPassThrough(!pin.PassThrough);
    pinned.GroupToggleRequested += TogglePinGroup;
    pinned.PinManagerRequested += ShowPinManager;
    pinned.Closed += closed =>
    {
      pinnedImages.Remove(closed);
      pinGroups.Remove(closed);
      if (!pinnedImages.Any(other => other.SessionId == closed.SessionId &&
          other.Revision == closed.Revision))
        RemovePinTextTask((closed.SessionId, closed.Revision));
      // 关闭单张清 owned 记录：仅非活动编辑槽（在途事务结束后才清理）。
      if (!shuttingDown)
      {
        pinSnapshotStore?.DeleteSnapshot(closed.PinId);
      }
      pinManager?.Refresh();
    };
    pinnedImages.Add(pinned);
    _ = ShowPinnedAsync(pinned);
  }

  /// <summary>编组同步移动：同组成员应用同一物理位移（不含发起者自身）。</summary>
  private void MovePinGroup(PinnedImageWindow source, int dx, int dy)
  {
    if (dx == 0 && dy == 0) return;
    if (!pinGroups.TryGetValue(source, out Guid group)) return;
    foreach ((PinnedImageWindow member, Guid memberGroup) in pinGroups)
    {
      if (member == source || memberGroup != group || member.Disposed) continue;
      nint handle = member.Handle;
      if (!GetWindowRect(handle, out RECT bounds)) continue;
      SetWindowPos(handle, 0,
        bounds.Left + dx, bounds.Top + dy, 0, 0,
        0x0001 | 0x0004 | 0x0010);
    }
  }

  /// <summary>编组开关：首次编组创建新组；同组多成员共享一个组 id。</summary>
  private void TogglePinGroup(PinnedImageWindow pin)
  {
    if (pinGroups.Remove(pin, out _))
    {
      pin.SetGrouped(false);
      return;
    }
    Guid group = pinGroups.Values.FirstOrDefault();
    if (group == default) group = Guid.NewGuid();
    pinGroups[pin] = group;
    pin.SetGrouped(true);
  }

  /// <summary>始终可达的贴图管理入口（穿透恢复）。</summary>
  /// <summary>始终可达的贴图管理入口（穿透恢复；托盘同入口）。</summary>
  internal void ShowPinManagerFromTray()
  {
    if (shuttingDown) return;
    if (DispatcherQueue.HasThreadAccess) ShowPinManager();
    else DispatcherQueue.TryEnqueue(ShowPinManager);
  }

  /// <summary>#238 新建类型化贴图：默认文字（AC“新入口默认文字”）。
  /// 文件预览经原生 picker 授权，仅图片/有界纯文本，未知格式拒绝。</summary>
  internal async void CreateTypedPinFromTray(Features.Pins.TypedPinWindow.ContentKind kind)
  {
    if (shuttingDown || pinsExitReview) return;
    void Create()
    {
      if (pinnedImages.Count + typedPins.Count >= 4)
      {
        _ = ShowPinMessageAsync("最多同时打开四张贴图，请先关闭一张。");
        return;
      }
      var pin = CreateTypedPin(kind);
      if (kind == Features.Pins.TypedPinWindow.ContentKind.FilePreview)
      {
        _ = PickTypedPinFileAsync(pin);
      }
    }
    if (DispatcherQueue.HasThreadAccess) Create();
    else DispatcherQueue.TryEnqueue(Create);
    await Task.CompletedTask;
  }

  private Features.Pins.TypedPinWindow CreateTypedPin(Features.Pins.TypedPinWindow.ContentKind kind, Guid? id = null)
  {
    var pin = new Features.Pins.TypedPinWindow(kind, id, async view =>
    {
      var host = new WebWorkbenchHost(application, resourceBroker, annotationStore,
        applicationOwner: false, fixedRoute: WorkbenchRoute.ImageEdit, projectStore: projectStore, pinEditHost: true);
      await host.InitializeAsync(view, layout.WebAssetsRoot);
      return host;
    });
    pin.Closed += closed =>
    {
      typedPins.Remove(closed);
      if (!shuttingDown) pinSnapshotStore?.DeleteSnapshot(closed.PinId);
      pinManager?.Refresh();
    };
    typedPins.Add(pin);
    pin.Show();
    return pin;
  }

  private async Task PickTypedPinFileAsync(Features.Pins.TypedPinWindow pin)
  {
    try
    {
      var picker = new Windows.Storage.Pickers.FileOpenPicker();
      WinRT.Interop.InitializeWithWindow.Initialize(
        picker, WindowNative.GetWindowHandle(this));
      picker.FileTypeFilter.Add(".png");
      picker.FileTypeFilter.Add(".jpg");
      picker.FileTypeFilter.Add(".jpeg");
      picker.FileTypeFilter.Add(".txt");
      picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
      Windows.Storage.StorageFile? file =
        await picker.PickSingleFileAsync();
      if (file is null) { pin.ForceClose(); return; }
      if (pin.Disposed || shuttingDown) return;
      string extension = Path.GetExtension(file.Path).ToLowerInvariant();
      if (extension is ".png" or ".jpg" or ".jpeg")
      {
        using var input = await file.OpenReadAsync();
        SystemImportImageItem imported = await InputService.ReadImportImageAsync(input, CancellationToken.None);
        if (pin.Disposed || shuttingDown) return;
        byte[] png = imported.Data;
        var lease = await annotationStore.UploadPngAsync(new MemoryStream(png), CancellationToken.None);
        WorkbenchAnnotationFile annotation = annotationStore.Take(lease.ResourceUri);
        typedPins.Remove(pin);
        try { PinScreenshot(annotation, Guid.NewGuid(), 0, null, []); }
        catch { annotation.Dispose(); throw; }
        finally { pin.ForceClose(); }
      }
      else await pin.LoadFileAsync(file.Path);
    }
    catch (Exception error)
    {
      VibeOCR.App.Services.AppLog.Error("Typed pin file pick failed", error);
      pin.ShowError($"文件预览失败：{error.Message}");
    }
  }

  private void ShowPinManager()
  {
    if (pinManager is null)
    {
      pinManager = new Features.Pins.PinManagerWindow(
        () => pinnedImages.ToArray(),
        manager => manager.Refresh(),
        kind => CreateTypedPinFromTray(kind),
        pin => { if (pinGroups.ContainsKey(pin)) TogglePinGroup(pin); },
        () => typedPins.ToArray());
      pinManager.Closed += _ => pinManager = null;
    }
    pinManager.Show();
  }

  private sealed record PinEditSlot(PinnedImageWindow Window, Guid SessionId);
  private PinEditSlot? pinEditing;
  private readonly Dictionary<PinnedImageWindow, Guid> pinGroups = [];
  private Features.Pins.PinManagerWindow? pinManager;

  /// <summary>进入贴图编辑（单槽）：另一张在编辑时激活它，不抢占（C3）。</summary>
  private bool pinTransitionInFlight;
  private bool pinsExitReview;
  private TaskCompletionSource? pinTransitionComplete;
  private async Task EnterPinEditAsync(PinnedImageWindow target)
  {
    if (shuttingDown || pinsExitReview || pinTransitionInFlight || target.Disposed) return;
    pinTransitionInFlight = true;
    pinTransitionComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
      await EnterPinEditCoreAsync(target);
    }
    finally { pinTransitionInFlight = false; pinTransitionComplete.TrySetResult(); }
  }

  private async Task EnterPinEditCoreAsync(PinnedImageWindow target)
  {
    if (pinEditing is { } existing)
    {
      if (existing.Window == target) { target.Activate(); return; }
      if (!await existing.Window.SaveEditAsync()) { existing.Window.Activate(); return; }
    }
    if (pinSnapshotStore is null)
    {
      return;
    }
    byte[] png;
    try { png = await File.ReadAllBytesAsync(target.CurrentImagePath); }
    catch (Exception error)
    {
      VibeOCR.App.Services.AppLog.Error("Pin edit source read failed", error);
      return;
    }
    Guid? sessionId = await commandHandler.TryOpenPinEditSessionAsync(
      target.PinId, png, CancellationToken.None, target.CurrentExcludeBoxes);
    if (sessionId is not { } id) return;
    if (shuttingDown || target.Disposed)
    {
      await application.ExecuteAsync(new(Guid.NewGuid(), new ClosePinEditSessionCommand()), CancellationToken.None);
      return;
    }
    pinEditing = new PinEditSlot(target, id);
    await target.EnterEditModeAsync(
      application, resourceBroker, annotationStore, projectStore, layout.WebAssetsRoot, id);
    if (!target.Editing) await ExitPinEditAsync(target);
  }

  /// <summary>放弃编辑：关闭 pinEdit 会话；会话事件回调完成退出与槽释放。</summary>
  private async Task ExitPinEditAsync(PinnedImageWindow target)
  {
    if (pinEditing is not { } slot || slot.Window != target)
    {
      return;
    }
    _ = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(Guid.NewGuid(), new ClosePinEditSessionCommand()),
      CancellationToken.None);
  }

  private Task pinSnapshotSwap = Task.CompletedTask;

  private void OnPinSnapshotApplied(Guid sessionId, long revision, Guid pinId)
  {
    void Apply()
    {
      if (pinEditing is { } slot && slot.SessionId == sessionId)
      {
        PinnedImageWindow target = slot.Window;
        string? pngPath = pinSnapshotStore?.GetSnapshotPngPath(pinId);
        if (pngPath is { } path)
        {
          try
          {
            (int width, int height) = Features.Pins.PinSnapshotStore.SniffPngSize(File.ReadAllBytes(path));
            pinSnapshotSwap = target.ApplySnapshotAsync(path, width, height, sessionId, pinSnapshotStore!.GetSnapshotExclusions(pinId) ?? []);
          }
          catch (Exception error)
          {
            VibeOCR.App.Services.AppLog.Error("Pin snapshot swap failed", error);
            _ = target.ExitEditModeAsync();
          }
        }
        else
        {
          _ = target.ExitEditModeAsync();
        }
      }
    }
    if (DispatcherQueue.HasThreadAccess) Apply();
    else DispatcherQueue.TryEnqueue(Apply);
  }

  private void OnPinEditSessionClosed(Guid sessionId)
  {
    void Restore()
    {
      if (pinEditing is { } slot && slot.SessionId == sessionId)
      {
        pinEditing = null;
        _ = slot.Window.ExitEditModeAsync();
      }
    }
    if (DispatcherQueue.HasThreadAccess) Restore();
    else DispatcherQueue.TryEnqueue(Restore);
  }

  private Task OnPinHistoryReopenRequested(PinHistoryImage image)
  {
    void Reopen()
    {
      if (shuttingDown) throw new InvalidOperationException("Application is closing.");
      if (pinnedImages.Count + typedPins.Count >= 4) throw new PinLimitReachedException();
      string temp = Path.Combine(resourceRoot, $"pin-{Guid.NewGuid():N}.png");
      File.WriteAllBytes(temp, image.Png);
      var annotation = new WorkbenchAnnotationFile(temp, image.Width, image.Height);
      try { PinScreenshot(annotation, Guid.NewGuid(), 0, null, []); }
      catch { annotation.Dispose(); throw; }
    }
    if (DispatcherQueue.HasThreadAccess) { Reopen(); return Task.CompletedTask; }
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    if (!DispatcherQueue.TryEnqueue(() =>
    {
      try { Reopen(); completion.TrySetResult(); }
      catch (Exception error) { completion.TrySetException(error); }
    })) completion.TrySetException(new InvalidOperationException("UI dispatcher is unavailable."));
    return completion.Task;
  }

  internal async Task<bool> PreparePinsExitAsync()
  {
    if (pinsExitReview) return false;
    pinsExitReview = true;
    bool prepared = false;
    try
    {
      if (pinTransitionComplete is { } transition) await transition.Task;
      if (pinEditing is { } slot && !await slot.Window.PrepareEditExitAsync()) return false;
      if (pinSnapshotStore is not null)
      {
        foreach (PinnedImageWindow pin in pinnedImages)
          pinSnapshotStore.SaveRecovery(pin.RecoveryState(), await File.ReadAllBytesAsync(pin.CurrentImagePath));
        foreach (Features.Pins.TypedPinWindow pin in typedPins)
          pinSnapshotStore.SaveRecovery(pin.RecoveryState());
      }
      prepared = true;
      return true;
    }
    catch (Exception error)
    {
      AppLog.Error("Pin exit recovery persistence failed", error);
      await ShowPinMessageAsync("贴图恢复内容保存失败，请重试后退出。");
      return false;
    }
    finally { if (!prepared) pinsExitReview = false; }
  }

  private async Task ShowPinMessageAsync(string message)
  {
    Activate();
    await new ContentDialog { Title = "贴图", Content = message, CloseButtonText = "确定",
      XamlRoot = WorkbenchWebView.XamlRoot }.ShowAsync();
  }

  private void RestorePins()
  {
    if (pinSnapshotStore is null) return;
    foreach (var record in pinSnapshotStore.LoadRecovery())
    {
      try
      {
        if (record.Kind == "Image")
        {
          string? path = pinSnapshotStore.GetSnapshotPngPath(record.Id);
          if (path is null) continue;
          PinHistoryImage? decoded = pinSnapshotStore.ReadSnapshotPng(record.Id);
          if (decoded is null) continue;
          byte[] png = decoded.Png;
          int width = decoded.Width, height = decoded.Height;
          IReadOnlyList<WorkbenchExclusionBox> exclusions = pinSnapshotStore.GetSnapshotExclusions(record.Id) ?? record.ExcludeBoxes ?? [];
          string temp = Path.Combine(resourceRoot, $"pin-{Guid.NewGuid():N}.png");
          File.WriteAllBytes(temp, png);
          PinnedImageWindow? pin = null;
          pin = new PinnedImageWindow(new WorkbenchAnnotationFile(temp, width, height),
            Guid.NewGuid(), 0, null, exclusions, () => PreparePinTextAsync(pin!.SessionId,
              pin.Revision, pin.CurrentImagePath, pin.CurrentExcludeBoxes), () => supervisorInstanceId?.Invoke(), record.Id);
          WirePinWindow(pin);
          pin.RestoreGeometry(record);
          if (record.Grouped) TogglePinGroup(pin);
        }
        else if (Enum.TryParse(record.Kind, out Features.Pins.TypedPinWindow.ContentKind kind))
        {
          var typed = CreateTypedPin(kind, record.Id);
          typed.Restore(record);
        }
      }
      catch (Exception error) { AppLog.Warn($"Skipped damaged pin: {record.Id}: {error.Message}"); }
    }
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
      return await preparePinnedTextLayer(
        sessionId, revision, imagePath, cancellationToken);
    if (PinnedTextMask.CoversEntireImage(excludeBoxes))
      throw new PinnedTextPreparationException(
        "整张图都在屏蔽区内：没有可取文字；贴图本身保持不变。");
    string masked = await PinnedTextMask.CreateMaskedPngAsync(
      imagePath, excludeBoxes, cancellationToken);
    try
    {
      return await preparePinnedTextLayer(
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
