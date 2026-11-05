using VibeOCR.App.ViewModels;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Inference;

/// <summary>Owns the single active session, its clients and Supervisor Job.
/// Runtime remains authoritative for environment selection and switch commit.</summary>
internal sealed class ManagedRuntimeConnection(
  PortableLayout layout,
  RuntimeStatusViewModel status,
  Action<ProductRuntimeSession?, ProductRuntimeSession?> publish)
{
  private readonly SemaphoreSlim gate = new(1, 1);
  public ProductRuntimeSession? Session { get; private set; }
  public event Action<string>? Milestone;

  private async Task EnterAsync(CancellationToken cancellationToken)
  {
    using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    bound.CancelAfter(TimeSpan.FromSeconds(120));
    try { await gate.WaitAsync(bound.Token); }
    catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
    {
      throw new InvalidOperationException("等待运行环境生命周期门超时（120 秒），连接尝试未能开始。", error);
    }
  }

  public async Task<bool> ConnectAsync(CancellationToken cancellationToken, bool injectSoakCrash = false)
  {
    await EnterAsync(cancellationToken);
    try
    {
      if (Session is not null) return true;
      Milestone?.Invoke("T3");
      AppLog.Info("Runtime demand: starting product Supervisor");
      Milestone?.Invoke("T4");
      ProductRuntimeSession session = await ProductRuntimeSession.StartAsync(layout, cancellationToken, injectSoakCrash);
      Publish(session);
      status.ApplySnapshot(session.Status);
      Milestone?.Invoke("T5");
      return Session is not null;
    }
    finally { gate.Release(); }
  }

  private void Publish(ProductRuntimeSession? next)
  {
    ProductRuntimeSession? previous = Session;
    Session = next;
    publish(previous, next);
  }

  public async Task DisconnectAsync()
  {
    await EnterAsync(CancellationToken.None);
    try
    {
      if (Session is not { } session) return;
      Publish(null);
      AppLog.Info("Runtime demand: disconnect begin");
      await session.DisposeAsync();
    }
    finally { gate.Release(); }
  }
}
