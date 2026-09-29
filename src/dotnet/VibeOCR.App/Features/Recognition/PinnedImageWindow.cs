using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;

namespace VibeOCR.App.Features.Recognition;

/// <summary>A standalone topmost image snapshot. It owns only its PNG lease and WebView.</summary>
internal sealed class PinnedImageWindow : IDisposable
{
  private const string ImageUrl = "https://pin.vibeocr/image.png";
  private const string FitLinesScript =
    "requestAnimationFrame(()=>{for(const line of document.querySelectorAll('.line')){" +
    "const glyphs=line.querySelector('span');" +
    "const width=glyphs?.scrollWidth||0;" +
    "if(line.clientWidth>0&&width>line.clientWidth)glyphs.style.transform=`scaleX(${line.clientWidth/width})`;" +
    "}" +
    "});";
  private const int GwlExStyle = -20;
  private const long WsExLayered = 0x00080000;
  private const uint LwaAlpha = 0x00000002;
  private readonly Window window = new();
  private readonly WebView2 view = new();
  private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center };
  private readonly Button copySelectionButton;
  private readonly Button prepareTextButton;
  private readonly Button zoomInButton;
  private readonly Slider opacitySlider;
  private readonly WorkbenchAnnotationFile image;
  private readonly IAnnotatedImagePlatform platform;
  private readonly Func<Task<RecognitionTextLayerState?>> prepareText;
  private readonly Func<string?> currentServiceInstance;
  private readonly List<IRandomAccessStream> responseStreams = [];
  private readonly Guid sessionId;
  private readonly long revision;
  private readonly int imageWidth;
  private readonly int imageHeight;
  private RecognitionTextLayerState? layer;
  private bool loaded;
  private bool disposed;
  private CoreWebView2? core;
  private ulong activeNavigation;
  private string navigationState = "not_started";
  private string? documentUri;
  private double zoom = 1;
  private long textGeneration;
  private bool oldSnapshot;

  public event Action<PinnedImageWindow>? Closed;

  internal string SmokeImagePath => image.Path;
  internal string SmokeNavigationState => $"loaded={loaded}; lines={layer?.Lines?.Count}; {navigationState}";
  internal bool SmokeDisposed => disposed;
  internal bool SmokeAlwaysOnTop =>
    window.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };
  internal double SmokeZoom => zoom;
  internal byte SmokeNativeAlpha
  {
    get
    {
      if (!GetLayeredWindowAttributes(
        WinRT.Interop.WindowNative.GetWindowHandle(window), out _, out byte alpha, out _))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      return alpha;
    }
  }
  internal PointInt32 SmokePosition => window.AppWindow.Position;
  internal Guid SessionId => sessionId;
  internal long Revision => revision;
  internal async Task<string> SmokeEvaluateAsync(string script) =>
    await view.ExecuteScriptAsync(script);
  internal async Task<string> SmokeDispatchMouseAsync(string type, double x, double y, int buttons) =>
    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
      JsonSerializer.Serialize(new { type, x, y, button = "left", buttons, clickCount = 1 }));
  internal void SmokeInvokeCopySelection() =>
    new ButtonAutomationPeer(copySelectionButton).Invoke();
  internal void SmokeInvokePrepareText() =>
    new ButtonAutomationPeer(prepareTextButton).Invoke();
  internal void SmokeInvokeZoomIn() => new ButtonAutomationPeer(zoomInButton).Invoke();
  internal void SmokeSetOpacity(double value)
  {
    var peer = new SliderAutomationPeer(opacitySlider);
    ((IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)).SetValue(value);
  }
  internal async Task SmokeDragTitleBarAsync(int dx, int dy)
  {
    window.Activate();
    PointInt32 before = window.AppWindow.Position;
    int x = before.X + window.AppWindow.Size.Width / 2;
    int y = before.Y + 14;
    if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastWin32Error());
    MouseEvent(0x0002, 0, 0, 0, 0);
    try
    {
      if (!SetCursorPos(x + dx, y + dy))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      await Task.Delay(100);
    }
    finally { MouseEvent(0x0004, 0, 0, 0, 0); }
  }
  internal async Task SmokeCapturePreviewAsync(string path)
  {
    File.Create(path).Dispose();
    StorageFile file = await StorageFile.GetFileFromPathAsync(path);
    using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
    await view.CoreWebView2.CapturePreviewAsync(
      CoreWebView2CapturePreviewImageFormat.Png, stream);
    await stream.FlushAsync();
  }

  public PinnedImageWindow(
    WorkbenchAnnotationFile image,
    Guid sessionId,
    long revision,
    RecognitionTextLayerState? layer,
    Func<Task<RecognitionTextLayerState?>> prepareText,
    Func<string?> currentServiceInstance)
  {
    this.image = image;
    this.sessionId = sessionId;
    this.revision = revision;
    this.layer = layer;
    this.prepareText = prepareText;
    this.currentServiceInstance = currentServiceInstance;
    platform = new AnnotatedImagePlatform(
      () => WinRT.Interop.WindowNative.GetWindowHandle(window));
    Span<byte> header = stackalloc byte[24];
    using (FileStream source = File.OpenRead(image.Path))
    {
      source.ReadExactly(header);
    }
    imageWidth = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
    imageHeight = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);

    var root = new Grid();
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
    AddButton(toolbar, "复制图片", async () =>
    {
      await platform.CopyPngAsync(image.Path, CancellationToken.None);
      status.Text = "已复制图片";
    });
    prepareTextButton = AddButton(toolbar, "取字", async () =>
    {
      long generation = textGeneration;
      status.Text = "正在准备文字层";
      RecognitionTextLayerState? prepared = await prepareText();
      if (disposed || generation != textGeneration) return;
      if (prepared is not null) Update(prepared, detached: true);
    });
    copySelectionButton = AddButton(toolbar, "复制所选", CopySelectionAsync);
    AddButton(toolbar, "保存", async () =>
    {
      status.Text = await platform.SavePngAsync(image.Path, CancellationToken.None)
        ? "已保存图片" : "已取消保存";
    });
    AddButton(toolbar, "缩小", () => ChangeZoomAsync(-0.1));
    zoomInButton = AddButton(toolbar, "放大", () => ChangeZoomAsync(0.1));
    opacitySlider = new Slider { Minimum = 0.2, Maximum = 1, Value = 1, Width = 96 };
    ToolTipService.SetToolTip(opacitySlider, "透明度");
    opacitySlider.ValueChanged += (_, args) => ApplyWindowOpacity(args.NewValue);
    toolbar.Children.Add(opacitySlider);
    AddButton(toolbar, "关闭", () => { window.Close(); return Task.CompletedTask; });
    toolbar.Children.Add(status);
    root.Children.Add(toolbar);
    Grid.SetRow(view, 1);
    root.Children.Add(view);
    window.Content = root;
    window.Title = "VibeOCR 贴图";
    var presenter = OverlappedPresenter.Create();
    presenter.IsAlwaysOnTop = true;
    window.AppWindow.SetPresenter(presenter);
    ApplyWindowOpacity(1);
    window.AppWindow.MoveAndResize(new RectInt32(160, 160,
      Math.Clamp(imageWidth + 24, 640, 960), Math.Clamp(imageHeight + 80, 200, 760)));
    window.Closed += (_, _) => Dispose();
  }

  public async Task ShowAsync()
  {
    window.Activate();
    await view.EnsureCoreWebView2Async();
    if (disposed) return;
    core = view.CoreWebView2;
    core.Settings.AreDevToolsEnabled = false;
    core.Settings.IsWebMessageEnabled = false;
    core.Settings.AreDefaultContextMenusEnabled = true;
    core.NewWindowRequested += OnNewWindowRequested;
    core.PermissionRequested += OnPermissionRequested;
    core.NavigationStarting += OnNavigationStarting;
    core.NavigationCompleted += OnNavigationCompleted;
    core.AddWebResourceRequestedFilter(ImageUrl, CoreWebView2WebResourceContext.Image);
    core.WebResourceRequested += OnImageRequested;
    loaded = true;
    Render();
  }

  private static void OnNewWindowRequested(CoreWebView2 sender,
    CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

  private static void OnPermissionRequested(CoreWebView2 sender,
    CoreWebView2PermissionRequestedEventArgs args)
  {
    args.State = CoreWebView2PermissionState.Deny;
    args.Handled = true;
  }

  private void OnNavigationStarting(CoreWebView2 sender,
    CoreWebView2NavigationStartingEventArgs args)
  {
    if (!IsDocumentNavigationAllowed(args.Uri, documentUri, args.IsUserInitiated)) args.Cancel = true;
    else activeNavigation = args.NavigationId;
    navigationState = $"starting: scheme={new Uri(args.Uri).Scheme}, blank={args.Uri == "about:blank"}, user={args.IsUserInitiated}, cancelled={args.Cancel}, id={args.NavigationId}";
  }

  private async void OnNavigationCompleted(CoreWebView2 sender,
    CoreWebView2NavigationCompletedEventArgs args)
  {
    navigationState += $"; completed: success={args.IsSuccess}, status={args.WebErrorStatus}, id={args.NavigationId}, expected={activeNavigation}";
    if (disposed || args.NavigationId != activeNavigation) return;
    if (!args.IsSuccess)
    {
      status.Text = "贴图加载失败，请关闭后重新打开";
      AppLog.Warn($"Pinned image navigation failed: {args.WebErrorStatus}");
      return;
    }
    try
    {
      view.Opacity = 1;
      view.IsHitTestVisible = true;
      await view.ExecuteScriptAsync(
        $"document.body.style.zoom='{zoom.ToString(CultureInfo.InvariantCulture)}';" +
        FitLinesScript);
    }
    catch (Exception error)
    {
      if (!disposed) AppLog.Error("Pinned image navigation failed", error);
    }
  }

  public void Update(RecognitionTextLayerState? next, bool detached = false)
  {
    if (disposed) return;
    if (next is not null && (next.Binding?.SessionId != sessionId.ToString("N") ||
        next.Binding.Revision != revision)) return;
    RecognitionTextLayerState? valid = next is { Status: "textlayer.ready" } &&
      next.Binding?.SessionId == sessionId.ToString("N") &&
      next.Binding.Revision == revision ? next : null;
    if (valid is null)
    {
      textGeneration++;
      oldSnapshot = true;
    }
    if (ReferenceEquals(layer, valid))
    {
      if (valid is null) status.Text = "旧快照：文字层已失效，可按贴图原图重新取字";
      return;
    }
    layer = valid;
    status.Text = valid is null
      ? "旧快照：文字层已失效，可按贴图原图重新取字"
      : detached || oldSnapshot
        ? "旧快照：可在贴图上选字复制"
        : "可在图片上选字复制";
    if (loaded) Render();
  }

  public void MarkOldSnapshot()
  {
    if (disposed) return;
    oldSnapshot = true;
    status.Text = layer is null
      ? "旧快照：可按贴图原图取字"
      : "旧快照：可在贴图上选字复制";
  }

  private async void OnImageRequested(CoreWebView2 sender,
    CoreWebView2WebResourceRequestedEventArgs args)
  {
    if (disposed) return;
    var deferral = args.GetDeferral();
    try
    {
      if (disposed) return;
      StorageFile file = await StorageFile.GetFileFromPathAsync(image.Path);
      if (disposed) return;
      var stream = await file.OpenReadAsync();
      if (disposed)
      {
        stream.Dispose();
        return;
      }
      responseStreams.Add(stream);
      args.Response = sender.Environment.CreateWebResourceResponse(
        stream, 200, "OK", "Content-Type: image/png");
    }
    catch (Exception error)
    {
      if (!disposed)
      {
        AppLog.Error("Pinned image resource failed", error);
        try
        {
          args.Response = sender.Environment.CreateWebResourceResponse(
            null, 404, "Not Found", "");
        }
        catch (Exception responseError)
        {
          AppLog.Error("Pinned image error response failed", responseError);
        }
      }
    }
    finally
    {
      try { deferral.Complete(); }
      catch (Exception error)
      {
        if (!disposed) AppLog.Error("Pinned image request completion failed", error);
      }
    }
  }

  private void Render()
  {
    // Keep WebView2 in the layout while navigation replaces a stale text layer.
    view.Opacity = 0;
    view.IsHitTestVisible = false;
    // OCR supplies line boxes only. Browser glyph layout allows substrings but is approximate.
    var html = new StringBuilder("<!doctype html><html><head><meta charset='utf-8'>")
      .Append("<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src https://pin.vibeocr; style-src 'unsafe-inline'\">")
      .Append("<style>body{margin:0;background:#202020;overflow:auto}#stage{position:relative;width:100%;aspect-ratio:")
      .Append(imageWidth).Append('/').Append(imageHeight)
      .Append(";container-type:inline-size}img{width:100%;display:block}.line{position:absolute;overflow:hidden;color:transparent;white-space:pre;line-height:1;user-select:text;cursor:text}.line span{display:inline-block;transform-origin:left top}.line::selection{background:#1f6feb70}</style></head><body><div id='stage'><img src='")
      .Append(ImageUrl).Append("' alt='贴图'>");
    foreach (RecognitionTextLayerLine line in layer?.Lines ?? [])
    {
      double fontCqw = (line.Y2 - line.Y1) / 10 * imageHeight / imageWidth;
      html.Append("<div class='line' style='left:")
        .Append(Percent(line.X1)).Append("%;top:").Append(Percent(line.Y1))
        .Append("%;width:").Append(Percent(line.X2 - line.X1))
        .Append("%;height:").Append(Percent(line.Y2 - line.Y1))
        .Append("%;font-size:").Append(fontCqw.ToString("F4", CultureInfo.InvariantCulture))
        .Append("cqw'><span>").Append(WebUtility.HtmlEncode(line.Text)).Append("</span>\n</div>");
    }
    html.Append("</div></body></html>");
    navigationState = $"requested: chars={html.Length}";
    documentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html.ToString()));
    core!.Navigate(documentUri);
  }

  internal static bool IsDocumentNavigationAllowed(string uri, string? expected, bool userInitiated) =>
    !userInitiated && expected is not null && string.Equals(uri, expected, StringComparison.Ordinal);

  private static string Percent(double normalized) =>
    (normalized / 10).ToString("F4", CultureInfo.InvariantCulture);

  private async Task CopySelectionAsync()
  {
    RecognitionTextLayerState? selectedLayer = layer;
    if (selectedLayer is not null &&
        selectedLayer.ServiceInstance != (currentServiceInstance() ?? string.Empty))
    {
      Update(null);
      selectedLayer = null;
    }
    if (selectedLayer is null || !loaded)
    {
      status.Text = "文字层尚未就绪";
      return;
    }
    string json = await view.ExecuteScriptAsync("window.getSelection()?.toString() ?? ''");
    string? text = JsonSerializer.Deserialize<string>(json);
    if (!disposed && ReferenceEquals(layer, selectedLayer) &&
      !string.IsNullOrWhiteSpace(text))
    {
      await platform.CopyTextAsync(text, CancellationToken.None);
      status.Text = "已复制所选文字";
    }
    else status.Text = "请先在图片上选中文字";
  }

  private async Task ChangeZoomAsync(double delta)
  {
    zoom = Math.Clamp(zoom + delta, 0.25, 4);
    if (loaded)
    {
      await view.ExecuteScriptAsync($"document.body.style.zoom='{zoom.ToString(CultureInfo.InvariantCulture)}'");
    }
  }

  private Button AddButton(StackPanel bar, string label, Func<Task> action)
  {
    var button = new Button { Content = label };
    button.Click += async (_, _) =>
    {
      try { await action(); }
      catch (Exception error)
      {
        if (disposed) return;
        AppLog.Error($"Pinned image action failed: {label}", error);
        status.Text = error switch
        {
          ClipboardBusyException => "剪贴板被占用，请重试",
          PinnedTextPreparationException preparation => preparation.Message,
          _ => $"{label}失败，请重试",
        };
      }
    };
    bar.Children.Add(button);
    return button;
  }

  public void Dispose()
  {
    if (disposed) return;
    disposed = true;
    textGeneration++;
    loaded = false;
    if (core is { } current)
    {
      current.NewWindowRequested -= OnNewWindowRequested;
      current.PermissionRequested -= OnPermissionRequested;
      current.NavigationStarting -= OnNavigationStarting;
      current.NavigationCompleted -= OnNavigationCompleted;
      current.WebResourceRequested -= OnImageRequested;
      core = null;
    }
    try { view.Close(); }
    catch (Exception error) { AppLog.Error("Pinned WebView close failed", error); }
    foreach (IRandomAccessStream stream in responseStreams)
    {
      try { stream.Dispose(); }
      catch (Exception error) { AppLog.Error("Pinned image stream close failed", error); }
    }
    responseStreams.Clear();
    image.Dispose();
    Closed?.Invoke(this);
  }

  public void Close()
  {
    if (!disposed) window.Close();
  }

  private void ApplyWindowOpacity(double opacity)
  {
    if (disposed) return;
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
    nint style = GetWindowLongPtrW(handle, GwlExStyle);
    _ = SetWindowLongPtrW(handle, GwlExStyle, (nint)((long)style | WsExLayered));
    if (!SetLayeredWindowAttributes(handle, 0,
      (byte)Math.Round(Math.Clamp(opacity, 0.2, 1) * 255), LwaAlpha))
      throw new Win32Exception(Marshal.GetLastWin32Error());
  }

  [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
  private static extern nint GetWindowLongPtrW(nint window, int index);

  [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
  private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetLayeredWindowAttributes(
    nint window, uint colorKey, byte alpha, uint flags);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetLayeredWindowAttributes(
    nint window, out uint colorKey, out byte alpha, out uint flags);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetCursorPos(int x, int y);

  [DllImport("user32.dll", EntryPoint = "mouse_event")]
  private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, nuint extraInfo);
}
