using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeOCR.Platform.Windows;

/// <summary>Win32 seam for the edge sensor window, fakeable in unit tests.</summary>
public interface IEdgeSensorNativeMethods
{
    /// <summary>Registers the window class and creates the layered sensor window (hidden).</summary>
    nint CreateSensorWindow(string className, nint windowProc);

    bool SetAlpha(nint window, byte alpha);

    bool PlaceTopMost(nint window, PhysicalRectangle bounds);

    bool ShowNoActivate(nint window);

    bool HideWindow(nint window);

    /// <summary>Destroys the window and unregisters its per-instance class.</summary>
    bool DestroySensorWindow(nint window, string className);

    nint DefaultWindowProc(nint window, uint message, nuint wParam, nint lParam);
}

/// <summary>贴边感应条的行为契约，供悬浮工具栏控制器注入与测试。</summary>
public interface IEdgeSensor : IDisposable
{
    event EventHandler? PointerEntered;

    event EventHandler? DisplayChanged;

    bool IsArmed { get; }

    nint Handle { get; }

    void Arm(PhysicalRectangle bounds);

    void Disarm();
}

/// <summary>
/// 贴边感应条：停靠隐藏时贴屏幕边缘的 2px 不可见 Tool 窗口。
/// 以 WS_EX_LAYERED + alpha=1 实现“视觉为零但可命中”，收到鼠标消息即触发
/// PointerEntered；WS_EX_NOACTIVATE 与 WM_MOUSEACTIVATE→MA_NOACTIVATE 保证
/// 不抢前台焦点。注意感应条必须可命中，不能返回 HTTRANSPARENT——那会让它
/// 对鼠标完全透明、收不到任何消息，揭示机制失效。
/// </summary>
public sealed class EdgeSensorWindow : IEdgeSensor
{
    private const uint WmDisplayChange = 0x007E;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmMouseMove = 0x0200;
    private const nint MaNoActivate = 3;

    private readonly IEdgeSensorNativeMethods _native;
    private readonly string _className;
    // 字段持有委托防止 GC 回收原生函数指针。
    private readonly SensorWindowProc _windowProc;
    private readonly nint _handle;
    private bool _disposed;

    public event EventHandler? PointerEntered;

    public event EventHandler? DisplayChanged;

    public bool IsArmed { get; private set; }

    /// <summary>原生窗口句柄；测试与自检可向其 SendMessage 注入消息。</summary>
    public nint Handle => _handle;

    public EdgeSensorWindow(IEdgeSensorNativeMethods? native = null)
    {
        _native = native ?? new EdgeSensorNativeMethods();
        _className = $"VibeOCR.EdgeSensor.{Guid.NewGuid():N}";
        _windowProc = WindowProc;
        _handle = _native.CreateSensorWindow(
            _className,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
        if (_handle == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to create the edge sensor window.");
        }

        // alpha=1/255：人眼不可见但命中测试仍落在窗口上；alpha=0 会被系统
        // 视为完全透明而穿透命中，感应失效。
        if (!_native.SetAlpha(_handle, 1))
        {
            _native.DestroySensorWindow(_handle, _className);
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to make the edge sensor window translucent.");
        }
    }

    /// <summary>贴边就位并显示（不激活）。</summary>
    public void Arm(PhysicalRectangle bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _native.PlaceTopMost(_handle, bounds);
        _native.ShowNoActivate(_handle);
        IsArmed = true;
    }

    public void Disarm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _native.HideWindow(_handle);
        IsArmed = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _native.HideWindow(_handle);
        _native.DestroySensorWindow(_handle, _className);
        IsArmed = false;
    }

    // internal 供单测直接注入窗口消息。
    internal nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmMouseMove:
                PointerEntered?.Invoke(this, EventArgs.Empty);
                return 0;
            case WmMouseActivate:
                return MaNoActivate;
            case WmDisplayChange:
                DisplayChanged?.Invoke(this, EventArgs.Empty);
                return 0;
            default:
                return _native.DefaultWindowProc(window, message, wParam, lParam);
        }
    }

    private delegate nint SensorWindowProc(nint window, uint message, nuint wParam, nint lParam);
}

internal sealed class EdgeSensorNativeMethods : IEdgeSensorNativeMethods
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwHide = 0;
    private const uint SwShowNoActivate = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint LwaAlpha = 0x00000002;
    private static readonly nint HwndTopMost = -1;
    private static readonly nint ArrowCursor = 32512;

    public nint CreateSensorWindow(string className, nint windowProc)
    {
        nint instance = GetModuleHandle(null);
        var windowClass = new WindowClassW
        {
            LpfnWndProc = windowProc,
            HInstance = instance,
            HCursor = LoadCursor(0, ArrowCursor),
            LpszClassName = className,
        };
        if (RegisterClassW(ref windowClass) == 0)
        {
            return 0;
        }

        return CreateWindowExW(
            WsExLayered | WsExToolWindow | WsExNoActivate | WsExTopMost,
            className,
            string.Empty,
            WsPopup,
            0,
            0,
            ScreenEdgeGeometry.SensorThicknessPx,
            ScreenEdgeGeometry.SensorThicknessPx,
            0,
            0,
            instance,
            0);
    }

    public bool SetAlpha(nint window, byte alpha) =>
        SetLayeredWindowAttributes(window, 0, alpha, LwaAlpha);

    public bool PlaceTopMost(nint window, PhysicalRectangle bounds) =>
        SetWindowPos(
            window,
            HwndTopMost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            SwpNoActivate);

    public bool ShowNoActivate(nint window) => ShowWindow(window, SwShowNoActivate);

    public bool HideWindow(nint window) => ShowWindow(window, SwHide);

    public bool DestroySensorWindow(nint window, string className)
    {
        bool destroyed = DestroyWindow(window);
        UnregisterClassW(className, GetModuleHandle(null));
        return destroyed;
    }

    public nint DefaultWindowProc(nint window, uint message, nuint wParam, nint lParam) =>
        DefWindowProcW(window, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassW
    {
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public string? LpszMenuName;
        public string? LpszClassName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint cursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WindowClassW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parentWindow,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(
        nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(
        nint window, uint message, nuint wParam, nint lParam);
}
