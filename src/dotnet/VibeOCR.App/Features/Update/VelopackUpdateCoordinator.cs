using Velopack;
using Velopack.Exceptions;

namespace VibeOCR.App.Features.Update;

internal sealed class VelopackUpdateCoordinator : IUpdateCoordinator
{
    internal const string PackId = "VibeOCRNext";
    internal const string Channel = "win";
    internal const int MaximumDeltasBeforeFallback = 1;
    private readonly IReadOnlyList<VelopackFeedEndpoint> _feeds;
    private readonly Action? _verifyWritableRoot;
    private UpdateManager? _selectedManager;
    private UpdateInfo? _pendingUpdate;

    internal VelopackUpdateCoordinator(
        IReadOnlyList<VelopackFeedEndpoint> feeds,
        Action? verifyWritableRoot = null)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        if (feeds.Count == 0)
        {
            throw new ArgumentException("At least one update feed is required.", nameof(feeds));
        }
        _feeds = feeds;
        _verifyWritableRoot = verifyWritableRoot;
    }

    public static VelopackUpdateCoordinator Create(
        string configFile,
        Action? verifyWritableRoot = null) =>
        new(VelopackFeedFactory.FromSettings(configFile), verifyWritableRoot);

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
                    MaximumDeltasBeforeFallback = MaximumDeltasBeforeFallback,
                });
            // Portable 版同样位于 Velopack 布局(默认 locator 可发现
            // Update.exe/sq.version);仅在完全脱离 Velopack 上下文(开发
            // 运行)时 IsInstalled 为 false,交给 NotInstalledException 分支
            // 报告,不做硬拒绝。
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
            // 应用更新前再次验证便携根可写,防止更新缓存回落用户目录。
            if (_verifyWritableRoot is not null)
            {
                try
                {
                    _verifyWritableRoot();
                }
                catch (Exception error) when (
                    error is InvalidOperationException or IOException or
                    UnauthorizedAccessException)
                {
                    return new UpdateApplyResult(
                        UpdateApplyStatus.Failed,
                        $"便携目录不可写，无法应用更新：{error.Message}");
                }
            }
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
