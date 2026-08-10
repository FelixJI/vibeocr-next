using System.Net;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public async Task DownloadVerifyFallsBackAcrossBadSources()
    {
        byte[] packageBytes = Encoding.UTF8.GetBytes("verified next package");
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(packageBytes));
        var handler = new UpdateFallbackHandler(packageBytes, expectedHash);
        using var client = new HttpClient(handler);
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-next-update-{Guid.NewGuid():N}");

        try
        {
            var source = new GitHubUpdateSource("0.9.0", root, root, client);
            (string version, bool available) = await source.FetchLatestAsync(CancellationToken.None);

            Assert.Equal("1.0.0", version);
            Assert.True(available);
            Assert.True(await source.DownloadVerifyAsync(CancellationToken.None));
            Assert.Contains("https://github.test/package.zip.sha256", handler.Requests);
            Assert.Contains(
                "https://gh-proxy.com/https://github.test/package.zip.sha256",
                handler.Requests);
            Assert.DoesNotContain(
                "https://gh-proxy.com/https://github.test/package.zip",
                handler.Requests);
            Assert.Contains(
                "https://ghfast.top/https://github.test/package.zip.sha256",
                handler.Requests);
            Assert.Contains(
                "https://ghfast.top/https://github.test/package.zip",
                handler.Requests);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static GitHubUpdateSource.ReleaseAsset MakeAsset(string name) =>
        new(name, $"https://example.test/{name}");

    private sealed class UpdateFallbackHandler(byte[] packageBytes, string expectedHash)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);

            if (url == GitHubUpdateSource.ReleasesEndpoint)
            {
                const string releaseJson = """
                    [
                      {
                        "tag_name": "v1.0.0",
                        "draft": false,
                        "assets": [
                          {
                            "name": "VibeOCR-Next-v1.0.0-win64.zip",
                            "browser_download_url": "https://github.test/package.zip"
                          },
                          {
                            "name": "VibeOCR-Next-v1.0.0-win64.zip.sha256",
                            "browser_download_url": "https://github.test/package.zip.sha256"
                          }
                        ]
                      }
                    ]
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json"),
                });
            }

            if (url.StartsWith("https://github.test/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));

            if (url == "https://gh-proxy.com/https://github.test/package.zip.sha256")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>proxy error</html>", Encoding.UTF8, "text/html"),
                });
            }

            if (url == "https://ghfast.top/https://github.test/package.zip.sha256")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{expectedHash}  VibeOCR-Next-v1.0.0-win64.zip\n",
                        Encoding.UTF8,
                        "application/octet-stream"),
                });
            }

            if (url == "https://ghfast.top/https://github.test/package.zip")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
