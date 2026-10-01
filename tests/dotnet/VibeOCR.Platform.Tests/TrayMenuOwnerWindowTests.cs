using System.ComponentModel;
using System.Runtime.InteropServices;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

/// <summary>
/// seam 级契约：TrackPopupMenu 模态阻塞、SetForegroundWindow 会真实抢占
/// 前台，菜单握手（owner 前台 + 结束 WM_NULL，绝不用主窗）无法在自动化
/// 测试里用真窗复现，故仅保留握手与建窗失败两条 fake 契约；其余行为见
/// 下方真实 Win32 集成测试。
/// </summary>
public sealed class TrayMenuOwnerWindowTests
{
    private const nint MenuHandle = 0x0042C0DE;

    [Fact]
    public void TrackContextMenuForegroundsOwnerAndCompletesWmNullHandshake()
    {
        var native = new FakeOwnerNative();
        using var owner = new TrayMenuOwnerWindow(native);

        int selected = owner.TrackContextMenu(MenuHandle, 120, 340);

        Assert.Equal(77, selected);
        // 顺序契约（跳过构造期注册记录）：owner 前台 → 模态菜单（owner 为
        // 宿主）→ 菜单结束 WM_NULL 消除握手；前台目标始终是 owner 句柄。
        Assert.Equal(
            $"setforeground:0x{0x5678:X}|track:{MenuHandle}:0x102:0x{0x5678:X}|post:0x{0x5678:X}:0x0",
            string.Join('|', native.Calls[1..]));
    }

    [Fact]
    public void CreateThrowsWhenWindowCreationFailsWithoutInstallingRouter()
    {
        var native = new FakeOwnerNative { NextHandle = 0 };
        Assert.Throws<Win32Exception>(() => new TrayMenuOwnerWindow(native));
        Assert.Equal(0, native.RouterInstalls);
    }

    [Fact]
    public void TaskbarMessageRegistrationFailureFailsConstruction()
    {
        // RegisterWindowMessageW 失败返回 0；0 即 WM_NULL，绝不能当重挂广播。
        var native = new FakeOwnerNative { NextTaskbarMessage = 0 };
        Assert.Throws<Win32Exception>(() => new TrayMenuOwnerWindow(native));
        // 失败后不再建窗、不装路由。
        Assert.Equal(["taskbar-registered"], native.Calls);
        Assert.Equal(0, native.RouterInstalls);
    }

    [Fact]
    public void RouterFailureDestroysCreatedWindow()
    {
        var native = new FakeOwnerNative { RouterThrows = true };
        Assert.Throws<InvalidOperationException>(() => new TrayMenuOwnerWindow(native));
        Assert.Contains($"destroy:0x{0x5678:X}", native.Calls);
    }

    [Fact]
    public void SetForegroundFailureFailsHandshakeBeforeMenu()
    {
        var native = new FakeOwnerNative { ForegroundResult = false };
        using var owner = new TrayMenuOwnerWindow(native);

        Assert.Throws<Win32Exception>(() => owner.TrackContextMenu(MenuHandle, 1, 2));
        // 前台失败时不得显示菜单，也不得伪造 WM_NULL 握手完成。
        Assert.DoesNotContain(native.Calls, call => call.StartsWith("track:"));
        Assert.DoesNotContain(native.Calls, call => call.StartsWith("post:"));
    }

    [Fact]
    public void TrackMenuThrowStillPostsWmNullToReleaseHandshake()
    {
        var native = new FakeOwnerNative { TrackThrows = true };
        using var owner = new TrayMenuOwnerWindow(native);

        Assert.Throws<InvalidOperationException>(() => owner.TrackContextMenu(MenuHandle, 3, 4));
        Assert.Equal($"post:0x{0x5678:X}:0x0", native.Calls[^1]);
    }

    private sealed class FakeOwnerNative : ITrayMenuOwnerNativeMethods
    {
        public nint NextHandle { get; set; } = 0x5678;

        public int NextCommand { get; set; } = 77;

        public uint NextTaskbarMessage { get; set; } = 0xC0DE;

        public bool ForegroundResult { get; set; } = true;

        public bool RouterThrows { get; set; }

        public bool TrackThrows { get; set; }

        public List<string> Calls { get; } = [];

        public int RouterInstalls { get; private set; }

        public uint RegisterTaskbarCreatedMessage()
        {
            Calls.Add("taskbar-registered");
            return NextTaskbarMessage;
        }

        public nint CreateOwnerWindow() => NextHandle;

        public IDisposable InstallMessageRouter(nint windowHandle, Action<WindowMessage> route)
        {
            if (RouterThrows)
            {
                throw new InvalidOperationException("router install failed");
            }

            RouterInstalls++;
            return new RouterToken();
        }

        public bool SetForeground(nint window)
        {
            Calls.Add($"setforeground:0x{window:X}");
            return ForegroundResult;
        }

        public int TrackContextMenu(nint menu, uint flags, int x, int y, nint window)
        {
            Calls.Add($"track:{menu}:0x{flags:X}:0x{window:X}");
            if (TrackThrows)
            {
                throw new InvalidOperationException("track failed");
            }

            return NextCommand;
        }

        public bool PostMessage(nint window, uint message, nuint wParam, nint lParam)
        {
            Calls.Add($"post:0x{window:X}:0x{message:X}");
            return true;
        }

        public bool DestroyOwnerWindow(nint window)
        {
            Calls.Add($"destroy:0x{window:X}");
            return true;
        }

        private sealed class RouterToken : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

public sealed class TrayIconReattachTests
{
    [Fact]
    public void ReattachAfterTaskbarRecreatedReaddsSameGuidAndDeletesOnce()
    {
        var native = new FakeTrayNative();
        var service = new TrayIconService(native);
        service.Show(0x1111, 0x8001, "VibeOCR");

        service.Reattach();
        service.Dispose();
        service.Dispose();

        // 同 GUID 重挂（AC5：任务栏重建后无重复），Dispose 只删一次。
        Assert.Equal(2, native.AddCalls);
        Assert.Equal(1, native.DeleteCalls);
        Assert.All(native.AddedIds, id => Assert.Equal(native.AddedIds[0], id));
        Assert.All(native.AddedWindows, window => Assert.Equal((nint)0x1111, window));
        Assert.All(native.AddedCallbacks, callback => Assert.Equal((uint)0x8001, callback));
        Assert.All(native.AddedTooltips, tooltip => Assert.Equal("VibeOCR", tooltip));
    }

    [Fact]
    public void ReattachRequiresVisibleIcon()
    {
        var service = new TrayIconService(new FakeTrayNative());

        Assert.Throws<InvalidOperationException>(service.Reattach);
    }

    [Theory]
    [InlineData(TrayIconCallback.RightButtonUp, true)]
    [InlineData(TrayIconCallback.ContextMenu, true)]
    [InlineData(TrayIconCallback.LeftButtonUp, false)]
    [InlineData(TrayIconCallback.LeftDoubleClick, false)]
    public void ContextMenuRequestClassification(uint lParam, bool expected)
    {
        Assert.Equal(expected, TrayIconCallback.IsContextMenuRequest(lParam));
    }

    private sealed class FakeTrayNative : ITrayIconNativeMethods
    {
        public int AddCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public List<Guid> AddedIds { get; } = [];

        public List<nint> AddedWindows { get; } = [];

        public List<uint> AddedCallbacks { get; } = [];

        public List<string> AddedTooltips { get; } = [];

        public bool Add(Guid id, nint windowHandle, uint callbackMessage, string tooltip)
        {
            AddCalls++;
            AddedIds.Add(id);
            AddedWindows.Add(windowHandle);
            AddedCallbacks.Add(callbackMessage);
            AddedTooltips.Add(tooltip);
            return true;
        }

        public bool Delete(Guid id, nint windowHandle)
        {
            DeleteCalls++;
            return true;
        }
    }
}

/// <summary>
/// 真实 Win32 集成：宿主必须是真实隐藏顶层 ToolWindow（非 HWND_MESSAGE、
/// 不显示、不进任务栏），消息经 WindowMessageService 子类化路由；合成
/// TaskbarCreated 定向投递到本窗口验证可接收性，不向桌面广播。
/// </summary>
public sealed class TrayMenuOwnerWindowWin32Tests
{
    private const uint WmAppTrayCallback = 0x8001;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [Fact]
    public void RealOwnerWindowIsHiddenTopLevelToolWindow()
    {
        using var owner = new TrayMenuOwnerWindow();

        // 隐藏：从未 Show，不在任务栏/AltTab 出现。
        Assert.False(IsWindowVisible(owner.Handle));
        // 真实顶层窗口（GetParent==0），不是 HWND_MESSAGE 消息专用窗。
        Assert.Equal(0, GetParent(owner.Handle));
        long style = GetWindowLongPtrW(owner.Handle, GwlStyle);
        long exStyle = GetWindowLongPtrW(owner.Handle, GwlExStyle);
        Assert.NotEqual(0, style & WsPopup);
        Assert.NotEqual(0, exStyle & WsExToolWindow);
    }

    [Fact]
    public void RealOwnerWindowRoutesTrayCallbackAndSyntheticTaskbarCreated()
    {
        using var owner = new TrayMenuOwnerWindow();
        var received = new List<uint>();
        int taskbarRecreated = 0;
        owner.MessageReceived += (_, message) => received.Add(message.Id);
        owner.TaskbarCreated += (_, _) => taskbarRecreated++;

        // SendMessage 同步直达窗口过程，无需消息泵。
        nint routed = SendMessage(
            owner.Handle, WmAppTrayCallback, 0, (nint)TrayIconCallback.RightButtonUp);
        uint taskbarMessage = RegisterWindowMessageW("TaskbarCreated");
        nint broadcast = SendMessage(owner.Handle, taskbarMessage, 0, 0);

        Assert.Equal(0, routed);
        Assert.Equal(0, broadcast);
        Assert.Contains(WmAppTrayCallback, received);
        Assert.Contains(taskbarMessage, received);
        Assert.Equal(1, taskbarRecreated);
    }

    [Fact]
    public void RealOwnerWindowIsDestroyedAfterDispose()
    {
        var owner = new TrayMenuOwnerWindow();
        nint handle = owner.Handle;
        Assert.True(IsWindow(handle));

        owner.Dispose();
        owner.Dispose();

        Assert.False(IsWindow(handle));
    }
}
