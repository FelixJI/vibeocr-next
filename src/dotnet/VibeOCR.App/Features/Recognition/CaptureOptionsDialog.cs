using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// #236 原生截图选项对话框：延时秒数（1–60，默认 3）、包含光标（默认关）、
/// 窗口边框（默认保留可见边框）与固定区域坐标。保存时重读当前偏好、只
/// 合并选项字段（保留期间自动更新的上次区域）；NaN/无效数值回落默认；
/// IO 失败显示可见错误并保持窗口打开。内容放进 ScrollViewer，按内容尺
/// 寸自适应，不依赖固定像素。纯 WinUI 代码构建，不新增图标。
/// </summary>
internal sealed class CaptureOptionsDialog
{
  private readonly ICapturePreferencesStore _store;
  private readonly NumberBox _delay = new()
  {
    Header = "延时截图等待秒数（1–60）",
    Minimum = 1,
    Maximum = 60,
    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    SmallChange = 1,
    Width = 220,
  };
  private readonly ToggleSwitch _cursor = new()
  {
    Header = "截图包含鼠标光标",
    OnContent = "包含光标",
    OffContent = "不包含光标（默认）",
  };
  private readonly ComboBox _bounds = new() { Header = "活动窗口截图边框", Width = 300 };
  private readonly NumberBox _fixedX = FixedField("固定区域 X（像素）", int.MinValue);
  private readonly NumberBox _fixedY = FixedField("固定区域 Y（像素）", int.MinValue);
  private readonly NumberBox _fixedW = FixedField("固定区域宽（像素）");
  private readonly NumberBox _fixedH = FixedField("固定区域高（像素）");
  private readonly Button _save = new() { Content = "保存" };
  private readonly Button _cancel = new() { Content = "取消" };
  private readonly TextBlock _error = new()
  {
    Visibility = Visibility.Collapsed,
    TextWrapping = TextWrapping.Wrap,
    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
  };
  private Window? _window;

  internal CaptureOptionsDialog(ICapturePreferencesStore store) => _store = store;

  internal Window? Window => _window;
  internal NumberBox DelayInput => _delay;
  internal ToggleSwitch CursorInput => _cursor;
  internal ComboBox BoundsInput => _bounds;
  internal NumberBox FixedXInput => _fixedX;
  internal NumberBox FixedYInput => _fixedY;
  internal NumberBox FixedWInput => _fixedW;
  internal NumberBox FixedHInput => _fixedH;
  internal Button SaveButton => _save;
  internal Button CancelButton => _cancel;
  internal TextBlock ErrorText => _error;

  private static NumberBox FixedField(string header, int minimum = 0) => new()
  {
    Header = header,
    Minimum = minimum,
    AcceptsExpression = false,
    Width = 220,
  };

  public void Show(nint ownerWindow)
  {
    CapturePreferences preferences = _store.Load();
    _delay.Value = preferences.EffectiveDelaySeconds;
    _cursor.IsOn = preferences.IncludeCursor;
    _bounds.Items.Add("保留窗口可见边框（不向外扩展阴影）");
    _bounds.Items.Add("仅客户区（不含标题栏与边框）");
    _bounds.SelectedIndex =
        preferences.EffectiveWindowBoundsMode == ScreenTargetQuery.WindowBoundsClient ? 1 : 0;
    if (preferences.FixedRegion is { } fixedRegion)
    {
      _fixedX.Value = fixedRegion.X;
      _fixedY.Value = fixedRegion.Y;
      _fixedW.Value = fixedRegion.Width;
      _fixedH.Value = fixedRegion.Height;
    }

    AutomationProperties.SetName(_save, "保存截图选项");
    AutomationProperties.SetName(_cancel, "放弃修改");

    var fixedGrid = new Grid();
    fixedGrid.ColumnDefinitions.Add(new ColumnDefinition());
    fixedGrid.ColumnDefinitions.Add(new ColumnDefinition());
    fixedGrid.RowDefinitions.Add(new RowDefinition());
    fixedGrid.RowDefinitions.Add(new RowDefinition());
    Grid.SetColumn(_fixedX, 0);
    Grid.SetRow(_fixedX, 0);
    Grid.SetColumn(_fixedY, 1);
    Grid.SetRow(_fixedY, 0);
    Grid.SetColumn(_fixedW, 0);
    Grid.SetRow(_fixedW, 1);
    Grid.SetColumn(_fixedH, 1);
    Grid.SetRow(_fixedH, 1);
    fixedGrid.Children.Add(_fixedX);
    fixedGrid.Children.Add(_fixedY);
    fixedGrid.Children.Add(_fixedW);
    fixedGrid.Children.Add(_fixedH);
    var buttons = new StackPanel
    {
      Orientation = Orientation.Horizontal,
      Spacing = 12,
      HorizontalAlignment = HorizontalAlignment.Right,
    };
    buttons.Children.Add(_save);
    buttons.Children.Add(_cancel);
    var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
    panel.Children.Add(_delay);
    panel.Children.Add(_cursor);
    panel.Children.Add(_bounds);
    panel.Children.Add(new TextBlock
    {
      Text = "窗口截图不向边界外扩展系统阴影；全屏、显示器与区域截图保留桌面内的阴影。",
      TextWrapping = TextWrapping.Wrap,
    });
    panel.Children.Add(fixedGrid);
    panel.Children.Add(_error);
    panel.Children.Add(buttons);

    _window = new Window { Content = new ScrollViewer { Content = panel }, Title = "截图选项" };
    _cancel.Click += (_, _) => _window.Close();
    _save.Click += (_, _) => TrySave();

    OverlappedPresenter presenter = OverlappedPresenter.Create();
    presenter.IsResizable = true;
    _window.AppWindow.SetPresenter(presenter);
    // 按内容期望尺寸收缩窗口：不超过主工作区，长内容滚动。
    panel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
    double desiredWidth = Math.Min(480, Math.Max(360, panel.DesiredSize.Width + 48));
    double desiredHeight = Math.Min(640, panel.DesiredSize.Height + 48);
    _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(
        (int)desiredWidth, (int)desiredHeight));
    DisplayArea area = DisplayArea.Primary;
    _window.AppWindow.Move(new Windows.Graphics.PointInt32(
        area.WorkArea.X + Math.Max(0, (int)((area.WorkArea.Width - desiredWidth) / 2)),
        area.WorkArea.Y + Math.Max(0, (int)((area.WorkArea.Height - desiredHeight) / 2))));
    _window.Activate();
  }

  /// <summary>
  /// 保存当前输入。失败（IO/非法）显示可见错误并保持窗口打开，不丢
  /// 待编辑选项；测试经 <see cref="TrySave"/> 直接驱动同一逻辑。
  /// </summary>
  internal string? TrySave()
  {
    int delay = (int)Normalize(_delay.Value, fallback: 3, min: 1, max: 60);
    _delay.Value = delay;
    PhysicalRectangle? fixedRegion = null;
    if (HasValue(_fixedX) || HasValue(_fixedY) || HasValue(_fixedW) || HasValue(_fixedH))
    {
      int x = (int)Normalize(_fixedX.Value, fallback: 0, min: int.MinValue, max: int.MaxValue);
      int y = (int)Normalize(_fixedY.Value, fallback: 0, min: int.MinValue, max: int.MaxValue);
      int width = (int)Normalize(_fixedW.Value, fallback: 0, min: 0, max: int.MaxValue);
      int height = (int)Normalize(_fixedH.Value, fallback: 0, min: 0, max: int.MaxValue);
      if (width <= 0 || height <= 0)
      {
        return ShowError("固定区域的宽与高必须为正数（像素）。");
      }

      fixedRegion = new PhysicalRectangle(x, y, width, height);
    }

    try
    {
      // 保存时重读：对话框打开期间自动更新的上次区域不得被旧快照覆盖。
      CapturePreferences current = _store.Load();
      _store.Save(current with
      {
        DelaySeconds = delay,
        IncludeCursor = _cursor.IsOn,
        WindowBoundsMode = _bounds.SelectedIndex == 1
              ? ScreenTargetQuery.WindowBoundsClient
              : ScreenTargetQuery.WindowBoundsFrame,
        FixedRegion = fixedRegion,
      });
    }
    catch (Exception error) when (
        error is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
      return ShowError($"保存截图选项失败：{error.Message}");
    }

    _error.Visibility = Visibility.Collapsed;
    _window?.Close();
    return null;
  }

  private string? ShowError(string message)
  {
    _error.Text = message;
    _error.Visibility = Visibility.Visible;
    return message;
  }

  private static bool HasValue(NumberBox box) =>
      !double.IsNaN(box.Value);

  private static double Normalize(double value, double fallback, double min, double max)
  {
    if (double.IsNaN(value) || double.IsInfinity(value))
    {
      return Math.Clamp(fallback, min, max);
    }

    return Math.Clamp(Math.Round(value), min, max);
  }
}
