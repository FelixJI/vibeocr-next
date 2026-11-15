using VibeOCR.App.Web;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WebView2ProductEnvironmentTests : IDisposable
{
  private readonly string? _previousUserDataFolder;

  public WebView2ProductEnvironmentTests()
  {
    WebView2ProductEnvironment.ResetForTest();
    // F2：不泄漏全局测试状态，Dispose 恢复原值。
    _previousUserDataFolder =
        Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
    Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", null);
  }

  public void Dispose()
  {
    WebView2ProductEnvironment.ResetForTest();
    Environment.SetEnvironmentVariable(
        "WEBVIEW2_USER_DATA_FOLDER", _previousUserDataFolder);
  }

  private static (PortableLayout Layout, string Root) TempLayout()
  {
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-webview2-env-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return (PortableLayout.Resolve(Path.Combine(root, "VibeOCR.WinUI.exe"),
        "production"), root);
  }

  [Fact]
  public void SameBundledVersionResolvesOneStableUserDataDirectory()
  {
    (PortableLayout layout, string root) = TempLayout();
    try
    {
      const string version = "156.0.4314.58";
      WebView2ProductEnvironment.Configure(layout);

      string first = layout.WebView2FixedRoot(version);
      string second = layout.WebView2FixedRoot(version);

      Assert.Equal(first, second);
      Assert.Equal(
          Path.Combine(root, "state", "webview2-fixed", version), first);
      // 旧 Evergreen 目录保持并列、不被选中。
      Assert.Equal(
          Path.Combine(root, "state", "webview2"), layout.WebView2Root);
      Assert.NotEqual(layout.WebView2Root, first);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void DifferentBundledVersionsResolveIsolatedDirectories()
  {
    (PortableLayout layout, string root) = TempLayout();
    try
    {
      string current = layout.WebView2FixedRoot("156.0.4314.58");
      string upgraded = layout.WebView2FixedRoot("157.0.4341.60");

      Assert.Equal(Path.GetDirectoryName(current), Path.GetDirectoryName(upgraded));
      Assert.NotEqual(current, upgraded);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void DevTreeWithoutBundledRuntimeKeepsTheLegacyDefault()
  {
    // 开发树（无随包目录）：选择结果为 null，EnsurePortableState 保持旧
    // state/webview2 默认；不存在第二处版本 pin。
    (PortableLayout layout, string root) = TempLayout();
    try
    {
      Assert.Null(WebView2ProductEnvironment.PrepareProductionUserData(layout));
      Assert.False(WebView2ProductEnvironment.HasBundledRuntime);

      layout.EnsurePortableState(
          WebView2ProductEnvironment.PrepareProductionUserData(layout));

      Assert.True(Directory.Exists(layout.WebView2Root));
      Assert.False(Directory.Exists(
          Path.Combine(root, "state", "webview2-fixed")));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void BundledTreeWithoutReadableVersionFailsClosedAndKeepsLegacyUntouched()
  {
    // 无版本资源的占位 msedgewebview2.exe 读不出完整四段版本：fail closed，
    // 不创建/不选中旧 state/webview2（旧目录未变契约）。
    (PortableLayout layout, string root) = TempLayout();
    string runtimeRoot = Path.Combine(root, WebView2ProductEnvironment.BundledFolderName);
    try
    {
      Directory.CreateDirectory(runtimeRoot);
      File.WriteAllText(
          Path.Combine(runtimeRoot, WebView2ProductEnvironment.BrowserExecutableName),
          "stub");

      Assert.Throws<InvalidOperationException>(
          () => WebView2ProductEnvironment.PrepareProductionUserData(layout, root));
      Assert.Null(Directory.GetDirectories(root).FirstOrDefault(
          path => Path.GetFileName(path) == "state"));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void BundledFolderResolutionFollowsAppBaseDirectoryOnly()
  {
    (PortableLayout _, string root) = TempLayout();
    try
    {
      string empty = Path.Combine(root, "empty");
      Directory.CreateDirectory(empty);
      string bundledBase = Path.Combine(root, "bundled");
      string runtimeRoot = Path.Combine(bundledBase, WebView2ProductEnvironment.BundledFolderName);
      Directory.CreateDirectory(runtimeRoot);
      File.WriteAllText(
          Path.Combine(runtimeRoot, WebView2ProductEnvironment.BrowserExecutableName), "x");

      Assert.Null(WebView2ProductEnvironment.ResolveBundledRuntimeFolder(empty));
      Assert.Equal(
          runtimeRoot,
          WebView2ProductEnvironment.ResolveBundledRuntimeFolder(bundledBase));
      Assert.False(WebView2ProductEnvironment.IsPublishedProductLayout(bundledBase));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}
