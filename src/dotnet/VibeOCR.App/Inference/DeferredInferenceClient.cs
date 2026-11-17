// Deferred inference gateway used while the Supervisor is starting.
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.App.Inference;

public sealed class InferenceClientNotAttachedException(
  string message,
  Exception? innerException = null)
  : InvalidOperationException(message, innerException);

/// <summary>
/// T2 基线 smoke 专用 PDF 请求探针（Goal #325/#327）：只记录 App 侧
/// DeferredInferenceClient→Runtime 的真实 wire 请求（不含 worker 进程内
/// native 调用）。仅 smoke 安装；未安装时所有调用只多一次 volatile 读，
/// 无日志、无等待、不改 wire 语义。事件缓冲有界，溢出显式置标记由调用方
/// 失败，不静默截断。
/// </summary>
internal sealed class PdfSmokeRequestProbe
{
  internal const int MaxEvents = 20000;

  internal sealed record RequestEvent(
      string Kind, string Session, int Page, int Param,
      double StartedAtMs, double DurationMs, string Outcome, string? Error);

  private readonly object _lock = new();
  private readonly List<RequestEvent> _events = [];
  private bool _overflowed;
  private int _inflight;
  private int _maxInflight;

  internal int MaxInflight => Volatile.Read(ref _maxInflight);

  internal async Task<T> MeasureAsync<T>(
      string kind, string session, int page, int param, Func<Task<T>> call)
  {
    lock (_lock)
    {
      _inflight++;
      if (_inflight > _maxInflight) _maxInflight = _inflight;
    }
    long start = System.Diagnostics.Stopwatch.GetTimestamp();
    try
    {
      T result = await call();
      Record(kind, session, page, param, start, "completed", null);
      return result;
    }
    catch (OperationCanceledException error)
    {
      Record(kind, session, page, param, start, "cancelled", error.Message);
      throw;
    }
    catch (Exception error)
    {
      Record(kind, session, page, param, start, "faulted", error.GetType().Name + ": " + error.Message);
      throw;
    }
  }

  internal async Task MeasureAsync(
      string kind, string session, int page, int param, Func<Task> call) =>
    await MeasureAsync<object?>(kind, session, page, param, async () =>
    {
      await call();
      return null;
    });

  private void Record(
      string kind, string session, int page, int param, long start,
      string outcome, string? error)
  {
    long end = System.Diagnostics.Stopwatch.GetTimestamp();
    double durationMs = (end - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    double startedAtMs = start * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    lock (_lock)
    {
      _inflight--;
      if (_events.Count >= MaxEvents)
      {
        _overflowed = true;
        return;
      }
      _events.Add(new RequestEvent(
          kind, session, page, param, startedAtMs, durationMs, outcome, error));
    }
  }

  internal (IReadOnlyList<RequestEvent> Events, int MaxInflight, bool Overflowed) Snapshot()
  {
    lock (_lock) return (_events.ToArray(), Volatile.Read(ref _maxInflight), _overflowed);
  }
}

/// <summary>
/// An <see cref="IInferenceClient"/> whose calls delegate to an attached inner
/// client once <see cref="Attach"/> is called. While a Supervisor startup
/// attempt is pending (<see cref="MarkStartupPending"/>), calls wait for its
/// outcome instead of throwing; without one, every call fails fast.
/// </summary>
public sealed class DeferredInferenceClient(CancellationToken shutdownToken = default)
  : IInferenceClient
{
  private const string NotAttachedMessage =
    "The inference Supervisor client is not attached. "
    + "Wait for Supervisor startup to complete or inspect diagnostics.";

  private readonly SupervisorAttachGate _gate = new(NotAttachedMessage, shutdownToken);
  private readonly object _lock = new();
  private IInferenceClient? _inner;
  private int _submitAttempts;
  private string? _lastSubmittedJobId;

  public bool IsAttached => _gate.IsAttached;
  internal int SubmitAttempts => Volatile.Read(ref _submitAttempts);
  internal string? LastSubmittedJobId => Volatile.Read(ref _lastSubmittedJobId);

  /// <summary>
  /// T2 基线 smoke 观测窄缝：非 null 时仅包装 PDF 会话 wire 方法计数/计时；
  /// 正常路径恒为 null，零开销直通。不新增日志、IPC 或生产环境选项。
  /// 读写均走 Volatile，跨线程发布准确。
  /// </summary>
  private static PdfSmokeRequestProbe? _pdfSmokeProbe;

  internal static PdfSmokeRequestProbe? PdfSmokeProbe => Volatile.Read(ref _pdfSmokeProbe);

  internal static void SetPdfSmokeProbe(PdfSmokeRequestProbe? probe) =>
    Volatile.Write(ref _pdfSmokeProbe, probe);

  public Uri BaseUrl => Current.BaseUrl;

  public void Attach(IInferenceClient client)
  {
    ArgumentNullException.ThrowIfNull(client);
    lock (_lock)
    {
      _inner = client;
    }
    _gate.MarkAttached();
  }

  public void Detach(IInferenceClient client)
  {
    lock (_lock)
    {
      if (ReferenceEquals(_inner, client))
      {
        _inner = null;
        _gate.MarkDetached();
      }
    }
  }

  /// <summary>Announce an in-flight Supervisor startup attempt so unattached
  /// calls wait for its outcome instead of failing fast.</summary>
  public void MarkStartupPending() => _gate.MarkStartupPending();

  /// <summary>Release calls waiting on a failed startup attempt.</summary>
  public void MarkStartupFailed(Exception cause) => _gate.MarkStartupFailed(cause);

  private IInferenceClient Current =>
    Volatile.Read(ref _inner)
    ?? throw new InferenceClientNotAttachedException(NotAttachedMessage);

  public async Task<JobRef> SubmitAsync(
    SubmitRequest request,
    IReadOnlyDictionary<string, SubmitUpload> uploads,
    CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    Interlocked.Increment(ref _submitAttempts);
    JobRef job = await Current.SubmitAsync(request, uploads, cancellationToken);
    Volatile.Write(ref _lastSubmittedJobId, job.JobId);
    return job;
  }

  public async Task<JobUpdate> ObserveAsync(
    string jobId, int afterSequence, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.ObserveAsync(jobId, afterSequence, cancellationToken);
  }

  public async Task<JobCommandResult> CommandAsync(
    JobCommand command, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.CommandAsync(command, cancellationToken);
  }

  public async Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.GetResidencyAsync(cancellationToken);
  }

  public async Task<ResidencyStatus> PreloadRuntimeAsync(
    Wire.RuntimePreloadRequest request, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.PreloadRuntimeAsync(request, cancellationToken);
  }

  public async Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.GetRuntimeStatusAsync(cancellationToken);
  }

  public async Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.GetHealthAsync(cancellationToken);
  }

  public async Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.GetSettingsAsync(cancellationToken);
  }

  public async Task<SettingsSnapshot> UpdateSettingsAsync(
    SettingsSnapshot settings, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.UpdateSettingsAsync(settings, cancellationToken);
  }

  public async Task<ExportResult> ExportAsync(
    ExportRequest request, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.ExportAsync(request, cancellationToken);
  }

  public async Task<byte[]> FetchResultAssetAsync(
    string jobId, string itemId, string assetId, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken);
    return await Current.FetchResultAssetAsync(jobId, itemId, assetId, cancellationToken);
  }

  public async Task<PdfSessionOpenResult> OpenPdfSessionAsync(
    string path, string? password, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    PdfSmokeRequestProbe? probe = PdfSmokeProbe;
    if (probe is null) return await Current.OpenPdfSessionAsync(path, password, ct);
    return await probe.MeasureAsync("open_session", "", -1, 0,
        () => Current.OpenPdfSessionAsync(path, password, ct));
  }

  public async Task<byte[]> RenderPdfPageAsync(
    string sessionId, int page, int size, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    PdfSmokeRequestProbe? probe = PdfSmokeProbe;
    if (probe is null) return await Current.RenderPdfPageAsync(sessionId, page, size, ct);
    return await probe.MeasureAsync("render_thumbnail", sessionId, page, size,
        () => Current.RenderPdfPageAsync(sessionId, page, size, ct));
  }

  public async Task<PdfMutateResult> RotatePdfPagesAsync(
    string sessionId, int[] pages, int angle, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    return await Current.RotatePdfPagesAsync(sessionId, pages, angle, ct);
  }

  public async Task<PdfMutateResult> DeletePdfPagesAsync(
    string sessionId, int[] pages, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    return await Current.DeletePdfPagesAsync(sessionId, pages, ct);
  }

  public async Task<PdfMutateResult> InsertPdfBlankAsync(string sessionId, int afterIndex, double width, double height, CancellationToken ct) { await _gate.WaitAsync(ct); return await Current.InsertPdfBlankAsync(sessionId, afterIndex, width, height, ct); }
  public async Task<PdfMutateResult> InsertPdfFromAsync(string sessionId, string sourcePath, int afterIndex, CancellationToken ct) { await _gate.WaitAsync(ct); return await Current.InsertPdfFromAsync(sessionId, sourcePath, afterIndex, ct); }
  public async Task<PdfMutateResult> ReorderPdfAsync(string sessionId, int[] newOrder, CancellationToken ct) { await _gate.WaitAsync(ct); return await Current.ReorderPdfAsync(sessionId, newOrder, ct); }

  public async Task<string> SavePdfAsync(
    string sessionId, string outputPath, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    return await Current.SavePdfAsync(sessionId, outputPath, ct);
  }

  public async Task<VibeOCR.Runtime.Contracts.Generated.Wire.PdfDocumentMirror> GetPdfModelAsync(string sessionId, CancellationToken ct) { await _gate.WaitAsync(ct); return await Current.GetPdfModelAsync(sessionId, ct); }
  public async Task LoadPdfAsync(string sessionId, Action<System.Text.Json.JsonElement> progress, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    PdfSmokeRequestProbe? probe = PdfSmokeProbe;
    if (probe is null) { await Current.LoadPdfAsync(sessionId, progress, ct); return; }
    await probe.MeasureAsync("load_detect", sessionId, -1, 0,
        () => Current.LoadPdfAsync(sessionId, progress, ct));
  }
  public async Task<byte[]> RenderPdfPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    PdfSmokeRequestProbe? probe = PdfSmokeProbe;
    if (probe is null) return await Current.RenderPdfPreviewAsync(sessionId, page, dpi, ct);
    return await probe.MeasureAsync("render_preview", sessionId, page, dpi,
        () => Current.RenderPdfPreviewAsync(sessionId, page, dpi, ct));
  }
  public async Task<VibeOCR.Runtime.Contracts.Generated.Wire.PdfMutationResponse> UpdatePdfBlockTextAsync(string sessionId, VibeOCR.Runtime.Contracts.Generated.Wire.UpdateBlockTextRequest request, CancellationToken ct) { await _gate.WaitAsync(ct); return await Current.UpdatePdfBlockTextAsync(sessionId, request, ct); }
  public async Task<VibeOCR.Runtime.Contracts.Generated.Wire.PageInspectResponse> InspectPdfPageAsync(string sessionId, int page, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    PdfSmokeRequestProbe? probe = PdfSmokeProbe;
    if (probe is null) return await Current.InspectPdfPageAsync(sessionId, page, ct);
    return await probe.MeasureAsync("inspect_page", sessionId, page, 0,
        () => Current.InspectPdfPageAsync(sessionId, page, ct));
  }
  public async Task<VibeOCR.Runtime.Contracts.Generated.Wire.PdfMutationResponse> AddPdfTextLayersAsync(string sessionId, VibeOCR.Runtime.Contracts.Generated.Wire.BatchAddTextLayerRequest request, CancellationToken ct) { await _gate.WaitAsync(ct); return await Current.AddPdfTextLayersAsync(sessionId, request, ct); }
  public async Task DeletePdfTextLayersAsync(string sessionId, int[] pages, Action<System.Text.Json.JsonElement> progress, CancellationToken ct) { await _gate.WaitAsync(ct); await Current.DeletePdfTextLayersAsync(sessionId, pages, progress, ct); }
  public async Task CancelPdfAsync(string sessionId, CancellationToken ct) { await _gate.WaitAsync(ct); await Current.CancelPdfAsync(sessionId, ct); }
  public async Task ResetPdfCancelAsync(string sessionId, CancellationToken ct) { await _gate.WaitAsync(ct); await Current.ResetPdfCancelAsync(sessionId, ct); }
  public async Task<string> SavePdfOperationAsync(string sessionId, string outputPath, IReadOnlyDictionary<string, System.Text.Json.JsonElement> settings, bool copyExport, bool rebindTarget, bool overwrite, CancellationToken ct) { await _gate.WaitAsync(ct); return await Current.SavePdfOperationAsync(sessionId, outputPath, settings, copyExport, rebindTarget, overwrite, ct); }
  public async Task<string> SavePdfWithSettingsAsync(string sessionId, string outputPath, IReadOnlyDictionary<string, System.Text.Json.JsonElement> settings, CancellationToken ct) { await _gate.WaitAsync(ct); return await Current.SavePdfWithSettingsAsync(sessionId, outputPath, settings, ct); }

  public async Task ClosePdfSessionAsync(string sessionId, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    PdfSmokeRequestProbe? probe = PdfSmokeProbe;
    if (probe is null) { await Current.ClosePdfSessionAsync(sessionId, ct); return; }
    await probe.MeasureAsync("close_session", sessionId, -1, 0,
        () => Current.ClosePdfSessionAsync(sessionId, ct));
  }

  public ValueTask DisposeAsync()
  {
    IInferenceClient? inner = Interlocked.Exchange(ref _inner, null);
    _gate.MarkDetached();
    if (inner is not null)
    {
      return inner.DisposeAsync();
    }

    return ValueTask.CompletedTask;
  }
}
