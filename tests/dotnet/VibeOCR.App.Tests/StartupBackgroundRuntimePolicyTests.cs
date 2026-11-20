using VibeOCR.App;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class StartupBackgroundRuntimePolicyTests
{
  [Theory]
  [InlineData(false, null)]
  [InlineData(false, "")]
  [InlineData(false, "screenshot-e2e")]
  [InlineData(false, "text-selection-e2e")]
  [InlineData(false, "capture-first-frame-e2e")]
  [InlineData(false, "capture-replacement-e2e")]
  [InlineData(true, "t6")]
  [InlineData(false, "native-actions-e2e")]
  public void OnlyExplicitDiagnosticProbesStartInBackground(bool expected, string? smoke)
    => Assert.Equal(expected, App.ShouldStartBackgroundRuntime(smoke));

  [Theory]
  [InlineData(true, null, false)]
  [InlineData(false, null, true)]
  [InlineData(false, "", false)]
  [InlineData(false, "screenshot-e2e", false)]
  [InlineData(false, "text-selection-e2e", false)]
  [InlineData(false, "capture-first-frame-e2e", false)]
  [InlineData(false, "capture-replacement-e2e", false)]
  [InlineData(false, "t6", false)]
  [InlineData(false, "native-actions-e2e", false)]
  [InlineData(false, "paddle-modes-e2e", false)]
  [InlineData(false, "web-ready", false)]
  [InlineData(false, "1", false)]
  [InlineData(false, "t3", false)]
  public void OnlyProductionLaunchesAutoPrepare(
      bool expected, string? smoke, bool velopackUpdateSelfTest)
    => Assert.Equal(
        expected,
        App.ShouldAutoPrepareRuntimeOnLaunch(smoke, velopackUpdateSelfTest));
}
