using VibeOCR.App.Inference;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class DeferredQrCodeClientTests
{
    [Fact]
    public async Task UnattachedCallThrowsWhenNoStartupIsPendingAsync()
    {
        var deferred = new DeferredQrCodeClient();

        Assert.False(deferred.IsAttached);
        await Assert.ThrowsAsync<InferenceClientNotAttachedException>(
            () => deferred.GenerateAsync("vibeocr", "png", CancellationToken.None));
    }

    [Fact]
    public async Task PendingStartupCallWaitsThenDelegatesAfterAttachAsync()
    {
        var deferred = new DeferredQrCodeClient();
        deferred.MarkStartupPending();
        Task<QrCodeGeneratedImage> call = deferred.GenerateAsync(
            "vibeocr", "png", CancellationToken.None);
        Assert.False(call.IsCompleted);

        deferred.Attach(new StubQrCodeClient());

        QrCodeGeneratedImage image = await call;
        Assert.Equal("image/png", image.MediaType);
    }

    [Fact]
    public async Task StartupFailureReleasesPendingCallsAsync()
    {
        var deferred = new DeferredQrCodeClient();
        deferred.MarkStartupPending();
        Task<IReadOnlyList<QrCodeDecodedItem>> pending = deferred.DecodeAsync(
            "abc", CancellationToken.None);

        deferred.MarkStartupFailed(new InvalidOperationException("supervisor crash"));

        await Assert.ThrowsAsync<InferenceClientNotAttachedException>(() => pending);
    }

    private sealed class StubQrCodeClient : IQrCodeClient
    {
        public Task<IReadOnlyList<QrCodeDecodedItem>> DecodeAsync(
            string base64Image, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<QrCodeDecodedItem>>([]);

        public Task<QrCodeGeneratedImage> GenerateAsync(
            string data, string format, CancellationToken ct) =>
            Task.FromResult(new QrCodeGeneratedImage("aGk=", $"image/{format}"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
