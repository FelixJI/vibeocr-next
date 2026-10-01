using System.Runtime.InteropServices;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
/// 主题经 root RequestedTheme 走原生 ThemeResource：命令按钮/图标继承
/// Fluent 主题化前景与悬停态，高对比时回落系统可见资源；系统变化仅在
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

    private readonly Window _window = new();
    private readonly FontIcon _gripIcon = new()
    {
        Glyph = "\uE700",
        FontSize = 12,
        Opacity = 0.7,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 235, 235)),
    };
    private readonly Border _grip;
    private readonly Grid _root = new();
    private readonly UISettings _uiSettings = new();
    private readonly ThemeSettings _themeSettings;
    private readonly WindowMessageService _messages;
    private readonly nint _handle;
    private Windows.Foundation.Point? _dragOrigin;
    private FloatingToolbarTheme _theme = FloatingToolbarTheme.System;
    private bool _disposed;

    public event EventHandler? PointerEntered;

    public event EventHandler? PointerExited;

    public event EventHandler<PhysicalRectangle>? DragStarted;

    public event EventHandler<PhysicalRectangle>? DragCompleted;

    public event EventHandler<FloatingToolbarCommand>? CommandInvoked;

    public bool IsVisible { get; private set; }

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
        if (!GetWindowRect(_handle, out RectL rect))
        {
            throw new InvalidOperationException("Failed to query the toolbar window bounds.");
        }

        return new PhysicalRectangle(
            rect.Left,
            rect.Top,
            Math.Max(1, rect.Right - rect.Left),
            Math.Max(1, rect.Bottom - rect.Top));
    }

    public void ShowAt(PhysicalRectangle bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.AppWindow.MoveAndResize(new RectInt32(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height));
        ShowWindow(_handle, SwShowNoActivate);
        IsVisible = true;
    }

    public void Hide()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    /// <summary>按当前设置与系统状态重涂：RequestedTheme + 少量铬色。</summary>
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
        _gripIcon.Foreground = new SolidColorBrush(foreground);
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
        _themeSettings.Changed -= OnSystemHighContrastChanged;
        _root.ActualThemeChanged -= OnRootActualThemeChanged;
        _messages.MessageHandled -= OnMessageHandled;
        _messages.Dispose();
        _window.Close();
    }

    private Border BuildGrip()
    {
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
            "\uE722", "截图识别", FloatingToolbarCommand.CaptureScreenshot));
        bar.Children.Add(CreateCommandButton(
            "\uE70F", "截图编辑", FloatingToolbarCommand.ScreenshotEdit));
        bar.Children.Add(CreateCommandButton(
            "\uE77F", "剪贴板识别", FloatingToolbarCommand.ClipboardRecognize));
        bar.Children.Add(CreateCommandButton(
            "\uE8A7", "显示工作台", FloatingToolbarCommand.ShowMainWindow));
        bar.Children.Add(CreateCommandButton(
            "\uE713", "设置", FloatingToolbarCommand.OpenSettings));
        bar.Children.Add(CreateCommandButton(
            "\uE70E", "隐藏悬浮栏", FloatingToolbarCommand.DismissToolbar));
        _root.Children.Add(bar);

        _root.PointerEntered += (_, _) => PointerEntered?.Invoke(this, EventArgs.Empty);
        _root.PointerExited += (_, _) => PointerExited?.Invoke(this, EventArgs.Empty);
        _grip.PointerPressed += OnGripPointerPressed;
        _grip.PointerMoved += OnGripPointerMoved;
        _grip.PointerReleased += OnGripPointerReleased;
        _grip.PointerCaptureLost += OnGripPointerEnded;
        _window.Content = _root;
    }

    private Button CreateCommandButton(string glyph, string tooltip, FloatingToolbarCommand command)
    {
        var button = new Button
        {
            Width = ButtonSizeDip,
            Height = ButtonSizeDip,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = 16,
            },
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => CommandInvoked?.Invoke(this, command);
        return button;
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
        PhysicalRectangle bounds = GetBounds();
        SetWindowPos(
            _handle,
            0,
            bounds.X + (int)Math.Round(deltaX),
            bounds.Y + (int)Math.Round(deltaY),
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
    private static extern bool GetWindowRect(nint window, out RectL rect);

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
