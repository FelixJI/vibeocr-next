using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;

namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// 独立钉图窗口：原大小图片加窄边框贴屏。窗口无原生标题栏，右上角悬浮
/// 置顶/原大小/最小化/最大化/关闭五个窗口控制；取字、复制所选、复制图片、
/// 保存、缩放与透明度收进左上角“更多”按钮的原生 Flyout，不占布局空间；
/// 顶部窄条是拖动把手。文字选择/复制/保存能力保持不变。
/// </summary>
internal sealed class PinnedImageWindow : IDisposable
{
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
  private const uint MouseEventFLeftDown = 0x0002;
  private const uint MouseEventFLeftUp = 0x0004;
  private const int SmCxSizeFrame = 32;
  private const int SmCySizeFrame = 33;
  private const int SmCxPaddedBorder = 92;
  private const uint GaRoot = 2;
  private const uint SwpNoSize = 0x0001;
  private const uint SwpNoZOrder = 0x0004;
  private const uint SwpNoActivate = 0x0010;

  /// <summary>顶部拖动把手高度（DIP），同时是右上角窗口控制所在条带。</summary>
  internal const double BandHeightDip = 36;
  private const double BandButtonWidthDip = 34;
  private const double BandButtonHeightDip = 28;

  private const string PinGlyph = "\uE718";
  private const string UnpinGlyph = "\uE77A";
  private const string MoreGlyph = "\uE10C";
  private const string ChromeMinimizeGlyph = "\uE921";
  private const string ChromeMaximizeGlyph = "\uE922";
  private const string ChromeRestoreGlyph = "\uE923";
  private const string ChromeCloseGlyph = "\uE8BB";

  private readonly string imageUrl;
  private readonly Window window = new();
  private readonly OverlappedPresenter presenter = OverlappedPresenter.Create();
  private readonly Grid root = new();
  private readonly Grid band = new();
  private readonly WebView2 view = new();
  private readonly TextBlock status = new()
  {
    FontSize = 12,
    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
    TextWrapping = TextWrapping.Wrap,
  };
  private readonly Border statusChip = new()
  {
    HorizontalAlignment = HorizontalAlignment.Left,
    VerticalAlignment = VerticalAlignment.Bottom,
    Margin = new Thickness(4),
    Padding = new Thickness(8, 4, 8, 4),
    CornerRadius = new CornerRadius(6),
    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(178, 32, 32, 32)),
    Visibility = Visibility.Collapsed,
    IsHitTestVisible = false,
    MaxWidth = 420,
  };
  /// <summary>左上角“更多”Flyout：原生开关/焦点/轻量关闭，不自建悬停。</summary>
  private readonly Flyout actionsFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
  private readonly Button copySelectionButton;
  private readonly Button prepareTextButton;
  private readonly Button zoomInButton;
  private readonly Button topmostButton;
  private readonly Button originalSizeButton;
  private readonly Button minimizeButton;
  private readonly Button maximizeButton;
  private readonly Button closeButton;
  private readonly Button moreButton;
  private readonly FontIcon topmostIcon = new() { Glyph = UnpinGlyph, FontSize = 14 };
  private readonly FontIcon maximizeIcon = new() { Glyph = ChromeMaximizeGlyph, FontSize = 14 };
  private readonly Slider opacitySlider;
  /// <summary>导航后把 stage 宽固定为图片像素/devicePixelRatio，保持 1:1。</summary>
  private readonly string stageScaleScript;
  private readonly WorkbenchAnnotationFile image;
  private readonly IAnnotatedImagePlatform platform;
  private readonly Func<Task<RecognitionTextLayerState?>> prepareText;
  private readonly IReadOnlyList<WorkbenchExclusionBox> excludeBoxes = [];
  private readonly Func<string?> currentServiceInstance;
  private readonly List<IRandomAccessStream> responseStreams = [];
  private readonly Guid sessionId;
  private readonly long revision;
  private readonly int imageWidth;
  private readonly int imageHeight;
  private RecognitionTextLayerState? layer;

  /// <summary>按冻结的归一化排除矩形丢弃正面积相交行；无层/无矩形原样返回。</summary>
  private static RecognitionTextLayerState? FilterExcludedLines(
    RecognitionTextLayerState? layer, IReadOnlyList<WorkbenchExclusionBox> boxes)
  {
    if (layer is null || boxes.Count == 0 || layer.Lines is not { Count: > 0 }) return layer;
    List<RecognitionTextLayerLine> kept = [];
    foreach (RecognitionTextLayerLine line in layer.Lines)
    {
      bool intersects = boxes.Any(box =>
        WorkbenchExclusionBox.LineIntersectsBox(line, box));
      if (!intersects) kept.Add(line);
    }
    return kept.Count == layer.Lines.Count ? layer : layer with { Lines = kept };
  }

  /// <summary>
  /// 原大小客户区换算：WebView2 的 CSS 像素随显示器 DPI 栅格化，客户区物理
  /// 尺寸等于图片像素尺寸时图片按 1:1 显示；图片超出可用工作区时窗口钳制到
  /// 工作区，图片内容仍保持 1:1（可滚动查看）。
  /// </summary>
  internal static (int Width, int Height) ComputeOriginalClientSize(
    int imageWidth, int imageHeight, int maxWidth, int maxHeight) =>
    (Math.Clamp(imageWidth, 1, Math.Max(1, maxWidth)),
      Math.Clamp(imageHeight, 1, Math.Max(1, maxHeight)));

  /// <summary>贴图缩放范围：与既有 缩小/放大 步进一致。</summary>
  internal static double ClampZoom(double zoom) => Math.Clamp(zoom, 0.25, 4);

  private bool loaded;
  private bool documentReady;
  private bool disposed;
  private CoreWebView2? core;
  private ulong activeNavigation;
  private string navigationState = "not_started";
  private string? documentUri;
  private double zoom = 1;
  private long textGeneration;
  private bool oldSnapshot;
  private uint appliedStageDpi;
  private Windows.Foundation.Point? dragOrigin;

  public event Action<PinnedImageWindow>? Closed;

  internal string SmokeImagePath => image.Path;
  internal string SmokeNavigationState => $"loaded={loaded}; lines={layer?.Lines?.Count}; {navigationState}";
  internal bool SmokeDisposed => disposed;
  internal bool SmokeDocumentReady => documentReady;
  internal bool SmokeAlwaysOnTop => presenter.IsAlwaysOnTop;
  internal double SmokeZoom => zoom;
  internal bool SmokeIsMaximized => IsZoomed(WinRT.Interop.WindowNative.GetWindowHandle(window));
  internal bool SmokeIsMinimized => IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(window));
  internal (int Width, int Height) SmokeImageSize => (imageWidth, imageHeight);
  internal SizeInt32 SmokeClientSize
  {
    get
    {
      if (!GetClientRect(WinRT.Interop.WindowNative.GetWindowHandle(window), out RectL rect) ||
          rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        throw new InvalidOperationException("Pin client bounds are unavailable.");
      return new SizeInt32(rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
  }
  internal SizeInt32 SmokeExpectedOriginalClientSize
  {
    get
    {
      (int width, int height) = ComputeOriginalClientSize(
        imageWidth, imageHeight, AvailableClientWidth(), AvailableClientHeight());
      return new SizeInt32(width, height);
    }
  }
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
  internal void SmokeInvokeToggleTopmost() => new ButtonAutomationPeer(topmostButton).Invoke();
  internal void SmokeInvokeOriginalSize() => new ButtonAutomationPeer(originalSizeButton).Invoke();
  internal void SmokeInvokeMinimize() => new ButtonAutomationPeer(minimizeButton).Invoke();
  internal void SmokeInvokeMaximize() => new ButtonAutomationPeer(maximizeButton).Invoke();
  internal void SmokeRevealActions() => actionsFlyout.ShowAt(moreButton);
  internal void SmokeDismissActions() => actionsFlyout.Hide();
  /// <summary>模拟任务栏还原：最小化后由系统还原路径回到正常状态。</summary>
  internal void SmokeRestoreFromMinimized() => presenter.Restore();
  internal async Task SmokeDragTitleBarAsync(int dx, int dy)
  {
    // Real-mouse-path drag on the top band handle. Verify the point really hits
    // this pin window before the native press, wait until the press actually
    // activated it, and release only after the window really moved.
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
    window.Activate();
    if (!GetWindowRect(handle, out RectL bounds))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    uint dpi = GetDpiForWindow(handle);
    if (dpi == 0) throw new InvalidOperationException("Pin DPI is unavailable.");
    int Metric(int index) => GetSystemMetricsForDpi(index, dpi);
    // 把手条带在客户区顶部：物理高度按 DPI 换算，拖点取左侧四分位避开右上角按钮。
    int bandHeight = (int)Math.Ceiling(BandHeightDip * dpi / 96.0);
    int x = bounds.Left + (bounds.Right - bounds.Left) / 4;
    int y = bounds.Top + Metric(SmCySizeFrame) + Metric(SmCxPaddedBorder) + bandHeight / 2;
    string geometry = $"handle=0x{(long)handle:X}, rect=({bounds.Left},{bounds.Top}," +
      $"{bounds.Right},{bounds.Bottom}), dpi={dpi}, point=({x},{y})";
    nint hitRoot = GetAncestor(WindowFromPoint(new PointL { X = x, Y = y }), GaRoot);
    if (hitRoot != handle)
      throw new InvalidOperationException($"Pin drag point is not its band: " +
        $"{geometry}, hitRoot=0x{(long)hitRoot:X}.");
    if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastWin32Error());
    bool down = false;
    try
    {
      MouseEvent(MouseEventFLeftDown, 0, 0, 0, 0);
      down = true;
      bool activated = false;
      for (int attempt = 0; attempt < 40; attempt++)
      {
        if (GetForegroundWindow() == handle) { activated = true; break; }
        await Task.Delay(25);
      }
      if (!activated)
        throw new InvalidOperationException($"Pin press did not activate it: " +
          $"{geometry}, fg=0x{(long)GetForegroundWindow():X}.");
      if (!SetCursorPos(x + dx, y + dy))
        throw new Win32Exception(Marshal.GetLastWin32Error());
      for (int attempt = 0; ; attempt++)
      {
        if (!GetWindowRect(handle, out RectL moved))
          throw new Win32Exception(Marshal.GetLastWin32Error());
        if (moved.Left != bounds.Left || moved.Top != bounds.Top) break;
        if (attempt >= 200)
          throw new InvalidOperationException($"Pin did not follow the drag: {geometry}, " +
            $"cursor=({x + dx},{y + dy}), now=({moved.Left},{moved.Top}).");
        await Task.Delay(25);
      }
    }
    finally
    {
      if (down) MouseEvent(MouseEventFLeftUp, 0, 0, 0, 0);
    }
  }
  /// <summary>真实鼠标点击右上角关闭按钮：验证悬浮控件在 WebView2 之上可交互。</summary>
  internal void SmokeMouseClickCloseButton()
  {
    root.UpdateLayout();
    Windows.Foundation.Rect dip = closeButton.TransformToVisual(root).TransformBounds(
      new Windows.Foundation.Rect(0, 0, closeButton.ActualWidth, closeButton.ActualHeight));
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
    uint dpi = GetDpiForWindow(handle);
    if (dpi == 0) throw new InvalidOperationException("Pin DPI is unavailable.");
    PointL origin = new();
    if (!ClientToScreen(handle, ref origin))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    int x = origin.X + (int)Math.Round((dip.X + dip.Width / 2) * dpi / 96.0);
    int y = origin.Y + (int)Math.Round((dip.Y + dip.Height / 2) * dpi / 96.0);
    string geometry = $"handle=0x{(long)handle:X}, button=({dip.X:F1},{dip.Y:F1}," +
      $"{dip.Width:F1}x{dip.Height:F1}), dpi={dpi}, point=({x},{y})";
    nint hitRoot = GetAncestor(WindowFromPoint(new PointL { X = x, Y = y }), GaRoot);
    if (hitRoot != handle)
      throw new InvalidOperationException($"Pin close button is not hittable: " +
        $"{geometry}, hitRoot=0x{(long)hitRoot:X}.");
    if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastWin32Error());
    MouseEvent(MouseEventFLeftDown, 0, 0, 0, 0);
    MouseEvent(MouseEventFLeftUp, 0, 0, 0, 0);
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
    IReadOnlyList<WorkbenchExclusionBox> excludeBoxes,
    Func<Task<RecognitionTextLayerState?>> prepareText,
    Func<string?> currentServiceInstance)
  {
    this.image = image;
    imageUrl = "https://pin.vibeocr/image" + Path.GetExtension(image.Path);
    this.sessionId = sessionId;
    this.revision = revision;
    // 冻结的排除矩形：贴图重取字（含会话失效后的冻结路径）返回的行
    // 也按同一正面积相交策略过滤，不因重新识别找回跨边界行。
    this.excludeBoxes = excludeBoxes;
    this.layer = FilterExcludedLines(layer, excludeBoxes);
    this.prepareText = prepareText;
    this.currentServiceInstance = currentServiceInstance;
    platform = new AnnotatedImagePlatform(
      () => WinRT.Interop.WindowNative.GetWindowHandle(window));
    imageWidth = image.Width;
    imageHeight = image.Height;
    stageScaleScript =
      "(()=>{const s=document.getElementById('stage');" +
      "if(s)s.style.width=(" + imageWidth.ToString(CultureInfo.InvariantCulture) +
      "/window.devicePixelRatio)+'px';})();";

    var bandButtons = BuildBand();
    topmostButton = bandButtons.Topmost;
    originalSizeButton = bandButtons.OriginalSize;
    minimizeButton = bandButtons.Minimize;
    maximizeButton = bandButtons.Maximize;
    closeButton = bandButtons.Close;
    moreButton = bandButtons.More;
    var actionButtons = BuildActionsFlyout();
    prepareTextButton = actionButtons.PrepareText;
    copySelectionButton = actionButtons.CopySelection;
    zoomInButton = actionButtons.ZoomIn;
    opacitySlider = actionButtons.Opacity;
    SyncTopmostButton();
    SyncMaximizeButton();
    statusChip.Child = status;
    status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
      statusChip.Visibility = string.IsNullOrEmpty(status.Text)
        ? Visibility.Collapsed : Visibility.Visible);
    root.Children.Add(view);
    root.Children.Add(band);
    root.Children.Add(statusChip);
    window.Content = root;
    window.Title = "VibeOCR 贴图";
    presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
    presenter.IsAlwaysOnTop = true;
    window.AppWindow.SetPresenter(presenter);
    ApplyWindowOpacity(1);
    // 原大小开窗：客户区物理尺寸等于图片像素；位置固定锚点并钳制在工作区内。
    (int availableWidth, int availableHeight) = ComputeAvailableClientSize();
    (int clientWidth, int clientHeight) = ComputeOriginalClientSize(
      imageWidth, imageHeight, availableWidth, availableHeight);
    RectInt32 work = DisplayArea.GetFromWindowId(
      window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
    window.AppWindow.Move(new PointInt32(
      work.X + Math.Min(160, Math.Max(0, work.Width - clientWidth)),
      work.Y + Math.Min(160, Math.Max(0, work.Height - clientHeight))));
    window.AppWindow.ResizeClient(new SizeInt32(clientWidth, clientHeight));
    window.AppWindow.Changed += OnAppWindowChanged;
    window.Closed += (_, _) => Dispose();
  }

  private (Button Topmost, Button OriginalSize, Button Minimize, Button Maximize, Button Close,
    Button More) BuildBand()
  {
    band.Height = BandHeightDip;
    band.VerticalAlignment = VerticalAlignment.Top;
    band.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(64, 32, 32, 32));
    band.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    Button more = AddBandButton(band,
      new FontIcon { Glyph = MoreGlyph, FontSize = 14 }, "更多", () =>
    {
      actionsFlyout.ShowAt(moreButton);
      return Task.CompletedTask;
    });
    Grid.SetColumn(more, 0);
    more.HorizontalAlignment = HorizontalAlignment.Left;
    more.VerticalAlignment = VerticalAlignment.Center;
    more.Margin = new Thickness(2, 0, 0, 0);
    var buttons = new StackPanel
    {
      Orientation = Orientation.Horizontal,
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(0, 0, 2, 0),
      Spacing = 2,
    };
    Button topmost = AddBandButton(buttons, topmostIcon, "取消置顶", () =>
    {
      ToggleTopmost();
      return Task.CompletedTask;
    });
    Button originalSize = AddBandButton(buttons,
      new TextBlock { Text = "1:1", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
      "原大小", RestoreOriginalSizeAsync);
    Button minimize = AddBandButton(buttons,
      new FontIcon { Glyph = ChromeMinimizeGlyph, FontSize = 14 }, "最小化", () =>
    {
      presenter.Minimize();
      return Task.CompletedTask;
    });
    Button maximize = AddBandButton(buttons, maximizeIcon, "最大化", () =>
    {
      ToggleMaximize();
      return Task.CompletedTask;
    });
    Button close = AddBandButton(buttons,
      new FontIcon { Glyph = ChromeCloseGlyph, FontSize = 14 }, "关闭", () =>
    {
      window.Close();
      return Task.CompletedTask;
    });
    // 小图窗口窄于五键时顶部条可横向滚动，不放大图片显示比例。
    var scroller = new ScrollViewer
    {
      Content = buttons,
      HorizontalContentAlignment = HorizontalAlignment.Right,
      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
      HorizontalScrollMode = ScrollMode.Enabled,
      VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
      VerticalScrollMode = ScrollMode.Disabled,
      IsTabStop = false,
    };
    Grid.SetColumn(scroller, 1);
    band.Children.Add(scroller);
    band.DoubleTapped += (_, args) => { ToggleMaximize(); args.Handled = true; };
    band.PointerPressed += OnBandPointerPressed;
    band.PointerMoved += OnBandPointerMoved;
    band.PointerReleased += OnBandPointerReleased;
    band.PointerCaptureLost += OnBandPointerEnded;
    return (topmost, originalSize, minimize, maximize, close, more);
  }

  private (Button PrepareText, Button CopySelection, Button ZoomIn, Slider Opacity)
    BuildActionsFlyout()
  {
    var panel = new StackPanel { Spacing = 4 };
    var primary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
    Button prepareText = AddButton(primary, "取字", async () =>
    {
      long generation = textGeneration;
      status.Text = "正在准备文字层";
      RecognitionTextLayerState? prepared = await this.prepareText();
      if (disposed || generation != textGeneration) return;
      if (prepared is not null) Update(prepared, detached: true);
    });
    Button copySelection = AddButton(primary, "复制所选", CopySelectionAsync);
    AddButton(primary, "复制图片", async () =>
    {
      await platform.CopyImageAsync(image.Path, CancellationToken.None);
      status.Text = "已复制图片";
    });
    AddButton(primary, "保存", async () =>
    {
      status.Text = await platform.SaveImageAsync(image.Path, CancellationToken.None)
        ? "已保存图片" : "已取消保存";
    });
    var secondary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
    AddButton(secondary, "缩小", () => ChangeZoomAsync(-0.1));
    Button zoomIn = AddButton(secondary, "放大", () => ChangeZoomAsync(0.1));
    Slider opacity = new() { Minimum = 0.2, Maximum = 1, Value = 1, Width = 96 };
    ToolTipService.SetToolTip(opacity, "透明度");
    opacity.ValueChanged += (_, args) => ApplyWindowOpacity(args.NewValue);
    secondary.Children.Add(opacity);
    panel.Children.Add(primary);
    panel.Children.Add(secondary);
    actionsFlyout.Content = panel;
    return (prepareText, copySelection, zoomIn, opacity);
  }

  private void ToggleTopmost()
  {
    presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
    SyncTopmostButton();
  }

  private void ToggleMaximize()
  {
    if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
    else presenter.Maximize();
    SyncMaximizeButton();
  }

  /// <summary>原大小：缩放回到 1:1，窗口脱离最大化并恢复为图片像素客户区。</summary>
  private async Task RestoreOriginalSizeAsync()
  {
    zoom = 1;
    if (disposed) return;
    if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
    (int availableWidth, int availableHeight) = ComputeAvailableClientSize();
    (int clientWidth, int clientHeight) = ComputeOriginalClientSize(
      imageWidth, imageHeight, availableWidth, availableHeight);
    window.AppWindow.ResizeClient(new SizeInt32(clientWidth, clientHeight));
    SyncMaximizeButton();
    if (loaded)
    {
      await view.ExecuteScriptAsync(
        $"document.body.style.zoom='{zoom.ToString(CultureInfo.InvariantCulture)}'");
    }
  }

  private void SyncTopmostButton()
  {
    bool topmost = presenter.IsAlwaysOnTop;
    topmostIcon.Glyph = topmost ? UnpinGlyph : PinGlyph;
    string label = topmost ? "取消置顶" : "置顶";
    AutomationProperties.SetName(topmostButton, label);
    ToolTipService.SetToolTip(topmostButton, label);
  }

  private void SyncMaximizeButton()
  {
    bool maximized = presenter.State == OverlappedPresenterState.Maximized;
    string glyph = maximized ? ChromeRestoreGlyph : ChromeMaximizeGlyph;
    if (maximizeIcon.Glyph == glyph) return;
    maximizeIcon.Glyph = glyph;
    string label = maximized ? "还原" : "最大化";
    AutomationProperties.SetName(maximizeButton, label);
    ToolTipService.SetToolTip(maximizeButton, label);
  }

  private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
  {
    SyncMaximizeButton();
    // 跨显示器 DPI 变化时重设 stage 宽，维持 1 图片像素 = 1 物理像素。
    uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
    if (dpi == 0 || dpi == appliedStageDpi || !documentReady || disposed) return;
    appliedStageDpi = dpi;
    _ = ReapplyStageScaleAsync();
  }

  private async Task ReapplyStageScaleAsync()
  {
    try { await view.ExecuteScriptAsync(stageScaleScript); }
    catch (Exception error)
    {
      if (!disposed) AppLog.Error("Pinned image stage rescale failed", error);
    }
  }

  /// <summary>当前显示器工作区扣除窗口边框后可用的客户区物理尺寸。</summary>
  private (int Width, int Height) ComputeAvailableClientSize()
  {
    RectInt32 work = DisplayArea.GetFromWindowId(
      window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
    uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
    int frameX = GetSystemMetricsForDpi(SmCxSizeFrame, dpi) +
      GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
    int frameY = GetSystemMetricsForDpi(SmCySizeFrame, dpi) +
      GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
    return (Math.Max(1, work.Width - frameX * 2), Math.Max(1, work.Height - frameY * 2));
  }

  private int AvailableClientWidth() => ComputeAvailableClientSize().Width;
  private int AvailableClientHeight() => ComputeAvailableClientSize().Height;

  private void OnBandPointerPressed(object sender, PointerRoutedEventArgs args)
  {
    var point = args.GetCurrentPoint(band);
    if (!point.Properties.IsLeftButtonPressed) return;
    if (!TryGetCursorLocation(out Windows.Foundation.Point origin)) return;
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
    if (IsZoomed(handle)) presenter.Restore();
    dragOrigin = origin;
    band.CapturePointer(args.Pointer);
    args.Handled = true;
  }

  private void OnBandPointerMoved(object sender, PointerRoutedEventArgs args)
  {
    if (dragOrigin is not { } origin || !TryGetCursorLocation(out Windows.Foundation.Point current))
      return;
    double deltaX = current.X - origin.X;
    double deltaY = current.Y - origin.Y;
    if (deltaX == 0 && deltaY == 0) return;
    dragOrigin = current;
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
    if (!GetWindowRect(handle, out RectL bounds)) return;
    SetWindowPos(handle, 0,
      checked(bounds.Left + (int)Math.Round(deltaX)),
      checked(bounds.Top + (int)Math.Round(deltaY)),
      0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    args.Handled = true;
  }

  private void OnBandPointerReleased(object sender, PointerRoutedEventArgs args)
  {
    if (dragOrigin is null) return;
    dragOrigin = null;
    band.ReleasePointerCapture(args.Pointer);
    args.Handled = true;
  }

  private void OnBandPointerEnded(object sender, PointerRoutedEventArgs args)
  {
    // 捕获丢失（Esc/窗口切换）视作拖动结束。
    dragOrigin = null;
  }

  private static bool TryGetCursorLocation(out Windows.Foundation.Point location)
  {
    if (!GetCursorPos(out PointL point))
    {
      location = default;
      return false;
    }
    location = new Windows.Foundation.Point(point.X, point.Y);
    return true;
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
    core.AddWebResourceRequestedFilter(imageUrl, CoreWebView2WebResourceContext.Image);
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
    if (!IsDocumentNavigationAllowed(args.Uri, documentUri)) args.Cancel = true;
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
        stageScaleScript + FitLinesScript);
      if (disposed || args.NavigationId != activeNavigation) return;
      documentReady = true;
      appliedStageDpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
      copySelectionButton.IsEnabled = layer is not null;
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
    // 重取字结果与初始层同一过滤策略：正面积相交的行不进入贴图。
    next = FilterExcludedLines(next, excludeBoxes);
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
        stream, 200, "OK", "Content-Type: " + image.MediaType);
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
    documentReady = false;
    activeNavigation = 0;
    copySelectionButton.IsEnabled = false;
    // Keep WebView2 in the layout while navigation replaces a stale text layer.
    view.Opacity = 0;
    view.IsHitTestVisible = false;
    // OCR supplies line boxes only. Browser glyph layout allows substrings but is approximate.
    var html = new StringBuilder("<!doctype html><html><head><meta charset='utf-8'>")
      .Append("<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src https://pin.vibeocr; style-src 'unsafe-inline'\">")
      .Append("<style>body{margin:0;background:#202020;overflow:auto}#stage{position:relative;width:100%;aspect-ratio:")
      .Append(imageWidth).Append('/').Append(imageHeight)
      .Append(";container-type:inline-size}img{width:100%;display:block}.line{position:absolute;overflow:hidden;color:transparent;white-space:pre;line-height:1;user-select:text;cursor:text}.line span{display:inline-block;transform-origin:left top}.line::selection{background:#1f6feb70}</style></head><body><div id='stage'><img src='")
      .Append(imageUrl).Append("' alt='贴图'>");
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

  internal static bool IsDocumentNavigationAllowed(string uri, string? expected) =>
    expected is not null && string.Equals(uri, expected, StringComparison.Ordinal);

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
    if (selectedLayer is null || !loaded || !documentReady)
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
    zoom = ClampZoom(zoom + delta);
    if (loaded)
    {
      await view.ExecuteScriptAsync($"document.body.style.zoom='{zoom.ToString(CultureInfo.InvariantCulture)}'");
    }
  }

  private Button AddButton(StackPanel bar, string label, Func<Task> action) =>
    WireClick(new Button { Content = label }, label, action, addTo: bar);

  private Button AddBandButton(Panel bar, object content, string label, Func<Task> action)
  {
    var button = new Button
    {
      Content = content,
      Width = BandButtonWidthDip,
      Height = BandButtonHeightDip,
      Padding = new Thickness(0),
    };
    return WireClick(button, label, action, addTo: bar);
  }

  private Button WireClick(Button button, string label, Func<Task> action, Panel addTo)
  {
    AutomationProperties.SetName(button, label);
    ToolTipService.SetToolTip(button, label);
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
    addTo.Children.Add(button);
    return button;
  }

  public void Dispose()
  {
    if (disposed) return;
    disposed = true;
    textGeneration++;
    loaded = false;
    window.AppWindow.Changed -= OnAppWindowChanged;
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

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetWindowRect(nint window, out RectL rect);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetClientRect(nint window, out RectL rect);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ClientToScreen(nint window, ref PointL point);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsZoomed(nint window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsIconic(nint window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetCursorPos(out PointL point);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetWindowPos(
    nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

  [DllImport("user32.dll")]
  private static extern uint GetDpiForWindow(nint window);

  [DllImport("user32.dll")]
  private static extern int GetSystemMetricsForDpi(int index, uint dpi);

  [DllImport("user32.dll")]
  private static extern nint WindowFromPoint(PointL point);

  [DllImport("user32.dll")]
  private static extern nint GetAncestor(nint window, uint flags);

  [StructLayout(LayoutKind.Sequential)]
  private struct RectL
  {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct PointL
  {
    public int X;
    public int Y;
  }
}
