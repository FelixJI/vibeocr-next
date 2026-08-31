using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Update;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
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
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new QrCodeViewModel(client, input),
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

      WorkbenchCommandOutcome started = await handler.ExecuteAsync(
        new GenerateQrCodeCommand("hello"),
        TestContext.Current.CancellationToken);
      QrCodeWorkbenchState busy = Assert.IsType<QrCodeWorkbenchState>(
        Assert.Single(started.States));
      Assert.True(busy.IsBusy);
      Assert.Equal("qrcode.running", busy.StatusCode);

      WorkbenchCommandOutcome cancelled = await handler.ExecuteAsync(
        new CancelQrCodeCommand(),
        TestContext.Current.CancellationToken);
      QrCodeWorkbenchState idle = Assert.IsType<QrCodeWorkbenchState>(
        Assert.Single(cancelled.States));
      Assert.False(idle.IsBusy);
      Assert.Equal("qrcode.cancelled", idle.StatusCode);

      client.CompleteSuccessfully();
      await client.Completion;
      await handler.DisposeAsync();
      Assert.Empty(published);
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
