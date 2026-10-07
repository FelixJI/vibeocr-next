using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Inference;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Features.Batch;

public sealed class BatchViewModel(
    IInferenceClient inference,
    IBatchFileSource files) : INotifyPropertyChanged
{
    private readonly InferenceJobRunner _jobs = new(inference);
    private readonly object _counterLock = new();
    private CancellationTokenSource? _run;
    private long _generation;
    private bool _isRunning;
    private int _completedCount;
    private int _failedCount;
    private RecognitionModeOption? _recognitionMode;
    private MineruConfig? _mineruConfig;
    private string? _taskModeId;
    private PaddleModeOptions? _options;
    private bool _mineruFlashTierAvailable;

    /// <summary>队列中是否存在原生 Office 文档（决定是否需要 flash 组）。</summary>
    public bool HasNativeOfficeInputs => Items.Any(item => BatchCommands.IsNativeOfficeDocument(item.Path));

    /// <summary>当前绑定模式是否为 MinerU 文档解析。</summary>
    public bool IsMineruDocumentMode => _recognitionMode?.PipelineId == "MinerU";

    /// <summary>运行环境目录声明的 flash 档可用性（随模式绑定冻结）。</summary>
    public bool MineruFlashTierAvailable => _mineruFlashTierAvailable;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<BatchItemViewModel> Items { get; } = [];
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public int CompletedCount { get => _completedCount; private set => Set(ref _completedCount, value); }
    public int FailedCount { get => _failedCount; private set => Set(ref _failedCount, value); }
    public int TotalCount => Items.Count;
    public string Progress => $"{CompletedCount + FailedCount}/{TotalCount}";

    /// <summary>
    /// 绑定批量识别模式及其类型化 MinerU 4 配置；mineru_document 批量任务
    /// 必须携带目录默认 tier 的 typed 配置，不发送遗留 engine 选项。
    /// taskModeId 是用户显式选择的模式 id：非空而 mode 为 null（目录缺失/
    /// 环境切换）时 StartAsync 必须拒绝，不静默回退通用文字 OCR。
    /// 选项原样冻结，提交时按绑定模式严格 ToWire：不支持或越界字段明确
    /// 拒绝整批提交，不静默丢弃（#110 AC2）。
    /// mineruFlashTierAvailable 来自 mineru_config_catalog：原生 Office
    /// 文档仅 flash 档可整篇解析，可用时成组提交，不可用时按文件拒绝。
    /// </summary>
    public void SetRecognitionMode(
        RecognitionModeOption? mode,
        MineruConfig? mineruConfig = null,
        PaddleModeOptions? options = null,
        string? taskModeId = null,
        bool mineruFlashTierAvailable = false)
    {
        _options = mode is null ? null : options;
        _recognitionMode = mode;
        _mineruConfig = mode is null ? null : mineruConfig;
        _taskModeId = taskModeId;
        _mineruFlashTierAvailable = mode is null ? false : mineruFlashTierAvailable;
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var known = Items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths.Select(Path.GetFullPath)) if (known.Add(path)) Items.Add(new BatchItemViewModel(path));
        NotifyQueue();
    }

    public void SetPageRange(Guid id, string? range)
    {
        if (IsRunning) throw new InvalidOperationException("识别期间不能修改页码范围。");
        BatchItemViewModel item = Items.Single(entry => entry.Id == id);
        string? selected = string.IsNullOrWhiteSpace(range) ? null : PageRangeSelection.Validate(range);
        if (BatchCommands.IsNativeOfficeDocument(item.Path) && selected is not (null or "all"))
            throw new ArgumentException("Office 文档由 MinerU 整篇解析；如需按页识别，请先转换为 PDF。");
        if (item.PageRange == selected) return;
        item.PageRange = selected;
        item.Reset();
        CompletedCount = Items.Count(entry => entry.State == BatchItemState.Completed);
        FailedCount = Items.Count(entry => entry.State == BatchItemState.Failed);
        NotifyQueue();
    }

    private string EffectivePageRange(BatchItemViewModel item) => item.PageRange ??
        (_recognitionMode?.PipelineId == "MinerU" &&
         Path.GetExtension(item.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? _mineruConfig?.PageRange : null) ?? "all";

    public async Task PickFilesAsync(CancellationToken cancellationToken) => AddFiles(await files.PickFilesAsync(cancellationToken));
    public void Move(Guid id, int delta) { int from = Items.ToList().FindIndex(item => item.Id == id); int to = Math.Clamp(from + delta, 0, Items.Count - 1); if (from >= 0 && from != to) Items.Move(from, to); }
    public void Remove(Guid id) { BatchItemViewModel? item = Items.FirstOrDefault(entry => entry.Id == id); if (item is not null && item.State != BatchItemState.Running) Items.Remove(item); NotifyQueue(); }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) throw new InvalidOperationException("A batch is already running.");
        if (_taskModeId is not null && _recognitionMode is null)
        {
            throw new RecognitionModeUnavailableException(
                $"批量任务识别模式 {_taskModeId} 在当前环境不可用，已拒绝按通用文字识别执行；请在设置中检查运行环境后重试。");
        }
        string pipeline = _recognitionMode?.PipelineId ?? "OCR";
        OcrEngine? engine = _recognitionMode?.Engine;
        // 提交冻结点（读取文件 await 前）：按绑定的模式对原始 typed 选项
        // 严格 ToWire；不支持或越界字段明确拒绝整批提交并按既有条目失败
        // seam 呈现精确原因，不静默丢弃后仍提交（#110 AC2）。
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? options;
        try
        {
            options = _options?.ToWire(_recognitionMode!);
        }
        catch (ArgumentException error)
        {
            FailedCount = 0;
            foreach (BatchItemViewModel item in Items.Where(
                item => item.State is BatchItemState.Pending or BatchItemState.Failed or BatchItemState.Cancelled))
            {
                item.Error = $"INVALID_MODE_OPTIONS: {error.Message}";
                item.State = BatchItemState.Failed;
                IncrementFailed();
            }
            return;
        }
        BatchItemViewModel[] candidates = Items.Where(item => item.State is BatchItemState.Pending or BatchItemState.Failed or BatchItemState.Cancelled).ToArray();
        CompletedCount = Items.Count(item => item.State == BatchItemState.Completed);
        FailedCount = 0;
        // 提交冻结点按文件门禁：文档不进图片解码引擎，原生 Office 仅在
        // flash 档可用且无页范围限制时可提交，未知扩展名按文件失败；拖放
        // 路径没有挑选器过滤，单个坏输入不能炸掉整批。
        List<BatchItemViewModel> runnable = [];
        foreach (BatchItemViewModel item in candidates)
        {
            string? rejection = BatchCommands.DescribeInputRejection(
                item.Path,
                pipeline,
                pipeline == "MinerU" ? EffectiveMineruTier(item.Path) : null,
                pipeline == "MinerU" ? EffectivePageRange(item) : null);
            if (rejection is null)
            {
                runnable.Add(item);
            }
            else
            {
                item.Reset();
                item.Error = rejection;
                item.State = BatchItemState.Failed;
                IncrementFailed();
            }
        }
        BatchItemViewModel[] pending = [.. runnable];
        if (pending.Length == 0) { NotifyProgress(); return; }
        long generation = Interlocked.Increment(ref _generation);
        _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (generation == Volatile.Read(ref _generation)) IsRunning = true;

        foreach (BatchItemViewModel item in pending) { if (generation == Volatile.Read(ref _generation)) { item.Reset(); item.State = BatchItemState.Running; } }

        // 原生格式分组：原生 Office 仅 flash 档可整篇解析，PDF/图片全档可用。
        // 页码范围不同的文件分别提交，PDF/图片保持用户选定的档位。
        List<(MineruConfig? Config, BatchItemViewModel[] Items)> groups = [];
        if (pipeline == "MinerU")
        {
            foreach (var group in pending.GroupBy(item => (
                Office: BatchCommands.IsNativeOfficeDocument(item.Path),
                Range: Path.GetExtension(item.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? EffectivePageRange(item) : "all")))
            {
                MineruConfig? config = group.Key.Office ? WithFlashTier(_mineruConfig) : _mineruConfig;
                if (config is not null) config = config with { PageRange = group.Key.Range };
                groups.Add((config, [.. group]));
            }
        }
        else
        {
            groups.Add((null, pending));
        }

        try
        {
            foreach ((MineruConfig? groupConfig, BatchItemViewModel[] groupItems) in groups)
            {
                if (generation != Volatile.Read(ref _generation)) return;
                var inputs = new List<InferenceUploadInput>();
                var inputPages = new Dictionary<Guid, (string Key, int Page)[]>();
                foreach (BatchItemViewModel item in groupItems)
                {
                    try
                    {
                        (InferenceUploadInput Input, int Page)[] prepared = await PrepareInputsAsync(
                            item, pipeline, EffectivePageRange(item), _run.Token);
                        inputs.AddRange(prepared.Select(entry => entry.Input));
                        inputPages[item.Id] = prepared.Select(entry => (entry.Input.ClientItemKey, entry.Page)).ToArray();
                    }
                    // 单文件确定性读失败（超限 InvalidDataException、
                    // 无权限 UnauthorizedAccessException）只标记该文件，
                    // 不中断整批，也不让 item 停畑Running 而无法移除。
                    catch (Exception error) when (error is IOException or
                        ArgumentException or InferenceClientException or InvalidDataException or
                        UnauthorizedAccessException)
                    {
                        item.Error = error is InferenceClientException clientError ? clientError.Code.ToString() : error.Message;
                        item.State = BatchItemState.Failed;
                        IncrementFailed();
                    }
                }
                if (inputs.Count == 0) continue;

                InferenceJobRun job = await _jobs.RunRecognitionAsync(
                    pipeline,
                    JobPriority.Background,
                    inputs,
                    options: options,
                    cancellationToken: _run.Token,
                    engine: engine,
                    mineru: pipeline == "MinerU" ? groupConfig : null);
                JobSnapshot snapshot = job.Snapshot;

                if (generation != Volatile.Read(ref _generation)) return;

                if (snapshot.State is JobState.Cancelled)
                {
                    // 取消属于整次运行：后续组不再提交，全部待跑项一起取消，
                    // 不能只取消当前组而把剩余项留在“处理中”。
                    foreach (BatchItemViewModel item in pending) if (item.State is BatchItemState.Running) item.State = BatchItemState.Cancelled;
                    NotifyProgress();
                    return;
                }

                foreach (BatchItemViewModel item in groupItems)
                {
                    if (generation != Volatile.Read(ref _generation)) return;
                    if (!inputPages.TryGetValue(item.Id, out var pages)) continue;
                    ItemOutcome[] outcomes = pages.Select(page => job.OutcomesByClientItemKey[page.Key]).ToArray();
                    ItemOutcome outcome = outcomes.FirstOrDefault(entry => entry.State != ItemState.Succeeded) ?? outcomes[0];
                    if (outcome.State is ItemState.Succeeded)
                    {
                        item.Result = MergePageResults(outcomes, pages.Select(page => page.Page).ToArray(), pipeline);
                        item.State = BatchItemState.Completed;
                        IncrementCompleted();
                    }
                    else if (outcome.State is ItemState.Cancelled)
                    {
                        item.State = BatchItemState.Cancelled;
                    }
                    else
                    {
                        item.Error = BatchCommands.DescribeFailure(
                            outcome.ErrorCode, OutcomeReason(outcome));
                        item.State = BatchItemState.Failed;
                        IncrementFailed();
                    }
                }
            }
            NotifyProgress();
        }
        catch (OperationCanceledException)
        {
            if (generation == Volatile.Read(ref _generation)) foreach (BatchItemViewModel item in pending) if (item.State is BatchItemState.Running) item.State = BatchItemState.Cancelled;
        }
        catch (InferenceClientException error)
        {
            if (generation == Volatile.Read(ref _generation)) foreach (BatchItemViewModel item in pending) if (item.State is BatchItemState.Running) { item.Error = error.Code.ToString(); item.State = BatchItemState.Failed; IncrementFailed(); }
        }
        catch (Exception error) when (error is IOException)
        {
            if (generation == Volatile.Read(ref _generation)) foreach (BatchItemViewModel item in pending) if (item.State is BatchItemState.Running) { item.Error = error.GetType().Name; item.State = BatchItemState.Failed; IncrementFailed(); }
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation)) { IsRunning = false; if (ReferenceEquals(Interlocked.CompareExchange(ref _run, null, _run), _run)) _run?.Dispose(); }
        }
    }

    private async Task<(InferenceUploadInput Input, int Page)[]> PrepareInputsAsync(
        BatchItemViewModel item, string pipeline, string range, CancellationToken token)
    {
        string key = item.Id.ToString("N");
        if (pipeline != "MinerU" && Path.GetExtension(item.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            PdfSessionOpenResult session = await inference.OpenPdfSessionAsync(item.Path, null, token);
            try
            {
                int[] pages = PageRangeSelection.Resolve(range, session.PageCount);
                var inputs = new List<(InferenceUploadInput, int)>();
                foreach (int page in pages)
                {
                    byte[] image = await inference.RenderPdfPageAsync(session.SessionId, page, 2048, token);
                    inputs.Add((new InferenceUploadInput($"{key}-page-{page}",
                        $"{Path.GetFileNameWithoutExtension(item.Name)}-page-{page + 1}.png", "image/png", image), page));
                }
                return [.. inputs];
            }
            finally { await inference.ClosePdfSessionAsync(session.SessionId, CancellationToken.None); }
        }
        if (!BatchCommands.IsDocument(item.Path)) _ = PageRangeSelection.Resolve(range, 1);
        (byte[] data, string mediaType) = await files.ReadAsync(item.Path, token);
        return [(new InferenceUploadInput(key, item.Name, mediaType, data), -1)];
    }

    private static RecognizeResponse MergePageResults(ItemOutcome[] outcomes, int[] pages, string pipeline)
    {
        RecognizeResponse[] results = outcomes.Select(outcome => RecognitionOutcomeMapper.ToResponse(outcome, pipeline)).ToArray();
        if (pages is [-1]) return results[0];
        JsonElement[] Blocks(Func<RecognizeResponse, JsonElement[]?> select) => results
            .SelectMany((result, index) => (select(result) ?? []).Select(block =>
            {
                JsonObject value = JsonNode.Parse(block.GetRawText())!.AsObject();
                value["page_idx"] = pages[index];
                if (value["block_id"] is JsonValue id)
                    value["block_id"] = $"page-{pages[index]}-{id.GetValue<string>()}";
                return JsonSerializer.SerializeToElement(value);
            })).ToArray();
        string raw = string.Join("\n\n", results.Select(result => result.RawText ?? result.Text));
        return new RecognizeResponse
        {
            Text = raw, RawText = raw, Pipeline = pipeline,
            MarkdownText = string.Join("\n\n", results.Select(result => result.MarkdownText ?? result.Text)),
            HtmlText = string.Join("\n", results.Select(result => result.HtmlText ?? System.Net.WebUtility.HtmlEncode(result.Text))),
            RawBlocks = Blocks(result => result.RawBlocks),
            ContentBlocks = Blocks(result => result.ContentBlocks),
        };
    }

    /// <summary>outcome 失败详情里的 message 字段（服务端 reason）。</summary>
    private static string? OutcomeReason(ItemOutcome outcome) =>
        outcome.ErrorDetail.TryGetValue("message", out System.Text.Json.JsonElement value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>该文件按原生格式分组后实际将执行的 MinerU 档位。</summary>
    private MineruTier? EffectiveMineruTier(string path) =>
        BatchCommands.IsNativeOfficeDocument(path) && _mineruFlashTierAvailable
            ? MineruTier.Flash
            : _mineruConfig?.Tier;

    /// <summary>flash 组配置：原生 Office 整篇解析，不带页范围。</summary>
    private static MineruConfig WithFlashTier(MineruConfig? config) =>
        config is null
            ? new MineruConfig(MineruTier.Flash)
            : new MineruConfig(MineruTier.Flash, config.OcrMode, MineruConfig.AllPages, config.Language);

    public void CancelAll() { Interlocked.Increment(ref _generation); _run?.Cancel(); foreach (BatchItemViewModel item in Items.Where(item => item.State is BatchItemState.Running or BatchItemState.Pending)) item.State = BatchItemState.Cancelled; IsRunning = false; }
    public void ResetTemporaryQueue() { CancelAll(); Items.Clear(); CompletedCount = 0; FailedCount = 0; NotifyQueue(); }

    public Task<byte[]> FetchResultAssetAsync(
        string jobId, string itemId, string assetId, CancellationToken cancellationToken) =>
        inference.FetchResultAssetAsync(jobId, itemId, assetId, cancellationToken);

    public async Task<ExportResult> ExportAsync(Guid id, string outputPath, string format, bool overwrite, CancellationToken ct)
    {
        BatchItemViewModel item = Items.Single(entry => entry.Id == id);
        if (item.Result is null) throw new InvalidOperationException("The batch item has no result.");
        return await inference.ExportAsync(new ExportRequest(item.Result.RawText ?? item.Result.Text, item.Result.MarkdownText ?? item.Result.Text, item.Result.HtmlText ?? item.Result.Text, outputPath, format, overwrite, item.Result.ContentBlocks), ct);
    }

    public async Task<IReadOnlyList<ExportResult>> ExportAllAsync(string directory, string format, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exports = new List<ExportResult>();
        foreach (BatchItemViewModel item in Items.Where(entry => entry.Result is not null))
        {
            string path = BatchCommands.UniqueOutputPath(directory, item.Path, format, reserved);
            exports.Add(await ExportAsync(item.Id, path, format, false, ct));
        }
        return exports;
    }

    private void NotifyQueue() { PropertyChanged?.Invoke(this, new(nameof(TotalCount))); NotifyProgress(); }
    private void NotifyProgress() => PropertyChanged?.Invoke(this, new(nameof(Progress)));
    private void IncrementCompleted() { lock (_counterLock) _completedCount++; PropertyChanged?.Invoke(this, new(nameof(CompletedCount))); NotifyProgress(); }
    private void IncrementFailed() { lock (_counterLock) _failedCount++; PropertyChanged?.Invoke(this, new(nameof(FailedCount))); NotifyProgress(); }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(name)); if (name is nameof(CompletedCount) or nameof(FailedCount)) NotifyProgress(); }
}
