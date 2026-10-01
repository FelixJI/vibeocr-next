using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.ViewModels;

public enum SupervisorHealthState
{
    NotReady,
    Connecting,
    Ready,
    ProtocolIncompatible,
    Faulted,
}

public sealed record SupervisorHealth(
    SupervisorHealthState State,
    string? InstanceId,
    int? ProtocolVersion,
    string? Detail);

public sealed record StartupMilestone(string Name, double ElapsedMilliseconds);

public sealed partial class DiagnosticsViewModel : INotifyPropertyChanged
{
    private readonly Func<PrerequisiteStatus, CancellationToken, Task> _repair;
    private readonly Func<(string? InstanceId, IReadOnlyList<string> Lines)>? _deviceEvidence;
    private readonly object _milestoneLock = new();
    private readonly List<StartupMilestone> _milestones = [];
    private SupervisorHealth _supervisor = new(SupervisorHealthState.NotReady, null, null, null);
    private const int DeviceEvidenceMaxLines = 20;

    public DiagnosticsViewModel(
        string profile,
        PrerequisiteReport prerequisites,
        Func<PrerequisiteStatus, CancellationToken, Task>? repair = null,
        RuntimeStatusViewModel? runtimeStatus = null,
        Func<(string? InstanceId, IReadOnlyList<string> Lines)>? deviceEvidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        Profile = profile;
        Prerequisites = prerequisites?.Items ?? throw new ArgumentNullException(nameof(prerequisites));
        _repair = repair ?? (static (_, _) => Task.CompletedTask);
        RuntimeStatus = runtimeStatus ?? new RuntimeStatusViewModel();
        _deviceEvidence = deviceEvidence;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Profile { get; }

    public IReadOnlyList<PrerequisiteStatus> Prerequisites { get; }

    public RuntimeStatusViewModel RuntimeStatus { get; }

    public string AppVersion { get; } =
        typeof(DiagnosticsViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public string SupervisorInstanceId => _supervisor.InstanceId ?? "未知";

    /// <summary>
    /// 启动里程碑的线程安全快照：后台连接线程可并发写入（RecordMilestone），
    /// 桥接/导出读者每次拿到独立数组，永不与写入者共享可变集合。
    /// </summary>
    public IReadOnlyList<StartupMilestone> Milestones
    {
        get { lock (_milestoneLock) return [.. _milestones]; }
    }

    public string SupervisorStatus => _supervisor.State switch
    {
        SupervisorHealthState.NotReady => "未就绪",
        SupervisorHealthState.Connecting => "正在连接",
        SupervisorHealthState.Ready => "已就绪",
        SupervisorHealthState.ProtocolIncompatible => "协议不兼容",
        SupervisorHealthState.Faulted => "连接失败",
        _ => "未知",
    };

    /// <summary>当前 supervisor 健康记录（只读快照，供取证轨迹等消费）。</summary>
    public SupervisorHealth Supervisor => _supervisor;

    /// <summary>
    /// 当前 Paddle 实例的设备决策日志证据：仅当 provider 的当前实例与同一
    /// 健康快照的 InstanceId 一致且两者非空时，展示含 [Paddle worker] 且含
    /// [推理设备] 或 [GPU] 的真实日志的末尾最多 20 行（经 Redact 脱敏）。
    /// 这只是设备决策日志，不是实际执行设备的实测，也不是 actualDevice 真值；
    /// 实例不一致或无证据时为空，旧实例日志不泄漏。
    /// </summary>
    public IReadOnlyList<string> DeviceEvidence =>
        SelectDeviceEvidence(_supervisor, _deviceEvidence);

    public string ProtocolStatus => _supervisor.ProtocolVersion is int supervisorVersion
        ? $"客户端 v{ProtocolConstants.Version} / Supervisor v{supervisorVersion}"
        : $"客户端 v{ProtocolConstants.Version} / Supervisor 未知";

    public bool IsReady =>
        Prerequisites.All(item => item.IsInstalled) &&
        _supervisor.State == SupervisorHealthState.Ready &&
        _supervisor.ProtocolVersion == ProtocolConstants.Version;

    public void UpdateSupervisor(SupervisorHealth health)
    {
        _supervisor = health ?? throw new ArgumentNullException(nameof(health));
        OnPropertyChanged(nameof(SupervisorStatus));
        OnPropertyChanged(nameof(SupervisorInstanceId));
        OnPropertyChanged(nameof(ProtocolStatus));
        OnPropertyChanged(nameof(IsReady));
    }

    /// <summary>真实进程日志事件到达时通知绑定重读；仅广播 DeviceEvidence 的
    /// PropertyChanged，不引入计时器或轮询。</summary>
    public void NotifyDeviceEvidenceChanged() =>
        OnPropertyChanged(nameof(DeviceEvidence));

    public void RecordMilestone(string name, TimeSpan elapsed)
    {
        if (!StartupName().IsMatch(name))
        {
            throw new ArgumentException("Milestone must be T0 through T6.", nameof(name));
        }

        lock (_milestoneLock)
        {
            _milestones.RemoveAll(item => item.Name == name);
            _milestones.Add(new StartupMilestone(name, elapsed.TotalMilliseconds));
        }
    }

    public Task RepairAsync(PrerequisiteKind kind, CancellationToken cancellationToken)
    {
        PrerequisiteStatus item = Prerequisites.Single(status => status.Kind == kind);
        if (item.IsInstalled)
        {
            throw new InvalidOperationException($"{kind} does not require repair.");
        }

        return _repair(item, cancellationToken);
    }

    public async Task ExportAsync(string destination, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        // 健康与 provider 各读取一次，保证 supervisor 段与 device_evidence 段对应同一次快照。
        SupervisorHealth health = _supervisor;
        IReadOnlyList<string> deviceEvidence = SelectDeviceEvidence(health, _deviceEvidence);
        var document = new
        {
            schema_version = 2,
            profile = Profile,
            app_version = AppVersion,
            protocol_version = ProtocolConstants.Version,
            supervisor = new
            {
                state = health.State.ToString(),
                instance_id = health.InstanceId,
                protocol_version = health.ProtocolVersion,
                detail = Redact(health.Detail),
            },
            device_evidence = new
            {
                note = "Paddle 当前实例的设备决策日志（[Paddle worker] 设备相关行，已脱敏）；不等于作业成功的实测设备。",
                lines = deviceEvidence,
            },
            prerequisites = Prerequisites.Select(item => new
            {
                kind = item.Kind.ToString(),
                installed = item.IsInstalled,
                version = item.InstalledVersion,
                minimum = item.MinimumVersion,
            }),
            milestones = Milestones.OrderBy(item => item.Name),
        };
        string? directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(destination);
        await JsonSerializer.SerializeAsync(
            stream,
            document,
            new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true,
            },
            cancellationToken);
    }

    /// <summary>按 provider 当前实例与同一健康快照的一致性筛选设备决策日志；
    /// 实例缺失或不一致时返回空，避免泄漏旧实例日志。</summary>
    private static IReadOnlyList<string> SelectDeviceEvidence(
        SupervisorHealth health,
        Func<(string? InstanceId, IReadOnlyList<string> Lines)>? provider)
    {
        if (provider is null)
        {
            return [];
        }

        string supervisorInstance = health.InstanceId ?? "";
        if (supervisorInstance.Length == 0)
        {
            return [];
        }

        (string? instanceId, IReadOnlyList<string>? lines) = provider();
        if (string.IsNullOrEmpty(instanceId) ||
            !string.Equals(instanceId, supervisorInstance, StringComparison.Ordinal))
        {
            return [];
        }

        return lines is null
            ? []
            : lines
                .Where(line => !string.IsNullOrEmpty(line) &&
                    line.Contains("[Paddle worker]", StringComparison.Ordinal) &&
                    (line.Contains("[推理设备]", StringComparison.Ordinal) ||
                     line.Contains("[GPU]", StringComparison.Ordinal)))
                .TakeLast(DeviceEvidenceMaxLines)
                // bridge 总消息限制 64 KiB；20 条 × 400 字符即使全部 JSON 转义也有余量。
                .Select(line => Redact(line)!)
                .Select(line => line.Length > 400 ? line[..400] + "…" : line)
                .ToArray();
    }

    private static string? Redact(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string sanitized = Secret().Replace(value, "$1<redacted>");
        return WindowsPath().Replace(sanitized, "<redacted>");
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [GeneratedRegex("^T[0-6]$", RegexOptions.CultureInvariant)]
    private static partial Regex StartupName();

    [GeneratedRegex("(?i)(token\\s*[=:]\\s*)[^;\\s,\\\"]+")]
    private static partial Regex Secret();

    [GeneratedRegex("[A-Za-z]:\\\\[^;\\r\\n]+")]
    private static partial Regex WindowsPath();
}
