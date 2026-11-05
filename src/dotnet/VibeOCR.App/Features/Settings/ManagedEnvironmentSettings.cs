using VibeOCR.Platform.Bootstrap;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Services;
using VibeOCR.Platform.Inference;
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.App.Features.Settings;

/// <summary>Settings projection of the frozen Runtime manager's named environments.</summary>
public sealed class ManagedEnvironmentSettings(
    IManagedEnvironmentClient manager,
    Func<string, CancellationToken, Task> activate,
    Func<(string Id, int Revision)?> runningSession,
    ProductMaintenanceCoordinator productMaintenance,
    Func<ManagedEnvironmentSession?>? currentSession = null,
    Func<string, string?, CancellationToken, Task>? activateDevice = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? activeCleanup;

    public bool SupportsCleanup => manager.SupportsEnvironmentCleanup;
    public ManagedCleanupPlan? CleanupPlan { get; private set; }
    public ManagedCleanupResult? CleanupResult { get; private set; }
    public const int CleanupPageSize = 3;
    public int CleanupPage { get; private set; }
    public int CleanupPageCount => Math.Max(1, (Math.Max(CleanupPlan?.Items.Count ?? 0, CleanupResult?.Items.Count ?? 0) + CleanupPageSize - 1) / CleanupPageSize);
    public ManagedCleanupPlan? CleanupPlanPage => CleanupPlan is { } plan ? plan with
    {
        Items = plan.Items.Skip(CleanupPage * CleanupPageSize).Take(CleanupPageSize).Select(item => item with
        {
      Reason = CleanupSummary(item.Reason),
      LastError = item.LastError is null ? null : CleanupSummary(item.LastError),
            Paths = item.Paths.Take(1).Select(CleanupSummary).ToArray(),
        }).ToArray(),
        LastResult = null,
    } : null;
    public ManagedCleanupResult? CleanupResultPage => CleanupResult is { } result ? result with
    {
        Items = result.Items.Skip(CleanupPage * CleanupPageSize).Take(CleanupPageSize)
            .Select(item => item with { Detail = CleanupSummary(item.Detail) }).ToArray(),
    } : null;
    // Only optional display text is shortened; Runtime retains exact paths and the full plan.
    private static string CleanupSummary(string value) => value.Length <= 200 ? value :
        value[..(char.IsHighSurrogate(value[199]) ? 199 : 200)] + "…";
    public Task SetCleanupPageAsync(int page, CancellationToken cancellationToken) => RunAsync(() =>
    {
        if (page < 0 || page >= CleanupPageCount) throw new InvalidOperationException("清理页码已失效，请重新检查。");
        CleanupPage = page;
        return Task.CompletedTask;
    }, cancellationToken);
    public bool CanCancelCleanup => Volatile.Read(ref activeCleanup) is not null;

    public Task PreviewCleanupAsync(CancellationToken cancellationToken) => RunAsync(async () =>
    {
        if (!SupportsCleanup) throw new NotSupportedException("当前 Runtime 不支持空间清理。");
        if (CanCancelCleanup) throw new InvalidOperationException("请等待当前操作结束后再检查清理项。");
        ManagedCleanupPlan preview = await manager.PreviewEnvironmentCleanupAsync(cancellationToken);
        CleanupPage = 0;
        CleanupPlan = preview;
        CleanupResult = preview.LastResult;
        Status = "已检查清理影响，请选择需要移除的项目并确认。";
    }, cancellationToken);

    public Task CleanupAsync(string planId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ManagedCleanupPlan plan = CleanupPlanPage ?? throw new InvalidOperationException("请先检查可清理项。");
        if (!SupportsCleanup || plan.PlanId != planId || itemIds.Count == 0 || itemIds.Distinct(StringComparer.Ordinal).Count() != itemIds.Count ||
            itemIds.Any(id => !plan.Items.Any(item => item.Id == id && item.CanClean)))
            throw new InvalidOperationException("清理计划已变化或包含受保护项目，请重新检查。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable lease = productMaintenance.Acquire(ProductMaintenanceOwner.RuntimeMaintenance, linked.Cancel);
        Volatile.Write(ref activeCleanup, linked);
        CleanupResult = null;
        StateChanged?.Invoke();
        try
        {
            CleanupResult = await manager.RunEnvironmentCleanupAsync(planId, itemIds, linked.Token);
            Status = CleanupResult.Items.All(item => item.State == "deleted")
                ? "所选路径已移除；模型和受保护资源保留。"
                : "清理部分完成；失败或取消项目可重新检查并继续。";
        }
        catch (OperationCanceledException)
        {
            Status = "清理已中止；请重新检查待清理记录，已删除文件不会恢复。";
        }
        finally
        {
            Volatile.Write(ref activeCleanup, null);
            CleanupPlan = null;
            CleanupPage = 0;
            await ReloadEnvironmentsAsync(CancellationToken.None, strictEvidence: false);
        }
    }, cancellationToken);

    public void CancelCleanup()
    {
        try { Volatile.Read(ref activeCleanup)?.Cancel(); }
        catch (ObjectDisposedException) { return; }
        Status = "正在取消清理；待清理记录会保留。";
        StateChanged?.Invoke();
    }

    private const string RunningEvidenceUnavailableMessage =
        "无法读取活动环境状态，请刷新或重新切换环境。";

    public event Action? StateChanged;
    public ManagedEnvironmentList? Snapshot { get; private set; }
    /// <summary>清单快照是否已成功读取过（含启动初始化中的中间快照）；
    /// 是界面加载判定的权威事实，与 Status 显示文案无关。</summary>
    public bool SnapshotLoaded => Snapshot is not null;
    /// <summary>清单读取是否已尝试（无论成败）：界面据此只在首次进入时自动
    /// 读一次；失败后交还显式重试，不自动循环，也不随文案变化失效。</summary>
    public bool ListFetchAttempted => Volatile.Read(ref listFetchAttempted) != 0;
    private int listFetchAttempted;
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "尚未读取运行环境";

    public bool IsRunning(ManagedEnvironment environment) =>
        runningSession() is { } session && session.Id == environment.Id &&
        session.Revision == environment.Revision;

    public Task RefreshAsync(CancellationToken cancellationToken) => RunAsync(async () =>
    {
        Volatile.Write(ref listFetchAttempted, 1);
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: true);
        Status = Snapshot?.ActiveId is null
            ? "尚未启动运行环境；请选择识别组件并准备配置。"
            : "运行环境状态已更新。";
    }, cancellationToken);

    /// <summary>保存全局默认或单环境 override 来源偏好：只写配置，不下载/不重启。</summary>
    public Task SetSourcesAsync(
        string? environmentId, string? modelSourceId,
        CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ManagedEnvironmentList updated = await manager.SetEnvironmentSourcesAsync(
            environmentId, modelSourceId, cancellationToken);
        Snapshot = updated;
        await ApplyRunningEvidenceAsync(cancellationToken);
        // 全局保存（environmentId=null）也会改变运行中环境的模型来源解析，
        // 只要有活动会话就如实提示下次启动生效。
        bool targetRunning = currentSession?.Invoke() is { } session &&
            (environmentId is null || session.EnvironmentId == environmentId);
        // 单产品语义：来源仅用于下载模型；全局保存由 Runtime 原子清除各环境
        // 旧 override，界面所示即实际解析值。
        string scope = environmentId is null ? "下载来源已保存并统一所有环境" : "该环境的来源已保存";
        Status = $"{scope}；仅用于模型下载，不会改变当前运行中的服务。";
        // 提交了模型来源且目标在运行才提示下次启动生效；纯清除不叠加。
        if (targetRunning && modelSourceId is not null)
            Status += "模型来源将在该环境下次启动时生效。";
    }, cancellationToken);

    public Task SetSourcesAsync(
        string? environmentId, string? paddleocrModelSourceId,
        string? mineruModelSourceId,
        CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ManagedEnvironmentList updated = await manager.SetEnvironmentSourcesAsync(
            environmentId, paddleocrModelSourceId, mineruModelSourceId, cancellationToken);
        Snapshot = updated;
        await ApplyRunningEvidenceAsync(cancellationToken);
        // 全局保存（environmentId=null）也会改变运行中环境的模型来源解析，
        // 只要有活动会话就如实提示下次启动生效。
        bool targetRunning = currentSession?.Invoke() is { } session &&
            (environmentId is null || session.EnvironmentId == environmentId);
        // 单产品语义：来源仅用于下载模型；全局保存由 Runtime 原子清除各环境
        // 旧 override，界面所示即实际解析值。
        string scope = environmentId is null ? "下载来源已保存并统一所有环境" : "该环境的来源已保存";
        Status = $"{scope}；仅用于模型下载，不会改变当前运行中的服务。";
        // 提交了任一模型来源且目标在运行才提示下次启动生效；纯清除不叠加。
        if (targetRunning && (paddleocrModelSourceId is not null || mineruModelSourceId is not null))
            Status += "模型来源将在该环境下次启动时生效。";
    }, cancellationToken);

    /// <summary>
    /// 后台编排步骤失败时写入用户可见状态并触发投影：供不在 RunAsync
    /// 状态机内的编排步骤（远程模式宿主入口）报告根因，避免被后台
    /// 追踪仅记录日志而呈现中性成功。
    /// </summary>
    public void ReportFailure(string status)
    {
        Status = status;
        StateChanged?.Invoke();
    }

    public Task SwitchAsync(string environmentId, CancellationToken cancellationToken, string? device = null) => RunAsync(async () =>
    {
        if (device is not null)
        {
            if (activateDevice is null) throw new NotSupportedException("当前宿主不支持独立设备选择。");
            await activateDevice(environmentId, device, cancellationToken);
        }
        else await activate(environmentId, cancellationToken);
        try
        {
            await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: true);
            Status = Snapshot?.ActiveId == environmentId
                ? "环境已切换。模型与引擎状态以目标服务检查结果为准。"
                : "环境已切换；清单尚未反映目标环境，请刷新核实。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "环境已切换；后续清单刷新已取消，请稍后刷新。";
        }
        catch (Exception error)
        {
            Status = $"环境已切换；后续清单刷新失败：{error.Message}";
        }
    }, cancellationToken);

    private async Task ApplyRunningEvidenceAsync(CancellationToken cancellationToken)
    {
        ManagedEnvironmentSession? session = currentSession?.Invoke();
        ManagedEnvironmentList? snapshot = Snapshot;
        if (session is null || snapshot is null || snapshot.ActiveId != session.EnvironmentId)
            return;
        ManagedEnvironment? current = snapshot.Environments.SingleOrDefault(item =>
            item.Id == session.EnvironmentId && item.Revision == session.Revision);
        if (current is null) return;
        try
        {
            Wire.Health health = await session.Client.GetHealthAsync(cancellationToken);
            ResidencyStatus residency = await session.Client.GetResidencyAsync(cancellationToken);
            RuntimeStatusSnapshot status = await session.Client.GetRuntimeStatusAsync(cancellationToken);
            if (currentSession?.Invoke() != session || Snapshot != snapshot) return;
            if (!health.Ready || health.Draining ||
                health.InstanceId != session.Process.Ready.InstanceId ||
                status.InstanceId != session.Process.Ready.InstanceId) return;
            ManagedEnvironment observed = ProjectRunning(current, health, residency, status);
      Snapshot = snapshot with
      {
        Environments = [.. snapshot.Environments.Select(item =>
                item.Id == current.Id ? observed : item)]
      };
        }
        catch (Exception error) when (error is HttpRequestException or InferenceClientException or VibeOCR.Runtime.Client.RuntimeClientException)
        {
            throw new InvalidOperationException(RunningEvidenceUnavailableMessage, error);
        }
    }

    // 列表刷新统一叠加运行证据；变更成功后的探针失败标注为未核验。
    private async Task ReloadEnvironmentsAsync(CancellationToken cancellationToken, bool strictEvidence)
    {
        Snapshot = await manager.ListEnvironmentsAsync(cancellationToken);
        try
        {
            await ApplyRunningEvidenceAsync(cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            // 变更已成功；运行证据不可读时如实标注，不得伪造 ready，也不得
            // 静默回退冻结管理器的 not_started 投影。
            MarkRunningEvidenceUnverified(error);
            if (strictEvidence) throw;
        }
    }

    private void MarkRunningEvidenceUnverified(Exception error)
    {
        AppLog.Warn($"Managed environment running evidence unavailable: {error.Message}");
        ManagedEnvironmentSession? session = currentSession?.Invoke();
        ManagedEnvironmentList? snapshot = Snapshot;
        if (session is null || snapshot is null || snapshot.ActiveId != session.EnvironmentId)
            return;
        ManagedEnvironment? current = snapshot.Environments.SingleOrDefault(item =>
            item.Id == session.EnvironmentId && item.Revision == session.Revision);
        if (current is null) return;
    Snapshot = snapshot with
    {
      Environments = [.. snapshot.Environments.Select(item =>
            item.Id == current.Id
                ? item with
                {
                    EngineState = "unverified",
                    ModelState = "not_checked",
                    ServiceState = "unverified",
                    Reason = RunningEvidenceUnavailableMessage,
                }
                : item)]
    };
    }

    internal static ManagedEnvironment ProjectRunning(
        ManagedEnvironment environment, Wire.Health health,
        ResidencyStatus residency, RuntimeStatusSnapshot status)
    {
        var catalog = new RuntimeSelectionService(health);
        bool supportedEngine = environment.Engine is "paddle" or "mineru";
        string[] modeIds = environment.Engine switch
        {
            "paddle" => ["paddle_text", "paddle_table", "paddle_structure"],
            "mineru" => ["mineru_document"],
            _ => [],
        };
        RecognitionModeOption[] modes = !supportedEngine ? [] : [.. catalog.RecognitionModes.Where(mode =>
            modeIds.Contains(mode.Id, StringComparer.Ordinal))];
        string engineState = !supportedEngine ? environment.EngineState
            : modes.Any(mode => mode.Availability == "ready") ? "ready"
            : modes.Any(mode => mode.Availability == "preparation_required") ? "preparation_required"
            : modes.Length > 0 ? "unavailable" : "unverified";
        if (supportedEngine && modes.Length == 0 && !catalog.SupportsRecognitionModes)
        {
            OcrEngine? engine = environment.Engine == "paddle"
                ? OcrEngine.PaddleOcr : null;
            RuntimeEngineOption? option = catalog.EngineOptions.FirstOrDefault(item => item.Engine == engine);
            if (option is not null)
                engineState = option.Availability switch
                {
                    Wire.OcrEngineAvailability.Ready => "ready",
                    Wire.OcrEngineAvailability.PreparationRequired => "preparation_required",
                    _ => "unavailable",
                };
        }
        bool hasPaddle = environment.Engine == "paddle";
        bool hasMineru = environment.Engine == "mineru";
        Wire.MineruConfigCatalog? mineru = health.CapabilityDescriptors?
            .Select(descriptor => descriptor.MineruConfigCatalog)
            .FirstOrDefault(catalog => catalog is not null);
        bool mineruReady = mineru?.Tiers.Any(tier =>
            tier.Id == mineru.DefaultTier && tier.Availability == Wire.MineruTierAvailability.Ready) == true;
        bool paddleModelReady = hasPaddle && residency.Entries.Any(entry =>
            entry.ResourceKind == RecognitionResourceKind.Process &&
            entry.ResourceId == "paddle" && entry.ModelsPrepared == true);
        string modelState = hasMineru && mineruReady || paddleModelReady ? "ready"
            : environment.ModelState;
        return environment with
        {
            EngineState = engineState,
            ModelState = modelState,
            ServiceState = status.ServiceState == RuntimeServiceState.Ready ? "ready" : "unavailable",
            // Runtime status reports the selected accelerator, not the device
            // actually used by a task. Residency VRAM may belong to other work.
            ActualDevice = null,
        };
    }

    public Task DeleteAsync(string environmentId, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await manager.DeleteEnvironmentAsync(environmentId, cancellationToken);
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        Status = "环境已删除。";
    }, cancellationToken);

    private Task RunAsync(Func<Task> action, CancellationToken cancellationToken,
        string? failureStatus = null) =>
        RunAsync<object?>(async () => { await action(); return null; }, cancellationToken, failureStatus);

    private async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> action,
        CancellationToken cancellationToken, string? failureStatus = null)
    {
        await gate.WaitAsync(cancellationToken);
        IsBusy = true;
        try
        {
            StateChanged?.Invoke();
            return await action();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Status = failureStatus ?? error.Message;
            throw;
        }
        finally
        {
            IsBusy = false;
            gate.Release();
            StateChanged?.Invoke();
        }
    }
}
