using System.ComponentModel;
using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Services;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchSettingsOwner(Func<CancellationToken, Task>? ensureRuntimeReady, Func<bool>? inferenceAttached, Func<SettingsViewModel> settingsFactory, Lazy<ShellViewModel> shell, ShellActionDispatcher? shellActions, Func<string?> supervisorInstanceId, WorkbenchOperations operations, Func<CancellationToken, Task> refreshFeatureStates, Action invalidateImageRecognition)
{
  public event Action? PinnedTextEnvironmentChanged;
  internal bool Handles(WorkbenchCommand command) => command is RefreshRuntimeCommand or RefreshEnvironmentListCommand or CreateEnvironmentCommand or SetEnvironmentSourcesCommand or PreviewEnvironmentInstallCommand or ConfirmEnvironmentInstallCommand or CancelEnvironmentInstallCommand or InvalidateEnvironmentPlanCommand or SwitchEnvironmentCommand or SetEnvironmentCleanupPageCommand or PreviewEnvironmentCleanupCommand or RunEnvironmentCleanupCommand or CancelEnvironmentCleanupCommand or DeleteEnvironmentCommand or RepairEmptyEnvironmentCommand or FindCompatibleEnvironmentCommand or PrepareEnvironmentCommand or PrepareRemoteHostCommand or SetThemeCommand or SetStartupCommand or BeginHotkeyRecordingCommand or EndHotkeyRecordingCommand or SetActionHotkeyCommand or ResetActionHotkeyCommand or SetFloatingToolbarEnabledCommand or SetFloatingToolbarLayoutCommand or SetFloatingToolbarPreferencesCommand or ShowFloatingToolbarCommand or HideFloatingToolbarCommand or SetDownloadSourceCommand or SetAcceleratorCommand or SetRuntimeFeatureCommand or SetMineruConnectionCommand or SetMineruRecognitionCommand or SetDefaultRecognitionModeCommand or PrepareMineruConnectionCommand or InstallRuntimeCommand or ConfirmRuntimeInstallCommand or CancelRuntimeMaintenanceCommand or RetryRuntimeMaintenanceCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => command switch
  {
    RefreshRuntimeCommand => await RefreshRuntimeAsync(cancellationToken),
    RefreshEnvironmentListCommand => await RunEnvironmentAsync(
      environment => environment.RefreshAsync(cancellationToken), cancellationToken),
    CreateEnvironmentCommand create => await RunEnvironmentAsync(
      environment => environment.CreateAsync(create.Name, cancellationToken), cancellationToken),
    SetEnvironmentSourcesCommand setEnvironmentSources => await RunEnvironmentAsync(
      environment => setEnvironmentSources.IndependentModelSources
        ? environment.SetSourcesAsync(setEnvironmentSources.EnvironmentId,
            setEnvironmentSources.PackageSourceId, setEnvironmentSources.PaddleocrModelSourceId,
            setEnvironmentSources.MineruModelSourceId, cancellationToken)
        : environment.SetSourcesAsync(setEnvironmentSources.EnvironmentId,
            setEnvironmentSources.PackageSourceId, setEnvironmentSources.ModelSourceId,
            cancellationToken), cancellationToken),
    PreviewEnvironmentInstallCommand preview => await RunEnvironmentAsync(
      environment => environment.PreviewAsync(preview.EnvironmentId, preview.Recipe,
        preview.SourceId, cancellationToken), cancellationToken),
    ConfirmEnvironmentInstallCommand confirmEnvironment => StartEnvironmentOperation(
      environment => environment.InstallAsync(confirmEnvironment.PlanId,
        confirmEnvironment.SourceId, cancellationToken)),
    CancelEnvironmentInstallCommand => CancelEnvironmentInstall(),
    InvalidateEnvironmentPlanCommand => InvalidateEnvironmentPlan(),
    SwitchEnvironmentCommand switchEnvironment => StartEnvironmentOperation(
      environment => environment.SwitchAsync(switchEnvironment.EnvironmentId, cancellationToken),
      refreshCatalog: true),
    SetEnvironmentCleanupPageCommand page => await RunEnvironmentAsync(
      environment => environment.SetCleanupPageAsync(page.Page, cancellationToken), cancellationToken),
    PreviewEnvironmentCleanupCommand => await RunEnvironmentAsync(
      environment => environment.PreviewCleanupAsync(cancellationToken), cancellationToken),
    RunEnvironmentCleanupCommand cleanup => StartEnvironmentOperation(
      environment => environment.CleanupAsync(cleanup.PlanId, cleanup.ItemIds, cancellationToken)),
    CancelEnvironmentCleanupCommand => CancelEnvironmentCleanup(),
    DeleteEnvironmentCommand deleteEnvironment => await RunEnvironmentAsync(
      environment => environment.DeleteAsync(deleteEnvironment.EnvironmentId, cancellationToken), cancellationToken),
    RepairEmptyEnvironmentCommand repair => await RunEnvironmentAsync(
      environment => environment.RepairEmptyAsync(repair.EnvironmentId, cancellationToken), cancellationToken),
    FindCompatibleEnvironmentCommand findCompatible => await RunEnvironmentAsync(
      environment => environment.FindCompatibleAsync(findCompatible.Recipe, cancellationToken), cancellationToken),
    PrepareEnvironmentCommand prepareEnvironment => await RunEnvironmentAsync(
      environment => environment.PrepareAsync(prepareEnvironment.Recipe, cancellationToken), cancellationToken),
    PrepareRemoteHostCommand => PrepareRemoteHost(cancellationToken),
    SetThemeCommand setTheme => SetTheme(setTheme),
    SetStartupCommand startup => SetStartup(startup),
    BeginHotkeyRecordingCommand recording => BeginHotkeyRecording(recording),
    EndHotkeyRecordingCommand recording => EndHotkeyRecording(recording),
    SetActionHotkeyCommand setActionHotkey => SetActionHotkey(setActionHotkey),
    ResetActionHotkeyCommand resetActionHotkey => ResetActionHotkey(resetActionHotkey),
    SetFloatingToolbarEnabledCommand toolbarEnabled => SetFloatingToolbarEnabled(
      toolbarEnabled),
    SetFloatingToolbarLayoutCommand toolbarLayout => SetFloatingToolbarLayout(
      toolbarLayout),
    SetFloatingToolbarPreferencesCommand toolbarPreferences => SetFloatingToolbarPreferences(
      toolbarPreferences),
    ShowFloatingToolbarCommand => ShowFloatingToolbar(),
    HideFloatingToolbarCommand => HideFloatingToolbar(),
    SetDownloadSourceCommand source => await SetSourceAsync(
      source,
      cancellationToken),
    SetAcceleratorCommand accelerator => SetAccelerator(accelerator),
    SetRuntimeFeatureCommand feature => SetFeature(feature),
    SetMineruConnectionCommand mineru => await SetMineruConnectionAsync(
      mineru,
      cancellationToken),
    SetMineruRecognitionCommand mineruRecognition => await SetMineruRecognitionAsync(
      mineruRecognition,
      cancellationToken),
    SetDefaultRecognitionModeCommand defaultMode => await SetDefaultRecognitionModeAsync(
      defaultMode,
      cancellationToken),
    PrepareMineruConnectionCommand => await PrepareMineruConnectionAsync(
      cancellationToken),
    InstallRuntimeCommand => await InstallRuntimeAsync(cancellationToken),
    ConfirmRuntimeInstallCommand confirm => StartRuntimeInstall(confirm.PlanId, cancellationToken),
    CancelRuntimeMaintenanceCommand => CancelRuntimeMaintenance(),
    RetryRuntimeMaintenanceCommand => await RetryRuntimeMaintenanceAsync(
      cancellationToken),
    _ => throw new InvalidOperationException("Unsupported Settings workbench command."),
  };

  private int environmentSwitching;
  // 保留已就绪实例的基线，避免首次设置通知误清贴图取字。
  private string pinnedServiceInstance = supervisorInstanceId() ?? string.Empty;
  private bool pinMaintenanceNotified;
  private SettingsViewModel? settings;
  internal SettingsViewModel? CurrentModel => settings;
  internal SettingsViewModel EnsureModel() => settings ??= CreateSettings();
  internal WorkbenchTheme Theme => theme;
  internal bool IsEnvironmentSwitching => Volatile.Read(ref environmentSwitching) != 0;
  private WorkbenchTheme theme = WorkbenchTheme.System;
  private async Task<SettingsWorkbenchState> RefreshRuntimeAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    if (ensureRuntimeReady is not null) await ensureRuntimeReady(cancellationToken);
    await settings.LoadSnapshotAsync(cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> RunEnvironmentAsync(
    Func<ManagedEnvironmentSettings, Task> action,
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    settings ??= CreateSettings();
    ManagedEnvironmentSettings environments = settings.Environments
      ?? throw new InvalidOperationException("运行环境管理器不可用。");
    await action(environments);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState? StartEnvironmentOperation(
    Func<ManagedEnvironmentSettings, Task> action,
    bool refreshCatalog = false)
  {
    settings ??= CreateSettings();
    ManagedEnvironmentSettings environments = settings.Environments
      ?? throw new InvalidOperationException("运行环境管理器不可用。");
    if (refreshCatalog)
    {
      Interlocked.Exchange(ref environmentSwitching, 1);
      settings.ClearSelection();
      // 切换尚未提交，失效文案不得声称实例已更换；结束后按实际当前服务回读。
      settings.InvalidateSnapshot("服务状态待重新检查。");
    }
    return operations.Start(SettingsState(settings), async () =>
    {
      bool completed = false;
      try
      {
        if (refreshCatalog)
          await RefreshRecognitionCatalogStatesAsync(CancellationToken.None);
        await action(environments);
        completed = true;
      }
      catch (Exception error) when (error is not OperationCanceledException)
      {
        AppLog.Warn($"Environment operation failed: {error.GetType().Name}: {error.Message}");
      }
      finally
      {
        if (refreshCatalog)
        {
          try
          {
            if (!operations.IsDisposed)
            {
              if (completed)
              {
                await RefreshRecognitionCatalogAsync(CancellationToken.None);
                // 以新环境重新读取设置快照：Backend/驻留等旧实例投影不得
                // 继续冒充当前状态。
                await settings.LoadSnapshotAsync(CancellationToken.None);
              }
              else
              {
                // 失败同样按实际当前服务回读；不刷环境列表，
                // 避免其中性成功文案覆盖切换失败的根因。
                await settings.LoadSnapshotAsync(refreshEnvironments: false, CancellationToken.None);
                await RefreshRecognitionCatalogStatesAsync(CancellationToken.None);
              }
            }
          }
          catch (Exception error) when (error is not OperationCanceledException)
          {
            AppLog.Warn($"Recognition catalog refresh failed: {error.GetType().Name}: {error.Message}");
          }
          finally
          {
            Interlocked.Exchange(ref environmentSwitching, 0);
            if (!operations.IsDisposed) operations.Publish(SettingsState(settings));
          }
        }
      }
    });
  }

  private SettingsWorkbenchState CancelEnvironmentCleanup()
  {
    settings ??= CreateSettings();
    settings.Environments?.CancelCleanup();
    return SettingsState(settings);
  }

  private SettingsWorkbenchState CancelEnvironmentInstall()
  {
    settings ??= CreateSettings();
    settings.Environments?.CancelInstall();
    return SettingsState(settings);
  }

  /// <summary>远程模式专用环境的基础名；重复创建时追加序号，绝不复用
  /// 同名用户环境。</summary>
  private const string RemoteHostEnvironmentName = "远程基础服务";

  /// <summary>本次应用会话内远程模式专用环境 id：重试时验证仍存在且为空
  /// 才复用（改名不影响同 id 复用）；不跨会话持久化。</summary>
  private string? remoteHostEnvironmentId;

  /// <summary>远程模式入口跨步骤禁重入：各步之间 RunAsync 会释放
  /// IsBusy，此标志覆盖整段链路，SettingsState 的环境忙碌投影含它，
  /// finally 里滑零；重复点击直接拒绝，不进入第二套编排。</summary>
  private int remoteHostPreparing;

  private static string NextRemoteHostEnvironmentName(ManagedEnvironmentList? snapshot)
  {
    var taken = new HashSet<string>(
      (snapshot?.Environments ?? []).Select(item => item.Name),
      StringComparer.OrdinalIgnoreCase);
    string candidate = RemoteHostEnvironmentName;
    int suffix = 2;
    while (!taken.Add(candidate))
    {
      candidate = $"{RemoteHostEnvironmentName} {suffix++}";
    }
    return candidate;
  }

  /// <summary>
  /// 远程模式宿主入口编排：随包离线基础配方一键就绪并启动承载识别
  /// 服务。基础配方只取 Runtime 目录内 dependency_origin=bundled_pack
  /// 的目录项，宿主不自行推导依赖。已安装可复用环境只切换启动；无可
  /// 复用环境时仅复用本次会话创建的专用环境 id（仍为空时重试续装），
  /// 全链路走 Runtime 权威离线安装事务（取消/CAS/回滚保留），安装
  /// 取消或失败不切换，不触碰用户既有环境。整段链路在后台追踪闭包内
  /// 执行，IsBusy 立即覆盖长操作，入口回执不等待安装完成。
  /// </summary>
  private SettingsWorkbenchState? PrepareRemoteHost(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    _ = settings.Environments
      ?? throw new InvalidOperationException("运行环境管理器不可用。");
    if (Interlocked.CompareExchange(ref remoteHostPreparing, 1, 0) != 0)
      throw new InvalidOperationException("远程模式基础服务正在准备中，请稍候。");
    try
    {
      return StartEnvironmentOperation(async environment =>
      {
        try
        {
          await environment.RefreshAsync(cancellationToken);
          // 目录缺失/安装未完成等失败经 ReportFailure 状态 seam 用户可见，
          // 不被后台追踪仅记录日志。
          string? baseRecipe = environment.Snapshot?.Recipes?.FirstOrDefault(recipe =>
              string.Equals(recipe.DependencyOrigin, "bundled_pack", StringComparison.Ordinal))?.Id;
          if (baseRecipe is null)
          {
            environment.ReportFailure("Runtime 目录未提供随包离线基础配方，请更新产品。");
            throw new InvalidOperationException(
              "Runtime 目录未提供随包离线基础配方，请更新产品。");
          }
          await environment.FindCompatibleAsync(baseRecipe, cancellationToken);
          if (environment.Compatibility?.Selected is { } reusable)
          {
            // 已有可复用环境：只切换启动，不新建、不重装。
            await environment.SwitchAsync(reusable.EnvironmentId, cancellationToken);
            return;
          }
          // 本次会话专用环境：仍存在且为空则同 id 续装，否则新建；
          // 绝不按名称复用用户环境。
          ManagedEnvironmentList snapshot = environment.Snapshot
            ?? throw new InvalidOperationException("运行环境状态不可用，请重试。");
          ManagedEnvironment? session = remoteHostEnvironmentId is { } id
            ? snapshot.Environments.FirstOrDefault(item => item.Id == id)
            : null;
          string targetId;
          if (session is { Status: "empty" })
          {
            targetId = session.Id;
          }
          else
          {
            ManagedEnvironment created = await environment.CreateAsync(
              NextRemoteHostEnvironmentName(snapshot), cancellationToken);
            targetId = created.Id;
            remoteHostEnvironmentId = targetId;
          }
          await environment.PreviewAsync(targetId, baseRecipe, null, cancellationToken);
          string planId = environment.Plan?.PlanId
            ?? throw new InvalidOperationException("基础服务安装预览失败，请重试。");
          await environment.InstallAsync(planId, null, cancellationToken);
          // 取消/失败不得切换：以 Runtime 权威快照确认 installed 后才启动。
          await environment.RefreshAsync(CancellationToken.None);
          ManagedEnvironment? installed = environment.Snapshot?.Environments
            .FirstOrDefault(item => item.Id == targetId);
          if (installed is null ||
              !string.Equals(installed.Status, "installed", StringComparison.Ordinal))
          {
            environment.ReportFailure("基础服务安装未完成，未切换运行环境；可再次启用重试。");
            throw new InvalidOperationException(
              "基础服务安装未完成，未切换运行环境；可再次启用重试。");
          }
          await environment.SwitchAsync(targetId, cancellationToken);
        }
        finally
        {
          Interlocked.Exchange(ref remoteHostPreparing, 0);
        }
      }, refreshCatalog: true);
    }
    catch
    {
      // 闭包未被追踪发布（同步段失败）：同样滑零，避免永久占用入口。
      Interlocked.Exchange(ref remoteHostPreparing, 0);
      throw;
    }
  }

  private SettingsWorkbenchState InvalidateEnvironmentPlan()
  {
    settings ??= CreateSettings();
    settings.Environments?.InvalidatePlan();
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetTheme(SetThemeCommand command)
  {
    theme = command.Theme;
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  private SettingsWorkbenchState SetStartup(SetStartupCommand command)
  {
    settings ??= CreateSettings();
    shell.Value.SetStartWithSystem(command.Enabled);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState BeginHotkeyRecording(BeginHotkeyRecordingCommand command)
  {
    ShellActions().BeginHotkeyRecording(command.RecordingId);
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  internal SettingsWorkbenchState EndHotkeyRecording(EndHotkeyRecordingCommand command)
  {
    ShellActions().EndHotkeyRecording(command.RecordingId);
    return settings is null ? SettingsShellState() : SettingsState(settings);
  }

  internal void EndHotkeyRecording()
  {
    if (shellActions?.IsHotkeyRecording != true) return;
    shellActions.EndHotkeyRecording();
    operations.Publish(settings is null ? SettingsShellState() : SettingsState(settings));
  }

  /// <summary>
  /// 动作键位设置：结果（成功/冲突/保存失败及当前实际注册状态）以设置页
  /// 的动作状态列表回显，命令本身不因键位被拒而报协议错误。
  /// </summary>
  private SettingsWorkbenchState SetActionHotkey(SetActionHotkeyCommand command)
  {
    settings ??= CreateSettings();
    ShellActions().TrySetHotkeyAction(command.ActionId, command.Hotkey, out _);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState ResetActionHotkey(ResetActionHotkeyCommand command)
  {
    settings ??= CreateSettings();
    ShellActions().ResetHotkeyAction(command.ActionId, out _);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetFloatingToolbarEnabled(
    SetFloatingToolbarEnabledCommand command)
  {
    settings ??= CreateSettings();
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    string? error = current.Enabled == command.Enabled
      ? null
      : ApplyToolbar(actions, current with { Enabled = command.Enabled });
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState SetFloatingToolbarLayout(
    SetFloatingToolbarLayoutCommand command)
  {
    settings ??= CreateSettings();
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    string? error = current.Edge == command.Edge && current.AutoHide == command.AutoHide
      ? null
      : ApplyToolbar(actions, current with
      {
        Edge = command.Edge,
        AutoHide = command.AutoHide,
      });
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState SetFloatingToolbarPreferences(
    SetFloatingToolbarPreferencesCommand command)
  {
    settings ??= CreateSettings();
    if ((command.LingerMs is null && command.Theme is null && command.PeekPixels is null) ||
      command.PeekPixels is < 1 or > FloatingToolbarSettings.MaximumPeekPixels ||
      command.LingerMs is < FloatingToolbarSettings.MinimumLingerMs or > FloatingToolbarSettings.MaximumLingerMs ||
      command.Theme is not (null or "system" or "light" or "dark"))
    {
      return SettingsState(settings, "收起时间须为 100–5000 毫秒，露出像素须为 1–20，主题须为跟随系统、亮色或深色；原设置已保留。");
    }
    ShellActionDispatcher actions = ShellActions();
    FloatingToolbarSettings current = actions.ToolbarSettings;
    int lingerMs = command.LingerMs ?? current.LingerMs;
    int peekPixels = command.PeekPixels ?? current.PeekPixels;
    FloatingToolbarTheme theme = command.Theme is null
      ? current.Theme : FloatingToolbarSettings.ParseTheme(command.Theme);
    string? error = current.LingerMs == lingerMs && current.Theme == theme && current.PeekPixels == peekPixels
      ? null
      : ApplyToolbar(actions, current with { LingerMs = lingerMs, Theme = theme, PeekPixels = peekPixels });
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState ShowFloatingToolbar()
  {
    settings ??= CreateSettings();
    ShellActions().TryShowFloatingToolbar(out string? error);
    return SettingsState(settings, error);
  }

  private SettingsWorkbenchState HideFloatingToolbar()
  {
    settings ??= CreateSettings();
    ShellActions().TryHideFloatingToolbar(out string? error);
    return SettingsState(settings, error);
  }

  /// <summary>
  /// 应用悬浮工具栏偏好：保存失败保旧并显式回显错误，不很报成功。
  /// </summary>
  private static string? ApplyToolbar(
    ShellActionDispatcher actions,
    FloatingToolbarSettings next) =>
    actions.TryApplyFloatingToolbar(next, out string? error) ? null : error;

  private ShellActionDispatcher ShellActions() =>
    shellActions ?? throw new InvalidOperationException(
      "Shell actions are unavailable in this mode.");

  private async Task<SettingsWorkbenchState> SetSourceAsync(
    SetDownloadSourceCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetSourceAsync(
      command.Kind,
      command.SourceId,
      cancellationToken);
    if (command.Kind == "package_index") settings.Environments?.InvalidatePlan();
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetAccelerator(SetAcceleratorCommand command)
  {
    settings ??= CreateSettings();
    settings.SetPendingAccelerator(command.Accelerator);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetFeature(SetRuntimeFeatureCommand command)
  {
    settings ??= CreateSettings();
    settings.SetFeatureEnabled(command.FeatureId, command.Enabled);
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> SetMineruConnectionAsync(
    SetMineruConnectionCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetMineruConnectionAsync(
      command.Mode,
      command.ApiUrl,
      command.ApiKey,
      cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  /// <summary>
  /// 保存全局 MinerU 识别偏好：能力/目录校验在 SettingsViewModel 内
  /// fail closed；偏好不改变目录可用性，无需刷新 health，任务页状态
  /// 经 RecognitionSelection 快照即时携带新偏好。
  /// </summary>
  private async Task<SettingsWorkbenchState> SetMineruRecognitionAsync(
    SetMineruRecognitionCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetMineruRecognitionAsync(
      command.Tier,
      command.OcrMode,
      command.PageRange,
      command.Language,
      cancellationToken);
    return SettingsState(settings);
  }

  /// <summary>
  /// 保存默认识别模式：能力门禁 + 目录 ready 校验在
  /// SettingsViewModel 内 fail closed；保存成功后重发任务页状态，继承
  /// 选项立即回显新默认，在跑/排队任务参数不受影响。
  /// </summary>
  private async Task<SettingsWorkbenchState> SetDefaultRecognitionModeAsync(
    SetDefaultRecognitionModeCommand command,
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.SetDefaultRecognitionModeAsync(
      command.ModeId,
      cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> PrepareMineruConnectionAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    await settings.PrepareMineruRemoteAsync(cancellationToken);
    await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    return SettingsState(settings);
  }

  private Task RefreshRecognitionCatalogStatesAsync(CancellationToken cancellationToken) => refreshFeatureStates(cancellationToken);

  internal async Task RefreshRecognitionCatalogAsync(CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    // Announce the session before capturing its catalog generation. Initial
    // status/property projection must not invalidate that session's first load.
    OnSettingsChanged();
    settings.ClearSelection();
    try
    {
      if (inferenceAttached?.Invoke() != false)
        await settings.RefreshSelectionAsync(cancellationToken);
    }
    finally
    {
      if (!operations.IsDisposed)
        await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    }
  }

  private SettingsViewModel CreateSettings()
  {
    SettingsViewModel model = settingsFactory();
    if (model.Environments is { } environments)
      environments.StateChanged += OnSettingsChanged;
    model.Maintenance.StateChanged += OnSettingsChanged;
    model.RuntimeStatus.PropertyChanged += OnSettingsPropertyChanged;
    model.PropertyChanged += OnSettingsPropertyChanged;
    return model;
  }

  internal void OnSettingsChanged()
  {
    if (!operations.IsDisposed && settings is not null)
    {
      string service = supervisorInstanceId() ?? string.Empty;
      bool maintaining = settings.Maintenance.State.IsRunning;
      bool instanceChanged = service != pinnedServiceInstance;
      if (instanceChanged || (maintaining && !pinMaintenanceNotified))
        PinnedTextEnvironmentChanged?.Invoke();
      pinnedServiceInstance = service;
      pinMaintenanceNotified = maintaining;
      if (instanceChanged)
      {
        // 服务实例更换（切换/维护停止/崩溃恢复）：旧实例的在途快照与加速
        // 器目标不得继续作为当前状态投影。
        settings.InvalidateSnapshot();
      }
      if (maintaining) invalidateImageRecognition();
      operations.Publish(SettingsState(settings));
    }
  }

  internal void OnToolbarStateChanged()
  {
    if (!operations.IsDisposed)
      operations.Publish(settings is null ? SettingsShellState() : SettingsState(settings));
  }

  /// <summary>
  /// 运行环境维护（含切换）事件使在途截图会话识别失效：迟到结果
  /// 不得覆盖当前会话。两个通道各自失效，互不牵连；只接现有维护事件，
  /// 不接管环境状态机。
  /// </summary>
  private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args) => OnSettingsChanged();

  private SettingsWorkbenchState? StartRuntimeInstall(string planId, CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    RejectLegacyMaintenanceForManagedSettings(settings);
    return operations.Start(SettingsState(settings),
      () => CompleteRuntimeInstallAsync(settings, planId, cancellationToken));
  }

  private async Task CompleteRuntimeInstallAsync(SettingsViewModel model, string planId, CancellationToken cancellationToken)
  {
    await model.ConfirmInstallAsync(planId, cancellationToken);
    if (!operations.IsDisposed)
    {
      operations.PublishTerminal(SettingsState(model));
      await RefreshRecognitionCatalogStatesAsync(cancellationToken);
    }
  }

  private async Task<SettingsWorkbenchState> InstallRuntimeAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    RejectLegacyMaintenanceForManagedSettings(settings);
    await settings.PreviewInstallAsync(cancellationToken);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState CancelRuntimeMaintenance()
  {
    settings ??= CreateSettings();
    settings.CancelMaintenance();
    return SettingsState(settings);
  }

  private async Task<SettingsWorkbenchState> RetryRuntimeMaintenanceAsync(
    CancellationToken cancellationToken)
  {
    settings ??= CreateSettings();
    RejectLegacyMaintenanceForManagedSettings(settings);
    await settings.RetryMaintenanceAsync(cancellationToken);
    return SettingsState(settings);
  }

  private static void RejectLegacyMaintenanceForManagedSettings(SettingsViewModel model)
  {
    if (model.Environments is not null)
      throw new InvalidOperationException("请为指定环境预览锁定配方后安装。");
  }

  private SettingsWorkbenchState SettingsShellState() => new(
    theme,
    false,
    "settings.ready",
    "unknown",
    shell.Value.StartWithSystem,
    Sources: [],
    HotkeyActions: HotkeyActionStates(),
    FloatingToolbar: ToolbarProjection());

  private IReadOnlyList<SettingsHotkeyActionState> HotkeyActionStates() =>
    shellActions is null
      ? []
      : [.. shellActions.GetHotkeyActions().Select(status => new SettingsHotkeyActionState(
        status.ActionId,
        status.DisplayName,
        status.ConfiguredHotkey,
        status.RegisteredHotkey,
        status.Error,
        HotkeyActionCatalog.DefaultBinding(status.ActionId)))];

  private SettingsFloatingToolbarState? ToolbarProjection(string? error = null) =>
    shellActions is null
      ? null
      : new SettingsFloatingToolbarState(
        shellActions.ToolbarSettings.Enabled,
        FloatingToolbarSettings.EdgeName(shellActions.ToolbarSettings.Edge),
        shellActions.ToolbarSettings.AutoHide,
        FormatToolbarVisibility(shellActions.ToolbarVisibility),
        error ?? "",
        shellActions.ToolbarSettings.LingerMs,
        FloatingToolbarSettings.ThemeName(shellActions.ToolbarSettings.Theme),
        shellActions.ToolbarSettings.PeekPixels);

  private static string FormatToolbarVisibility(FloatingToolbarVisibility visibility) =>
    visibility switch
    {
      FloatingToolbarVisibility.UserHidden => "userHidden",
      FloatingToolbarVisibility.EdgeHidden => "edgeHidden",
      FloatingToolbarVisibility.Visible => "visible",
      FloatingToolbarVisibility.Suspended => "suspended",
      _ => "disabled",
    };

  /// <summary>
  /// 全局 MinerU 识别偏好投影：值来自 extra.mineru_recognition，
  /// tier/language 可用性来自当前目录（设置页下拉动态数据源）；
  /// 未保存时 Tier/OcrMode/PageRange/Language 为 null，UI 按目录默认展示。
  /// </summary>
  private static SettingsMineruRecognitionState? ProjectMineruRecognition(
    SettingsViewModel viewModel)
  {
    MineruRecognitionState? state = viewModel.MineruRecognition;
    if (state is null) return null;
    RuntimeSelectionService? selection = viewModel.Selection;
    return new SettingsMineruRecognitionState(
      state.Supported,
      state.Stored,
      state.Tier is { } tier ? WireTierName(tier) : null,
      state.OcrMode is { } ocrMode ? WireOcrModeName(ocrMode) : null,
      state.PageRange,
      state.Language,
      state.Invalid,
      state.InvalidReason,
      selection?.SupportsMineruConfig is true
        ? [.. selection.MineruTiers.Select(tier => new SettingsMineruTierOptionState(
            tier.Id, tier.Availability, tier.ReasonCode))]
        : null,
      selection?.SupportsMineruConfig is true ? selection.MineruLanguages : null,
      selection?.MineruDefaultTier);
  }

  private static string WireTierName(MineruTier tier) => tier switch
  {
    MineruTier.Flash => "flash",
    MineruTier.Basic => "basic",
    MineruTier.Standard => "standard",
    MineruTier.Advanced => "advanced",
    _ => tier.ToString(),
  };

  private static string WireOcrModeName(MineruOcrMode mode) => mode switch
  {
    MineruOcrMode.Auto => "auto",
    MineruOcrMode.Txt => "txt",
    MineruOcrMode.Ocr => "ocr",
    _ => mode.ToString(),
  };

  private SettingsWorkbenchState SettingsState(
    SettingsViewModel viewModel,
    string? toolbarError = null) => new(
    theme,
    viewModel.IsBusy,
    viewModel.RestartRequired ? "settings.restartRequired" : "settings.ready",
    viewModel.Backend,
    shell.Value.StartWithSystem,
    [.. viewModel.Sources.Select(source => new SettingsSourceOptionState(
      source.Kind,
      source.Id,
      source.DisplayName,
      source.Selected))],
    viewModel.PendingBackend,
    viewModel.CanSwitchBackend,
    [.. viewModel.Features.Select(feature => new SettingsFeatureOptionState(
      feature.FeatureId,
      feature.DisplayName,
      feature.Accelerator,
      feature.Selected))],
    HotkeyActions: HotkeyActionStates(),
    FloatingToolbar: ToolbarProjection(toolbarError),
    Maintenance: new SettingsMaintenanceState(
      viewModel.Maintenance.State.IsRunning,
      viewModel.Maintenance.State.StatusCode,
      viewModel.Maintenance.State.OperationId,
      viewModel.Maintenance.State.RequestedComponentIds,
      viewModel.Maintenance.State.EffectiveComponentIds,
      viewModel.Maintenance.State.RequestedSourceIds,
      viewModel.Maintenance.State.EffectiveSourceIds,
      viewModel.Maintenance.State.CanCancel,
      viewModel.Maintenance.State.CanRetry,
      viewModel.Maintenance.State.FailureReason,
      viewModel.Maintenance.State.FailureCode),
    StatusMessage: viewModel.Status,
    ServiceStatus: viewModel.RuntimeStatus.ServiceStatus,
    MaintenanceStatus: viewModel.RuntimeStatus.Status,
    MaintenancePhase: viewModel.RuntimeStatus.Phase,
    ProgressActive: viewModel.RuntimeStatus.IsOperationActive,
    ProgressText: viewModel.RuntimeStatus.ProgressText,
    ProgressDetail: viewModel.RuntimeStatus.ProgressDetail,
    ProgressPercent: viewModel.RuntimeStatus.IsProgressIndeterminate ? null : viewModel.RuntimeStatus.ProgressValue,
    CanPreviewInstall: viewModel.Maintenance.SupportsInstallPlan,
    InstallPlan: viewModel.Maintenance.Plan,
    MineruConnection: viewModel.MineruConnection is { } connection
      ? new SettingsMineruConnectionState(
        connection.Supported,
        connection.Mode,
        connection.ApiUrl,
        connection.HasApiKey)
      : null,
    DefaultRecognitionMode: viewModel.DefaultRecognitionMode is { } defaultMode
      ? new SettingsDefaultRecognitionModeState(
        defaultMode.Supported,
        defaultMode.ModeId,
        defaultMode.Stored)
      : null,
    MineruRecognition: ProjectMineruRecognition(viewModel),
    RecognitionModes: viewModel.Selection?.SupportsRecognitionModes is true
      ? [.. viewModel.Selection.RecognitionModes.Select(mode => new SettingsRecognitionModeOptionState(
          mode.Id,
          SettingsViewModel.DisplayName(mode.Id),
          mode.Availability,
          mode.ReasonCode))]
      : null,
    Environments: viewModel.Environments?.Snapshot?.Environments.Select(item =>
      new SettingsEnvironmentState(
        item.Id, item.Name, item.Revision, item.Kind, item.Status,
        item.PythonState, item.DependencyState, item.EngineState, item.ModelState,
        item.ServiceState,
        item.ConfiguredRecognitionTypes ?? [],
        item.TargetDevice, item.ActualDevice, item.Reason,
        item.PythonVersion, item.Abi, item.Python, item.Path, item.DiskBytes,
        item.SourceIds ?? [],
        item.OverrideSourceIds ?? [],
        item.UnknownSourceIds ?? [],
        item.ResolvedSources?.Select(source => new SettingsEnvironmentResolvedSourceState(
          source.Kind, source.Id, source.DisplayName, source.Origin)).ToArray(),
        item.LastInstallFailure is { } failure
          ? new SettingsEnvironmentInstallFailureState(
            failure.Phase, failure.EnvironmentRevision, failure.Recipe,
            failure.ReasonCode, failure.NextAction, failure.Detail,
            failure.RequestedSourceIds, failure.EffectiveSourceIds)
          : null)).ToArray(),
    ActiveEnvironmentId: viewModel.Environments?.Snapshot?.ActiveId,
    EnvironmentPlan: viewModel.Environments?.Plan is { } environmentPlan
      ? new SettingsEnvironmentPlanState(
        environmentPlan.PlanId, environmentPlan.EnvironmentId,
        environmentPlan.Recipe, environmentPlan.SourceIds,
        environmentPlan.Dependencies ?? [],
        environmentPlan.RequestedRecipe ?? environmentPlan.Recipe,
        environmentPlan.RequestedSourceIds,
        environmentPlan.EnvironmentRevision,
        environmentPlan.Sources?.Select(source => new SettingsEnvironmentPlanSourceState(
          source.Id, source.Kind, source.DisplayName, source.Endpoint,
          source.Requested, source.InheritedFrom, source.Usage, source.ActualEndpoint)).ToArray(),
        environmentPlan.DependencyOrigin, environmentPlan.PythonOrigin,
        environmentPlan.RuntimeWheelOrigin)
      : null,
    EnvironmentStatus: viewModel.Environments?.Status ?? "",
    // 远程模式入口准备期（含各步之间的空窗）同样锁定环境区与 MinerU
    // 编辑器：不能只依赖各步 RunAsync 的瞬时 IsBusy。
    EnvironmentBusy: (viewModel.Environments?.IsBusy ?? false) ||
      Volatile.Read(ref environmentSwitching) != 0 || Volatile.Read(ref remoteHostPreparing) != 0,
    EnvironmentSources: viewModel.Environments?.Snapshot?.Sources?.Select(source =>
      new SettingsEnvironmentSourceState(
        source.Id, source.Kind, source.DisplayName, source.Endpoint, source.IsDefault)).ToArray(),
    EnvironmentDefaultSourceIds: viewModel.Environments?.Snapshot?.DefaultSourceIds,
    EnvironmentResolvedDefaultSources: viewModel.Environments?.Snapshot?.ResolvedDefaultSources?.Select(source =>
      new SettingsEnvironmentResolvedSourceState(source.Kind, source.Id, source.DisplayName, source.Origin)).ToArray(),
    EnvironmentUnknownDefaultSourceIds: viewModel.Environments?.Snapshot?.UnknownDefaultSourceIds,
    EnvironmentPackageSourceIds: viewModel.Environments?.Snapshot?.PackageSourceIds,
    EnvironmentCanCancelInstall: viewModel.Environments?.CanCancelInstall ?? false,
    EnvironmentInstallProgress: viewModel.Environments?.InstallProgress,
    EnvironmentInstallLog: viewModel.Environments?.InstallLog,
    EnvironmentSupportsInstallProgress: viewModel.Environments?.SupportsInstallProgress ?? false,
    EnvironmentRecipes: viewModel.Environments?.Snapshot?.Recipes?.Select(recipe =>
      new SettingsEnvironmentRecipeState(
        recipe.Id, recipe.DisplayName, recipe.ConfiguredRecognitionTypes,
        recipe.Accelerator, recipe.TargetDevice, recipe.PythonVersion, recipe.Abi,
        recipe.Platform, recipe.ScopeId, recipe.ComponentIds, recipe.RecipeLock,
        recipe.Dependencies, recipe.DependencyOrigin, recipe.PythonOrigin,
        recipe.RuntimeWheelOrigin)).ToArray(),
    EnvironmentHardware: viewModel.Environments?.Snapshot?.Hardware is { } hardware
      ? new SettingsEnvironmentHardwareState(
        hardware.NvidiaDriver?.Status ?? "unknown",
        hardware.NvidiaDriver?.ReasonCode,
        hardware.NvidiaDriver?.DriverVersion)
      // 旧 Runtime payload 无 hardware：诚实按未探测呈现，不臆造可用性。
      : new SettingsEnvironmentHardwareState("unknown"),
    EnvironmentSupportsCleanup: viewModel.Environments?.SupportsCleanup ?? false,
    EnvironmentCleanupPlan: viewModel.Environments?.CleanupPlanPage,
    EnvironmentCleanupResult: viewModel.Environments?.CleanupResultPage,
    EnvironmentCleanupPage: viewModel.Environments?.CleanupPage ?? 0,
    EnvironmentCleanupPageCount: viewModel.Environments?.CleanupPageCount ?? 1,
    EnvironmentCanCancelCleanup: viewModel.Environments?.CanCancelCleanup ?? false,
    EnvironmentCompatibility: viewModel.Environments?.Compatibility is { } compatibility
      ? new SettingsEnvironmentCompatibilityState(
        compatibility.Recipe,
        compatibility.Selected?.EnvironmentId,
        compatibility.Selected?.EnvironmentRevision,
        compatibility.Selected?.SelectionReason,
        compatibility.Environments?.Select(match => new SettingsEnvironmentQueryMatchState(
          match.EnvironmentId, match.Name, match.Revision, match.Status,
          match.Active, match.Selected, match.ReasonCode)).ToArray())
      : null);

  internal SettingsWorkbenchState CurrentState() => settings is null ? SettingsShellState() : SettingsState(settings);
  internal void Stop()
  {
    if (settings is not null)
    {
      if (settings.Environments is { } environments) environments.StateChanged -= OnSettingsChanged;
      settings.Maintenance.StateChanged -= OnSettingsChanged;
      settings.RuntimeStatus.PropertyChanged -= OnSettingsPropertyChanged;
      settings.PropertyChanged -= OnSettingsPropertyChanged;
      settings.CancelMaintenance();
    }
    if (shellActions is not null) shellActions.ToolbarStateChanged -= OnToolbarStateChanged;
  }
}
