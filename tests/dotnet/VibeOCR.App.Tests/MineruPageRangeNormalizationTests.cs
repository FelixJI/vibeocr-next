using VibeOCR.App.Features.Recognition;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 单输入 MinerU 页码范围归一化契约：全局默认 page_range 仅对 PDF 输入
/// 保留；图片/截图等图像输入提交前固定 all（上游对非 PDF 输入拒绝页码
/// 范围），tier/ocr_mode/language 原样保留。
/// </summary>
public sealed class MineruPageRangeNormalizationTests
{
    [Fact]
    public void NormalizeKeepsPdfRangesAndForcesAllForImages()
    {
        var pdfRange = new MineruConfig(MineruTier.Flash, MineruOcrMode.Txt, "2-4", "korean");

        Assert.Null(RecognitionViewModel.NormalizeMineruPageRange(
            null, "image/png", "shot.png"));
        Assert.Equal(MineruConfig.AllPages, RecognitionViewModel.NormalizeMineruPageRange(
            new MineruConfig(MineruTier.Basic), "image/png", "shot.png")!.PageRange);
        Assert.Equal("2-4", RecognitionViewModel.NormalizeMineruPageRange(
            pdfRange, "application/pdf", "doc.pdf")!.PageRange);
        // 媒体类型缺失/未知但文件名明确 PDF：保留范围。
        Assert.Equal("2-4", RecognitionViewModel.NormalizeMineruPageRange(
            pdfRange, "application/octet-stream", "DOC.PDF")!.PageRange);
        // 图像输入：范围归一为 all，其余字段原样保留。
        MineruConfig normalized = RecognitionViewModel.NormalizeMineruPageRange(
            pdfRange, "image/png", "shot.png")!;
        Assert.Equal(MineruConfig.AllPages, normalized.PageRange);
        Assert.Equal(MineruTier.Flash, normalized.Tier);
        Assert.Equal(MineruOcrMode.Txt, normalized.OcrMode);
        Assert.Equal("korean", normalized.Language);
        // 非 PDF 名后缀不冒充 PDF。
        Assert.Equal(MineruConfig.AllPages, RecognitionViewModel.NormalizeMineruPageRange(
            pdfRange, null, "shot.pdf.png")!.PageRange);
    }

    [Fact]
    public async Task ImageSubmissionCarriesAllPagesForMineruJobs()
    {
        var fake = new CapturingClient();
        var viewModel = new RecognitionViewModel(fake, new StubInputService());
        viewModel.SetRecognitionMode(
            MineruMode(),
            new MineruConfig(MineruTier.Flash, MineruOcrMode.Txt, "2-4", "ch"));

        await viewModel.RecognizeCapturedInputAsync(
            new RecognitionInput([1, 2, 3, 4], "image/png", "shot.png", "screenshot"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.SubmitCalls);
        Assert.Equal(JobKind.MineruParse, fake.LastRequest!.Kind);
        Assert.Equal("MinerU", fake.LastRequest.Pipeline.PipelineId);
        Assert.NotNull(fake.LastRequest.Pipeline.Mineru);
        Assert.Equal(MineruTier.Flash, fake.LastRequest.Pipeline.Mineru.Tier);
        Assert.Equal(MineruOcrMode.Txt, fake.LastRequest.Pipeline.Mineru.OcrMode);
        Assert.Equal(MineruConfig.AllPages, fake.LastRequest.Pipeline.Mineru.PageRange);
        Assert.Equal("ch", fake.LastRequest.Pipeline.Mineru.Language);
        Assert.Equal("识别完成", viewModel.Status);
    }

    [Fact]
    public async Task PdfSubmissionKeepsTheConfiguredRange()
    {
        var fake = new CapturingClient();
        var viewModel = new RecognitionViewModel(fake, new StubInputService());
        viewModel.SetRecognitionMode(
            MineruMode(),
            new MineruConfig(MineruTier.Basic, MineruOcrMode.Auto, "1-3", "ch"));

        await viewModel.RecognizeCapturedInputAsync(
            new RecognitionInput([1, 2, 3, 4], "application/pdf", "doc.pdf", "file"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.SubmitCalls);
        Assert.NotNull(fake.LastRequest!.Pipeline.Mineru);
        Assert.Equal("1-3", fake.LastRequest.Pipeline.Mineru.PageRange);
    }

    private static RecognitionModeOption MineruMode() => new(
        "mineru_document",
        "document",
        "MinerU",
        null,
        "advanced_component",
        "ready",
        null,
        null,
        [],
        "process_keep_alive",
        SupportsPreload: true,
        SupportsTtl: false,
        SupportsPinning: false,
        SupportsRelease: false);

    private sealed class StubInputService : IInputService
    {
        public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RecognitionInput?>(null);

        public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RecognitionInput?>(null);

        public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RecognitionInput?>(null);

        public Task<RecognitionInput?> ReadDroppedFileAsync(
            string path, CancellationToken cancellationToken) =>
            Task.FromResult<RecognitionInput?>(null);
    }

    private sealed class CapturingClient : InferenceClientStub
    {
        public int SubmitCalls { get; private set; }

        public SubmitRequest? LastRequest { get; private set; }

        public override Task<JobRef> SubmitAsync(
            SubmitRequest request,
            IReadOnlyDictionary<string, SubmitUpload> uploads,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            LastRequest = request;
            return Task.FromResult(new JobRef
            {
                JobId = "job-mineru-page-range",
                Items =
                [
                    new JobItem
                    {
                        ItemId = "it-0",
                        ClientItemKey = request.Items[0].ClientItemKey,
                        Ordinal = 0,
                        DisplayName = request.Items[0].DisplayName,
                        State = ItemState.Queued,
                    },
                ],
            });
        }

        public override Task<JobUpdate> ObserveAsync(
            string jobId, int afterSequence, CancellationToken cancellationToken) =>
            Task.FromResult(new JobUpdate
            {
                Snapshot = new JobSnapshot
                {
                    JobId = jobId,
                    Kind = JobKind.MineruParse,
                    Priority = JobPriority.Interactive,
                    State = JobState.Completed,
                },
                Events = [],
                Outcomes =
                [
                    new ItemOutcome
                    {
                        ItemId = "it-0",
                        State = ItemState.Succeeded,
                        Attempt = 1,
                        PayloadType = "ocr.v1",
                        Payload = new Dictionary<string, System.Text.Json.JsonElement>
                        {
                            ["raw_text"] = System.Text.Json.JsonSerializer.SerializeToElement(
                                "mineru text"),
                        },
                    },
                ],
                ThroughSequence = afterSequence,
            });
    }
}
