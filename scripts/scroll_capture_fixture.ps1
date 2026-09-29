# Owned synthetic scrollable window plus bounded native input/UIA helpers for the
# scroll capture smoke. Every action only touches windows owned by this run:
# the fixture process it started or the app PID supplied by the harness.
param(
    [Parameter(Mandatory = $true)][ValidateSet(
        'fixture', 'windows', 'probe', 'foreground', 'focus', 'wheel',
        'mouse-move', 'mouse-down', 'mouse-up', 'key', 'hide', 'close', 'quit',
        'uia-find', 'uia-invoke', 'uia-bar-text')][string]$Action,
    [int]$AppPid = 0,
    [int]$FixturePid = 0,
    [long]$Handle = 0,
    [uint32]$ThreadId = 0,
    [int]$X = 0,
    [int]$Y = 0,
    [int]$Delta = -120,
    [string]$Key = 'enter',
    [string]$AutomationId = ''
)

$ErrorActionPreference = 'Stop'
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
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

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
        if (GetSystemMetrics(78) < 700 || GetSystemMetrics(79) < 500)
            throw new InvalidOperationException("Scroll smoke requires a 700x500 physical desktop.");
        using (var form = new ScrollFixtureForm())
        {
            form.Location = new Point(GetSystemMetrics(76) + 120, GetSystemMetrics(77) + 120);
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
                found.Add(new WindowInfo { Handle = window.ToInt64(), Visible = IsWindowVisible(window), Bounds = bounds });
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

    public static void FocusFixture(long handle, int fixturePid, int x, int y)
    {
        RequireOwner(handle, fixturePid);
        RequirePointOwner(x, y, fixturePid);
        if (GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2) != new IntPtr(handle))
            throw new InvalidOperationException("Focus point is outside the scroll fixture root.");
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Cursor move failed.");
        SetForegroundWindow(new IntPtr(handle));
        try { Send(new[] { Mouse(0x0002) }); }
        finally { Send(new[] { Mouse(0x0004) }); }
        RequireForegroundInternal(fixturePid);
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
        if (!ScreenToClient(root, ref point))
            throw new InvalidOperationException("Wheel point conversion failed.");
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
            X = (int)(((long)x - GetSystemMetrics(76)) * 65536 / GetSystemMetrics(78)),
            Y = (int)(((long)y - GetSystemMetrics(77)) * 65536 / GetSystemMetrics(79)),
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
    private static readonly string RowText =
        "行 {0:D4} · line {0:D4} · VibeOCR 滚动拼接校验 ScrollStitch abc-0123456789";
    private int _scrollOffset;

    public ScrollFixtureForm()
    {
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
        g.Clear(Color.White);
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
            g.DrawString(string.Format(RowText, row), Font, Brushes.Black, TextLeft, y + 9);
        }
    }

    protected override void WndProc(ref Message m)
    {
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
        'windows' { [ScrollCaptureNative]::Windows($AppPid) | ConvertTo-Json -Compress -Depth 4 }
        'probe' { [ScrollCaptureNative]::Probe($AppPid, $FixturePid, $X, $Y) | ConvertTo-Json -Compress -Depth 5 }
        'foreground' { [ScrollCaptureNative]::Foreground($FixturePid) }
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
            $invokable.Invoke()
            Convert-ElementToEvidence $found | ConvertTo-Json -Compress
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
