using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.FloatingToolbar;

/// <summary>
/// 悬浮工具栏装配：按配置（默认关闭）在 UI 线程创建窗口、感应条与计时器，
/// 并把工具栏命令桥接到统一动作分派器（与热键/托盘/主窗同一动作目录）。
/// 截图类动作的避让由分派器统一负责：截图入口先 Suspend 让位，完成后
/// Resume 恢复原态。
/// </summary>
internal sealed class FloatingToolbarShell : IDisposable
{
    private const uint WmMouseMove = 0x0200;

    private readonly FloatingToolbarController _controller;
    private readonly IFloatingToolbarView _view;
    private readonly Func<string, bool> _dispatchAction;
    private readonly Action _openSettings;
    private bool _disposed;

    private FloatingToolbarShell(
        FloatingToolbarController controller,
        IFloatingToolbarView view,
        Func<string, bool> dispatchAction,
        Action openSettings)
    {
        _controller = controller;
        _view = view;
        _dispatchAction = dispatchAction;
        _openSettings = openSettings;
        view.CommandInvoked += OnCommandInvoked;
    }

    /// <summary>
    /// 读取配置并尝试启动悬浮工具栏；未启用或不在 UI 线程时返回 null。
    /// forceEnabled 用于自检模式强制启用而不改写用户配置。
    /// </summary>
    public static FloatingToolbarShell? TryCreate(
        PortableLayout layout,
        Func<string, bool> dispatchAction,
        Action openSettings,
        bool forceEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(dispatchAction);
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
            dispatchAction,
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

    public FloatingToolbarSettings Settings => _controller.Settings;

    internal FloatingToolbarController.ToolbarState State => _controller.State;

    internal FloatingToolbarVisibility Visibility => _controller.Visibility;

    public void Show() => _controller.Show();

    public void Hide() => _controller.Hide();

    public void Toggle() => _controller.Toggle();

    public void ApplySettings(FloatingToolbarSettings settings) =>
        _controller.ApplySettings(settings);

    /// <summary>
    /// 截图避让：成功进入 Suspended 才返回恢复句柄（释放时恢复原态），
    /// 当前无需让位时返回 null。所有截图入口共用，重复调用安全。
    /// </summary>
    public IDisposable? TrySuspendForCapture()
    {
        _controller.Suspend();
        return _controller.State == FloatingToolbarController.ToolbarState.Suspended
            ? new ResumeOnDispose(_controller)
            : null;
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
                    _dispatchAction(HotkeyActionCatalog.ScreenshotRecognize);
                    break;
                case FloatingToolbarCommand.ScreenshotEdit:
                    _dispatchAction(HotkeyActionCatalog.ScreenshotEdit);
                    break;
                case FloatingToolbarCommand.ClipboardRecognize:
                    _dispatchAction(HotkeyActionCatalog.ClipboardRecognize);
                    break;
                case FloatingToolbarCommand.ShowMainWindow:
                    _dispatchAction(HotkeyActionCatalog.ShowWorkbench);
                    break;
                case FloatingToolbarCommand.OpenSettings:
                    _openSettings();
                    break;
                case FloatingToolbarCommand.DismissToolbar:
                    // 旧“收回到边缘”按用户预期改为明确主动隐藏：
                    // 感应条一并撤防，鼠标路过不恢复。
                    _controller.Hide();
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

    private sealed class ResumeOnDispose(FloatingToolbarController controller) : IDisposable
    {
        private FloatingToolbarController? _controller = controller;

        public void Dispose() =>
            Interlocked.Exchange(ref _controller, null)?.Resume();
    }
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
