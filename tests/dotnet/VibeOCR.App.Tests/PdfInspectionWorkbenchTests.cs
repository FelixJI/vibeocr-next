using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PdfInspectionWorkbenchTests
{
  [Fact]
  public async Task LegacyPdfCapabilityDoesNotRequestInspectionOrAtomicEditing()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-legacy-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client();
      var model = new PdfViewModel(client, new Source());
      model.SetInspectionCapabilities(["pdf.edit.v2"]);
      await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = Handler(model, root, broker, annotations);
      PdfWorkbenchState state = await SelectAsync(handler, 0);
      Assert.False(state.CanInspectPage); Assert.False(state.CanCorrectText);
      Assert.Null(state.PageInspect); Assert.Null(state.PagePreview);
      Assert.False(client.InspectEntered.Task.IsCompleted); Assert.Equal(0, client.PreviewCalls);
      Assert.NotNull(state.Pages![0].Thumbnail);
      PdfBlockEditResult edit = await model.UpdateBlockTextAsync(model.SessionId!, model.Revision, 0, 0, "new", "old", CancellationToken.None);
      Assert.False(edit.Applied); Assert.Equal(0, client.EditCalls);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task CurrentPageResourcesAreBoundedRevokedAndRetryable()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-inspect-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client();
      var model = new PdfViewModel(client, new Source());
      model.SetInspectionCapabilities(["pdf.page-inspect.v1", "pdf.block-edit.v1"]);
      await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = Handler(model, root, broker, annotations);
      PdfWorkbenchState first = await SelectAsync(handler, 0);
      Assert.Equal("pdf.inspect.ready", first.PageInspectStatusCode);
      Assert.Equal("doc-1", first.SessionId);
      Assert.Equal(300, client.LastDpi);
      Assert.NotNull(first.PagePreview); Assert.NotNull(first.PageInspect);
      int files = Directory.GetFiles(root).Length;
      PdfWorkbenchState next = State(await handler.ExecuteAsync(new SelectPdfPageCommand(1, true), CancellationToken.None));
      Assert.NotEqual(first.PagePreview.Url, next.PagePreview!.Url);
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () => await broker.OpenAsync(new Uri(first.PagePreview.Url), TestContext.Current.CancellationToken));
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () => await broker.OpenAsync(new Uri(first.PageInspect.Url), TestContext.Current.CancellationToken));
      Assert.Equal(files, Directory.GetFiles(root).Length);
      client.FailPreview = true;
      PdfWorkbenchState failed = State(await handler.ExecuteAsync(new RetryPdfPageInspectCommand(), CancellationToken.None));
      Assert.Equal("pdf.inspect.failed", failed.PageInspectStatusCode); Assert.Null(failed.PagePreview); Assert.Null(failed.PageInspect);
      client.FailPreview = false;
      PdfWorkbenchState retried = State(await handler.ExecuteAsync(new RetryPdfPageInspectCommand(), CancellationToken.None));
      Assert.Equal("pdf.inspect.ready", retried.PageInspectStatusCode);
      WorkbenchCommandOutcome stale = await handler.ExecuteAsync(new UpdatePdfBlockTextCommand(1, 0, "new", "old", model.Revision, "another-session"), CancellationToken.None);
      Assert.NotNull(stale.Error); Assert.Equal(0, client.EditCalls);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task LateInspectionCannotPublishForNewlySelectedPage()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-inspect-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client { PendingInspect = new TaskCompletionSource<Wire.PageInspectResponse>() };
      var model = new PdfViewModel(client, new Source());
      model.SetInspectionCapabilities(["pdf.page-inspect.v1", "pdf.block-edit.v1"]); await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
      using var broker = new WorkbenchResourceBroker(root); using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = Handler(model, root, broker, annotations);
      Task<PdfWorkbenchState> old = SelectAsync(handler, 0);
      await client.InspectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Task<PdfWorkbenchState> next = SelectAsync(handler, 1);
      client.PendingInspect.SetResult(Inspect(0));
      Assert.Null((await old).PagePreview);
      PdfWorkbenchState current = await next;
      Assert.Equal(1, current.SelectedPage); Assert.NotNull(current.PageInspect);
      await using WorkbenchResourceResponse content = await broker.OpenAsync(new Uri(current.PageInspect.Url), TestContext.Current.CancellationToken);
      using JsonDocument payload = await JsonDocument.ParseAsync(content.Content, cancellationToken: TestContext.Current.CancellationToken);
      Assert.Equal(1, payload.RootElement.GetProperty("page").GetInt32());
      Assert.Equal(1, client.PreviewCalls);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  private static async Task<PdfWorkbenchState> SelectAsync(DesktopWorkbenchCommandHandler handler, int page) =>
    State(await handler.ExecuteAsync(new SetCurrentPdfPageCommand(page), CancellationToken.None));
  private static PdfWorkbenchState State(WorkbenchCommandOutcome outcome) => Assert.IsType<PdfWorkbenchState>(Assert.Single(outcome.States));
  private static DesktopWorkbenchCommandHandler Handler(PdfViewModel model, string root, WorkbenchResourceBroker broker, WorkbenchAnnotationStore annotations) => new(
    static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
    () => model, static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
    new DiagnosticsViewModel("test", new PrerequisiteReport([])), broker, root, static () => 0, annotations);
  private static Wire.PageInspectResponse Inspect(int page) => new() { SchemaVersion = 2, InstanceId = "test", Page = page, Rotation = 0, Rect = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(612), JsonSerializer.SerializeToElement(792)], OcrBlocks = [], NativeLines = [] };
  private sealed class Source : IPdfFileSource
  {
    public Task<string?> PickFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> PickSavePathAsync(string name, CancellationToken ct) => Task.FromResult<string?>(null);
  }
  private sealed class Client : InferenceClientStub
  {
    public bool FailPreview { get; set; }
    public int PreviewCalls { get; private set; }
    public int LastDpi { get; private set; }
    public int EditCalls { get; private set; }
    public TaskCompletionSource<Wire.PageInspectResponse>? PendingInspect { get; init; }
    public TaskCompletionSource InspectEntered { get; } = new();
    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct) => Task.FromResult(new PdfSessionOpenResult("doc-1", 2, path,
      new Wire.PdfDocumentMirror { Pages = [new() { PageIndex = 0, Rect = Inspect(0).Rect }, new() { PageIndex = 1, Rect = Inspect(0).Rect }] }));
    public override Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct) => Task.FromResult(new byte[] { 1, 2, 3 });
    public override Task<Wire.PageInspectResponse> InspectPdfPageAsync(string sessionId, int page, CancellationToken ct) { InspectEntered.TrySetResult(); return page == 0 && PendingInspect is not null ? PendingInspect.Task : Task.FromResult(Inspect(page)); }
    public override Task<byte[]> RenderPdfPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct) { PreviewCalls++; LastDpi = dpi; return FailPreview ? Task.FromException<byte[]>(new IOException("synthetic rendering failure")) : Task.FromResult(new byte[] { 3, 2, 1 }); }
    public override Task<Wire.PdfMutationResponse> UpdatePdfBlockTextAsync(string sessionId, Wire.UpdateBlockTextRequest request, CancellationToken ct) { EditCalls++; throw new InvalidOperationException("Stale editing must not reach runtime."); }
  }
}
