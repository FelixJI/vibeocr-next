using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Http = VibeOCR.Contracts.HttpV2;

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
/// Shared projection of the authenticated HTTP runtime status endpoint.
/// Views consume one stable model regardless of the transport available at
/// the current lifecycle phase.
/// </summary>
public sealed class RuntimeStatusViewModel : INotifyPropertyChanged
{
    private string _serviceStatus = "等待运行时检查";
    private string _profile = "正在识别运行时配置";
    private string _backendVersion = "未知";
    private string _status = "等待运行时检查";
    private string _sourceIdentity = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RuntimeComponentItem> Components { get; } = [];
    public string Profile { get => _profile; private set => SetField(ref _profile, value); }
    public string BackendVersion
    {
        get => _backendVersion;
        private set => SetField(ref _backendVersion, value);
    }
    public string ServiceStatus { get => _serviceStatus; private set => SetField(ref _serviceStatus, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string SourceIdentity
    {
        get => _sourceIdentity;
        private set => SetField(ref _sourceIdentity, value);
    }

    public void ReportServicePausedForMaintenance() =>
        ServiceStatus = "运行环境维护中，识别服务已暂停。";

    public void ReportServiceUnavailable()
    {
        ServiceStatus = "运行环境暂不可用，请重新检查或打开诊断与修复。";
        Status = ServiceStatus;
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

        ServiceStatus = snapshot.ServiceState switch
        {
            Http.RuntimeServiceState.Ready => "运行时已就绪",
            Http.RuntimeServiceState.Degraded => "运行时降级",
            _ => "运行时状态未知",
        };
        Status = ServiceStatus;
    }

    private void ReplaceComponents(IEnumerable<RuntimeComponentItem> components)
    {
        Components.Clear();
        foreach (RuntimeComponentItem component in components)
        {
            Components.Add(component);
        }
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

    private static string Accelerator(Http.RuntimeAccelerator accelerator) => accelerator switch
    {
        Http.RuntimeAccelerator.Cpu => "CPU",
        Http.RuntimeAccelerator.NvidiaCuda => "NVIDIA CUDA",
        _ => "未知",
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
