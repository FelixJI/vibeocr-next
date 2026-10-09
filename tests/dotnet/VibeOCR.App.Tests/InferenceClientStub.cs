using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.App.Tests;

/// <summary>
/// Generic-job test adapter. Individual tests override only the seams they
/// exercise; no legacy recognition/status/events/result facade exists here.
/// </summary>
internal abstract class InferenceClientStub : IInferenceClient
{
    public virtual Uri BaseUrl => new("http://127.0.0.1:1");

    public virtual Task<JobRef> SubmitAsync(
        SubmitRequest request,
        IReadOnlyDictionary<string, SubmitUpload> uploads,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<JobUpdate> ObserveAsync(
        string jobId,
        int afterSequence,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<JobCommandResult> CommandAsync(
        JobCommand command,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("This stub does not expose runtime health.");

    public virtual Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<SettingsSnapshot> UpdateSettingsAsync(
        SettingsSnapshot settings,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<byte[]> FetchResultAssetAsync(
        string jobId, string itemId, string assetId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<PdfSessionOpenResult> OpenPdfSessionAsync(
        string path,
        string? password,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<byte[]> RenderPdfPageAsync(
        string sessionId,
        int page,
        int size,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<PdfMutateResult> RotatePdfPagesAsync(
        string sessionId,
        int[] pages,
        int angle,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<PdfMutateResult> DeletePdfPagesAsync(
        string sessionId,
        int[] pages,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<string> SavePdfAsync(
        string sessionId,
        string outputPath,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual Task<PdfMutateResult> InsertPdfBlankAsync(string sessionId, int afterIndex, double width, double height, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<PdfMutateResult> InsertPdfFromAsync(string sessionId, string sourcePath, int afterIndex, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task<PdfMutateResult> ReorderPdfAsync(string sessionId, int[] newOrder, CancellationToken ct) => throw new NotImplementedException();

    public virtual Task<VibeOCR.Runtime.Contracts.Generated.Wire.PdfDocumentMirror> GetPdfModelAsync(string sessionId, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task LoadPdfAsync(string sessionId, Action<System.Text.Json.JsonElement> progress, CancellationToken ct) => Task.CompletedTask;
    public virtual Task<byte[]> RenderPdfPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct) => RenderPdfPageAsync(sessionId, page, 1024, ct);
    public virtual Task<VibeOCR.Runtime.Contracts.Generated.Wire.PdfMutationResponse> UpdatePdfBlockTextAsync(string sessionId, VibeOCR.Runtime.Contracts.Generated.Wire.UpdateBlockTextRequest request, CancellationToken ct) => throw new NotSupportedException("PDF block update unavailable.");
    public virtual Task<VibeOCR.Runtime.Contracts.Generated.Wire.PageInspectResponse> InspectPdfPageAsync(string sessionId, int page, CancellationToken ct) => throw new NotSupportedException("PDF page inspect unavailable.");
    public virtual Task<VibeOCR.Runtime.Contracts.Generated.Wire.PdfMutationResponse> AddPdfTextLayersAsync(string sessionId, VibeOCR.Runtime.Contracts.Generated.Wire.BatchAddTextLayerRequest request, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task DeletePdfTextLayersAsync(string sessionId, int[] pages, Action<System.Text.Json.JsonElement> progress, CancellationToken ct) => throw new NotImplementedException();
    public virtual Task CancelPdfAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public virtual Task ResetPdfCancelAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public virtual Task<string> SavePdfWithSettingsAsync(string sessionId, string outputPath, IReadOnlyDictionary<string, System.Text.Json.JsonElement> settings, CancellationToken ct) => SavePdfAsync(sessionId, outputPath, ct);

    public virtual Task<string> SavePdfOperationAsync(string sessionId, string outputPath,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> settings, bool copyExport, bool rebindTarget, bool overwrite, CancellationToken ct)
        => copyExport || rebindTarget || !overwrite ? Task.FromException<string>(new NotSupportedException()) : SavePdfWithSettingsAsync(sessionId, outputPath, settings, ct);

    public virtual Task ClosePdfSessionAsync(
        string sessionId,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
