using System.Text.Json;
using VibeOCR.App.Inference;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// T3（#328）观测窄缝契约：探针安装后 submit/observe/写层/保存/渲染类调用
/// 的结果、异常与取消语义与探针未安装时完全一致；事件记录真实 bytes/
/// PNG 像素/job id，缺测保持 null。观测不得改变业务结果。
/// </summary>
public sealed class DeferredInferenceClientProbeSeamTests
{
  private static byte[] Png(int width, int height) =>
    PdfPipelineSmokeFactsTests.PngFacts.CraftedPng(width, height);

  private static SubmitRequest SubmitRequestOf(int items)
  {
    var request = new SubmitRequest
    {
      RequestId = "request-1",
      Kind = JobKind.Recognition,
      Priority = JobPriority.Interactive,
      Pipeline = new PipelineSelection { PipelineId = "OCR" },
      Items = Enumerable.Repeat(new SubmitItem
      {
        ClientItemKey = "page-0",
        Ordinal = 0,
        DisplayName = "page-1.png",
        Source = new Dictionary<string, JsonElement>(),
      }, items).ToArray(),
    };
    return request;
  }

  private static Wire.BatchAddTextLayerRequest LayersOf(params int[] pages) => new()
  {
    Pages = pages.Select(page => new Wire.BatchAddTextLayerPage
    {
      Page = page,
      OcrResult = new Dictionary<string, JsonElement>(),
    }).ToArray(),
  };

  [Fact]
  public async Task SubmitRecordsUploadBytesAndJobIdWithoutChangingResult()
  {
    var deferred = new DeferredInferenceClient();
    var inner = new SeamStubInferenceClient();
    deferred.Attach(inner);
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      var uploads = new Dictionary<string, SubmitUpload>
      {
        ["input-0"] = new("image/png", new byte[100]),
        ["input-1"] = new("image/png", new byte[50]),
      };
      JobRef job = await deferred.SubmitAsync(SubmitRequestOf(2), uploads, CancellationToken.None);
      Assert.Equal("job-9", job.JobId);
      Assert.Equal(1, deferred.SubmitAttempts);
      Assert.Equal("job-9", deferred.LastSubmittedJobId);
      var (events, _, overflow) = probe.Snapshot();
      Assert.False(overflow);
      PdfSmokeRequestProbe.RequestEvent entry = Assert.Single(events);
      Assert.Equal("job_submit", entry.Kind);
      Assert.Equal(2, entry.Param);
      Assert.Equal(150, entry.Bytes);
      Assert.Null(entry.Pixels);
      Assert.Equal("job-9", entry.Detail);
      Assert.Equal("completed", entry.Outcome);
      // 未安装探针时同一调用保持原语义（结果与计数一致）。
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
    }
    JobRef direct = await deferred.SubmitAsync(SubmitRequestOf(1),
      new Dictionary<string, SubmitUpload>(), CancellationToken.None);
    Assert.Equal("job-9", direct.JobId);
    Assert.Equal(2, deferred.SubmitAttempts);
  }

  [Fact]
  public async Task ObserveRecordsJobIdScopeAndSequence()
  {
    var deferred = new DeferredInferenceClient();
    deferred.Attach(new SeamStubInferenceClient());
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      JobUpdate update = await deferred.ObserveAsync("job-9", 7, CancellationToken.None);
      Assert.Equal(7, update.ThroughSequence);
      var (events, _, _) = probe.Snapshot();
      PdfSmokeRequestProbe.RequestEvent entry = Assert.Single(events);
      Assert.Equal("job_observe", entry.Kind);
      Assert.Equal("job-9", entry.Session);
      Assert.Equal(7, entry.Param);
      Assert.Equal("completed", entry.Outcome);
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
    }
  }

  [Fact]
  public async Task AddTextLayersRecordsPagesAndReturnsSameInstance()
  {
    var deferred = new DeferredInferenceClient();
    var inner = new SeamStubInferenceClient();
    deferred.Attach(inner);
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      Wire.BatchAddTextLayerRequest request = LayersOf(2, 5, 6);
      Wire.PdfMutationResponse response = await deferred.AddPdfTextLayersAsync("s1", request, CancellationToken.None);
      Assert.Same(inner.Mutation, response);
      Assert.Same(request, inner.LastAddTextLayersRequest);
      var (events, _, _) = probe.Snapshot();
      PdfSmokeRequestProbe.RequestEvent entry = Assert.Single(events);
      Assert.Equal("add_text_layers", entry.Kind);
      Assert.Equal("s1", entry.Session);
      Assert.Equal(2, entry.Page);
      Assert.Equal(3, entry.Param);
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
    }
  }

  [Fact]
  public async Task SaveOperationRecordsFlagBits()
  {
    var deferred = new DeferredInferenceClient();
    var inner = new SeamStubInferenceClient();
    deferred.Attach(inner);
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      string saved = await deferred.SavePdfOperationAsync(
          "s1", "C:\\out.pdf", new Dictionary<string, JsonElement>(),
          copyExport: false, rebindTarget: true, overwrite: true, CancellationToken.None);
      Assert.Equal(SeamStubInferenceClient.SavedPath, saved);
      var (events, _, _) = probe.Snapshot();
      PdfSmokeRequestProbe.RequestEvent entry = Assert.Single(events);
      Assert.Equal("save_operation", entry.Kind);
      Assert.Equal(2 | 4, entry.Param);
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
    }
  }

  [Fact]
  public async Task RenderPreviewRecordsRealBytesAndPngPixels()
  {
    var deferred = new DeferredInferenceClient();
    var inner = new SeamStubInferenceClient { PreviewPayload = Png(4, 5) };
    deferred.Attach(inner);
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      byte[] preview = await deferred.RenderPdfPreviewAsync("s1", 3, 300, CancellationToken.None);
      Assert.Same(inner.PreviewPayload, preview);
      var (events, _, _) = probe.Snapshot();
      PdfSmokeRequestProbe.RequestEvent entry = Assert.Single(events);
      Assert.Equal("render_preview", entry.Kind);
      Assert.Equal(3, entry.Page);
      Assert.Equal(300, entry.Param);
      Assert.Equal(inner.PreviewPayload.Length, entry.Bytes);
      Assert.Equal(20, entry.Pixels);
      // 非 PNG 载荷：bytes 真实、像素保持缺口，不落 0。
      inner.PreviewPayload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25];
      await deferred.RenderPdfPreviewDemandAsync("s1",
          new Wire.RenderPreviewRequest { Page = 4, Dpi = 216 }, CancellationToken.None);
      var (all, _, _) = probe.Snapshot();
      Assert.Equal(2, all.Count);
      Assert.Null(all[1].Pixels);
      Assert.Equal(25, all[1].Bytes);
      Assert.Equal("png_ihdr_unparsed", all[1].Detail);
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
    }
  }

  [Fact]
  public async Task CancellationAndFaultsPropagateAndAreRecorded()
  {
    var deferred = new DeferredInferenceClient();
    var inner = new SeamStubInferenceClient
    {
      RenderCancelled = true,
      AddLayersFault = new InvalidOperationException("write boom"),
    };
    deferred.Attach(inner);
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        deferred.RenderPdfPreviewAsync("s1", 0, 300, CancellationToken.None));
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        deferred.AddPdfTextLayersAsync("s1", LayersOf(0), CancellationToken.None));
      var (events, _, _) = probe.Snapshot();
      Assert.Equal(2, events.Count);
      Assert.Equal("cancelled", events[0].Outcome);
      Assert.NotNull(events[0].EndedAtMs);
      Assert.Equal("faulted", events[1].Outcome);
      Assert.Contains("write boom", events[1].Error);
      Assert.Null(events[1].Bytes);
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
    }
  }

  [Fact]
  public async Task FactsDerivationFailureNeverChangesTheBusinessResult()
  {
    var deferred = new DeferredInferenceClient();
    var inner = new SeamStubInferenceClient { PreviewPayload = Png(2, 2) };
    deferred.Attach(inner);
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      byte[] preview = await probe.MeasureAsync("render_preview", "s1", 0, 0,
          () => Task.FromResult(inner.PreviewPayload),
          _ => throw new InvalidOperationException("facts boom"));
      Assert.Same(inner.PreviewPayload, preview);
      var (events, _, _) = probe.Snapshot();
      PdfSmokeRequestProbe.RequestEvent entry = Assert.Single(events);
      Assert.Equal("completed", entry.Outcome);
      Assert.Null(entry.Bytes);
      Assert.Null(entry.Pixels);
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
    }
  }

  private sealed class SeamStubInferenceClient : IInferenceClient
  {
    internal const string SavedPath = "C:\\isolated\\saved.pdf";

    public byte[] PreviewPayload { get; set; } = [];
    public Wire.PdfMutationResponse Mutation { get; } = new()
    {
      SchemaVersion = 2,
      InstanceId = "sup-1",
      Diff = new Wire.ModelDiff(),
    };
    public bool RenderCancelled { get; set; }
    public Exception? AddLayersFault { get; set; }
    public SubmitRequest? LastSubmitRequest { get; private set; }
    public Wire.BatchAddTextLayerRequest? LastAddTextLayersRequest { get; private set; }
    public (bool Copy, bool Rebind, bool Overwrite)? LastSaveFlags { get; private set; }

    public Uri BaseUrl => new("http://127.0.0.1:1");

    public Task<JobRef> SubmitAsync(
        SubmitRequest request,
        IReadOnlyDictionary<string, SubmitUpload> uploads,
        CancellationToken cancellationToken)
    {
      LastSubmitRequest = request;
      return Task.FromResult(new JobRef { JobId = "job-9" });
    }

    public Task<JobUpdate> ObserveAsync(string jobId, int afterSequence, CancellationToken cancellationToken)
      => Task.FromResult(new JobUpdate
      {
        Snapshot = new JobSnapshot
        {
          JobId = jobId,
          Kind = JobKind.Recognition,
          Priority = JobPriority.Interactive,
          State = JobState.Running,
        },
        Events = Array.Empty<StageEvent>(),
        Outcomes = Array.Empty<ItemOutcome>(),
        ThroughSequence = afterSequence,
      });

    public Task<JobCommandResult> CommandAsync(JobCommand command, CancellationToken cancellationToken)
      => Task.FromResult(new JobCommandResult(command.CommandId, command.Kind, CancelMode.Cooperative, null));

    public Task<Wire.PdfMutationResponse> AddPdfTextLayersAsync(
        string sessionId, Wire.BatchAddTextLayerRequest request, CancellationToken ct)
    {
      LastAddTextLayersRequest = request;
      return AddLayersFault is null
        ? Task.FromResult(Mutation)
        : Task.FromException<Wire.PdfMutationResponse>(AddLayersFault);
    }

    public Task<string> SavePdfOperationAsync(
        string sessionId, string outputPath, IReadOnlyDictionary<string, JsonElement> settings,
        bool copyExport, bool rebindTarget, bool overwrite, CancellationToken ct)
    {
      LastSaveFlags = (copyExport, rebindTarget, overwrite);
      return Task.FromResult(SavedPath);
    }

    public Task<byte[]> RenderPdfPreviewAsync(string sessionId, int page, int dpi, CancellationToken ct)
      => RenderCancelled
        ? Task.FromException<byte[]>(new OperationCanceledException("ct"))
        : Task.FromResult(PreviewPayload);

    public Task<byte[]> RenderPdfPreviewDemandAsync(
        string sessionId, Wire.RenderPreviewRequest request, CancellationToken ct)
      => Task.FromResult(PreviewPayload);

    public Task CancelPdfAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public Task ResetPdfCancelAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

    public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken ct)
      => Task.FromException<ExportResult>(new NotImplementedException());

    public Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken)
      => Task.FromResult(new ResidencyStatus());

    public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken)
      => Task.FromResult(new SettingsSnapshot());

    public Task<SettingsSnapshot> UpdateSettingsAsync(SettingsSnapshot settings, CancellationToken cancellationToken)
      => Task.FromResult(settings);

    public Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken)
      => Task.FromResult(new Wire.Health
      {
        SchemaVersion = 2, InstanceId = "sup-1", ProtocolVersion = 2, Ready = true,
        Draining = false, Capabilities = Array.Empty<string>(),
      });

    public Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct)
      => Task.FromException<PdfSessionOpenResult>(new NotImplementedException());

    public Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct)
      => Task.FromException<byte[]>(new NotImplementedException());

    public Task<PdfMutateResult> RotatePdfPagesAsync(string sessionId, int[] pages, int angle, CancellationToken ct)
      => Task.FromException<PdfMutateResult>(new NotImplementedException());

    public Task<PdfMutateResult> DeletePdfPagesAsync(string sessionId, int[] pages, CancellationToken ct)
      => Task.FromException<PdfMutateResult>(new NotImplementedException());

    public Task<string> SavePdfAsync(string sessionId, string outputPath, CancellationToken ct)
      => Task.FromException<string>(new NotImplementedException());

    public Task ClosePdfSessionAsync(string sessionId, CancellationToken ct)
      => Task.FromException(new NotImplementedException());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
