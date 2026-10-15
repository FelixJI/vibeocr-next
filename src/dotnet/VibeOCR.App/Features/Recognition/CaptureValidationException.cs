namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// #236 本地截图校验失败的稳定原因码：作为 recognition.captureRefused.
/// &lt;reason&gt; 状态码前缀投影到既有 FeatureValues 文案路径。
/// </summary>
public static class CaptureRefusal
{
  public const string MissingFixedRegion = "missingFixedRegion";
  public const string RegionUnavailable = "regionUnavailable";
  public const string WindowUnavailable = "windowUnavailable";
  public const string MonitorUnavailable = "monitorUnavailable";
  public const string LayoutChanged = "layoutChanged";
  public const string TooLarge = "tooLarge";

  public const string StatusCodePrefix = "recognition.captureRefused.";
}

/// <summary>
/// 本地截图校验拒绝（未配置区域/目标消失/显示器断开/越界/超限）。
/// 不继承 IOException：RunInputAsync 的 Supervisor 断开处理器不得吞掉
/// 这类与 Runtime 无关的可修复指引；消息为既有中文指引文案。
/// </summary>
public sealed class CaptureValidationException(string reasonCode, string message)
    : Exception(message)
{
  public string ReasonCode { get; } = reasonCode;

  public string StatusCode => CaptureRefusal.StatusCodePrefix + ReasonCode;
}
