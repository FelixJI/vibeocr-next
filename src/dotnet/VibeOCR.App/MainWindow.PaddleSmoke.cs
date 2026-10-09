using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
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
  // 隔离验收环境使用 ASCII 名，避免依赖所在卷是否启用 8.3 短名称。
  private const string PaddleSmokeEnvironmentName = "paddle-smoke-cpu";
  private const string PaddleSmokeRecipe = "paddleocr-cpu";
  // 安装预检失败可能只出现在公开状态中，不生成持久失败记录。
  private const string PaddleSmokeInstallRejectedStatus = "安装未完成；失败原因请查看该环境记录。";
  // paddleModesSmokeStarted 字段随 MainWindow.xaml.cs 的 OnHostStateChanged
  // 钩子一并声明（见交付说明），本文件只引用不声明，避免未读告警。
  private string paddleSmokeStage = "starting";
  private string paddleSmokeOutcome = "failed";
  // 本次合成输入已获取证据的兑底快照（job/outcomes/copies/exports 引用），
  // 仅供失败 health 保留取证；成功路径不写入最终 JSON，合同不变。
  private object? paddleSmokePartialEvidence;

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
      // 失败路径保全：尽力保留本次合成输入已获取的证据与 workbench 预览
      // PNG；预览/写盘的任何异常都不掩盖原始失败（不新增报告框架）。
      string? failurePreviewPath = null;
      if (phase != "install")
      {
        try
        {
          string preview = Path.ChangeExtension(path, ".png");
          if (!File.Exists(preview))
          {
            using (new FileStream(preview, FileMode.CreateNew)) { }
            StorageFile previewFile = await StorageFile.GetFileFromPathAsync(preview);
            using IRandomAccessStream previewStream =
              await previewFile.OpenAsync(FileAccessMode.ReadWrite);
            await WorkbenchWebView.CoreWebView2.CapturePreviewAsync(
              CoreWebView2CapturePreviewImageFormat.Png, previewStream);
            await previewStream.FlushAsync();
          }
          failurePreviewPath = preview;
        }
        catch
        {
          // 预览保全失败不掩盖原失败，也不删除现场文件。
          failurePreviewPath = null;
        }
      }
      File.WriteAllText(path, JsonSerializer.Serialize(new
      {
        schema_version = 1,
        state = paddleSmokeOutcome,
        phase,
        stage = paddleSmokeStage,
        mode = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_MODE"),
        error = error.ToString(),
        // 已获取的原始 job/outcomes/clipboard/capture（若任何）。
        partial_evidence = paddleSmokePartialEvidence,
        preview_path = failurePreviewPath,
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
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
    int timeoutMinutes = ParsePaddleSmokeMinutes(
      "VIBEOCR_PADDLE_SMOKE_INSTALL_TIMEOUT_MINUTES", 60);
    // 等待启动与安装共享同一超时预算。
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
    RecordPaddleSmokeStage("wait install executable");
    await WaitForPaddleSmokeInstallExecutableAsync(timeout.Token);
    ManagedEnvironmentList list = await WaitForPaddleSmokeSnapshotAsync(TimeSpan.FromMinutes(1));
    if (!list.Environments.Any(item => item.Name == PaddleSmokeEnvironmentName))
    {
      RecordPaddleSmokeStage("create isolated environment");
      // 公共一键准备按配方展示名自动命名；先用既有宿主命令创建 ASCII 测试容器。
      WorkbenchCommandReceipt created = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new CreateEnvironmentCommand(PaddleSmokeEnvironmentName)),
        timeout.Token);
      if (!created.Ok)
        throw new InvalidOperationException($"Smoke fixture creation failed: {created.Error?.Code}");
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
    await SelectSmokeValueAsync("#environment-component-select", "paddleocr");
    await SelectSmokeValueAsync("#environment-device-select", "cpu");
    await ClickManagedSmokeButtonAsync("继续准备依赖");
    await WaitForSmokeDomAsync(
      "!!document.querySelector('.runtime-install-plan button:not(:disabled)') && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('paddleocr-cpu') && " +
      "document.querySelector('.runtime-install-plan')?.textContent.includes('TUNA PyPI 镜像')",
      TimeSpan.FromMinutes(2));
    string planText = await PaddleSmokeDomTextAsync(".runtime-install-plan") ?? "";

    RecordPaddleSmokeStage("confirm install");
    await ClickManagedSmokeButtonAsync("确认安装依赖");
    while (true)
    {
      SettingsWorkbenchState settingsState = await PaddleSmokeSettingsStateAsync(timeout.Token);
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
      // 预检或安装失败可能没有持久记录，公开失败状态仍须及时结束冒烟。
      if (!settingsState.EnvironmentBusy && !settingsState.EnvironmentCanCancelInstall &&
          settingsState.EnvironmentStatus.StartsWith(
            PaddleSmokeInstallRejectedStatus, StringComparison.Ordinal))
      {
        throw new InvalidOperationException(
          "Install did not complete: " +
          $"{settingsState.EnvironmentStatus}; maintenance=" +
          $"{JsonSerializer.Serialize(settingsState.Maintenance)}; progress=" +
          $"{JsonSerializer.Serialize(settingsState.EnvironmentInstallProgress)}");
      }
      await Task.Delay(500, timeout.Token);
    }
  }

  private async Task<SettingsWorkbenchState> PaddleSmokeSettingsStateAsync(
    CancellationToken cancellation) =>
    (await application.BootstrapAsync(cancellation))
      .States.Select(item => item.State).OfType<SettingsWorkbenchState>().Single();

  // 默认初始化释放维护锁后才连接推理；单看设置维护状态不足以判断就绪。
  private async Task WaitForPaddleSmokeInstallExecutableAsync(
    CancellationToken cancellation)
  {
    while (true)
    {
      if (smokeInferenceAttached!())
      {
        SettingsWorkbenchState settings = await PaddleSmokeSettingsStateAsync(cancellation);
        if (settings.Maintenance is not { IsRunning: true } &&
            !settings.EnvironmentBusy && !settings.EnvironmentCanCancelInstall)
          return;
      }
      await Task.Delay(500, cancellation);
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
    // 稳定 runtime 面板作导航证明：冷启动清单未到时不渲染环境 Select。
    await NavigateSmokeAsync("设置", ".settings-runtime-panel");
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
      "document.querySelector('.settings-runtime-panel')?.textContent.includes('运行时已就绪') === true",
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

    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      "(() => { const d=document.querySelector('details.recognition-options'); " +
      "if(d?.open) d.querySelector('summary')?.click(); return true; })()");

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
    // sceneEditing 会话的 canvas-editor 只在现场窗口渲染；session DOM 查现场面。
    await WaitForSmokeDomAsync(
      "document.querySelector('.canvas-editor')?.dataset.screenshotSession === " +
      JsonSerializer.Serialize(captured.ScreenshotSession!.SessionId),
      TimeSpan.FromSeconds(30),
      editorSurface: true);
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
    catch (Exception error) when (error is OperationCanceledException or TimeoutException)
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
      "pdf_operations" => await RunPaddlePdfOperationsAsync(mode, fixture, session, timeoutMinutes),
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
    var evidence = new
    {
      input_kind = "batch", mode, fixture_paths = new[] { fixture, second },
      option = new { name = optionName, value = optionValue },
      submit_attempts = smokeSubmitAttempts!(), job,
      queue = new { terminal.ItemCount, terminal.CompletedCount,
        terminal.FailedCount, terminal.Items, dom_text = queueText },
      structured_inspected = inspected, copies, exports,
    };
    paddleSmokePartialEvidence = evidence;
    if (PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_BUTTONS") is { Length: > 0 } labels)
      foreach (string label in labels.Split('|', StringSplitOptions.RemoveEmptyEntries))
        exports.Add(await RunPaddleBatchExportAsync(label));
    paddleSmokeOutcome = "passed";
    return evidence;
  }

  private async Task<object> RunPaddlePdfInputAsync(
    string mode, string pipeline, string fixture, string optionName,
    string optionValue, ManagedEnvironmentSession session, int timeoutMinutes)
  {
    if (!Path.GetExtension(fixture).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("PDF input requires a PDF fixture.");
    RecordPaddleSmokeStage($"pdf select mode {mode}");
    await NavigateSmokeAsync("PDF", "button");
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
    bool textLayer = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_PDF_TEXT_LAYER") == "1";
    if (textLayer && pipeline != "OCR") throw new InvalidOperationException("PDF text layer smoke requires the coordinate-preserving OCR pipeline.");
    RecordPaddleSmokeStage($"pdf recognize first page {mode}");
    await ClickManagedSmokeButtonAsync(textLayer ? "添加选中页文字层" : "提取/解析选中页");
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
    if (textLayer && (!terminal.IsModified || terminal.Pages?.FirstOrDefault(page => page.Index == 0)?.AddedThisSession != true))
      throw new InvalidOperationException("PDF text layer was not committed as an unsaved current-session layer.");
    bool inspected = !textLayer && await InspectPaddlePdfStructureAsync(mode);
    var copies = await ClickPaddleCopyButtonsAsync([]);
    paddleSmokePartialEvidence = new
    {
      input_kind = "pdf", mode, job,
      pdf = new { opened.PageCount, terminal.SelectedPages, terminal.Pages },
      structured_inspected = inspected, copies,
    };
    if (PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_PDF_WORKSPACE") == "1")
    {
      object workspace = await RunPaddlePdfWorkspaceAsync(terminal, fixture, timeoutMinutes);
      paddleSmokeOutcome = "passed";
      return new { input_kind = "pdf_workspace", mode, fixture_path = fixture, job, workspace };
    }
    if (PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_PDF_EDITING") == "1")
    {
      if (!textLayer) throw new InvalidOperationException("PDF editing smoke requires a committed OCR layer.");
      object editing = await RunPaddlePdfEditingAsync(terminal);
      paddleSmokeOutcome = "passed";
      return new { input_kind = "pdf_editing", mode, fixture_path = fixture, job, editing };
    }
    object saved = await RunPaddlePdfSaveAsync();
    paddleSmokeOutcome = "passed";
    return new
    {
      input_kind = "pdf", mode, fixture_path = fixture,
      option = new { name = optionName, value = optionValue },
      submit_attempts = smokeSubmitAttempts!(), job,
      pdf = new { opened.PageCount, terminal.SelectedPages, terminal.Pages },
      structured_inspected = inspected, text_layer = textLayer, copies, saved,
    };
  }

  private async Task<object> RunPaddlePdfEditingAsync(PdfWorkbenchState layered, bool workspace = false)
  {
    RecordPaddleSmokeStage("PDF HD editing: cancel and page switch");
    double scale = WindowGeometryPolicy.GetWindowScale(WinRT.Interop.WindowNative.GetWindowHandle(this));
    AppWindow.Resize(new SizeInt32(WindowGeometryPolicy.ScaleToPhysical(640, scale), WindowGeometryPolicy.ScaleToPhysical(768, scale)));
    await WaitForSmokeDomAsync("document.querySelectorAll('.pdf-text-box.ocr').length >= 2 && !!document.querySelector('.pdf-inspection-sheet img')?.naturalWidth", TimeSpan.FromSeconds(30));
    await ClickManagedSmokeButtonAsync("适应页面");
    string indicesJson = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("""
      (() => [...document.querySelectorAll('.pdf-text-box.ocr')]
        .sort((a,b) => b.getBoundingClientRect().width*b.getBoundingClientRect().height-a.getBoundingClientRect().width*a.getBoundingClientRect().height)
        .slice(0,2).map(e => Number(e.dataset.blockIndex)))()
      """);
    int[] indices = JsonSerializer.Deserialize<int[]>(indicesJson) ?? throw new InvalidOperationException("Missing editable OCR blocks.");
    if (indices.Length != 2) throw new InvalidOperationException("Two trusted OCR blocks are required.");
    async Task SelectBlockAsync(int index)
    {
      string selector = $".pdf-text-box.ocr[data-block-index='{index}']";
      await WaitForSmokeDomAsync($"!!document.querySelector({JsonSerializer.Serialize(selector)})", TimeSpan.FromSeconds(30));
      string result = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync($"(() => {{ const e=document.querySelector({JsonSerializer.Serialize(selector)}); e.scrollIntoView({{block:'center'}}); e.click(); return true; }})()");
      if (result != "true") throw new InvalidOperationException("OCR block selection failed.");
      await WaitForSmokeDomAsync("!!document.querySelector('textarea[aria-label=\"校正文字\"]')", TimeSpan.FromSeconds(30));
    }
    async Task SetDraftAsync(string text)
    {
      string result = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync($$"""
        (() => { const e=document.querySelector('textarea[aria-label="校正文字"]');
          if(!e) return false;
          Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(e,{{JsonSerializer.Serialize(text)}});
          e.dispatchEvent(new Event('input',{bubbles:true})); return true; })()
        """);
      if (result != "true") throw new InvalidOperationException("OCR draft input is missing.");
      await WaitForSmokeDomAsync($"document.querySelector('textarea[aria-label=\"校正文字\"]')?.value === {JsonSerializer.Serialize(text)}", TimeSpan.FromSeconds(30));
    }
    await SelectBlockAsync(indices[0]);
    await SetDraftAsync("CANCELLED_DRAFT201");
    await ClickManagedSmokeButtonAsync("取消校正");
    PdfWorkbenchState cancelled = await WaitForPaddlePdfAsync(state => !state.IsBusy, TimeSpan.FromSeconds(30));
    if (cancelled.Revision != layered.Revision) throw new InvalidOperationException("Cancelling a draft changed PDF revision.");
    await ClickManagedSmokeButtonAsync("插入空白页");
    PdfWorkbenchState inserted = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.PageCount == layered.PageCount + 1, TimeSpan.FromSeconds(30));
    await SelectBlockAsync(indices[0]); await SetDraftAsync("PAGE_SWITCH_DRAFT201");
    await ClickManagedSmokeButtonAsync("下一页");
    await WaitForSmokeDomAsync("!document.querySelector('textarea[aria-label=\"校正文字\"]') && document.querySelector('.pdf-inspection-sheet img')?.alt.includes('第 2 页')", TimeSpan.FromSeconds(30));
    await ClickManagedSmokeButtonAsync("上一页");
    await WaitForPaddlePdfAsync(state => state.SelectedPage == 0 && state.PageInspectStatusCode == "pdf.inspect.ready", TimeSpan.FromSeconds(30));
    string[] replacements = [
      "中文校正长句：高清检查中的可搜索文字应完整保存，保留扫描图形与其他文字块，并在重新打开后找到末尾标记CNTAIL201。",
      "English correction keeps the entire searchable sentence after saving and reopening, including this final marker ENTAIL201."
    ];
    var edits = new List<object>();
    PdfWorkbenchState edited = inserted;
    for (int position = 0; position < indices.Length; position++)
    {
      await SelectBlockAsync(indices[position]);
      string oldTextJson = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("document.querySelector('textarea[aria-label=\"校正文字\"]').value");
      string oldText = JsonSerializer.Deserialize<string>(oldTextJson)!;
      await SetDraftAsync(replacements[position]);
      string reachable = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("""
        (() => { const b=[...document.querySelectorAll('button')].find(e=>e.textContent.trim()==='提交校正');
          if(!b||b.disabled) return false; b.scrollIntoView({block:'center'}); b.focus(); const r=b.getBoundingClientRect();
          return r.width>0&&r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight; })()
        """);
      if (reachable != "true") throw new InvalidOperationException("Correction button is unreachable in the narrow viewport.");
      long before = edited.Revision;
      await ClickManagedSmokeButtonAsync("提交校正");
      edited = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.SessionId == inserted.SessionId && state.SelectedPage == 0 && state.Revision > before && state.PageInspectStatusCode == "pdf.inspect.ready" && state.PagePreview is not null && state.PageInspect is not null, TimeSpan.FromSeconds(30));
      string expectedPreview = JsonSerializer.Serialize(edited.PagePreview!.Url);
      await WaitForSmokeDomAsync($"(() => {{ const image=document.querySelector('.pdf-inspection-sheet img'); return image?.src === {expectedPreview} && image.complete && image.naturalWidth > 160 && image.naturalHeight > 0 && document.querySelectorAll('.pdf-text-box.ocr').length >= 2; }})()", TimeSpan.FromSeconds(30));
      if (!edited.IsModified) throw new InvalidOperationException("Correction was not marked unsaved.");
      edits.Add(new { block_index = indices[position], old_text = oldText, new_text = replacements[position], edited.Revision, edited.IsModified, edit_button_reachable = true });
      paddleSmokePartialEvidence = new { input_kind = "pdf_editing", edits, edited.Revision, edited.IsModified };
    }
    string viewportJson = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("""
      (() => { const b=[...document.querySelectorAll('button')].find(e=>e.textContent.trim()==='保存');
        if(!b||b.disabled) throw new Error('Save unavailable'); b.scrollIntoView({block:'center'}); b.focus(); const r=b.getBoundingClientRect();
        const image=document.querySelector('.pdf-inspection-sheet img');
        return {width:innerWidth,height:innerHeight,devicePixelRatio,save_reachable:r.width>0&&r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight,
          hd_width:image?.naturalWidth,hd_height:image?.naturalHeight,hd_url:image?.src,boxes:document.querySelectorAll('.pdf-text-box').length}; })()
      """);
    JsonElement viewportEvidence = JsonSerializer.Deserialize<JsonElement>(viewportJson);
    if (!viewportEvidence.TryGetProperty("save_reachable", out JsonElement saveReachable) || saveReachable.ValueKind != JsonValueKind.True ||
        !viewportEvidence.TryGetProperty("hd_width", out JsonElement hdWidth) || !hdWidth.TryGetInt32(out int width) || width <= 160 ||
        !viewportEvidence.TryGetProperty("hd_height", out JsonElement hdHeight) || !hdHeight.TryGetInt32(out int height) || height <= 0)
      throw new InvalidOperationException($"Save button or current revision HD preview evidence is unavailable: {viewportJson}");
    string editingScreenshot = Path.Combine(ValidatePaddleSmokeOwnedPath(RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_DIR"), "pdf editing evidence"), "pdf-editing-narrow.png");
    using (new FileStream(editingScreenshot, FileMode.CreateNew)) { }
    StorageFile editingPreview = await StorageFile.GetFileFromPathAsync(editingScreenshot);
    using (IRandomAccessStream previewStream = await editingPreview.OpenAsync(FileAccessMode.ReadWrite))
    {
      await WorkbenchWebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, previewStream);
      await previewStream.FlushAsync();
    }
    object saved = await RunPaddlePdfSaveAsync("editing");
    string outputPath = JsonSerializer.SerializeToElement(saved).GetProperty("file").GetString()!;
    if (workspace)
    {
      await SelectBlockAsync(indices[0]);
      string continuedText = replacements[0] + " AFTER_SAVE_AS202";
      await SetDraftAsync(continuedText);
      long savedRevision = edited.Revision;
      await ClickManagedSmokeButtonAsync("提交校正");
      PdfWorkbenchState continued = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Revision > savedRevision && state.IsModified && state.PageInspectStatusCode == "pdf.inspect.ready", TimeSpan.FromSeconds(30));
      edits.Add(new { block_index = indices[0], old_text = replacements[0], new_text = continuedText, continued.Revision, continued.IsModified });
      await ClickManagedSmokeButtonAsync("保存");
      await WaitForPaddlePdfAsync(state => !state.IsBusy && !state.IsModified && state.DocumentId == continued.DocumentId, TimeSpan.FromMinutes(2));
      return new { edits, saved, continued_revision = continued.Revision, continued.DocumentId, continued.PageCount, editing_screenshot = editingScreenshot, window_scale = scale, viewport = viewportEvidence };
    }
    await ClickManagedSmokeButtonAsync("关闭文档");
    await WaitForPaddlePdfAsync(state => state.PageCount == 0, TimeSpan.FromSeconds(30));
    await ClickManagedSmokeButtonAsync("打开 PDF"); await CompletePaddleOpenPickerAsync(outputPath);
    PdfWorkbenchState reopened = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.PageCount == 2 && state.DetectedCount == 2 && state.PageInspectStatusCode == "pdf.inspect.ready", TimeSpan.FromSeconds(30));
    await WaitForSmokeDomAsync("document.querySelectorAll('.pdf-text-box.native').length > 0", TimeSpan.FromSeconds(30));
    string reopenedJson = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('.pdf-text-box')].map(e=>e.getAttribute('aria-label'))");
    string[] reopenedLabels = JsonSerializer.Deserialize<string[]>(reopenedJson) ?? [];
    if (!reopenedLabels.Any(label => label.Contains(replacements[0][..16], StringComparison.Ordinal)) || !reopenedLabels.Any(label => label.Contains(replacements[1][..26], StringComparison.Ordinal)) || reopenedLabels.Any(label => label.Contains("CANCELLED_DRAFT201", StringComparison.Ordinal) || label.Contains("PAGE_SWITCH_DRAFT201", StringComparison.Ordinal)))
      throw new InvalidOperationException("Reopened PDF does not expose corrected text previews or contains cancelled drafts.");
    return new { edits, cancelled_revision = cancelled.Revision, inserted_revision = inserted.Revision,
      edited.Revision, edited.IsModified, saved, reopened_revision = reopened.Revision, reopened.IsBusy,
      reopened.PageCount, reopened_resources = new { reopened.PagePreview, reopened.PageInspect }, reopened_labels = reopenedLabels, editing_screenshot = editingScreenshot, full_text_verification = "external-extraction-required",
      window_scale = scale, window_dpi = PaddleSmokeNative.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)), viewport = viewportEvidence };
  }
  private async Task<object> RunPaddlePdfWorkspaceAsync(PdfWorkbenchState layered, string fixture, int timeoutMinutes)
  {
    if (layered.PageCount != 75 || layered.DocumentId is null) throw new InvalidOperationException("Workspace fixture A must contain 75 pages.");
    string fixtures = Path.GetDirectoryName(Path.GetDirectoryName(fixture))!;
    string second = ValidatePaddleSmokeOwnedPath(Path.Combine(fixtures, "workspace-b", "same.pdf"), "workspace second fixture");
    string external = ValidatePaddleSmokeOwnedPath(Path.Combine(fixtures, "document_mixed.pdf"), "workspace insertion fixture");
    string exports = ValidatePaddleSmokeOwnedPath(RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_DIR"), "workspace exports");
    var resources = new List<object>();
    async Task SampleResourcesAsync(string stage)
    {
      int thumbnails = int.Parse(await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.pdf-page-list img').length"));
      string[] files = Directory.GetFiles(resourceRoot, "*", SearchOption.AllDirectories);
      if (thumbnails > 64 || files.Length > 72) throw new InvalidOperationException($"Workspace resource budget exceeded at {stage}: {thumbnails}/{files.Length}");
      using var process = System.Diagnostics.Process.GetCurrentProcess();
      PdfWorkbenchState state = await WaitForPaddlePdfAsync(_ => true, TimeSpan.FromSeconds(30));
      resources.Add(new { stage, thumbnails, published_files = files.Length, published_bytes = files.Sum(path => new FileInfo(path).Length),
        app_peak_working_set_bytes = process.PeakWorkingSet64, peak_scope = "current App process only; excludes Runtime/OCR children",
        pdf_control_state_json_bytes = JsonSerializer.SerializeToUtf8Bytes(state).Length });
    }
    async Task SelectPageAsync(int page)
    {
      await ClickManagedSmokeButtonAsync("取消选择");
      string selector = $"input[aria-label='选择第 {page + 1} 页']";
      await WaitForSmokeDomAsync($"!!document.querySelector({JsonSerializer.Serialize(selector)})", TimeSpan.FromSeconds(30));
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync($"document.querySelector({JsonSerializer.Serialize(selector)}).click()");
      await WaitForPaddlePdfAsync(state => state.SelectedPages?.SequenceEqual(new[] { page }) == true && state.SelectedPage == page && state.PageInspectStatusCode == "pdf.inspect.ready", TimeSpan.FromSeconds(30));
    }
    async Task ActivateAsync(int index, string documentId)
    {
      await WaitForSmokeDomAsync($"document.querySelectorAll('.pdf-documents button').length > {index}", TimeSpan.FromSeconds(30));
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync($"document.querySelectorAll('.pdf-documents button')[{index}].click()");
      await WaitForPaddlePdfAsync(state => state.DocumentId == documentId && state.PageInspectStatusCode == "pdf.inspect.ready", TimeSpan.FromSeconds(30));
    }
    async Task<PdfWorkbenchState> ExportCopiesAsync(string directory, bool retry = false, bool cancel = false)
    {
      Directory.CreateDirectory(directory);
      long previousGeneration = (await WaitForPaddlePdfAsync(_ => true, TimeSpan.FromSeconds(30))).ExportGeneration;
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("(() => { const e=document.querySelector('details.pdf-export'); if(!e.open)e.querySelector('summary').click(); })()");
      if (cancel)
      {
        // Observe the actual public stop button becoming enabled; click it during the
        // first non-cancellable save, without sleeps or private command injection.
        string armed = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("""
          (() => { window.__pdfSmokeCancelled=false; const observer=new MutationObserver(() => {
            const b=[...document.querySelectorAll('button')].find(e=>e.textContent.trim()==='停止后续导出');
            if(b&&!b.disabled){observer.disconnect();b.click();window.__pdfSmokeCancelled=true;}
          }); observer.observe(document.body,{subtree:true,attributes:true,childList:true}); window.__pdfSmokeCancelObserver=observer; return true; })()
          """);
        if (armed != "true") throw new InvalidOperationException("Export cancellation observer was not armed.");
      }
      await ClickManagedSmokeButtonAsync(retry ? "重试未完成项" : "选择目录并导出");
      nint dialog = await WaitForPaddleSaveDialogAsync(TimeSpan.FromSeconds(30));
      try { await CompletePaddlePickerAsync(dialog, directory, isFolder: true); }
      catch { CancelPaddleDialog(dialog); throw; }
      PdfWorkbenchState result = await WaitForPaddlePdfAsync(state => state.ExportGeneration > previousGeneration && !state.Exporting && state.ExportItems?.Count == 2 && state.ExportItems.All(item => item.Status != "saving"), TimeSpan.FromMinutes(2));
      if (cancel)
      {
        string cancelled = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("(() => {window.__pdfSmokeCancelObserver?.disconnect();return window.__pdfSmokeCancelled===true;})()");
        if (cancelled != "true" || !result.ExportItems!.Any(item => item.Status == "saved") || !result.ExportItems!.Any(item => item.Status is "cancelled" or "not_started"))
          throw new InvalidOperationException($"Batch cancellation did not retain a completed output and stop subsequent files: {JsonSerializer.Serialize(result.ExportItems)}");
      }
      return result;
    }
    RecordPaddleSmokeStage("workspace overwrite, explicit native deletion, orientation and structure");
    await SampleResourcesAsync("initial-75");
    string overwritten = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("""
      (() => {const label=[...document.querySelectorAll('label')].find(e=>e.textContent.includes('显式覆盖已有文字层'));
        if(!label)return false;label.click();return document.getElementById(label.htmlFor)?.checked===true;})()
      """);
    if (overwritten != "true") throw new InvalidOperationException("Explicit overwrite was not enabled through its public checkbox.");
    long revision = layered.Revision;
    await ClickManagedSmokeButtonAsync("添加选中页文字层");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Revision > revision && state.AddedCount == 1, TimeSpan.FromMinutes(timeoutMinutes));
    await SelectPageAsync(1);
    await ClickManagedSmokeButtonAsync("删除选中文字层");
    await ClickManagedSmokeButtonAsync("确认删除文字层");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Pages?.FirstOrDefault(page => page.Index == 1)?.HasTextLayer == false, TimeSpan.FromSeconds(30));
    await SelectPageAsync(0);
    await ClickManagedSmokeButtonAsync("自动文字朝向");
    PdfWorkbenchState oriented = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Summary.Contains("失败 0 页", StringComparison.Ordinal) && state.Summary.Contains("未处理 0 页", StringComparison.Ordinal), TimeSpan.FromMinutes(timeoutMinutes));
    await EnterSmokeTextAsync("#pdf-insert-after", "75");
    await ClickManagedSmokeButtonAsync("插入其他 PDF"); await CompletePaddleOpenPickerAsync(external);
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.PageCount == 76, TimeSpan.FromSeconds(30));
    // Reorder within the active window, then restore the first OCR page for HD editing.
    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("document.querySelector('button[aria-label=\"第 1 页向后移动\"]').click()");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.SelectedPage == 1, TimeSpan.FromSeconds(30));
    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("document.querySelector('button[aria-label=\"第 2 页向前移动\"]').click()");
    PdfWorkbenchState ready = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.SelectedPage == 0 && state.PageInspectStatusCode == "pdf.inspect.ready", TimeSpan.FromSeconds(30));
    await EnterSmokeTextAsync("#pdf-insert-after", "76");
    object editing = await RunPaddlePdfEditingAsync(ready, workspace: true);
    JsonElement editEvidence = JsonSerializer.SerializeToElement(editing);
    string saveTarget = editEvidence.GetProperty("saved").GetProperty("file").GetString()!;
    await ClickManagedSmokeButtonAsync("下一组");
    await WaitForPaddlePdfAsync(state => state.WindowStart == 64 && state.Pages?.Count == 13, TimeSpan.FromSeconds(30));
    await SampleResourcesAsync("tail-window-77");
    await ClickManagedSmokeButtonAsync("上一组");
    await WaitForPaddlePdfAsync(state => state.WindowStart == 0 && state.Pages?.Count == 64, TimeSpan.FromSeconds(30));
    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("document.querySelector('button[aria-label=\"放大页面\"]').click()");
    await ClickManagedSmokeButtonAsync("添加 PDF"); await CompletePaddleOpenPickerAsync(second);
    PdfWorkbenchState b = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.PageCount == 3 && state.Documents?.Count == 2 && state.DocumentId != layered.DocumentId && state.PageInspectStatusCode == "pdf.inspect.ready", TimeSpan.FromSeconds(30));
    string bId = b.DocumentId!;
    await SampleResourcesAsync("active-b-3");
    await ClickManagedSmokeButtonAsync("顺时针 90°");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.IsModified, TimeSpan.FromSeconds(30));
    await ClickManagedSmokeButtonAsync("逆时针 90°");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Pages![0].Rotation == 0, TimeSpan.FromSeconds(30));
    await ActivateAsync(0, layered.DocumentId);
    await SampleResourcesAsync("reactivate-a-77");
    await ClickManagedSmokeButtonAsync("顺时针 90°");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.IsModified, TimeSpan.FromSeconds(30));
    await ClickManagedSmokeButtonAsync("逆时针 90°");
    PdfWorkbenchState beforeCopies = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Pages![0].Rotation == 0, TimeSpan.FromSeconds(30));
    if (beforeCopies.Documents!.Any(document => !document.IsModified)) throw new InvalidOperationException("Both source entries must remain dirty before copy export.");
    RecordPaddleSmokeStage("workspace real window exit confirmation cancelled");
    nint ownedWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
    if (!PaddleSmokeNative.PostMessageW(ownedWindow, 0x0112, 0xF060, 0)) throw new InvalidOperationException("Owned window close request was rejected.");
    await WaitForPaddleConditionAsync(() => activePdfCloseDialog is { IsLoaded: true }, TimeSpan.FromSeconds(30));
    static Microsoft.UI.Xaml.Controls.Button? FindCancelButton(Microsoft.UI.Xaml.DependencyObject parent)
    {
      if (parent is Microsoft.UI.Xaml.Controls.Button button && button.Content is string text && text == "取消" && button.IsEnabled) return button;
      for (int index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        if (FindCancelButton(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
      return null;
    }
    Microsoft.UI.Xaml.Controls.Button cancelButton = FindCancelButton(activePdfCloseDialog!) ?? throw new InvalidOperationException("Real PDF close dialog has no public cancel button.");
    var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(cancelButton);
    var invoke = peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke) as Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider
      ?? throw new InvalidOperationException("Close dialog cancel button does not expose Invoke.");
    invoke.Invoke();
    await WaitForPaddleConditionAsync(() => activePdfCloseDialog is null, TimeSpan.FromSeconds(30));
    PdfWorkbenchState afterExitCancel = await WaitForPaddlePdfAsync(state => state.DocumentId == layered.DocumentId && state.Documents?.Count == 2 && state.Documents.All(document => document.IsModified), TimeSpan.FromSeconds(30));
    RecordPaddleSmokeStage("workspace copy export commit collision and retry");
    string partialDirectory = Path.Combine(exports, "partial"); Directory.CreateDirectory(partialDirectory);
    string competitor = Path.Combine(partialDirectory, Path.GetFileName(saveTarget));
    var raceObserved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var watcher = new FileSystemWatcher(partialDirectory, ".*.tmp"))
    {
      watcher.Created += (_, change) => {
        if (!Path.GetFileName(change.FullPath).StartsWith($".{Path.GetFileName(saveTarget)}.", StringComparison.Ordinal)) return;
        try { using var file = new FileStream(competitor, FileMode.CreateNew, FileAccess.Write, FileShare.Read); using var writer = new StreamWriter(file); writer.Write("T4_SYNTHETIC_COMPETITOR"); writer.Flush(); raceObserved.TrySetResult(competitor); }
        catch (Exception error) { raceObserved.TrySetException(error); }
      };
      watcher.EnableRaisingEvents = true;
      PdfWorkbenchState partial = await ExportCopiesAsync(partialDirectory);
      await raceObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
      if (partial.ExportItems!.Select(item => item.Status).SequenceEqual(new[] { "failed", "saved" }) != true || File.ReadAllText(competitor) != "T4_SYNTHETIC_COMPETITOR")
        throw new InvalidOperationException($"Final commit competition was not reported per file: {JsonSerializer.Serialize(partial.ExportItems)}");
      paddleSmokePartialEvidence = new { input_kind = "pdf_workspace", editing, partial.ExportItems, competitor, resources };
    }
    PdfWorkbenchState retried = await ExportCopiesAsync(partialDirectory, retry: true);
    if (retried.ExportItems!.Any(item => item.Status != "saved") || retried.Documents!.Any(document => !document.IsModified)) throw new InvalidOperationException("Retry failed or copy cleared source dirty flags.");
    string cancelDirectory = Path.Combine(exports, "cancelled");
    PdfWorkbenchState cancelledBatch = await ExportCopiesAsync(cancelDirectory, cancel: true);
    PdfWorkbenchState completedBatch = await ExportCopiesAsync(cancelDirectory, retry: true);
    if (completedBatch.ExportItems!.Any(item => item.Status != "saved") || completedBatch.Documents!.Any(document => !document.IsModified)) throw new InvalidOperationException("Cancelled batch retry failed or lost modifications.");
    object aSaved = await SaveCurrentWorkspaceTargetAsync(saveTarget);
    await ActivateAsync(1, bId);
    object bSaved = await RunPaddlePdfSaveAsync("workspace-b");
    await ClickManagedSmokeButtonAsync("关闭文档");
    await WaitForPaddlePdfAsync(state => state.Documents?.Count == 1, TimeSpan.FromSeconds(30));
    await ClickManagedSmokeButtonAsync("关闭文档");
    await WaitForPaddlePdfAsync(state => state.PageCount == 0, TimeSpan.FromSeconds(30));
    string reopenedTarget = completedBatch.ExportItems!.First(item => item.DocumentId == layered.DocumentId).Output!;
    // Public state exposes basenames; resolve only inside the synthetic export directory.
    reopenedTarget = Path.Combine(cancelDirectory, reopenedTarget);
    await ClickManagedSmokeButtonAsync("打开 PDF"); await CompletePaddleOpenPickerAsync(reopenedTarget);
    PdfWorkbenchState reopened = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.PageCount == 77 && state.PageInspectStatusCode == "pdf.inspect.ready", TimeSpan.FromSeconds(30));
    await SampleResourcesAsync("reopened-copy-a");
    return new { editing, oriented.Summary, original_paths = new[] { fixture, second }, a_saved = aSaved, b_saved = bSaved,
      partial_directory = partialDirectory, retry_items = retried.ExportItems, competitor,
      cancelled_directory = cancelDirectory, cancelled_items = cancelledBatch.ExportItems, completed_items = completedBatch.ExportItems,
      before_copy_documents = beforeCopies.Documents, after_copy_documents = completedBatch.Documents, reopened.PageCount,
      exit_cancel = new { real_window_close = true, public_dialog_cancel = true, afterExitCancel.DocumentId, retained_documents = afterExitCancel.Documents?.Count, continued_export = true },
      resources, full_text_verification = "external-extraction-required" };
  }

  private async Task<object> SaveCurrentWorkspaceTargetAsync(string target)
  {
    await ClickManagedSmokeButtonAsync("保存");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && !state.IsModified, TimeSpan.FromMinutes(2));
    if (!File.Exists(target)) throw new InvalidOperationException("Current target disappeared after save.");
    return new { file = target, bytes = new FileInfo(target).Length, saved_via = "current-target" };
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

  private async Task<object> RunPaddlePdfOperationsAsync(string mode, string fixture, ManagedEnvironmentSession session, int timeoutMinutes)
  {
    if (mode != "paddle_text") throw new InvalidOperationException("PDF operations smoke requires Paddle text OCR.");
    await NavigateSmokeAsync("PDF", "button");
    await WaitForPaddleModeAsync("#pdf-task-engine", mode);
    await SelectSmokeValueAsync("#pdf-task-engine", mode);
    RecordPaddleSmokeStage("open eight orientation pages via public picker");
    await ClickManagedSmokeButtonAsync("打开 PDF");
    await CompletePaddleOpenPickerAsync(fixture);
    PdfWorkbenchState opened = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.DetectedCount == 8, TimeSpan.FromMinutes(2));
    await ClickManagedSmokeButtonAsync("全选页面");
    await SelectSmokeValueAsync("#pdf-operation-range", "all");
    int before = smokeSubmitAttempts!();
    RecordPaddleSmokeStage("correct all eight orientation pages through public UI");
    await ClickManagedSmokeButtonAsync("自动文字朝向");
    PdfWorkbenchState corrected = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Summary.Contains("已纠正 6 页、无需处理 2 页、失败 0 页、未处理 0 页", StringComparison.Ordinal), TimeSpan.FromMinutes(timeoutMinutes));
    int[] expectedRotations = [0, 0, 270, 270, 180, 180, 90, 90];
    if (corrected.Pages is null || !corrected.Pages.Select(page => page.Rotation).SequenceEqual(expectedRotations) || smokeSubmitAttempts!() - before != 8)
      throw new InvalidOperationException($"Orientation page/job mapping failed: {JsonSerializer.Serialize(corrected)}");
    object correctedJob = await ObservePaddleInputJobAsync(session, "OCR", "local_models_only", "true", smokeSubmitAttempts!() - 1, 1);
    RecordPaddleSmokeStage("repeat orientation and verify all eight pages are skipped");
    await ClickManagedSmokeButtonAsync("自动文字朝向");
    PdfWorkbenchState repeated = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Summary.Contains("已纠正 0 页、无需处理 8 页、失败 0 页、未处理 0 页", StringComparison.Ordinal), TimeSpan.FromMinutes(timeoutMinutes));
    object repeatedJob = await ObservePaddleInputJobAsync(session, "OCR", "local_models_only", "true", smokeSubmitAttempts!() - 1, 1);
    object orientationSaved = await RunPaddlePdfSaveAsync("orientation");
    paddleSmokePartialEvidence = new { opened, corrected, repeated, corrected_job = correctedJob, repeated_job = repeatedJob, orientation_saved = orientationSaved };

    await ClickManagedSmokeButtonAsync("关闭文档");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.PageCount == 0, TimeSpan.FromMinutes(2));
    string fixtures = Path.GetDirectoryName(fixture)!;
    string scanned = ValidatePaddleSmokeOwnedPath(Path.Combine(fixtures, "document_scan.pdf"), "PDF operations scan fixture");
    string external = ValidatePaddleSmokeOwnedPath(Path.Combine(fixtures, "document_mixed.pdf"), "PDF operations insertion fixture");
    RecordPaddleSmokeStage("add T1 text layer before structure operations");
    await ClickManagedSmokeButtonAsync("打开 PDF"); await CompletePaddleOpenPickerAsync(scanned);
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.DetectedCount == 1 && state.CanAddTextLayer, TimeSpan.FromMinutes(2));
    await ClickManagedSmokeButtonAsync("添加选中页文字层");
    PdfWorkbenchState layered = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.AddedCount == 1 && state.IsModified, TimeSpan.FromMinutes(timeoutMinutes));
    long revision = layered.Revision;
    await ClickManagedSmokeButtonAsync("顺时针 90°");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Revision > revision, TimeSpan.FromMinutes(2));
    await EnterSmokeTextAsync("#pdf-insert-after", "1");
    await EnterSmokeTextAsync("#pdf-blank-width", "640");
    await EnterSmokeTextAsync("#pdf-blank-height", "480");
    await ClickManagedSmokeButtonAsync("插入空白页");
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.PageCount == 2, TimeSpan.FromMinutes(2));
    await ClickManagedSmokeButtonAsync("插入其他 PDF"); await CompletePaddleOpenPickerAsync(external);
    PdfWorkbenchState inserted = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.PageCount == 3, TimeSpan.FromMinutes(2));
    if (inserted.Pages is not { Count: 3 } insertedPages || insertedPages[0].AddedThisSession != true || insertedPages[1].HasTextLayer != true || insertedPages[2].Width != 640 || insertedPages[2].Height != 480)
      throw new InvalidOperationException("Inserted PDF/page metadata did not preserve the original OCR page.");
    RecordPaddleSmokeStage("keyboard-accessible move followed by drag reorder");
    revision = inserted.Revision;
    await WaitForSmokeDomAsync("(() => {const button=document.querySelector('button[aria-label=\"第 1 页向后移动\"]');if(!button||button.disabled)return false;button.focus();button.click();return true;})()", TimeSpan.FromSeconds(30));
    await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Revision > revision, TimeSpan.FromMinutes(2));
    string dragged = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      "(() => {const from=document.querySelector('[data-page-index=\"1\"]'),to=document.querySelector('[data-page-index=\"2\"]');if(!from||!to)return false;from.dispatchEvent(new DragEvent('dragstart',{bubbles:true}));to.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true}));from.dispatchEvent(new DragEvent('dragend',{bubbles:true}));return true;})()");
    if (dragged != "true") throw new InvalidOperationException("Public PDF drag controls are unavailable.");
    PdfWorkbenchState reordered = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Pages?[2].AddedThisSession == true, TimeSpan.FromMinutes(2));
    if (reordered.SelectedPages is null || !reordered.SelectedPages.SequenceEqual([2])) throw new InvalidOperationException("Page selection did not follow OCR page identity.");
    RecordPaddleSmokeStage("continue OCR on current rotated revision");
    await ClickManagedSmokeButtonAsync("提取/解析选中页");
    PdfWorkbenchState continued = await WaitForPaddlePdfAsync(state => !state.IsBusy && state.Pages?[2].StatusCode == "pdf.page.done" && state.Summary.StartsWith("已提取/解析", StringComparison.Ordinal), TimeSpan.FromMinutes(timeoutMinutes));
    object combinedSaved = await RunPaddlePdfSaveAsync("operations");
    paddleSmokeOutcome = "passed";
    return new { input_kind = "pdf_operations", fixture_path = fixture, submit_attempts = smokeSubmitAttempts!(), opened, corrected, repeated, corrected_job = correctedJob, repeated_job = repeatedJob, orientation_saved = orientationSaved, layered, inserted, reordered, continued, combined_saved = combinedSaved,
      drag_input = "public DOM DragEvent", move_input = "public accessible button", expected_combined_order = new[] { "external text PDF", "640x480 blank page", "original OCR scan with Rotate=90" } };
  }

  private async Task<PdfWorkbenchState> WaitForPaddlePdfAsync(
    Func<PdfWorkbenchState, bool> done, TimeSpan timeout)
  {
    using var cancellation = new CancellationTokenSource(timeout);
    while (true)
    {
      PdfWorkbenchState state = (await application.BootstrapAsync(cancellation.Token))
        .States.Select(item => item.State).OfType<PdfWorkbenchState>().Single();
      if (!state.IsBusy && state.StatusCode is
          "pdf.failed" or "pdf.backendUnavailable" or "pdf.outOfMemory" or "pdf.cancelled")
        throw new InvalidOperationException($"PDF operation stopped: {state.StatusCode}");
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
      // 仅合成输入：ItemOutcome 原始返回（ItemId/PayloadType/Payload），
      // broker JSON 随 App 退出被清理，证据落 health 文件供核对上游结构。
      outcomes = observed.Outcomes,
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
        // 仅合成输入：ItemOutcome 原始返回（ItemId/PayloadType/Payload），
        // broker JSON 随 App 退出被清理，证据落 health 文件供核对上游结构。
        outcomes = observed.Outcomes,
      };
      // 失败兑底快照：后续 UI 验证/导出异常时，已获取的 job/outcomes 不
      // 随顶层 catch 只留 error 字符串而丢失；成功路径 health 合同不变。
      paddleSmokePartialEvidence = new { job = jobEvidence, capture };
    }

    // Host completion precedes the WebView resource fetch; wait for the actual
    // result DOM, including table/formula views, before reading or copying it.
    if (succeeded)
    {
      string selector = mode switch
      {
        "paddle_table" => ".structured-result table td, .structured-result table th",
        "paddle_formula" => ".structured-formula code",
        "paddle_structure" or "paddle_document_vl" => ".structured-blocks li",
        _ => ".result-document",
      };
      await WaitForSmokeDomAsync(
        "(() => { const result=document.querySelector(" + JsonSerializer.Serialize(selector) +
        "); if(!result?.textContent?.trim()) return false; " +
        "const text=[...document.querySelectorAll('.result-document, .structured-result')]" +
        ".map(e=>e.textContent ?? '').join(' ').toLowerCase(); " +
        "const tokens=" + JsonSerializer.Serialize(tokens) + "; " +
        "return !tokens.length || tokens.some(t=>text.includes(t.toLowerCase())); })()",
        TimeSpan.FromSeconds(15));
    }
    string? resultDocument = await PaddleSmokeDomTextAsync(".result-document");
    string? structuredBlocks = await PaddleSmokeDomTextAsync(".structured-result");
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
        "Array.from(document.querySelectorAll('.structured-result table td, " +
        ".structured-result table th')).some(cell => cell.rowSpan > 1 || cell.colSpan > 1)");
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
      paddleSmokePartialEvidence = new { job = jobEvidence, capture, ui };
      if (mode == "paddle_formula")
      {
        const string choice = ".structured-result section:has(.structured-formula) select";
        int count = int.Parse(await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
          $"document.querySelector({JsonSerializer.Serialize(choice)})?.options.length ?? 0"),
          System.Globalization.CultureInfo.InvariantCulture);
        if (count < 2)
          throw new InvalidOperationException("Multi-formula fixture produced fewer than two selectable formulas.");
        var formulas = new List<object>();
        ui["formula_selections"] = formulas;
        for (int index = 0; index < count; index++)
        {
          await SelectSmokeValueAsync(choice, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
          await WaitForSmokeDomAsync(
            "document.querySelector('.structured-result section:has(.structured-formula) label')" +
            $"?.textContent?.trim().startsWith('公式 {index + 1}/{count}')",
            TimeSpan.FromSeconds(5));
          string? latex = await PaddleSmokeDomTextAsync(".structured-formula code");
          bool rendered = await PaddleSmokeDomBoolAsync(
            "(() => { const m=document.querySelector('.structured-formula math'); " +
            "return !!m && m.getBoundingClientRect().width > 0 && " +
            "m.getBoundingClientRect().height > 0; })()");
          await ClickPaddleCopyButtonsAsync([]);
          string? copied = await ReadPaddleClipboardAsync("复制 LaTeX");
          formulas.Add(new { index, latex, rendered, copied });
          if (string.IsNullOrWhiteSpace(latex) || copied != latex || !rendered)
            throw new InvalidOperationException($"Formula {index + 1} selection/render/native copy failed.");
        }
      }
      // 引用可变 ui 的兑底快照：后续导出失败时 copies 与已完成 exports
      // 仍在失败 health 的 partial_evidence 中。
      paddleSmokePartialEvidence = new { job = jobEvidence, capture, ui };
      var exports = new List<object?>();
      // 先挂引用再逐项导出：中途失败时已完成项保留，成功值不变。
      ui["exports"] = exports;
      foreach (string label in exportButtons)
      {
        exports.Add(await RunPaddleUiExportAsync(label));
      }
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
        if (Path.GetFileName(PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_FIXTURE")) == "document_mixed.png" &&
            (ui.GetValueOrDefault("structured_table_present") is not true ||
             ui.GetValueOrDefault("formula_latex_present") is not true ||
             ui.GetValueOrDefault("formula_rendered") is not true ||
             ui.GetValueOrDefault("image_block_present") is not true))
          throw new InvalidOperationException(
            $"{mode} mixed fixture lost its table, rendered formula, or image preview.");
        break;
    }
  }

  /// <summary>
  /// 复制走真实公共按钮 + 系统剪贴板：先确认目标按钮存在，再以原生
  /// Clipboard.Clear 把剪贴板置空（不写入任何目标格式内容），点击后
  /// 读回标准 HtmlFormat/文本核验片段；出现“复制失败”提示即证据
  /// （不重试）。
  /// </summary>
  private async Task<Dictionary<string, object?>> ClickPaddleCopyButtonsAsync(
    string[] tokens)
  {
    var evidence = new Dictionary<string, object?>();
    foreach (string label in new[] { "复制文本", "复制表格 HTML / TSV", "复制 LaTeX" })
    {
      // 先确认目标按钮存在；不存在/禁用时跳过该标签（公共 UI 未提供该
      // 复制能力不算复制失败），存在后才准备剪贴板。
      string present = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "(() => { const b=Array.from(document.querySelectorAll('button')).find(b => " +
        $"b.textContent?.trim() === {JsonSerializer.Serialize(label)}); " +
        "return !!b && !b.disabled; })()");
      if (present != "true") continue;
      // 原生 Clear 而非读基线比对：合法的重复复制会产生与上次相同的内
      // 容，“内容必须变化”会把真成功误判为超时；Clear 后目标格式出现即
      // 本次复制的结果，也不把预写内容冒充通过。Clear 失败即失败。
      await RunOnPaddleUiThreadAsync(() =>
      {
        Clipboard.Clear();
        return true;
      });
      string found = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "(() => { const b=Array.from(document.querySelectorAll('button')).find(b => " +
        $"b.textContent?.trim() === {JsonSerializer.Serialize(label)}); " +
        "if(!b || b.disabled) return false; b.click(); return true; })()");
      if (found != "true") continue;
      evidence[label] = await VerifyPaddleClipboardCopyAsync(label, tokens);
      await WaitForSmokeDomAsync(
        "(() => { const alerts=Array.from(document.querySelectorAll('[role=alert]')); " +
        "return !alerts.some(a => (a.textContent ?? '').includes('复制失败')); })()",
        TimeSpan.FromSeconds(5));
    }
    return evidence;
  }

  /// <summary>
  /// 轮询剪贴板直到目标格式出现（点击前已原生 Clear，出现即本次复制结
  /// 果）：表格 → 标准 HtmlFormat 含 table/td 片段；LaTeX/文本 → 非空纯
  /// 文本。均保留本次自有内容全文供比对（非真实原有剪贴板内容）；剪贴
  /// 板访问固定回 UI 线程队列。
  /// </summary>
  private async Task<object?> VerifyPaddleClipboardCopyAsync(
    string label, string[] tokens)
  {
    bool expectHtml = label == "复制表格 HTML / TSV";
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (true)
    {
      string? snapshot = await ReadPaddleClipboardAsync(label);
      if (snapshot is { Length: > 0 } value)
      {
        if (expectHtml)
        {
          if (!value.Contains("<table", StringComparison.OrdinalIgnoreCase) ||
              !value.Contains("<td", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
              $"{label}: clipboard HtmlFormat lacks table fragment: " +
              value[..Math.Min(200, value.Length)]);
          return new
          {
            format = "CF_HTML",
            table_fragment = true,
            html_chars = value.Length,
            html = value,
          };
        }
        return new
        {
          format = "text",
          chars = value.Length,
          // 点击前已 Clear，value 即本次按钮写入的自有内容，保留全文
          // （TSV/LaTeX/文本）供主代理与上游 Outcomes 比对。
          text = value,
          token_hit = tokens.Length == 0 ? (bool?)null :
            tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase)),
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
      await CompletePaddlePickerAsync(dialog, fixture);
    }
    catch
    {
      CancelPaddleDialog(dialog);
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
      "导出 Excel" or "导出全部 Excel" => "xlsx",
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
      await CompletePaddlePickerAsync(dialog, exportDir, isFolder: true);
    }
    catch
    {
      CancelPaddleDialog(dialog);
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

  private async Task<object> RunPaddlePdfSaveAsync(string? suffix = null)
  {
    string? exportDir = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_EXPORT_DIR");
    if (string.IsNullOrWhiteSpace(exportDir))
      throw new InvalidOperationException("PDF save directory is missing.");
    exportDir = ValidatePaddleSmokeOwnedPath(exportDir, "pdf save dir");
    Directory.CreateDirectory(exportDir);
    string target = Path.Combine(exportDir,
      $"paddle-{RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_MODE")}-ui-save{(suffix is null ? "" : "-" + suffix)}.pdf");
    if (File.Exists(target))
      throw new InvalidOperationException($"PDF save target already exists: {target}");
    RecordPaddleSmokeStage("save PDF via public picker");
    await ClickManagedSmokeButtonAsync("另存为并切换保存目标");
    nint dialog = await WaitForPaddleSaveDialogAsync(TimeSpan.FromSeconds(30));
    try
    {
      await CompletePaddlePickerAsync(dialog, target);
    }
    catch
    {
      CancelPaddleDialog(dialog);
      throw;
    }
    await WaitForPaddleConditionAsync(() =>
      File.Exists(target) && new FileInfo(target).Length > 0,
      TimeSpan.FromMinutes(2));
    await WaitForPaddlePdfAsync(state => !state.IsBusy && !state.IsModified, TimeSpan.FromMinutes(2));
    return new { button_label = "另存为并切换保存目标", file = target,
      bytes = new FileInfo(target).Length, saved_via = "ui-file-save-picker" };
  }

  /// <summary>
  /// 真实公共 UI 导出：点击导出按钮后只操作“被本实例主窗口直接 owned”
  /// 的 FileSavePicker（#32770，WinRT broker 独立进程承载）；归属不明立
  /// 即停止。定向设置原生文件名控件并点击确认；确认后再出现意外
  /// 弹窗定向取消并记失败。不新增生产任意路径入口。
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
      "导出 Excel" => ".xlsx",
      _ => throw new InvalidOperationException($"Unknown export label: {buttonLabel}"),
    };
    string fileName = $"paddle-{mode}-ui-export{extension}";
    string target = Path.Combine(exportDir, fileName);
    if (File.Exists(target))
      throw new InvalidOperationException($"Export target must be a fresh file: {target}");

    RecordPaddleSmokeStage($"ui export {buttonLabel}");
    await ClickManagedSmokeButtonAsync(buttonLabel);
    nint dialog = await WaitForPaddleSaveDialogAsync(TimeSpan.FromSeconds(30));
    Exception? dialogError = null;
    try
    {
      await CompletePaddlePickerAsync(dialog, target);
    }
    catch (Exception error)
    {
      dialogError = error;
      CancelPaddleDialog(dialog); // 只取消本次 owned picker。
    }
    await Task.Delay(500);
    // 确认后残留/新出现的同属保存弹窗（如覆盖确认）视为异常：取消并失败。
    nint extra = FindPaddleSaveDialog();
    if (extra != 0)
    {
      CancelPaddleDialog(extra);
      throw new InvalidOperationException(
        $"Unexpected save dialog after confirming {target}.", dialogError);
    }
    if (dialogError is not null)
      throw new InvalidOperationException($"UI save failed: {dialogError.Message}", dialogError);
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
      status = await PaddleSmokeDomTextAsync(".status-line"),
      saved_via = "ui-file-save-picker",
    };
  }

  private async Task CompletePaddlePickerAsync(nint dialog, string path, bool isFolder = false)
  {
    nint edit = 0;
    nint confirm = 0;
    // Shell picker focus is local to its broker thread; Windows may keep a
    // different app foreground. Address only our owned native controls instead
    // of sending global keyboard input. Observed edits: file 1001/1148, folder 1152.
    await WaitForPaddleConditionAsync(() =>
    {
      if (FindPaddleSaveDialog() != dialog)
        throw new InvalidOperationException("Picker dialog ownership changed.");
      var edits = new List<nint>();
      confirm = 0;
      PaddleSmokeNative.EnumChildWindows(dialog, (child, _) =>
      {
        if (!PaddleSmokeNative.IsWindowVisible(child)) return true;
        int id = PaddleSmokeNative.GetDlgCtrlID(child);
        string kind = PaddleSmokeNative.GetWindowClassName(child);
        if (kind == "Edit" && (isFolder ? id == 1152 : id is 1001 or 1148)) edits.Add(child);
        if (kind == "Button" && id == 1) confirm = child;
        return true;
      }, 0);
      if (edits.Count > 1)
        throw new InvalidOperationException("Picker filename control is ambiguous.");
      edit = edits.Count == 1 ? edits[0] : 0;
      return edit != 0 && confirm != 0;
    }, TimeSpan.FromSeconds(10));
    if (FindPaddleSaveDialog() != dialog || !PaddleSmokeNative.IsChild(dialog, edit))
      throw new InvalidOperationException("Picker filename control ownership changed.");
    if (PaddleSmokeNative.SetText(edit, 0x000C, 0, path, 2, 1000, out nint accepted) == 0 || accepted == 0)
      throw new InvalidOperationException("Picker filename input failed.");
    var actual = new System.Text.StringBuilder(path.Length + 2);
    if (PaddleSmokeNative.ReadText(edit, 0x000D, actual.Capacity, actual, 2, 1000, out _) == 0 ||
        !string.Equals(actual.ToString(), path, StringComparison.Ordinal))
      throw new InvalidOperationException("Picker filename did not retain the isolated path.");
    await WaitForPaddleConditionAsync(() =>
    {
      if (FindPaddleSaveDialog() != dialog || !PaddleSmokeNative.IsChild(dialog, confirm))
        throw new InvalidOperationException("Picker confirmation ownership changed.");
      return PaddleSmokeNative.IsWindowEnabled(confirm);
    }, TimeSpan.FromSeconds(10));
    // FolderPicker requires the button click path; file pickers accept IDOK.
    bool confirmed = isFolder
      ? PaddleSmokeNative.Click(confirm, 0x00F5, 0, 0, 2, 1000, out _) != 0
      : PaddleSmokeNative.PostMessageW(dialog, 0x0111, 1, confirm);
    if (!confirmed)
      throw new InvalidOperationException("Picker confirmation failed.");
    await WaitForPaddleConditionAsync(
      () => !PaddleSmokeNative.IsWindowVisible(dialog), TimeSpan.FromSeconds(30));
  }

  private void CancelPaddleDialog(nint dialog)
  {
    if (FindPaddleSaveDialog() == dialog)
      PaddleSmokeNative.PostMessageW(dialog, 0x0111, 2, 0); // IDCANCEL
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
  /// FileSavePicker 归属规则：可见 AND #32770 AND 直接 owner 是本实例主
  /// 窗口。WinRT picker 由 broker 独立进程承载（实测对话框 pid ≠ App 进
  /// 程 pid、owner 仍指向本实例主窗口），故不做同 PID 要求；不按标题/
  /// 前台放行，也不接受无 owner 窗口。不满足即返回 0，由调用方按
  /// “归属不明”失败，绝不操作其它窗口。
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
      // PickerHost 可跨进程承载；直接 owner 必须仍为本实例主窗口。
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
  /// 只渲染并捕获本选区器自建的纯 Win32 顶层位图窗口：fixture PNG 经
  /// BitmapDecoder 解码 BGRA8，按实际工作区只缩不放适配成 32bpp DIB，
  /// 用 WS_EX_TOPMOST|WS_EX_TOOLWINDOW 的 WS_POPUP STATIC SS_BITMAP 窗口
  /// 直接 GDI 绘制（XAML/WinUI 宿主组合实机不稳定呈全白，含原生子窗，
  /// 见 #110），验证捕获区顶层归属后由真实屏幕捕获服务采集像素。
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
      (int sourceWidth, int sourceHeight) = PngPixelSize(fixture);
      LastCapture = null;
      // #110 实机：XAML Image 两轮全白；12:36 重跑证明即使原生 STATIC
      // 子窗放进 WinUI/XAML 宿主组合仍可能整屏纯白（同 fixture 首跑成功、
      // 重跑 nonWhite=0）。改为本选区器自建的纯 Win32 顶层位图窗口（无
      // 任何 XAML/WinUI 宿主）：BitmapDecoder→只缩不放 DIB→WS_POPUP
      // STATIC SS_BITMAP 直接 GDI 绘制，捕获只覆盖自身客户区。
      nint windowHandle = 0;
      nint bitmap = 0;
      try
      {
        cancellationToken.ThrowIfCancellationRequested();
        // WS_POPUP 顶层 STATIC：无标题栏/边框，客户区即窗口；位图 1:1
        // 物理像素，不做 DPI 放大。
        windowHandle = PaddleSmokeNative.CreateWindowExW(
          0x00000008u | 0x00000080u, "STATIC", null,
          // SS_NOTIFY makes hit-testing return this STATIC instead of HTTRANSPARENT.
          0x80000000u | 0x0000000Eu | 0x00000100u, 0, 0, 1, 1, nint.Zero, 0, 0, 0);
        if (windowHandle == 0)
          throw new InvalidOperationException(
            $"Synthetic bitmap window creation failed: {Marshal.GetLastPInvokeError()}.");
        uint dpi = PaddleSmokeNative.GetDpiForWindow(windowHandle);
        RectInt32 work = DisplayArea.GetFromWindowId(
          Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle),
          DisplayAreaFallback.Nearest).WorkArea;
        // 按实际工作区只缩不放（留边距），fixture 完整可见且不超出屏幕。
        double fitScale = Math.Min(1.0, Math.Min(
          Math.Max(160, work.Width - 60) / (double)sourceWidth,
          Math.Max(160, work.Height - 60) / (double)sourceHeight));
        int displayWidth = Math.Max(1, (int)Math.Round(sourceWidth * fitScale));
        int displayHeight = Math.Max(1, (int)Math.Round(sourceHeight * fitScale));

        // BGRA8 解码；需要缩小时经 WIC 变换降采样（不放大）。
        StorageFile fixtureFile = await StorageFile.GetFileFromPathAsync(fixture);
        using IRandomAccessStream fixtureStream = await fixtureFile.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(fixtureStream);
        var transform = new BitmapTransform
        {
          ScaledWidth = (uint)displayWidth,
          ScaledHeight = (uint)displayHeight,
          InterpolationMode = fitScale < 1.0
            ? BitmapInterpolationMode.Fant : BitmapInterpolationMode.NearestNeighbor,
        };
        PixelDataProvider decoded = await decoder.GetPixelDataAsync(
          BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
          ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] sourcePixels = decoded.DetachPixelData();
        cancellationToken.ThrowIfCancellationRequested();
        if (decoder.PixelWidth != (uint)sourceWidth ||
            decoder.PixelHeight != (uint)sourceHeight ||
            sourcePixels.Length != checked(displayWidth * displayHeight * 4))
          throw new InvalidOperationException(
            $"Fixture decode mismatch: header={sourceWidth}x{sourceHeight}, " +
            $"decoded={decoder.PixelWidth}x{decoder.PixelHeight}, " +
            $"bytes={sourcePixels.Length}.");
        // comctl32 v6 STATIC 按位图 alpha 合成 32bpp DIB；Ignore 模式返回
        // 的 0 alpha 会让整图不可见，统一置为不透明。
        for (int offset = 3; offset < sourcePixels.Length; offset += 4)
          sourcePixels[offset] = 255;
        bitmap = PaddleSmokeNative.CreateOpaqueTopDownBitmap(
          sourcePixels, displayWidth, displayHeight);
        if (bitmap == 0)
          throw new InvalidOperationException(
            $"Fixture DIB creation failed: {Marshal.GetLastPInvokeError()}.");
        if (PaddleSmokeNative.SendMessageW(windowHandle, 0x0172, 0, bitmap) != 0)
          throw new InvalidOperationException("Synthetic bitmap window rejected the DIB.");
        // 显示 + 同步重绘：顶层窗口大小即位图大小，置于工作区内偏移处。
        if (!PaddleSmokeNative.SetWindowPos(windowHandle, -1,
              work.X + 30, work.Y + 30, displayWidth, displayHeight, 0x0040u))
          throw new InvalidOperationException(
            $"Synthetic bitmap window positioning failed: {Marshal.GetLastPInvokeError()}.");
        if (!PaddleSmokeNative.UpdateWindow(windowHandle) &&
            !PaddleSmokeNative.IsWindowVisible(windowHandle))
          throw new InvalidOperationException("Synthetic bitmap window is not visible.");
        if (!PaddleSmokeNative.GetClientRect(windowHandle, out PaddleSmokeNative.Rect client))
          throw new InvalidOperationException(
            "Synthetic bitmap window bounds are unavailable.");
        PaddleSmokeNative.Point origin = new();
        if (!PaddleSmokeNative.ClientToScreen(windowHandle, ref origin))
          throw new InvalidOperationException(
            "Synthetic bitmap window origin is unavailable.");
        var bounds = new PhysicalRectangle(
          origin.X, origin.Y,
          client.Right - client.Left, client.Bottom - client.Top);
        await Task.Delay(200, cancellationToken);
        // 捕获区顶层归属必须是自身窗口：被 XAML/其它窗口覆盖即 fail closed。
        PaddleSmokeNative.Point center = new()
        {
          X = bounds.X + bounds.Width / 2,
          Y = bounds.Y + bounds.Height / 2,
        };
        if (PaddleSmokeNative.WindowFromPoint(center) != windowHandle)
          throw new InvalidOperationException(
            $"Synthetic bitmap window is not the top-level window at its center " +
            $"(dpi={dpi}, fitScale={fitScale:0.###}, bounds={bounds.Width}x{bounds.Height}).");

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
            {
              PaddleSmokeNative.Point retryCenter = new()
              {
                X = bounds.X + bounds.Width / 2,
                Y = bounds.Y + bounds.Height / 2,
              };
              nint hit = PaddleSmokeNative.WindowFromPoint(retryCenter);
              throw new InvalidOperationException(
                $"Fixture pixels did not render on screen: nonWhite={nonWhite}, " +
                $"white={white}, bounds={bounds.Width}x{bounds.Height}, dpi={dpi}, " +
                $"fitScale={fitScale:0.###}, hitOwnWindow={hit == windowHandle}.");
            }
            await Task.Delay(250, cancellationToken);
          }
        }
        return selection;
      }
      finally
      {
        if (windowHandle != 0)
          PaddleSmokeNative.DestroyWindow(windowHandle);
        if (bitmap != 0)
          PaddleSmokeNative.DeleteObject(bitmap);
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

    // ------- 合成 fixture GDI 位图路径（#110：XAML Image 呈现全白） -------

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode,
      SetLastError = true)]
    public static extern nint CreateWindowExW(
      uint extendedStyle, string className, string? windowName, uint style,
      int x, int y, int width, int height,
      nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern nint SendMessageW(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateWindow(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint handle);

    [DllImport("gdi32.dll")]
    public static extern uint SetBkColor(nint deviceContext, uint color);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextColor(nint deviceContext, uint color);

    [DllImport("gdi32.dll")]
    public static extern nint GetStockObject(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
      nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
      public uint Size;
      public int Width, Height;
      public ushort Planes, BitCount;
      public uint Compression, ImageSize;
      public int XPelsPerMeter, YPelsPerMeter;
      public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
      public BitmapInfoHeader Header;
      public uint Colors;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(
      nint deviceContext, ref BitmapInfo info, uint usage,
      out nint bits, nint fileMapping, uint fileOffset);

    /// <summary>
    /// 创建 top-down 32bpp DIB（负高：首行即顶部，与解码行序一致）并拷入
    /// BGRA 像素；任何失败返回 0，由调用方 fail closed。
    /// </summary>
    public static nint CreateOpaqueTopDownBitmap(byte[] bgra, int width, int height)
    {
      var info = new BitmapInfo
      {
        Header = new BitmapInfoHeader
        {
          Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
          Width = width,
          Height = -height,
          Planes = 1,
          BitCount = 32,
          Compression = 0,
          ImageSize = (uint)checked(width * height * 4),
        },
      };
      nint bitmap = CreateDIBSection(nint.Zero, ref info, 0,
        out nint bits, nint.Zero, 0);
      if (bitmap == 0) return 0;
      if (bits == nint.Zero)
      {
        DeleteObject(bitmap);
        return 0;
      }
      Marshal.Copy(bgra, 0, bits, bgra.Length);
      return bitmap;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(nint parent, EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetDlgCtrlID(nint window);

    [DllImport("user32.dll")]
    public static extern bool IsChild(nint parent, nint child);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    public static extern nint SetText(nint window, uint message, nint wParam,
      string text, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    public static extern nint ReadText(nint window, uint message, nint wParam,
      System.Text.StringBuilder text, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static extern nint Click(nint window, uint message, nint wParam,
      nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
  }
}
