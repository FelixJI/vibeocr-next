using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VibeOCR.Platform.Windows;
using Windows.Graphics;

namespace VibeOCR.App.Features.Recognition;

// Only constructed by the isolated screenshot-e2e process. Never samples the desktop
// outside the client area of the synthetic WinUI window it creates.
internal sealed class SyntheticScreenRegionPicker : IScreenRegionPicker
{
  internal sealed record CaptureEvidence(int Width, int Height, int WhitePixels, int DarkPixels);

  internal CaptureEvidence? Evidence { get; private set; }
  internal string? Failure { get; private set; }

  public async Task<ScreenRegionSelection?> PickAsync(CancellationToken cancellationToken)
  {
    Failure = null;
    var window = new Window
    {
      Content = new Grid
      {
        Background = new SolidColorBrush(Microsoft.UI.Colors.White),
      },
    };
    nint child = 0;
    nint font = 0;
    WindowMessageService? messages = null;
    nint? OnWindowMessage(WindowMessage message)
    {
      if (message.Id != 0x0138 || message.LParam != child)
        return null;
      nint deviceContext = (nint)message.WParam;
      SetBkColor(deviceContext, 0x00FFFFFF);
      SetTextColor(deviceContext, 0);
      return GetStockObject(0);
    }
    try
    {
      ((OverlappedPresenter)window.AppWindow.Presenter).IsAlwaysOnTop = true;
      window.AppWindow.MoveAndResize(new RectInt32(100, 100, 1000, 300));
      window.Activate();
      nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
      messages = new WindowMessageService(handle);
      messages.MessageHandled += OnWindowMessage;
      if (!GetClientRect(handle, out Rect client) ||
          client.Right <= client.Left || client.Bottom <= client.Top)
      {
        throw new InvalidOperationException("Synthetic WinUI client bounds are unavailable.");
      }

      const uint childStyle = 0x40000000 | 0x10000000 | 0x00000001 | 0x00000200;
      child = CreateWindowExW(0, "STATIC", "VibeOCR 123", childStyle,
        0, 0, client.Right - client.Left, client.Bottom - client.Top,
        handle, 0, 0, 0);
      if (child == 0)
      {
        throw new InvalidOperationException(
          $"Synthetic text control creation failed: {Marshal.GetLastPInvokeError()}.");
      }
      font = CreateFontW(-72, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
      if (font == 0)
      {
        throw new InvalidOperationException(
          $"Synthetic text font creation failed: {Marshal.GetLastPInvokeError()}.");
      }
      SendMessageW(child, 0x0030, font, 1);
      if (!UpdateWindow(child))
      {
        // UpdateWindow returns zero when there is no invalid region to paint.
        if (!IsWindowVisible(child))
          throw new InvalidOperationException("Synthetic text control is not visible.");
      }

      if (!GetClientRect(child, out Rect childClient))
        throw new InvalidOperationException("Synthetic text control bounds are unavailable.");
      Point origin = new();
      if (!ClientToScreen(child, ref origin))
        throw new InvalidOperationException("Synthetic text control origin is unavailable.");
      var bounds = new PhysicalRectangle(
        origin.X, origin.Y,
        childClient.Right - childClient.Left, childClient.Bottom - childClient.Top);
      cancellationToken.ThrowIfCancellationRequested();
      await using var capture = new ScreenCaptureService(Guid.NewGuid());
      CapturedFrame frame = capture.Capture(bounds, TimeSpan.FromMinutes(1));
      byte[] pixels = capture.Read(frame);
      int white = 0;
      int dark = 0;
      for (int offset = 0; offset < pixels.Length; offset += 4)
      {
        if (pixels[offset] > 245 && pixels[offset + 1] > 245 && pixels[offset + 2] > 245)
          white++;
        if (pixels[offset] < 40 && pixels[offset + 1] < 40 && pixels[offset + 2] < 40)
          dark++;
      }
      if (white < 10000 || dark < 100)
      {
        Point center = new()
        {
          X = bounds.X + bounds.Width / 2,
          Y = bounds.Y + bounds.Height / 2,
        };
        nint hit = WindowFromPoint(center);
        throw new InvalidDataException(
          $"Synthetic Win32 text pixels did not render: white={white}, dark={dark}, " +
          $"bounds={bounds.Width}x{bounds.Height}, dpi={GetDpiForWindow(handle)}, " +
          $"childVisible={IsWindowVisible(child)}, hitOwnChild={hit == child}.");
      }
      Evidence = new CaptureEvidence(frame.Width, frame.Height, white, dark);
      return new ScreenRegionSelection(bounds, pixels, frame.Stride);
    }
    catch (Exception error)
    {
      Failure = error.Message;
      throw;
    }
    finally
    {
      if (messages is not null)
      {
        messages.MessageHandled -= OnWindowMessage;
        messages.Dispose();
      }
      if (child != 0)
        DestroyWindow(child);
      if (font != 0)
        DeleteObject(font);
      window.Close();
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct Rect { public int Left, Top, Right, Bottom; }

  [StructLayout(LayoutKind.Sequential)]
  private struct Point { public int X, Y; }

  [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode,
    SetLastError = true)]
  private static extern nint CreateWindowExW(
    uint extendedStyle, string className, string windowName, uint style,
    int x, int y, int width, int height,
    nint parent, nint menu, nint instance, nint parameter);

  [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode,
    SetLastError = true)]
  private static extern nint CreateFontW(
    int height, int width, int escapement, int orientation, int weight,
    uint italic, uint underline, uint strikeout, uint charSet,
    uint outputPrecision, uint clipPrecision, uint quality,
    uint pitchAndFamily, string faceName);

  [DllImport("user32.dll", EntryPoint = "SendMessageW")]
  private static extern nint SendMessageW(
    nint handle, uint message, nint wParam, nint lParam);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool UpdateWindow(nint handle);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DestroyWindow(nint handle);

  [DllImport("gdi32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DeleteObject(nint handle);

  [DllImport("gdi32.dll")]
  private static extern uint SetBkColor(nint deviceContext, uint color);

  [DllImport("gdi32.dll")]
  private static extern uint SetTextColor(nint deviceContext, uint color);

  [DllImport("gdi32.dll")]
  private static extern nint GetStockObject(int index);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetClientRect(nint handle, out Rect rect);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ClientToScreen(nint handle, ref Point point);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindowVisible(nint handle);

  [DllImport("user32.dll")]
  private static extern uint GetDpiForWindow(nint handle);

  [DllImport("user32.dll")]
  private static extern nint WindowFromPoint(Point point);
}
