using VibeOCR.Platform.Windows;

namespace VibeOCR.App;

internal sealed record SelfTestInstanceScope(
  string SingleInstanceName,
  string? ExclusiveMutexName)
{
  internal static SelfTestInstanceScope Resolve(
    string profile,
    string? smokeMode,
    string? instanceId)
  {
    bool isUiSmoke = smokeMode is "web-ready" or "screenshot-e2e" or
      "text-selection-e2e" or "managed-environment-e2e" or "native-actions-e2e" or "paddle-modes-e2e";
    // t6 兼容两种形态：无 instance id 保持既有生产名单例行为
    // （collect_startup_metrics 入口不变）；显式 GUID 时切换到隔离名单。
    bool optionalInstanceId = smokeMode == "t6";
    bool hasInstanceId = !string.IsNullOrWhiteSpace(instanceId);
    if (!isUiSmoke && !optionalInstanceId)
    {
      if (hasInstanceId)
      {
        throw new InvalidOperationException(
          "A self-test instance ID requires an isolated UI smoke mode.");
      }
      return new SelfTestInstanceScope($"VibeOCR-{profile}", null);
    }
    if (!hasInstanceId)
    {
      if (!optionalInstanceId)
      {
        throw new InvalidOperationException(
          "The UI smoke requires a 32-character GUID instance ID.");
      }
      // t6 未提供 instance id：保持既有生产名单例行为。
      return new SelfTestInstanceScope($"VibeOCR-{profile}", null);
    }

    if (!Guid.TryParseExact(instanceId, "N", out Guid parsedId))
    {
      throw new InvalidOperationException(
        "The UI smoke requires a 32-character GUID instance ID.");
    }

    string normalizedId = parsedId.ToString("N");
    return new SelfTestInstanceScope(
      $"VibeOCR-{profile}-self-test-{normalizedId}",
      $"{FrontendExclusiveLock.MutexName}.{normalizedId}");
  }
}
