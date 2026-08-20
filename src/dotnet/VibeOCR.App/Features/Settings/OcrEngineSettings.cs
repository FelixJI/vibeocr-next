using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Settings;

/// <summary>
/// The user's persisted global OCR engine choice. Only the stable wire engine
/// name is stored; availability and catalogs always come from the Backend.
/// </summary>
internal readonly record struct OcrEnginePreference(OcrEngine? Engine, bool RequiresChoice)
{
    /// <summary>The migrated default for configs that never chose an engine.</summary>
    public static OcrEnginePreference Default =>
        new(OcrEngineSettings.ToEngine(OcrEngineSettings.DefaultEngineWireName), false);
}

/// <summary>
/// app_settings.json owner for the <c>ocr.engine</c> key. A missing key (fresh
/// or pre-selection config) migrates to the RapidOCR default; an unknown value
/// is preserved and reported so the user must re-select instead of silently
/// falling back.
/// </summary>
internal static class OcrEngineSettings
{
    internal const string SectionName = "ocr";
    internal const string EngineKey = "engine";
    internal const string DefaultEngineWireName = "rapidocr";

    public static OcrEnginePreference Load(string configFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        if (!File.Exists(configFile))
        {
            return OcrEnginePreference.Default;
        }
        try
        {
            JsonNode? value = JsonNode.Parse(File.ReadAllText(configFile))?[SectionName]?[EngineKey];
            if (value is null)
            {
                return OcrEnginePreference.Default;
            }
            string? wireName = (value as JsonValue)?.GetValue<string>();
            OcrEngine? engine = ToEngine(wireName);
            return engine is null
                ? new OcrEnginePreference(null, true)
                : new OcrEnginePreference(engine, false);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            // 无法读取的配置不能推导出静默默认值;要求用户重新选择。
            return new OcrEnginePreference(null, true);
        }
    }

    public static void Save(PortableLayout layout, OcrEngine engine)
    {
        ArgumentNullException.ThrowIfNull(layout);
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        JsonObject section = root[SectionName] as JsonObject ?? [];
        section[EngineKey] = ToWireName(engine);
        root[SectionName] = section;
        AppSettingsStore.Write(layout, root);
    }

    /// <summary>
    /// The persisted global engine for plain-text OCR pipelines; a null config
    /// path (tests) resolves to the migrated default.
    /// </summary>
    public static OcrEngine? GlobalEngine(string? configFile) =>
        configFile is null
            ? ToEngine(DefaultEngineWireName)
            : Load(configFile).Engine;

    internal static OcrEngine? ToEngine(string? wireName) => wireName switch
    {
        "rapidocr" => OcrEngine.RapidOcr,
        "windows" => OcrEngine.Windows,
        "paddleocr" => OcrEngine.PaddleOcr,
        _ => null,
    };

    internal static string ToWireName(OcrEngine engine) => engine switch
    {
        OcrEngine.RapidOcr => "rapidocr",
        OcrEngine.Windows => "windows",
        OcrEngine.PaddleOcr => "paddleocr",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine."),
    };
}
