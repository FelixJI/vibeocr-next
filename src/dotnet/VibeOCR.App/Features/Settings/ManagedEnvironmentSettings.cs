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
    Action? installAttempted = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? activeInstall;

    private const string RunningEvidenceUnavailableMessage =
        "无法读取活动环境状态，请刷新或重新切换环境。";

    public event Action? StateChanged;
    public ManagedEnvironmentList? Snapshot { get; private set; }
    public ManagedEnvironmentPlan? Plan { get; private set; }
    public bool IsBusy { get; private set; }
    public bool CanCancelInstall => Volatile.Read(ref activeInstall) is not null;
    public string Status { get; private set; } = "尚未读取运行环境";

    public bool IsRunning(ManagedEnvironment environment) =>
        runningSession() is { } session && session.Id == environment.Id &&
        session.Revision == environment.Revision;

    /// <summary>取消后读取持久终态的有界预算；测试可缩短以同步验证超时分支。</summary>
    internal TimeSpan InstallCancelConfirmTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public Task RefreshAsync(CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: true);
        Status = Snapshot?.ActiveId is null
            ? "尚未启动运行环境；可以创建空环境并稍后安装依赖。"
            : "运行环境状态已更新。";
    }, cancellationToken);

    public Task CreateAsync(string name, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await manager.CreateEnvironmentAsync(name, cancellationToken);
        Plan = null;
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        Status = "空环境已创建；未安装识别依赖。";
    }, cancellationToken);

    public Task PreviewAsync(string environmentId, string recipe, string? sourceId, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        Plan = null;
        Plan = await manager.PreviewEnvironmentInstallAsync(
            environmentId, recipe,
            sourceId is null ? null : [sourceId], cancellationToken);
        string sourceSummary = Plan.RequestedSourceIds is null
            ? $"继承当前配置（生效：{string.Join("、", Plan.SourceIds)}）"
            : string.Join("、", Plan.SourceIds);
        Status = $"已预览 {Plan.Recipe} 锁定配方；来源：{sourceSummary}。确认后才会安装。";
    }, cancellationToken);

    public Task InstallAsync(string planId, string? sourceId, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ManagedEnvironmentPlan plan = Plan
            ?? throw new InvalidOperationException("请先预览此环境的锁定配方。");
        // 确认与预览一致：继承预览只能以继承确认，显式预览只能以同源确认。
        if (plan.PlanId != planId ||
            (plan.RequestedSourceIds is null) != (sourceId is null) ||
            (sourceId is not null &&
             (plan.RequestedSourceIds?.Count != 1 || plan.RequestedSourceIds[0] != sourceId)))
            throw new InvalidOperationException("安装计划已变化，请重新预览。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable lease = productMaintenance.Acquire(
            ProductMaintenanceOwner.RuntimeMaintenance, linked.Cancel);
        Volatile.Write(ref activeInstall, linked);
        bool cancelled = false;
        try
        {
            StateChanged?.Invoke();
            installAttempted?.Invoke();
            await manager.InstallEnvironmentAsync(
                plan, sourceId is null ? null : [sourceId], linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception)
        {
            try { await ReloadEnvironmentsAsync(CancellationToken.None, strictEvidence: false); }
            catch (Exception) { /* Preserve the original installation error. */ }
            throw;
        }
        finally
        {
            // #123：安装事务一结束就关闭取消入口；成功后的刷新只是结果展示。
            Volatile.Write(ref activeInstall, null);
            StateChanged?.Invoke();
        }
        if (cancelled)
        {
            await ConfirmCancelledInstallAsync(plan);
            return;
        }
        Plan = null;
        try
        {
            await ReloadEnvironmentsAsync(linked.Token, strictEvidence: false);
            Status = "依赖已安装；请切换环境以验证并启动服务。";
        }
        catch (OperationCanceledException)
        {
            // 事务已提交成功；刷新取消只影响展示，不得改判为安装取消/失败。
            Status = "依赖已安装；结果刷新已取消，请刷新列表查看最新状态。";
        }
        catch (Exception)
        {
            Status = "依赖已安装；结果刷新失败，请刷新列表查看最新状态。";
        }
    }, cancellationToken, "安装未完成；失败原因请查看该环境记录。");

    /// <summary>保存全局默认或单环境 override 来源偏好：只写配置，不下载/不安装/不重启。</summary>
    public Task SetSourcesAsync(
        string? environmentId, string? packageSourceId, string? modelSourceId,
        CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ManagedEnvironmentList updated = await manager.SetEnvironmentSourcesAsync(
            environmentId, packageSourceId, modelSourceId, cancellationToken);
        Snapshot = updated;
        // 来源配置变化使旧预览失效（confirm 时管理器也会拒绝旧计划）。
        Plan = null;
        await ApplyRunningEvidenceAsync(cancellationToken);
        bool touchesModel = modelSourceId is not null;
        bool targetRunning = environmentId is not null &&
            currentSession?.Invoke() is { } session && session.EnvironmentId == environmentId;
        string scope = environmentId is null ? "全局默认来源" : "该环境的来源 override";
        Status = $"已保存{scope}；仅影响后续安装与继承，不会下载或安装任何内容。";
        if (touchesModel && targetRunning)
            Status += "模型来源将在该环境下次启动时生效，不会改变当前运行中的服务。";
    }, cancellationToken);

    // #123：取消后以 plan_id 绑定读取持久终态；无法归属的结果一律未确认，
    // 不凭 revision 增长宣布成功，不把自然失败改写成取消，不把中断归因用户。
    private async Task ConfirmCancelledInstallAsync(ManagedEnvironmentPlan plan)
    {
        using var deadline = new CancellationTokenSource(InstallCancelConfirmTimeout);
        while (true)
        {
            try
            {
                await ReloadEnvironmentsAsync(deadline.Token, strictEvidence: false);
            }
            catch (Exception error)
            {
                Status = $"已请求取消；最终状态读取失败（{error.Message}），结果未确认，请稍后刷新查看。";
                return;
            }
            ManagedEnvironment? environment = Snapshot?.Environments
                .FirstOrDefault(item => item.Id == plan.EnvironmentId);
            if (environment is null)
            {
                Status = "已请求取消；目标环境已不存在，本次结果未确认。";
                return;
            }
            if (environment.LastInstallFailure is { PlanId: string failurePlanId } failure &&
                failurePlanId == plan.PlanId)
            {
                if (failure.Phase == "installing")
                {
                    // 操作锁尚未释放；有界重读，超时转未确认。
                    try { await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token); }
                    catch (OperationCanceledException)
                    {
                        Status = "已请求取消；安装可能仍在进行或已中断，结果未确认，请稍后刷新查看。";
                        return;
                    }
                    continue;
                }
                if (failure.Phase == "failed" &&
                    failure.EnvironmentRevision == plan.EnvironmentRevision)
                {
                    if (failure.ReasonCode == "install_interrupted")
                    {
                        if (environment.Revision == plan.EnvironmentRevision)
                        {
                            Status = "安装已中断；原环境保持不变，详情可在该环境记录中查看。";
                            return;
                        }
                    }
                    else
                    {
                        Status = $"安装已经失败（{failure.ReasonCode}）；失败详情保留在该环境记录中，取消未改变该结果。";
                        return;
                    }
                }
            }
            break;
        }
        Status = "已请求取消；本次安装结果未确认，请刷新列表查看。";
    }

    public void CancelInstall()
    {
        CancellationTokenSource? install = Volatile.Read(ref activeInstall);
        if (install is null) return;
        try { install.Cancel(); }
        catch (ObjectDisposedException) { return; }
        Status = "正在取消环境安装…";
        StateChanged?.Invoke();
    }

    public async Task CancelAndWaitForInstallAsync()
    {
        CancellationTokenSource? install = Volatile.Read(ref activeInstall);
        try { install?.Cancel(); }
        catch (ObjectDisposedException) { }
        await gate.WaitAsync();
        gate.Release();
    }

    public void InvalidatePlan()
    {
        Plan = null;
        StateChanged?.Invoke();
    }

    public Task SwitchAsync(string environmentId, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await activate(environmentId, cancellationToken);
        Plan = null;
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: true);
        Status = Snapshot?.ActiveId == environmentId
            ? "环境已切换。模型与引擎状态以目标服务检查结果为准。"
            : "环境切换未提交。";
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
            Snapshot = snapshot with { Environments = [.. snapshot.Environments.Select(item =>
                item.Id == current.Id ? observed : item)] };
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
        Snapshot = snapshot with { Environments = [.. snapshot.Environments.Select(item =>
            item.Id == current.Id
                ? item with
                {
                    EngineState = "unverified",
                    ModelState = "not_checked",
                    ServiceState = "unverified",
                    Reason = RunningEvidenceUnavailableMessage,
                }
                : item)] };
    }

    internal static ManagedEnvironment ProjectRunning(
        ManagedEnvironment environment, Wire.Health health,
        ResidencyStatus residency, RuntimeStatusSnapshot status)
    {
        var catalog = new RuntimeSelectionService(health);
        string[] modeIds = environment.Recipe switch
        {
            "rapidocr-cpu" => ["rapid_text"],
            "paddleocr-cpu" or "paddleocr-cuda" => ["paddle_text"],
            "mineru-cpu" => ["mineru_document"],
            "rapidocr+mineru-cpu" or "rapidocr+mineru-cuda" => ["rapid_text", "mineru_document"],
            _ => [],
        };
        RecognitionModeOption[] modes = [.. catalog.RecognitionModes.Where(mode =>
            modeIds.Length == 0 || modeIds.Contains(mode.Id, StringComparer.Ordinal))];
        string engineState = modes.Any(mode => mode.Availability == "ready") ? "ready"
            : modes.Any(mode => mode.Availability == "preparation_required") ? "preparation_required"
            : modes.Length > 0 ? "unavailable" : "unverified";
        if (modes.Length == 0 && !catalog.SupportsRecognitionModes)
        {
            OcrEngine? engine = environment.Recipe?.StartsWith("rapidocr", StringComparison.Ordinal) == true
                ? OcrEngine.RapidOcr
                : environment.Recipe?.StartsWith("paddleocr", StringComparison.Ordinal) == true
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
        bool hasPaddle = environment.Recipe?.StartsWith("paddleocr", StringComparison.Ordinal) == true;
        bool hasMineru = environment.Recipe?.Contains("mineru", StringComparison.Ordinal) == true;
        Wire.MineruConfigCatalog? mineru = health.CapabilityDescriptors?
            .Select(descriptor => descriptor.MineruConfigCatalog)
            .FirstOrDefault(catalog => catalog is not null);
        bool mineruReady = mineru?.Tiers.Any(tier =>
            tier.Id == mineru.DefaultTier && tier.Availability == Wire.MineruTierAvailability.Ready) == true;
        bool paddleModelReady = hasPaddle && residency.Entries.Any(entry =>
            entry.ResourceKind == RecognitionResourceKind.Model &&
            entry.RecognitionMode == RecognitionMode.PaddleText &&
            entry.Kind != ResidencyKind.Evicted);
        string modelState = engineState == "unavailable" ? "unavailable"
            : hasMineru && mineruReady || paddleModelReady ? "ready"
            : hasMineru || hasPaddle ? "pending" : "not_applicable";
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
        Plan = null;
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        Status = "环境已删除。";
    }, cancellationToken);

    public Task RepairEmptyAsync(string environmentId, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await manager.RepairEmptyEnvironmentAsync(environmentId, cancellationToken);
        Plan = null;
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        Status = "空环境解释器已修复；仍未安装识别依赖。";
    }, cancellationToken);

    private async Task RunAsync(Func<Task> action, CancellationToken cancellationToken,
        string? failureStatus = null)
    {
        await gate.WaitAsync(cancellationToken);
        IsBusy = true;
        try
        {
            StateChanged?.Invoke();
            await action();
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
