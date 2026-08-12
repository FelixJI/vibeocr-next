using VibeOCR.Platform.Update;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class UpdateArtifactCleanerTests
{
    [Fact]
    public async Task CleanupRemovesOnlyWhitelistedArtifacts()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vibeocr-cleaner-{Guid.NewGuid():N}");
        string data = Path.Combine(root, "data");
        string update = Path.Combine(data, "cache", "update");
        Directory.CreateDirectory(Path.Combine(update, "transaction-stale", "staging"));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(update, "package.zip"), "zip", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(update, "package.zip.sha256"), "hash", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(update, "updater.exe"), "exe", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(update, "updater.ready"), "ready", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(update, "startup.healthy"), "healthy", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(update, "progress.json"), "{}", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(update, "keep.txt"), "keep", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(update, "application.healthy"), "healthy", cancellationToken);

        try
        {
            await UpdateArtifactCleaner.CleanupAsync(
                data,
                TimeSpan.Zero,
                cancellationToken);

            Assert.False(Directory.Exists(Path.Combine(update, "transaction-stale")));
            Assert.False(File.Exists(Path.Combine(update, "package.zip")));
            Assert.False(File.Exists(Path.Combine(update, "updater.exe")));
            Assert.False(File.Exists(Path.Combine(update, "application.healthy")));
            Assert.True(File.Exists(Path.Combine(update, "progress.json")));
            Assert.True(File.Exists(Path.Combine(update, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupAcceptsAnExplicitUserDataRootOutsideInstallRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"install-{Guid.NewGuid():N}");
        string data = Path.Combine(Path.GetTempPath(), $"data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(data, "cache", "update", "transaction-stale"));
        try
        {
            await UpdateArtifactCleaner.CleanupAsync(
                data,
                TimeSpan.Zero,
                TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(
                Path.Combine(data, "cache", "update", "transaction-stale")));
        }
        finally
        {
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }
}
