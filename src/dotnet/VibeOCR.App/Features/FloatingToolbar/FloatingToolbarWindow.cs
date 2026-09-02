using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeOCR.Platform.Windows;
using Windows.Graphics;

namespace VibeOCR.App.Features.FloatingToolbar;

internal enum FloatingToolbarCommand
{
    CaptureScreenshot,
    ShowMainWindow,
    OpenSettings,
    DismissToolbar,
}

/// <summary>
/// 悬浮工具栏窗口：无边框、置顶、不进任务栏/Alt+Tab；子类化拦截
/// WM_MOUSEACTIVATE 返回 MA_NOACTIVATE，显示走 SW_SHOWNOACTIVATE，悬停与
/// 点击都不抢前台焦点。左侧把手拖动重定位，松手由控制器判定贴边吸附。
/// </summary>
internal sealed class FloatingToolbarWindow : IFloatingToolbarView
{
    internal const double DesignWidthDip = 188;
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
    private readonly WindowMessageService _messages;
    private readonly Border _grip;
    private readonly nint _handle;
    private Windows.Foundation.Point? _dragOrigin;
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
        AddToolWindowStyle();
        _messages = new WindowMessageService(_handle);
        _messages.MessageHandled += OnMessageHandled;
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messages.MessageHandled -= OnMessageHandled;
        _messages.Dispose();
        _window.Close();
    }

    private static Border BuildGrip()
    {
        return new Border
        {
            Width = GripWidthDip,
            Height = ButtonSizeDip,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Child = new FontIcon
            {
                Glyph = "\uE700",
                FontSize = 12,
                Opacity = 0.7,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 235, 235)),
            },
        };
    }

    private void BuildContent()
    {
        var root = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(238, 24, 24, 24)),
        };
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
            "\uE8A7", "显示主窗口", FloatingToolbarCommand.ShowMainWindow));
        bar.Children.Add(CreateCommandButton(
            "\uE713", "设置", FloatingToolbarCommand.OpenSettings));
        bar.Children.Add(CreateCommandButton(
            "\uE70E", "收回到边缘", FloatingToolbarCommand.DismissToolbar));
        root.Children.Add(bar);

        root.PointerEntered += (_, _) => PointerEntered?.Invoke(this, EventArgs.Empty);
        root.PointerExited += (_, _) => PointerExited?.Invoke(this, EventArgs.Empty);
        _grip.PointerPressed += OnGripPointerPressed;
        _grip.PointerMoved += OnGripPointerMoved;
        _grip.PointerReleased += OnGripPointerReleased;
        _grip.PointerCaptureLost += OnGripPointerEnded;
        _window.Content = root;
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
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 235, 235)),
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
        long style = GetWindowLongPtrW(_handle, GwlExStyle);
        SetWindowLongPtrW(_handle, GwlExStyle, (nint)(style | WsExToolWindow));
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
