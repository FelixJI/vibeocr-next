using System.Text.Json;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.Platform.Bootstrap;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.App.Features.Settings;

/// <summary>UI projection of the durable maintenance operation.</summary>
public sealed record RuntimeMaintenanceState(
    bool IsRunning,
    string StatusCode,
    string? OperationId,
    IReadOnlyList<string> RequestedComponentIds,
    IReadOnlyList<string> EffectiveComponentIds,
    IReadOnlyList<string> RequestedSourceIds,
    IReadOnlyList<string> EffectiveSourceIds,
    bool CanCancel,
    bool CanRetry,
    string FailureReason = "",
    string FailureCode = "",
    string Accelerator = "cpu")
{
    public static RuntimeMaintenanceState Idle { get; } = new(
        false,
        "idle",
        null,
        [],
        [],
        [],
        [],
        CanCancel: false,
        CanRetry: false);

    public static RuntimeMaintenanceState Unavailable { get; } = new(
        false,
        "unavailable",
        null,
        [],
        [],
        [],
        [],
        CanCancel: false,
        CanRetry: false);
}

/// <summary>
/// Orchestrates durable runtime maintenance operations on top of the existing
/// <see cref="IRuntimeInstallerClient"/>: explicit-intent ensure (including
/// base-only), cancellation through the durable v2 flow, and retry that
/// reuses the source operation's normalized intent unless a new install is
/// started. Requested/effective component ids from Backend snapshots are the
/// installation truth, including requested/effective source ids echoed by the
/// Runtime maintenance snapshot.
/// </summary>
public sealed class RuntimeMaintenanceCoordinator
{
    private readonly Func<IRuntimeInstallerClient> _installer;
    private readonly RuntimeStatusViewModel _runtimeStatus;
    private readonly ProductMaintenanceCoordinator _productMaintenance;
    private readonly string? _operationStorePath;
    private Host.RuntimeMaintenanceEvent? _lastUpdate;
    private bool _restored;
    private long _previewGeneration;
    private long _lastSequence = -1;
    private readonly Func<CancellationToken, Task>? _stopService;
    private readonly Func<Task>? _restoreService;
    private CancellationTokenSource? _active;
    private RuntimeMaintenanceState _state = RuntimeMaintenanceState.Idle;

    public RuntimeMaintenanceCoordinator(
        Func<IRuntimeInstallerClient> installer,
        RuntimeStatusViewModel runtimeStatus,
        ProductMaintenanceCoordinator? productMaintenance = null,
        Func<CancellationToken, Task>? stopService = null,
        Func<Task>? restoreService = null,
        string? operationStorePath = null)
    {
        _operationStorePath = operationStorePath;
        _stopService = stopService;
        _restoreService = restoreService;
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _runtimeStatus = runtimeStatus ?? throw new ArgumentNullException(nameof(runtimeStatus));
        _productMaintenance = productMaintenance ?? new ProductMaintenanceCoordinator();
    }

    private sealed record StoredOperation(RuntimeMaintenanceState State, Host.RuntimeMaintenanceEvent? LastUpdate);

    private bool NeedsRecovery => _state.IsRunning || (_state.StatusCode == "unknown" && _state.OperationId is not null);

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (_active is not null || (_restored && !NeedsRecovery)) return;
        bool loadStored = !_restored;
        _restored = true;
        if (_operationStorePath is null || !File.Exists(_operationStorePath)) return;
        try
        {
            StoredOperation stored = JsonSerializer.Deserialize<StoredOperation>(File.ReadAllText(_operationStorePath))
                ?? throw new InvalidDataException("运行环境维护记录为空。");
            if (stored.State is null || string.IsNullOrWhiteSpace(stored.State.OperationId) ||
                (stored.LastUpdate is not null &&
                    stored.LastUpdate.Snapshot.OperationId != stored.State.OperationId))
                throw new JsonException("维护记录缺少操作标识或与快照不一致。");
            if (loadStored)
            {
                _state = stored.State;
                _lastUpdate = stored.LastUpdate;
                _lastSequence = _lastUpdate?.Snapshot.Sequence ?? -1;
            }
            if (_state.OperationId is not { } operationId) return;
            _runtimeStatus.BeginMaintenance(operationId);
            if (_lastUpdate is not null) _runtimeStatus.ApplyMaintenance(_lastUpdate);
            if (!NeedsRecovery)
            {
                _runtimeStatus.CompleteMaintenance(_state.StatusCode);
                StateChanged?.Invoke();
                return;
            }
            RuntimeMaintenanceObserveEnvelope page = await _installer().ObserveAsync(operationId, 0,
                cancellationToken: cancellationToken);
            ApplyRecoveredSnapshot(page.Snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or RuntimeInstallerException)
        {
            SetState(_state with { IsRunning = false, StatusCode = "unknown", CanCancel = false, CanRetry = false,
                FailureReason = "无法核实上次维护结果，请重新检查状态后预览安装范围。", FailureCode = "maintenance.recovery_unavailable" });
            _runtimeStatus.CompleteMaintenance("unknown");
        }
    }

    private void ApplyRecoveredSnapshot(Host.RuntimeMaintenanceSnapshot snapshot)
    {
        ApplyEvent(new Host.RuntimeMaintenanceEvent
        {
            ProtocolVersion = 2, EventVersion = 1, EventType = Host.RuntimeMaintenanceEventType.Snapshot,
            Operation = snapshot.Operation, Snapshot = snapshot, MessageCode = "runtime.recovered",
        });
        bool running = snapshot.OperationState is Host.RuntimeOperationState.Running or Host.RuntimeOperationState.Queued;
        bool retryable = snapshot.OperationState is Host.RuntimeOperationState.Failed or Host.RuntimeOperationState.Cancelled;
        SetState(_state with { IsRunning = running, StatusCode = snapshot.OperationState.ToString().ToLowerInvariant(),
            CanCancel = false, CanRetry = retryable, FailureReason = "", FailureCode = "" });
        if (!running) _runtimeStatus.CompleteMaintenance(_state.StatusCode);
    }

    public RuntimeMaintenanceState State => _state;
    public Host.RuntimeInstallPlan? Plan { get; private set; }
    public bool SupportsInstallPlan
    {
        get
        {
            try { return _installer().SupportsInstallPlan; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task PreviewAsync(
        RuntimeSelectionService selection, string accelerator,
        IReadOnlyCollection<string> featureIds, IReadOnlyCollection<string> sourceIds,
        CancellationToken cancellationToken)
    {
        if (_state.IsRunning) throw new InvalidOperationException("运行环境维护尚未结束。");
        long generation = Interlocked.Increment(ref _previewGeneration);
        Plan = null;
        StateChanged?.Invoke();
        Host.RuntimeInstallPlan plan = await _installer().PreviewInstallAsync(new RuntimeInstallSelection
        {
            InstallComponentIds = selection.SelectComponentIds(accelerator, featureIds),
            DownloadSourceIds = sourceIds.Count > 0 ? [.. sourceIds] : null,
        }, accelerator, cancellationToken);
        if (generation != Volatile.Read(ref _previewGeneration) || _state.IsRunning) return;
        Plan = plan;
        StateChanged?.Invoke();
    }

    public void DiscardPlan()
    {
        Interlocked.Increment(ref _previewGeneration);
        Plan = null;
        StateChanged?.Invoke();
    }


    public event Action? StateChanged;

    /// <summary>
    /// Start an ensure operation with an explicit normalized intent. Empty
    /// feature selection sends an empty install list (base only); null/empty
    /// sources delegate to the Backend settings default.
    /// </summary>
    public async Task ConfirmAsync(string planId, CancellationToken cancellationToken)
    {
        Host.RuntimeInstallPlan plan = Plan
            ?? throw new InvalidOperationException("请先预览安装计划。");
        if (_state.IsRunning || plan.PlanId != planId)
            throw new InvalidOperationException("安装计划已更改，请重新预览。");
        if (!DateTimeOffset.TryParse(plan.ExpiresAt, out DateTimeOffset expiry) || expiry <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("安装计划已过期，请重新预览。");
        if (plan.Blockers.Count > 0)
            throw new InvalidOperationException("请先处理安装计划中的阻碍。");
        IReadOnlyList<string> componentIds = plan.RequestedComponentIds ?? [];
        var intent = new RuntimeInstallSelection
        {
            InstallComponentIds = componentIds,
            DownloadSourceIds = plan.RequestedDownloadSourceIds,
        };
        string operationId = $"ui-{Guid.NewGuid():N}";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable productLease = _productMaintenance.Acquire(
            ProductMaintenanceOwner.RuntimeMaintenance,
            linked.Cancel);
        _runtimeStatus.BeginMaintenance(operationId);
        _lastSequence = -1;
        _lastUpdate = null;
        _active = linked;
        DiscardPlan();
        try
        {
        SetState(new RuntimeMaintenanceState(
            true,
            "running",
            operationId,
            componentIds,
            plan.EffectiveComponentIds,
            intent.DownloadSourceIds ?? [],
            plan.EffectiveDownloadSourceIds,
            CanCancel: true,
            CanRetry: false,
            Accelerator: plan.Accelerator == Host.Accelerator.Cpu ? "cpu" : "nvidia_cuda"));
        IProgress<Host.RuntimeMaintenanceEvent> progress =
            new SynchronousProgress(ApplyEvent);
            if (_stopService is not null) await _stopService(linked.Token);
            IRuntimeInstallerClient installer = _installer();
            await installer.ConfirmInstallAsync(
                plan.PlanId,
                operationId,
                progress,
                linked.Token).ConfigureAwait(false);
            ApplySources(installer.LastMaintenanceSources);
            SetState(new RuntimeMaintenanceState(
                false,
                "succeeded",
                operationId,
                _state.RequestedComponentIds,
                _state.EffectiveComponentIds,
                _state.RequestedSourceIds,
                _state.EffectiveSourceIds,
                CanCancel: false,
                CanRetry: false, Accelerator: _state.Accelerator));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            SetState(new RuntimeMaintenanceState(
                false,
                "cancelled",
                operationId,
                _state.RequestedComponentIds,
                _state.EffectiveComponentIds,
                _state.RequestedSourceIds,
                _state.EffectiveSourceIds,
                CanCancel: false,
                CanRetry: true, Accelerator: _state.Accelerator));
            throw;
        }
        catch (RuntimeInstallerException error)
        {
            SetState(new RuntimeMaintenanceState(
                false,
                "failed",
                operationId,
                _state.RequestedComponentIds,
                _state.EffectiveComponentIds,
                _state.RequestedSourceIds,
                _state.EffectiveSourceIds,
                CanCancel: false,
                CanRetry: true, Accelerator: _state.Accelerator));
            SetState(_state with { FailureReason = FailureReason(error),
                FailureCode = error.CanonicalCode ?? "runtime.install_failed" });
            throw;
        }
        catch (Exception)
        {
            SetState(_state with { IsRunning = false, StatusCode = "failed", CanCancel = false, CanRetry = true });
            throw;
        }
        finally
        {
            try { if (_restoreService is not null) await _restoreService(); }
            finally
            {
                Interlocked.CompareExchange(ref _active, null, linked);
                SetState(_state with { IsRunning = false, CanCancel = false });
            }
        }
    }

    /// <summary>Cancel the running operation; the client performs the durable cancel.</summary>
    public void Cancel() => _active?.Cancel();

    /// <summary>
    /// Retry the last failed/cancelled operation without a new selection; the
    /// omitted selection reuses the source operation's normalized intent.
    /// </summary>
    public async Task RetryAsync(CancellationToken cancellationToken)
    {
        if (_state.IsRunning || !_state.CanRetry)
            throw new InvalidOperationException("没有可重试的运行环境维护操作。");
        // Re-resolve the failed intent; a new plan must be explicitly confirmed.
        long generation = Interlocked.Increment(ref _previewGeneration);
        Plan = null;
        Host.RuntimeInstallPlan plan = await _installer().PreviewInstallAsync(new RuntimeInstallSelection
        {
            InstallComponentIds = _state.RequestedComponentIds,
            DownloadSourceIds = _state.RequestedSourceIds.Count > 0 ? _state.RequestedSourceIds : null,
        }, _state.Accelerator, cancellationToken);
        if (generation != Volatile.Read(ref _previewGeneration) || _state.IsRunning) return;
        Plan = plan;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Applies events inline: operation state must reflect every snapshot
    /// before the installer call returns, independent of any sync context.
    /// </summary>
    private sealed class SynchronousProgress(
        Action<Host.RuntimeMaintenanceEvent> handler) : IProgress<Host.RuntimeMaintenanceEvent>
    {
        public void Report(Host.RuntimeMaintenanceEvent value) => handler(value);
    }

    private void ApplyEvent(Host.RuntimeMaintenanceEvent update)
    {
        Host.RuntimeMaintenanceSnapshot snapshot = update.Snapshot;
        if (snapshot.OperationId != _state.OperationId || snapshot.Sequence <= _lastSequence) return;
        _lastSequence = snapshot.Sequence;
        _lastUpdate = update;
        _runtimeStatus.ApplyMaintenance(update);
        SetState(_state with
        {
            RequestedComponentIds =
                snapshot.RequestedComponentIds ?? _state.RequestedComponentIds,
            EffectiveComponentIds =
                snapshot.EffectiveComponentIds ?? _state.EffectiveComponentIds,
        });
    }

    private void SetState(RuntimeMaintenanceState value)
    {
        _state = _active is null ? value : value with { IsRunning = true };
        if (_operationStorePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_operationStorePath)!);
                string temporary = _operationStorePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new StoredOperation(value, _lastUpdate)));
                File.Move(temporary, _operationStorePath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _state = _state with { FailureReason = "维护记录无法保存；关闭应用前请核实本次结果并导出诊断。" };
            }
        }
        if (!value.IsRunning && value.OperationId is not null)
            _runtimeStatus.CompleteMaintenance(value.StatusCode);
        StateChanged?.Invoke();
    }

    private static string FailureReason(RuntimeInstallerException error)
    {
        string? reason = error.Detail is not null && error.Detail.TryGetValue("reason_code", out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return reason switch
        {
            "disk_full" => "磁盘空间不足，请释放空间后重新预览。",
            "permission_denied" => "无法写入运行环境目录，请检查目录权限。",
            "network_error" or "download_failed" => "下载失败，请检查网络或调整下载来源后重新预览。",
            _ => "安装未成功完成。请重新检查运行环境、调整下载来源或导出诊断，再预览重试。",
        };
    }

    private void ApplySources(RuntimeMaintenanceSourceSnapshot? sources)
    {
        if (sources is null) return;
        SetState(_state with
        {
            RequestedSourceIds = sources.RequestedSourceIds,
            EffectiveSourceIds = sources.EffectiveSourceIds,
        });
    }
}
