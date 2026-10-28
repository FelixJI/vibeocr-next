using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Services;
using Velopack.Sources;

namespace VibeOCR.App.Features.Update;

internal sealed record VelopackFeedEndpoint(Uri BaseUri, IFileDownloader Downloader)
{
    public SimpleWebSource CreateSource() => new(BaseUri, Downloader);
}

/// <summary>
/// 解析后的更新源：单选源对应的 feed + 用户可见目录。检查与下载始终绑定
/// 同一选定源，不跨源静默回退。
/// </summary>
internal sealed record ResolvedUpdateFeeds(
    VelopackFeedEndpoint Feed,
    IReadOnlyList<UpdateSourceOption> Options,
    string DetectionEndpoint,
    string? Error = null)
{
    internal const string DirectId = "direct";
    internal const string CustomId = "custom";
}

internal static class VelopackFeedFactory
{
    internal const string DirectBaseUrl =
        "https://github.com/FelixJI/vibeocr-next/releases/latest/download/";

    internal const string SelectedField = "selected";
    internal const string CustomField = "custom";
    internal const int MaximumUrlLength = 2048;

    /// <summary>
    /// 解析配置为“单一选定源”：显式 <c>update.selected</c>（direct/custom）
    /// 优先；无显式选择时沿用旧 <c>update.sources</c> 数组语义（跳过无效
    /// 项，取第一个可用项；全部不可用或无配置回落官方直连）。
    /// </summary>
    public static ResolvedUpdateFeeds Resolve(string configFile)
    {
        UpdateSourceOption direct = DirectOption(selected: false);
        if (!File.Exists(configFile))
        {
            return new(Direct(), [direct with { Selected = true }], DirectBaseUrl);
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new(Direct(), [direct with { Selected = true }], DirectBaseUrl);
            }
            JsonElement update = document.RootElement.TryGetProperty(
                "update", out JsonElement node) && node.ValueKind == JsonValueKind.Object
                ? node
                : default;
            if (update.ValueKind != JsonValueKind.Object)
            {
                // 无 update 节点或非对象（null/数组等）：按无配置处理，
                // 不把畸形配置当成硬错误阻断启动。
                return new(Direct(), [direct with { Selected = true }], DirectBaseUrl);
            }
            string? selected = update.TryGetProperty(SelectedField, out JsonElement selectedNode) &&
                selectedNode.ValueKind == JsonValueKind.String
                ? selectedNode.GetString()
                : null;
            if (selected == ResolvedUpdateFeeds.CustomId)
            {
                (VelopackFeedEndpoint? custom, UpdateSourceOption? option, string? detection) =
                    ReadCustomEntry(update);
                if (custom is not null && option is not null)
                {
                    return new(custom, [direct, option], detection!);
                }
                // 显式选择自定义但配置无效：回落官方直连并如实说明；
                // 用户可在界面重新保存自定义源。
                return new(Direct(), [direct with { Selected = true }], DirectBaseUrl,
                    "自定义更新源配置无效，当前使用官方直连；请重新保存自定义源。");
            }
            if (selected == ResolvedUpdateFeeds.DirectId)
            {
                return new(Direct(), [direct with { Selected = true }], DirectBaseUrl);
            }
            (VelopackFeedEndpoint? legacy, UpdateSourceOption? legacyOption, string? legacyDetection) =
                ReadFirstLegacyEntry(update);
            if (legacy is not null && legacyOption is not null)
            {
                return new(legacy, [direct, legacyOption], legacyDetection!);
            }
            return new(Direct(), [direct with { Selected = true }], DirectBaseUrl);
        }
        catch (Exception error) when (
            error is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(Direct(), [direct with { Selected = true }], DirectBaseUrl);
        }
    }

    /// <summary>旧 <c>update.sources</c> 数组读取（兼容既有部署）。</summary>
    public static IReadOnlyList<VelopackFeedEndpoint> FromSettings(string configFile)
    {
        if (!File.Exists(configFile))
        {
            return [Direct()];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configFile));
            if (!document.RootElement.TryGetProperty("update", out JsonElement update) ||
                !update.TryGetProperty("sources", out JsonElement sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                return [Direct()];
            }

            var endpoints = new List<VelopackFeedEndpoint>();
            foreach (JsonElement source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object ||
                    !source.TryGetProperty("kind", out JsonElement kindElement) ||
                    kindElement.ValueKind != JsonValueKind.String) continue;
                string? kind = kindElement.GetString();
                string? url = source.TryGetProperty("url", out JsonElement urlElement) &&
                    urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()
                    : null;
                VelopackFeedEndpoint? endpoint = kind switch
                {
                    "direct" => Direct(),
                    "url_prefix" when CreateHttpUri(url) is { } prefix =>
                        Prefix(prefix),
                    "forward_proxy" when CreateHttpUri(url) is { } proxy =>
                        ForwardProxy(proxy),
                    _ => null,
                };
                if (endpoint is not null) endpoints.Add(endpoint);
            }
            return endpoints.Count > 0 ? endpoints : [Direct()];
        }
        catch (Exception error) when (
            error is JsonException or IOException or UnauthorizedAccessException)
        {
            return [Direct()];
        }
    }

    /// <summary>
    /// 持久化更新源选择：只改 <c>update.selected</c>/<c>update.custom</c>
    /// 节点，保留配置中的其他字段（与 hotkeys 持久化同一策略）。
    /// </summary>
    public static void SaveSelection(string configFile, string sourceId, string? kind, string? url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        JsonObject root = ReadForUpdate(configFile);
        JsonObject update = root["update"] as JsonObject ?? [];
        if (sourceId == ResolvedUpdateFeeds.DirectId)
        {
            update[SelectedField] = ResolvedUpdateFeeds.DirectId;
        }
        else
        {
            Uri? endpoint = ValidateCustomSource(kind, url);
            if (endpoint is null)
            {
                throw new InvalidDataException(
                    "自定义更新源无效：地址必须是 http/https 绝对 URL，不能包含用户名、查询串或片段，长度不超过 2048 字符。");
            }
            update[SelectedField] = ResolvedUpdateFeeds.CustomId;
            update[CustomField] = new JsonObject
            {
                ["kind"] = kind,
                ["url"] = endpoint.AbsoluteUri,
            };
        }
        root["update"] = update;
        AtomicFile.WriteAllText(configFile, root.ToJsonString(WriteOptions));
    }

    /// <summary>
    /// 校验自定义源 URL 边界：绝对 http/https、无 userinfo/query/fragment、
    /// 长度受限；返回规范 URI，供检查与下载共用。
    /// </summary>
    internal static Uri? ValidateCustomSource(string? kind, string? url)
    {
        if (kind is not ("url_prefix" or "forward_proxy")) return null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaximumUrlLength) return null;
        Uri? uri = CreateHttpUri(url);
        if (uri is null) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }
        return uri;
    }

    private static (VelopackFeedEndpoint? Feed, UpdateSourceOption? Option, string? Detection) ReadCustomEntry(
        JsonElement update)
    {
        if (!update.TryGetProperty(CustomField, out JsonElement custom) ||
            custom.ValueKind != JsonValueKind.Object ||
            !custom.TryGetProperty("kind", out JsonElement kindNode) ||
            kindNode.ValueKind != JsonValueKind.String ||
            kindNode.GetString() is not { } kind ||
            !custom.TryGetProperty("url", out JsonElement urlNode) ||
            urlNode.ValueKind != JsonValueKind.String ||
            urlNode.GetString() is not { } url)
        {
            return (null, null, null);
        }
        Uri? uri = ValidateCustomSource(kind, url);
        if (uri is null) return (null, null, null);
        VelopackFeedEndpoint endpoint = kind == "url_prefix" ? Prefix(uri) : ForwardProxy(uri);
        UpdateSourceOption option = new(
            ResolvedUpdateFeeds.CustomId,
            kind,
            kind == "url_prefix" ? "自定义镜像前缀" : "自定义正向代理",
            DisplayEndpoint(uri),
            Selected: true);
        return (endpoint, option, DetectionEndpoint(kind, uri));
    }

    private static (VelopackFeedEndpoint? Feed, UpdateSourceOption? Option, string? Detection) ReadFirstLegacyEntry(
        JsonElement update)
    {
        if (!update.TryGetProperty("sources", out JsonElement sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return (null, null, null);
        }
        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("kind", out JsonElement kindElement) ||
                kindElement.ValueKind != JsonValueKind.String) continue;
            string? kind = kindElement.GetString();
            string? url = source.TryGetProperty("url", out JsonElement urlElement) &&
                urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;
            switch (kind)
            {
                case "direct":
                    return (Direct(), new UpdateSourceOption(
                        "source-0", "direct", "配置文件官方直连", DirectBaseUrl, true),
                        DirectBaseUrl);
                case "url_prefix" when CreateHttpUri(url) is { } prefix:
                    return (Prefix(prefix), new UpdateSourceOption(
                        "source-0", "url_prefix", "配置文件镜像前缀", DisplayEndpoint(prefix), true),
                        DetectionEndpoint("url_prefix", prefix));
                case "forward_proxy" when CreateHttpUri(url) is { } proxy:
                    return (ForwardProxy(proxy), new UpdateSourceOption(
                        "source-0", "forward_proxy", "配置文件正向代理", DisplayEndpoint(proxy), true),
                        DetectionEndpoint("forward_proxy", proxy));
            }
        }
        return (null, null, null);
    }

    /// <summary>当前源的实际检测地址：前缀拼接官方 Release 路径（脱敏），
    /// 代理保持官方地址经代理下载。</summary>
    private static string DetectionEndpoint(string kind, Uri uri) => kind == "url_prefix"
        ? $"{DisplayEndpoint(uri).TrimEnd('/')}/{DirectBaseUrl}"
        : DirectBaseUrl;

    /// <summary>端点的脱敏展示：仅 scheme/host/port/path；凭据、查询串与
    /// 片段永不进入桥接投影（transport 仍使用原始 URL，行为不变）。</summary>
    internal static string DisplayEndpoint(Uri uri) => new UriBuilder(uri)
    {
        UserName = string.Empty,
        Password = string.Empty,
        Query = string.Empty,
        Fragment = string.Empty,
    }.Uri.AbsoluteUri;

    private static JsonObject ReadForUpdate(string configFile)
    {
        if (!File.Exists(configFile)) return [];
        try
        {
            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(configFile));
            return parsed as JsonObject ?? throw new JsonException(
                "更新源配置文件根必须是 JSON 对象。");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "更新源配置文件无法解析，请检查后重试。", error);
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static UpdateSourceOption DirectOption(bool selected) => new(
        ResolvedUpdateFeeds.DirectId,
        "direct",
        "官方直连（GitHub）",
        DirectBaseUrl,
        selected);

    internal static VelopackFeedEndpoint Direct() => new(
        new Uri(DirectBaseUrl),
        new HttpClientFileDownloader());

    internal static VelopackFeedEndpoint Prefix(Uri prefix) => new(
        new Uri($"{prefix.AbsoluteUri.TrimEnd('/')}/{DirectBaseUrl}"),
        new HttpClientFileDownloader());

    internal static VelopackFeedEndpoint ForwardProxy(Uri proxy) => new(
        new Uri(DirectBaseUrl),
        new ProxyFileDownloader(proxy));

    private static Uri? CreateHttpUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUrlLength) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return null;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }
}
