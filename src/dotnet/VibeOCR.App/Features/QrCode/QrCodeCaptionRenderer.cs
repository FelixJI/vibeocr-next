using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeOCR.App.Features.QrCode;

/// <summary>
/// Minimal native GDI caption helper for generated QR/barcode images: measures
/// and renders a wrapped text strip as 32bpp BGRA pixels. Mirrors the P/Invoke
/// style of <c>VibeOCR.Platform/Windows/ScreenCaptureService.cs</c>
/// (CreateCompatibleDC + GetDIBits with Win32Exception on failure); introduces
/// no new text framework or dependency.
/// </summary>
internal static class QrCodeCaptionRenderer
{
  /// <summary>Horizontal padding kept between the text and the image border.</summary>
  public const int SidePadding = 8;

  private const int FontHeightPixels = 20;
  private const int FontWeightNormal = 400;
  private const uint DefaultCharset = 1;
  private const uint ClearTypeQuality = 5;
  private const uint BackgroundTransparent = 1;
  private const uint Whiteness = 0x00FF0062;
  private const uint DrawCenter = 0x0001;
  private const uint DrawWordBreak = 0x0010;
  private const uint DrawCalcRect = 0x0400;
  private const uint DrawNoPrefix = 0x0800;
  private const uint DrawEditControl = 0x2000;
  private const uint WrapFlags = DrawWordBreak | DrawEditControl | DrawNoPrefix;

  /// <summary>Measure the wrapped height of <paramref name="text"/> constrained to the given inner width.</summary>
  public static int MeasureHeight(int innerWidth, string text)
  {
    ArgumentException.ThrowIfNullOrEmpty(text);
    ArgumentOutOfRangeException.ThrowIfLessThan(innerWidth, 1);
    nint screen = GetDC(0);
    if (screen == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDC failed.");
    nint memory = 0;
    nint font = 0;
    nint previousFont = 0;
    try
    {
      (memory, font, previousFont) = CreateTextSurface(screen);
      var rect = new Rect { Left = 0, Top = 0, Right = innerWidth, Bottom = int.MaxValue };
      int height = DrawText(memory, text, text.Length, ref rect, WrapFlags | DrawCalcRect);
      if (height <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "DrawTextW measurement failed.");
      return height;
    }
    finally
    {
      ReleaseTextSurface(memory, font, previousFont);
      ReleaseDC(0, screen);
    }
  }

  /// <summary>Render <paramref name="text"/> onto a white BGRA strip of the given size, wrapped within the side padding.</summary>
  public static byte[] Render(int width, int height, string text)
  {
    ArgumentException.ThrowIfNullOrEmpty(text);
    ArgumentOutOfRangeException.ThrowIfLessThan(width, 2 * SidePadding + 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
    byte[] pixels = new byte[checked(width * height * 4)];
    nint screen = GetDC(0);
    if (screen == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDC failed.");
    nint memory = 0;
    nint bitmap = 0;
    nint font = 0;
    nint previousFont = 0;
    nint previousBitmap = 0;
    try
    {
      (memory, font, previousFont) = CreateTextSurface(screen);
      bitmap = CreateCompatibleBitmap(screen, width, height);
      if (bitmap == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateCompatibleBitmap failed.");
      previousBitmap = SelectObject(memory, bitmap);
      if (previousBitmap == 0 || previousBitmap == -1)
        throw new Win32Exception(Marshal.GetLastPInvokeError(), "SelectObject(bitmap) failed.");
      if (!PatBlt(memory, 0, 0, width, height, Whiteness))
        throw new Win32Exception(Marshal.GetLastPInvokeError(), "PatBlt failed.");
      var rect = new Rect { Left = SidePadding, Top = 0, Right = width - SidePadding, Bottom = height };
      int drawn = DrawText(memory, text, text.Length, ref rect, WrapFlags | DrawCenter);
      if (drawn <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "DrawTextW produced no output.");

      var info = new BitmapInfo
      {
        Header = new BitmapInfoHeader
        {
          Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
          Width = width,
          Height = -height,
          Planes = 1,
          BitCount = 32,
          Compression = 0,
          ImageSize = (uint)pixels.Length,
        },
      };
      int scanLines = GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0);
      if (scanLines != height)
        throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDIBits failed.");
      return pixels;
    }
    finally
    {
      if (previousFont != 0 && memory != 0) SelectObject(memory, previousFont);
      if (previousBitmap != 0 && memory != 0) SelectObject(memory, previousBitmap);
      if (font != 0) DeleteObject(font);
      if (bitmap != 0) DeleteObject(bitmap);
      if (memory != 0) DeleteDC(memory);
      ReleaseDC(0, screen);
    }
  }

  private static (nint Memory, nint Font, nint PreviousFont) CreateTextSurface(nint screen)
  {
    nint memory = CreateCompatibleDC(screen);
    if (memory == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateCompatibleDC failed.");
    nint font = CreateFontW(
      -FontHeightPixels, 0, 0, 0, FontWeightNormal, 0, 0, 0, DefaultCharset,
      0, 0, ClearTypeQuality, 0, "Microsoft YaHei UI");
    if (font == 0)
    {
      DeleteDC(memory);
      throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFontW failed.");
    }
    nint previousFont = SelectObject(memory, font);
    if (previousFont == 0 || previousFont == -1)
    {
      DeleteObject(font);
      DeleteDC(memory);
      throw new Win32Exception(Marshal.GetLastPInvokeError(), "SelectObject(font) failed.");
    }
    if (SetBkMode(memory, BackgroundTransparent) == 0)
    {
      SelectObject(memory, previousFont);
      DeleteObject(font);
      DeleteDC(memory);
      throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetBkMode failed.");
    }
    SetTextColor(memory, 0x00000000);
    return (memory, font, previousFont);
  }

  private static void ReleaseTextSurface(nint memory, nint font, nint previousFont)
  {
    if (previousFont != 0 && memory != 0) SelectObject(memory, previousFont);
    if (font != 0) DeleteObject(font);
    if (memory != 0) DeleteDC(memory);
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct Rect
  {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
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

  [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern int DrawText(
    nint deviceContext,
    string text,
    int count,
    ref Rect rectangle,
    uint format);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern nint CreateCompatibleDC(nint deviceContext);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern nint SelectObject(nint deviceContext, nint value);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern int SetBkMode(nint deviceContext, uint mode);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern uint SetTextColor(nint deviceContext, uint color);

  [DllImport("gdi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool PatBlt(
    nint deviceContext,
    int x,
    int y,
    int width,
    int height,
    uint rasterOperation);

  [DllImport("gdi32.dll", SetLastError = true)]
  private static extern int GetDIBits(
    nint deviceContext,
    nint bitmap,
    uint start,
    uint lines,
    [Out] byte[] bits,
    ref BitmapInfo info,
    uint usage);

  [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern nint CreateFontW(
    int height,
    int width,
    int escapement,
    int orientation,
    int weight,
    uint italic,
    uint underline,
    uint strikeOut,
    uint charSet,
    uint outputPrecision,
    uint clipPrecision,
    uint quality,
    uint pitchAndFamily,
    string faceName);

  [DllImport("gdi32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeleteObject(nint value);

  [DllImport("gdi32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeleteDC(nint deviceContext);
}
