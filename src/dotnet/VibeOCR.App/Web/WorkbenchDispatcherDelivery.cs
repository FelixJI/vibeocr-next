namespace VibeOCR.App.Web;

internal sealed class WorkbenchDispatcherDelivery
{
  private Task pending = Task.CompletedTask;
  private readonly SemaphoreSlim gate = new(1, 1);

  internal async Task PostAsync(Func<Action, bool> enqueue, Action publish, CancellationToken cancellationToken)
  {
    // 新 session 也必须等待旧 callback 离开原生队列，不能靠取消把积压转移到 dispatcher。
    await gate.WaitAsync(cancellationToken);
    try
    {
      await pending.WaitAsync(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      pending = occupied.Task;
      try
      {
        if (!enqueue(() =>
        {
          try { if (!cancellationToken.IsCancellationRequested) publish(); completion.TrySetResult(); }
          catch (Exception error) { completion.TrySetException(error); }
          finally { occupied.TrySetResult(); }
        })) throw new InvalidOperationException("Workbench dispatcher is unavailable.");
      }
      catch (Exception error)
      {
        occupied.TrySetResult();
        completion.TrySetException(error);
      }
      await completion.Task.WaitAsync(cancellationToken);
    }
    finally { gate.Release(); }
  }
}
