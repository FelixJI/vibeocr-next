using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeOCR.Platform.Windows;

/// <summary>
/// Win32 seam for the tray menu owner window, fakeable in unit tests.
/// 仅因 TrackPopupMenu 模态阻塞与 SetForegroundWindow 抢占前台而无法真窗
/// 自动化，才为菜单握手契约保留此 seam（与 ITrayIconNativeMethods 同例）。
/// </summary>
public interface ITrayMenuOwnerNativeMethods
{
    /// <summary>注册并返回会话级 TaskbarCreated 广播消息 ID。</summary>
    uint RegisterTaskbarCreatedMessage();

    /// <summary>创建隐藏的顶层 WS_POPUP/WS_EX_TOOLWINDOW 宿主窗口。</summary>
    nint CreateOwnerWindow();

    /// <summary>为宿主窗口安装消息路由（真实实现复用 WindowMessageService 子类化）。</summary>
    IDisposable InstallMessageRouter(nint windowHandle, Action<WindowMessage> route);

    bool SetForeground(nint window);

    int TrackContextMenu(nint menu, uint flags, int x, int y, nint window);

    bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    bool DestroyOwnerWindow(nint window);
}

/// <summary>
/// 托盘菜单宿主：真实隐藏顶层 WS_POPUP/WS_EX_TOOLWINDOW 窗口（复用标准
/// STATIC 窗口类，不注册新 WCLASS）。绝不用 HWND_MESSAGE——消息专用窗不
/// 参与激活、收不到 TaskbarCreated 广播。窗口永不显示、不进任务栏/AltTab，
/// 经 WindowMessageService 子类化路由消息；向宿主应用提供菜单 owner 句柄、
/// 托盘回调路由（uVersion=0 lParam 约定由调用方解释）与 TaskbarCreated
/// 重挂事件，菜单握手（SetForegroundWindow(owner)+TrackPopupMenu+WM_NULL）
/// 只针对本 owner，绝不能 SetForegroundWindow 主窗。
/// </summary>
public sealed class TrayMenuOwnerWindow : IDisposable
{
    internal const uint TrackPopupRightButton = 0x0002;
    internal const uint TrackPopupReturnCommand = 0x0100;
    private const uint WmNull = 0x0000;

    private readonly ITrayMenuOwnerNativeMethods _native;
    private readonly uint _taskbarCreatedMessage;
    private readonly IDisposable _messageRouter;
    private readonly nint _handle;
    private bool _disposed;

    public TrayMenuOwnerWindow(ITrayMenuOwnerNativeMethods? native = null)
    {
        _native = native ?? new TrayMenuOwnerNativeMethods();
        _taskbarCreatedMessage = _native.RegisterTaskbarCreatedMessage();
        if (_taskbarCreatedMessage == 0)
        {
            // RegisterWindowMessageW 失败返回 0 且不承诺 last error；0 即
            // WM_NULL，绝不能被当作重挂广播路由。
            throw new Win32Exception(
                "Failed to register the TaskbarCreated broadcast message.");
        }

        _handle = _native.CreateOwnerWindow();
        if (_handle == 0)
        {
            // CreateWindowExW 失败有文档化 last error。
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to create the tray menu owner window.");
        }

        try
        {
            _messageRouter = _native.InstallMessageRouter(_handle, RouteMessage);
        }
        catch
        {
            // 路由安装失败必须销毁已建窗口，避免隐藏 HWND 泄漏。
            _native.DestroyOwnerWindow(_handle);
            throw;
        }
    }

    /// <summary>Explorer/任务栏重建广播；应用在此重挂同 GUID 托盘图标。</summary>
    public event EventHandler? TaskbarCreated;

    /// <summary>宿主窗口收到的消息（含托盘回调消息，由应用解释 lParam）。</summary>
    public event EventHandler<WindowMessage>? MessageReceived;

    /// <summary>菜单 owner 与托盘回调窗口句柄；测试可向其注入消息。</summary>
    public nint Handle => _handle;

    /// <summary>
    /// 以本 owner 为前台显示模态上下文菜单（TPM_RETURNCMD 直接返回选中项，
    /// 取消返回 0）。菜单结束补发 WM_NULL 消除前台握手，保证 Esc/点击外部
    /// 正常关闭且不残留、不抢回主窗前台。
    /// </summary>
    public int TrackContextMenu(nint menu, int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // SetForegroundWindow 失败返回 0 且不承诺 last error（前台锁等）：
        // 不读 last error、不伪装握手成功，菜单未显示即 fail fast。
        if (!_native.SetForeground(_handle))
        {
            throw new Win32Exception(
                "Failed to make the tray menu owner window foreground.");
        }

        try
        {
            return _native.TrackContextMenu(
                menu,
                TrackPopupRightButton | TrackPopupReturnCommand,
                x,
                y,
                _handle);
        }
        finally
        {
            // 菜单结束（含异常）都必须补发 WM_NULL 解除前台握手，否则下一
            // 次菜单可能无法正常关闭。
            _native.PostMessage(_handle, WmNull, 0, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // 先拆消息路由再销毁窗口，释放各一次、不重复。
        _messageRouter.Dispose();
        _native.DestroyOwnerWindow(_handle);
    }

    // internal 供单测与真实路由注入窗口消息。
    internal void RouteMessage(WindowMessage message)
    {
        if (message.Id == _taskbarCreatedMessage)
        {
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
        }

        MessageReceived?.Invoke(this, message);
    }
}

internal sealed class TrayMenuOwnerNativeMethods : ITrayMenuOwnerNativeMethods
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;

    public uint RegisterTaskbarCreatedMessage() => RegisterWindowMessageW("TaskbarCreated");

    public nint CreateOwnerWindow() =>
        CreateWindowExW(
            WsExToolWindow,
            "STATIC",
            string.Empty,
            WsPopup,
            0,
            0,
            0,
            0,
            0,
            0,
            GetModuleHandle(null),
            0);

    public IDisposable InstallMessageRouter(nint windowHandle, Action<WindowMessage> route)
    {
        var router = new WindowMessageService(windowHandle);
        router.MessageReceived += (_, message) => route(message);
        return router;
    }

    public bool SetForeground(nint window) => SetForegroundWindow(window);

    public int TrackContextMenu(nint menu, uint flags, int x, int y, nint window) =>
        TrackPopupMenu(menu, flags, x, y, 0, window, 0);

    public bool PostMessage(nint window, uint message, nuint wParam, nint lParam) =>
        PostMessageW(window, message, wParam, lParam);

    public bool DestroyOwnerWindow(nint window) => DestroyWindow(window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

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
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint window,
        nint rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
