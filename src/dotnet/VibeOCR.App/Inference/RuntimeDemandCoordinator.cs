using System.Runtime.ExceptionServices;
using VibeOCR.App.Features.Maintenance;

namespace VibeOCR.App.Inference;

/// <summary>
/// Owns one preparation/connection attempt. Callers cancel their own wait;
/// the final departing waiter cancels the shared attempt and awaits its cleanup.
/// Passive gateway reads never create demand.
/// </summary>
internal sealed class RuntimeDemandCoordinator(
  Func<bool, CancellationToken, Task> connect,
  Func<Task> disconnect,
  ProductMaintenanceCoordinator maintenance,
  CancellationToken shutdown,
  ManagedRuntimeConnection? connection = null,
  bool shellOnly = false)
{
  public ManagedRuntimeConnection? Connection { get; } = connection;
  private readonly object gate = new();
  private Attempt? current;
  private bool ready;
  private bool enabled;
  private bool paused;
  private long transition;
  private sealed class Attempt(CancellationToken shutdown, bool initialize, bool borrowLease)
  {
    public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly bool Initialize = initialize;
    public readonly bool BorrowLease = borrowLease;
    public int Waiters;
    public bool Committed;
    public volatile bool LifecycleCancellationRequested;
    public Exception? CleanupFailure;
    public readonly List<CancellationToken> WaiterTokens = [];
  }

  public Task EnsureReadyAsync(CancellationToken cancellationToken) => WaitAsync(true, cancellationToken);

  private async Task WaitAsync(bool initialize, CancellationToken cancellationToken, bool borrowLease = false)
  {
    if (shellOnly) throw new InvalidOperationException("外壳模式不启动识别服务（--shell-only）。");
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      shutdown.ThrowIfCancellationRequested();
      Attempt attempt;
      bool start = false;
      bool draining;
      lock (gate)
      {
        if (paused) throw new InvalidOperationException("运行环境维护尚未结束，识别服务保持暂停。");
        if (ready && current is null) return;
        if (current is null)
        {
          current = new Attempt(shutdown, initialize, borrowLease);
          start = true;
        }
        attempt = current;
        draining = attempt.Cancellation.IsCancellationRequested;
        if (!draining)
        {
          attempt.Waiters++;
          attempt.WaiterTokens.Add(cancellationToken);
        }
      }
      if (draining)
      {
        // A new demand waits for the previous owner's rollback/disposal before
        // starting its own attempt. Cancellation of that old owner is expected.
        using var drainingCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancellationToken);
        try { await attempt.Completion.Task.WaitAsync(drainingCancellation.Token); }
        catch (Exception) when (attempt.Completion.Task.IsCompleted)
        {
          cancellationToken.ThrowIfCancellationRequested();
          shutdown.ThrowIfCancellationRequested();
          if (attempt.CleanupFailure is { } cleanup) ExceptionDispatchInfo.Capture(cleanup).Throw();
          // Preparation failure belongs to the departing demand, not this one.
        }
        continue;
      }
      using CancellationTokenRegistration registration = cancellationToken.Register(() =>
      {
        lock (gate)
        {
          if (!attempt.Committed && !attempt.Completion.Task.IsCompleted && attempt.WaiterTokens.All(token => token.IsCancellationRequested))
            attempt.Cancellation.Cancel();
        }
      });
      if (start) _ = RunAsync(attempt);
      try
      {
        cancellationToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancellationToken);
        await attempt.Completion.Task.WaitAsync(linked.Token);
        lock (gate)
        {
          if (!ready) throw new InvalidOperationException("产品识别服务未就绪。");
        }
        return;
      }
      finally
      {
        bool drain = false;
        lock (gate)
        {
          attempt.Waiters--;
          attempt.WaiterTokens.Remove(cancellationToken);
          bool lifecycleCancellation = shutdown.IsCancellationRequested || attempt.LifecycleCancellationRequested ||
            paused || !ReferenceEquals(current, attempt);
          if (attempt.Waiters == 0 && !attempt.Completion.Task.IsCompleted &&
              (lifecycleCancellation || !attempt.Committed))
          {
            attempt.Cancellation.Cancel();
            drain = true;
          }
        }
        if (drain)
        {
          try { await attempt.Completion.Task; }
          catch (Exception) { /* The caller's original cancellation/failure wins. */ }
        }
      }
    }
  }

  private async Task RunAsync(Attempt attempt)
  {
    IDisposable? lease = null;
    Exception? failure = null;
    try
    {
      // This lease spans initialization, attach and catalog publication. Update
      // or environment maintenance cannot enter the old prepare/attach gap.
      if (attempt.BorrowLease)
      {
        if (maintenance.State.ActiveOwner != ProductMaintenanceOwner.RuntimeMaintenance)
          throw new InvalidOperationException("恢复连接缺少维护操作归属。");
      }
      else
        lease = maintenance.Acquire(ProductMaintenanceOwner.RuntimeMaintenance, () =>
        {
          attempt.LifecycleCancellationRequested = true;
          attempt.Cancellation.Cancel();
        });
      await connect(attempt.Initialize, attempt.Cancellation.Token);
      lock (gate)
      {
        shutdown.ThrowIfCancellationRequested();
        if (attempt.LifecycleCancellationRequested) throw new OperationCanceledException(attempt.Cancellation.Token);
        attempt.Cancellation.Token.ThrowIfCancellationRequested();
        if (paused || !ReferenceEquals(current, attempt) ||
            attempt.WaiterTokens.All(token => token.IsCancellationRequested))
          throw new OperationCanceledException(attempt.Cancellation.Token);
        // This is the connection commit point. Cancellation after it only ends
        // that caller's wait. Keep the attempt published until its lease is
        // released; even reentrant demand must await the complete transition.
        attempt.Committed = true;
        ready = Connection is null || Connection.Session is not null;
        enabled |= ready;
      }
    }
    catch (Exception error)
    {
      // Acquisition failure does not own the competing operation's connection.
      try
      {
        if (lease is not null || attempt.BorrowLease)
          await disconnect();
      }
      catch (Exception cleanup)
      {
        attempt.CleanupFailure = cleanup;
        error = new AggregateException(error, cleanup);
      }
      failure = error;
    }
    finally
    {
      lease?.Dispose();
      lock (gate)
      {
        if (failure is not null) ready = false;
        if (ReferenceEquals(current, attempt)) current = null;
        if (failure is OperationCanceledException)
          attempt.Completion.TrySetCanceled(attempt.Cancellation.Token);
        else if (failure is not null)
          attempt.Completion.TrySetException(failure);
        else if (!attempt.Completion.Task.IsCompleted)
          attempt.Completion.TrySetResult();
        attempt.Cancellation.Dispose();
      }
      // Every attempt has a consumer except after all waiters cancel. Observe
      // failure here too, while preserving the original outcome for consumers.
      _ = attempt.Completion.Task.Exception;
    }
  }

  internal sealed record PauseOperation(long Owner, Task Completion);

  public PauseOperation BeginPause(CancellationToken cancellationToken)
  {
    Attempt? pending;
    long owner;
    lock (gate)
    {
      paused = true;
      owner = ++transition;
      ready = false;
      pending = current;
      pending?.Cancellation.Cancel();
    }
    return new PauseOperation(owner, DrainPauseAsync(owner, pending, cancellationToken));
  }

  public async Task<long> PauseAsync(CancellationToken cancellationToken)
  {
    PauseOperation pause = BeginPause(cancellationToken);
    await pause.Completion;
    return pause.Owner;
  }

  private async Task DrainPauseAsync(long owner, Attempt? pending, CancellationToken cancellationToken)
  {
    Exception? cleanupFailure = null;
    if (pending is not null)
    {
      try { await pending.Completion.Task.WaitAsync(cancellationToken); }
      catch (Exception) when (pending.Completion.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
      {
        // The old attempt reports business failure to its own callers. Its
        // resource cleanup failure also belongs to this pause and must survive
        // a second disconnect after the connection already published null.
        cleanupFailure = pending.CleanupFailure;
      }
    }
    try
    {
      Task disconnecting;
      lock (gate)
      {
        // A superseding transition owns its own connection. Start disposal
        // under the same gate as this ownership check, before it can activate.
        disconnecting = owner == transition ? disconnect() : Task.CompletedTask;
      }
      await disconnecting;
    }
    catch (Exception error) when (cleanupFailure is not null)
    {
      throw new AggregateException(cleanupFailure, error);
    }
    if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
  }

  public Task RestoreAsync(long owner, bool maintenanceLeaseHeld = false)
  {
    lock (gate)
    {
      // The caller carries its own pause ticket; a delayed restore cannot
      // unpause a newer maintenance operation or application stop.
      if (owner != transition || !paused || shutdown.IsCancellationRequested) return Task.CompletedTask;
      paused = false;
      return enabled ? WaitAsync(false, shutdown, maintenanceLeaseHeld) : Task.CompletedTask;
    }
  }

  public async Task RecoverAsync(Func<bool>? isCurrentSession = null, Action? recovering = null,
    Action<Exception>? recoveryFailed = null)
  {
    long? reportingOwner = null;
    try
    {
      PauseOperation pause;
      if (isCurrentSession is not null)
      {
        long observedOwner;
        Attempt? observedAttempt;
        lock (gate)
        {
          shutdown.ThrowIfCancellationRequested();
          if (!isCurrentSession()) return;
          reportingOwner = observedOwner = transition;
          observedAttempt = current;
          if (paused) return;
        }
        if (observedAttempt is not null)
        {
          try { await observedAttempt.Completion.Task.WaitAsync(shutdown); }
          catch (Exception) when (observedAttempt.Completion.Task.IsCompleted)
          {
            shutdown.ThrowIfCancellationRequested();
            if (observedAttempt.CleanupFailure is { } cleanup) ExceptionDispatchInfo.Capture(cleanup).Throw();
          }
        }
        lock (gate)
        {
          shutdown.ThrowIfCancellationRequested();
          if (observedOwner != transition || paused || current is not null || !isCurrentSession()) return;
          recovering?.Invoke();
          pause = BeginPause(shutdown);
        }
      }
      else
        pause = BeginPause(shutdown);
      reportingOwner = pause.Owner;
      await pause.Completion;
      Task recovery;
      lock (gate)
      {
        shutdown.ThrowIfCancellationRequested();
        if (pause.Owner != transition) return;
        paused = false;
        recovery = WaitAsync(false, shutdown);
      }
      await recovery;
    }
    catch (Exception error)
    {
      if (!(error is OperationCanceledException && shutdown.IsCancellationRequested))
      {
        lock (gate)
        {
          // Our disconnect may already have published null. Report by the
          // accepted transition, not the departed session's continued presence.
          if (reportingOwner == transition && current is null &&
              (isCurrentSession is null || isCurrentSession() || Connection?.Session is null))
            recoveryFailed?.Invoke(error);
        }
      }
      throw;
    }
  }

  public async Task StopAsync()
  {
    await PauseAsync(CancellationToken.None);
  }
}
