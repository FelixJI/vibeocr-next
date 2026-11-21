using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Windows;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace VibeOCR.App.Features.FloatingToolbar;

internal enum FloatingToolbarCommand
{
  CaptureScreenshot,
  ScreenshotEdit,
  ClipboardRecognize,
  ShowMainWindow,
  OpenSettings,
  DismissToolbar,
}

/// <summary>
/// 悬浮工具栏窗口：无边框、置顶、不进任务栏/Alt+Tab；扩展样式声明
/// WS_EX_NOACTIVATE（窗口级不激活，点击不成为前台，同贴边感应条先例），
/// 子类化拦截 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE，显示走
/// SW_SHOWNOACTIVATE，悬停、点击与拖动都不抢前台焦点。左侧把手拖动重定位，
/// 松手由控制器判定贴边吸附。
/// 主题经 root RequestedTheme 走原生 ThemeResource：图标与背景使用同次
/// 主题解析，按钮保留 Fluent 悬停态，高对比时回落系统可见资源；系统变化仅在
/// 窗口生命周期内订阅一次并封送 UI 线程，不修改用户系统主题。
/// </summary>
internal sealed class FloatingToolbarWindow : IFloatingToolbarView
{
  internal const double DesignWidthDip = 272;
  internal const double DesignHeightDip = 44;
  private const double ButtonSizeDip = 36;
  private const double GripWidthDip = 24;
  private const uint WmMouseActivate = 0x0021;
  private const nint MaNoActivate = 3;
  private const uint SwHide = 0;
  private const uint SwShowNoActivate = 4;
  private const uint SwpNoSize = 0x0001;
  private const uint SwpNoZOrder = 0x0004;
  private const uint SwpNoActivate = 0x0010;
  private const int VkLButton = 0x01;
  // 命令触发/失败脉冲时长：长于两轮轮询（100ms×2）保证视觉可感知，
  // 短于 linger 收起窗口，不与截图避让的隐藏竞争。
  private static readonly TimeSpan CommandPulseDuration = TimeSpan.FromMilliseconds(280);

  private readonly Window _window = new();
  private readonly ContentControl _gripIcon = ScreenshotToolbarIcons.CreateIcon("grip");
  private readonly Border _grip;
  private readonly Grid _root = new();
  private readonly List<Button> _commandButtons = [];
  private readonly Popup _popup = new()
  {
    ShouldConstrainToRootBounds = false,
    IsLightDismissEnabled = false,
    HorizontalAlignment = HorizontalAlignment.Left,
    VerticalAlignment = VerticalAlignment.Top,
    HorizontalOffset = 0,
    VerticalOffset = 0,
  };
  private readonly Grid _host = new();
  private readonly UISettings _uiSettings = new();
  private readonly ThemeSettings _themeSettings;
  private readonly WindowMessageService _messages;
  // Windowed Popup 的内容宿主是独立 HWND：主窗口的 NOACTIVATE 样式
  // 覆盖不到它，点击瞬间会把前台抢给 Popup 宿主。每次 Popup 打开后
  // 枚举子窗口，为内容桥接 HWND 补同样样式（Popup HWND 可能重建）。
  private readonly List<nint> _popupHosts = [];
  private readonly nint _handle;
  private Windows.Foundation.Point? _dragOrigin;
  private FloatingToolbarTheme _theme = FloatingToolbarTheme.System;
  private bool _disposed;
  private bool _popupOpenPending;
  private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _pointerTimer;
  private bool? _pointerOver;
  // 命令反馈：每按钮的脉冲截止时刻（Environment.TickCount64 基准），
  // 期间轮询不得覆盖该按钮视觉；到期由轮询恢复常态并清理背景。
  private readonly long[] _commandPulseUntil = new long[6];
  private readonly bool[] _commandPulseFailure = new bool[6];
  // 右键菜单保持：菜单模态期间不触发 PointerExited（防 linger 收起），
  // 整栏边框强调；菜单关闭（本线程 hwndMenu 消失）后自动解除。
  private bool _menuHold;

  public event EventHandler? PointerEntered;

  public event EventHandler? PointerExited;

  public event EventHandler<PhysicalRectangle>? DragStarted;

  public event EventHandler<PhysicalRectangle>? DragCompleted;

  public event EventHandler<FloatingToolbarCommand>? CommandInvoked;

  /// <summary>
  /// #236 右键请求统一截图模式菜单（与托盘/热键同一菜单）；不改变任何
  /// 既有按钮行为。
  /// </summary>
  public event EventHandler? ModeMenuRequested;

  public bool IsVisible { get; private set; }

  public bool IsPointerOver
  {
    get
    {
      if (!IsVisible || !GetCursorPos(out PointL point)) return false;
      PhysicalRectangle bounds = GetBounds();
      return point.X >= bounds.X && point.X < bounds.Right
        && point.Y >= bounds.Y && point.Y < bounds.Bottom;
    }
  }

  /// <summary>原生窗口句柄，供自检注入消息。</summary>
  public nint Handle => _handle;

  public FloatingToolbarWindow()
  {
    _grip = BuildGrip();
    BuildContent();
    OverlappedPresenter presenter = OverlappedPresenter.Create();
    presenter.SetBorderAndTitleBar(false, false);
    presenter.IsAlwaysOnTop = true;
    presenter.IsResizable = false;
    _window.AppWindow.SetPresenter(presenter);
    _window.AppWindow.IsShownInSwitchers = false;
    _handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
    _themeSettings = ThemeSettings.CreateForWindowId(_window.AppWindow.Id);
    AddToolWindowStyle();
    _messages = new WindowMessageService(_handle);
    _messages.MessageHandled += OnMessageHandled;
    // Popup 的 XAML 进入/离开事件可能缺失或来自子控件；仅可见期间校验
    // 真实屏幕坐标，隐藏、截图避让及释放时停止，不在后台轮询。同一轮询
    // 顺带驱动按钮 PointerOver/Normal 视觉状态：windowed Popup + NOACTIVATE
    // 组合下 XAML 悬停状态更新不可靠，鼠标与按钮反馈由坐标权威兜底。
    _pointerTimer = _window.DispatcherQueue.CreateTimer();
    _pointerTimer.Interval = TimeSpan.FromMilliseconds(100);
    _pointerTimer.Tick += (_, _) =>
    {
        UpdatePointerPresence();
        UpdateButtonHoverStates();
    };
    // 主题跟随：窗口生命周期内各订阅一次；系统线程回调封送 UI 线程。
    _root.ActualThemeChanged += OnRootActualThemeChanged;
    _themeSettings.Changed += OnSystemHighContrastChanged;
    // Window 创建后保持隐藏，显示一律走 ShowAt。
    _window.AppWindow.Hide();
  }

  public PhysicalRectangle GetPreferredSize()
  {
    uint dpi = GetDpiForWindow(_handle);
    if (dpi == 0)
    {
      dpi = 96;
    }

    double scale = dpi / 96.0;
    return new PhysicalRectangle(
        0,
        0,
        (int)Math.Ceiling(DesignWidthDip * scale),
        (int)Math.Ceiling(DesignHeightDip * scale));
  }

  public PhysicalRectangle GetBounds()
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    PointL origin = new();
    if (!ClientToScreen(_handle, ref origin) || !GetClientRect(_handle, out RectL rect)
        || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
    {
      throw new InvalidOperationException("Failed to query the toolbar client bounds.");
    }

    return new PhysicalRectangle(
        origin.X,
        origin.Y,
        rect.Right - rect.Left,
        rect.Bottom - rect.Top);
  }

  public void ShowAt(PhysicalRectangle bounds)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _window.AppWindow.MoveAndResize(new RectInt32(
        bounds.X,
        bounds.Y,
        bounds.Width,
        bounds.Height));
    _window.AppWindow.ResizeClient(new SizeInt32(bounds.Width, bounds.Height));
    PointL origin = new();
    if (!ClientToScreen(_handle, ref origin))
    {
      throw new InvalidOperationException("Failed to locate the toolbar client origin.");
    }

    PointInt32 position = _window.AppWindow.Position;
    _window.AppWindow.Move(new PointInt32(
        checked(position.X + bounds.X - origin.X),
        checked(position.Y + bounds.Y - origin.Y)));
    ShowWindow(_handle, SwShowNoActivate);
    IsVisible = true;
    _pointerOver = null;
    _pointerTimer.Start();
    OpenPopupWhenHostReady();
  }

  /// <summary>
  /// Popup 必须绑定宿主 XamlRoot 才能打开：冷启动（OnLaunched 同步
  /// ShowDocked）时窗口内容尚未 Loaded、XamlRoot 为 null，直接设
  /// IsOpen 会抛 0x8000FFFF「unparented popup」。未就绪时等宿主一次
  /// 真实 Loaded 再开（同 SyntheticScreenRegionPicker 先例）；内容
  /// 已加载的常规揭示/恢复路径保持原有立即打开。
  /// </summary>
  private void OpenPopupWhenHostReady()
  {
    if (TryOpenPopup())
    {
      return;
    }

    if (!_popupOpenPending)
    {
      _popupOpenPending = true;
      _host.Loaded += OnHostLoadedOpenPopup;
    }
  }

  private bool TryOpenPopup()
  {
    if (_popup.XamlRoot is null && _host.XamlRoot is { } hostRoot)
    {
      _popup.XamlRoot = hostRoot;
    }

    if (_popup.XamlRoot is null)
    {
      return false;
    }

    _popup.IsOpen = true;
    ApplyNoActivateToPopupHosts();
    return true;
  }

  /// <summary>
  /// Popup 打开后枚举主窗口子窗口，只为 XAML 内容桥接 HWND 补
  /// WS_EX_NOACTIVATE 样式：修复点击 Popup 承载的按钮瞬间桥接宿主被
  /// 激活、前台被抢的焦点异常。样式足以让点击不激活；消息子类化会
  /// 干扰桥接/输入路由窗口的合成路径（曾致首帧白屏与启动 fastfail），
  /// 一律不做，输入路由窗口也绝不动。
  /// </summary>
  private void ApplyNoActivateToPopupHosts()
  {
    EnumChildWindows(_handle, (child, _) =>
    {
      if (child == _handle || _popupHosts.Contains(child)) return true;
      if (!IsContentBridgeWindow(child)) return true;
      const int GwlExStyle = -20;
      const long WsExNoActivate = 0x08000000;
      long style = GetWindowLongPtrW(child, GwlExStyle);
      SetWindowLongPtrW(child, GwlExStyle, (nint)(style | WsExNoActivate));
      _popupHosts.Add(child);
      return true;
    }, nint.Zero);
  }

  private static bool IsContentBridgeWindow(nint window)
  {
    // DllImport 不能 marshal Span<char>（EnumChildWindows 的 native 回调里
    // 抛 MarshalDirectiveException 会 fastfail 整个进程），用 char[]。
    char[] buffer = new char[128];
    int length = GetClassName(window, buffer, buffer.Length);
    if (length <= 0) return false;
    string name = new(buffer, 0, length);
    return name is "Microsoft.UI.Content.DesktopChildSiteBridge"
        or "Windows.UI.Composition.DesktopWindowContentBridge";
  }

  private void OnHostLoadedOpenPopup(object sender, RoutedEventArgs args)
  {
    CancelPendingPopupOpen();
    // 等待期间 Hide/Dispose 已发生则不再迟到重开。
    if (_disposed || !IsVisible)
    {
      return;
    }

    TryOpenPopup();
  }

  /// <summary>取消尚未发生的首次打开，并退订等待：不重复订阅、不迟到重开。</summary>
  private void CancelPendingPopupOpen()
  {
    if (!_popupOpenPending)
    {
      return;
    }

    _popupOpenPending = false;
    _host.Loaded -= OnHostLoadedOpenPopup;
  }

  public void Hide()
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _pointerTimer.Stop();
    _pointerOver = null;
    SetMenuHold(false);
    // 隐藏瞬间按钮可能停在 PointerOver/Pressed 或命令脉冲：popup 关闭会
    // 丢弃待发的指针事件，避让重开后残留冻结态；这里显式归位并清脉冲。
    for (int index = 0; index < _commandButtons.Count; index++)
    {
      _commandPulseUntil[index] = 0;
      _commandPulseFailure[index] = false;
      _commandButtons[index].Background = new SolidColorBrush(
          Windows.UI.Color.FromArgb(0, 0, 0, 0));
      VisualStateManager.GoToState(_commandButtons[index], "Normal", useTransitions: false);
    }
    CancelPendingPopupOpen();
    _popup.IsOpen = false;
    ShowWindow(_handle, SwHide);
    IsVisible = false;
  }

  /// <summary>
  /// 应用用户主题偏好：System 走原生默认（跟随系统 light/dark，且天然
  /// 使用高对比资源），Light/Dark 固定元素主题、不随系统变化覆盖；高对比
  /// 激活时一律回落 Default 以保住系统可见资源。纯视觉更新，不显示窗口、
  /// 不抢焦点、不影响 no-activate/任务栏/Alt+Tab 行为。
  /// </summary>
  public void ApplyTheme(FloatingToolbarTheme theme)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _theme = theme;
    ApplyResolvedTheme();
  }

  /// <summary>按当前设置与系统状态重涂：RequestedTheme + 铬色与图标前景。</summary>
  private void ApplyResolvedTheme()
  {
    if (_disposed)
    {
      return;
    }

    bool highContrast = IsSystemHighContrast();
    _root.RequestedTheme = ResolveRequestedTheme(_theme, highContrast);
    Windows.UI.Color background;
    Windows.UI.Color foreground;
    if (highContrast)
    {
      // 高对比：直接取系统可见的前景/背景配对。
      background = _uiSettings.GetColorValue(UIColorType.Background);
      foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
    }
    else if (_root.ActualTheme == ElementTheme.Dark)
    {
      (background, foreground) = ResolveChromeColors(dark: true);
    }
    else
    {
      (background, foreground) = ResolveChromeColors(dark: false);
    }

    _root.Background = new SolidColorBrush(background);
    var foregroundBrush = new SolidColorBrush(foreground);
    _gripIcon.Foreground = foregroundBrush;
    foreach (Button button in _commandButtons)
    {
      // 图标绑定自身 Foreground，须与背景使用同一次主题解析结果。
      if (button.Content is ContentControl icon)
      {
        icon.Foreground = foregroundBrush;
      }
    }
  }

  private bool IsSystemHighContrast() =>
      _themeSettings.HighContrast;

  /// <summary>
  /// System 跟随系统；手动 Light/Dark 固定不被系统 light/dark 覆盖；
  /// 高对比优先回落系统资源（Default）。
  /// </summary>
  internal static ElementTheme ResolveRequestedTheme(
      FloatingToolbarTheme theme,
      bool highContrast)
  {
    if (highContrast || theme == FloatingToolbarTheme.System)
    {
      return ElementTheme.Default;
    }

    return theme == FloatingToolbarTheme.Light
        ? ElementTheme.Light
        : ElementTheme.Dark;
  }

  /// <summary>非高对比铬色：深色沿用既有值，浅色提供可辨浅底深字。</summary>
  internal static (Windows.UI.Color Background, Windows.UI.Color Foreground) ResolveChromeColors(
      bool dark) => dark
      ? (Windows.UI.Color.FromArgb(238, 24, 24, 24), Windows.UI.Color.FromArgb(255, 235, 235, 235))
      : (Windows.UI.Color.FromArgb(238, 249, 249, 249), Windows.UI.Color.FromArgb(255, 36, 36, 36));

  private void OnRootActualThemeChanged(FrameworkElement sender, object args)
  {
    // 系统 light/dark 翻转（仅 System 模式会改变 ActualTheme）：原生
    // UI 线程回调，重涂铬色；手动主题下 ActualTheme 不变、不触发。
    ApplyResolvedTheme();
  }

  private void OnSystemHighContrastChanged(ThemeSettings sender, object args)
  {
    // 系统线程回调：封送 UI 线程；已释放或入队失败时安全跳过，
    // 绝不显示窗口、不抢焦点。
    if (_disposed)
    {
      return;
    }

    _ = _window.DispatcherQueue.TryEnqueue(ApplyResolvedTheme);
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _pointerTimer.Stop();
    CancelPendingPopupOpen();
    _popup.IsOpen = false;
    _themeSettings.Changed -= OnSystemHighContrastChanged;
    _root.ActualThemeChanged -= OnRootActualThemeChanged;
    _messages.MessageHandled -= OnMessageHandled;
    _messages.Dispose();
    _popupHosts.Clear();
    _popup.Child = null;
    _window.Close();
  }

  private Border BuildGrip()
  {
    _gripIcon.Opacity = 0.7;
    return new Border
    {
      Width = GripWidthDip,
      Height = ButtonSizeDip,
      VerticalAlignment = VerticalAlignment.Center,
      Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
      CornerRadius = new CornerRadius(6),
      Child = _gripIcon,
    };
  }

  private void BuildContent()
  {
    _root.Width = DesignWidthDip;
    _root.Height = DesignHeightDip;
    // 初始铬色沿用既有深色，ApplyTheme 在首次显示前按设置重涂。
    _root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(238, 24, 24, 24));
    var bar = new StackPanel
    {
      Orientation = Orientation.Horizontal,
      VerticalAlignment = VerticalAlignment.Center,
      HorizontalAlignment = HorizontalAlignment.Center,
    };
    bar.Children.Add(_grip);
    bar.Children.Add(CreateCommandButton(
        "capture", "截图识别", FloatingToolbarCommand.CaptureScreenshot));
    bar.Children.Add(CreateCommandButton(
        "editImage", "截图编辑", FloatingToolbarCommand.ScreenshotEdit));
    bar.Children.Add(CreateCommandButton(
        "clipboard", "剪贴板识别", FloatingToolbarCommand.ClipboardRecognize));
    bar.Children.Add(CreateCommandButton(
        "workbench", "显示工作台", FloatingToolbarCommand.ShowMainWindow));
    bar.Children.Add(CreateCommandButton(
        "settings", "设置", FloatingToolbarCommand.OpenSettings));
    bar.Children.Add(CreateCommandButton(
        "exit", "隐藏悬浮栏", FloatingToolbarCommand.DismissToolbar));
    _root.Children.Add(bar);

    // 右键 = 截图模式菜单入口（#236）；不拦截按钮/把手的左键语义。
    // 菜单保持先于事件：整栏立即进入强调态，菜单模态期间不因鼠标离开
    // 触发 linger 收起，菜单关闭后由轮询解除并恢复正常停留计时。
    _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
    {
      if (args.GetCurrentPoint(_root).Properties.IsRightButtonPressed)
      {
        SetMenuHold(true);
        ModeMenuRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
      }
    }), true);
    _root.PointerEntered += (_, _) => UpdatePointerPresence();
    _root.PointerExited += (_, _) => UpdatePointerPresence();
    _grip.PointerPressed += OnGripPointerPressed;
    _grip.PointerMoved += OnGripPointerMoved;
    _grip.PointerReleased += OnGripPointerReleased;
    _grip.PointerCaptureLost += OnGripPointerEnded;
    // Windowed Popup preserves the foreground while receiving pointer input.
    _popup.Child = _root;
    _host.Children.Add(_popup);
    _window.Content = _host;
  }

  private Button CreateCommandButton(string iconName, string tooltip, FloatingToolbarCommand command)
  {
    var button = new HoverHandButton
    {
      Width = ButtonSizeDip,
      Height = ButtonSizeDip,
      Margin = new Thickness(2, 0, 2, 0),
      Padding = new Thickness(0),
      Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
      BorderThickness = new Thickness(0),
      AllowFocusOnInteraction = false,
      Content = ScreenshotToolbarIcons.CreateIcon(iconName),
    };
    AutomationProperties.SetName(button, tooltip);
    ToolTipService.SetToolTip(button, tooltip);
    // 点击即时反馈先于命令分派：按钮立即进入“已触发”脉冲，命令成败
    // 之后由装配层补成功/失败视觉；不等外部窗口出现才可感知。
    button.Click += (_, _) =>
    {
      PulseCommand(command);
      CommandInvoked?.Invoke(this, command);
    };
    _commandButtons.Add(button);
    return button;
  }

  /// <summary>
  /// 悬停显示手型光标的命令按钮。ProtectedCursor 在 WinUI 3 是 protected，
  /// 不能从外部按按钮设置（同 ScreenRegionPicker 先例）；截屏选区工具栏
  /// 已确立“悬停工具栏按钮显示手型”的用户偏好，悬浮工具栏保持一致。
  /// </summary>
  private sealed class HoverHandButton : Button
  {
    public HoverHandButton() =>
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
  }

  /// <summary>
  /// 轮询驱动的按钮视觉：windowed Popup 承载内容 + NOACTIVATE 窗口
  /// 组合下 XAML 的 PointerOver/Pressed 状态更新不可靠（悬停无高亮、
  /// 按下无反馈、避让重开后残留按下态），以真实光标坐标与左键状态为
  /// 权威，按按钮强制切换 CommonStates；命令脉冲期间的按钮不被覆盖。
  /// </summary>
  private void UpdateButtonHoverStates()
  {
    if (_disposed || !IsVisible || _commandButtons.Count == 0) return;
    double scale = GetDpiForWindow(_handle) / 96.0;
    if (scale <= 0) scale = 1;
    if (!GetCursorPos(out PointL point)) return;
    PhysicalRectangle bounds = GetBounds();
    // 光标 → 窗口客户区 DIP 坐标；按钮矩形经 TransformToVisual 投影到
    // 同一 _root 坐标系（popup 偏移为 0，客户区即 _root）。
    double x = (point.X - bounds.X) / scale;
    double y = (point.Y - bounds.Y) / scale;
    Windows.Foundation.Rect[] rects = _commandButtons
        .Select(button => button.TransformToVisual(_root).TransformBounds(
            new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight)))
        .ToArray();
    int hovered = HitTestButton(rects, x, y);
    bool pressed = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
    long now = Environment.TickCount64;
    for (int index = 0; index < _commandButtons.Count; index++)
    {
      if (index < _commandPulseUntil.Length && now < _commandPulseUntil[index]) continue;
      if (index < _commandPulseUntil.Length && _commandPulseUntil[index] != 0)
      {
        // 脉冲到期（成功或失败）：清背景回透明并复位标记，再进入常规判定。
        _commandPulseUntil[index] = 0;
        _commandPulseFailure[index] = false;
        _commandButtons[index].Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
      }
      VisualStateManager.GoToState(
          _commandButtons[index],
          pressed && index == hovered ? "Pressed"
          : index == hovered ? "PointerOver"
          : "Normal",
          useTransitions: true);
    }
  }

  /// <summary>
  /// 命令触发脉冲：按钮立即进入按下态并短暂着前景色淡底（成功语义的
  /// “已触发”反馈），脉冲期内轮询不覆盖、重复触发顺延；截图类命令随
  /// 后由避让隐藏接管，其余命令到期由轮询恢复常态。
  /// </summary>
  internal void PulseCommand(FloatingToolbarCommand command)
  {
    if (_disposed) return;
    int index = CommandIndex(command);
    if (index < 0 || index >= _commandButtons.Count) return;
    _commandPulseFailure[index] = false;
    _commandPulseUntil[index] = Environment.TickCount64 +
        (long)CommandPulseDuration.TotalMilliseconds;
    _commandButtons[index].Background = new SolidColorBrush(
        Windows.UI.Color.FromArgb(52, CurrentForeground().R, CurrentForeground().G, CurrentForeground().B));
    VisualStateManager.GoToState(_commandButtons[index], "Pressed", useTransitions: true);
  }

  /// <summary>命令失败脉冲：按钮短暂失败红底（异常已由装配层记录）。</summary>
  internal void FlashCommandFailure(FloatingToolbarCommand command)
  {
    if (_disposed) return;
    int index = CommandIndex(command);
    if (index < 0 || index >= _commandButtons.Count) return;
    _commandPulseFailure[index] = true;
    _commandPulseUntil[index] = Environment.TickCount64 +
        (long)CommandPulseDuration.TotalMilliseconds;
    _commandButtons[index].Background = new SolidColorBrush(
        Windows.UI.Color.FromArgb(150, 196, 43, 28));
    VisualStateManager.GoToState(_commandButtons[index], "Pressed", useTransitions: true);
  }

  private Windows.UI.Color CurrentForeground()
  {
    if (_gripIcon.Foreground is SolidColorBrush gripBrush) return gripBrush.Color;
    return Windows.UI.Color.FromArgb(255, 36, 36, 36);
  }

  /// <summary>命令到按钮下标的固定映射（BuildContent 添加顺序）：命令
  /// 脉冲/失败反馈按下标定位按钮，顺序契约由 HoverTests 锁定。</summary>
  internal static int CommandIndex(FloatingToolbarCommand command) => command switch
  {
    FloatingToolbarCommand.CaptureScreenshot => 0,
    FloatingToolbarCommand.ScreenshotEdit => 1,
    FloatingToolbarCommand.ClipboardRecognize => 2,
    FloatingToolbarCommand.ShowMainWindow => 3,
    FloatingToolbarCommand.OpenSettings => 4,
    FloatingToolbarCommand.DismissToolbar => 5,
    _ => -1,
  };

  /// <summary>
  /// 右键菜单保持视觉：整栏描边强调（菜单已展开的即时反馈），解除时
  /// 恢复无边框；与主题重涂互不冲突（仅动边框，不动背景/前景）。
  /// </summary>
  private void SetMenuHold(bool hold)
  {
    if (_menuHold == hold) return;
    _menuHold = hold;
    Windows.UI.Color accent = CurrentForeground();
    _root.BorderThickness = hold ? new Thickness(1) : default;
    _root.BorderBrush = hold
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(200, accent.R, accent.G, accent.B))
        : null;
    _root.CornerRadius = hold ? new CornerRadius(6) : new CornerRadius(0);
  }

  /// <summary>按钮矩形命中判定（纯函数供测试）：返回命中下标，未命中为 -1。</summary>
  internal static int HitTestButton(Windows.Foundation.Rect[] rects, double x, double y)
  {
    for (int index = 0; index < rects.Length; index++)
    {
      Windows.Foundation.Rect rect = rects[index];
      if (x >= rect.X && x < rect.X + rect.Width &&
          y >= rect.Y && y < rect.Y + rect.Height)
      {
        return index;
      }
    }
    return -1;
  }

  private void UpdatePointerPresence()
  {
    if (_disposed || !IsVisible) return;
    if (_menuHold)
    {
      // 菜单模态期间视为持续停留：不触发 PointerExited，防 linger 收起；
      // 本线程菜单窗口消失即菜单关闭，解除保持并回到真实光标判定。
      if (ThreadHasOpenMenu()) return;
      SetMenuHold(false);
    }
    bool over = IsPointerOver;
    if (_pointerOver == over) return;
    _pointerOver = over;
    if (over) PointerEntered?.Invoke(this, EventArgs.Empty);
    else PointerExited?.Invoke(this, EventArgs.Empty);
  }

  private static bool ThreadHasOpenMenu()
  {
    var info = GuiThreadInfo.Default;
    return GetGUIThreadInfo(0 /* 当前线程 */, ref info) && info.hwndMenuOwner != 0;
  }

  private void OnGripPointerPressed(object sender, PointerRoutedEventArgs args)
  {
    if (!args.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed
        || !TryGetCursorLocation(out Windows.Foundation.Point origin))
    {
      return;
    }

    _dragOrigin = origin;
    _grip.CapturePointer(args.Pointer);
    DragStarted?.Invoke(this, GetBounds());
    args.Handled = true;
  }

  private void OnGripPointerMoved(object sender, PointerRoutedEventArgs args)
  {
    if (_dragOrigin is not { } origin || !TryGetCursorLocation(out Windows.Foundation.Point current))
    {
      return;
    }

    double deltaX = current.X - origin.X;
    double deltaY = current.Y - origin.Y;
    if (deltaX == 0 && deltaY == 0)
    {
      return;
    }

    _dragOrigin = current;
    PointInt32 position = _window.AppWindow.Position;
    SetWindowPos(
        _handle,
        0,
        checked(position.X + (int)Math.Round(deltaX)),
        checked(position.Y + (int)Math.Round(deltaY)),
        0,
        0,
        SwpNoSize | SwpNoZOrder | SwpNoActivate);
    args.Handled = true;
  }

  private void OnGripPointerReleased(object sender, PointerRoutedEventArgs args)
  {
    if (_dragOrigin is null)
    {
      return;
    }

    _dragOrigin = null;
    _grip.ReleasePointerCapture(args.Pointer);
    DragCompleted?.Invoke(this, GetBounds());
    args.Handled = true;
  }

  private void OnGripPointerEnded(object sender, PointerRoutedEventArgs args)
  {
    // 捕获丢失（Esc/窗口切换）视作拖动结束，交给控制器按当前位置判定。
    if (_dragOrigin is null)
    {
      return;
    }

    _dragOrigin = null;
    DragCompleted?.Invoke(this, GetBounds());
  }

  private nint? OnMessageHandled(WindowMessage message) =>
      message.Id == WmMouseActivate ? MaNoActivate : null;

  private void AddToolWindowStyle()
  {
    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x00000080;
    // WS_EX_NOACTIVATE：窗口级不激活，点击/拖动不把工具栏提为前台
    // （同 EdgeSensor 先例），补齐 show/move 参数与 MA_NOACTIVATE 的语义。
    const long WsExNoActivate = 0x08000000;
    long style = GetWindowLongPtrW(_handle, GwlExStyle);
    SetWindowLongPtrW(_handle, GwlExStyle, (nint)(style | WsExToolWindow | WsExNoActivate));
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

  [DllImport("user32.dll")]
  private static extern uint GetDpiForWindow(nint window);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetClientRect(nint window, out RectL rect);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ClientToScreen(nint window, ref PointL point);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ShowWindow(nint window, uint command);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetWindowPos(
      nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

  [DllImport("user32.dll")]
  private static extern nint GetWindowLongPtrW(nint window, int index);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetCursorPos(out PointL point);

  [DllImport("user32.dll")]
  private static extern short GetAsyncKeyState(int virtualKey);

  private delegate bool EnumChildWindowsProc(nint window, nint lParam);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool EnumChildWindows(
      nint window, EnumChildWindowsProc callback, nint lParam);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

  [DllImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

  [StructLayout(LayoutKind.Sequential)]
  private struct GuiThreadInfo
  {
    public int cbSize;
    public uint flags;
    public nint hwndActive;
    public nint hwndFocus;
    public nint hwndCapture;
    public nint hwndMenuOwner;
    public nint hwndMoveSize;
    public nint hwndCaret;
    public int caretLeft;
    public int caretTop;
    public int caretRight;
    public int caretBottom;
    public static GuiThreadInfo Default => new() { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
  }

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
