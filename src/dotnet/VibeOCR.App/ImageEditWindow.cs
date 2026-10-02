using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Windows;
using Windows.Graphics;

namespace VibeOCR.App;

/// <summary>截图现场仅承载共享编辑器；应用、命令与资源仍由主窗口拥有。</summary>
internal sealed class ImageEditWindow : Window, IAsyncDisposable
{
  private readonly WebView2 webView = new();
  private readonly WebWorkbenchHost host;
  private readonly string assetFolder;
  private bool closed;
  private bool initialized;
  private Task? disposal;
  private readonly TextBlock recovery = new() { Text = "图片编辑器正在加载…", Visibility = Visibility.Collapsed };

  public ImageEditWindow(
    Guid sessionId,
    IWorkbenchApplication application,
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotations,
    string assetFolder,
    PhysicalRectangle? captureBounds)
  {
    SessionId = sessionId;
    this.assetFolder = assetFolder;
    host = new WebWorkbenchHost(application, broker, annotations,
      applicationOwner: false, fixedRoute: WorkbenchRoute.ImageEdit);
    host.RecoveryRequired += ShowRecovery;
    host.ProtocolViolation += error => AppLog.Error("Image editor bridge rejected a message", error);
    Title = "VibeOCR · 图片编辑";
    var content = new Grid();
    content.Children.Add(webView);
    content.Children.Add(recovery);
    webView.Loaded += OnLoaded;
    Content = content;
    Closed += OnClosed;
    AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
    var area = captureBounds is { } bounds
      ? DisplayArea.GetFromPoint(new PointInt32(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
          DisplayAreaFallback.Nearest)
      : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
    RectInt32 work = area.WorkArea;
    double scale = WindowGeometryPolicy.GetWindowScale(WinRT.Interop.WindowNative.GetWindowHandle(this));
    int width = Math.Min(work.Width, WindowGeometryPolicy.ScaleToPhysical(1000, scale));
    int height = Math.Min(work.Height, WindowGeometryPolicy.ScaleToPhysical(720, scale));
    int x = Math.Clamp(captureBounds?.X ?? work.X + (work.Width - width) / 2, work.X, work.X + work.Width - width);
    int y = Math.Clamp(captureBounds?.Y ?? work.Y + (work.Height - height) / 2, work.Y, work.Y + work.Height - height);
    AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
  }

  public Guid SessionId { get; }
  internal WebView2 WebView => webView;

  private async void OnLoaded(object sender, RoutedEventArgs args)
  {
    if (initialized || closed) return;
    initialized = true;
    try { await host.InitializeAsync(webView, assetFolder); }
    catch (Exception error)
    {
      AppLog.Error("Image editor initialization failed", error);
      ShowRecovery();
    }
  }

  private void ShowRecovery()
  {
    if (closed) return;
    recovery.Text = "图片编辑器暂不可用；关闭窗口后重新截图。";
    recovery.Visibility = Visibility.Visible;
  }

  private async void OnClosed(object sender, WindowEventArgs args)
  {
    closed = true;
    webView.Loaded -= OnLoaded;
    Closed -= OnClosed;
    await DisposeAsync();
  }

  public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

  private async Task DisposeCoreAsync()
  {
    closed = true;
    host.RecoveryRequired -= ShowRecovery;
    await host.DisposeAsync();
    webView.Close();
  }
}