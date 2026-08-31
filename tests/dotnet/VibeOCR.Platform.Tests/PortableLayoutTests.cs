using System.Diagnostics;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.ProductLayout;
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
            TestDirectory.Delete(bundle, recursive: true);
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

        TestDirectory.Delete(root, recursive: true);
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

        TestDirectory.Delete(root, recursive: true);
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

        TestDirectory.Delete(root, recursive: true);
    }

    [Fact]
    public void StateRootInAnAdjacentPrefixFailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-app-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<PortableLayoutException>(() => PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production",
                userDataRoot: root + "-evil\\state"));
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StateFileWriteReplacesOnlyContainedTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-state-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production");
            layout.EnsurePortableState();
            string target = Path.Combine(layout.StateRoot, "config", "app_settings.json");
            File.WriteAllText(target, "before");

            layout.WriteStateFileAtomically(target, "after");

            Assert.Equal("after", File.ReadAllText(target));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(target)!,
                ".app_settings.json.*.tmp"));
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StateFileWriteRejectsAdjacentPrefixAndLeavesItUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-state-write-{Guid.NewGuid():N}");
        string adjacent = root + "-evil";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(adjacent);
        string outside = Path.Combine(adjacent, "app_settings.json");
        File.WriteAllText(outside, "before");
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production");

            Assert.Throws<PortableLayoutException>(
                () => layout.WriteStateFileAtomically(outside, "after"));

            Assert.Equal("before", File.ReadAllText(outside));
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
            TestDirectory.Delete(adjacent, recursive: true);
        }
    }

    [Fact]
    public void StateFileWriteRejectsReparseParentAndLeavesOutsideFileUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-state-write-{Guid.NewGuid():N}");
        string outsideRoot = root + "-outside";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideRoot);
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production");
            layout.EnsurePortableState();
            string configDirectory = Path.GetDirectoryName(layout.ConfigFile)!;
            TestDirectory.Delete(configDirectory);
            CreateJunction(configDirectory, outsideRoot);
            string outside = Path.Combine(outsideRoot, "app_settings.json");
            File.WriteAllText(outside, "before");

            Assert.Throws<PortableLayoutException>(
                () => layout.WriteStateFileAtomically(layout.ConfigFile, "after"));

            Assert.Equal("before", File.ReadAllText(outside));
        }
        finally
        {
            string configDirectory = Path.Combine(root, "state", "config");
            if (Directory.Exists(configDirectory))
            {
                TestDirectory.Delete(configDirectory);
            }
            TestDirectory.Delete(root, recursive: true);
            TestDirectory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void StateFileWriteWrapsReplacementRejectionAndPreservesTheOriginalFile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-state-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production");
            layout.EnsurePortableState();
            File.WriteAllText(layout.ConfigFile, "before");

            PortableLayoutException error = Assert.Throws<PortableLayoutException>(() =>
                layout.WriteStateFileAtomically(
                    layout.ConfigFile,
                    "after",
                    () => throw new IOException("replacement rejected")));

            Assert.Contains("replacement rejected", error.Message);
            Assert.Equal("before", File.ReadAllText(layout.ConfigFile));
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StateFileWriteHoldsParentAgainstReplacementUntilPromotionCompletes()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-state-write-{Guid.NewGuid():N}");
        string outsideRoot = root + "-outside";
        string configDirectory = Path.Combine(root, "state", "config");
        string displacedDirectory = configDirectory + "-displaced";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideRoot);
        string sentinel = Path.Combine(outsideRoot, "app_settings.json");
        File.WriteAllText(sentinel, "outside");
        bool replacementRejected = false;
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production");
            layout.EnsurePortableState();
            File.WriteAllText(layout.ConfigFile, "before");

            Assert.Throws<PortableLayoutException>(() =>
                layout.WriteStateFileAtomically(layout.ConfigFile, "after", () =>
                {
                    try
                    {
                        Directory.Move(configDirectory, displacedDirectory);
                        CreateJunction(configDirectory, outsideRoot);
                    }
                    catch (Exception error) when (
                        error is IOException or UnauthorizedAccessException)
                    {
                        replacementRejected = true;
                        throw;
                    }
                }));

            Assert.Equal("outside", File.ReadAllText(sentinel));
            string securedDirectory = Directory.Exists(displacedDirectory)
                ? displacedDirectory
                : configDirectory;
            Assert.Equal(
                replacementRejected ? "before" : "after",
                File.ReadAllText(Path.Combine(securedDirectory, "app_settings.json")));
            Assert.Empty(Directory.EnumerateFiles(securedDirectory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(configDirectory)
                && File.GetAttributes(configDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                TestDirectory.Delete(configDirectory);
            }
            if (Directory.Exists(root))
            {
                TestDirectory.Delete(root, recursive: true);
            }
            if (Directory.Exists(outsideRoot))
            {
                TestDirectory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DirectoryCreationDoesNotEscapeAReplacedParent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-directory-create-{Guid.NewGuid():N}");
        string outsideRoot = root + "-outside";
        string stateDirectory = Path.Combine(root, "state");
        string displacedDirectory = stateDirectory + "-displaced";
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(outsideRoot);
        string sentinel = Path.Combine(outsideRoot, "sentinel.txt");
        File.WriteAllText(sentinel, "outside");
        bool replacementRejected = false;
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production");

            layout.EnsureContainedDirectory(layout.CacheRoot, () =>
            {
                try
                {
                    Directory.Move(stateDirectory, displacedDirectory);
                    CreateJunction(stateDirectory, outsideRoot);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    replacementRejected = true;
                }
            });

            Assert.True(replacementRejected);
            Assert.Equal("outside", File.ReadAllText(sentinel));
            Assert.False(Directory.Exists(Path.Combine(outsideRoot, "cache")));
            Assert.True(Directory.Exists(layout.CacheRoot));
        }
        finally
        {
            if (Directory.Exists(stateDirectory)
                && File.GetAttributes(stateDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                TestDirectory.Delete(stateDirectory);
            }
            if (Directory.Exists(root))
            {
                TestDirectory.Delete(root, recursive: true);
            }
            if (Directory.Exists(outsideRoot))
            {
                TestDirectory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void WritableProbeDoesNotTouchAReplacedLexicalStateDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-probe-write-{Guid.NewGuid():N}");
        string outsideRoot = root + "-outside";
        string stateDirectory = Path.Combine(root, "state");
        string displacedDirectory = stateDirectory + "-displaced";
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(outsideRoot);
        const string probeName = ".probe-controlled";
        string sentinel = Path.Combine(outsideRoot, probeName);
        File.WriteAllText(sentinel, "outside");
        try
        {
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(root, "VibeOCR.Next.exe"),
                "production");

            Assert.Throws<PortableLayoutException>(() =>
                layout.ProbeWritableStateRoot(probeName, () =>
                {
                    Directory.Move(stateDirectory, displacedDirectory);
                    CreateJunction(stateDirectory, outsideRoot);
                }));

            Assert.Equal("outside", File.ReadAllText(sentinel));
            if (Directory.Exists(displacedDirectory))
            {
                Assert.Empty(Directory.EnumerateFiles(displacedDirectory, ".probe-controlled*"));
            }
            else
            {
                Assert.True(Directory.Exists(stateDirectory));
                Assert.False(
                    File.GetAttributes(stateDirectory).HasFlag(FileAttributes.ReparsePoint));
            }
        }
        finally
        {
            if (Directory.Exists(stateDirectory)
                && File.GetAttributes(stateDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                TestDirectory.Delete(stateDirectory);
            }
            if (Directory.Exists(root))
            {
                TestDirectory.Delete(root, recursive: true);
            }
            if (Directory.Exists(outsideRoot))
            {
                TestDirectory.Delete(outsideRoot, recursive: true);
            }
        }
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
            "Microsoft.Web.WebView2.Core.dll",
            "WebView2Loader.dll",
            "Newtonsoft.Json.dll",
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
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VelopackPortableRootKeepsStateOutsideTheVersionedCurrentPayload()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-velopack-{Guid.NewGuid():N}");
        string current = Path.Combine(root, "current");
        CreateProductLayoutFixture(current);
        File.WriteAllText(Path.Combine(root, ".portable"), "");
        File.WriteAllText(Path.Combine(root, "Update.exe"), "fixture");
        File.WriteAllText(Path.Combine(root, "VibeOCR.exe"), "fixture");
        try
        {
            PortableProductRoots roots = PortableProductRoots.Resolve(current);
            PortableLayout layout = PortableLayout.Resolve(
                Path.Combine(current, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: roots.InstallRoot,
                productRootOverride: roots.ProductRoot);

            Assert.Equal(Path.GetFullPath(root), layout.InstallRoot);
            Assert.Equal(Path.GetFullPath(current), layout.ProductRoot);
            Assert.Equal(Path.Combine(root, "state"), layout.DataRoot);
            Assert.Equal(Path.Combine(root, "VibeOCR.exe"), layout.ProductEntry);
            Assert.Equal(
                Path.Combine(current, "app", "VibeOCR.WinUI.exe"),
                layout.AppEntry);

            layout.EnsurePortableState();
            string manifest = File.ReadAllText(Path.Combine(root, "portable-layout.json"));
            Assert.Contains("\"root\":\"current\"", manifest);
            Assert.False(Directory.Exists(Path.Combine(current, "state", "config")));
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableProductRootsRejectsAFalseCurrentDirectoryWithoutVelopackMarkers()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-velopack-{Guid.NewGuid():N}");
        string current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        try
        {
            PortableProductRoots roots = PortableProductRoots.Resolve(current);

            Assert.Equal(Path.GetFullPath(current), roots.InstallRoot);
            Assert.Equal(Path.GetFullPath(current), roots.ProductRoot);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProductRootClosureToleratesOnlyKnownPortableRuntimeEntries()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-closure-{Guid.NewGuid():N}");
        string metadata = Path.Combine(root, "app", "metadata");
        Directory.CreateDirectory(metadata);
        string[] required =
        [
            "VibeOCR.exe",
            "Velopack.dll",
            "Microsoft.Web.WebView2.Core.dll",
            "WebView2Loader.dll",
            "Newtonsoft.Json.dll",
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
        Directory.CreateDirectory(Path.Combine(root, "velopack"));
        File.WriteAllText(Path.Combine(root, "velopack", "velopack_VibeOCRNext.log"), "log");
        try
        {
            // state、portable-layout.json 与 Velopack 自有日志允许存在。
            PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root);

            File.Delete(Path.Combine(root, "portable-layout.json"));
            PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root);

            File.WriteAllText(Path.Combine(root, "velopack", "unexpected.bin"), "fixture");
            Assert.Throws<InvalidDataException>(() => PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root));
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
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
            TestDirectory.Delete(root, recursive: true);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Unable to start mklink.");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to create junction: {error}");
        }
    }

    [Fact]
    public void ProductRootClosureRejectsVelopackRuntimeJunction()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-closure-{Guid.NewGuid():N}");
        string outside = Path.Combine(Path.GetTempPath(), $"vibeocr-closure-outside-{Guid.NewGuid():N}");
        CreateProductLayoutFixture(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "velopack_VibeOCRNext.log"), "log");
        string link = Path.Combine(root, "velopack");
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidDataException>(() => PortableLayout.Resolve(
                Path.Combine(root, "app", "VibeOCR.WinUI.exe"),
                "production",
                installRootOverride: root));
        }
        finally
        {
            if (Directory.Exists(link) &&
                File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
            {
                TestDirectory.Delete(link);
            }
            TestDirectory.Delete(root, recursive: true);
            TestDirectory.Delete(outside, recursive: true);
        }
    }

    private static void CreateProductLayoutFixture(string root)
    {
        string metadata = Path.Combine(root, "app", "metadata");
        Directory.CreateDirectory(metadata);
        string[] required =
        [
            "VibeOCR.exe",
            "Velopack.dll",
            "Microsoft.Web.WebView2.Core.dll",
            "WebView2Loader.dll",
            "Newtonsoft.Json.dll",
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
        File.WriteAllText(Path.Combine(root, "sq.version"), "0.4.2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("other")]
    public void UnknownProfilesAreRejected(string profile) =>
        Assert.Throws<ArgumentException>(() => PortableLayout.Resolve("C:\\VibeOCR", profile));
}
