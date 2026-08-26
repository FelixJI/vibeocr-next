using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace VibeOCR.App.Workbench;

public sealed class WorkbenchApplication : IWorkbenchApplication
{
  private const int HistoryLimit = 256;
  private readonly object _gate = new();
  private readonly HashSet<string> _capabilities;
  private readonly List<WorkbenchStateEnvelope> _history = [];
  private readonly Dictionary<string, WorkbenchStateEnvelope> _snapshots =
    new(StringComparer.Ordinal);
  private readonly HashSet<Channel<WorkbenchStateEnvelope>> _subscriptions = [];
  private readonly IWorkbenchCommandHandler? _commandHandler;
  private WorkbenchRoute _route;
  private long _revision;
  private bool _disposed;
  private bool _initialStatesLoaded;

  public WorkbenchApplication(
    IEnumerable<string> capabilities,
    WorkbenchRoute initialRoute,
    IWorkbenchCommandHandler? commandHandler = null)
  {
    ArgumentNullException.ThrowIfNull(capabilities);
    _capabilities = capabilities.ToHashSet(StringComparer.Ordinal);
    _route = initialRoute;
    _commandHandler = commandHandler;
    _snapshots["shell"] = ShellState(0);
    if (commandHandler is IWorkbenchStateSource stateSource)
    {
      stateSource.StateChanged += OnHandlerStateChanged;
    }
  }

  public async ValueTask<WorkbenchBootstrap> BootstrapAsync(
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (_commandHandler is IWorkbenchBootstrapSource bootstrapSource)
    {
      await bootstrapSource.PrepareBootstrapAsync(cancellationToken);
    }
    lock (_gate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      EnsureInitialStates();
      return new WorkbenchBootstrap(
        WorkbenchProtocol.Version,
        Guid.NewGuid(),
        _revision,
        _route,
        _snapshots.Values.OrderBy(state => state.Scope).ToArray(),
        _capabilities);
    }
  }

  public async ValueTask<WorkbenchCommandReceipt> ExecuteAsync(
    WorkbenchCommandEnvelope envelope,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(envelope);
    cancellationToken.ThrowIfCancellationRequested();
    lock (_gate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (envelope.Command is NavigateWorkbenchCommand navigate)
      {
        _route = navigate.Route;
        _revision++;
        WorkbenchStateEnvelope state = ShellState(_revision);
        RecordAndPublish(state);
        return new WorkbenchCommandReceipt(
          envelope.Id,
          _revision,
          null);
      }
    }

    if (_commandHandler is null)
    {
      return Unsupported(envelope.Id);
    }

    WorkbenchCommandOutcome outcome = await _commandHandler.ExecuteAsync(
      envelope.Command,
      cancellationToken);
    lock (_gate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (outcome.Error is not null)
      {
        return new WorkbenchCommandReceipt(envelope.Id, _revision, outcome.Error);
      }
      foreach (WorkbenchState next in outcome.States)
      {
        _revision++;
        RecordAndPublish(new WorkbenchStateEnvelope(
          _revision,
          next.Scope,
          WorkbenchStateChange.Replace,
          next));
      }
      return new WorkbenchCommandReceipt(envelope.Id, _revision, null);
    }
  }

  public async IAsyncEnumerable<WorkbenchStateEnvelope> SubscribeAsync(
    long afterRevision,
    [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    Channel<WorkbenchStateEnvelope> channel = Channel.CreateUnbounded<WorkbenchStateEnvelope>(
      new UnboundedChannelOptions
      {
        SingleReader = true,
        SingleWriter = false,
      });
    WorkbenchStateEnvelope[] replay;
    lock (_gate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      replay = _history.Where(state => state.Revision > afterRevision).ToArray();
      _subscriptions.Add(channel);
    }

    try
    {
      foreach (WorkbenchStateEnvelope state in replay)
      {
        yield return state;
      }
      await foreach (WorkbenchStateEnvelope state in channel.Reader.ReadAllAsync(cancellationToken))
      {
        yield return state;
      }
    }
    finally
    {
      lock (_gate)
      {
        _subscriptions.Remove(channel);
      }
    }
  }

  public async ValueTask DisposeAsync()
  {
    lock (_gate)
    {
      if (_disposed) return;
      _disposed = true;
      if (_commandHandler is IWorkbenchStateSource stateSource)
      {
        stateSource.StateChanged -= OnHandlerStateChanged;
      }
      foreach (Channel<WorkbenchStateEnvelope> channel in _subscriptions)
      {
        channel.Writer.TryComplete();
      }
      _subscriptions.Clear();
    }
    if (_commandHandler is IAsyncDisposable disposable)
    {
      await disposable.DisposeAsync();
    }
  }

  private WorkbenchStateEnvelope ShellState(long revision) => new(
    revision,
    "shell",
    WorkbenchStateChange.Replace,
    new ShellWorkbenchState(_route));

  private WorkbenchCommandReceipt Unsupported(Guid id) => new(
    id,
    _revision,
    new WorkbenchProblem(
      "unsupported_command",
      WorkbenchProblemCategory.InvalidCommand,
      false,
      "workbench.error.unsupportedCommand"));

  private void RecordAndPublish(WorkbenchStateEnvelope state)
  {
    _snapshots[state.Scope] = state;
    _history.Add(state);
    if (_history.Count > HistoryLimit) _history.RemoveAt(0);
    foreach (Channel<WorkbenchStateEnvelope> channel in _subscriptions)
    {
      channel.Writer.TryWrite(state);
    }
  }

  private void OnHandlerStateChanged(WorkbenchState state)
  {
    lock (_gate)
    {
      if (_disposed) return;
      _revision++;
      RecordAndPublish(new WorkbenchStateEnvelope(
        _revision,
        state.Scope,
        WorkbenchStateChange.Replace,
        state));
    }
  }

  private void EnsureInitialStates()
  {
    if (_initialStatesLoaded) return;
    _initialStatesLoaded = true;
    if (_commandHandler is not IWorkbenchStateSource stateSource) return;
    foreach (WorkbenchState state in stateSource.InitialStates)
    {
      _snapshots.TryAdd(
        state.Scope,
        new WorkbenchStateEnvelope(
          _revision,
          state.Scope,
          WorkbenchStateChange.Replace,
          state));
    }
  }
}
