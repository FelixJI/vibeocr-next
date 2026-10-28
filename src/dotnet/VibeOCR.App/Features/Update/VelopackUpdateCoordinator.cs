using Velopack;
using Velopack.Exceptions;

namespace VibeOCR.App.Features.Update;

internal sealed class VelopackUpdateCoordinator : IUpdateCoordinator
{
    internal const string PackId = "VibeOCRNext";
    internal const string Channel = "win";
    internal const int MaximumDeltasBeforeFallback = 1;
    private readonly Action? _verifyWritableRoot;
    private readonly string? _configFile;
    private readonly object _sourceGate = new();
    private VelopackFeedEndpoint[] _feeds;
    private VelopackFeedEndpoint? _selectedFeed;
    private UpdateManager? _selectedManager;
    private UpdateInfo? _pendingUpdate;

    /// <summary>
    /// 固定 feed 构造（自检/测试）：不支持运行中改源；检查与下载仍绑定
    /// 同一组 feed。
    /// </summary>
    internal VelopackUpdateCoordinator(
        IReadOnlyList<VelopackFeedEndpoint> feeds,
        Action? verifyWritableRoot = null)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        if (feeds.Count == 0)
        {
            throw new ArgumentException("At least one update feed is required.", nameof(feeds));
        }
        _feeds = [.. feeds];
        _verifyWritableRoot = verifyWritableRoot;
    }

    /// <summary>配置驱动构造（生产）：feed 随 <c>update.selected</c> 解析。</summary>
    internal VelopackUpdateCoordinator(string configFile, Action? verifyWritableRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFile);
        _configFile = configFile;
        _verifyWritableRoot = verifyWritableRoot;
        ResolvedUpdateFeeds resolved = VelopackFeedFactory.Resolve(configFile);
        _feeds = [resolved.Feed];
    }

    public static VelopackUpdateCoordinator Create(
        string configFile,
        Action? verifyWritableRoot = null) => new(configFile, verifyWritableRoot);

    public UpdateSourceCatalog LoadSources()
    {
        if (_configFile is null)
        {
            return new([DirectOption()], VelopackFeedFactory.DirectBaseUrl);
        }
        ResolvedUpdateFeeds resolved = VelopackFeedFactory.Resolve(_configFile);
        return new(resolved.Options, resolved.DetectionEndpoint, resolved.Error);
    }

    public UpdateSourceCatalog SelectSource(string sourceId, string? kind, string? url)
    {
        if (_configFile is null)
        {
            throw new NotSupportedException("当前更新通道不支持更改更新源。");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (sourceId is not (ResolvedUpdateFeeds.DirectId or ResolvedUpdateFeeds.CustomId))
        {
            throw new InvalidDataException($"未知更新源：{sourceId}。");
        }
        VelopackFeedFactory.SaveSelection(_configFile, sourceId, kind, url);
        ResolvedUpdateFeeds resolved;
        lock (_sourceGate)
        {
            resolved = VelopackFeedFactory.Resolve(_configFile);
            _feeds = [resolved.Feed];
            // 切源后旧检查结果不能继续用于下载；下载必须基于新源重新检查。
            _selectedFeed = null;
            _selectedManager = null;
            _pendingUpdate = null;
        }
        return new(resolved.Options, resolved.DetectionEndpoint, resolved.Error);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        lock (_sourceGate)
        {
            _selectedFeed = null;
            _selectedManager = null;
            _pendingUpdate = null;
        }
        Exception? lastError = null;

        VelopackFeedEndpoint[] feeds;
        lock (_sourceGate)
        {
            feeds = [.. _feeds];
        }

        foreach (VelopackFeedEndpoint feed in feeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Portable 版同样位于 Velopack 布局(默认 locator 可发现
            // Update.exe/sq.version);仅在完全脱离 Velopack 上下文(开发
            // 运行)时构造/检查抛 InvalidOperationException,交给下方分支
            // 报告,不做硬拒绝。
            try
            {
                var manager = new UpdateManager(
                    feed.CreateSource(),
                    new UpdateOptions
                    {
                        ExplicitChannel = Channel,
                        MaximumDeltasBeforeFallback = MaximumDeltasBeforeFallback,
                    });
                UpdateInfo? update = await manager.CheckForUpdatesAsync()
                    .WaitAsync(cancellationToken);
                lock (_sourceGate)
                {
                    _selectedFeed = feed;
                    _selectedManager = manager;
                    _pendingUpdate = update;
                }
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
            catch (InvalidOperationException)
            {
                // 开发运行脱离 Velopack 布局（无 VelopackLocator）：按未安装
                // 报告可理解错误，不抛出到命令层。
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    ErrorMessage: "当前安装不是 Velopack 更新布局，无法应用内检查更新；请从发布包安装后重试。");
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
        VelopackFeedEndpoint[] feeds;
        UpdateManager? manager;
        UpdateInfo? pending;
        lock (_sourceGate)
        {
            feeds = [.. _feeds];
            manager = _selectedManager;
            pending = _pendingUpdate;
        }
        if (manager is null || pending is null)
        {
            return new UpdateApplyResult(UpdateApplyStatus.Failed, "请先检查更新。");
        }
        if (_selectedFeed is not { } checkedFeed ||
            !feeds.Any(feed => ReferenceEquals(feed, checkedFeed)))
        {
            // 防御：检查所用的源已不是当前选定源（例如并发切源）。
            return new UpdateApplyResult(UpdateApplyStatus.Failed, "更新源已切换，请重新检查更新。");
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
            await manager.DownloadUpdatesAsync(
                pending,
                value => progress?.Report(value),
                cancellationToken);
            manager.WaitExitThenApplyUpdates(
                pending.TargetFullRelease,
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

    private static UpdateSourceOption DirectOption() => new(
        ResolvedUpdateFeeds.DirectId,
        "direct",
        "官方直连（GitHub）",
        VelopackFeedFactory.DirectBaseUrl,
        Selected: true);
}
