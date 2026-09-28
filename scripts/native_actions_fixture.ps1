param(
    [Parameter(Mandatory = $true)][ValidateSet('fixture', 'windows', 'hide', 'close', 'quit', 'focus-fixture', 'hotkey', 'recognize-hotkey', 'foreground', 'probe', 'hover', 'tab', 'enter', 'escape')][string]$Action,
    [int]$AppPid = 0,
    [int]$FixturePid = 0,
    [int]$ForegroundPid = 0,
    [long]$Handle = 0,
    [uint32]$ThreadId = 0,
    [int]$X = 0,
    [int]$Y = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

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

    private static string Bounds(IntPtr window)
    {
        if (!GetWindowRect(window, out Rect rect))
            throw new InvalidOperationException("Synthetic window geometry is unavailable.");
        return rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom;
    }

    public static void RunFixture()
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
            if (!SetWindowPos(background, new IntPtr(-1), desktopX, desktopY,
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
                !SetWindowPos(root, new IntPtr(-1), x, y, 500, 320, showNoActivate))
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
                found.Add(new WindowInfo { Handle = window.ToInt64(), Visible = IsWindowVisible(window), Bounds = bounds });
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

    private static Input Key(ushort key, bool up) => new Input
    {
        Type = 1,
        Data = new InputUnion { Key = new KeyInput { Key = key, Flags = up ? 2u : 0u } }
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
        RequirePointOwner(x, y, fixturePid);
        if (GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2) != new IntPtr(handle))
            throw new InvalidOperationException("Synthetic focus point is outside the small fixture root.");
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Synthetic cursor move failed.");
        SetForegroundWindow(new IntPtr(handle));
        try { Send(new[] { Mouse(0x0002) }); }
        finally { Send(new[] { Mouse(0x0004) }); }
        RequireForeground(fixturePid);
    }

    public static void Hover(int appPid, int x, int y)
    {
        RequireForeground(appPid);
        RequirePointOwner(x, y, appPid);
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Synthetic hover failed.");
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

    public static void OverlayKey(int appPid, ushort key)
    {
        RequireForeground(appPid);
        Tap(key);
    }
}
'@

$oldDpi = [NativeActionsFixture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($oldDpi -eq [IntPtr]::Zero) { throw 'Per Monitor V2 native control context unavailable' }
try {
    switch ($Action) {
        'fixture' { [NativeActionsFixture]::RunFixture() }
        'windows' { [NativeActionsFixture]::Windows($AppPid) | ConvertTo-Json -Compress -Depth 4 }
        'hide' { [NativeActionsFixture]::Hide($Handle, $AppPid) }
        'close' { [NativeActionsFixture]::Close($Handle, $AppPid) }
        'quit' { [NativeActionsFixture]::Quit($Handle, $FixturePid, $ThreadId) }
        'focus-fixture' { [NativeActionsFixture]::FocusFixture($Handle, $FixturePid, $X, $Y) }
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
