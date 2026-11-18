using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  public Task OcrPreparesOnlyOnePageAtATimeAndDrainsBeforeWrite(bool nextReadFails, bool pageChanged) =>
    OnDispatcherAsync(async () =>
  {
    var client = new Client(9);
    var terminal = NewGate();
    var terminalRead = NewGate();
    var nextIssued = NewGate();
    var nextRead = NewGate();
    var firstWrite = NewGate();
    int nextReads = 0;
    client.BeforeObserve = async (id, ct) =>
    {
      if (id != "job-0") return;
      await terminal.Task.WaitAsync(ct);
      terminalRead.TrySetResult();
    };
    client.OnImage = async (_, kind, page) =>
    {
      if (kind == "ocr" && page == 4 && ++nextReads == 1)
      {
        nextIssued.TrySetResult();
        await nextRead.Task;
        if (nextReadFails) throw new IOException("injected next-batch render failure");
      }
      return Client.Png;
    };
    client.OnWrite = _ => { firstWrite.TrySetResult(); return Task.CompletedTask; };
    await using var fixture = new Fixture(client);
    try
    {
      await fixture.OpenAsync("ocr-overlap.pdf");
      PdfViewModel model = fixture.Handler.PdfDocuments[0].Model;
      await WaitUntilAsync(() => model.DetectedCount == 9);
      await fixture.SelectReadyAsync(0);
      Task run = model.StartOcrAsync(Enumerable.Range(0, 8).ToArray(), false, Ct, addTextLayer: true);
      await nextIssued.Task.WaitAsync(Timeout, Ct);
      Assert.Single(client.Submitted);
      Assert.Equal("ocr", model.Phase);
      Assert.DoesNotContain(client.ImageLog, image => image.Item2 == "ocr" && image.Item3 > 4);

      // A held speculative native read leaves room for the newly selected viewport.
      await fixture.SelectReadyAsync(8);
      Assert.Equal(2, client.MaximumImageReads);
      if (pageChanged) model.Pages[4].ContentGeneration++;
      terminal.TrySetResult();
      await terminalRead.Task.WaitAsync(Timeout, Ct);
      await Task.Yield();
      Assert.False(firstWrite.Task.IsCompleted);
      Assert.DoesNotContain(client.Acks, ack => ack.Item2 == Wire.PdfReadCancelScope.AllReads);

      nextRead.TrySetResult();
      await run.WaitAsync(Timeout, Ct);
      Assert.Equal(pageChanged ? 2 : 1, nextReads);
      Assert.Equal(Enumerable.Range(0, 4).Select(index => $"page-{index}"), client.Submitted.First());
      Assert.Equal(Enumerable.Range(nextReadFails ? 5 : 4, nextReadFails ? 3 : 4).Select(index => $"page-{index}"),
        client.Submitted.Last());
      Assert.Equal(Enumerable.Range(0, 8).Where(index => !nextReadFails || index != 4), client.Written);
      Assert.Equal(nextReadFails ? PdfPageState.Failed : PdfPageState.Done, model.Pages[4].State);
      Assert.True(model.IsModified);
    }
    finally { terminal.TrySetResult(); nextRead.TrySetResult(); }
  });

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public Task CancellingOcrRevokesQueuedSpeculationOrDrainsAdmittedRead(bool queued) => OnDispatcherAsync(async () =>
  {
    var client = new Client(13);
    var speculate = NewGate();
    var nativeRead = NewGate();
    var revoked = NewGate();
    var remoteTerminal = NewGate();
    var cancelled = NewGate();
    int nativeRenders = 0;
    client.BeforeObserve = async (id, ct) =>
    {
      if (id != "job-1") return;
      if (ct.CanBeCanceled) await remoteTerminal.Task.WaitAsync(ct);
      else { cancelled.TrySetResult(); await remoteTerminal.Task; }
    };
    client.OnImage = async (_, kind, page) =>
    {
      if (kind == "ocr" && page == 8)
      {
        if (!queued) nativeRenders++;
        speculate.TrySetResult();
        await nativeRead.Task;
      }
      return Client.Png;
    };
    // The queued request owns no native lease. It can finish on the watermark ACK
    // while the native slot remains held by another request.
    client.OnDemandPreview = async (session, request) =>
    {
      if (!queued || request.Page != 8 || request.Dpi is null)
        return await client.ReadImageForPreviewAsync(session, request);
      long epoch = Assert.IsType<Wire.PdfReadContext>(request.Read).Epoch;
      client.OnAck = (_, cutoff) =>
      {
        if (cutoff.Scope == Wire.PdfReadCancelScope.Background && cutoff.BeforeEpoch > epoch)
          revoked.TrySetResult();
        return Task.CompletedTask;
      };
      speculate.TrySetResult();
      if (await Task.WhenAny(nativeRead.Task, revoked.Task) == revoked.Task)
        throw new IOException("old epoch revoked before acquiring native gate");
      nativeRenders++;
      return Client.Png;
    };
    client.FailPage = "page-0";
    await using var fixture = new Fixture(client);
    try
    {
      await fixture.OpenAsync("cancel-overlap.pdf");
      PdfViewModel model = fixture.Handler.PdfDocuments[0].Model;
      await WaitUntilAsync(() => model.DetectedCount == 13);
      await fixture.SelectReadyAsync(0);
      Task run = model.StartOcrAsync(Enumerable.Range(0, 12).ToArray(), false, Ct, addTextLayer: true);
      await speculate.Task.WaitAsync(Timeout, Ct);
      model.Cancel();
      await cancelled.Task.WaitAsync(Timeout, Ct);
      remoteTerminal.TrySetResult();
      if (!queued)
      {
        await Task.Yield();
        Assert.False(run.IsCompleted);
        Assert.True(model.IsSettling);
        nativeRead.TrySetResult();
      }
      await run.WaitAsync(Timeout, Ct);
      Assert.Equal(queued ? 0 : 1, nativeRenders);
      if (queued) Assert.True(revoked.Task.IsCompletedSuccessfully);
      Assert.False(model.IsSettling);
      Assert.True(model.IsModified);
      Assert.Equal(new[] { 1, 2, 3 }, client.Written);
      Assert.Equal(PdfPageState.Failed, model.Pages[0].State);
      Assert.All(model.Pages.Skip(1).Take(3), page => Assert.Equal(PdfPageState.Done, page.State));
      Assert.All(model.Pages.Skip(4), page => Assert.Equal(PdfPageState.None, page.State));
      Assert.DoesNotContain(client.ImageLog, image => image.Item2 == "ocr" && image.Item3 > 8);
      Assert.Equal(2, client.Submitted.Count);
    }
    finally { remoteTerminal.TrySetResult(); nativeRead.TrySetResult(); }
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
    var failHigh = NewGate();
    bool failing = true;
    int previewCalls = 0;
    client.OnPreview = async (page, tier) =>
    {
      if (page != 0) return Client.Png;
      Interlocked.Increment(ref previewCalls);
      if (!failing) return Client.Png;
      if (tier > 768)
      {
        await failHigh.Task;
        highFailed.TrySetResult();
      }
      else if (!failLow) return Client.Png;
      throw new InvalidOperationException("injected preview failure");
    };
    await using var fixture = new Fixture(client);
    using var failed = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" &&
      state.PageInspectStatusCode == "pdf.inspect.failed" && state.PageInspect is not null &&
      (state.PagePreview is null) == failLow && highFailed.Task.IsCompleted);
    using var inspected = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" && state.PageInspect is not null);
    try
    {
      await fixture.OpenAsync("preview.pdf");
      await inspected.Next.WaitAsync(Timeout, Ct);
    }
    finally { failHigh.TrySetResult(); }
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
  [InlineData("worker-error", false)]
  [InlineData("malformed", true)]
  [InlineData("missing-terminal", false)]
  [InlineData("bad-page", false)]
  public Task HttpFaultsKeepBothOrdinarySlotsAndAutoUntilAcknowledged(string streamFault, bool rejectFirstAck) =>
    OnDispatcherAsync(async () =>
    {
      using var http = new DemandHttpHandler(streamFault, rejectFirstAck);
      await using var transport = new InferenceHttpClient(new Uri("http://127.0.0.1:1"), "test-token", http);
      await using var fixture = new Fixture(transport);
      try
      {
        await fixture.OpenAsync("lost-http.pdf");
        await fixture.CommandAsync(new SetPdfDemandCommand(0, [0], 160, 1600));
        var issued = new HashSet<string>();
        for (int i = 0; i < 3; i++) issued.Add((await http.Reads.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct)).Operation);
        Assert.Equal(new[] { "load", "render_preview", "render_thumbnail" }, issued.Order());

        // Worker work becomes unobservable, while the real client parses the
        // HTTP problem/stream and the fake server gates its cutoff receipt.
        using var failedPreview = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" &&
          state.PageInspectStatusCode == "pdf.inspect.failed");
        using var failedDetection = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" &&
          state.Pages?.Any(page => page.Index == 1 && page.DetectionState == "failed") == true);
        http.RespondToOldPreview.TrySetResult();
        await failedPreview.Next.WaitAsync(Timeout, Ct);
        http.RespondToOldReads.TrySetResult();
        await failedDetection.Next.WaitAsync(Timeout, Ct);
        await http.FirstAckEntered.Task.WaitAsync(Timeout, Ct);
        Assert.Equal(1, http.AckAttempts);
        using var ready = new StateObserver(fixture.Handler, state => state.SessionId == "doc-2" && state.PagePreview is not null);
        using var incomplete = new StateObserver(fixture.Handler, state => state.SessionId == "doc-2" &&
          state.Pages?.FirstOrDefault()?.DetectionState == "failed");
        await fixture.OpenAsync("http-other.pdf");
        if (rejectFirstAck) await http.RetryAckEntered.Task.WaitAsync(Timeout, Ct);

        PdfDemandDiagnostics held = fixture.Handler.Pdf.DemandDiagnostics;
        Assert.Equal(2, held.OrdinaryIssued.Current);
        Assert.Equal(1, held.BackgroundIssued.Current);
        Assert.False(http.Reads.Reader.TryRead(out _));
        Assert.False(ready.Next.IsCompleted);

        http.ConfirmOldCutoff.TrySetResult();
        Assert.NotNull((await ready.Next.WaitAsync(Timeout, Ct)).PagePreview);
        bool receivedOtherLoad = false;
        while (!receivedOtherLoad)
        {
          var read = await http.Reads.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
          Assert.Equal("doc-2", read.Session);
          receivedOtherLoad = read.Operation == "load";
        }
        await incomplete.Next.WaitAsync(Timeout, Ct);
        Assert.Equal(0, http.OtherAckAttempts);
        Assert.Equal(0, fixture.Handler.Pdf.DemandDiagnostics.BackgroundIssued.Current);
      }
      finally
      {
        http.RespondToOldPreview.TrySetResult();
        http.RespondToOldReads.TrySetResult();
        http.ConfirmOldCutoff.TrySetResult();
      }
    });

  private sealed class DemandHttpHandler(string streamFault, bool rejectFirstAck) : HttpMessageHandler
  {
    private const string PageZero = """{"phase":"load","current":1,"total":3,"page_index":0,"page_payload":{"page_index":0,"rect":[0,0,300,500]}}""";
    private int openCalls, ackAttempts, otherAckAttempts;
    internal int AckAttempts => Volatile.Read(ref ackAttempts);
    internal int OtherAckAttempts => Volatile.Read(ref otherAckAttempts);
    internal readonly TaskCompletionSource RespondToOldReads = NewGate();
    internal readonly TaskCompletionSource RespondToOldPreview = NewGate();
    internal readonly TaskCompletionSource FirstAckEntered = NewGate();
    internal readonly TaskCompletionSource RetryAckEntered = NewGate();
    internal readonly TaskCompletionSource ConfirmOldCutoff = NewGate();
    internal readonly Channel<(string Session, string Operation)> Reads = Channel.CreateUnbounded<(string, string)>();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      string[] path = request.RequestUri!.AbsolutePath.Split('/');
      string session = path[^2], op = path[^1];
      switch (op)
      {
        case "open":
          using (JsonDocument input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
          {
            var model = new Wire.PdfDocumentMirror
            {
              FilePath = input.RootElement.GetProperty("path").GetString(),
              Pages = Enumerable.Range(0, 3).Select(page => new Wire.PdfPageInfoMirror { PageIndex = page }).ToArray(),
            };
            return Json(JsonSerializer.Serialize(new { session_id = $"doc-{++openCalls}", model }));
          }
        case "cancel":
          if (session == "doc-1")
          {
            if (Interlocked.Increment(ref ackAttempts) == 1)
            {
              FirstAckEntered.TrySetResult();
              if (rejectFirstAck) return Json("""{"cancelled":false}""");
            }
            else RetryAckEntered.TrySetResult();
            await ConfirmOldCutoff.Task;
          }
          else Interlocked.Increment(ref otherAckAttempts);
          return Json("""{"cancelled":true}""");
        case "render_preview":
        case "render_thumbnail":
          Reads.Writer.TryWrite((session, op));
          if (session == "doc-1")
          {
            await (op == "render_preview" ? RespondToOldPreview.Task : RespondToOldReads.Task);
            return Json("""{"schema_version":2,"instance_id":"test","code":"INTERNAL_ERROR","message":"worker transport lost","category":"internal","retryable":false,"detail":{}}""",
              HttpStatusCode.InternalServerError);
          }
          return new HttpResponseMessage(HttpStatusCode.OK)
          {
            Content = new ByteArrayContent(Client.Png) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } },
          };
        case "load":
          Reads.Writer.TryWrite((session, op));
          if (session == "doc-1")
          {
            await RespondToOldReads.Task;
            string body = streamFault switch
            {
              "worker-error" => """{"phase":"load","current":0,"total":3,"message":"error: worker disconnected"}""",
              "malformed" => "{invalid-json",
              "missing-terminal" => PageZero,
              "bad-page" => PageZero.Replace("300", "\"invalid\"", StringComparison.Ordinal),
              _ => throw new InvalidOperationException("Unknown stream test case."),
            };
            return Ndjson(body);
          }
          return Ndjson("""{"phase":"load","current":0,"total":3,"message":"incomplete"}""");
        case "page_inspect":
          return Json(JsonSerializer.Serialize(new Wire.PageInspectResponse
          {
            SchemaVersion = 2, InstanceId = "test", Page = 0, Rotation = 0, Rect = [], OcrBlocks = [], NativeLines = [],
          }));
        case "close":
          return Json("""{"ok":true}""");
        default:
          throw new InvalidOperationException($"Unexpected PDF request: {op}");
      }
    }

    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) =>
      new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ndjson(string content) =>
      new(HttpStatusCode.OK) { Content = new StringContent(content + "\n", Encoding.UTF8, "application/x-ndjson") };
  }

  [Fact]
  public Task UndetectedLandscapePageReceivesInspectionGeometryBeforeBackgroundDetectionArrives() => OnDispatcherAsync(async () =>
  {
    // 长文档后部横向页：open_doc 镜像 Rect 为零（IncludeOpenMirrorRects=false 模拟），
    // 后台全篇 presence 检测被挂起；当前页检查必须先回填真实显示空间几何，供前端
    // logicalSize/高清需求使用，且不得把文字层 unknown 冒充 known。
    var client = new Client(60) { IncludeOpenMirrorRects = false };
    var auto = NewGate();
    client.OnAuto = _ => auto.Task;
    client.SetPageRect(55, 842, 595);
    await using var fixture = new Fixture(client);
    try
    {
      await fixture.OpenAsync("long.pdf");
      PdfWorkbenchState selected = await fixture.SelectReadyAsync(55);
      PdfWorkbenchPage geometry = Assert.Single(selected.Pages!, page => page.Index == 55);
      Assert.Equal(842, geometry.Width);
      Assert.Equal(595, geometry.Height);
      Assert.False(geometry.Detected);
      Assert.Equal("unknown", geometry.DetectionState);
      Assert.True(selected.DetectedCount < 55);

      // 几何投递后，高清（更长边）档位的按需预览请求即可被调度（合成 PNG 仅
      // 证明档位/资源链路，真实清晰度由 runtime 渲染侧负责）。
      using var cleared = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" &&
        state.SelectedPage == 55 && state.PagePreview is not null &&
        client.PreviewRequests.Any(request => request.Page == 55 && request.MaxEdge == 2400));
      await fixture.CommandAsync(new SetPdfDemandCommand(55, [55], 160, 2400));
      PdfWorkbenchState highResolution = await cleared.Next.WaitAsync(Timeout, Ct);
      Assert.NotNull(highResolution.PagePreview);
      Assert.Contains(highResolution.Pages!, page => page.Index == 55 && page.Width == 842 && page.Height == 595);
      // 同步完成的 stub 不保证达到并发 2；约束是普通槽位上限从未被突破。
      Assert.InRange(client.MaximumImageReads, 1, 2);
      Assert.Equal(0, client.LegacyLoads);
      Assert.Equal(0, client.LegacyInspects + client.LegacyPreviews);
    }
    finally { auto.TrySetResult(); }
  });

  [Fact]
  public Task StaleInspectionCannotOverwriteRotatedPageGeometry() => OnDispatcherAsync(async () =>
  {
    var client = new Client(60) { IncludeOpenMirrorRects = false };
    var auto = NewGate();
    client.OnAuto = _ => auto.Task;
    client.SetPageRect(55, 842, 595);
    Wire.PageInspectResponse stale = client.InspectOf(55); // 旋转前的横向几何（迟到结果）
    var staleInspect = NewGate<Wire.PageInspectResponse>();
    int inspectCalls = 0;
    client.OnInspect = page => page == 55 && Interlocked.Increment(ref inspectCalls) == 1
      ? staleInspect.Task
      : Task.FromResult(client.InspectOf(page));
    await using var fixture = new Fixture(client);
    try
    {
      await fixture.OpenAsync("stale.pdf");
      PdfViewModel model = fixture.Handler.PdfDocuments[0].Model;
      await fixture.CommandAsync(new SetCurrentPdfPageCommand(55));
      await WaitUntilAsync(() => client.Inspections.Any(page => page == 55));
      // 检查在读途中执行写：写栅栏推进读 epoch，写后几何以 diff 镜像为准。
      long revisionBeforeRotate = model.Revision;
      await model.RotateAsync([55], 90, Ct);
      Assert.Equal(1, model.Pages[55].ContentGeneration);
      Assert.Equal(595, model.Pages[55].Width);
      Assert.Equal(842, model.Pages[55].Height);
      // 观察者先于释放创建：释放后的调度链可能在下一个 await 前就把 ready 发布完。
      using var settled = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" &&
        state.SelectedPage == 55 && state.PageInspectStatusCode == "pdf.inspect.ready");
      staleInspect.TrySetResult(stale);
      await WaitUntilAsync(() => client.Inspections.Count(page => page == 55) >= 2);
      await settled.Next.WaitAsync(Timeout, Ct);
      // 旧检查结果不得把写后几何改回旋转前的横向值，也不得回退旋转；dirty/修订
      // 保持写路径语义。
      Assert.Equal(595, model.Pages[55].Width);
      Assert.Equal(842, model.Pages[55].Height);
      Assert.Equal(90, model.Pages[55].Rotation);
      Assert.Equal(1, model.Pages[55].ContentGeneration);
      Assert.Equal(PdfDetectionState.Unknown, model.Pages[55].DetectionState);
      Assert.Equal(revisionBeforeRotate + 1, model.Revision);
      Assert.True(model.IsModified);
    }
    finally { auto.TrySetResult(); staleInspect.TrySetResult(stale); }
  });

  [Fact]
  public Task StructureChangePreservesBackfilledGeometryWithoutRepeatReads() => OnDispatcherAsync(async () =>
  {
    // 未检测横向页已由当前页检查回填几何（842×595/rotation 90）；随后删除其后
    // 页面：当前页 index/PageKey/Generation 不变，diff 为全量镜像且服务端模型
    // 仍 0 Rect/rotation 0（page_inspect 不回写）。不得把已回填几何归零，也不得
    // 靠重复 inspect/render 恢复。
    var client = new Client(60) { IncludeOpenMirrorRects = false };
    var auto = NewGate();
    client.OnAuto = _ => auto.Task;
    client.SetPageRect(55, 842, 595);
    client.SetPageRotation(55, 90);
    await using var fixture = new Fixture(client);
    try
    {
      await fixture.OpenAsync("structure.pdf");
      PdfViewModel model = fixture.Handler.PdfDocuments[0].Model;
      await fixture.SelectReadyAsync(55);
      long pageKey = model.Pages[55].PageKey;
      long revisionBefore = model.Revision;
      int inspects = client.Inspections.Count;
      int reads = client.ImageLog.Count;

      await model.DeletePagesAsync([59], Ct);
      Assert.Equal(59, model.PageCount);
      Assert.Equal(842, model.Pages[55].Width);
      Assert.Equal(595, model.Pages[55].Height);
      Assert.Equal(90, model.Pages[55].Rotation);
      Assert.Equal(pageKey, model.Pages[55].PageKey);
      Assert.Equal(0, model.Pages[55].ContentGeneration);
      Assert.Equal(PdfDetectionState.Unknown, model.Pages[55].DetectionState);
      Assert.Equal(0, model.DetectedCount);
      Assert.Equal(revisionBefore + 1, model.Revision);
      Assert.True(model.IsModified);
      Assert.Equal(inspects, client.Inspections.Count);
      Assert.Equal(reads, client.ImageLog.Count);

      // 投影同源：几何保留后当前页检查仍复用暖缓存，无需重复读取。
      PdfWorkbenchState after = await fixture.StateAsync(new SetPdfWindowCommand(0));
      PdfWorkbenchPage projected = Assert.Single(after.Pages!, page => page.Index == 55);
      Assert.Equal(842, projected.Width);
      Assert.Equal(595, projected.Height);
      Assert.Equal("pdf.inspect.ready", after.PageInspectStatusCode);
      Assert.Equal(inspects, client.Inspections.Count);
      Assert.Equal(reads, client.ImageLog.Count);
    }
    finally { auto.TrySetResult(); }
  });

  [Fact]
  public Task DemandSettingsChangeRefreshesThroughCoordinatorWithoutLegacyReads() => OnDispatcherAsync(async () =>
  {
    var client = new Client(3);
    var held = NewGate<byte[]>();
    client.OnImage = (_, _, _) => held.Task; // 两个普通读槽位均被在读占满
    await using var fixture = new Fixture(client);
    try
    {
      await fixture.OpenAsync("settings.pdf");
      PdfViewModel model = fixture.Handler.PdfDocuments[0].Model;
      await fixture.CommandAsync(new SetPdfDemandCommand(0, [0, 1], 160, null));
      Assert.Equal(("doc-1", "preview", 0), await client.Images.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct));
      Assert.Equal(("doc-1", "thumbnail", 0), await client.Images.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct));
      Assert.Equal(2, fixture.Handler.Pdf.DemandDiagnostics.OrdinaryIssued.Current);

      var settings = model.ProcessingSettings with { MaxPixels = 4_000_000 };
      await fixture.CommandAsync(new SetPdfProcessingSettingsCommand(settings));
      // demand 模式设置刷新不得发出无 read context 的 legacy 检查/预览读，
      // 也不得在两个普通槽位占满时追加第三份并发读取。
      Assert.Equal(0, client.LegacyInspects);
      Assert.Equal(0, client.LegacyPreviews);
      Assert.Equal(2, client.MaximumImageReads);
      Assert.False(client.Images.Reader.TryRead(out _));

      held.TrySetResult(Client.Png); // 旧 PixelLimit 键的读取完成即作废，按新键重排
      using var ready = new StateObserver(fixture.Handler, state => state.SessionId == "doc-1" &&
        state.PageInspectStatusCode == "pdf.inspect.ready" && state.PagePreview is not null &&
        state.ProcessingSettings == settings &&
        client.PreviewRequests.Any(request => request.Page == 0 && request.MaxPixels == 4_000_000));
      PdfWorkbenchState refreshed = await ready.Next.WaitAsync(Timeout, Ct);
      Assert.NotNull(refreshed.PagePreview);
      Assert.Equal(2, client.MaximumImageReads);
      Assert.Equal(0, client.LegacyInspects + client.LegacyPreviews);
      Assert.Equal(0, client.LegacyLoads);
      await WaitUntilAsync(() => fixture.Handler.Pdf.DemandDiagnostics.OrdinaryIssued.Current == 0);

      // RenderDpi 仅影响 OCR 业务渲染：按需预览键（MaxPixels）不变，不触发重读，
      // 协调器缓存的原帧继续投影。
      int reads = client.ImageLog.Count;
      var dpiSettings = settings with { RenderDpi = 150 };
      PdfWorkbenchState dpiState = await fixture.StateAsync(new SetPdfProcessingSettingsCommand(dpiSettings));
      Assert.Equal("pdf.inspect.ready", dpiState.PageInspectStatusCode);
      Assert.NotNull(dpiState.PagePreview);
      Assert.Equal(reads, client.ImageLog.Count);
      Assert.Equal(0, client.LegacyInspects + client.LegacyPreviews);
      Assert.Equal(2, client.MaximumImageReads);
    }
    finally { held.TrySetResult(Client.Png); }
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

  [Fact]
  public Task ClosedPdfKeepsRetryableJobOwnerWithoutRepeatingDirtyAuthorization() => OnDispatcherAsync(async () =>
  {
    var client = new Client();
    int confirms = 0, forgetAttempts = 0;
    bool connected = false;
    client.BeforeForget = _ =>
    {
      forgetAttempts++;
      if (!connected) throw new IOException("injected disconnect before forget");
    };
    await using var fixture = new Fixture(client, _ =>
    {
      confirms++;
      return Task.FromResult(PdfCloseDecision.Discard);
    });
    await fixture.OpenAsync("jobs.pdf");
    PdfViewModel model = Assert.Single(fixture.Handler.PdfDocuments).Model;
    await model.StartOcrAsync([0], false, Ct, addTextLayer: true);
    Assert.True(model.IsModified);

    PdfWorkbenchState pending = await fixture.StateAsync(new ClosePdfCommand());
    Assert.Null(pending.SessionId);
    Assert.True(Assert.Single(pending.Documents!).CloseFailed);
    Assert.True(model.HasPendingResultCleanup);
    Assert.True(model.IsModified);
    Assert.Equal(PdfPageState.Done, model.Pages[0].State);
    Assert.Equal(PdfSaveDisposition.Rejected, (await model.SaveAsync("closed.pdf", Ct)).Disposition);
    Assert.Equal(1, confirms);
    Assert.Equal(1, forgetAttempts);
    Assert.Equal(1, client.OwnedJobs);
    Assert.Equal("doc-1", Assert.Single(client.Closed));

    await fixture.OpenAsync("other.pdf");
    PdfWorkbenchState restored = await fixture.OpenAsync("jobs.pdf");
    Assert.Null(restored.SessionId);
    Assert.Equal(2, fixture.Handler.PdfDocuments.Count);
    Assert.Contains(restored.Documents!, doc => doc.CloseFailed);
    connected = true;
    PdfWorkbenchState remaining = await fixture.StateAsync(new ClosePdfCommand());
    Assert.Equal("doc-2", remaining.SessionId);
    Assert.Single(fixture.Handler.PdfDocuments);
    Assert.Equal(1, confirms);
    Assert.Equal(2, forgetAttempts);
    Assert.Equal(0, client.OwnedJobs);
    Assert.Equal("doc-1", Assert.Single(client.Closed));
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

  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    DateTime deadline = DateTime.UtcNow + Timeout;
    while (!condition())
    {
      Assert.True(DateTime.UtcNow < deadline, "Expected condition was not met within the timeout.");
      await Task.Delay(10, Ct);
    }
  }

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

    internal Fixture(IInferenceClient client, Func<PdfDocumentEntry, Task<PdfCloseDecision>>? confirm = null)
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
    internal static readonly byte[] Png = TestPng;
    private static readonly JsonElement[] Rect = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0),
      JsonSerializer.SerializeToElement(300), JsonSerializer.SerializeToElement(500)];
    private readonly Dictionary<string, JobItem[]> jobs = [];
    private readonly ConcurrentDictionary<int, JsonElement[]> pageRects = new();
    private readonly ConcurrentDictionary<int, int> pageRotations = new();
    private int opens, activeReads, maxReads, legacyInspects, legacyPreviews;
    private int remaining = -1;
    internal int MaximumImageReads => Volatile.Read(ref maxReads);
    internal int LegacyLoads;
    internal Func<string, string, int, Task<byte[]>>? OnImage;
    internal Func<string, Task>? OnAuto, OnWrite, OnClose;
    internal Func<int, int, Task<byte[]>>? OnPreview;
    internal Func<int, Task<Wire.PageInspectResponse>>? OnInspect;
    internal Func<string, Wire.PdfReadCancelRequest, Task>? OnAck;
    internal Func<string, Wire.RenderPreviewRequest, Task<byte[]>>? OnDemandPreview;
    internal Func<string, CancellationToken, Task>? BeforeObserve;
    internal Action<string>? BeforeForget;
    internal int OwnedJobs => jobs.Count;
    internal string? FailPage { get; set; }
    internal bool IncludeOpenMirrorRects { get; set; } = true;
    internal int LegacyInspects => Volatile.Read(ref legacyInspects);
    internal int LegacyPreviews => Volatile.Read(ref legacyPreviews);
    internal ConcurrentQueue<(int Page, int? MaxEdge, int? MaxPixels)> PreviewRequests { get; } = [];
    internal ConcurrentQueue<(string, int)> Rotations { get; } = [];
    internal ConcurrentQueue<(string, Wire.PdfReadCancelScope)> Acks { get; } = [];
    internal ConcurrentQueue<(string, string, int)> ImageLog { get; } = [];
    internal ConcurrentQueue<int> Inspections { get; } = [];
    internal ConcurrentQueue<string> Closed { get; } = [];
    internal ConcurrentQueue<int[]> ExplicitPresence { get; } = [];
    internal ConcurrentQueue<string[]> Submitted { get; } = [];
    internal ConcurrentQueue<int> Written { get; } = [];
    internal TaskCompletionSource Acknowledged { get; } = NewGate();
    internal Channel<(string, string, int)> Images { get; } = Channel.CreateUnbounded<(string, string, int)>();
    internal Channel<string> Autos { get; } = Channel.CreateUnbounded<string>();

    private static JsonElement[] RectOf(double x0, double y0, double x1, double y1) =>
      [JsonSerializer.SerializeToElement(x0), JsonSerializer.SerializeToElement(y0), JsonSerializer.SerializeToElement(x1), JsonSerializer.SerializeToElement(y1)];
    internal void SetPageRect(int index, double width, double height) => pageRects[index] = RectOf(0, 0, width, height);
    internal void SetPageRotation(int index, int rotation) => pageRotations[index] = ((rotation % 360) + 360) % 360;
    // 与真实后端一致：结构变更回 _diff_full；未检测页 Rect=(0,0,0,0)/rotation 0。
    private static readonly JsonElement[] ZeroRect = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0),
      JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0)];
    private Wire.PdfDocumentMirror UndetectedFullModel() => new()
    { Pages = Enumerable.Range(0, remaining).Select(index => new Wire.PdfPageInfoMirror
        { PageIndex = index, Rect = ZeroRect }).ToArray() };
    public override Task<PdfMutateResult> DeletePdfPagesAsync(string session, int[] targets, CancellationToken ct)
    {
      if (remaining < 0) remaining = pages;
      remaining -= targets.Length;
      return Task.FromResult(new PdfMutateResult(remaining, new Wire.ModelDiff
        { FullModel = UndetectedFullModel(), ModifiedFlag = true }));
    }
    private JsonElement[] RectOf(int index) => pageRects.TryGetValue(index, out JsonElement[]? rect) ? rect : Rect;
    internal Wire.PageInspectResponse InspectOf(int page) => new() { SchemaVersion = 2, InstanceId = "test",
      Page = page, Rotation = pageRotations.TryGetValue(page, out int rotation) ? rotation : 0,
      Rect = RectOf(page), OcrBlocks = [], NativeLines = [] };
    private Wire.PdfPageInfoMirror Page(int index, bool text = false) => new()
      { PageIndex = index, Rect = RectOf(index), HasTextLayer = text, HasOcrTextLayer = text };
    // open_doc 镜像几何默认携带；置 false 模拟“检测未抵达时 Rect 为零/缺失”。
    private Wire.PdfDocumentMirror Model() => new() { Pages = Enumerable.Range(0, pages).Select(index =>
      IncludeOpenMirrorRects ? Page(index) : Page(index) with { Rect = null }).ToArray() };

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
    public Task<byte[]> RenderPdfPreviewDemandAsync(string session, Wire.RenderPreviewRequest request, CancellationToken ct)
    {
      PreviewRequests.Enqueue((request.Page, request.MaxEdge, request.MaxPixels));
      return OnDemandPreview?.Invoke(session, request) ?? ReadImageForPreviewAsync(session, request);
    }
    internal Task<byte[]> ReadImageForPreviewAsync(string session, Wire.RenderPreviewRequest request) =>
      ReadImageAsync(session, request.Dpi is null ? "preview" : "ocr", request.Page, request.MaxEdge);
    public async Task<Wire.PageInspectResponse> InspectPdfPageDemandAsync(string session, Wire.PageInspectRequest request, CancellationToken ct)
    {
      Inspections.Enqueue(request.Page);
      if (OnInspect is { } gate) return await gate(request.Page);
      return InspectOf(request.Page);
    }
    // legacy 无 read context 读：计数并计入并发观测（不得出现在 demand 路径）。
    public override Task<Wire.PageInspectResponse> InspectPdfPageAsync(string session, int page, CancellationToken ct)
    {
      Interlocked.Increment(ref legacyInspects);
      int working = Interlocked.Increment(ref activeReads);
      if (working > maxReads) Interlocked.Exchange(ref maxReads, working);
      Interlocked.Decrement(ref activeReads);
      return Task.FromResult(InspectOf(page));
    }
    public override Task<byte[]> RenderPdfPreviewAsync(string session, int page, int dpi, CancellationToken ct)
    {
      Interlocked.Increment(ref legacyPreviews);
      return ReadImageAsync(session, "legacy-preview", page);
    }
    public override Task<PdfMutateResult> RotatePdfPagesAsync(string session, int[] targets, int angle, CancellationToken ct)
    {
      Rotations.Enqueue((session, Assert.Single(targets)));
      foreach (int index in targets)
      {
        // 显示空间 90° 旋转交换宽高（与真实 set_rotation 后 page.rect 一致）。
        JsonElement[] rect = RectOf(index);
        pageRects[index] = RectOf(0, 0, rect[3].GetDouble() - rect[1].GetDouble(), rect[2].GetDouble() - rect[0].GetDouble());
        pageRotations[index] = ((pageRotations.TryGetValue(index, out int rotation) ? rotation : 0) + angle) % 360;
      }
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
      Submitted.Enqueue(request.Items.Select(item => item.ClientItemKey).ToArray());
      string id = "job-" + jobs.Count;
      JobItem[] items = request.Items.Select((item, index) => new JobItem
        { ItemId = id + "-" + index, ClientItemKey = item.ClientItemKey, Ordinal = index, DisplayName = item.DisplayName, State = ItemState.Queued }).ToArray();
      jobs[id] = items;
      return Task.FromResult(new JobRef { JobId = id, Items = items });
    }

    public override async Task<JobUpdate> ObserveAsync(string id, int sequence, CancellationToken ct)
    {
      if (BeforeObserve is { } gate) await gate(id, ct);
      return new JobUpdate
      {
        Events = [], ThroughSequence = sequence + 1,
        Snapshot = new JobSnapshot { JobId = id, Kind = JobKind.Recognition, Priority = JobPriority.Background,
          State = JobState.Completed, Items = jobs[id] },
        Outcomes = jobs[id].Select(item => new ItemOutcome { ItemId = item.ItemId, Attempt = 1,
          State = item.ClientItemKey == FailPage ? ItemState.Failed : ItemState.Succeeded,
          PayloadType = "ocr.v1", Payload = new Dictionary<string, JsonElement>
          {
            ["raw_text"] = JsonSerializer.SerializeToElement("selected"),
            ["text_blocks"] = JsonSerializer.SerializeToElement(new[] { new { text = "selected", score = .9, bbox = new[] { 10, 20, 80, 40 } } }),
            ["preproc_angle"] = JsonSerializer.SerializeToElement(0),
          } }).ToArray(),
      };
    }

    public override Task<JobCommandResult> CommandAsync(JobCommand command, CancellationToken ct)
    {
      if (command.Kind == JobCommandKind.Forget)
      {
        BeforeForget?.Invoke(command.JobId);
        if (!jobs.Remove(command.JobId))
          throw new InferenceClientException(HttpV2ErrorCode.JobNotFound, "job was already released", retryable: false);
      }
      return Task.FromResult(new JobCommandResult(command.CommandId, command.Kind,
        command.Kind == JobCommandKind.Cancel ? CancelMode.Cooperative : null, null));
    }

    public override async Task<Wire.PdfMutationResponse> AddPdfTextLayersAsync(string session, Wire.BatchAddTextLayerRequest request, CancellationToken ct)
    {
      if (OnWrite is { } gate) await gate(session);
      foreach (var page in request.Pages) Written.Enqueue(page.Page);
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
