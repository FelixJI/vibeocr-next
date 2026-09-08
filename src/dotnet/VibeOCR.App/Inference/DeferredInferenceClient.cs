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

  public bool IsAttached => _gate.IsAttached;

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
    return await Current.SubmitAsync(request, uploads, cancellationToken);
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

  public async Task<PdfSessionOpenResult> OpenPdfSessionAsync(
    string path, string? password, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    return await Current.OpenPdfSessionAsync(path, password, ct);
  }

  public async Task<byte[]> RenderPdfPageAsync(
    string sessionId, int page, int size, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    return await Current.RenderPdfPageAsync(sessionId, page, size, ct);
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

  public async Task<string> SavePdfAsync(
    string sessionId, string outputPath, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    return await Current.SavePdfAsync(sessionId, outputPath, ct);
  }

  public async Task ClosePdfSessionAsync(string sessionId, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    await Current.ClosePdfSessionAsync(sessionId, ct);
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
