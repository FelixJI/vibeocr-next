using System.Text.Json;
using Velopack.Sources;

namespace VibeOCR.App.Features.Update;

internal sealed record VelopackFeedEndpoint(Uri BaseUri, IFileDownloader Downloader)
{
    public SimpleWebSource CreateSource() => new(BaseUri, Downloader);

    public Uri AssetUri(string fileName, string? releaseVersion = null)
    {
        string baseUrl = BaseUri.AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(releaseVersion))
        {
            baseUrl = baseUrl.Replace(
                "/releases/latest/download/",
                $"/releases/download/v{releaseVersion}/",
                StringComparison.Ordinal);
        }
        return new Uri(new Uri(baseUrl), fileName);
    }
}

internal static class VelopackFeedFactory
{
    internal const string DirectBaseUrl =
        "https://github.com/FelixJI/vibeocr-next/releases/latest/download/";

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
                if (!source.TryGetProperty("kind", out JsonElement kindElement)) continue;
                string? kind = kindElement.GetString();
                string? url = source.TryGetProperty("url", out JsonElement urlElement)
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
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return null;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }
}
