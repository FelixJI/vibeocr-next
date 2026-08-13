namespace VibeOCR.App.Features.Update;

public interface IUpdateCoordinator
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);

    Task<UpdateApplyResult> DownloadAndApplyAsync(
        IProgress<int>? progress,
        CancellationToken cancellationToken);
}

public enum UpdateCheckStatus
{
    Latest,
    Available,
    NotInstalled,
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
