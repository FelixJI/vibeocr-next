// Attach-state machine shared by the deferred Supervisor gateways.
namespace VibeOCR.App.Inference;

/// <summary>
/// Tracks whether a deferred Supervisor gateway can serve calls now, must fail
/// fast, or is waiting for an in-flight startup attempt. While an attempt is
/// pending, unattached calls wait for its outcome instead of throwing, so
/// commands issued during Supervisor startup complete once the client
/// attaches; a failed attempt releases waiters with its cause, and the
/// shutdown token releases them as cancelled.
/// </summary>
internal sealed class SupervisorAttachGate
{
  private readonly string _notAttachedMessage;
  private readonly CancellationToken _shutdown;
  private readonly object _lock = new();
  private TaskCompletionSource? _pending;
  private bool _attached;

  public SupervisorAttachGate(string notAttachedMessage, CancellationToken shutdown = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(notAttachedMessage);
    _notAttachedMessage = notAttachedMessage;
    _shutdown = shutdown;
  }

  public bool IsAttached
  {
    get { lock (_lock) return _attached; }
  }

  /// <summary>Announce an in-flight startup attempt so calls wait for it.</summary>
  public void MarkStartupPending()
  {
    lock (_lock)
    {
      if (!_attached)
      {
        _pending ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      }
    }
  }

  public void MarkAttached()
  {
    lock (_lock)
    {
      _attached = true;
      TaskCompletionSource? pending = _pending;
      _pending = null;
      pending?.TrySetResult();
    }
  }

  public void MarkDetached()
  {
    lock (_lock)
    {
      _attached = false;
      _pending = null;
    }
  }

  /// <summary>
  /// Resolve the pending startup attempt as failed: waiting calls observe the
  /// failure cause and later calls fail fast again. During shutdown the
  /// waiters are released as cancelled instead.
  /// </summary>
  public void MarkStartupFailed(Exception cause)
  {
    ArgumentNullException.ThrowIfNull(cause);
    lock (_lock)
    {
      TaskCompletionSource? pending = _pending;
      _pending = null;
      _attached = false;
      if (pending is null)
      {
        return;
      }
      if (_shutdown.IsCancellationRequested)
      {
        pending.TrySetCanceled(_shutdown);
      }
      else
      {
        pending.TrySetException(new InferenceClientNotAttachedException(
          $"Supervisor startup failed: {cause.Message}", cause));
      }
    }
  }

  /// <summary>
  /// Wait until the gateway is attached. Fails fast when no startup attempt
  /// is pending; otherwise observes the attempt outcome (attach, failure, or
  /// shutdown/caller cancellation).
  /// </summary>
  public async Task WaitAsync(CancellationToken cancellationToken)
  {
    Task waiter;
    lock (_lock)
    {
      if (_attached)
      {
        return;
      }
      waiter = _pending?.Task
        ?? Task.FromException(new InferenceClientNotAttachedException(_notAttachedMessage));
    }
    using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
      _shutdown,
      cancellationToken);
    await waiter.WaitAsync(linked.Token);
  }
}
