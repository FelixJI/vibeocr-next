using System.ComponentModel;
using System.Globalization;
using System.Text;
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
  private const uint DrawSingleLine = 0x0020;
  private const uint DrawCalcRect = 0x0400;
  private const uint DrawNoPrefix = 0x0800;
  private const uint DrawEditControl = 0x2000;
  private const uint TextFlags = DrawEditControl | DrawNoPrefix;

  /// <summary>Measure and draw the same explicitly wrapped grapheme lines on one native text surface.</summary>
  public static (byte[] Pixels, int Height) RenderStrip(int width, string text)
  {
    ArgumentException.ThrowIfNullOrEmpty(text);
    ArgumentOutOfRangeException.ThrowIfLessThan(width, 2 * SidePadding + 1);
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
      string wrapped = WrapText(memory, width - 2 * SidePadding, text);
      var measured = new Rect { Right = width - 2 * SidePadding, Bottom = int.MaxValue };
      int height = DrawText(memory, wrapped, wrapped.Length, ref measured, TextFlags | DrawCalcRect);
      if (height <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "DrawTextW measurement failed.");
      byte[] pixels = new byte[checked(width * height * 4)];
      bitmap = CreateCompatibleBitmap(screen, width, height);
      if (bitmap == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateCompatibleBitmap failed.");
      previousBitmap = SelectObject(memory, bitmap);
      if (previousBitmap == 0 || previousBitmap == -1)
        throw new Win32Exception(Marshal.GetLastPInvokeError(), "SelectObject(bitmap) failed.");
      if (!PatBlt(memory, 0, 0, width, height, Whiteness))
        throw new Win32Exception(Marshal.GetLastPInvokeError(), "PatBlt failed.");
      var rect = new Rect { Left = SidePadding, Top = 0, Right = width - SidePadding, Bottom = height };
      int drawn = DrawText(memory, wrapped, wrapped.Length, ref rect, TextFlags | DrawCenter);
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
      return (pixels, height);
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

  private static string WrapText(nint memory, int innerWidth, string text)
  {
    var wrapped = new StringBuilder();
    foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
    {

      string line = "";
      TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(paragraph);
      while (elements.MoveNext())
      {
        string element = elements.GetTextElement();
        string candidate = line + element;
        var bounds = new Rect { Right = innerWidth, Bottom = int.MaxValue };
        int measured = DrawText(memory, candidate, candidate.Length, ref bounds,
          DrawSingleLine | DrawNoPrefix | DrawCalcRect);
        if (measured <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "DrawTextW line measurement failed.");
        if (bounds.Right > innerWidth)
        {
          if (line.Length == 0) throw new ArgumentException("底部说明的单个字符超出图片宽度，请使用更宽的图片。", nameof(text));
          wrapped.Append(line).Append('\n');
          line = element;
          bounds = new Rect { Right = innerWidth, Bottom = int.MaxValue };
          if (DrawText(memory, line, line.Length, ref bounds, DrawSingleLine | DrawNoPrefix | DrawCalcRect) <= 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "DrawTextW character measurement failed.");
          if (bounds.Right > innerWidth) throw new ArgumentException("底部说明的单个字符超出图片宽度，请使用更宽的图片。", nameof(text));
        }
        else line = candidate;
      }
      wrapped.Append(line).Append('\n');
    }
    return wrapped.ToString(0, wrapped.Length - 1);
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
