using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PaddleSmokePathTests
{
  [Theory]
  [InlineData("paddle-smoke", true)]
  [InlineData("paddle-smoke/mode", true)]
  [InlineData("paddle-smoke-other/mode", false)]
  [InlineData("paddle-smoke/../outside", false)]
  [InlineData("../../app", false)]
  public void ExportsStayInOwnedOutput(string relative, bool allowed)
  {
    string output = Path.Combine(Path.GetTempPath(), $"paddle-export-{Guid.NewGuid():N}", "state", "output");
    string path = Path.Combine(output, relative);
    if (allowed)
      Assert.Equal(Path.GetFullPath(path), MainWindow.ValidatePaddleSmokeExportPath(path, output));
    else
      Assert.Throws<InvalidOperationException>(() => MainWindow.ValidatePaddleSmokeExportPath(path, output));
  }
}
