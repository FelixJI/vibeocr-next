using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeOCR.App.Inference;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Features.Recognition;

public sealed class RecognitionViewModel : INotifyPropertyChanged
{
    private readonly IInferenceClient _inference;
    private readonly InferenceJobRunner _jobs;
    private readonly IInputService _inputs;
    private CancellationTokenSource? _activeRun;
    private long _generation;
    private bool _isBusy;
    private string _resultText = string.Empty;
    private RecognizeResponse? _result;
    private RecognitionInput? _currentInput;
    private string _status = "请选择图片";
    private string? _taskEngine;
    private RecognitionModeOption? _taskRecognitionMode;
    private MineruConfig? _taskMineruConfig;
    private PaddleModeOptions? _taskOptions;

    public RecognitionViewModel(
        IInferenceClient inference,
        IInputService inputs)
    {
        _inference = inference ?? throw new ArgumentNullException(nameof(inference));
        _jobs = new InferenceJobRunner(inference);
        _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string ResultText { get => _resultText; private set => SetField(ref _resultText, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public RecognitionInput? CurrentInput { get => _currentInput; private set => SetField(ref _currentInput, value); }
    public bool HasResult => _result is not null;
    public JobState? TerminalState { get; private set; }
    public string Pipeline { get; set; } = "OCR";
    public string? Language { get; set; }
    public RecognizeResponse? Result => _result;

    public Task<byte[]> FetchResultAssetAsync(
        string jobId, string itemId, string assetId, CancellationToken cancellationToken) =>
        _inference.FetchResultAssetAsync(jobId, itemId, assetId, cancellationToken);

    /// <summary>
    /// Task-level recognition-mode id, or a legacy wire engine id when the
    /// runtime does not expose Protocol 2.8 recognition modes.
    /// </summary>
    public string? TaskEngine
    {
        get => _taskEngine;
        set => SetField(ref _taskEngine, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    /// <summary>
    /// 绑定任务级识别模式、其类型化 MinerU 4 配置与原始 typed 模式选项
    /// （仅目录声明 ocr.mineru-config.v1 时由宿主提供；null 完全省略
    /// mineru 块）。选项在提交冻结点按绑定模式严格 ToWire：不支持或
    /// 越界字段明确拒绝提交，不静默丢弃（#110 AC2）。
    /// 提交时若 TaskEngine 已显式选择模式而 mode 未能绑定（目录缺失/环境
    /// 切换），EffectiveEngine 会拒绝提交，不静默回退通用文字识别。
    /// </summary>
    public void SetRecognitionMode(
        RecognitionModeOption? taskMode,
        MineruConfig? mineruConfig = null,
        PaddleModeOptions? options = null)
    {
        _taskOptions = taskMode is null ? null : options;
        _taskRecognitionMode = taskMode;
        _taskMineruConfig = taskMode is null ? null : mineruConfig;
    }

    /// <summary>The engine explicitly selected for this task, if any.</summary>
    public OcrEngine? EffectiveEngine
    {
        get
        {
            if (_taskRecognitionMode is not null) return _taskRecognitionMode.Engine;
            OcrEngine? task = OcrEngineWire.Parse(TaskEngine);
            if (task is not null)
            {
                return task;
            }
            if (TaskEngine is not null)
            {
                throw new RecognitionModeUnavailableException(
                    $"任务识别模式 {TaskEngine} 尚未绑定 Runtime catalog，已拒绝回退通用文字识别；请在设置中检查运行环境后重试。");
            }
            return null;
        }
    }

    public string EffectivePipeline => _taskRecognitionMode?.PipelineId ?? Pipeline;

    public ResultActions CreateResultActions(IResultActionPlatform platform)
    {
        var actions = new ResultActions(_inference, platform);
        if (_result is not null) actions.SetResult(_result);
        return actions;
    }

    public Task RecognizeFileAsync(CancellationToken cancellationToken) =>
        RecognizeViaSupervisorAsync(_inputs.PickFileAsync, cancellationToken);

    public Task RecognizeClipboardAsync(CancellationToken cancellationToken) =>
        RecognizeViaSupervisorAsync(_inputs.ReadClipboardAsync, cancellationToken);

    public Task RecognizeScreenshotAsync(CancellationToken cancellationToken) =>
        RecognizeViaSupervisorAsync(_inputs.CaptureScreenAsync, cancellationToken);

    public Task RecognizeDroppedFileAsync(string path, CancellationToken cancellationToken) =>
        RecognizeViaSupervisorAsync(ct => _inputs.ReadDroppedFileAsync(path, ct), cancellationToken);

    /// <summary>
    /// 纯截图会话：只采集输入并建立本地编辑基准，不提交任何 OCR 任务。
    /// Supervisor 未连接时同样可用。
    /// </summary>
    public Task OpenImageForEditAsync(CancellationToken cancellationToken) =>
        RunInputAsync(_inputs.PickFileAsync, cancellationToken,
            recognize: false, persistCurrentInput: true);

    public Task PasteImageForEditAsync(CancellationToken cancellationToken) =>
        RunInputAsync(_inputs.ReadClipboardAsync, cancellationToken,
            recognize: false, persistCurrentInput: true);

    public Task DropImageForEditAsync(string path, CancellationToken cancellationToken) =>
        RunInputAsync(ct => _inputs.ReadDroppedFileAsync(path, ct), cancellationToken,
            recognize: false, persistCurrentInput: true);

    public void ReleaseInput() => CurrentInput = null;

    public Task CaptureScreenshotSessionAsync(CancellationToken cancellationToken) =>
        RunInputAsync(_inputs.CaptureScreenAsync, cancellationToken,
            recognize: false, persistCurrentInput: true);

    public Task CaptureScrollingScreenshotSessionAsync(CancellationToken cancellationToken) =>
        RunInputAsync(_inputs.CaptureScrollingScreenAsync, cancellationToken,
            recognize: false, persistCurrentInput: true);

    /// <summary>
    /// 显式识别截图会话当前导出的最终 PNG；不回退未编辑原图，
    /// 也不替换编辑器的基准输入（CurrentInput 保持会话基准图）。
    /// </summary>
    public Task RecognizeCapturedInputAsync(
        RecognitionInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RunInputAsync(
            _ => Task.FromResult<RecognitionInput?>(input),
            cancellationToken,
            recognize: true,
            persistCurrentInput: false);
    }

    /// <summary>清除旧识别结果及其关联（编辑修订/新会话/维护事件后调用）。</summary>
    public void InvalidateResult()
    {
        _result = null;
        ResultText = string.Empty;
        if (CurrentInput is not null)
        {
            Status = "内容已更新，旧识别结果已失效";
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResult)));
    }

    public void Cancel() => _activeRun?.Cancel();

    public Task RecognizeViaSupervisorAsync(
        Func<CancellationToken, Task<RecognitionInput?>> loadInput,
        CancellationToken cancellationToken) =>
        RunInputAsync(loadInput, cancellationToken, recognize: true, persistCurrentInput: true);

    private async Task RunInputAsync(
        Func<CancellationToken, Task<RecognitionInput?>> loadInput,
        CancellationToken cancellationToken,
        bool recognize,
        bool persistCurrentInput)
    {
        ArgumentNullException.ThrowIfNull(loadInput);
        string pipeline = recognize ? EffectivePipeline : Pipeline;
        OcrEngine? engine = recognize ? EffectiveEngine : null;
        MineruConfig? mineru = _taskMineruConfig;
        // 提交冻结点（输入 await 前）：按冻结的模式对原始 typed 选项严格
        // ToWire；不支持或越界字段在这里明确拒绝整个提交，不静默丢弃后
        // 仍提交（#110 AC2）。状态展示过滤走 ProjectWire，与此分开。
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? options = null;
        if (recognize && _taskOptions is not null)
        {
            try
            {
                options = _taskOptions.ToWire(_taskRecognitionMode!);
            }
            catch (ArgumentException error)
            {
                TerminalState = JobState.Failed;
                Status = $"识别选项无效，已拒绝提交：{error.Message}";
                return;
            }
        }
        long generation = Interlocked.Increment(ref _generation);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _activeRun,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        previous?.Cancel();
        previous?.Dispose();
        CancellationTokenSource run = _activeRun;
        if (generation == Volatile.Read(ref _generation))
        {
            IsBusy = true;
            TerminalState = null;
            Status = "正在读取输入";
        }

        try
        {
            RecognitionInput? input = await loadInput(run.Token);
            if (input is null)
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    TerminalState = JobState.Cancelled;
                    Status = "已取消选择";
                }
                return;
            }

            if (generation == Volatile.Read(ref _generation))
            {
                if (persistCurrentInput)
                {
                    CurrentInput = input;
                }
                _result = null;
                ResultText = string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResult)));
                Status = recognize ? "正在识别" : "截图已捕获，可编辑标注";
            }

            if (!recognize)
            {
                // 纯截图：到此为止，不产生任何推理/安装请求。
                if (generation == Volatile.Read(ref _generation))
                {
                    TerminalState = null;
                }
                return;
            }

            const string clientItemKey = "recognition-input";
            InferenceJobRun job = await _jobs.RunRecognitionAsync(
                pipeline,
                JobPriority.Interactive,
                [
                    new InferenceUploadInput(
                        clientItemKey,
                        input.DisplayName,
                        input.MediaType,
                        input.Data),
                ],
                options: options,
                cancellationToken: run.Token,
                engine: engine,
                mineru: pipeline == "MinerU" ? mineru : null);
            JobSnapshot snapshot = job.Snapshot;

            if (generation != Volatile.Read(ref _generation)) return;

            if (snapshot.State is JobState.Cancelled)
            {
                TerminalState = JobState.Cancelled;
                Status = "已取消";
                return;
            }
            if (snapshot.State is JobState.Failed)
            {
                TerminalState = JobState.Failed;
                Status = "识别失败";
                return;
            }

            ItemOutcome outcome = job.OutcomesByClientItemKey[clientItemKey];
            if (outcome.State is not ItemState.Succeeded)
            {
                TerminalState = outcome.State is ItemState.Cancelled
                    ? JobState.Cancelled : JobState.Failed;
                Status = TerminalState is JobState.Cancelled ? "已取消" : "识别失败";
                return;
            }

            _result = RecognitionOutcomeMapper.ToResponse(outcome, pipeline);
            ResultText = _result.Text;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResult)));
            Status = "识别完成";
        }
        catch (OperationCanceledException)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                TerminalState = JobState.Cancelled;
                Status = "已取消";
            }
        }
        catch (InferenceClientException error)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                TerminalState = error.Code is HttpV2ErrorCode.Cancelled
                    ? JobState.Cancelled : JobState.Failed;
                Status = LocalizeV2(error.Code);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                TerminalState = JobState.Failed;
                Status = "Supervisor 已断开，请重试";
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                IsBusy = false;
                if (ReferenceEquals(Interlocked.CompareExchange(ref _activeRun, null, run), run))
                    run.Dispose();
            }
        }
    }

    private static string LocalizeV2(HttpV2ErrorCode code) => code switch
    {
        HttpV2ErrorCode.ValidationError => "输入图片无效",
        HttpV2ErrorCode.QuotaExceeded => "输入过大",
        HttpV2ErrorCode.Unauthorized => "Supervisor 会话无效",
        HttpV2ErrorCode.ForbiddenLoopback => "Supervisor 拒绝非本地连接",
        HttpV2ErrorCode.JobNotFound => "任务已过期",
        HttpV2ErrorCode.BackendUnavailable or HttpV2ErrorCode.TransientBackend
            => "Supervisor 暂不可用，请重试",
        HttpV2ErrorCode.Cancelled => "已取消",
        HttpV2ErrorCode.OutOfMemory => "内存或显存不足",
        HttpV2ErrorCode.SupervisorDraining => "Supervisor 正在关闭，请稍后",
        HttpV2ErrorCode.ProtocolMismatch => "Supervisor 协议不兼容",
        HttpV2ErrorCode.OcrEngineUnknown => "未知引擎，请重新选择",
        HttpV2ErrorCode.OcrEngineUnavailable => "所选引擎不可用，请重新选择本次识别模式",
        HttpV2ErrorCode.OcrEnginePreparationRequired => "所选引擎需要先准备依赖",
        HttpV2ErrorCode.OcrEngineNotValidForPipeline => "该管线不支持所选引擎",
        HttpV2ErrorCode.OcrEngineLanguageUnavailable => "所选引擎缺少语言包",
        _ => "识别失败",
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
