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
}
