using VibeOCR.App.Features.QrCode;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class DesktopWorkbenchCommandHandlerTests
{
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
        static () => 0);
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
}
