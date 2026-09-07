using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using VibeOCR.Platform.Windows;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.System;

namespace VibeOCR.App.Features.Recognition;

public sealed record ScreenRegionSelection(
    PhysicalRectangle Bounds,
    byte[] Bgra,
    int Stride);

public interface IScreenRegionPicker
{
    Task<ScreenRegionSelection?> PickAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Lightweight region selector for the WinUI screenshot workflow.  It takes a
/// single frozen snapshot after hiding the main window, then crops from that
/// snapshot so the application cannot reappear in the final OCR input.
/// </summary>
public sealed class ScreenRegionPicker(Func<nint> ownerWindow) : IScreenRegionPicker
{
    private const long MaximumCaptureBytes = 256L << 20;
    private const int VirtualScreenX = 76;
    private const int VirtualScreenY = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;
    private readonly Func<nint> _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));

    public async Task<ScreenRegionSelection?> PickAsync(CancellationToken cancellationToken)
    {
        PhysicalRectangle desktop = GetVirtualDesktop();
        nint owner = _ownerWindow();
        ShowWindow(owner, 0);
        // 暂时强制任务栏自动隐藏，否则它会盖住截图遮罩顶部或因置顶而闪现。
        // 保存原状态，在 finally 中恢复，避免改变用户的任务栏偏好。
        AppBarStateScope taskbarState = HideTaskbars();
        try
        {
            await Task.Delay(180, cancellationToken);
            await using var capture = new ScreenCaptureService(Guid.NewGuid());
            CapturedFrame frame = capture.Capture(desktop, TimeSpan.FromMinutes(1));
            byte[] desktopBgra = capture.Read(frame);
            byte[] desktopBmp = EncodeTopDownBmp(
                desktopBgra,
                desktop.Width,
                desktop.Height,
                frame.Stride);
            BitmapImage background = await LoadBitmapAsync(
                desktopBmp,
                desktop.Width,
                desktop.Height);
            PhysicalRectangle? selected = await ShowOverlayAsync(
                desktop,
                background,
                desktopBgra,
                cancellationToken);
            if (selected is null)
            {
                return null;
            }

            byte[] cropped = CropBgra(desktopBgra, desktop, selected.Value);
            return new ScreenRegionSelection(
                selected.Value,
                cropped,
                selected.Value.Width * 4);
        }
        finally
        {
            taskbarState.Dispose();
            ShowWindow(owner, 9);
            SetForegroundWindow(owner);
        }
    }

    /// <summary>
    /// Enumerate the primary taskbar (plus any secondary monitor Shell tray
    /// windows) and force them into auto-hide for the duration of the screenshot
    /// overlay, returning a scope that restores the prior state on dispose.
    /// </summary>
    private static AppBarStateScope HideTaskbars()
    {
        var saved = new List<(nint hWnd, uint State)>();
        nint taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != nint.Zero)
        {
            saved.Add((taskbar, GetTaskbarState(taskbar)));
            SetTaskbarState(taskbar, AbsAutohide | AbsAlwaysOnTop);
        }

        // Secondary-monitor taskbars live in windows of class "Shell_SecondaryTrayWnd".
        nint secondary = nint.Zero;
        while ((secondary = FindWindowEx(nint.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != nint.Zero)
        {
            saved.Add((secondary, GetTaskbarState(secondary)));
            SetTaskbarState(secondary, AbsAutohide | AbsAlwaysOnTop);
        }

        return new AppBarStateScope(saved);
    }

    private static uint GetTaskbarState(nint taskbar)
    {
        var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>(), hWnd = taskbar };
        return (uint)SHAppBarMessage(AbmGetState, ref data);
    }

    private static void SetTaskbarState(nint taskbar, uint state)
    {
        var data = new AppBarData
        {
            cbSize = (uint)Marshal.SizeOf<AppBarData>(),
            hWnd = taskbar,
            lParam = (nint)(int)state,
        };
        SHAppBarMessage(AbmSetState, ref data);
    }

  private static async Task<PhysicalRectangle?> ShowOverlayAsync(
    PhysicalRectangle desktop,
    BitmapImage background,
    byte[] pixels,
    CancellationToken cancellationToken)
  {
    var completion = new TaskCompletionSource<PhysicalRectangle?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var session = new ScreenSelectionSession(desktop.Width, desktop.Height);
    var overlay = new Window();
    var root = new Grid { RequestedTheme = ElementTheme.Dark };
    root.Children.Add(new Image { Source = background, Stretch = Stretch.Fill });
    var canvas = new Canvas { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    root.Children.Add(canvas);
    // Four shades leave the selected pixels unobscured.
    Rectangle[] shades = Enumerable.Range(0, 4).Select(_ => new Rectangle
    {
      Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 0, 0)),
      IsHitTestVisible = false,
    }).ToArray();
    foreach (Rectangle shade in shades) canvas.Children.Add(shade);
    var selection = new Rectangle
    {
      Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
      StrokeThickness = 1,
      IsHitTestVisible = false,
      Visibility = Visibility.Collapsed,
    };
    canvas.Children.Add(selection);
    Rectangle[] handles = Enumerable.Range(0, 8).Select(_ => new Rectangle
    {
      Width = 7,
      Height = 7,
      Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
      IsHitTestVisible = false,
      Visibility = Visibility.Collapsed,
    }).ToArray();
    foreach (Rectangle handle in handles) canvas.Children.Add(handle);
    var sizeLabel = new TextBlock();
    var help = new TextBlock
    {
      Text = "拖动框选；选区内拖动移动，边缘拖动缩放\nEnter 识别 · 右键返回 / 退出 · Esc 退出 · 方向键微调 · Shift ×10 · Ctrl+方向键缩放\nCtrl+Z 撤销 · Ctrl+Y / Ctrl+Shift+Z 重做 · M 放大镜 · Ctrl+C 复制色号",
      IsHitTestVisible = false,
    };
    var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
    var panel = new StackPanel { Spacing = 8 };
    panel.Children.Add(help);
    panel.Children.Add(sizeLabel);
    panel.Children.Add(toolbar);
    var panelBorder = new Border
    {
      Background = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 24, 24, 24)),
      Padding = new Thickness(12),
      CornerRadius = new CornerRadius(8),
      Child = panel,
    };
    canvas.Children.Add(panelBorder);
    Button AddButton(string text, Action action)
    {
      var button = new Button { Content = text };
      button.Click += (_, _) => action();
      toolbar.Children.Add(button);
      return button;
    }
    void Finish(bool accept)
    {
      if (completion.Task.IsCompleted || (accept && !session.CanConfirm)) return;
      PhysicalRectangle? result = accept && session.Selection is { } rect
          ? rect with { X = rect.X + desktop.X, Y = rect.Y + desktop.Y } : null;
      completion.TrySetResult(result);
      overlay.Close();
    }
    void Back()
    {
      if (session.Back()) Finish(false);
      canvas.ReleasePointerCaptures();
    }
    Button confirm = AddButton("识别 (Enter)", () => Finish(true));
    Button undo = AddButton("撤销", session.Undo);
    Button redo = AddButton("重做", session.Redo);
    AddButton("重选", () => { if (session.Selection is not null || session.IsDragging) Back(); });
    AddButton("退出 (Esc)", () => Finish(false));
    foreach (Button button in toolbar.Children.OfType<Button>()) button.Click += (_, _) => Render();

    var magnifierCanvas = new Canvas { Width = 99, Height = 99 };
    SolidColorBrush[] pixelBrushes = Enumerable.Range(0, 121).Select(_ => new SolidColorBrush()).ToArray();
    for (int i = 0; i < pixelBrushes.Length; i++)
    {
      var pixel = new Rectangle { Width = 9, Height = 9, Fill = pixelBrushes[i] };
      Canvas.SetLeft(pixel, i % 11 * 9);
      Canvas.SetTop(pixel, i / 11 * 9);
      magnifierCanvas.Children.Add(pixel);
    }
    var crosshair = new Rectangle
    {
      Width = 11,
      Height = 11,
      Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red),
      StrokeThickness = 2,
    };
    Canvas.SetLeft(crosshair, 44);
    Canvas.SetTop(crosshair, 44);
    magnifierCanvas.Children.Add(crosshair);
    var colorLabel = new TextBlock();
    var magnifierPanel = new StackPanel { Spacing = 4 };
    magnifierPanel.Children.Add(magnifierCanvas);
    magnifierPanel.Children.Add(colorLabel);
    var magnifier = new Border
    {
      Child = magnifierPanel,
      Padding = new Thickness(8),
      IsHitTestVisible = false,
      Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 24, 24, 24)),
      Visibility = Visibility.Collapsed,
    };
    canvas.Children.Add(magnifier);
    bool showMagnifier = true;
    string? colorHex = null;
    Windows.Foundation.Point? lastPoint = null;
    PhysicalPoint ToPhysical(Windows.Foundation.Point point) => new(
        (int)Math.Round(point.X * desktop.Width / Math.Max(1, canvas.ActualWidth)),
        (int)Math.Round(point.Y * desktop.Height / Math.Max(1, canvas.ActualHeight)));
    void UpdateMagnifier(Windows.Foundation.Point point)
    {
      lastPoint = point;
      PhysicalPoint location = ToPhysical(point);
      int x = Math.Clamp(location.X, 0, desktop.Width - 1);
      int y = Math.Clamp(location.Y, 0, desktop.Height - 1);
      for (int i = 0; i < pixelBrushes.Length; i++)
      {
        int px = Math.Clamp(x + i % 11 - 5, 0, desktop.Width - 1);
        int py = Math.Clamp(y + i / 11 - 5, 0, desktop.Height - 1);
        int offset = (py * desktop.Width + px) * 4;
        pixelBrushes[i].Color = Windows.UI.Color.FromArgb(255, pixels[offset + 2], pixels[offset + 1], pixels[offset]);
      }
      Windows.UI.Color color = pixelBrushes[60].Color;
      colorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
      colorLabel.Text = $"{desktop.X + x}, {desktop.Y + y}\n{colorHex}\nRGB {color.R}, {color.G}, {color.B}";
      magnifier.Visibility = showMagnifier ? Visibility.Visible : Visibility.Collapsed;
      Canvas.SetLeft(magnifier, Math.Max(0, point.X + 24 + 180 > canvas.ActualWidth ? point.X - 180 : point.X + 24));
      Canvas.SetTop(magnifier, Math.Max(0, point.Y + 24 + 190 > canvas.ActualHeight ? point.Y - 190 : point.Y + 24));
    }
    void Place(Rectangle rectangle, double x, double y, double w, double h)
    {
      Canvas.SetLeft(rectangle, x); Canvas.SetTop(rectangle, y);
      rectangle.Width = Math.Max(0, w); rectangle.Height = Math.Max(0, h);
    }
    void Render()
    {
      double w = canvas.ActualWidth, h = canvas.ActualHeight;
      double sx = w / desktop.Width, sy = h / desktop.Height;
      PhysicalRectangle? rect = session.Selection;
      double left = rect?.X * sx ?? 0, top = rect?.Y * sy ?? 0;
      double right = rect?.Right * sx ?? 0, bottom = rect?.Bottom * sy ?? 0;
      Place(shades[0], 0, 0, w, top);
      Place(shades[1], 0, top, left, bottom - top);
      Place(shades[2], right, top, w - right, bottom - top);
      Place(shades[3], 0, bottom, w, h - bottom);
      selection.Visibility = rect is null ? Visibility.Collapsed : Visibility.Visible;
      Place(selection, left, top, right - left, bottom - top);
      (double X, double Y)[] points = [(left, top), ((left + right) / 2, top), (right, top),
                (left, (top + bottom) / 2), (right, (top + bottom) / 2),
                (left, bottom), ((left + right) / 2, bottom), (right, bottom)];
      for (int i = 0; i < handles.Length; i++)
      {
        handles[i].Visibility = rect is null ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(handles[i], points[i].X - 3.5);
        Canvas.SetTop(handles[i], points[i].Y - 3.5);
      }
      sizeLabel.Text = rect is { } r ? $"{r.Width} × {r.Height} px · 调整完成后按 Enter 识别" : "请选择区域";
      confirm.IsEnabled = session.CanConfirm;
      undo.IsEnabled = session.CanUndo;
      redo.IsEnabled = session.CanRedo;
      panelBorder.Measure(new Windows.Foundation.Size(w, h));
      double pw = panelBorder.DesiredSize.Width, ph = panelBorder.DesiredSize.Height;
      Canvas.SetLeft(panelBorder, Math.Clamp(left, 0, Math.Max(0, w - pw)));
      Canvas.SetTop(panelBorder, rect is null ? Math.Max(0, h - ph - 20) :
          bottom + ph + 12 <= h ? bottom + 12 : Math.Max(0, top - ph - 12));
      panelBorder.Visibility = session.IsDragging ? Visibility.Collapsed : Visibility.Visible;
    }
    canvas.PointerPressed += (_, args) =>
    {
      if (!args.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed || args.Handled) return;
      // Child buttons and the help panel must not start a new selection.
      if (args.OriginalSource is DependencyObject source)
      {
        for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
          if (node == panelBorder) return;
      }
      session.Begin(ToPhysical(args.GetCurrentPoint(canvas).Position),
                  Math.Max(1, (int)Math.Ceiling(6 * desktop.Width / Math.Max(1, canvas.ActualWidth))));
      canvas.CapturePointer(args.Pointer);
      Render();
      args.Handled = true;
    };
    root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
    {
      if (!args.GetCurrentPoint(canvas).Properties.IsRightButtonPressed) return;
      Back(); Render(); args.Handled = true;
    }), true);
    canvas.PointerMoved += (_, args) =>
    {
      Windows.Foundation.Point point = args.GetCurrentPoint(canvas).Position;
      session.Move(ToPhysical(point));
      UpdateMagnifier(point);
      Render();
    };
    canvas.PointerReleased += (_, args) =>
    {
      if (!session.IsDragging || args.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
      session.End(ToPhysical(args.GetCurrentPoint(canvas).Position));
      canvas.ReleasePointerCapture(args.Pointer);
      Render(); args.Handled = true;
    };
    canvas.PointerCaptureLost += (_, _) => { session.CancelDrag(); Render(); };
    canvas.PointerCanceled += (_, _) => { session.CancelDrag(); Render(); };
    var keyboardSink = new Button
    {
      Width = 1,
      Height = 1,
      Opacity = 0,
      IsTabStop = true,
      HorizontalAlignment = HorizontalAlignment.Left,
      VerticalAlignment = VerticalAlignment.Top
    };
    root.Children.Add(keyboardSink);
    root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, args) =>
        {
          bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
          bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
          int step = shift ? 10 : 1;
          switch (args.Key)
          {
            case VirtualKey.Escape: Finish(false); break;
            case VirtualKey.Enter: Finish(true); break;
            case VirtualKey.Z when control && shift: session.Redo(); break;
            case VirtualKey.Z when control: session.Undo(); break;
            case VirtualKey.Y when control: session.Redo(); break;
            case VirtualKey.Left: session.Adjust(-step, 0, control); break;
            case VirtualKey.Right: session.Adjust(step, 0, control); break;
            case VirtualKey.Up: session.Adjust(0, -step, control); break;
            case VirtualKey.Down: session.Adjust(0, step, control); break;
            case VirtualKey.M when !control:
              showMagnifier = !showMagnifier;
              if (lastPoint is { } point) UpdateMagnifier(point);
              break;
            case VirtualKey.C when control && colorHex is not null:
              try
              {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(colorHex);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                colorLabel.Text += "\n已复制";
              }
              catch (COMException) { colorLabel.Text += "\n剪贴板暂不可用"; }
              break;
            default: return;
          }
          Render(); args.Handled = true;
        }), true);
    canvas.SizeChanged += (_, _) => Render();
    overlay.Content = root;
    overlay.Closed += (_, _) => completion.TrySetResult(null);
    OverlappedPresenter presenter = OverlappedPresenter.Create();
    presenter.SetBorderAndTitleBar(false, false);
    presenter.IsAlwaysOnTop = true;
    presenter.IsResizable = false;
    overlay.AppWindow.SetPresenter(presenter);
    overlay.AppWindow.IsShownInSwitchers = false;
    overlay.AppWindow.MoveAndResize(new RectInt32(desktop.X, desktop.Y, desktop.Width, desktop.Height));
    overlay.Activate();
    keyboardSink.Focus(FocusState.Programmatic);
    using CancellationTokenRegistration registration = cancellationToken.Register(() =>
    root.DispatcherQueue.TryEnqueue(() => { completion.TrySetCanceled(cancellationToken); overlay.Close(); }));
    return await completion.Task;
  }
    public static PhysicalRectangle ScaleSelection(
        PhysicalRectangle desktop,
        double left,
        double top,
        double width,
        double height,
        double canvasWidth,
        double canvasHeight)
    {
        int x = desktop.X + (int)Math.Round(left * desktop.Width / canvasWidth);
        int y = desktop.Y + (int)Math.Round(top * desktop.Height / canvasHeight);
        int right = desktop.X + (int)Math.Round((left + width) * desktop.Width / canvasWidth);
        int bottom = desktop.Y + (int)Math.Round((top + height) * desktop.Height / canvasHeight);
        return new PhysicalRectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static byte[] CropBgra(
        byte[] source,
        PhysicalRectangle desktop,
        PhysicalRectangle selected)
    {
        int sourceStride = checked(desktop.Width * 4);
        int targetStride = checked(selected.Width * 4);
        byte[] cropped = new byte[checked(targetStride * selected.Height)];
        int offsetX = selected.X - desktop.X;
        int offsetY = selected.Y - desktop.Y;
        for (int row = 0; row < selected.Height; row++)
        {
            System.Buffer.BlockCopy(
                source,
                checked((offsetY + row) * sourceStride + offsetX * 4),
                cropped,
                row * targetStride,
                targetStride);
        }

        return cropped;
    }

    private static async Task<BitmapImage> LoadBitmapAsync(byte[] data, int physicalWidth, int physicalHeight)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(data);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage
        {
            // Pin the decode size to the captured physical pixels and interpret it
            // in physical units. Without this, WinUI 3 defaults to
            // DecodePixelType=Logical and auto-scales the decoded bitmap to the
            // effective/logical layout size — so on a >100% DPI display the frozen
            // desktop backdrop is downsampled then stretched back by Stretch.Fill,
            // producing the blurry overlay the screenshot workflow used to show.
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = physicalWidth,
            DecodePixelHeight = physicalHeight,
        };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static byte[] EncodeTopDownBmp(byte[] bgra, int width, int height, int stride)
    {
        int pixelBytes = checked(stride * height);
        byte[] bmp = new byte[checked(54 + pixelBytes)];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(-height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)32).CopyTo(bmp, 28);
        BitConverter.GetBytes(pixelBytes).CopyTo(bmp, 34);
        bgra.CopyTo(bmp, 54);
        return bmp;
    }

    private static PhysicalRectangle GetVirtualDesktop()
    {
        var desktop = new PhysicalRectangle(
            GetSystemMetrics(VirtualScreenX),
            GetSystemMetrics(VirtualScreenY),
            GetSystemMetrics(VirtualScreenWidth),
            GetSystemMetrics(VirtualScreenHeight));
        desktop.Validate();
        if (checked((long)desktop.Width * desktop.Height * 4) > MaximumCaptureBytes)
        {
            throw new InvalidDataException("虚拟桌面截图超过 256 MiB 限制。");
        }
        return desktop;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern nint SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    private const uint AbmGetState = 4;
    private const uint AbmSetState = 10;
    private const uint AbsAutohide = 0x0000001;
    private const uint AbsAlwaysOnTop = 0x0000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Restores the saved taskbar auto-hide states on dispose. Holds the saved
    /// (hWnd, originalState) pairs captured before the screenshot overlay forced
    /// them into auto-hide.
    /// </summary>
    private sealed class AppBarStateScope : IDisposable
    {
        private List<(nint HWnd, uint State)>? _saved;
        private bool _disposed;

        public AppBarStateScope(List<(nint HWnd, uint State)> saved) => _saved = saved;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_saved is null)
            {
                return;
            }

            foreach ((nint hWnd, uint state) in _saved)
            {
                SetTaskbarState(hWnd, state);
            }

            _saved = null;
        }
    }
}
