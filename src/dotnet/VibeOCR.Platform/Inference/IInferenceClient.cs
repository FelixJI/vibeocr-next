// Phase 7B: WinUI inference supervisor client surface.
//
// The plan (§7B) requires:
//   * a new IInferenceClient/InferenceHttpClient based on HttpClient, typed
//     DTOs and multipart streaming;
//   * an InferenceSupervisorProcess reusing log/Job Object/whole-tree
//     termination and startup-error presentation.
//
// This file declares the transport-neutral IInferenceClient interface. The
// concrete InferenceHttpClient (HttpClient + HttpV2 DTOs) lives alongside;
// InferenceSupervisorProcess owns the child process lifecycle.
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.Platform.Inference;

/// <summary>
/// Transport-neutral v2 supervisor client used by WinUI ViewModels.
/// Mirrors the Python SupervisorClient surface so the two front-ends share
/// one contract.
/// </summary>
public interface IInferenceClient : IAsyncDisposable
{
    /// <summary>Supervisor base URL (always loopback).</summary>
    Uri BaseUrl { get; }

    /// <summary>
    /// Submit one logical job. Multipart uploads are keyed by the attachment
    /// names referenced by <see cref="SubmitItem.Source"/>.
    /// </summary>
    Task<JobRef> SubmitAsync(
        SubmitRequest request,
        IReadOnlyDictionary<string, SubmitUpload> uploads,
        CancellationToken cancellationToken);

    /// <summary>
    /// Explicitly preload recognition modes through the shared preload
    /// endpoint (pipelines stays the required legacy projection). The request
    /// is executed by the Backend against its configured service; the frontend
    /// never connects to the remote MinerU service directly.
    /// </summary>
    Task<ResidencyStatus> PreloadRuntimeAsync(
        Wire.RuntimePreloadRequest request,
        CancellationToken cancellationToken) => Task.FromException<ResidencyStatus>(
            new NotSupportedException("This inference client does not expose runtime preload."));

    /// <summary>
    /// Atomically observe the current snapshot, ordered events and typed
    /// outcomes after <paramref name="afterSequence"/>.
    /// </summary>
    Task<JobUpdate> ObserveAsync(
        string jobId, int afterSequence, CancellationToken cancellationToken);

    /// <summary>Cancel, retry or forget a job through the generic command seam.</summary>
    Task<JobCommandResult> CommandAsync(JobCommand command, CancellationToken cancellationToken);

    /// <summary>Residency status (model TTL/pin/LRU/VRAM).</summary>
    Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken);

    /// <summary>Backend runtime profile, component and maintenance status.</summary>
    Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(CancellationToken cancellationToken) =>
        Task.FromException<RuntimeStatusSnapshot>(
            new NotSupportedException("This inference client does not expose runtime status."));

    /// <summary>
    /// Backend health snapshot including capability descriptors; selection
    /// catalogs (engines, download sources, component variants) live on the
    /// descriptors of the capabilities that advertise them.
    /// </summary>
    Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
        Task.FromException<Wire.Health>(
            new NotSupportedException("This inference client does not expose runtime health."));

    /// <summary>Backend settings snapshot.</summary>
    Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replace Backend settings and return the updated snapshot. The Backend
    /// snapshot is the long-term source of truth for download source
    /// preferences; callers must not persist a second copy.
    /// </summary>
    Task<SettingsSnapshot> UpdateSettingsAsync(
        SettingsSnapshot settings, CancellationToken cancellationToken);

    /// <summary>Export OCR result to a file (txt/markdown/html) via the supervisor.</summary>
    Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken);

    /// <summary>Fetch an authorized, job-bound PNG result asset.</summary>
    Task<byte[]> FetchResultAssetAsync(
        string jobId, string itemId, string assetId, CancellationToken cancellationToken) =>
        Task.FromException<byte[]>(new NotSupportedException("Result assets are unavailable."));

    // PDF session operations (v2 — proxied through supervisor)
    Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct);
    Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct);
    Task<PdfMutateResult> RotatePdfPagesAsync(string sessionId, int[] pages, int angle, CancellationToken ct);
    Task<PdfMutateResult> DeletePdfPagesAsync(string sessionId, int[] pages, CancellationToken ct);
    Task<PdfMutateResult> InsertPdfBlankAsync(string sessionId, int afterIndex, double width, double height, CancellationToken ct) =>
        Task.FromException<PdfMutateResult>(new NotSupportedException("PDF operation unavailable."));
    Task<PdfMutateResult> InsertPdfFromAsync(string sessionId, string sourcePath, int afterIndex, CancellationToken ct) =>
        Task.FromException<PdfMutateResult>(new NotSupportedException("PDF operation unavailable."));
    Task<PdfMutateResult> ReorderPdfAsync(string sessionId, int[] newOrder, CancellationToken ct) =>
        Task.FromException<PdfMutateResult>(new NotSupportedException("PDF operation unavailable."));
    Task<string> SavePdfAsync(string sessionId, string outputPath, CancellationToken ct);
    Task<Wire.PdfDocumentMirror> GetPdfModelAsync(string sessionId, CancellationToken ct) =>
        Task.FromException<Wire.PdfDocumentMirror>(new NotSupportedException("PDF model unavailable."));
    Task LoadPdfAsync(string sessionId, Action<System.Text.Json.JsonElement> progress, CancellationToken ct) => Task.FromException(new NotSupportedException("PDF operation unavailable."));
    Task<byte[]> RenderPdfPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct) =>
        Task.FromException<byte[]>(new NotSupportedException("PDF preview unavailable."));
    Task<Wire.PdfMutationResponse> UpdatePdfBlockTextAsync(string sessionId, Wire.UpdateBlockTextRequest request, CancellationToken ct) =>
        Task.FromException<Wire.PdfMutationResponse>(new NotSupportedException("PDF block update unavailable."));
    Task<Wire.PageInspectResponse> InspectPdfPageAsync(string sessionId, int page, CancellationToken ct) =>
        Task.FromException<Wire.PageInspectResponse>(new NotSupportedException("PDF page inspect unavailable."));
    Task<Wire.PdfMutationResponse> AddPdfTextLayersAsync(string sessionId, Wire.BatchAddTextLayerRequest request, CancellationToken ct) =>
        Task.FromException<Wire.PdfMutationResponse>(new NotSupportedException("PDF text layers unavailable."));
    Task DeletePdfTextLayersAsync(string sessionId, int[] pages, Action<System.Text.Json.JsonElement> progress, CancellationToken ct) =>
        Task.FromException(new NotSupportedException("PDF text layers unavailable."));
    Task CancelPdfAsync(string sessionId, CancellationToken ct) => Task.FromException(new NotSupportedException("PDF operation unavailable."));
    Task ResetPdfCancelAsync(string sessionId, CancellationToken ct) => Task.FromException(new NotSupportedException("PDF operation unavailable."));
    Task<string> SavePdfWithSettingsAsync(string sessionId, string outputPath,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> settings, CancellationToken ct) => SavePdfAsync(sessionId, outputPath, ct);
    Task ClosePdfSessionAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// Multipart content for one attachment referenced by a
/// <see cref="SubmitItem"/>. Display names belong to the manifest rather than
/// the transport payload.
/// </summary>
public sealed record SubmitUpload(string? ContentType, IReadOnlyList<byte> Content);

/// <summary>
/// Result of a generic job command. Cancel returns <see cref="CancelMode"/>,
/// retry returns <see cref="JobRef"/>, and forget returns neither.
/// </summary>
public sealed record JobCommandResult(
    string CommandId,
    JobCommandKind Kind,
    CancelMode? CancelMode,
    JobRef? JobRef);

/// <summary>Export request mirroring the v2 /v2/export endpoint.</summary>
public sealed record ExportRequest(
    string RawText, string MarkdownText, string HtmlText,
    string OutputPath, string Format, bool Overwrite,
    IReadOnlyList<System.Text.Json.JsonElement>? ContentBlocks = null);

/// <summary>Export result from the v2 supervisor.</summary>
public sealed record ExportResult(
    string OutputPath, long BytesWritten, bool Incomplete = false, int ImagesMissing = 0);
