using Velopack;
using Velopack.Exceptions;

namespace VibeOCR.App.Features.Update;

internal sealed class VelopackUpdateCoordinator : IUpdateCoordinator
{
    internal const string PackId = "VibeOCRNext";
    internal const string Channel = "win";
    private readonly IReadOnlyList<VelopackFeedEndpoint> _feeds;
    private UpdateManager? _selectedManager;
    private UpdateInfo? _pendingUpdate;

    internal VelopackUpdateCoordinator(IReadOnlyList<VelopackFeedEndpoint> feeds)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        if (feeds.Count == 0)
        {
            throw new ArgumentException("At least one update feed is required.", nameof(feeds));
        }
        _feeds = feeds;
    }

    public static VelopackUpdateCoordinator Create(string configFile) =>
        new(VelopackFeedFactory.FromSettings(configFile));

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        _selectedManager = null;
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
            if (!manager.IsInstalled)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    ErrorMessage: "便携版不支持自动更新，请手动下载新版 Setup 或 Portable。");
            }

            try
            {
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
        catch (Exception error) when (
            error is IOException or HttpRequestException or InvalidOperationException)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, error.Message);
        }
    }
}
