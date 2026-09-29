using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    /// </summary>
    public void SetRecognitionMode(
        RecognitionModeOption? mode,
        MineruConfig? mineruConfig = null,
        PaddleModeOptions? options = null,
        string? taskModeId = null)
    {
        _options = mode is null ? null : options;
        _recognitionMode = mode;
        _mineruConfig = mode is null ? null : mineruConfig;
        _taskModeId = taskModeId;
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var known = Items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths.Select(Path.GetFullPath)) if (known.Add(path)) Items.Add(new BatchItemViewModel(path));
        NotifyQueue();
    }

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
        MineruConfig? mineru = _mineruConfig;
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
        BatchItemViewModel[] pending = Items.Where(item => item.State is BatchItemState.Pending or BatchItemState.Failed or BatchItemState.Cancelled).ToArray();
        if (pending.Length == 0) return;
        long generation = Interlocked.Increment(ref _generation);
        _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CompletedCount = Items.Count(item => item.State == BatchItemState.Completed);
        FailedCount = 0;
        if (generation == Volatile.Read(ref _generation)) IsRunning = true;

        foreach (BatchItemViewModel item in pending) { if (generation == Volatile.Read(ref _generation)) { item.Reset(); item.State = BatchItemState.Running; } }

        try
        {
            var inputs = new InferenceUploadInput[pending.Length];
            for (int i = 0; i < pending.Length; i++)
            {
                (byte[] data, string mediaType) = await files.ReadAsync(pending[i].Path, _run.Token);
                inputs[i] = new InferenceUploadInput(
                    pending[i].Id.ToString("N"),
                    pending[i].Name,
                    mediaType,
                    data);
            }

            InferenceJobRun job = await _jobs.RunRecognitionAsync(
                pipeline,
                JobPriority.Background,
                inputs,
                options: options,
                cancellationToken: _run.Token,
                engine: engine,
                mineru: pipeline == "MinerU" ? mineru : null);
            JobSnapshot snapshot = job.Snapshot;

            if (generation != Volatile.Read(ref _generation)) return;

            if (snapshot.State is JobState.Cancelled)
            {
                foreach (BatchItemViewModel item in pending) if (item.State is BatchItemState.Running) item.State = BatchItemState.Cancelled;
                NotifyProgress();
                return;
            }

            foreach (BatchItemViewModel item in pending)
            {
                if (generation != Volatile.Read(ref _generation)) return;
                ItemOutcome outcome = job.OutcomesByClientItemKey[item.Id.ToString("N")];
                if (outcome.State is ItemState.Succeeded)
                {
                    item.Result = RecognitionOutcomeMapper.ToResponse(outcome, pipeline);
                    item.State = BatchItemState.Completed;
                    IncrementCompleted();
                }
                else if (outcome.State is ItemState.Cancelled)
                {
                    item.State = BatchItemState.Cancelled;
                }
                else
                {
                    item.Error = outcome.ErrorCode ?? "INFERENCE_FAILED";
                    item.State = BatchItemState.Failed;
                    IncrementFailed();
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
