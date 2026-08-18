using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class PortableLayoutTests
{
    [Fact]
    public void ProductionStateRootIsPortableRelative()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vibe OCR With Spaces");
        PortableLayout layout = PortableLayout.Resolve(
            Path.Combine(root, "VibeOCR.Next.exe"),
            "production");

        Assert.Equal("production", layout.Profile);
        Assert.Equal(Path.GetFullPath(root), layout.InstallRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "state"), layout.DataRoot);
        Assert.Equal(layout.DataRoot, layout.StateRoot);
        Assert.Equal(Path.Combine(layout.StateRoot, "output"), layout.OutputRoot);
        Assert.Equal(
            Path.Combine(layout.StateRoot, "config", "app_settings.json"),
            layout.ConfigFile);
        Assert.Equal(Path.Combine(layout.StateRoot, "cache"), layout.CacheRoot);
        Assert.Equal(Path.Combine(layout.StateRoot, "logs"), layout.LogsRoot);
        Assert.Equal(Path.Combine(layout.StateRoot, "models"), layout.ModelsRoot);
        Assert.Equal(Path.Combine(layout.StateRoot, "webview2"), layout.WebView2Root);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(root), "portable-layout.json"),
            layout.PortableLayoutManifest);
        Assert.True(layout.UsesSharedRuntimeStore);
        Assert.DoesNotContain(
            layout.GetType().GetProperties(),
            property => property.Name.Contains("Python", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WinUiDevProductDataIsIsolated()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-layout-{Guid.NewGuid():N}");
        PortableLayout layout = PortableLayout.Resolve(root, "winui-dev");

        string profileRoot = Path.Combine(root, "data", "profiles", "winui-dev");
        Assert.Equal(profileRoot, layout.DataRoot);
        Assert.Equal(Path.Combine(profileRoot, "output"), layout.OutputRoot);
        Assert.Equal(
            Path.Combine(profileRoot, "config", "app_settings.json"),
            layout.ConfigFile);
        Assert.Null(layout.PortableLayoutManifest);
        Assert.False(layout.UsesSharedRuntimeStore);
        Assert.False(Directory.Exists(profileRoot));
    }

    [Fact]
    public void ExplicitPortableLayoutManifestWinsOverTheDefault()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-bundle-{Guid.NewGuid():N}");
        string manifest = Path.Combine(root, "explicit-layout.json");

        PortableLayout layout = PortableLayout.Resolve(
            Path.Combine(root, "next"),
            "production",
            manifest);

        Assert.Equal(Path.GetFullPath(manifest), layout.PortableLayoutManifest);
    }

    [Fact]
    public void ResolverNeverScansParentsForPortableLayout()
    {
        string bundle = Path.Combine(Path.GetTempPath(), $"vibeocr-bundle-{Guid.NewGuid():N}");
        string product = Path.Combine(bundle, "next");
        Directory.CreateDirectory(product);
        File.WriteAllText(Path.Combine(bundle, "portable-layout.json"), "{}");
        try
        {
            PortableLayout layout = PortableLayout.Resolve(product, "production");
            // 只绑定产品根内的默认清单,绝不向上扫描父目录。
            Assert.Equal(
                Path.Combine(Path.GetFullPath(product), "portable-layout.json"),
                layout.PortableLayoutManifest);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public void EnsurePortableStateProbesCreatesDirectoriesAndManifest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"VibeOCR 便携 测试-{Guid.NewGuid():N}");
        string exe = Path.Combine(root, "VibeOCR.Next.exe");
        Directory.CreateDirectory(root);
        PortableLayout layout = PortableLayout.Resolve(exe, "production");

        layout.EnsurePortableState();

        foreach (string directory in new[]
        {
            layout.StateRoot,
            layout.CacheRoot,
            Path.GetDirectoryName(layout.ConfigFile)!,
            layout.LogsRoot,
            layout.ModelsRoot,
            layout.OutputRoot,
            layout.UpdateRoot,
            layout.TempRoot,
            layout.LocksRoot,
            layout.WebView2Root,
        })
        {
            Assert.True(Directory.Exists(directory), directory);
        }
        string manifest = File.ReadAllText(layout.PortableLayoutManifest!);
        Assert.Contains("\"shared_root\":\"state\"", manifest);
        Assert.Contains("\"next\"", manifest);
        // 幂等:内容不变不重写。
        string firstWrite = File.GetLastWriteTimeUtc(layout.PortableLayoutManifest!)
            .ToString("O");
        layout.EnsurePortableState();
        Assert.Equal(
            firstWrite,
            File.GetLastWriteTimeUtc(layout.PortableLayoutManifest!).ToString("O"));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void UnwritableStateRootFailsClosedWithoutFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-readonly-{Guid.NewGuid():N}");
        string exe = Path.Combine(root, "VibeOCR.Next.exe");
        Directory.CreateDirectory(root);
        PortableLayout layout = PortableLayout.Resolve(exe, "production");
        PortableLayout redirected = layout with
        {
            DataRoot = @"Q:\Definitely Missing\vibeocr-state",
        };

        PortableLayoutException error = Assert.Throws<PortableLayoutException>(
            redirected.ProbeWritableStateRoot);

        Assert.Contains("移动", error.Message);
        Assert.Contains("不会请求管理员权限", error.Message);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void StateRootEscapingTheInstallRootFailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-escape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        Assert.Throws<PortableLayoutException>(() => PortableLayout.Resolve(
            Path.Combine(root, "VibeOCR.Next.exe"),
            "production",
            userDataRoot: Path.Combine(Path.GetTempPath(), "elsewhere")));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ExplicitInstallRootResolvesTheVelopackProductLayout()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-product-{Guid.NewGuid():N}");
        string metadata = Path.Combine(root, "app", "metadata");
        Directory.CreateDirectory(metadata);
        string[] required =
        [
            "VibeOCR.exe",
            "Velopack.dll",
            "LICENSE",
            "CHANGELOG.md",
            "app/VibeOCR.WinUI.exe",
            "app/VibeOCR.WinUI.dll",
            "app/VibeOCR.WinUI.pri",
            "app/App.xbf",
            "app/MainWindow.xbf",
            "app/WebAssets/index.html",
            "app/metadata/component-lock.json",
            "app/metadata/component-identities.json",
            "app/metadata/product-release-manifest.json",
            "runtime/backend/runtime-manifest.json",
            "runtime/installer/vibeocr-runtime-installer.exe",
        ];
        foreach (string relative in required)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
        File.WriteAllText(
            Path.Combine(metadata, "product-layout.json"),
            """
            {
              "schema_version": 1,
              "product_id": "vibeocr",
              "public_entry": "VibeOCR.exe",
              "roots": {"app":"app","runtime":"runtime","metadata":"app/metadata"},
              "app": {"entry":"app/VibeOCR.WinUI.exe","web_assets":"app/WebAssets"},
              "runtime": {"manifest":"runtime/backend/runtime-manifest.json","installer":"runtime/installer/vibeocr-runtime-installer.exe"},
              "metadata": {"component_lock":"app/metadata/component-lock.json","component_identities":"app/metadata/component-identities.json","release_manifest":"app/metadata/product-release-manifest.json"},
              "user_data": {"relative":"state"}
            }
            """);
        File.WriteAllText(Path.Combine(root, "sq.version"), "0.3.1");
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root);

            Assert.Equal(Path.GetFullPath(root), layout.InstallRoot);
            Assert.Equal(Path.Combine(root, "VibeOCR.exe"), layout.ProductEntry);
            Assert.Equal(
                Path.Combine(root, "app", "metadata", "component-lock.json"),
                layout.ComponentLock);
            Assert.Equal(
                Path.Combine(root, "runtime", "backend", "runtime-manifest.json"),
                layout.RuntimeManifest);
            Assert.Equal(Path.Combine(root, "state"), layout.DataRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProductRootClosureToleratesOnlyPortableStateEntries()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-closure-{Guid.NewGuid():N}");
        string metadata = Path.Combine(root, "app", "metadata");
        Directory.CreateDirectory(metadata);
        string[] required =
        [
            "VibeOCR.exe",
            "Velopack.dll",
            "LICENSE",
            "CHANGELOG.md",
            "app/VibeOCR.WinUI.exe",
            "app/VibeOCR.WinUI.dll",
            "app/VibeOCR.WinUI.pri",
            "app/App.xbf",
            "app/MainWindow.xbf",
            "app/WebAssets/index.html",
            "app/metadata/component-lock.json",
            "app/metadata/component-identities.json",
            "app/metadata/product-release-manifest.json",
            "runtime/backend/runtime-manifest.json",
            "runtime/installer/vibeocr-runtime-installer.exe",
        ];
        foreach (string relative in required)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
        File.WriteAllText(
            Path.Combine(metadata, "product-layout.json"),
            """
            {"schema_version":1,"product_id":"vibeocr","public_entry":"VibeOCR.exe",
             "roots":{"app":"app","runtime":"runtime","metadata":"app/metadata"},
             "app":{"entry":"app/VibeOCR.WinUI.exe","web_assets":"app/WebAssets"},
             "runtime":{"manifest":"runtime/backend/runtime-manifest.json","installer":"runtime/installer/vibeocr-runtime-installer.exe"},
             "metadata":{"component_lock":"app/metadata/component-lock.json","component_identities":"app/metadata/component-identities.json","release_manifest":"app/metadata/product-release-manifest.json"},
             "user_data":{"relative":"state"}}
            """);
        Directory.CreateDirectory(Path.Combine(root, "state", "logs"));
        File.WriteAllText(Path.Combine(root, "portable-layout.json"), "{}");
        try
        {
            // state/portable-layout.json 允许存在也允许缺失。
            PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root);

            File.Delete(Path.Combine(root, "portable-layout.json"));
            PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplicitInstallRootRejectsRootClutter()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-product-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "app", "metadata"));
        File.WriteAllText(Path.Combine(root, "app", "metadata", "product-layout.json"), "{}");
        File.WriteAllText(Path.Combine(root, "unexpected.exe"), "fixture");
        try
        {
            Assert.Throws<InvalidDataException>(() => PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("other")]
    public void UnknownProfilesAreRejected(string profile) =>
        Assert.Throws<ArgumentException>(() => PortableLayout.Resolve("C:\\VibeOCR", profile));
}
