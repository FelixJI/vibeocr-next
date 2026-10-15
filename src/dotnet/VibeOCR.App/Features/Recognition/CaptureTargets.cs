namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// #236 原生截图模式：Region 是既有框选入口（零行为变化）；其余为直接
/// 目标模式，全部经既有 WorkbenchCommand → ViewModel → InputService 链
/// 进入现场编辑会话，不在 Shell 层绕过状态机。
/// </summary>
public enum ScreenshotCaptureKind
{
  Region,
  Fullscreen,
  ActiveWindow,
  Monitor,
  FixedRegion,
  LastRegion,
  DelayedRegion,
}

/// <summary>
/// 一次直接目标截图请求。MonitorDevice 为 ScreenTargetQuery 的稳定设备名；
/// 空串表示“唯一/主显示器”。TargetWindow 是菜单打开前记录的真实目标窗口
/// （托盘/悬浮栏会让位，捕获时的前台可能是菜单宿主）；0 表示未记录。
/// </summary>
public sealed record ScreenshotCaptureRequest(
    ScreenshotCaptureKind Kind,
    string? MonitorDevice = null,
    nint TargetWindow = 0);
