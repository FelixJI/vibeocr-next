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
    if (phase is not ("create" or "install" or "restart" or "sources") || path is null ||
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
    };
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

  private async Task InstallSmokeRecipeAsync(ManagedEnvironment environment)
  {
    RecordManagedSmokeStage($"select {environment.Name}");
    await SelectSmokeEnvironmentAsync(environment.Id);
    await SelectSmokeValueAsync("#environment-component-select", "rapidocr");
    await ClickManagedSmokeButtonAsync("继续准备依赖");
    await WaitForSmokeDomAsync("!!document.querySelector('.runtime-install-plan button:not(:disabled)') && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('rapidocr-cpu') && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('TUNA PyPI 镜像')",
      TimeSpan.FromMinutes(2));
    RecordManagedSmokeStage($"install {environment.Name}");
    await ClickManagedSmokeButtonAsync("确认安装依赖");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
    while (true)
    {
      ManagedEnvironment? current = smokeEnvironmentSnapshot!()?.Environments
        .SingleOrDefault(item => item.Id == environment.Id);
      if (current?.LastInstallFailure is { Phase: "failed" } failure)
        throw new InvalidOperationException(
          $"Environment install failed: {failure.ReasonCode}: {failure.Detail}");
      if (current?.Status == "installed" && current.Revision > environment.Revision)
      {
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
    id = environment.Id, name = environment.Name, revision = environment.Revision,
    status = environment.Status, python_state = environment.PythonState,
    python = environment.Python, path = environment.Path, recipe = environment.Recipe,
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
          // 服务 attach 早于目录/设置回读完成；等待生产忙碌投影释放后再提交 OCR。
          WorkbenchBootstrap bootstrap = await application.BootstrapAsync(cancellation.Token);
          if (!bootstrap.States.Select(item => item.State).OfType<SettingsWorkbenchState>()
              .Single().EnvironmentBusy)
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
    string selector = stage == "confirmation" ? ".runtime-install-plan" : ".managed-environment-list";
    await WaitForSmokeDomAsync(
      "(() => { const e = document.querySelector(" + JsonSerializer.Serialize(selector) +
      "); if (!e || !e.querySelector('button:not(:disabled)')) return false; " +
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
