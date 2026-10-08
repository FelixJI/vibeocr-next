using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PdfPageOperationsTests
{
  private static RecognitionModeOption Mode(string availability = "ready", OcrEngine engine = OcrEngine.PaddleOcr, bool localModelsOnly = true) =>
    new("paddle_text", "paddleocr", "OCR", engine, "local", availability, null, null,
      localModelsOnly ? ["use_doc_orientation_classify", "use_doc_unwarping", "local_models_only"] : ["use_doc_orientation_classify", "use_doc_unwarping"], "local", false, false, false, false);

  [Fact]
  public async Task LandscapePortraitUseDisplayedSizeAndAreIdempotentWithoutOcr()
  {
    var client = new Client(3);
    client.Models[1] = Page(1, 0, 700, 300);
    client.Models[2] = Page(2, 0, 400, 400);
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    await model.OrientPagesAsync([0, 1, 2], true, CancellationToken.None);
    await model.OrientPagesAsync([0, 1, 2], true, CancellationToken.None);
    Assert.Equal(new[] { (0, 90) }, client.Rotations);
    await model.OrientPagesAsync([0, 1, 2], false, CancellationToken.None);
    Assert.Equal(new[] { (0, 90), (0, 90), (1, 90) }, client.Rotations);
    Assert.Equal(0, client.SubmitCount);
    Assert.Equal(0, client.RenderCount);
  }

  [Fact]
  public async Task StructureMapsResultsAndDoesNotReplaceOldPageIdentityWithNewPage()
  {
    var client = new Client(3);
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    model.Pages[0].OcrText = "old page zero";
    model.Pages[0].Result = new RecognizeResponse { Text = "old page zero", PreprocAngle = 0 };
    model.SelectedPage = 0;
    await model.InsertBlankAsync(0, 640, 480, CancellationToken.None);
    Assert.Equal(new int?[] { 0, null, 1, 2 }, model.LastPageMapping);
    Assert.Equal(640, model.Pages[1].Width);
    Assert.Null(model.Pages[1].Result);
    await model.ReorderAsync([2, 0, 1, 3], CancellationToken.None);
    Assert.Equal("old page zero", model.Pages[1].OcrText);
    Assert.Equal(1, model.SelectedPage);
    Assert.Equal(4, model.DetectedCount);
    await Assert.ThrowsAsync<ArgumentException>(() => model.ReorderAsync([0, 0, 1, 2], CancellationToken.None));
    Assert.Equal(1, client.ReorderCount);
  }

  [Theory]
  [InlineData(null, "未返回可判定")]
  [InlineData(-1, "未返回可判定")]
  [InlineData(0, "无需处理 1")]
  [InlineData(90, "已纠正 1")]
  public async Task DirectionRequiresActualAngleAndUsesCounterclockwiseCorrection(int? angle, string summary)
  {
    var client = new Client(2) { Angle = angle };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    model.SetRecognitionMode(Mode());
    await model.CorrectOrientationAsync([1], CancellationToken.None);
    Assert.Contains(summary, model.Summary);
    Assert.Equal(angle == 90 ? new[] { (1, -90) } : [], client.Rotations);
    Assert.True(client.Request!.Pipeline.Options["use_doc_orientation_classify"].GetBoolean());
    Assert.False(client.Request.Pipeline.Options["use_doc_unwarping"].GetBoolean());
    Assert.True(client.Request.Pipeline.Options["local_models_only"].GetBoolean());
    Assert.Equal(0, model.AddedCount);
    Assert.Null(model.Pages[1].Result);
  }

  [Fact]
  public async Task DirectionNegotiatesLocalPolicyAndReportsMissingModelsWithoutRotation()
  {
    var client = new Client(1) { FailurePhase = "models" };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    model.SetRecognitionMode(Mode(localModelsOnly: false));
    await model.CorrectOrientationAsync([0], CancellationToken.None);
    Assert.Contains("不支持仅使用本地模型", model.Summary);
    Assert.Equal(0, client.RenderCount); Assert.Equal(0, client.SubmitCount);
    model.SetRecognitionMode(Mode());
    await model.CorrectOrientationAsync([0], CancellationToken.None);
    Assert.Contains("本地模型未准备", model.Summary); Assert.Contains("显式运行 Paddle OCR", model.Summary);
    Assert.Contains("失败 1", model.Summary); Assert.Contains("无需处理 0", model.Summary);
    Assert.False(model.IsSettling); Assert.Empty(client.Rotations);
    await model.StartOcrAsync([0], false, CancellationToken.None);
    Assert.False(client.Request!.Pipeline.Options.ContainsKey("local_models_only"));
  }

  [Fact]
  public async Task UnsupportedUnavailableEmptyAndNoTextRemainDistinctAndNeverInstallOrWrite()
  {
    var client = new Client(1) { Angle = 90, Text = "" };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    await model.CorrectOrientationAsync([0], CancellationToken.None);
    Assert.Contains("已配置且就绪", model.Summary);
    model.SetRecognitionMode(Mode(engine: OcrEngine.RapidOcr));
    await model.CorrectOrientationAsync([0], CancellationToken.None);
    Assert.Contains("不支持", model.Summary);
    Assert.Equal(0, client.SubmitCount);
    model.SetRecognitionMode(Mode());
    await model.CorrectOrientationAsync([], CancellationToken.None);
    Assert.Equal(0, client.RenderCount);
    await model.CorrectOrientationAsync([0], CancellationToken.None);
    Assert.Contains("无可判定文字", model.Summary);
    Assert.Contains("失败 1", model.Summary);
    Assert.Empty(client.Rotations);
  }

  [Fact]
  public async Task CancelWaitsForActualRotationAndPreservesCompletedWrite()
  {
    var client = new Client(2) { Angle = 90, RotateGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    model.SetRecognitionMode(Mode());
    Task operation = model.CorrectOrientationAsync([0, 1], CancellationToken.None);
    await client.RotateEntered.Task;
    model.Cancel();
    Assert.True(model.IsSettling);
    await model.InsertBlankAsync(0, 100, 100, CancellationToken.None);
    Assert.Equal(2, model.PageCount);
    client.RotateGate.SetResult(); await operation;
    Assert.False(model.IsSettling);
    Assert.Equal(270, model.Pages[0].Rotation);
    Assert.Equal(new[] { (0, -90) }, client.Rotations);
    Assert.Contains("已纠正 1", model.Summary);
    Assert.Contains("未处理 1", model.Summary);
  }

  [Fact]
  public async Task LateThumbnailDoesNotReturnForNewRevision()
  {
    var client = new Client(1) { RenderGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    Task<byte[]?> thumbnail = model.RenderThumbnailAsync(0, CancellationToken.None);
    await model.RotateAsync([0], 90, CancellationToken.None);
    client.RenderGate.SetResult([1]);
    Assert.Null(await thumbnail);
  }

  [Fact]
  public async Task LostStructureResponseBlocksFurtherWritesUntilReopen()
  {
    var client = new Client(2) { LoseInsertResponse = true };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    await model.InsertBlankAsync(0, 100, 100, CancellationToken.None);
    Assert.Equal(3, client.Models.Count);
    Assert.Equal(2, model.PageCount);
    Assert.True(model.IsSettling);
    Assert.Contains("写入结果未确认", model.Summary);
    await model.RotateAsync([0], 90, CancellationToken.None);
    Assert.Empty(client.Rotations);
  }

  [Theory]
  [InlineData("render", false, "渲染失败")]
  [InlineData("detect", false, "方向检测失败")]
  [InlineData("rotate", true, "旋转失败")]
  public async Task DirectionFailuresNeverCountAsUnchangedAndKeepRecoveryBoundary(string phase, bool requiresReopen, string detail)
  {
    var client = new Client(1) { Angle = 90, FailurePhase = phase };
    var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
    model.SetRecognitionMode(Mode());
    await model.CorrectOrientationAsync([0], CancellationToken.None);
    Assert.Contains("失败 1", model.Summary); Assert.Contains("无需处理 0", model.Summary);
    Assert.Contains(detail, model.Summary); Assert.Equal(requiresReopen, model.IsSettling);
    Assert.Equal(0, model.AddedCount);
  }

  private sealed class Source : IPdfFileSource { public Task<string?> PickFileAsync(CancellationToken ct) => Task.FromResult<string?>(null); }
  private static Wire.PdfPageInfoMirror Page(int index, int rotation = 0, double width = 300, double height = 500) =>
    new() { PageIndex = index, Rotation = rotation, Rect = new[] { 0d, 0d, width, height }.Select(value => JsonSerializer.SerializeToElement(value)).ToArray(), HasTextLayer = false };

  private sealed class Client(int count) : InferenceClientStub
  {
    public List<Wire.PdfPageInfoMirror> Models { get; } = Enumerable.Range(0, count).Select(index => Page(index)).ToList();
    public List<(int, int)> Rotations { get; } = [];
    public int SubmitCount { get; private set; }
    public int RenderCount { get; private set; }
    public int ReorderCount { get; private set; }
    public int? Angle { get; init; } = 0;
    public string Text { get; init; } = "synthetic text";
    public SubmitRequest? Request { get; private set; }
    public TaskCompletionSource? RotateGate { get; init; }
    public TaskCompletionSource RotateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<byte[]>? RenderGate { get; init; }
    public bool LoseInsertResponse { get; init; }
    public string? FailurePhase { get; init; }
    private Wire.PdfDocumentMirror Model() => new() { Pages = Models.ToArray(), IsModified = Rotations.Count > 0 || ReorderCount > 0 || Models.Count != count };
    private PdfMutateResult Mutation() => new(Models.Count, new Wire.ModelDiff { FullModel = Model() });
    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct) => Task.FromResult(new PdfSessionOpenResult("session", Models.Count, path, Model()));
    public override Task<Wire.PdfDocumentMirror> GetPdfModelAsync(string sessionId, CancellationToken ct) => Task.FromResult(Model());
    public override Task LoadPdfAsync(string sessionId, Action<JsonElement> progress, CancellationToken ct)
    { foreach (var page in Models) progress(JsonSerializer.SerializeToElement(new { phase = "load", current = page.PageIndex + 1, total = Models.Count, page_payload = page })); return Task.CompletedTask; }
    public override Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct) => RenderGate?.Task ?? Task.FromResult(new byte[] { 1 });
    public override Task<byte[]> RenderPdfPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct) { RenderCount++; if (FailurePhase == "render") throw new IOException("synthetic render failed"); return Task.FromResult(new byte[] { 1 }); }
    public override async Task<PdfMutateResult> RotatePdfPagesAsync(string sessionId, int[] pages, int angle, CancellationToken ct)
    {
      Assert.False(ct.CanBeCanceled); RotateEntered.TrySetResult(); if (RotateGate is not null) await RotateGate.Task;
      if (FailurePhase == "rotate") throw new IOException("synthetic rotation response lost");
      foreach (int index in pages) { Rotations.Add((index, angle)); var page = Models[index]; Models[index] = Page(index, ((page.Rotation ?? 0) + angle + 360) % 360, page.Rect![3].GetDouble(), page.Rect[2].GetDouble()); }
      return Mutation();
    }
    public override Task<PdfMutateResult> InsertPdfBlankAsync(string sessionId, int afterIndex, double width, double height, CancellationToken ct)
    { Models.Insert(afterIndex + 1, Page(afterIndex + 1, 0, width, height)); Reindex(); if (LoseInsertResponse) throw new IOException("synthetic lost response"); return Task.FromResult(Mutation()); }
    public override Task<PdfMutateResult> ReorderPdfAsync(string sessionId, int[] newOrder, CancellationToken ct)
    { ReorderCount++; var old = Models.ToArray(); Models.Clear(); Models.AddRange(newOrder.Select(index => old[index])); Reindex(); return Task.FromResult(Mutation()); }
    private void Reindex() { for (int index = 0; index < Models.Count; index++) { var page = Models[index]; Models[index] = Page(index, page.Rotation ?? 0, page.Rect![2].GetDouble(), page.Rect[3].GetDouble()); } }
    public override Task<JobRef> SubmitAsync(SubmitRequest request, IReadOnlyDictionary<string, SubmitUpload> uploads, CancellationToken ct)
    { SubmitCount++; Request = request; return Task.FromResult(new JobRef { JobId = "job", Items = request.Items.Select(item => new JobItem { ItemId = "item", ClientItemKey = item.ClientItemKey, Ordinal = item.Ordinal, DisplayName = item.DisplayName, State = ItemState.Queued }).ToArray() }); }
    public override Task<JobUpdate> ObserveAsync(string jobId, int sequence, CancellationToken ct) => Task.FromResult(new JobUpdate {
      ThroughSequence = sequence + 1, Events = [], Snapshot = new JobSnapshot { JobId = jobId, Kind = JobKind.Recognition, Priority = JobPriority.Background, State = JobState.Completed, Items = [] },
      Outcomes = [new ItemOutcome { ItemId = "item", Attempt = 1, State = FailurePhase is "detect" or "models" ? ItemState.Failed : ItemState.Succeeded,
        ErrorCode = FailurePhase == "models" ? "BACKEND_UNAVAILABLE" : null,
        ErrorDetail = FailurePhase == "models" ? new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement("local_models_not_prepared") } : [],
        PayloadType = "ocr.v1", Payload = new Dictionary<string, JsonElement> {
        ["doc_orientation_angle"] = JsonSerializer.SerializeToElement(Angle), ["preproc_angle"] = JsonSerializer.SerializeToElement(0), ["raw_text"] = JsonSerializer.SerializeToElement(Text),
        ["text_blocks"] = JsonSerializer.SerializeToElement(new[] { new { text = Text, bbox = new[] { 100, 100, 800, 200 } } }) } }] });
  }
}
