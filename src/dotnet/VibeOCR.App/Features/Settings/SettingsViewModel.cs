using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Services;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.App.Features.Maintenance;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.App.Features.Settings;

/// <summary>One user-selectable upstream source preference.</summary>
public sealed record SettingsSourceOption(
    string Kind,
    string Id,
    string DisplayName,
    bool Selected);

/// <summary>One optional feature for the pending accelerator.</summary>
public sealed record SettingsFeatureOption(
    string FeatureId,
    string DisplayName,
    string Accelerator,
    bool Selected);

/// <summary>任务继承绑定时默认识别模式的解析状态。</summary>
internal enum RuntimeDefaultModeBinding
{
    /// <summary>Backend 未声明能力或快照未携带默认信息：按旧语义提交。</summary>
    NotApplicable,
    /// <summary>已回显有效模式 id：继承绑定它。</summary>
    Bound,
    /// <summary>持久值无法解析（非字符串/显式 null）：提交拒绝并提示修复。</summary>
    Invalid,
    /// <summary>能力已声明但快照未回显默认值：提交拒绝，不猜默认。</summary>
    Unread,
}

internal sealed record RecognitionSelectionSnapshot(
    RuntimeSelectionService Catalog,
    RuntimeDefaultModeBinding DefaultMode = RuntimeDefaultModeBinding.NotApplicable,
    string? DefaultRecognitionModeId = null,
    MineruRecognitionPreference? MineruPreference = null,
    string? MineruPreferenceError = null)
{
    /// <summary>
    /// 消费方统一入口：把持久化的全局 MinerU 偏好解析为提交配置。偏好
    /// 语法无法解析时对 mineru_document fail closed（不静默回退目录默认），
    /// 错误消息可直接展示。
    /// </summary>
    public MineruConfig? MineruConfigFor(string? modeId)
    {
        if (MineruPreferenceError is not null &&
            string.Equals(modeId, "mineru_document", StringComparison.Ordinal))
        {
            throw new RuntimeSelectionException(
                RuntimeSelectionErrorKind.InvalidCatalogEntry,
                $"已保存的 MinerU 识别参数无法解析（{MineruPreferenceError}），请在本页重新保存修复。");
        }
        return Catalog.MineruConfigFor(modeId, MineruPreference);
    }
}

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly HashSet<string> UserSelectableSourceKinds = new(StringComparer.Ordinal)
    {
        "package_index",
        "model_registry",
        "paddleocr_model_registry",
        "mineru_model_registry",
    };

    private readonly IInferenceClient _inference;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    // 设置快照代际与 busy 的唯一串行化点：LoadSnapshot 起始段、
    // InvalidateSnapshot、finally 复位全部在同一把锁内原子完成。
    private readonly object _busyGuard = new();
    private long _generation;
    private long _selectionGeneration;
    private bool _isBusy;
    private string _status = "正在读取设置";
    private string? _backend;
    private string _pendingBackend = "cpu";
    private bool _restartRequired;
    private bool _selectionStaged;
    private HashSet<string> _installedComponentIds = new(StringComparer.Ordinal);
    private RuntimeSelectionService? _selection;
    private RecognitionSelectionSnapshot? _recognitionSelection;
    private IReadOnlyList<SettingsSourceOption> _sources = [];
    private IReadOnlyList<SettingsFeatureOption> _features = [];
    private IReadOnlyList<string> _selectedSourceIds = [];
    private MineruConnectionState? _mineruConnection;
    private DefaultRecognitionModeState? _defaultRecognitionMode;
    private MineruRecognitionState? _mineruRecognition;

    public SettingsViewModel(
        IInferenceClient inference,
        RuntimeStatusViewModel? runtimeStatus = null,
        Func<IRuntimeInstallerClient>? installerFactory = null,
        ProductMaintenanceCoordinator? productMaintenance = null,
        Func<CancellationToken, Task>? stopService = null,
        Func<Task>? restoreService = null,
        string? operationStorePath = null,
        ManagedEnvironmentSettings? environments = null)
    {
        _inference = inference ?? throw new ArgumentNullException(nameof(inference));
        Environments = environments;
        RuntimeStatus = runtimeStatus ?? new RuntimeStatusViewModel();
        Maintenance = installerFactory is null
            ? new RuntimeMaintenanceCoordinator(
                () => throw new InvalidOperationException(
                    "Runtime installer is unavailable in this mode."),
                RuntimeStatus,
                productMaintenance)
            : new RuntimeMaintenanceCoordinator(
                installerFactory,
                RuntimeStatus,
                productMaintenance, stopService, restoreService, operationStorePath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> PreloadPipelines { get; } = [];
    public ObservableCollection<ResidencyEntry> ResidencyEntries { get; } = [];
    public ObservableCollection<PipelineSpec> ResidencyPipelines { get; } = [];
    public RuntimeStatusViewModel RuntimeStatus { get; }
    public ManagedEnvironmentSettings? Environments { get; }
    public int DefaultTtlSeconds { get; private set; } = 300;
    public int? VramTotalMb { get; private set; }
    public int? VramUsedMb { get; private set; }
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }

    /// <summary>
    /// 当前运行时 profile 声明的目标加速器（cpu/nvidia_cuda）；在首次真
    /// 实快照前为 null，不得把默认目标 cpu 冒充为实际运行设备。
    /// </summary>
    public string? Backend { get => _backend; private set => SetField(ref _backend, value); }
    public string PendingBackend { get => _pendingBackend; set => SetField(ref _pendingBackend, value); }
    public bool RestartRequired { get => _restartRequired; private set => SetField(ref _restartRequired, value); }
    public bool CanSwitchBackend => !IsBusy && Backend is not null && !string.Equals(Backend, PendingBackend, StringComparison.Ordinal);

    public IReadOnlyList<SettingsSourceOption> Sources
    {
        get => _sources;
        private set => SetField(ref _sources, value);
    }

    public IReadOnlyList<SettingsFeatureOption> Features
    {
        get => _features;
        private set => SetField(ref _features, value);
    }

    public RuntimeSelectionService? Selection => _selection;

    /// <summary>
    /// 当前 MinerU 连接投影（extra.mineru_connection）；首次设置读取前为
    /// null，UI 按未加载呈现。API Key 明文不经过此投影。
    /// </summary>
    public MineruConnectionState? MineruConnection
    {
        get => _mineruConnection;
        private set => SetField(ref _mineruConnection, value);
    }

    /// <summary>
    /// 当前默认识别模式投影（extra.default_recognition_mode）；首次
    /// 读取前为 null，UI 按未加载呈现。Supported=false 时只读兼容说明，
    /// 不向旧 Backend 写未知键。Stored=true 仅表示 Backend 回显了该键，
    /// 不代表用户已显式保存。
    /// </summary>
    public DefaultRecognitionModeState? DefaultRecognitionMode
    {
        get => _defaultRecognitionMode;
        private set => SetField(ref _defaultRecognitionMode, value);
    }

    /// <summary>
    /// 全局 MinerU 识别偏好投影（extra.mineru_recognition）；首次读取前
    /// 为 null。Invalid=true 表示持久值语法无法解析：提交路径 fail closed，
    /// 不静默降级目录默认。
    /// </summary>
    public MineruRecognitionState? MineruRecognition
    {
        get => _mineruRecognition;
        private set => SetField(ref _mineruRecognition, value);
    }

    internal RecognitionSelectionSnapshot? RecognitionSelection =>
        Volatile.Read(ref _recognitionSelection);

    /// <summary>Durable maintenance operations driven by the staged selection.</summary>
    public RuntimeMaintenanceCoordinator Maintenance { get; }

    public Task LoadSnapshotAsync(CancellationToken cancellationToken) =>
        LoadSnapshotAsync(refreshEnvironments: true, cancellationToken);

    /// <summary>refreshEnvironments=false 不刷环境列表，保留其根因失败文案。</summary>
    public async Task LoadSnapshotAsync(bool refreshEnvironments, CancellationToken cancellationToken)
    {
        if (refreshEnvironments && Environments is not null)
            await Environments.RefreshAsync(cancellationToken);
        await Maintenance.RestoreAsync(cancellationToken);
        long generation = BeginBusy("正在读取模型驻留状态");
        try
        {
            try
            {
                RuntimeStatusSnapshot runtime = await _inference.GetRuntimeStatusAsync(cancellationToken);
                if (generation != Volatile.Read(ref _generation)) return;
                RuntimeStatus.ApplySnapshot(runtime);
                Backend = runtime.Profile.Accelerator == RuntimeAccelerator.Cpu ? "cpu" : "nvidia_cuda";
                _installedComponentIds = runtime.Profile.Components
                    .Where(component => component.ActualState == RuntimeComponentActualState.Ready)
                    .Select(component => component.ComponentId).ToHashSet(StringComparer.Ordinal);
                if (!_selectionStaged) PendingBackend = Backend;
            }
            catch (NotSupportedException)
            {
                // Older test doubles retain installer-local status.
            }
            try
            {
                ResidencyStatus status = await _inference.GetResidencyAsync(cancellationToken);
                if (generation != Volatile.Read(ref _generation)) return;
                DefaultTtlSeconds = status.DefaultTtlSeconds;
                VramTotalMb = status.VramTotalMb;
                VramUsedMb = status.VramUsedMb;
                ResidencyEntries.Clear(); foreach (var e in status.Entries) ResidencyEntries.Add(e);
                ResidencyPipelines.Clear(); foreach (var p in status.Pipelines) ResidencyPipelines.Add(p);
                PropertyChanged?.Invoke(this, new(nameof(DefaultTtlSeconds)));
                PropertyChanged?.Invoke(this, new(nameof(VramTotalMb)));
                PropertyChanged?.Invoke(this, new(nameof(VramUsedMb)));
                Status = $"默认 TTL {status.DefaultTtlSeconds}s；已驻留管线 {status.Entries.Count} 个";
            }
            catch (NotSupportedException)
            {
                // 旧实例的迟到失败与迟到成功同样不得覆盖新状态。
                if (generation == Volatile.Read(ref _generation))
                    Status = "当前运行环境不提供模型驻留控制。";
            }
            catch (InferenceClientException error)
            {
                if (generation == Volatile.Read(ref _generation))
                    Status = LocalizeV2(error.Code);
            }
            // 已失效的旧 runtime/residency 流程不得继续接管 selection：
            // 否则其 forceReload 会清掉后续新代的选择代并以新代身份投影。
            if (generation != Volatile.Read(ref _generation)) return;
            await LoadSelectionSerializedAsync(forceReload: true, cancellationToken, generation);
        }
        catch (VibeOCR.App.Inference.InferenceClientNotAttachedException)
        {
            if (generation == Volatile.Read(ref _generation))
                Status = "尚未连接运行环境；可配置来源或准备依赖。";
        }
        catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) Status = "已取消"; }
        catch (RuntimeSelectionException error) { if (generation == Volatile.Read(ref _generation)) Status = LocalizeSelection(error); }
        catch (InferenceClientException error) { if (generation == Volatile.Read(ref _generation)) Status = LocalizeV2(error.Code); }
        catch (Exception error) when (generation == Volatile.Read(ref _generation))
        {
            AppLog.Warn($"Settings refresh failed: {error.GetType().Name}: {error.Message}");
            Status = "设置状态读取失败，请重试或打开诊断与修复。";
        }
        finally { EndBusy(generation); }
    }

    private long BeginBusy(string status)
    {
        lock (_busyGuard)
        {
            long generation = ++_generation;
            IsBusy = true;
            // 同步通知可能重入失效；失效后不再写本次旧文案。
            if (generation == _generation) Status = status;
            return generation;
        }
    }

    private void EndBusy(long generation)
    {
        lock (_busyGuard)
        {
            if (generation == _generation) IsBusy = false;
        }
    }

    /// <summary>
    /// 读取（或复用已有）Runtime 目录快照。返回 false 仅表示本次读取被
    /// 代际失效丢弃（环境切换/实例更换的 ClearSelection/Invalidate，
    /// 未发布快照）；true 表示读取完成（含确认旧 Backend 无目录能力）。
    /// 既有只 await 的调用者可忽略结果。
    /// </summary>
    public Task<bool> LoadSelectionAsync(CancellationToken cancellationToken) =>
        LoadSelectionSerializedAsync(forceReload: false, cancellationToken);

    /// <inheritdoc cref="LoadSelectionAsync"/>
    public Task<bool> RefreshSelectionAsync(CancellationToken cancellationToken) =>
        LoadSelectionSerializedAsync(forceReload: true, cancellationToken);

    public void ClearSelection()
    {
        Interlocked.Increment(ref _selectionGeneration);
        _selection = null;
        Volatile.Write(ref _recognitionSelection, null);
    }

    /// <summary>
    /// 环境切换或服务实例更换时作废旧快照：在途读取不得再投影，旧的
    /// 目标加速器与旧实例的状态文案也不再冒充当前状态；Sources/
    /// MineruConnection 是用户配置而非实例状态，不清空以免丢失。
    /// status 覆盖默认文案：操作开始处结果未知，用中性的待重检文案。
    /// </summary>
    public void InvalidateSnapshot(string? status = null)
    {
        lock (_busyGuard)
        {
            _generation++;
            // 实例更换同样作废在途目录读取的选择代：旧实例 selection 的
            // 迟到成功/失败都不得越过新状态（与 ClearSelection 同一语义）。
            Interlocked.Increment(ref _selectionGeneration);
            IsBusy = false;
            Backend = null;
            Status = status ?? "服务实例已更换，状态待重新检查。";
        }
    }

    /// <summary>
    /// Persist the per-kind source selection in Backend settings (the
    /// long-term source of truth); null id clears that kind's selection.
    /// </summary>
    public async Task SetSourceAsync(string kind, string? sourceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (Maintenance.State.IsRunning) { Status = "运行环境维护尚未结束。"; return; }
        Maintenance.DiscardPlan();
        if (_selection is null)
        {
            Status = "运行时目录尚未加载，请先刷新运行时";
            return;
        }
        if (!UserSelectableSourceKinds.Contains(kind))
        {
            Status = "当前版本不支持设置此下载源类别";
            return;
        }
        try
        {
            await _settingsGate.WaitAsync(cancellationToken);
            try
            {
                IReadOnlyList<string> next = ComposeSourceSelection(kind, sourceId);
                SettingsSnapshot updated = await _selection.ApplySourcePreferenceAsync(
                    _inference,
                    next.Count == 0 ? null : next,
                    cancellationToken);
                Maintenance.DiscardPlan();
                _selectedSourceIds = updated.DownloadSourceIds ?? [];
                Sources = ProjectSources(_selection, _selectedSourceIds);
                Status = "已保存下载源偏好";
            }
            finally { _settingsGate.Release(); }
        }
        catch (RuntimeSelectionException error)
        {
            Status = LocalizeSelection(error);
        }
        catch (InferenceClientException error)
        {
            Status = LocalizeV2(error.Code);
        }
    }

    /// <summary>
    /// 通过 /v2/settings extra.mineru_connection 写入 MinerU 连接偏好。远程
    /// 写入需要 Backend 声明 ocr.mineru-remote-api.v1，旧 Backend 明确失败；
    /// apiKey 为 null 时保留 Backend 已存 Key（宿主合并），空字符串清除，
    /// 非空替换。保存本身不验证远程服务连通性，成功后回读刷新运行时目录。
    /// </summary>
    public async Task SetMineruConnectionAsync(
        string? mode,
        string? apiUrl,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (Maintenance.State.IsRunning) { Status = "运行环境维护尚未结束。"; return; }
        RuntimeSelectionService? selection = _selection;
        if (selection is null)
        {
            Status = MineruCatalogUnavailableStatus();
            return;
        }
        if (mode == "remote" && !selection.SupportsMineruRemoteApi)
        {
            Status =
                $"当前 Backend 未声明 {RuntimeSelectionService.MineruRemoteApiCapability}，无法保存远程 MinerU 配置";
            return;
        }
        try
        {
            await _settingsGate.WaitAsync(cancellationToken);
            try
            {
                SettingsSnapshot updated = await MineruConnectionSettings.ApplyAsync(
                    _inference, mode, apiUrl, apiKey, cancellationToken);
                MineruConnection = MineruConnectionSettings.Read(
                    updated, selection.SupportsMineruRemoteApi);
            }
            finally { _settingsGate.Release(); }
        }
        catch (ArgumentException error) { Status = error.Message; return; }
        catch (InferenceClientException error) { Status = LocalizeV2(error.Code); return; }
        catch (RuntimeSelectionException error) { Status = LocalizeSelection(error); return; }
        // 写入成功后回读刷新目录：本地/远程切换可能改变 mineru 模式可用性。
        try
        {
            await LoadSelectionSerializedAsync(forceReload: true, cancellationToken);
            Status = MineruConnection?.IsRemote == true
                ? "已保存 MinerU 远程配置（保存不验证服务连通性）"
                : "已保存 MinerU 本地配置";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // 保存已生效；目录刷新失败不应把它流报成未保存。
            Status = "MinerU 配置已保存；刷新运行时目录失败，请重新检查状态。";
        }
    }

    /// <summary>
    /// 通过 /v2/settings extra.default_recognition_mode 写入默认识别模式。
    /// 写入需要 Backend 声明 ocr.default-recognition-mode.v1，且目标必须是
    /// 当前目录内 ready 的模式 id：未知/不可用（含需要准备组件）fail
    /// closed，不静默降级也不触发安装。保存不改变在跑/已排队任务参数；
    /// 失败保留原已提交默认。
    /// </summary>
    public async Task SetDefaultRecognitionModeAsync(
        string modeId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        if (Maintenance.State.IsRunning) { Status = "运行环境维护尚未结束。"; return; }
        RuntimeSelectionService? selection = _selection;
        if (selection is null)
        {
            Status = "运行时目录尚未加载，请先刷新运行时";
            return;
        }
        if (!selection.SupportsDefaultRecognitionMode)
        {
            Status =
                $"当前 Backend 未声明 {RuntimeSelectionService.DefaultRecognitionModeCapability}，无法保存默认识别模式";
            return;
        }
        if (!selection.SupportsRecognitionModes)
        {
            Status = "当前 Backend 未提供识别模式目录，无法保存默认识别模式";
            return;
        }
        try
        {
            // 与 Backend PUT 同一严格度：仅 ready 模式可保存为默认，
            // preparation_required/unavailable/未知 id 一律拒绝。
            RecognitionModeOption mode = selection.FindRecognitionMode(modeId);
            if (!string.Equals(mode.Availability, "ready", StringComparison.Ordinal))
            {
                Status = mode.Availability == "preparation_required"
                    ? $"模式 {SettingsViewModel.DisplayName(modeId)} 需要先准备对应组件，就绪后才能设为默认"
                    : $"模式 {SettingsViewModel.DisplayName(modeId)} 当前不可用，不能设为默认";
                return;
            }
            // 与提交同一入口：校验默认模式切换为 mineru_document 时当前
            // 全局偏好可解析（无效持久值 fail closed，提示先修复）。
            (RecognitionSelection ?? new RecognitionSelectionSnapshot(selection))
                .MineruConfigFor(mode.Id);
        }
        catch (RuntimeSelectionException error)
        {
            Status = LocalizeSelection(error);
            return;
        }
        try
        {
            await _settingsGate.WaitAsync(cancellationToken);
            try
            {
                SettingsSnapshot updated = await DefaultRecognitionModeSettings.ApplyAsync(
                    _inference, modeId, cancellationToken);
                DefaultRecognitionMode = DefaultRecognitionModeSettings.Read(
                    updated, selection.SupportsDefaultRecognitionMode);
                Status = $"已保存默认识别模式：{DisplayName(modeId)}";
            }
            finally { _settingsGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (InferenceClientException error)
        {
            // 保存失败：原已提交默认、持久值与投影均不变，仅提示可重试。
            Status = LocalizeV2(error.Code);
            return;
        }
        catch (RuntimeSelectionException error)
        {
            Status = LocalizeSelection(error);
            return;
        }
        // 保存成功后重发布任务目录快照的默认状态，任务页继承选项立即反映。
        PublishRecognitionSelection(selection, DefaultRecognitionMode);
    }

    /// <summary>
    /// 通过 /v2/settings extra.mineru_recognition 写入全局 MinerU 识别
    /// 偏好（tier/ocr_mode/page_range/language）。写入需要 Backend 声明
    /// ocr.mineru-config.v1；tier/language 必须在当前目录内可用（未知或
    /// 不可用 fail closed，不静默降级）。保存成功后重发布任务目录快照，
    /// 在跑/已排队任务参数不变。
    /// </summary>
    public async Task SetMineruRecognitionAsync(
        string? tier,
        string? ocrMode,
        string? pageRange,
        string? language,
        CancellationToken cancellationToken)
    {
        if (Maintenance.State.IsRunning) { Status = "运行环境维护尚未结束。"; return; }
        RuntimeSelectionService? selection = _selection;
        if (selection is null)
        {
            Status = MineruCatalogUnavailableStatus();
            return;
        }
        if (!selection.SupportsMineruConfig)
        {
            Status = $"当前 Backend 未声明 {RuntimeSelectionService.MineruConfigCapability}，无法保存 MinerU 识别参数";
            return;
        }
        if (!MineruRecognitionSettings.TryParseTier(tier, out MineruTier tierValue))
        {
            Status = "未知 MinerU 识别档位，请重新选择";
            return;
        }
        if (!MineruRecognitionSettings.TryParseOcrMode(ocrMode, out MineruOcrMode ocrModeValue))
        {
            Status = "未知 MinerU OCR 模式，请重新选择";
            return;
        }
        string normalizedPageRange = pageRange?.Trim().ToLowerInvariant() ?? "";
        if (!MineruRecognitionSettings.IsValidPageRange(normalizedPageRange))
        {
            Status = "页码范围请填写 all 或 1,3-5；r1 表示最后一页";
            return;
        }
        if (string.IsNullOrWhiteSpace(language))
        {
            Status = "MinerU 语言不能为空";
            return;
        }
        // 语言只能来自目录声明值（ch/korean/...），不造不存在的 wire 值；
        // 仅本地模式生效，远程由服务端启动参数决定（UI 侧禁用编辑）。
        if (!selection.MineruLanguages.Contains(language, StringComparer.Ordinal))
        {
            Status = "当前运行时目录不提供该 MinerU 语言，请重新选择";
            return;
        }
        var preference = new MineruRecognitionPreference(
            tierValue, ocrModeValue, normalizedPageRange, language);
        try
        {
            // 与提交路径同一严格度：目录层校验（tier 可用性/语言在目录内）。
            selection.MineruConfigFor("mineru_document", preference);
        }
        catch (RuntimeSelectionException error)
        {
            Status = LocalizeSelection(error);
            return;
        }
        try
        {
            await _settingsGate.WaitAsync(cancellationToken);
            try
            {
                SettingsSnapshot updated = await MineruRecognitionSettings.ApplyAsync(
                    _inference, preference, cancellationToken);
                MineruRecognition = MineruRecognitionSettings.Read(
                    updated, selection.SupportsMineruConfig);
                Status = "已保存 MinerU 识别参数";
            }
            finally { _settingsGate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException error) { Status = error.Message; return; }
        catch (InferenceClientException error) { Status = LocalizeV2(error.Code); return; }
        // 保存成功：立即重发布偏好，消费方无需等待目录刷新。
        PublishRecognitionSelection(selection, DefaultRecognitionMode);
    }

    /// <summary>
    /// 对已保存的远程 MinerU 配置执行真实准备：/v2/runtime/preload
    /// （pipelines=['MinerU']、recognition_modes=['mineru_document']），
    /// 请求由 Backend 转发到远程 MinerU 4 服务（前端不直连），完成后回读
    /// 刷新 health/tier 目录。不宣称服务可解析，tier 可用性以刷新后的
    /// 目录与实际识别任务为准。
    /// </summary>
    public async Task PrepareMineruRemoteAsync(CancellationToken cancellationToken)
    {
        if (Maintenance.State.IsRunning) { Status = "运行环境维护尚未结束。"; return; }
        RuntimeSelectionService? selection = _selection;
        if (selection is null)
        {
            Status = MineruCatalogUnavailableStatus();
            return;
        }
        if (MineruConnection?.IsRemote != true)
        {
            Status = "请先保存远程 MinerU 配置，再执行验证与准备。";
            return;
        }
        RecognitionModeOption mineruMode;
        try
        {
            mineruMode = selection.FindRecognitionMode("mineru_document");
        }
        catch (RuntimeSelectionException error)
        {
            Status = LocalizeSelection(error);
            return;
        }
        if (!mineruMode.SupportsPreload)
        {
            Status = "当前运行时声明 mineru_document 不支持预加载，请更新 Backend。";
            return;
        }
        long generation = BeginBusy("正在验证并准备远程 MinerU 服务…");
        try
        {
            if (generation != Volatile.Read(ref _generation)) return;
            await _inference.PreloadRuntimeAsync(
                new Wire.RuntimePreloadRequest
                {
                    Pipelines = ["MinerU"],
                    RecognitionModes = [Wire.RecognitionModeId.MineruDocument],
                },
                cancellationToken);
            // 准备完成必须回读：tier/能力目录以刷新后的 Backend 目录为准。
            if (generation != Volatile.Read(ref _generation)) return;
            await LoadSelectionSerializedAsync(forceReload: true, cancellationToken, generation);
            if (generation == Volatile.Read(ref _generation))
                Status = "远程 MinerU 准备已执行并刷新目录；tier 可用性以识别任务实际结果为准。";
        }
        catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) Status = "已取消"; }
        catch (NotSupportedException)
        {
            if (generation == Volatile.Read(ref _generation))
                Status = "当前 Supervisor 不支持运行时预加载，请更新运行环境。";
        }
        catch (InferenceClientException error) { if (generation == Volatile.Read(ref _generation)) Status = LocalizeMineruPreparationFailure(error); }
        catch (RuntimeSelectionException error) { if (generation == Volatile.Read(ref _generation)) Status = LocalizeSelection(error); }
        finally { EndBusy(generation); }
    }

    /// <summary>
    /// 目录未加载时的 MinerU 配置指引。无已启动运行环境承载识别服务时刷新
    /// 永远无效，必须如实说明：远程 MinerU 不需要本地 MinerU/PaddleOCR 依赖，
    /// 但设置与目录服务运行在已安装的运行环境里。环境证据来自 Runtime
    /// 权威快照，宿主不自行推断依赖图，也不默认安装任何引擎。
    /// </summary>
    internal string MineruCatalogUnavailableStatus()
    {
        ManagedEnvironmentList? snapshot = Environments?.Snapshot;
        if (snapshot is null)
            return "运行时目录尚未加载，请先刷新运行时";
        bool hasInstalledActive = snapshot.ActiveId is { } activeId &&
            snapshot.Environments.Any(item => item.Id == activeId &&
                string.Equals(item.Status, "installed", StringComparison.Ordinal));
        return hasInstalledActive
            ? "运行时目录尚未加载，识别服务可能正在启动，请稍后重新检查状态。"
            : "当前没有已启动的运行环境承载识别服务。远程 MinerU 不需要本地 MinerU/PaddleOCR 依赖，但连接配置由识别服务保存：可在 MinerU 连接区启用远程模式，自动从安装包准备基础服务。";
    }

    /// <summary>
    /// 远程准备失败的本地化：仅对 Runtime 白名单化的 ValidationError
    /// reason（mineru_api_endpoint_incompatible /
    /// mineru_api_authentication_failed）给出可操作建议；其余按既有
    /// v2 错误码本地化，不回显远程服务正文、URL 或 API Key。
    /// </summary>
    internal static string LocalizeMineruPreparationFailure(InferenceClientException error)
    {
        if (error.Code == HttpV2ErrorCode.ValidationError &&
            error.Detail.TryGetValue("reason", out JsonElement reason) &&
            reason.ValueKind == JsonValueKind.String)
        {
            switch (reason.GetString())
            {
                case "mineru_api_endpoint_incompatible":
                    return "远程 MinerU 服务接口不匹配：请填自部署 MinerU 4 解析服务的根地址（如 https://mineru4.example.com），不要带 /file_parse 等具体路径，也不要填 OpenAI/VLM 兼容地址；修正后保存再重试。";
                case "mineru_api_authentication_failed":
                    return "远程 MinerU 服务拒绝了认证：请检查已保存的 API Key 是否正确且对该服务有效；修正后保存再重试。";
            }
        }
        return LocalizeV2(error.Code);
    }

    /// <summary>Stage the accelerator for the pending feature selection.</summary>
    public void SetPendingAccelerator(string accelerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        if (Maintenance.State.IsRunning) { Status = "运行环境维护尚未结束。"; return; }
        Maintenance.DiscardPlan();
        _selectionStaged = true;
        PendingBackend = accelerator;
        Features = _selection is null
            ? []
            : ProjectFeatures(_selection, accelerator, []);
    }

    /// <summary>Toggle one optional feature for the pending accelerator (session state).</summary>
    public void SetFeatureEnabled(string featureId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        if (Maintenance.State.IsRunning) { Status = "运行环境维护尚未结束。"; return; }
        IReadOnlyList<SettingsFeatureOption> current = Features;
        if (!current.Any(item => item.FeatureId == featureId))
        {
            Status = $"未知功能 {featureId}";
            return;
        }
        Maintenance.DiscardPlan();
        _selectionStaged = true;
        Features = [.. current.Select(item => item.FeatureId == featureId
            ? item with { Selected = enabled }
            : item)];
        Status = enabled
            ? $"已选择功能 {featureId}（安装属运行时维护操作）"
            : $"已取消功能 {featureId}";
    }

    /// <summary>Selected optional feature ids for the pending accelerator (N4 consumes these).</summary>
    public IReadOnlyList<string> PendingFeatureIds =>
        [.. Features.Where(item => item.Selected).Select(item => item.FeatureId)];

    /// <summary>
    /// 用户确认后以显式 intent 启动 ensure:未选功能时发送空 component 列表
    /// (显式 base-only);来源使用当前 Backend Settings 偏好。
    /// </summary>
    public async Task PreviewInstallAsync(CancellationToken cancellationToken)
    {
        if (_selection is null)
        {
            Status = "运行时目录尚未加载，请先刷新运行时";
            return;
        }
        try
        {
            await Maintenance.PreviewAsync(
                _selection,
                PendingBackend,
                PendingFeatureIds,
                _selectedSourceIds,
                cancellationToken);
            Status = "请核对 Backend 返回的安装计划，再确认执行。";
        }
        catch (OperationCanceledException)
        {
            Status = "运行时维护操作已取消";
        }
        catch (RuntimeSelectionException error)
        {
            Status = LocalizeSelection(error);
        }
        catch (RuntimeInstallerException)
        {
            Status = "安装预览失败，请检查下载来源或导出诊断。";
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            Status = error.Message;
        }
    }

    public async Task ConfirmInstallAsync(string planId, CancellationToken cancellationToken)
    {
        string? previousOperation = Maintenance.State.OperationId;
        try
        {
            await Maintenance.ConfirmAsync(planId, cancellationToken);
            Status = "运行环境维护已完成";
        }
        catch (OperationCanceledException) { Status = "运行环境维护已取消"; }
        catch (Exception error) when (error is RuntimeInstallerException or InvalidOperationException or NotSupportedException)
        {
            // Only envelope-granted retryable failures prompt a direct retry.
            Status = error is RuntimeInstallerException installerError
                ? string.Equals(installerError.CanonicalCode, "CANCELLED", StringComparison.Ordinal)
                    ? "运行环境维护已取消"
                    : installerError.Retryable
                        ? "安装失败，可重试、调整下载来源或导出诊断。"
                        : "安装失败，请重新预览安装范围或导出诊断。"
                : error.Message;
        }
        finally
        {
            if (Maintenance.State.OperationId != previousOperation && !Maintenance.State.IsRunning)
            {
                // The restored Supervisor owns the current device and engine availability.
                // Do not let recognition reuse the pre-maintenance catalog if refresh fails.
                string outcome = Status;
                ClearSelection();
                if (Maintenance.State.StatusCode == "succeeded") _selectionStaged = false;
                await LoadSnapshotAsync(CancellationToken.None);
                if (_selection is not null) Status = outcome;
            }
        }
    }

    public void CancelMaintenance() => Maintenance.Cancel();

    public async Task RetryMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Maintenance.RetryAsync(cancellationToken);
            if (Maintenance.Plan is not { } plan) return;
            _selectionStaged = true;
            PendingBackend = plan.Accelerator == Host.Accelerator.Cpu ? "cpu" : "nvidia_cuda";
            if (_selection is not null)
            {
                HashSet<string> requested = (plan.RequestedComponentIds ?? []).ToHashSet(StringComparer.Ordinal);
                string[] selected = _selection.Variants
                    .Where(variant => variant.Accelerator == PendingBackend && requested.Contains(variant.ComponentId))
                    .Select(variant => variant.FeatureId).ToArray();
                Features = ProjectFeatures(_selection, PendingBackend, selected);
            }
            Status = "已重新预览上次安装范围，请核对并确认。";
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (RuntimeInstallerException)
        {
            Status = "重新预览失败，请检查运行环境或导出诊断。";
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            Status = error.Message;
        }
    }

    public void Cancel() { }

    private async Task<bool> LoadSelectionSerializedAsync(
        bool forceReload,
        CancellationToken cancellationToken,
        long? snapshotGeneration = null)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            if (snapshotGeneration is { } generation &&
                generation != Volatile.Read(ref _generation))
            {
                // 旧快照流程在等门期间被失效（实例更换/新读取）：不得清掉
                // 新代的 selection 并以新代身份继续读取投影。
                return false;
            }
            if (!forceReload && RecognitionSelection is not null)
            {
                return true;
            }
            if (forceReload) ClearSelection();
            return await LoadSelectionCoreAsync(cancellationToken);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private async Task<bool> LoadSelectionCoreAsync(CancellationToken cancellationToken)
    {
        long selectionGeneration = Volatile.Read(ref _selectionGeneration);
        try
        {
            Wire.Health health = await _inference.GetHealthAsync(cancellationToken);
            RuntimeSelectionService selection = new(health);
            SettingsSnapshot settings = await _inference.GetSettingsAsync(cancellationToken);
            IReadOnlyList<string> selectedSourceIds = settings.DownloadSourceIds ?? [];
            if (selectionGeneration != Volatile.Read(ref _selectionGeneration)) return false;

            // Commit one complete catalog snapshot only after every remote read
            // succeeds. Concurrent bootstrap/execution callers then observe the
            // same selection instead of a partially projected catalog.
            _selectedSourceIds = selectedSourceIds;
            MineruConnection = MineruConnectionSettings.Read(
                settings, selection.SupportsMineruRemoteApi);
            DefaultRecognitionMode = DefaultRecognitionModeSettings.Read(
                settings, selection.SupportsDefaultRecognitionMode);
            MineruRecognition = MineruRecognitionSettings.Read(
                settings, selection.SupportsMineruConfig);
            Sources = ProjectSources(selection, selectedSourceIds);
            IReadOnlyList<string> selectedFeatures = _selectionStaged ? PendingFeatureIds :
                selection.Variants.Where(variant => variant.Accelerator == PendingBackend &&
                    _installedComponentIds.Contains(variant.ComponentId))
                    .Select(variant => variant.FeatureId).ToArray();
            Features = ProjectFeatures(selection, PendingBackend, selectedFeatures);
            // Publish the catalog marker last. Readers that observe the new
            // selection can therefore also observe its matching projections;
            // command paths additionally await this same gate. 投影写入可能
            // 重入 OnSettingsChanged/InvalidateSnapshot 推进代际：发布前
            // 复核，失效则不发布旧 snapshot。
            if (selectionGeneration != Volatile.Read(ref _selectionGeneration))
                return false;
            _selection = selection;
            PublishRecognitionSelection(selection, DefaultRecognitionMode);
            return true;
        }
        catch (NotSupportedException)
        {
            // Pre-2.7 clients do not expose health; selection stays unloaded.
            // 迟到旧实例的 NotSupported 同样受代际约束，不得绕过失效。
            return selectionGeneration == Volatile.Read(ref _selectionGeneration);
        }
        catch (RuntimeSelectionException error)
        {
            if (selectionGeneration == Volatile.Read(ref _selectionGeneration))
                Status = LocalizeSelection(error);
            throw;
        }
        catch (InferenceClientException error)
        {
            // 迟到失败与迟到成功同一代际闸门：旧实例/旧目录读取的失败
            // 不得覆盖新实例状态。
            if (selectionGeneration == Volatile.Read(ref _selectionGeneration))
                Status = LocalizeV2(error.Code);
            throw;
        }
    }

    private IReadOnlyList<string> ComposeSourceSelection(string kind, string? sourceId)
    {
        List<string> next = [.. _selectedSourceIds.Where(id =>
            !IsSelectionForKind(id, kind))];
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            next.Add(sourceId);
        }
        return next;
    }

    private bool IsSelectionForKind(string sourceId, string kind) =>
        _selection?.Sources.Any(source =>
            source.Id == sourceId &&
            string.Equals(source.Kind, kind, StringComparison.Ordinal)) == true;

    private void PublishRecognitionSelection(
        RuntimeSelectionService selection,
        DefaultRecognitionModeState? defaultMode = null)
    {
        // 统一从当前 MineruRecognition 投影解析：任何重发布路径（目录
        // 加载/保存默认模式/保存识别参数）都携带已存偏好或 Invalid
        // fail-closed 标记，不会把已保存配置清掉。
        (MineruRecognitionPreference? preference, string? error) =
            MineruRecognitionPreferenceFrom(MineruRecognition);
        Volatile.Write(
            ref _recognitionSelection,
            new RecognitionSelectionSnapshot(
                selection,
                BindingFor(defaultMode),
                defaultMode?.ModeId,
                preference,
                error));
    }

    private static (MineruRecognitionPreference? Preference, string? Error)
        MineruRecognitionPreferenceFrom(MineruRecognitionState? state)
    {
        if (state is not { Supported: true, Stored: true })
        {
            return (null, null);
        }
        if (state.Invalid || state.Tier is not { } tier || state.OcrMode is not { } ocrMode ||
            state.PageRange is not { } pageRange || state.Language is not { } language)
        {
            return (null, state.InvalidReason ?? "mineru_recognition_shape");
        }
        return (new MineruRecognitionPreference(tier, ocrMode, pageRange, language), null);
    }

    private static RuntimeDefaultModeBinding BindingFor(
        DefaultRecognitionModeState? defaultMode) => defaultMode switch
    {
        null or { Supported: false } => RuntimeDefaultModeBinding.NotApplicable,
        { ModeId: { } id } when !string.IsNullOrWhiteSpace(id) => RuntimeDefaultModeBinding.Bound,
        { Stored: true } => RuntimeDefaultModeBinding.Invalid,
        _ => RuntimeDefaultModeBinding.Unread,
    };

    private static IReadOnlyList<SettingsSourceOption> ProjectSources(
        RuntimeSelectionService selection,
        IReadOnlyList<string> selectedIds) =>
        [.. selection.Sources
            .Where(source => UserSelectableSourceKinds.Contains(source.Kind))
            .Select(source => new SettingsSourceOption(
                source.Kind,
                source.Id,
                SourceDisplayName(source),
                selectedIds.Contains(source.Id)))];

    private static IReadOnlyList<SettingsFeatureOption> ProjectFeatures(
        RuntimeSelectionService selection,
        string accelerator,
        IReadOnlyList<string> selectedIds) =>
        [.. selection.Variants
            .Where(variant => variant.Accelerator == accelerator)
            .Select(variant => new SettingsFeatureOption(
                variant.FeatureId,
                FeatureDisplayName(variant.FeatureId),
                variant.Accelerator,
                selectedIds.Contains(variant.FeatureId)))];

    internal static string DisplayName(OcrEngine engine) => engine switch
    {
        OcrEngine.RapidOcr => "RapidOCR",
        OcrEngine.Windows => "Windows OCR",
        OcrEngine.PaddleOcr => "PaddleOCR",
        _ => engine.ToString(),
    };

    internal static string DisplayName(string mode) => mode switch
    {
        "rapid_text" => "快速 OCR（RapidOCR）",
        "windows_text" => "Windows OCR（系统内置）",
        "paddle_text" => "通用 OCR（PaddleOCR）",
        "paddle_structure" => "文档结构识别（PP-StructureV3）",
        "paddle_document_vl" => "视觉文档解析（PaddleOCR-VL）",
        "mineru_document" => "深度文档解析（MinerU）",
        "paddle_table" => "表格结构识别（PaddleOCR）",
        "paddle_formula" => "数学公式识别（PaddleOCR）",
        _ => mode,
    };

    internal static string SourceDisplayName(Wire.DownloadSourceDescriptor source) => source.Id switch
    {
        "tuna-pypi" => "TUNA PyPI 镜像",
        "pypi" => "PyPI 官方源",
        "huggingface" or "paddleocr-huggingface" or "mineru-huggingface" => "Hugging Face",
        "modelscope" or "paddleocr-modelscope" or "mineru-modelscope" => "ModelScope",
        "paddleocr-bos" => "百度 BOS",
        _ => source.Id,
    };

    internal static string FeatureDisplayName(string featureId) => featureId switch
    {
        "paddleocr" => "PaddleOCR 本地文字与文档识别",
        "mineru" => "MinerU 本地深度文档解析（远程模式无需安装）",
        "document_parsing" => "文档解析（PaddleOCR/MinerU）",
        "gpu_runtime" => "CUDA GPU 运行时",
        _ => featureId,
    };

    internal static string LocalizeSelection(RuntimeSelectionException error) => error.Kind switch
    {
        RuntimeSelectionErrorKind.CapabilityMissing => "当前 Backend 不支持该选择能力",
        RuntimeSelectionErrorKind.InvalidCatalogEntry or RuntimeSelectionErrorKind.DuplicateCatalogEntry
            => "运行时目录数据无效，请刷新或更新 Backend",
        RuntimeSelectionErrorKind.UnknownEngine => "未知引擎，请重新选择",
        RuntimeSelectionErrorKind.EngineUnavailable => "该引擎当前不可用，请先安装所需依赖或选择其他引擎",
        RuntimeSelectionErrorKind.UnknownSource => "未知下载源，请重新选择",
        RuntimeSelectionErrorKind.DuplicateSourceKind => "每种下载源类别只能选择一个",
        RuntimeSelectionErrorKind.UnknownFeature => "当前加速器不支持该功能",
        _ => "选择失败",
    };

    private static string LocalizeV2(HttpV2ErrorCode code) => code switch
    {
        HttpV2ErrorCode.Unauthorized => "Supervisor 会话无效",
        HttpV2ErrorCode.ForbiddenLoopback => "Supervisor 拒绝非本地连接",
        HttpV2ErrorCode.BackendUnavailable or HttpV2ErrorCode.TransientBackend => "Supervisor 暂不可用，请重试",
        HttpV2ErrorCode.OutOfMemory => "内存或显存不足",
        HttpV2ErrorCode.SupervisorDraining => "Supervisor 正在关闭，请稍后",
        HttpV2ErrorCode.ProtocolMismatch => "Supervisor 协议不兼容",
        HttpV2ErrorCode.OcrEngineUnknown => "未知引擎，请重新选择",
        HttpV2ErrorCode.OcrEngineUnavailable => "所选引擎不可用，请在设置中重选",
        HttpV2ErrorCode.OcrEnginePreparationRequired => "所选引擎需要先准备依赖",
        HttpV2ErrorCode.OcrEngineNotValidForPipeline => "该管线不支持所选引擎",
        HttpV2ErrorCode.OcrEngineLanguageUnavailable => "所选引擎缺少语言包",
        HttpV2ErrorCode.DownloadSourceUnknown => "未知下载源，请重新选择",
        HttpV2ErrorCode.RuntimeComponentUnknown => "未知运行时组件",
        HttpV2ErrorCode.RuntimeCapabilityUnavailable => "当前 Backend 不支持该能力",
        _ => "操作失败",
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(IsBusy) or nameof(Backend) or nameof(PendingBackend))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSwitchBackend)));
    }
}
