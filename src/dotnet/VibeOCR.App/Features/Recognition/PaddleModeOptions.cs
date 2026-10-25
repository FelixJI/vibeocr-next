using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Recognition;

/// <summary>Explicit per-mode overrides; omitted fields retain Runtime defaults.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaddleModeOptions
{
    public string? ModelPreset { get; init; }
    public string? Device { get; init; }
    public bool? UseDocOrientationClassify { get; init; }
    public bool? UseDocUnwarping { get; init; }
    public bool? UseTextlineOrientation { get; init; }
    public bool? UseTableRecognition { get; init; }
    public bool? UseTableOrientationClassify { get; init; }
    public bool? UseOcrResultsWithTableCells { get; init; }

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // #270：已退役的已知选项字段（VL 专用与独立公式识别专用）。旧记录中
    // 出现这些字段时，兼容副本仅剔除它们并保留同记录其余合法设置；
    // 其他未知/非法字段仍按严格解析失败处理（回退默认，不静默吞）。
    private static readonly string[] RetiredKnownFields =
    [
        "vl_use_layout_detection",
        "vl_use_chart_recognition",
        "vl_use_seal_recognition",
        "use_ocr_for_image_block",
        "formula_recognition_batch_size",
        "formula_recognition_model_name",
        "use_formula_recognition",
        "use_seal_recognition",
        "use_chart_recognition",
    ];

    internal static PaddleModeOptions Load(PortableLayout layout, string modeId)
    {
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        JsonNode? stored = root["recognition_options"]?[modeId];
        if (stored is null) return new();
        JsonNode compat = stored.DeepClone();
        if (compat is JsonObject options)
            foreach (string field in RetiredKnownFields)
                options.Remove(field);
        try
        {
            return compat.Deserialize<PaddleModeOptions>(JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            // 仅明确退役字段兼容；未知字段与非法类型仍按既有严格解析回退默认。
            return new();
        }
    }

    internal void Save(PortableLayout layout, string modeId)
    {
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        JsonObject modes = root["recognition_options"] as JsonObject ?? new();
        modes[modeId] = JsonSerializer.SerializeToNode(this, JsonOptions);
        root["recognition_options"] = modes;
        AppSettingsStore.Write(layout, root);
    }
    public IReadOnlyDictionary<string, JsonElement> ToWire(RecognitionModeOption mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (ModelPreset is not null && ModelPreset is not ("tiny" or "small" or "medium"))
            throw new ArgumentException("Unsupported Paddle model preset.");
        if (Device is not null && Device is not ("auto" or "cpu" or "cuda" or "directml"))
            throw new ArgumentException("Unsupported Paddle provider.");
        JsonElement serialized = JsonSerializer.SerializeToElement(this, JsonOptions);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in serialized.EnumerateObject())
        {
            if (!mode.SupportedOptions.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException($"Mode {mode.Id} does not support {property.Name}.");
            result.Add(property.Name, property.Value.Clone());
        }
        return result;
    }

    /// <summary>
    /// 状态展示投影专用（如工作台模式信息）：持久化的选项在环境切换后
    /// 可能不再被当前 catalog 支持（旧选项被移除、范围收紧）；不满足
    /// 当前模式合同的字段被过滤而不是抛异常——模式本身绝不因此静默
    /// 降级，仅展示回到 Runtime 默认。任务提交禁止使用本投影：必须以
    /// 冻结模式对原始 typed 选项调用 <see cref="ToWire"/>，不支持或
    /// 越界字段明确拒绝（#110 AC2）。
    /// </summary>
    internal IReadOnlyDictionary<string, JsonElement> ProjectWire(RecognitionModeOption mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        JsonElement serialized = JsonSerializer.SerializeToElement(this, JsonOptions);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in serialized.EnumerateObject())
        {
            if (!mode.SupportedOptions.Contains(property.Name, StringComparer.Ordinal)) continue;
            if (property.Name == "model_preset" && property.Value.GetString() is not ("tiny" or "small" or "medium")) continue;
            if (property.Name == "device" && property.Value.GetString() is not ("auto" or "cpu" or "cuda" or "directml")) continue;
            result.Add(property.Name, property.Value.Clone());
        }
        return result;
    }
}