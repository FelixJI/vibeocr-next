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
    bool isWebReadySmoke = smokeMode is "web-ready" or "screenshot-e2e";
    bool hasInstanceId = !string.IsNullOrWhiteSpace(instanceId);
    if (!isWebReadySmoke)
    {
      if (hasInstanceId)
      {
        throw new InvalidOperationException(
          "A self-test instance ID requires an isolated UI smoke mode.");
      }
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
