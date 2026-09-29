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
    public bool? UseDocOrientationClassify { get; init; }
    public bool? UseDocUnwarping { get; init; }
    public bool? UseTextlineOrientation { get; init; }
    public bool? UseTableRecognition { get; init; }
    public bool? UseFormulaRecognition { get; init; }
    public bool? UseSealRecognition { get; init; }
    public bool? UseChartRecognition { get; init; }
    public bool? VlUseLayoutDetection { get; init; }
    public bool? VlUseChartRecognition { get; init; }
    public bool? VlUseSealRecognition { get; init; }
    public bool? UseOcrForImageBlock { get; init; }
    public bool? UseTableOrientationClassify { get; init; }
    public bool? UseOcrResultsWithTableCells { get; init; }
    public int? FormulaRecognitionBatchSize { get; init; }
    public string? FormulaRecognitionModelName { get; init; }

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // PaddleX 3.7.2 发行包内真实存在的公式识别模型名（与 Runtime catalog 一致）。
    private static readonly HashSet<string> WireFormulaModelNames =
    [
        "LaTeX_OCR_rec",
        "PP-FormulaNet-L",
        "PP-FormulaNet-S",
        "PP-FormulaNet_plus-L",
        "PP-FormulaNet_plus-M",
        "PP-FormulaNet_plus-S",
        "UniMERNet",
    ];

    internal static PaddleModeOptions Load(PortableLayout layout, string modeId)
    {
        JsonObject root = AppSettingsStore.ReadForUpdate(layout);
        JsonNode? stored = root["recognition_options"]?[modeId];
        if (stored is null) return new();
        try
        {
            return stored.Deserialize<PaddleModeOptions>(JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            // 新环境 catalog 不再支持旧选项/旧版本字段时不能让整页崩溃：
            // 回退默认值，用户重新保存后覆盖；本次提交仅携带当前环境支持项。
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
        if (FormulaRecognitionBatchSize is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(FormulaRecognitionBatchSize));
        if (FormulaRecognitionModelName is not null &&
            !WireFormulaModelNames.Contains(FormulaRecognitionModelName))
            throw new ArgumentException("Unsupported formula recognition model.");
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
            if (property.Name == "formula_recognition_batch_size" &&
                (property.Value.GetInt32() is < 1 or > 64)) continue;
            if (property.Name == "formula_recognition_model_name" &&
                property.Value.GetString() is { } model &&
                !WireFormulaModelNames.Contains(model)) continue;
            result.Add(property.Name, property.Value.Clone());
        }
        return result;
    }
}