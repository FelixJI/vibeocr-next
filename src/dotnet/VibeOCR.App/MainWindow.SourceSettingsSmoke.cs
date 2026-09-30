using System.Text.Json;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  /// <summary>
  /// #105 来源配置 smoke：真实 WebView2 公开 UI 从两个空环境完成
  /// 选源 → 预览 → 确认 → 取消（合成中断）→ 重新预览，
  /// 并验证另一环境的配置与状态不变；全程无 Supervisor、无完整安装。
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

    RecordManagedSmokeStage("catalog names");
    // 目录展示名（非 raw id）出现在公开下拉中。
    await WaitForSmokeDomAsync(
      "document.querySelector('#managed-package-source-select')?.textContent.includes('TUNA PyPI 镜像') === true && " +
      "document.querySelector('#managed-package-source-select')?.textContent.includes('PyPI 官方源') === true",
      TimeSpan.FromSeconds(30));

    RecordManagedSmokeStage("global default");
    // 全局默认 → PyPI：A/B 均继承全局，且不触发下载/安装/服务。
    await WaitForSmokeDomAsync("(() => { const d=document.querySelector('details.managed-source-defaults'); if(!d)return false;if(!d.open)d.querySelector('summary').click();return d.open;})()", TimeSpan.FromSeconds(30));
    await SelectSmokeValueAsync("#managed-global-package-source", "pypi");
    await ClickManagedSmokeButtonAsync("保存全局默认来源");
    ManagedEnvironmentList globalSaved = await WaitForSmokeSnapshotAsync(list =>
      (list.DefaultSourceIds ?? []).Count == 1 && list.DefaultSourceIds![0] == "pypi" &&
      list.Environments.All(item =>
        ResolvedSmokeSource(item, "package_index") == "pypi" &&
        ResolvedSmokeOrigin(item, "package_index") == "global_default"),
      TimeSpan.FromSeconds(30));
    if (smokeInstallAttempts!() != 0 || smokeInferenceAttached!())
      throw new InvalidOperationException("Saving source defaults installed or started anything.");

    RecordManagedSmokeStage("environment override");
    // 环境 B 覆盖依赖包来源 → TUNA；A 的解析与状态保持不变。
    await SelectSmokeEnvironmentAsync(pair[1].Id);
    await WaitForSmokeDomAsync("(() => { const d=document.querySelector('details.managed-source-config'); if(!d)return false;if(!d.open)d.querySelector('summary').click();return d.open;})()", TimeSpan.FromSeconds(30));
    await SelectSmokeValueAsync("#managed-env-package-source", "tuna-pypi");
    await ClickManagedSmokeButtonAsync("保存本环境来源");
    ManagedEnvironmentList overrideSaved = await WaitForSmokeSnapshotAsync(list =>
      ResolvedSmokeSource(BySmokeId(list, pair[1].Id), "package_index") == "tuna-pypi" &&
      ResolvedSmokeOrigin(BySmokeId(list, pair[1].Id), "package_index") == "environment_override" &&
      ResolvedSmokeSource(BySmokeId(list, pair[0].Id), "package_index") == "pypi",
      TimeSpan.FromSeconds(30));
    ManagedEnvironment beforePreview = BySmokeId(overrideSaved, pair[1].Id);
    if (beforePreview.Revision != pair[1].Revision || beforePreview.Status != "empty" ||
        (BySmokeId(overrideSaved, pair[0].Id).OverrideSourceIds ?? []).Count != 0)
      throw new InvalidOperationException("Saving B's override mutated A or B's revision.");

    RecordManagedSmokeStage("preview follow");
    // 空环境 A、跟随配置预览：计划显示请求源=跟随、生效源=PyPI。
    await SelectSmokeEnvironmentAsync(pair[0].Id);
    await SelectSmokeValueAsync("#managed-recipe-select", "rapidocr-cpu");
    await SelectSmokeValueAsync("#managed-package-source-select", "");
    await ClickManagedSmokeButtonAsync("预览依赖");
    await WaitForSmokeDomAsync(
      "document.querySelector('.runtime-install-plan')?.textContent.includes('请求源：跟随配置') === true && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('生效源：PyPI 官方源') === true",
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
    if ((interruptedFailure.EffectiveSourceIds ?? []).Count != 1 ||
        interruptedFailure.EffectiveSourceIds![0] != "pypi" ||
        interruptedFailure.RequestedSourceIds is not null)
      throw new InvalidOperationException(
        "Cancelled install lost its frozen requested/effective source evidence.");

    RecordManagedSmokeStage("re-preview keeps evidence");
    await SelectSmokeValueAsync("#managed-package-source-select", "");
    await ClickManagedSmokeButtonAsync("预览依赖");
    await WaitForSmokeDomAsync(
      "document.querySelector('.runtime-install-plan')?.textContent.includes('请求源：跟随配置') === true",
      TimeSpan.FromMinutes(2));
    ManagedEnvironmentList repreviewed = await WaitForSmokeSnapshotAsync(list =>
      JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(BySmokeId(list, pair[0].Id).LastInstallFailure),
        JsonSerializer.SerializeToElement(interruptedFailure)),
      TimeSpan.FromSeconds(30));
    ManagedEnvironment finalB = BySmokeId(repreviewed, pair[1].Id);
    if (finalB.Revision != beforePreview.Revision || finalB.Status != "empty" ||
        finalB.LastInstallFailure is not null ||
        ResolvedSmokeSource(finalB, "package_index") != "tuna-pypi" ||
        ResolvedSmokeOrigin(finalB, "package_index") != "environment_override")
      throw new InvalidOperationException("Environment B changed during A's preview/cancel cycle.");

    return new
    {
      default_source_ids = repreviewed.DefaultSourceIds ?? [],
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
        resolved_package_origin = ResolvedSmokeOrigin(finalB, "package_index"),
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
