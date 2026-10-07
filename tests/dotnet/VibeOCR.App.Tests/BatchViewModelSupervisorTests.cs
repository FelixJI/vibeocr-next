// Phase 7B tests: BatchViewModel v2 supervisor path.
//
// Verifies plan §7B: "BatchViewModel 一次提交逻辑 job, 不在 UI 切 GPU 微批".
// The v2 path submits ALL pending inputs in ONE generic recognition job, then
// maps typed outcomes by client item key rather than response position.
using System.Text.Json;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class BatchViewModelSupervisorTests
{
    [Theory]
    [InlineData("xlsx", ".xlsx")]
    [InlineData("docx", ".docx")]
    public void OfficeBatchExportsKeepTheirFormatAndDistinctNames(string format, string extension)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-export-{Guid.NewGuid():N}");
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(root, "table" + extension),
            BatchCommands.UniqueOutputPath(root, "table.png", format, reserved));
        Assert.Equal(Path.Combine(root, "table_1" + extension),
            BatchCommands.UniqueOutputPath(root, "table.jpg", format, reserved));
    }

    [Fact]
    public async Task SupervisorPathSubmitsAllInputsAsOneJobAndMapsPerItemResults()
    {
        var files = new FakeBatchFileSource();
        var fake = new FakeBatchInferenceClient();
        var viewModel = new BatchViewModel(fake, files);

        viewModel.AddFiles([CreateTempPng("a"), CreateTempPng("b"), CreateTempPng("c")]);
        await viewModel.StartAsync(TestContext.Current.CancellationToken);

        // Exactly one submit carrying all three inputs.
        Assert.Equal(1, fake.SubmitCalls);
        Assert.NotNull(fake.LastUploads);
        Assert.Equal(3, fake.LastUploads!.Count);
        Assert.Equal(JobKind.Recognition, fake.LastRequest?.Kind);
        Assert.Equal(JobPriority.Background, fake.LastRequest?.Priority);
        Assert.Equal("OCR", fake.LastRequest?.Pipeline.PipelineId);
        // The fake returns outcomes in reverse order. Correct UI order proves
        // mapping uses client_item_key through the JobRef item mapping.
        Assert.Equal(3, viewModel.CompletedCount);
        Assert.Equal(0, viewModel.FailedCount);
        Assert.Equal(BatchItemState.Completed, viewModel.Items[0].State);
        Assert.Equal($"ocr-{Path.GetFileNameWithoutExtension(viewModel.Items[0].Name)}", viewModel.Items[0].Result?.Text);
        Assert.Equal($"ocr-{Path.GetFileNameWithoutExtension(viewModel.Items[1].Name)}", viewModel.Items[1].Result?.Text);
        Assert.Equal($"ocr-{Path.GetFileNameWithoutExtension(viewModel.Items[2].Name)}", viewModel.Items[2].Result?.Text);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task SupervisorPathContinuesOnPerItemFailure()
    {
        // Item 1 fails (ErrorCode set); items 0 and 2 still complete.
        var files = new FakeBatchFileSource();
        var fake = new FakeBatchInferenceClient(perItemFailures: new HashSet<int> { 1 });
        var viewModel = new BatchViewModel(fake, files);

        viewModel.AddFiles([CreateTempPng("a"), CreateTempPng("b"), CreateTempPng("c")]);
        await viewModel.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, viewModel.CompletedCount);
        Assert.Equal(1, viewModel.FailedCount);
        Assert.Equal(BatchItemState.Completed, viewModel.Items[0].State);
        Assert.Equal(BatchItemState.Failed, viewModel.Items[1].State);
        Assert.NotNull(viewModel.Items[1].Error);
        Assert.Equal(BatchItemState.Completed, viewModel.Items[2].State);
    }

    [Fact]
    public async Task RecognitionModeRoutesTheWholeBatchThroughItsBoundPipeline()
    {
        var files = new FakeBatchFileSource();
        var fake = new FakeBatchInferenceClient();
        var viewModel = new BatchViewModel(fake, files);
        viewModel.SetRecognitionMode(DocumentMode(
            "mineru_document",
            "MinerU",
            "process_keep_alive"), new MineruConfig(MineruTier.Basic));
        viewModel.AddFiles([CreateTempPng("m")]);

        await viewModel.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("MinerU", fake.LastRequest?.Pipeline.PipelineId);
        Assert.Equal(JobKind.MineruParse, fake.LastRequest?.Kind);
        Assert.Null(fake.LastRequest?.Pipeline.Engine);
        Assert.Equal(MineruTier.Basic, fake.LastRequest?.Pipeline.Mineru?.Tier);
    }

    [Fact]
    public async Task UnsupportedModeOptionRejectsBatchSubmissionInsteadOfSilentDrop()
    {
        // 回归契约（#110 AC2）：选项违反模式合同时，整批提交明确拒绝，
        // 不静默丢弃不支持字段后仍提交。
        var files = new FakeBatchFileSource();
        var fake = new FakeBatchInferenceClient();
        var viewModel = new BatchViewModel(fake, files);
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
        viewModel.AddFiles([CreateTempPng("bad")]);

        await viewModel.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, fake.SubmitCalls);
        Assert.Equal(BatchItemState.Failed, viewModel.Items[0].State);
        Assert.StartsWith("INVALID_MODE_OPTIONS", viewModel.Items[0].Error);
        Assert.Contains("use_table_recognition", viewModel.Items[0].Error);
        Assert.Equal(1, viewModel.FailedCount);
        Assert.Equal(0, viewModel.CompletedCount);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task SupervisorPathMarksItemsCancelledWhenJobCancelled()
    {
        var files = new FakeBatchFileSource();
        var fake = new FakeBatchInferenceClient(terminalState: JobState.Cancelled);
        var viewModel = new BatchViewModel(fake, files);

        viewModel.AddFiles([CreateTempPng("a"), CreateTempPng("b")]);
        await viewModel.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BatchItemState.Cancelled, viewModel.Items[0].State);
        Assert.Equal(BatchItemState.Cancelled, viewModel.Items[1].State);
        Assert.Equal(0, viewModel.CompletedCount);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task SupervisorPathDoesNotSliceIntoMultipleSubmits()
    {
        // The plan explicitly forbids the UI from slicing a batch into
        // per-item microbatches. Assert exactly one submit regardless of input count.
        var files = new FakeBatchFileSource();
        var fake = new FakeBatchInferenceClient();
        var viewModel = new BatchViewModel(fake, files);

        viewModel.AddFiles(Enumerable.Range(0, 8).Select(i => CreateTempPng($"f{i}")).ToArray());
        await viewModel.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.SubmitCalls);
        Assert.Equal(8, fake.LastUploads!.Count);
        Assert.Equal(8, viewModel.CompletedCount);
    }

    [Fact]
    public async Task LocalCancellationUsesOneGenericCancelCommand()
    {
        var files = new FakeBatchFileSource();
        var fake = new FakeBatchInferenceClient();
        var viewModel = new BatchViewModel(fake, files);
        viewModel.AddFiles([CreateTempPng("a"), CreateTempPng("b")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await viewModel.StartAsync(cancellation.Token);

        Assert.NotNull(fake.LastCommand);
        Assert.Equal(JobCommandKind.Cancel, fake.LastCommand!.Kind);
        Assert.Equal("batch-1", fake.LastCommand.JobId);
        Assert.All(viewModel.Items, item => Assert.Equal(BatchItemState.Cancelled, item.State));
    }

    [Fact]
    public async Task EmptyBatchReturnsImmediately()
    {
        var viewModel = new BatchViewModel(new FakeBatchInferenceClient(), new FakeBatchFileSource());
        await viewModel.StartAsync(CancellationToken.None); // No items -> returns immediately
        Assert.False(viewModel.IsRunning);
    }

    // ------------------------------------------------------------------
    // 文档输入回归（批量接受 PDF/Office 前必须能拒绝错误的引擎）
    // ------------------------------------------------------------------

    [Fact]
    public async Task OfficeIsRejectedWhilePdfPagesAndImagesUseImagePipeline()
    {
        // Office 仍按文件拒绝，PDF 页和图片进入当前识别管道。
        string root = NewTempDirectory();
        try
        {
            var files = new FakeBatchFileSource();
            var fake = new FakeBatchInferenceClient();
            var viewModel = new BatchViewModel(fake, files);
            viewModel.AddFiles([
                CreateTempInput(root, "sheet", ".xlsx"),
                CreateTempInput(root, "scan", ".png"),
                CreateTempInput(root, "paper", ".pdf")]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, fake.SubmitCalls);
            Assert.Equal(4, fake.LastUploads!.Count);
            Assert.All(fake.LastUploads.Values, upload => Assert.Equal("image/png", upload.ContentType));
            Assert.Equal(BatchItemState.Completed, viewModel.Items.Single(item => item.Name == "scan.png").State);
            Assert.Equal(BatchItemState.Completed, viewModel.Items.Single(item => item.Name == "paper.pdf").State);
            foreach (string name in new[] { "sheet.xlsx" })
            {
                BatchItemViewModel rejected = viewModel.Items.Single(item => item.Name == name);
                Assert.Equal(BatchItemState.Failed, rejected.State);
                Assert.StartsWith("UNSUPPORTED_INPUT_KIND", rejected.Error);
                Assert.Contains("MinerU", rejected.Error);
            }
            Assert.Equal(1, viewModel.FailedCount);
            Assert.Equal(2, viewModel.CompletedCount);
            Assert.False(viewModel.IsRunning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedExtensionIsRejectedPerItemInsteadOfAbortingTheBatch()
    {
        // 拖放不过滤扩展名：未知格式按文件失败，不炸掉整批。
        string root = NewTempDirectory();
        try
        {
            var files = new FakeBatchFileSource();
            var fake = new FakeBatchInferenceClient();
            var viewModel = new BatchViewModel(fake, files);
            viewModel.AddFiles([CreateTempInput(root, "notes", ".txt"), CreateTempInput(root, "scan", ".png")]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, fake.SubmitCalls);
            Assert.Equal(BatchItemState.Failed, viewModel.Items.Single(item => item.Name == "notes.txt").State);
            Assert.StartsWith("UNSUPPORTED_INPUT_KIND", viewModel.Items.Single(item => item.Name == "notes.txt").Error);
            Assert.Contains(".txt", viewModel.Items.Single(item => item.Name == "notes.txt").Error);
            Assert.Equal(BatchItemState.Completed, viewModel.Items.Single(item => item.Name == "scan.png").State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MineruSubmitsPdfWithDocumentMediaType()
    {
        // MinerU 模式提交 PDF 时携带 application/pdf（旧实现仅认图片）。
        string root = NewTempDirectory();
        try
        {
            var files = new FakeBatchFileSource();
            var fake = new FakeBatchInferenceClient();
            var viewModel = new BatchViewModel(fake, files);
            viewModel.SetRecognitionMode(DocumentMode(
                "mineru_document",
                "MinerU",
                "process_keep_alive"), new MineruConfig(MineruTier.Basic));
            viewModel.AddFiles([CreateTempInput(root, "report", ".pdf")]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, fake.SubmitCalls);
            Assert.Equal(JobKind.MineruParse, fake.LastRequest?.Kind);
            Assert.Equal("application/pdf", fake.LastUploads!.Values.Single().ContentType);
            Assert.Equal(BatchItemState.Completed, viewModel.Items[0].State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfficeDocumentsSubmitAsExplicitFlashGroupWhenAvailable()
    {
        // Office 走显式 flash 档整篇解析，PDF/图片保持绑定默认档。
        string root = NewTempDirectory();
        try
        {
            var files = new FakeBatchFileSource();
            var fake = new FakeBatchInferenceClient();
            var viewModel = new BatchViewModel(fake, files);
            viewModel.SetRecognitionMode(
                DocumentMode("mineru_document", "MinerU", "process_keep_alive"),
                new MineruConfig(MineruTier.Basic),
                taskModeId: null,
                mineruFlashTierAvailable: true);
            viewModel.AddFiles([CreateTempInput(root, "ledger", ".xlsx"), CreateTempInput(root, "report", ".pdf")]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, fake.SubmitCalls);
            Assert.Equal(JobKind.MineruParse, fake.LastRequest?.Kind);
            // flash 组：只含 Office，显式 flash 档且整篇解析。
            Assert.Equal(MineruTier.Flash, fake.Jobs[0].Request.Pipeline.Mineru?.Tier);
            Assert.Equal("ledger.xlsx", fake.Jobs[0].Request.Items.Single().DisplayName);
            Assert.Equal(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fake.Jobs[0].Uploads.Values.Single().ContentType);
            Assert.Equal(MineruConfig.AllPages, fake.Jobs[0].Request.Pipeline.Mineru?.PageRange);
            // 默认档组：只含 PDF，保持绑定 basic 档。
            Assert.Equal(MineruTier.Basic, fake.Jobs[1].Request.Pipeline.Mineru?.Tier);
            Assert.Equal("report.pdf", fake.Jobs[1].Request.Items.Single().DisplayName);
            Assert.Equal("application/pdf", fake.Jobs[1].Uploads.Values.Single().ContentType);
            Assert.All(viewModel.Items, item => Assert.Equal(BatchItemState.Completed, item.State));
            Assert.Equal(2, viewModel.CompletedCount);
            Assert.Equal(0, viewModel.FailedCount);
            Assert.False(viewModel.IsRunning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledFirstGroupCancelsAllPendingItemsAcrossGroups()
    {
        // 第一组取消属于整次运行取消：剩余组不再提交，全部待跑项一起取消。
        string root = NewTempDirectory();
        try
        {
            var files = new FakeBatchFileSource();
            var fake = new FakeBatchInferenceClient(terminalState: JobState.Cancelled);
            var viewModel = new BatchViewModel(fake, files);
            viewModel.SetRecognitionMode(
                DocumentMode("mineru_document", "MinerU", "process_keep_alive"),
                new MineruConfig(MineruTier.Basic),
                taskModeId: null,
                mineruFlashTierAvailable: true);
            viewModel.AddFiles([CreateTempInput(root, "ledger", ".xlsx"), CreateTempInput(root, "report", ".pdf")]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, fake.SubmitCalls); // 第二组未提交
            Assert.All(viewModel.Items, item => Assert.Equal(BatchItemState.Cancelled, item.State));
            Assert.Equal(0, viewModel.CompletedCount);
            Assert.Equal(0, viewModel.FailedCount);
            Assert.False(viewModel.IsRunning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfficeUsesWholeDocumentAndPdfInheritsDefaultPageRange()
    {
        // 全局页码只作用于 PDF，Office 整篇解析且不能设置无效的逐文件范围。
        string root = NewTempDirectory();
        try
        {
            var files = new FakeBatchFileSource();
            var fake = new FakeBatchInferenceClient();
            var viewModel = new BatchViewModel(fake, files);
            viewModel.SetRecognitionMode(
                DocumentMode("mineru_document", "MinerU", "process_keep_alive"),
                new MineruConfig(MineruTier.Basic, MineruOcrMode.Auto, "1-3"),
                taskModeId: null,
                mineruFlashTierAvailable: true);
            viewModel.AddFiles([CreateTempInput(root, "ledger", ".xlsx"), CreateTempInput(root, "report", ".pdf")]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, fake.SubmitCalls);
            Assert.Equal("all", fake.Jobs[0].Request.Pipeline.Mineru!.PageRange);
            Assert.Equal(MineruTier.Basic, fake.LastRequest?.Pipeline.Mineru?.Tier);
            Assert.Equal("1-3", fake.LastRequest?.Pipeline.Mineru?.PageRange);
            BatchItemViewModel office = viewModel.Items.Single(item => item.Name == "ledger.xlsx");
            Assert.Equal(BatchItemState.Completed, office.State);
            Assert.Throws<ArgumentException>(() => viewModel.SetPageRange(office.Id, "2"));
            Assert.Equal(BatchItemState.Completed, viewModel.Items.Single(item => item.Name == "report.pdf").State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfficeDocumentsAreRejectedPerItemWhenFlashTierUnavailable()
    {
        // flash 档不可用时按文件拒绝；同批 PDF 仍按默认档提交。
        string root = NewTempDirectory();
        try
        {
            var files = new FakeBatchFileSource();
            var fake = new FakeBatchInferenceClient();
            var viewModel = new BatchViewModel(fake, files);
            viewModel.SetRecognitionMode(
                DocumentMode("mineru_document", "MinerU", "process_keep_alive"),
                new MineruConfig(MineruTier.Basic));
            viewModel.AddFiles([CreateTempInput(root, "ledger", ".xlsx"), CreateTempInput(root, "report", ".pdf")]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, fake.SubmitCalls);
            Assert.Equal(MineruTier.Basic, fake.LastRequest?.Pipeline.Mineru?.Tier);
            Assert.Equal("application/pdf", fake.LastUploads!.Values.Single().ContentType);
            BatchItemViewModel office = viewModel.Items.Single(item => item.Name == "ledger.xlsx");
            Assert.Equal(BatchItemState.Failed, office.State);
            Assert.StartsWith("UNSUPPORTED_INPUT_KIND", office.Error);
            Assert.Contains("Flash", office.Error);
            Assert.Equal(BatchItemState.Completed, viewModel.Items.Single(item => item.Name == "report.pdf").State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MineruFailureReasonsRenderActionableChineseHints()
    {
        // 白名单 reason 映射为可行动提示；未知详情不回显，回退到错误码。
        string root = NewTempDirectory();
        try
        {
            foreach ((string reason, string hint) in new (string, string)[]
            {
                ("mineru_api_endpoint_incompatible", "连接地址"),
                ("mineru_api_authentication_failed", "鉴权"),
            })
            {
                var files = new FakeBatchFileSource();
                var fake = new FakeBatchInferenceClient(
                    perItemFailures: new HashSet<int> { 0 }, failureReason: reason);
                var viewModel = new BatchViewModel(fake, files);
                viewModel.AddFiles([CreateTempInput(root, "scan", ".png")]);

                await viewModel.StartAsync(TestContext.Current.CancellationToken);

                Assert.Equal(BatchItemState.Failed, viewModel.Items[0].State);
                Assert.Contains(hint, viewModel.Items[0].Error);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownFailureDetailIsNotEchoedToTheQueue()
    {
        // 服务端未知 reason 可能携带本地路径/凭据：只显示错误码，不回显。
        string root = NewTempDirectory();
        try
        {
            var files = new FakeBatchFileSource();
            var fake = new FakeBatchInferenceClient(
                perItemFailures: new HashSet<int> { 0 },
                failureReason: @"C:\models\secret.bin missing");
            var viewModel = new BatchViewModel(fake, files);
            viewModel.AddFiles([CreateTempInput(root, "scan", ".png")]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(BatchItemState.Failed, viewModel.Items[0].State);
            Assert.Equal("OUT_OF_MEMORY", viewModel.Items[0].Error);
            Assert.DoesNotContain("secret", viewModel.Items[0].Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BatchMediaTypesCoverDocumentAndExtendedImageExtensions()
    {
        // 与运行时共享 MIME 映射对齐：图片全集 + PDF/Office 文档。
        Assert.Equal("application/pdf", BatchCommands.MediaType(".pdf"));
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            BatchCommands.MediaType(".docx"));
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            BatchCommands.MediaType(".pptx"));
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            BatchCommands.MediaType(".xlsx"));
        Assert.Equal("image/tiff", BatchCommands.MediaType(".tif"));
        Assert.Equal("image/tiff", BatchCommands.MediaType(".tiff"));
        Assert.Equal("image/gif", BatchCommands.MediaType(".gif"));
        Assert.Equal("image/jp2", BatchCommands.MediaType(".jp2"));
        Assert.Equal("image/png", BatchCommands.MediaType(".PNG"));
        Assert.Throws<InvalidDataException>(() => BatchCommands.MediaType(".exe"));
        Assert.Contains(".pdf", BatchCommands.Extensions);
        Assert.Contains(".xlsx", BatchCommands.Extensions);
        Assert.Contains(".docx", BatchCommands.Extensions);
        Assert.Contains(".pptx", BatchCommands.Extensions);
    }

    [Theory]
    [InlineData("OCR")]
    [InlineData("TABLE_RECOGNITION")]
    [InlineData("FORMULA_RECOGNITION")]
    public async Task PdfInputUsesSelectedPipelineAndKeepsOneFileResult(string pipeline)
    {
        var client = new FakeBatchInferenceClient();
        var model = new BatchViewModel(client, new FakeBatchFileSource());
        model.SetRecognitionMode(DocumentMode("test", pipeline, "model_residency"));
        model.AddFiles([Path.Combine(Path.GetTempPath(), "document.pdf")]);
        await model.StartAsync(CancellationToken.None);
        Assert.Equal(BatchItemState.Completed, model.Items[0].State);
        Assert.Equal(new[] { 0, 1, 2 }, client.RenderedPages);
        Assert.Equal(pipeline, client.LastRequest!.Pipeline.PipelineId);
        Assert.Equal(3, client.LastRequest.Items.Count);
        Assert.Contains("page-3", model.Items[0].Result!.Text);
        Assert.Equal(1, client.ClosedSessions);
        Assert.Equal(new[] { 0, 1, 2 }, model.Items[0].Result!.ContentBlocks!.Select(block => block.GetProperty("page_idx").GetInt32()));
        Assert.Equal(3, model.Items[0].Result!.ContentBlocks!.Select(block => block.GetProperty("block_id").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task SeparateFilePageRangesReachMineruJobsWithoutChangingDefaults()
    {
        string root = NewTempDirectory();
        try
        {
            var client = new FakeBatchInferenceClient();
            var model = new BatchViewModel(client, new FakeBatchFileSource());
            model.SetRecognitionMode(DocumentMode("mineru_document", "MinerU", "remote_service"),
                new MineruConfig(MineruTier.Basic, pageRange: "all"));
            model.AddFiles([CreateTempInput(root, "first", ".pdf"), CreateTempInput(root, "second", ".pdf")]);
            model.SetPageRange(model.Items[0].Id, "1,3");
            model.SetPageRange(model.Items[1].Id, "2-r1");
            await model.StartAsync(CancellationToken.None);
            Assert.Equal(new[] { "1,3", "2-r1" }, client.Jobs.Select(job => job.Request.Pipeline.Mineru!.PageRange));
            Assert.Equal(2, model.CompletedCount);
            Assert.Empty(client.RenderedPages);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ImagePageOneIsNotSentAsUnsupportedRemotePdfRange()
    {
        string path = CreateTempPng("single");
        try
        {
            var client = new FakeBatchInferenceClient();
            var model = new BatchViewModel(client, new FakeBatchFileSource());
            model.SetRecognitionMode(DocumentMode("mineru_document", "MinerU", "remote_service"),
                new MineruConfig(MineruTier.Basic));
            model.AddFiles([path]);
            model.SetPageRange(model.Items[0].Id, "1");
            await model.StartAsync(CancellationToken.None);
            Assert.Equal("all", client.LastRequest!.Pipeline.Mineru!.PageRange);
            Assert.Equal(BatchItemState.Completed, model.Items[0].State);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PdfRangesArePerFileAndInvalidFileDoesNotBlockOthers()
    {
        var client = new FakeBatchInferenceClient();
        var model = new BatchViewModel(client, new FakeBatchFileSource());
        model.AddFiles([Path.Combine(Path.GetTempPath(), "first.pdf"), Path.Combine(Path.GetTempPath(), "second.pdf")]);
        model.SetPageRange(model.Items[0].Id, "2-r1");
        model.SetPageRange(model.Items[1].Id, "4");
        await model.StartAsync(CancellationToken.None);
        Assert.Equal(new[] { 1, 2 }, client.RenderedPages);
        Assert.Equal(BatchItemState.Completed, model.Items[0].State);
        Assert.Equal(BatchItemState.Failed, model.Items[1].State);
        Assert.Contains("共 3 页", model.Items[1].Error);
        Assert.Equal(2, client.ClosedSessions);
        Assert.Equal(1, model.CompletedCount);
        Assert.Equal(1, model.FailedCount);
    }

    [Fact]
    public async Task CancelledPdfRenderingClosesSessionWithoutSubmittingPages()
    {
        var client = new FakeBatchInferenceClient { CancelRendering = true };
        var model = new BatchViewModel(client, new FakeBatchFileSource());
        model.AddFiles([Path.Combine(Path.GetTempPath(), "cancel.pdf")]);
        await model.StartAsync(CancellationToken.None);
        Assert.Equal(BatchItemState.Cancelled, model.Items[0].State);
        Assert.Equal(1, client.ClosedSessions);
        Assert.Empty(client.Jobs);
        Assert.False(model.IsRunning);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1,,3")]
    [InlineData("-1")]
    public void InvalidPageRangeDoesNotReplaceFilePreference(string range)
    {
        var model = new BatchViewModel(new FakeBatchInferenceClient(), new FakeBatchFileSource());
        model.AddFiles([Path.Combine(Path.GetTempPath(), "first.pdf")]);
        Assert.Throws<ArgumentException>(() => model.SetPageRange(model.Items[0].Id, range));
        Assert.Null(model.Items[0].PageRange);
    }

    [Fact]
    public async Task OversizedAndLockedInputsFailPerFileWithoutStallingTheBatch()
    {
        // 回归：超限（InvalidDataException，BatchFileSource 256 MiB 门禁）与
        // 无权限（UnauthorizedAccessException，File.ReadAllBytesAsync）是单文件
        // 确定性读失败：按文件 Failed、其余文件照常提交完成，item 不停留
        // Running（Running 状态会被 Remove 拒绝，队列卡死无法清理）。
        string root = NewTempDirectory();
        try
        {
            string oversized = CreateTempInput(root, "oversized", ".png");
            string locked = CreateTempInput(root, "locked", ".png");
            string good = CreateTempInput(root, "good", ".png");
            var files = new FakeBatchFileSource(new Dictionary<string, Exception>
            {
                [oversized] = new InvalidDataException("Batch input exceeds 256 MiB."),
                [locked] = new UnauthorizedAccessException("Access denied."),
            });
            var fake = new FakeBatchInferenceClient();
            var viewModel = new BatchViewModel(fake, files);
            viewModel.AddFiles([oversized, locked, good]);

            await viewModel.StartAsync(TestContext.Current.CancellationToken);

            // 坏文件在提交前按文件失败：只有好文件进入唯一一次 job。
            Assert.Equal(1, fake.SubmitCalls);
            Assert.Single(fake.LastUploads!);
            BatchItemViewModel oversizedItem = viewModel.Items.Single(item => item.Name == "oversized.png");
            BatchItemViewModel lockedItem = viewModel.Items.Single(item => item.Name == "locked.png");
            Assert.Equal(BatchItemState.Failed, oversizedItem.State);
            Assert.Equal(BatchItemState.Failed, lockedItem.State);
            Assert.Contains("256 MiB", oversizedItem.Error);
            Assert.NotNull(lockedItem.Error);
            Assert.Equal(BatchItemState.Completed, viewModel.Items.Single(item => item.Name == "good.png").State);
            Assert.Equal(1, viewModel.CompletedCount);
            Assert.Equal(2, viewModel.FailedCount);
            Assert.False(viewModel.IsRunning);
            // 失败项可从队列移除（非 Running）。
            viewModel.Remove(oversizedItem.Id);
            viewModel.Remove(lockedItem.Id);
            Assert.Single(viewModel.Items);
            Assert.Equal("good.png", viewModel.Items[0].Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Fakes + helpers
    // ------------------------------------------------------------------

    private static string CreateTempPng(string stem)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vibeocr-batch-sup-{stem}-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [(byte)stem[0], 1, 2]);
        return path;
    }

    private static string CreateTempInput(string directory, string stem, string extension)
    {
        string path = Path.Combine(directory, stem + extension);
        File.WriteAllBytes(path, [(byte)stem[0], 1, 2]);
        return path;
    }

    private static string NewTempDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-batch-sup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static RecognitionModeOption DocumentMode(
        string id,
        string pipeline,
        string lifecycleKind) => new(
            id,
            "document",
            pipeline,
            null,
            "advanced_component",
            "ready",
            null,
            "document-component",
            [],
            lifecycleKind,
            lifecycleKind == "model_residency",
            true,
            lifecycleKind == "model_residency",
            true);

    private sealed class FakeBatchFileSource : IBatchFileSource
    {
        private readonly IReadOnlyDictionary<string, Exception> _readFailures;

        public FakeBatchFileSource(IReadOnlyDictionary<string, Exception>? readFailures = null)
        {
            _readFailures = readFailures ?? new Dictionary<string, Exception>();
        }

        public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<(byte[] Data, string MediaType)> ReadAsync(string path, CancellationToken cancellationToken)
        {
            if (_readFailures.TryGetValue(path, out Exception? failure)) throw failure;
            return Task.FromResult((File.ReadAllBytes(path), BatchCommands.MediaType(Path.GetExtension(path))));
        }
    }

    /// <summary>
    /// Fake v2 supervisor for batch. ObserveAsync returns all terminal outcomes
    /// in reverse order so tests detect positional result mapping. 每次提交
    /// （Request, Uploads）按顺序留存，供原生格式分组断言。
    /// </summary>
    private sealed class FakeBatchInferenceClient : InferenceClientStub
    {
        public sealed record SubmittedJob(SubmitRequest Request, IReadOnlyDictionary<string, SubmitUpload> Uploads);

        private readonly JobState _terminalState;
        private readonly IReadOnlySet<int> _failures;
        private IReadOnlyList<JobItem> _items = Array.Empty<JobItem>();

        public FakeBatchInferenceClient(
            JobState terminalState = JobState.Completed,
            IReadOnlySet<int>? perItemFailures = null,
            string? failureReason = null)
        {
            _terminalState = terminalState;
            _failures = perItemFailures ?? new HashSet<int>();
            _failureReason = failureReason;
        }

        private readonly string? _failureReason;

        public bool CancelRendering { get; init; }
        public List<int> RenderedPages { get; } = [];
        public int ClosedSessions { get; private set; }
        public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct)
            => Task.FromResult(new PdfSessionOpenResult("pdf-session", 3, path));
        public override Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct)
        {
            if (CancelRendering) throw new OperationCanceledException();
            RenderedPages.Add(page);
            return Task.FromResult(new byte[] { (byte)page, 1 });
        }
        public override Task ClosePdfSessionAsync(string sessionId, CancellationToken ct)
        {
            ClosedSessions++;
            return Task.CompletedTask;
        }

        public int SubmitCalls { get; private set; }
        public SubmitRequest? LastRequest { get; private set; }
        public IReadOnlyDictionary<string, SubmitUpload>? LastUploads { get; private set; }
        public IReadOnlyList<SubmittedJob> Jobs { get; } = new List<SubmittedJob>();
        public JobCommand? LastCommand { get; private set; }

        public override Task<JobRef> SubmitAsync(
            SubmitRequest request,
            IReadOnlyDictionary<string, SubmitUpload> uploads,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            LastRequest = request;
            LastUploads = uploads;
            ((List<SubmittedJob>)Jobs).Add(new SubmittedJob(request, uploads));
            _items = request.Items.Select((item, index) => new JobItem
            {
                ItemId = $"it-{index}",
                ClientItemKey = item.ClientItemKey,
                Ordinal = item.Ordinal,
                DisplayName = item.DisplayName,
                State = ItemState.Queued,
            }).ToArray();
            return Task.FromResult(new JobRef
            {
                JobId = $"batch-{SubmitCalls}",
                Items = _items,
            });
        }

        public override Task<JobUpdate> ObserveAsync(
            string jobId,
            int afterSequence,
            CancellationToken cancellationToken)
        {
            JobState state = _terminalState is JobState.Completed && _failures.Count > 0
                ? JobState.CompletedWithErrors
                : _terminalState;
            ItemOutcome[] outcomes = _items
                .Reverse()
                .Select(item =>
                {
                    bool failed = _failures.Contains(item.Ordinal);
                    ItemState itemState = state is JobState.Cancelled
                        ? ItemState.Cancelled
                        : failed ? ItemState.Failed : ItemState.Succeeded;
                    string stem = Path.GetFileNameWithoutExtension(item.DisplayName);
                    return new ItemOutcome
                    {
                        ItemId = item.ItemId,
                        State = itemState,
                        Attempt = 1,
                        PayloadType = itemState is ItemState.Succeeded ? "ocr.v1" : null,
                        Payload = itemState is ItemState.Succeeded
                            ? new Dictionary<string, JsonElement>
                            {
                                ["raw_text"] = JsonSerializer.SerializeToElement($"ocr-{stem}"),
                                ["content_list"] = JsonSerializer.SerializeToElement(new[] { new { block_id = "block-0", type = "text", text = $"ocr-{stem}", page_idx = 0 } }),
                            }
                            : null,
                        ErrorCode = itemState is ItemState.Failed ? "OUT_OF_MEMORY" : null,
                        ErrorDetail = itemState is ItemState.Failed && _failureReason is not null
                            ? new Dictionary<string, JsonElement>
                            {
                                ["message"] = JsonSerializer.SerializeToElement(_failureReason),
                            }
                            : new Dictionary<string, JsonElement>(),
                    };
                })
                .ToArray();
            return Task.FromResult(new JobUpdate
            {
                Snapshot = new JobSnapshot
                {
                    JobId = jobId,
                    Kind = JobKind.Recognition,
                    Priority = JobPriority.Background,
                    State = state,
                    Items = _items,
                    EventSequence = afterSequence + 1,
                },
                Events = Array.Empty<StageEvent>(),
                Outcomes = outcomes,
                ThroughSequence = afterSequence + 1,
            });
        }

        public override Task<JobCommandResult> CommandAsync(
            JobCommand command,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(new JobCommandResult(
                command.CommandId,
                command.Kind,
                CancelMode.Cooperative,
                null));
        }

        public override Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ResidencyStatus());

        public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SettingsSnapshot());
    }

}
