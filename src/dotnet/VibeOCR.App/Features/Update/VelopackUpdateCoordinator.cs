using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using Velopack;
using Velopack.Exceptions;
using Velopack.Logging;

namespace VibeOCR.App.Features.Update;

internal sealed class VelopackUpdateCoordinator : IUpdateCoordinator
{
    internal const string PackId = "VibeOCRNext";
    internal const string Channel = "win";
    internal const string SetupFileName = "VibeOCRNext-Setup.exe";
    private readonly IReadOnlyList<VelopackFeedEndpoint> _feeds;
    private readonly string _cacheRoot;
    private UpdateManager? _selectedManager;
    private VelopackFeedEndpoint? _selectedLegacyFeed;
    private string? _selectedLegacyVersion;
    private UpdateInfo? _pendingUpdate;

    internal VelopackUpdateCoordinator(
        IReadOnlyList<VelopackFeedEndpoint> feeds,
        string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        if (feeds.Count == 0) throw new ArgumentException("At least one update feed is required.", nameof(feeds));
        _feeds = feeds;
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public static VelopackUpdateCoordinator Create(string configFile, string cacheRoot) =>
        new(VelopackFeedFactory.FromSettings(configFile), cacheRoot);

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        _selectedManager = null;
        _selectedLegacyFeed = null;
        _selectedLegacyVersion = null;
        _pendingUpdate = null;
        Exception? lastError = null;

        foreach (VelopackFeedEndpoint feed in _feeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manager = new UpdateManager(
                feed.CreateSource(),
                new UpdateOptions
                {
                    ExplicitChannel = Channel,
                    MaximumDeltasBeforeFallback = -1,
                });
            try
            {
                if (!manager.IsInstalled)
                {
                    VelopackAssetFeed remote = await feed.CreateSource()
                        .GetReleaseFeed(
                            NullVelopackLogger.Instance,
                            PackId,
                            Channel)
                        .WaitAsync(cancellationToken);
                    VelopackAsset? latest = remote.Assets
                        .Where(asset =>
                            asset.Type == VelopackAssetType.Full &&
                            string.Equals(asset.PackageId, PackId, StringComparison.Ordinal))
                        .OrderByDescending(asset => asset.Version)
                        .FirstOrDefault();
                    if (latest is null)
                    {
                        throw new InvalidDataException("The update feed has no VibeOCR Next full package.");
                    }
                    _selectedLegacyFeed = feed;
                    _selectedLegacyVersion = latest.Version.ToNormalizedString();
                    return new UpdateCheckResult(
                        UpdateCheckStatus.NotInstalled,
                        _selectedLegacyVersion,
                        latest.NotesMarkdown);
                }

                UpdateInfo? update = await manager.CheckForUpdatesAsync()
                    .WaitAsync(cancellationToken);
                _selectedManager = manager;
                _pendingUpdate = update;
                return update is null
                    ? new UpdateCheckResult(
                        UpdateCheckStatus.Latest,
                        manager.CurrentVersion?.ToNormalizedString())
                    : new UpdateCheckResult(
                        UpdateCheckStatus.Available,
                        update.TargetFullRelease.Version.ToNormalizedString(),
                        update.TargetFullRelease.NotesMarkdown);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (
                error is HttpRequestException or IOException or NotInstalledException or
                InvalidDataException)
            {
                lastError = error;
            }
        }

        return new UpdateCheckResult(
            UpdateCheckStatus.Error,
            ErrorMessage: lastError?.Message ?? "无法连接更新源。");
    }

    public async Task<UpdateApplyResult> DownloadAndApplyAsync(
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (_selectedLegacyFeed is not null)
        {
            return await DownloadAndLaunchSetupAsync(
                _selectedLegacyFeed,
                _selectedLegacyVersion!,
                progress,
                cancellationToken);
        }
        if (_selectedManager is null || _pendingUpdate is null)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, "请先检查更新。");
        }

        try
        {
            await _selectedManager.DownloadUpdatesAsync(
                _pendingUpdate,
                value => progress?.Report(value),
                cancellationToken);
            _selectedManager.WaitExitThenApplyUpdates(
                _pendingUpdate.TargetFullRelease,
                silent: false,
                restart: true,
                restartArgs: ["--profile", "production"]);
            return new UpdateApplyResult(UpdateApplyStatus.ApplyStarted);
        }
        catch (OperationCanceledException)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Cancelled);
        }
        catch (AcquireLockFailedException)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, "已有更新任务正在运行。");
        }
        catch (ChecksumFailedException)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, "更新包校验失败，请重试。");
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, error.Message);
        }
    }

    private async Task<UpdateApplyResult> DownloadAndLaunchSetupAsync(
        VelopackFeedEndpoint feed,
        string releaseVersion,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            string setupPath = Path.Combine(_cacheRoot, SetupFileName);
            string checksumText = await feed.Downloader.DownloadString(
                feed.AssetUri(SetupFileName + ".sha256", releaseVersion).AbsoluteUri);
            await feed.Downloader.DownloadFile(
                feed.AssetUri(SetupFileName, releaseVersion).AbsoluteUri,
                setupPath,
                value => progress?.Report(value),
                cancelToken: cancellationToken);
            string expected = checksumText.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)[0];
            await using FileStream input = File.OpenRead(setupPath);
            string actual = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(input, cancellationToken));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(setupPath);
                return new UpdateApplyResult(UpdateApplyStatus.Failed, "安装器校验失败，请重试。");
            }
            using Process? setup = Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true,
            });
            if (setup is null)
            {
                return new UpdateApplyResult(UpdateApplyStatus.Failed, "无法启动安装器。");
            }
            return new UpdateApplyResult(UpdateApplyStatus.ApplyStarted);
        }
        catch (OperationCanceledException)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Cancelled);
        }
        catch (Exception error) when (
            error is IOException or HttpRequestException or InvalidOperationException or Win32Exception)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, error.Message);
        }
    }
}
