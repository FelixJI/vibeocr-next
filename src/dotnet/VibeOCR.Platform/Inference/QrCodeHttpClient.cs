// HttpClient-based QR client for the v2 supervisor's /v2/qrcode/* endpoints.
using System.Text.Json;
using VibeOCR.Runtime.Client;
using VibeOCR.Runtime.Contracts.Generated;

namespace VibeOCR.Platform.Inference;

/// <summary>Concrete IQrCodeClient over HttpClient (loopback, Bearer token).</summary>
public sealed class QrCodeHttpClient : IQrCodeClient
{
    private readonly RuntimeHttpClient _runtime;
    public QrCodeHttpClient(Uri baseUrl, string sessionToken, HttpMessageHandler? handler = null)
    {
        _runtime = new RuntimeHttpClient(baseUrl, sessionToken, handler);
    }

    public async Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
        ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        if (image.IsEmpty) throw new ArgumentException("Image must not be empty.", nameof(image));
        // #213: the base64 round-trip exists only for this Python wire boundary;
        // local desktop decoding consumes the raw bytes directly.
        string base64Image = Convert.ToBase64String(image.Span);
        using StringContent content = _runtime.CreateJsonContent(
            new { image = base64Image });
        using HttpResponseMessage response = await _runtime.PostAsync(
            RuntimeOperationPaths.DecodeQrCode, content, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowTypedAsync(response, cancellationToken).ConfigureAwait(false);
        }

        using JsonDocument doc = await _runtime
            .ReadJsonDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var items = new List<QrCodeDecodedItem>();
        foreach (JsonElement code in doc.RootElement.GetProperty("codes").EnumerateArray())
        {
            items.Add(new QrCodeDecodedItem(
                code.GetProperty("data").GetString() ?? string.Empty,
                code.TryGetProperty("format", out JsonElement fmt) ? (fmt.GetString() ?? "QR") : "QR",
                code.TryGetProperty("is_url", out JsonElement isUrl) && isUrl.GetBoolean()));
        }

        return items;
    }

    public async Task<QrCodeGeneratedImage> GenerateAsync(
        string data, string format, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        using StringContent content = _runtime.CreateJsonContent(
            new { data, format });
        using HttpResponseMessage response = await _runtime.PostAsync(
            RuntimeOperationPaths.GenerateQrCode, content, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowTypedAsync(response, cancellationToken).ConfigureAwait(false);
        }

        using JsonDocument doc = await _runtime
            .ReadJsonDocumentAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return new QrCodeGeneratedImage(
            doc.RootElement.GetProperty("image").GetString() ?? string.Empty,
            doc.RootElement.TryGetProperty("media_type", out JsonElement mt) ? (mt.GetString() ?? "image/png") : "image/png");
    }

    public ValueTask DisposeAsync()
    {
        return _runtime.DisposeAsync();
    }

    private async Task ThrowTypedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.EnsureSuccessAsync(response, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RuntimeClientException exc)
        {
            throw new InferenceClientException(
                exc.Code, exc.Message, exc.Retryable, exc.Detail);
        }
    }
}
