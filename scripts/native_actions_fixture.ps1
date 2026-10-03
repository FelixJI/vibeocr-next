param(
    [Parameter(Mandatory = $true)][ValidateSet('fixture', 'windows', 'hide', 'close', 'quit', 'focus-fixture', 'webview-bounds', 'hotkey', 'recognize-hotkey', 'foreground', 'probe', 'hover', 'tab', 'enter', 'escape', 'selection', 'cursor', 'magnifier', 'minimize', 'restore', 'tray-state', 'tray-click', 'taskbar-created', 'down', 'tray-fixture', 'tray-keyboard', 'tray-keyboard-resume', 'tray-expose', 'tray-left-click', 'tray-double-click', 'tray-gone', 'tray-menu-quit', 'tray-menu-open', 'tray-menu-toggle')][string]$Action,
    [int]$AppPid = 0,
    [int]$FixturePid = 0,
    [int]$ForegroundPid = 0,
    [long]$Handle = 0,
    [uint32]$ThreadId = 0,
    [int]$X = 0,
    [int]$Y = 0,
    [Guid]$IconGuid = [Guid]::Empty
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class NativeActionsFixture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    public sealed class WindowInfo
    {
        public long Handle { get; set; }
        public bool Visible { get; set; }
        public bool Iconic { get; set; }
        public string ClassName { get; set; }
        public uint Dpi { get; set; }
        public Rect Bounds { get; set; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyInput Key;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public UIntPtr Extra;
    }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint extended, string className, string text,
        uint style, int x, int y, int width, int height, IntPtr parent,
        IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,
        IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message,
        IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect rectangle);

    private static string ClassName(IntPtr window)
    {
        var text = new System.Text.StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    public static void Show(long handle, int pid, int command)
    {
        RequireOwner(handle, pid);
        ShowWindow(new IntPtr(handle), command);
    }

    public static object TrayState(int appPid, int fixturePid, long mainHandle)
    {
        RequireOwner(mainHandle, appPid);
        var windows = Windows(appPid);
        var owners = Array.FindAll(windows, w => string.Equals(w.ClassName, "STATIC", StringComparison.OrdinalIgnoreCase) &&
            !w.Visible && w.Bounds.Left == w.Bounds.Right && w.Bounds.Top == w.Bounds.Bottom &&
            (GetWindowLongPtrW(new IntPtr(w.Handle), -20).ToInt64() & 0x80) != 0);
        if (owners.Length != 1) throw new InvalidOperationException("Isolated tray owner is missing or ambiguous.");
        IntPtr foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        return new {
            Main = Array.Find(windows, w => w.Handle == mainHandle), Owner = owners[0],
            Menus = Array.FindAll(windows, w => w.ClassName == "#32768" && w.Visible),
            ForegroundHandle = foreground.ToInt64(), ForegroundPid = foregroundPid,
            ForegroundOwned = foregroundPid == (uint)appPid || foregroundPid == (uint)fixturePid
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string name);
    public static long TaskbarHandle() => FindWindow("Shell_TrayWnd", null).ToInt64();
    public static long TrayShellRoot(long handle, int appPid, Guid iconGuid)
    {
        Rect rectangle = TrayRect(handle, appPid, iconGuid);
        IntPtr hit = WindowFromPoint(new Point { X = (rectangle.Left + rectangle.Right) / 2, Y = (rectangle.Top + rectangle.Bottom) / 2 });
        GetWindowThreadProcessId(hit, out uint hitPid);
        GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out uint shellPid);
        if (shellPid == 0 || hitPid != shellPid) throw new InvalidOperationException("Owned GUID point is not on the Shell.");
        return GetAncestor(hit, 2).ToInt64();
    }

    public static Rect TrayRect(long handle, int appPid, Guid iconGuid)
    {
        RequireOwner(handle, appPid);
        if (iconGuid == Guid.Empty) throw new InvalidOperationException("Owned tray probe requires its known isolated GUID.");
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = new IntPtr(handle), Id = 1, Guid = iconGuid };
        int result = Shell_NotifyIconGetRect(ref identifier, out Rect rectangle);
        if (result != 0) throw new InvalidOperationException("Owned tray icon rectangle unavailable: HRESULT " + result.ToString("X8"));
        return rectangle;
    }

    public static object TrayClick(long handle, int appPid, Guid iconGuid, bool left = false, bool doubleClick = false)
    {
        Rect rectangle = TrayRect(handle, appPid, iconGuid);

        int x = (rectangle.Left + rectangle.Right) / 2, y = (rectangle.Top + rectangle.Bottom) / 2;
        // The sole system window touched is the Shell point belonging to this icon.
        string hitClass = ClassName(WindowFromPoint(new Point { X = x, Y = y }));
        TrayShellRoot(handle, appPid, iconGuid);
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Synthetic cursor move failed.");
        uint down = left ? 0x0002u : 0x0008u, up = left ? 0x0004u : 0x0010u;
        try { Send(new[] { Mouse(down) }); }
        finally { Send(new[] { Mouse(up) }); }
        if (doubleClick)
        {
            try { Send(new[] { Mouse(down) }); }
            finally { Send(new[] { Mouse(up) }); }
        }
        return new { IconRect = rectangle, HitClass = hitClass };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuBarInfo { public uint Size; public Rect Bounds; public IntPtr Menu, Window; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMenuBarInfo(IntPtr window, int objectId, int item, ref MenuBarInfo info);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuStringW(IntPtr menu, uint item, System.Text.StringBuilder text, int size, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out Rect rectangle);

    public static object ClickMenuItem(long handle, int appPid, string expectedLabel)
    {
        RequireOwner(handle, appPid);
        RequireForeground(appPid);
        IntPtr window = new IntPtr(handle);
        if (ClassName(window) != "#32768" || !IsWindowVisible(window)) throw new InvalidOperationException("Visible owned menu is required.");
        var info = new MenuBarInfo { Size = (uint)Marshal.SizeOf<MenuBarInfo>() };
        // Documented OBJID_CLIENT retrieves this popup's HMENU, without Shell/UIA enumeration.
        if (!GetMenuBarInfo(window, -4, 0, ref info) || info.Menu == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned popup menu metadata unavailable.");
        int count = GetMenuItemCount(info.Menu);
        if (count < 1 || count > 16) throw new InvalidOperationException("Unexpected owned menu item count.");
        int selected = -1;
        string selectedLabel = string.Empty;
        for (int index = 0; index < count; index++)
        {
            var text = new System.Text.StringBuilder(256);
            GetMenuStringW(info.Menu, (uint)index, text, text.Capacity, 0x400);
            string label = text.ToString();
            bool match = expectedLabel == "悬浮工具栏"
                ? label == "启用悬浮工具栏" || label == "显示悬浮工具栏" || label == "隐藏悬浮工具栏"
                : label == expectedLabel;
            if (!match)
            {
                continue;
            }
            if (selected >= 0) throw new InvalidOperationException("Owned menu label is ambiguous.");
            selected = index;
            selectedLabel = label;
        }
        if (selected < 0) throw new InvalidOperationException("Expected owned menu item is missing.");
        if (!GetMenuItemRect(IntPtr.Zero, info.Menu, (uint)selected, out Rect rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned menu item bounds unavailable.");
        int x = (rectangle.Left + rectangle.Right) / 2, y = (rectangle.Top + rectangle.Bottom) / 2;
        RequirePointOwner(x, y, appPid);
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Owned menu cursor move failed.");
        try { Send(new[] { Mouse(0x0002) }); }
        finally { Send(new[] { Mouse(0x0004) }); }
        return new { MenuHandle = handle, ItemRect = rectangle, ItemCount = count, Label = selectedLabel };
    }
    public static object TrayGone(long handle, Guid iconGuid)
    {
        if (iconGuid == Guid.Empty) throw new InvalidOperationException("Known isolated icon GUID is required.");
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Guid = iconGuid };
        return new { OwnerGone = !IsWindow(new IntPtr(handle)), IconGone = Shell_NotifyIconGetRect(ref identifier, out _) != 0 };
    }

    public static void TaskbarCreated(long handle, int appPid)
    {
        RequireOwner(handle, appPid);
        if (!PostMessage(new IntPtr(handle), RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Owned TaskbarCreated post failed.");
    }

    private static string Bounds(IntPtr window)
    {
        if (!GetWindowRect(window, out Rect rect))
            throw new InvalidOperationException("Synthetic window geometry is unavailable.");
        return rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom;
    }

    public static void RunFixture(bool tray = false)
    {
        const uint visible = 0x10000000, child = 0x40000000;
        const uint popup = 0x80000000, overlapped = 0x00CF0000;
        const uint groupBox = 0x00000007, border = 0x00800000;
        const uint showNoActivate = 0x0050;
        int desktopX = GetSystemMetrics(76), desktopY = GetSystemMetrics(77);
        int desktopW = GetSystemMetrics(78), desktopH = GetSystemMetrics(79);
        if (desktopW < 700 || desktopH < 500)
            throw new InvalidOperationException("Synthetic smoke requires a 700x500 physical desktop.");
        IntPtr background = CreateWindowEx(0, "STATIC", "", popup,
            desktopX, desktopY, desktopW, desktopH,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (background == IntPtr.Zero) throw new InvalidOperationException("Synthetic background creation failed.");
        IntPtr root = IntPtr.Zero;
        try
        {
            if (!tray && !SetWindowPos(background, new IntPtr(-1), desktopX, desktopY,
                    desktopW, desktopH, showNoActivate))
                throw new InvalidOperationException("Synthetic background show failed.");
            UpdateWindow(background);
            int x = desktopX + 80, y = desktopY + 80;
            root = CreateWindowEx(0, "STATIC", "VibeOCR synthetic UIA",
                overlapped, x, y, 500, 320,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (root == IntPtr.Zero) throw new InvalidOperationException("Synthetic root creation failed.");
            IntPtr group = CreateWindowEx(0, "BUTTON", "Container",
                child | visible | groupBox, 20, 20, 430, 230,
                root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            IntPtr button = CreateWindowEx(0, "BUTTON", "Synthetic button",
                child | visible, 30, 50, 140, 42,
                group, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            IntPtr edit = CreateWindowEx(0, "EDIT", "",
                child | visible | border, 30, 120, 180, 38,
                group, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (group == IntPtr.Zero || button == IntPtr.Zero || edit == IntPtr.Zero ||
                !SetWindowPos(root, new IntPtr(tray ? 0 : -1), x, y, 500, 320, showNoActivate))
                throw new InvalidOperationException("Synthetic UIA controls or show failed.");
            UpdateWindow(root);
            Console.WriteLine(Environment.ProcessId + "|" + GetCurrentThreadId() + "|" +
                root.ToInt64() + "|" + group.ToInt64() + "|" + button.ToInt64() + "|" +
                edit.ToInt64() + "|" + background.ToInt64() + "|" + Bounds(root) +
                "|" + Bounds(group) + "|" + Bounds(button) + "|" + Bounds(edit) +
                "|" + Bounds(background));
            Console.Out.Flush();
            while (GetMessage(out Message message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (root != IntPtr.Zero) DestroyWindow(root);
            DestroyWindow(background);
        }
    }

    public static WindowInfo[] Windows(int pid)
    {
        var found = new List<WindowInfo>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == (uint)pid && GetWindowRect(window, out Rect bounds))
                found.Add(new WindowInfo { Handle = window.ToInt64(), Visible = IsWindowVisible(window), Iconic = IsIconic(window), ClassName = ClassName(window), Dpi = GetDpiForWindow(window), Bounds = bounds });
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }

    public static void RequireOwner(long handle, int pid)
    {
        IntPtr window = new IntPtr(handle);
        if (pid <= 0 || !IsWindow(window)) throw new InvalidOperationException("Synthetic or app window is gone.");
        GetWindowThreadProcessId(window, out uint owner);
        if (owner != (uint)pid) throw new InvalidOperationException("Window owner is outside this smoke run.");
    }

    private static void RequireForeground(int pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
        if (pid <= 0 || owner != (uint)pid)
            throw new InvalidOperationException("Foreground window is outside this smoke run.");
    }

    public static long Foreground(int appPid)
    {
        RequireForeground(appPid);
        return GetForegroundWindow().ToInt64();
    }

    public static object Probe(int appPid, int fixturePid, int x, int y)
    {
        IntPtr foreground = GetForegroundWindow();
        IntPtr hit = WindowFromPoint(new Point { X = x, Y = y });
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        GetWindowThreadProcessId(hit, out uint hitPid);
        return new {
            ForegroundHandle = foreground.ToInt64(), ForegroundPid = foregroundPid,
            HitHandle = hit.ToInt64(), HitPid = hitPid,
            AppWindows = Windows(appPid), FixtureWindows = Windows(fixturePid),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public uint Size, Flags;
        public IntPtr Handle;
        public Point Position;
    }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern IntPtr LoadCursor(IntPtr instance, int id);

    // Read-only observation of the global cursor, compared against the shared
    // handles of the standard system cursors. No input is injected here.
    public static object CursorState()
    {
        var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>(), Flags = 1 };
        if (!GetCursorInfo(ref info))
            throw new InvalidOperationException("Global cursor observation failed.");
        bool Standard(int id) =>
            info.Handle != IntPtr.Zero && info.Handle == LoadCursor(IntPtr.Zero, id);
        return new {
            shown = (info.Flags & 1) != 0,
            handle = info.Handle.ToInt64(),
            x = info.Position.X, y = info.Position.Y,
            arrow = Standard(32512), cross = Standard(32515),
            sizeNWSE = Standard(32642), sizeNESW = Standard(32643),
            sizeWE = Standard(32644), sizeNS = Standard(32645), sizeAll = Standard(32646),
        };
    }

    private static void RequirePointOwner(int x, int y, int pid)
    {
        GetWindowThreadProcessId(WindowFromPoint(new Point { X = x, Y = y }), out uint owner);
        if (pid <= 0 || owner != (uint)pid)
            throw new InvalidOperationException("Input point is outside this smoke run.");
    }

    public static void Hide(long handle, int pid)
    {
        RequireOwner(handle, pid);
        ShowWindow(new IntPtr(handle), 0);
    }

    public static void Close(long handle, int pid)
    {
        RequireOwner(handle, pid);
        if (!PostMessage(new IntPtr(handle), 0x0010, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Synthetic app close failed.");
    }

    public static void Quit(long handle, int pid, uint threadId)
    {
        RequireOwner(handle, pid);
        uint ownerThread = GetWindowThreadProcessId(new IntPtr(handle), out _);
        if (threadId == 0 || ownerThread != threadId ||
            !PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Synthetic fixture quit failed.");
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint type);
    private static Input Key(ushort key, bool up) => new Input
    {
        Type = 1,
        Data = new InputUnion { Key = new KeyInput { Scan = (ushort)MapVirtualKeyW(key, 0), Flags = 0x0008u | (key >= 0x21 && key <= 0x28 ? 1u : 0u) | (up ? 2u : 0u) } }
    };
    private static Input Mouse(uint flag) => new Input
    {
        Type = 0,
        Data = new InputUnion { Mouse = new MouseInput { Flags = flag } }
    };
    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Synthetic input was not fully delivered.");
    }

    public static void FocusFixture(long handle, int fixturePid, int x, int y)
    {
        RequireOwner(handle, fixturePid);
        IntPtr window = new IntPtr(handle);
        bool wasTopmost = (GetWindowLongPtr(window, -20).ToInt64() & 8) != 0;
        if (!SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0013))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned test window could not be exposed.");
        try
        {
            RequirePointOwner(x, y, fixturePid);
            if (GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2) != window)
                throw new InvalidOperationException("Synthetic focus point is outside the owned fixture root.");
            if (!SetCursorPos(x, y)) throw new InvalidOperationException("Synthetic cursor move failed.");
            RequirePointOwner(x, y, fixturePid);
            try { Send(new[] { Mouse(0x0002) }); }
            finally { Send(new[] { Mouse(0x0004) }); }
            for (int attempt = 0; attempt < 50; attempt++)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundPid);
                if (foregroundPid == (uint)fixturePid) break;
                System.Threading.Thread.Sleep(20);
            }
            RequireForeground(fixturePid);
        }
        finally
        {
            if (!wasTopmost && !SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, 0x0013))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned test window topmost state could not be restored.");
        }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr state);

    public static Rect WebViewBounds(long handle, int appPid)
    {
        RequireOwner(handle, appPid);
        var matches = new List<Rect>();
        EnumChildWindows(new IntPtr(handle), (child, _) => {
            var name = new StringBuilder(256);
            GetClassName(child, name, name.Capacity);
            GetWindowThreadProcessId(child, out uint owner);
            if (owner == (uint)appPid && IsWindowVisible(child) &&
                name.ToString() == "Microsoft.UI.Content.DesktopChildSiteBridge" &&
                GetAncestor(child, 2).ToInt64() == handle && GetWindowRect(child, out Rect bounds))
                matches.Add(bounds);
            return true;
        }, IntPtr.Zero);
        if (matches.Count != 1)
            throw new InvalidOperationException("Expected exactly one visible owned WebView host.");
        return matches[0];
    }

    public static void Hover(int appPid, int x, int y)
    {
        RequireForeground(appPid);
        RequirePointOwner(x, y, appPid);
        Send(new[] { new Input
        {
            Type = 0,
            Data = new InputUnion { Mouse = new MouseInput
            {
                X = (int)(((long)x - GetSystemMetrics(76)) * 65536 / GetSystemMetrics(78)),
                Y = (int)(((long)y - GetSystemMetrics(77)) * 65536 / GetSystemMetrics(79)),
                Flags = 0x0001 | 0x4000 | 0x8000,
            } },
        } });
    }

    private static void Tap(ushort key)
    {
        try { Send(new[] { Key(key, false) }); }
        finally { Send(new[] { Key(key, true) }); }
    }

    public static void Hotkey(int foregroundPid, ushort key)
    {
        RequireForeground(foregroundPid);
        // Ctrl+Alt+Shift+F10/F11, matching public settings fields in this smoke.
        try { Send(new[] { Key(0x11, false), Key(0x12, false), Key(0x10, false), Key(key, false) }); }
        finally { Send(new[] { Key(key, true), Key(0x10, true), Key(0x12, true), Key(0x11, true) }); }
    }

    public static void NotificationMenuKey()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundPid);
        GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out uint shellPid);
        if (shellPid == 0 || foregroundPid != shellPid) throw new InvalidOperationException("Keyboard foreground is not the owned icon's Shell.");
        try { Send(new[] { Key(0x10, false), Key(0x79, false) }); }
        finally { Send(new[] { Key(0x79, true), Key(0x10, true) }); }
    }

    public static void OverlayKey(int appPid, ushort key)
    {
        RequireForeground(appPid);
        Tap(key);
    }
}
'@

function Get-OwnedTrayTarget {
    $rect = [NativeActionsFixture]::TrayRect($Handle, $AppPid, $IconGuid)
    $rootHandle = [NativeActionsFixture]::TrayShellRoot($Handle, $AppPid, $IconGuid)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$rootHandle)
    # Query only our tooltip and fixed Shell labels; never collect other icon names.
    $names = @('VibeOCR', '显示隐藏的图标', '隐藏的图标菜单', 'Show hidden icons', 'Hidden icon menu')
    $conditions = [System.Windows.Automation.Condition[]]@($names | ForEach-Object {
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $_)
    })
    $matches = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree,
        [System.Windows.Automation.OrCondition]::new($conditions))
    $centerX = ($rect.Left + $rect.Right) / 2
    $centerY = ($rect.Top + $rect.Bottom) / 2
    $targets = @($matches | Where-Object {
        $bounds = $_.Current.BoundingRectangle
        $bounds.Left -le $centerX -and $bounds.Right -ge $centerX -and
        $bounds.Top -le $centerY -and $bounds.Bottom -ge $centerY
    })
    if ($targets.Count -ne 1) { throw 'Owned GUID notification target is missing or ambiguous.' }
    @{ Element = $targets[0]; Rect = $rect; IsChevron = $targets[0].Current.Name -ne 'VibeOCR' }
}

$oldDpi = [NativeActionsFixture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($oldDpi -eq [IntPtr]::Zero) { throw 'Per Monitor V2 native control context unavailable' }
try {
    switch ($Action) {
        'tray-fixture' { [NativeActionsFixture]::RunFixture($true) }
        'fixture' { [NativeActionsFixture]::RunFixture() }
        'selection' {
            if (-not ([NativeActionsFixture]::Windows($AppPid) | Where-Object { $_.Handle -eq $Handle -and $_.Visible })) {
                throw 'Selection window is outside the owned app.'
            }
            Add-Type -AssemblyName UIAutomationClient
            Add-Type -AssemblyName UIAutomationTypes
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
            $nodes = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.Condition]::TrueCondition)
            $labels = @($nodes | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match ' · \d+ × \d+ px · Enter' })
            if ($labels.Count -ne 1) { throw 'Owned picker selection label is unavailable or ambiguous.' }
            $labels[0]
        }
        'cursor' { [NativeActionsFixture]::CursorState() | ConvertTo-Json -Compress }
        'magnifier' {
            if (-not ([NativeActionsFixture]::Windows($AppPid) | Where-Object { $_.Handle -eq $Handle -and $_.Visible })) {
                throw 'Magnifier observation window is outside the owned app.'
            }
            Add-Type -AssemblyName UIAutomationClient
            Add-Type -AssemblyName UIAutomationTypes
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
            $nodes = $root.FindAll([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.Condition]::TrueCondition)
            $labels = @($nodes | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^-?\d+, -?\d+\r?\n#[0-9A-F]{6}\r?\nRGB \d+, \d+, \d+$' })
            if ($labels.Count -ne 1) { throw 'Owned picker magnifier label is unavailable or ambiguous.' }
            $labels[0]
        }
        'windows' { [NativeActionsFixture]::Windows($AppPid) | ConvertTo-Json -Compress -Depth 4 }
        'minimize' { [NativeActionsFixture]::Show($Handle, $AppPid, 6) }
        'restore' { [NativeActionsFixture]::Show($Handle, $AppPid, 9) }
        'tray-state' { [NativeActionsFixture]::TrayState($AppPid, $FixturePid, $Handle) | ConvertTo-Json -Compress -Depth 5 }
        'tray-expose' {
            try { $target = Get-OwnedTrayTarget }
            catch {
                if ($_.Exception.Message -notlike '*Owned GUID point is not on the Shell*') { throw }
                # Hidden overflow can retain an icon rectangle while its popup is closed.
                Add-Type -AssemblyName UIAutomationClient
                Add-Type -AssemblyName UIAutomationTypes
                $taskbar = [NativeActionsFixture]::TaskbarHandle()
                if (!$taskbar) { throw 'Shell taskbar is absent' }
                $shell = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$taskbar)
                $conditions = [System.Windows.Automation.Condition[]]@(@('显示隐藏的图标','隐藏的图标菜单','Show hidden icons','Hidden icon menu') | ForEach-Object {
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$_)
                })
                $chevrons = $shell.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.OrCondition]::new($conditions))
                if ($chevrons.Count -ne 1 -or $chevrons[0].Current.IsOffscreen) { throw 'Visible Shell overflow control missing or ambiguous' }
                $chevrons[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                $deadline = [DateTime]::UtcNow.AddSeconds(3)
                do {
                    try { $target = Get-OwnedTrayTarget; break }
                    catch {
                        if ($_.Exception.Message -notlike '*Owned GUID point is not on the Shell*' -and
                            $_.Exception.Message -notlike '*Owned GUID notification target is missing or ambiguous*') { throw }
                        if ([DateTime]::UtcNow -ge $deadline) { throw }
                        Start-Sleep -Milliseconds 100
                    }
                } while ($true)
            }
            if ($target.IsChevron) {
                $target.Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            }
            @{ IconRect = $target.Rect; IsChevron = $target.IsChevron } | ConvertTo-Json -Compress -Depth 3
        }
        'tray-keyboard' {
            $target = Get-OwnedTrayTarget
            if ($target.IsChevron) { throw 'Owned icon must be exposed before keyboard interaction.' }
            $target.Element.SetFocus()
            [NativeActionsFixture]::NotificationMenuKey()
            @{ IconRect = $target.Rect; Keyboard = $true } | ConvertTo-Json -Compress -Depth 3
        }
        'tray-keyboard-resume' {
            $target = Get-OwnedTrayTarget
            if ($target.IsChevron -or -not $target.Element.Current.HasKeyboardFocus) {
                throw 'Menu cancellation did not return keyboard focus to the owned tray icon.'
            }
            # Deliberately do not call SetFocus: verify Esc preserved continuous keyboard use.
            [NativeActionsFixture]::NotificationMenuKey()
            @{ IconRect = $target.Rect; Keyboard = $true; Refocused = $false } | ConvertTo-Json -Compress -Depth 3
        }
        { $_ -in @('tray-click', 'tray-left-click', 'tray-double-click') } {
            $target = Get-OwnedTrayTarget
            if ($target.IsChevron) { throw 'Owned icon must be exposed before mouse interaction.' }
            [NativeActionsFixture]::TrayClick($Handle, $AppPid, $IconGuid, $Action -ne 'tray-click', $Action -eq 'tray-double-click') |
                ConvertTo-Json -Compress -Depth 3
        }
        'tray-gone' { [NativeActionsFixture]::TrayGone($Handle, $IconGuid) | ConvertTo-Json -Compress }
        'tray-menu-quit' { [NativeActionsFixture]::ClickMenuItem($Handle, $AppPid, '退出 VibeOCR') | ConvertTo-Json -Compress -Depth 3 }
        'tray-menu-open' { [NativeActionsFixture]::ClickMenuItem($Handle, $AppPid, '打开工作台') | ConvertTo-Json -Compress -Depth 3 }
        'tray-menu-toggle' { [NativeActionsFixture]::ClickMenuItem($Handle, $AppPid, '悬浮工具栏') | ConvertTo-Json -Compress -Depth 3 }
        'taskbar-created' { [NativeActionsFixture]::TaskbarCreated($Handle, $AppPid) }
        'down' { [NativeActionsFixture]::OverlayKey($AppPid, 0x28) }
        'hide' { [NativeActionsFixture]::Hide($Handle, $AppPid) }
        'close' { [NativeActionsFixture]::Close($Handle, $AppPid) }
        'quit' { [NativeActionsFixture]::Quit($Handle, $FixturePid, $ThreadId) }
        'focus-fixture' { [NativeActionsFixture]::FocusFixture($Handle, $FixturePid, $X, $Y) }
        'webview-bounds' { [NativeActionsFixture]::WebViewBounds($Handle, $AppPid) | ConvertTo-Json -Compress }
        'hotkey' { [NativeActionsFixture]::Hotkey($FixturePid, 0x79) }
        'recognize-hotkey' { [NativeActionsFixture]::Hotkey($ForegroundPid, 0x7A) }
        'foreground' { [NativeActionsFixture]::Foreground($AppPid) }
        'probe' { [NativeActionsFixture]::Probe($AppPid, $FixturePid, $X, $Y) | ConvertTo-Json -Compress -Depth 5 }
        'hover' { [NativeActionsFixture]::Hover($AppPid, $X, $Y) }
        'tab' { [NativeActionsFixture]::OverlayKey($AppPid, 0x09) }
        'enter' { [NativeActionsFixture]::OverlayKey($AppPid, 0x0D) }
        'escape' { [NativeActionsFixture]::OverlayKey($AppPid, 0x1B) }
    }
} finally {
    [void][NativeActionsFixture]::SetThreadDpiAwarenessContext($oldDpi)
}
