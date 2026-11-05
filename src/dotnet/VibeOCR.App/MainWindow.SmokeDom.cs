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

  private async Task ClickWorkbenchSmokeButtonAsync(string label)
  {
    await WaitForSmokeDomAsync(
      "(() => { const root=document; " +
      "const b=Array.from(root.querySelectorAll('button')).find(b => " +
      "b.textContent?.trim() === " + JsonSerializer.Serialize(label) +
      " && !b.disabled); if (!b) return false; b.click(); return true; })()",
      TimeSpan.FromSeconds(30));
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
