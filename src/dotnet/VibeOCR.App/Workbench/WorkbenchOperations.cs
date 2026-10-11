namespace VibeOCR.App.Workbench;

// Shared application lifetime and tracked publication, without feature state or dispatch.
internal sealed class WorkbenchOperations
{
  private readonly CancellationTokenSource lifetime = new();
  private readonly HashSet<Task> tracked = [];
  private int disposed;
  internal event Action<WorkbenchState>? StateChanged;
  internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
  internal CancellationToken Token => lifetime.Token;
  internal void Publish(WorkbenchState state) => StateChanged?.Invoke(state);
  internal void PublishTerminal(WorkbenchState state) => Publish(state with { IsTerminal = true });
  internal TState? Start<TState>(TState start, Func<Task> operation) where TState : WorkbenchState
  {
    Publish(start); Track(operation()); return null;
  }
  internal void Track(Task operation)
  {
    lock (tracked) tracked.Add(operation);
    _ = operation.ContinueWith(completed => { lock (tracked) tracked.Remove(completed); },
      CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
  }
  internal bool BeginDispose()
  {
    if (Interlocked.Exchange(ref disposed, 1) != 0) return false;
    lifetime.Cancel(); return true;
  }
  internal Task DrainAsync()
  {
    lock (tracked) return Task.WhenAll(tracked.ToArray());
  }
  internal void Release() => lifetime.Dispose();
}
