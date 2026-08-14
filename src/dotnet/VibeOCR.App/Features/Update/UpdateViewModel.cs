using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VibeOCR.App.Features.Update;

/// <summary>Maps stable update coordinator results to user-visible state.</summary>
public sealed class UpdateViewModel(IUpdateCoordinator coordinator, Action? requestShutdown = null)
    : INotifyPropertyChanged
{
    private readonly IUpdateCoordinator _coordinator = coordinator ??
        throw new ArgumentNullException(nameof(coordinator));
    private readonly Action _requestShutdown = requestShutdown ?? (() => { });
    private CancellationTokenSource? _activeRun;
    private bool _isBusy;
    private string _status = string.Empty;
    private string _statusCode = "update.current";
    private string? _latestVersion;
    private bool _updateAvailable;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string StatusCode { get => _statusCode; private set => SetField(ref _statusCode, value); }
    public string? LatestVersion { get => _latestVersion; private set => SetField(ref _latestVersion, value); }
    public bool UpdateAvailable { get => _updateAvailable; private set => SetField(ref _updateAvailable, value); }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeRun = run;
        IsBusy = true;
        Status = "正在检查更新";
        try
        {
            UpdateCheckResult result = await _coordinator.CheckAsync(run.Token);
            LatestVersion = result.Version;
            switch (result.Status)
            {
                case UpdateCheckStatus.Latest:
                    UpdateAvailable = false;
                    StatusCode = "update.current";
                    Status = "已是最新版本";
                    break;
                case UpdateCheckStatus.Available:
                    UpdateAvailable = true;
                    StatusCode = "update.available";
                    Status = $"发现新版本 {result.Version}";
                    break;
                case UpdateCheckStatus.Error:
                    UpdateAvailable = false;
                    StatusCode = "update.error";
                    Status = result.ErrorMessage ?? "检查更新失败，请检查网络";
                    break;
                default:
                    throw new InvalidOperationException("Unsupported update check result.");
            }
        }
        catch (OperationCanceledException)
        {
            StatusCode = "update.cancelled";
            Status = "已取消";
        }
        catch (Exception)
        {
            StatusCode = "update.error";
            Status = "检查更新失败，请检查网络";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DownloadAndApplyAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || !UpdateAvailable) return;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeRun = run;
        IsBusy = true;
        Status = "正在下载";
        try
        {
            UpdateApplyResult result = await _coordinator.DownloadAndApplyAsync(null, run.Token);
            switch (result.Status)
            {
                case UpdateApplyStatus.Downloaded:
                    StatusCode = "update.downloaded";
                    Status = "更新已下载";
                    break;
                case UpdateApplyStatus.ApplyStarted:
                    StatusCode = "update.applying";
                    Status = "更新器已就绪，应用即将退出";
                    _requestShutdown();
                    break;
                case UpdateApplyStatus.Cancelled:
                    StatusCode = "update.cancelled";
                    Status = "已取消";
                    break;
                case UpdateApplyStatus.Failed:
                    StatusCode = "update.error";
                    Status = result.ErrorMessage ?? "下载失败，请检查网络";
                    break;
                default:
                    throw new InvalidOperationException("Unsupported update apply result.");
            }
        }
        catch (OperationCanceledException)
        {
            StatusCode = "update.cancelled";
            Status = "已取消";
        }
        catch (Exception)
        {
            StatusCode = "update.error";
            Status = "下载失败，请检查网络";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cancel() => _activeRun?.Cancel();

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
