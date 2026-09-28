using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VibeOCR.Platform.Windows;
using Windows.Graphics;

namespace VibeOCR.App.Features.Recognition;

// Only constructed by the isolated screenshot-e2e process. Never samples the desktop
// outside the opaque client area of the synthetic WinUI window it creates.
internal sealed class SyntheticScreenRegionPicker : IScreenRegionPicker
{
  internal sealed record CaptureEvidence(int Width, int Height, int WhitePixels, int DarkPixels);

  internal CaptureEvidence? Evidence { get; private set; }

  public async Task<ScreenRegionSelection?> PickAsync(CancellationToken cancellationToken)
  {
    var window = new Window
    {
      Content = new Grid
      {
        Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        Children =
        {
          new TextBlock
          {
            Text = "VibeOCR 123",
            FontSize = 96,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
          },
        },
      },
    };
    try
    {
      ((OverlappedPresenter)window.AppWindow.Presenter).IsAlwaysOnTop = true;
      window.AppWindow.MoveAndResize(new RectInt32(100, 100, 1000, 300));
      window.Activate();
      await Task.Delay(500, cancellationToken);

      nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
      if (!GetClientRect(handle, out Rect client))
      {
        throw new InvalidOperationException("Synthetic WinUI client bounds are unavailable.");
      }
      Point origin = new();
      if (!ClientToScreen(handle, ref origin))
      {
        throw new InvalidOperationException("Synthetic WinUI client origin is unavailable.");
      }
      var bounds = new PhysicalRectangle(
        origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
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
        throw new InvalidDataException(
          $"Synthetic WinUI pixels did not render: white={white}, dark={dark}.");
      }
      Evidence = new CaptureEvidence(frame.Width, frame.Height, white, dark);
      return new ScreenRegionSelection(bounds, pixels, frame.Stride);
    }
    finally
    {
      window.Close();
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct Rect { public int Left, Top, Right, Bottom; }

  [StructLayout(LayoutKind.Sequential)]
  private struct Point { public int X, Y; }

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetClientRect(nint handle, out Rect rect);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ClientToScreen(nint handle, ref Point point);
}
