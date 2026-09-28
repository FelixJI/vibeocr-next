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
            () => deferred.DecodeAsync("abc", CancellationToken.None));
    }

    [Fact]
    public async Task PendingStartupCallWaitsThenDelegatesAfterAttachAsync()
    {
        var deferred = new DeferredQrCodeClient();
        deferred.MarkStartupPending();
        Task<IReadOnlyList<QrCodeDecodedItem>> call = deferred.DecodeAsync(
            "abc", CancellationToken.None);
        Assert.False(call.IsCompleted);

        deferred.Attach(new StubQrCodeClient());

        Assert.Empty(await call);
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

    [Fact]
    public async Task GenerationWorksBeforeAttachDuringStartupFailureAndAfterDetachAsync()
    {
        var deferred = new DeferredQrCodeClient();
        deferred.MarkStartupPending();
        Assert.Equal("image/png", (await deferred.GenerateAsync("中文🙂", "qrcode", CancellationToken.None)).MediaType);
        deferred.MarkStartupFailed(new InvalidOperationException("startup failed"));
        Assert.Equal("image/png", (await deferred.GenerateAsync("hello", "qrcode", CancellationToken.None)).MediaType);
        var attached = new StubQrCodeClient();
        deferred.Attach(attached);
        deferred.Detach(attached);
        Assert.Equal("image/png", (await deferred.GenerateAsync("after detach", "qrcode", CancellationToken.None)).MediaType);
    }

    [Fact]
    public async Task GenerationHonorsShutdownAndCallerCancellationAsync()
    {
        using var shutdown = new CancellationTokenSource();
        var deferred = new DeferredQrCodeClient(shutdown.Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deferred.GenerateAsync("hello", "qrcode", cancelled.Token));
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deferred.GenerateAsync("hello", "qrcode", CancellationToken.None));
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
