using System.Runtime.InteropServices;
using VibeOCR.App.Web;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WebView2ProductEnvironmentTests : IDisposable
{
  public WebView2ProductEnvironmentTests()
  {
    WebView2ProductEnvironment.ResetForTest();
  }

  public void Dispose()
  {
    WebView2ProductEnvironment.ResetForTest();
  }

  [Fact]
  public void WrapHostInitializationFailureWrapsRecoverableStartupAsEvergreenGuidance()
  {
    var comError = new COMException("E_FAIL", unchecked((int)0x80004005));
    var wrapped = WebView2ProductEnvironment.WrapHostInitializationFailure(comError);
    Assert.NotNull(wrapped);
    Assert.Contains("WebView2", wrapped.Message);
    Assert.Contains("developer.microsoft.com", wrapped.Message);
    Assert.Same(comError, wrapped.InnerException);

    Assert.Null(WebView2ProductEnvironment.WrapHostInitializationFailure(
        new OperationCanceledException()));
  }

  [Fact]
  public void ResolveBundledRuntimeFolderOnlyFollowsAppBaseDirectory()
  {
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-webview2-resolve-{Guid.NewGuid():N}");
    try
    {
      string baseDir = Path.Combine(root, "app");
      string runtime = Path.Combine(
          baseDir, WebView2ProductEnvironment.BundledFolderName);
      Directory.CreateDirectory(runtime);
      File.WriteAllText(
          Path.Combine(runtime, WebView2ProductEnvironment.BrowserExecutableName),
          "stub");

      Assert.Equal(
          Path.GetFullPath(runtime),
          WebView2ProductEnvironment.ResolveBundledRuntimeFolder(baseDir));
      Assert.Null(WebView2ProductEnvironment.ResolveBundledRuntimeFolder(
          Path.Combine(root, "elsewhere")));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}
