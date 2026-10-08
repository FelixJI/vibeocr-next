// HttpClient-based PDF session client for the v2 supervisor's /v2/pdf/sessions/* routes.
//
// All request/response payloads follow the generated v2 wire contracts exactly
// (OpenRequest, PdfOpenResponse, PdfMutationResponse, PdfDocumentResponse,
// SaveRequest, SaveResponse): mutation responses carry a ModelDiff (never a
// page_count), authoritative page counts come from full_model or the existing
// /model endpoint, and the save request/response field is `path`.
using System.Text.Json;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Runtime.Client;
using VibeOCR.Runtime.Contracts.Generated;

namespace VibeOCR.Platform.Inference;

public sealed class PdfSessionHttpClient : IPdfSessionClient
{
    private readonly RuntimeHttpClient _runtime;
    private readonly bool _ownsRuntime;

    public PdfSessionHttpClient(Uri baseUrl, string sessionToken, HttpMessageHandler? handler = null)
        : this(new RuntimeHttpClient(baseUrl, sessionToken, handler), ownsRuntime: true)
    {
    }

    /// <summary>
    /// Internal seam so <see cref="InferenceHttpClient"/> can delegate its PDF
    /// session operations to one shared transport instead of duplicating the
    /// wire parsing. The caller keeps ownership of <paramref name="runtime"/>.
    /// </summary>
    internal PdfSessionHttpClient(RuntimeHttpClient runtime, bool ownsRuntime = false)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _ownsRuntime = ownsRuntime;
    }

    public async Task<PdfSessionOpenResult> OpenAsync(string path, string? password, CancellationToken ct)
    {
        // OpenRequest is additionalProperties:false with `path` only; a non-null
        // password would be rejected by the server, so refuse it up front.
        if (!string.IsNullOrEmpty(password))
        {
            throw new NotSupportedException(
                "The v2 PDF session open operation does not accept a password.");
        }
        using StringContent content = _runtime.CreateJsonContent(new { path });
        using HttpResponseMessage resp = await _runtime.PostAsync(
            RuntimeOperationPaths.OpenPdfSession, content, ct);
        await EnsureSuccessAsync(resp, ct);
        using JsonDocument doc = await _runtime.ReadJsonDocumentAsync(resp, ct);
        JsonElement root = doc.RootElement;
        JsonElement model = RequiredProperty(root, "model", "open");
        return new PdfSessionOpenResult(
            RequiredString(root, "session_id", "open"),
            ReadModelPageCount(model, "open"),
            RequiredString(model, "file_path", "open"),
            model.Deserialize<Wire.PdfDocumentMirror>());
    }

    public async Task<byte[]> RenderAsync(string sessionId, int page, int size, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _runtime.GetAsync(
            $"{BindSessionPath(RuntimeOperationPaths.RenderPdfPage, sessionId)}?page={page}&size={size}", ct);
        await EnsureSuccessAsync(resp, ct);
        return await _runtime.ReadBinaryAsync(resp, "image/png", ct);
    }

    public async Task<PdfMutateResult> RotateAsync(string sessionId, int[] pages, int angle, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(new { pages, angle });
        using HttpResponseMessage resp = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.RotatePdfPages, sessionId), content, ct);
        return await ReadMutationPageCountAsync(sessionId, resp, "rotate", ct);
    }

    public async Task<PdfMutateResult> DeletePagesAsync(string sessionId, int[] pages, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(new { pages });
        using HttpResponseMessage resp = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.DeletePdfPages, sessionId), content, ct);
        return await ReadMutationPageCountAsync(sessionId, resp, "delete_pages", ct);
    }

    public async Task<string> SaveAsync(string sessionId, string outputPath, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(
            new { path = outputPath });
        using HttpResponseMessage resp = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.SavePdfSession, sessionId), content, ct);
        await EnsureSuccessAsync(resp, ct);
        using JsonDocument doc = await _runtime.ReadJsonDocumentAsync(resp, ct);
        return RequiredString(doc.RootElement, "path", "save");
    }

    public Task<PdfMutateResult> InsertBlankAsync(string sessionId, int afterIndex, double width, double height, CancellationToken ct) =>
        MutateAsync(sessionId, "insert_blank", new { after_index = afterIndex, width, height }, ct);
    public Task<PdfMutateResult> InsertFromAsync(string sessionId, string sourcePath, int afterIndex, CancellationToken ct) =>
        MutateAsync(sessionId, "insert_from", new { source_path = sourcePath, after_index = afterIndex }, ct);
    public Task<PdfMutateResult> ReorderAsync(string sessionId, int[] newOrder, CancellationToken ct) =>
        MutateAsync(sessionId, "reorder", new { new_order = newOrder }, ct);
    private async Task<PdfMutateResult> MutateAsync(string sessionId, string operation, object body, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(body);
        using HttpResponseMessage response = await _runtime.PostAsync(
            $"/v2/pdf/sessions/{Uri.EscapeDataString(sessionId)}/{operation}", content, ct);
        return await ReadMutationPageCountAsync(sessionId, response, operation, ct);
    }

    public async Task<Wire.PdfDocumentMirror> GetModelAsync(string sessionId, CancellationToken ct)
    {
        using HttpResponseMessage response = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.GetPdfSessionModel, sessionId), null, ct);
        await EnsureSuccessAsync(response, ct);
        using JsonDocument document = await _runtime.ReadJsonDocumentAsync(response, ct);
        return document.RootElement.Deserialize<Wire.PdfDocumentMirror>()
            ?? throw new InvalidOperationException("PDF model is missing.");
    }

    public Task LoadAsync(string sessionId, Action<JsonElement> progress, CancellationToken ct) =>
        StreamAsync(sessionId, "load", null, progress, ct);

    public Task DeleteTextLayersAsync(string sessionId, int[] pages, Action<JsonElement> progress, CancellationToken ct) =>
        StreamAsync(sessionId, "delete_text_layers", new { pages }, progress, ct);

    private async Task StreamAsync(string sessionId, string operation, object? body,
        Action<JsonElement> progress, CancellationToken ct)
    {
        using StringContent? content = body is null ? null : _runtime.CreateJsonContent(body);
        using HttpResponseMessage response = await _runtime.PostStreamAsync(
            $"/v2/pdf/sessions/{Uri.EscapeDataString(sessionId)}/{operation}", content, ct);
        await EnsureSuccessAsync(response, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement value = document.RootElement;
            if (value.TryGetProperty("message", out JsonElement message) &&
                message.ValueKind == JsonValueKind.String && message.GetString()!.StartsWith("error:", StringComparison.Ordinal))
                throw new InvalidOperationException("PDF stream failed.");
            progress(value.Clone());
        }
    }

    public async Task<byte[]> RenderPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(new { page, dpi });
        using HttpResponseMessage response = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.RenderPdfPreview, sessionId), content, ct);
        await EnsureSuccessAsync(response, ct);
        return await _runtime.ReadBinaryAsync(response, "image/png", ct);
    }

    public async Task<Wire.PdfMutationResponse> UpdateBlockTextAsync(string sessionId,
        Wire.UpdateBlockTextRequest request, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(request);
        using HttpResponseMessage response = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.UpdatePdfBlockText, sessionId), content, ct);
        await EnsureSuccessAsync(response, ct);
        using JsonDocument document = await _runtime.ReadJsonDocumentAsync(response, ct);
        return document.RootElement.Deserialize<Wire.PdfMutationResponse>()
            ?? throw new InvalidOperationException("PDF block update result is missing.");
    }

    public async Task<Wire.PageInspectResponse> InspectPageAsync(string sessionId, int page, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(new { page });
        using HttpResponseMessage response = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.InspectPdfPage, sessionId), content, ct);
        await EnsureSuccessAsync(response, ct);
        using JsonDocument document = await _runtime.ReadJsonDocumentAsync(response, ct);
        return document.RootElement.Deserialize<Wire.PageInspectResponse>()
            ?? throw new InvalidOperationException("PDF page inspect payload is missing.");
    }

    public async Task<Wire.PdfMutationResponse> AddTextLayersAsync(string sessionId,
        Wire.BatchAddTextLayerRequest request, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(request);
        using HttpResponseMessage response = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.AddPdfTextLayerBatch, sessionId), content, ct);
        await EnsureSuccessAsync(response, ct);
        using JsonDocument document = await _runtime.ReadJsonDocumentAsync(response, ct);
        return document.RootElement.Deserialize<Wire.PdfMutationResponse>()
            ?? throw new InvalidOperationException("PDF write result is missing.");
    }

    public Task CancelAsync(string sessionId, CancellationToken ct) => SignalAsync(sessionId, "cancel", ct);
    public Task ResetCancelAsync(string sessionId, CancellationToken ct) => SignalAsync(sessionId, "reset_cancel", ct);
    private async Task SignalAsync(string sessionId, string operation, CancellationToken ct)
    {
        using HttpResponseMessage response = await _runtime.PostAsync(
            $"/v2/pdf/sessions/{Uri.EscapeDataString(sessionId)}/{operation}", null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<string> SaveWithSettingsAsync(string sessionId, string outputPath,
        IReadOnlyDictionary<string, JsonElement> settings, CancellationToken ct)
    {
        using StringContent content = _runtime.CreateJsonContent(new {
            path = outputPath, pdf_settings = settings, rewrite_text_layers = false });
        using HttpResponseMessage response = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.SavePdfSession, sessionId), content, ct);
        await EnsureSuccessAsync(response, ct);
        using JsonDocument document = await _runtime.ReadJsonDocumentAsync(response, ct);
        return RequiredString(document.RootElement, "path", "save");
    }

    public async Task CloseAsync(string sessionId, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.ClosePdfSession, sessionId), content: null, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public ValueTask DisposeAsync()
    {
        return _ownsRuntime ? _runtime.DisposeAsync() : ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------
    // v2 wire helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// PdfMutationResponse carries a ModelDiff, never a page count. When the
    /// diff includes a full_model its pages are authoritative; otherwise the
    /// existing /model endpoint is read back — local page arithmetic is never
    /// used to guess the new count.
    /// </summary>
    private async Task<PdfMutateResult> ReadMutationPageCountAsync(
        string sessionId, HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        await EnsureSuccessAsync(resp, ct);
        using JsonDocument doc = await _runtime.ReadJsonDocumentAsync(resp, ct);
        JsonElement root = doc.RootElement;
        Wire.PdfMutationResponse mutation = root.Deserialize<Wire.PdfMutationResponse>()
            ?? throw new InvalidOperationException("PDF mutation result is missing.");
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("diff", out JsonElement diff) &&
            diff.ValueKind == JsonValueKind.Object &&
            diff.TryGetProperty("full_model", out JsonElement fullModel) &&
            fullModel.ValueKind == JsonValueKind.Object &&
            fullModel.TryGetProperty("pages", out JsonElement pages) &&
            pages.ValueKind == JsonValueKind.Array)
        {
            return new PdfMutateResult(pages.GetArrayLength(), mutation.Diff);
        }
        using HttpResponseMessage modelResp = await _runtime.PostAsync(
            BindSessionPath(RuntimeOperationPaths.GetPdfSessionModel, sessionId),
            content: null, ct);
        await EnsureSuccessAsync(modelResp, ct);
        using JsonDocument modelDoc = await _runtime.ReadJsonDocumentAsync(modelResp, ct);
        return new PdfMutateResult(
            ReadModelPageCount(modelDoc.RootElement, $"{operation} model read-back"),
            mutation.Diff with { FullModel = modelDoc.RootElement.Deserialize<Wire.PdfDocumentMirror>() });
    }

    private static int ReadModelPageCount(JsonElement model, string operation) =>
        model.ValueKind == JsonValueKind.Object &&
            model.TryGetProperty("pages", out JsonElement pages) &&
            pages.ValueKind == JsonValueKind.Array
            ? pages.GetArrayLength()
            : throw new InferenceClientException(
                HttpV2ErrorCode.ProtocolMismatch,
                $"The v2 PDF {operation} response has no authoritative 'pages'.",
                retryable: false);

    private static JsonElement RequiredProperty(JsonElement parent, string name, string operation) =>
        parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : throw new InferenceClientException(
                HttpV2ErrorCode.ProtocolMismatch,
                $"The v2 PDF {operation} response is missing '{name}'.",
                retryable: false);

    private static string RequiredString(JsonElement parent, string name, string operation)
    {
        JsonElement value = RequiredProperty(parent, name, operation);
        return value.ValueKind == JsonValueKind.String ? value.GetString()!
            : throw new InferenceClientException(
            HttpV2ErrorCode.ProtocolMismatch,
            $"The v2 PDF {operation} response field '{name}' is not a string.",
            retryable: false);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.EnsureSuccessAsync(response, cancellationToken);
        }
        catch (RuntimeClientException exc)
        {
            throw new InferenceClientException(
                exc.Code, exc.Message, exc.Retryable, exc.Detail);
        }
    }

    private static string BindSessionPath(string template, string sessionId) =>
        template.Replace(
            "{session_id}", Uri.EscapeDataString(sessionId), StringComparison.Ordinal);
}
