using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using System.Runtime.InteropServices;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Windows;
using Windows.Graphics;

namespace VibeOCR.App;

/// <summary>截图现场仅承载共享编辑器；应用、命令与资源仍由主窗口拥有。</summary>
internal sealed class ImageEditWindow : IAsyncDisposable
{
  private readonly WebView2 webView = new();
  private readonly WebWorkbenchHost host;
  private readonly string assetFolder;
  private readonly Window window;
  private readonly ScreenshotCaptureScene? captureScene;
  private bool closed;
  private bool initialized;
  private bool restoreCaptureFocus;
  private Task? disposal;
  private readonly TextBlock recoveryMessage = new()
  {
    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
  };
  private readonly Border recovery = new()
  {
    Visibility = Visibility.Collapsed,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center,
    Padding = new Thickness(16),
    CornerRadius = new CornerRadius(8),
    Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
  };

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
    this.assetFolder = assetFolder;
    this.captureScene = captureScene;
    if (captureScene is not null) webView.Opacity = 0;
    window = captureScene?.Window ?? new Window();
    host = new WebWorkbenchHost(application, broker, annotations,
      applicationOwner: false, fixedRoute: WorkbenchRoute.ImageEdit);
    host.CaptureBackground = captureScene?.TakeBackground();
    host.RecoveryRequired += ShowRecovery;
    host.StateChanged += OnHostStateChanged;
    host.ProtocolViolation += error => AppLog.Error("Image editor bridge rejected a message", error);
    window.Title = "VibeOCR · 图片编辑";
    var recoveryPanel = new StackPanel { Spacing = 12 };
    recoveryPanel.Children.Add(recoveryMessage);
    var exit = new Button { Content = "退出截图" };
    exit.Click += (_, _) => Close();
    recoveryPanel.Children.Add(exit);
    recovery.Child = recoveryPanel;
    var content = captureScene?.Root ?? new Grid();
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

  private async void OnLoaded(object sender, RoutedEventArgs args)
  {
    if (initialized || closed) return;
    initialized = true;
    try
    {
      if (captureScene is { } scene)
      {
        await webView.EnsureCoreWebView2Async();
        webView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
        string geometry = JsonSerializer.Serialize(new
        {
          x = scene.Bounds.X - scene.Desktop.X,
          y = scene.Bounds.Y - scene.Desktop.Y,
          width = scene.Bounds.Width,
          height = scene.Bounds.Height,
          desktopWidth = scene.Desktop.Width,
          desktopHeight = scene.Desktop.Height,
          initialTool = scene.InitialTool,
        });
        await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
          "window.vibeocrCaptureScene=" + geometry + ";" +
          "document.addEventListener('DOMContentLoaded',()=>{" +
          "document.documentElement.dataset.captureScene='true';});");
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
    recoveryMessage.Text = "图片编辑器暂不可用；退出后可重新截图。";
    recovery.Visibility = Visibility.Visible;
  }

  private async void OnHostStateChanged(string state)
  {
    if (closed || state != "bridge-ready") return;
    try
    {
      // 等待冻结桌面和选区都解码，避免 bridge-ready 早于 React 首帧时露出黑底。
      if (captureScene is not null)
      {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!closed)
        {
          string ready = await webView.CoreWebView2.ExecuteScriptAsync(
            "(()=>{const b=document.querySelector('.capture-scene-backdrop img');" +
            "const c=document.querySelector('canvas[aria-label=\"图片检查画布\"]');" +
            "return !!(b?.complete&&b.naturalWidth&&c?.dataset.frameReady==='true'&&getComputedStyle(c).opacity!=='0');})()");
          if (closed) return;
          if (ready == "true") break;
          if (timeout.Elapsed > TimeSpan.FromSeconds(10))
            throw new TimeoutException("截图现场背景未就绪。");
          await Task.Delay(16);
        }
      }
      if (!closed) webView.Opacity = 1;
    }
    catch (Exception error)
    {
      AppLog.Error("Capture scene presentation failed", error);
      ShowRecovery();
    }
  }

  private async void OnClosed(object sender, WindowEventArgs args)
  {
    closed = true;
    webView.Loaded -= OnLoaded;
    window.Closed -= OnClosed;
    Closed?.Invoke(this, args);
    await DisposeAsync();
  }

  public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

  private async Task DisposeCoreAsync()
  {
    closed = true;
    host.RecoveryRequired -= ShowRecovery;
    host.StateChanged -= OnHostStateChanged;
    await host.DisposeAsync();
    webView.Close();
    captureScene?.Dispose();
  }
}
