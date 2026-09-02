using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.FloatingToolbar;

/// <summary>悬浮工具栏窗口的行为契约，供控制器注入与测试。</summary>
internal interface IFloatingToolbarView : IDisposable
{
    event EventHandler? PointerEntered;

    event EventHandler? PointerExited;

    event EventHandler<PhysicalRectangle>? DragStarted;

    event EventHandler<PhysicalRectangle>? DragCompleted;

    event EventHandler<FloatingToolbarCommand>? CommandInvoked;

    bool IsVisible { get; }

    nint Handle { get; }

    PhysicalRectangle GetPreferredSize();

    PhysicalRectangle GetBounds();

    void ShowAt(PhysicalRectangle bounds);

    void Hide();
}

/// <summary>仅交互窗口期存在的单次延迟计时器；空闲时不存在任何计时器。</summary>
internal interface IFloatingToolbarDelayTimer : IDisposable
{
    event EventHandler? Tick;

    void Start(TimeSpan delay);

    void Stop();
}

/// <summary>
/// 悬浮工具栏状态机：Hidden（感应条贴边）⇄ Revealed（工具栏显示，鼠标
/// 离开 linger 超时收回）⇄ Dragging（拖动，松手吸附或自由浮动），外加
/// PinnedDocked/PinnedFloating（auto_hide=false 常显）与 Suspended（截图
/// 期间临时让位）。全部转换由窗口消息/指针事件驱动，空闲零轮询。
/// </summary>
internal sealed class FloatingToolbarController : IDisposable
{
    internal enum ToolbarState
    {
        Inactive,
        Hidden,
        Revealed,
        Dragging,
        PinnedFloating,
        PinnedDocked,
        Suspended,
    }

    private readonly IFloatingToolbarView _view;
    private readonly Func<IEdgeSensor> _sensorFactory;
    private readonly Func<IFloatingToolbarDelayTimer> _timerFactory;
    private readonly Func<PhysicalRectangle, bool> _fullscreenGuard;
    private readonly Func<IReadOnlySet<ScreenEdge>> _occupiedEdges;
    private readonly Func<PhysicalRectangle> _primaryMonitor;
    private readonly Func<PhysicalRectangle, PhysicalRectangle> _monitorOf;
    private readonly Action<FloatingToolbarSettings> _persist;
    private FloatingToolbarSettings _settings;
    private IEdgeSensor? _sensor;
    private IFloatingToolbarDelayTimer? _lingerTimer;
    private ToolbarState _state = ToolbarState.Inactive;
    private PhysicalRectangle _dockedMonitor;
    private ToolbarState _suspendedState;
    private PhysicalRectangle _suspendedBounds;

    public FloatingToolbarController(
        IFloatingToolbarView view,
        Func<IEdgeSensor> sensorFactory,
        Func<IFloatingToolbarDelayTimer> timerFactory,
        FloatingToolbarSettings settings,
        Func<PhysicalRectangle, bool> fullscreenGuard,
        Func<IReadOnlySet<ScreenEdge>> occupiedEdges,
        Func<PhysicalRectangle> primaryMonitor,
        Func<PhysicalRectangle, PhysicalRectangle> monitorOf,
        Action<FloatingToolbarSettings> persist)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _sensorFactory = sensorFactory ?? throw new ArgumentNullException(nameof(sensorFactory));
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fullscreenGuard = fullscreenGuard ?? throw new ArgumentNullException(nameof(fullscreenGuard));
        _occupiedEdges = occupiedEdges ?? throw new ArgumentNullException(nameof(occupiedEdges));
        _primaryMonitor = primaryMonitor ?? throw new ArgumentNullException(nameof(primaryMonitor));
        _monitorOf = monitorOf ?? throw new ArgumentNullException(nameof(monitorOf));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));

        _view.PointerEntered += OnViewPointerEntered;
        _view.PointerExited += OnViewPointerExited;
        _view.DragStarted += OnViewDragStarted;
        _view.DragCompleted += OnViewDragCompleted;
    }

    internal ToolbarState State => _state;

    internal FloatingToolbarSettings Settings => _settings;

    /// <summary>当前贴边的感应条句柄；未启用或已揭示时为 0。</summary>
    internal nint ArmedSensorHandle =>
        _sensor is { IsArmed: true } sensor ? sensor.Handle : 0;

    public void Start()
    {
        if (_state != ToolbarState.Inactive)
        {
            return;
        }

        _dockedMonitor = _primaryMonitor();
        if (_settings.AutoHide)
        {
            HideToEdge();
        }
        else
        {
            ShowDocked(ToolbarState.PinnedDocked);
        }
    }

    public void Stop()
    {
        _lingerTimer?.Stop();
        DestroySensor();
        if (_state != ToolbarState.Inactive)
        {
            _view.Hide();
        }

        _state = ToolbarState.Inactive;
    }

    /// <summary>截图流程前临时让位，避免工具栏被截入选区背景。</summary>
    public void Suspend()
    {
        if (_state is not (ToolbarState.Revealed
            or ToolbarState.PinnedFloating
            or ToolbarState.PinnedDocked))
        {
            return;
        }

        _suspendedState = _state;
        _suspendedBounds = _view.GetBounds();
        _lingerTimer?.Stop();
        _view.Hide();
        _state = ToolbarState.Suspended;
    }

    public void Resume()
    {
        if (_state != ToolbarState.Suspended)
        {
            return;
        }

        if (_suspendedState == ToolbarState.PinnedFloating)
        {
            _view.ShowAt(_suspendedBounds);
            _state = ToolbarState.PinnedFloating;
        }
        else if (_settings.AutoHide)
        {
            // Revealed 恢复时鼠标多半已离开，直接收回边缘重新布防。
            HideToEdge();
        }
        else
        {
            ShowDocked(ToolbarState.PinnedDocked);
        }
    }

    /// <summary>“收回到边缘”命令：贴边隐藏或停靠显示（auto_hide=false）。</summary>
    public void Dismiss()
    {
        if (_state is not (ToolbarState.Revealed
            or ToolbarState.PinnedFloating
            or ToolbarState.PinnedDocked))
        {
            return;
        }

        if (_settings.AutoHide)
        {
            HideToEdge();
        }
        else
        {
            ShowDocked(ToolbarState.PinnedDocked);
        }
    }

    internal void ApplySettings(FloatingToolbarSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (!next.Enabled)
        {
            Stop();
            _settings = next;
            return;
        }

        bool wasInactive = _state == ToolbarState.Inactive;
        _settings = next;
        if (wasInactive)
        {
            Start();
            return;
        }

        ReapplyLayout();
    }

    public void Dispose() => Stop();

    private void OnSensorPointerEntered(object? sender, EventArgs eventArgs)
    {
        if (_state != ToolbarState.Hidden)
        {
            return;
        }

        // 全屏应用（游戏/视频）期间不揭示、不遮挡；感应条保持贴边，
        // 下一次事件再评估，维持事件驱动。
        if (_fullscreenGuard(_dockedMonitor))
        {
            return;
        }

        _sensor?.Disarm();
        PhysicalRectangle size = _view.GetPreferredSize();
        _view.ShowAt(ScreenEdgeGeometry.GetDockedToolbarRectangle(
            _dockedMonitor,
            _settings.Edge,
            size.Width,
            size.Height));
        _state = ToolbarState.Revealed;
    }

    private void OnSensorDisplayChanged(object? sender, EventArgs eventArgs)
    {
        // 显示器热插拔/分辨率变化：停靠显示器重算（消失时回落最近显示器）。
        if (_state != ToolbarState.Hidden)
        {
            return;
        }

        _dockedMonitor = _monitorOf(_dockedMonitor);
        _sensor?.Arm(ScreenEdgeGeometry.GetSensorRectangle(_dockedMonitor, _settings.Edge));
    }

    private void OnViewPointerEntered(object? sender, EventArgs eventArgs)
    {
        if (_state == ToolbarState.Revealed)
        {
            _lingerTimer?.Stop();
        }
    }

    private void OnViewPointerExited(object? sender, EventArgs eventArgs)
    {
        if (_state != ToolbarState.Revealed)
        {
            return;
        }

        _lingerTimer ??= CreateLingerTimer();
        _lingerTimer.Start(TimeSpan.FromMilliseconds(_settings.LingerMs));
    }

    private void OnLingerTick(object? sender, EventArgs eventArgs)
    {
        if (_state != ToolbarState.Revealed)
        {
            return;
        }

        _lingerTimer?.Stop();
        HideToEdge();
    }

    private void OnViewDragStarted(object? sender, PhysicalRectangle bounds)
    {
        if (_state is not (ToolbarState.Revealed
            or ToolbarState.PinnedFloating
            or ToolbarState.PinnedDocked))
        {
            return;
        }

        _lingerTimer?.Stop();
        _sensor?.Disarm();
        _state = ToolbarState.Dragging;
    }

    private void OnViewDragCompleted(object? sender, PhysicalRectangle bounds)
    {
        if (_state != ToolbarState.Dragging)
        {
            return;
        }

        PhysicalRectangle monitor = _monitorOf(bounds);
        ScreenEdge? snap = ScreenEdgeGeometry.FindSnapEdge(bounds, monitor);
        if (snap is { } edge && !_occupiedEdges().Contains(edge))
        {
            _settings = _settings with { Edge = edge };
            _persist(_settings);
            _dockedMonitor = monitor;
            if (_settings.AutoHide)
            {
                HideToEdge();
            }
            else
            {
                ShowDocked(ToolbarState.PinnedDocked);
            }

            return;
        }

        _view.ShowAt(bounds);
        _state = ToolbarState.PinnedFloating;
    }

    private IFloatingToolbarDelayTimer CreateLingerTimer()
    {
        IFloatingToolbarDelayTimer timer = _timerFactory();
        timer.Tick += OnLingerTick;
        return timer;
    }

    private void HideToEdge()
    {
        _view.Hide();
        EnsureSensor().Arm(ScreenEdgeGeometry.GetSensorRectangle(
            _dockedMonitor,
            _settings.Edge));
        _state = ToolbarState.Hidden;
    }

    private void ShowDocked(ToolbarState next)
    {
        PhysicalRectangle size = _view.GetPreferredSize();
        _view.ShowAt(ScreenEdgeGeometry.GetDockedToolbarRectangle(
            _dockedMonitor,
            _settings.Edge,
            size.Width,
            size.Height));
        _state = next;
    }

    private void ReapplyLayout()
    {
        switch (_state)
        {
            case ToolbarState.Hidden:
                if (_settings.AutoHide)
                {
                    EnsureSensor().Arm(ScreenEdgeGeometry.GetSensorRectangle(
                        _dockedMonitor,
                        _settings.Edge));
                }
                else
                {
                    _sensor?.Disarm();
                    ShowDocked(ToolbarState.PinnedDocked);
                }

                break;
            case ToolbarState.PinnedDocked:
                if (_settings.AutoHide)
                {
                    HideToEdge();
                }
                else
                {
                    ShowDocked(ToolbarState.PinnedDocked);
                }

                break;
            default:
                // Revealed/Dragging/PinnedFloating/Suspended：位置已就绪，
                // 后续自然转换按新设置执行。
                break;
        }
    }

    private IEdgeSensor EnsureSensor()
    {
        if (_sensor is null)
        {
            _sensor = _sensorFactory();
            _sensor.PointerEntered += OnSensorPointerEntered;
            _sensor.DisplayChanged += OnSensorDisplayChanged;
        }

        return _sensor;
    }

    private void DestroySensor()
    {
        if (_sensor is null)
        {
            return;
        }

        _sensor.PointerEntered -= OnSensorPointerEntered;
        _sensor.DisplayChanged -= OnSensorDisplayChanged;
        _sensor.Dispose();
        _sensor = null;
    }
}
