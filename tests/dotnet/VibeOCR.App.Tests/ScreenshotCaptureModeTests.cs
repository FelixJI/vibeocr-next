using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Inference;
using VibeOCR.App.Features.Shell;
using VibeOCR.Platform.Windows;

using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// #236 截图模式契约：新动作默认不绑定、模式菜单构造（单/多显示器）、
/// 命令请求经 ViewModel 路由到 InputService 对应入口（旧入口零变化）。
/// </summary>
public class ScreenshotCaptureModeTests
{
  [Fact]
  public void NewModeActionsAreKnownDefaultUnboundAndLegacyDefaultUnchanged()
  {
    string[] expected =
    [
        HotkeyActionCatalog.ScreenshotFullscreen,
            HotkeyActionCatalog.ScreenshotWindow,
            HotkeyActionCatalog.ScreenshotMonitor,
            HotkeyActionCatalog.ScreenshotFixed,
            HotkeyActionCatalog.ScreenshotRepeat,
            HotkeyActionCatalog.ScreenshotDelayed,
            HotkeyActionCatalog.ScreenshotMenu,
        ];
    Assert.Subset(HotkeyActionCatalog.Actions.ToHashSet(), expected.ToHashSet());
    foreach (string action in expected)
    {
      Assert.True(HotkeyActionCatalog.IsKnown(action));
      Assert.Null(HotkeyActionCatalog.DefaultBinding(action));
    }

    // 既有默认不变：仅 screenshot_recognize 携带 Ctrl+Alt+Q。
    Assert.Equal("Ctrl+Alt+Q", HotkeyActionCatalog.DefaultBinding(
        HotkeyActionCatalog.ScreenshotRecognize));
    Assert.Null(HotkeyActionCatalog.DefaultBinding(HotkeyActionCatalog.ScreenshotEdit));
    Assert.Null(HotkeyActionCatalog.DefaultBinding(HotkeyActionCatalog.ScreenshotFixed));
  }

  [Fact]
  public void ModeMenuBuildsDirectItemForSingleMonitorAndAlwaysOffersOptions()
  {
    IReadOnlyList<ScreenshotModeMenuItem> items = ScreenshotModeMenu.BuildItems(
        [new ScreenMonitorTarget(new PhysicalRectangle(0, 0, 2194, 1234), true, @"\\.\DISPLAY1")]);

    ScreenshotModeMenuItem monitor = Assert.Single(items, item =>
        item.ActionId == HotkeyActionCatalog.ScreenshotMonitor);
    Assert.Null(monitor.Children);
    Assert.NotNull(monitor.MonitorDevice);
    ScreenshotModeMenuItem options = Assert.Single(items, item => item.IsOptions);
    Assert.Equal("截图选项…", options.Label);
    Assert.Equal(HotkeyActionCatalog.ScreenshotFullscreen, items[0].ActionId);
    Assert.Equal(HotkeyActionCatalog.ScreenshotWindow, items[1].ActionId);
    Assert.Equal(HotkeyActionCatalog.ScreenshotRepeat,
        items.First(item => item.Label == "重复上次区域").ActionId);
    Assert.Equal(HotkeyActionCatalog.ScreenshotDelayed,
        items.First(item => item.Label == "延时截图").ActionId);
    Assert.Equal(HotkeyActionCatalog.ScreenshotFixed,
        items.First(item => item.Label == "固定区域截图").ActionId);
  }

  [Fact]
  public void ModeMenuExpandsSubmenuForMultipleMonitorsWithDeviceTargets()
  {
    IReadOnlyList<ScreenshotModeMenuItem> items = ScreenshotModeMenu.BuildItems(
    [
        new ScreenMonitorTarget(new PhysicalRectangle(0, 0, 1920, 1080), true, @"\\.\DISPLAY1"),
            new ScreenMonitorTarget(new PhysicalRectangle(1920, 0, 1280, 720), false, @"\\.\DISPLAY2"),
        ]);

    ScreenshotModeMenuItem monitor = Assert.Single(items, item => item.Label == "显示器截图");
    Assert.Null(monitor.ActionId);
    Assert.Equal(2, monitor.Children!.Count);
    Assert.Equal(@"\\.\DISPLAY1", monitor.Children[0].MonitorDevice);
    Assert.Equal(@"\\.\DISPLAY2", monitor.Children[1].MonitorDevice);
    Assert.Contains("主", monitor.Children[0].Label);
    Assert.DoesNotContain("主", monitor.Children[1].Label);
    Assert.All(monitor.Children, child =>
        Assert.Equal(HotkeyActionCatalog.ScreenshotMonitor, child.ActionId));
  }

  [Fact]
  public async Task ViewModelRoutesAreaRequestsAndKeepsRegionFlowUnchanged()
  {
    var inputs = new RoutingInputService();
    var viewModel = new RecognitionViewModel(new DeferredInferenceClient(), inputs);

    await viewModel.CaptureScreenshotSessionAsync(
        null, new ScreenshotCaptureRequest(ScreenshotCaptureKind.Fullscreen),
        CancellationToken.None);
    Assert.Equal(1, inputs.AreaCalls);
    Assert.Equal(ScreenshotCaptureKind.Fullscreen, inputs.LastAreaRequest?.Kind);
    Assert.Equal(0, inputs.DelayedCalls);

    await viewModel.CaptureScreenshotSessionAsync(
        null, new ScreenshotCaptureRequest(ScreenshotCaptureKind.Monitor, @"\\.\DISPLAY2"),
        CancellationToken.None);
    Assert.Equal(ScreenshotCaptureKind.Monitor, inputs.LastAreaRequest?.Kind);
    Assert.Equal(@"\\.\DISPLAY2", inputs.LastAreaRequest?.MonitorDevice);

    await viewModel.CaptureScreenshotSessionAsync(
        null, new ScreenshotCaptureRequest(ScreenshotCaptureKind.DelayedRegion),
        CancellationToken.None);
    Assert.Equal(1, inputs.DelayedCalls);
    Assert.Equal(2, inputs.AreaCalls);

    await viewModel.CaptureScreenshotSessionAsync(
        null, new ScreenshotCaptureRequest(ScreenshotCaptureKind.FixedRegion),
        CancellationToken.None);
    Assert.Equal(ScreenshotCaptureKind.FixedRegion, inputs.LastAreaRequest?.Kind);

    // 旧入口（request 为 null）保持既有框选语义。
    await viewModel.CaptureScreenshotSessionAsync(null, null, CancellationToken.None);
    await viewModel.CaptureScreenshotSessionAsync(null, CancellationToken.None);
    Assert.Equal(2, inputs.WithActionsCalls);
    Assert.Equal(3, inputs.AreaCalls);
  }

  private sealed class RoutingInputService : IInputService
  {
    public int AreaCalls;
    public int DelayedCalls;
    public int WithActionsCalls;
    public ScreenshotCaptureRequest? LastAreaRequest;

    public Task<RecognitionInput?> CaptureAreaAsync(
        ScreenshotCaptureRequest request, CancellationToken cancellationToken)
    {
      AreaCalls++;
      LastAreaRequest = request;
      return Task.FromResult<RecognitionInput?>(new RecognitionInput(
          [1, 2, 3, 4], "image/png", "capture.png", "screenshot"));
    }

    public Task<RecognitionInput?> CaptureDelayedRegionAsync(
        ScreenshotSelectionActions? actions, CancellationToken cancellationToken)
    {
      DelayedCalls++;
      return Task.FromResult<RecognitionInput?>(new RecognitionInput(
          [1, 2, 3, 4], "image/png", "capture.png", "screenshot"));
    }

    public Task<RecognitionInput?> CaptureScreenWithActionsAsync(
        ScreenshotSelectionActions? actions, CancellationToken cancellationToken)
    {
      WithActionsCalls++;
      return Task.FromResult<RecognitionInput?>(new RecognitionInput(
          [1, 2, 3, 4], "image/png", "capture.png", "screenshot"));
    }

    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken)
        => Task.FromResult<RecognitionInput?>(new RecognitionInput(
            [1, 2, 3, 4], "image/png", "file.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken)
        => PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken)
        => PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken)
        => PickFileAsync(cancellationToken);
  }
}
