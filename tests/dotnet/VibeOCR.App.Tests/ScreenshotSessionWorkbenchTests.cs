using System.Text.Json;
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
using Xunit;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;

namespace VibeOCR.App.Tests;

/// <summary>
/// #106 纯截图会话契约：捕获零 OCR、内容修订失效、旧 revision/旧会话拒绝、
/// 显式识别只消费编辑器导出的最终 PNG、迟到响应不覆盖新会话。
/// </summary>
public sealed class ScreenshotSessionWorkbenchTests
{
  private static readonly byte[] AnnotationPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
  private static readonly byte[] CaptureBytes = [9, 8, 7, 6, 5];

  private sealed class FixedCaptureInput : IInputService
  {
    public int CaptureCalls;

    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput(CaptureBytes, "image/png", "capture.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref CaptureCalls);
      return Task.FromResult<RecognitionInput?>(
        new RecognitionInput(CaptureBytes, "image/bmp", "screenshot.bmp", "screenshot"));
    }

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
  }

  /// <summary>Records submitted uploads; every submit completes with raw text.</summary>
  private class RecordingRecognitionClient : InferenceClientStub
  {
    public List<IReadOnlyList<byte>> UploadedContent { get; } = [];

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      UploadedContent.AddRange(uploads.Values.Select(upload => upload.Content));
      return Task.FromResult(new JobRef
      {
        JobId = "job-session",
        Items =
        [
          new JobItem
          {
            ItemId = "it-0",
            ClientItemKey = request.Items[0].ClientItemKey,
            Ordinal = 0,
            DisplayName = request.Items[0].DisplayName,
            State = ItemState.Queued,
          },
        ],
      });
    }

    public override Task<JobUpdate> ObserveAsync(
      string jobId,
      int afterSequence,
      CancellationToken cancellationToken) => Task.FromResult(new JobUpdate
      {
        Snapshot = new JobSnapshot
        {
          JobId = jobId,
          Kind = JobKind.Recognition,
          Priority = JobPriority.Interactive,
          State = JobState.Completed,
        },
        Events = Array.Empty<StageEvent>(),
        Outcomes =
        [
          new ItemOutcome
          {
            ItemId = "it-0",
            State = ItemState.Succeeded,
            Attempt = 1,
            PayloadType = "ocr.v1",
            Payload = new Dictionary<string, JsonElement>
            {
              ["raw_text"] = JsonSerializer.SerializeToElement("session text"),
            },
          },
        ],
        ThroughSequence = afterSequence,
      });
  }

  private sealed class SubmitBlockingRecognitionClient : RecordingRecognitionClient
  {
    private readonly TaskCompletionSource completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => completion.Task;

    public void Complete() => completion.TrySetResult();

    public override async Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      _ = await base.SubmitAsync(request, uploads, cancellationToken);
      await completion.Task.WaitAsync(cancellationToken);
      return new JobRef
      {
        JobId = "job-session",
        Items =
        [
          new JobItem
          {
            ItemId = "it-0",
            ClientItemKey = request.Items[0].ClientItemKey,
            Ordinal = 0,
            DisplayName = request.Items[0].DisplayName,
            State = ItemState.Queued,
          },
        ],
      };
    }
  }

  private static WorkbenchAnnotationLease UploadAnnotation(
    WorkbenchAnnotationStore store,
    byte[] png) =>
    store.UploadPngAsync(
        new MemoryStream(png),
        TestContext.Current.CancellationToken)
      .GetAwaiter().GetResult();

  private static DesktopWorkbenchCommandHandler CreateHandler(
    RecognitionViewModel recognition,
    string resourceRoot,
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotationStore,
    SettingsViewModel? settings = null,
    IAnnotatedImagePlatform? platform = null,
    Func<bool>? inferenceAttached = null) => new(
      () => recognition,
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      settings is null
        ? static () => throw new InvalidOperationException()
        : () => settings,
      () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
      static () => throw new InvalidOperationException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])),
      broker,
      resourceRoot,
      static () => 0,
      annotationStore,
      platform,
      inferenceAttached);

  private static string TemporaryRoot()
  {
    string root = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-session-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return root;
  }

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

  [Fact]
  public async Task CaptureScreenshotSessionPublishesSessionWithoutAnyOcrSubmit()
  {
    string root = TemporaryRoot();
    try
    {
      // 任何推理提交都会让捕获失败：纯截图必须零 OCR/零安装请求。
      var inference = new ThrowingSubmitClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        WorkbenchCommandOutcome started = await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        Assert.Null(started.Error);

        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Assert.Equal("recognition.session", captured.StatusCode);
        Assert.Equal(0, captured.ScreenshotSession!.Revision);
        Assert.NotNull(captured.Input);
        Assert.Null(captured.Result);
        Assert.Equal(1, inputs.CaptureCalls);
        Assert.False(recognition.IsBusy);
        Assert.Equal(0, inference.SubmitCalls);
        WorkbenchCommandOutcome modeChanged = await handler.ExecuteAsync(
          new SetTaskEngineCommand(null), TestContext.Current.CancellationToken);
        Assert.Null(modeChanged.Error);
        RecognitionWorkbenchState changed = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(modeChanged.States));
        Assert.Equal(captured.Input, changed.Input);
        Assert.Equal(captured.ScreenshotSession, changed.ScreenshotSession);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private sealed class ThrowingSubmitClient : InferenceClientStub
  {
    public int SubmitCalls;

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref SubmitCalls);
      throw new InvalidOperationException("pure capture must not submit OCR");
    }
  }

  [Fact]
  public async Task RevisionNotifyInvalidatesResultAndRejectsStaleSessionCommands()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new RecordingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var platform = new RecordingAnnotatedImagePlatform();
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        platform);

      Guid sessionId;
      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);

        using (var completedAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.Result is not null))
        {
          WorkbenchCommandOutcome recognized = await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          Assert.Null(recognized.Error);
          RecognitionWorkbenchState completed = await completedAwaiter.Task;
          Assert.Equal("recognition.completed", completed.StatusCode);
          Assert.NotNull(completed.Result);
        }
      }

      // 内容修订使旧结果失效（包括宿主持有的 resultActions）。
      WorkbenchCommandOutcome notify = await handler.ExecuteAsync(
        new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState notified = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(notify.States));
      Assert.Equal(1, notified.ScreenshotSession?.Revision);
      Assert.Null(notified.Result);
      Assert.False(recognition.HasResult);
      // 结果动作随修订一并失效：复制旧结果必须失败。
      WorkbenchCommandOutcome copyStaleResult = await handler.ExecuteAsync(
        new CopyRecognitionResultCommand("plain"),
        TestContext.Current.CancellationToken);
      Assert.Equal("desktop_command_failed", copyStaleResult.Error?.Code);

      // 旧 revision / 未知会话的识别与复制一律拒绝。
      WorkbenchAnnotationLease staleLease = UploadAnnotation(annotationStore, AnnotationPng);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new RecognizeScreenshotImageCommand(staleLease.ResourceUri.AbsoluteUri, sessionId, 0),
        TestContext.Current.CancellationToken)).Error?.Code);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new RecognizeScreenshotImageCommand(
          staleLease.ResourceUri.AbsoluteUri, Guid.NewGuid(), 1),
        TestContext.Current.CancellationToken)).Error?.Code);
      WorkbenchAnnotationLease copyLease = UploadAnnotation(annotationStore, AnnotationPng);
      Assert.Equal("screenshot_session_stale", (await handler.ExecuteAsync(
        new CopyScreenshotImageCommand(copyLease.ResourceUri.AbsoluteUri, sessionId, 0),
        TestContext.Current.CancellationToken)).Error?.Code);

      // 当前 revision 的复制仍走原生 clipboard 并消费一次性租约。
      WorkbenchCommandOutcome copied = await handler.ExecuteAsync(
        new CopyScreenshotImageCommand(copyLease.ResourceUri.AbsoluteUri, sessionId, 1),
        TestContext.Current.CancellationToken);
      Assert.Null(copied.Error);
      Assert.Equal(AnnotationPng, platform.CopiedBytes);
      Assert.Throws<WorkbenchAnnotationAccessException>(() =>
        annotationStore.Take(copyLease.ResourceUri));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private sealed class RecordingAnnotatedImagePlatform : IAnnotatedImagePlatform
  {
    public byte[]? CopiedBytes { get; private set; }

    public async Task CopyPngAsync(string sourcePath, CancellationToken cancellationToken)
    {
      CopiedBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
    }

    public Task<bool> SavePngAsync(string sourcePath, CancellationToken cancellationToken) =>
      Task.FromResult(true);
  }

  [Fact]
  public async Task RecognizeSubmitsOnlyTheAnnotatedFinalPng()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new RecordingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        long inputBytes = captured.Input!.ByteLength;
        await handler.ExecuteAsync(
          new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
          TestContext.Current.CancellationToken);

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        using (var completedAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.Result is not null))
        {
          await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 1),
            TestContext.Current.CancellationToken);
          RecognitionWorkbenchState completed = await completedAwaiter.Task;

          // OCR 输入只能是编辑器导出的最终 PNG，不得回退未编辑基准图。
          Assert.Equal(AnnotationPng, Assert.Single(inference.UploadedContent));
          // 会话编辑基准（Input）保持稳定，不会被识别输入替换。
          Assert.Equal(inputBytes, completed.Input?.ByteLength);
          Assert.NotNull(completed.ScreenshotSession);
          Assert.Equal(1, completed.ScreenshotSession.Revision);
        }
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task LateRecognitionAfterRevisionChangeIsDropped()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new SubmitBlockingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);

        using (var busyAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.IsBusy))
        {
          await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          await busyAwaiter.Task;
        }

        // 识别在途时内容修订：旧响应不得覆盖新内容。
        WorkbenchCommandOutcome notify = await handler.ExecuteAsync(
          new NotifyScreenshotSessionRevisionCommand(sessionId, 1),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState notified = Assert.IsType<RecognitionWorkbenchState>(
          Assert.Single(notify.States));
        Assert.Equal(1, notified.ScreenshotSession?.Revision);
        Assert.False(recognition.IsBusy);

        var published = new List<RecognitionWorkbenchState>();
        void OnStateChanged(WorkbenchState state)
        {
          if (state is RecognitionWorkbenchState recognitionState)
            published.Add(recognitionState);
        }
        handler.StateChanged += OnStateChanged;
        try
        {
          inference.Complete();
          await handler.DisposeAsync();
        }
        finally
        {
          handler.StateChanged -= OnStateChanged;
        }
        Assert.DoesNotContain(published, state => state.Result is not null);
        Assert.DoesNotContain(published, state => state.ScreenshotSession is null);
        Assert.False(recognition.HasResult);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task FileRecognitionSupersedesScreenshotSession()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new RecordingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings: new SettingsViewModel(inference),
        inferenceAttached: () => false);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);

        // 新输入（文件识别）取代会话：旧会话命令立即失效。
        using (var fileAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.Result is not null && state.ScreenshotSession is null))
        {
          await handler.ExecuteAsync(
            new SelectRecognitionImageCommand(),
            TestContext.Current.CancellationToken);
          RecognitionWorkbenchState fileCompleted = await fileAwaiter.Task;
          Assert.Null(fileCompleted.ScreenshotSession);
        }

        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);
        WorkbenchCommandOutcome stale = await handler.ExecuteAsync(
          new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
          TestContext.Current.CancellationToken);
        Assert.Equal("screenshot_session_stale", stale.Error?.Code);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task MaintenanceEventInvalidatesInFlightSessionRecognition()
  {
    string root = TemporaryRoot();
    try
    {
      var inference = new SubmitBlockingRecognitionClient();
      var inputs = new FixedCaptureInput();
      var recognition = new RecognitionViewModel(inference, inputs);
      var installer = new HangingInstallerClient();
      var settings = new SettingsViewModel(
        new MaintenanceHealthClient(),
        installerFactory: () => installer);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotationStore = new WorkbenchAnnotationStore(root);
      await using var handler = CreateHandler(
        recognition,
        root,
        broker,
        annotationStore,
        settings,
        inferenceAttached: () => true);

      using (var capturedAwaiter = new RecognitionStateAwaiter(
        handler,
        state => !state.IsBusy && state.ScreenshotSession is not null))
      {
        await handler.ExecuteAsync(
          new CaptureScreenshotSessionCommand(),
          TestContext.Current.CancellationToken);
        RecognitionWorkbenchState captured = await capturedAwaiter.Task;
        Guid sessionId = Guid.Parse(captured.ScreenshotSession!.SessionId);
        WorkbenchAnnotationLease lease = UploadAnnotation(annotationStore, AnnotationPng);

        using (var busyAwaiter = new RecognitionStateAwaiter(
          handler,
          state => state.IsBusy))
        {
          await handler.ExecuteAsync(
            new RecognizeScreenshotImageCommand(lease.ResourceUri.AbsoluteUri, sessionId, 0),
            TestContext.Current.CancellationToken);
          await busyAwaiter.Task;
        }

        // 运行环境维护事件使在途会话识别失效，不覆盖当前会话。
        await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
        using (var expiredAwaiter = new RecognitionStateAwaiter(
          handler,
          state => !state.IsBusy && state.StatusCode == "recognition.expired"))
        {
          Task install = RunMaintenanceAsync(settings);
          RecognitionWorkbenchState expired = await expiredAwaiter.Task;
          Assert.NotNull(expired.ScreenshotSession);
          Assert.Equal(0, expired.ScreenshotSession.Revision);
          Assert.Null(expired.Result);
          Assert.False(recognition.IsBusy);

          installer.Release();
          await install;
        }
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private static async Task RunMaintenanceAsync(SettingsViewModel settings)
  {
    await settings.PreviewInstallAsync(TestContext.Current.CancellationToken);
    await settings.ConfirmInstallAsync(
      settings.Maintenance.Plan!.PlanId,
      TestContext.Current.CancellationToken);
  }

  private sealed class HangingInstallerClient : IRuntimeInstallerClient
  {
    private readonly TaskCompletionSource completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public bool SupportsInstallPlan => true;

    public void Release() => completion.TrySetResult();

    public Task<Host.RuntimeInstallPlan> PreviewInstallAsync(
      RuntimeInstallSelection selection,
      string accelerator,
      CancellationToken cancellationToken = default) => Task.FromResult(
      new Host.RuntimeInstallPlan
      {
        PlanId = "session-plan",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
        Accelerator = accelerator == "cpu"
          ? Host.Accelerator.Cpu
          : Host.Accelerator.NvidiaCuda,
        ProfileId = "win-x64-cpu",
        RequestedComponentIds = selection.InstallComponentIds,
        EffectiveComponentIds = selection.InstallComponentIds ?? [],
        RequestedDownloadSourceIds = selection.DownloadSourceIds,
        EffectiveDownloadSourceIds = selection.DownloadSourceIds ?? [],
        Source = new Host.RuntimeSourceIdentity
        {
          BackendVersion = "0.14.0",
          BackendSourceSha = "test",
          RuntimeManifestSha256 = "test",
        },
        Components = [],
        Blockers = [],
        Cost = new Host.RuntimeInstallPlanCost
        {
          DownloadBytes = null,
          AdditionalDiskBytes = null,
          UnknownReasonCodes = [],
        },
      });

    public Task<RuntimeLaunch> ConfirmInstallAsync(
      string planId,
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
      CancellationToken cancellationToken = default) => HangAsync(operationId, progress, cancellationToken);

    public Task<RuntimeLaunch> EnsureAsync(
      RuntimeInstallSelection? selection,
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
      CancellationToken cancellationToken = default) => HangAsync(operationId, progress, cancellationToken);

    private async Task<RuntimeLaunch> HangAsync(
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress,
      CancellationToken cancellationToken)
    {
      // 维护运行状态由协调器 SetState 驱动；这里只需挂起保持 IsRunning。
      await completion.Task.WaitAsync(cancellationToken);
      return new RuntimeLaunch(
        @"C:\store\python.exe",
        "vibeocr.runtime.host.main",
        @"C:\store",
        @"C:\store\models",
        new Dictionary<string, string>());
    }

    public Task<RuntimeInspection> InspectAsync(CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeLaunch> EnsureAsync(CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeLaunch> RepairAsync(CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeLaunch> EnsureAsync(
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeLaunch> RepairAsync(
      string operationId,
      IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeHostEnvelope> CancelAsync(
      string operationId,
      string commandId,
      long? expectedSequence = null,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeHostEnvelope> RetryAsync(
      string operationId,
      string newOperationId,
      string commandId,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeHostEnvelope> RetryAsync(
      string operationId,
      string newOperationId,
      RuntimeInstallSelection? selection,
      string commandId,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task<RuntimeMaintenanceObserveEnvelope> ObserveAsync(
      string operationId,
      long afterSequence,
      int limit = 128,
      CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();
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

  /// <summary>Health/settings for the maintenance path; jobs never run here.</summary>
  private sealed class MaintenanceHealthClient : InferenceClientStub
  {
    public override Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new ResidencyStatus());

    public Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new RuntimeStatusSnapshot
      {
        InstanceId = "sup-session",
        ServiceState = RuntimeServiceState.Ready,
        BackendVersion = "0.14.0",
        Profile = new RuntimeProfileStatus
        {
          ProfileId = "win-x64-cpu",
          Accelerator = RuntimeAccelerator.Cpu,
          Components = [],
        },
      });

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new Wire.Health
      {
        SchemaVersion = 2,
        InstanceId = "sup-session",
        ProtocolVersion = 2,
        Ready = true,
        Draining = false,
        Capabilities =
        [
          RuntimeSelectionService.DownloadSourceCapability,
          RuntimeSelectionService.ComponentSelectionCapability,
        ],
        CapabilityDescriptors =
        [
          new Wire.CapabilityDescriptor
          {
            Name = RuntimeSelectionService.DownloadSourceCapability,
            Lifecycle = "active",
            IntroducedIn = "2.7.0",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            DownloadSourceCatalog = new Wire.DownloadSourceCatalog
            {
              Sources =
              [
                new Wire.DownloadSourceDescriptor
                {
                  Kind = "package_index",
                  Id = "tuna-pypi",
                  Endpoint = "https://mirrors.tuna.example/pypi/simple",
                },
              ],
            },
          },
          new Wire.CapabilityDescriptor
          {
            Name = RuntimeSelectionService.ComponentSelectionCapability,
            Lifecycle = "active",
            IntroducedIn = "2.7.0",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            ComponentVariantCatalog = new Wire.ComponentVariantCatalog
            {
              Variants =
              [
                new Wire.ComponentVariantDescriptor
                {
                  FeatureId = "document_parsing",
                  Accelerator = "cpu",
                  ComponentId = "document_parsing",
                },
              ],
            },
          },
        ],
      });

    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new SettingsSnapshot());
  }
}
