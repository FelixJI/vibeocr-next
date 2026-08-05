using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Http = VibeOCR.Contracts.HttpV2;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.App.ViewModels;

public sealed class RuntimeComponentItem : INotifyPropertyChanged
{
    private string _state;

    public RuntimeComponentItem(string componentId, string displayName, string? version, string state)
    {
        ComponentId = componentId;
        DisplayName = displayName;
        Version = version ?? "未知";
        _state = state;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string ComponentId { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public string State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }

    public void UpdateState(string state) => State = state;
}

/// <summary>
/// Shared projection of installer NDJSON events and the authenticated HTTP
/// runtime status endpoint. Views consume one stable model regardless of the
/// transport available at the current lifecycle phase.
/// </summary>
public sealed class RuntimeStatusViewModel : INotifyPropertyChanged
{
    private string _profile = "正在识别运行时配置";
    private string _backendVersion = "未知";
    private string _status = "等待运行时检查";
    private string _phase = "尚未开始";
    private string _progressText = "";
    private string _sourceIdentity = "";
    private double _progressValue;
    private bool _isProgressIndeterminate = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RuntimeComponentItem> Components { get; } = [];
    public string Profile { get => _profile; private set => SetField(ref _profile, value); }
    public string BackendVersion
    {
        get => _backendVersion;
        private set => SetField(ref _backendVersion, value);
    }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string Phase { get => _phase; private set => SetField(ref _phase, value); }
    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetField(ref _isProgressIndeterminate, value);
    }
    public string SourceIdentity
    {
        get => _sourceIdentity;
        private set => SetField(ref _sourceIdentity, value);
    }

    public void ApplyProfile(Host.RuntimeProfileDescriptor? profile)
    {
        if (profile is null) return;
        Profile = $"{profile.ProfileId} · {Accelerator(profile.Accelerator)}";
        ReplaceComponents(profile.Components.Select(component =>
            new RuntimeComponentItem(
                component.ComponentId,
                component.DisplayName,
                component.Version,
                "等待检查")));
    }

    public void ApplyMaintenance(Host.RuntimeMaintenanceEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Host.RuntimeMaintenanceSnapshot snapshot = update.Snapshot;
        Phase = PhaseText(snapshot.Phase);
        Status = OperationStateText(snapshot.OperationState);
        ApplyProgress(snapshot.Progress);

        if (snapshot.Phase == Host.RuntimeMaintenancePhase.VerifyRuntime)
        {
            foreach (RuntimeComponentItem component in Components)
            {
                component.UpdateState("正在验证");
            }
            return;
        }

        if (snapshot.ComponentId is not null)
        {
            RuntimeComponentItem? component = Components.FirstOrDefault(
                item => item.ComponentId == snapshot.ComponentId);
            component?.UpdateState(snapshot.OperationState switch
            {
                Host.RuntimeOperationState.Failed => "失败",
                Host.RuntimeOperationState.Cancelled => "已取消",
                _ => "正在安装",
            });
        }
    }

    public void ApplySnapshot(Http.RuntimeStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BackendVersion = snapshot.BackendVersion;
        SourceIdentity = SourceIdentityText(snapshot);
        Profile = $"{snapshot.Profile.ProfileId} · {Accelerator(snapshot.Profile.Accelerator)}";
        ReplaceComponents(snapshot.Profile.Components.Select(component =>
            new RuntimeComponentItem(
                component.ComponentId,
                component.DisplayName,
                ActualVersion(component) ?? component.Version,
                ComponentStateText(component))));

        Status = snapshot.ServiceState switch
        {
            Http.RuntimeServiceState.Ready => "运行时已就绪",
            Http.RuntimeServiceState.Degraded => "运行时降级",
            Http.RuntimeServiceState.Maintenance => "正在维护运行时",
            _ => "运行时状态未知",
        };
        if (snapshot.Maintenance is { } maintenance)
        {
            Phase = PhaseText(maintenance.Phase);
            ApplyProgress(maintenance.Progress);
        }
        else
        {
            Phase = "维护任务已完成";
            ProgressText = "";
            ProgressValue = 100;
            IsProgressIndeterminate = false;
        }
    }

    private void ReplaceComponents(IEnumerable<RuntimeComponentItem> components)
    {
        Components.Clear();
        foreach (RuntimeComponentItem component in components)
        {
            Components.Add(component);
        }
    }

    private void ApplyProgress(Host.ProgressSnapshot? progress)
    {
        if (progress is null)
        {
            ProgressText = "";
            IsProgressIndeterminate = true;
            return;
        }
        if (progress.Unit == Host.ProgressUnit.Steps || progress.Total is not > 0)
        {
            ProgressText = progress.Total is > 0
                ? $"{progress.Current} / {progress.Total.Value} 步"
                : $"已完成 {progress.Current} 步";
            IsProgressIndeterminate = true;
            return;
        }
        ProgressValue = Math.Clamp(progress.Current * 100d / progress.Total.Value, 0, 100);
        ProgressText = MeasuredProgressText(
            progress.Current,
            progress.Total.Value,
            progress.Unit == Host.ProgressUnit.Bytes ? "bytes" : "项",
            EstimatedRemainingSeconds(progress));
        IsProgressIndeterminate = false;
    }

    private void ApplyProgress(Http.ProgressSnapshot? progress)
    {
        if (progress is null)
        {
            ProgressText = "";
            IsProgressIndeterminate = true;
            return;
        }
        if (progress.Unit == Http.ProgressUnit.Steps || progress.Total is not > 0)
        {
            ProgressText = progress.Total is > 0
                ? $"{progress.Current} / {progress.Total.Value} 步"
                : $"已完成 {progress.Current} 步";
            IsProgressIndeterminate = true;
            return;
        }
        ProgressValue = Math.Clamp(progress.Current * 100d / progress.Total.Value, 0, 100);
        ProgressText = MeasuredProgressText(
            progress.Current,
            progress.Total.Value,
            progress.Unit == Http.ProgressUnit.Bytes ? "bytes" : "项",
            EstimatedRemainingSeconds(progress));
        IsProgressIndeterminate = false;
    }

    private static string MeasuredProgressText(
        long current,
        long total,
        string unit,
        double? estimatedRemainingSeconds)
    {
        string text = $"{current} / {total} {unit}";
        return estimatedRemainingSeconds is >= 0
            ? $"{text} · 预计剩余 {Math.Ceiling(estimatedRemainingSeconds.Value)} 秒"
            : text;
    }

    private static double? EstimatedRemainingSeconds(object progress)
    {
        object? value = progress.GetType()
            .GetProperty("EstimatedRemainingSeconds")?
            .GetValue(progress);
        return value is null ? null : Convert.ToDouble(value);
    }

    private static string? ActualVersion(Http.RuntimeComponentStatus component) =>
        component.GetType().GetProperty("ActualVersion")?.GetValue(component) as string;

    private static string SourceIdentityText(Http.RuntimeStatusSnapshot snapshot)
    {
        object? source = snapshot.GetType().GetProperty("Source")?.GetValue(snapshot);
        if (source is null) return "";
        string? sourceSha = source.GetType().GetProperty("BackendSourceSha")?.GetValue(source) as string;
        string? manifest = source.GetType().GetProperty("RuntimeManifestSha256")?.GetValue(source) as string;
        if (string.IsNullOrWhiteSpace(sourceSha) || string.IsNullOrWhiteSpace(manifest)) return "";
        return $"Source {sourceSha[..Math.Min(12, sourceSha.Length)]} · " +
            $"manifest {manifest[..Math.Min(12, manifest.Length)]}";
    }

    private static string Accelerator(Host.Accelerator accelerator) => accelerator switch
    {
        Host.Accelerator.Cpu => "CPU",
        Host.Accelerator.NvidiaCuda => "NVIDIA CUDA",
        _ => "未知",
    };

    private static string Accelerator(Http.RuntimeAccelerator accelerator) => accelerator switch
    {
        Http.RuntimeAccelerator.Cpu => "CPU",
        Http.RuntimeAccelerator.NvidiaCuda => "NVIDIA CUDA",
        _ => "未知",
    };

    private static string OperationStateText(Host.RuntimeOperationState state) => state switch
    {
        Host.RuntimeOperationState.Queued => "维护任务排队中",
        Host.RuntimeOperationState.Running => "正在准备 Backend 运行时",
        Host.RuntimeOperationState.Succeeded => "维护操作已完成",
        Host.RuntimeOperationState.Failed => "运行时安装失败",
        Host.RuntimeOperationState.Cancelled => "运行时安装已取消",
        _ => "运行时状态未知",
    };

    private static string PhaseText(Host.RuntimeMaintenancePhase phase) => phase switch
    {
        Host.RuntimeMaintenancePhase.ValidateBinding => "验证正式组件绑定",
        Host.RuntimeMaintenancePhase.WaitForLock => "等待安装锁",
        Host.RuntimeMaintenancePhase.PrepareRuntime => "准备 Python 运行时",
        Host.RuntimeMaintenancePhase.InstallProfile => "安装重依赖",
        Host.RuntimeMaintenancePhase.InstallBackend => "安装 Backend",
        Host.RuntimeMaintenancePhase.VerifyRuntime => "验证运行时",
        Host.RuntimeMaintenancePhase.CommitRuntime => "提交运行时切换",
        _ => "处理运行时",
    };

    private static string PhaseText(Http.RuntimeMaintenancePhase phase) => phase switch
    {
        Http.RuntimeMaintenancePhase.ValidateBinding => "验证正式组件绑定",
        Http.RuntimeMaintenancePhase.WaitForLock => "等待安装锁",
        Http.RuntimeMaintenancePhase.PrepareRuntime => "准备 Python 运行时",
        Http.RuntimeMaintenancePhase.InstallProfile => "安装重依赖",
        Http.RuntimeMaintenancePhase.InstallBackend => "安装 Backend",
        Http.RuntimeMaintenancePhase.VerifyRuntime => "验证运行时",
        Http.RuntimeMaintenancePhase.CommitRuntime => "提交运行时切换",
        _ => "处理运行时",
    };

    private static string ComponentStateText(Http.RuntimeComponentState state) => state switch
    {
        Http.RuntimeComponentState.NotRequired => "无需安装",
        Http.RuntimeComponentState.Pending => "等待安装",
        Http.RuntimeComponentState.Installing => "正在安装",
        Http.RuntimeComponentState.Verifying => "正在验证",
        Http.RuntimeComponentState.Ready => "已就绪",
        Http.RuntimeComponentState.Failed => "失败",
        Http.RuntimeComponentState.Cancelled => "已取消",
        _ => "未知",
    };

    private static string ComponentStateText(Http.RuntimeComponentStatus component)
    {
        string state = ComponentStateText(component.State);
        object? actual = component.GetType().GetProperty("ActualState")?.GetValue(component);
        object? drift = component.GetType().GetProperty("DriftReason")?.GetValue(component);
        string? actualName = actual?.ToString();
        string? driftName = drift?.ToString();
        if (component.State == Http.RuntimeComponentState.Ready)
        {
            state = actualName switch
            {
                "Missing" or "missing" => "缺失",
                "Drifted" or "drifted" => "已漂移",
                "Unknown" or "unknown" => "实际状态未知",
                _ => state,
            };
        }
        string? driftText = driftName switch
        {
            null or "None" or "none" => null,
            "Missing" or "missing" => "文件缺失",
            "VersionMismatch" or "version_mismatch" => "版本不一致",
            "IdentityMismatch" or "identity_mismatch" => "来源身份不一致",
            "IntegrityFailed" or "integrity_failed" => "完整性校验失败",
            "Unexpected" or "unexpected" => "存在非预期组件",
            _ => "状态不一致",
        };
        return driftText is null ? state : $"{state} · {driftText}";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
