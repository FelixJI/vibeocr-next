// Deferred QR client mirroring DeferredInferenceClient: waits while a
// Supervisor startup attempt is pending, throws until Attach otherwise.
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Inference;

public sealed class DeferredQrCodeClient(CancellationToken shutdownToken = default)
  : IQrCodeClient
{
  private const string NotAttachedMessage =
    "The QR Supervisor client is not attached. "
    + "Wait for Supervisor startup to complete or inspect diagnostics.";

  private readonly SupervisorAttachGate _gate = new(NotAttachedMessage, shutdownToken);
  private readonly object _lock = new();
  private IQrCodeClient? _inner;

  public bool IsAttached => _gate.IsAttached;

  public void Attach(IQrCodeClient client)
  {
    ArgumentNullException.ThrowIfNull(client);
    lock (_lock) _inner = client;
    _gate.MarkAttached();
  }

  public void Detach(IQrCodeClient client)
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

  private IQrCodeClient Current =>
    Volatile.Read(ref _inner)
    ?? throw new InferenceClientNotAttachedException(NotAttachedMessage);

  public async Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
    string base64Image, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    return await Current.DecodeAsync(base64Image, ct);
  }

  public async Task<QrCodeGeneratedImage> GenerateAsync(
    string data, string format, CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    return await Current.GenerateAsync(data, format, ct);
  }

  public ValueTask DisposeAsync()
  {
    IQrCodeClient? inner = Interlocked.Exchange(ref _inner, null);
    _gate.MarkDetached();
    return inner?.DisposeAsync() ?? ValueTask.CompletedTask;
  }
}
