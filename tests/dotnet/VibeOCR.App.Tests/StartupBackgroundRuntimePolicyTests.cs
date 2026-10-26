using VibeOCR.App;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 首次启动自动就绪策略契约：普通启动在首窗后后台准备默认环境；截图
/// 首帧/替换与 managed-environment 安装阶段保持按需启动，restart 阶段
/// 恢复。共享尝试、关闭取消与失败重试语义见 RuntimeDemandCoordinatorTests。
/// </summary>
public sealed class StartupBackgroundRuntimePolicyTests
{
  [Theory]
  [InlineData(true, null, null)]
  [InlineData(false, "capture-first-frame-e2e", null)]
  [InlineData(false, "capture-replacement-e2e", null)]
  [InlineData(false, "managed-environment-e2e", null)]
  [InlineData(true, "managed-environment-e2e", "restart")]
  public void StartupPolicyKeepsContractedDifferences(bool expected, string? smoke, string? phase)
    => Assert.Equal(expected, App.ShouldStartBackgroundRuntime(smoke, phase));
}
