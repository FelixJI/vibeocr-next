using System.Text.Json;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  /// <summary>
  /// #105 来源设置 smoke（收敛后的简单设置）：真实 WebView2 公开 UI 从两个
  /// 空环境完成统一来源保存 → 预览 → 确认 → 取消（合成中断）→ 重新预览，
  /// 并验证另一环境的解析与状态不变；全程无 Supervisor、无完整安装。
  /// </summary>
  private async Task<object> CompleteSourceSettingsSmokeAsync()
  {
    if (smokeEnvironmentSnapshot is null || smokeInstallAttempts is null ||
        smokeInferenceAttached is null)
      throw new InvalidOperationException("Source settings smoke dependencies are missing.");

    await CreateSmokeEnvironmentsAsync();
    await NavigateSmokeAsync("设置", "#managed-environment-select");
    ManagedEnvironmentList created = await WaitForSmokeEnvironmentsAsync(
      [SmokeEnvironmentA, SmokeEnvironmentB], TimeSpan.FromMinutes(5));
    ManagedEnvironment[] pair = SmokePair(created);
    if (created.ActiveId is not null || smokeInferenceAttached!() ||
        smokeInstallAttempts!() != 0 ||
        pair.Any(item => item.Status != "empty" || item.PythonState != "ready"))
      throw new InvalidOperationException(
        "Source smoke requires two fresh empty environments without a service.");

    RecordManagedSmokeStage("simple source setting");
    // 收敛后的简单设置：依赖与模型各一个全局选择；目录展示名直出；
    // 模型只列两类引擎共同支持的提供方（无百度 BOS），不再渲染单环境
    // override 或第二处“全局默认来源”入口，也没有按次下载源选择。
    await WaitForSmokeDomAsync(
      "document.querySelector('#settings-package-source')?.textContent.includes('TUNA PyPI 镜像') === true && " +
      "document.querySelector('#settings-package-source')?.textContent.includes('PyPI 官方源') === true && " +
      "document.querySelector('#settings-model-source')?.textContent.includes('魔搭') === true && " +
      "document.querySelector('#settings-model-source')?.textContent.includes('百度 BOS') === false",
      TimeSpan.FromSeconds(30));
    await WaitForSmokeDomAsync(
      "Array.from(document.querySelector('#settings-package-source').options).every(o => o.value !== '') && " +
      "Array.from(document.querySelector('#settings-model-source').options).every(o => o.value !== '') && " +
      "document.querySelector('details.managed-source-config') === null && " +
      "document.querySelector('details.managed-source-defaults') === null && " +
      "document.querySelector('#managed-recipe-select') === null && " +
      "document.querySelector('#managed-package-source-select') === null",
      TimeSpan.FromSeconds(30));
    // 新状态的 Runtime 默认与界面默认必须同时为 TUNA / 魔搭。
    if (ResolvedSmokeSource(pair[0], "paddleocr_model_registry") != "paddleocr-modelscope" ||
        ResolvedSmokeSource(pair[0], "mineru_model_registry") != "mineru-modelscope")
      throw new InvalidOperationException("Runtime model defaults must use ModelScope.");
    await WaitForSmokeDomAsync(
      "document.querySelector('#settings-package-source')?.value === 'tuna-pypi' && " +
      "document.querySelector('#settings-model-source')?.value === 'modelscope'",
      TimeSpan.FromSeconds(30));

    RecordManagedSmokeStage("save unified sources");
    // 保存统一来源 → PyPI + 魔搭：所有环境解析跟随（无残留 override），
    // 且不触发下载/安装/服务。
    await SelectSmokeValueAsync("#settings-package-source", "pypi");
    await SelectSmokeValueAsync("#settings-model-source", "modelscope");
    await ClickManagedSmokeButtonAsync("保存下载来源");
    await WaitForSmokeSnapshotAsync(list =>
      (list.DefaultSourceIds ?? []).Order().SequenceEqual(
        new[] { "pypi", "paddleocr-modelscope", "mineru-modelscope" }.Order()) &&
      list.Environments.All(item =>
        ResolvedSmokeSource(item, "package_index") == "pypi" &&
        ResolvedSmokeSource(item, "paddleocr_model_registry") == "paddleocr-modelscope" &&
        ResolvedSmokeSource(item, "mineru_model_registry") == "mineru-modelscope" &&
        (item.OverrideSourceIds ?? []).Count == 0),
      TimeSpan.FromSeconds(30));
    if (smokeInstallAttempts!() != 0 || smokeInferenceAttached!())
      throw new InvalidOperationException("Saving unified sources installed or started anything.");

    RecordManagedSmokeStage("preview follow");
    // 空环境 A、统一选择默认“文字识别 · CPU”：预览跟随已保存的统一来源。
    await SelectSmokeEnvironmentAsync(pair[0].Id);
    await ClickManagedSmokeButtonAsync("预览依赖");
    await WaitForSmokeDomAsync(
      "document.querySelector('.runtime-install-plan')?.textContent.includes('下载来源：PyPI 官方源') === true && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('ModelScope') === true",
      TimeSpan.FromMinutes(2));

    RecordManagedSmokeStage("confirm then cancel");
    // 通过公开按钮确认/取消；只读操作日志用于确认实际安装已启动，
    // 避免定时猜测启动速度，也不以重试隐藏取消抢跑。
    string planId = await ReadSmokeInstallPlanIdAsync(pair[0].Id);
    await ClickManagedSmokeButtonAsync("确认安装依赖");
    string journal = Path.Combine(layout.StateRoot, "state", "environments.json");
    using (var started = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
    {
      while (true)
      {
        using JsonDocument registry = JsonDocument.Parse(await File.ReadAllTextAsync(journal, started.Token));
        JsonElement record = registry.RootElement.GetProperty("environments").GetProperty(pair[0].Id);
        int revision = record.GetProperty("revision").GetInt32();
        if (revision != pair[0].Revision)
          // 成功提交会剔除 last_install_operation 并推进 revision：
          // 立即失败，不等取消路径超时。
          throw new InvalidOperationException(
            $"Install committed before cancel: revision={revision}.");
        if (record.TryGetProperty("last_install_operation", out JsonElement operation) &&
            operation.GetProperty("plan_id").GetString() == planId)
        {
          string? phase = operation.GetProperty("phase").GetString();
          if (phase == "installing") break;
          if (phase == "failed")
            throw new InvalidOperationException(
              $"Install failed before cancel: reason_code=" +
              $"{operation.GetProperty("reason_code").GetString()}, " +
              $"detail={operation.GetProperty("detail").GetString()}.");
        }
        await Task.Delay(100, started.Token);
      }
    }
    ManagedEnvironmentList cancelled = await CancelSmokeInstallAndWaitForOutcomeAsync(
      pair[0].Id, planId, pair[0].Revision);
    ManagedEnvironment interrupted = BySmokeId(cancelled, pair[0].Id);
    var interruptedFailure = interrupted.LastInstallFailure!;
    if (smokeInstallAttempts!() != 1 || interrupted.Status != "empty" ||
        interrupted.Revision != pair[0].Revision)
      throw new InvalidOperationException(
        "Cancelled install changed the environment beyond the durable failure record.");
    if (!(interruptedFailure.EffectiveSourceIds ?? []).Order().SequenceEqual(
          new[] { "pypi", "paddleocr-modelscope", "mineru-modelscope" }.Order()) ||
        interruptedFailure.RequestedSourceIds is not null)
      throw new InvalidOperationException(
        "Cancelled install lost its frozen requested/effective source evidence.");

    RecordManagedSmokeStage("re-preview keeps evidence");
    await ClickManagedSmokeButtonAsync("预览依赖");
    await WaitForSmokeDomAsync(
      "document.querySelector('.runtime-install-plan')?.textContent.includes('下载来源：PyPI 官方源') === true",
      TimeSpan.FromMinutes(2));
    ManagedEnvironmentList repreviewed = await WaitForSmokeSnapshotAsync(list =>
      JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(BySmokeId(list, pair[0].Id).LastInstallFailure),
        JsonSerializer.SerializeToElement(interruptedFailure)),
      TimeSpan.FromSeconds(30));
    ManagedEnvironment finalB = BySmokeId(repreviewed, pair[1].Id);
    if (finalB.Revision != pair[1].Revision || finalB.Status != "empty" ||
        finalB.LastInstallFailure is not null ||
        ResolvedSmokeSource(finalB, "package_index") != "pypi" ||
        ResolvedSmokeSource(finalB, "paddleocr_model_registry") != "paddleocr-modelscope" ||
        ResolvedSmokeSource(finalB, "mineru_model_registry") != "mineru-modelscope" ||
        (finalB.OverrideSourceIds ?? []).Count != 0)
      throw new InvalidOperationException("Environment B changed during A's preview/cancel cycle.");

    return new
    {
      default_source_ids = repreviewed.DefaultSourceIds ?? [],
      unified_model_provider = "modelscope",
      a = new
      {
        id = pair[0].Id,
        revision = BySmokeId(repreviewed, pair[0].Id).Revision,
        status = BySmokeId(repreviewed, pair[0].Id).Status,
        resolved_package = ResolvedSmokeSource(BySmokeId(repreviewed, pair[0].Id), "package_index"),
        resolved_package_origin = ResolvedSmokeOrigin(BySmokeId(repreviewed, pair[0].Id), "package_index"),
      },
      b = new
      {
        id = pair[1].Id,
        revision = finalB.Revision,
        status = finalB.Status,
        override_source_ids = finalB.OverrideSourceIds ?? [],
        resolved_package = ResolvedSmokeSource(finalB, "package_index"),
        resolved_paddleocr_model = ResolvedSmokeSource(finalB, "paddleocr_model_registry"),
        resolved_mineru_model = ResolvedSmokeSource(finalB, "mineru_model_registry"),
      },
      a_failure = new
      {
        phase = interruptedFailure.Phase,
        reason_code = interruptedFailure.ReasonCode,
        plan_id = interruptedFailure.PlanId,
        requested_source_ids = interruptedFailure.RequestedSourceIds,
        effective_source_ids = interruptedFailure.EffectiveSourceIds ?? [],
      },
      install_attempts = smokeInstallAttempts!(),
      service_attached = smokeInferenceAttached!(),
    };
  }

  private static ManagedEnvironment BySmokeId(ManagedEnvironmentList list, string id) =>
    list.Environments.Single(item => item.Id == id);

  private async Task<string> ReadSmokeInstallPlanIdAsync(string environmentId)
  {
    string planJournal = Path.Combine(
      layout.StateRoot, "state", "environment-plans", $"{environmentId}.json");
    using JsonDocument plan = JsonDocument.Parse(await File.ReadAllTextAsync(planJournal));
    return plan.RootElement.GetProperty("plan_id").GetString()
      ?? throw new InvalidOperationException("Source smoke plan id is missing.");
  }

  /// <summary>
  /// #123：取消后的终态分类。以本次预览的 plan_id 绑定安装记录：
  /// 真实中断才继续；先于取消的自然失败或已提交的成功立即以明确
  /// 错误报告，不空等 install_interrupted 超时，也不把自然失败算通过。
  /// </summary>
  private async Task<ManagedEnvironmentList> CancelSmokeInstallAndWaitForOutcomeAsync(
    string environmentId, string planId, int startRevision)
  {
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    bool cancelSent = false;
    while (true)
    {
      ManagedEnvironmentList? list = smokeEnvironmentSnapshot!();
      ManagedEnvironment? current = list?.Environments
        .SingleOrDefault(item => item.Id == environmentId);
      if (current?.LastInstallFailure is { PlanId: string failurePlanId } failure &&
          failurePlanId == planId &&
          failure.Phase == "failed" &&
          failure.EnvironmentRevision == startRevision)
      {
        if (cancelSent && failure.ReasonCode == "install_interrupted" &&
            current.Revision == startRevision)
          return list!;
        throw new InvalidOperationException(
          $"Cancel did not interrupt this install; durable record: phase={failure.Phase}, " +
          $"reason_code={failure.ReasonCode}, detail={failure.Detail}.");
      }
      // 未知 phase 或无法归属的记录：不报失败/中断，继续有界等待诊断。
      if (current is not null && (current.Revision != startRevision || current.Status != "empty"))
        throw new InvalidOperationException(
          $"Cancel raced with a committed install: revision={current.Revision}, " +
          $"status={current.Status}, recipe={current.Recipe}.");
      if (!cancelSent)
        cancelSent = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
          "(() => { const b=Array.from(document.querySelectorAll('button')).find(b => " +
          "b.textContent?.trim() === '取消安装' && !b.disabled); " +
          "if (!b) return false; b.click(); return true; })()") == "true";
      try { await Task.Delay(100, cancellation.Token); }
      catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
      {
        throw new TimeoutException(
          $"Source settings smoke cancel outcome timed out: plan_id={planId}, " +
          $"revision={current?.Revision.ToString() ?? "<missing>"}, " +
          $"status={current?.Status ?? "<missing>"}, " +
          $"failure={current?.LastInstallFailure?.ReasonCode ?? "<none>"}.");
      }
    }
  }

  private static string? ResolvedSmokeSource(ManagedEnvironment environment, string kind) =>
    environment.ResolvedSources?.FirstOrDefault(item => item.Kind == kind)?.Id;

  private static string? ResolvedSmokeOrigin(ManagedEnvironment environment, string kind) =>
    environment.ResolvedSources?.FirstOrDefault(item => item.Kind == kind)?.Origin;

  private async Task<ManagedEnvironmentList> WaitForSmokeSnapshotAsync(
    Func<ManagedEnvironmentList, bool> predicate, TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
      while (true)
      {
        ManagedEnvironmentList? list = smokeEnvironmentSnapshot!();
        if (list is not null && predicate(list)) return list;
        await Task.Delay(100, cancellation.Token);
      }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
      throw new TimeoutException(
        $"Source settings smoke snapshot wait timed out: active={smokeEnvironmentSnapshot!()?.ActiveId}, " +
        $"attempts={smokeInstallAttempts!()}.");
    }
  }
}
