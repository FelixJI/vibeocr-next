using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class PortableLayoutTests
{
    [Fact]
    public void ProductionLayoutContainsOnlyProductOwnedPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vibe OCR With Spaces");
        string userDataRoot = Path.Combine(Path.GetTempPath(), "VibeOCR User Data");
        PortableLayout layout = PortableLayout.Resolve(
            Path.Combine(root, "VibeOCR.Next.exe"),
            "production",
            userDataRoot: userDataRoot);

        Assert.Equal("production", layout.Profile);
        Assert.Equal(Path.GetFullPath(root), layout.InstallRoot);
        Assert.Equal(Path.GetFullPath(userDataRoot), layout.DataRoot);
        Assert.Equal(Path.Combine(userDataRoot, "output"), layout.OutputRoot);
        Assert.Equal(Path.Combine(userDataRoot, "config", "app_settings.json"), layout.ConfigFile);
        Assert.Null(layout.PortableLayoutManifest);
        Assert.DoesNotContain(
            layout.GetType().GetProperties(),
            property => property.Name.Contains("Model", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Python", StringComparison.OrdinalIgnoreCase));
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
        Assert.False(Directory.Exists(profileRoot));
    }

    [Fact]
    public void SharedLayoutIsBoundOnlyWhenExplicitlySupplied()
    {
        string bundle = Path.Combine(Path.GetTempPath(), $"vibeocr-bundle-{Guid.NewGuid():N}");
        string product = Path.Combine(bundle, "next");
        string manifest = Path.Combine(bundle, "portable-layout.json");

        PortableLayout standalone = PortableLayout.Resolve(product, "production");
        PortableLayout shared = PortableLayout.Resolve(product, "production", manifest);

        Assert.Null(standalone.PortableLayoutManifest);
        Assert.Equal(Path.GetFullPath(manifest), shared.PortableLayoutManifest);
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
            Assert.Null(layout.PortableLayoutManifest);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public void ExplicitInstallRootResolvesTheVersionedProductLayout()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-product-{Guid.NewGuid():N}");
        string metadata = Path.Combine(root, "app", "metadata");
        Directory.CreateDirectory(metadata);
        string[] required =
        [
            "VibeOCR.exe",
            "LICENSE",
            "CHANGELOG.md",
            "app/VibeOCR.WinUI.exe",
            "app/VibeOCR.WinUI.dll",
            "app/VibeOCR.WinUI.pri",
            "app/App.xbf",
            "app/MainWindow.xbf",
            "app/WebAssets/index.html",
            "app/tools/updater.exe",
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
              "app": {"entry":"app/VibeOCR.WinUI.exe","web_assets":"app/WebAssets","updater":"app/tools/updater.exe"},
              "runtime": {"manifest":"runtime/backend/runtime-manifest.json","installer":"runtime/installer/vibeocr-runtime-installer.exe"},
              "metadata": {"component_lock":"app/metadata/component-lock.json","component_identities":"app/metadata/component-identities.json","release_manifest":"app/metadata/product-release-manifest.json"},
              "user_data": {"known_folder":"LocalApplicationData","relative":"VibeOCR"}
            }
            """);
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root,
                userDataRoot: Path.Combine(root, "external-data"));

            Assert.Equal(Path.GetFullPath(root), layout.InstallRoot);
            Assert.Equal(Path.Combine(root, "VibeOCR.exe"), layout.ProductEntry);
            Assert.Equal(
                Path.Combine(root, "app", "metadata", "component-lock.json"),
                layout.ComponentLock);
            Assert.Equal(
                Path.Combine(root, "runtime", "backend", "runtime-manifest.json"),
                layout.RuntimeManifest);
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
