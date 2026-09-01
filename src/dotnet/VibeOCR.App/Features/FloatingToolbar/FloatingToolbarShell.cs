using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.FloatingToolbar;

/// <summary>
/// 悬浮工具栏装配：按配置（默认关闭）在 UI 线程创建窗口、感应条与计时器，
/// 并把工具栏命令桥接到现有入口。截图命令先 Suspend 让位再复用热键同路径，
/// 完成后 Resume。
/// </summary>
internal sealed class FloatingToolbarShell : IDisposable
{
    private const uint WmMouseMove = 0x0200;

    private readonly FloatingToolbarController _controller;
    private readonly IFloatingToolbarView _view;
    private readonly Func<Task> _captureScreenshot;
    private readonly Action _showMainWindow;
    private readonly Action _openSettings;
    private bool _disposed;

    private FloatingToolbarShell(
        FloatingToolbarController controller,
        IFloatingToolbarView view,
        Func<Task> captureScreenshot,
        Action showMainWindow,
        Action openSettings)
    {
        _controller = controller;
        _view = view;
        _captureScreenshot = captureScreenshot;
        _showMainWindow = showMainWindow;
        _openSettings = openSettings;
        view.CommandInvoked += OnCommandInvoked;
    }

    /// <summary>
    /// 读取配置并尝试启动悬浮工具栏；未启用或不在 UI 线程时返回 null。
    /// forceEnabled 用于自检模式强制启用而不改写用户配置。
    /// </summary>
    public static FloatingToolbarShell? TryCreate(
        PortableLayout layout,
        Func<Task> captureScreenshot,
        Action showMainWindow,
        Action openSettings,
        bool forceEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        FloatingToolbarSettings settings = FloatingToolbarSettings.Load(layout);
        if (forceEnabled && !settings.Enabled)
        {
            settings = settings with { Enabled = true };
        }

        if (!settings.Enabled)
        {
            return null;
        }

        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return null;
        }

        var view = new FloatingToolbarWindow();
        var controller = new FloatingToolbarController(
            view,
            () => new EdgeSensorWindow(),
            () => new DispatcherQueueDelayTimer(queue),
            settings,
            DesktopScreenQuery.IsForegroundWindowFullscreen,
            DesktopScreenQuery.GetTaskbarOccupiedEdges,
            DesktopScreenQuery.GetPrimaryMonitor,
            DesktopScreenQuery.GetMonitorContaining,
            next => PersistQuietly(layout, next));
        var shell = new FloatingToolbarShell(
            controller,
            view,
            captureScreenshot,
            showMainWindow,
            openSettings);
        controller.Start();
        return shell;
    }

    /// <summary>
    /// 自检：向真实感应条注入 WM_MOUSEMOVE 驱动完整揭示路径，断言工具栏
    /// 可见，Dismiss 后恢复隐藏。返回是否全部通过。
    /// </summary>
    public bool RunInteractionSelfTest()
    {
        nint sensor = _controller.ArmedSensorHandle;
        if (sensor == 0)
        {
            return false;
        }

        SendMessage(sensor, WmMouseMove, 0, 0);
        bool revealed = _view.IsVisible
            && _controller.State == FloatingToolbarController.ToolbarState.Revealed;
        _controller.Dismiss();
        bool dismissed = !_view.IsVisible && _controller.ArmedSensorHandle != 0;
        return revealed && dismissed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.CommandInvoked -= OnCommandInvoked;
        _controller.Dispose();
        _view.Dispose();
    }

    private void OnCommandInvoked(object? sender, FloatingToolbarCommand command)
    {
        try
        {
            switch (command)
            {
                case FloatingToolbarCommand.CaptureScreenshot:
                    _controller.Suspend();
                    _ = RunCaptureAsync();
                    break;
                case FloatingToolbarCommand.ShowMainWindow:
                    _showMainWindow();
                    break;
                case FloatingToolbarCommand.OpenSettings:
                    _openSettings();
                    break;
                case FloatingToolbarCommand.DismissToolbar:
                    _controller.Dismiss();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }
        catch (Exception error)
        {
            // 命令失败只记录，悬浮工具栏自身不因此退出。
            AppLog.Warn($"Floating toolbar command {command} failed: {error.Message}");
        }
    }

    private async Task RunCaptureAsync()
    {
        try
        {
            await _captureScreenshot();
        }
        catch (Exception error) when (
            error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Floating toolbar capture failed: {error.Message}");
        }
        finally
        {
            _controller.Resume();
        }
    }

    private static void PersistQuietly(PortableLayout layout, FloatingToolbarSettings settings)
    {
        try
        {
            FloatingToolbarSettings.Save(layout, settings);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            AppLog.Warn($"Failed to persist floating toolbar settings: {error.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}

/// <summary>DispatcherQueue 单发延迟计时器，仅在 linger 窗口期运行。</summary>
internal sealed class DispatcherQueueDelayTimer : IFloatingToolbarDelayTimer
{
    private readonly DispatcherQueueTimer _timer;

    public DispatcherQueueDelayTimer(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Tick += OnTick;
    }

    public event EventHandler? Tick;

    public void Start(TimeSpan delay)
    {
        _timer.Interval = delay;
        _timer.IsRepeating = false;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(DispatcherQueueTimer sender, object args) =>
        Tick?.Invoke(this, EventArgs.Empty);
}
