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

  private sealed class Attempt(CancellationToken shutdown, bool initialize, bool borrowLease,
    Func<CancellationToken, Task>? activate)
  {
    public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly bool Initialize = initialize;
    public readonly bool BorrowLease = borrowLease;
    public readonly Func<CancellationToken, Task>? Activate = activate;
    public int Waiters;
    public bool Committed;
    public readonly List<CancellationToken> WaiterTokens = [];
  }

  public Task EnsureReadyAsync(CancellationToken cancellationToken) => WaitAsync(true, cancellationToken);

  private async Task WaitAsync(bool initialize, CancellationToken cancellationToken, bool borrowLease = false,
    Func<CancellationToken, Task>? activate = null)
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
        if (ready && current is null && activate is null) return;
        if (current is null)
        {
          current = new Attempt(shutdown, initialize, borrowLease, activate);
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
        try { await attempt.Completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested) { }
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
        if (activate is null)
        {
          lock (gate)
          {
            if (!ready) throw new InvalidOperationException("当前运行环境未提供识别服务，请选择可用环境后重试。");
          }
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
          if (attempt.Waiters == 0 && !attempt.Committed && !attempt.Completion.Task.IsCompleted)
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
    bool activated = false;
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
        lease = maintenance.Acquire(ProductMaintenanceOwner.RuntimeMaintenance, attempt.Cancellation.Cancel);
      if (attempt.Activate is not null) await attempt.Activate(attempt.Cancellation.Token);
      else await connect(attempt.Initialize, attempt.Cancellation.Token);
      activated = true;
      lock (gate)
      {
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
        // The Platform switch transaction preserves the previous session on
        // activation failure. Only a committed candidate belongs to this attempt.
        if ((lease is not null || attempt.BorrowLease) && (attempt.Activate is null || activated))
          await disconnect();
      }
      catch (Exception cleanup) { error = new AggregateException(error, cleanup); }
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

  public async Task PauseAsync(CancellationToken cancellationToken)
  {
    Task? pending;
    lock (gate)
    {
      paused = true;
      transition++;
      ready = false;
      current?.Cancellation.Cancel();
      pending = current?.Completion.Task;
    }
    if (pending is not null)
    {
      try { await pending.WaitAsync(cancellationToken); }
      catch (Exception) when (pending.IsCompleted && !cancellationToken.IsCancellationRequested)
      {
        // The old attempt reports its failure to its own callers. Pausing still
        // owns disconnecting any preserved session; its cleanup failure propagates.
      }
    }
    await disconnect();
  }

  public Task RestoreAsync(bool maintenanceLeaseHeld = false)
  {
    bool resume;
    lock (gate) { paused = false; resume = enabled; }
    return resume && !shutdown.IsCancellationRequested ? WaitAsync(false, shutdown, maintenanceLeaseHeld) : Task.CompletedTask;
  }

  public async Task RecoverAsync()
  {
    await PauseAsync(shutdown);
    lock (gate) { paused = false; }
    await WaitAsync(false, shutdown);
  }

  public async Task SwitchAsync(Func<CancellationToken, Task> activate, CancellationToken cancellationToken)
  {
    Task? pending;
    long owner;
    lock (gate)
    {
      paused = true;
      owner = ++transition;
      ready = false;
      current?.Cancellation.Cancel();
      pending = current?.Completion.Task;
    }
    try
    {
      if (pending is not null)
      {
        try { await pending.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
      }
      Task switching;
      lock (gate)
      {
        if (owner != transition) throw new OperationCanceledException(cancellationToken);
        paused = false;
        switching = WaitAsync(false, cancellationToken, activate: activate);
      }
      await switching;
    }
    catch
    {
      lock (gate)
      {
        // A cancelled switch must not leave the preserved old session paused,
        // or undo a newer maintenance/shutdown owner's pause.
        if (owner == transition)
        {
          paused = false;
          ready = Connection?.Session is not null;
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
