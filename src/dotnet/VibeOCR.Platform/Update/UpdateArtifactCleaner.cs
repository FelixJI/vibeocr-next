namespace VibeOCR.Platform.Update;

/// <summary>Removes updater hand-off artifacts after the new application is healthy.</summary>
public static class UpdateArtifactCleaner
{
    private static readonly string[] CacheFiles =
    [
        "updater.exe",
        "updater.ready",
        "application.healthy",
        "startup.healthy",
    ];

    public static async Task CleanupAsync(
        string dataRoot,
        TimeSpan initialDelay,
        CancellationToken cancellationToken = default)
    {
        string data = Path.GetFullPath(dataRoot);
        if (initialDelay > TimeSpan.Zero)
        {
            await Task.Delay(initialDelay, cancellationToken);
        }

        string updateRoot = Path.Combine(data, "cache", "update");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(updateRoot))
            {
                foreach (string directory in Directory.EnumerateDirectories(
                    updateRoot,
                    "transaction-*",
                    SearchOption.TopDirectoryOnly))
                {
                    TryDeleteDirectory(directory);
                }
                foreach (string file in Directory.EnumerateFiles(updateRoot))
                {
                    string name = Path.GetFileName(file);
                    if (CacheFiles.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteFile(file);
                    }
                }
            }
            if (!HasPendingArtifacts(updateRoot))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
    }

    private static bool HasPendingArtifacts(string updateRoot)
    {
        if (!Directory.Exists(updateRoot))
        {
            return false;
        }
        return Directory.EnumerateDirectories(
                updateRoot,
                "transaction-*",
                SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateFiles(updateRoot).Any(file =>
            {
                string name = Path.GetFileName(file);
                return CacheFiles.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
