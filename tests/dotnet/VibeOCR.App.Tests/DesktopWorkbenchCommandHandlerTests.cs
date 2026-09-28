using System.Text.Json;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Update;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class DesktopWorkbenchCommandHandlerTests
{
  private static readonly byte[] AnnotationPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

  [Fact]
  public async Task AnnotatedImageCopyConsumesOpaqueUploadOnlyAfterNativeSuccess()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      var platform = new RecordingAnnotatedImagePlatform();
      await using DesktopWorkbenchCommandHandler handler = CreateAnnotationHandler(
        broker,
        annotationStore,
        platform,
        resourceRoot);
      await using MemoryStream upload = new(AnnotationPng);
      WorkbenchAnnotationLease lease = await annotationStore.UploadPngAsync(
        upload,
        TestContext.Current.CancellationToken);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new CopyAnnotatedImageCommand(lease.ResourceUri.AbsoluteUri),
        TestContext.Current.CancellationToken);

      Assert.Null(outcome.Error);
      Assert.Equal(AnnotationPng, platform.CopiedBytes);
      Assert.Throws<WorkbenchAnnotationAccessException>(() =>
        annotationStore.Take(lease.ResourceUri));
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task AnnotatedImageSaveCancellationIsVisibleAndUploadIsCleaned()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      var platform = new RecordingAnnotatedImagePlatform { SaveResult = false };
      await using DesktopWorkbenchCommandHandler handler = CreateAnnotationHandler(
        broker,
        annotationStore,
        platform,
        resourceRoot);
      await using MemoryStream upload = new(AnnotationPng);
      WorkbenchAnnotationLease lease = await annotationStore.UploadPngAsync(
        upload,
        TestContext.Current.CancellationToken);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SaveAnnotatedImageCommand(lease.ResourceUri.AbsoluteUri),
        TestContext.Current.CancellationToken);

      Assert.Equal("annotation_operation_cancelled", outcome.Error?.Code);
      Assert.Equal(AnnotationPng, platform.SavedBytes);
      Assert.Throws<WorkbenchAnnotationAccessException>(() =>
        annotationStore.Take(lease.ResourceUri));
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task AnnotatedImageNativeFailureIsVisibleAndUploadIsCleaned()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      var platform = new RecordingAnnotatedImagePlatform
      {
        CopyError = new IOException("clipboard unavailable"),
      };
      await using DesktopWorkbenchCommandHandler handler = CreateAnnotationHandler(
        broker,
        annotationStore,
        platform,
        resourceRoot);
      await using MemoryStream upload = new(AnnotationPng);
      WorkbenchAnnotationLease lease = await annotationStore.UploadPngAsync(
        upload,
        TestContext.Current.CancellationToken);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new CopyAnnotatedImageCommand(lease.ResourceUri.AbsoluteUri),
        TestContext.Current.CancellationToken);

      Assert.Equal("desktop_command_failed", outcome.Error?.Code);
      Assert.Throws<WorkbenchAnnotationAccessException>(() =>
        annotationStore.Take(lease.ResourceUri));
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task QrCodeWorkStartsBusyAndCancellationSuppressesLateSuccess()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new BlockingQrCodeClient();
      var input = new EmptyQrCodeInput();
      var viewModel = new QrCodeViewModel(client, input);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => viewModel,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore);
      var published = new List<WorkbenchState>();
      handler.StateChanged += published.Add;

      try
      {
        WorkbenchCommandOutcome started = await handler.ExecuteAsync(
          new GenerateQrCodeCommand("hello"),
          TestContext.Current.CancellationToken);
        Assert.Empty(started.States);
        QrCodeWorkbenchState busy = Assert.IsType<QrCodeWorkbenchState>(
          Assert.Single(published));
        Assert.True(busy.IsBusy);
        Assert.Equal("qrcode.running", busy.StatusCode);

        WorkbenchCommandOutcome cancelled = await handler.ExecuteAsync(
          new CancelQrCodeCommand(),
          TestContext.Current.CancellationToken);
        QrCodeWorkbenchState idle = Assert.IsType<QrCodeWorkbenchState>(
          Assert.Single(cancelled.States));
        Assert.False(idle.IsBusy);
        Assert.Equal("qrcode.cancelled", idle.StatusCode);
      }
      finally
      {
        client.CompleteSuccessfully();
      }
      await client.Completion;
      await handler.DisposeAsync();
      Assert.Single(published);
      Assert.Null(viewModel.GeneratedImageBase64);
      Assert.False(viewModel.IsBusy);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task ClearQrCodeDuringGenerationImmediatelyResetsBusyAndBlocksLateResult()
  {
    string resourceRoot = Path.Combine(Path.GetTempPath(), $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new FirstThenBlockingQrCodeClient();
      var viewModel = new QrCodeViewModel(client, new EmptyQrCodeInput());
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => viewModel,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, resourceRoot, static () => 0, annotationStore);
      var previewReady = new TaskCompletionSource<QrCodeWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      var published = new List<WorkbenchState>();
      handler.StateChanged += state =>
      {
        published.Add(state);
        if (state is QrCodeWorkbenchState { IsBusy: false, GeneratedResource: not null } preview)
          previewReady.TrySetResult(preview);
      };

      await handler.ExecuteAsync(new GenerateQrCodeCommand("preview"), TestContext.Current.CancellationToken);
      Assert.NotNull((await previewReady.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).GeneratedResource);
      await handler.ExecuteAsync(new GenerateQrCodeCommand("pending"), TestContext.Current.CancellationToken);
      Assert.True(viewModel.IsBusy);

      WorkbenchCommandOutcome cleared = await handler.ExecuteAsync(
        new ClearQrCodeCommand(), TestContext.Current.CancellationToken);
      QrCodeWorkbenchState state = Assert.IsType<QrCodeWorkbenchState>(Assert.Single(cleared.States));
      Assert.False(state.IsBusy);
      Assert.Null(state.GeneratedResource);
      Assert.False(viewModel.IsBusy);
      int publishedBeforeLateResult = published.Count;
      client.CompletePending();
      await handler.DisposeAsync();
      Assert.Equal(publishedBeforeLateResult, published.Count);
      Assert.Null(viewModel.GeneratedImageBase64);
    }
    finally { Directory.Delete(resourceRoot, recursive: true); }
  }

  [Fact]
  public async Task QrCodeViewModelSwitchesBetweenDecodeAndGenerateWithoutStaleState()
  {
    var blockingDecode = new BlockingDecodeQrCodeClient();
    var first = new QrCodeViewModel(blockingDecode, new FixedQrCodeInput());
    Task decode = first.DecodeAsync(QrCodeInputKind.File, TestContext.Current.CancellationToken);
    Assert.True(first.IsBusy);
    first.GenerateText = "new";
    await first.GenerateAsync(TestContext.Current.CancellationToken);
    Assert.False(first.IsBusy);
    Assert.NotNull(first.GeneratedImageBase64);
    blockingDecode.CompleteSuccessfully();
    await decode;
    Assert.Empty(first.Codes);
    Assert.False(first.IsBusy);

    var blockingGenerate = new BlockingQrCodeClient();
    var second = new QrCodeViewModel(blockingGenerate, new FixedQrCodeInput()) { GenerateText = "old" };
    Task generate = second.GenerateAsync(TestContext.Current.CancellationToken);
    await second.DecodeAsync(QrCodeInputKind.File, TestContext.Current.CancellationToken);
    Assert.False(second.IsBusy);
    blockingGenerate.CompleteSuccessfully();
    await generate;
    Assert.Null(second.GeneratedImageBase64);
    Assert.False(second.IsBusy);
  }

  [Fact]
  public async Task QrCodeGenerateWithoutAttachedSupervisorPublishesImage()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new DeferredQrCodeClient();
      var maintenance = new ProductMaintenanceCoordinator();
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new QrCodeViewModel(client, new FixedQrCodeInput()),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new UpdateViewModel(new CurrentUpdateCoordinator(), productMaintenance: maintenance),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore);
      await handler.ExecuteAsync(new CheckUpdateCommand(), TestContext.Current.CancellationToken);
      using IDisposable runtimeLease = maintenance.Acquire(ProductMaintenanceOwner.RuntimeMaintenance, () => { });
      Assert.Equal(ProductMaintenanceOwner.RuntimeMaintenance, maintenance.State.ActiveOwner);
      await using var application = new WorkbenchApplication(
        ["qrcode.generate", "qrcode.clipboard"], WorkbenchRoute.QrCode, handler);
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      await using IAsyncEnumerator<WorkbenchStateEnvelope> updates = application
        .SubscribeAsync(0, timeout.Token)
        .GetAsyncEnumerator(timeout.Token);

      WorkbenchCommandReceipt receipt = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new GenerateQrCodeCommand("hello")),
        timeout.Token);

      Assert.True(receipt.Ok);
      Assert.True(await updates.MoveNextAsync());
      QrCodeWorkbenchState busy = Assert.IsType<QrCodeWorkbenchState>(updates.Current.State);
      Assert.True(busy.IsBusy);
      Assert.Equal("qrcode.running", busy.StatusCode);
      Assert.True(await updates.MoveNextAsync());
      QrCodeWorkbenchState generated = Assert.IsType<QrCodeWorkbenchState>(updates.Current.State);
      Assert.False(generated.IsBusy);
      Assert.Equal("qrcode.ready", generated.StatusCode);
      Assert.NotNull(generated.GeneratedResource);
      Assert.True(updates.Current.Revision >= receipt.Revision);

      WorkbenchCommandReceipt decodeReceipt = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new DecodeQrCodeClipboardCommand()),
        timeout.Token);
      Assert.True(decodeReceipt.Ok);
      Assert.True(await updates.MoveNextAsync());
      Assert.True(await updates.MoveNextAsync());
      QrCodeWorkbenchState unavailable = Assert.IsType<QrCodeWorkbenchState>(updates.Current.State);
      Assert.False(unavailable.IsBusy);
      Assert.Equal("qrcode.decodeUnavailable", unavailable.StatusCode);
      Assert.NotNull(unavailable.GeneratedResource);

      WorkbenchCommandReceipt invalidReceipt = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new GenerateQrCodeCommand("")),
        timeout.Token);
      Assert.True(invalidReceipt.Ok);
      Assert.True(await updates.MoveNextAsync());
      Assert.True(await updates.MoveNextAsync());
      QrCodeWorkbenchState invalid = Assert.IsType<QrCodeWorkbenchState>(updates.Current.State);
      Assert.Equal("qrcode.invalidInput", invalid.StatusCode);
      Assert.Equal(generated.GeneratedResource, invalid.GeneratedResource);

      client.MarkStartupPending();
      client.MarkStartupFailed(new InvalidOperationException("startup failed"));
      WorkbenchCommandReceipt afterFailure = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new GenerateQrCodeCommand("after failure")),
        timeout.Token);
      Assert.True(afterFailure.Ok);
      Assert.True(await updates.MoveNextAsync());
      Assert.True(await updates.MoveNextAsync());
      Assert.NotNull(Assert.IsType<QrCodeWorkbenchState>(updates.Current.State).GeneratedResource);

      var failingDecode = new FailingDecodeQrCodeClient();
      client.Attach(failingDecode);
      WorkbenchCommandReceipt decodeFailure = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new DecodeQrCodeClipboardCommand()),
        timeout.Token);
      Assert.True(decodeFailure.Ok);
      Assert.True(await updates.MoveNextAsync());
      Assert.True(await updates.MoveNextAsync());
      Assert.Equal("qrcode.failed", Assert.IsType<QrCodeWorkbenchState>(updates.Current.State).StatusCode);

      client.Detach(failingDecode);
      WorkbenchCommandReceipt afterDetach = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new GenerateQrCodeCommand("after detach")),
        timeout.Token);
      Assert.True(afterDetach.Ok);
      Assert.True(await updates.MoveNextAsync());
      Assert.True(await updates.MoveNextAsync());
      Assert.NotNull(Assert.IsType<QrCodeWorkbenchState>(updates.Current.State).GeneratedResource);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task UnexpectedQrCodeGenerationFailurePublishesGenericFailure()
  {
    string resourceRoot = Path.Combine(Path.GetTempPath(), $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var viewModel = new QrCodeViewModel(new FailingQrCodeClient(), new EmptyQrCodeInput());
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => viewModel,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore);
      var published = new List<WorkbenchState>();
      handler.StateChanged += published.Add;

      await handler.ExecuteAsync(new GenerateQrCodeCommand("hello"), TestContext.Current.CancellationToken);
      await handler.DisposeAsync();
      Assert.Contains(published, state => state is QrCodeWorkbenchState qr && qr.StatusCode == "qrcode.generateFailed");
      Assert.True(viewModel.GenerateFailed);
      Assert.False(viewModel.GenerateInvalidInput);
      Assert.Equal("二维码生成失败，请重试", viewModel.GenerateStatus);
    }
    finally { Directory.Delete(resourceRoot, recursive: true); }
  }

  [Fact]
  public async Task RecognitionDuringSupervisorStartupWaitsAndCompletesAfterAttach()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var deferred = new DeferredInferenceClient();
      deferred.MarkStartupPending();
      var inputs = new SignallingInputService();
      var recognition = new RecognitionViewModel(deferred, inputs);
      var settings = new SettingsViewModel(deferred);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        inferenceAttached: () => deferred.IsAttached);
      var terminal = new TaskCompletionSource<RecognitionWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      var published = new List<RecognitionWorkbenchState>();
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState recognitionState)
        {
          published.Add(recognitionState);
          if (recognitionState is { IsBusy: false })
            terminal.TrySetResult(recognitionState);
        }
      };

      WorkbenchCommandOutcome started = await handler.ExecuteAsync(
        new SelectRecognitionImageCommand(),
        TestContext.Current.CancellationToken);
      Assert.Empty(started.States);
      Assert.True(Assert.Single(published).IsBusy);

      // 后台识别在启动窗口内采集输入并在网关处等待 Attach，而不是失败。
      await inputs.Captured.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      deferred.Attach(new CompletedRecognitionInferenceClient());

      RecognitionWorkbenchState finalState = await terminal.Task.WaitAsync(
        TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      Assert.Equal("recognition.completed", finalState.StatusCode);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Theory]
  [InlineData(JobState.Failed, "recognition.failed")]
  [InlineData(JobState.Cancelled, "recognition.cancelled")]
  public async Task TerminalRecognitionRemainsVisibleWhenHandlerReprojectsWithoutResult(
    JobState terminalState, string statusCode)
  {
    string resourceRoot = Path.Combine(Path.GetTempPath(),
      $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new CompletedRecognitionInferenceClient { FinalState = terminalState };
      var recognition = new RecognitionViewModel(client, new SignallingInputService());
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new SettingsViewModel(client),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, resourceRoot, static () => 0, annotationStore);
      var failed = new TaskCompletionSource<RecognitionWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: false } final)
          failed.TrySetResult(final);
      };

      await handler.ExecuteAsync(new SelectRecognitionImageCommand(),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState terminal = await failed.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.Equal(statusCode, terminal.StatusCode);
      Assert.Null(terminal.Result);
      Assert.Equal(terminalState, recognition.TerminalState);

      WorkbenchCommandOutcome reprojected = await handler.ExecuteAsync(
        new SetTaskEngineCommand(string.Empty), TestContext.Current.CancellationToken);
      Assert.Equal(statusCode, Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(reprojected.States)).StatusCode);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task RuntimeMaintenanceLeaseChangesArePublishedToUpdateWorkbenchState()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-handler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var maintenance = new ProductMaintenanceCoordinator();
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new UpdateViewModel(
          new CurrentUpdateCoordinator(),
          productMaintenance: maintenance),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore);
      var published = new List<WorkbenchState>();
      handler.StateChanged += published.Add;
      await handler.ExecuteAsync(
        new CheckUpdateCommand(),
        TestContext.Current.CancellationToken);
      published.Clear();

      IDisposable runtime = maintenance.Acquire(
        ProductMaintenanceOwner.RuntimeMaintenance,
        () => { });
      Assert.True(Assert.IsType<UpdateWorkbenchState>(Assert.Single(published))
        .CanCancelRuntimeMaintenance);

      runtime.Dispose();
      Assert.False(Assert.IsType<UpdateWorkbenchState>(published[1])
        .CanCancelRuntimeMaintenance);
      Assert.Equal(2, published.Count);

      await handler.DisposeAsync();
      published.Clear();
      using IDisposable afterDispose = maintenance.Acquire(
        ProductMaintenanceOwner.RuntimeMaintenance,
        () => { });
      Assert.Empty(published);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  /// <summary>Signals the moment an input is acquired so tests can assert
  /// capture ordering against gateway attachment.</summary>
  private sealed class SignallingInputService : IInputService
  {
    public TaskCompletionSource Captured { get; } = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => Acquire();

    private Task<RecognitionInput?> Acquire()
    {
      Captured.TrySetResult();
      return Task.FromResult<RecognitionInput?>(
        new RecognitionInput([1, 2, 3, 4], "image/png", "file.png", "file"));
    }
  }

  /// <summary>Attaches as a ready Supervisor whose first job completes with
  /// raw text "late attach" on the first observe probe.</summary>
  private sealed class CompletedRecognitionInferenceClient : InferenceClientStub
  {
    public JobState FinalState { get; init; } = JobState.Completed;

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken) => Task.FromResult(new JobRef
      {
        JobId = "job-late",
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
          State = FinalState,
        },
        Events = Array.Empty<StageEvent>(),
        Outcomes = FinalState is JobState.Failed ? [] :
        [
          new ItemOutcome
          {
            ItemId = "it-0",
            State = ItemState.Succeeded,
            Attempt = 1,
            PayloadType = "ocr.v1",
            Payload = new Dictionary<string, JsonElement>
            {
              ["raw_text"] = JsonSerializer.SerializeToElement("late attach"),
            },
          },
        ],
        ThroughSequence = afterSequence,
      });
  }

  private sealed class BlockingQrCodeClient : IQrCodeClient
  {
    private readonly TaskCompletionSource<QrCodeGeneratedImage> completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => completion.Task;

    public void CompleteSuccessfully() => completion.TrySetResult(new QrCodeGeneratedImage(
      Convert.ToBase64String([1, 2, 3]),
      "image/png"));

    public Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
      string base64Image,
      CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<QrCodeDecodedItem>>([]);

    public Task<QrCodeGeneratedImage> GenerateAsync(
      string data,
      string format,
      CancellationToken cancellationToken) => completion.Task;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class FirstThenBlockingQrCodeClient : IQrCodeClient
  {
    private readonly TaskCompletionSource<QrCodeGeneratedImage> pending = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private int calls;

    public void CompletePending() => pending.TrySetResult(new QrCodeGeneratedImage(
      Convert.ToBase64String([4, 5, 6]), "image/png"));

    public Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
      string base64Image, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<QrCodeDecodedItem>>([]);

    public Task<QrCodeGeneratedImage> GenerateAsync(
      string data, string format, CancellationToken cancellationToken) =>
      Interlocked.Increment(ref calls) == 1
        ? Task.FromResult(new QrCodeGeneratedImage(Convert.ToBase64String([1, 2, 3]), "image/png"))
        : pending.Task;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class BlockingDecodeQrCodeClient : IQrCodeClient
  {
    private readonly TaskCompletionSource<IReadOnlyList<QrCodeDecodedItem>> completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public void CompleteSuccessfully() => completion.TrySetResult(
      [new QrCodeDecodedItem("stale", "QR_CODE", false)]);

    public Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
      string base64Image, CancellationToken cancellationToken) => completion.Task;

    public Task<QrCodeGeneratedImage> GenerateAsync(
      string data, string format, CancellationToken cancellationToken) =>
      Task.FromResult(new QrCodeGeneratedImage("AQID", "image/png"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class FailingDecodeQrCodeClient : IQrCodeClient
  {
    public Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
      string base64Image, CancellationToken cancellationToken) =>
      Task.FromException<IReadOnlyList<QrCodeDecodedItem>>(new HttpRequestException("disconnected"));

    public Task<QrCodeGeneratedImage> GenerateAsync(
      string data, string format, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("Generate must remain local.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class FailingQrCodeClient : IQrCodeClient
  {
    public Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
      string base64Image, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<QrCodeDecodedItem>>([]);

    public Task<QrCodeGeneratedImage> GenerateAsync(
      string data, string format, CancellationToken cancellationToken) =>
      Task.FromException<QrCodeGeneratedImage>(new InvalidOperationException("diagnostic detail"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private static DesktopWorkbenchCommandHandler CreateAnnotationHandler(
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotationStore,
    IAnnotatedImagePlatform platform,
    string resourceRoot) => new(
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])),
      broker,
      resourceRoot,
      static () => 0,
      annotationStore,
      platform);

  private sealed class RecordingAnnotatedImagePlatform : IAnnotatedImagePlatform
  {
    public byte[]? CopiedBytes { get; private set; }

    public byte[]? SavedBytes { get; private set; }

    public bool SaveResult { get; init; } = true;

    public Exception? CopyError { get; init; }

    public async Task CopyPngAsync(
      string sourcePath,
      CancellationToken cancellationToken)
    {
      CopiedBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
      if (CopyError is not null)
      {
        throw CopyError;
      }
    }

    public Task CopyTextAsync(string text, CancellationToken cancellationToken)
    {
      CopiedText = text;
      return Task.CompletedTask;
    }

    public string? CopiedText { get; private set; }

    public async Task<bool> SavePngAsync(
      string sourcePath,
      CancellationToken cancellationToken)
    {
      SavedBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
      return SaveResult;
    }
  }

  private sealed class EmptyQrCodeInput : IQrCodeInput
  {
    public Task<QrCodeInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<QrCodeInput?>(null);

    public Task<QrCodeInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<QrCodeInput?>(null);

    public Task<QrCodeInput?> ReadDroppedFileAsync(
      string path,
      CancellationToken cancellationToken) => Task.FromResult<QrCodeInput?>(null);
  }

  private sealed class FixedQrCodeInput : IQrCodeInput
  {
    private static readonly QrCodeInput Image = new([1, 2, 3], "image/png", "test.png");

    public Task<QrCodeInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<QrCodeInput?>(Image);

    public Task<QrCodeInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<QrCodeInput?>(Image);

    public Task<QrCodeInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken) =>
      Task.FromResult<QrCodeInput?>(Image);
  }

  private sealed class CurrentUpdateCoordinator : IUpdateCoordinator
  {
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.Latest, "0.3.0"));

    public Task<UpdateApplyResult> DownloadAndApplyAsync(
      IProgress<int>? progress,
      CancellationToken cancellationToken) =>
      Task.FromResult(new UpdateApplyResult(UpdateApplyStatus.Downloaded));
  }
}
