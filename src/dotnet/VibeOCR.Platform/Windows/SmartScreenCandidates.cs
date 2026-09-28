using System.Runtime.InteropServices;

namespace VibeOCR.Platform.Windows;

/// <summary>Visible external top-level windows in front-to-back order at capture time.</summary>
public sealed class SmartScreenCandidates
{
    public sealed record Window(nint Handle, PhysicalRectangle Bounds);

    private readonly IReadOnlyList<Window> _windows;
    public PhysicalRectangle Desktop { get; }
    public IReadOnlyList<Window> Windows => _windows;

    public SmartScreenCandidates(PhysicalRectangle desktop, IReadOnlyList<Window> windows)
    {
        desktop.Validate();
        Desktop = desktop;
        _windows = windows.ToArray();
    }

    public Window? Hit(PhysicalPoint point) =>
        _windows.FirstOrDefault(window => Contains(window.Bounds, point));

    public bool IsCurrent(Window window, PhysicalRectangle currentDesktop,
        Func<nint, PhysicalRectangle?> currentBounds) =>
        currentDesktop == Desktop && currentBounds(window.Handle) == window.Bounds;

    public PhysicalRectangle? ClipToCapture(PhysicalRectangle candidate, Window window)
    {
        PhysicalRectangle? insideWindow = Intersect(candidate, window.Bounds);
        return insideWindow is { } bounds ? Intersect(bounds, Desktop) : null;
    }

    public static PhysicalRectangle? Intersect(PhysicalRectangle first, PhysicalRectangle second)
    {
        long left = Math.Max((long)first.X, second.X);
        long top = Math.Max((long)first.Y, second.Y);
        long right = Math.Min((long)first.X + first.Width, (long)second.X + second.Width);
        long bottom = Math.Min((long)first.Y + first.Height, (long)second.Y + second.Height);
        return right > left && bottom > top
            ? new PhysicalRectangle(checked((int)left), checked((int)top),
                checked((int)(right - left)), checked((int)(bottom - top)))
            : null;
    }

    public PhysicalRectangle ToLocal(PhysicalRectangle absolute) =>
        absolute with { X = checked(absolute.X - Desktop.X), Y = checked(absolute.Y - Desktop.Y) };

    public static SmartScreenCandidates Capture(PhysicalRectangle desktop)
    {
        var windows = new List<Window>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint processId);
            if (processId != (uint)Environment.ProcessId &&
                TryReadWindowBounds(handle) is { } bounds &&
                Intersect(bounds, desktop) is not null)
            {
                windows.Add(new Window(handle, bounds));
            }
            return true;
        }, 0);
        return new SmartScreenCandidates(desktop, windows);
    }

    public static PhysicalRectangle? TryReadWindowBounds(nint handle)
    {
        if (handle == 0 || !IsWindow(handle) || !IsWindowVisible(handle) || IsIconic(handle))
            return null;
        nint style = GetWindowLongPtr(handle, -20);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0)
            return null;
        bool layered = (style.ToInt64() & 0x00080000) != 0;
        byte alpha = 0;
        uint flags = 0;
        bool attributesKnown = layered &&
            GetLayeredWindowAttributes(handle, out _, out alpha, out flags);
        if (!CanUseRectangularHitTest(style.ToInt64(), attributesKnown, alpha, flags))
            return null;
        if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 &&
            cloaked != 0)
            return null;
        if (DwmGetWindowAttribute(handle, 9, out Rect rect, Marshal.SizeOf<Rect>()) != 0 &&
            !GetWindowRect(handle, out rect))
            return null;
        long width = (long)rect.Right - rect.Left;
        long height = (long)rect.Bottom - rect.Top;
        return width > 0 && height > 0 && width <= int.MaxValue && height <= int.MaxValue
            ? new PhysicalRectangle(rect.Left, rect.Top, (int)width, (int)height)
            : null;
    }

    // UpdateLayeredWindow supports per-pixel alpha that a bounding rectangle cannot
    // describe. GetLayeredWindowAttributes then fails; only a known, uniformly opaque
    // layered window can participate in rectangle-based hit testing.
    internal static bool CanUseRectangularHitTest(
        long extendedStyle, bool attributesKnown, byte alpha, uint flags)
    {
        const long layered = 0x00080000;
        const long transparent = 0x00000020;
        const uint alphaFlag = 0x00000002;
        return (extendedStyle & transparent) == 0 &&
            ((extendedStyle & layered) == 0 ||
             (attributesKnown && flags == alphaFlag && alpha == 255));
    }

    public static PhysicalRectangle CurrentDesktop() => new(
        GetSystemMetrics(76), GetSystemMetrics(77),
        GetSystemMetrics(78), GetSystemMetrics(79));

    private static bool Contains(PhysicalRectangle bounds, PhysicalPoint point) =>
        (long)point.X >= bounds.X && (long)point.X < (long)bounds.X + bounds.Width &&
        (long)point.Y >= bounds.Y && (long)point.Y < (long)bounds.Y + bounds.Height;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint handle, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(
        nint handle, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint handle, uint attribute, out Rect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint handle, uint attribute, out int value, int size);
}
