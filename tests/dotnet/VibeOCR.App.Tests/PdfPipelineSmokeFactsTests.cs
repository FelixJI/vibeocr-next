using VibeOCR.App.Inference;

using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PdfPipelineSmokeFactsTests
{
  private static PdfSmokeRequestProbe.RequestEvent Event(
      string kind, double startedAtMs, double? durationMs = 10.0, string outcome = "completed",
      long? bytes = null, long? pixels = null, int page = 0, string? detail = null) =>
    new(kind, "s1", page, 0, startedAtMs, startedAtMs + durationMs, durationMs, outcome, null,
        bytes, pixels, detail);

  public sealed class PageSelection
  {
    [Fact]
    public void AllCoversEveryPageWithinWindow()
    {
      Assert.Equal([0, 1, 2, 3, 4, 5], PdfPipelinePageSelection.Resolve("all", 6));
    }

    [Fact]
    public void AllBeyondSelectionWindowIsRejected()
    {
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("all", 65));
    }

    [Fact]
    public void FirstNIsBoundedByWindowAndPageCount()
    {
      Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], PdfPipelinePageSelection.Resolve("first:8", 120));
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("first:65", 120));
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("first:9", 8));
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("first:0", 8));
    }

    [Fact]
    public void ExplicitPagesAreDistinctOrderedAndValidated()
    {
      Assert.Equal([1, 2, 3], PdfPipelinePageSelection.Resolve("pages:3,1,2", 6));
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("pages:5", 5));
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("pages:64", 120));
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("pages:x", 6));
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("pages:", 6));
    }

    [Fact]
    public void UnknownSpecAndNonPositivePageCountFail()
    {
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("everything", 6));
      Assert.Throws<InvalidOperationException>(
          () => PdfPipelinePageSelection.Resolve("all", 0));
    }

    [Fact]
    public void ExpectedBatchesMatchesSerialChunkContract()
    {
      Assert.Equal(1, PdfPipelinePageSelection.ExpectedBatches(1));
      Assert.Equal(1, PdfPipelinePageSelection.ExpectedBatches(4));
      Assert.Equal(2, PdfPipelinePageSelection.ExpectedBatches(5));
      Assert.Equal(2, PdfPipelinePageSelection.ExpectedBatches(8));
    }
  }

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

  public sealed class PhaseStats
  {
    [Fact]
    public void WindowFiltersByStartTimestampInProbeDomain()
    {
      var events = new List<PdfSmokeRequestProbe.RequestEvent>
      {
        Event("render_preview", 100.0),
        Event("job_submit", 199.9),
        Event("job_submit", 200.0),
        Event("add_text_layers", 400.0),
      };
      Assert.Equal(["render_preview", "job_submit"],
          PdfPipelinePhaseStats.Window(events, 50.0, 200.0).Select(request => request.Kind));
    }

    [Fact]
    public void ByKindAggregatesOnlyMeasuredValuesAndKeepsGapsExplicit()
    {
      var events = new List<PdfSmokeRequestProbe.RequestEvent>
      {
        Event("render_preview", 10.0, 10.0, bytes: 100, pixels: 1000),
        Event("render_preview", 20.0, 30.0, bytes: 200, pixels: 2000),
        Event("render_preview", 30.0, outcome: "pending"),
        Event("job_submit", 40.0, 5.0, outcome: "completed", bytes: 300),
      };
      IReadOnlyDictionary<string, PdfPipelineKindStat> byKind = PdfPipelinePhaseStats.ByKind(events);
      PdfPipelineKindStat render = byKind["render_preview"];
      Assert.Equal(3, render.Count);
      Assert.Equal(2, render.Completed);
      Assert.Equal(1, render.Pending);
      Assert.Equal(40.0, render.DurationSumMs);
      Assert.Equal(20.0, render.DurationMedianMs);
      Assert.Equal(30.0, render.DurationMaxMs);
      Assert.Equal(300, render.BytesTotal);
      Assert.Equal(3000, render.PixelsTotal);
      Assert.Equal(0, render.CompletedMissingBytes);
      PdfPipelineKindStat submit = byKind["job_submit"];
      Assert.Equal(300, submit.BytesTotal);
      Assert.Null(submit.PixelsTotal);
      var noBytes = new List<PdfSmokeRequestProbe.RequestEvent> { Event("save_operation", 1.0) };
      PdfPipelineKindStat save = PdfPipelinePhaseStats.ByKind(noBytes)["save_operation"];
      Assert.Null(save.BytesTotal);
      Assert.Equal(1, save.CompletedMissingBytes);
    }

    [Fact]
    public void ValidateOcrWindowEnforcesSerialContractAndFailClosed()
    {
      var ok = new List<PdfSmokeRequestProbe.RequestEvent>
      {
        Event("load_targeted", 1.0),
        Event("render_preview", 2.0, bytes: 100, pixels: 900),
        Event("job_submit", 3.0, detail: "job-1"),
        Event("job_observe", 4.0),
        Event("add_text_layers", 5.0),
      };
      Assert.Empty(PdfPipelinePhaseStats.ValidateOcrWindow(ok, expectedBatches: 1, pageCount: 4));

      Assert.Contains(PdfPipelinePhaseStats.ValidateOcrWindow(ok, expectedBatches: 2, pageCount: 8),
          error => error.Contains("job_submit count 1 != expected serial batches 2"));

      var faultedSubmit = new List<PdfSmokeRequestProbe.RequestEvent>
      {
        Event("render_preview", 1.0),
        Event("job_submit", 2.0, outcome: "faulted"),
      };
      Assert.Contains(PdfPipelinePhaseStats.ValidateOcrWindow(faultedSubmit, 1, 4),
          error => error.Contains("non-completed outcome"));

      var noRender = new List<PdfSmokeRequestProbe.RequestEvent>
      {
        Event("job_submit", 1.0, detail: "job-1"),
      };
      Assert.Contains(PdfPipelinePhaseStats.ValidateOcrWindow(noRender, 1, 4),
          error => error.Contains("render_preview"));

      var pendingWrite = new List<PdfSmokeRequestProbe.RequestEvent>
      {
        Event("render_preview", 1.0),
        Event("job_submit", 2.0, detail: "job-1"),
        Event("add_text_layers", 3.0, outcome: "pending"),
      };
      Assert.Contains(PdfPipelinePhaseStats.ValidateOcrWindow(pendingWrite, 1, 4),
          error => error.Contains("still pending"));
    }

    [Fact]
    public void FrameGapsKeepEmptyAsNotMeasuredAndCountThreshold()
    {
      (int count, double? median, double? max, int over) = PdfPipelinePhaseStats.FrameGaps([]);
      Assert.Equal(0, count);
      Assert.Null(median);
      Assert.Null(max);
      Assert.Equal(0, over);
      (count, median, max, over) = PdfPipelinePhaseStats.FrameGaps([16.0, 16.0, 300.0, 8.0]);
      Assert.Equal(4, count);
      Assert.Equal(16.0, median);
      Assert.Equal(300.0, max);
      Assert.Equal(1, over);
    }

    [Fact]
    public void MedianHandlesEvenAndOddCounts()
    {
      Assert.Null(PdfPipelinePhaseStats.Median([]));
      Assert.Equal(5.0, PdfPipelinePhaseStats.Median([5.0]));
      Assert.Equal(15.0, PdfPipelinePhaseStats.Median([10.0, 20.0]));
      Assert.Equal(20.0, PdfPipelinePhaseStats.Median([30.0, 10.0, 20.0]));
    }
  }
}
