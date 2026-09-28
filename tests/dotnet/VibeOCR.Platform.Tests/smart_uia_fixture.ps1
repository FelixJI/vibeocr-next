$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class SmartUiaFixture
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint BS_GROUPBOX = 0x00000007;
    private const uint WS_BORDER = 0x00800000;
    private const uint ES_LEFT = 0;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint LWA_ALPHA = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle, string className, string text, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static string Bounds(IntPtr window)
    {
        if (!GetWindowRect(window, out Rect rect))
            throw new InvalidOperationException("Synthetic GetWindowRect failed.");
        return rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom;
    }

    public static int Run()
    {
        if (SetThreadDpiAwarenessContext(new IntPtr(-4)) == IntPtr.Zero)
            throw new InvalidOperationException("Per-monitor-v2 synthetic window context unavailable.");
        int x = GetSystemMetrics(76) + 80;
        int y = GetSystemMetrics(77) + 80;
        IntPtr root = CreateWindowEx(0, "STATIC", "VibeOCR synthetic UIA",
            WS_OVERLAPPEDWINDOW, x, y, 500, 320,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (root == IntPtr.Zero)
            throw new InvalidOperationException("Synthetic root creation failed.");
        IntPtr veil = IntPtr.Zero;
        try
        {
            IntPtr group = CreateWindowEx(0, "BUTTON", "Container",
                WS_CHILD | WS_VISIBLE | BS_GROUPBOX, 20, 20, 430, 230,
                root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            IntPtr button = CreateWindowEx(0, "BUTTON", "Synthetic button",
                WS_CHILD | WS_VISIBLE, 30, 50, 140, 42,
                group, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            IntPtr edit = CreateWindowEx(0, "EDIT", "",
                WS_CHILD | WS_VISIBLE | WS_BORDER | ES_LEFT, 30, 120, 180, 38,
                group, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (group == IntPtr.Zero || button == IntPtr.Zero || edit == IntPtr.Zero ||
                !SetWindowPos(root, new IntPtr(-1), x, y, 500, 320,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW))
                throw new InvalidOperationException("Synthetic child creation or show failed.");

            // Above the fixture in z-order, but every pixel is globally transparent.
            // A rectangle-only hit test must not let it hide the button below.
            veil = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW, "STATIC", "",
                WS_POPUP, x, y, 500, 320,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (veil == IntPtr.Zero ||
                !SetLayeredWindowAttributes(veil, 0, 0, LWA_ALPHA) ||
                !SetWindowPos(veil, new IntPtr(-1), x, y, 500, 320,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW))
                throw new InvalidOperationException("Synthetic transparent layer creation failed.");

            Console.WriteLine(
                Environment.ProcessId + "|" + GetCurrentThreadId() + "|" +
                root.ToInt64() + "|" + group.ToInt64() + "|" +
                button.ToInt64() + "|" + edit.ToInt64() + "|" + veil.ToInt64() + "|" +
                Bounds(root) + "|" + Bounds(group) + "|" +
                Bounds(button) + "|" + Bounds(edit) + "|" + Bounds(veil));
            Console.Out.Flush();
            while (GetMessage(out Message message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            return 0;
        }
        finally
        {
            if (veil != IntPtr.Zero)
                DestroyWindow(veil);
            DestroyWindow(root);
        }
    }
}
'@

exit [SmartUiaFixture]::Run()
