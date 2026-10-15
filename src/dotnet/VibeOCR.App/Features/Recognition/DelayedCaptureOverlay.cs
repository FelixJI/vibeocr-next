using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// #236 延时截图倒计时窗口：无边框置顶小窗显示剩余秒数，Esc 或“取消”
/// 按钮显式中止；倒计时结束返回 true 后才进入框选。窗口仅呈现文本与
/// 现有按钮，不新增图标、不采样屏幕。结束（完成/取消/异常/取消令牌）
/// 都会停止计时器、关闭窗口；取消时恢复原前台，成功时保留用户新前台。
/// </summary>
internal sealed class DelayedCaptureOverlay
{
  private readonly Window _window = new();
  private readonly TextBlock _countdown = new() { FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
  private readonly Button _cancel = new() { Content = "取消", HorizontalAlignment = HorizontalAlignment.Center };
  private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
  private readonly nint _previousForeground;
  private readonly nint _handle;
  private int _remaining;
  private bool _stopped;

  internal DelayedCaptureOverlay(int delaySeconds)
  {
    _remaining = Math.Clamp(delaySeconds, 1, 60);
    _previousForeground = GetForegroundWindow();
    _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
    _timer.Interval = TimeSpan.FromSeconds(1);
    _timer.Tick += (_, _) =>
    {
      _remaining--;
      if (_remaining <= 0)
      {
        Stop(true);
        return;
      }

      _countdown.Text = $"{_remaining}";
    };

    var hint = new TextBlock
    {
      Text = "秒后开始框选 · Esc 或“取消”中止",
      HorizontalAlignment = HorizontalAlignment.Center,
      Opacity = 0.8,
    };
    var panel = new StackPanel { Padding = new Thickness(24, 16, 24, 16), Spacing = 10 };
    panel.Children.Add(_countdown);
    panel.Children.Add(hint);
    panel.Children.Add(_cancel);
    var root = new Grid
    {
      Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black),
    };
    root.Children.Add(panel);
    AutomationProperties.SetName(_cancel, "取消延时截图");
    root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
    {
      if (args.Key == VirtualKey.Escape) Stop(false);
    }), true);

    _window.Content = root;
    _window.Closed += (_, _) => Stop(false);
    _cancel.Click += (_, _) => Stop(false);
    OverlappedPresenter presenter = OverlappedPresenter.Create();
    presenter.SetBorderAndTitleBar(false, false);
    presenter.IsAlwaysOnTop = true;
    presenter.IsResizable = false;
    _window.AppWindow.SetPresenter(presenter);
    _window.AppWindow.IsShownInSwitchers = false;
    _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(320, 150));
    DisplayArea area = DisplayArea.Primary;
    _window.AppWindow.Move(new Windows.Graphics.PointInt32(
        area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - 320) / 2),
        area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - 150) / 2)));
    _handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
  }

  internal Window Window => _window;
  internal Button CancelButton => _cancel;
  internal nint Handle => _handle;

  /// <summary>展示并等待倒计时；false 表示用户取消/窗口关闭。</summary>
  public static async Task<bool> RunAsync(
      int delaySeconds, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return await new DelayedCaptureOverlay(delaySeconds).ShowAsync(cancellationToken);
  }

  internal async Task<bool> ShowAsync(CancellationToken cancellationToken)
  {
    _countdown.Text = $"{_remaining}";
    _window.Activate();
    // #245 C2：普通 Window 激活不会建立 XAML 焦点，root.KeyDown 收不到
    // Esc。直接聚焦可见“取消”按钮这一真实可聚焦控件（不引入不可见幽灵
    // Tab stop）；未 Loaded 时挂一次 Loaded 再聚焦，保证首帧后成功。
    if (_cancel.IsLoaded)
    {
      _cancel.Focus(FocusState.Programmatic);
    }
    else
    {
      _cancel.Loaded += (_, _) => _cancel.Focus(FocusState.Programmatic);
    }
    _timer.Start();
    using CancellationTokenRegistration registration = cancellationToken.Register(
        () => _window.DispatcherQueue.TryEnqueue(() => Stop(false)));
    bool proceed = false;
    try
    {
      proceed = await _completion.Task;
      return proceed;
    }
    finally
    {
      Stop(false);
      CloseWindowAndRestore(restoreForeground: !proceed);
    }
  }

  internal void Stop(bool proceed)
  {
    if (_stopped)
    {
      return;
    }

    _stopped = true;
    _timer.Stop();
    _completion.TrySetResult(proceed);
  }

  private void CloseWindowAndRestore(bool restoreForeground)
  {
    try
    {
      _window.Close();
    }
    catch (COMException)
    {
      // 窗口已由关闭按钮销毁。
    }
    finally
    {
      if (restoreForeground && _previousForeground != 0 && _previousForeground != _handle)
      {
        SetForegroundWindow(_previousForeground);
      }
    }
  }
  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetForegroundWindow(nint window);
}
