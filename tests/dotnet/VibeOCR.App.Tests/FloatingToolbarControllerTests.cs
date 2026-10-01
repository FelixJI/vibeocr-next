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
        List<FloatingToolbarSettings>? persisted = null,
        Action<FloatingToolbarSettings>? persist = null)
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
            persist ?? (next => persisted?.Add(next)));
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
    public void FailedDragPositionSaveKeepsPreviousEdgeAndCurrentFloatingPosition()
    {
        using FloatingToolbarController controller = CreateController(
            persist: _ => throw new IOException("settings read-only"));
        controller.Start();
        _sensor.RaisePointerEntered();
        _view.RaiseDragStarted();
        var dragged = new PhysicalRectangle(1, 500, 200, 44);
        _view.SimulatedBounds = dragged;

        _view.RaiseDragCompleted();

        Assert.Equal(ScreenEdge.Top, controller.Settings.Edge);
        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedFloating, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Equal(dragged, _view.LastShownBounds);
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
        Assert.Equal(0, controller.ArmedSensorHandle);

        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.True(_sensor.IsArmed);
    }

    [Fact]
    public void SuspendFromHiddenRemovesSensorAndResumeRearms()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);

        // 截图避让从自动收起态进入：感应条也必须撤防，成品无感应条。
        controller.Suspend();

        Assert.Equal(FloatingToolbarController.ToolbarState.Suspended, controller.State);
        Assert.False(_view.IsVisible);
        Assert.False(_sensor.IsArmed);
        Assert.Equal(0, controller.ArmedSensorHandle);
        _sensor.RaisePointerEntered();
        Assert.Equal(FloatingToolbarController.ToolbarState.Suspended, controller.State);

        // 取消/结束后恢复原态：重新贴边布防，不强制显示。
        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.False(_view.IsVisible);
        Assert.True(_sensor.IsArmed);
        Assert.Equal(new PhysicalRectangle(0, 0, 1920, 2), _sensor.ArmedBounds);
    }

    [Fact]
    public void SuspendFromUserHiddenKeepsUserHiddenAfterResume()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        controller.Hide();
        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);

        controller.Suspend();
        Assert.Equal(FloatingToolbarController.ToolbarState.Suspended, controller.State);
        Assert.False(_sensor.IsArmed);
        Assert.False(_view.IsVisible);

        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_sensor.IsArmed);
        Assert.False(_view.IsVisible);
    }

    [Fact]
    public void SuspendDuringDraggingEndsDragViaExistingSemanticsAndAvoidsCapture()
    {
        var persisted = new List<FloatingToolbarSettings>();
        using FloatingToolbarController controller = CreateController(persisted: persisted);
        controller.Start();
        _sensor.RaisePointerEntered();
        _view.RaiseDragStarted();
        Assert.Equal(FloatingToolbarController.ToolbarState.Dragging, controller.State);

        // 拖拽中触发热键截图：按既有拖动完成语义收尾后让位，
        // 浮栏与感应条均不入图。
        controller.Suspend();

        Assert.Equal(FloatingToolbarController.ToolbarState.Suspended, controller.State);
        Assert.False(_view.IsVisible);
        Assert.False(_sensor.IsArmed);
        // 释放点近顶边（未占用）→ 贴边偏好按既有规则持久化。
        Assert.Equal(ScreenEdge.Top, Assert.Single(persisted).Edge);

        // 结束恢复合理可见态：自动收起重新布防。
        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.False(_view.IsVisible);
        Assert.True(_sensor.IsArmed);
    }

    [Fact]
    public void SuspendDuringDraggingRestoresFloatingPositionAfterCapture()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();
        _view.RaiseDragStarted();
        // 拖到屏幕中央：无吸附边，按既有语义原位浮动。
        _view.SimulatedBounds = new PhysicalRectangle(700, 500, 200, 44);

        controller.Suspend();

        Assert.Equal(FloatingToolbarController.ToolbarState.Suspended, controller.State);
        Assert.False(_view.IsVisible);
        Assert.False(_sensor.IsArmed);

        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedFloating, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Equal(new PhysicalRectangle(700, 500, 200, 44), _view.LastShownBounds);
    }

    [Fact]
    public void HiddenByUserPreferenceChangedDuringSuspendWinsOnResume()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        controller.Suspend();

        // 截图避让期间实时设置改为主动隐藏：恢复时尊重当前偏好。
        controller.ApplySettings(new FloatingToolbarSettings(true, ScreenEdge.Top, true, 600, true));

        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_sensor.IsArmed);
        Assert.False(_view.IsVisible);
    }

    [Fact]
    public void ResumeAfterHiddenByUserClearedDuringSuspendRearmsSensor()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        controller.Hide();
        controller.Suspend();

        // 避让期间取消主动隐藏：恢复后按新偏好回到自动收起，重新布防。
        controller.ApplySettings(new FloatingToolbarSettings(true, ScreenEdge.Top, true, 600, false));

        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.True(_sensor.IsArmed);
        Assert.False(_view.IsVisible);
        Assert.Equal(new PhysicalRectangle(0, 0, 1920, 2), _sensor.ArmedBounds);
    }

    [Fact]
    public void ResumeAfterHiddenByUserClearedDuringSuspendShowsDockedWhenNotAutoHide()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        controller.Hide();
        controller.Suspend();

        // 避让期间取消主动隐藏且改为常显：恢复后停靠显示。
        controller.ApplySettings(new FloatingToolbarSettings(true, ScreenEdge.Top, false, 600, false));

        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedDocked, controller.State);
        Assert.True(_view.IsVisible);
        Assert.False(_sensor.IsArmed);
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
    public void UserHideDisarmsSensorAndHoverDoesNotReveal()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        Assert.True(_sensor.IsArmed);

        controller.Hide();

        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_view.IsVisible);
        Assert.False(_sensor.IsArmed);
        Assert.Equal(0, controller.ArmedSensorHandle);

        // 主动隐藏态不能被感应 hover 恢复。
        _sensor.RaisePointerEntered();
        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_view.IsVisible);

        // 感应条保持撤防，后续自动收起/揭示机制不介入。
        _sensor.RaiseDisplayChanged();
        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_sensor.IsArmed);
    }

    [Fact]
    public void UserHidePersistsPreferenceAndRestartStaysUserHidden()
    {
        var persisted = new List<FloatingToolbarSettings>();
        using (FloatingToolbarController first = CreateController(persisted: persisted))
        {
            first.Start();
            first.Hide();
        }

        FloatingToolbarSettings saved = Assert.Single(persisted);
        Assert.True(saved.HiddenByUser);
        Assert.True(saved.Enabled);

        // 重启：按保存的偏好直接进入主动隐藏，不布防感应条。
        using FloatingToolbarController second = CreateController(settings: saved);
        second.Start();

        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, second.State);
        Assert.False(_sensor.IsArmed);
        Assert.False(_view.IsVisible);
    }

    [Fact]
    public void FailedUserHideKeepsVisibleStateAndPreference()
    {
        using FloatingToolbarController controller = CreateController(
            settings: new FloatingToolbarSettings(true, ScreenEdge.Top, false, 600),
            persist: _ => throw new IOException("settings read-only"));
        controller.Start();

        Assert.Throws<IOException>(() => controller.Hide());

        Assert.Equal(FloatingToolbarController.ToolbarState.PinnedDocked, controller.State);
        Assert.True(_view.IsVisible);
        Assert.False(controller.Settings.HiddenByUser);
    }

    [Fact]
    public void FailedUserShowRemainsHiddenAndCanPersistOnRetry()
    {
        bool readOnly = true;
        var persisted = new List<FloatingToolbarSettings>();
        using FloatingToolbarController controller = CreateController(
            settings: new FloatingToolbarSettings(true, ScreenEdge.Top, true, 600, true),
            persist: next =>
            {
                if (readOnly) throw new IOException("settings read-only");
                persisted.Add(next);
            });
        controller.Start();

        Assert.Throws<IOException>(() => controller.Show());
        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_view.IsVisible);
        Assert.True(controller.Settings.HiddenByUser);

        readOnly = false;
        controller.Show();
        Assert.Equal(FloatingToolbarController.ToolbarState.Revealed, controller.State);
        Assert.False(Assert.Single(persisted).HiddenByUser);
    }

    [Fact]
    public void ShowFromUserHiddenRevealsAndAutoHideStillWorksAfterwards()
    {
        var persisted = new List<FloatingToolbarSettings>();
        using FloatingToolbarController controller = CreateController(persisted: persisted);
        controller.Start();
        controller.Hide();
        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);

        controller.Show();

        // 找回：偏好清零并持久化，工具栏显示。
        Assert.Equal(FloatingToolbarController.ToolbarState.Revealed, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Equal(2, persisted.Count);
        Assert.False(persisted[^1].HiddenByUser);

        // 离开后 linger 收回：回到正常自动收起，感应条重新布防且可再揭示。
        _view.RaisePointerExited();
        _timer.Fire();
        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.True(_sensor.IsArmed);
        _sensor.RaisePointerEntered();
        Assert.Equal(FloatingToolbarController.ToolbarState.Revealed, controller.State);
    }

    [Fact]
    public void ShowFromAutoHiddenRevealsWithoutPersisting()
    {
        var persisted = new List<FloatingToolbarSettings>();
        using FloatingToolbarController controller = CreateController(persisted: persisted);
        controller.Start();
        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);

        controller.Show();

        // 自动收起态显式显示：偏好本就是 false，不需要写配置。
        Assert.Equal(FloatingToolbarController.ToolbarState.Revealed, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Empty(persisted);
    }

    [Fact]
    public void ToggleSwitchesBetweenVisibleAndUserHidden()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();

        controller.Toggle();
        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_view.IsVisible);
        Assert.False(_sensor.IsArmed);

        controller.Toggle();
        Assert.Equal(FloatingToolbarController.ToolbarState.Revealed, controller.State);
        Assert.True(_view.IsVisible);
    }

    [Fact]
    public void ToggleFromAutoHiddenEntersUserHidden()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();

        controller.Toggle();

        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_sensor.IsArmed);
    }

    [Fact]
    public void ToggleIsIgnoredWhileInactiveAndSuspended()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Toggle();
        Assert.Equal(FloatingToolbarController.ToolbarState.Inactive, controller.State);

        controller.Start();
        controller.Suspend();
        controller.Toggle();
        Assert.Equal(FloatingToolbarController.ToolbarState.Suspended, controller.State);

        controller.Resume();
        controller.ApplySettings(new FloatingToolbarSettings(false, ScreenEdge.Top, true, 600));
        controller.Toggle();
        Assert.Equal(FloatingToolbarController.ToolbarState.Inactive, controller.State);
    }

    [Fact]
    public void ApplySettingsHiddenByUserTransitionsLive()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);

        controller.ApplySettings(new FloatingToolbarSettings(true, ScreenEdge.Top, true, 600, true));

        Assert.Equal(FloatingToolbarController.ToolbarState.UserHidden, controller.State);
        Assert.False(_sensor.IsArmed);
        Assert.False(_view.IsVisible);

        controller.ApplySettings(new FloatingToolbarSettings(true, ScreenEdge.Top, true, 600, false));

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.True(_sensor.IsArmed);
        Assert.False(_view.IsVisible);
    }

    [Fact]
    public void ApplySettingsEdgeChangeRearmsOnNewEdge()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();

        controller.ApplySettings(new FloatingToolbarSettings(true, ScreenEdge.Right, true, 600));

        Assert.Equal(new PhysicalRectangle(1918, 0, 2, 1080), _sensor.ArmedBounds);
    }

    [Fact]
    public void StartAppliesConfiguredThemeToView()
    {
        using FloatingToolbarController controller = CreateController(
            new FloatingToolbarSettings(true, ScreenEdge.Top, true, 600, false, FloatingToolbarTheme.Dark));

        controller.Start();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.Equal([FloatingToolbarTheme.Dark], _view.AppliedThemes);
    }

    [Fact]
    public void ThemeOnlySettingsChangeKeepsStateSensorAndLingerSemantics()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();
        _sensor.RaisePointerEntered();

        controller.ApplySettings(new FloatingToolbarSettings(
            true, ScreenEdge.Top, true, 600, false, FloatingToolbarTheme.Dark));

        // 主题变化只重涂外观：状态、可见性与 linger 语义全部保持，
        // 不布防/撤防感应条、不重启计时。
        Assert.Equal(FloatingToolbarController.ToolbarState.Revealed, controller.State);
        Assert.True(_view.IsVisible);
        Assert.Equal(FloatingToolbarTheme.Dark, _view.AppliedThemes[^1]);

        _view.RaisePointerExited();
        Assert.Equal(TimeSpan.FromMilliseconds(600), _timer.PendingDelay);
    }

    [Fact]
    public void ThemeChangeDuringSuspensionDoesNotCancelSuspensionOrResume()
    {
        using FloatingToolbarController controller = CreateController();
        controller.Start();

        controller.Suspend();
        controller.ApplySettings(new FloatingToolbarSettings(
            true, ScreenEdge.Top, true, 600, false, FloatingToolbarTheme.Light));

        // 截图避让不因主题变化取消：感应条保持撤防，成品无污染。
        Assert.Equal(FloatingToolbarController.ToolbarState.Suspended, controller.State);
        Assert.False(_view.IsVisible);
        Assert.False(_sensor.IsArmed);

        controller.Resume();

        Assert.Equal(FloatingToolbarController.ToolbarState.Hidden, controller.State);
        Assert.True(_sensor.IsArmed);
        Assert.Equal(FloatingToolbarTheme.Light, _view.AppliedThemes[^1]);
    }

    [Fact]
    public void NewDefaultLingerDrivesTimeoutAndLingerUpdateAppliesOnNextExit()
    {
        using FloatingToolbarController controller = CreateController(
            FloatingToolbarSettings.Default with { Enabled = true });
        controller.Start();
        _sensor.RaisePointerEntered();

        // 新无配置默认 300ms 驱动离开计时，不误用于展开/其他延迟。
        _view.RaisePointerExited();
        Assert.Equal(TimeSpan.FromMilliseconds(300), _timer.PendingDelay);
        _view.RaisePointerEntered();
        Assert.False(_timer.IsRunning);

        // 新 linger 值下次离开生效。
        controller.ApplySettings(new FloatingToolbarSettings(true, ScreenEdge.Top, true, 2500));
        _view.RaisePointerExited();
        Assert.Equal(TimeSpan.FromMilliseconds(2500), _timer.PendingDelay);
    }

    private sealed class FakeToolbarView : IFloatingToolbarView
    {
        public event EventHandler? PointerEntered;

        public event EventHandler? PointerExited;

        public event EventHandler<PhysicalRectangle>? DragStarted;

        public event EventHandler<PhysicalRectangle>? DragCompleted;

        public event EventHandler<FloatingToolbarCommand>? CommandInvoked;

        public bool IsVisible { get; private set; }

        public List<FloatingToolbarTheme> AppliedThemes { get; } = [];

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

        public void ApplyTheme(FloatingToolbarTheme theme) => AppliedThemes.Add(theme);

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
