# Owned synthetic scrollable window plus bounded native input/UIA helpers for the
# scroll capture smoke. Every action only touches windows owned by this run:
# the fixture process it started or the app PID supplied by the harness.
param(
    [Parameter(Mandatory = $true)][ValidateSet(
        'fixture', 'windows', 'taskbar-state', 'probe', 'foreground', 'focus', 'raise', 'wheel', 'scenario',
        'mouse-move', 'mouse-down', 'mouse-up', 'key', 'hide', 'lower', 'close', 'quit',
        'uia-find', 'uia-invoke', 'uia-bar-text', 'metrics', 'save-file', 'frame', 'geometry', 'window-text')][string]$Action,
    [int]$AppPid = 0,
    [int]$FixturePid = 0,
    [long]$Handle = 0,
    [uint32]$ThreadId = 0,
    [int]$X = 0,
    [int]$Y = 0,
    [int]$Delta = -120,
    [ValidateSet('normal', 'low-texture', 'dynamic', 'jump', 'move')][string]$Scenario = 'normal',
    [string]$Key = 'enter',
    [string]$AutomationId = '',
    [string]$OutputPath = '',
    [string]$EvidenceRoot = '',
    [int]$Width = 0,
    [int]$Height = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$references = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter *.dll | ForEach-Object FullName) + @([System.Windows.Forms.Form].Assembly.Location, [System.Drawing.Font].Assembly.Location, (Join-Path $PSHOME 'System.Windows.Forms.Primitives.dll'), (Join-Path $PSHOME 'System.Private.Windows.Core.dll'), (Join-Path $PSHOME 'System.Private.Windows.GdiPlus.dll'))
Add-Type -ReferencedAssemblies ($references | Select-Object -Unique) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class ScrollCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData { public uint Size; public IntPtr Window; public uint Callback, Edge; public Rect Bounds; public IntPtr Param; }
    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    public static uint TaskbarState()
    {
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        return (uint)SHAppBarMessage(4, ref data).ToUInt64();
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint { public int X, Y; }
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
        public bool Enabled { get; set; }
        public long ExtendedStyle { get; set; }
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
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetText(IntPtr window, uint message, IntPtr wp, string text, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ReadText(IntPtr window, uint message, int count, System.Text.StringBuilder text, uint flags, uint timeout, out IntPtr result);
    private static string ClassName(IntPtr window) {
        var value = new System.Text.StringBuilder(128);
        GetClassNameW(window, value, value.Capacity);
        return value.ToString();
    }
    public static void SaveFile(long mainHandle, int appPid, string output) {
        RequireOwner(mainHandle, appPid);
        var main = new IntPtr(mainHandle);
        IntPtr dialog = IntPtr.Zero, edit = IntPtr.Zero, confirm = IntPtr.Zero;
        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until) {
            EnumWindows((window, _) => {
                if (IsWindowVisible(window) && ClassName(window) == "#32770" && GetWindowLongPtrW(window, -8) == main) {
                    dialog = window; return false;
                }
                return true;
            }, IntPtr.Zero);
            if (dialog != IntPtr.Zero) break;
            System.Threading.Thread.Sleep(100);
        }
        if (dialog == IntPtr.Zero) throw new InvalidOperationException("Owned save picker not found.");
        EnumChildWindows(dialog, (child, _) => {
            if (!IsWindowVisible(child)) return true;
            int id = GetDlgCtrlID(child);
            if (ClassName(child) == "Edit" && (id == 1001 || id == 1148)) {
                if (edit != IntPtr.Zero) throw new InvalidOperationException("Ambiguous filename field.");
                edit = child;
            }
            if (ClassName(child) == "Button" && id == 1) confirm = child;
            return true;
        }, IntPtr.Zero);
        if (edit == IntPtr.Zero || confirm == IntPtr.Zero || GetWindowLongPtrW(dialog, -8) != main)
            throw new InvalidOperationException("Owned save controls unavailable.");
        if (SetText(edit, 0x000C, IntPtr.Zero, output, 2, 1000, out IntPtr result) == IntPtr.Zero || result == IntPtr.Zero)
            throw new InvalidOperationException("Save filename rejected.");
        var readBack = new System.Text.StringBuilder(output.Length + 2);
        if (ReadText(edit, 0x000D, readBack.Capacity, readBack, 2, 1000, out _) == IntPtr.Zero || readBack.ToString() != output)
            throw new InvalidOperationException("Save filename mismatch.");
        until = DateTime.UtcNow.AddSeconds(5);
        while (!IsWindowEnabled(confirm) && DateTime.UtcNow < until) System.Threading.Thread.Sleep(100);
        if (!IsWindowEnabled(confirm) || GetWindowLongPtrW(dialog, -8) != main ||
            !PostMessage(dialog, 0x0111, new IntPtr(1), confirm))
            throw new InvalidOperationException("Save confirmation failed.");
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    public static object Geometry(long handle, int fixturePid) {
        RequireOwner(handle, fixturePid);
        var window = new IntPtr(handle);
        GetWindowRect(window, out Rect outer);
        GetClientRect(window, out Rect inner);
        var point = new NativePoint(); ClientToScreen(window, ref point);
        return new { outer, client = new { X=point.X, Y=point.Y, Width=inner.Right, Height=inner.Bottom }, dpi=GetDpiForWindow(window) };
    }
    public static void CaptureOwned(long handle, int fixturePid, int x, int y, int width, int height, string output) {
        RequireOwner(handle, fixturePid);
        if(width<=0||height<=0||(long)width*height>8000000) throw new InvalidOperationException("Invalid owned frame size.");
        RequirePointOwner(x,y,fixturePid);RequirePointOwner(x+width-1,y+height-1,fixturePid);
        using(var bitmap=new Bitmap(width,height)) {
            using(var graphics=Graphics.FromImage(bitmap)) graphics.CopyFromScreen(x,y,0,0,new Size(width,height));
            bitmap.Save(output,System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static string Bounds(IntPtr window)
    {
        if (!GetWindowRect(window, out Rect rect))
            throw new InvalidOperationException("Scroll fixture window geometry is unavailable.");
        return rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom;
    }

    public static void RunScrollFixture()
    {
        if (SetThreadDpiAwarenessContext(new IntPtr(-4)) == IntPtr.Zero)
            throw new InvalidOperationException("Per Monitor V2 scroll fixture context unavailable.");
        if (GetSystemMetrics(78) < 700 || GetSystemMetrics(79) < 720)
            throw new InvalidOperationException("Scroll smoke requires a 700x720 physical desktop.");
        using (var form = new ScrollFixtureForm())
        {
            form.Location = new Point(GetSystemMetrics(76) + 120, GetSystemMetrics(77) + Math.Min(700, GetSystemMetrics(79) - 420));
            _ = form.Handle; // Force handle creation so client screen coordinates are exact.
            Point client = form.PointToScreen(Point.Empty);
            UpdateWindow(form.Handle);
            Console.WriteLine(Environment.ProcessId + "|" + GetCurrentThreadId() + "|" +
                form.Handle.ToInt64() + "|" + Bounds(form.Handle) + "|" +
                client.X + "," + client.Y + "," + (client.X + form.ClientSize.Width) + "," +
                (client.Y + form.ClientSize.Height) + "|" +
                ScrollFixtureForm.RowCount + "|" + ScrollFixtureForm.RowHeight + "|" +
                ScrollFixtureForm.StripeWidth + "|" + ScrollFixtureForm.TextLeft + "|" +
                ScrollFixtureForm.WheelStepPixels);
            Console.Out.Flush();
            Application.Run(form);
        }
    }

    public static WindowInfo[] Windows(int pid)
    {
        var found = new List<WindowInfo>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == (uint)pid && GetWindowRect(window, out Rect bounds))
                found.Add(new WindowInfo { Handle = window.ToInt64(), Visible = IsWindowVisible(window), Enabled = IsWindowEnabled(window), ExtendedStyle = GetWindowLongPtrW(window, -20).ToInt64(), Bounds = bounds });
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }

    public static void RequireOwner(long handle, int pid)
    {
        IntPtr window = new IntPtr(handle);
        if (pid <= 0 || !IsWindow(window)) throw new InvalidOperationException("Owned window is gone.");
        GetWindowThreadProcessId(window, out uint owner);
        if (owner != (uint)pid) throw new InvalidOperationException("Window owner is outside this smoke run.");
    }

    public static long Foreground(int pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
        if (pid <= 0 || owner != (uint)pid)
            throw new InvalidOperationException("Foreground window is outside this smoke run.");
        return GetForegroundWindow().ToInt64();
    }

    public static object Probe(int appPid, int fixturePid, int x, int y)
    {
        IntPtr foreground = GetForegroundWindow();
        IntPtr hit = WindowFromPoint(new NativePoint { X = x, Y = y });
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
        GetWindowThreadProcessId(WindowFromPoint(new NativePoint { X = x, Y = y }), out uint owner);
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned window close failed.");
    }

    public static void Quit(long handle, int pid, uint threadId)
    {
        RequireOwner(handle, pid);
        uint ownerThread = GetWindowThreadProcessId(new IntPtr(handle), out _);
        if (threadId == 0 || ownerThread != threadId ||
            !PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Scroll fixture quit failed.");
    }

    public static void Scenario(long handle, int pid, int mode)
    {
        RequireOwner(handle, pid);
        if (mode < 0 || mode > 4 || !PostMessage(new IntPtr(handle), 0x8001, new IntPtr(mode), IntPtr.Zero))
            throw new InvalidOperationException("Owned fixture scenario failed.");
    }

    public static void Raise(long handle, int pid)
    {
        RequireOwner(handle, pid);
        if (!SetWindowPos(new IntPtr(handle), new IntPtr(-1), 0, 0, 0, 0, 0x0053))
            throw new InvalidOperationException("Owned fixture raise failed.");
    }

    public static void Lower(long handle, int pid)
    {
        RequireOwner(handle, pid);
        if (!SetWindowPos(new IntPtr(handle), new IntPtr(-2), 0, 0, 0, 0, 0x0013))
            throw new InvalidOperationException("Owned fixture demotion failed.");
    }

    public static void FocusFixture(long handle, int fixturePid, int x, int y)
    {
        RequireOwner(handle, fixturePid);
        bool wasTopmost = (GetWindowLongPtrW(new IntPtr(handle), -20).ToInt64() & 8) != 0;
        if (!SetWindowPos(new IntPtr(handle), new IntPtr(-1), 0, 0, 0, 0, 0x0003))
            throw new InvalidOperationException("Owned window activation failed.");
        try
        {
            SetForegroundWindow(new IntPtr(handle));
            System.Threading.Thread.Sleep(200);
            if (GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2) != new IntPtr(handle))
                throw new InvalidOperationException("Focus point is outside the owned window root.");
            if (!SetCursorPos(x, y)) throw new InvalidOperationException("Cursor move failed.");
            SetForegroundWindow(new IntPtr(handle));
            try { Send(new[] { Mouse(0x0002) }); }
            finally { Send(new[] { Mouse(0x0004) }); }
            RequireForegroundInternal(fixturePid);
        }
        finally
        {
            if (!wasTopmost) SetWindowPos(new IntPtr(handle), new IntPtr(-2), 0, 0, 0, 0, 0x0003);
        }
    }

    private static void RequireForegroundInternal(int pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
        if (pid <= 0 || owner != (uint)pid)
            throw new InvalidOperationException("Foreground window is outside this smoke run.");
    }

    public static void Wheel(long handle, int fixturePid, int x, int y, int delta)
    {
        RequireOwner(handle, fixturePid);
        IntPtr root = new IntPtr(handle);
        NativePoint point = new NativePoint { X = x, Y = y };
        IntPtr wParam = new IntPtr(((long)(short)delta << 16) & 0xFFFFFFFF);
        IntPtr lParam = new IntPtr(((point.Y & 0xFFFF) << 16) | (point.X & 0xFFFF));
        if (!PostMessage(root, 0x020A, wParam, lParam))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Wheel delivery to the owned fixture failed.");
    }

    private static Input Move(int x, int y) => new Input
    {
        Type = 0,
        Data = new InputUnion { Mouse = new MouseInput
        {
            X = (int)((((long)x - GetSystemMetrics(76)) * 65536 + 32768) / GetSystemMetrics(78)),
            Y = (int)((((long)y - GetSystemMetrics(77)) * 65536 + 32768) / GetSystemMetrics(79)),
            Flags = 0x0001 | 0x4000 | 0x8000,
        } },
    };
    private static Input Mouse(uint flag) => new Input
    {
        Type = 0,
        Data = new InputUnion { Mouse = new MouseInput { Flags = flag } },
    };
    private static Input KeyInputOf(ushort key, bool up) => new Input
    {
        Type = 1,
        Data = new InputUnion { Key = new KeyInput { Key = key, Flags = up ? 2u : 0u } },
    };
    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned input was not fully delivered.");
    }

    public static void MouseMove(int appPid, int x, int y)
    {
        RequireForeground(appPid);
        RequirePointOwner(x, y, appPid);
        Send(new[] { Move(x, y) });
    }

    public static void MouseButton(int appPid, int x, int y, bool down)
    {
        RequireForeground(appPid);
        RequirePointOwner(x, y, appPid);
        Send(new[] { Mouse(down ? 0x0002u : 0x0004u) });
    }

    public static void OverlayKey(int appPid, ushort key)
    {
        RequireForeground(appPid);
        try { Send(new[] { KeyInputOf(key, false) }); }
        finally { Send(new[] { KeyInputOf(key, true) }); }
    }

    private static void RequireForeground(int pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
        if (pid <= 0 || owner != (uint)pid)
            throw new InvalidOperationException("Foreground window is outside this smoke run.");
    }
}

public sealed class ScrollFixtureForm : Form
{
    public const int RowCount = 400;
    public const int RowHeight = 36;
    public const int StripeWidth = 24;
    public const int TextLeft = 32;
    public const int WheelStepPixels = 120;
    public const int FixtureClientWidth = 460;
    public const int FixtureClientHeight = 336;
    private const int WM_MOUSEWHEEL = 0x020A;
    private int _scrollOffset;
    private int _paintMode;
    private int _phase;
    private readonly System.Windows.Forms.Timer _animation = new System.Windows.Forms.Timer { Interval = 100 };

    public ScrollFixtureForm()
    {
        _animation.Tick += (_, _) => { _phase = (_phase + 1) % 239; Invalidate(); };
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        ClientSize = new Size(FixtureClientWidth, FixtureClientHeight);
        Font = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        Text = "VibeOCR scroll fixture 滚动校验";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(_paintMode == 2 ? Color.FromArgb(_phase, 128, 200) : Color.White);
        if (_paintMode != 0) return;
        int first = Math.Max(0, _scrollOffset / RowHeight - 1);
        int last = Math.Min(RowCount - 1, (_scrollOffset + ClientSize.Height) / RowHeight + 1);
        for (int row = first; row <= last; row++)
        {
            int y = row * RowHeight - _scrollOffset;
            using (SolidBrush stripe = new SolidBrush(
                Color.FromArgb(255, row & 0xFF, (row >> 8) & 0xFF, 0x40)))
            {
                g.FillRectangle(stripe, 0, y, StripeWidth, RowHeight);
            }
            g.DrawLine(Pens.Gray, StripeWidth, y, ClientSize.Width, y);
            g.DrawLine(Pens.Gray, 180, y, 180, y + RowHeight);
            g.DrawString($"行 {row:D4} / Row", Font, Brushes.Black, TextLeft, y + 9);
            g.DrawString($"中文 · line {row:D4} · VibeOCR", Font, Brushes.Black, 188, y + 9);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _animation.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x8001)
        {
            int mode = m.WParam.ToInt32();
            if (mode >= 0 && mode <= 2)
            {
                _paintMode = mode;
                _animation.Enabled = mode == 2;
                if (mode == 0) _scrollOffset = 0;
            }
            else if (mode == 3) _scrollOffset = Math.Min(RowCount * RowHeight - ClientSize.Height, _scrollOffset + 1000);
            else if (mode == 4) Left += 8;
            Invalidate();
            return;
        }
        if (m.Msg == WM_MOUSEWHEEL)
        {
            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            int maxOffset = RowCount * RowHeight - ClientSize.Height;
            int next = Math.Max(0, Math.Min(maxOffset,
                _scrollOffset + (delta < 0 ? WheelStepPixels : -WheelStepPixels)));
            if (next != _scrollOffset)
            {
                _scrollOffset = next;
                Invalidate();
            }
            return;
        }
        base.WndProc(ref m);
    }
}
'@

# UIA lookup is scoped to the app PID's own top-level windows (smallest window
# first) so the harness never performs a desktop-wide text scan.
function Find-AppElementById([int]$TargetPid, [string]$Id) {
    if ($TargetPid -le 0 -or [string]::IsNullOrWhiteSpace($Id)) {
        throw 'UIA lookup requires an app PID and an AutomationId.'
    }
    $owned = [ScrollCaptureNative]::Windows($TargetPid)
    $sorted = $owned | Sort-Object {
        ($_.Bounds.Right - $_.Bounds.Left) * ($_.Bounds.Bottom - $_.Bounds.Top)
    }
    foreach ($window in $sorted) {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.Handle)
        if ($null -eq $root) { continue }
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $condition)
        if ($element) {
            return @{ Element = $element; Window = $root; WindowHandle = $window.Handle }
        }
    }
    return $null
}

function Convert-ElementToEvidence($Found) {
    $current = $Found.Element.Current
    return @{
        found = $true
        automationId = $current.AutomationId
        name = $current.Name
        className = $current.ClassName
        enabled = $current.IsEnabled
        windowHandle = $Found.WindowHandle
        left = [math]::Round($current.BoundingRectangle.Left)
        top = [math]::Round($current.BoundingRectangle.Top)
        right = [math]::Round($current.BoundingRectangle.Right)
        bottom = [math]::Round($current.BoundingRectangle.Bottom)
    }
}

$oldDpi = [ScrollCaptureNative]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($oldDpi -eq [IntPtr]::Zero) { throw 'Per Monitor V2 helper context unavailable' }
try {
    switch ($Action) {
        'fixture' { [ScrollCaptureNative]::RunScrollFixture() }
        'taskbar-state' { [ScrollCaptureNative]::TaskbarState() }
        'save-file' {
            $rootPath = [IO.Path]::GetFullPath($EvidenceRoot).TrimEnd('\') + '\'
            $target = [IO.Path]::GetFullPath($OutputPath)
            if (-not $target.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetExtension($target) -ne '.png' -or (Test-Path -LiteralPath $target)) {
                throw 'Save target must be a new PNG inside this smoke evidence directory'
            }
            [ScrollCaptureNative]::SaveFile($Handle, $AppPid, $target)
        }
        'window-text' {
            [ScrollCaptureNative]::RequireOwner($Handle, $AppPid)
            $windowRoot = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
            $nodes = $windowRoot.FindAll([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.Condition]::TrueCondition)
            @($nodes | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 40) | ConvertTo-Json -Compress
        }
        'geometry' { [ScrollCaptureNative]::Geometry($Handle, $FixturePid) | ConvertTo-Json -Compress -Depth 4 }
        'frame' {
            $rootPath = [IO.Path]::GetFullPath($EvidenceRoot).TrimEnd('\') + '\'
            $target = [IO.Path]::GetFullPath($OutputPath)
            if (-not $target.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetExtension($target) -ne '.png' -or (Test-Path -LiteralPath $target)) { throw 'Invalid new evidence frame path' }
            [ScrollCaptureNative]::CaptureOwned($Handle, $FixturePid, $X, $Y, $Width, $Height, $target)
        }
        'metrics' {
            if ($AppPid -le 0) { throw 'Owned app PID is required' }
            $process = [Diagnostics.Process]::GetProcessById($AppPid)
            $process.Refresh()
            @{ pid = $AppPid; observedUtc = [DateTime]::UtcNow.ToString('O'); workingSetBytes = $process.WorkingSet64;
               peakWorkingSetBytes = $process.PeakWorkingSet64; privateBytes = $process.PrivateMemorySize64;
               scope = 'WinUI app process only; OS lifetime peak includes startup, excludes WebView2/Runtime children' } | ConvertTo-Json -Compress
        }
        'windows' { [ScrollCaptureNative]::Windows($AppPid) | ConvertTo-Json -Compress -Depth 4 }
        'probe' { [ScrollCaptureNative]::Probe($AppPid, $FixturePid, $X, $Y) | ConvertTo-Json -Compress -Depth 5 }
        'foreground' { [ScrollCaptureNative]::Foreground($FixturePid) }
        'raise' { [ScrollCaptureNative]::Raise($Handle, $FixturePid) }
        'lower' { [ScrollCaptureNative]::Lower($Handle, $FixturePid) }
        'scenario' {
            $mode = switch ($Scenario) { 'normal' { 0 }; 'low-texture' { 1 }; 'dynamic' { 2 }; 'jump' { 3 }; 'move' { 4 } }
            [ScrollCaptureNative]::Scenario($Handle, $FixturePid, $mode)
        }
        'focus' { [ScrollCaptureNative]::FocusFixture($Handle, $FixturePid, $X, $Y) }
        'wheel' { [ScrollCaptureNative]::Wheel($Handle, $FixturePid, $X, $Y, $Delta) }
        'mouse-move' { [ScrollCaptureNative]::MouseMove($AppPid, $X, $Y) }
        'mouse-down' { [ScrollCaptureNative]::MouseButton($AppPid, $X, $Y, $true) }
        'mouse-up' { [ScrollCaptureNative]::MouseButton($AppPid, $X, $Y, $false) }
        'key' {
            $virtualKey = switch ($Key) {
                'tab' { 0x09 }
                'enter' { 0x0D }
                'escape' { 0x1B }
                default { throw "Unsupported owned key: $Key" }
            }
            [ScrollCaptureNative]::OverlayKey($AppPid, [uint16]$virtualKey)
        }
        'hide' { [ScrollCaptureNative]::Hide($Handle, $AppPid) }
        'close' { [ScrollCaptureNative]::Close($Handle, $(if ($FixturePid -gt 0) { $FixturePid } else { $AppPid })) }
        'quit' { [ScrollCaptureNative]::Quit($Handle, $FixturePid, $ThreadId) }
        'uia-find' {
            $found = Find-AppElementById $AppPid $AutomationId
            if ($null -eq $found) { @{ found = $false } | ConvertTo-Json -Compress }
            else { Convert-ElementToEvidence $found | ConvertTo-Json -Compress }
        }
        'uia-invoke' {
            $found = Find-AppElementById $AppPid $AutomationId
            if ($null -eq $found) { throw "AutomationId not found in app PID $($AppPid): $($AutomationId)" }
            if (-not $found.Element.Current.IsEnabled) {
                throw "AutomationId is disabled: $($AutomationId)"
            }
            $invokable = $found.Element.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern)
            $evidence = Convert-ElementToEvidence $found
            $invokable.Invoke()
            $evidence | ConvertTo-Json -Compress
        }
        'uia-bar-text' {
            $found = Find-AppElementById $AppPid $AutomationId
            if ($null -eq $found) { throw "AutomationId not found in app PID $($AppPid): $($AutomationId)" }
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $bar = $found.Element
            while ($true) {
                $parent = $walker.GetParent($bar)
                if ($null -eq $parent) { break }
                if ($parent -eq [System.Windows.Automation.AutomationElement]::RootElement) { break }
                if ($parent.Current.ProcessId -ne $AppPid) { break }
                $bar = $parent
            }
            $texts = New-Object System.Collections.Generic.List[string]
            $descendants = $bar.FindAll([System.Windows.Automation.TreeScope]::Subtree,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($item in $descendants) {
                if ($texts.Count -ge 200) { break }
                $name = $item.Current.Name
                if (-not [string]::IsNullOrWhiteSpace($name)) { $texts.Add($name) }
            }
            $joined = ($texts -join ' | ')
            if ($joined.Length -gt 500) { $joined = $joined.Substring(0, 500) }
            @{ windowHandle = $bar.Current.NativeWindowHandle; text = $joined } | ConvertTo-Json -Compress
        }
    }
} finally {
    [void][ScrollCaptureNative]::SetThreadDpiAwarenessContext($oldDpi)
}
