using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
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
      int files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
      PdfWorkbenchState next = State(await handler.ExecuteAsync(new SelectPdfPageCommand(1, true), CancellationToken.None));
      Assert.NotEqual(first.PagePreview.Url, next.PagePreview!.Url);
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () => await broker.OpenAsync(new Uri(first.PagePreview.Url), TestContext.Current.CancellationToken));
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () => await broker.OpenAsync(new Uri(first.PageInspect.Url), TestContext.Current.CancellationToken));
      Assert.Equal(files, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
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

  [Theory]
  [InlineData(150, 16_000_000, 150)]
  [InlineData(300, 1_000_000, 103)]
  public async Task RenderSettingsReloadCurrentPageWithoutChangingRevision(int dpi, int pixels, int expectedDpi)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-settings-{Guid.NewGuid():N}");
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
      PdfWorkbenchState original = await SelectAsync(handler, 0);
      int fileCount = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
      long revision = model.Revision;
      var settings = model.ProcessingSettings with { RenderDpi = dpi, MaxPixels = pixels };
      var refreshed = new TaskCompletionSource<PdfWorkbenchState>(TaskCreationOptions.RunContinuationsAsynchronously);
      PdfWorkbenchState? loading = null;
      handler.StateChanged += state =>
      {
        if (state is not PdfWorkbenchState current || current.ProcessingSettings != settings) return;
        if (current.PageInspectStatusCode == "pdf.inspect.loading") loading = current;
        if (current.PageInspectStatusCode == "pdf.inspect.ready") refreshed.TrySetResult(current);
      };
      WorkbenchCommandOutcome change = await handler.ExecuteAsync(new SetPdfProcessingSettingsCommand(settings), CancellationToken.None);
      Assert.Null(change.Error);
      PdfWorkbenchState updated = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.NotNull(loading); Assert.Null(loading.PagePreview); Assert.Null(loading.PageInspect);
      Assert.Equal(revision, model.Revision); Assert.Equal(expectedDpi, client.LastDpi); Assert.Equal(2, client.PreviewCalls);
      Assert.NotEqual(original.PagePreview!.Url, updated.PagePreview!.Url);
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () => await broker.OpenAsync(new Uri(original.PagePreview.Url), TestContext.Current.CancellationToken));
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () => await broker.OpenAsync(new Uri(original.PageInspect!.Url), TestContext.Current.CancellationToken));
      Assert.Equal(fileCount, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
      PdfWorkbenchState otherSettings = State(await handler.ExecuteAsync(new SetPdfProcessingSettingsCommand(settings with { CleanOnSave = true }), CancellationToken.None));
      Assert.Equal(updated.PagePreview.Url, otherSettings.PagePreview!.Url); Assert.Equal(2, client.PreviewCalls);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task LateRenderCannotPublishAfterSettingsChange()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var oldPreview = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
      var client = new Client { PendingPreview = oldPreview };
      var model = new PdfViewModel(client, new Source());
      model.SetInspectionCapabilities(["pdf.page-inspect.v1", "pdf.block-edit.v1"]);
      await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = Handler(model, root, broker, annotations);
      Task<PdfWorkbenchState> previous = SelectAsync(handler, 0);
      await client.PreviewEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      var settings = model.ProcessingSettings with { RenderDpi = 150 };
      var refreshed = new TaskCompletionSource<PdfWorkbenchState>(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is PdfWorkbenchState { PageInspectStatusCode: "pdf.inspect.ready" } current && current.ProcessingSettings == settings)
          refreshed.TrySetResult(current);
      };
      WorkbenchCommandOutcome change = await handler.ExecuteAsync(new SetPdfProcessingSettingsCommand(settings), CancellationToken.None);
      Assert.Null(change.Error); Assert.False(oldPreview.Task.IsCompleted);
      client.PendingPreview = null;
      oldPreview.SetResult([9, 8, 7]);
      await previous;
      PdfWorkbenchState current = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.Equal(2, client.PreviewCalls); Assert.Equal(150, client.LastDpi);
      await using WorkbenchResourceResponse content = await broker.OpenAsync(new Uri(current.PagePreview!.Url), TestContext.Current.CancellationToken);
      using var bytes = new MemoryStream();
      await content.Content.CopyToAsync(bytes, TestContext.Current.CancellationToken);
      Assert.Equal(new byte[] { 3, 2, 1 }, bytes.ToArray());
      Assert.Equal(4, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length); // two thumbnails + current inspect / preview
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Theory]
  [InlineData(true, true)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  public async Task CatalogRefreshLoadsOpenedPageOnlyWhenInspectionBecomesAvailable(bool opened, bool available)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-catalog-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client();
      var settings = new SettingsViewModel(client);
      PdfViewModel? model = opened ? new PdfViewModel(client, new Source()) : null;
      if (model is not null) await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
      int factoryCalls = 0;
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(client, new NoInput()),
        static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
        () => { factoryCalls++; return model ?? throw new InvalidOperationException("Catalog refresh must not create a PDF view model."); },
        () => settings, static () => new ShellViewModel(new NoShellActions(), new NoShellActions()), static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])), broker, root, static () => 0, annotations);
      await handler.RefreshRecognitionCatalogAsync(TestContext.Current.CancellationToken);
      if (opened)
      {
        PdfWorkbenchState legacy = await SelectAsync(handler, 0);
        Assert.False(legacy.CanInspectPage); Assert.Null(legacy.PagePreview); Assert.Null(legacy.PageInspect);
        Assert.NotNull(legacy.Pages![0].Thumbnail);
      }
      Assert.False(client.InspectEntered.Task.IsCompleted); Assert.Equal(0, client.PreviewCalls);
      long revision = model?.Revision ?? 0;
      client.Capabilities = available ? ["pdf.edit.v2", "pdf.page-inspect.v1", "pdf.block-edit.v1"] : ["pdf.edit.v2"];
      var published = new List<PdfWorkbenchState>();
      handler.StateChanged += state => { if (state is PdfWorkbenchState current) published.Add(current); };
      await handler.RefreshRecognitionCatalogAsync(TestContext.Current.CancellationToken);
      PdfWorkbenchState refreshed = Assert.Single(published);
      Assert.Equal(opened ? 1 : 0, factoryCalls);
      Assert.Equal(revision, model?.Revision ?? 0);
      if (opened && available)
      {
        Assert.True(refreshed.CanInspectPage); Assert.True(refreshed.CanCorrectText);
        Assert.Equal("pdf.inspect.ready", refreshed.PageInspectStatusCode);
        Assert.NotNull(refreshed.PagePreview); Assert.NotNull(refreshed.PageInspect);
        Assert.Equal(1, client.PreviewCalls); Assert.Equal(300, client.LastDpi);
        await using WorkbenchResourceResponse content = await broker.OpenAsync(new Uri(refreshed.PageInspect.Url), TestContext.Current.CancellationToken);
        using JsonDocument payload = await JsonDocument.ParseAsync(content.Content, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, payload.RootElement.GetProperty("page").GetInt32());
      }
      else
      {
        Assert.False(refreshed.CanInspectPage); Assert.Null(refreshed.PagePreview); Assert.Null(refreshed.PageInspect);
        Assert.False(client.InspectEntered.Task.IsCompleted); Assert.Equal(0, client.PreviewCalls);
        if (opened) Assert.NotNull(refreshed.Pages![0].Thumbnail);
        else { Assert.Equal(0, refreshed.PageCount); Assert.Equal(-1, refreshed.SelectedPage); }
      }
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  private sealed class NoShellActions : IHotkeyRegistrar, IStartupRegistrar
  {
    public bool Register(string hotkey, out string? conflict) => throw new NotSupportedException();
    public void Unregister() => throw new NotSupportedException();
    public bool SetEnabled(bool enabled) => throw new NotSupportedException();
  }

  private sealed class NoInput : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken ct) => throw new NotSupportedException();
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
    public IReadOnlyList<string> Capabilities { get; set; } = ["pdf.edit.v2"];
    public override Task<Wire.Health> GetHealthAsync(CancellationToken ct) => Task.FromResult(new Wire.Health
    {
      SchemaVersion = 2, InstanceId = "test", ProtocolVersion = 2, Ready = true, Draining = false,
      Capabilities = Capabilities, CapabilityDescriptors = [],
    });
    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken ct) => Task.FromResult(new SettingsSnapshot());
    public bool FailPreview { get; set; }
    public int PreviewCalls { get; private set; }
    public int LastDpi { get; private set; }
    public int EditCalls { get; private set; }
    public TaskCompletionSource<Wire.PageInspectResponse>? PendingInspect { get; init; }
    public TaskCompletionSource InspectEntered { get; } = new();
    public TaskCompletionSource<byte[]>? PendingPreview { get; set; }
    public TaskCompletionSource PreviewEntered { get; } = new();
    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct) => Task.FromResult(new PdfSessionOpenResult("doc-1", 2, path,
      new Wire.PdfDocumentMirror { Pages = [new() { PageIndex = 0, Rect = Inspect(0).Rect }, new() { PageIndex = 1, Rect = Inspect(0).Rect }] }));
    public override Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct) => Task.FromResult(new byte[] { 1, 2, 3 });
    public override Task<Wire.PageInspectResponse> InspectPdfPageAsync(string sessionId, int page, CancellationToken ct) { InspectEntered.TrySetResult(); return page == 0 && PendingInspect is not null ? PendingInspect.Task : Task.FromResult(Inspect(page)); }
    public override Task<byte[]> RenderPdfPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct) { PreviewCalls++; LastDpi = dpi; PreviewEntered.TrySetResult(); if (PendingPreview is not null) return PendingPreview.Task; return FailPreview ? Task.FromException<byte[]>(new IOException("synthetic rendering failure")) : Task.FromResult(new byte[] { 3, 2, 1 }); }
    public override Task<Wire.PdfMutationResponse> UpdatePdfBlockTextAsync(string sessionId, Wire.UpdateBlockTextRequest request, CancellationToken ct) { EditCalls++; throw new InvalidOperationException("Stale editing must not reach runtime."); }
  }
}
