using System.Text.Json;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 默认识别模式的实际请求实参契约：真实 DesktopWorkbenchCommandHandler /
/// SettingsViewModel / 任务 ViewModel 链路提交，fake 只替换 HTTP 边界并
/// 记录最终 SubmitRequest。覆盖已提交默认（含非 OCR pipeline 的
/// mineru_document + typed MinerU 配置）、显式本次覆盖优先且不改持久默认，
/// 以及在途任务提交点冻结（保存新默认不重写已提交参数）。
/// </summary>
public sealed class DefaultModeRequestContractTests
{
  [Fact]
  public async Task InheritedDefaultDrivesSingleRequestEngineWithoutChangingSetting()
  {
    var fake = Client("windows_text");
    await using TestHarness harness = await Harness(fake);
    await harness.Handler.ExecuteAsync(
      new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
    await fake.WaitSubmittedAsync();

    PipelineSelection pipeline = Assert.Single(fake.Submissions).Pipeline;
    Assert.Equal("OCR", pipeline.PipelineId);
    Assert.Equal(OcrEngine.Windows, pipeline.Engine);
    Assert.Null(pipeline.Mineru);
    // 继承默认只影响本次请求，不写设置。
    Assert.Equal(0, fake.UpdateCalls);
    Assert.Equal("windows_text", harness.Settings.DefaultRecognitionMode?.ModeId);
  }

  [Fact]
  public async Task ExplicitOverrideWinsForThisRequestAndKeepsCommittedDefault()
  {
    var fake = Client("windows_text");
    await using TestHarness harness = await Harness(fake);
    await harness.Handler.ExecuteAsync(
      new SetTaskEngineCommand("rapid_text"), TestContext.Current.CancellationToken);
    await harness.Handler.ExecuteAsync(
      new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
    await fake.WaitSubmittedAsync();

    PipelineSelection pipeline = Assert.Single(fake.Submissions).Pipeline;
    Assert.Equal("OCR", pipeline.PipelineId);
    Assert.Equal(OcrEngine.RapidOcr, pipeline.Engine);
    Assert.Equal(0, fake.UpdateCalls);
    Assert.Equal("windows_text", harness.Settings.DefaultRecognitionMode?.ModeId);
  }

  [Fact]
  public async Task MineruDefaultSubmitsMineruPipelineWithTypedConfig()
  {
    var fake = Client("mineru_document", mineruReady: true);
    await using TestHarness harness = await Harness(fake);
    await harness.Handler.ExecuteAsync(
      new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
    await fake.WaitSubmittedAsync();

    PipelineSelection pipeline = Assert.Single(fake.Submissions).Pipeline;
    Assert.Equal("MinerU", pipeline.PipelineId);
    Assert.Null(pipeline.Engine);
    Assert.NotNull(pipeline.Mineru);
    Assert.Equal(MineruTier.Basic, pipeline.Mineru!.Tier);
    Assert.Equal(MineruOcrMode.Auto, pipeline.Mineru.OcrMode);
    Assert.Equal("all", pipeline.Mineru.PageRange);
    Assert.Equal("ch", pipeline.Mineru.Language);
  }

  [Fact]
  public async Task BatchInheritsCommittedDefaultForItsSingleSubmission()
  {
    var fake = Client("windows_text");
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-default-batch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var batch = new BatchViewModel(fake, new BatchFileSource());
      batch.AddFiles([TempPng(root, "a"), TempPng(root, "b")]);
      await using TestHarness harness = await Harness(fake, batch: batch);
      await harness.Handler.ExecuteAsync(
        new StartBatchCommand(), TestContext.Current.CancellationToken);
      await fake.WaitSubmittedAsync();
      await fake.WaitIdleAsync();

      SubmitRequest request = Assert.Single(fake.Submissions);
      Assert.Equal(JobKind.Recognition, request.Kind);
      Assert.Equal("OCR", request.Pipeline.PipelineId);
      Assert.Equal(OcrEngine.Windows, request.Pipeline.Engine);
      Assert.Equal(2, request.Items.Count);
      Assert.Equal(0, fake.UpdateCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SavingNewDefaultDuringInputLoadKeepsFrozenSubmitParameters()
  {
    // 提交冻结点在输入 await 之前：输入阻塞期间保存新默认（真实
    // handler 命令），放行后的首次提交仍用冻结时绑定的默认参数。
    RecordingClient fake = Client("windows_text");
    var inputGate = new TaskCompletionSource(
      TaskCreationOptions.RunContinuationsAsynchronously);
    var inputs = new GatedInputService(inputGate.Task);
    await using TestHarness harness = await Harness(fake, input: inputs);
    WorkbenchCommandOutcome started = await harness.Handler.ExecuteAsync(
      new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
    Assert.Null(started.Error);
    await inputs.WaitEnteredAsync();
    Assert.Empty(fake.Submissions);

    WorkbenchCommandOutcome saved = await harness.Handler.ExecuteAsync(
      new SetDefaultRecognitionModeCommand("rapid_text"),
      TestContext.Current.CancellationToken);
    Assert.Null(saved.Error);
    Assert.Equal(
      "rapid_text", harness.Settings.DefaultRecognitionMode?.ModeId);
    Assert.Empty(fake.Submissions);

    inputGate.TrySetResult();
    await fake.WaitSubmittedAsync();
    PipelineSelection pipeline = Assert.Single(fake.Submissions).Pipeline;
    Assert.Equal("OCR", pipeline.PipelineId);
    Assert.Equal(OcrEngine.Windows, pipeline.Engine);
    Assert.Equal(
      "rapid_text", harness.Settings.DefaultRecognitionMode?.ModeId);
  }

  [Fact]
  public async Task PdfOcrInheritsCommittedDefaultForItsSubmission()
  {
    var fake = Client("windows_text");
    var pdf = new PdfViewModel(fake, new StubPdfSource());
    await using TestHarness harness = await Harness(fake, pdf: pdf);
    WorkbenchCommandOutcome opened = await harness.Handler.ExecuteAsync(
      new OpenDroppedPdfCommand("scan.pdf"), TestContext.Current.CancellationToken);
    Assert.Null(opened.Error);

    WorkbenchCommandOutcome ocr = await harness.Handler.ExecuteAsync(
      new OcrPdfPagesCommand(), TestContext.Current.CancellationToken);
    Assert.Null(ocr.Error);
    await fake.WaitSubmittedAsync();

    SubmitRequest request = Assert.Single(fake.Submissions);
    Assert.Equal("OCR", request.Pipeline.PipelineId);
    Assert.Equal(OcrEngine.Windows, request.Pipeline.Engine);
    Assert.Equal(0, fake.UpdateCalls);
  }

  [Fact]
  public async Task ColdStartRecognitionWaitsForCatalogAndSubmitsCommittedDefault()
  {
    // 冷启动竞态回归：未 attach 时不得以空快照冻结提交参数；输入先采集，
    // 权威目录与已提交默认加载后首个请求必须携带默认模式（旧实现提交
    // engine=null，Runtime 落回 RapidOCR）。
    var deferred = new DeferredInferenceClient();
    deferred.MarkStartupPending();
    RecordingClient fake = Client("windows_text");
    var recognition = new RecognitionViewModel(deferred, new FixedInputService());
    var settings = new SettingsViewModel(deferred);
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-default-cold-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    var broker = new WorkbenchResourceBroker(resourceRoot);
    var annotations = new WorkbenchAnnotationStore(resourceRoot);
    try
    {
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, resourceRoot, static () => 0, annotations,
        inferenceAttached: () => deferred.IsAttached);

      WorkbenchCommandOutcome started = await handler.ExecuteAsync(
        new SelectRecognitionImageCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(started.Error);
      // 输入采集不等 Runtime；随后 attach 携带目录与已提交默认。
      deferred.Attach(fake);

      await fake.WaitSubmittedAsync(TimeSpan.FromSeconds(10));
      PipelineSelection pipeline = Assert.Single(fake.Submissions).Pipeline;
      Assert.Equal("OCR", pipeline.PipelineId);
      Assert.Equal(OcrEngine.Windows, pipeline.Engine);
      Assert.Equal(0, fake.UpdateCalls);
    }
    finally
    {
      broker.Dispose();
      annotations.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ColdStartBatchWaitsForCatalogAndSubmitsCommittedDefault()
  {
    var deferred = new DeferredInferenceClient();
    deferred.MarkStartupPending();
    RecordingClient fake = Client("windows_text");
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-default-cold-batch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var broker = default(WorkbenchResourceBroker);
    try
    {
      var batch = new BatchViewModel(deferred, new BatchFileSource());
      batch.AddFiles([TempPng(root, "a")]);
      var settings = new SettingsViewModel(deferred);
      string resourceRoot = Path.Combine(root, "resources");
      Directory.CreateDirectory(resourceRoot);
      broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotations = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        () => batch,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, resourceRoot, static () => 0, annotations,
        inferenceAttached: () => deferred.IsAttached);

      // StartBatchCommand 在未 attach 时会同步等待权威目录（提交前取得
      // 默认），须以后台任务启动再 Attach，避免与主线程互等。
      Task<WorkbenchCommandOutcome> started = handler.ExecuteAsync(
        new StartBatchCommand(), TestContext.Current.CancellationToken).AsTask();
      deferred.Attach(fake);
      WorkbenchCommandOutcome outcome = await started;
      Assert.Null(outcome.Error);

      await fake.WaitSubmittedAsync(TimeSpan.FromSeconds(10));
      SubmitRequest request = Assert.Single(fake.Submissions);
      Assert.Equal("OCR", request.Pipeline.PipelineId);
      Assert.Equal(OcrEngine.Windows, request.Pipeline.Engine);
      Assert.Equal(0, fake.UpdateCalls);
    }
    finally
    {
      broker?.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ColdStartFreezeIgnoresOverrideChosenWhileAwaitingInput()
  {
    // 冷启动延迟冻结：输入等待期间的新 UI 选择不注入已启动的本次任务，
    // 提交仍按启动时意图（跟随已提交默认）解析；新选择保留给下一次。
    var deferred = new DeferredInferenceClient();
    deferred.MarkStartupPending();
    RecordingClient fake = Client("windows_text");
    var inputGate = new TaskCompletionSource(
      TaskCreationOptions.RunContinuationsAsynchronously);
    var inputs = new GatedInputService(inputGate.Task);
    var recognition = new RecognitionViewModel(deferred, inputs);
    var settings = new SettingsViewModel(deferred);
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-default-override-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    var broker = new WorkbenchResourceBroker(resourceRoot);
    var annotations = new WorkbenchAnnotationStore(resourceRoot);
    try
    {
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, resourceRoot, static () => 0, annotations,
        inferenceAttached: () => deferred.IsAttached);

      WorkbenchCommandOutcome started = await handler.ExecuteAsync(
        new SelectRecognitionImageCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(started.Error);
      await inputs.WaitEnteredAsync();
      deferred.Attach(fake);
      // 目录可加载后，用户在输入等待期间另选本次 override。
      await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
      WorkbenchCommandOutcome overridden = await handler.ExecuteAsync(
        new SetTaskEngineCommand("rapid_text"),
        TestContext.Current.CancellationToken);
      Assert.Null(overridden.Error);

      inputGate.TrySetResult();
      await fake.WaitSubmittedAsync(TimeSpan.FromSeconds(10));
      PipelineSelection pipeline = Assert.Single(fake.Submissions).Pipeline;
      // 已启动任务按已提交默认提交；新选择只影响下一次。
      Assert.Equal(OcrEngine.Windows, pipeline.Engine);
      Assert.Equal("rapid_text", recognition.TaskEngine);
    }
    finally
    {
      broker.Dispose();
      annotations.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ColdStartSelectionCallbackCancellationCancelsSubmit()
  {
    // 环境切换窗口：延迟回调返回 null 时本次提交取消，不以空快照提交。
    var fake = Client("windows_text");
    var recognition = new RecognitionViewModel(fake, new FixedInputService());
    static Task<RecognitionSubmitSelection?> Cancelled(CancellationToken _) =>
      Task.FromResult<RecognitionSubmitSelection?>(null);
    await recognition.RecognizeFileAsync(
      TestContext.Current.CancellationToken, Cancelled);
    Assert.Equal(JobState.Cancelled, recognition.TerminalState);
    Assert.Equal("运行环境正在切换，已取消本次识别", recognition.Status);
    Assert.Empty(fake.Submissions);
  }

  [Fact]
  public async Task EnvironmentSwitchingCancelsBatchAndPdfSubmitWithoutRapidFallback()
  {
    // 真实 handler 提交路径回归：环境切换窗口（StartEnvironmentOperation
    // 置位的同一状态）内启动批量/PDF，提交必须取消而非以空快照提交
    // （旧实现 engine=null 落回 RapidOCR）。
    var fake = Client("windows_text");
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-default-switch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var broker = default(WorkbenchResourceBroker);
    try
    {
      var batch = new BatchViewModel(fake, new BatchFileSource());
      batch.AddFiles([TempPng(root, "a")]);
      var pdf = new PdfViewModel(fake, new StubPdfSource());
      var settings = new SettingsViewModel(fake);
      string resourceRoot = Path.Combine(root, "resources");
      Directory.CreateDirectory(resourceRoot);
      broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotations = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        () => batch,
        static () => throw new InvalidOperationException(),
        () => pdf,
        () => settings,
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, resourceRoot, static () => 0, annotations);
      System.Reflection.FieldInfo switching = typeof(DesktopWorkbenchCommandHandler)
        .GetField("environmentSwitching",
          System.Reflection.BindingFlags.NonPublic |
          System.Reflection.BindingFlags.Instance)!;

      switching.SetValue(handler, 1);
      WorkbenchCommandOutcome batchOutcome = await handler.ExecuteAsync(
        new StartBatchCommand(), TestContext.Current.CancellationToken);
      var batchState = Assert.IsType<BatchWorkbenchState>(
        Assert.Single(batchOutcome.States));
      Assert.False(batchState.IsRunning);
      Assert.Null(batchOutcome.Error);

      WorkbenchCommandOutcome pdfOutcome = await handler.ExecuteAsync(
        new OcrPdfPagesCommand(), TestContext.Current.CancellationToken);
      var pdfState = Assert.IsType<PdfWorkbenchState>(
        Assert.Single(pdfOutcome.States));
      Assert.False(pdfState.IsBusy);
      Assert.Null(pdfOutcome.Error);

      // 未提交任何请求；退出切换窗口后同一链路恢复提交。
      Assert.Empty(fake.Submissions);
      switching.SetValue(handler, 0);
      WorkbenchCommandOutcome resumed = await handler.ExecuteAsync(
        new StartBatchCommand(), TestContext.Current.CancellationToken);
      Assert.Null(resumed.Error);
      await fake.WaitSubmittedAsync(TimeSpan.FromSeconds(10));
      SubmitRequest request = Assert.Single(fake.Submissions);
      Assert.Equal(OcrEngine.Windows, request.Pipeline.Engine);
    }
    finally
    {
      broker?.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SelectionInvalidatedDuringLoadRecoversNewGenerationDefault()
  {
    // F8 回归：提交点目录读取（Deferred 等待 attach 后的 health/settings
    // 读取）被代际失效（切换路径同款 ClearSelection）丢弃时，旧实现把
    // null 快照当旧 Backend 兼容提交 engine=null（Runtime 落回 Rapid）；
    // 修复后直接取消本次提交（零提交、不重试），随后新任务按新代目录
    // 与已提交默认提交 Windows。
    var deferred = new DeferredInferenceClient();
    deferred.MarkStartupPending();
    RecordingClient fake = Client("windows_text");
    var healthGate = new TaskCompletionSource(
      TaskCreationOptions.RunContinuationsAsynchronously);
    fake.HealthGate = healthGate;
    var recognition = new RecognitionViewModel(deferred, new FixedInputService());
    var settings = new SettingsViewModel(deferred);
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-default-invalidate-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    var broker = new WorkbenchResourceBroker(resourceRoot);
    var annotations = new WorkbenchAnnotationStore(resourceRoot);
    try
    {
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, resourceRoot, static () => 0, annotations,
        inferenceAttached: () => deferred.IsAttached);

      WorkbenchCommandOutcome started = await handler.ExecuteAsync(
        new SelectRecognitionImageCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(started.Error);
      // 输入先采集；提交点目录读取在 Deferred 网关等待 attach。
      deferred.Attach(fake);
      // 目录读取到达 inner health（已过 generation 记录点）后再推进代际。
      await TestContext.Current.CancellationToken.WaitAsync(
        () => fake.HealthCalls > 0, 5000);
      // 先注册终态等待，再放行 healthGate，避免终态先发被漏掉。
      using (var terminal = new RecognitionStateAwaiter(
        handler, state => !state.IsBusy))
      {
        settings.ClearSelection();
        healthGate.TrySetResult();

        // 失效读取直接取消本次提交（不重试、不猜默认）：零提交。
        RecognitionWorkbenchState cancelled = await terminal.Task;
        Assert.False(cancelled.IsBusy);
        Assert.Empty(fake.Submissions);
      }

      // 新代正常路径：再次发起即用新代目录与已提交默认提交 Windows。
      using (var resumedTerminal = new RecognitionStateAwaiter(
        handler, state => !state.IsBusy))
      {
        WorkbenchCommandOutcome resumed = await handler.ExecuteAsync(
          new SelectRecognitionImageCommand(),
          TestContext.Current.CancellationToken);
        Assert.Null(resumed.Error);
        RecognitionWorkbenchState completed = await resumedTerminal.Task;
        Assert.False(completed.IsBusy);
      }
      await fake.WaitSubmittedAsync(TimeSpan.FromSeconds(10));
      SubmitRequest request = Assert.Single(fake.Submissions);
      Assert.Equal("OCR", request.Pipeline.PipelineId);
      Assert.Equal(OcrEngine.Windows, request.Pipeline.Engine);
    }
    finally
    {
      broker.Dispose();
      annotations.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>
  /// 与 ScreenshotSessionWorkbenchTests 同款终态等待：构造即订阅，
  /// Dispose 退订；Task 固定 5s 超时，不漏早发终态。
  /// </summary>
  private sealed class RecognitionStateAwaiter : IDisposable
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly Func<RecognitionWorkbenchState, bool> predicate;
    private readonly TaskCompletionSource<RecognitionWorkbenchState> ready =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecognitionStateAwaiter(
      DesktopWorkbenchCommandHandler handler,
      Func<RecognitionWorkbenchState, bool> predicate)
    {
      this.handler = handler;
      this.predicate = predicate;
      handler.StateChanged += OnStateChanged;
    }

    public Task<RecognitionWorkbenchState> Task => ready.Task.WaitAsync(
      TimeSpan.FromSeconds(5),
      TestContext.Current.CancellationToken);

    private void OnStateChanged(WorkbenchState state)
    {
      if (state is RecognitionWorkbenchState recognition && predicate(recognition))
      {
        ready.TrySetResult(recognition);
      }
    }

    public void Dispose() => handler.StateChanged -= OnStateChanged;
  }

  private static string TempPng(string root, string name)
  {
    string path = Path.Combine(root, name + ".png");
    File.WriteAllBytes(path, [1, 2, 3, 4]);
    return path;
  }

  private static RecordingClient Client(
    string defaultMode,
    bool mineruReady = false) => new()
  {
    Health = RequestHealth(mineruReady),
    Settings = new SettingsSnapshot
    {
      Extra = new Dictionary<string, JsonElement>
      {
        ["default_recognition_mode"] = JsonSerializer.SerializeToElement(defaultMode),
      },
    },
  };

  /// <summary>
  /// Build the full handler with real view models; the recording client only
  /// replaces the HTTP boundary and the selection catalog is loaded through
  /// the real SettingsViewModel gate.
  /// </summary>
  private static async Task<TestHarness> Harness(
    RecordingClient fake,
    BatchViewModel? batch = null,
    PdfViewModel? pdf = null,
    IInputService? input = null)
  {
    var recognition = new RecognitionViewModel(
      fake, input ?? new FixedInputService());
    var settings = new SettingsViewModel(fake);
    await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
    Assert.NotNull(settings.RecognitionSelection?.Catalog);
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-default-mode-req-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    var broker = new WorkbenchResourceBroker(resourceRoot);
    var annotations = new WorkbenchAnnotationStore(resourceRoot);
    Func<PdfViewModel> pdfFactory =
      pdf is null ? static () => throw new InvalidOperationException() : () => pdf;
    DesktopWorkbenchCommandHandler handler = new(
      () => recognition,
      batch is null ? static () => throw new InvalidOperationException() : () => batch,
      static () => throw new InvalidOperationException(),
      pdfFactory,
      () => settings,
      () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
      static () => throw new InvalidOperationException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])),
      broker, resourceRoot, static () => 0, annotations);
    return new TestHarness(handler, broker, annotations, root, settings);
  }

  private static Wire.Health RequestHealth(bool mineruReady) => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-req",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities =
    [
      RuntimeSelectionService.DefaultRecognitionModeCapability,
      RuntimeSelectionService.RecognitionModesCapability,
      RuntimeSelectionService.MineruConfigCapability,
    ],
    CapabilityDescriptors =
    [
      new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.RecognitionModesCapability,
        Lifecycle = "active",
        IntroducedIn = "2.8.0",
        DeprecatedIn = null,
        SunsetAt = null,
        Replacement = null,
        RecognitionModeCatalog = new Wire.RecognitionModeCatalog
        {
          Modes =
          [
            Mode(Wire.RecognitionModeId.RapidText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.WindowsText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.PaddleText,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleStructure,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleDocumentVl,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.MineruDocument,
              mineruReady ? Wire.RecognitionModeAvailability.Ready
                : Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleTable,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleFormula,
              Wire.RecognitionModeAvailability.PreparationRequired),
          ],
        },
      },
      new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.MineruConfigCapability,
        Lifecycle = "active",
        IntroducedIn = "2.8.1",
        DeprecatedIn = null,
        SunsetAt = null,
        Replacement = null,
        MineruConfigCatalog = new Wire.MineruConfigCatalog
        {
          DefaultTier = Wire.MineruTierId.Basic,
          Tiers = [new Wire.MineruTierDescriptor
          {
            Id = Wire.MineruTierId.Basic,
            Availability = Wire.MineruTierAvailability.Ready,
            ReasonCode = null,
          }],
          Languages = ["ch"],
        },
      },
    ],
  };

  private static Wire.RecognitionModeDescriptor Mode(
    Wire.RecognitionModeId id,
    Wire.RecognitionModeAvailability availability)
  {
    var (family, pipeline, engine, provisioning, lifecycle) = id switch
    {
      Wire.RecognitionModeId.RapidText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Rapidocr,
        Wire.RecognitionModeProvisioning.BaseRuntime,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.WindowsText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Windows,
        Wire.RecognitionModeProvisioning.OperatingSystem,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.PaddleText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Paddleocr,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleStructure => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PPStructureV3, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleDocumentVl => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PaddleOCRVL, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.MineruDocument => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.MinerU, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.PaddleTable => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.TABLERECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleFormula => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.FORMULARECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    bool notReady = availability != Wire.RecognitionModeAvailability.Ready;
    bool mineru = id == Wire.RecognitionModeId.MineruDocument;
    return new Wire.RecognitionModeDescriptor
    {
      Id = id,
      Family = family,
      PipelineId = pipeline,
      Engine = engine,
      Provisioning = provisioning,
      Availability = availability,
      ReasonCode = notReady ? "runtime_component_missing" : null,
      RequiredComponent = notReady ? (mineru ? "mineru-cpu" : "paddleocr-cpu") : null,
      SupportedOptions = [],
      Lifecycle = new Wire.RecognitionModeLifecycle
      {
        Kind = lifecycle,
        SupportsPreload = mineru,
        SupportsTtl = mineru,
        SupportsPinning = false,
        SupportsRelease = mineru,
      },
    };
  }

  private sealed class GatedInputService(Task gate) : IInputService
  {
    private readonly TaskCompletionSource _entered =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RecognitionInput?> PickFileAsync(
      CancellationToken cancellationToken)
    {
      _entered.TrySetResult();
      await gate.WaitAsync(cancellationToken);
      return new RecognitionInput([1, 2, 3, 4], "image/png", "file.png", "file");
    }

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => PickFileAsync(cancellationToken);

    public async Task WaitEnteredAsync() =>
      await TestContext.Current.CancellationToken.WaitAsync(
        () => _entered.Task.IsCompleted, 1500);
  }

  private sealed class FixedInputService : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(
        new RecognitionInput([1, 2, 3, 4], "image/png", "file.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => PickFileAsync(cancellationToken);
  }

  private sealed class BatchFileSource : IBatchFileSource
  {
    public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<(byte[] Data, string MediaType)> ReadAsync(
      string path, CancellationToken cancellationToken) =>
      Task.FromResult((File.ReadAllBytes(path), "image/png"));
  }

  private sealed class StubPdfSource : IPdfFileSource
  {
    public Task<string?> PickFileAsync(CancellationToken ct) =>
      Task.FromResult<string?>("test.pdf");
  }

  private sealed class StubHotkeyRegistrar : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict)
    {
      conflict = null;
      return true;
    }

    public void Unregister() { }
  }

  private sealed class StubStartupRegistrar : IStartupRegistrar
  {
    public bool SetEnabled(bool enabled) => true;
  }

  /// <summary>Records every SubmitRequest; optional gates freeze in-flight jobs.</summary>
  private sealed class RecordingClient : InferenceClientStub
  {
    private readonly List<SubmitRequest> _submissions = [];
    private IReadOnlyList<JobItem> _items = Array.Empty<JobItem>();
    private readonly TaskCompletionSource _idle =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Wire.Health Health { get; init; } = new()
    {
      SchemaVersion = 2,
      InstanceId = "sup-req",
      ProtocolVersion = 2,
      Ready = true,
      Draining = false,
      Capabilities = [],
    };

    public SettingsSnapshot Settings { get; set; } = new();

    public IReadOnlyList<SubmitRequest> Submissions => _submissions;

    public TaskCompletionSource? ObserveGate { get; set; }

    public TaskCompletionSource? HealthGate { get; set; }

    public int HealthCalls { get; private set; }

    public int UpdateCalls { get; private set; }

    public override async Task<Wire.Health> GetHealthAsync(
      CancellationToken cancellationToken)
    {
      HealthCalls++;
      if (HealthGate is { } gate)
      {
        HealthGate = null;
        await gate.Task.WaitAsync(cancellationToken);
      }
      return Health;
    }

    public override Task<ResidencyStatus> GetResidencyAsync(
      CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());

    public override Task<SettingsSnapshot> GetSettingsAsync(
      CancellationToken cancellationToken) => Task.FromResult(Settings);

    public override Task<SettingsSnapshot> UpdateSettingsAsync(
      SettingsSnapshot settings,
      CancellationToken cancellationToken)
    {
      UpdateCalls++;
      Settings = settings;
      return Task.FromResult(settings);
    }

    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(
      string path,
      string? password,
      CancellationToken cancellationToken) =>
      Task.FromResult(new PdfSessionOpenResult("pdf-1", 2, path));

    public override Task<byte[]> RenderPdfPageAsync(
      string sessionId,
      int page,
      int size,
      CancellationToken cancellationToken) =>
      Task.FromResult(new byte[] { 1, 2, 3 });

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      _submissions.Add(request);
      _items = request.Items.Select((item, index) => new JobItem
      {
        ItemId = $"it-{index}",
        ClientItemKey = item.ClientItemKey,
        Ordinal = item.Ordinal,
        DisplayName = item.DisplayName,
        State = ItemState.Queued,
      }).ToArray();
      return Task.FromResult(new JobRef { JobId = "job-req-1", Items = _items });
    }

    public override async Task<JobUpdate> ObserveAsync(
      string jobId,
      int afterSequence,
      CancellationToken cancellationToken)
    {
      if (ObserveGate is { } gate)
      {
        await gate.Task.WaitAsync(cancellationToken);
      }
      _idle.TrySetResult();
      return new JobUpdate
      {
        Snapshot = new JobSnapshot
        {
          JobId = jobId,
          Kind = JobKind.Recognition,
          Priority = JobPriority.Interactive,
          State = JobState.Completed,
          Items = _items,
        },
        Events = [],
        Outcomes = _items.Select(item => new ItemOutcome
        {
          ItemId = item.ItemId,
          Attempt = 1,
          State = ItemState.Succeeded,
          PayloadType = "ocr.v1",
          Payload = new Dictionary<string, JsonElement>
          {
            ["raw_text"] = JsonSerializer.SerializeToElement("识别结果"),
          },
        }).ToArray(),
        ThroughSequence = afterSequence + 1,
      };
    }

    public async Task WaitSubmittedAsync(TimeSpan? timeout = null) =>
      await TestContext.Current.CancellationToken.WaitAsync(
          () => _submissions.Count > 0, (int)(timeout ?? TimeSpan.FromMilliseconds(1500)).TotalMilliseconds);

    public async Task WaitIdleAsync() =>
      await TestContext.Current.CancellationToken.WaitAsync(
          () => _idle.Task.IsCompleted, 3000);
  }

  private sealed class TestHarness(
    DesktopWorkbenchCommandHandler handler,
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotations,
    string root,
    SettingsViewModel settings) : IAsyncDisposable
  {
    public DesktopWorkbenchCommandHandler Handler => handler;
    public SettingsViewModel Settings => settings;

    public async ValueTask DisposeAsync()
    {
      await handler.DisposeAsync();
      broker.Dispose();
      annotations.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }
}

file static class WaitExtensions
{
  public static async Task WaitAsync(
    this CancellationToken cancellationToken,
    Func<bool> condition,
    int timeoutMs)
  {
    var deadline = Environment.TickCount64 + timeoutMs;
    while (!condition())
    {
      if (Environment.TickCount64 > deadline)
      {
        throw new TimeoutException(
          $"Condition not reached within {timeoutMs}ms.");
      }
      if (cancellationToken.IsCancellationRequested)
      {
        cancellationToken.ThrowIfCancellationRequested();
      }
      await Task.Delay(10, cancellationToken);
    }
  }
}
