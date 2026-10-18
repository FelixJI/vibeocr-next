using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VibeOCR.App.Web;

namespace VibeOCR.App.Features.Pins;

/// <summary>
/// #238 类型化贴图窗：文字（默认）/颜色/文件预览（仅授权图片与有界纯
/// 文本；未知格式明确拒绝）。内容类型显式建模，与任何截图 OCR 绑定无关。
/// 原生轻量承载（与贴图同为原生窗口 chrome），不引新 bridge/依赖。
/// </summary>
internal sealed class TypedPinWindow : IDisposable
{
  public enum ContentKind
  {
    Text,
    Color,
    Formula,
    /// <summary>待授权选择文件后解析为图片/文本预览。</summary>
    FilePreview,
    FileImage,
    FileText,
  }

  private const int GwlExStyle = -20;
  private const long WsExLayered = 0x00080000;
  private const uint LwaAlpha = 0x00000002;
  private const double BandHeightDip = 32;

  private readonly Window window = new();
  private readonly OverlappedPresenter presenter = OverlappedPresenter.Create();
  private readonly Grid root = new();
  private readonly Grid band = new();
  private readonly TextBox editor = new();
  private readonly Border swatch = new();
  private readonly Image image = new();
  private readonly TextBlock status = new()
  {
    FontSize = 11,
    Opacity = 0.8,
  };
  private readonly Func<WebView2, Task<WebWorkbenchHost>>? createFormulaHost;
  private WebView2? formulaView;
  private WebWorkbenchHost? formulaHost;
  private ContentKind kind;
  private bool disposed;

  /// <summary>贴图身份（4 张上限计数与编组共用）。</summary>
  internal Guid PinId { get; }
  internal ContentKind Kind => kind;
  internal bool Disposed => disposed;
  public event Action<TypedPinWindow>? Closed;

  internal TypedPinWindow(ContentKind kind, Guid? pinId = null,
    Func<WebView2, Task<WebWorkbenchHost>>? createFormulaHost = null)
  {
    PinId = pinId ?? Guid.NewGuid();
    this.kind = kind;
    editor.MaxLength = kind == ContentKind.Formula ? 4096 : 256 * 1024;
    this.createFormulaHost = createFormulaHost;
    BuildBand();
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    root.Children.Add(band);
    Grid content = BuildContent(kind);
    Grid.SetRow(content, 1);
    root.Children.Add(content);
    Grid.SetRow(status, 2);
    root.Children.Add(status);
    window.Content = root;
    window.Title = kind switch
    {
      ContentKind.Text => "VibeOCR 文字贴图",
      ContentKind.Color => "VibeOCR 颜色贴图",
      ContentKind.Formula => "VibeOCR 公式贴图",
      ContentKind.FileImage or ContentKind.FilePreview => "VibeOCR 图片贴图",
      _ => "VibeOCR 文本文件贴图",
    };
    presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
    presenter.IsAlwaysOnTop = true;
    window.AppWindow.SetPresenter(presenter);
    RectInt32 work = DisplayArea.GetFromWindowId(
      window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
    window.AppWindow.MoveAndResize(new RectInt32(
      work.X + 120, work.Y + 120, kind == ContentKind.Formula ? 640 : 360, kind == ContentKind.Formula ? 400 : 200));
    window.Closed += (_, _) => Dispose();
  }

  private Grid BuildContent(ContentKind kind)
  {
    var host = new Grid { Padding = new Thickness(8) };
    switch (kind)
    {
      case ContentKind.Text:
        editor.AcceptsReturn = true;
        editor.TextWrapping = TextWrapping.Wrap;
        editor.PlaceholderText = "输入文字内容；默认文字贴图";
        editor.FontSize = 16;
        host.Children.Add(editor);
        break;
      case ContentKind.Color:
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        swatch.Height = 56;
        swatch.CornerRadius = new CornerRadius(6);
        swatch.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
        swatch.BorderThickness = new Thickness(1);
        editor.PlaceholderText = "颜色值，如 #1F6FEB";
        editor.TextChanged += (_, _) => ApplySwatchColor();
        panel.Children.Add(swatch);
        panel.Children.Add(editor);
        host.Children.Add(panel);
        break;
      case ContentKind.Formula:
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editor.PlaceholderText = "输入 LaTeX（最多 4KiB）";
        editor.AcceptsReturn = true;
        host.Children.Add(editor);
        var render = new Button { Content = "渲染公式" };
        Grid.SetRow(render, 1);
        host.Children.Add(render);
        var preview = new Grid();
        Grid.SetRow(preview, 2);
        formulaView = new WebView2();
        preview.Children.Add(formulaView);
        image.Stretch = Stretch.Uniform;
        preview.Children.Add(image);
        host.Children.Add(preview);
        render.Click += async (_, _) =>
        {
          render.IsEnabled = false;
          try { await RenderFormulaAsync(); }
          finally { render.IsEnabled = true; }
        };
        break;
      case ContentKind.FilePreview:
        editor.AcceptsReturn = true;
        editor.TextWrapping = TextWrapping.Wrap;
        editor.IsReadOnly = true;
        host.Children.Add(new ScrollViewer { Content = editor });
        break;
      case ContentKind.FileImage:
        image.Stretch = Stretch.Uniform;
        host.Children.Add(image);
        break;
      default:
        editor.AcceptsReturn = true;
        editor.TextWrapping = TextWrapping.Wrap;
        editor.IsReadOnly = true;
        host.Children.Add(new ScrollViewer { Content = editor });
        break;
    }
    return host;
  }

  private void ApplySwatchColor()
  {
    if (kind != ContentKind.Color) return;
    string raw = editor.Text.Trim();
    if (raw.StartsWith('#') && raw.Length == 7 &&
      byte.TryParse(raw[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
      byte.TryParse(raw[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
      byte.TryParse(raw[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
    {
      swatch.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
      status.Text = "颜色有效";
    }
    else
    {
      status.Text = "请输入 #RRGGBB 颜色值";
    }
  }

  private void BuildBand()
  {
    band.Height = BandHeightDip;
    band.VerticalAlignment = VerticalAlignment.Top;
    band.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(96, 32, 32, 32));
    var buttons = new StackPanel
    {
      Orientation = Orientation.Horizontal,
      HorizontalAlignment = HorizontalAlignment.Right,
      VerticalAlignment = VerticalAlignment.Center,
    };
    AddBandButton(buttons, "关闭", () =>
    {
      window.Close();
      return Task.CompletedTask;
    });
    band.Children.Add(buttons);
    band.PointerPressed += OnBandPointerPressed;
    band.PointerMoved += OnBandPointerMoved;
    band.PointerReleased += OnBandPointerReleased;
    band.PointerCaptureLost += (_, _) => dragOrigin = null;
  }

  private Windows.Foundation.Point? dragOrigin;

  private void OnBandPointerPressed(object sender, PointerRoutedEventArgs args)
  {
    var point = args.GetCurrentPoint(band);
    if (!point.Properties.IsLeftButtonPressed) return;
    if (!GetCursorPos(out PointL origin)) return;
    dragOrigin = new Windows.Foundation.Point(origin.X, origin.Y);
    band.CapturePointer(args.Pointer);
    args.Handled = true;
  }

  private void OnBandPointerMoved(object sender, PointerRoutedEventArgs args)
  {
    if (dragOrigin is not { } origin || !GetCursorPos(out PointL current)) return;
    double deltaX = current.X - origin.X;
    double deltaY = current.Y - origin.Y;
    if (deltaX == 0 && deltaY == 0) return;
    dragOrigin = new Windows.Foundation.Point(current.X, current.Y);
    nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
    if (!GetWindowRect(handle, out RectL bounds)) return;
    SetWindowPos(handle, 0,
      checked(bounds.Left + (int)Math.Round(deltaX)),
      checked(bounds.Top + (int)Math.Round(deltaY)),
      0, 0, 0x0001 | 0x0004 | 0x0010);
    args.Handled = true;
  }

  private void OnBandPointerReleased(object sender, PointerRoutedEventArgs args)
  {
    if (dragOrigin is null) return;
    dragOrigin = null;
    band.ReleasePointerCapture(args.Pointer);
    args.Handled = true;
  }

  internal void SetText(string text)
  {
    editor.Text = text;
  }

  /// <summary>文件预览装载：仅授权图片（png/jpg）与有界纯文本（≤256KiB）；
  /// 其他格式/超限明确拒绝（窗口保持打开但无内容）。</summary>
  internal async Task LoadFileAsync(string path)
  {
    string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
    try
    {
      if (extension == ".txt")
      {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 256 * 1024)
        {
          status.Text = "文本文件超过 256KiB 上限，已拒绝预览";
          return;
        }
        byte[] bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes);
        using var text = new StreamReader(new MemoryStream(bytes), new System.Text.UTF8Encoding(false, true),
          detectEncodingFromByteOrderMarks: true);
        string content = await text.ReadToEndAsync();
        if (content.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
          throw new InvalidOperationException("文件包含二进制控制字节，仅支持纯文本。");
        editor.Text = content;
        kind = ContentKind.FileText;
        status.Text = "已装载文本预览";
        return;
      }
      status.Text = $"不支持的预览格式 {extension}；仅 png/jpg/txt";
    }
    catch (Exception error)
    {
      status.Text = $"文件预览失败：{error.Message}";
    }
  }

  internal async Task RenderFormulaAsync()
  {
    if (formulaView is null || createFormulaHost is null) return;
    try
    {
      formulaView.Visibility = Visibility.Visible;
      image.Visibility = Visibility.Collapsed;
      root.UpdateLayout();
      formulaHost ??= await createFormulaHost(formulaView);
      byte[] png = await formulaHost.RenderFormulaAsync(editor.Text);
      if (disposed) return;
      using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
      using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
      {
        writer.WriteBytes(png);
        await writer.StoreAsync();
      }
      stream.Seek(0);
      var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
      await bitmap.SetSourceAsync(stream);
      image.Source = bitmap;
      image.Visibility = Visibility.Visible;
      formulaView.Visibility = Visibility.Collapsed;
      status.Text = "公式已渲染；LaTeX 源保留，可修改后重新渲染。";
    }
    catch (Exception error)
    {
      image.Source = null;
      status.Text = $"公式渲染失败，源文本已保留：{error.Message}";
    }
  }

  internal PinSnapshotStore.Recovery RecoveryState() => new(PinId, kind.ToString(), editor.Text,
    window.AppWindow.Position.X, window.AppWindow.Position.Y, window.AppWindow.Size.Width, window.AppWindow.Size.Height);

  internal void Restore(PinSnapshotStore.Recovery recovery)
  {
    editor.Text = recovery.Content;
    window.AppWindow.MoveAndResize(new RectInt32(recovery.X, recovery.Y, recovery.Width, recovery.Height));
    if (kind == ContentKind.Color) ApplySwatchColor();
    if (kind == ContentKind.Formula) _ = RenderFormulaAsync();
  }

  internal void ShowError(string message) { if (!disposed) status.Text = message; }

  internal bool HasFormulaImage => image.Source is not null;
  internal string StatusText => status.Text;

  public void Show()
  {
    window.Activate();
    if (kind == ContentKind.Color) ApplySwatchColor();
  }

  public void Dispose()
  {
    if (disposed) return;
    disposed = true;
    if (formulaHost is { } host) _ = host.DisposeAsync();
    formulaView?.Close();
    Closed?.Invoke(this);
  }

  public void ForceClose() => window.Close();

  private void AddBandButton(Panel bar, string label, Func<Task> action)
  {
    var button = new Button
    {
      Content = label,
      FontSize = 12,
      MinWidth = 48,
      Padding = new Thickness(6, 2, 6, 2),
    };
    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
    button.Click += async (_, _) =>
    {
      try { await action(); }
      catch (Exception error)
      {
        VibeOCR.App.Services.AppLog.Error($"Typed pin action failed: {label}", error);
      }
    };
    bar.Children.Add(button);
  }

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetWindowRect(nint window, out RectL rect);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetCursorPos(out PointL point);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetWindowPos(
    nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

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
