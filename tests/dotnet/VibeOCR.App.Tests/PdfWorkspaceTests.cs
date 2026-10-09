using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PdfWorkspaceTests
{
  [Fact]
  public async Task SameNamedDocumentsRetainSelectionAndLateSaveAsOwnsOriginalEntry()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "a", "same.pdf")), CancellationToken.None);
    PdfDocumentEntry first = Assert.Single(handler.PdfDocuments);
    await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SelectPdfPagesCommand([1])), CancellationToken.None);
    fixture.Client.PendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WorkbenchCommandOutcome> save = handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SavePdfAsCommand()), CancellationToken.None).AsTask();
    await fixture.Client.SaveEntered.Task;
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "b", "same.pdf")), CancellationToken.None);
    PdfDocumentEntry second = handler.PdfDocuments.Last();
    Assert.NotEqual(first.Id, second.Id); Assert.NotEqual(first.Model.SessionId, second.Model.SessionId);
    fixture.Client.PendingSave.SetResult(fixture.SaveTarget);
    await save;
    Assert.Equal(fixture.SaveTarget, first.Model.FilePath);
    Assert.EndsWith(Path.Combine("b", "same.pdf"), second.Model.FilePath!);
    Assert.True(second.Model.IsModified);
    var selected = await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new ActivatePdfDocumentCommand()), CancellationToken.None);
    PdfWorkbenchState state = Assert.IsType<PdfWorkbenchState>(Assert.Single(selected.States));
    Assert.Equal(first.Id, state.DocumentId); Assert.Equal([1], state.SelectedPages!);
    await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SavePdfCommand()), CancellationToken.None);
    Assert.Equal(fixture.SaveTarget, fixture.Client.Saves.Last().Path);
  }

  [Fact]
  public async Task WindowAndSwitchRevokePublishedResourcesAndDuplicateTargetActivates()
  {
    using var fixture = new Fixture(70);
    await using var handler = fixture.Handler();
    string path = Path.Combine(fixture.Root, "a.pdf");
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None);
    PdfDocumentEntry first = Assert.Single(handler.PdfDocuments);
    var initial = await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SetPdfWindowCommand(0)), CancellationToken.None);
    string url = Assert.IsType<PdfWorkbenchState>(Assert.Single(initial.States)).Pages![0].Thumbnail!.Url;
    Assert.Equal(64, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
    await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SetPdfWindowCommand(64)), CancellationToken.None);
    Assert.Equal(6, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
    Assert.Throws<WorkbenchResourceAccessException>(() => fixture.Broker.OpenAsync(new Uri(url), TestContext.Current.CancellationToken).GetAwaiter().GetResult());
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "b.pdf")), CancellationToken.None);
    Assert.Equal(64, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None);
    Assert.Equal(2, handler.PdfDocuments.Count);
    Assert.Equal(6, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
  }

  [Fact]
  public async Task CopyBatchRecordsPartialFailureRetryAndCancellationWithoutClearingDirty()
  {
    var client = new Client(); var workspace = new PdfWorkspace();
    for (int i = 0; i < 3; i++)
    { var model = Model(client); await model.OpenPathAsync(Path.Combine(Path.GetTempPath(), $"batch-{i}.pdf"), CancellationToken.None); workspace.Add(model); }
    string directory = Path.Combine(Path.GetTempPath(), $"t4-export-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
    client.FailCopy = workspace.Documents[1].Model.SessionId;
    await workspace.ExportAsync(directory, false, false, () => { }, CancellationToken.None);
    Assert.Equal(["saved", "failed", "saved"], workspace.ExportItems.Select(item => item.Status));
    Assert.All(workspace.Documents, entry => Assert.True(entry.Model.IsModified));
    client.FailCopy = null;
    await workspace.ExportAsync(directory, false, true, () => { }, CancellationToken.None);
    Assert.All(workspace.ExportItems, item => Assert.Equal("saved", item.Status));
    client.PendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
    client.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task exporting = workspace.ExportAsync(directory, false, false, () => { }, CancellationToken.None);
    await client.SaveEntered.Task;
    workspace.CancelExport(); client.PendingSave.SetResult(client.Saves.Last().Path);
    await exporting;
    Assert.Equal(["saved", "cancelled", "not_started"], workspace.ExportItems.Select(item => item.Status));
    Assert.All(workspace.Documents, entry => Assert.True(entry.Model.IsModified));
  }

  [Fact]
  public async Task ExitCancelKeepsSessionsAndCloseFailureCanRetry()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "a.pdf")), CancellationToken.None);
    PdfDocumentEntry entry = Assert.Single(handler.PdfDocuments);
    fixture.Decision = PdfCloseDecision.Cancel;
    Assert.False(await handler.RequestCloseAllPdfAsync()); Assert.Empty(fixture.Client.Closed); Assert.Single(handler.PdfDocuments);
    fixture.Decision = PdfCloseDecision.Discard; fixture.Client.FailClose = true;
    Assert.False(await handler.RequestCloseAllPdfAsync()); Assert.NotNull(entry.CloseError); Assert.Single(handler.PdfDocuments);
    fixture.Client.FailClose = false;
    Assert.True(await handler.RequestCloseAllPdfAsync()); Assert.Empty(handler.PdfDocuments);
  }

  [Fact]
  public async Task SettlementWaitsForNonCancelableSaveAndLimitDoesNotRemoveDocuments()
  {
    var client = new Client { PendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    PdfViewModel model = Model(client); await model.OpenPathAsync("source.pdf", CancellationToken.None);
    Task<PdfSaveResult> save = model.SaveAsync("target.pdf", CancellationToken.None);
    await client.SaveEntered.Task; Task settled = model.CancelAndSettleAsync();
    Assert.False(settled.IsCompleted);
    client.PendingSave.SetResult(Path.GetFullPath("target.pdf"));
    Assert.True((await save).Saved); await settled; Assert.False(model.IsModified);
    var workspace = new PdfWorkspace();
    for (int i = 0; i < PdfWorkspace.MaxDocuments; i++) workspace.Add(Model(client));
    Assert.Throws<InvalidOperationException>(() => workspace.Add(Model(client)));
    Assert.Equal(16, workspace.Documents.Count);
  }

  [Fact]
  public async Task LateRotationDoesNotRevokeAnotherDocumentsWindowAndExitCancelsBeforeSettlement()
  {
    using var fixture = new Fixture(70);
    await using var handler = fixture.Handler();
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "first.pdf")), CancellationToken.None);
    PdfDocumentEntry first = Assert.Single(handler.PdfDocuments);
    fixture.Client.PendingRotate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WorkbenchCommandOutcome> rotating = handler.ExecuteAsync(new PdfBoundCommand(first.Id, new RotatePdfCommand(90)), CancellationToken.None).AsTask();
    await fixture.Client.RotateEntered.Task;
    var opened = await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "second.pdf")), CancellationToken.None);
    PdfWorkbenchState secondState = Assert.IsType<PdfWorkbenchState>(Assert.Single(opened.States));
    string url = secondState.Pages![0].Thumbnail!.Url;
    fixture.Client.PendingRotate.SetResult(new PdfMutateResult(70));
    PdfWorkbenchState late = Assert.IsType<PdfWorkbenchState>(Assert.Single((await rotating).States));
    Assert.Equal(secondState.DocumentId, late.DocumentId);
    Assert.Equal(url, late.Pages![0].Thumbnail!.Url);
    Assert.Equal(64, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
    await using (var resource = await fixture.Broker.OpenAsync(new Uri(url), TestContext.Current.CancellationToken)) Assert.True(resource.ContentLength > 0);
    fixture.Client.PendingRotate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Client.RotateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WorkbenchCommandOutcome> settlingWrite = handler.ExecuteAsync(new PdfBoundCommand(secondState.DocumentId!, new RotatePdfCommand(90)), CancellationToken.None).AsTask();
    await fixture.Client.RotateEntered.Task;
    fixture.Decision = PdfCloseDecision.Cancel;
    Task<bool> exiting = handler.RequestCloseAllPdfAsync();
    Assert.Contains(handler.PdfDocuments.Last().Model.SessionId!, fixture.Client.Cancelled);
    Assert.False(exiting.IsCompleted);
    fixture.Client.PendingRotate.SetResult(new PdfMutateResult(70));
    await settlingWrite;
    Assert.False(await exiting);
    Assert.Equal(2, handler.PdfDocuments.Count);
  }

  [Fact]
  public async Task FailedOpensDoNotConsumeSlotsAndEmptyWorkspaceSettingsAreBound()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    fixture.Client.FailOpen = true;
    PdfWorkbenchState empty = Assert.IsType<PdfWorkbenchState>(Assert.Single((await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "bad.pdf")), CancellationToken.None)).States));
    Assert.Empty(handler.PdfDocuments); Assert.NotNull(empty.DocumentId);
    var parameters = new PdfProcessingSettings(RenderDpi: 144);
    await handler.ExecuteAsync(new PdfBoundCommand(empty.DocumentId!, new SetPdfProcessingSettingsCommand(parameters), 0), CancellationToken.None);
    Assert.Equal(144, Assert.Single(handler.PdfDocuments).Model.ProcessingSettings.RenderDpi);
    fixture.Client.FailOpen = false;
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "good.pdf")), CancellationToken.None);
    PdfDocumentEntry retained = Assert.Single(handler.PdfDocuments);
    fixture.Client.FailOpen = true;
    for (int attempt = 0; attempt < PdfWorkspace.MaxDocuments + 2; attempt++)
    {
      await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "bad.pdf")), CancellationToken.None);
      await handler.ExecuteAsync(new PdfBoundCommand(retained.Id, new ActivatePdfDocumentCommand()), CancellationToken.None);
      Assert.Same(retained, Assert.Single(handler.PdfDocuments));
      Assert.True(retained.Model.HasSession);
    }
    Assert.NotEqual(empty.DocumentId, retained.Id);
  }

  [Fact]
  public async Task CancelledLateOpenWithFailedRemoteCloseRemainsVisibleAndRetryable()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    fixture.Client.PendingOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string path = Path.Combine(fixture.Root, "late.pdf");
    Task<WorkbenchCommandOutcome> opening = handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None).AsTask();
    await fixture.Client.OpenEntered.Task;
    PdfDocumentEntry entry = Assert.Single(handler.PdfDocuments);
    fixture.Client.FailClose = true;
    await handler.ExecuteAsync(new PdfBoundCommand(entry.Id, new CancelPdfCommand()), CancellationToken.None);
    fixture.Client.PendingOpen.SetResult(fixture.Client.OpenResult(path));
    PdfWorkbenchState failed = Assert.IsType<PdfWorkbenchState>(Assert.Single((await opening).States));
    Assert.False(entry.Model.HasSession); Assert.True(entry.Model.HasRemoteSession);
    Assert.Equal(entry.Id, Assert.Single(failed.Documents!).DocumentId);
    Assert.True(Assert.Single(failed.Documents!).CloseFailed);
    fixture.Client.FailClose = false;
    await handler.ExecuteAsync(new PdfBoundCommand(entry.Id, new ClosePdfCommand()), CancellationToken.None);
    Assert.Empty(handler.PdfDocuments); Assert.Single(fixture.Client.Closed);
  }

  [Fact]
  public async Task LegacyRuntimeRejectsSaveAsAndCopyBeforeSendingRequests()
  {
    var client = new Client(); var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("source.pdf", CancellationToken.None);
    Assert.Equal(PdfSaveDisposition.Rejected, (await model.SaveAsAsync("target.pdf", CancellationToken.None)).Disposition);
    Assert.Equal(PdfSaveDisposition.Rejected, (await model.ExportCopyAsync("copy.pdf", model.Revision, model.ProcessingSettings, CancellationToken.None)).Disposition);
    Assert.Empty(client.Saves); Assert.True(model.IsModified); Assert.Equal("source.pdf", model.FilePath);
  }

  private static PdfViewModel Model(Client client)
  { var model = new PdfViewModel(client, new Source()); model.SetInspectionCapabilities(["pdf.copy-export.v1"]); return model; }
  private sealed class Source : IPdfFileSource
  { public Task<string?> PickFileAsync(CancellationToken ct) => Task.FromResult<string?>(null); }
  private sealed class Client(int pageCount = 2) : InferenceClientStub
  {
    public List<(string Session, string Path)> Saves { get; } = [];
    public List<string> Closed { get; } = [];
    public List<string> Cancelled { get; } = [];
    public TaskCompletionSource<PdfMutateResult>? PendingRotate { get; set; }
    public TaskCompletionSource RotateEntered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override Task<PdfMutateResult> RotatePdfPagesAsync(string sessionId, int[] pages, int angle, CancellationToken ct)
    { RotateEntered.TrySetResult(); return PendingRotate?.Task ?? Task.FromResult(new PdfMutateResult(pageCount)); }
    public override Task CancelPdfAsync(string sessionId, CancellationToken ct) { Cancelled.Add(sessionId); return Task.CompletedTask; }
    public TaskCompletionSource<string>? PendingSave { get; set; }
    public TaskCompletionSource SaveEntered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? FailCopy { get; set; }
    public bool FailClose { get; set; }
    public bool FailOpen { get; set; }
    public TaskCompletionSource<PdfSessionOpenResult>? PendingOpen { get; set; }
    public TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PdfSessionOpenResult OpenResult(string path) => new(Guid.NewGuid().ToString("N"), pageCount, path,
      new Wire.PdfDocumentMirror { IsModified = true, Pages = Enumerable.Range(0, pageCount).Select(index => new Wire.PdfPageInfoMirror { PageIndex = index, Rect = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(612), JsonSerializer.SerializeToElement(792)] }).ToArray() });
    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct)
    {
      OpenEntered.TrySetResult();
      if (FailOpen && Path.GetFileName(path) == "bad.pdf") throw new InferenceClientException(HttpV2ErrorCode.ValidationError, "synthetic bad PDF", false);
      return PendingOpen?.Task ?? Task.FromResult(OpenResult(path));
    }
    public override Task<Wire.PdfDocumentMirror> GetPdfModelAsync(string sessionId, CancellationToken ct) => Task.FromResult(new Wire.PdfDocumentMirror { IsModified = true, Pages = Enumerable.Range(0, pageCount).Select(index => new Wire.PdfPageInfoMirror { PageIndex = index, Rect = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(612), JsonSerializer.SerializeToElement(792)] }).ToArray() });
    public override Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct) => Task.FromResult(new byte[] { 1, 2, 3 });
    public override Task<string> SavePdfOperationAsync(string sessionId, string path, IReadOnlyDictionary<string, JsonElement> settings, bool copyExport, bool rebindTarget, bool overwrite, CancellationToken ct)
    {
      Saves.Add((sessionId, path)); SaveEntered.TrySetResult();
      if (copyExport && FailCopy == sessionId) throw new InferenceClientException(HttpV2ErrorCode.ValidationError, "synthetic target collision", false);
      return PendingSave?.Task ?? Task.FromResult(path);
    }
    public override Task ClosePdfSessionAsync(string sessionId, CancellationToken ct)
    { if (FailClose) throw new IOException("synthetic close failure"); Closed.Add(sessionId); return Task.CompletedTask; }
  }
  private sealed class Fixture : IDisposable
  {
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"vibeocr-t4-{Guid.NewGuid():N}");
    public string SaveTarget => Path.Combine(Root, "saved.pdf");
    public Client Client { get; }
    public WorkbenchResourceBroker Broker { get; }
    private WorkbenchAnnotationStore Annotations { get; }
    public PdfCloseDecision Decision { get; set; } = PdfCloseDecision.Cancel;
    public Fixture(int pages = 2)
    { Directory.CreateDirectory(Root); Client = new(pages); Broker = new(Root); Annotations = new(Root); }
    public DesktopWorkbenchCommandHandler Handler() => new(
      () => throw new NotSupportedException(), () => throw new NotSupportedException(), () => throw new NotSupportedException(),
      () => Model(Client), () => throw new NotSupportedException(), () => throw new NotSupportedException(), () => throw new NotSupportedException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])), Broker, Root, static () => 0, Annotations,
      confirmPdfClose: _ => Task.FromResult(Decision), pickPdfSavePath: () => Task.FromResult<string?>(SaveTarget));
    public void Dispose() { Annotations.Dispose(); Broker.Dispose(); }
  }
}
