using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 悬浮工具栏状态机契约：揭示/linger 收回、全屏退避、拖动吸附、任务栏
/// 边剔除、Suspend/Resume 与设置重放，全部用 fake 驱动。
/// </summary>
public sealed class FloatingToolbarControllerTests
{
    private static readonly PhysicalRectangle Primary =
        new(0, 0, 1920, 1080);

    private readonly FakeToolbarView _view = new();
    private readonly FakeEdgeSensor _sensor = new();
    private readonly FakeDelayTimer _timer = new();

    private FloatingToolbarController CreateController(
        FloatingToolbarSettings? settings = null,
        Func<PhysicalRectangle, bool>? fullscreenGuard = null,
        IReadOnlySet<ScreenEdge>? occupiedEdges = null,
        Func<PhysicalRectangle, PhysicalRectangle>? monitorOf = null,
        Func<PhysicalRectangle>? primaryMonitor = null,
        List<FloatingToolbarSettings>? persisted = null)
    {
        return new FloatingToolbarController(
            _view,
            () => _sensor,
            () => _timer,
            settings ?? new FloatingToolbarSettings(true, ScreenEdge.Top, true, 600),
            fullscreenGuard ?? (_ => false),
            () => occupiedEdges ?? new HashSet<ScreenEdge> { ScreenEdge.Bottom },
            primaryMonitor ?? (() => Primary),
            monitorOf ?? (_ => Primary),
            next => persisted?.Add(next));
    }

    [Fact]
    public void StartWithAutoHideArmsSensorAtTopEdge()
    {
        using FloatingToolbarController controller = CreateController();

        controller.Start();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.True(_sensor.IsArmed);
        Assert.Equal(new PhysicalRectangle(0, 0, 1920, 2), _sensor.ArmedBounds);
        Assert.False(_view.IsVisible);
    }

    [Fact]
    public void StartWithoutAutoHideShowsDockedAndSkipsSensor()
    {
        using FloatingToolbarController controller = CreateController(
            new FloatingToolbarSettings(true, ScreenEdge.Top, false, 600));

        controller.Start();

        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedDocked, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Equal(new PhysicalRectangle(860, 0, 200, 44), _view.LastShownBounds);
        Assert.False(_sensor.IsArmed);
    }

    [Fact]
    public void SensorEntryRevealsToolbarCenteredOnDockedEdge()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();

        _sensor.RaisePointerEntered();

        Assert.Equal(FloatingToolbarController.ToolbarState.Revealed, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Equal(new PhysicalRectangle(860, 0, 200, 44), _view.LastShownBounds);
        Assert.False(_sensor.IsArmed);
        Assert.Equal(1, _sensor.DisarmCount);
    }

    [Fact]
    public void SensorEntryIsIgnoredWhileForegroundIsFullscreen()
    {
        using FloatingToolbarController controller = CreateController(fullscreenGuard: _ => true);
        controller.Start();

        _sensor.RaisePointerEntered();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.True(_sensor.IsArmed);
        Assert.False(_view.IsVisible);
    }

    [Fact]
    public void PointerExitSchedulesLingerAndReentryCancelsIt()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();

        _view.RaisePointerExited();
        Assert.True(_timer.IsRunning);
        Assert.Equal(TimeSpan.FromMilliseconds(600), _timer.PendingDelay);

        _view.RaisePointerEntered();
        Assert.False(_timer.IsRunning);

        // linger 未触发前保持显示。
        _timer.Fire();
        Assert.True(_view.IsVisible);
    }

    [Fact]
    public void LingerTimeoutHidesToolbarAndRearmsSensor()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();
        _view.RaisePointerExited();

        _timer.Fire();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.False(_view.IsVisible);
        Assert.True(_sensor.IsArmed);
        Assert.Equal(new PhysicalRectangle(0, 0, 1920, 2), _sensor.ArmedBounds);
    }

    [Fact]
    public void DragReleaseNearFreeEdgeSnapsHidesAndPersists()
    {
        var persisted = new List<FloatingToolbarSettings>();
        using FloatingToolbarController controller = CreateController(persisted: persisted);
        controller.Start();
        _sensor.RaisePointerEntered();

        _view.RaiseDragStarted();
        Assert.Equal(FloatingToolbarController.ToolbarState.Dragging, controller.State);
        // 拖到左边缘（未占用）附近松手。
        _view.SimulatedBounds = new PhysicalRectangle(1, 500, 200, 44);
        _view.RaiseDragCompleted();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.False(_view.IsVisible);
        Assert.True(_sensor.IsArmed);
        // 左边感应条：贴左缘、竖跨全屏。
        Assert.Equal(new PhysicalRectangle(0, 0, 2, 1080), _sensor.ArmedBounds);
        FloatingToolbarSettings only = Assert.Single(persisted);
        Assert.Equal(ScreenEdge.Left, only.Edge);
        Assert.Equal(ScreenEdge.Left, controller.Settings.Edge);
    }

    [Fact]
    public void DragReleaseNearOccupiedEdgeFallsBackToFloating()
    {
        using FloatingToolbarController controller = CreateController(
            occupiedEdges: new HashSet<ScreenEdge> { ScreenEdge.Top });
        controller.Start();
        _sensor.RaisePointerEntered();
        _view.RaiseDragStarted();

        _view.SimulatedBounds = new PhysicalRectangle(860, 3, 200, 44);
        _view.RaiseDragCompleted();

        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedFloating, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Equal(new PhysicalRectangle(860, 3, 200, 44), _view.LastShownBounds);
        Assert.False(_sensor.IsArmed);
    }

    [Fact]
    public void DragReleaseInScreenCenterStaysFloating()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();
        _view.RaiseDragStarted();

        _view.SimulatedBounds = new PhysicalRectangle(700, 500, 200, 44);
        _view.RaiseDragCompleted();

        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedFloating, controller.State);
        Assert.True(_view.IsVisible);
        Assert.False(_sensor.IsArmed);
    }

    [Fact]
    public void SuspendHidesForScreenshotAndResumeReturnsToEdge()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();
        _view.RaisePointerExited();

        controller.Suspend();

        Assert.Equal(FloatingToolbarController.ToolbarState.Suspended, controller.State);
        Assert.False(_view.IsVisible);
        Assert.False(_timer.IsRunning);

        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.True(_sensor.IsArmed);
    }

    [Fact]
    public void SuspendRestoresFloatingPositionAfterScreenshot()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();
        _view.RaiseDragStarted();
        _view.SimulatedBounds = new PhysicalRectangle(700, 500, 200, 44);
        _view.RaiseDragCompleted();
        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedFloating, controller.State);

        controller.Suspend();
        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedFloating, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Equal(new PhysicalRectangle(700, 500, 200, 44), _view.LastShownBounds);
    }

    [Fact]
    public void DismissSnapsBackToDockedEdge()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();

        controller.Dismiss();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.False(_view.IsVisible);
        Assert.True(_sensor.IsArmed);
    }

    [Fact]
    public void DisplayChangeRearmsSensorOnNearestMonitor()
    {
        // 热插拔后原停靠显示器消失：monitorOf 回落到一个右侧显示器。
        var remapped = new PhysicalRectangle(1920, 0, 1920, 1080);
        using FloatingToolbarController controller = CreateController(monitorOf: _ => remapped);
        controller.Start();
        Assert.Equal(new PhysicalRectangle(0, 0, 1920, 2), _sensor.ArmedBounds);

        _sensor.RaiseDisplayChanged();

        Assert.Equal(new PhysicalRectangle(1920, 0, 1920, 2), _sensor.ArmedBounds);
    }

    [Fact]
    public void ApplySettingsDisabledStopsAndTearsDownSensor()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();

        controller.ApplySettings(new FloatingToolbarSettings(false, ScreenEdge.Top, true, 600));

        Assert.Equal(FloatingToolbarController.ToolbarState.Inactive, controller.State);
        Assert.False(_view.IsVisible);
        Assert.True(_sensor.Disposed);
    }

    [Fact]
    public void ApplySettingsEdgeChangeRearmsOnNewEdge()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();

        controller.ApplySettings(new FloatingToolbarSettings(true, ScreenEdge.Right, true, 600));

        Assert.Equal(new PhysicalRectangle(1918, 0, 2, 1080), _sensor.ArmedBounds);
    }

    private sealed class FakeToolbarView : IFloatingToolbarView
    {
        public event EventHandler? PointerEntered;

        public event EventHandler? PointerExited;

        public event EventHandler<PhysicalRectangle>? DragStarted;

        public event EventHandler<PhysicalRectangle>? DragCompleted;

        public event EventHandler<FloatingToolbarCommand>? CommandInvoked;

        public bool IsVisible { get; private set; }

        public PhysicalRectangle LastShownBounds { get; private set; }

        public PhysicalRectangle SimulatedBounds { get; set; } = new(860, 0, 200, 44);

        public nint Handle => 0x2000;

        public PhysicalRectangle GetPreferredSize() => new(0, 0, 200, 44);

        public PhysicalRectangle GetBounds() => SimulatedBounds;

        public void ShowAt(PhysicalRectangle bounds)
        {
            LastShownBounds = bounds;
            SimulatedBounds = bounds;
            IsVisible = true;
        }

        public void Hide() => IsVisible = false;

        public void Dispose()
        {
        }

        public void RaisePointerEntered() =>
            PointerEntered?.Invoke(this, EventArgs.Empty);

        public void RaisePointerExited() =>
            PointerExited?.Invoke(this, EventArgs.Empty);

        public void RaiseDragStarted() =>
            DragStarted?.Invoke(this, SimulatedBounds);

        public void RaiseDragCompleted() =>
            DragCompleted?.Invoke(this, SimulatedBounds);

        public void RaiseCommand(FloatingToolbarCommand command) =>
            CommandInvoked?.Invoke(this, command);
    }

    private sealed class FakeEdgeSensor : IEdgeSensor
    {
        public event EventHandler? PointerEntered;

        public event EventHandler? DisplayChanged;

        public bool IsArmed { get; private set; }

        public nint Handle => 0x1000;

        public PhysicalRectangle? ArmedBounds { get; private set; }

        public int DisarmCount { get; private set; }

        public bool Disposed { get; private set; }

        public void Arm(PhysicalRectangle bounds)
        {
            ArmedBounds = bounds;
            IsArmed = true;
        }

        public void Disarm()
        {
            IsArmed = false;
            DisarmCount++;
        }

        public void Dispose() => Disposed = true;

        public void RaisePointerEntered() =>
            PointerEntered?.Invoke(this, EventArgs.Empty);

        public void RaiseDisplayChanged() =>
            DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeDelayTimer : IFloatingToolbarDelayTimer
    {
        public event EventHandler? Tick;

        public TimeSpan? PendingDelay { get; private set; }

        public bool IsRunning { get; private set; }

        public void Start(TimeSpan delay)
        {
            PendingDelay = delay;
            IsRunning = true;
        }

        public void Stop() => IsRunning = false;

        public void Dispose()
        {
        }

        public void Fire()
        {
            // 模拟真实单发计时器语义：被 Stop 后不再触发。
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}
