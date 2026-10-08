using ManagedEnvironmentInstallProgress = VibeOCR.Runtime.Contracts.Generated.Host.ManagedEnvironmentInstallEvent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Workbench;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  private const string SmokeEnvironmentA = "RapidOCR · CPU";
  private const string SmokeEnvironmentB = "Smoke B";
  private string managedSmokeStage = "starting";
  private readonly List<object> managedInstallProgressEvidence = [];

  private void RecordManagedSmokeStage(string stage)
  {
    managedSmokeStage = stage;
    AppLog.Info($"Managed environment smoke: {stage}");
  }

  private async Task CompleteManagedEnvironmentE2eSmokeAsync()
  {
    string? path = Environment.GetEnvironmentVariable("VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH");
    string? phase = Environment.GetEnvironmentVariable("VIBEOCR_MANAGED_ENVIRONMENT_E2E_PHASE");
    string expected = Path.Combine(Directory.GetParent(layout.InstallRoot)!.FullName,
      $"managed-environment-{phase}.json");
    if (phase is not ("create" or "install" or "restart" or "sources" or "progress" or "cleanup") || path is null ||
        !string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
    {
      Close();
      return;
    }

    try
    {
      if (screenshotSmokePicker is null || smokeSubmitAttempts is null ||
          smokeLastJobId is null || smokeInferenceAttached is null ||
          smokeEnvironmentSnapshot is null || smokeManagedSession is null ||
          smokeInstallAttempts is null)
        throw new InvalidOperationException("Managed environment smoke dependencies are missing.");

      RecordManagedSmokeStage(phase);
      object evidence = phase switch
      {
        "create" => await CreateSmokeEnvironmentsAsync(),
        "install" => await InstallAndSwitchSmokeEnvironmentsAsync(),
        "sources" => await CompleteSourceSettingsSmokeAsync(),
        "progress" => await CompleteInstallProgressSmokeAsync(),
        "cleanup" => await CompleteCleanupSmokeAsync(),
        _ => await VerifyRestartedSmokeEnvironmentAsync(),
      };
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = "passed",
        phase,
        install_attempts = smokeInstallAttempts(),
        evidence,
      }));
    }
    catch (Exception error)
    {
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = "failed",
        phase,
        stage = managedSmokeStage,
        error = error.ToString(),
        install_attempts = smokeInstallAttempts?.Invoke(),
        install_progress = managedInstallProgressEvidence,
      }));
    }
    Close();
  }

  private async Task<object> CreateSmokeEnvironmentsAsync()
  {
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
    await WaitForPaddleSmokeSnapshotAsync(TimeSpan.FromMinutes(1));
    await SelectSmokeValueAsync("#environment-component-select", "rapidocr");
    await SelectSmokeValueAsync("#environment-device-select", "cpu");
    await ClickManagedSmokeButtonAsync("准备此配置");
    await WaitForSmokeEnvironmentsAsync([SmokeEnvironmentA], TimeSpan.FromMinutes(5));
    await WaitForSmokeDomAsync(
      "!!document.querySelector('.runtime-install-plan button:not(:disabled)') && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('rapidocr-cpu') && " +
      "!document.querySelector('.managed-environment-advanced') && " +
      "!document.querySelector(\"input[aria-label='新环境名称（留空自动命名）']\")",
      TimeSpan.FromMinutes(2));
    await SaveManagedSmokePreviewAsync("confirmation");
    await ClickManagedSmokeButtonAsync("取消");
    // 第二个空环境只用于验证隔离和旧记录兼容；用户不再需要手工创建。
    WorkbenchCommandReceipt created = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(Guid.NewGuid(), new CreateEnvironmentCommand(SmokeEnvironmentB)),
      CancellationToken.None);
    if (!created.Ok)
      throw new InvalidOperationException($"Smoke fixture creation failed: {created.Error?.Code}");
    ManagedEnvironmentList list = await WaitForSmokeEnvironmentsAsync(
      [SmokeEnvironmentA, SmokeEnvironmentB], TimeSpan.FromMinutes(5));
    if (list.ActiveId is not null || smokeInferenceAttached!() || smokeInstallAttempts!() != 0)
      throw new InvalidOperationException("Empty environment creation started a service or installer.");
    ManagedEnvironment[] environments = SmokePair(list);
    if (environments.Any(item => item.Status != "empty" || item.PythonState != "ready"))
      throw new InvalidOperationException("Named environments are not executable empty environments.");
    // 空闲（无服务/无维护）不得渲染滚动维护进度；目标设备未读取真实
    // 快照前不得冒充任何设备（AC1）。
    await WaitForSmokeDomAsync(
      "(() => { const panel = document.querySelector('.settings-runtime-panel'); return !!panel && " +
      "panel.textContent.includes('默认运行环境：RapidOCR · CPU（尚未启动）') && " +
      "!panel.querySelector('[role=progressbar]'); })()",
      TimeSpan.FromSeconds(30));
    await SaveManagedSmokePreviewAsync("environment-list");
    return new { active_id = list.ActiveId, environments = environments.Select(SmokeEnvironmentEvidence) };
  }

  private async Task<object> InstallAndSwitchSmokeEnvironmentsAsync()
  {
    if (smokeInferenceAttached!() || smokeInstallAttempts!() != 0)
      throw new InvalidOperationException("Restarting empty environments installed or started a service.");
    await NavigateSmokeAsync("单次识别", "button");
    await ClickManagedSmokeButtonAsync("截图");
    RecognitionWorkbenchState captured = await WaitForScreenshotStateAsync(
      state => !state.IsBusy && state.ScreenshotSession is not null, TimeSpan.FromSeconds(30));
    SyntheticScreenRegionPicker.CaptureEvidence capture = screenshotSmokePicker!.Evidence ??
      throw new InvalidOperationException("Synthetic screenshot was not captured.");
    if (captured.Result is not null || smokeSubmitAttempts!() != 0 || smokeInstallAttempts!() != 0)
      throw new InvalidOperationException("Empty-environment screenshot submitted OCR or installed dependencies.");

    await NavigateSmokeAsync("二维码与条码", "#qr-content");
    await EnterSmokeTextAsync("#qr-content", "VibeOCR managed environment smoke 123");
    await ClickManagedSmokeButtonAsync("生成图片");
    await WaitForSmokeDomAsync("document.querySelector('.qr-resource-preview')?.naturalWidth > 0",
      TimeSpan.FromSeconds(15));
    if (smokeInferenceAttached() || smokeInstallAttempts() != 0)
      throw new InvalidOperationException("Local QR generation installed or started a service.");

    // 导航证明用稳定 runtime 面板：冷启动权威清单未到时 React 不渲染
    // 环境列表（environments.length===0），名单由随后等待覆盖。
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
    ManagedEnvironmentList empty = await WaitForSmokeEnvironmentsAsync(
      [SmokeEnvironmentA, SmokeEnvironmentB], TimeSpan.FromMinutes(2));
    ManagedEnvironment[] initial = SmokePair(empty);
    if (initial.Any(item => item.Status != "empty") || empty.ActiveId is not null)
      throw new InvalidOperationException("Empty environment state changed across restart.");
    WorkbenchCommandReceipt sources = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(Guid.NewGuid(), new SetEnvironmentSourcesCommand(null, "tuna-pypi", null)),
      CancellationToken.None);
    if (!sources.Ok) throw new InvalidOperationException("Managed fixture source selection failed.");
    await InstallSmokeRecipeAsync(initial[0]);
    await InstallSmokeRecipeAsync(initial[1]);
    if (smokeInstallAttempts() != 2)
      throw new InvalidOperationException("Expected two real manager install attempts.");
    ManagedEnvironment[] installed = SmokePair(smokeEnvironmentSnapshot!() ??
      throw new InvalidOperationException("Installed environment state is unavailable."));

    object first = await SwitchAndRecognizeSmokeAsync(initial[0]);
    object second = await SwitchAndRecognizeSmokeAsync(initial[1]);
    int beforeSwitchBack = smokeInstallAttempts();
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
    await SelectSmokeEnvironmentAsync(initial[0].Id);
    await ClickManagedSmokeButtonAsync("切换到此环境");
    ManagedEnvironmentSession returned = await WaitForSmokeSessionAsync(initial[0].Id);
    if (smokeInstallAttempts() != beforeSwitchBack)
      throw new InvalidOperationException("Switching back invoked the installer.");
    return new
    {
      empty_restart = initial.Select(SmokeEnvironmentEvidence),
      installed_environments = installed.Select(SmokeEnvironmentEvidence),
      capture,
      local_qr_ready = true,
      recognition = new[] { first, second },
      switch_back = new { environment_id = returned.EnvironmentId, revision = returned.Revision },
      install_attempts_after_switch_back = smokeInstallAttempts(),
      install_progress = managedInstallProgressEvidence,
    };
  }

  private async Task<object> CompleteCleanupSmokeAsync()
  {
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
    ManagedEnvironmentList list = await WaitForSmokeEnvironmentsAsync(
      [SmokeEnvironmentA, SmokeEnvironmentB], TimeSpan.FromMinutes(2));
    ManagedEnvironment[] pair = SmokePair(list);
    if (list.ActiveId != pair[0].Id || pair.Any(item => item.Status != "installed"))
      throw new InvalidOperationException("Cleanup requires the isolated installed A/B fixture.");
    await ClickManagedSmokeButtonAsync("检查可清理项");
    await WaitForSmokeDomAsync("document.querySelector('[aria-label=可清理项]')?.textContent.includes('Smoke B') === true",
      TimeSpan.FromMinutes(2));
    SettingsWorkbenchState preview = (await application.BootstrapAsync(CancellationToken.None))
      .States.Select(item => item.State).OfType<SettingsWorkbenchState>().Single();
    ManagedCleanupPlan plan = preview.EnvironmentCleanupPlan ?? throw new InvalidOperationException("Cleanup plan missing.");
    if (plan.Items.Single(item => item.Id == $"environment:{pair[0].Id}").CanClean ||
        !plan.Items.Single(item => item.Id == $"environment:{pair[1].Id}").CanClean)
      throw new InvalidOperationException("Cleanup protection does not match active/idle environments.");
    string selected = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      "(() => { const row = [...document.querySelectorAll('[aria-label=可清理项] > li')].find(x => x.textContent.includes('Smoke B')); " +
      "const box = row?.querySelector('input[type=checkbox]'); if (!box || box.disabled) return false; box.click(); return true; })()");
    if (selected != "true") throw new InvalidOperationException("Idle environment cleanup checkbox is unavailable.");
    await ClickManagedSmokeButtonAsync("预览所选清理影响");
    await SaveManagedSmokePreviewAsync("cleanup-confirmation");
    await ClickManagedSmokeButtonAsync("确认清理所选项目");
    await WaitForSmokeDomAsync("document.querySelector('[aria-label=清理结果]')?.textContent.includes('已移除') === true",
      TimeSpan.FromMinutes(5));
    SettingsWorkbenchState completed = (await application.BootstrapAsync(CancellationToken.None))
      .States.Select(item => item.State).OfType<SettingsWorkbenchState>().Single();
    ManagedCleanupResult result = completed.EnvironmentCleanupResult ?? throw new InvalidOperationException("Cleanup result missing.");
    if (result.Items.Count != 1 || result.Items[0].Id != $"environment:{pair[1].Id}" || result.Items[0].State != "deleted" ||
        Directory.Exists(pair[1].Path) || !Directory.Exists(pair[0].Path) ||
        smokeEnvironmentSnapshot!()!.Environments.Any(item => item.Id == pair[1].Id))
      throw new InvalidOperationException("Cleanup result disagrees with registry or actual directories.");
    await SaveManagedSmokePreviewAsync("cleanup-result");
    object recognition = await SwitchAndRecognizeSmokeAsync(pair[0]);
    if (smokeInstallAttempts!() != 0) throw new InvalidOperationException("Retained OCR environment was reinstalled.");
    return new { removed_environment_id = pair[1].Id, retained_environment_id = pair[0].Id,
      removed_path = pair[1].Path, retained_path = pair[0].Path, result, recognition };
  }
  private async Task<object> VerifyRestartedSmokeEnvironmentAsync()
  {
    // 同上：稳定面板作导航证明，冷启动清单由随后的权威名单等待吸收（F7：
    // restart 冷入口列表尚未到达而面板已渲染）。
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
    ManagedEnvironmentList list = await WaitForSmokeEnvironmentsAsync(
      [SmokeEnvironmentA, SmokeEnvironmentB], TimeSpan.FromMinutes(2));
    ManagedEnvironment a = SmokePair(list)[0];
    ManagedEnvironmentSession session = await WaitForSmokeSessionAsync(a.Id);
    await SelectSmokeEnvironmentAsync(a.Id);
    await WaitForSmokeDomAsync(
      "document.querySelector('.settings-runtime-panel')?.textContent.includes('运行时已就绪') === true",
      TimeSpan.FromMinutes(2));
    ManagedEnvironment projected = smokeEnvironmentSnapshot!()!.Environments.Single(item => item.Id == a.Id);
    if (projected.ServiceState != "ready" || projected.Revision != session.Revision)
      throw new InvalidOperationException("Running environment projection stayed stale after startup.");
    if (list.ActiveId != a.Id || a.Revision != session.Revision ||
        smokeInstallAttempts!() != 0 || !smokeInferenceAttached!())
      throw new InvalidOperationException("Restart did not reuse active A without installation.");
    await SaveManagedSmokePreviewAsync("environment-list");
    return new
    {
      active_id = list.ActiveId,
      active_revision = list.ActiveRevision,
      python = a.Python,
      service_state = session.Status.ServiceState.ToString(),
      projected_service_state = projected.ServiceState,
    };
  }

  private async Task<object> CompleteInstallProgressSmokeAsync()
  {
    string recipe = Environment.GetEnvironmentVariable("VIBEOCR_MANAGED_PROGRESS_RECIPE") ?? "rapidocr-cpu";
    if (recipe is not ("rapidocr-cpu" or "paddleocr-cpu"))
      throw new InvalidOperationException("Progress smoke supports isolated CPU recipes only.");
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
    await WaitForPaddleSmokeSnapshotAsync(TimeSpan.FromMinutes(1));
    WorkbenchCommandReceipt created = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(Guid.NewGuid(), new CreateEnvironmentCommand("安装进度验收")),
      CancellationToken.None);
    if (!created.Ok) throw new InvalidOperationException("Progress fixture creation failed.");
    ManagedEnvironmentList list = await WaitForSmokeEnvironmentsAsync(["安装进度验收"], TimeSpan.FromMinutes(2));
    ManagedEnvironment target = list.Environments.Single(item => item.Name == "安装进度验收");
    WorkbenchCommandReceipt sources = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(Guid.NewGuid(), new SetEnvironmentSourcesCommand(null, "tuna-pypi", null)),
      CancellationToken.None);
    if (!sources.Ok) throw new InvalidOperationException("Progress fixture source selection failed.");
    await InstallSmokeRecipeAsync(target, recipe);
    ManagedEnvironment installed = smokeEnvironmentSnapshot!()!.Environments.Single(item => item.Id == target.Id);
    return new { installed = SmokeEnvironmentEvidence(installed), install_progress = managedInstallProgressEvidence };
  }

  private async Task InstallSmokeRecipeAsync(ManagedEnvironment environment, string recipe = "rapidocr-cpu")
  {
    RecordManagedSmokeStage($"select {environment.Name}");
    await SelectSmokeEnvironmentAsync(environment.Id);
    await SelectSmokeValueAsync("#environment-component-select", recipe.StartsWith("paddleocr", StringComparison.Ordinal) ? "paddleocr" : "rapidocr");
    await ClickManagedSmokeButtonAsync("继续准备依赖");
    await WaitForSmokeDomAsync("!!document.querySelector('.runtime-install-plan button:not(:disabled)') && " +
      $"document.querySelector('.runtime-install-plan')?.textContent.includes('{recipe}') && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('TUNA PyPI 镜像')",
      TimeSpan.FromMinutes(2));
    RecordManagedSmokeStage($"install {environment.Name}");
    await ClickManagedSmokeButtonAsync("确认安装依赖");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
    var phases = new HashSet<string>(StringComparer.Ordinal);
    var observations = new List<object>();
    bool liveLog = false;
    bool capturedLiveLog = false;
    while (true)
    {
      SettingsWorkbenchState settingsState = (await application.BootstrapAsync(timeout.Token))
        .States.Select(item => item.State).OfType<SettingsWorkbenchState>().Single();
      ManagedEnvironmentInstallProgress? progress = settingsState.EnvironmentInstallProgress;
      if (progress?.EnvironmentId == environment.Id && progress.State == "running" &&
          settingsState.EnvironmentCanCancelInstall)
      {
        string visible = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
          "JSON.stringify({phase:document.querySelector('.environment-install-progress')?.dataset.installPhase," +
          "log:document.querySelector('.environment-install-progress pre')?.textContent ?? ''})");
        using JsonDocument dom = JsonDocument.Parse(JsonSerializer.Deserialize<string>(visible) ?? "{}");
        bool phaseVisible = dom.RootElement.TryGetProperty("phase", out JsonElement domPhase) &&
          domPhase.GetString() == progress.Phase;
        string logText = dom.RootElement.GetProperty("log").GetString() ?? "";
        bool hasLog = phaseVisible && settingsState.EnvironmentInstallLog?.Count > 0 &&
          !string.IsNullOrWhiteSpace(logText) && !logText.Contains("等待命令输出", StringComparison.Ordinal);
        liveLog |= hasLog;
        if (hasLog && !capturedLiveLog)
        {
          await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
            "(() => { const log = document.querySelector('.environment-install-progress details:last-child'); if(log && !log.open) log.querySelector('summary').click(); })()");
          await SaveManagedSmokePreviewAsync($"live-progress-{environment.Id}");
          capturedLiveLog = true;
        }
        if (phaseVisible && phases.Add(progress.Phase) || hasLog && observations.Count < 10)
          observations.Add(new
          {
            observed_at = DateTimeOffset.UtcNow,
            event_time = progress.Timestamp,
            progress.Phase,
            progress.Seq,
            dependencies = progress.Dependencies.Count,
            progress.DownloadFilesCompleted,
            progress.DownloadFilesTotal,
            live_log = hasLog,
            dom = visible
          });
      }
      ManagedEnvironment? current = smokeEnvironmentSnapshot!()?.Environments
        .SingleOrDefault(item => item.Id == environment.Id);
      if (current?.LastInstallFailure is { Phase: "failed" } failure)
      {
        managedInstallProgressEvidence.Add(new { environment_id = environment.Id, recipe,
          observed = observations, failure, terminal = progress, logs = settingsState.EnvironmentInstallLog });
        if (progress is null || progress.EnvironmentId != environment.Id || progress.State is not ("failed" or "cancelled"))
          throw new InvalidOperationException("Installation failure did not expose a bound terminal progress state.");
        await WaitForInstallProgressDomAsync(progress);
        await SaveManagedSmokePreviewAsync($"failed-progress-{environment.Id}");
        throw new InvalidOperationException(
          $"Environment install failed: {failure.ReasonCode}: {failure.Detail}");
      }
      if (current?.Status == "installed" && current.Revision > environment.Revision)
      {
        if (phases.Count < 2 || !liveLog || progress?.State != "succeeded" ||
            progress.Dependencies.Any(item => item.InstallState != "installed"))
          throw new InvalidOperationException("Installation did not expose continuous live phases/logs and committed package state.");
        await WaitForInstallProgressDomAsync(progress);
        string terminalDom = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
          "JSON.stringify(document.querySelector('.environment-install-progress')?.dataset)");
        managedInstallProgressEvidence.Add(new
        {
          environment_id = environment.Id,
          recipe,
          observed = observations,
          finished_event_time = progress.Timestamp,
          finished_observed_at = DateTimeOffset.UtcNow,
          terminal = progress,
          terminal_dom = terminalDom
        });
        await SaveManagedSmokePreviewAsync($"progress-{environment.Id}");
        // 安装终态后维护进度动画必须退出，不得残留空闲滚动（AC2）。
        await WaitForSmokeDomAsync(
          "(() => { const panel = document.querySelector('.settings-runtime-panel'); return !!panel && " +
          "!panel.querySelector('[role=progressbar]'); })()",
          TimeSpan.FromSeconds(30));
        return;
      }
      if (current?.Status is "failed" or "unavailable")
        throw new InvalidOperationException($"Environment install failed: {current.Reason}");
      await Task.Delay(250, timeout.Token);
    }
  }

  private Task WaitForInstallProgressDomAsync(ManagedEnvironmentInstallProgress progress)
  {
    string expected = JsonSerializer.Serialize(new
    {
      attempt = progress.AttemptId,
      seq = progress.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
      environment = progress.EnvironmentId,
      state = progress.State,
      phase = progress.Phase,
      installed = progress.Dependencies.Count(item => item.InstallState == "installed").ToString(System.Globalization.CultureInfo.InvariantCulture),
      total = progress.DependencyTotalKnown ? progress.Dependencies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown"
    });
    return WaitForSmokeDomAsync("(() => { const expected = " + expected +
      "; const actual = document.querySelector('.environment-install-progress')?.dataset; return !!actual && " +
      "actual.installAttempt === expected.attempt && actual.installSeq === expected.seq && " +
      "actual.installEnvironment === expected.environment && actual.installState === expected.state && " +
      "actual.installPhase === expected.phase && actual.installInstalled === expected.installed && " +
      "actual.installTotal === expected.total; })()", TimeSpan.FromSeconds(30));
  }

  private async Task<object> SwitchAndRecognizeSmokeAsync(ManagedEnvironment environment)
  {
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
    RecordManagedSmokeStage($"select {environment.Name}");
    await SelectSmokeEnvironmentAsync(environment.Id);
    await ClickManagedSmokeButtonAsync("切换到此环境");
    RecordManagedSmokeStage($"wait for service {environment.Name}");
    ManagedEnvironmentSession session = await WaitForSmokeSessionAsync(environment.Id);
    RecordManagedSmokeStage($"service ready {environment.Name}");
    ManagedEnvironment current = smokeEnvironmentSnapshot!()?.Environments.Single(item =>
      item.Id == environment.Id) ?? throw new InvalidOperationException("Environment disappeared.");
    if (current.Revision != session.Revision || !smokeInferenceAttached!())
      throw new InvalidOperationException("Supervisor does not match selected environment revision.");
    await NavigateSmokeAsync("单次识别", "button");
    int before = smokeSubmitAttempts!();
    string? previousSessionId = (await application.BootstrapAsync(CancellationToken.None))
      .States.Select(item => item.State).OfType<RecognitionWorkbenchState>()
      .Single().ScreenshotSession?.SessionId;
    await ClickManagedSmokeButtonAsync("截图");
    RecognitionWorkbenchState capturedState = await WaitForScreenshotStateAsync(state => !state.IsBusy &&
      state.ScreenshotSession is { } captured &&
      captured.SessionId != previousSessionId && state.Result is null,
      TimeSpan.FromSeconds(30));
    // Host state arrives before React has necessarily replaced the previous
    // editor. Wait for this exact session and its pixels before dispatching OCR.
    // sceneEditing 会话的 canvas-editor 与“识别当前图”只在现场窗口渲染（主窗是
    // EmptyStage）；session DOM 查现场面，识别结果仍在主窗。
    await WaitForSmokeDomAsync(
      "document.querySelector('.canvas-editor')?.dataset.screenshotSession === " +
      JsonSerializer.Serialize(capturedState.ScreenshotSession!.SessionId),
      TimeSpan.FromSeconds(30),
      editorSurface: true);
    await WaitForCanvasAsync();
    RecordManagedSmokeStage($"recognize {environment.Name}");
    await ClickSmokeButtonAsync("识别当前图");
    await WaitForScreenshotStateAsync(state =>
    {
      if (!state.IsBusy && state.StatusCode is "recognition.cancelled" or
          "recognition.modeUnavailable" or "recognition.expired")
        throw new InvalidOperationException($"Managed smoke OCR stopped: {state.StatusCode}");
      return !state.IsBusy && state.Result is not null;
    }, TimeSpan.FromMinutes(35));
    await WaitForSmokeDomAsync("(document.querySelector('.result-document')?.textContent ?? '').includes('VibeOCR') && (document.querySelector('.result-document')?.textContent ?? '').includes('123')",
      TimeSpan.FromSeconds(15));
    if (smokeSubmitAttempts() != before + 1 || string.IsNullOrWhiteSpace(smokeLastJobId!()))
      throw new InvalidOperationException("Synthetic OCR task was not submitted exactly once.");
    string taskId = smokeLastJobId()!;
    var observed = await session.Client.ObserveAsync(taskId, 0, CancellationToken.None);
    if (observed.Snapshot.JobId != taskId ||
        observed.Snapshot.EnvironmentId != session.EnvironmentId ||
        observed.Snapshot.EnvironmentRevision != session.Revision)
      throw new InvalidOperationException("Job record environment binding does not match the active session.");
    return new
    {
      task_id = observed.Snapshot.JobId,
      environment_id = observed.Snapshot.EnvironmentId,
      environment_revision = observed.Snapshot.EnvironmentRevision,
      python = current.Python,
      environment_path = current.Path,
      // The Python path is read from the manager. The script separately probes
      // sys.prefix; it is path evidence, not a value reported by the OCR task.
      python_evidence = "manager_path_pending_read_only_prefix_probe",
      ocr_visible = true,
    };
  }

  private static ManagedEnvironment[] SmokePair(ManagedEnvironmentList list)
  {
    ManagedEnvironment[] pair = [
      list.Environments.Single(item => item.Name == SmokeEnvironmentA),
      list.Environments.Single(item => item.Name == SmokeEnvironmentB),
    ];
    if (pair[0].Id == pair[1].Id) throw new InvalidOperationException("Smoke environment IDs overlap.");
    return pair;
  }

  private static object SmokeEnvironmentEvidence(ManagedEnvironment environment) => new
  {
    id = environment.Id,
    name = environment.Name,
    revision = environment.Revision,
    status = environment.Status,
    python_state = environment.PythonState,
    python = environment.Python,
    path = environment.Path,
    recipe = environment.Recipe,
  };

  private async Task<ManagedEnvironmentList> WaitForSmokeEnvironmentsAsync(
    string[] names, TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    while (true)
    {
      ManagedEnvironmentList? list = smokeEnvironmentSnapshot!();
      if (list is not null && names.All(name => list.Environments.Any(item => item.Name == name)))
        return list;
      await Task.Delay(100, cancellation.Token);
    }
  }

  private async Task<ManagedEnvironmentSession> WaitForSmokeSessionAsync(string id)
  {
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    try
    {
      while (true)
      {
        ManagedEnvironmentSession? session = smokeManagedSession!();
        if (session?.EnvironmentId == id && smokeInferenceAttached!())
        {
          // attach 到设置回读开始前也可能不 busy；必须等同一修订的生产投影就绪。
          WorkbenchBootstrap bootstrap = await application.BootstrapAsync(cancellation.Token);
          ManagedEnvironmentList? projected = smokeEnvironmentSnapshot!();
          if (!bootstrap.States.Select(item => item.State).OfType<SettingsWorkbenchState>()
              .Single().EnvironmentBusy && projected?.ActiveId == id &&
              projected.Environments.Any(item => item.Id == id && item.Revision == session.Revision && item.ServiceState == "ready"))
            return session;
        }
        await Task.Delay(100, cancellation.Token);
      }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
      throw new TimeoutException($"Managed service did not attach: expected={id}, " +
        $"session={smokeManagedSession!()?.EnvironmentId}, attached={smokeInferenceAttached!()}, " +
        $"active={smokeEnvironmentSnapshot!()?.ActiveId}.");
    }
  }

  private async Task NavigateSmokeAsync(string label, string selector)
  {
    string script = "(() => { const a=document.querySelector('a[aria-label=" +
      JsonSerializer.Serialize(label) + "]'); if(!a) return false; a.click(); return true; })()";
    if (await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(script) != "true")
      throw new InvalidOperationException($"Smoke navigation unavailable: {label}");
    await WaitForSmokeDomAsync($"!!document.querySelector({JsonSerializer.Serialize(selector)})",
      TimeSpan.FromSeconds(30));
  }

  private async Task EnterSmokeTextAsync(string selector, string value)
  {
    string script = "(() => { const e=document.querySelector(" + JsonSerializer.Serialize(selector) +
      "); if(!e) return false; const proto=e instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:e instanceof HTMLInputElement?HTMLInputElement.prototype:null; if(!proto) return false; const setter=Object.getOwnPropertyDescriptor(proto,'value').set; " +
      "setter.call(e," + JsonSerializer.Serialize(value) + "); e.dispatchEvent(new Event('input',{bubbles:true})); return true; })()";
    if (await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(script) != "true")
      throw new InvalidOperationException($"Smoke input unavailable: {selector}");
    await Task.Delay(100);
  }

  private string? selectedSmokeEnvironmentId;

  private async Task SelectSmokeEnvironmentAsync(string id)
  {
    await WaitForSmokeDomAsync(
      "(() => { const row=Array.from(document.querySelectorAll('.managed-environment-item'))" +
      ".find(e => e.dataset.environmentId === " + JsonSerializer.Serialize(id) +
      "); if (!row) return false; row.scrollIntoView({block:'nearest'}); return true; })()",
      TimeSpan.FromMinutes(2));
    selectedSmokeEnvironmentId = id;
  }

  private async Task ClickManagedSmokeButtonAsync(string label)
  {
    bool rowAction = label is "切换到此环境" or "启动并验证当前环境" or
      "继续准备依赖" or "重新准备依赖";
    string scope = rowAction
      ? "Array.from(document.querySelectorAll('.managed-environment-item')).find(e => " +
        "e.dataset.environmentId === " + JsonSerializer.Serialize(selectedSmokeEnvironmentId) + ")"
      : "document";
    // 就绪与点击在同一 DOM turn 完成；行操作绑定精确环境 id，不点第一个同名按钮。
    await WaitForSmokeDomAsync(
      "(() => { const root=" + scope + "; if (!root) return false; " +
      "const b=Array.from(root.querySelectorAll('button')).find(b => " +
      "b.textContent?.trim() === " + JsonSerializer.Serialize(label) +
      " && !b.disabled); if (!b) return false; b.click(); return true; })()",
      TimeSpan.FromSeconds(30));
  }

  private async Task SaveManagedSmokePreviewAsync(string stage)
  {
    bool progress = stage.Contains("progress", StringComparison.Ordinal);
    string selector = progress ? ".environment-install-progress" : stage == "confirmation" ? ".runtime-install-plan" : ".managed-environment-list";
    await WaitForSmokeDomAsync(
      "(() => { const e = document.querySelector(" + JsonSerializer.Serialize(selector) +
      "); if (!e" + (progress ? "" : " || !e.querySelector('button:not(:disabled)')") + ") return false; " +
      "e.scrollIntoView({block:'center'}); return true; })()",
      TimeSpan.FromSeconds(30));
    await Task.Delay(100);
    string health = Environment.GetEnvironmentVariable("VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH")
      ?? throw new InvalidOperationException("Managed smoke output is missing.");
    string path = Path.ChangeExtension(health, $".{stage}.png");
    using (new FileStream(path, FileMode.CreateNew)) { }
    StorageFile file = await StorageFile.GetFileFromPathAsync(path);
    using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
    await WorkbenchWebView.CoreWebView2.CapturePreviewAsync(
      CoreWebView2CapturePreviewImageFormat.Png, stream);
    await stream.FlushAsync();
  }

  private async Task SelectSmokeValueAsync(string selector, string value)
  {
    // 设置页刷新期间 Select 合法禁用；就绪（存在+option+enabled）与赋值+change
    // 在同一 DOM turn 完成，避免两次 ExecuteScript 之间的竞态。
    await WaitForSmokeDomAsync(
      "(() => { const e=document.querySelector(" + JsonSerializer.Serialize(selector) +
      "); if(!e || e.disabled || !Array.from(e.options).some(o => o.value === " +
      JsonSerializer.Serialize(value) + ")) return false; e.value=" + JsonSerializer.Serialize(value) +
      "; e.dispatchEvent(new Event('change',{bubbles:true})); return true; })()",
      TimeSpan.FromMinutes(2));
    await Task.Delay(100);
    // 后置等待：选择触发的 invalidate 会同步清空兼容结果，自动 compat 查询
    // 可能在选择之后补发并重新 busy 禁用控件；等待控件保持目标值且可交互。
    await WaitForSmokeDomAsync(
      "(() => { const e=document.querySelector(" + JsonSerializer.Serialize(selector) +
      "); return !!e && e.value === " + JsonSerializer.Serialize(value) +
      " && !e.disabled; })()",
      TimeSpan.FromMinutes(2));
  }

  private async Task WaitForSmokeDomAsync(string predicate, TimeSpan timeout,
    bool editorSurface = false)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    while (true)
    {
      // 现场面逐次读取：scene 窗口由 DispatcherQueue 异步创建，早绑定会冻结
      // fallback；CoreWebView2 未初始化时继续轮询，不当作 DOM 证据。
      if ((editorSurface ? SmokeEditorWebView : WorkbenchWebView).CoreWebView2 is { } view &&
          await view.ExecuteScriptAsync($"(() => !!({predicate}))()") == "true")
        return;
      try { await Task.Delay(100, cancellation.Token); }
      catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
      {
        // 最小超时诊断：predicate + 该 surface 下 managed Select 值/disabled 与
        // 按钮标签/disabled 概览；不含整页 body 或凭据类内容。
        string probe = "probe-unavailable";
        if ((editorSurface ? SmokeEditorWebView : WorkbenchWebView).CoreWebView2 is { } failed)
          probe = await failed.ExecuteScriptAsync(
            "(() => { const s=document.querySelector('#managed-environment-select'); " +
            "return { managedSelect: s ? { value: s.value, disabled: s.disabled } : null, " +
            "buttons: Array.from(document.querySelectorAll('button')).map(b => " +
            "({ label: (b.textContent || '').trim(), disabled: b.disabled })) }; })()");
        throw new TimeoutException(
          $"Smoke DOM wait timed out after {timeout} on " +
          $"{(editorSurface ? "editor" : "workbench")} surface; predicate={predicate}; probe={probe}");
      }
    }
  }
}
