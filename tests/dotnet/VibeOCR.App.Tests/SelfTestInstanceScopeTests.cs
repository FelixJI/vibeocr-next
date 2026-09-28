using VibeOCR.App;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class SelfTestInstanceScopeTests
{
  [Fact]
  public void ProductionLaunchUsesStableNamedObjects()
  {
    SelfTestInstanceScope scope = SelfTestInstanceScope.Resolve(
      "production",
      smokeMode: null,
      instanceId: null);

    Assert.Equal("VibeOCR-production", scope.SingleInstanceName);
    Assert.Null(scope.ExclusiveMutexName);
  }

  [Theory]
  [InlineData("web-ready")]
  [InlineData("screenshot-e2e")]
  [InlineData("managed-environment-e2e")]
  public void UiSmokeUsesIsolatedNamedObjects(string smokeMode)
  {
    const string instanceId = "c240f369b28e4444b0d45f4a4d331cd0";

    SelfTestInstanceScope scope = SelfTestInstanceScope.Resolve(
      "production",
      smokeMode,
      instanceId);

    Assert.Equal(
      $"VibeOCR-production-self-test-{instanceId}",
      scope.SingleInstanceName);
    Assert.Equal(
      $@"Local\VibeOCR.Frontend.Exclusive.v2.{instanceId}",
      scope.ExclusiveMutexName);
  }

  [Theory]
  [InlineData("web-ready", null)]
  [InlineData("web-ready", "not-a-guid")]
  [InlineData("screenshot-e2e", null)]
  [InlineData("screenshot-e2e", "not-a-guid")]
  [InlineData("managed-environment-e2e", null)]
  [InlineData("managed-environment-e2e", "not-a-guid")]
  [InlineData(null, "c240f369b28e4444b0d45f4a4d331cd0")]
  public void InvalidSelfTestScopeIsRejected(string? smokeMode, string? instanceId) =>
    Assert.Throws<InvalidOperationException>(() => SelfTestInstanceScope.Resolve(
      "production",
      smokeMode,
      instanceId));
}
