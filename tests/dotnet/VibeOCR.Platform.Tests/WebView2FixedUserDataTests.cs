using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class WebView2FixedUserDataTests
{
  private static readonly PortableLayout Layout =
      PortableLayout.Resolve("C:\\VibeOCR", "production");

  [Fact]
  public void SameRuntimeVersionResolvesOneStableDirectory()
  {
    string first = Layout.WebView2FixedRoot("156.0.4314.58");
    string second = Layout.WebView2FixedRoot("156.0.4314.58");

    Assert.Equal(first, second);
    Assert.Equal(
        Path.Combine("C:\\VibeOCR", "state", "webview2-fixed", "156.0.4314.58"),
        first);
    // 并列目录：与旧 Evergreen 语义的 state/webview2 同层、互不嵌套。
    Assert.Equal(
        Layout.WebView2Root,
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(first))!, "webview2"));
    Assert.NotEqual(first, Layout.WebView2Root);
  }

  [Fact]
  public void DifferentRuntimeVersionsStayIsolated()
  {
    string current = Layout.WebView2FixedRoot("156.0.4314.58");
    string upgraded = Layout.WebView2FixedRoot("157.0.4341.60");

    Assert.NotEqual(current, upgraded);
    Assert.Equal(Path.GetDirectoryName(current), Path.GetDirectoryName(upgraded));
  }

  [Theory]
  [InlineData("156.0.4314")]
  [InlineData("156.0.4314.58.1")]
  [InlineData("")]
  [InlineData("  ")]
  [InlineData("../escape")]
  [InlineData("156.0.4314.58x")]
  public void RejectsNonFourPartVersions(string version)
  {
    Assert.Throws<PortableLayoutException>(
        () => Layout.WebView2FixedRoot(version));
  }

  [Fact]
  public void EnsurePortableStatePreparesOnlyTheSelectedUserDataDirectory()
  {
    // #321 回归：随包模式只准备所选 Fixed 目录；旧 state/webview2 不被创建
    // 或依赖（旧实现无条件创建旧目录，本测试在其上失败）。
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-fixed-state-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var layout = PortableLayout.Resolve(
          Path.Combine(root, "VibeOCR.WinUI.exe"), "production");
      string fixedRoot = layout.WebView2FixedRoot("156.0.4314.58");

      layout.EnsurePortableState(fixedRoot);

      Assert.True(Directory.Exists(fixedRoot));
      Assert.False(Directory.Exists(layout.WebView2Root));
      // 非 bundled 默认调用保持旧语义。
      layout.EnsurePortableState();
      Assert.True(Directory.Exists(layout.WebView2Root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}
