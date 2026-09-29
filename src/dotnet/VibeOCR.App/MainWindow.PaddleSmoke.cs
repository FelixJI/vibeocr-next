using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Workbench;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App;

/// <summary>
/// Goal #110 隔离候选专用五模式冒烟入口（VIBEOCR_SELF_TEST_SMOKE=paddle-modes-e2e）。
/// 复用 ManagedEnvironmentSmoke/ScreenshotSmoke 的真实 helpers 与公共按钮/
/// 选择入口；截图输入来自本进程创建的置顶合成窗口，文件/批量/PDF 输入
/// 来自隔离根内的合成 fixture，不读取真实桌面内容。CPU/缺模型导致的真实拒绝按 blocked/failed 如实记录，
/// 不伪造通过；GPU 仅在另有实证槽验证，本冒烟保持 UNVERIFIED。
/// </summary>
public sealed partial class MainWindow
{
  private const string PaddleSmokeEnvironmentName = "Paddle 冒烟";
  private const string PaddleSmokeRecipe = "paddleocr-cpu";
  // paddleModesSmokeStarted 字段随 MainWindow.xaml.cs 的 OnHostStateChanged
  // 钩子一并声明（见交付说明），本文件只引用不声明，避免未读告警。
  private string paddleSmokeStage = "starting";
  private string paddleSmokeOutcome = "failed";

  private static string? PaddleSmokeEnv(string name) =>
    Environment.GetEnvironmentVariable(name);

  private void RecordPaddleSmokeStage(string stage)
  {
    paddleSmokeStage = stage;
    AppLog.Info($"Paddle modes smoke: {stage}");
  }

  // 五模式自定义选区器不经过 App._screenshotSmokePicker（保持
  // SyntheticScreenRegionPicker 原类型，#104/#107 依赖其 Evidence）；由
  // App.xaml.cs 在 InputService 的 IScreenRegionPicker 参数处按自检环境
  // 变量选择注入（见交付说明的接线补丁）。

  private async Task CompletePaddleModesSmokeAsync()
  {
    string? path = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_HEALTH");
    string? phase = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_PHASE");
    // 仿 ManagedEnvironmentSmoke：health 文件必须位于本次候选父目录
    // （隔离根）内，否则不是本进程的冒烟请求；固定输出拒绝覆盖已存在文件。
    string? healthPath = null;
    if (path is not null)
    {
      string smokeRoot = Directory.GetParent(layout.InstallRoot)!.FullName;
      string full = Path.GetFullPath(path);
      if (!string.Equals(Directory.GetParent(full)!.FullName, smokeRoot,
            StringComparison.OrdinalIgnoreCase))
      {
        Close();
        return;
      }
      if (File.Exists(full))
      {
        // 固定输出拒绝覆盖已存在文件：在 try 内以失败 health 形式落盘
        // 不可能（文件已存在），因此直接记录日志并退出。
        AppLog.Error($"Paddle smoke health file already exists: {full}");
        Close();
        return;
      }
      healthPath = full;
    }
    if (healthPath is null || phase is not ("install" or "recognize" or "inputs"))
    {
      Close();
      return;
    }
    path = healthPath;
    try
    {
      if (smokeEnvironmentSnapshot is null || smokeInstallAttempts is null ||
          smokeManagedSession is null || smokeInferenceAttached is null ||
          smokeSubmitAttempts is null || smokeLastJobId is null)
        throw new InvalidOperationException("Paddle smoke dependencies are missing.");
      object evidence = phase == "install"
        ? await RunPaddleSmokeInstallAsync()
        : await RunPaddleSmokeRecognizeAsync(phase == "inputs");
      string? previewPath = null;
      if (phase != "install")
      {
        previewPath = Path.ChangeExtension(path, ".png");
        using (new FileStream(previewPath, FileMode.CreateNew)) { }
        StorageFile previewFile = await StorageFile.GetFileFromPathAsync(previewPath);
        using IRandomAccessStream preview = await previewFile.OpenAsync(FileAccessMode.ReadWrite);
        await WorkbenchWebView.CoreWebView2.CapturePreviewAsync(
          CoreWebView2CapturePreviewImageFormat.Png, preview);
        await preview.FlushAsync();
      }
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = paddleSmokeOutcome,
        phase,
        stage = paddleSmokeStage,
        mode = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_MODE"),
        preview_path = previewPath,
        evidence,
      }));
    }
    catch (Exception error)
    {
      paddleSmokeOutcome = "failed";
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = paddleSmokeOutcome,
        phase,
        stage = paddleSmokeStage,
        mode = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_MODE"),
        error = error.ToString(),
      }));
    }
    finally
    {
      Close();
    }
  }

  // ------------------------- phase: install -------------------------

  private async Task<object> RunPaddleSmokeInstallAsync()
  {
    RecordPaddleSmokeStage("wait environments");
    await NavigateSmokeAsync("设置", "input[aria-label='新环境名称']");
    ManagedEnvironmentList list = await WaitForPaddleSmokeSnapshotAsync(TimeSpan.FromMinutes(1));
    if (!list.Environments.Any(item => item.Name == PaddleSmokeEnvironmentName))
    {
      RecordPaddleSmokeStage("create empty environment");
      await EnterSmokeTextAsync("input[aria-label='新环境名称']", PaddleSmokeEnvironmentName);
      await ClickManagedSmokeButtonAsync("创建空环境");
      list = await WaitForSmokeEnvironmentsAsync(
        [PaddleSmokeEnvironmentName], TimeSpan.FromMinutes(5));
    }
    ManagedEnvironment environment = list.Environments
      .Single(item => item.Name == PaddleSmokeEnvironmentName);
    if (environment.Status == "installed" && environment.Recipe == PaddleSmokeRecipe)
    {
      paddleSmokeOutcome = "passed";
      return new { reused = true, environment = SmokeEnvironmentEvidence(environment) };
    }
    if (environment.Status != "empty")
      throw new InvalidOperationException(
        $"Unexpected environment status: {environment.Status} ({environment.Reason}).");

    RecordPaddleSmokeStage("preview install plan");
    await SelectSmokeEnvironmentAsync(environment.Id);
    await SelectSmokeValueAsync("#managed-recipe-select", PaddleSmokeRecipe);
    await ClickManagedSmokeButtonAsync("预览依赖");
    await WaitForSmokeDomAsync(
      "!!document.querySelector('.runtime-install-plan button:not(:disabled)') && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('paddleocr-cpu') && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('TUNA PyPI 镜像')",
      TimeSpan.FromMinutes(2));
    string planText = await PaddleSmokeDomTextAsync(".runtime-install-plan") ?? "";
    RecordPaddleSmokeStage("confirm install");
    await ClickManagedSmokeButtonAsync("确认安装依赖");
    int timeoutMinutes = ParsePaddleSmokeMinutes(
      "VIBEOCR_PADDLE_SMOKE_INSTALL_TIMEOUT_MINUTES", 60);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
    while (true)
    {
      ManagedEnvironment? current = smokeEnvironmentSnapshot!()?.Environments
        .SingleOrDefault(item => item.Id == environment.Id);
      if (current?.LastInstallFailure is { Phase: "failed" } failure)
        throw new InvalidOperationException(
          $"{PaddleSmokeRecipe} install failed: {failure.ReasonCode}: {failure.Detail}");
      if (current?.Status == "installed" && current.Revision > environment.Revision)
      {
        paddleSmokeOutcome = "passed";
        return new
        {
          reused = false,
          install_attempts = smokeInstallAttempts!(),
          public_source_plan_preview = planText,
          environment = SmokeEnvironmentEvidence(current),
        };
      }
      if (current?.Status is "failed" or "unavailable")
        throw new InvalidOperationException(
          $"{PaddleSmokeRecipe} install failed: {current.Reason}");
      await Task.Delay(500, timeout.Token);
    }
  }

  // ------------------------ phase: recognize ------------------------

  private async Task<object> RunPaddleSmokeRecognizeAsync(bool inputs)
  {
    string mode = RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_MODE");
    string expectedPipeline = RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_PIPELINE");
    string fixture = ValidatePaddleSmokeOwnedPath(
      RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_FIXTURE"), "fixture");
    if (!File.Exists(fixture)) throw new FileNotFoundException("Fixture missing.", fixture);
    if (PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_DIR") is { Length: > 0 } exportDir)
    {
      // 导出目录同样必须在隔离根内且不在候选副本里；具体导出文件在
      // RunPaddleUiExportAsync 内再次校验并拒绝已存在目标。
      ValidatePaddleSmokeOwnedPath(exportDir, "export dir");
    }
    string optionName = RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_OPTION_NAME");
    string optionValue = RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_OPTION_VALUE");
    string optionKind = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_OPTION_KIND") ?? "bool";
    string[] tokens = (PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_TOKENS") ?? "")
      .Split('|', StringSplitOptions.RemoveEmptyEntries);
    string[] exportButtons = (PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS") ?? "")
      .Split('|', StringSplitOptions.RemoveEmptyEntries);
    int timeoutMinutes = ParsePaddleSmokeMinutes("VIBEOCR_PADDLE_SMOKE_TIMEOUT_MINUTES", 40);

    RecordPaddleSmokeStage("switch environment");
    await NavigateSmokeAsync("设置", "#managed-environment-select");
    ManagedEnvironmentList list = await WaitForSmokeEnvironmentsAsync(
      [PaddleSmokeEnvironmentName], TimeSpan.FromMinutes(2));
    ManagedEnvironment environment = list.Environments
      .Single(item => item.Name == PaddleSmokeEnvironmentName);
    if (environment.Status != "installed" || environment.Recipe != PaddleSmokeRecipe)
      throw new InvalidOperationException(
        $"Environment is not installed with {PaddleSmokeRecipe}: {environment.Status}.");
    await SelectSmokeEnvironmentAsync(environment.Id);
    if (list.ActiveId != environment.Id)
      await ClickManagedSmokeButtonAsync("切换到此环境");
    ManagedEnvironmentSession session = await WaitForSmokeSessionAsync(environment.Id);
    await WaitForSmokeDomAsync(
      "document.querySelector('.settings-runtime-panel')?.textContent.includes('服务 ready') === true",
      TimeSpan.FromMinutes(2));

    RecordPaddleSmokeStage($"select mode {mode}");
    await NavigateSmokeAsync("单次识别", "button");
    await WaitForPaddleModeAsync("#recognition-task-engine", mode);
    string? availability = await PaddleSmokeDomTextAsync(
      "(() => { const o = Array.from(document.querySelector('#recognition-task-engine').options)" +
      $".find(o => o.value === {JsonSerializer.Serialize(mode)}); return o ? o.textContent : ''; }})()");
    await SelectSmokeValueAsync("#recognition-task-engine", mode);

    RecordPaddleSmokeStage($"set option {optionName}={optionValue}");
    await WaitForSmokeDomAsync("!!document.querySelector('details.recognition-options')",
      TimeSpan.FromSeconds(30));
    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      "(() => { document.querySelector('details.recognition-options > summary')?.click(); return true; })()");
    // 元素 id 与 WebAssets/src/components/PaddleOptionsEditor.tsx 一致：
    // bool → `${modeId}-${选项名}`；批量大小 Input → `${modeId}-formula-batch`；
    // 公式模型 Select → `${modeId}-formula-model`。
    string elementId = optionKind switch
    {
      "int" => $"{mode}-formula-batch",
      "enum" when optionName == "formula_recognition_model_name" =>
        $"{mode}-formula-model",
      _ => $"{mode}-{optionName}",
    };
    string optionSelector = $"#{elementId}";
    await WaitForSmokeDomAsync(
      $"!!document.querySelector({JsonSerializer.Serialize(optionSelector)})",
      TimeSpan.FromSeconds(15));
    if (optionKind == "int")
    {
      await EnterSmokeTextAsync(optionSelector, optionValue);
    }
    else
    {
      await SelectSmokeValueAsync(optionSelector, optionValue);
    }
    if (mode == "paddle_structure")
    {
      await WaitForSmokeDomAsync(
        "!!document.querySelector('#paddle_structure-use_chart_recognition')",
        TimeSpan.FromSeconds(15));
      await SelectSmokeValueAsync("#paddle_structure-use_chart_recognition", "true");
    }
    await ClickManagedSmokeButtonAsync("保存参数");
    await WaitForSmokeDomAsync(
      "document.querySelector('details.recognition-options output')?.textContent" +
      ".includes('参数已保存') === true",
      TimeSpan.FromSeconds(15));

    if (inputs)
      return await RunPaddleSmokeInputsAsync(mode, expectedPipeline, fixture,
        optionName, optionValue, availability ?? "", tokens, exportButtons,
        session, timeoutMinutes);

    RecordPaddleSmokeStage("capture synthetic fixture");
    int submitsBefore = smokeSubmitAttempts!();
    string? previousSessionId = (await application.BootstrapAsync(CancellationToken.None))
      .States.Select(item => item.State).OfType<RecognitionWorkbenchState>()
      .Single().ScreenshotSession?.SessionId;
    await ClickSmokeButtonAsync("纯截图");
    RecognitionWorkbenchState captured = await WaitForScreenshotStateAsync(
      state => !state.IsBusy && state.ScreenshotSession is { } capturedSession &&
        capturedSession.SessionId != previousSessionId && state.Result is null,
      TimeSpan.FromSeconds(30));
    SyntheticFixtureRegionPicker.CaptureEvidence? capture =
      SyntheticFixtureRegionPicker.LastCapture;
    if (capture is null)
      throw new InvalidOperationException("Synthetic fixture capture has no evidence.");
    await WaitForSmokeDomAsync(
      "document.querySelector('.canvas-editor')?.dataset.screenshotSession === " +
      JsonSerializer.Serialize(captured.ScreenshotSession!.SessionId),
      TimeSpan.FromSeconds(30));
    await WaitForCanvasAsync();

    RecordPaddleSmokeStage($"recognize {mode}");
    await ClickSmokeButtonAsync("识别当前图");
    RecognitionWorkbenchState terminal;
    using (var cancellation = new CancellationTokenSource(
      TimeSpan.FromMinutes(timeoutMinutes)))
    {
      terminal = await PollPaddleTerminalAsync(submitsBefore, cancellation.Token);
    }
    return await CollectPaddleRecognitionEvidenceAsync(
      mode, expectedPipeline, optionName, optionValue, availability ?? throw new InvalidOperationException("Mode availability is missing."), tokens,
      exportButtons, session, submitsBefore, terminal, smokeLastJobId!(), capture);
  }

  private async Task WaitForPaddleModeAsync(string selector, string mode)
  {
    try
    {
      await WaitForSmokeDomAsync(
        $"Array.from(document.querySelector({JsonSerializer.Serialize(selector)})?.options ?? [])" +
        $".some(o => o.value === {JsonSerializer.Serialize(mode)})",
        TimeSpan.FromMinutes(2));
    }
    catch (OperationCanceledException error)
    {
      string? dom = await PaddleSmokeDomTextAsync(
        "(() => JSON.stringify({options:Array.from(document.querySelector('" +
        selector + "')?.options ?? []).map(o => ({value:o.value,text:o.textContent}))," +
        "page:(document.body?.innerText ?? '').slice(0,1200)}))()");
      RecognitionWorkbenchState state = (await application.BootstrapAsync(
        CancellationToken.None)).States.Select(item => item.State)
        .OfType<RecognitionWorkbenchState>().Single();
      string engines = JsonSerializer.Serialize(state.Engines?.Select(engine => new
      {
        engine.Engine, engine.Availability, engine.ReasonCode,
      }));
      throw new InvalidOperationException(
        $"Mode {mode} absent from {selector} after 2 minutes. DOM={dom}; " +
        $"bootstrap recognition engines={engines}", error);
    }
  }

  private async Task<object> RunPaddleSmokeInputsAsync(
    string mode, string pipeline, string fixture, string optionName,
    string optionValue, string availability, string[] tokens,
    string[] exportButtons, ManagedEnvironmentSession session,
    int timeoutMinutes)
  {
    string kind = RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_INPUT_KIND");
    return kind switch
    {
      "file" => await RunPaddleFileInputAsync(mode, pipeline, fixture,
        optionName, optionValue, availability, tokens, exportButtons,
        session, timeoutMinutes),
      "batch" => await RunPaddleBatchInputAsync(mode, pipeline, fixture,
        optionName, optionValue, session, timeoutMinutes),
      "pdf" => await RunPaddlePdfInputAsync(mode, pipeline, fixture,
        optionName, optionValue, session, timeoutMinutes),
      _ => throw new InvalidOperationException($"Unknown Paddle input kind: {kind}"),
    };
  }

  private async Task<object> RunPaddleFileInputAsync(
    string mode, string pipeline, string fixture, string optionName,
    string optionValue, string availability, string[] tokens,
    string[] exportButtons, ManagedEnvironmentSession session,
    int timeoutMinutes)
  {
    if (Path.GetExtension(fixture).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Single-file input requires an image fixture.");
    int before = smokeSubmitAttempts!();
    RecordPaddleSmokeStage("select image via public picker");
    await ClickManagedSmokeButtonAsync("选择图片");
    await CompletePaddleOpenPickerAsync(fixture);
    RecordPaddleSmokeStage($"wait file recognition {mode}");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
    RecognitionWorkbenchState terminal = await PollPaddleTerminalAsync(before, timeout.Token);
    return await CollectPaddleRecognitionEvidenceAsync(mode, pipeline,
      optionName, optionValue, availability, tokens, exportButtons, session,
      before, terminal, smokeLastJobId!(), null);
  }

  private async Task<object> RunPaddleBatchInputAsync(
    string mode, string pipeline, string fixture, string optionName,
    string optionValue, ManagedEnvironmentSession session, int timeoutMinutes)
  {
    string second = ValidatePaddleSmokeOwnedPath(
      RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_SECOND_FIXTURE"),
      "second fixture");
    if (!File.Exists(second) || string.Equals(second, fixture,
          StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Batch requires two distinct image fixtures.");
    RecordPaddleSmokeStage($"batch select mode {mode}");
    await NavigateSmokeAsync("批量识别", "button");
    await WaitForPaddleModeAsync("#batch-task-engine", mode);
    await SelectSmokeValueAsync("#batch-task-engine", mode);
    foreach (string path in new[] { fixture, second })
    {
      RecordPaddleSmokeStage($"batch add {Path.GetFileName(path)}");
      await ClickManagedSmokeButtonAsync("添加图片");
      await CompletePaddleOpenPickerAsync(path);
    }
    BatchWorkbenchState queued = await WaitForPaddleBatchAsync(
      state => state.ItemCount == 2 && !state.IsRunning,
      TimeSpan.FromSeconds(30));
    if (queued.Items?.Select(item => item.Name).Order().SequenceEqual(
          new[] { Path.GetFileName(fixture), Path.GetFileName(second) }.Order()) != true)
      throw new InvalidOperationException("Public batch queue does not contain both fixtures.");
    int before = smokeSubmitAttempts!();
    RecordPaddleSmokeStage($"batch recognize {mode}");
    await ClickManagedSmokeButtonAsync("开始识别");
    BatchWorkbenchState terminal = await WaitForPaddleBatchAsync(
      state => !state.IsRunning && state.CompletedCount + state.FailedCount == 2,
      TimeSpan.FromMinutes(timeoutMinutes));
    if (terminal.CompletedCount != 2 || terminal.FailedCount != 0 ||
        terminal.Items?.Count != 2 ||
        terminal.Items.Any(item => item.StatusCode != "batch.item.completed") == true)
      throw new InvalidOperationException($"Batch incomplete: {JsonSerializer.Serialize(terminal)}");
    object job = await ObservePaddleInputJobAsync(session, pipeline,
      optionName, optionValue, before, 2);
    string? queueText = await PaddleSmokeDomTextAsync(".batch-queue");
    if (string.IsNullOrWhiteSpace(queueText))
      throw new InvalidOperationException("Completed batch queue is absent from public UI.");
    bool inspected = await InspectPaddleStructuredItemAsync(
      $"查看 {Path.GetFileName(fixture)} 的结构化结果", mode);
    if (!inspected && mode is ("paddle_table" or "paddle_formula" or
        "paddle_structure" or "paddle_document_vl"))
      throw new InvalidOperationException($"Batch {mode} has no inspectable structured result.");
    Dictionary<string, object?> copies = inspected
      ? await ClickPaddleCopyButtonsAsync([]) : new();
    if (mode == "paddle_table" && !copies.ContainsKey("复制表格 HTML / TSV"))
      throw new InvalidOperationException("Batch table copy control is unavailable.");
    var exports = new List<object>();
    if (PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS") is { Length: > 0 } labels)
      foreach (string label in labels.Split('|', StringSplitOptions.RemoveEmptyEntries))
        exports.Add(await RunPaddleBatchExportAsync(label));
    paddleSmokeOutcome = "passed";
    return new
    {
      input_kind = "batch", mode, fixture_paths = new[] { fixture, second },
      option = new { name = optionName, value = optionValue },
      submit_attempts = smokeSubmitAttempts!(), job,
      queue = new { terminal.ItemCount, terminal.CompletedCount,
        terminal.FailedCount, terminal.Items, dom_text = queueText },
      structured_inspected = inspected, copies, exports,
    };
  }

  private async Task<object> RunPaddlePdfInputAsync(
    string mode, string pipeline, string fixture, string optionName,
    string optionValue, ManagedEnvironmentSession session, int timeoutMinutes)
  {
    if (!Path.GetExtension(fixture).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("PDF input requires a PDF fixture.");
    RecordPaddleSmokeStage($"pdf select mode {mode}");
    await NavigateSmokeAsync("PDF 工作台", "button");
    await WaitForPaddleModeAsync("#pdf-task-engine", mode);
    await SelectSmokeValueAsync("#pdf-task-engine", mode);
    RecordPaddleSmokeStage("open PDF via public picker");
    await ClickManagedSmokeButtonAsync("打开 PDF");
    await CompletePaddleOpenPickerAsync(fixture);
    PdfWorkbenchState opened = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.PageCount > 0,
      TimeSpan.FromMinutes(2));
    if (opened.SelectedPages is null || !opened.SelectedPages.Contains(0))
    {
      await WaitForSmokeDomAsync(
        "!!document.querySelector('[aria-label=\"选择第 1 页\"]')",
        TimeSpan.FromSeconds(30));
      string clicked = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "(() => { const e=document.querySelector('[aria-label=\"选择第 1 页\"]'); " +
        "if(!e) return false; e.click(); return true; })()");
      if (clicked != "true")
        throw new InvalidOperationException("First PDF page selection is unavailable.");
      opened = await WaitForPaddlePdfAsync(
        state => state.SelectedPages?.Contains(0) == true,
        TimeSpan.FromSeconds(30));
    }
    int before = smokeSubmitAttempts!();
    RecordPaddleSmokeStage($"pdf recognize first page {mode}");
    await ClickManagedSmokeButtonAsync("OCR 选中页");
    PdfWorkbenchState terminal = await WaitForPaddlePdfAsync(
      state => !state.IsBusy &&
        state.Pages?.FirstOrDefault(page => page.Index == 0)?.StatusCode
          is ("pdf.page.done" or "pdf.page.failed"),
      TimeSpan.FromMinutes(timeoutMinutes));
    if (terminal.Pages?.FirstOrDefault(page => page.Index == 0)
          ?.StatusCode != "pdf.page.done")
      throw new InvalidOperationException($"PDF page OCR failed: {JsonSerializer.Serialize(terminal)}");
    object job = await ObservePaddleInputJobAsync(session, pipeline,
      optionName, optionValue, before, 1);
    bool inspected = await InspectPaddlePdfStructureAsync(mode);
    var copies = await ClickPaddleCopyButtonsAsync([]);
    object saved = await RunPaddlePdfSaveAsync();
    paddleSmokeOutcome = "passed";
    return new
    {
      input_kind = "pdf", mode, fixture_path = fixture,
      option = new { name = optionName, value = optionValue },
      submit_attempts = smokeSubmitAttempts!(), job,
      pdf = new { opened.PageCount, terminal.SelectedPages, terminal.Pages },
      structured_inspected = inspected, copies, saved,
    };
  }

  private async Task<BatchWorkbenchState> WaitForPaddleBatchAsync(
    Func<BatchWorkbenchState, bool> done, TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    while (true)
    {
      BatchWorkbenchState state = (await application.BootstrapAsync(cancellation.Token))
        .States.Select(item => item.State).OfType<BatchWorkbenchState>().Single();
      if (done(state)) return state;
      await Task.Delay(250, cancellation.Token);
    }
  }

  private async Task<PdfWorkbenchState> WaitForPaddlePdfAsync(
    Func<PdfWorkbenchState, bool> done, TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    while (true)
    {
      PdfWorkbenchState state = (await application.BootstrapAsync(cancellation.Token))
        .States.Select(item => item.State).OfType<PdfWorkbenchState>().Single();
      if (done(state)) return state;
      await Task.Delay(250, cancellation.Token);
    }
  }

  private async Task<object> ObservePaddleInputJobAsync(
    ManagedEnvironmentSession session, string pipeline, string optionName,
    string optionValue, int before, int itemCount)
  {
    string? jobId = smokeLastJobId!();
    if (smokeSubmitAttempts!() != before + 1 || string.IsNullOrWhiteSpace(jobId))
      throw new InvalidOperationException(
        $"Expected one submitted job: before={before}, after={smokeSubmitAttempts()}, id={jobId}");
    var observed = await session.Client.ObserveAsync(jobId, 0, CancellationToken.None);
    var snapshot = observed.Snapshot;
    var selection = snapshot.Pipeline;
    bool optionRecorded = selection?.Options is { } options &&
      options.TryGetValue(optionName, out JsonElement value) &&
      NormalizePaddleSmokeJson(value) == optionValue;
    bool chartRecorded = RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_MODE") !=
      "paddle_structure" || selection?.Options is { } chartOptions &&
      chartOptions.TryGetValue("use_chart_recognition", out JsonElement chart) &&
      NormalizePaddleSmokeJson(chart) == "true";
    if (selection?.PipelineId.ToString() != pipeline ||
        snapshot.EnvironmentId != session.EnvironmentId ||
        snapshot.EnvironmentRevision != session.Revision ||
        !optionRecorded || !chartRecorded ||
        snapshot.Items.Count != itemCount || snapshot.Summary.Succeeded != itemCount ||
        snapshot.Summary.Failed != 0)
      throw new InvalidOperationException(
        $"Input job mode/options/environment/items mismatch: {JsonSerializer.Serialize(snapshot)}");
    return new
    {
      task_id = jobId, snapshot.State, snapshot.Pipeline,
      snapshot.EnvironmentId, snapshot.EnvironmentRevision,
      snapshot.Summary, snapshot.Items,
    };
  }

  private async Task<bool> InspectPaddleStructuredItemAsync(string label, string mode)
  {
    if (mode is "paddle_table" or "paddle_formula" or
        "paddle_structure" or "paddle_document_vl")
      await WaitForSmokeDomAsync(
        "!!document.querySelector('button[aria-label=" +
        JsonSerializer.Serialize(label) + "]')",
        TimeSpan.FromSeconds(30));
    string script = "(() => { const b=document.querySelector('button[aria-label=" +
      JsonSerializer.Serialize(label) + "]'); if(!b || b.disabled) return false; " +
      "b.click(); return true; })()";
    if (await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(script) != "true")
      return false;
    await WaitForSmokeDomAsync(
      "!!document.querySelector('.structured-result ol.structured-blocks')",
      TimeSpan.FromSeconds(30));
    if (mode is "paddle_table" or "paddle_structure" or "paddle_document_vl")
    {
      string predicate = mode == "paddle_table"
        ? "!!document.querySelector('.structured-result table td, .structured-result table th')"
        : "document.querySelectorAll('.structured-result ol.structured-blocks li').length > 0";
      if (!await PaddleSmokeDomBoolAsync(predicate))
        throw new InvalidOperationException($"Batch {mode} has no structured preview.");
    }
    return true;
  }

  private async Task<bool> InspectPaddlePdfStructureAsync(string mode)
  {
    await WaitForSmokeDomAsync(
      "!!document.querySelector('.pdf-main .structured-result ol.structured-blocks')",
      TimeSpan.FromSeconds(30));
    string predicate = mode == "paddle_table"
      ? "!!document.querySelector('.pdf-main .structured-result table td, .pdf-main .structured-result table th')"
      : "document.querySelectorAll('.pdf-main .structured-result ol.structured-blocks li').length > 0";
    if (!await PaddleSmokeDomBoolAsync(predicate))
      throw new InvalidOperationException($"PDF {mode} has no structured preview.");
    return true;
  }

  /// <summary>
  /// 轮询识别终态：成功（Result 非空）、失败（recognition.failed）或显式
  /// 提交被拒（命令结束但无 job、无结果）都视为终态；超时抛出并保留现场，
  /// 不做盲重试。
  /// </summary>
  private async Task<RecognitionWorkbenchState> PollPaddleTerminalAsync(
    int submitsBefore, CancellationToken cancellation)
  {
    while (true)
    {
      RecognitionWorkbenchState state = (await application.BootstrapAsync(cancellation))
        .States.Select(item => item.State).OfType<RecognitionWorkbenchState>()
        .Single();
      if (state.Result is not null && !state.IsBusy) return state;
      if (state.StatusCode == "recognition.failed" && !state.IsBusy) return state;
      if (!state.IsBusy && state.Result is null &&
          smokeSubmitAttempts!() == submitsBefore &&
          smokeLastJobId!() is null)
      {
        await Task.Delay(2000, cancellation);
        RecognitionWorkbenchState recheck = (await application.BootstrapAsync(cancellation))
          .States.Select(item => item.State).OfType<RecognitionWorkbenchState>()
          .Single();
        if (!recheck.IsBusy && recheck.Result is null &&
            smokeSubmitAttempts() == submitsBefore && smokeLastJobId() is null)
          return recheck;
      }
      await Task.Delay(250, cancellation);
    }
  }

  private async Task<object> CollectPaddleRecognitionEvidenceAsync(
    string mode,
    string expectedPipeline,
    string optionName,
    string optionValue,
    string availability,
    string[] tokens,
    string[] exportButtons,
    ManagedEnvironmentSession session,
    int submitsBefore,
    RecognitionWorkbenchState terminal,
    string? jobId,
    SyntheticFixtureRegionPicker.CaptureEvidence? capture)
  {
    string? resultDocument = await PaddleSmokeDomTextAsync(".result-document");
    string? structuredBlocks = await PaddleSmokeDomTextAsync(
      ".structured-result ol.structured-blocks");
    bool succeeded = terminal.Result is not null;
    object? jobEvidence = null;
    if (succeeded)
    {
      if (smokeSubmitAttempts!() != submitsBefore + 1 || string.IsNullOrWhiteSpace(jobId))
        throw new InvalidOperationException(
          $"Expected exactly one job for {mode}: attempts={smokeSubmitAttempts()}, job={jobId}.");
      var observed = await session.Client.ObserveAsync(jobId, 0, CancellationToken.None);
      string pipelineId = observed.Snapshot.Pipeline?.PipelineId.ToString() ?? "";
      if (pipelineId != expectedPipeline)
        throw new InvalidOperationException(
          $"Job pipeline {pipelineId} does not match mode {mode} expectation {expectedPipeline}.");
      var options = observed.Snapshot.Pipeline?.Options;
      bool optionConsumed = options is not null &&
        options.TryGetValue(optionName, out System.Text.Json.JsonElement value) &&
        NormalizePaddleSmokeJson(value) == optionValue;
      bool chartConsumed = mode != "paddle_structure" ||
        options is not null &&
        options.TryGetValue("use_chart_recognition", out JsonElement chart) &&
        NormalizePaddleSmokeJson(chart) == "true";
      if (!optionConsumed || !chartConsumed)
        throw new InvalidOperationException(
          $"Non-default options were not recorded on the job for {mode}: " +
          $"{optionName}={optionValue}, chart={chartConsumed}.");
      if (observed.Snapshot.EnvironmentId != session.EnvironmentId ||
          observed.Snapshot.EnvironmentRevision != session.Revision)
        throw new InvalidOperationException($"Job environment binding mismatch for {jobId}.");
      jobEvidence = new
      {
        task_id = observed.Snapshot.JobId,
        state = observed.Snapshot.State,
        pipeline_id = pipelineId,
        engine = observed.Snapshot.Pipeline?.Engine?.ToString(),
        options = options,
        // 明确局限：这里是 job 请求侧 wire 记录，证明选项随请求到达
        // supervisor 并归属该 job；不等于 worker 内部消费证据。
        options_evidence_note = "request-side wire record; worker-side consumption not proven by this smoke",
        environment_id = observed.Snapshot.EnvironmentId,
        environment_revision = observed.Snapshot.EnvironmentRevision,
        summary = observed.Snapshot.Summary,
      };
    }

    // ---- 公共 UI 结果链：token 命中、结构化预览、真实按钮复制/导出 ----
    bool tokenVisible = tokens.Length == 0 || tokens.Any(token =>
      (resultDocument?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false) ||
      (structuredBlocks?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false));
    var ui = new Dictionary<string, object?>();
    if (succeeded)
    {
      if (tokens.Length > 0 && !tokenVisible)
        throw new InvalidOperationException(
          $"Expected tokens [{string.Join('|', tokens)}] not visible for {mode}.");
      ui["token_visible"] = tokenVisible;
      ui["structured_table_present"] = await PaddleSmokeDomBoolAsync(
        "!!document.querySelector('.structured-result .structured-table-scroll table')");
      ui["structured_merged_cell_present"] = await PaddleSmokeDomBoolAsync(
        "!!document.querySelector('.structured-result table td[rowspan], " +
        ".structured-result table td[colspan], .structured-result table th[rowspan], " +
        ".structured-result table th[colspan])");
      ui["formula_latex_present"] = await PaddleSmokeDomBoolAsync(
        "!!document.querySelector('.structured-formula code') && " +
        "(document.querySelector('.structured-formula code')?.textContent ?? '').length > 0");
      ui["formula_rendered"] = await PaddleSmokeDomBoolAsync(
        "(() => { const math = document.querySelector('.structured-formula .formula-preview math'); " +
        "if (!math || math.closest('.formula-preview').querySelector('[style]')) return false; " +
        "const box = math.getBoundingClientRect(); " +
        "return box.width > 0 && box.height > 0 && " +
        "[...math.querySelectorAll('mfrac')].every(f => f.children.length === 2 && " +
        "f.children[0].getBoundingClientRect().y < f.children[1].getBoundingClientRect().y); })()");
      ui["image_block_present"] = await PaddleSmokeDomBoolAsync(
        "!!document.querySelector('.structured-result img')");
      string blocksCount = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "document.querySelectorAll('.structured-result ol.structured-blocks li').length");
      ui["structured_blocks_count"] = int.Parse(
        blocksCount, System.Globalization.CultureInfo.InvariantCulture);
      await AssertPaddleStructureEvidenceAsync(mode, ui);
      ui["copies"] = await ClickPaddleCopyButtonsAsync(tokens);
      var exports = new List<object?>();
      foreach (string label in exportButtons)
      {
        exports.Add(await RunPaddleUiExportAsync(label));
      }
      ui["exports"] = exports;
    }

    // 提交被目录/环境显式拒绝（无 job）= blocked；真实 job 失败 = failed。
    paddleSmokeOutcome = succeeded ? "passed" : (jobId is null ? "blocked" : "failed");
    return new
    {
      mode,
      fixture_path = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_FIXTURE"),
      option = new
      {
        name = optionName,
        value = optionValue,
        kind = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_OPTION_KIND") ?? "bool",
      },
      mode_availability_label = availability,
      capture,
      submit_attempts = smokeSubmitAttempts!(),
      dom = new
      {
        status_code = terminal.StatusCode,
        result_document = resultDocument,
        structured_blocks = structuredBlocks,
      },
      job = jobEvidence,
      ui,
      environment = new
      {
        id = session.EnvironmentId,
        revision = session.Revision,
        // 冒烟环境固定为 CPU recipe；GPU 状态保持 UNVERIFIED，不因硬件
        // 可见而标记通过。
        device = "cpu-recipe",
        gpu = "UNVERIFIED",
      },
    };
  }

  /// <summary>
  /// 结构级断言（不硬编码识别精度）：table 模式必须有含单元格的结构化
  /// 表格预览；formula 模式必须有可复制 LaTeX 且（已渲染 或 明确保留
  /// 原文的渲染失败提示）；structure/VL 模式必须有真实版面区块，不允
  /// 许 0 结构却 passed。
  /// </summary>
  private async Task AssertPaddleStructureEvidenceAsync(
    string mode, Dictionary<string, object?> ui)
  {
    switch (mode)
    {
      case "paddle_table":
        if (ui.GetValueOrDefault("structured_table_present") is not true)
          throw new InvalidOperationException(
            "paddle_table finished without a structured table preview.");
        if (!await PaddleSmokeDomBoolAsync(
              "document.querySelectorAll('.structured-result .structured-table-scroll table td, " +
              ".structured-result .structured-table-scroll table th').length > 0"))
          throw new InvalidOperationException("paddle_table table preview has no cells.");
        break;
      case "paddle_formula":
        if (ui.GetValueOrDefault("formula_latex_present") is not true)
          throw new InvalidOperationException(
            "paddle_formula finished without copyable LaTeX.");
        if (ui.GetValueOrDefault("formula_rendered") is not true)
        {
          string? structuredText = await PaddleSmokeDomTextAsync(".structured-result");
          bool renderErrorKept = structuredText?.Contains(
            "公式无法渲染", StringComparison.Ordinal) ?? false;
          if (!renderErrorKept)
            throw new InvalidOperationException(
              "paddle_formula LaTeX neither rendered nor retained with an explicit render error.");
        }
        break;
      case "paddle_structure":
      case "paddle_document_vl":
        if (ui.GetValueOrDefault("structured_blocks_count") is not int count || count < 1)
          throw new InvalidOperationException(
            $"{mode} finished without structured layout blocks.");
        break;
    }
  }

  /// <summary>
  /// 复制走真实公共按钮 + 系统剪贴板，读回标准 HtmlFormat/文本核验片段；
  /// 出现“复制失败”提示即证据（不重试）。
  /// </summary>
  private async Task<Dictionary<string, object?>> ClickPaddleCopyButtonsAsync(
    string[] tokens)
  {
    var evidence = new Dictionary<string, object?>();
    foreach (string label in new[] { "复制文本", "复制表格 HTML / TSV", "复制 LaTeX" })
    {
      // 点击前记录剪贴板基线：轮询以“内容变化”为准，避免把上一次复制
      // 的残留（如表格 TSV 文本）误认成本次结果。
      string? baseline = await ReadPaddleClipboardAsync(label);
      string found = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "(() => { const b=Array.from(document.querySelectorAll('button')).find(b => " +
        $"b.textContent?.trim() === {JsonSerializer.Serialize(label)}); " +
        "if(!b || b.disabled) return false; b.click(); return true; })()");
      if (found != "true") continue;
      evidence[label] = await VerifyPaddleClipboardCopyAsync(label, tokens, baseline);
      await WaitForSmokeDomAsync(
        "(() => { const alerts=Array.from(document.querySelectorAll('[role=alert]')); " +
        "return !alerts.some(a => (a.textContent ?? '').includes('复制失败')); })()",
        TimeSpan.FromSeconds(5));
    }
    return evidence;
  }

  /// <summary>
  /// 轮询剪贴板直到目标格式出现且与基线不同：表格 → 标准 HtmlFormat 含
  /// table/td 片段；LaTeX/文本 → 非空纯文本。剪贴板访问固定回 UI 线程队列。
  /// </summary>
  private async Task<object?> VerifyPaddleClipboardCopyAsync(
    string label, string[] tokens, string? baseline)
  {
    bool expectHtml = label == "复制表格 HTML / TSV";
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (true)
    {
      string? snapshot = await ReadPaddleClipboardAsync(label);
      if (snapshot is { Length: > 0 } value && value != baseline)
      {
        if (expectHtml)
        {
          if (!value.Contains("<table", StringComparison.OrdinalIgnoreCase) ||
              !value.Contains("<td", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
              $"{label}: clipboard HtmlFormat lacks table fragment: " +
              value[..Math.Min(200, value.Length)]);
          return new { format = "CF_HTML", table_fragment = true, html_chars = value.Length };
        }
        return new
        {
          format = "text",
          chars = value.Length,
          token_hit = tokens.Length == 0 ? (bool?)null :
            tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase)),
          preview = value[..Math.Min(80, value.Length)],
        };
      }
      await Task.Delay(150, cancellation.Token);
    }
  }

  /// <summary>读取剪贴板目标格式（HTML 读标准 HtmlFormat）；无目标格式时返回 null。</summary>
  private async Task<string?> ReadPaddleClipboardAsync(string label)
  {
    bool expectHtml = label == "复制表格 HTML / TSV";
    return await RunOnPaddleUiThreadAsync(() =>
    {
      DataPackageView content = Clipboard.GetContent();
      if (expectHtml)
      {
        return content.Contains(StandardDataFormats.Html)
          ? ReadClipboardHtmlSync(content) : null;
      }
      return content.Contains(StandardDataFormats.Text)
        ? ReadClipboardTextSync(content) : null;
    });
  }

  private static string ReadClipboardTextSync(DataPackageView content)
  {
    try
    {
      return content.GetTextAsync().AsTask().GetAwaiter().GetResult();
    }
    catch (Exception error)
    {
      return $"<clipboard read failed: {error.Message}>";
    }
  }

  private static string ReadClipboardHtmlSync(DataPackageView content)
  {
    try
    {
      return content.GetHtmlFormatAsync().AsTask().GetAwaiter().GetResult();
    }
    catch (Exception error)
    {
      return $"<clipboard read failed: {error.Message}>";
    }
  }

  private async Task<T> RunOnPaddleUiThreadAsync<T>(Func<T> action)
  {
    var completion = new TaskCompletionSource<T>(
      TaskCreationOptions.RunContinuationsAsynchronously);
    if (!DispatcherQueue.TryEnqueue(() =>
    {
      try { completion.TrySetResult(action()); }
      catch (Exception error) { completion.TrySetException(error); }
    }))
    {
      throw new InvalidOperationException("UI dispatcher is unavailable.");
    }
    return await completion.Task;
  }

  private async Task CompletePaddleOpenPickerAsync(string fixture)
  {
    nint dialog = await WaitForPaddleSaveDialogAsync(TimeSpan.FromSeconds(30));
    try
    {
      if (!PaddleSmokeNative.SetForegroundWindow(dialog) ||
          PaddleSmokeNative.GetForegroundWindow() != dialog)
        throw new InvalidOperationException("Open dialog could not take foreground.");
      foreach (char c in fixture)
      {
        PaddleSmokeNative.SendChar(c);
        await Task.Delay(10);
      }
      await Task.Delay(150);
      PaddleSmokeNative.SendKey(0x0D);
      await WaitForPaddleConditionAsync(
        () => !PaddleSmokeNative.IsWindowVisible(dialog), TimeSpan.FromSeconds(30));
    }
    catch
    {
      PaddleSmokeNative.SendKey(0x1B);
      throw;
    }
    if (FindPaddleSaveDialog() != 0)
      throw new InvalidOperationException("Unexpected picker dialog after file selection.");
  }

  private async Task<object> RunPaddleBatchExportAsync(string buttonLabel)
  {
    string? exportDir = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_DIR");
    if (string.IsNullOrWhiteSpace(exportDir))
      throw new InvalidOperationException("Batch export directory is missing.");
    exportDir = ValidatePaddleSmokeOwnedPath(exportDir, "batch export dir");
    Directory.CreateDirectory(exportDir);
    string format = buttonLabel switch
    {
      "导出 Markdown" or "导出全部 Markdown" => "markdown",
      "导出 Word" or "导出全部 Word" => "docx",
      "导出 XLSX" or "导出全部 Excel" => "xlsx",
      _ => throw new InvalidOperationException($"Unexpected batch export label: {buttonLabel}"),
    };
    string publicLabel = format switch
    {
      "markdown" => "导出全部 Markdown",
      "docx" => "导出全部 Word",
      _ => "导出全部 Excel",
    };
    string[] before = Directory.GetFiles(exportDir);
    RecordPaddleSmokeStage($"batch export {publicLabel}");
    await ClickManagedSmokeButtonAsync(publicLabel);
    nint dialog = await WaitForPaddleSaveDialogAsync(TimeSpan.FromSeconds(30));
    try
    {
      if (!PaddleSmokeNative.SetForegroundWindow(dialog) ||
          PaddleSmokeNative.GetForegroundWindow() != dialog)
        throw new InvalidOperationException("Folder dialog could not take foreground.");
      foreach (char c in exportDir)
      {
        PaddleSmokeNative.SendChar(c);
        await Task.Delay(10);
      }
      PaddleSmokeNative.SendKey(0x0D);
      await WaitForPaddleConditionAsync(
        () => !PaddleSmokeNative.IsWindowVisible(dialog), TimeSpan.FromSeconds(30));
    }
    catch
    {
      PaddleSmokeNative.SendKey(0x1B);
      throw;
    }
    string[] created = [];
    string extension = format switch
    {
      "markdown" => ".md",
      "docx" => ".docx",
      _ => ".xlsx",
    };
    await WaitForPaddleConditionAsync(() =>
    {
      created = Directory.GetFiles(exportDir)
        .Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
      return created.Length == 2 && created.All(path =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase) &&
        new FileInfo(path).Length > 0);
    }, TimeSpan.FromMinutes(2));
    bool incomplete = await PaddleSmokeDomBoolAsync(
      "!!document.querySelector('[role=alert].form-note')?.textContent?.includes('部分图片缺失')");
    if (incomplete)
      throw new InvalidOperationException($"Batch {format} export reported missing images.");
    return new { button_label = publicLabel, format, files = created,
      bytes = created.Select(path => new FileInfo(path).Length).ToArray(),
      incomplete };
  }

  private async Task<object> RunPaddlePdfSaveAsync()
  {
    string? exportDir = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_DIR");
    if (string.IsNullOrWhiteSpace(exportDir))
      throw new InvalidOperationException("PDF save directory is missing.");
    exportDir = ValidatePaddleSmokeOwnedPath(exportDir, "pdf save dir");
    Directory.CreateDirectory(exportDir);
    string target = Path.Combine(exportDir,
      $"paddle-{RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_MODE")}-ui-save.pdf");
    if (File.Exists(target))
      throw new InvalidOperationException($"PDF save target already exists: {target}");
    RecordPaddleSmokeStage("save PDF via public picker");
    await ClickManagedSmokeButtonAsync("保存");
    nint dialog = await WaitForPaddleSaveDialogAsync(TimeSpan.FromSeconds(30));
    try
    {
      if (!PaddleSmokeNative.SetForegroundWindow(dialog) ||
          PaddleSmokeNative.GetForegroundWindow() != dialog)
        throw new InvalidOperationException("PDF save dialog could not take foreground.");
      foreach (char c in target)
      {
        PaddleSmokeNative.SendChar(c);
        await Task.Delay(10);
      }
      PaddleSmokeNative.SendKey(0x0D);
      await WaitForPaddleConditionAsync(
        () => !PaddleSmokeNative.IsWindowVisible(dialog), TimeSpan.FromSeconds(30));
    }
    catch
    {
      PaddleSmokeNative.SendKey(0x1B);
      throw;
    }
    await WaitForPaddleConditionAsync(() =>
      File.Exists(target) && new FileInfo(target).Length > 0,
      TimeSpan.FromMinutes(2));
    return new { button_label = "保存", file = target,
      bytes = new FileInfo(target).Length, saved_via = "ui-file-save-picker" };
  }

  /// <summary>
  /// 真实公共 UI 导出：点击导出按钮后只操作“同进程且被主窗口 owned”的
  /// FileSavePicker（#32770）；归属不明立即停止。键盘输入隔离根内固定新
  /// 文件名并回车确认；确认后再出现意外弹窗按 ESC 取消并记失败。不新增
  /// 生产任意路径入口。
  /// </summary>
  private async Task<object> RunPaddleUiExportAsync(string buttonLabel)
  {
    string mode = RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_MODE");
    string? exportDir = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_DIR");
    if (string.IsNullOrWhiteSpace(exportDir))
      throw new InvalidOperationException("Isolated export dir env is missing.");
    exportDir = ValidatePaddleSmokeOwnedPath(exportDir, "export dir");
    Directory.CreateDirectory(exportDir);
    string extension = buttonLabel switch
    {
      "导出 Markdown" => ".md",
      "导出 Word" => ".docx",
      "导出 XLSX" => ".xlsx",
      _ => throw new InvalidOperationException($"Unknown export label: {buttonLabel}"),
    };
    string fileName = $"paddle-{mode}-ui-export{extension}";
    string target = Path.Combine(exportDir, fileName);
    if (File.Exists(target))
      throw new InvalidOperationException($"Export target must be a fresh file: {target}");

    RecordPaddleSmokeStage($"ui export {buttonLabel}");
    await ClickManagedSmokeButtonAsync(buttonLabel);
    nint dialog = await WaitForPaddleSaveDialogAsync(TimeSpan.FromSeconds(30));
    string? dialogError = null;
    try
    {
      if (!PaddleSmokeNative.SetForegroundWindow(dialog) ||
          PaddleSmokeNative.GetForegroundWindow() != dialog)
        throw new InvalidOperationException("Save dialog could not take foreground.");
      // 焦点默认在文件名输入框；逐字符 UNICODE 注入完整隔离路径后回车。
      foreach (char c in target)
      {
        PaddleSmokeNative.SendChar(c);
        await Task.Delay(10);
      }
      await Task.Delay(150);
      PaddleSmokeNative.SendKey(0x0D); // Enter
      await WaitForPaddleConditionAsync(
        () => !PaddleSmokeNative.IsWindowVisible(dialog), TimeSpan.FromSeconds(30));
    }
    catch (Exception error)
    {
      dialogError = error.Message;
      PaddleSmokeNative.SendKey(0x1B); // ESC：取消本次保存，不留半开的弹窗。
    }
    await Task.Delay(500);
    // 确认后残留/新出现的同属保存弹窗（如覆盖确认）视为异常：取消并失败。
    nint extra = FindPaddleSaveDialog();
    if (extra != 0)
    {
      PaddleSmokeNative.SendKey(0x1B);
      throw new InvalidOperationException($"Unexpected save dialog after confirming {target}.");
    }
    if (dialogError is not null)
      throw new InvalidOperationException($"UI save failed: {dialogError}");
    long length = 0;
    try
    {
      await WaitForPaddleConditionAsync(() =>
      {
        if (!File.Exists(target)) return false;
        length = new FileInfo(target).Length;
        return length > 0;
      }, TimeSpan.FromMinutes(2));
    }
    catch (TimeoutException)
    {
      throw new InvalidOperationException($"Export via public UI did not produce {target}.");
    }
    return new
    {
      button_label = buttonLabel,
      file = target,
      bytes = length,
      saved_via = "ui-file-save-picker",
    };
  }

  private async Task<nint> WaitForPaddleSaveDialogAsync(TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    while (true)
    {
      nint dialog = FindPaddleSaveDialog();
      if (dialog != 0) return dialog;
      await Task.Delay(100, cancellation.Token);
    }
  }

  /// <summary>
  /// FileSavePicker 归属规则：同进程 AND 被主窗口 owned 的 #32770 对话框；
  /// 不满足即返回 0，由调用方按“归属不明”失败，绝不操作其它窗口。
  /// </summary>
  private nint FindPaddleSaveDialog()
  {
    nint main = WinRT.Interop.WindowNative.GetWindowHandle(this);
    nint found = 0;
    var probe = new PaddleSmokeNative.EnumWindowsProc((hwnd, _) =>
    {
      if (!PaddleSmokeNative.IsWindowVisible(hwnd)) return true;
      if (PaddleSmokeNative.GetWindowClassName(hwnd) != "#32770") return true;
      if (PaddleSmokeNative.GetWindowLongPtr(hwnd, -8) != main) return true;
      PaddleSmokeNative.GetWindowThreadProcessId(hwnd, out uint pid);
      if (pid != (uint)Environment.ProcessId) return true;
      found = hwnd;
      return false;
    });
    PaddleSmokeNative.EnumWindows(probe, nint.Zero);
    GC.KeepAlive(probe);
    return found;
  }

  private static async Task WaitForPaddleConditionAsync(
    Func<bool> done, TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
      while (!done())
      {
        await Task.Delay(200, cancellation.Token);
      }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
      throw new TimeoutException("Timed out waiting for the smoke condition.");
    }
  }

  /// <summary>执行返回字符串的脚本并解码 JSON 字符串。</summary>
  private async Task<string?> PaddleSmokeDomTextAsync(string selectorOrScript)
  {
    string expression = selectorOrScript.StartsWith("(() =>", StringComparison.Ordinal)
      ? selectorOrScript
      : $"document.querySelector({JsonSerializer.Serialize(selectorOrScript)})?.textContent ?? ''";
    string raw = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(expression);
    return JsonDocument.Parse(raw).RootElement.GetString();
  }

  private async Task<bool> PaddleSmokeDomBoolAsync(string predicate) =>
    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync($"(() => !!({predicate}))()") == "true";

  private static string NormalizePaddleSmokeJson(System.Text.Json.JsonElement value) =>
    value.ValueKind switch
    {
      JsonValueKind.True => "true",
      JsonValueKind.False => "false",
      JsonValueKind.Number => value.GetRawText(),
      _ => value.GetString() ?? "",
    };

  private static string RequiredPaddleSmokeEnv(string name) =>
    PaddleSmokeEnv(name) ?? throw new InvalidOperationException($"Missing env {name}.");

  private static int ParsePaddleSmokeMinutes(string name, int fallback)
  {
    string? raw = PaddleSmokeEnv(name);
    return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
  }

  private async Task<ManagedEnvironmentList> WaitForPaddleSmokeSnapshotAsync(TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    while (true)
    {
      ManagedEnvironmentList? list = smokeEnvironmentSnapshot!();
      if (list is not null) return list;
      await Task.Delay(100, cancellation.Token);
    }
  }

  /// <summary>
  /// 路径所有权：fixture/导出目录必须严格位于本次候选父目录（隔离根）
  /// 之内、候选副本之外；拒绝隔离根外的任意路径。
  /// </summary>
  private string ValidatePaddleSmokeOwnedPath(string raw, string kind)
  {
    string smokeRoot = Directory.GetParent(layout.InstallRoot)!.FullName;
    string rootPrefix = smokeRoot.TrimEnd(Path.DirectorySeparatorChar) +
      Path.DirectorySeparatorChar;
    string candidatePrefix = layout.InstallRoot.TrimEnd(Path.DirectorySeparatorChar) +
      Path.DirectorySeparatorChar;
    string full = Path.GetFullPath(raw);
    if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException($"{kind} must stay inside the smoke root: {full}");
    if (full.StartsWith(candidatePrefix, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException(
        $"{kind} must not be inside the candidate copy: {full}");
    return full;
  }

  // ---------------- 合成 fixture 选区器（不读取真实桌面） ----------------

  /// <summary>
  /// 只渲染并捕获本选区器自己创建的置顶窗口客户区：把冒烟 fixture PNG
  /// 显示在独立 WinUI 窗口内，用真实屏幕捕获服务采集像素后关闭窗口。
  /// </summary>
  internal sealed class SyntheticFixtureRegionPicker : IScreenRegionPicker
  {
    internal sealed record CaptureEvidence(
      string Fixture, int Width, int Height, long WhitePixels, long NonWhitePixels,
      double FitScale);

    internal static CaptureEvidence? LastCapture { get; private set; }

    public async Task<ScreenRegionSelection?> PickAsync(CancellationToken cancellationToken)
    {
      string? fixture = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_FIXTURE");
      if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture))
        throw new FileNotFoundException("Paddle smoke fixture is missing.", fixture);
      var window = new Window
      {
        Content = new Grid
        {
          Background = new SolidColorBrush(Microsoft.UI.Colors.White),
          Children =
          {
            new Image
            {
              Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(fixture)),
              // Uniform：按工作区适配等比缩放（fit=1 时仍为像素 1:1）。
              Stretch = Stretch.Uniform,
              HorizontalAlignment = HorizontalAlignment.Center,
              VerticalAlignment = VerticalAlignment.Center,
            },
          },
        },
      };
      try
      {
        ((OverlappedPresenter)window.AppWindow.Presenter).IsAlwaysOnTop = true;
        window.Activate();
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        uint dpi = PaddleSmokeNative.GetDpiForWindow(handle);
        double scale = dpi / 96.0;
        (int width, int height) = PngPixelSize(fixture);
        // 按实际显示器工作区适配：大图（如 1700x2200 混排页）在高 DPI
        // 下 1:1 物理尺寸可能超出屏幕；用 Stretch.Uniform 等比缩到
        // 可用区内，fixture 完整可见且捕获始终只覆盖自身客户区，
        // 不读取真实桌面背景。
        RectInt32 work = DisplayArea.GetFromWindowId(
          window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int maxClientWidth = Math.Max(160, work.Width - 80);
        int maxClientHeight = Math.Max(160, work.Height - 80);
        double physicalWidth = width * scale;
        double physicalHeight = height * scale;
        double fitScale = Math.Min(1.0,
          Math.Min(maxClientWidth / physicalWidth, maxClientHeight / physicalHeight));
        int targetClientWidth = Math.Max(64, (int)Math.Ceiling(physicalWidth * fitScale));
        int targetClientHeight = Math.Max(64, (int)Math.Ceiling(physicalHeight * fitScale));
        // 预留标题栏/边框余量（DPI 缩放）；超出目标的客户区为白色边距，
        // Uniform 居中保持比例完整显示 fixture。外框整体钳制在工作区内，
        // 客户区因此不小于 fitScale 目标时 fixture 1:1，略小时 Uniform 再
        // 等比缩小，始终完整可见。
        int chromeWidth = (int)Math.Ceiling(32 * scale) * 2;
        int chromeHeight = (int)Math.Ceiling(96 * scale);
        int outerWidth = Math.Min(targetClientWidth + chromeWidth, work.Width - 60);
        int outerHeight = Math.Min(targetClientHeight + chromeHeight, work.Height - 60);
        window.AppWindow.MoveAndResize(new RectInt32(
          work.X + 30, work.Y + 30, outerWidth, outerHeight));
        await Task.Delay(200, cancellationToken);
        if (!PaddleSmokeNative.GetClientRect(handle, out PaddleSmokeNative.Rect client))
          throw new InvalidOperationException("Fixture window client bounds are unavailable.");
        PaddleSmokeNative.Point origin = new();
        if (!PaddleSmokeNative.ClientToScreen(handle, ref origin))
          throw new InvalidOperationException("Fixture window origin is unavailable.");
        var bounds = new PhysicalRectangle(
          origin.X, origin.Y,
          client.Right - client.Left, client.Bottom - client.Top);

        ScreenRegionSelection? selection = null;
        await using (var capture = new ScreenCaptureService(Guid.NewGuid()))
        {
          for (int attempt = 0; attempt < 60; attempt++)
          {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedFrame frame = capture.Capture(bounds, TimeSpan.FromSeconds(10));
            byte[] pixels = capture.Read(frame);
            long white = 0;
            long nonWhite = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
              int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
              if (r > 245 && g > 245 && b > 245) white++;
              else nonWhite++;
            }
            long total = (long)bounds.Width * bounds.Height;
            if (nonWhite >= 2000 && nonWhite <= total * 6 / 10)
            {
              LastCapture = new CaptureEvidence(
                fixture, frame.Width, frame.Height, white, nonWhite, fitScale);
              selection = new ScreenRegionSelection(bounds, pixels, frame.Stride);
              break;
            }
            if (attempt == 59)
              throw new InvalidOperationException(
                $"Fixture pixels did not render on screen: nonWhite={nonWhite}, " +
                $"white={white}, bounds={bounds.Width}x{bounds.Height}, dpi={dpi}, " +
                $"fitScale={fitScale:0.###}.");
            await Task.Delay(250, cancellationToken);
          }
        }
        return selection;
      }
      finally
      {
        window.Close();
      }
    }

    /// <summary>从 PNG 文件头读取像素尺寸（IHDR），不依赖 UI 线程解码。</summary>
    private static (int width, int height) PngPixelSize(string path)
    {
      using var stream = File.OpenRead(path);
      Span<byte> header = stackalloc byte[24];
      if (stream.Read(header) != 24 || header[0] != 0x89 ||
          header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
        throw new InvalidDataException($"Fixture is not a PNG: {path}");
      int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
      int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
      if (width <= 0 || height <= 0) throw new InvalidDataException($"Invalid PNG size: {path}");
      return (width, height);
    }
  }

  /// <summary>Win32 互操作；仅本冒烟使用，句柄一律走指针宽度 API。</summary>
  internal static class PaddleSmokeNative
  {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(nint hwnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(nint handle, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint handle, ref Point point);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint handle);

    public static string GetWindowClassName(nint hwnd)
    {
      var builder = new System.Text.StringBuilder(64);
      GetClassNameW(hwnd, builder, 64);
      return builder.ToString();
    }

    public static nint GetWindowLongPtr(nint hwnd, int index) =>
      GetWindowLongPtrW(hwnd, index);

    // SendInput INPUT 在 x64 上为 40 字节：type + padding + 最大联合体
    // （MOUSEINPUT 32 字节）。KEYBDINPUT 用 wScan 承载 UNICODE 字符。
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
      public int Dx, Dy;
      public uint MouseData, Flags, Time;
      public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
      public ushort Vk, Scan;
      public uint Flags, Time;
      public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
      [FieldOffset(0)] public MOUSEINPUT Mouse;
      [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
      public int Type;
      public INPUTUNION Union;
    }

    private const uint KeyEventFUnicode = 0x0004;
    private const uint KeyEventFKeyUp = 0x0002;

    public static void SendChar(char c)
    {
      INPUT down = KeyboardInput(0, c, KeyEventFUnicode);
      INPUT up = KeyboardInput(0, c, KeyEventFUnicode | KeyEventFKeyUp);
      SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
    }

    public static void SendKey(ushort vk)
    {
      INPUT down = KeyboardInput(vk, 0, 0);
      INPUT up = KeyboardInput(vk, 0, KeyEventFKeyUp);
      SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyboardInput(ushort vk, ushort scan, uint flags) => new()
    {
      Type = 1,
      Union = new INPUTUNION
      {
        Keyboard = new KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags },
      },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(int count, INPUT[] inputs, int size);
  }
}
