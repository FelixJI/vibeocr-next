using ManagedEnvironmentInstallProgress = VibeOCR.Runtime.Contracts.Generated.Host.ManagedEnvironmentInstallEvent;
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
    Action? installAttempted = null,
    Func<string, string?, CancellationToken, Task>? activateDevice = null,
    Func<CancellationToken, Task>? stopService = null,
    Func<Task>? restoreService = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? activeInstall;
    private CancellationTokenSource? activeCleanup;
    private int restoringService;
    public bool IsRestoringService => Volatile.Read(ref restoringService) != 0;
    private readonly object progressLock = new();
    private ManagedEnvironmentInstallProgress? installProgress;
    private string[] installLog = [];
    private int installGeneration;
  private int progressNotificationQueued;
  private long progressNotificationVersion;
    public ManagedEnvironmentInstallProgress? InstallProgress { get { lock (progressLock) return installProgress; } }
    public IReadOnlyList<string> InstallLog { get { lock (progressLock) return installLog; } }
    public bool SupportsInstallProgress => manager.SupportsEnvironmentInstallProgress;

  private void NotifyInstallProgress(SynchronizationContext? context)
  {
    Interlocked.Increment(ref progressNotificationVersion);
    if (context is null) { StateChanged?.Invoke(); return; }
    QueueInstallProgressNotification(context);
  }

  private void QueueInstallProgressNotification(SynchronizationContext context)
  {
    if (Interlocked.CompareExchange(ref progressNotificationQueued, 1, 0) != 0) return;
    try
    {
      context.Post(_ =>
      {
        long version = Volatile.Read(ref progressNotificationVersion);
        try { StateChanged?.Invoke(); }
        catch (Exception) { /* UI observers cannot change the install result. */ }
        finally
        {
          Volatile.Write(ref progressNotificationQueued, 0);
          if (Volatile.Read(ref progressNotificationVersion) != version)
            QueueInstallProgressNotification(context);
        }
      }, null);
    }
    catch (Exception)
    {
      Volatile.Write(ref progressNotificationQueued, 0);
      // A closing UI context cannot affect the installation transaction.
    }
  }
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
        if (CanCancelInstall || CanCancelCleanup) throw new InvalidOperationException("请等待当前操作结束后再检查清理项。");
        ManagedCleanupPlan preview = await manager.PreviewEnvironmentCleanupAsync(cancellationToken);
        // Explicitly entering cleanup ends the previous installation display flow.
        // Durable environment/failure records remain in Snapshot; a live attempt is never discarded.
        InvalidateSelectionPlans();
        lock (progressLock)
        {
            if (installProgress?.State != "running") { installProgress = null; installLog = []; }
        }
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
            InvalidateSelectionPlans();
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

    // 准备目标归属由 Runtime 持久保存；选择代阻止迟到结果覆盖当前计划。
    private int selectionGeneration;

    private const string RunningEvidenceUnavailableMessage =
        "无法读取活动环境状态，请刷新或重新切换环境。";

    public event Action? StateChanged;
    public ManagedEnvironmentList? Snapshot { get; private set; }
    public ManagedEnvironmentPlan? Plan { get; private set; }
    /// <summary>当前选择配方的兼容查询结果；仅由 FindCompatibleAsync 写入，
    /// 任何环境变更/失效都清空，防止旧结果覆盖新选择。</summary>
    public ManagedEnvironmentQueryResult? Compatibility { get; private set; }
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
            ? "尚未启动运行环境；请选择识别组件并准备配置。"
            : "运行环境状态已更新。";
    }, cancellationToken);

    public Task<ManagedEnvironment> CreateAsync(string name, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ManagedEnvironment created = await manager.CreateEnvironmentAsync(name, cancellationToken);
        InvalidateSelectionPlans();
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        Status = "空环境已创建；未安装识别依赖。";
        return created;
    }, cancellationToken);

    public Task PreviewAsync(string environmentId, string recipe, string? sourceId, CancellationToken cancellationToken)
    {
        int generation = Volatile.Read(ref selectionGeneration);
        return RunAsync(async () =>
        {
            if (IsSelectionStale(generation)) return;
            await EnsureCatalogAsync(recipe, cancellationToken);
            if (IsSelectionStale(generation)) return;
            Plan = null;
            ManagedEnvironmentPlan plan = await manager.PreviewEnvironmentInstallAsync(
                environmentId, recipe,
                sourceId is null ? null : [sourceId], cancellationToken);
            if (IsSelectionStale(generation)) return;
            Plan = plan;
            Status = $"已预览 {plan.Recipe} 锁定配方；下载来源：{string.Join("、", plan.SourceIds)}。确认后才会安装。";
        }, cancellationToken);
    }

    /// <summary>
    /// 推荐配置选择：按 Runtime 权威目录核验配方后只读查询可直接复用的
    /// 已安装环境；不创建、不安装、不切换，也不占用运行中的任务。
    /// </summary>
    public Task FindCompatibleAsync(string recipe, CancellationToken cancellationToken)
    {
        int generation = Volatile.Read(ref selectionGeneration);
        return RunAsync(async () =>
        {
            if (IsSelectionStale(generation)) return;
            await EnsureCatalogAsync(recipe, cancellationToken);
            if (IsSelectionStale(generation)) return;
            ManagedEnvironmentQueryResult result =
                await manager.FindCompatibleEnvironmentAsync(recipe, cancellationToken);
            if (IsSelectionStale(generation)) return;
            Compatibility = result;
            Status = Compatibility.Selected is { } selected
                ? $"{recipe} 有可直接复用的环境（{SelectionSummary(selected)}）；切换前不会安装任何内容。"
                : $"{recipe} 暂无可直接复用的环境；可自动创建空环境并预览依赖，确认后才会安装。";
        }, cancellationToken);
    }

    /// <summary>
    /// 兼容时只返回切换目标；否则由 Runtime 持久复用或创建准备目标并预览。
    /// 失败、双击与导航重挂载都复用同一目标，不按名称猜归属。
    /// </summary>
    public Task PrepareAsync(string recipe, CancellationToken cancellationToken)
    {
        int generation = Volatile.Read(ref selectionGeneration);
        return RunAsync(async () =>
        {
            if (IsSelectionStale(generation)) return;
            await EnsureCatalogAsync(recipe, cancellationToken);
            if (IsSelectionStale(generation)) return;
            ManagedEnvironmentQueryResult compatible =
                await manager.FindCompatibleEnvironmentAsync(recipe, cancellationToken);
            if (IsSelectionStale(generation)) return;
            Compatibility = compatible;
            if (compatible.Selected is { } selected)
            {
                Plan = null;
                Status = $"{recipe} 有可直接复用的环境（{SelectionSummary(selected)}）；" +
                    "切换即可使用，本次准备未创建或安装任何内容。";
                return;
            }
            ManagedEnvironment? target = await EnsurePrepareEnvironmentAsync(recipe, generation, cancellationToken);
            if (IsSelectionStale(generation) || target is null) return;
            string environmentId = target.Id;
            if (target.Status == "installed")
            {
                ManagedEnvironmentQueryResult refreshed = await manager.FindCompatibleEnvironmentAsync(recipe, cancellationToken);
                if (IsSelectionStale(generation)) return;
                Compatibility = refreshed;
                if (refreshed.Selected is not null)
                {
                    Plan = null;
                    Status = "已找到可直接复用的环境；本次准备未安装任何内容。";
                    return;
                }
            }
            Plan = null;
            ManagedEnvironmentPlan plan = await manager.PreviewEnvironmentInstallAsync(
                environmentId, recipe, null, cancellationToken);
            if (IsSelectionStale(generation)) return;
            Plan = plan;
            string environmentName = Snapshot?.Environments
                .FirstOrDefault(item => item.Id == environmentId)?.Name ?? environmentId;
            Status = $"已准备 {recipe} 安装计划（{(plan.Dependencies?.Count ?? 0)} 项依赖，目标环境「{environmentName}」）；" +
                "确认后才会安装。";
        }, cancellationToken);
    }

    /// <summary>
    /// 创建后刷新失败时，旧快照可能缺少已创建 ID；先权威读取再判缺失。
    /// </summary>
    private async Task<ManagedEnvironment?> EnsurePrepareEnvironmentAsync(
        string recipe, int generation, CancellationToken cancellationToken)
    {
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        if (IsSelectionStale(generation)) return null;
        ManagedEnvironment target = await manager.PrepareEnvironmentTargetAsync(recipe, cancellationToken);
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        return target;
    }

    private bool IsSelectionStale(int generation) =>
        Volatile.Read(ref selectionGeneration) != generation;

    private static string SelectionSummary(ManagedEnvironmentSelection selection) =>
        selection.SelectionReason == "active_environment"
            ? "当前活动环境"
            : "按稳定顺序选中";

    /// <summary>
    /// 宿主对 Runtime 目录的核验：桥的配方 id 协议类型不是目录真值，未知
    /// 配方在这里 fail closed 拒绝（Runtime 端 _recipe 仍会再拒绝一次）。
    /// 快照尚未加载时先只读补一次列表，不依赖前端刷新时序。
    /// </summary>
    private async Task EnsureCatalogAsync(string recipe, CancellationToken cancellationToken)
    {
        if (Snapshot?.Recipes is not { Count: > 0 })
            await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        if (Snapshot?.Recipes is not { Count: > 0 } recipes ||
            recipes.All(item => !string.Equals(item.Id, recipe, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "配方不在当前 Runtime 目录中，请刷新后重试。");
    }

    public Task InstallAsync(string planId, string? sourceId, CancellationToken cancellationToken)
    {
        int generation = Volatile.Read(ref selectionGeneration);
        if (CanCancelInstall)
            return Task.FromException(new InvalidOperationException("安装正在进行，请等待或取消当前安装。"));
        return RunAsync(async () =>
        {
            if (IsSelectionStale(generation))
                throw new InvalidOperationException("安装计划已变化，请重新预览。");
            ManagedEnvironmentPlan plan = Plan
                ?? throw new InvalidOperationException("请先预览此环境的锁定配方。");
            // 确认与预览一致：继承预览只能以继承确认，显式预览只能以同源确认。
            if (plan.PlanId != planId ||
                (plan.RequestedSourceIds is null) != (sourceId is null) ||
                (sourceId is not null &&
                 (plan.RequestedSourceIds?.Count != 1 || plan.RequestedSourceIds[0] != sourceId)))
                throw new InvalidOperationException("安装计划已变化，请重新预览。");
            // 保留冻结计划供导航/取消展示，同时使先前排队请求过期。
            Interlocked.Increment(ref selectionGeneration);
            Compatibility = null;
            CleanupResult = null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using IDisposable lease = productMaintenance.Acquire(
                ProductMaintenanceOwner.RuntimeMaintenance, linked.Cancel);
            Volatile.Write(ref activeInstall, linked);
            int attemptGeneration = Interlocked.Increment(ref installGeneration);
            SynchronizationContext? context = SynchronizationContext.Current;
            lock (progressLock) { installProgress = null; installLog = []; }
            void Progress(ManagedEnvironmentInstallProgress update)
            {
                void Apply()
                {
          lock (progressLock)
          {
                    if (attemptGeneration != Volatile.Read(ref installGeneration) ||
                        update.PlanId != plan.PlanId || update.EnvironmentId != plan.EnvironmentId ||
                        update.EnvironmentRevision != plan.EnvironmentRevision) return;
                        if (installProgress is { } previous &&
                            (previous.AttemptId != update.AttemptId || update.Seq <= previous.Seq || previous.State != "running")) return;
                        installProgress = update;
                        if (update.Log is { } log)
                        {
                            string entry = $"{update.Timestamp} [{update.Phase}/{log.Stream}] {log.Text}" +
                                (log.Truncated ? " [输出已截断]" : "");
                            string[] next = [.. installLog, entry];
                            int start = Math.Max(0, next.Length - 200);
                            int chars = 0;
                            for (int index = next.Length - 1; index >= start; index--)
                            {
                                chars += next[index].Length;
                                if (chars > 64000) { start = index + 1; break; }
                            }
                            installLog = start == 0 ? next : ["[较早输出已截断]", .. next[start..]];
                        }
                    }
          NotifyInstallProgress(context);
                }
        Apply();
            }
            bool cancelled = false;
            IAsyncDisposable? maintenance = null;
            bool stopped = false;
            Exception? installError = null;
            try
            {
                StateChanged?.Invoke();
                // Runtime freezes admission and checks job leases before we stop any process.
                if (plan.RequiresServiceStop)
                {
                    if (stopService is null || restoreService is null)
                        throw new InvalidOperationException("当前宿主不支持活动环境维护，请更新应用。");
                    maintenance = await manager.BeginEnvironmentMaintenanceAsync(plan.EnvironmentId, linked.Token);
                    stopped = true;
                    await stopService(linked.Token);
                }
                installAttempted?.Invoke();
                await manager.InstallEnvironmentAsync(
                    plan, sourceId is null ? null : [sourceId], Progress, linked.Token);
                // The final managed response proves commit, even if observation failed.
                Interlocked.Increment(ref installGeneration);
                lock (progressLock)
                    if (installProgress is { } observed)
                        installProgress = observed with
                        {
                            State = "succeeded",
                            Phase = "complete",
                            Current = "依赖已验证并提交；模型与服务尚未启动",
                            Log = null
                        };
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception error)
            {
                installError = error;
                Interlocked.Increment(ref installGeneration);
                lock (progressLock)
                    if (installProgress is { State: "running" } observed)
                        installProgress = observed with { State = "failed", Current = error.Message, Log = null };
                try { await ReloadEnvironmentsAsync(CancellationToken.None, strictEvidence: false); }
                catch (Exception) { /* Preserve the original installation error. */ }
                throw;
            }
            finally
            {
                // Installation has reached a terminal outcome. Restoration is a
                // separate, non-cancellable phase, still owned by the settings gate.
                Volatile.Write(ref restoringService, stopped ? 1 : 0);
                Volatile.Write(ref activeInstall, null);
                StateChanged?.Invoke();
                Exception? recoveryError = null;
                try
                {
                    try { if (maintenance is not null) await maintenance.DisposeAsync(); }
                    catch (Exception error) { recoveryError = error; }
                    // Even a reservation disposal failure must not skip the bounded restore.
                    try { if (stopped && restoreService is not null) await restoreService(); }
                    catch (Exception error)
                    {
                        recoveryError = recoveryError is null ? error : new AggregateException(recoveryError, error);
                    }
                    if (recoveryError is not null)
                        throw installError is null ? recoveryError : new AggregateException(
                            "环境维护失败，释放维护或恢复原服务也失败。", installError, recoveryError);
                }
                finally
                {
                    Volatile.Write(ref restoringService, 0);
                    StateChanged?.Invoke();
                }
            }
            if (cancelled)
            {
                await ConfirmCancelledInstallAsync(plan);
                Interlocked.Increment(ref installGeneration);
                ManagedEnvironmentInstallFailure? evidence = Snapshot?.Environments
                    .FirstOrDefault(item => item.Id == plan.EnvironmentId)?.LastInstallFailure;
                lock (progressLock)
                    if (installProgress is { State: "running" } observed)
                        installProgress = observed with
                        {
                            State = evidence is { Phase: "failed" } && evidence.PlanId == plan.PlanId
                                ? evidence.ReasonCode == "install_interrupted" ? "cancelled" : "failed" : "running",
              Current = Status,
              Log = null,
                        };
                return;
            }
            Plan = null;
            try
            {
                await ReloadEnvironmentsAsync(linked.Token, strictEvidence: false);
                Status = plan.RequiresServiceStop ? "依赖已更新，已恢复识别服务。"
                    : "依赖已安装；请切换环境以验证并启动服务。";
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
    }

    /// <summary>保存全局默认或单环境 override 来源偏好：只写配置，不下载/不安装/不重启。</summary>
    public Task SetSourcesAsync(
        string? environmentId, string? packageSourceId, string? modelSourceId,
        CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ManagedEnvironmentList updated = await manager.SetEnvironmentSourcesAsync(
            environmentId, packageSourceId, modelSourceId, cancellationToken);
        Snapshot = updated;
        // 来源配置变化使旧预览/兼容结果失效（confirm 时管理器也会拒绝旧计划）。
        InvalidateSelectionPlans();
        await ApplyRunningEvidenceAsync(cancellationToken);
        bool touchesModel = modelSourceId is not null;
        // 全局保存（environmentId=null）也会改变运行中环境的模型来源解析，
        // 只要有活动会话就如实提示下次启动生效。
        bool targetRunning = currentSession?.Invoke() is { } session &&
            (environmentId is null || session.EnvironmentId == environmentId);
        // 单产品语义：来源仅用于安装依赖与下载模型；全局保存由 Runtime
        // 原子清除各环境旧 override，界面所示即实际解析值。
        string scope = environmentId is null ? "下载来源已保存并统一所有环境" : "该环境的来源已保存";
        Status = $"{scope}；仅用于安装依赖与下载模型，不会下载或安装任何内容。";
        if (touchesModel && targetRunning)
            Status += "模型来源将在该环境下次启动时生效，不会改变当前运行中的服务。";
    }, cancellationToken);

    public Task SetSourcesAsync(
        string? environmentId, string? packageSourceId, string? paddleocrModelSourceId,
        string? mineruModelSourceId,
        CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ManagedEnvironmentList updated = await manager.SetEnvironmentSourcesAsync(
            environmentId, packageSourceId, paddleocrModelSourceId, mineruModelSourceId, cancellationToken);
        Snapshot = updated;
        // 来源配置变化使旧预览/兼容结果失效（confirm 时管理器也会拒绝旧计划）。
        InvalidateSelectionPlans();
        await ApplyRunningEvidenceAsync(cancellationToken);
        // 全局保存（environmentId=null）也会改变运行中环境的模型来源解析，
        // 只要有活动会话就如实提示下次启动生效。
        bool targetRunning = currentSession?.Invoke() is { } session &&
            (environmentId is null || session.EnvironmentId == environmentId);
        // 单产品语义：来源仅用于安装依赖与下载模型；全局保存由 Runtime
        // 原子清除各环境旧 override，界面所示即实际解析值。
        string scope = environmentId is null ? "下载来源已保存并统一所有环境" : "该环境的来源已保存";
        Status = $"{scope}；仅用于安装依赖与下载模型，不会下载或安装任何内容。";
        if (targetRunning)
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
        try { Volatile.Read(ref activeCleanup)?.Cancel(); install?.Cancel(); }
        catch (ObjectDisposedException) { }
        await gate.WaitAsync();
        gate.Release();
    }

    public void InvalidatePlan()
    {
        InvalidateSelectionPlans();
        StateChanged?.Invoke();
    }

    private void InvalidateSelectionPlans()
    {
        Interlocked.Increment(ref selectionGeneration);
        Plan = null;
        CleanupPlan = null;
        Compatibility = null;
    }

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
        InvalidateSelectionPlans();
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
            entry.ResourceKind == RecognitionResourceKind.Model &&
            entry.RecognitionMode == RecognitionMode.PaddleText &&
            entry.Kind != ResidencyKind.Evicted);
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
        InvalidateSelectionPlans();
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        Status = "环境已删除。";
    }, cancellationToken);

    public Task RepairEmptyAsync(string environmentId, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await manager.RepairEmptyEnvironmentAsync(environmentId, cancellationToken);
        InvalidateSelectionPlans();
        await ReloadEnvironmentsAsync(cancellationToken, strictEvidence: false);
        Status = "空环境解释器已修复；仍未安装识别依赖。";
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
