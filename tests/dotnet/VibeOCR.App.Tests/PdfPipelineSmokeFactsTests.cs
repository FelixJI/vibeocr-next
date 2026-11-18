using System.Text.Json;
using VibeOCR.App.Inference;
using VibeOCR.Contracts.HttpV2;

using Xunit;

namespace VibeOCR.App.Tests;

[Collection("PDF smoke probe")]
public sealed class PdfPipelineSmokeFactsTests
{
  [Fact]
  public void BusinessWindowKeepsOwnedRequestsAndExcludesBackgroundAndUiReads()
  {
    PdfSmokeRequestProbe.RequestEvent[] events =
    [
      Event("render_preview", "s1", 100, "business_render"),
      Event("render_preview", "s1", 110, "ui_preview", "faulted"),
      Event("load_detect", "s1", 112, "document_detection", "cancelled"),
      Event("load_targeted", "s1", 114, "business_presence"),
      Event("load_targeted", "different", 120, "business_presence"),
      Event("job_submit", "", 130) with { Detail = "job1" },
      Event("job_observe", "job1", 140),
      Event("job_observe", "unowned", 150),
      Event("add_text_layers", "s1", 160),
      Event("save_operation", "s1", 170),
    ];
    var window = PdfPipelinePhaseStats.Window(events, 100, 160);
    Assert.DoesNotContain(window, request => request.Kind == "save_operation");
    var business = PdfPipelinePhaseStats.BusinessRequests(window, "s1");
    Assert.Equal(["render_preview", "load_targeted", "job_submit", "job_observe", "add_text_layers"],
      business.Select(request => request.Kind));
  }

  [Fact]
  public void CoverageRequiresExactUniquePagesIncludingBeyondTheProjection()
  {
    Assert.True(PdfPipelinePhaseStats.CoversPages([119, 65, 3], [3, 65, 119]));
    Assert.False(PdfPipelinePhaseStats.CoversPages([3, 65, 65], [3, 65, 119]));
    Assert.False(PdfPipelinePhaseStats.CoversPages([], [3, 65, 119]));
  }

  [Fact]
  public void PhaseStatsCountAllCompactedPollsWithoutInventingMedian()
  {
    var first = Event("job_observe", "job-1", 100);
    var compacted = Event("job_observe", "job-1", 110) with
    {
      DurationMs = null,
      Polls = new(3, 12, 2, 7),
    };
    var stats = JsonSerializer.SerializeToElement(PdfPipelinePhaseStats.ByKind([first, compacted]))
      .GetProperty("job_observe");
    Assert.Equal(4, stats.GetProperty("count").GetInt32());
    Assert.Equal(4, stats.GetProperty("completed").GetInt32());
    Assert.Equal(17, stats.GetProperty("duration_sum_ms").GetDouble());
    Assert.Equal(2, stats.GetProperty("duration_min_ms").GetDouble());
    Assert.Equal(7, stats.GetProperty("duration_max_ms").GetDouble());
    Assert.Equal(JsonValueKind.Null, stats.GetProperty("duration_median_ms").ValueKind);
  }

  [Fact]
  public void ObserveFactsKeepTerminalIdentityAndSettingsWithoutOutcomes()
  {
    var update = new JobUpdate
    {
      Snapshot = new JobSnapshot
      {
        JobId = "job-1", InstanceId = "s1", Kind = JobKind.Recognition, Priority = JobPriority.Background,
        State = JobState.Completed, FinishedAt = "2026-11-18T00:00:00Z",
        Pipeline = new PipelineSelection { PipelineId = "OCR", Engine = OcrEngine.PaddleOcr },
        Summary = new JobSummary { Succeeded = 1, Total = 1 },
        Items = [new JobItem { ItemId = "it-0000", DisplayName = "page-120.png", ClientItemKey = "page-119", State = ItemState.Succeeded }],
      },
      Events = [new StageEvent { Sequence = 1, Stage = "paddle_onnx_snapshot",
        Detail = new Dictionary<string, JsonElement>
        {
          ["pipeline_id"] = JsonSerializer.SerializeToElement("OCR"),
          ["device"] = JsonSerializer.SerializeToElement("cpu"),
          ["provider"] = JsonSerializer.SerializeToElement("CPUExecutionProvider"),
          ["auto_device_fallback"] = JsonSerializer.SerializeToElement(false),
        } }],
      Outcomes = [new ItemOutcome { ItemId = "it-0000", Attempt = 1, State = ItemState.Succeeded,
        Payload = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("private result") } }],
      ThroughSequence = 1,
    };
    var observation = Assert.IsType<PdfSmokeRequestProbe.Observation>(PdfSmokeRequestProbe.ObserveFacts(update).Job);
    var terminal = Assert.IsType<PdfSmokeRequestProbe.JobFact>(observation.Terminal);
    Assert.Equal("s1", terminal.InstanceId);
    Assert.Equal(OcrEngine.PaddleOcr, terminal.Engine);
    Assert.Equal("page-119", Assert.Single(terminal.Items).ClientItemKey);
    Assert.Equal("CPUExecutionProvider", observation.Paddle?.Provider);
    Assert.False(observation.Paddle?.AutomaticFallback);
    Assert.DoesNotContain("private result", JsonSerializer.Serialize(observation));
  }

  private static PdfSmokeRequestProbe.RequestEvent Event(
    string kind, string session, int start, string? purpose = null, string outcome = "completed") =>
    new(kind, session, 0, 0, start, start + 5, 5, outcome, null, Purpose: purpose);

  public sealed class PngFacts
  {
    [Fact]
    public void ParsesIhdrDimensionsFromCraftedPng()
    {
      byte[] png = CraftedPng(640, 480);
      Assert.True(PngImageFacts.TryParseDimensions(png, out int width, out int height));
      Assert.Equal(640, width);
      Assert.Equal(480, height);
      PdfSmokeRequestProbe.ResultFacts facts = PngImageFacts.Of(png);
      Assert.Equal(png.Length, facts.Bytes);
      Assert.Equal(640L * 480, facts.Pixels);
      Assert.Null(facts.Detail);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void ZeroDimensionsAreRejected(int width, int height)
    {
      Assert.False(PngImageFacts.TryParseDimensions(CraftedPng(width, height), out _, out _));
    }

    [Fact]
    public void NonPngAndTruncatedPayloadsStayUnparsed()
    {
      Assert.False(PngImageFacts.TryParseDimensions(
          [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x00, 0x00], out _, out _));
      Assert.False(PngImageFacts.TryParseDimensions(new byte[3], out _, out _));
      byte[] jpegLike = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[40]];
      PdfSmokeRequestProbe.ResultFacts facts = PngImageFacts.Of(jpegLike);
      Assert.Equal(jpegLike.Length, facts.Bytes);
      Assert.Null(facts.Pixels);
      Assert.Equal("png_ihdr_unparsed", facts.Detail);
      PdfSmokeRequestProbe.ResultFacts empty = PngImageFacts.Of(Array.Empty<byte>());
      Assert.Equal(0, empty.Bytes);
      Assert.Null(empty.Pixels);
    }

    internal static byte[] CraftedPng(int width, int height)
    {
      byte[] png = new byte[33];
      byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
      signature.CopyTo(png, 0);
      png[11] = 0x0D;
      png[12] = (byte)'I';
      png[13] = (byte)'H';
      png[14] = (byte)'D';
      png[15] = (byte)'R';
      png[16] = (byte)(width >> 24);
      png[17] = (byte)(width >> 16);
      png[18] = (byte)(width >> 8);
      png[19] = (byte)width;
      png[20] = (byte)(height >> 24);
      png[21] = (byte)(height >> 16);
      png[22] = (byte)(height >> 8);
      png[23] = (byte)height;
      png[24] = 8;
      png[25] = 6;
      return png;
    }
  }

}
