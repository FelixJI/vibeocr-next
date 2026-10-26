using VibeOCR.ProductLayout;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class DotNetRuntimeProbeTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), $"vibeocr-dotnet-{Guid.NewGuid():N}");

  [Fact]
  public void AcceptsStandardRuntimeWithoutDesktopRuntime()
  {
    Install("Microsoft.NETCore.App", "10.0.13");

    Assert.Equal("10.0.13", DotNetRuntimeProbe.FindVersion(_root));
  }

  [Fact]
  public void RejectsOtherMajorsAndPreviewAndSelectsHighestCompatiblePatch()
  {
    Install("Microsoft.NETCore.App", "9.0.20");
    Install("Microsoft.NETCore.App", "11.0.0");
    Install("Microsoft.NETCore.App", "10.0.0-preview.1");
    Assert.Null(DotNetRuntimeProbe.FindVersion(_root));

    Install("Microsoft.NETCore.App", "10.0.9");
    Install("Microsoft.NETCore.App", "10.0.13");
    Assert.Equal("10.0.13", DotNetRuntimeProbe.FindVersion(_root));
  }

  [Fact]
  public void RejectsMissingCoreRuntimeEvenWhenDesktopDirectoryExists()
  {
    Assert.Null(DotNetRuntimeProbe.FindVersion(_root));
    Install("Microsoft.WindowsDesktop.App", "10.0.13");
    Assert.Null(DotNetRuntimeProbe.FindVersion(_root));
  }

  private void Install(string framework, string version) =>
      Directory.CreateDirectory(Path.Combine(_root, "dotnet", "shared", framework, version));

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }
}
