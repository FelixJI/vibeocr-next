using System.Buffers.Binary;
using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.App.Workbench;

internal sealed record PdfDemandGauge(long Current, long Peak);
internal sealed record PdfDemandDiagnostics(
  PdfDemandGauge OrdinaryIssued,
  PdfDemandGauge BackgroundIssued,
  PdfDemandGauge Pending,
  PdfDemandGauge WarmBytes,
  PdfDemandGauge WarmPixels,
  PdfDemandGauge ActiveImages,
  PdfDemandGauge ActiveBytes,
  PdfDemandGauge ActivePixels,
  PdfDemandGauge UnreclaimedFiles);

// All state is owned by the workbench dispatcher. HTTP operations remain issued
// until the body and any resource adoption/revocation finish, even after demand changes.
internal sealed class PdfDemandCoordinator(
  WorkbenchResourcesOwner resources,
  WorkbenchOperations operations,
  Action<PdfViewModel> publish)
{
  private const int FirstPreviewEdge = 768;
  private const int DefaultPreviewEdge = 1600;
  private const int DefaultThumbnailEdge = 160;
  private const int PendingLimit = 16;
  private const int MaxPreviewPixels = 16_000_000;
  private const long WarmBytes = 32L * 1024 * 1024;
  private const long WarmPixels = 32L * 1024 * 1024;

  internal sealed record ViewportDemand(int Page, int[] VisiblePages, int ThumbnailSize, int PreviewMaxEdge);
  internal sealed record Projection(WorkbenchResourceReference? Preview, WorkbenchResourceReference? Inspect, string InspectStatus);
  private enum Kind { Preview, Inspect, Thumbnail }
  private readonly record struct Key(string Session, long Page, long Generation, Kind Kind, int Tier, int PixelLimit);
  private sealed record Job(Key Key, int Index);
  private sealed record UnconfirmedRead(Session Session, Wire.PdfReadScope Scope, long Epoch, Action Release);
  private sealed class Entry(Key key)
  {
    internal Key Key { get; } = key;
    internal WorkbenchResourceReference? Resource;
    internal Wire.PageInspectResponse? Inspect;
    internal int InspectIndex = -1;
    internal long Bytes;
    internal long Pixels;
    internal LinkedListNode<Entry>? Warm;
  }

  private sealed class Session(string id, PdfViewModel model, long epoch)
  {
    internal string Id { get; } = id;
    internal PdfViewModel Model { get; } = model;
    internal SemaphoreSlim Control { get; } = new(1);
    internal long Viewport = epoch, ViewportAck = epoch;
    internal long Background = epoch, BackgroundAck = epoch;
    internal bool Writing, Foreground, AutoAttempted, Blocked;
    internal int Reads;
    internal TaskCompletionSource? Drained;
    internal readonly HashSet<Key> Failed = [];
    internal readonly HashSet<Key> FulfilledThumbnails = [];
    internal ViewportDemand? Demand;
  }

  private readonly Dictionary<string, Session> sessions = [];
  private readonly Dictionary<Key, Entry> warm = [];
  private readonly LinkedList<Entry> lru = [];
  private readonly HashSet<Key> issued = [];
  private readonly HashSet<Session> handshakes = [];
  private readonly List<UnconfirmedRead> unconfirmedReads = [];
  private readonly SemaphoreSlim ordinary = new(2);
  private readonly SemaphoreSlim writes = new(1);
  private readonly List<WorkbenchResourceReference> unreclaimed = [];
  private Session? active;
  private Session? auto;
  private Entry? current, previous;
  private long epoch, warmBytes, warmPixels;
  private int foregroundWaiters, pending, pendingPeak, ordinaryIssued, ordinaryPeak, backgroundPeak;
  private long warmBytesPeak, warmPixelsPeak, activeImagesPeak, activeBytesPeak, activePixelsPeak, unreclaimedPeak;
  private bool queued, stopped, prefetchIssued;

  internal PdfDemandDiagnostics Snapshot()
  {
    var (images, bytes, pixels) = ActiveUsage();
    return new(new(ordinaryIssued, ordinaryPeak), new(auto is null ? 0 : 1, backgroundPeak),
      new(pending, pendingPeak), new(warmBytes, warmBytesPeak), new(warmPixels, warmPixelsPeak),
      new(images, activeImagesPeak), new(bytes, activeBytesPeak), new(pixels, activePixelsPeak),
      new(unreclaimed.Count, unreclaimedPeak));
  }

  private (int Images, long Bytes, long Pixels) ActiveUsage() => (
    (current is null ? 0 : 1) + (previous is null ? 0 : 1),
    (current?.Bytes ?? 0) + (previous?.Bytes ?? 0),
    (current?.Pixels ?? 0) + (previous?.Pixels ?? 0));

  private static void EnterRead(Session session)
  {
    if (session.Reads++ == 0) session.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
  }

  private static void LeaveRead(Session session)
  {
    if (--session.Reads == 0) session.Drained?.TrySetResult();
  }

  private void EnterOrdinary(Session session)
  {
    EnterRead(session);
    ordinaryPeak = Math.Max(ordinaryPeak, ++ordinaryIssued);
  }

  private void LeaveOrdinary(Session session)
  {
    LeaveRead(session);
    ordinaryIssued--;
    ordinary.Release();
  }

  internal Task WaitForReadDrainAsync(string id) =>
    sessions.GetValueOrDefault(id)?.Drained?.Task ?? Task.CompletedTask;

  internal void Activate(PdfViewModel? model)
  {
    Session? next = model is { CanDemandPages: true, SessionId: { } id } ? Get(model, id) : null;
    if (ReferenceEquals(active, next)) return;
    if (next is not null) RecoverUnconfirmedSessions();
    if (active is { } old)
    {
      AdvanceViewport(old);
      if (auto == old && !old.Foreground) AdvanceBackground(old);
    }
    active = next;
    pending = 0;
    ChangePreview(null);
    if (next is not null)
    {
      next.AutoAttempted = false;
      next.Failed.Clear();
      Recover(next);
      AdoptCachedPreview(next);
    }
    Kick();
  }

  internal ViewportDemand? GetDemand(PdfViewModel model) => ModelSession(model)?.Demand;

  internal bool SetDemand(PdfViewModel model, int page, int[] visible, int? thumb = null, int? edge = null)
  {
    if (stopped || ModelSession(model) is not { } session || session != active) return false;
    RecoverUnconfirmedSessions();
    ViewportDemand? old = session.Demand;
    var next = new ViewportDemand(page, visible, thumb ?? old?.ThumbnailSize ?? DefaultThumbnailEdge,
      edge ?? old?.PreviewMaxEdge ?? DefaultPreviewEdge);
    if (old is not null && old.Page == next.Page && old.ThumbnailSize == next.ThumbnailSize &&
      old.PreviewMaxEdge == next.PreviewMaxEdge && old.VisiblePages.SequenceEqual(visible))
    {
      if (session.Blocked) Recover(session);
      return false;
    }
    if (old is not null && issued.Any(key => key.Session == session.Id &&
        (key.Kind == Kind.Thumbnail
          ? old.ThumbnailSize != next.ThumbnailSize || !visible.Any(index => Matches(session, index, key))
          : old.Page != page || key.Kind == Kind.Preview && old.PreviewMaxEdge != next.PreviewMaxEdge)))
      AdvanceViewport(session);
    session.Demand = next;
    pending = 0;
    session.Failed.Clear(); session.FulfilledThumbnails.Clear();
    foreach (var reference in unreclaimed.ToArray())
      if (resources.TryReleaseResource(reference)) unreclaimed.Remove(reference);
    Recover(session);
    AdoptCachedPreview(session);
    Kick();
    return true;
  }

  internal void ModelChanged(PdfViewModel model)
  {
    if (ModelSession(model) is not { } session) return;
    if (session == active) AdoptCachedPreview(session);
    Kick();
  }

  internal void RetryInspect(PdfViewModel model)
  {
    if (ModelSession(model) is not { } session || session != active) return;
    int page = model.SelectedPage;
    var (first, target) = PreviewJobs(session, page);
    foreach (Job? job in new[] { first, target, MakeJob(session, page, Kind.Inspect, 0) })
      if (job is not null) session.Failed.Remove(job.Key);
    RecoverUnconfirmedSessions();
    Recover(session);
    Kick();
  }

  internal Projection GetProjection(PdfViewModel model)
  {
    if (ModelSession(model) is not { } session || session != active)
      return new(null, null, "pdf.inspect.none");

    Job? inspectJob = MakeJob(session, model.SelectedPage, Kind.Inspect, 0);
    Entry? inspect = inspectJob is null ? null : Find(inspectJob.Key);
    bool inspectReady = model.CanInspectPage && inspect?.InspectIndex == model.SelectedPage;
    var (first, target) = PreviewJobs(session, model.SelectedPage);
    WorkbenchResourceReference? preview = current is not null && Matches(session, model.SelectedPage, current.Key)
      ? current.Resource : null;
    bool failed = target is not null && session.Failed.Contains(target.Key) ||
      preview is null && first is not null && session.Failed.Contains(first.Key) ||
      model.CanInspectPage && inspectJob is not null && session.Failed.Contains(inspectJob.Key);
    var status = failed ? "pdf.inspect.failed"
      : !model.CanInspectPage || inspectJob is null ? "pdf.inspect.none"
      : inspectReady && preview is not null ? "pdf.inspect.ready" : "pdf.inspect.loading";
    return new(preview, inspectReady ? inspect?.Resource : null, status);
  }

  private static (Job? First, Job? Target) PreviewJobs(Session session, int page)
  {
    if (session.Demand is not { } demand || demand.Page != page) return default;
    return (MakeJob(session, page, Kind.Preview, Math.Min(FirstPreviewEdge, demand.PreviewMaxEdge)),
      MakeJob(session, page, Kind.Preview, demand.PreviewMaxEdge));
  }

  internal WorkbenchResourceReference? Thumbnail(PdfViewModel model, int index)
  {
    Session? session = ModelSession(model);
    if (session != active || session?.Demand is not { } demand || !demand.VisiblePages.Contains(index)) return null;
    return MakeJob(session, index, Kind.Thumbnail, demand.ThumbnailSize) is { } job ? Find(job.Key)?.Resource : null;
  }

  internal void Invalidate(PdfViewModel model, bool structure = false)
  {
    if (ModelSession(model) is not { } session) return;
    // Only walk the bounded resource cache. The page index of a surviving page is
    // deliberately absent from the key so reorder retains its image and inspect data.
    HashSet<(long, long)> pages = model.Pages.Select(page => (page.PageKey, page.ContentGeneration)).ToHashSet();
    foreach (Entry entry in lru.ToArray())
      if (entry.Key.Session == session.Id && !pages.Contains((entry.Key.Page, entry.Key.Generation))) RemoveWarm(entry);
    if (current is { } image && image.Key.Session == session.Id && !pages.Contains((image.Key.Page, image.Key.Generation)))
      ChangePreview(null);
    if (structure)
    {
      if (session.Demand is { } demand)
        session.Demand = demand with { Page = model.SelectedPage, VisiblePages = [] };
      session.AutoAttempted = false;
    }
    session.Failed.Clear(); session.FulfilledThumbnails.Clear();
    Kick();
  }

  internal async ValueTask<IAsyncDisposable?> AcquireWriteAsync(
    PdfViewModel model, string id, CancellationToken ct)
  {
    Session session = Get(model, id);
    await writes.WaitAsync(ct);
    session.Writing = true;
    try
    {
      long cutoff = ++epoch;
      session.Viewport = session.Background = cutoff;
      session.AutoAttempted = false;
      await session.Control.WaitAsync(ct);
      try
      {
        await CancelAsync(session, Wire.PdfReadCancelScope.AllReads, cutoff);
        RecordAcknowledgement(session, Wire.PdfReadCancelScope.AllReads, cutoff);
        session.Blocked = false;
      }
      finally { session.Control.Release(); }
      ct.ThrowIfCancellationRequested();
      return new WriteLease(this, session);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      FinishWrite(session);
      throw;
    }
    catch (Exception)
    {
      session.Blocked = true;
      FinishWrite(session);
      return null;
    }
  }

  private sealed class WriteLease(PdfDemandCoordinator owner, Session session) : IAsyncDisposable
  {
    public ValueTask DisposeAsync()
    {
      owner.FinishWrite(session);
      return ValueTask.CompletedTask;
    }
  }

  private void FinishWrite(Session session)
  {
    session.Writing = false;
    writes.Release();
    Kick();
  }

  internal Task<bool> EnsurePresenceAsync(PdfViewModel model, string id, int[] pages, CancellationToken ct) =>
    RunBusinessReadAsync(model, id, ct, (readEpoch, currentRead) => model.ReadPresenceAsync(id, readEpoch, pages, currentRead));

  internal Task<byte[]> RenderBusinessPreviewAsync(PdfViewModel model, string id, int page, int dpi, CancellationToken ct) =>
    RunBusinessReadAsync(model, id, ct, (readEpoch, _) =>
      model.Inference.RenderPdfPreviewDemandAsync(id, new Wire.RenderPreviewRequest
      {
        Page = page, Dpi = dpi,
        Read = new() { Scope = Wire.PdfReadScope.Background, Epoch = readEpoch },
      }, CancellationToken.None));

  internal Task<(bool Issued, bool Present)> TryPrefetchPresenceAsync(
    PdfViewModel model, string id, int page, CancellationToken stop, CancellationToken cancelRun,
    Func<bool> canSpeculate, Func<bool> ownsTarget) =>
    TryPrefetchReadAsync(model, id, stop, cancelRun, canSpeculate, (readEpoch, current) =>
      model.ReadPresenceAsync(id, readEpoch, [page], () => current() && ownsTarget(), prefetch: true));

  internal Task<(bool Issued, byte[] Png)> TryPrefetchPreviewAsync(
    PdfViewModel model, string id, int page, int dpi, CancellationToken stop, CancellationToken cancelRun, Func<bool> canSpeculate) =>
    TryPrefetchReadAsync(model, id, stop, cancelRun, canSpeculate, (readEpoch, _) =>
      model.Inference.RenderPdfPreviewDemandAsync(id, new Wire.RenderPreviewRequest
      {
        Page = page, Dpi = dpi,
        Read = new() { Scope = Wire.PdfReadScope.Background, Epoch = readEpoch },
      }, CancellationToken.None));

  private async Task<(bool Issued, T Value)> TryPrefetchReadAsync<T>(
    PdfViewModel model, string id, CancellationToken stop, CancellationToken cancelRun,
    Func<bool> canSpeculate, Func<long, Func<bool>, Task<T>> call)
  {
    await Task.Yield();
    Session? session = ModelSession(model);
    if (stop.IsCancellationRequested || !canSpeculate() || stopped || prefetchIssued || foregroundWaiters != 0 ||
        !model.CanIssueDemandReads || unreclaimed.Count != 0 ||
        session is null || session != active || session.Id != id || session.Writing || session.Blocked ||
        session.Foreground || auto == session || PendingAck(session, false) || PendingAck(session, true) ||
        NextJobs(session).Any() || !ordinary.Wait(0))
      return (false, default!);

    prefetchIssued = true;
    EnterOrdinary(session);
    long readEpoch = session.Background;
    bool complete = false, revokeAttempted = false;
    // The end of a batch only stops admission. Run cancellation also revokes queued native reads.
    bool Current() => !stopped && !session.Writing && model.CanIssueDemandReads &&
      model.SessionId == id && session.Background == readEpoch && !cancelRun.IsCancellationRequested;
    void Release() { prefetchIssued = false; LeaveOrdinary(session); }
    try
    {
      using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancelRun);
      Task<T> pending = call(readEpoch, Current);
      if (await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, wake.Token)) != pending)
      {
        if (session.Background == readEpoch) AdvanceBackground(session);
        revokeAttempted = true;
        await EnsureAckAsync(session, viewport: false);
      }
      else wake.Cancel();
      // HTTP remains open until queued revocation or already-admitted native work actually settles.
      T result = await pending;
      complete = true;
      return Current() ? (true, result) : (false, default!);
    }
    finally
    {
      if (complete || !KeepUntilAcknowledged(session, Wire.PdfReadScope.Background, readEpoch, Release))
        Release();
      else if (!revokeAttempted)
        await EnsureAckAsync(session, viewport: false);
      Kick();
    }
  }

  private async Task<T> RunBusinessReadAsync<T>(PdfViewModel model, string id, CancellationToken ct,
    Func<long, Func<bool>, Task<T>> call)
  {
    Session session = Get(model, id);
    session.Foreground = true;
    foregroundWaiters++;
    bool slot = false, waiting = true, sent = false, complete = false;
    long readEpoch = 0;
    try
    {
      if (auto == session && !PendingAck(session, false)) AdvanceBackground(session);
      RecoverUnconfirmedSessions();
      Recover(session);
      if (!await EnsureAckAsync(session, viewport: false))
        throw new IOException("PDF background read cancellation was not acknowledged.");
      await ordinary.WaitAsync(ct);
      slot = true;
      EnterOrdinary(session);
      foregroundWaiters--; waiting = false;
      Kick();
      ct.ThrowIfCancellationRequested();
      readEpoch = session.Background;
      if (session.Writing || session.Blocked || model.SessionId != id)
        throw new InvalidOperationException("PDF business read target changed before submission.");
      bool Current() => !session.Writing && model.SessionId == id &&
        session.Background == readEpoch && !ct.IsCancellationRequested;
      using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
      sent = true;
      Task<T> pending = call(readEpoch, Current);
      if (await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, wake.Token)) != pending)
      {
        AdvanceBackground(session);
        await EnsureAckAsync(session, viewport: false);
      }
      else wake.Cancel();
      T result = await pending;
      complete = true;
      ct.ThrowIfCancellationRequested();
      return result;
    }
    finally
    {
      if (slot && (!sent || complete ||
          !KeepUntilAcknowledged(session, Wire.PdfReadScope.Background, readEpoch, () => LeaveOrdinary(session))))
        LeaveOrdinary(session);
      if (waiting) foregroundWaiters--;
      session.Foreground = false;
      Kick();
    }
  }

  internal void Forget(string id)
  {
    if (!sessions.Remove(id, out Session? session)) return;
    if (active == session) { active = null; ChangePreview(null); }
    foreach (Entry entry in lru.Where(entry => entry.Key.Session == id).ToArray()) RemoveWarm(entry);
    if (previous?.Key.Session == id) { Entry retired = previous; previous = null; ReleaseIfUnowned(retired); }
  }

  internal void Stop()
  {
    stopped = true;
    pending = 0;
    foreach (Session session in sessions.Values.ToArray())
    {
      if (session.Writing) continue;
      AdvanceViewport(session);
      if (!session.Foreground) AdvanceBackground(session);
      operations.Track(AcknowledgeAsync(session));
    }
  }

  internal void Release()
  {
    Entry? a = current, b = previous;
    current = previous = null;
    foreach (Entry entry in lru.ToArray()) RemoveWarm(entry);
    ReleaseIfUnowned(a); ReleaseIfUnowned(b);
    sessions.Clear();
    foreach (var reference in unreclaimed.ToArray()) resources.TryReleaseResource(reference);
    unreclaimed.Clear();
  }

  private Session Get(PdfViewModel model, string id)
  {
    if (!sessions.TryGetValue(id, out Session? session)) sessions.Add(id, session = new(id, model, ++epoch));
    return session;
  }

  private Session? ModelSession(PdfViewModel model) =>
    model.SessionId is { } id ? sessions.GetValueOrDefault(id) : null;

  private void AdvanceViewport(Session session) => session.Viewport = ++epoch;
  private void AdvanceBackground(Session session) { session.Background = ++epoch; session.AutoAttempted = false; }
  private static bool PendingAck(Session session, bool viewport) =>
    viewport ? session.ViewportAck < session.Viewport : session.BackgroundAck < session.Background;

  private static Task CancelAsync(Session session, Wire.PdfReadCancelScope scope, long cutoff) =>
    session.Model.Inference.CancelPdfReadsAsync(session.Id,
      new Wire.PdfReadCancelRequest { Scope = scope, BeforeEpoch = cutoff }, CancellationToken.None);

  private async Task<bool> EnsureAckAsync(Session session, bool viewport)
  {
    if (!PendingAck(session, viewport)) return !session.Blocked;
    await session.Control.WaitAsync();
    try
    {
      while (PendingAck(session, viewport))
      {
        if (session.Writing || session.Model.SessionId != session.Id) return false;
        long cutoff = viewport ? session.Viewport : session.Background;
        var scope = viewport ? Wire.PdfReadCancelScope.Viewport : Wire.PdfReadCancelScope.Background;
        await CancelAsync(session, scope, cutoff);
        RecordAcknowledgement(session, scope, cutoff);
      }
      return !session.Blocked;
    }
    catch (Exception)
    {
      session.Blocked = true;
      return false;
    }
    finally { session.Control.Release(); }
  }

  // A failed transport can leave work admitted at the worker. Keep its original
  // slot and session drain ownership until the worker has fenced that epoch.
  // This list is bounded by the same two ordinary slots and one automatic slot.
  private bool KeepUntilAcknowledged(Session session, Wire.PdfReadScope scope, long readEpoch, Action release)
  {
    if ((scope == Wire.PdfReadScope.Viewport ? session.ViewportAck : session.BackgroundAck) > readEpoch)
      return false;
    unconfirmedReads.Add(new(session, scope, readEpoch, release));
    if (scope == Wire.PdfReadScope.Viewport)
    {
      if (session.Viewport <= readEpoch) AdvanceViewport(session);
    }
    else if (session.Background <= readEpoch) AdvanceBackground(session);
    return true;
  }

  private void RecordAcknowledgement(Session session, Wire.PdfReadCancelScope scope, long cutoff)
  {
    if (scope != Wire.PdfReadCancelScope.Background) session.ViewportAck = Math.Max(session.ViewportAck, cutoff);
    if (scope != Wire.PdfReadCancelScope.Viewport) session.BackgroundAck = Math.Max(session.BackgroundAck, cutoff);
    for (int i = unconfirmedReads.Count - 1; i >= 0; i--)
    {
      UnconfirmedRead read = unconfirmedReads[i];
      if (read.Session != session) continue;
      long acknowledged = read.Scope == Wire.PdfReadScope.Viewport ? session.ViewportAck : session.BackgroundAck;
      if (acknowledged <= read.Epoch) continue;
      unconfirmedReads.RemoveAt(i);
      read.Release();
    }
  }

  private void RecoverUnconfirmedSessions()
  {
    // A new user demand can retry confirmation even for a now-inactive document.
    // A failed acknowledgement itself never schedules another attempt.
    foreach (Session session in unconfirmedReads.Select(read => read.Session).Distinct().ToArray())
      Recover(session);
  }

  private void Recover(Session session)
  {
    if (session.Writing || !session.Blocked) return;
    session.Blocked = false;
    Kick();
  }

  private void Kick()
  {
    if (queued || stopped) return;
    queued = true;
    operations.Track(PumpAsync());
  }

  private async Task PumpAsync()
  {
    await Task.Yield();
    queued = false;
    if (stopped) return;
    // Obsolete requests can belong to the document we just left.
    foreach (Session session in sessions.Values.ToArray())
      if (!session.Writing && !session.Blocked && (PendingAck(session, true) || !session.Foreground && PendingAck(session, false)) &&
          handshakes.Add(session))
        operations.Track(AcknowledgeAsync(session));
    if (active is not { } state || !state.Model.CanIssueDemandReads || state.Model.SessionId != state.Id)
    {
      pending = 0;
      return;
    }

    Job[] work = NextJobs(state).Take(PendingLimit).ToArray();
    pending = work.Length;
    pendingPeak = Math.Max(pendingPeak, pending);
    if (state.Writing || state.Blocked) return;
    if (foregroundWaiters == 0 && unreclaimed.Count == 0 && !PendingAck(state, true))
      foreach (Job job in work)
      {
        if (!ordinary.Wait(0)) break;
        EnterOrdinary(state);
        issued.Add(job.Key);
        pending--;
        operations.Track(ReadAsync(state, job));
      }

    if (auto is null && !state.Foreground && !state.AutoAttempted && !state.Model.HasBusinessRun &&
        !PendingAck(state, false) && state.Model.HasUnknownPresence)
    {
      auto = state;
      EnterRead(state);
      backgroundPeak = 1;
      state.AutoAttempted = true;
      operations.Track(DetectAsync(state));
    }
  }

  private async Task AcknowledgeAsync(Session session)
  {
    try
    {
      if (await EnsureAckAsync(session, viewport: true) && !session.Foreground)
        await EnsureAckAsync(session, viewport: false);
    }
    finally { handshakes.Remove(session); Kick(); }
  }

  private async Task DetectAsync(Session session)
  {
    long readEpoch = session.Background;
    bool complete = false;
    void Release() { auto = null; LeaveRead(session); }
    try
    {
      await session.Model.ReadPresenceAsync(session.Id, readEpoch, null,
        () => !stopped && active == session && !session.Writing && session.Background == readEpoch);
      complete = true;
    }
    catch (Exception) { }
    finally
    {
      bool failedCurrentAttempt = !complete && session.Background == readEpoch;
      if (complete || !KeepUntilAcknowledged(session, Wire.PdfReadScope.Background, readEpoch, Release)) Release();
      if (failedCurrentAttempt) session.AutoAttempted = true;
      Kick();
    }
  }

  private IEnumerable<Job> NextJobs(Session session)
  {
    if (session.Demand is not { } demand || demand.Page != session.Model.SelectedPage) yield break;
    var (first, final) = PreviewJobs(session, demand.Page);
    bool ready = first is not null && (Find(first.Key) is not null || session.Failed.Contains(first.Key));
    if (final is not null && Find(final.Key) is not null) ready = true;
    if (!ready && Needed(session, first)) yield return first!;
    if (ready)
    {
      Job? inspect = session.Model.CanInspectPage ? MakeJob(session, demand.Page, Kind.Inspect, 0) : null;
      if (Needed(session, inspect)) yield return inspect!;
      if (Needed(session, final)) yield return final!;
    }
    foreach (int index in demand.VisiblePages)
    {
      Job? thumb = MakeJob(session, index, Kind.Thumbnail, demand.ThumbnailSize);
      if (Needed(session, thumb)) yield return thumb!;
    }
  }

  private bool Needed(Session session, Job? job)
  {
    if (job is null || issued.Contains(job.Key) || session.Failed.Contains(job.Key) ||
        session.FulfilledThumbnails.Contains(job.Key)) return false;
    Entry? entry = Find(job.Key);
    Touch(entry);
    return entry is null || (job.Key.Kind == Kind.Inspect && entry.InspectIndex != job.Index);
  }

  private static Job? MakeJob(Session session, int index, Kind kind, int tier) =>
    index >= 0 && index < session.Model.Pages.Count && session.Model.Pages[index] is { } page
      ? new(new Key(session.Id, page.PageKey, page.ContentGeneration, kind, tier, kind == Kind.Preview ? Math.Min(MaxPreviewPixels, session.Model.ProcessingSettings.MaxPixels) : 0), index) : null;

  private static bool Matches(Session session, int index, Key key) =>
    MakeJob(session, index, key.Kind, key.Tier)?.Key == key;

  private async Task ReadAsync(Session session, Job job)
  {
    long readEpoch = session.Viewport;
    var read = new Wire.PdfReadContext { Scope = Wire.PdfReadScope.Viewport, Epoch = readEpoch };
    bool Current() => !stopped && session == active && !session.Writing && readEpoch == session.Viewport &&
      session.Model.SessionId == session.Id && Matches(session, job.Index, job.Key);
    var entry = new Entry(job.Key);
    bool sent = false, httpComplete = false;
    void Release() { issued.Remove(job.Key); LeaveOrdinary(session); }
    try
    {
      if (job.Key.Kind == Kind.Inspect)
      {
        entry.Inspect = Find(job.Key)?.Inspect;
        if (entry.Inspect is null)
        {
          sent = true;
          entry.Inspect = await session.Model.Inference.InspectPdfPageDemandAsync(
            session.Id, new Wire.PageInspectRequest { Page = job.Index, Read = read }, CancellationToken.None);
          httpComplete = true;
        }
        if (!Current()) return;
        // 检查携带真实显示空间几何：presence 未抵达也先回填，否则后部未知页没有
        // logicalSize，高清需求无从发起；仅几何，不把文字层 unknown 冒充 known。
        session.Model.ApplyInspectGeometry(job.Index, job.Key.Page, job.Key.Generation, entry.Inspect);
        entry.InspectIndex = job.Index;
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry.Inspect with { Page = job.Index });
        entry.Bytes = payload.LongLength;
        if (entry.Bytes > WarmBytes) throw new InvalidDataException("PDF inspect exceeds cache budget.");
        entry.Resource = await resources.PublishBytesAsync(payload, "application/json", ".json", CancellationToken.None);
      }
      else
      {
        sent = true;
        byte[] bytes = job.Key.Kind == Kind.Thumbnail
          ? await session.Model.Inference.RenderPdfThumbnailDemandAsync(session.Id,
            new Wire.RenderThumbnailRequest { Page = job.Index, Size = job.Key.Tier, Read = read }, CancellationToken.None)
          : await session.Model.Inference.RenderPdfPreviewDemandAsync(session.Id,
            new Wire.RenderPreviewRequest { Page = job.Index, MaxEdge = job.Key.Tier, MaxPixels = job.Key.PixelLimit, Read = read }, CancellationToken.None);
        httpComplete = true;
        if (!Current()) return;
        entry.Pixels = PngPixels(bytes);
        entry.Bytes = bytes.LongLength;
        if (entry.Pixels <= 0 || job.Key.Kind == Kind.Preview && entry.Pixels > job.Key.PixelLimit ||
            job.Key.Kind == Kind.Thumbnail && (entry.Pixels > MaxPreviewPixels || entry.Bytes > WarmBytes))
          throw new InvalidDataException("PDF demand PNG dimensions or size exceed the render budget.");
        entry.Resource = await resources.PublishBytesAsync(bytes, "image/png", ".png", CancellationToken.None);
      }
      if (!Current()) return;
      if (job.Key.Kind == Kind.Preview) ChangePreview(entry);
      Store(entry);
      if (job.Key.Kind == Kind.Thumbnail) session.FulfilledThumbnails.Add(job.Key);
      if (entry.Warm is null && job.Key.Kind != Kind.Preview) session.Failed.Add(job.Key);
      publish(session.Model);
    }
    catch (Exception)
    {
      if (Current())
      {
        session.Failed.Add(job.Key);
        publish(session.Model);
      }
    }
    finally
    {
      ReleaseIfUnowned(entry);
      if (!sent || httpComplete ||
          !KeepUntilAcknowledged(session, Wire.PdfReadScope.Viewport, readEpoch, Release)) Release();
      Kick();
    }
  }

  private Entry? Find(Key key) => current?.Key == key ? current : previous?.Key == key ? previous : warm.GetValueOrDefault(key);

  private void AdoptCachedPreview(Session session)
  {
    if (session.Demand is not { } demand || demand.Page != session.Model.SelectedPage)
    {
      ChangePreview(null);
      return;
    }
    Entry? Lookup(int tier) => MakeJob(session, demand.Page, Kind.Preview, tier) is { } job ? Find(job.Key) : null;
    Entry? cached = Lookup(demand.PreviewMaxEdge) ?? Lookup(Math.Min(demand.PreviewMaxEdge, FirstPreviewEdge));
    Touch(cached);
    ChangePreview(cached);
  }

  private void Touch(Entry? entry)
  {
    if (entry?.Warm is { } node) { lru.Remove(node); lru.AddFirst(node); }
  }

  private void ChangePreview(Entry? next)
  {
    if (current == next) return;
    Entry? expired = previous;
    if (current is not null) previous = current;
    current = next;
    if (previous == current) previous = null;
    var (images, bytes, pixels) = ActiveUsage();
    activeImagesPeak = Math.Max(activeImagesPeak, images);
    activeBytesPeak = Math.Max(activeBytesPeak, bytes);
    activePixelsPeak = Math.Max(activePixelsPeak, pixels);
    ReleaseIfUnowned(expired);
  }

  private void Store(Entry entry)
  {
    if (entry.Bytes > WarmBytes || entry.Pixels > WarmPixels) return;
    if (warm.TryGetValue(entry.Key, out Entry? old)) RemoveWarm(old);
    while ((warmBytes + entry.Bytes > WarmBytes || warmPixels + entry.Pixels > WarmPixels) && lru.Last is { } node)
      RemoveWarm(node.Value);
    warm.Add(entry.Key, entry);
    entry.Warm = lru.AddFirst(entry);
    warmBytes += entry.Bytes;
    warmPixels += entry.Pixels;
    warmBytesPeak = Math.Max(warmBytesPeak, warmBytes);
    warmPixelsPeak = Math.Max(warmPixelsPeak, warmPixels);
  }

  private void RemoveWarm(Entry entry)
  {
    if (entry.Warm is not { } node) return;
    lru.Remove(node);
    warm.Remove(entry.Key);
    entry.Warm = null;
    warmBytes -= entry.Bytes;
    warmPixels -= entry.Pixels;
    ReleaseIfUnowned(entry);
  }

  private void ReleaseIfUnowned(Entry? entry)
  {
    if (entry is not { Warm: null, Resource: { } reference } || entry == current || entry == previous) return;
    entry.Resource = null;
    if (!resources.TryReleaseResource(reference))
    {
      unreclaimed.Add(reference);
      unreclaimedPeak = Math.Max(unreclaimedPeak, unreclaimed.Count);
    }
  }

  private static long PngPixels(byte[] bytes)
  {
    if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return 0;
    int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
    int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
    return width > 0 && height > 0 ? (long)width * height : 0;
  }
}
