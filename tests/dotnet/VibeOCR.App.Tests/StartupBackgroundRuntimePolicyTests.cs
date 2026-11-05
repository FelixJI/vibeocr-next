using VibeOCR.App;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 普通原生壳保持按需获取；显式自检的原有就绪语义不变。
/// 首次计算共享、关闭取消与失败重试语义见 RuntimeDemandCoordinatorTests。
/// </summary>
public sealed class StartupBackgroundRuntimePolicyTests
{
  [Theory]
  [InlineData(false, null, null)]
  [InlineData(false, "", null)]
  [InlineData(false, "  ", null)]
  [InlineData(false, "capture-first-frame-e2e", null)]
  [InlineData(false, "capture-replacement-e2e", null)]
  [InlineData(false, "managed-environment-e2e", null)]
  [InlineData(true, "managed-environment-e2e", "restart")]
  public void StartupPolicyKeepsContractedDifferences(bool expected, string? smoke, string? phase)
    => Assert.Equal(expected, App.ShouldStartBackgroundRuntime(smoke, phase));
}
