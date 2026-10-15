using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeOCR.Platform.Windows;

public readonly record struct LogicalPoint(double X, double Y);

public readonly record struct PhysicalPoint(int X, int Y);

public readonly record struct PhysicalRectangle(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public void Validate()
    {
        if (Width <= 0 || Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), "Capture dimensions must be positive.");
        }
    }
}

public sealed record MonitorGeometry(
    LogicalPoint LogicalOrigin,
    PhysicalRectangle PhysicalBounds,
    int DpiX,
    int DpiY);

/// <summary>
/// A captured screen frame. In the v2 architecture screenshots are returned as
/// raw BGRA bytes (no shared memory payload).
/// </summary>
public sealed record CapturedFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    string PixelFormat);

public interface IScreenCaptureNativeMethods
{
    byte[] CaptureBgra(PhysicalRectangle bounds);

    /// <summary>
    /// 带可选光标叠加的捕获；默认在旧实现上忽略光标，保持既有桩兼容。
    /// </summary>
    byte[] CaptureBgra(PhysicalRectangle bounds, bool includeCursor) => CaptureBgra(bounds);
}

/// <summary>
/// 把真实系统光标绘制到已冻结的捕获表面（内存 DC）上。GDI BitBlt 不采集
/// 光标；启用“包含光标”时在读取像素前把 GetCursorInfo 的光标按热点与
/// 捕获原点偏移绘入。共享光标句柄不销毁；GetIconInfo 返回的临时位图句柄
/// 由调用方清理。
/// </summary>
public interface IScreenCursorOverlay
{
    bool TryDraw(nint memoryDc, PhysicalRectangle bounds);
}

public sealed class GdiScreenCursorOverlay : IScreenCursorOverlay
{
    private const int CursorShowing = 0x00000001;
    private const uint DiNormal = 0x0003;

    public bool TryDraw(nint memoryDc, PhysicalRectangle bounds)
    {
        var info = new CursorInfo { CbSize = (uint)Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Handle == 0)
        {
            return false;
        }

        // CURSORINFO.pt 是热点在屏幕上的位置；DrawIconEx 以左上角绘制。
        var icon = new IconInfo();
        int hotX = 0;
        int hotY = 0;
        if (GetIconInfo(info.Handle, ref icon))
        {
            hotX = icon.XHotspot;
            hotY = icon.YHotspot;
            if (icon.MaskBitmap != 0) DeleteObject(icon.MaskBitmap);
            if (icon.ColorBitmap != 0) DeleteObject(icon.ColorBitmap);
        }

        return TryDrawCursor(
            memoryDc, info.Handle, hotX, hotY, info.Position.X, info.Position.Y, bounds);
    }

    /// <summary>按热点偏移把指定光标句柄绘制到捕获表面的独立实现，供契约测试。</summary>
    internal static bool TryDrawCursor(
        nint memoryDc, nint cursor, int hotspotX, int hotspotY,
        int screenX, int screenY, PhysicalRectangle bounds)
    {
        if (memoryDc == 0 || cursor == 0)
        {
            return false;
        }

        int x = screenX - hotspotX - bounds.X;
        int y = screenY - hotspotY - bounds.Y;
        return DrawIconEx(memoryDc, x, y, cursor, 0, 0, 0, 0, DiNormal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public uint CbSize;
        public uint Flags;
        public nint Handle;
        public NativePoint Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int XHotspot;
        public int YHotspot;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint cursor, ref IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        nint deviceContext, int x, int y, nint icon, int width, int height,
        uint frameStep, nint brush, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}

public sealed class ScreenCaptureService : IAsyncDisposable
{
    private readonly IScreenCaptureNativeMethods _native;

    public ScreenCaptureService(
        Guid sessionId,
        IScreenCaptureNativeMethods? native = null,
        IScreenCursorOverlay? cursorOverlay = null)
    {
        _native = native ?? new GdiScreenCaptureNativeMethods(cursorOverlay ?? new GdiScreenCursorOverlay());
    }

    public static PhysicalPoint LogicalToPhysical(LogicalPoint point, MonitorGeometry monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (monitor.DpiX <= 0 || monitor.DpiY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitor), "Monitor DPI must be positive.");
        }

        monitor.PhysicalBounds.Validate();
        int x = monitor.PhysicalBounds.X + (int)Math.Round(
            (point.X - monitor.LogicalOrigin.X) * monitor.DpiX / 96.0,
            MidpointRounding.AwayFromZero);
        int y = monitor.PhysicalBounds.Y + (int)Math.Round(
            (point.Y - monitor.LogicalOrigin.Y) * monitor.DpiY / 96.0,
            MidpointRounding.AwayFromZero);
        return new PhysicalPoint(x, y);
    }

    public CapturedFrame Capture(PhysicalRectangle bounds, TimeSpan ttl, bool includeCursor = false)
    {
        bounds.Validate();
        int expected = checked(bounds.Width * bounds.Height * 4);
        byte[] pixels = _native.CaptureBgra(bounds, includeCursor);
        if (pixels.Length != expected)
        {
            throw new InvalidDataException(
                $"Native capture returned {pixels.Length} bytes; expected {expected} BGRA bytes.");
        }

        return new CapturedFrame(pixels, bounds.Width, bounds.Height, bounds.Width * 4, "BGRA8");
    }

    public byte[] Read(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.Pixels;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class GdiScreenCaptureNativeMethods(IScreenCursorOverlay? cursor = null)
        : IScreenCaptureNativeMethods
    {
        private const uint SourceCopy = 0x00CC0020;
        private const uint CaptureLayeredWindows = 0x40000000;

        public byte[] CaptureBgra(PhysicalRectangle bounds) => CaptureBgra(bounds, false);

        public byte[] CaptureBgra(PhysicalRectangle bounds, bool includeCursor)
        {
            nint screen = GetDC(0);
            if (screen == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDC failed.");
            }

            nint memory = 0;
            nint bitmap = 0;
            nint previous = 0;
            try
            {
                memory = CreateCompatibleDC(screen);
                bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
                if (memory == 0 || bitmap == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "GDI surface creation failed.");
                }

                previous = SelectObject(memory, bitmap);
                if (previous == 0 || previous == (nint)(-1) || !BitBlt(
                        memory,
                        0,
                        0,
                        bounds.Width,
                        bounds.Height,
                        screen,
                        bounds.X,
                        bounds.Y,
                        SourceCopy | CaptureLayeredWindows))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "BitBlt failed.");
                }

                // 光标叠加必须在 GetDIBits 读取像素前绘入同一 GDI 表面。
                if (includeCursor && cursor is not null)
                {
                    cursor.TryDraw(memory, bounds);
                }

                if (SelectObject(memory, previous) is 0 or -1)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to restore GDI surface.");
                }

                previous = 0;

                byte[] pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = bounds.Width,
                        Height = -bounds.Height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0,
                        ImageSize = (uint)pixels.Length,
                    },
                };
                int scanLines = GetDIBits(
                    memory,
                    bitmap,
                    0,
                    (uint)bounds.Height,
                    pixels,
                    ref info,
                    0);
                if (scanLines != bounds.Height)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDIBits failed.");
                }

                return pixels;
            }
            finally
            {
                if (previous != 0 && memory != 0)
                {
                    SelectObject(memory, previous);
                }

                if (bitmap != 0)
                {
                    DeleteObject(bitmap);
                }

                if (memory != 0)
                {
                    DeleteDC(memory);
                }

                ReleaseDC(0, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint ImageSize;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ColorsUsed;
            public uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetDC(nint window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(nint window, nint deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern nint CreateCompatibleDC(nint deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern nint SelectObject(nint deviceContext, nint value);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(
            nint destination,
            int destinationX,
            int destinationY,
            int width,
            int height,
            nint source,
            int sourceX,
            int sourceY,
            uint operation);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int GetDIBits(
            nint deviceContext,
            nint bitmap,
            uint start,
            uint lines,
            [Out] byte[] bits,
            ref BitmapInfo info,
            uint usage);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(nint value);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(nint deviceContext);
    }
}
