using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// 默认识别模式的用户可见投影。ModeId 是 Runtime 目录的稳定模式
/// id；Stored=true 仅表示 Backend 回显了该键（GET 会注入解析后的兼容
/// 默认，不代表用户已显式保存）。目录校验由调用方按目录执行。
/// </summary>
public sealed record DefaultRecognitionModeState(
    bool Supported,
    string? ModeId,
    bool Stored);

/// <summary>
/// /v2/settings <c>extra.default_recognition_mode</c> 的读写适配
/// （ocr.default-recognition-mode.v1）。Backend 设置快照是唯一事实源：
/// 更新只替换 default_recognition_mode 一个键，原样保留其他 extra 键、
/// 驻留 TTL、下载源偏好与 MinerU 连接。写入门禁由调用方按能力执行——
/// 未声明能力的 Backend 不写入未知键；保存失败时原已提交默认不变。
/// </summary>
public static class DefaultRecognitionModeSettings
{
    public const string ExtraKey = "default_recognition_mode";

    /// <summary>
    /// 从 Backend 设置快照投影当前默认识别模式。能力未声明时不解读该键
    /// （ModeId=null），UI 按只读兼容说明呈现；键存在但值不是字符串时
    /// 原样返回 null 并保留 Stored 标记，由目录校验暴露无效值。
    /// </summary>
    public static DefaultRecognitionModeState Read(
        SettingsSnapshot snapshot,
        bool supported)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!supported)
        {
            return new DefaultRecognitionModeState(false, null, Stored: false);
        }
        string? modeId = null;
        bool stored = false;
        if (snapshot.Extra is { } extra &&
            extra.TryGetValue(ExtraKey, out JsonElement value))
        {
            stored = true;
            if (value.ValueKind == JsonValueKind.String)
            {
                modeId = value.GetString();
            }
        }
        return new DefaultRecognitionModeState(true, modeId, stored);
    }

    /// <summary>
    /// 校验并持久化默认识别模式偏好（读-改-写，保持其他 extra 键）。
    /// modeId 必须是当前 Runtime 目录中 ready 的模式 id——调用方在能力
    /// 门禁后自行执行目录校验，本方法只做非空与写回。返回 Backend 回读
    /// 的更新后快照。
    /// </summary>
    public static async Task<SettingsSnapshot> ApplyAsync(
        IInferenceClient inference,
        string modeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inference);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
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
        extra[ExtraKey] = JsonSerializer.SerializeToElement(modeId);
        return await inference
            .UpdateSettingsAsync(
                current with { Extra = extra },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
