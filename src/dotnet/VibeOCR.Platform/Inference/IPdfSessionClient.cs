// Phase 8 blocker fix: PDF session client for v2 supervisor PDF endpoints.
// Mirrors the supervisor's /v2/pdf/sessions/* routes so PdfViewModel can
// open/render/rotate/delete/save/close through the Supervisor HTTP contract.
using System.Text.Json;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.Platform.Inference;

public sealed record PdfSessionOpenResult(string SessionId, int PageCount, string FilePath, Wire.PdfDocumentMirror? Model = null);
public sealed record PdfMutateResult(int PageCount, Wire.ModelDiff? Diff = null);

/// <summary>
/// PDF session operations via the v2 supervisor. The supervisor owns the PDF
/// child process; these methods proxy through it.
/// </summary>
public interface IPdfSessionClient : IAsyncDisposable
{
    Task<PdfSessionOpenResult> OpenAsync(string path, string? password, CancellationToken ct);
    Task<byte[]> RenderAsync(string sessionId, int page, int size, CancellationToken ct);
    Task<PdfMutateResult> RotateAsync(string sessionId, int[] pages, int angle, CancellationToken ct);
    Task<PdfMutateResult> DeletePagesAsync(string sessionId, int[] pages, CancellationToken ct);
    Task<PdfMutateResult> InsertBlankAsync(string sessionId, int afterIndex, double width, double height, CancellationToken ct);
    Task<PdfMutateResult> InsertFromAsync(string sessionId, string sourcePath, int afterIndex, CancellationToken ct);
    Task<PdfMutateResult> ReorderAsync(string sessionId, int[] newOrder, CancellationToken ct);
    Task<string> SaveAsync(string sessionId, string outputPath, CancellationToken ct);
    Task<Wire.PdfDocumentMirror> GetModelAsync(string sessionId, CancellationToken ct);
    Task LoadAsync(string sessionId, Action<JsonElement> progress, CancellationToken ct);
    Task<byte[]> RenderPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct);
    Task<Wire.PdfMutationResponse> AddTextLayersAsync(string sessionId, Wire.BatchAddTextLayerRequest request, CancellationToken ct);
    Task DeleteTextLayersAsync(string sessionId, int[] pages, Action<JsonElement> progress, CancellationToken ct);
    Task CancelAsync(string sessionId, CancellationToken ct);
    Task ResetCancelAsync(string sessionId, CancellationToken ct);
    Task<string> SaveWithSettingsAsync(string sessionId, string outputPath, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken ct);
    Task CloseAsync(string sessionId, CancellationToken ct);
}
