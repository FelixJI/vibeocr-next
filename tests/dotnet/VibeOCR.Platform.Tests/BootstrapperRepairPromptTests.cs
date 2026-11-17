using VibeOCR.Bootstrapper;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class BootstrapperRepairPromptTests
{
  [Theory]
  [InlineData("随包 .NET Runtime 文件缺失：hostfxr.dll")]
  [InlineData(@"layout.missing-entry: app\WebView2Runtime\msedgewebview2.exe")]
  public void InteractiveLaunchExplainsTheFailureAndHowToRepair(string reason)
  {
    string? message = BootstrapperRepairPrompt.CreateMessage(
        reason, selfTest: false, artifactSmoke: false, userInteractive: true);

    Assert.NotNull(message);
    Assert.Contains("无法启动", message);
    Assert.Contains(reason, message);
    Assert.Contains("重新下载完整便携包", message);
  }

  [Theory]
  [InlineData(true, false, true)]
  [InlineData(false, true, true)]
  [InlineData(false, false, false)]
  public void SelfTestsAndNoninteractiveSessionsDoNotRequestAPrompt(
      bool selfTest, bool artifactSmoke, bool userInteractive)
  {
    Assert.Null(BootstrapperRepairPrompt.CreateMessage(
        "失败原因", selfTest, artifactSmoke, userInteractive));
  }
}
