using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Windows;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace VibeOCR.App;

/// <summary>截图现场仅承载共享编辑器；应用、命令与资源仍由主窗口拥有。</summary>
internal sealed class ImageEditWindow : IAsyncDisposable
{
  private readonly WebView2 webView = new();
  private readonly IWorkbenchApplication application;
  private static int activeWebViewCount;
  private readonly TaskCompletionSource<long> presented = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal long CreatedTimestamp { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
  internal Task<long> Presented => presented.Task;
  internal static int ActiveWebViewCount => Volatile.Read(ref activeWebViewCount);
  internal WebWorkbenchHost Host => host;
  internal ScreenshotCaptureScene? CaptureScene => captureScene;
  private readonly WebWorkbenchHost host;
  private readonly string assetFolder;
  private readonly Window window;
  private readonly ScreenshotCaptureScene? captureScene;
  private bool closed;
  private bool initialized;
  private bool restoreCaptureFocus;
  private Task? disposal;
  private Task? controlledDisposalGate;
  private TaskCompletionSource? controlledDisposalReached;
  internal Task HoldDisposalForSmoke(Task release)
  {
    if (disposal is not null) throw new InvalidOperationException("Editor disposal already started.");
    controlledDisposalGate = release;
    controlledDisposalReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    return controlledDisposalReached.Task;
  }
  private readonly TextBlock recoveryMessage = new();
  private readonly Button exitButton = new() { Content = "退出截图" };
  private readonly Border recovery = new()
  {
    Visibility = Visibility.Collapsed,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center,
    Padding = new Thickness(16),
    CornerRadius = new CornerRadius(8),
  };
  // 根容器随工作台主题设置同步 RequestedTheme（高对比优先回落 Default；
  // scene 复用截图窗 Root 时仅影响主题资源，不碰像素内容与生命周期）。
  private readonly FrameworkElement contentRoot;
  private readonly ThemeSettings themeSettings;
  private WorkbenchTheme workbenchTheme = WorkbenchTheme.System;

  public ImageEditWindow(
    Guid sessionId,
    IWorkbenchApplication application,
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotations,
    string assetFolder,
    PhysicalRectangle? captureBounds,
    ScreenshotCaptureScene? captureScene = null)
  {
    SessionId = sessionId;
    this.application = application;
    this.assetFolder = assetFolder;
    this.captureScene = captureScene;
    if (captureScene is not null) webView.Opacity = 0;
    window = captureScene?.Window ?? new Window();
    host = new WebWorkbenchHost(application, broker, annotations,
      applicationOwner: false, fixedRoute: WorkbenchRoute.ImageEdit);
    host.CaptureBackground = captureScene?.TakeBackground();
    if (captureScene is { } geometry)
      host.CaptureScene = new(Guid.NewGuid(), sessionId, geometry.Bounds.X - geometry.Desktop.X,
        geometry.Bounds.Y - geometry.Desktop.Y, geometry.Bounds.Width, geometry.Bounds.Height,
        geometry.Desktop.Width, geometry.Desktop.Height, geometry.InitialTool ?? "select");
    host.RecoveryRequired += ShowRecovery;
    host.StateChanged += OnHostStateChanged;
    host.ThemeObserved += ApplyWorkbenchTheme;
    host.ProtocolViolation += error => AppLog.Error("Image editor bridge rejected a message", error);
    window.Title = "VibeOCR · 图片编辑";
    var recoveryPanel = new StackPanel { Spacing = 12 };
    recoveryPanel.Children.Add(recoveryMessage);
    exitButton.Click += (_, _) => Close();
    recoveryPanel.Children.Add(exitButton);
    recovery.Child = recoveryPanel;
    var content = captureScene?.Root ?? new Grid();
    contentRoot = content;
    // #232：两个原生窗口（含截图现场）接同一宿主导入命令：DataPackage 由
    // C# 读取，异步处理用原生事件 deferral，完成前不释放拖放源。
    content.AllowDrop = true;
    content.DragOver += OnDragOver;
    content.Drop += OnDrop;
    content.Children.Add(webView);
    content.Children.Add(recovery);
    content.AddHandler(UIElement.PreviewKeyDownEvent,
      new Microsoft.UI.Xaml.Input.KeyEventHandler((_, args) =>
      {
        if (recovery.Visibility != Visibility.Visible || args.Key != Windows.System.VirtualKey.Escape) return;
        args.Handled = true;
        Close();
      }), true);
    webView.Loaded += OnLoaded;
    if (captureScene is null) window.Content = content;
    window.Closed += OnClosed;
    themeSettings = ThemeSettings.CreateForWindowId(window.AppWindow.Id);
    themeSettings.Changed += OnSystemHighContrastChanged;
    // 恢复面颜色经 App.xaml 的 ThemeResource style 动态绑定，随根元素
    // 主题/高对比切换重解析，替代旧固定黑底白字。
    if (Application.Current?.Resources is { } resources)
    {
      if (resources.TryGetValue("RecoveryPanelBorderStyle", out object? borderStyle) && borderStyle is Style border)
        recovery.Style = border;
      if (resources.TryGetValue("RecoveryPanelTextStyle", out object? textStyle) && textStyle is Style text)
        recoveryMessage.Style = text;
    }
    Interlocked.Increment(ref activeWebViewCount);
    if (captureScene is not null) return;
    window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
    var area = captureBounds is { } bounds
      ? DisplayArea.GetFromPoint(new PointInt32(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
          DisplayAreaFallback.Nearest)
      : DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest);
    RectInt32 work = area.WorkArea;
    double scale = WindowGeometryPolicy.GetWindowScale(Handle);
    int width = Math.Min(work.Width, WindowGeometryPolicy.ScaleToPhysical(1000, scale));
    int height = Math.Min(work.Height, WindowGeometryPolicy.ScaleToPhysical(720, scale));
    int x = Math.Clamp(captureBounds?.X ?? work.X + (work.Width - width) / 2, work.X, work.X + work.Width - width);
    int y = Math.Clamp(captureBounds?.Y ?? work.Y + (work.Height - height) / 2, work.Y, work.Y + work.Height - height);
    window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
  }

  public Guid SessionId { get; }
  internal WebView2 WebView => webView;
  internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(window);
  internal bool ReusesCaptureOverlay => captureScene is not null;
  public event EventHandler<WindowEventArgs>? Closed;
  public void Activate() => window.Activate();
  public void Close() => window.Close();
  internal void TransferOwnerRestorationTo(ScreenshotCaptureScene? successor)
  {
    if (captureScene is not null && successor is not null)
      captureScene.TransferOwnerRestorationTo(successor);
  }
  internal void HideForCapture()
  {
    restoreCaptureFocus = GetForegroundWindow() == Handle;
    window.AppWindow.Hide();
  }
  internal void RestoreAfterCapture()
  {
    if (closed) return;
    window.AppWindow.Show(false);
    if (restoreCaptureFocus) SetForegroundWindow(Handle);
    restoreCaptureFocus = false;
  }

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();
  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetForegroundWindow(nint window);

  private void OnDragOver(object sender, DragEventArgs args)
  {
    if (args.DataView.Contains(StandardDataFormats.Bitmap) ||
      args.DataView.Contains(StandardDataFormats.StorageItems) ||
      args.DataView.Contains(StandardDataFormats.Text))
    {
      args.AcceptedOperation = DataPackageOperation.Copy;
    }
  }

  private async void OnDrop(object sender, DragEventArgs args)
  {
    var deferral = args.GetDeferral();
    try
    {
      if (closed) return;
      await host.ImportDroppedContentAsync(args.DataView);
    }
    catch (Exception error) when (
      error is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
      AppLog.Error("Image editor drop failed", error);
    }
    finally
    {
      deferral.Complete();
    }
  }

  private async void OnLoaded(object sender, RoutedEventArgs args)
  {
    if (initialized || closed) return;
    initialized = true;
    try
    {
      if (captureScene is not null)
      {
        await webView.EnsureCoreWebView2Async();
        if (closed) return;
        webView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
      }
      await host.InitializeAsync(webView, assetFolder);
    }
    catch (Exception error)
    {
      AppLog.Error("Image editor initialization failed", error);
      ShowRecovery();
    }
  }

  private void ShowRecovery()
  {
    if (closed) return;
    presented.TrySetCanceled();
    recoveryMessage.Text = "图片编辑器暂不可用；退出后可重新截图。";
    // 失效 WebView 移出 Tab 链，避免焦点停在不可见错误页。
    webView.Visibility = Visibility.Collapsed;
    recovery.Visibility = Visibility.Visible;
    // 原生退出按钮获得键盘焦点：恢复面首次 Tab 即可达且可见。
    exitButton.Focus(FocusState.Keyboard);
  }

  /// <summary>既有 settings 真值驱动的根主题；高对比激活时一律 Default，
  /// 与浮栏同一优先级（WorkbenchThemePresenter 唯一解析）。</summary>
  internal void ApplyWorkbenchTheme(WorkbenchTheme theme)
  {
    workbenchTheme = theme;
    window.DispatcherQueue.TryEnqueue(ApplyResolvedTheme);
  }

  private void ApplyResolvedTheme()
  {
    // 入队后窗口可能已关闭/进入 teardown：不再触碰根元素主题。
    if (closed) return;
    contentRoot.RequestedTheme = WorkbenchThemePresenter.ResolveRequestedTheme(
      workbenchTheme, themeSettings.HighContrast);
    // RequestedTheme 不包含标准标题栏；无边框截图 scene 不需要设置。
    if (captureScene is null)
      window.AppWindow.TitleBar.PreferredTheme = ResolveTitleBarTheme(
        workbenchTheme, themeSettings.HighContrast);
  }

  /// <summary>标准标题栏复用单一主题偏好，高对比优先使用 Windows 应用主题。</summary>
  internal static TitleBarTheme ResolveTitleBarTheme(WorkbenchTheme theme, bool highContrast) =>
    highContrast || theme == WorkbenchTheme.System
      ? TitleBarTheme.UseDefaultAppMode
      : theme == WorkbenchTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark;

  private void OnSystemHighContrastChanged(ThemeSettings sender, object args) =>
    window.DispatcherQueue.TryEnqueue(ApplyResolvedTheme);

  private void OnHostStateChanged(string state)
  {
    if (closed) return;
    if (captureScene is not null && state == "navigation-starting")
    {
      webView.Opacity = 0;
      return;
    }
    if (state != (captureScene is null ? "bridge-ready" : "capture-pixel-ready")) return;
    webView.Opacity = 1;
    presented.TrySetResult(System.Diagnostics.Stopwatch.GetTimestamp());
  }

  private async void OnClosed(object sender, WindowEventArgs args)
  {
    closed = true;
    webView.Loaded -= OnLoaded;
    window.Closed -= OnClosed;
    Closed?.Invoke(this, args);
    try { await DisposeAsync(); }
    catch (Exception error) { AppLog.Error("Image editor disposal failed", error); }
  }

  public ValueTask DisposeAsync()
  {
    if (disposal is not null) return new(disposal);
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    disposal = completion.Task;
    _ = CompleteDisposalAsync(completion);
    return new(disposal);
  }

  private async Task CompleteDisposalAsync(TaskCompletionSource completion)
  {
    try { await DisposeCoreAsync(); completion.TrySetResult(); }
    catch (Exception error) { completion.TrySetException(error); }
  }

  private async Task DisposeCoreAsync()
  {
    closed = true;
    host.RecoveryRequired -= ShowRecovery;
    host.StateChanged -= OnHostStateChanged;
    host.ThemeObserved -= ApplyWorkbenchTheme;
    themeSettings.Changed -= OnSystemHighContrastChanged;
    presented.TrySetCanceled();
    await host.DisposeAsync();
    if (controlledDisposalGate is { } gate)
    {
      controlledDisposalReached?.TrySetResult();
      await gate;
      controlledDisposalGate = null;
      controlledDisposalReached = null;
    }
    webView.Close();
    Interlocked.Decrement(ref activeWebViewCount);
    captureScene?.Dispose();
  }
}
