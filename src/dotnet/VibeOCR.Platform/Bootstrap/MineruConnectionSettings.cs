using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// MinerU 连接的用户可见投影。API Key 只以是否已配置呈现，明文不进入
/// 工作台状态、日志或诊断。
/// </summary>
public sealed record MineruConnectionState(
    bool Supported,
    string Mode,
    string ApiUrl,
    bool HasApiKey)
{
    public bool IsRemote => string.Equals(Mode, "remote", StringComparison.Ordinal);
}

/// <summary>
/// /v2/settings <c>extra.mineru_connection</c> 的读写适配。Backend 设置快照
/// 是唯一事实源：更新只替换 mineru_connection 一个键，原样保留其他 extra
/// 键、驻留 TTL 与下载源偏好。API Key 采用三态：<c>null</c> 表示保留
/// Backend 已存的 Key（宿主读回当前设置后合并，Backend 无需新字段），
/// 空字符串显式清除，非空字符串替换。保存不验证远程服务连通性，
/// 本进程也不会向远程 MinerU 服务发起任何解析请求（解析始终沿
/// Supervisor 既有链路）。
/// </summary>
public static class MineruConnectionSettings
{
    public const int MaxApiUrlLength = 2048;
    private const string ExtraKey = "mineru_connection";

    /// <summary>
    /// 从 Backend 设置快照投影当前 MinerU 连接；缺省本地模式，格式无法
    /// 识别或能力未声明时按本地处理（写入仍受能力与输入校验约束）。
    /// </summary>
    public static MineruConnectionState Read(
        SettingsSnapshot snapshot,
        bool remoteSupported)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!remoteSupported)
        {
            return new MineruConnectionState(false, "local", "", false);
        }
        string mode = "local";
        string apiUrl = "";
        bool hasApiKey = false;
        if (snapshot.Extra is { } extra &&
            extra.TryGetValue(ExtraKey, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            string? storedMode = StringProperty(value, "mode");
            if (storedMode is "local" or "remote")
            {
                mode = storedMode;
            }
            if (mode == "remote")
            {
                apiUrl = StringProperty(value, "api_url") ?? "";
                hasApiKey = !string.IsNullOrEmpty(StringProperty(value, "api_key"));
            }
        }
        return new MineruConnectionState(remoteSupported, mode, apiUrl, hasApiKey);
    }

    /// <summary>
    /// 校验并持久化 MinerU 连接偏好。local 只写 mode；remote 需要 http(s)
    /// 根地址（允许反向代理路径，禁止 userinfo/query/fragment/空白）；
    /// apiKey 为 null 时保留当前已存 Key，空字符串显式清除，非空替换
    /// （仅禁止 CRLF，长度由桥接消息总限额约束）。返回 Backend 回读的
    /// 更新后快照。
    /// </summary>
    public static async Task<SettingsSnapshot> ApplyAsync(
        IInferenceClient inference,
        string? mode,
        string? apiUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inference);
        if (mode != "local" && mode != "remote")
        {
            throw new ArgumentException("MinerU 模式必须是本地或远程。", nameof(mode));
        }
        ValidateRemoteTarget(mode, apiUrl, apiKey);
        return await PersistAsync(inference, mode, apiUrl, apiKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<SettingsSnapshot> PersistAsync(
        IInferenceClient inference,
        string mode,
        string? apiUrl,
        string? apiKey,
        CancellationToken cancellationToken)
    {
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
        extra[ExtraKey] = mode == "local"
            ? JsonSerializer.SerializeToElement(new { mode })
            : JsonSerializer.SerializeToElement(new
            {
                mode,
                api_url = apiUrl,
                // null=未编辑：读回当前设置后合并已存 Key，Backend 仍收到完整对象。
                api_key = apiKey ?? StoredApiKey(current),
            });
        return await inference
            .UpdateSettingsAsync(
                current with { Extra = extra },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string StoredApiKey(SettingsSnapshot snapshot) =>
        snapshot.Extra is { } extra &&
        extra.TryGetValue(ExtraKey, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("api_key", out JsonElement key) &&
        key.ValueKind == JsonValueKind.String
            ? key.GetString() ?? string.Empty
            : string.Empty;

    private static void ValidateRemoteTarget(string mode, string? apiUrl, string? apiKey)
    {
        if (mode != "remote")
        {
            return;
        }
        if (string.IsNullOrEmpty(apiUrl) || apiUrl.Length > MaxApiUrlLength)
        {
            throw new ArgumentException(
                "远程 MinerU 服务根地址不能为空且长度受限。",
                nameof(apiUrl));
        }
        if (apiUrl.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "远程 MinerU 服务根地址不能包含空白字符。",
                nameof(apiUrl));
        }
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "远程 MinerU 服务根地址必须是 http(s) 根地址（可含反代路径），"
                + "不能带用户信息、查询串或片段。",
                nameof(apiUrl));
        }
        // Key 长度不设任意上限（Backend 无此限制）；桥接消息总限额约束长度。
        if (apiKey is not null && (apiKey.Contains('\r') || apiKey.Contains('\n')))
        {
            throw new ArgumentException("API Key 不能包含换行。", nameof(apiKey));
        }
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
