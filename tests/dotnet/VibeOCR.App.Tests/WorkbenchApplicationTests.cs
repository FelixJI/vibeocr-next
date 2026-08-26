using VibeOCR.App.Workbench;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WorkbenchApplicationTests
{
  [Fact]
  public async Task NavigateCommandPublishesOneRevisionedShellState()
  {
    await using var application = new WorkbenchApplication(
      ["recognition.capture"],
      WorkbenchRoute.Recognition);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

    WorkbenchBootstrap bootstrap = await application.BootstrapAsync(timeout.Token);
    await using IAsyncEnumerator<WorkbenchStateEnvelope> updates = application
      .SubscribeAsync(bootstrap.Revision, timeout.Token)
      .GetAsyncEnumerator(timeout.Token);
    ValueTask<bool> next = updates.MoveNextAsync();

    Guid commandId = Guid.NewGuid();
    WorkbenchCommandReceipt receipt = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        commandId,
        new NavigateWorkbenchCommand(WorkbenchRoute.Pdf)),
      timeout.Token);

    Assert.True(await next);
    Assert.Equal(commandId, receipt.Id);
    Assert.Null(receipt.Error);
    Assert.Equal(receipt.Revision, updates.Current.Revision);
    Assert.Equal("shell", updates.Current.Scope);
    Assert.Equal(WorkbenchStateChange.Replace, updates.Current.Change);
    ShellWorkbenchState shell = Assert.IsType<ShellWorkbenchState>(updates.Current.State);
    Assert.Equal(WorkbenchRoute.Pdf, shell.Route);
  }

  [Fact]
  public async Task FeatureCommandPublishesOnlyHandlerOwnedTypedState()
  {
    var handler = new StubCommandHandler();
    await using var application = new WorkbenchApplication(
      ["recognition.capture"],
      WorkbenchRoute.Recognition,
      handler);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var states = application
      .SubscribeAsync(0, cancellation.Token)
      .GetAsyncEnumerator(cancellation.Token);

    WorkbenchCommandReceipt receipt = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        Guid.NewGuid(),
        new CaptureRecognitionScreenCommand()),
      cancellation.Token);

    Assert.True(receipt.Ok);
    Assert.True(await states.MoveNextAsync());
    Assert.Equal("recognition", states.Current.Scope);
    RecognitionWorkbenchState state = Assert.IsType<RecognitionWorkbenchState>(
      states.Current.State);
    Assert.True(state.IsBusy);
    Assert.Equal("recognition.running", state.StatusCode);
    Assert.IsType<CaptureRecognitionScreenCommand>(handler.Command);
  }

  [Fact]
  public async Task BootstrapIncludesEveryHandlerOwnedInitialState()
  {
    var handler = new StreamingCommandHandler();
    await using var application = new WorkbenchApplication(
      ["recognition.capture"],
      WorkbenchRoute.Recognition,
      handler);

    WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
      CancellationToken.None);

    Assert.Collection(
      bootstrap.States.OrderBy(state => state.Scope),
      state => Assert.Equal("batch", state.Scope),
      state => Assert.Equal("recognition", state.Scope),
      state => Assert.Equal("shell", state.Scope));
  }

  [Fact]
  public async Task BootstrapPreparesRuntimeCatalogBeforeReadingInitialStates()
  {
    var handler = new StreamingCommandHandler();
    await using var application = new WorkbenchApplication(
      ["recognition.capture"],
      WorkbenchRoute.Recognition,
      handler);

    WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
      CancellationToken.None);

    Assert.True(handler.Prepared);
    RecognitionWorkbenchState state = Assert.IsType<RecognitionWorkbenchState>(
      bootstrap.States.Single(item => item.Scope == "recognition").State);
    Assert.Equal("recognition.catalog-ready", state.StatusCode);
  }

  [Fact]
  public async Task EachBootstrapStartsANewSessionWithoutResettingRevision()
  {
    await using var application = new WorkbenchApplication(
      ["recognition.capture"],
      WorkbenchRoute.Recognition);

    WorkbenchBootstrap first = await application.BootstrapAsync(
      CancellationToken.None);
    WorkbenchCommandReceipt receipt = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(
        Guid.NewGuid(),
        new NavigateWorkbenchCommand(WorkbenchRoute.Pdf)),
      CancellationToken.None);
    WorkbenchBootstrap recovered = await application.BootstrapAsync(
      CancellationToken.None);

    Assert.NotEqual(first.SessionId, recovered.SessionId);
    Assert.Equal(receipt.Revision, recovered.Revision);
    Assert.True(recovered.Revision > first.Revision);
    Assert.All(
      recovered.States,
      state => Assert.True(state.Revision <= recovered.Revision));
  }

  [Fact]
  public async Task HandlerCanPublishCompletionAfterImmediateCommandReceipt()
  {
    var handler = new StreamingCommandHandler();
    await using var application = new WorkbenchApplication(
      ["recognition.capture"],
      WorkbenchRoute.Recognition,
      handler);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    WorkbenchBootstrap bootstrap = await application.BootstrapAsync(timeout.Token);
    await using IAsyncEnumerator<WorkbenchStateEnvelope> updates = application
      .SubscribeAsync(bootstrap.Revision, timeout.Token)
      .GetAsyncEnumerator(timeout.Token);
    ValueTask<bool> next = updates.MoveNextAsync();

    handler.Publish(new RecognitionWorkbenchState(
      false,
      "recognition.completed"));

    Assert.True(await next);
    Assert.Equal("recognition", updates.Current.Scope);
    Assert.Equal(
      "recognition.completed",
      Assert.IsType<RecognitionWorkbenchState>(updates.Current.State).StatusCode);
  }

  private sealed class StubCommandHandler : IWorkbenchCommandHandler
  {
    public WorkbenchCommand? Command { get; private set; }

    public ValueTask<WorkbenchCommandOutcome> ExecuteAsync(
      WorkbenchCommand command,
      CancellationToken cancellationToken)
    {
      Command = command;
      return ValueTask.FromResult(new WorkbenchCommandOutcome(
        [new RecognitionWorkbenchState(true, "recognition.running", null)],
        null));
    }
  }

  private sealed class StreamingCommandHandler :
    IWorkbenchCommandHandler,
    IWorkbenchStateSource,
    IWorkbenchBootstrapSource
  {
    public bool Prepared { get; private set; }

    public IReadOnlyList<WorkbenchState> InitialStates =>
    [
      new RecognitionWorkbenchState(
        false,
        Prepared ? "recognition.catalog-ready" : "recognition.ready"),
      new BatchWorkbenchState(false, 0, 0, 0),
    ];

    public event Action<WorkbenchState>? StateChanged;

    public ValueTask<WorkbenchCommandOutcome> ExecuteAsync(
      WorkbenchCommand command,
      CancellationToken cancellationToken) => ValueTask.FromResult(
        new WorkbenchCommandOutcome([], null));

    public ValueTask PrepareBootstrapAsync(CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Prepared = true;
      return ValueTask.CompletedTask;
    }

    public void Publish(WorkbenchState state) => StateChanged?.Invoke(state);
  }
}
