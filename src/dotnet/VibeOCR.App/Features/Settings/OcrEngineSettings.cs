using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Settings;

/// <summary>
/// The user's persisted recognition-mode choice plus the legacy OCR engine
/// shape used before Protocol 2.8. Availability always comes from Backend.
/// </summary>
internal readonly record struct OcrEnginePreference(string? Mode, OcrEngine? Engine, bool RequiresChoice)
{
    /// <summary>A fresh config lets the runtime catalog choose Rapid text OCR.</summary>
    public static OcrEnginePreference Default =>
        new(null, null, false);
}

/// <summary>
/// app_settings.json owner for <c>ocr.recognition_mode</c> and the legacy
/// <c>ocr.engine</c> key. Unknown values are preserved and reported so the user
/// must re-select instead of silently falling back.
/// </summary>
internal static class OcrEngineSettings
{
    internal const string SectionName = "ocr";
    internal const string EngineKey = "engine";
    internal const string ModeKey = "recognition_mode";

    public static OcrEnginePreference Load(string configFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        if (!File.Exists(configFile))
        {
            return OcrEnginePreference.Default;
        }
        try
        {
            JsonNode? section = JsonNode.Parse(File.ReadAllText(configFile))?[SectionName];
            string? mode = (section?[ModeKey] as JsonValue)?.GetValue<string>();
            if (mode is not null)
                return ToPipeline(mode) is null
                    ? new OcrEnginePreference(null, null, true)
                    : new OcrEnginePreference(mode, null, false);
            JsonNode? value = section?[EngineKey];
            if (value is null)
            {
                return OcrEnginePreference.Default;
            }
            string? wireName = (value as JsonValue)?.GetValue<string>();
            OcrEngine? engine = ToEngine(wireName);
            return engine is null
                ? new OcrEnginePreference(null, null, true)
                : new OcrEnginePreference(null, engine, false);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            // 无法读取的配置不能推导出静默默认值;要求用户重新选择。
            return new OcrEnginePreference(null, null, true);
        }
    }

    public static void Save(PortableLayout layout, OcrEngine engine)
    {
        ArgumentNullException.ThrowIfNull(layout);
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        JsonObject section = root[SectionName] as JsonObject ?? [];
        section[EngineKey] = ToWireName(engine);
        section.Remove(ModeKey);
        root[SectionName] = section;
        AppSettingsStore.Write(layout, root);
    }

    public static void SaveMode(PortableLayout layout, string mode)
    {
        ArgumentNullException.ThrowIfNull(layout);
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        JsonObject section = root[SectionName] as JsonObject ?? [];
        section[ModeKey] = mode;
        section.Remove(EngineKey);
        root[SectionName] = section;
        AppSettingsStore.Write(layout, root);
    }

    /// <summary>
    /// The persisted global engine for plain-text OCR pipelines; a null config
    /// path (tests) resolves to the migrated default.
    /// </summary>
    public static OcrEngine? GlobalEngine(string? configFile)
    {
        if (configFile is null)
        {
            return null;
        }

        OcrEnginePreference preference = Load(configFile);
        if (preference.RequiresChoice)
        {
            throw new InvalidOperationException(
                "OCR 识别模式配置无效，请在设置中重新选择。");
        }
        if (preference.Engine is not null || preference.Mode is null)
        {
            return preference.Engine;
        }

        return ToLegacyEngine(preference.Mode) ?? throw new InvalidOperationException(
            $"识别模式 {preference.Mode} 需要支持 recognition mode catalog 的 Runtime，不能按文本 OCR 静默降级。");
    }

    public static string? GlobalMode(string? configFile) =>
        configFile is null ? null : Load(configFile).Mode;

    internal static string? ToPipeline(string? mode) => mode switch
    {
        "rapid_text" or "windows_text" or "paddle_text" => "OCR",
        "paddle_structure" => "PP-StructureV3",
        "paddle_document_vl" => "PaddleOCR-VL",
        "mineru_document" => "MinerU",
        "paddle_table" => "TABLE_RECOGNITION",
        "paddle_formula" => "FORMULA_RECOGNITION",
        _ => null,
    };

    internal static string? ToRecognitionMode(OcrEngine? engine) => engine switch
    {
        OcrEngine.RapidOcr => "rapid_text",
        OcrEngine.Windows => "windows_text",
        OcrEngine.PaddleOcr => "paddle_text",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine."),
    };

    internal static OcrEngine? ToLegacyEngine(string? mode) => mode switch
    {
        "rapid_text" => OcrEngine.RapidOcr,
        "windows_text" => OcrEngine.Windows,
        "paddle_text" => OcrEngine.PaddleOcr,
        _ => null,
    };

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
