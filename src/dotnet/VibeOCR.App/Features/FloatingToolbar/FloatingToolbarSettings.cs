using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.FloatingToolbar;

/// <summary>悬浮工具栏主题偏好：跟随系统或固定明暗。</summary>
internal enum FloatingToolbarTheme
{
    /// <summary>跟随系统 light/dark 与高对比设置（新配置默认）。</summary>
    System,

    /// <summary>固定浅色：系统 light/dark 切换不覆盖用户选择。</summary>
    Light,

    /// <summary>固定深色：系统 light/dark 切换不覆盖用户选择。</summary>
    Dark,
}

/// <summary>
/// app_settings.json 的 floating_toolbar 节点。默认关闭，不影响存量用户；
/// 节点缺失或损坏时回退默认值且不改写文件。hidden_by_user 记录用户主动
/// 隐藏偏好（区别于靠边自动收起），重启后保持主动隐藏、不被感应条恢复；
/// 旧配置无该字段时等价 false，保持兼容。linger_ms 新配置默认 300ms，
/// 已显式保存的 600 或其他有效值原样保留；theme 缺失等价 system。
/// </summary>
internal sealed record FloatingToolbarSettings(
    bool Enabled,
    ScreenEdge Edge,
    bool AutoHide,
    int LingerMs,
    bool HiddenByUser = false,
    FloatingToolbarTheme Theme = FloatingToolbarTheme.System)
{
    public const int DefaultLingerMs = 300;
    public const int MinimumLingerMs = 100;
    public const int MaximumLingerMs = 5000;

    public static FloatingToolbarSettings Default { get; } =
        new(false, ScreenEdge.Top, true, DefaultLingerMs, false, FloatingToolbarTheme.System);

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
                LingerMs: ClampLinger(ReadValue(node, "linger_ms", DefaultLingerMs)),
                HiddenByUser: ReadValue(node, "hidden_by_user", false),
                Theme: ReadTheme(node));
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
            ["hidden_by_user"] = settings.HiddenByUser,
            ["theme"] = ThemeName(settings.Theme),
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

    public static string ThemeName(FloatingToolbarTheme theme) => theme switch
    {
        FloatingToolbarTheme.System => "system",
        FloatingToolbarTheme.Light => "light",
        FloatingToolbarTheme.Dark => "dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null),
    };

    public static FloatingToolbarTheme ParseTheme(string name) => name switch
    {
        "system" => FloatingToolbarTheme.System,
        "light" => FloatingToolbarTheme.Light,
        "dark" => FloatingToolbarTheme.Dark,
        _ => throw new FormatException($"Unknown floating toolbar theme: {name}"),
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

    private static FloatingToolbarTheme ReadTheme(JsonObject node)
    {
        try
        {
            return node["theme"] is { } value
                ? ParseTheme(value.GetValue<string>())
                : FloatingToolbarTheme.System;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            return FloatingToolbarTheme.System;
        }
    }
}
