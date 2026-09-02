using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.FloatingToolbar;

/// <summary>
/// app_settings.json 的 floating_toolbar 节点。默认关闭，不影响存量用户；
/// 节点缺失或损坏时回退默认值且不改写文件。
/// </summary>
internal sealed record FloatingToolbarSettings(
    bool Enabled,
    ScreenEdge Edge,
    bool AutoHide,
    int LingerMs)
{
    public const int DefaultLingerMs = 600;
    public const int MinimumLingerMs = 100;
    public const int MaximumLingerMs = 5000;

    public static FloatingToolbarSettings Default { get; } =
        new(false, ScreenEdge.Top, true, DefaultLingerMs);

    public static FloatingToolbarSettings Load(PortableLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        try
        {
            if (!File.Exists(layout.ConfigFile))
            {
                return Default;
            }

            JsonObject root = AppSettingsStore.ReadForUpdate(layout);
            if (root["floating_toolbar"] is not JsonObject node)
            {
                return Default;
            }

            // 字段级容错：单个字段缺失/非法回退默认，不拖垮整个节点。
            return new FloatingToolbarSettings(
                Enabled: ReadValue(node, "enabled", false),
                Edge: ReadEdge(node),
                AutoHide: ReadValue(node, "auto_hide", true),
                LingerMs: ClampLinger(ReadValue(node, "linger_ms", DefaultLingerMs)));
        }
        catch (Exception error) when (
            error is JsonException or KeyNotFoundException or FormatException
                or InvalidOperationException or InvalidCastException)
        {
            return Default;
        }
    }

    public static void Save(PortableLayout layout, FloatingToolbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(settings);
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        root["floating_toolbar"] = new JsonObject
        {
            ["enabled"] = settings.Enabled,
            ["edge"] = EdgeName(settings.Edge),
            ["auto_hide"] = settings.AutoHide,
            ["linger_ms"] = ClampLinger(settings.LingerMs),
        };
        AppSettingsStore.Write(layout, root);
    }

    public static string EdgeName(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Top => "top",
        ScreenEdge.Bottom => "bottom",
        ScreenEdge.Left => "left",
        ScreenEdge.Right => "right",
        _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, null),
    };

    public static ScreenEdge ParseEdge(string name) => name switch
    {
        "top" => ScreenEdge.Top,
        "bottom" => ScreenEdge.Bottom,
        "left" => ScreenEdge.Left,
        "right" => ScreenEdge.Right,
        _ => throw new FormatException($"Unknown floating toolbar edge: {name}"),
    };

    private static int ClampLinger(int lingerMs) =>
        Math.Clamp(lingerMs, MinimumLingerMs, MaximumLingerMs);

    private static T ReadValue<T>(JsonObject node, string name, T fallback)
        where T : struct
    {
        try
        {
            return node[name] is { } value ? value.GetValue<T>() : fallback;
        }
        catch (Exception error) when (
            error is InvalidOperationException or FormatException or InvalidCastException)
        {
            return fallback;
        }
    }

    private static ScreenEdge ReadEdge(JsonObject node)
    {
        try
        {
            return node["edge"] is { } value
                ? ParseEdge(value.GetValue<string>())
                : ScreenEdge.Top;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            return ScreenEdge.Top;
        }
    }
}
