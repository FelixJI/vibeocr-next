using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using VibeOCR.App.Workbench;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  private string managedSmokeStage = "starting";

  private void RecordManagedSmokeStage(string stage)
  {
    managedSmokeStage = stage;
    AppLog.Info($"Managed environment smoke: {stage}");
  }

  /// <summary>
  /// 预构建运行时树冒烟：启动后台 initialize_default 后，断言随包
  /// paddleocr-ort-directml 产品环境已注册（kind=product、installed）且
  /// active 指向它、会话已附着，然后写健康 JSON 退出。安装/进度/取消
  /// 事务已随在线安装退役，本 fixture 不再驱动它们。
  /// </summary>
  private async Task CompleteManagedEnvironmentE2eSmokeAsync()
  {
    string? path = Environment.GetEnvironmentVariable("VIBEOCR_MANAGED_ENVIRONMENT_E2E_HEALTH");
    string? phase = Environment.GetEnvironmentVariable("VIBEOCR_MANAGED_ENVIRONMENT_E2E_PHASE");
    string expected = Path.Combine(Directory.GetParent(layout.InstallRoot)!.FullName,
      $"managed-environment-{phase}.json");
    if (phase is null || phase.Length is < 1 or > 32 ||
        !phase.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') || path is null ||
        !string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
    {
      Close();
      return;
    }

    try
    {
      if (smokeEnvironmentSnapshot is null || smokeManagedSession is null ||
          smokeInferenceAttached is null)
        throw new InvalidOperationException("Managed environment smoke dependencies are missing.");

      RecordManagedSmokeStage(phase);
      object evidence = await VerifyPrebuiltProductEnvironmentAsync();
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = "passed",
        phase,
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
      }));
    }
    Close();
  }

  private async Task<object> VerifyPrebuiltProductEnvironmentAsync()
  {
    // 先等真实会话附着：initialize_default 在启动后台准备中完成注册并
    // 启动服务；这里只验证事实，不触发任何安装或切换。
    RecordManagedSmokeStage("wait session");
    ManagedEnvironmentSession session = await WaitForSmokeSessionAsync(null, requireProjection: false);
    ManagedEnvironmentList list = await WaitForSmokeProductEnvironmentAsync(
      TimeSpan.FromMinutes(10));
    ManagedEnvironment product = list.Environments.SingleOrDefault(item =>
        string.Equals(item.Kind, "product", StringComparison.Ordinal) &&
        string.Equals(item.Recipe, "paddleocr-ort-directml", StringComparison.Ordinal))
      ?? throw new InvalidOperationException(
        "Prebuilt product environment (kind=product, recipe=paddleocr-ort-directml) is not registered.");
    if (list.ActiveId != product.Id)
      throw new InvalidOperationException(
        $"Active environment does not point at the product environment: active={list.ActiveId}.");
    if (!string.Equals(product.Status, "installed", StringComparison.Ordinal))
      throw new InvalidOperationException(
        $"Prebuilt product environment is not installed: {product.Status} ({product.Reason}).");
    if (product.Revision != session.Revision || session.EnvironmentId != product.Id ||
        !smokeInferenceAttached!())
      throw new InvalidOperationException(
        "Attached session does not match the active prebuilt product environment.");
    await SaveManagedSmokePreviewAsync("environment-list");
    return new
    {
      active_id = list.ActiveId,
      active_revision = list.ActiveRevision,
      environment = SmokeEnvironmentEvidence(product),
      service_state = session.Status.ServiceState.ToString(),
    };
  }

  private static object SmokeEnvironmentEvidence(ManagedEnvironment environment) => new
  {
    id = environment.Id,
    name = environment.Name,
    revision = environment.Revision,
    kind = environment.Kind,
    status = environment.Status,
    python_state = environment.PythonState,
    python = environment.Python,
    path = environment.Path,
    recipe = environment.Recipe,
  };

  /// <summary>等待预构建产品环境出现在快照：按 kind/recipe 匹配（product
  /// 环境的名字是“默认环境”，不随配方目录显示名变化）。</summary>
  private async Task<ManagedEnvironmentList> WaitForSmokeProductEnvironmentAsync(
    TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    while (true)
    {
      ManagedEnvironmentList? list = smokeEnvironmentSnapshot!();
      if (list is not null && list.Environments.Any(item =>
          string.Equals(item.Kind, "product", StringComparison.Ordinal) &&
          string.Equals(item.Recipe, "paddleocr-ort-directml", StringComparison.Ordinal)))
        return list;
      await Task.Delay(100, cancellation.Token);
    }
  }

  private async Task<ManagedEnvironmentSession> WaitForSmokeSessionAsync(
    string? id, bool requireProjection = true)
  {
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    try
    {
      while (true)
      {
        ManagedEnvironmentSession? session = smokeManagedSession!();
        if (session is not null && (id is null || session.EnvironmentId == id) && smokeInferenceAttached!())
        {
          if (!requireProjection) return session;
          // attach 到设置回读开始前也可能不 busy；必须等同一修订的生产投影就绪。
          WorkbenchBootstrap bootstrap = await application.BootstrapAsync(cancellation.Token);
          ManagedEnvironmentList? projected = smokeEnvironmentSnapshot!();
          if (!bootstrap.States.Select(item => item.State).OfType<SettingsWorkbenchState>()
              .Single().EnvironmentBusy && projected is not null && projected.ActiveId == session.EnvironmentId &&
              projected.Environments.Any(item => item.Id == session.EnvironmentId && item.Revision == session.Revision && item.ServiceState == "ready"))
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
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    TimeSpan budget = TimeSpan.FromSeconds(30);
    string query = "document.querySelector('a[aria-label=" + JsonSerializer.Serialize(label) + "]')";
    // bridge-ready acknowledges bootstrap delivery, before React commits navigation.
    await WaitForSmokeDomAsync($"!!{query}", budget);
    string script = "(() => { const a=" + query +
      "; if(!a) return false; a.click(); return true; })()";
    if (await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(script) != "true")
      throw new InvalidOperationException($"Smoke navigation unavailable: {label}");
    TimeSpan remaining = budget - elapsed.Elapsed;
    await WaitForSmokeDomAsync($"!!document.querySelector({JsonSerializer.Serialize(selector)})",
      remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
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

  private Task WaitForManagedSmokeEnvironmentListAsync() => WaitForSmokeDomAsync(
    "!!document.querySelector('.managed-environment-list')", TimeSpan.FromSeconds(30));

  private async Task SelectSmokeEnvironmentAsync(string id)
  {
    await WaitForManagedSmokeEnvironmentListAsync();
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
    // 环境列表位于设置页路由下：截图前先导航（Paddle 冒烟同一入口）。
    await NavigateSmokeAsync("设置", ".managed-environment-list");
    if (stage == "environment-list") await WaitForManagedSmokeEnvironmentListAsync();
    await WaitForSmokeDomAsync(
      "(() => { const e = document.querySelector(" + JsonSerializer.Serialize(".managed-environment-list") +
      "); if (!e) return false; " +
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
            "(() => { const s=document.querySelector('#paddle-backend-select'); " +
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
