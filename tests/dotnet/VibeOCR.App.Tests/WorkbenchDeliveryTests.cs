using System.Text;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WorkbenchDeliveryTests(ITestOutputHelper output)
{
  [Theory]
  [InlineData(10000)]
  [InlineData(100000)]
  public async Task PausedHostDeliveryThroughApplicationKeepsOneCallbackAndAnotherSurfaceResponds(int count)
  {
    var source = new DeliverySource(); await using var app = new WorkbenchApplication([], WorkbenchRoute.Recognition, source);
    var bootstrap = await app.BootstrapAsync(TestContext.Current.CancellationToken);
    var delivery = new WorkbenchDispatcherDelivery();
    var callbacks = new System.Collections.Concurrent.ConcurrentQueue<Action>(); using var queued = new SemaphoreSlim(0);
    int callbackPeak = 0; int statePeak = 0; long deliveredRevision = -1; string? terminal = null; int messages = 0; long bytes = 0;
    long finalRevision = count + 1;
    async Task PublishHost()
    {
      await foreach (var state in app.SubscribeAsync(bootstrap.Revision, TestContext.Current.CancellationToken))
      {
        await delivery.PostAsync(callback => { callbacks.Enqueue(callback); callbackPeak = Math.Max(callbackPeak, callbacks.Count); queued.Release(); return true; }, () =>
        {
          messages++; bytes += Encoding.UTF8.GetByteCount(WorkbenchBridgeCodec.SerializeState(Guid.Empty, state));
          deliveredRevision = state.Revision;
          if (state.State is RecognitionWorkbenchState recognition) terminal = recognition.StatusCode;
        }, TestContext.Current.CancellationToken);
        if (deliveredRevision == finalRevision) break;
      }
    }
    Task pausedHost = PublishHost();
    await using var other = app.SubscribeAsync(bootstrap.Revision, TestContext.Current.CancellationToken,
      new HashSet<string> { "shell", "recognition", "settings" }).GetAsyncEnumerator(TestContext.Current.CancellationToken);
    Task<bool> responsive = other.MoveNextAsync().AsTask();
    source.Publish(new RecognitionWorkbenchState(true, "recognition.running"));
    Assert.True(await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    Assert.True(await responsive.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    for (int index = 1; index < count; index++)
    {
      source.Publish(source.InitialStates[index % source.InitialStates.Count]);
      statePeak = Math.Max(statePeak, app.MaximumPendingSubscriptionStates);
      Assert.InRange(callbacks.Count, 0, 1);
    }
    var receipt = await app.ExecuteAsync(new(Guid.NewGuid(), new SetThemeCommand(WorkbenchTheme.Dark)), TestContext.Current.CancellationToken);
    Assert.True(receipt.Ok); Assert.False(pausedHost.IsCompleted);
    Assert.True(await other.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    Assert.True(other.Current.Revision > bootstrap.Revision + 1);
    source.Publish(new RecognitionWorkbenchState(false, "recognition.failed") { IsTerminal = true });
    Assert.InRange(app.MaximumPendingSubscriptionStates, 1, 20);
    Assert.True(callbacks.TryDequeue(out var first)); first();
    while (!pausedHost.IsCompleted)
    {
      Task signaled = queued.WaitAsync(TestContext.Current.CancellationToken);
      if (await Task.WhenAny(signaled, pausedHost).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) == pausedHost) break;
      await signaled;
      Assert.True(callbacks.TryDequeue(out var callback)); callback();
    }
    await pausedHost;
    Assert.Equal(finalRevision, deliveredRevision); Assert.Equal("recognition.failed", terminal);
    Assert.Equal(1, callbackPeak); Assert.InRange(statePeak, 1, 20);
    output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { pipeline = "application-subscription-dispatcher", input = count, messages, utf8Bytes = bytes, statePendingPeak = statePeak, callbackPendingPeak = callbackPeak, finalRevision = deliveredRevision }));
  }

  private sealed class DeliverySource : IWorkbenchCommandHandler, IWorkbenchStateSource
  {
    public IReadOnlyList<WorkbenchState> InitialStates { get; } = [
      new RecognitionWorkbenchState(true, "recognition.running"), new ImageEditWorkbenchState(false, "recognition.session"),
      new BatchWorkbenchState(true, 40, 1, 0), new PdfWorkbenchState(true, "pdf.open", 64, 0),
      new QrCodeWorkbenchState(false, "qrcode.ready", [], null), new SettingsWorkbenchState(WorkbenchTheme.Light, false, "settings.ready", "cpu", false),
      new UpdateWorkbenchState(true, "update.downloading", null, false), new AboutWorkbenchState("0.3.0", "MIT", "https://example.invalid"),
      new DiagnosticsWorkbenchState("ready", "v2", true, []), new ShellWorkbenchState(WorkbenchRoute.Recognition)];
    public event Action<WorkbenchState>? StateChanged;
    internal void Publish(WorkbenchState state) => StateChanged?.Invoke(state);
    public ValueTask<WorkbenchCommandOutcome> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => ValueTask.FromResult(new WorkbenchCommandOutcome([], null));
  }

  [Theory]
  [InlineData(10000)]
  [InlineData(100000)]
  public void PausedSurfaceHasScopeBoundedStorageAndRecoversCompleteState(int count)
  {
    IReadOnlySet<string> scopes = WorkbenchStateMailbox.AllScopes;
    var slow = new WorkbenchStateMailbox(scopes);
    var scene = new WorkbenchStateMailbox(new HashSet<string> { "shell", "recognition", "settings" });
    WorkbenchState[] owners = [new ShellWorkbenchState(WorkbenchRoute.ImageEdit),
      new RecognitionWorkbenchState(true, "recognition.running"), new ImageEditWorkbenchState(false, "recognition.session"),
      new PinEditWorkbenchState(false, "pinEdit.session"),
      new ImageConversionWorkbenchState(0, []),
      new BatchWorkbenchState(true, 40, 1, 0), new PdfWorkbenchState(true, "pdf.open", 64, 0),
      new QrCodeWorkbenchState(false, "qrcode.ready", [], null),
      new SettingsWorkbenchState(WorkbenchTheme.Light, false, "settings.ready", "cpu", false),
      new UpdateWorkbenchState(true, "update.downloading", null, false),
      new AboutWorkbenchState("0.3.0", "MIT", "https://example.invalid"),
      new DiagnosticsWorkbenchState("ready", "v2", true, [])];
    var snapshots = owners.ToDictionary(state => state.Scope, state => new WorkbenchStateEnvelope(0, state.Scope, WorkbenchStateChange.Replace, state));
    var counts = new Dictionary<string, int>(); var bytes = new Dictionary<string, long>(); int peak = 0;
    for (int index = 1; index <= count; index++)
    {
      WorkbenchState owner = owners[(index - 1) % owners.Length];
      var state = new WorkbenchStateEnvelope(index, owner.Scope, WorkbenchStateChange.Replace, owner);
      snapshots[state.Scope] = state; slow.Publish(state); scene.Publish(state);
      peak = Math.Max(peak, slow.PendingCount);
      foreach (var sent in scene.Drain(snapshots.Values, index))
      {
        Assert.Contains(sent.Scope, new[] { "shell", "recognition", "settings" });
        counts[sent.Scope] = counts.GetValueOrDefault(sent.Scope) + 1;
        bytes[sent.Scope] = bytes.GetValueOrDefault(sent.Scope) + Encoding.UTF8.GetByteCount(WorkbenchBridgeCodec.SerializeState(Guid.Empty, sent));
      }
    }
    Assert.Equal(scopes.Count, slow.PendingCount); Assert.Equal(scopes.Count, peak);
    long revision = count;
    void Publish(WorkbenchState owner) { var state = new WorkbenchStateEnvelope(++revision, owner.Scope, WorkbenchStateChange.Replace, owner); snapshots[owner.Scope] = state; slow.Publish(state); }
    Publish(new RecognitionWorkbenchState(false, "recognition.failed") { IsTerminal = true });
    // Real owners reproject the current typed terminal during catalog/resource notifications;
    // unrelated PDF progress and theme changes remain independently current.
    Publish(new RecognitionWorkbenchState(false, "recognition.failed"));
    Publish(new PdfWorkbenchState(true, "pdf.open", 64, 0, ProgressCurrent: 12, ProgressTotal: 64));
    Publish(new SettingsWorkbenchState(WorkbenchTheme.Dark, false, "settings.ready", "cpu", false));
    slow.Resync = true; var recovery = slow.Drain(snapshots.Values, revision);
    Assert.Equal(WorkbenchStateChange.Reset, recovery[0].Change); Assert.Equal(WorkbenchStateChange.Ready, recovery[^1].Change);
    Assert.Equal(scopes.Count + 2, recovery.Length); Assert.All(recovery, state => Assert.Equal(revision, state.Revision));
    Assert.Equal("recognition.failed", Assert.IsType<RecognitionWorkbenchState>(recovery.Single(state => state.Scope == "recognition").State).StatusCode);
    Assert.Equal(12, Assert.IsType<PdfWorkbenchState>(recovery.Single(state => state.Scope == "pdf").State).ProgressCurrent);
    Assert.Equal(WorkbenchTheme.Dark, Assert.IsType<SettingsWorkbenchState>(recovery.Single(state => state.Scope == "settings").State).Theme);
    var mainBytes = recovery.Sum(state => Encoding.UTF8.GetByteCount(WorkbenchBridgeCodec.SerializeState(Guid.Empty, state)));
    // Only a new recognition task replaces this owner's previous terminal.
    Publish(new RecognitionWorkbenchState(true, "recognition.running")); slow.Resync = true;
    Assert.Equal("recognition.running", Assert.IsType<RecognitionWorkbenchState>(slow.Drain(snapshots.Values, revision).Single(state => state.Scope == "recognition").State).StatusCode);
    output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { input = count, scene = new { messagesByScope = counts, utf8BytesByScope = bytes, unrelatedScopes = 0 }, main = new { recoveryMessages = recovery.Length, recoveryUtf8Bytes = mainBytes, pendingPeak = peak, slotBudget = 2 * scopes.Count }, finalRevision = revision }));
  }

  [Fact]
  public async Task CancelledOldDispatcherCallbackBlocksNewEnqueueWithoutPublishingOrPoisoningRecovery()
  {
    var delivery = new WorkbenchDispatcherDelivery(); var queued = new System.Collections.Concurrent.ConcurrentQueue<Action>(); using var queuedSignal = new SemaphoreSlim(0); int published = 0;
    bool Enqueue(Action action) { queued.Enqueue(action); Assert.Single(queued); queuedSignal.Release(); return true; }
    using var old = new CancellationTokenSource();
    Task first = delivery.PostAsync(Enqueue, () => published++, old.Token);
    old.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    Task second = delivery.PostAsync(Enqueue, () => published++, TestContext.Current.CancellationToken);
    Assert.Single(queued); Assert.False(second.IsCompleted);
    Assert.True(queued.TryDequeue(out var oldCallback)); oldCallback(); Assert.Equal(0, published);
    await queuedSignal.WaitAsync(TestContext.Current.CancellationToken);
    Assert.True(await queuedSignal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    Assert.True(queued.TryDequeue(out var currentCallback)); currentCallback(); await second; Assert.Equal(1, published);
    await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.PostAsync(_ => false, () => published++, CancellationToken.None));
    await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.PostAsync(_ => throw new InvalidOperationException("closed"), () => published++, CancellationToken.None));
    Task recovered = delivery.PostAsync(Enqueue, () => published++, TestContext.Current.CancellationToken);
    Assert.True(queued.TryDequeue(out var recoveredCallback)); recoveredCallback(); await recovered; Assert.Equal(2, published);
  }

  [Fact]
  public async Task HistoryGapUsesSnapshotTransactionAndOtherSubscriptionReceiptStillReturns()
  {
    await using var app = new WorkbenchApplication([], WorkbenchRoute.Recognition);
    for (int index = 0; index < 300; index++)
      Assert.True((await app.ExecuteAsync(new(Guid.NewGuid(), new NavigateWorkbenchCommand(WorkbenchRoute.Pdf)), CancellationToken.None)).Ok);
    await using var stream = app.SubscribeAsync(0, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
    Assert.True(await stream.MoveNextAsync()); Assert.Equal(WorkbenchStateChange.Reset, stream.Current.Change);
    Assert.True(await stream.MoveNextAsync()); Assert.Equal(WorkbenchRoute.Pdf, Assert.IsType<ShellWorkbenchState>(stream.Current.State).Route);
    Assert.True((await app.ExecuteAsync(new(Guid.NewGuid(), new NavigateWorkbenchCommand(WorkbenchRoute.QrCode)), CancellationToken.None)).Ok);
    Assert.True(await stream.MoveNextAsync()); Assert.Equal(WorkbenchStateChange.Ready, stream.Current.Change);
    Assert.True(await stream.MoveNextAsync()); Assert.Equal(WorkbenchRoute.QrCode, Assert.IsType<ShellWorkbenchState>(stream.Current.State).Route);
  }
}
