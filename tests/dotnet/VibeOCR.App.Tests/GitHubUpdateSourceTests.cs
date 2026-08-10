using VibeOCR.App.Features.Update;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// GitHubUpdateSource.SelectAsset 的 asset 选择逻辑测试。
///
/// 只接受产品化后的唯一公开资产名；开发阶段不兼容 Classic、Next 或历史命名。
/// </summary>
public sealed class GitHubUpdateSourceTests
{
    [Fact]
    public void SelectAssetPicksCanonicalVibeOcrAsset()
    {
        var assets = new[]
        {
            MakeAsset("VibeOCR-v1.0.0-win64.zip"),
            MakeAsset("VibeOCR-v1.0.0-win64.zip.sha256"),
        };

        var pkg = GitHubUpdateSource.SelectAsset(assets, "-win64.zip");
        var sha = GitHubUpdateSource.SelectAsset(assets, "-win64.zip.sha256");

        Assert.Equal("VibeOCR-v1.0.0-win64.zip", pkg!.Name);
        Assert.Equal("VibeOCR-v1.0.0-win64.zip.sha256", sha!.Name);
    }

    [Fact]
    public void SelectAssetRejectsLegacyFrontendNames()
    {
        // 双产物 release（build_variants=all）：Classic + Next 同时发布。
        // 旧 SingleOrDefault 会抛 InvalidOperationException → 检查更新崩溃。
        // 必须选 Next（本进程 WinUI 运行态），否则下到 Classic 包前端错配。
        var assets = new[]
        {
            MakeAsset("VibeOCR-Classic-v1.0.0-win64.zip"),
            MakeAsset("VibeOCR-Next-v1.0.0-win64.zip"),
            MakeAsset("VibeOCR-Classic-v1.0.0-win64.zip.sha256"),
            MakeAsset("VibeOCR-Next-v1.0.0-win64.zip.sha256"),
        };

        var pkg = GitHubUpdateSource.SelectAsset(assets, "-win64.zip");
        var sha = GitHubUpdateSource.SelectAsset(assets, "-win64.zip.sha256");

        Assert.Null(pkg);
        Assert.Null(sha);
    }

    [Fact]
    public void SelectAssetAcceptsCanonicalNameWithoutLegacyFallback()
    {
        // 回退分支：历史 release 或单 Classic 产物（无 -Next- 命名）。
        // 取第一个匹配项，保证 asset 仍能被选中（而非返回 null 导致 available=false）。
        var assets = new[]
        {
            MakeAsset("VibeOCR-v0.4.28-win64.zip"),
            MakeAsset("VibeOCR-v0.4.28-win64.zip.sha256"),
        };

        var pkg = GitHubUpdateSource.SelectAsset(assets, "-win64.zip");
        var sha = GitHubUpdateSource.SelectAsset(assets, "-win64.zip.sha256");

        Assert.Equal("VibeOCR-v0.4.28-win64.zip", pkg!.Name);
        Assert.Equal("VibeOCR-v0.4.28-win64.zip.sha256", sha!.Name);
    }

    [Fact]
    public void SelectAssetReturnsNullWhenNoneMatch()
    {
        // 无任何匹配 asset（如 release 只有源码 tarball，无 win64.zip）。
        // 返回 null → FetchLatestAsync 据此判 available=false（「已是最新版本」或不可用），
        // 而非崩溃。
        var assets = new[]
        {
            MakeAsset("Source.zip"),
            MakeAsset("VibeOCR-v1.0.0-linux.tar.gz"),
        };

        var pkg = GitHubUpdateSource.SelectAsset(assets, "-win64.zip");
        var sha = GitHubUpdateSource.SelectAsset(assets, "-win64.zip.sha256");

        Assert.Null(pkg);
        Assert.Null(sha);
    }

    [Fact]
    public void UpdateEndpointUsesOnlyTheNextRepository()
    {
        Assert.Equal(
            "https://api.github.com/repos/FelixJI/vibeocr-next/releases?per_page=20",
            GitHubUpdateSource.ReleasesEndpoint);
    }

    private static GitHubUpdateSource.ReleaseAsset MakeAsset(string name) =>
        new(name, $"https://example.test/{name}");
}
