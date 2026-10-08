// Deferred PDF session client mirroring DeferredInferenceClient.
using VibeOCR.Platform.Inference;
using System.Text.Json;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.App.Inference;

public sealed class DeferredPdfSessionClient : IPdfSessionClient
{
    private IPdfSessionClient? _inner;

    public bool IsAttached => Volatile.Read(ref _inner) is not null;

    public void Attach(IPdfSessionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Interlocked.Exchange(ref _inner, client);
    }

    public void Detach() => Interlocked.Exchange(ref _inner, null);

    private IPdfSessionClient Current =>
        Volatile.Read(ref _inner)
        ?? throw new InvalidOperationException("v2 PDF session client not attached; Phase 8 pending.");

    public Task<PdfSessionOpenResult> OpenAsync(string path, string? password, CancellationToken ct)
        => Current.OpenAsync(path, password, ct);
    public Task<byte[]> RenderAsync(string sessionId, int page, int size, CancellationToken ct)
        => Current.RenderAsync(sessionId, page, size, ct);
    public Task<PdfMutateResult> RotateAsync(string sessionId, int[] pages, int angle, CancellationToken ct)
        => Current.RotateAsync(sessionId, pages, angle, ct);
    public Task<PdfMutateResult> DeletePagesAsync(string sessionId, int[] pages, CancellationToken ct)
        => Current.DeletePagesAsync(sessionId, pages, ct);
    public Task<string> SaveAsync(string sessionId, string outputPath, CancellationToken ct)
        => Current.SaveAsync(sessionId, outputPath, ct);
    public Task<Wire.PdfDocumentMirror> GetModelAsync(string sessionId, CancellationToken ct) => Current.GetModelAsync(sessionId, ct);
    public Task LoadAsync(string sessionId, Action<JsonElement> progress, CancellationToken ct) => Current.LoadAsync(sessionId, progress, ct);
    public Task<byte[]> RenderPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct) => Current.RenderPreviewAsync(sessionId, page, dpi, ct);
    public Task<Wire.PdfMutationResponse> AddTextLayersAsync(string sessionId, Wire.BatchAddTextLayerRequest request, CancellationToken ct) => Current.AddTextLayersAsync(sessionId, request, ct);
    public Task DeleteTextLayersAsync(string sessionId, int[] pages, Action<JsonElement> progress, CancellationToken ct) => Current.DeleteTextLayersAsync(sessionId, pages, progress, ct);
    public Task CancelAsync(string sessionId, CancellationToken ct) => Current.CancelAsync(sessionId, ct);
    public Task ResetCancelAsync(string sessionId, CancellationToken ct) => Current.ResetCancelAsync(sessionId, ct);
    public Task<string> SaveWithSettingsAsync(string sessionId, string outputPath, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken ct) => Current.SaveWithSettingsAsync(sessionId, outputPath, settings, ct);
    public Task CloseAsync(string sessionId, CancellationToken ct)
        => Current.CloseAsync(sessionId, ct);
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _inner, null);
        return ValueTask.CompletedTask;
    }
}
