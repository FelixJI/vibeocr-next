using System.Text.Json;
using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class FloatingToolbarSettingsTests : IDisposable
{
    [Fact]
    public void PeekPixelsRoundTripPreservesUserChoice()
    {
        PortableLayout layout = CreateLayout();
        FloatingToolbarSettings.Save(layout, FloatingToolbarSettings.Default with { PeekPixels = 8 });
        Assert.Equal(8, FloatingToolbarSettings.Load(layout).PeekPixels);
    }
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-floating-toolbar-{Guid.NewGuid():N}");

    private PortableLayout CreateLayout()
    {
        Directory.CreateDirectory(_root);
        PortableLayout layout = PortableLayout.Resolve(
            Path.Combine(_root, "VibeOCR.Next.exe"),
            "production");
        layout.EnsurePortableState();
        return layout;
    }

    [Fact]
    public void LoadReturnsDefaultsWhenConfigMissing()
    {
        PortableLayout layout = CreateLayout();

        FloatingToolbarSettings settings = FloatingToolbarSettings.Load(layout);

        Assert.Equal(FloatingToolbarSettings.Default, settings);
        Assert.False(settings.Enabled);
        Assert.Equal(ScreenEdge.Top, settings.Edge);
        Assert.True(settings.AutoHide);
        // 新无配置默认 300ms；已显式保存值不迁改。
        Assert.Equal(FloatingToolbarSettings.DefaultLingerMs, settings.LingerMs);
        Assert.Equal(300, settings.LingerMs);
        Assert.Equal(FloatingToolbarTheme.System, settings.Theme);
    }

    [Fact]
    public void LoadFallsBackToDefaultsOnCorruptOrMissingNode()
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(layout.ConfigFile, "{\"hotkeys\": not-json");

        Assert.Equal(FloatingToolbarSettings.Default, FloatingToolbarSettings.Load(layout));

        File.WriteAllText(layout.ConfigFile, "{\"hotkeys\": {}}");
        Assert.Equal(FloatingToolbarSettings.Default, FloatingToolbarSettings.Load(layout));
    }

    [Fact]
    public void LoadRejectsUnknownEdgeAndClampsLinger()
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(
            layout.ConfigFile,
            """
            {
              "floating_toolbar": {
                "enabled": true,
                "edge": "diagonal",
                "auto_hide": false,
                "linger_ms": 99999
              }
            }
            """);

        FloatingToolbarSettings settings = FloatingToolbarSettings.Load(layout);

        Assert.True(settings.Enabled);
        Assert.Equal(ScreenEdge.Top, settings.Edge);
        Assert.False(settings.AutoHide);
        Assert.Equal(FloatingToolbarSettings.MaximumLingerMs, settings.LingerMs);
    }

    [Fact]
    public void LoadKeepsExplicitlySavedLingerIncludingLegacySixHundred()
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(
            layout.ConfigFile,
            """
            {
              "floating_toolbar": {
                "enabled": true,
                "edge": "top",
                "auto_hide": true,
                "linger_ms": 600
              }
            }
            """);

        FloatingToolbarSettings settings = FloatingToolbarSettings.Load(layout);

        // 旧默认 600 已被显式保存：不因新默认 300 被静默改写。
        Assert.Equal(600, settings.LingerMs);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(5000, 5000)]
    [InlineData(50, 100)]
    public void LoadAcceptsLingerBoundsAndClampsBelowMinimum(int saved, int expected)
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(
            layout.ConfigFile,
            $$"""
            {
              "floating_toolbar": {
                "linger_ms": {{saved}}
              }
            }
            """);

        FloatingToolbarSettings settings = FloatingToolbarSettings.Load(layout);

        Assert.Equal(expected, settings.LingerMs);
    }

    [Fact]
    public void SaveLoadRoundTripsAndPreservesSiblingNodes()
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(
            layout.ConfigFile,
            "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");

        FloatingToolbarSettings.Save(
            layout,
            new FloatingToolbarSettings(true, ScreenEdge.Left, false, 900, true));

        FloatingToolbarSettings loaded = FloatingToolbarSettings.Load(layout);
        Assert.Equal(
            new FloatingToolbarSettings(true, ScreenEdge.Left, false, 900, true),
            loaded);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(layout.ConfigFile));
        Assert.Equal(
            "Ctrl+Alt+Q",
            document.RootElement.GetProperty("hotkeys")
                .GetProperty("global_screenshot")
                .GetString());
    }

    [Fact]
    public void HiddenByUserDefaultsFalseAndRoundTrips()
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(
            layout.ConfigFile,
            """
            {
              "floating_toolbar": {
                "enabled": true,
                "edge": "left",
                "auto_hide": false,
                "linger_ms": 900
              }
            }
            """);

        // 旧配置无 hidden_by_user 字段：等价 false，兼容不迁改。
        FloatingToolbarSettings legacy = FloatingToolbarSettings.Load(layout);
        Assert.False(legacy.HiddenByUser);
        Assert.Equal(
            new FloatingToolbarSettings(true, ScreenEdge.Left, false, 900),
            legacy);

        FloatingToolbarSettings.Save(layout, legacy with { HiddenByUser = true });

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(layout.ConfigFile));
        Assert.True(
            document.RootElement.GetProperty("floating_toolbar")
                .GetProperty("hidden_by_user")
                .GetBoolean());
        Assert.True(FloatingToolbarSettings.Load(layout).HiddenByUser);
        Assert.False(FloatingToolbarSettings.Default.HiddenByUser);
    }

    [Fact]
    public void ThemeDefaultsToSystemWhenFieldMissingAndIsNotMigrated()
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(
            layout.ConfigFile,
            """
            {
              "floating_toolbar": {
                "enabled": true,
                "edge": "left",
                "auto_hide": false,
                "linger_ms": 900,
                "hidden_by_user": true
              }
            }
            """);

        FloatingToolbarSettings legacy = FloatingToolbarSettings.Load(layout);

        // 旧配置无 theme 字段：等价 system，兼容不迁改文件。
        Assert.Equal(FloatingToolbarTheme.System, legacy.Theme);
        Assert.Equal(
            new FloatingToolbarSettings(true, ScreenEdge.Left, false, 900, true),
            legacy);
        Assert.DoesNotContain("theme", File.ReadAllText(layout.ConfigFile));
    }

    [Fact]
    public void ThemeRoundTripsManualLightAndDark()
    {
        PortableLayout layout = CreateLayout();

        FloatingToolbarSettings.Save(
            layout,
            new FloatingToolbarSettings(true, ScreenEdge.Top, true, 300, false, FloatingToolbarTheme.Light));
        Assert.Equal(FloatingToolbarTheme.Light, FloatingToolbarSettings.Load(layout).Theme);

        FloatingToolbarSettings.Save(
            layout,
            new FloatingToolbarSettings(true, ScreenEdge.Top, true, 300, false, FloatingToolbarTheme.Dark));
        Assert.Equal(FloatingToolbarTheme.Dark, FloatingToolbarSettings.Load(layout).Theme);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(layout.ConfigFile));
        Assert.Equal(
            "dark",
            document.RootElement.GetProperty("floating_toolbar")
                .GetProperty("theme")
                .GetString());
    }

    [Fact]
    public void LoadFallsBackToSystemForUnknownThemeAndKeepsOtherFields()
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(
            layout.ConfigFile,
            """
            {
              "floating_toolbar": {
                "enabled": true,
                "edge": "bottom",
                "auto_hide": false,
                "linger_ms": 900,
                "theme": "purple"
              }
            }
            """);

        FloatingToolbarSettings settings = FloatingToolbarSettings.Load(layout);

        // 非法 theme 值字段级回退 system，不拖垮其他已保存字段。
        Assert.Equal(FloatingToolbarTheme.System, settings.Theme);
        Assert.True(settings.Enabled);
        Assert.Equal(ScreenEdge.Bottom, settings.Edge);
        Assert.Equal(900, settings.LingerMs);
    }

    [Fact]
    public void EdgeNameRoundTripsAllEdges()
    {
        foreach (ScreenEdge edge in Enum.GetValues<ScreenEdge>())
        {
            Assert.Equal(edge, FloatingToolbarSettings.ParseEdge(
                FloatingToolbarSettings.EdgeName(edge)));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结果。
        }
    }
}
