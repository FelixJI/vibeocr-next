using System.Text.Json;
using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class FloatingToolbarSettingsTests : IDisposable
{
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
        Assert.Equal(600, settings.LingerMs);
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
    public void SaveLoadRoundTripsAndPreservesSiblingNodes()
    {
        PortableLayout layout = CreateLayout();
        File.WriteAllText(
            layout.ConfigFile,
            "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");

        FloatingToolbarSettings.Save(
            layout,
            new FloatingToolbarSettings(true, ScreenEdge.Left, false, 900));

        FloatingToolbarSettings loaded = FloatingToolbarSettings.Load(layout);
        Assert.Equal(new FloatingToolbarSettings(true, ScreenEdge.Left, false, 900), loaded);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(layout.ConfigFile));
        Assert.Equal(
            "Ctrl+Alt+Q",
            document.RootElement.GetProperty("hotkeys")
                .GetProperty("global_screenshot")
                .GetString());
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
