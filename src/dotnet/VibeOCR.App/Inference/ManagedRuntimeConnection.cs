using VibeOCR.App.ViewModels;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Inference;

/// <summary>Owns the single active session, its clients and Supervisor Job.
/// Runtime remains authoritative for environment selection and switch commit.</summary>
internal sealed class ManagedRuntimeConnection(
  IManagedEnvironmentClient manager,
  PortableLayout layout,
  RuntimeStatusViewModel status,
  Action<ManagedEnvironmentSession?, ManagedEnvironmentSession?> publish)
{
  private readonly SemaphoreSlim gate = new(1, 1);
  public ManagedEnvironmentSession? Session { get; private set; }
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

  public async Task<bool> ConnectAsync(bool initialize, CancellationToken cancellationToken, bool injectSoakCrash = false)
  {
    await EnterAsync(cancellationToken);
    try
    {
      if (Session is not null) return true;
      ManagedEnvironmentList environments;
      if (initialize)
      {
        AppLog.Info("Runtime demand: initialize_default begin");
        environments = await manager.InitializeDefaultEnvironmentAsync(cancellationToken);
      }
      else
        environments = await manager.ListEnvironmentsAsync(cancellationToken);
      Milestone?.Invoke("T3");
      Milestone?.Invoke("T4");
      if (environments.ActiveId is null) return false;
      await ActivateCoreAsync(environments.ActiveId, cancellationToken, injectSoakCrash);
      Milestone?.Invoke("T5");
      return Session is not null;
    }
    finally { gate.Release(); }
  }

  public async Task SwitchAsync(string environmentId, CancellationToken cancellationToken, Action? committed = null)
  {
    await EnterAsync(cancellationToken);
    try { await ActivateCoreAsync(environmentId, cancellationToken, committed: committed); }
    finally { gate.Release(); }
  }

  private async Task ActivateCoreAsync(string environmentId, CancellationToken cancellationToken, bool injectSoakCrash = false, Action? committed = null)
  {
    AppLog.Info("Runtime demand: activate begin");
    var coordinator = new ManagedEnvironmentSwitchCoordinator(manager);
    ManagedEnvironmentSession? session = await coordinator.SwitchAsync(
      environmentId, Session, next =>
      {
        // Platform invokes this only after Runtime reservation commit succeeds.
        // Notify before App publication callbacks can cancel the command.
        committed?.Invoke();
        Publish(next);
      },
      Path.Combine(layout.DataRoot, "supervisor.log"), TimeSpan.FromSeconds(90),
      RuntimeCapabilityRequirements.Read(layout.ComponentLock), cancellationToken, injectSoakCrash);
    if (session is null) status.ReportServiceUnavailable();
    else status.ApplySnapshot(session.Status);
  }

  private void Publish(ManagedEnvironmentSession? next)
  {
    ManagedEnvironmentSession? previous = Session;
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
