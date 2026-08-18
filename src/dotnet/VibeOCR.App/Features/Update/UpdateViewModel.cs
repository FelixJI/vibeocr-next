using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeOCR.App.Features.Maintenance;

namespace VibeOCR.App.Features.Update;

/// <summary>Maps stable update coordinator results to user-visible state.</summary>
public sealed class UpdateViewModel(
    IUpdateCoordinator coordinator,
    Action? requestShutdown = null,
    ProductMaintenanceCoordinator? productMaintenance = null)
    : INotifyPropertyChanged
{
    private readonly IUpdateCoordinator _coordinator = coordinator ??
        throw new ArgumentNullException(nameof(coordinator));
    private readonly Action _requestShutdown = requestShutdown ?? (() => { });
    private readonly ProductMaintenanceCoordinator _productMaintenance =
        productMaintenance ?? new ProductMaintenanceCoordinator();
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
    public bool CanCancelRuntimeMaintenance =>
        _productMaintenance.State.ActiveOwner == ProductMaintenanceOwner.RuntimeMaintenance &&
        _productMaintenance.State.CanCancelActive;

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
            using IDisposable productLease = _productMaintenance.Acquire(
                ProductMaintenanceOwner.AppUpdate);
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
        catch (ProductMaintenanceConflictException conflict)
        {
            StatusCode = "update.runtimeBusy";
            Status = conflict.ActiveOwner == ProductMaintenanceOwner.RuntimeMaintenance
                ? "运行时维护正在进行；请等待完成，或在设置中取消安装后再更新"
                : "已有应用更新任务正在进行";
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

    /// <summary>Lets update UI cancel Runtime and wait for its lease to release.</summary>
    public async Task CancelRuntimeMaintenanceAndWaitAsync(CancellationToken cancellationToken)
    {
        StatusCode = "update.waitingRuntime";
        Status = "正在取消运行时维护并等待其安全退出";
        try
        {
            bool released = await _productMaintenance.CancelRuntimeMaintenanceAndWaitAsync(
                TimeSpan.FromMinutes(2), cancellationToken);
            StatusCode = released ? "update.runtimeReleased" : "update.runtimeBusy";
            Status = released ? "运行时维护已退出，可以继续更新" : "没有可取消的运行时维护";
        }
        catch (OperationCanceledException)
        {
            StatusCode = "update.cancelled";
            Status = "已取消等待运行时维护退出";
        }
        catch (TimeoutException)
        {
            StatusCode = "update.runtimeBusy";
            Status = "运行时维护未在限定时间内退出，未开始应用更新";
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
