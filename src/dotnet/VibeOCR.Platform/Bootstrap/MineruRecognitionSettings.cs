using System.Text.Json;
using System.Text.RegularExpressions;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// 持久化的全局 MinerU 识别偏好（tier/ocr_mode/page_range/language），
/// 与 wire <see cref="MineruConfig"/> 字段一一对应。language 仅在本地
/// 模式生效（mineru-api 服务进程 --language 启动参数，变更触发服务级
/// 重启）；远程模式语言由自部署服务启动参数决定，不按请求发送——UI
/// 在远程模式下禁用语言选择，存储值原样保留。消费方经
/// RuntimeSelectionService 目录校验后映射为请求配置。
/// </summary>
public sealed record MineruRecognitionPreference(
    MineruTier Tier,
    MineruOcrMode OcrMode,
    string PageRange,
    string Language);

/// <summary>
/// /v2/settings <c>extra.mineru_recognition</c> 的用户可见投影。Stored 仅
/// 表示键存在；Invalid=true 表示持久值语法无法解析——不静默降级回目录
/// 默认，由提交路径 fail closed 并提示修复。
/// </summary>
public sealed record MineruRecognitionState(
    bool Supported,
    bool Stored,
    MineruTier? Tier,
    MineruOcrMode? OcrMode,
    string? PageRange,
    string? Language,
    bool Invalid = false,
    string? InvalidReason = null);

/// <summary>
/// /v2/settings <c>extra.mineru_recognition</c> 的读写适配：Backend 设置
/// 快照是唯一事实源，更新只替换该键并原样保留其他 extra、TTL 与下载源
/// 偏好。tier 的目录可用性由 RuntimeSelectionService 在消费时校验
/// （fail closed），本适配只做语法层校验。
/// </summary>
public static partial class MineruRecognitionSettings
{
    public const string ExtraKey = "mineru_recognition";
    public const int MaxPageRangeLength = 512;

    // 与内部 wire 契约的 MINERU_PAGE_RANGE_PATTERN 同一语法（all 或
    // 逗号分隔的 1/r1/1-3 页码与闭合区间）；语义校验留在 Backend。
    [GeneratedRegex(
        @"^(all|r?[1-9][0-9]*(-r?[1-9][0-9]*)?(,r?[1-9][0-9]*(-r?[1-9][0-9]*)?)*)\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex PageRangeSyntax();

    public static bool IsValidPageRange(string? pageRange) =>
        !string.IsNullOrEmpty(pageRange) &&
        pageRange.Length <= MaxPageRangeLength &&
        PageRangeSyntax().IsMatch(pageRange);

    public static bool TryParseTier(string? value, out MineruTier tier)
    {
        switch (value)
        {
            case "flash": tier = MineruTier.Flash; return true;
            case "basic": tier = MineruTier.Basic; return true;
            case "standard": tier = MineruTier.Standard; return true;
            case "advanced": tier = MineruTier.Advanced; return true;
            default: tier = default; return false;
        }
    }

    public static bool TryParseOcrMode(string? value, out MineruOcrMode mode)
    {
        switch (value)
        {
            case "auto": mode = MineruOcrMode.Auto; return true;
            case "txt": mode = MineruOcrMode.Txt; return true;
            case "ocr": mode = MineruOcrMode.Ocr; return true;
            default: mode = default; return false;
        }
    }

    /// <summary>
    /// 从 Backend 设置快照投影全局 MinerU 识别偏好。能力未声明时不解读
    /// 该键；键存在但字段缺失、类型错误、枚举未知或页码范围非法时
    /// Invalid=true（附原因），调用方不得按目录默认静默顶替。
    /// </summary>
    public static MineruRecognitionState Read(
        SettingsSnapshot snapshot,
        bool supported)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!supported)
        {
            return new MineruRecognitionState(false, false, null, null, null, null);
        }
        if (snapshot.Extra is not { } extra ||
            !extra.TryGetValue(ExtraKey, out JsonElement value))
        {
            return new MineruRecognitionState(true, false, null, null, null, null);
        }
        if (value.ValueKind != JsonValueKind.Object ||
            !StringProperty(value, "tier", out string? tierText) ||
            !StringProperty(value, "ocr_mode", out string? ocrModeText) ||
            !StringProperty(value, "page_range", out string? pageRange) ||
            !StringProperty(value, "language", out string? language))
        {
            return Invalid("mineru_recognition_shape");
        }
        if (!TryParseTier(tierText, out MineruTier tier))
        {
            return Invalid("mineru_recognition_tier");
        }
        if (!TryParseOcrMode(ocrModeText, out MineruOcrMode ocrMode))
        {
            return Invalid("mineru_recognition_ocr_mode");
        }
        if (!IsValidPageRange(pageRange))
        {
            return Invalid("mineru_recognition_page_range");
        }
        if (string.IsNullOrWhiteSpace(language))
        {
            return Invalid("mineru_recognition_language");
        }
        return new MineruRecognitionState(true, true, tier, ocrMode, pageRange, language);

        MineruRecognitionState Invalid(string reason) => new(
            true, true, null, null, null, null, Invalid: true, InvalidReason: reason);
    }

    /// <summary>
    /// 校验并持久化全局 MinerU 识别偏好（读-改-写，保留其他 extra 键）。
    /// 语法层校验后写回完整对象；返回 Backend 回读的更新后快照。
    /// </summary>
    public static async Task<SettingsSnapshot> ApplyAsync(
        IInferenceClient inference,
        MineruRecognitionPreference preference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inference);
        ArgumentNullException.ThrowIfNull(preference);
        if (!IsValidPageRange(preference.PageRange))
        {
            throw new ArgumentException(
                "MinerU 页码范围必须是 all 或 1,3-5 形式的页码/区间（r 前缀表示倒序）。",
                nameof(preference));
        }
        if (string.IsNullOrWhiteSpace(preference.Language))
        {
            throw new ArgumentException("MinerU 语言不能为空。", nameof(preference));
        }
        SettingsSnapshot current = await inference
            .GetSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, JsonElement> extra = new(StringComparer.Ordinal);
        if (current.Extra is not null)
        {
            foreach (KeyValuePair<string, JsonElement> pair in current.Extra)
            {
                extra[pair.Key] = pair.Value;
            }
        }
        extra[ExtraKey] = JsonSerializer.SerializeToElement(new
        {
            tier = ToWireTier(preference.Tier),
            ocr_mode = ToWireOcrMode(preference.OcrMode),
            page_range = preference.PageRange,
            language = preference.Language,
        });
        return await inference
            .UpdateSettingsAsync(
                current with { Extra = extra },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ToWireTier(MineruTier tier) => tier switch
    {
        MineruTier.Flash => "flash",
        MineruTier.Basic => "basic",
        MineruTier.Standard => "standard",
        MineruTier.Advanced => "advanced",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown MinerU tier."),
    };

    private static string ToWireOcrMode(MineruOcrMode mode) => mode switch
    {
        MineruOcrMode.Auto => "auto",
        MineruOcrMode.Txt => "txt",
        MineruOcrMode.Ocr => "ocr",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown MinerU OCR mode."),
    };

    private static bool StringProperty(
        JsonElement value,
        string name,
        out string? text)
    {
        if (value.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            text = property.GetString();
            return true;
        }
        text = null;
        return false;
    }
}
