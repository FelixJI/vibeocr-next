using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeOCR.App.ViewModels;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.App.Features.Maintenance;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

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

internal sealed record RecognitionSelectionSnapshot(RuntimeSelectionService Catalog);

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly HashSet<string> UserSelectableSourceKinds = new(StringComparer.Ordinal)
    {
        "package_index",
        "model_registry",
    };

    private readonly IInferenceClient _inference;
    private readonly SemaphoreSlim _selectionLoadGate = new(1, 1);
    private long _generation;
    private bool _isBusy;
    private string _status = "正在读取设置";
    private string _backend = "cpu";
    private string _pendingBackend = "cpu";
    private bool _restartRequired;
    private bool _gpuAvailable;
    private RuntimeSelectionService? _selection;
    private RecognitionSelectionSnapshot? _recognitionSelection;
    private IReadOnlyList<SettingsSourceOption> _sources = [];
    private IReadOnlyList<SettingsFeatureOption> _features = [];
    private IReadOnlyList<string> _selectedSourceIds = [];

    public SettingsViewModel(
        IInferenceClient inference,
        RuntimeStatusViewModel? runtimeStatus = null,
        Func<IRuntimeInstallerClient>? installerFactory = null,
        ProductMaintenanceCoordinator? productMaintenance = null)
    {
        _inference = inference ?? throw new ArgumentNullException(nameof(inference));
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
                productMaintenance);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> PreloadPipelines { get; } = [];
    public ObservableCollection<ResidencyEntry> ResidencyEntries { get; } = [];
    public ObservableCollection<PipelineSpec> ResidencyPipelines { get; } = [];
    public RuntimeStatusViewModel RuntimeStatus { get; }
    public int DefaultTtlSeconds { get; private set; } = 300;
    public int? VramTotalMb { get; private set; }
    public int? VramUsedMb { get; private set; }
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string Backend { get => _backend; private set => SetField(ref _backend, value); }
    public string PendingBackend { get => _pendingBackend; set => SetField(ref _pendingBackend, value); }
    public bool RestartRequired { get => _restartRequired; private set => SetField(ref _restartRequired, value); }
    public bool GpuAvailable { get => _gpuAvailable; private set => SetField(ref _gpuAvailable, value); }
    public bool CanSwitchBackend => !IsBusy && !string.Equals(Backend, PendingBackend, StringComparison.Ordinal);

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

    internal RecognitionSelectionSnapshot? RecognitionSelection =>
        Volatile.Read(ref _recognitionSelection);

    /// <summary>Durable maintenance operations driven by the staged selection.</summary>
    public RuntimeMaintenanceCoordinator Maintenance { get; }

    public async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        long generation = Interlocked.Increment(ref _generation);
        if (generation == Volatile.Read(ref _generation)) { IsBusy = true; Status = "正在读取模型驻留状态"; }
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
            try
            {
                RuntimeStatusSnapshot runtime = await _inference.GetRuntimeStatusAsync(cancellationToken);
                if (generation == Volatile.Read(ref _generation)) RuntimeStatus.ApplySnapshot(runtime);
            }
            catch (NotSupportedException)
            {
                // Older test doubles and pre-2.2 clients retain installer-local status.
            }
            await LoadSelectionSerializedAsync(forceReload: true, cancellationToken);
        }
        catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) Status = "已取消"; }
        catch (RuntimeSelectionException error) { if (generation == Volatile.Read(ref _generation)) Status = LocalizeSelection(error); }
        catch (InferenceClientException error) { if (generation == Volatile.Read(ref _generation)) Status = LocalizeV2(error.Code); }
        catch (Exception) when (generation == Volatile.Read(ref _generation)) { Status = "Supervisor 已断开，请重试"; }
        finally { if (generation == Volatile.Read(ref _generation)) IsBusy = false; }
    }

    public Task LoadSelectionAsync(CancellationToken cancellationToken) =>
        LoadSelectionSerializedAsync(forceReload: false, cancellationToken);

    /// <summary>
    /// Persist the per-kind source selection in Backend settings (the
    /// long-term source of truth); null id clears that kind's selection.
    /// </summary>
    public async Task SetSourceAsync(string kind, string? sourceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
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
            IReadOnlyList<string> next = ComposeSourceSelection(kind, sourceId);
            SettingsSnapshot updated = await _selection.ApplySourcePreferenceAsync(
                _inference,
                next.Count == 0 ? null : next,
                cancellationToken);
            _selectedSourceIds = updated.DownloadSourceIds ?? [];
            Sources = ProjectSources(_selection, _selectedSourceIds);
            Status = "已保存下载源偏好";
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

    /// <summary>Stage the accelerator for the pending feature selection.</summary>
    public void SetPendingAccelerator(string accelerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        PendingBackend = accelerator;
        Features = _selection is null
            ? []
            : ProjectFeatures(_selection, accelerator, []);
    }

    /// <summary>Toggle one optional feature for the pending accelerator (session state).</summary>
    public void SetFeatureEnabled(string featureId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        IReadOnlyList<SettingsFeatureOption> current = Features;
        if (!current.Any(item => item.FeatureId == featureId))
        {
            Status = $"未知功能 {featureId}";
            return;
        }
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
    public async Task InstallPendingAsync(CancellationToken cancellationToken)
    {
        if (_selection is null)
        {
            Status = "运行时目录尚未加载，请先刷新运行时";
            return;
        }
        try
        {
            await Maintenance.InstallAsync(
                _selection,
                PendingBackend,
                PendingFeatureIds,
                _selectedSourceIds,
                cancellationToken);
            Status = "运行时维护操作已完成";
            await LoadSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Status = "运行时维护操作已取消";
        }
        catch (RuntimeSelectionException error)
        {
            Status = LocalizeSelection(error);
        }
        catch (RuntimeInstallerException error)
        {
            Status = $"运行时维护失败：{error.Message}";
        }
        catch (InvalidOperationException error)
        {
            Status = error.Message;
        }
    }

    public void CancelMaintenance() => Maintenance.Cancel();

    public async Task RetryMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Maintenance.RetryAsync(cancellationToken);
            Status = "运行时维护操作已完成";
            await LoadSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (RuntimeInstallerException error)
        {
            Status = $"运行时维护重试失败：{error.Message}";
        }
        catch (InvalidOperationException error)
        {
            Status = error.Message;
        }
    }

    public void DetectGpu(bool available) { GpuAvailable = available; if (!available && PendingBackend == "nvidia_cuda") PendingBackend = "cpu"; }
    public void Cancel() { }

    private async Task LoadSelectionSerializedAsync(
        bool forceReload,
        CancellationToken cancellationToken)
    {
        await _selectionLoadGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceReload && RecognitionSelection is not null)
            {
                return;
            }
            await LoadSelectionCoreAsync(cancellationToken);
        }
        finally
        {
            _selectionLoadGate.Release();
        }
    }

    private async Task LoadSelectionCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            Wire.Health health = await _inference.GetHealthAsync(cancellationToken);
            RuntimeSelectionService selection = new(health);
            SettingsSnapshot settings = await _inference.GetSettingsAsync(cancellationToken);
            IReadOnlyList<string> selectedSourceIds = settings.DownloadSourceIds ?? [];

            // Commit one complete catalog snapshot only after every remote read
            // succeeds. Concurrent bootstrap/execution callers then observe the
            // same selection instead of a partially projected catalog.
            _selectedSourceIds = selectedSourceIds;
            Sources = ProjectSources(selection, selectedSourceIds);
            Features = ProjectFeatures(selection, PendingBackend, []);
            // Publish the catalog marker last. Readers that observe the new
            // selection can therefore also observe its matching projections;
            // command paths additionally await this same gate.
            _selection = selection;
            PublishRecognitionSelection(selection);
        }
        catch (NotSupportedException)
        {
            // Pre-2.7 clients do not expose health; selection stays unloaded.
        }
        catch (RuntimeSelectionException error)
        {
            Status = LocalizeSelection(error);
            throw;
        }
        catch (InferenceClientException error)
        {
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

    private void PublishRecognitionSelection(RuntimeSelectionService selection) => Volatile.Write(
            ref _recognitionSelection,
            new RecognitionSelectionSnapshot(selection));

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
        "huggingface" => "Hugging Face",
        "modelscope" => "ModelScope",
        _ => source.Id,
    };

    internal static string FeatureDisplayName(string featureId) => featureId switch
    {
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
