using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeOCR.Platform.Windows;

public interface ITrayIconNativeMethods
{
    bool Add(Guid id, nint windowHandle, uint callbackMessage, string tooltip);
    bool Delete(Guid id, nint windowHandle);
    nint GetForeground();
    bool SetFocus(Guid id, nint windowHandle);
}

/// <summary>
/// NOTIFYICON uVersion=0 回调约定：托盘回调消息的 lParam 携带以下鼠标
/// 事件。按官方 Shell_NotifyIcon 文档，v0 下键盘菜单键（菜单键/Shift+F10）
/// 与右键同样发送 WM_RBUTTONUP，因此 RightButtonUp 已覆盖键盘菜单入口；
/// WM_CONTEXTMENU 是 NOTIFYICON_VERSION_4 行为，实际 v0 Shell 不会发送，
/// 仅作额外容错保留，不升级版本/引入高低字解析。
/// </summary>
public static class TrayIconCallback
{
    public const uint LeftButtonUp = 0x0202;

    public const uint LeftDoubleClick = 0x0203;

    public const uint RightButtonUp = 0x0205;

    /// <summary>NOTIFYICON_VERSION_4 才发送的键盘菜单消息；v0 容错保留。</summary>
    public const uint ContextMenu = 0x007C;

    /// <summary>lParam 是否为右键/键盘菜单请求（应弹托盘菜单而非显示主窗）。</summary>
    public static bool IsContextMenuRequest(uint callbackLParam) =>
        callbackLParam is RightButtonUp or ContextMenu;
}

public sealed class TrayIconService : IDisposable
{
    // 仅真实托盘 smoke 使用已知的隔离 GUID，让 Shell_NotifyIconGetRect 可精确
    // 定位自有图标；普通启动与其他 smoke 仍各自生成随机身份。
    private readonly Guid _id = ResolveIconId(
        Environment.GetEnvironmentVariable("VIBEOCR_TRAY_SELF_TEST"),
        Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE"),
        Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_INSTANCE"));
    private readonly ITrayIconNativeMethods _native;
    private nint _windowHandle;
    private uint _callbackMessage;
    private string _tooltip = string.Empty;
    private bool _visible;
    private bool _disposed;

    internal static Guid ResolveIconId(string? enabled, string? mode, string? instanceId) =>
        enabled == "1" && mode == "native-actions-e2e" &&
        Guid.TryParseExact(instanceId, "N", out Guid id) && id != Guid.Empty ? id : Guid.NewGuid();

    public TrayIconService(string iconPath)
        : this(new TrayIconNativeMethods(iconPath))
    {
    }

    public TrayIconService(ITrayIconNativeMethods native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public void Show(nint windowHandle, uint callbackMessage, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tooltip);
        if (_visible)
        {
            throw new InvalidOperationException("Tray icon is already visible.");
        }

        if (!_native.Add(_id, windowHandle, callbackMessage, tooltip))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to add tray icon.");
        }

        _windowHandle = windowHandle;
        _callbackMessage = callbackMessage;
        _tooltip = tooltip;
        _visible = true;
    }

    /// <summary>
    /// TaskbarCreated（Explorer/任务栏重建）后以同一 GUID 重新挂载图标：
    /// GUID 即托盘标识，重挂不产生重复图标；窗口句柄、回调消息与提示不变。
    /// </summary>
    public void Reattach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_visible)
        {
            throw new InvalidOperationException("Tray icon is not visible.");
        }

        if (!_native.Add(_id, _windowHandle, _callbackMessage, _tooltip))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to reattach tray icon after taskbar recreation.");
        }
    }

    public void RestoreFocusAfterMenu()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Esc leaves our hidden owner foreground; an outside click must keep its new focus.
        if (_visible && _native.GetForeground() == _windowHandle &&
            !_native.SetFocus(_id, _windowHandle))
        {
            throw new Win32Exception("Failed to return focus to the notification area.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_visible)
        {
            _native.Delete(_id, _windowHandle);
            _visible = false;
        }

        if (_native is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private sealed class TrayIconNativeMethods : ITrayIconNativeMethods, IDisposable
    {
        private const uint AddMessage = 0;
        private const uint DeleteMessage = 2;
        private const uint MessageFlag = 0x0001;
        private const uint IconFlag = 0x0002;
        private const uint TipFlag = 0x0004;
        private const uint GuidFlag = 0x0020;
        private const uint ImageIcon = 1;
        private const uint LoadFromFile = 0x0010;
        private const uint LoadDefaultSize = 0x0040;
        private readonly nint _icon;

        public TrayIconNativeMethods(string iconPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);
            string fullPath = Path.GetFullPath(iconPath);
            _icon = LoadImage(0, fullPath, ImageIcon, 0, 0, LoadFromFile | LoadDefaultSize);
            if (_icon == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Failed to load tray icon: {fullPath}");
            }
        }

        public bool Add(Guid id, nint windowHandle, uint callbackMessage, string tooltip)
        {
            NotifyIconData data = CreateData(id, windowHandle);
            data.Flags = MessageFlag | IconFlag | TipFlag | GuidFlag;
            data.CallbackMessage = callbackMessage;
            data.Icon = _icon;
            data.Tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
            return ShellNotifyIcon(AddMessage, ref data);
        }

        public bool Delete(Guid id, nint windowHandle)
        {
            NotifyIconData data = CreateData(id, windowHandle);
            data.Flags = GuidFlag;
            return ShellNotifyIcon(DeleteMessage, ref data);
        }

        public nint GetForeground() => GetForegroundWindow();

        public bool SetFocus(Guid id, nint windowHandle)
        {
            NotifyIconData data = CreateData(id, windowHandle);
            data.Flags = GuidFlag;
            return ShellNotifyIcon(3, ref data); // NIM_SETFOCUS
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        public void Dispose()
        {
            if (_icon != 0)
            {
                DestroyIcon(_icon);
            }
        }

        private static NotifyIconData CreateData(Guid id, nint windowHandle) =>
            new()
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                WindowHandle = windowHandle,
                Id = 1,
                Guid = id,
                Tip = string.Empty,
                Info = string.Empty,
                InfoTitle = string.Empty,
            };

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint Size;
            public nint WindowHandle;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public nint Icon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Tip;

            public uint State;
            public uint StateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Info;

            public uint TimeoutOrVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string InfoTitle;

            public uint InfoFlags;
            public Guid Guid;
            public nint BalloonIcon;
        }

        [DllImport(
            "shell32.dll",
            EntryPoint = "Shell_NotifyIconW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

        [DllImport(
            "user32.dll",
            EntryPoint = "LoadImageW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        private static extern nint LoadImage(
            nint instance,
            string name,
            uint type,
            int width,
            int height,
            uint loadFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(nint icon);
    }
}
