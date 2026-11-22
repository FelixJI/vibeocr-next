using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Configuration;

/// <summary>
/// 框选动作栏编辑工具显隐与顺序：值为 SelectionEditToolRow 词汇表
/// （11 个跨语言契约 tool id）的有序子集；空/损坏回退默认序列。
/// 与 image_editor 节点的 EditorPreferences（23 工具全排列）语义不同，
/// 独立节点 screenshot_toolbar，不复用其校验。
/// </summary>
public static class SelectionToolbarSettings
{
    public static readonly string[] DefaultTools =
    [
        "rectangle", "ellipse", "highlighter", "arrow", "text", "mosaic", "numbering",
    ];

    private static IReadOnlyList<string> Vocabulary =>
        VibeOCR.App.Features.Recognition.ScreenRegionPicker.SelectionEditToolRow
          .Select(row => row.Tool).ToList();

    public static IReadOnlyList<string> Load(PortableLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        try
        {
            if (!File.Exists(layout.ConfigFile)) return DefaultTools;
            JsonObject root = AppSettingsStore.ReadForUpdate(layout);
            if (root["screenshot_toolbar"] is not JsonObject node ||
                node["tools"] is not JsonArray tools)
                return DefaultTools;
            List<string> selected = [];
            foreach (JsonNode? entry in tools)
            {
                if (entry is JsonValue value && value.TryGetValue<string>(out string? tool) &&
                    Vocabulary.Contains(tool) && !selected.Contains(tool))
                    selected.Add(tool);
            }
            return selected.Count == 0 ? DefaultTools : selected;
        }
        catch (Exception error) when (
            error is JsonException or IOException or KeyNotFoundException or
              InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            return DefaultTools;
        }
    }

    public static void Save(PortableLayout layout, IReadOnlyList<string> tools)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(tools);
        // 写入前钳制到词汇表的无重有序子集；空集回默认。
        List<string> selected = [];
        foreach (string tool in tools)
            if (Vocabulary.Contains(tool) && !selected.Contains(tool))
                selected.Add(tool);
        if (selected.Count == 0) selected = [.. DefaultTools];
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        root["screenshot_toolbar"] = new JsonObject
        {
            ["tools"] = new JsonArray(
              selected.Select(tool => (JsonNode?)JsonSerializer.SerializeToNode(tool)).ToArray()),
        };
        AppSettingsStore.Write(layout, root);
    }
}
