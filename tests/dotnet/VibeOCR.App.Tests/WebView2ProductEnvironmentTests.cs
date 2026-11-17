using System.Runtime.InteropServices;
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

  [Fact]
  public async Task InitializeCoreWebView2RequiresAWebView()
  {
    // 窄边界快失败：无 WebView 时直接 ArgumentNullException，不隐藏调用错误。
    await Assert.ThrowsAsync<ArgumentNullException>(
        () => WebView2ProductEnvironment.InitializeCoreWebView2Async(null!));
  }

  [Theory]
  [InlineData(true, typeof(COMException))]
  [InlineData(true, typeof(FileNotFoundException))]
  [InlineData(true, typeof(DllNotFoundException))]
  [InlineData(true, typeof(FileLoadException))]
  [InlineData(false, typeof(OperationCanceledException))]
  [InlineData(false, typeof(OutOfMemoryException))]
  [InlineData(false, typeof(InvalidOperationException))]
  public void OnlyRecoverableStartupFailuresEnterTheProductRepairBoundary(
      bool expected,
      Type failure)
  {
    // #321：随包 WebView 可恢复启动失败（COM/文件加载）才包装产品报修；
    // 取消、OOM 等无关异常不进入报修边界（旧实现无此边界）。
    Assert.Equal(
        expected,
        WebView2StartupException.IsRecoverableStartupFailure(
            (Exception)Activator.CreateInstance(failure)!));
  }

  [Fact]
  public void OnlyBundledEnvironmentStartupFailuresBecomeProductRepair()
  {
    // #321：随包环境的 COM/文件加载失败进入固定产品报修（旧实现把 dev
    // 系统 Evergreen 的同类失败也包装成随包损坏，本分支测试在其上失败）。
    var cause = new COMException(
        "WebView2 environment HRESULT failure", unchecked((int)0x80070002));

    var repair = WebView2ProductEnvironment.WrapHostInitializationFailure(
        bundledEnvironment: true, cause);

    Assert.NotNull(repair);
    Assert.IsAssignableFrom<InvalidOperationException>(repair);
    Assert.Same(cause, repair!.InnerException);
    Assert.Equal(
        "VibeOCR 随包 WebView2 运行库初始化失败。" + WebView2StartupException.RepairGuidance,
        repair.Message);
    // 报修文案不展示实现类型名；原始异常已供日志。
    Assert.DoesNotContain("COMException", repair.Message);
  }

  [Theory]
  [InlineData(typeof(FileNotFoundException))]
  [InlineData(typeof(DllNotFoundException))]
  [InlineData(typeof(FileLoadException))]
  public void BundledRecoverableStartupFailuresBecomeProductRepair(Type failure)
  {
    var repair = WebView2ProductEnvironment.WrapHostInitializationFailure(
        bundledEnvironment: true,
        (Exception)Activator.CreateInstance(failure)!);

    Assert.NotNull(repair);
  }

  [Theory]
  [InlineData(typeof(COMException))]
  [InlineData(typeof(FileNotFoundException))]
  [InlineData(typeof(OperationCanceledException))]
  [InlineData(typeof(OutOfMemoryException))]
  public void SystemEvergreenFailuresAndUnrelatedExceptionsPassThrough(
      Type failure)
  {
    // dev 系统 Evergreen 环境的 COM/文件失败、以及取消/OOM，均不进入随包
    // 报修边界，按原异常透传。
    Assert.Null(WebView2ProductEnvironment.WrapHostInitializationFailure(
        bundledEnvironment: false,
        (Exception)Activator.CreateInstance(failure)!));
  }
}
