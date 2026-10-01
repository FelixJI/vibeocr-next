using System.Text.Json;
using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.Platform.Windows;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;
using ZXing;
using ZXing.Common;

namespace VibeOCR.App.Tests;

public sealed class DesktopWorkbenchCommandHandlerTests
{
  private static readonly byte[] AnnotationPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

  [Fact]
  public async Task SwitchEnvironmentPublishesNewRecognitionCatalogToAllPages()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-switch-catalog-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var inference = new SwitchCatalogInferenceClient { Health = SwitchHealth(Wire.OcrEngineId.Rapidocr) };
      var manager = new SwitchCatalogManager();
      var environments = new ManagedEnvironmentSettings(manager, (id, _) =>
      {
        manager.ActiveId = id;
        inference.Health = SwitchHealth(Wire.OcrEngineId.Paddleocr);
        return Task.CompletedTask;
      }, () => null, new ProductMaintenanceCoordinator());
      var settings = new SettingsViewModel(inference, environments: environments);
      await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
      RuntimeSelectionService previous = Assert.IsType<RuntimeSelectionService>(settings.Selection);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(inference, new SignallingInputService()),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings, CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, root, static () => 0, annotations);
      var published = new List<WorkbenchState>();
      var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        published.Add(state);
        if (state is PdfWorkbenchState { Engines: { } engines } &&
            engines.Any(choice => choice.Engine == "paddleocr"))
          refreshed.TrySetResult();
      };

      await handler.ExecuteAsync(new SwitchEnvironmentCommand("paddle"),
        TestContext.Current.CancellationToken);
      await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

      Assert.NotSame(previous, settings.Selection);
      Assert.Contains(published, state => state is RecognitionWorkbenchState { Engines: { } engines } &&
        engines.Any(choice => choice.Engine == "paddleocr"));
      Assert.Contains(published, state => state is BatchWorkbenchState { Engines: { } engines } &&
        engines.Any(choice => choice.Engine == "paddleocr"));
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task SwitchEnvironmentFailureReReadsSnapshotFromCurrentService()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-switch-failure-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var inference = new SwitchFailureInferenceClient
      {
        Health = SwitchHealth(Wire.OcrEngineId.Rapidocr),
      };
      var manager = new SwitchCatalogManager();
      var environments = new ManagedEnvironmentSettings(manager, (_, _) =>
        throw new InvalidOperationException("切换失败：目标环境不存在。"),
        () => null, new ProductMaintenanceCoordinator());
      var settings = new SettingsViewModel(inference, environments: environments);
      await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
      Assert.NotNull(settings.Selection);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(inference, new SignallingInputService()),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings, CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, root, static () => 0, annotations);
      // 开始前的预刷新与失败终态的重投影各发布一轮 Pdf 状态：第二次
      // 到达即切换失败终态已落定。
      int pdfStates = 0;
      var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is PdfWorkbenchState && Interlocked.Increment(ref pdfStates) == 2)
          finished.TrySetResult();
      };

      await handler.ExecuteAsync(new SwitchEnvironmentCommand("paddle"),
        TestContext.Current.CancellationToken);
      await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

      // 失败同样按实际当前服务回读：Backend/状态不得停留在开始失效后的
      // 空投影，也不得声称实例已更换；选择沿既有刷新路径重建。
      Assert.Equal("cpu", settings.Backend);
      Assert.DoesNotContain("服务实例已更换", settings.Status);
      Assert.NotNull(settings.Selection);
      // 切换失败根因保留在环境管理器状态里，不被刷新的成功文案覆盖。
      Assert.Equal("切换失败：目标环境不存在。", environments.Status);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

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
    // The imported image is the authoritative preview now; the late generation
    // result must not overwrite it.
    Assert.Equal(Convert.ToBase64String([1, 2, 3]), second.GeneratedImageBase64);
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
  public async Task QrCodePreviewDecodeRunsOncePerRevisionAndForcesOnRequest()
  {
    var client = new CountingQrCodeClient();
    client.QueueResult(new QrCodeDecodedItem("decoded-1", "QR_CODE", false));
    client.QueueResult(new QrCodeDecodedItem("decoded-2", "QR_CODE", false));
    client.QueueResult(new QrCodeDecodedItem("decoded-3", "QR_CODE", false));
    var viewModel = new QrCodeViewModel(client, new EmptyQrCodeInput()) { GenerateText = "first" };

    await viewModel.GenerateAsync(TestContext.Current.CancellationToken);
    Assert.True(viewModel.HasPreview);
    long generatedRevision = viewModel.PreviewRevision;
    Assert.True(viewModel.NeedsPreviewDecode);

    await viewModel.DecodeCurrentPreviewAsync(TestContext.Current.CancellationToken);
    Assert.Equal(1, client.DecodeCalls);
    Assert.Equal("decoded-1", viewModel.Codes.Single().Data);
    Assert.Equal(generatedRevision, viewModel.LastDecodedRevision);
    Assert.False(viewModel.NeedsPreviewDecode);

    // Auto decode must not repeat for the same revision (page switching/re-render safe).
    await viewModel.DecodeCurrentPreviewAsync(TestContext.Current.CancellationToken);
    Assert.Equal(1, client.DecodeCalls);

    // Explicit re-recognition decodes the same preview again.
    await viewModel.DecodeCurrentPreviewAsync(force: true, TestContext.Current.CancellationToken);
    Assert.Equal(2, client.DecodeCalls);
    Assert.Equal("decoded-2", viewModel.Codes.Single().Data);

    viewModel.GenerateText = "second";
    await viewModel.GenerateAsync(TestContext.Current.CancellationToken);
    Assert.NotEqual(generatedRevision, viewModel.PreviewRevision);
    Assert.True(viewModel.NeedsPreviewDecode);
    await viewModel.DecodeCurrentPreviewAsync(TestContext.Current.CancellationToken);
    Assert.Equal(3, client.DecodeCalls);
    Assert.Equal("decoded-3", viewModel.Codes.Single().Data);
    Assert.Equal(viewModel.PreviewRevision, viewModel.LastDecodedRevision);
  }

  [Fact]
  public async Task QrCodeUnavailablePreviewDecodeKeepsPreviewForRetry()
  {
    var client = new CountingQrCodeClient();
    client.QueueUnavailable();
    client.QueueResult(new QrCodeDecodedItem("after retry", "QR_CODE", false));
    var viewModel = new QrCodeViewModel(client, new EmptyQrCodeInput()) { GenerateText = "keep preview" };
    await viewModel.GenerateAsync(TestContext.Current.CancellationToken);
    string? previewBeforeFailure = viewModel.GeneratedImageBase64;
    Assert.NotNull(previewBeforeFailure);

    await viewModel.DecodeCurrentPreviewAsync(TestContext.Current.CancellationToken);
    Assert.True(viewModel.DecodeUnavailable);
    Assert.Equal("识别运行环境未就绪", viewModel.DecodeStatus);
    Assert.True(viewModel.NeedsPreviewDecode);
    Assert.Equal(previewBeforeFailure, viewModel.GeneratedImageBase64);

    // The service stays optional: retry works on the retained preview without regenerating.
    await viewModel.DecodeCurrentPreviewAsync(TestContext.Current.CancellationToken);
    Assert.False(viewModel.DecodeUnavailable);
    Assert.Equal("after retry", viewModel.Codes.Single().Data);
    Assert.False(viewModel.NeedsPreviewDecode);
  }

  [Fact]
  public async Task QrCodeGeneratedAndImportedPreviewsAreOneAuthoritativeState()
  {
    var client = new CountingQrCodeClient();
    client.QueueResult(new QrCodeDecodedItem("import", "QR_CODE", false));
    var viewModel = new QrCodeViewModel(client, new FixedQrCodeInput());

    await viewModel.DecodeAsync(QrCodeInputKind.File, TestContext.Current.CancellationToken);
    Assert.Equal(1, client.DecodeCalls);
    Assert.Equal(Convert.ToBase64String([1, 2, 3]), viewModel.GeneratedImageBase64);
    Assert.False(viewModel.NeedsPreviewDecode);

    // Regenerating replaces the single preview; the next auto decode uses the new bytes.
    viewModel.GenerateText = "replace";
    await viewModel.GenerateAsync(TestContext.Current.CancellationToken);
    Assert.Equal(Convert.ToBase64String([9, 9, 9]), viewModel.GeneratedImageBase64);
    Assert.True(viewModel.NeedsPreviewDecode);
  }

  [Fact]
  public async Task QrCodeCaptionReflectsInFlightSnapshotInsteadOfLaterEdits()
  {
    var client = new SnapshotQrCodeClient();
    var viewModel = new QrCodeViewModel(client, new EmptyQrCodeInput())
    {
      GenerateText = "encoded-payload",
      CaptionMode = QrCodeCaptionMode.Payload,
    };

    Task generate = viewModel.GenerateAsync(TestContext.Current.CancellationToken);
    await client.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    // User edits every caption-relevant input while generation is in flight.
    viewModel.GenerateText = "edited-in-flight";
    viewModel.CaptionMode = QrCodeCaptionMode.Off;
    viewModel.CaptionText = "unrelated";
    client.Release();
    await generate;

    Assert.Equal("encoded-payload", client.RequestedData);
    Assert.NotNull(viewModel.GeneratedImageBase64);
    (byte[] bgra, uint width, uint height) = await DecodePreviewPixelsAsync(viewModel.GeneratedImageBase64!);
    // The snapshot payload caption is present: the canvas is taller than the bare QR.
    Assert.True(height > width, $"caption strip missing: {width}x{height}");
    Assert.Equal("encoded-payload", DecodePreviewBarcode(bgra, width, height));
  }

  [Fact]
  public async Task ReleasingPreviewCancelsInFlightDecodeSoLateCodesDoNotRevive()
  {
    var client = new BlockingDecodeQrCodeClient();
    var viewModel = new QrCodeViewModel(client, new FixedQrCodeInput());
    Task decode = viewModel.DecodeAsync(QrCodeInputKind.File, TestContext.Current.CancellationToken);
    Assert.True(viewModel.IsBusy);
    Assert.NotNull(viewModel.GeneratedImageBase64);

    viewModel.ReleaseGeneratedImage();
    Assert.Null(viewModel.GeneratedImageBase64);
    Assert.False(viewModel.IsBusy);

    client.CompleteSuccessfully();
    await decode;
    Assert.Empty(viewModel.Codes);
    Assert.Null(viewModel.GeneratedImageBase64);
    Assert.False(viewModel.IsBusy);
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

  private static async Task<(byte[] Bgra, uint Width, uint Height)> DecodePreviewPixelsAsync(string base64Png)
  {
    byte[] png = Convert.FromBase64String(base64Png);
    using var stream = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(stream))
    {
      writer.WriteBytes(png);
      await writer.StoreAsync();
      writer.DetachStream();
    }
    stream.Seek(0);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
    byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
      BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
      ColorManagementMode.DoNotColorManage)).DetachPixelData();
    return (pixels, decoder.PixelWidth, decoder.PixelHeight);
  }

  private static string? DecodePreviewBarcode(byte[] bgra, uint width, uint height)
  {
    byte[] rgb = new byte[width * height * 3];
    for (int i = 0; i < width * height; i++)
    {
      rgb[i * 3] = bgra[i * 4 + 2];
      rgb[i * 3 + 1] = bgra[i * 4 + 1];
      rgb[i * 3 + 2] = bgra[i * 4];
    }
    var reader = new BarcodeReaderGeneric { Options = new DecodingOptions { TryHarder = true } };
    return reader.Decode(new RGBLuminanceSource(rgb, (int)width, (int)height))?.Text;
  }

  private sealed class CountingQrCodeClient : IQrCodeClient
  {
    private readonly Queue<Func<CancellationToken, Task<IReadOnlyList<QrCodeDecodedItem>>>> responses = [];

    public List<string> DecodedImages { get; } = [];

    public int DecodeCalls => DecodedImages.Count;

    public void QueueResult(params QrCodeDecodedItem[] items) =>
      responses.Enqueue(_ => Task.FromResult<IReadOnlyList<QrCodeDecodedItem>>(items));

    public void QueueUnavailable() =>
      responses.Enqueue(ct => Task.FromException<IReadOnlyList<QrCodeDecodedItem>>(
        new InferenceClientNotAttachedException("not attached")));

    public Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
      string base64Image, CancellationToken cancellationToken)
    {
      DecodedImages.Add(base64Image);
      return responses.Dequeue()(cancellationToken);
    }

    public Task<QrCodeGeneratedImage> GenerateAsync(
      string data, string format, CancellationToken cancellationToken) =>
      Task.FromResult(new QrCodeGeneratedImage(Convert.ToBase64String([9, 9, 9]), "image/png"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class SnapshotQrCodeClient : IQrCodeClient
  {
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? RequestedData { get; private set; }

    public Task Started => started.Task;

    public void Release() => release.TrySetResult();

    public async Task<QrCodeGeneratedImage> GenerateAsync(
      string data, string format, CancellationToken cancellationToken)
    {
      RequestedData = data;
      QrCodeGeneratedImage image = await LocalQrCodeGenerator.GenerateAsync(
        data, format, cancellationToken);
      started.TrySetResult();
      await release.Task.WaitAsync(cancellationToken);
      return image;
    }

    public Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
      string base64Image, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<QrCodeDecodedItem>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

  [Fact]
  public async Task HotkeyCommandsProjectRegistrarStateIntoSettings()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      (ShellActionDispatcher dispatcher, WindowsHotkeyRegistrar registrar, _, _, _) =
        CreateShellActions(root);
      registrar.InitializeActions();
      var settings = new SettingsViewModel(new CompletedRecognitionInferenceClient());
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: dispatcher);

      WorkbenchCommandOutcome bound = await handler.ExecuteAsync(
        new SetActionHotkeyCommand("clipboard_recognize", "Ctrl+Alt+C"),
        TestContext.Current.CancellationToken);
      var boundState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(bound.States));
      SettingsHotkeyActionState recognize = Assert.Single(
        boundState.HotkeyActions!, item => item.ActionId == "screenshot_recognize");
      Assert.Equal("Ctrl+Alt+Q", recognize.RegisteredHotkey);
      SettingsHotkeyActionState clipboard = Assert.Single(
        boundState.HotkeyActions!, item => item.ActionId == "clipboard_recognize");
      Assert.Equal("Ctrl+Alt+C", clipboard.ConfiguredHotkey);
      Assert.Equal("Ctrl+Alt+C", clipboard.RegisteredHotkey);

      // 重复键位被拒：命令仍成功（设置页以动作状态回显），错误可见且原绑定不变。
      WorkbenchCommandOutcome conflict = await handler.ExecuteAsync(
        new SetActionHotkeyCommand("screenshot_edit", "Ctrl+Alt+Q"),
        TestContext.Current.CancellationToken);
      Assert.Null(conflict.Error);
      var conflictState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(conflict.States));
      SettingsHotkeyActionState edit = Assert.Single(
        conflictState.HotkeyActions!, item => item.ActionId == "screenshot_edit");
      Assert.Null(edit.RegisteredHotkey);
      Assert.Null(edit.ConfiguredHotkey);
      Assert.Contains("已被动作", edit.Error);

      WorkbenchCommandOutcome reset = await handler.ExecuteAsync(
        new ResetActionHotkeyCommand("screenshot_recognize"),
        TestContext.Current.CancellationToken);
      var resetState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(reset.States));
      SettingsHotkeyActionState resetRecognize = Assert.Single(
        resetState.HotkeyActions!, item => item.ActionId == "screenshot_recognize");
      Assert.Equal("Ctrl+Alt+Q", resetRecognize.RegisteredHotkey);
      Assert.Null(resetRecognize.Error);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ToolbarCommandsApplyPreferencesAndProjectVisibility()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      (ShellActionDispatcher dispatcher, _, List<FloatingToolbarSettings> applied, _, _) =
        CreateShellActions(root);
      var settings = new SettingsViewModel(new CompletedRecognitionInferenceClient());
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: dispatcher);

      WorkbenchCommandOutcome enabled = await handler.ExecuteAsync(
        new SetFloatingToolbarEnabledCommand(true),
        TestContext.Current.CancellationToken);
      var enabledState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(enabled.States));
      Assert.True(enabledState.FloatingToolbar!.Enabled);

      WorkbenchCommandOutcome layout = await handler.ExecuteAsync(
        new SetFloatingToolbarLayoutCommand(ScreenEdge.Left, false),
        TestContext.Current.CancellationToken);
      var layoutState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(layout.States));
      Assert.Equal("left", layoutState.FloatingToolbar!.Edge);
      Assert.False(layoutState.FloatingToolbar.AutoHide);
      Assert.Equal(2, applied.Count);
      Assert.True(applied[0].Enabled);
      Assert.Equal(ScreenEdge.Left, applied[1].Edge);

      // 重复偏好不再重复应用。
      await handler.ExecuteAsync(
        new SetFloatingToolbarLayoutCommand(ScreenEdge.Left, false),
        TestContext.Current.CancellationToken);
      Assert.Equal(2, applied.Count);

      WorkbenchCommandOutcome shown = await handler.ExecuteAsync(
        new ShowFloatingToolbarCommand(),
        TestContext.Current.CancellationToken);
      var shownState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(shown.States));
      Assert.Equal("visible", shownState.FloatingToolbar!.Visibility);
      WorkbenchCommandOutcome hidden = await handler.ExecuteAsync(
        new HideFloatingToolbarCommand(),
        TestContext.Current.CancellationToken);
      var hiddenState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(hidden.States));
      Assert.Equal("visible", hiddenState.FloatingToolbar!.Visibility);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task CaptureScreenSuspendsFloatingToolbarUntilCompletion()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new CompletedRecognitionInferenceClient();
      var inputs = new SignallingInputService();
      var recognition = new RecognitionViewModel(client, inputs);
      var settings = new SettingsViewModel(client);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      var suspension = new TrackingSuspension();
      var dispatcher = new ShellActionDispatcher(
        () => null,
        new Dictionary<string, Func<Task>>(StringComparer.Ordinal),
        () => FloatingToolbarSettings.Default,
        () => FloatingToolbarVisibility.Visible,
        _ => null,
        () => null,
        () => null,
        () => suspension.Suspend());
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
        shellActions: dispatcher);
      var terminal = new TaskCompletionSource<RecognitionWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: false } final)
        {
          terminal.TrySetResult(final);
        }
      };

      await handler.ExecuteAsync(
        new CaptureRecognitionScreenCommand(),
        TestContext.Current.CancellationToken);

      // 采集输入时悬浮栏必须仍在让位中，完成（含取消）后才恢复。
      await inputs.Captured.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.True(suspension.Active);
      Assert.Equal(0, suspension.ResumedCount);

      await terminal.Task.WaitAsync(
        TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      Assert.False(suspension.Active);
      Assert.Equal(1, suspension.ResumedCount);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ShellActionCommandsFailClosedWithoutDispatcher()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      var settings = new SettingsViewModel(new CompletedRecognitionInferenceClient());
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
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
        annotationStore);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SetActionHotkeyCommand("screenshot_recognize", "Ctrl+Alt+Q"),
        TestContext.Current.CancellationToken);
      Assert.Equal("desktop_command_failed", outcome.Error?.Code);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task CaptureEntriesAreSingleFlightAcrossActionsAndRetryAfterCancel()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new CompletedRecognitionInferenceClient();
      var inputs = new BlockingCaptureInputService();
      var recognition = new RecognitionViewModel(client, inputs);
      var settings = new SettingsViewModel(client);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      (ShellActionDispatcher dispatcher, _, _, _, DispatchRecorder shows) =
        CreateShellActions(root);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: dispatcher);
      var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: false } final)
        {
          waiter.TrySetResult(final.StatusCode);
        }
      };

      WorkbenchCommandOutcome first = await handler.ExecuteAsync(
        new CaptureScreenshotSessionCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(first.Error);

      // 同动作重复触发与跨纯截图/识别入口都不得再开第二个选区。
      WorkbenchCommandOutcome repeat = await handler.ExecuteAsync(
        new CaptureScreenshotSessionCommand(),
        TestContext.Current.CancellationToken);
      Assert.Equal("capture_in_progress", repeat.Error?.Code);
      WorkbenchCommandOutcome textCapture = await handler.ExecuteAsync(
        new CaptureScreenshotTextSessionCommand(),
        TestContext.Current.CancellationToken);
      Assert.Equal("capture_in_progress", textCapture.Error?.Code);
      WorkbenchCommandOutcome cross = await handler.ExecuteAsync(
        new CaptureRecognitionScreenCommand(),
        TestContext.Current.CancellationToken);
      Assert.Equal("capture_in_progress", cross.Error?.Code);
      Assert.Equal(1, inputs.CaptureCalls);

      // 开始 capture 与重入被拒不显示工作台。
      Assert.Equal(0, shows.ShowWorkbench);

      // 用户在选区界面取消：guard 释放后可重试；取消也是纯截图会话的
      // 真实终态，显示一次呈现编辑器入口。
      inputs.CancelByUser();
      Assert.Equal(
        "recognition.cancelled",
        await waiter.Task.WaitAsync(
          TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
      Assert.Equal(1, await WaitForCountAsync(
        () => shows.ShowWorkbench, 1, TimeSpan.FromSeconds(5)));

      // 截图识别入口重试：同样只在真实终态后显示。
      waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
      WorkbenchCommandOutcome retry = await handler.ExecuteAsync(
        new CaptureRecognitionScreenCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(retry.Error);
      inputs.CancelByUser();
      Assert.Equal(
        "recognition.cancelled",
        await waiter.Task.WaitAsync(
          TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
      Assert.Equal(2, inputs.CaptureCalls);
      Assert.Equal(2, await WaitForCountAsync(
        () => shows.ShowWorkbench, 2, TimeSpan.FromSeconds(5)));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SessionCaptureShowsWorkbenchOnlyAfterTerminalCompletion()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new CompletedRecognitionInferenceClient();
      var inputs = new BlockingCaptureInputService();
      var recognition = new RecognitionViewModel(client, inputs);
      var settings = new SettingsViewModel(client);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      (ShellActionDispatcher dispatcher, _, _, _, DispatchRecorder shows) =
        CreateShellActions(root);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: dispatcher);
      var terminal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: false } final)
        {
          terminal.TrySetResult(final.StatusCode);
        }
      };

      WorkbenchCommandOutcome started = await handler.ExecuteAsync(
        new CaptureScreenshotSessionCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(started.Error);

      // 选区进行中：不显示工作台，不抢遮罩焦点。
      Assert.Equal(0, shows.ShowWorkbench);

      // 真正完成（确认选区建立编辑会话）后才显示。
      inputs.CompleteByUser();
      Assert.Equal(
        "recognition.session",
        await terminal.Task.WaitAsync(
          TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
      Assert.Equal(1, await WaitForCountAsync(
        () => shows.ShowWorkbench, 1, TimeSpan.FromSeconds(5)));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ScreenRecognitionShowsWorkbenchOnlyAfterTerminalCompletion()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new CompletedRecognitionInferenceClient();
      var inputs = new BlockingCaptureInputService();
      var recognition = new RecognitionViewModel(client, inputs);
      var settings = new SettingsViewModel(client);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      (ShellActionDispatcher dispatcher, _, _, _, DispatchRecorder shows) =
        CreateShellActions(root);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: dispatcher);
      var terminal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: false } final)
        {
          terminal.TrySetResult(final.StatusCode);
        }
      };

      WorkbenchCommandOutcome started = await handler.ExecuteAsync(
        new CaptureRecognitionScreenCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(started.Error);

      // 选区进行中：不显示工作台，不抢遮罩焦点。
      Assert.Equal(0, shows.ShowWorkbench);

      // 真正完成（确认选区并识别成功）后才显示；终态发布与 finally 内的
      // 显示分派存在微小先后，等待其到达。
      inputs.CompleteByUser();
      Assert.Equal(
        "recognition.completed",
        await terminal.Task.WaitAsync(
          TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
      Assert.Equal(1, await WaitForCountAsync(
        () => shows.ShowWorkbench, 1, TimeSpan.FromSeconds(5)));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>终态状态发布与 finally 内的显示分派顺序微小，轮询到计数稳定。</summary>
  private static async Task<int> WaitForCountAsync(
    Func<int> current,
    int expected,
    TimeSpan timeout)
  {
    DateTime deadline = DateTime.UtcNow + timeout;
    while (current() != expected && DateTime.UtcNow < deadline)
    {
      await Task.Delay(20);
    }

    return current();
  }

  [Fact]
  public async Task SessionCaptureRetryWorksAfterStartStatePublicationFails()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new CompletedRecognitionInferenceClient();
      var inputs = new BlockingCaptureInputService();
      var recognition = new RecognitionViewModel(client, inputs);
      var settings = new SettingsViewModel(client);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: CreateShellActions(root).Dispatcher);
      var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: false } final)
        {
          waiter.TrySetResult(final.StatusCode);
        }
      };
      // 首次启动状态发布（同步 StateChanged）故障：guard 必须释放，
      // 不得把截图永久锁死在 capture_in_progress。
      int publications = 0;
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: true } &&
          Interlocked.Increment(ref publications) == 1)
        {
          throw new InvalidOperationException("start state subscriber fault");
        }
      };

      WorkbenchCommandOutcome failed = await handler.ExecuteAsync(
        new CaptureScreenshotSessionCommand(),
        TestContext.Current.CancellationToken);
      Assert.Equal("desktop_command_failed", failed.Error?.Code);

      // 同一入口立即重试：可重试契约成立。
      WorkbenchCommandOutcome retry = await handler.ExecuteAsync(
        new CaptureScreenshotSessionCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(retry.Error);
      inputs.CancelByUser();
      Assert.Equal(
        "recognition.cancelled",
        await waiter.Task.WaitAsync(
          TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task RecognitionCaptureRetryWorksAfterStartStatePublicationFails()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new CompletedRecognitionInferenceClient();
      var inputs = new BlockingCaptureInputService();
      var recognition = new RecognitionViewModel(client, inputs);
      var settings = new SettingsViewModel(client);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: CreateShellActions(root).Dispatcher);
      int publications = 0;
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: true } &&
          Interlocked.Increment(ref publications) == 1)
        {
          throw new InvalidOperationException("start state subscriber fault");
        }
      };
      var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { IsBusy: false } final)
        {
          waiter.TrySetResult(final.StatusCode);
        }
      };

      WorkbenchCommandOutcome failed = await handler.ExecuteAsync(
        new CaptureRecognitionScreenCommand(),
        TestContext.Current.CancellationToken);
      Assert.Equal("desktop_command_failed", failed.Error?.Code);

      WorkbenchCommandOutcome retry = await handler.ExecuteAsync(
        new CaptureRecognitionScreenCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(retry.Error);
      inputs.CancelByUser();
      Assert.Equal(
        "recognition.cancelled",
        await waiter.Task.WaitAsync(
          TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ToolbarFailuresKeepOldStateAndSurfaceErrors()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-shell-actions-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      (ShellActionDispatcher dispatcher, _, List<FloatingToolbarSettings> applied, _, _) =
        CreateShellActions(
          root,
          applyOverride: _ => "无法保存悬浮工具栏设置，原设置已保留：disk full",
          showOverride: () => "悬浮工具栏已关闭，请先在设置中启用。");
      var settings = new SettingsViewModel(new CompletedRecognitionInferenceClient());
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: dispatcher);

      WorkbenchCommandOutcome failed = await handler.ExecuteAsync(
        new SetFloatingToolbarEnabledCommand(true),
        TestContext.Current.CancellationToken);
      var failedState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(failed.States));
      Assert.Contains("原设置已保留", failedState.FloatingToolbar!.Error);
      // 保存失败保旧：偏好未应用、未记录。
      Assert.False(failedState.FloatingToolbar.Enabled);
      Assert.Empty(applied);

      // 已关闭时 Show 必须显式报错，不得看似成功。
      WorkbenchCommandOutcome shown = await handler.ExecuteAsync(
        new ShowFloatingToolbarCommand(),
        TestContext.Current.CancellationToken);
      var shownState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(shown.States));
      Assert.Contains("请先在设置中启用", shownState.FloatingToolbar!.Error);

      // 成功应用后错误清空。
      string healthyRoot = Path.Combine(root, "healthy");
      Directory.CreateDirectory(healthyRoot);
      (ShellActionDispatcher healthy, _, List<FloatingToolbarSettings> healthyApplied, _, _) =
        CreateShellActions(healthyRoot);
      // 重新接线一个健康 handler 验证错误不残留：直接调用同一 dispatcher。
      await using var healthyHandler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        CreateShellViewModel,
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        shellActions: healthy);
      WorkbenchCommandOutcome ok = await healthyHandler.ExecuteAsync(
        new SetFloatingToolbarEnabledCommand(true),
        TestContext.Current.CancellationToken);
      var okState = Assert.IsType<SettingsWorkbenchState>(Assert.Single(ok.States));
      Assert.True(okState.FloatingToolbar!.Enabled);
      Assert.Equal(string.Empty, okState.FloatingToolbar.Error);
      Assert.Single(healthyApplied);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private static ShellViewModel CreateShellViewModel() =>
    new(new NoopHotkeyRegistrar(), new NoopStartupRegistrar());

  private static Wire.Health SwitchHealth(Wire.OcrEngineId engine) => new()
  {
    SchemaVersion = 2, InstanceId = engine.ToString(), ProtocolVersion = 2,
    Ready = true, Draining = false,
    Capabilities = [RuntimeSelectionService.EngineSelectionCapability],
    CapabilityDescriptors = [new Wire.CapabilityDescriptor
    {
      Name = RuntimeSelectionService.EngineSelectionCapability,
      Lifecycle = "active", IntroducedIn = "2.6.0",
      DeprecatedIn = null, SunsetAt = null, Replacement = null,
      OcrEngineCatalog = new Wire.OcrEngineCatalog { Engines = [new Wire.OcrEngineDescriptor
      {
        Id = engine, Availability = Wire.OcrEngineAvailability.Ready,
        IncludedInBase = true, ReasonCode = null, RequiredComponent = null,
      }] },
    }],
  };

  private sealed class SwitchCatalogInferenceClient : InferenceClientStub
  {
    public required Wire.Health Health { get; set; }
    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Health);
    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new SettingsSnapshot());
  }

  /// <summary>提供真实 runtime/residency 读数；必须重列 IInferenceClient 才能占住
  /// GetRuntimeStatusAsync 接口槽，否则走接口默认实现抛 NotSupportedException。</summary>
  private sealed class SwitchFailureInferenceClient : InferenceClientStub, IInferenceClient
  {
    public required Wire.Health Health { get; set; }
    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Health);
    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new SettingsSnapshot());
    public override Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new ResidencyStatus());
    public Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new RuntimeStatusSnapshot
      {
        InstanceId = "sup-switch-failure",
        ServiceState = RuntimeServiceState.Ready,
        BackendVersion = "0.14.0",
        Profile = new RuntimeProfileStatus
        {
          ProfileId = "win-x64-cpu",
          Accelerator = RuntimeAccelerator.Cpu,
          Components = [],
        },
      });
  }

  private sealed class SwitchCatalogManager : IManagedEnvironmentClient
  {
    public string ActiveId { get; set; } = "rapid";
    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentList(ActiveId, 1, []));
    public Task<ManagedEnvironment> CreateEnvironmentAsync(string name, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(string? environmentId,
      string? packageSourceId, string? modelSourceId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(string environmentId,
      string recipe, IReadOnlyList<string>? sourceIds = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<ManagedEnvironment> InstallEnvironmentAsync(ManagedEnvironmentPlan plan,
      IReadOnlyList<string>? sourceIds = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(string environmentId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(PreparedEnvironmentSwitch prepared,
      StartedEnvironmentHealth? startedHealth = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(string environmentId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteEnvironmentAsync(string environmentId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private static (ShellActionDispatcher Dispatcher, WindowsHotkeyRegistrar Registrar, List<FloatingToolbarSettings> Applied, TrackingSuspension Suspension, DispatchRecorder Dispatched) CreateShellActions(
    string root,
    Func<FloatingToolbarSettings, string?>? applyOverride = null,
    Func<string?>? showOverride = null,
    Func<string?>? hideOverride = null)
  {
    PortableLayout layout = PortableLayout.Resolve(
      Path.Combine(root, "VibeOCR.Next.exe"),
      "production");
    // These command tests need a settings directory, not the production
    // write/rename/delete probe covered by PortableLayoutTests.
    Directory.CreateDirectory(Path.GetDirectoryName(layout.ConfigFile)!);
    var registrar = new WindowsHotkeyRegistrar(
      new GlobalHotkeyService(new AcceptingHotkeyNative()),
      layout);
    var applied = new List<FloatingToolbarSettings>();
    var recorder = new DispatchRecorder();
    FloatingToolbarSettings current = FloatingToolbarSettings.Default with { Enabled = false };
    var suspension = new TrackingSuspension();
    var dispatcher = new ShellActionDispatcher(
      () => registrar,
      new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
      {
        // 可观测的 ShowWorkbench：命令层终态显示经分派器计数，
        // 与生产路径同一入口。
        [HotkeyActionCatalog.ShowWorkbench] = recorder.RecordShowWorkbench,
      },
      () => current,
      () => FloatingToolbarVisibility.Visible,
      next =>
      {
        string? error = applyOverride?.Invoke(next);
        if (error is not null)
        {
          // 保存失败保旧：不应用也不记录。
          return error;
        }

        current = next;
        applied.Add(next);
        return null;
      },
      showOverride ?? (() => null),
      hideOverride ?? (() => null),
      () => suspension.Suspend());
    return (dispatcher, registrar, applied, suspension, recorder);
  }

  /// <summary>记录经分派器发出的 ShowWorkbench 请求数（终态显示可观测）。</summary>
  private sealed class DispatchRecorder
  {
    private int showWorkbench;

    public int ShowWorkbench => showWorkbench;

    public Task RecordShowWorkbench()
    {
      Interlocked.Increment(ref showWorkbench);
      return Task.CompletedTask;
    }
  }

  private sealed class NoopHotkeyRegistrar : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict)
    {
      conflict = null;
      return true;
    }

    public void Unregister()
    {
    }
  }

  private sealed class NoopStartupRegistrar : IStartupRegistrar
  {
    public bool SetEnabled(bool enabled) => true;
  }

  private sealed class TrackingSuspension : IDisposable
  {
    private int active;

    public int ResumedCount { get; private set; }

    public bool Active => Volatile.Read(ref active) > 0;

    public IDisposable Suspend()
    {
      Interlocked.Increment(ref active);
      return this;
    }

    public void Dispose()
    {
      Interlocked.Decrement(ref active);
      ResumedCount++;
    }
  }

  private sealed class AcceptingHotkeyNative : IHotkeyNativeMethods
  {
    private readonly HashSet<int> activeIds = [];

    public bool Register(nint windowHandle, int id, HotkeyModifiers modifiers, uint virtualKey) =>
      activeIds.Add(id);

    public bool Unregister(nint windowHandle, int id) => activeIds.Remove(id);
  }

  /// <summary>
  /// 选区不结束的输入服务：模拟选区窗口打开，由测试决定何时取消；
  /// 统计 CaptureScreenAsync 调用次数验证单飞 guard。
  /// </summary>
  private sealed class BlockingCaptureInputService : IInputService
  {
    private readonly TaskCompletionSource<RecognitionInput?> completion = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private int captureCalls;

    public int CaptureCalls => captureCalls;

    public void CancelByUser() => completion.TrySetResult(null);

    /// <summary>模拟用户确认选区：返回有效输入继续识别。</summary>
    public void CompleteByUser() => completion.TrySetResult(
      new RecognitionInput([1, 2, 3, 4], "image/png", "capture.png", "screen"));

    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      completion.Task;

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      completion.Task;

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref captureCalls);
      return completion.Task;
    }

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => completion.Task;
  }
}
