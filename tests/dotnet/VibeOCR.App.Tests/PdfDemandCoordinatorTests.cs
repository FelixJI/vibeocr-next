using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PdfDemandCoordinatorTests
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  [Fact]
  public Task MetadataOpenAndNextDocumentDoNotWaitForAutomaticDetectionOrMakeAThirdIssuedRead() => OnDispatcherAsync(async () =>
  {
    var client = new Client();
    var heldRead = NewGate<byte[]>();
    var heldAuto = NewGate();
    client.OnImage = (session, kind, page) => session == "doc-1" ? heldRead.Task : Task.FromResult(Client.Png);
    client.OnAuto = session => session == "doc-1" ? heldAuto.Task : Task.CompletedTask;
    await using var fixture = new Fixture(client);
    try
    {
      PdfWorkbenchState opened = await fixture.OpenAsync("first.pdf");
      Assert.False(opened.IsBusy);
      Assert.True(opened.CanDemandPages);
      Assert.Equal(0, opened.SelectedPage);
      Assert.Equal(0, opened.DetectedCount);
      Assert.Equal(("doc-1", "preview", 0), await client.Images.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct));
      await fixture.CommandAsync(new SetPdfDemandCommand(0, [0, 1], 160, null));
      Assert.Equal(("doc-1", "thumbnail", 0), await client.Images.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct));
      Assert.Equal("doc-1", await client.Autos.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct));

      using var observed = new StateObserver(fixture.Handler, state => state.SessionId == "doc-2" && state.PagePreview is not null);
      PdfWorkbenchState second = await fixture.OpenAsync("second.pdf");
      Assert.Equal("doc-2", second.SessionId);
      await client.Acknowledged.Task.WaitAsync(Timeout, Ct);
      Assert.False(client.Images.Reader.TryRead(out _));
      Assert.False(observed.Next.IsCompleted);
      Assert.False(client.Autos.Reader.TryRead(out _));
      PdfDemandDiagnostics queues = fixture.Handler.Pdf.DemandDiagnostics;
      Assert.Equal(2, queues.OrdinaryIssued.Current);
      Assert.Equal(1, queues.BackgroundIssued.Current);
      Assert.InRange(queues.Pending.Peak, 1, 16);

      heldRead.TrySetResult(Client.Png);
      Assert.Equal(("doc-2", "preview", 0), await client.Images.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct));
      PdfWorkbenchState ready = await observed.Next.WaitAsync(Timeout, Ct);
      Assert.NotNull(ready.PagePreview);
      Assert.Equal(2, client.MaximumImageReads);
      Assert.Equal(0, client.LegacyLoads);
    }
    finally { heldRead.TrySetResult(Client.Png); heldAuto.TrySetResult(); }
  });

  [Fact]
  public Task ActualWritesWaitForTheirOwnAckAndCloseDoesNotBypassFailedAck() => OnDispatcherAsync(async () =>
  {
    var client = new Client();
    await using var fixture = new Fixture(client);
    await fixture.OpenAsync("first.pdf");
    PdfViewModel first = fixture.Handler.PdfDocuments[0].Model;
    await fixture.OpenAsync("second.pdf");
    PdfViewModel second = fixture.Handler.PdfDocuments[1].Model;

    var firstAck = NewGate();
    var firstAckEntered = NewGate();
    client.OnAck = (session, request) =>
    {
      if (request.Scope != Wire.PdfReadCancelScope.AllReads || session != "doc-1") return Task.CompletedTask;
      firstAckEntered.TrySetResult();
      return firstAck.Task;
    };
    using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
    Task rotateFirst = first.RotateAsync([0], 90, cancelled.Token);
    await firstAckEntered.Task.WaitAsync(Timeout, Ct);
    Task rotateSecond = second.RotateAsync([1], 90, Ct);
    Assert.Empty(client.Rotations);
    Assert.DoesNotContain(client.Acks, record => record == ("doc-2", Wire.PdfReadCancelScope.AllReads));
    cancelled.Cancel();
    firstAck.TrySetResult();
    await Task.WhenAll(rotateFirst, rotateSecond).WaitAsync(Timeout, Ct);
    Assert.Equal(("doc-2", 1), Assert.Single(client.Rotations));
    Assert.Contains(("doc-2", Wire.PdfReadCancelScope.AllReads), client.Acks);
    Assert.Equal(1, second.Pages[1].ContentGeneration);

    client.OnAck = (_, request) => request.Scope == Wire.PdfReadCancelScope.AllReads
      ? Task.FromException(new IOException("injected failed acknowledgement")) : Task.CompletedTask;
    await Assert.ThrowsAsync<InvalidOperationException>(() => first.CloseSessionAsync(Ct));
    Assert.True(first.HasSession);
    Assert.Empty(client.Closed);
    client.OnAck = null;
    await first.CloseSessionAsync(Ct);
    Assert.Equal("doc-1", Assert.Single(client.Closed));
    Assert.False(first.HasSession);
  });

  [Fact]
  public Task SelectedOcrConsumesExplicitPagePresenceAndAppliesEachCommitBeforeReadersResume() => OnDispatcherAsync(async () =>
  {
    var client = new Client();
    var auto = NewGate();
    var reachedWrite = NewGate();
    var releaseWrite = NewGate();
    client.OnAuto = _ => auto.Task;
    client.OnWrite = _ => { reachedWrite.TrySetResult(); return releaseWrite.Task; };
    await using var fixture = new Fixture(client);
    try
    {
      await fixture.OpenAsync("ocr.pdf");
      PdfViewModel model = fixture.Handler.PdfDocuments[0].Model;
      await client.Autos.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
      var progress = new ConcurrentQueue<int>();
      model.PropertyChanged += (_, _) =>
      {
        if (model.Phase == "load" && model.IsBusy) progress.Enqueue(model.ProgressCurrent);
      };
      Task write = model.StartOcrAsync([1], false, Ct, addTextLayer: true);
      await reachedWrite.Task.WaitAsync(Timeout, Ct);
      Assert.Equal(new[] { 1 }, Assert.Single(client.ExplicitPresence));
      Assert.Contains(1, progress);
      Assert.Equal(PdfDetectionState.Unknown, model.Pages[2].DetectionState);
      Assert.Equal(0, model.Pages[1].ContentGeneration);

      using var next = new StateObserver(fixture.Handler, state =>
        state.SelectedPage == 1 && state.Pages!.Single(page => page.Index == 1).ContentGeneration == 1 &&
        state.PagePreview is not null);
      var readWithoutContent = new ConcurrentQueue<long>();
      client.OnImage = (_, _, page) =>
      {
        if (page == 1) readWithoutContent.Enqueue(model.Pages[1].ContentGeneration);
        return Task.FromResult(Client.Png);
      };
      await fixture.CommandAsync(new SetCurrentPdfPageCommand(1));
      releaseWrite.TrySetResult();
      await write.WaitAsync(Timeout, Ct);
      await next.Next.WaitAsync(Timeout, Ct);
      Assert.Equal(1, model.Pages[1].ContentGeneration);
      Assert.Equal(0, model.Pages[0].ContentGeneration);
      Assert.Equal(1, model.AddedCount);
      Assert.NotEmpty(readWithoutContent);
      Assert.All(readWithoutContent, generation => Assert.Equal(1, generation));
    }
    finally { auto.TrySetResult(); releaseWrite.TrySetResult(); }
  });

  [Fact]
  public Task WarmReturnAndRemapReuseReadsAndKeepOffWindowCurrentPageIdentity() => OnDispatcherAsync(async () =>
  {
    var client = new Client(70);
    await using var fixture = new Fixture(client);
    PdfWorkbenchState opened = await fixture.OpenAsync("warm.pdf");
    await fixture.CommandAsync(new SetPdfDemandCommand(0, [], null, 768));
    PdfWorkbenchState initial = await fixture.SelectReadyAsync(0);
    PdfWorkbenchState other = await fixture.SelectReadyAsync(1);
    int previewZero = client.ImageLog.Count(image => image is ("doc-1", "preview", 0));
    int inspectZero = client.Inspections.Count(page => page == 0);
    PdfWorkbenchState warm = await fixture.SelectReadyAsync(0);
    Assert.Equal(initial.PagePreview!.Url, warm.PagePreview!.Url);
    Assert.Equal(initial.PageInspect!.Url, warm.PageInspect!.Url);
    Assert.Equal(previewZero, client.ImageLog.Count(image => image is ("doc-1", "preview", 0)));
    Assert.Equal(inspectZero, client.Inspections.Count(page => page == 0));

    PdfWorkbenchState offWindow = await fixture.StateAsync(new SetPdfWindowCommand(6));
    Assert.DoesNotContain(offWindow.Pages!, page => page.Index == 0);
    PdfWorkbenchPage current = Assert.IsType<PdfWorkbenchPage>(offWindow.CurrentPage);
    Assert.Equal(opened.Pages![0].PageKey, current.PageKey);
    Assert.Equal(opened.Pages[0].Width, current.Width);
    Assert.Null(current.Thumbnail);

    PdfViewModel model = fixture.Handler.PdfDocuments[0].Model;
    long key = model.Pages[0].PageKey;
    await fixture.StateAsync(new SetPdfWindowCommand(0));
    await model.ReorderAsync(new[] { 1, 0 }.Concat(Enumerable.Range(2, 68)).ToArray(), Ct);
    int pageOneReads = client.ImageLog.Count(image => image is ("doc-1", "preview", 1));
    int pageOneInspects = client.Inspections.Count(page => page == 1);
    PdfWorkbenchState moved = await fixture.SelectReadyAsync(1);
    Assert.Equal(key, moved.Pages![1].PageKey);
    Assert.Equal(0, moved.Pages[1].ContentGeneration);
    Assert.Equal(warm.PagePreview.Url, moved.PagePreview!.Url);
    Assert.NotEqual(other.PageInspect!.Url, moved.PageInspect!.Url);
    Assert.Equal(pageOneReads, client.ImageLog.Count(image => image is ("doc-1", "preview", 1)));
    Assert.Equal(pageOneInspects, client.Inspections.Count(page => page == 1));
    await using (WorkbenchResourceResponse response = await fixture.Broker.OpenAsync(new Uri(moved.PageInspect.Url), Ct))
    using (JsonDocument json = await JsonDocument.ParseAsync(response.Content, cancellationToken: Ct))
      Assert.Equal(1, json.RootElement.GetProperty("page").GetInt32());

    Assert.True(await fixture.Handler.RequestCloseAllPdfAsync().WaitAsync(Timeout, Ct));
    var drained = fixture.Handler.Pdf.DemandDiagnostics;
    Assert.Equal(0, drained.OrdinaryIssued.Current);
    Assert.Equal(0, drained.BackgroundIssued.Current);
    Assert.Equal(0, drained.ActiveImages.Current);
    Assert.Equal(0, drained.WarmBytes.Current);
    await Assert.ThrowsAsync<WorkbenchResourceAccessException>(
      async () => await fixture.Broker.OpenAsync(new Uri(warm.PagePreview.Url), Ct));
  });

  [Fact]
  public Task CancellingExitReviewKeepsDemandForUncachedPagesAndNewDocuments() => OnDispatcherAsync(async () =>
  {
    var client = new Client();
    await using var fixture = new Fixture(client, _ => Task.FromResult(PdfCloseDecision.Cancel));
    PdfWorkbenchState first = await fixture.OpenAsync("draft.pdf");
    await fixture.SelectReadyAsync(0);
    await fixture.CommandAsync(new PdfBoundCommand(first.DocumentId!, new SetPdfPreviewPositionCommand(
      new PdfPreviewPosition(Block: 0, Draft: "edited", OriginalText: "original", Revision: first.Revision, Page: 0))));

    Assert.False(await fixture.Handler.RequestCloseAllPdfAsync().WaitAsync(Timeout, Ct));
    Assert.Empty(client.Closed);
    Assert.Equal("edited", Assert.Single(fixture.Handler.PdfDocuments).Preview.Draft);
    Assert.NotNull((await fixture.SelectReadyAsync(2)).PagePreview);
    Assert.Contains(("doc-1", "preview", 2), client.ImageLog);

    await fixture.OpenAsync("next.pdf");
    PdfWorkbenchState second = await fixture.SelectReadyAsync(0);
    Assert.Equal("doc-2", second.SessionId);
    Assert.Contains(("doc-2", "preview", 0), client.ImageLog);
  });

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public Task RetryRepairsFailedPreviewAndReusesSuccessfulInspection(bool failLow) => OnDispatcherAsync(async () =>
  {
    var client = new Client();
    var highFailed = NewGate();
    bool failing = true;
    int previewCalls = 0;
    client.OnPreview = (page, tier) =>
    {
      if (page != 0) return Task.FromResult(Client.Png);
      Interlocked.Increment(ref previewCalls);
      if (failing && tier > 768) highFailed.TrySetResult();
      return failing && (failLow || tier > 768)
        ? Task.FromException<byte[]>(new InvalidOperationException("injected preview failure"))
        : Task.FromResult(Client.Png);
    };
    await using var fixture = new Fixture(client);
    using var failed = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" &&
      state.PageInspectStatusCode == "pdf.inspect.failed" && state.PageInspect is not null &&
      (state.PagePreview is null) == failLow && highFailed.Task.IsCompleted);
    await fixture.OpenAsync("preview.pdf");
    PdfWorkbenchState failure = await failed.Next.WaitAsync(Timeout, Ct);
    string inspectUrl = failure.PageInspect!.Url;
    Assert.Equal(2, previewCalls);

    failing = false;
    using var ready = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" &&
      state.PageInspectStatusCode == "pdf.inspect.ready" && state.PagePreview is not null &&
      fixture.Handler.Pdf.DemandDiagnostics.ActiveImages.Current == 2);
    await fixture.CommandAsync(new RetryPdfPageInspectCommand());
    PdfWorkbenchState repaired = await ready.Next.WaitAsync(Timeout, Ct);
    Assert.Equal(inspectUrl, repaired.PageInspect!.Url);
    Assert.Equal(0, Assert.Single(client.Inspections));
    Assert.Equal(failLow ? 4 : 3, previewCalls);
  });

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public Task AmbiguousResponsesRetainBothOrdinarySlotsAndAutoUntilOldSessionAcknowledges(bool failFirstAck) => OnDispatcherAsync(async () =>
  {
    var client = new Client();
    var lostImages = NewGate<byte[]>();
    var lostAuto = NewGate();
    var firstAck = NewGate();
    var firstAckEntered = NewGate();
    var retryAckEntered = NewGate();
    int attempts = 0;
    client.OnImage = (session, _, _) => session == "doc-1" ? lostImages.Task : Task.FromResult(Client.Png);
    client.OnAuto = session => session == "doc-1" ? lostAuto.Task : Task.CompletedTask;
    client.OnAck = (session, request) =>
    {
      if (session != "doc-1" || request.Scope != Wire.PdfReadCancelScope.Viewport) return Task.CompletedTask;
      if (Interlocked.Increment(ref attempts) == 1)
      {
        firstAckEntered.TrySetResult();
        if (failFirstAck) return Task.FromException(new IOException("first scoped acknowledgement failed"));
      }
      else retryAckEntered.TrySetResult();
      return firstAck.Task;
    };
    await using var fixture = new Fixture(client);
    try
    {
      await fixture.OpenAsync("lost.pdf");
      await client.Images.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
      await fixture.CommandAsync(new SetPdfDemandCommand(0, [0], 160, 1600));
      await client.Images.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
      await client.Autos.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
      // The fake transport returned without a body. Its remote work should be treated
      // as live until the worker acknowledgement below proves the cutoff.
      lostImages.TrySetException(new IOException("lost image response"));
      lostAuto.TrySetException(new IOException("lost NDJSON response"));
      await firstAckEntered.Task.WaitAsync(Timeout, Ct);

      Assert.Equal(1, Volatile.Read(ref attempts));
      using var ready = new StateObserver(fixture.Handler, state => state.SessionId == "doc-2" && state.PagePreview is not null);
      await fixture.OpenAsync("other.pdf");
      if (failFirstAck) await retryAckEntered.Task.WaitAsync(Timeout, Ct);
      PdfDemandDiagnostics before = fixture.Handler.Pdf.DemandDiagnostics;
      Assert.Equal(2, before.OrdinaryIssued.Current);
      Assert.Equal(1, before.BackgroundIssued.Current);
      Assert.DoesNotContain(client.ImageLog, read => read.Item1 == "doc-2");
      Assert.False(client.Autos.Reader.TryRead(out _));
      Assert.False(ready.Next.IsCompleted);

      firstAck.TrySetResult();
      Assert.NotNull((await ready.Next.WaitAsync(Timeout, Ct)).PagePreview);
      Assert.Equal("doc-2", await client.Autos.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct));
    }
    finally { lostImages.TrySetResult(Client.Png); lostAuto.TrySetResult(); firstAck.TrySetResult(); }
  });

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public Task CloseRetryStillRequiresAckAfterRemoteCloseFailsOrLosesItsResponse(bool removed) => OnDispatcherAsync(async () =>
  {
    var client = new Client();
    var calls = new List<string>();
    bool first = true, rejectAck = false;
    client.OnAck = (_, request) =>
    {
      if (request.Scope != Wire.PdfReadCancelScope.AllReads) return Task.CompletedTask;
      calls.Add("ack");
      return rejectAck
        ? Task.FromException(new InferenceClientException(
          removed ? HttpV2ErrorCode.ResourceNotFound : HttpV2ErrorCode.RuntimeBusy, "injected non-success ACK", retryable: false))
        : Task.CompletedTask;
    };
    client.OnClose = _ =>
    {
      calls.Add("close");
      if (!first) return Task.CompletedTask;
      first = false;
      return removed ? Task.FromException(new IOException("close committed before response loss"))
        : Task.FromException(new InvalidOperationException("close remains CLOSING"));
    };
    await using var fixture = new Fixture(client);
    await fixture.OpenAsync("retry-close.pdf");
    await fixture.CommandAsync(new ClosePdfCommand());
    Assert.True(Assert.Single(fixture.Handler.PdfDocuments).Model.HasSession);
    Assert.Equal(new[] { "ack", "close" }, calls);
    rejectAck = true;
    await fixture.CommandAsync(new ClosePdfCommand());
    Assert.True(Assert.Single(fixture.Handler.PdfDocuments).Model.HasSession);
    Assert.Equal(new[] { "ack", "close", "ack" }, calls);

    rejectAck = false;
    await fixture.CommandAsync(new ClosePdfCommand());
    Assert.Empty(fixture.Handler.PdfDocuments);
    Assert.Equal(new[] { "ack", "close", "ack", "ack", "close" }, calls);
  });

  // Production enters this owner from WinUI's dispatcher. A single reader
  // gives async test continuations the same serialization without a UI window.
  private static async Task OnDispatcherAsync(Func<Task> test)
  {
    var context = new TestDispatcher();
    Task? completion = null;
    async Task ExecuteAsync()
    {
      try { await test(); }
      finally { context.Queue.Writer.TryComplete(); }
    }
    context.Post(_ => completion = ExecuteAsync(), null);
    await foreach (var work in context.Queue.Reader.ReadAllAsync())
    {
      SynchronizationContext? previous = SynchronizationContext.Current;
      SynchronizationContext.SetSynchronizationContext(context);
      try { work.Callback(work.State); }
      finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    await completion!;
  }

  private sealed class TestDispatcher : SynchronizationContext
  {
    internal Channel<(SendOrPostCallback Callback, object? State)> Queue { get; } =
      Channel.CreateUnbounded<(SendOrPostCallback, object?)>(new UnboundedChannelOptions { SingleReader = true });
    public override void Post(SendOrPostCallback callback, object? state) => Queue.Writer.TryWrite((callback, state));
  }

  private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
  private static TaskCompletionSource<T> NewGate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

  private sealed class StateObserver : IDisposable
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly Action<WorkbenchState> receive;
    private readonly TaskCompletionSource<PdfWorkbenchState> result = NewGate<PdfWorkbenchState>();
    internal StateObserver(DesktopWorkbenchCommandHandler handler, Func<PdfWorkbenchState, bool> matches)
    {
      this.handler = handler;
      receive = state => { if (state is PdfWorkbenchState pdf && matches(pdf)) result.TrySetResult(pdf); };
      handler.StateChanged += receive;
    }
    internal Task<PdfWorkbenchState> Next => result.Task;
    public void Dispose() => handler.StateChanged -= receive;
  }

  private sealed class Fixture : IAsyncDisposable
  {
    private readonly string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-demand-{Guid.NewGuid():N}");
    private readonly WorkbenchAnnotationStore annotations;
    internal WorkbenchResourceBroker Broker { get; }
    internal DesktopWorkbenchCommandHandler Handler { get; }

    internal Fixture(Client client, Func<PdfDocumentEntry, Task<PdfCloseDecision>>? confirm = null)
    {
      Directory.CreateDirectory(root);
      Broker = new(root);
      annotations = new(root);
      Handler = new(
        static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
        () =>
        {
          var model = new PdfViewModel(client, new Source());
          model.SetInspectionCapabilities(["pdf.demand.v1", "pdf.page-inspect.v1", "pdf.block-edit.v1"]);
          return model;
        },
        static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])), Broker, root, static () => 0, annotations,
        confirmPdfClose: confirm ?? (_ => Task.FromResult(PdfCloseDecision.Discard)));
    }

    internal Task<PdfWorkbenchState> OpenAsync(string name) => StateAsync(new OpenDroppedPdfCommand(Path.Combine(root, name)));
    internal async Task<WorkbenchCommandOutcome> CommandAsync(WorkbenchCommand command)
    {
      WorkbenchCommandOutcome result = await Handler.ExecuteAsync(command, Ct).AsTask().WaitAsync(Timeout, Ct);
      Assert.Null(result.Error);
      return result;
    }
    internal async Task<PdfWorkbenchState> StateAsync(WorkbenchCommand command) =>
      Assert.IsType<PdfWorkbenchState>(Assert.Single((await CommandAsync(command)).States));
    internal async Task<PdfWorkbenchState> SelectReadyAsync(int index)
    {
      bool Ready(PdfWorkbenchState state) => state.SelectedPage == index && state.PageInspectStatusCode == "pdf.inspect.ready" && state.PagePreview is not null;
      using var observer = new StateObserver(Handler, Ready);
      PdfWorkbenchState state = await StateAsync(new SetCurrentPdfPageCommand(index));
      return Ready(state) ? state : await observer.Next.WaitAsync(Timeout, Ct);
    }

    public async ValueTask DisposeAsync()
    {
      await Handler.DisposeAsync();
      annotations.Dispose(); Broker.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }

  private sealed class Source : IPdfFileSource
  {
    public Task<string?> PickFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);
  }

  private sealed class Client(int pages = 3) : InferenceClientStub, IInferenceClient
  {
    internal static readonly byte[] Png = Convert.FromBase64String(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1ZcAAAAASUVORK5CYII=");
    private static readonly JsonElement[] Rect = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0),
      JsonSerializer.SerializeToElement(300), JsonSerializer.SerializeToElement(500)];
    private readonly Dictionary<string, JobItem[]> jobs = [];
    private int opens, activeReads, maxReads;
    internal int MaximumImageReads => Volatile.Read(ref maxReads);
    internal int LegacyLoads;
    internal Func<string, string, int, Task<byte[]>>? OnImage;
    internal Func<string, Task>? OnAuto, OnWrite, OnClose;
    internal Func<int, int, Task<byte[]>>? OnPreview;
    internal Func<string, Wire.PdfReadCancelRequest, Task>? OnAck;
    internal ConcurrentQueue<(string, int)> Rotations { get; } = [];
    internal ConcurrentQueue<(string, Wire.PdfReadCancelScope)> Acks { get; } = [];
    internal ConcurrentQueue<(string, string, int)> ImageLog { get; } = [];
    internal ConcurrentQueue<int> Inspections { get; } = [];
    internal ConcurrentQueue<string> Closed { get; } = [];
    internal ConcurrentQueue<int[]> ExplicitPresence { get; } = [];
    internal TaskCompletionSource Acknowledged { get; } = NewGate();
    internal Channel<(string, string, int)> Images { get; } = Channel.CreateUnbounded<(string, string, int)>();
    internal Channel<string> Autos { get; } = Channel.CreateUnbounded<string>();

    private static Wire.PdfPageInfoMirror Page(int index, bool text = false) => new()
      { PageIndex = index, Rect = Rect, HasTextLayer = text, HasOcrTextLayer = text };
    private Wire.PdfDocumentMirror Model() => new() { Pages = Enumerable.Range(0, pages).Select(index => Page(index)).ToArray() };

    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct) =>
      Task.FromResult(new PdfSessionOpenResult($"doc-{++opens}", pages, path, Model()));
    public override Task<Wire.PdfDocumentMirror> GetPdfModelAsync(string session, CancellationToken ct) => Task.FromResult(Model());
    public override Task LoadPdfAsync(string session, Action<JsonElement> progress, CancellationToken ct) { LegacyLoads++; return Task.CompletedTask; }

    public async Task LoadPdfDemandAsync(string session, Wire.PdfDemandLoadRequest request, Action<JsonElement> progress, CancellationToken ct)
    {
      if (request.Pages is null)
      {
        Autos.Writer.TryWrite(session);
        if (OnAuto is { } gate) await gate(session);
      }
      else ExplicitPresence.Enqueue(request.Pages.ToArray());
      int[] target = request.Pages?.ToArray() ?? Enumerable.Range(0, pages).ToArray();
      for (int i = 0; i < target.Length; i++)
        progress(JsonSerializer.SerializeToElement(new { phase = "load", current = i + 1, total = target.Length, page_payload = Page(target[i]) }));
      progress(JsonSerializer.SerializeToElement(new { phase = "load", current = target.Length, total = target.Length, message = "done" }));
    }

    public async Task CancelPdfReadsAsync(string session, Wire.PdfReadCancelRequest request, CancellationToken ct)
    {
      Acks.Enqueue((session, request.Scope));
      if (OnAck is { } gate) await gate(session, request);
      Acknowledged.TrySetResult();
    }

    private async Task<byte[]> ReadImageAsync(string session, string kind, int page, int? previewTier = null)
    {
      int working = Interlocked.Increment(ref activeReads);
      if (working > maxReads) Interlocked.Exchange(ref maxReads, working);
      Images.Writer.TryWrite((session, kind, page));
      ImageLog.Enqueue((session, kind, page));
      try
      {
        if (previewTier is { } tier && OnPreview is { } preview) return await preview(page, tier);
        return OnImage is { } gate ? await gate(session, kind, page) : Png;
      }
      finally { Interlocked.Decrement(ref activeReads); }
    }

    public Task<byte[]> RenderPdfThumbnailDemandAsync(string session, Wire.RenderThumbnailRequest request, CancellationToken ct) =>
      ReadImageAsync(session, "thumbnail", request.Page);
    public Task<byte[]> RenderPdfPreviewDemandAsync(string session, Wire.RenderPreviewRequest request, CancellationToken ct) =>
      ReadImageAsync(session, request.Dpi is null ? "preview" : "ocr", request.Page, request.MaxEdge);
    public Task<Wire.PageInspectResponse> InspectPdfPageDemandAsync(string session, Wire.PageInspectRequest request, CancellationToken ct)
    {
      Inspections.Enqueue(request.Page);
      return Task.FromResult(new Wire.PageInspectResponse { SchemaVersion = 2, InstanceId = "test",
        Page = request.Page, Rotation = 0, Rect = Rect, OcrBlocks = [], NativeLines = [] });
    }
    public override Task<PdfMutateResult> RotatePdfPagesAsync(string session, int[] targets, int angle, CancellationToken ct)
    {
      Rotations.Enqueue((session, Assert.Single(targets)));
      return Task.FromResult(new PdfMutateResult(pages, new Wire.ModelDiff { ModifiedFlag = true,
        ReplacedPages = targets.Select(index => Page(index) with { Rotation = angle }).ToArray() }));
    }
    public override Task<PdfMutateResult> ReorderPdfAsync(string session, int[] newOrder, CancellationToken ct) =>
      Task.FromResult(new PdfMutateResult(pages, new Wire.ModelDiff { ModifiedFlag = true }));
    public override Task ClosePdfSessionAsync(string session, CancellationToken ct)
    {
      Closed.Enqueue(session); return OnClose?.Invoke(session) ?? Task.CompletedTask;
    }

    public override Task<JobRef> SubmitAsync(SubmitRequest request, IReadOnlyDictionary<string, SubmitUpload> uploads, CancellationToken ct)
    {
      string id = "job-" + jobs.Count;
      JobItem[] items = request.Items.Select((item, index) => new JobItem
        { ItemId = id + "-" + index, ClientItemKey = item.ClientItemKey, Ordinal = index, DisplayName = item.DisplayName, State = ItemState.Queued }).ToArray();
      jobs[id] = items;
      return Task.FromResult(new JobRef { JobId = id, Items = items });
    }

    public override Task<JobUpdate> ObserveAsync(string id, int sequence, CancellationToken ct) =>
      Task.FromResult(new JobUpdate
      {
        Events = [], ThroughSequence = sequence + 1,
        Snapshot = new JobSnapshot { JobId = id, Kind = JobKind.Recognition, Priority = JobPriority.Background,
          State = JobState.Completed, Items = jobs[id] },
        Outcomes = jobs[id].Select(item => new ItemOutcome { ItemId = item.ItemId, Attempt = 1, State = ItemState.Succeeded,
          PayloadType = "ocr.v1", Payload = new Dictionary<string, JsonElement>
          {
            ["raw_text"] = JsonSerializer.SerializeToElement("selected"),
            ["text_blocks"] = JsonSerializer.SerializeToElement(new[] { new { text = "selected", score = .9, bbox = new[] { 10, 20, 80, 40 } } }),
            ["preproc_angle"] = JsonSerializer.SerializeToElement(0),
          } }).ToArray(),
      });

    public override async Task<Wire.PdfMutationResponse> AddPdfTextLayersAsync(string session, Wire.BatchAddTextLayerRequest request, CancellationToken ct)
    {
      if (OnWrite is { } gate) await gate(session);
      return new()
      {
        SchemaVersion = 2, InstanceId = "test", Diff = new Wire.ModelDiff
          { ReplacedPages = request.Pages.Select(page => Page(page.Page, true)).ToArray(), ModifiedFlag = true },
        Extra = new Dictionary<string, JsonElement>
          { ["results"] = JsonSerializer.SerializeToElement(request.Pages.ToDictionary(page => page.Page.ToString(), _ => new[] { 1, 0 })) },
      };
    }
  }
}
