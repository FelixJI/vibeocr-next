using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PdfTextLayerTests
{
  [Fact]
  public async Task WholeBookBeyondWindowSkipsExistingAndBoundsUploadsWritesAndResults()
  {
    var client = new PdfClient(70);
    client.Layered.UnionWith([0, 69]);
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    Assert.Equal(70, model.DetectedCount);
    await model.StartOcrAsync(Enumerable.Range(0, 70).ToArray(), false, CancellationToken.None, true);
    Assert.Equal(Enumerable.Range(1, 68), client.Written);
    Assert.All(client.BatchSizes, size => Assert.InRange(size, 1, 4));
    Assert.All(client.UploadSizes, size => Assert.InRange(size, 1, 4));
    Assert.Equal(17, client.UploadSizes.Count);
    Assert.All(client.Dpis, dpi => Assert.Equal(300, dpi));
    Assert.Equal(70, model.TextLayerCount);
    Assert.Equal(68, model.AddedCount);
    Assert.True(model.IsModified);
    Assert.True(model.Pages.Count(page => page.Result is not null) <= 64);
    await model.PrepareResultsAsync(0, 64, CancellationToken.None);
    Assert.NotNull(model.Pages[1].Result);
    Assert.Null(model.Pages[68].Result);
    int before = client.Written.Count;
    await model.StartOcrAsync(Enumerable.Range(0, 70).ToArray(), false, CancellationToken.None, true);
    Assert.Equal(before, client.Written.Count);
    await model.StartOcrAsync([1], true, CancellationToken.None, true);
    Assert.Equal(before + 1, client.Written.Count);
  }

  [Fact]
  public async Task ClosingAndReplacingDocumentReleaseWorkerSession()
  {
    var client = new PdfClient(1);
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("first.pdf", CancellationToken.None);
    await model.OpenPathAsync("second.pdf", CancellationToken.None);
    Assert.Equal(1, client.CloseCalls);
    await model.CloseSessionAsync(CancellationToken.None);
    Assert.Equal(2, client.CloseCalls);
    Assert.Empty(model.Pages);
  }

  [Fact]
  public async Task CancelWaitsForWriteAndCancellationSignalAndRetainsCommittedPage()
  {
    var client = new PdfClient(2) { WriteGate = new(), CancelGate = new() };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    Task adding = model.StartOcrAsync([0, 1], false, CancellationToken.None, true);
    Assert.True(model.IsSettling);
    model.Cancel();
    await model.RotateAsync([0], 90, CancellationToken.None);
    await model.StartOcrAsync([0], false, CancellationToken.None, true);
    Assert.Equal(0, client.RotateCalls);
    Assert.Single(client.UploadSizes);
    client.WriteGate.SetResult();
    await Task.Yield();
    Assert.True(model.IsSettling);
    client.CancelGate.SetResult();
    await adding;
    Assert.False(model.IsSettling);
    Assert.True(model.Pages[0].AddedThisSession);
    Assert.Equal(PdfPageState.Done, model.Pages[0].State);
    Assert.True(model.IsModified);
  }

  [Fact]
  public async Task CancellationKeepsGateUntilRecognitionWorkerIsTerminal()
  {
    var client = new PdfClient(1) { TerminalGate = new() };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    Task adding = model.StartOcrAsync([0], false, CancellationToken.None, true);
    model.Cancel();
    await model.SaveAsync("copy.pdf", CancellationToken.None);
    Assert.Null(client.SaveSettings);
    Assert.True(model.IsSettling);
    client.TerminalGate.SetResult();
    await adding;
    Assert.True(client.JobCancelRequested);
    Assert.False(model.IsSettling);
    Assert.Empty(client.Written);
  }

  [Fact]
  public async Task CancelledOpenWaitsForResourceAndClosesLateSession()
  {
    var client = new PdfClient(1) { OpenGate = new() };
    var model = new PdfViewModel(client, new Source());
    Task opening = model.OpenPathAsync("late.pdf", CancellationToken.None);
    model.Cancel();
    Assert.True(model.IsSettling);
    client.OpenGate.SetResult();
    await opening;
    Assert.Equal(1, client.CloseCalls);
    Assert.False(model.HasSession);
    Assert.False(model.IsSettling);
  }

  [Fact]
  public async Task UnknownRecognitionTerminalKeepsGateAndCloseFailureKeepsRecoveryReference()
  {
    var client = new PdfClient(1) { TerminalGate = new(), FailTerminal = true };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    Task adding = model.StartOcrAsync([0], false, CancellationToken.None, true);
    model.Cancel();
    client.TerminalGate.SetResult();
    await adding;
    Assert.True(model.IsSettling);
    Assert.Contains("终态未确认", model.Summary);
    Assert.Empty(client.Written);
    client.FailClose = true;
    await Assert.ThrowsAsync<IOException>(() => model.CloseSessionAsync(CancellationToken.None));
    Assert.Equal("pdf", model.SessionId);
    Assert.True(model.IsSettling);
    client.FailClose = false;
    await model.CloseSessionAsync(CancellationToken.None);
    Assert.False(model.HasSession);
    Assert.False(model.IsSettling);
  }

  [Theory]
  [InlineData(null)]
  [InlineData(12)]
  public async Task MissingOrNonInvertibleAngleAllowsExtractionButRejectsTextLayer(int? angle)
  {
    var client = new PdfClient(1) { Angle = angle };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    await model.StartOcrAsync([0], false, CancellationToken.None, true);
    Assert.Empty(client.Written);
    Assert.Equal(PdfPageState.Failed, model.Pages[0].State);
    Assert.Equal("中文 page-0", model.Pages[0].OcrText);
    await model.StartOcrAsync([0], false, CancellationToken.None);
    Assert.Equal(PdfPageState.Done, model.Pages[0].State);
  }

  [Fact]
  public async Task SaveFailureKeepsDirtyAndSettingsSaveClearsIt()
  {
    var client = new PdfClient(1);
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    await model.StartOcrAsync([0], false, CancellationToken.None, true);
    client.FailSave = true;
    await model.SaveAsync("copy.pdf", CancellationToken.None);
    Assert.True(model.IsModified);
    client.FailSave = false;
    model.SetProcessingSettings(new(FontSizeRatio: .6, CompressOnSave: false));
    await model.SaveAsync("copy.pdf", CancellationToken.None);
    Assert.False(model.IsModified);
    Assert.Equal(.6, client.SaveSettings!["font_size_ratio"].GetDouble());
    Assert.False(client.SaveSettings["compress_on_save"].GetBoolean());
  }

  [Fact]
  public void PixelBudgetAndPersistentOptionsAreReal()
  {
    var settings = new PdfProcessingSettings(RenderDpi: 600, MaxPixels: 4_000_000, FontSizeRatio: .6, CompressOnSave: false);
    int dpi = settings.DpiFor(612, 792);
    Assert.InRange(dpi, 72, 599);
    Assert.True(Math.Ceiling(612 * dpi / 72d) * Math.Ceiling(792 * dpi / 72d) <= settings.MaxPixels);
    Assert.Throws<ArgumentException>(() => settings.DpiFor(100000, 100000));
    string root = Path.Combine(Path.GetTempPath(), "vibeocr-pdf-options-" + Guid.NewGuid().ToString("N"));
    try
    {
      Directory.CreateDirectory(root);
      var layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.Next.exe"), "production");
      layout.EnsurePortableState();
      settings.Save(layout);
      Assert.Equal(settings, PdfProcessingSettings.Load(layout));
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
  }

  private sealed class Source : IPdfFileSource
  {
    public Task<string?> PickFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);
  }
  private sealed class PdfClient(int count) : InferenceClientStub
  {
    public HashSet<int> Layered { get; } = [];
    public HashSet<int> Added { get; } = [];
    public List<int> Written { get; } = [];
    public List<int> BatchSizes { get; } = [];
    public List<int> UploadSizes { get; } = [];
    public List<int> Dpis { get; } = [];
    private readonly Dictionary<string, IReadOnlyList<JobItem>> _jobs = [];
    private bool _dirty;
    public int? Angle { get; init; } = 0;
    public TaskCompletionSource? OpenGate { get; init; }
    public bool FailTerminal { get; init; }
    public bool FailClose { get; set; }
    public TaskCompletionSource? WriteGate { get; init; }
    public TaskCompletionSource? CancelGate { get; init; }
    public TaskCompletionSource? TerminalGate { get; init; }
    public bool JobCancelRequested { get; private set; }
    public int RotateCalls { get; private set; }
    public int CloseCalls { get; private set; }
    public override Task ClosePdfSessionAsync(string session, CancellationToken ct) { CloseCalls++; if (FailClose) throw new IOException("injected close failure"); return Task.CompletedTask; }
    public bool FailSave { get; set; }
    public IReadOnlyDictionary<string, JsonElement>? SaveSettings { get; private set; }
    private Wire.PdfPageInfoMirror Page(int index) => new() { PageIndex = index, HasTextLayer = Layered.Contains(index), HasOcrTextLayer = Added.Contains(index), Rect = new double[] { 0, 0, 612, 792 }.Select(value => JsonSerializer.SerializeToElement(value)).ToArray() };
    private Wire.PdfDocumentMirror Model() => new() { Pages = Enumerable.Range(0, count).Select(Page).ToArray(), IsModified = _dirty };
    public override async Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct)
    {
      Assert.False(ct.CanBeCanceled);
      if (OpenGate is not null) await OpenGate.Task;
      return new PdfSessionOpenResult("pdf", count, path, Model());
    }
    public override Task<Wire.PdfDocumentMirror> GetPdfModelAsync(string session, CancellationToken ct) => Task.FromResult(Model());
    public override Task LoadPdfAsync(string session, Action<JsonElement> progress, CancellationToken ct)
    {
      for (int i = 0; i < count; i++) progress(JsonSerializer.SerializeToElement(new { phase = "load", current = i + 1, total = count, page_payload = Page(i) }));
      return Task.CompletedTask;
    }
    public override Task<byte[]> RenderPdfPreviewAsync(string session, int page, int dpi, CancellationToken ct) { Dpis.Add(dpi); return Task.FromResult(new byte[] { 1 }); }
    public override Task<JobRef> SubmitAsync(SubmitRequest request, IReadOnlyDictionary<string, SubmitUpload> uploads, CancellationToken ct)
    {
      UploadSizes.Add(uploads.Count);
      string id = "job-" + UploadSizes.Count;
      var items = request.Items.Select((item, index) => new JobItem { ItemId = id + "-" + index, ClientItemKey = item.ClientItemKey, Ordinal = index, DisplayName = item.DisplayName, State = ItemState.Queued }).ToArray();
      _jobs[id] = items;
      return Task.FromResult(new JobRef { JobId = id, Items = items });
    }
    public override async Task<JobUpdate> ObserveAsync(string id, int sequence, CancellationToken ct)
    {
      if (TerminalGate is not null)
      {
        if (ct.CanBeCanceled) await Task.Delay(Timeout.Infinite, ct);
        else { await TerminalGate.Task; if (FailTerminal) throw new IOException("terminal observation failed"); }
      }
      var items = _jobs[id];
      return new JobUpdate {
        Events = [], Snapshot = new JobSnapshot { JobId = id, Kind = JobKind.Recognition, Priority = JobPriority.Background, State = JobState.Completed, Items = items }, ThroughSequence = sequence + 1,
        Outcomes = items.Select(item => new ItemOutcome { ItemId = item.ItemId, Attempt = 1, State = ItemState.Succeeded, PayloadType = "ocr.v1", Payload = Payload(item.ClientItemKey ?? throw new InvalidOperationException("Missing client key.")) }).ToArray(),
      };
    }
    public override Task<JobCommandResult> CommandAsync(JobCommand command, CancellationToken ct) { JobCancelRequested = true; return Task.FromResult(new JobCommandResult(command.CommandId, command.Kind, null, null)); }
    private Dictionary<string, JsonElement> Payload(string key)
    {
      var payload = new Dictionary<string, JsonElement> { ["raw_text"] = JsonSerializer.SerializeToElement("中文 " + key), ["text_blocks"] = JsonSerializer.SerializeToElement(new[] { new { text = "中文", score = .9, bbox = new[] { 100, 100, 500, 160 } } }) };
      if (Angle.HasValue) payload["preproc_angle"] = JsonSerializer.SerializeToElement(Angle.Value);
      return payload;
    }
    public override async Task<Wire.PdfMutationResponse> AddPdfTextLayersAsync(string session, Wire.BatchAddTextLayerRequest request, CancellationToken ct)
    {
      Assert.False(request.Save);
      Assert.False(ct.CanBeCanceled);
      if (WriteGate is not null) await WriteGate.Task;
      BatchSizes.Add(request.Pages.Count);
      foreach (var page in request.Pages) { Written.Add(page.Page); Layered.Add(page.Page); Added.Add(page.Page); }
      _dirty = true;
      return new() { SchemaVersion = 2, InstanceId = "test", Diff = new Wire.ModelDiff { ReplacedPages = request.Pages.Select(page => Page(page.Page)).ToArray(), ModifiedFlag = true }, Extra = new Dictionary<string, JsonElement> { ["results"] = JsonSerializer.SerializeToElement(request.Pages.ToDictionary(page => page.Page.ToString(), _ => new[] { 1, 0 })) } };
    }
    public override Task CancelPdfAsync(string session, CancellationToken ct) => CancelGate?.Task ?? Task.CompletedTask;
    public override Task<PdfMutateResult> RotatePdfPagesAsync(string session, int[] pages, int angle, CancellationToken ct) { RotateCalls++; return Task.FromResult(new PdfMutateResult(count)); }
    public override Task<string> SavePdfWithSettingsAsync(string session, string path, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken ct)
    {
      SaveSettings = settings;
      if (FailSave) throw new IOException("injected save failure");
      _dirty = false; return Task.FromResult(path);
    }
  }
}
