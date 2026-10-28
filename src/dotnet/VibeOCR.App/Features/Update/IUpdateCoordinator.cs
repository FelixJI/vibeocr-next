namespace VibeOCR.App.Features.Update;

public interface IUpdateCoordinator
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);

    Task<UpdateApplyResult> DownloadAndApplyAsync(
        IProgress<int>? progress,
        CancellationToken cancellationToken);

    /// <summary>读取当前更新源目录（候选选项 + 有效检测地址）。</summary>
    UpdateSourceCatalog LoadSources();

    /// <summary>
    /// 选择更新源并持久化：<c>sourceId</c> 为 <c>"direct"</c> 或
    /// <c>"custom"</c>（自定义需携带 <c>kind</c> 与 <c>url</c>）。切换后
    /// 既有检查结果立即失效，下载必须基于新源的重新检查。
    /// </summary>
    /// <exception cref="InvalidDataException">自定义源 URL/类型无效。</exception>
    UpdateSourceCatalog SelectSource(string sourceId, string? kind, string? url);
}

public enum UpdateCheckStatus
{
    Latest,
    Available,
    Error,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? Version = null,
    string? ReleaseNotes = null,
    string? ErrorMessage = null);

public enum UpdateApplyStatus
{
    Downloaded,
    ApplyStarted,
    Cancelled,
    Failed,
}

public sealed record UpdateApplyResult(
    UpdateApplyStatus Status,
    string? ErrorMessage = null);
