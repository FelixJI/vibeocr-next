using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;

namespace VibeOCR.App.Tests;

// 回执交付：同一 Web 文档内 bootstrap 换 session 后，已接受命令/PDF flush 仍交付一次；
// 导航/进程失败/dispose/换桥抑制旧文档在途消息。经真实消息泵接缝驱动宿主异步路径。
public sealed class WebWorkbenchCommandDeliveryTests
{
  private static string Bootstrap(Guid requestId) => JsonSerializer.Serialize(new
  {
    version = 2,
    kind = "request",
    id = requestId,
    type = "app.bootstrap",
    payload = new { },
  });

  private static string Command(Guid requestId, Guid session) => JsonSerializer.Serialize(new
  {
    version = 2,
    kind = "request",
    id = requestId,
    type = "app.command",
    payload = new
    {
      sessionId = session,
      command = new
      {
        scope = "shell",
        action = "navigate",
        arguments = new { route = "settings" },
      },
    },
  });

  private static string ApplySnapshot(Guid requestId, Guid session, string resourceUri) =>
    JsonSerializer.Serialize(new
    {
      version = 2,
      kind = "request",
      id = requestId,
      type = "app.command",
      payload = new
      {
        sessionId = session,
        command = new
        {
          scope = "pinEdit",
          action = "applySnapshot",
          arguments = new
          {
            contributeId = Guid.NewGuid().ToString("N"),
            resourceUri,
            sessionId = session.ToString("N"),
            revision = 0,
            excludeBoxes = Array.Empty<object>(),
          },
        },
      },
    });

  private static IEnumerable<(Guid Id, bool Ok)> Receipts(IEnumerable<string> posted)
  {
    foreach (string json in posted)
    {
      using JsonDocument document = JsonDocument.Parse(json);
      JsonElement root = document.RootElement;
      if (root.GetProperty("type").GetString() == "app.command")
        yield return (Guid.Parse(root.GetProperty("id").GetString()!),
          root.GetProperty("payload").GetProperty("ok").GetBoolean());
    }
  }

  private static IEnumerable<Guid> BootstrapResponses(IEnumerable<string> posted)
  {
    foreach (string json in posted)
    {
      using JsonDocument document = JsonDocument.Parse(json);
      if (document.RootElement.GetProperty("type").GetString() == "app.bootstrap")
        yield return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
    }
  }

  [Fact]
  public async Task AcceptedCommandStillDeliversOneReceiptAfterBootstrapChangesSession()
  {
    Guid first = Guid.NewGuid(), second = Guid.NewGuid();
    await using HostFixture fixture = new(first, second);
    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;

    Guid bootstrapId = Guid.NewGuid();
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(bootstrapId), fixture.Document);
    Assert.Equal([bootstrapId], BootstrapResponses(fixture.Posted));

    Guid commandId = Guid.NewGuid();
    Task command = fixture.Host.ReceiveWebMessageAsync(Command(commandId, first), fixture.Document);
    Assert.Single(fixture.Application.Executions);

    Guid rebootId = Guid.NewGuid();
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(rebootId), fixture.Document);
    Assert.Equal([bootstrapId, rebootId], BootstrapResponses(fixture.Posted));
    Assert.NotEqual(fixture.Application.ReturnedSessions[0], fixture.Application.ReturnedSessions[1]);

    gate.SetResult(new WorkbenchCommandReceipt(commandId, 7, null));
    await command;
    Assert.Equal([(commandId, true)], Receipts(fixture.Posted));
    Assert.Equal(2, fixture.Application.Bootstraps);
    Assert.Single(fixture.Application.Executions);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task BootstrapFailureReportsOnlyWhileItsRequestOwnsTheSession(bool superseded)
  {
    await using HostFixture fixture = new(Guid.NewGuid(), Guid.NewGuid());
    var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.BootstrapGate = firstGate;
    Task first = fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Guid secondId = Guid.NewGuid();
    Task? second = null;
    if (superseded)
    {
      fixture.Application.BootstrapGate = secondGate;
      second = fixture.Host.ReceiveWebMessageAsync(Bootstrap(secondId), fixture.Document);
      Assert.False(second.IsCompleted);
    }

    firstGate.SetException(new InvalidOperationException("Runtime catalog unavailable"));
    await first;
    Assert.Equal(superseded ? 0 : 1, fixture.Recoveries);
    Assert.Empty(fixture.Violations);
    Assert.Equal(superseded ? 0 : 1,
      fixture.States.Count(state => state.StartsWith("bridge-command-failed:", StringComparison.Ordinal)));
    Assert.Empty(BootstrapResponses(fixture.Posted));

    if (second is not null)
    {
      secondGate.SetResult();
      await second;
      Assert.Equal([secondId], BootstrapResponses(fixture.Posted));
      Assert.Equal(0, fixture.Recoveries);
    }
  }

  [Fact]
  public async Task DocumentResetSuppressesOldReceiptFlushAndStaleSessionInput()
  {
    Guid first = Guid.NewGuid();
    await using HostFixture fixture = new(first);
    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;

    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Task<bool> flush = fixture.Host.RequestPdfPreviewFlushAsync(
      first, Guid.NewGuid().ToString("N"), _ => { }, CancellationToken.None);

    Guid commandId = Guid.NewGuid();
    Task command = fixture.Host.ReceiveWebMessageAsync(Command(commandId, first), fixture.Document);
    fixture.Host.ResetWebDocument();
    Assert.False(await flush);

    gate.SetResult(new WorkbenchCommandReceipt(commandId, 7, null));
    await command;
    Assert.Empty(Receipts(fixture.Posted));

    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    await fixture.Host.ReceiveWebMessageAsync(Command(Guid.NewGuid(), first), fixture.Document);
    Assert.Single(fixture.Violations);
    Assert.Single(fixture.Application.Executions);

    var freshGate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = freshGate;
    Guid freshId = Guid.NewGuid();
    Task fresh = fixture.Host.ReceiveWebMessageAsync(
      Command(freshId, fixture.Application.ReturnedSessions[1]), fixture.Document);
    freshGate.SetResult(new WorkbenchCommandReceipt(freshId, 9, null));
    await fresh;
    Assert.Equal([(freshId, true)], Receipts(fixture.Posted));
  }

  [Fact]
  public async Task DisposeSuppressesAcceptedCommandReceipt()
  {
    Guid first = Guid.NewGuid();
    await using HostFixture fixture = new(first);
    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);

    Guid commandId = Guid.NewGuid();
    Task command = fixture.Host.ReceiveWebMessageAsync(Command(commandId, first), fixture.Document);
    await fixture.Host.DisposeAsync();
    gate.SetResult(new WorkbenchCommandReceipt(commandId, 7, null));
    await command;
    Assert.Empty(Receipts(fixture.Posted));
  }

  [Fact]
  public async Task ReplacedBridgeIdentitySuppressesOldDocumentReceipt()
  {
    Guid first = Guid.NewGuid();
    await using HostFixture fixture = new(first);
    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);

    Guid commandId = Guid.NewGuid();
    Task command = fixture.Host.ReceiveWebMessageAsync(Command(commandId, first), fixture.Document);
    var replacement = new List<string>();
    fixture.Host.RegisterBridge(new object(), replacement.Add);
    gate.SetResult(new WorkbenchCommandReceipt(commandId, 7, null));
    await command;
    Assert.Empty(Receipts(fixture.Posted));
    Assert.Empty(Receipts(replacement));
  }

  [Fact]
  public async Task PdfFlushKeepsIndependentContractAcrossBootstrapSessionChange()
  {
    Guid first = Guid.NewGuid(), second = Guid.NewGuid();
    await using HostFixture fixture = new(first, second);
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    string documentId = Guid.NewGuid().ToString("N");
    string request = "";
    Task<bool> flush = fixture.Host.RequestPdfPreviewFlushAsync(
      first, documentId, json => request = json, CancellationToken.None);
    Assert.False(flush.IsCompleted);

    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    using JsonDocument frame = JsonDocument.Parse(request);
    string reply = JsonSerializer.Serialize(new
    {
      version = 2,
      kind = "response",
      id = frame.RootElement.GetProperty("id").GetString()!,
      type = "pdf.flushPreview",
      payload = new { sessionId = first, documentId, ok = true },
    });
    await fixture.Host.ReceiveWebMessageAsync(reply, fixture.Document);
    Assert.True(await flush);
  }

  [Fact]
  public async Task StaleDocumentFailuresDoNotTriggerRecoveryButCurrentOnesDo()
  {
    Guid first = Guid.NewGuid();
    await using HostFixture fixture = new(first);

    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Task current = fixture.Host.ReceiveWebMessageAsync(Command(Guid.NewGuid(), first), fixture.Document);
    gate.SetException(new InvalidOperationException("current document failure"));
    await current;
    Assert.Equal(1, fixture.Recoveries);
    Assert.Contains("bridge-command-failed:InvalidOperationException", fixture.States);

    var staleTyped = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = staleTyped;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Task staleCancel = fixture.Host.ReceiveWebMessageAsync(
      Command(Guid.NewGuid(), fixture.Application.ReturnedSessions[1]), fixture.Document);
    fixture.Host.ResetWebDocument();
    staleTyped.SetCanceled(TestContext.Current.CancellationToken);
    await staleCancel;
    Assert.Empty(fixture.Violations);

    var staleGeneric = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = staleGeneric;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Task staleFail = fixture.Host.ReceiveWebMessageAsync(
      Command(Guid.NewGuid(), fixture.Application.ReturnedSessions[2]), fixture.Document);
    fixture.Host.ResetWebDocument();
    staleGeneric.SetException(new InvalidOperationException("stale document failure"));
    await staleFail;
    Assert.Equal(1, fixture.Recoveries);

    var disposedGate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = disposedGate;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Task disposedFail = fixture.Host.ReceiveWebMessageAsync(
      Command(Guid.NewGuid(), fixture.Application.ReturnedSessions[3]), fixture.Document);
    await fixture.Host.DisposeAsync();
    disposedGate.SetException(new InvalidOperationException("disposed failure"));
    await disposedFail;
    Assert.Equal(1, fixture.Recoveries);
  }

  [Fact]
  public async Task PinSnapshotApplyExceptionResetsInFlightAndRepliesBoundedFailureReceipt()
  {
    Guid session = Guid.NewGuid();
    await using HostFixture fixture = new(pinEditHost: true, session);
    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    WorkbenchAnnotationLease png = await fixture.Annotations.UploadPngAsync(
      new MemoryStream(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")),
      TestContext.Current.CancellationToken);

    Guid commandId = Guid.NewGuid();
    Task command = fixture.Host.ReceiveWebMessageAsync(
      ApplySnapshot(commandId, session, png.ResourceUri.AbsoluteUri), fixture.Document);
    gate.SetException(new IOException("disk failure during snapshot persist"));
    await command;

    // 真实命令异常：在途状态复位（否则退出对话框永久禁用且保存复挂起 Promise），
    // Web 收到有界失败终态回执可重试；不进入导航重置（不丢编辑现场）。
    Assert.False(fixture.Host.PinSnapshotInFlight);
    Assert.Equal([(commandId, false)], Receipts(fixture.Posted));
    Assert.Equal(0, fixture.Recoveries);
    Assert.Empty(fixture.Violations);
    Assert.Contains("bridge-command-failed:IOException", fixture.States);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ResetDocumentReportsReceiptLossOnlyAfterItsApplySucceeds(bool succeeds)
  {
    Guid source = Guid.NewGuid();
    await using HostFixture fixture = new(pinEditHost: true, source, Guid.NewGuid());
    List<Guid> lost = [];
    fixture.Host.PinSnapshotReceiptLost += lost.Add;
    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    string png = await UploadPinPngAsync(fixture);
    Guid commandId = Guid.NewGuid();
    Task command = fixture.Host.ReceiveWebMessageAsync(ApplySnapshot(commandId, source, png), fixture.Document);
    Assert.True(fixture.Host.PinSnapshotInFlight);
    fixture.Host.ResetWebDocument();
    Assert.Empty(lost);
    // A new document may bootstrap before the accepted native apply completes.
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    if (succeeds) gate.SetResult(new WorkbenchCommandReceipt(commandId, 7, null));
    else gate.SetException(new IOException("snapshot writer failed after document reset"));
    await command;
    Assert.Equal(succeeds ? new[] { source } : Array.Empty<Guid>(), lost);
    Assert.False(fixture.Host.PinSnapshotInFlight);
    fixture.Host.ResetWebDocument();
    Assert.Equal(succeeds ? new[] { source } : Array.Empty<Guid>(), lost);
    Assert.Empty(Receipts(fixture.Posted));
  }

  [Fact]
  public async Task IdleResetDoesNotBypassTheNextSuccessfulSnapshotAcknowledgement()
  {
    Guid source = Guid.NewGuid();
    await using HostFixture fixture = new(pinEditHost: true, Guid.NewGuid(), source);
    List<Guid> lost = [];
    fixture.Host.PinSnapshotReceiptLost += lost.Add;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    fixture.Host.ResetWebDocument();
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Assert.Empty(lost);
    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;
    Task<bool> flush = fixture.Host.RequestPinSnapshotFlushAsync(source, _ => { });
    string png = await UploadPinPngAsync(fixture);
    Guid commandId = Guid.NewGuid();
    Task command = fixture.Host.ReceiveWebMessageAsync(ApplySnapshot(commandId, source, png), fixture.Document);
    gate.SetResult(new WorkbenchCommandReceipt(commandId, 7, null));
    await command;
    Assert.Empty(lost);
    Assert.False(flush.IsCompleted);
    await fixture.Host.ReceiveWebMessageAsync(JsonSerializer.Serialize(new
    {
      version = WorkbenchProtocol.Version, kind = "event", type = "pinEdit.snapshotResult",
      payload = new { sessionId = source.ToString("N"), ok = true },
    }), fixture.Document);
    Assert.True(await flush);
    fixture.Host.ResetWebDocument();
    Assert.Empty(lost);
  }

  [Fact]
  public async Task ResetAfterCommitReportsItsUnacknowledgedSnapshotExactlyOnce()
  {
    Guid source = Guid.NewGuid();
    await using HostFixture fixture = new(pinEditHost: true, source);
    List<Guid> lost = [];
    fixture.Host.PinSnapshotReceiptLost += lost.Add;
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    var gate = new TaskCompletionSource<WorkbenchCommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Application.ExecuteGate = gate;
    string png = await UploadPinPngAsync(fixture);
    Guid commandId = Guid.NewGuid();
    Task command = fixture.Host.ReceiveWebMessageAsync(ApplySnapshot(commandId, source, png), fixture.Document);
    gate.SetResult(new WorkbenchCommandReceipt(commandId, 7, null));
    await command;
    Assert.Empty(lost);
    fixture.Host.ResetWebDocument();
    fixture.Host.ResetWebDocument();
    Assert.Equal([source], lost);
  }

  private static async Task<string> UploadPinPngAsync(HostFixture fixture)
  {
    WorkbenchAnnotationLease png = await fixture.Annotations.UploadPngAsync(
      new MemoryStream(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")),
      TestContext.Current.CancellationToken);
    return png.ResourceUri.AbsoluteUri;
  }

  private sealed class HostFixture : IAsyncDisposable
  {
    public readonly string Root = Path.Combine(Path.GetTempPath(), $"workbench-delivery-{Guid.NewGuid():N}");
    public readonly DeliveryApplication Application;
    public readonly WebWorkbenchHost Host;
    public readonly WorkbenchResourceBroker Broker;
    public readonly WorkbenchAnnotationStore Annotations;
    public readonly List<string> Posted = [];
    public readonly List<string> States = [];
    public readonly ConcurrentQueue<Exception> Violations = [];
    public readonly object Document = new();
    public int Recoveries;

    public HostFixture(params Guid[] sessions) : this(false, sessions)
    {
    }

    public HostFixture(bool pinEditHost, params Guid[] sessions)
    {
      Directory.CreateDirectory(Root);
      Application = new DeliveryApplication(sessions);
      Broker = new WorkbenchResourceBroker(Root);
      Annotations = new WorkbenchAnnotationStore(Root);
      Host = new WebWorkbenchHost(Application, Broker, Annotations,
        applicationOwner: false, pinEditHost: pinEditHost);
      Host.RegisterBridge(Document, Posted.Add);
      Host.StateChanged += States.Add;
      Host.ProtocolViolation += Violations.Enqueue;
      Host.RecoveryRequired += () => Recoveries++;
    }

    public async ValueTask DisposeAsync()
    {
      await Host.DisposeAsync();
      Broker.Dispose();
      Annotations.Dispose();
      Directory.Delete(Root, recursive: true);
    }
  }

  private sealed class DeliveryApplication : IWorkbenchApplication
  {
    private readonly Queue<Guid> sessions;
    public int Bootstraps;
    public readonly List<WorkbenchCommandEnvelope> Executions = [];
    public readonly List<Guid> ReturnedSessions = [];
    public TaskCompletionSource<WorkbenchCommandReceipt>? ExecuteGate;
    public TaskCompletionSource? BootstrapGate;

    public DeliveryApplication(params Guid[] sessions) => this.sessions = new(sessions);

    public async ValueTask<WorkbenchBootstrap> BootstrapAsync(CancellationToken cancellationToken)
    {
      Bootstraps++;
      Guid session = sessions.Count > 0 ? sessions.Dequeue() : Guid.NewGuid();
      if (BootstrapGate is { } gate) await gate.Task;
      ReturnedSessions.Add(session);
      return new WorkbenchBootstrap(
        WorkbenchProtocol.Version,
        session,
        0,
        WorkbenchRoute.Recognition,
        [],
        new HashSet<string>());
    }

    public ValueTask<WorkbenchCommandReceipt> ExecuteAsync(
      WorkbenchCommandEnvelope envelope,
      CancellationToken cancellationToken)
    {
      Executions.Add(envelope);
      return new ValueTask<WorkbenchCommandReceipt>(
        (ExecuteGate ?? throw new InvalidOperationException("No execute gate configured.")).Task);
    }

    public async IAsyncEnumerable<WorkbenchStateEnvelope> SubscribeAsync(
      long afterRevision,
      [EnumeratorCancellation] CancellationToken cancellationToken,
      IReadOnlySet<string>? scopes = null)
    {
      await Task.CompletedTask;
      yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
