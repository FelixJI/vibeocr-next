using System.Text.Json;
using Velopack.Sources;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.ProductLayout;

namespace VibeOCR.App.Features.Update;

internal static class VelopackUpdateSelfTest
{
    private const string RequestedEnvironment = "VIBEOCR_SELF_TEST_VELOPACK_UPDATE";

    internal static bool IsRequested =>
        Environment.GetEnvironmentVariable(RequestedEnvironment) == "1";

    internal static int Run()
    {
        string? resultFile = Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_RESULT");
        try
        {
            string nonce = Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_NONCE") ?? "";
            if (Environment.GetEnvironmentVariable("VIBEOCR_NEXT_TEST_MODE") != "artifact-smoke" ||
                !Guid.TryParseExact(nonce, "N", out _))
            {
                throw new InvalidOperationException(
                    "Velopack artifact smoke requires authenticated test mode.");
            }
            string target = RequiredEnvironment("VIBEOCR_SELF_TEST_TARGET_VERSION");
            string feedValue = RequiredEnvironment("VIBEOCR_SELF_TEST_UPDATE_FEED");
            var feed = new Uri(feedValue);
            if (feed.Scheme != Uri.UriSchemeHttp ||
                feed.Host is not ("127.0.0.1" or "localhost"))
            {
                throw new InvalidOperationException(
                    "Velopack artifact smoke feed must be loopback HTTP.");
            }
            string installRoot = Path.GetFullPath(
                RequiredEnvironment("VIBEOCR_SELF_TEST_INSTALL_ROOT"));
            PortableProductRoots roots = PortableProductRoots.Resolve(
                Path.Combine(installRoot, "current"));
            if (!string.Equals(
                    roots.InstallRoot,
                    installRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Velopack artifact smoke install root mismatch: {roots.InstallRoot}");
            }
            string relativeAppBase = Path.GetRelativePath(
                roots.ProductRoot,
                AppContext.BaseDirectory);
            if (Path.IsPathRooted(relativeAppBase) || relativeAppBase.StartsWith(".."))
            {
                throw new InvalidOperationException(
                    $"Velopack artifact smoke app escaped product root: {AppContext.BaseDirectory}");
            }
            string result = Path.GetFullPath(RequiredEnvironment("VIBEOCR_SELF_TEST_RESULT"));
            string stateRoot = Path.Combine(installRoot, "state");
            string relativeResult = Path.GetRelativePath(stateRoot, result);
            if (Path.IsPathRooted(relativeResult) || relativeResult.StartsWith(".."))
            {
                throw new InvalidOperationException(
                    "Velopack artifact smoke result escaped state root.");
            }

            string executable = Environment.ProcessPath ?? AppContext.BaseDirectory;
            PortableLayout layout = PortableLayout.Resolve(
                executable,
                "production",
                installRootOverride: roots.InstallRoot,
                productRootOverride: roots.ProductRoot);
            layout.EnsurePortableState();
            var coordinator = new VelopackUpdateCoordinator(
                [
                    new VelopackFeedEndpoint(feed, new HttpClientFileDownloader()),
                ],
                layout.ProbeWritableStateRoot);
            UpdateCheckResult check = coordinator.CheckAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (check.Status == UpdateCheckStatus.Latest && check.Version == target)
            {
                WriteResult(result, new
                {
                    installed_version = check.Version,
                    install_root = layout.InstallRoot,
                    state_root = layout.StateRoot,
                    process_id = Environment.ProcessId,
                });
                return 0;
            }
            if (check.Status != UpdateCheckStatus.Available || check.Version != target)
            {
                throw new InvalidOperationException(
                    $"Expected update {target}, got {check.Status}: {check.Version}; " +
                    check.ErrorMessage);
            }

            UpdateApplyResult apply = coordinator.DownloadAndApplyAsync(
                    progress: null,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (apply.Status != UpdateApplyStatus.ApplyStarted)
            {
                throw new InvalidOperationException(
                    $"Velopack apply did not start: {apply.Status}: {apply.ErrorMessage}");
            }
            return 0;
        }
        catch (Exception error)
        {
            if (!string.IsNullOrWhiteSpace(resultFile))
            {
                try
                {
                    WriteResult(resultFile, new { error = error.ToString() });
                }
                catch (Exception)
                {
                }
            }
            return 1;
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required.");

    private static void WriteResult(string resultFile, object value)
    {
        string full = Path.GetFullPath(resultFile);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, full, overwrite: true);
    }
}
