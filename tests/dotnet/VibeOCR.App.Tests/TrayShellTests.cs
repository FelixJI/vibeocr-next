using System.Runtime.InteropServices;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 托盘 owner/图标组合契约（真实 WinUI 接线见 smoke_tray_shell.mjs）：
/// 图标挂在 TrayMenuOwnerWindow（非主窗），回调经
/// owner 路由，uVersion=0 lParam（键盘与右键同为 WM_RBUTTONUP）按
/// TrayIconCallback 分类，TaskbarCreated 事件触发同 GUID Reattach。
/// </summary>
public sealed class TrayShellTests
{
    private const uint TrayMessage = 0x8001;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [Fact]
    public void TrayIconShowsOnOwnerHandleAndRoutesCallbacksWithClassification()
    {
        var native = new FakeTrayNative();
        using var owner = new TrayMenuOwnerWindow();
        using var tray = new TrayIconService(native);
        tray.Show(owner.Handle, TrayMessage, "VibeOCR");

        // 图标挂在 owner 句柄，绝不挂主窗。
        Assert.Equal(owner.Handle, native.AddedWindows.Single());

        var routed = new List<WindowMessage>();
        owner.MessageReceived += (_, message) => routed.Add(message);

        // v0 真实事件：右键与键盘菜单键（菜单键/Shift+F10）同样发送
        // WM_RBUTTONUP——同一真实 lParam 覆盖两个入口。
        SendMessage(owner.Handle, TrayMessage, 0, (nint)TrayIconCallback.RightButtonUp);
        // 左键/双击是明确打开动作。
        SendMessage(owner.Handle, TrayMessage, 0, (nint)TrayIconCallback.LeftButtonUp);
        // WM_CONTEXTMENU 是 v4 行为，v0 Shell 不发送；仅验证容错不回归。
        SendMessage(owner.Handle, TrayMessage, 0, (nint)TrayIconCallback.ContextMenu);

        Assert.Equal(3, routed.Count);
        Assert.All(routed, message => Assert.Equal(TrayMessage, message.Id));
        Assert.Contains(routed, m => (uint)m.LParam == TrayIconCallback.RightButtonUp);
        Assert.Contains(routed, m => (uint)m.LParam == TrayIconCallback.LeftButtonUp);
        Assert.Contains(routed, m => (uint)m.LParam == TrayIconCallback.ContextMenu);
        // v0 真实事件路径：右键/键盘同一 lParam 即菜单请求。
        Assert.True(TrayIconCallback.IsContextMenuRequest(TrayIconCallback.RightButtonUp));
        // v4 容错路径仅作兼容，不作为 v0 键盘入口的证据。
        Assert.True(TrayIconCallback.IsContextMenuRequest(TrayIconCallback.ContextMenu));
    }

    [Fact]
    public void TaskbarCreatedBroadcastToOwnerReattachesSameGuid()
    {
        var native = new FakeTrayNative();
        using var owner = new TrayMenuOwnerWindow();
        using var tray = new TrayIconService(native);
        tray.Show(owner.Handle, TrayMessage, "VibeOCR");
        int reattached = 0;
        // 与 App.OnTaskbarCreated 相同的接线：广播到达 owner 后同 GUID 重挂。
        owner.TaskbarCreated += (_, _) =>
        {
            tray.Reattach();
            reattached++;
        };

        // 合成广播只定向投递到自有 owner HWND，不向桌面广播、不重启 Explorer。
        nint result = SendMessage(owner.Handle, RegisterWindowMessageW("TaskbarCreated"), 0, 0);

        Assert.Equal(0, result);
        Assert.Equal(1, reattached);
        Assert.Equal(2, native.AddCalls);
        Assert.All(native.AddedIds, id => Assert.Equal(native.AddedIds[0], id));
        Assert.Equal(0, native.DeleteCalls);
    }

    private sealed class FakeTrayNative : ITrayIconNativeMethods
    {
        public int AddCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public List<Guid> AddedIds { get; } = [];

        public List<nint> AddedWindows { get; } = [];

        public nint GetForeground() => 0;
        public bool SetFocus(Guid id, nint windowHandle) => true;

        public bool Add(Guid id, nint windowHandle, uint callbackMessage, string tooltip)
        {
            AddCalls++;
            AddedIds.Add(id);
            AddedWindows.Add(windowHandle);
            return true;
        }

        public bool Delete(Guid id, nint windowHandle)
        {
            DeleteCalls++;
            return true;
        }
    }
}
