using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Configuration;

/// <summary>Owns fail-closed object parsing and atomic writes for app_settings.json.</summary>
internal static class AppSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static JsonObject ReadForUpdate(PortableLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!File.Exists(layout.ConfigFile))
        {
            return [];
        }
        JsonNode? parsed = JsonNode.Parse(File.ReadAllText(layout.ConfigFile));
        return parsed as JsonObject ?? throw new JsonException(
            "app_settings.json root must be a JSON object.");
    }

    public static void Write(PortableLayout layout, JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(root);
        layout.WriteStateFileAtomically(layout.ConfigFile, root.ToJsonString(WriteOptions));
    }
}
