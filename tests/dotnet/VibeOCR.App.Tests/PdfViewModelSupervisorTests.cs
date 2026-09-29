using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PdfViewModelSupervisorTests
{
    [Fact]
    public async Task NoSessionReturnsEarly()
    {
        var viewModel = new PdfViewModel(new FakePdfInference(), new StubPdfSource());

        await viewModel.StartOcrAsync([0], false, CancellationToken.None);

        Assert.Equal("请先打开 PDF", viewModel.Status);
    }

    [Fact]
    public async Task OcrRendersPagesThenSubmitsOneGenericRecognitionJob()
    {
        var fake = new FakePdfInference();
        var viewModel = new PdfViewModel(fake, new StubPdfSource());
        await viewModel.OpenPathAsync("test.pdf", CancellationToken.None);

        await viewModel.StartOcrAsync([0, 1], false, CancellationToken.None);

        Assert.Equal(2, fake.RenderCalls);
        Assert.Equal(1, fake.SubmitCalls);
        Assert.Equal(JobKind.Recognition, fake.LastRequest?.Kind);
        Assert.Equal(JobPriority.Background, fake.LastRequest?.Priority);
        Assert.Equal("OCR", fake.LastRequest?.Pipeline.PipelineId);
        Assert.Equal(["page-0", "page-1"],
            fake.LastRequest!.Items.Select(item => item.ClientItemKey));
        Assert.Equal(2, fake.LastUploads?.Count);
        Assert.Equal(PdfPageState.Done, viewModel.Pages[0].State);
        Assert.Equal("ocr-page-0", viewModel.Pages[0].OcrText);
        Assert.Equal(PdfPageState.Done, viewModel.Pages[1].State);
        Assert.Equal("ocr-page-1", viewModel.Pages[1].OcrText);
        Assert.Equal("OCR 完成：成功 2 页，失败 0 页", viewModel.Status);
    }

    [Fact]
    public async Task RecognitionModeRoutesPdfPagesThroughItsBoundPipeline()
    {
        var fake = new FakePdfInference();
        var viewModel = new PdfViewModel(fake, new StubPdfSource());
        viewModel.SetRecognitionMode(new RecognitionModeOption(
            "paddle_structure",
            "document",
            "PP-StructureV3",
            null,
            "advanced_component",
            "ready",
            null,
            "paddleocr-cpu",
            [],
            "model_residency",
            true,
            true,
            true,
            true));
        await viewModel.OpenPathAsync("test.pdf", CancellationToken.None);

        await viewModel.StartOcrAsync([0], false, CancellationToken.None);

        Assert.Equal("PP-StructureV3", fake.LastRequest?.Pipeline.PipelineId);
        Assert.Null(fake.LastRequest?.Pipeline.Engine);
    }

    [Fact]
    public async Task UnsupportedModeOptionRejectsPdfOcrInsteadOfSilentDrop()
    {
        // 回归契约（#110 AC2）：选项违反模式合同时，PDF OCR 在渲染/提交前
        // 明确拒绝并携带精确原因，不静默丢弃后仍提交。
        var fake = new FakePdfInference();
        var viewModel = new PdfViewModel(fake, new StubPdfSource());
        viewModel.SetRecognitionMode(new RecognitionModeOption(
            "paddle_formula",
            "specialized",
            "FORMULA_RECOGNITION",
            null,
            "advanced_component",
            "ready",
            null,
            "paddleocr-cpu",
            ["formula_recognition_batch_size"],
            "model_residency",
            true,
            true,
            true,
            true), options: new PaddleModeOptions { UseTableRecognition = true });
        await viewModel.OpenPathAsync("test.pdf", CancellationToken.None);

        await viewModel.StartOcrAsync([0], false, CancellationToken.None);

        Assert.Equal(0, fake.RenderCalls);
        Assert.Equal(0, fake.SubmitCalls);
        Assert.StartsWith("识别选项无效，已拒绝提交", viewModel.Status);
        Assert.Contains("use_table_recognition", viewModel.Status);
        Assert.Equal(PdfPageState.None, viewModel.Pages[0].State);
    }

    [Fact]
    public async Task OpenFailureKeepsNativeIssueForWorkbenchProjection()
    {
        // #110 回归：open 失败以原生语义码保留，供 workbench 映射固定
        // pdf.* 状态；无会话时不再被 PageCount 投影抹成 pdf.empty。
        var fake = new FailingPdfInference
        {
            OpenError = new InferenceClientException(
                HttpV2ErrorCode.BackendUnavailable, "supervisor down", retryable: true),
        };
        var viewModel = new PdfViewModel(fake, new StubPdfSource());

        await viewModel.OpenPathAsync("scan.pdf", CancellationToken.None);

        Assert.Null(viewModel.SessionId);
        Assert.Equal(PdfIssueKind.BackendUnavailable, viewModel.TerminalIssue);
        Assert.Equal("Supervisor 暂不可用", viewModel.Status);
    }

    [Fact]
    public async Task WorkbenchSurfacesDroppedOpenFailureAsNativeIssueState()
    {
        // 回归（旧实现失败）：真实 open 失败后 workbench 只按 PageCount 发布
        // pdf.empty，用户看到“尚未建立 PDF 会话”，失败被抹掉。
        string resourceRoot = Path.Combine(
            Path.GetTempPath(), $"vibeocr-pdf-issue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resourceRoot);
        try
        {
            var fake = new FailingPdfInference
            {
                OpenError = new InferenceClientException(
                    HttpV2ErrorCode.BackendUnavailable, "supervisor down", retryable: true),
            };
            var pdf = new PdfViewModel(fake, new StubPdfSource());
            using var broker = new WorkbenchResourceBroker(resourceRoot);
            using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
            await using var handler = CreatePdfWorkbenchHandler(pdf, resourceRoot, broker, annotationStore);

            WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
                new OpenDroppedPdfCommand("C:\\private\\scan.pdf"),
                CancellationToken.None);

            PdfWorkbenchState state = Assert.IsType<PdfWorkbenchState>(
                Assert.Single(outcome.States));
            Assert.Null(outcome.Error);
            Assert.False(state.IsBusy);
            Assert.Equal("pdf.backendUnavailable", state.StatusCode);
            Assert.Equal(0, state.PageCount);
        }
        finally
        {
            Directory.Delete(resourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WorkbenchSurfacesMutateFailureWithOpenSession()
    {
        // 回归（旧实现失败）：会话打开时的 mutate 失败原先仍被 PageCount 投影
        // 成 pdf.open；现在按语义码发布，且不携带路径/异常细节。
        string resourceRoot = Path.Combine(
            Path.GetTempPath(), $"vibeocr-pdf-issue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resourceRoot);
        try
        {
            var fake = new FailingPdfInference
            {
                RotateError = new InferenceClientException(
                    HttpV2ErrorCode.OutOfMemory, "oom", retryable: false),
            };
            var pdf = new PdfViewModel(fake, new StubPdfSource());
            using var broker = new WorkbenchResourceBroker(resourceRoot);
            using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
            await using var handler = CreatePdfWorkbenchHandler(pdf, resourceRoot, broker, annotationStore);

            WorkbenchCommandOutcome open = await handler.ExecuteAsync(
                new OpenDroppedPdfCommand("doc.pdf"), CancellationToken.None);
            Assert.Equal("pdf.open",
                Assert.IsType<PdfWorkbenchState>(Assert.Single(open.States)).StatusCode);

            WorkbenchCommandOutcome rotate = await handler.ExecuteAsync(
                new RotatePdfCommand(90), CancellationToken.None);

            PdfWorkbenchState state = Assert.IsType<PdfWorkbenchState>(
                Assert.Single(rotate.States));
            Assert.False(state.IsBusy);
            Assert.Equal("pdf.outOfMemory", state.StatusCode);
            Assert.Equal(2, state.PageCount);
        }
        finally
        {
            Directory.Delete(resourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledSaveAndRotateStayDistinctFromFailure()
    {
        // 取消与失败必须区分：取消不清除会话，也不得写成保存/操作失败。
        var fake = new FailingPdfInference();
        var viewModel = new PdfViewModel(fake, new StubPdfSource());
        await viewModel.OpenPathAsync("doc.pdf", CancellationToken.None);
        Assert.Null(viewModel.TerminalIssue);

        await viewModel.SaveAsync("C:\\out\\result.pdf", new CancellationToken(canceled: true));

        Assert.Equal(PdfIssueKind.Cancelled, viewModel.TerminalIssue);
        Assert.Equal("已取消", viewModel.Status);

        await viewModel.RotateAsync([0], 90, new CancellationToken(canceled: true));

        Assert.Equal(PdfIssueKind.Cancelled, viewModel.TerminalIssue);
        Assert.Equal("已取消", viewModel.Status);
        Assert.Equal("pdf-1", viewModel.SessionId);
        Assert.Equal(2, viewModel.PageCount);
    }

    [Fact]
    public async Task LateOpenFailureDoesNotOverrideNewerSession()
    {
        // 迟到失败（已被新会话取代）不得覆盖新会话状态或写入问题码。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FailingPdfInference { LateOpenGate = gate.Task };
        var viewModel = new PdfViewModel(fake, new StubPdfSource());

        Task stale = viewModel.OpenPathAsync("bad.pdf", CancellationToken.None);
        await viewModel.OpenPathAsync("good.pdf", CancellationToken.None);

        gate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale);

        Assert.Equal("pdf-1", viewModel.SessionId);
        Assert.Null(viewModel.TerminalIssue);
        Assert.Equal("已打开 2 页", viewModel.Status);
    }

    private static DesktopWorkbenchCommandHandler CreatePdfWorkbenchHandler(
        PdfViewModel pdf,
        string resourceRoot,
        WorkbenchResourceBroker broker,
        WorkbenchAnnotationStore annotationStore) =>
        new(
            static () => throw new InvalidOperationException(),
            static () => throw new InvalidOperationException(),
            static () => throw new InvalidOperationException(),
            () => pdf,
            static () => throw new InvalidOperationException(),
            static () => throw new InvalidOperationException(),
            static () => throw new InvalidOperationException(),
            new DiagnosticsViewModel("test", new PrerequisiteReport([])),
            broker,
            resourceRoot,
            static () => 0,
            annotationStore);

    private sealed class StubPdfSource : IPdfFileSource
    {
        public Task<string?> PickFileAsync(CancellationToken ct) =>
            Task.FromResult<string?>("test.pdf");
    }

    /// <summary>
    /// 按需注入失败的 PDF 会话 fake：默认行为成功（会话 pdf-1，2 页），
    /// 通过 OpenError/RotateError/LateOpenGate 或预取消的 CancellationToken
    /// 触发待测的失败/取消路径。
    /// </summary>
    private sealed class FailingPdfInference : InferenceClientStub
    {
        public InferenceClientException? OpenError { get; init; }
        public InferenceClientException? RotateError { get; init; }
        public Task? LateOpenGate { get; init; }
        private int _openCalls;

        public override async Task<PdfSessionOpenResult> OpenPdfSessionAsync(
            string path,
            string? password,
            CancellationToken ct)
        {
            // LateOpenGate 只门控第一次 open（迟到失败）；后续 open 正常成功。
            if (++_openCalls == 1 && LateOpenGate is { } gate)
            {
                await gate;
                throw new InvalidOperationException("late open failed");
            }
            if (OpenError is { } error) throw error;
            return new PdfSessionOpenResult("pdf-1", 2, path);
        }

        public override Task<byte[]> RenderPdfPageAsync(
            string sessionId,
            int page,
            int size,
            CancellationToken ct) => Task.FromResult(new byte[] { 1, 2, 3 });

        public override Task<PdfMutateResult> RotatePdfPagesAsync(
            string sessionId,
            int[] pages,
            int angle,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (RotateError is { } error) throw error;
            return Task.FromResult(new PdfMutateResult(pages.Length));
        }

        public override Task<string> SavePdfAsync(
            string sessionId,
            string outputPath,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(outputPath);
        }
    }

    private sealed class FakePdfInference : InferenceClientStub
    {
        private IReadOnlyList<JobItem> _items = Array.Empty<JobItem>();

        public int RenderCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public SubmitRequest? LastRequest { get; private set; }
        public IReadOnlyDictionary<string, SubmitUpload>? LastUploads { get; private set; }

        public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(
            string path,
            string? password,
            CancellationToken ct) =>
            Task.FromResult(new PdfSessionOpenResult("pdf-1", 2, path));

        public override Task<byte[]> RenderPdfPageAsync(
            string sessionId,
            int page,
            int size,
            CancellationToken ct)
        {
            RenderCalls++;
            return Task.FromResult(new byte[] { (byte)page, 1, 2 });
        }

        public override Task<JobRef> SubmitAsync(
            SubmitRequest request,
            IReadOnlyDictionary<string, SubmitUpload> uploads,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            LastRequest = request;
            LastUploads = uploads;
            _items = request.Items.Select((item, index) => new JobItem
            {
                ItemId = $"pdf-item-{index}",
                ClientItemKey = item.ClientItemKey,
                Ordinal = item.Ordinal,
                DisplayName = item.DisplayName,
                State = ItemState.Queued,
            }).ToArray();
            return Task.FromResult(new JobRef
            {
                JobId = "pdf-ocr-1",
                Items = _items,
            });
        }

        public override Task<JobUpdate> ObserveAsync(
            string jobId,
            int afterSequence,
            CancellationToken cancellationToken)
        {
            // Reverse wire order to prove page mapping uses client item keys.
            ItemOutcome[] outcomes = _items.Reverse().Select(item => new ItemOutcome
            {
                ItemId = item.ItemId,
                State = ItemState.Succeeded,
                Attempt = 1,
                PayloadType = "ocr.v1",
                Payload = new Dictionary<string, JsonElement>
                {
                    ["raw_text"] = JsonSerializer.SerializeToElement(
                        $"ocr-{item.ClientItemKey}"),
                },
            }).ToArray();
            return Task.FromResult(new JobUpdate
            {
                Snapshot = new JobSnapshot
                {
                    JobId = jobId,
                    Kind = JobKind.Recognition,
                    Priority = JobPriority.Background,
                    State = JobState.Completed,
                    Items = _items,
                },
                Events = Array.Empty<StageEvent>(),
                Outcomes = outcomes,
                ThroughSequence = afterSequence + 1,
            });
        }
    }
}
