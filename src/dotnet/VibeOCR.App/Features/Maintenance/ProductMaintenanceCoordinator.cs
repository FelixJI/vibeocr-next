namespace VibeOCR.App.Features.Maintenance;

/// <summary>Exclusive owner of product-changing work, independent of any UI page.</summary>
public enum ProductMaintenanceOwner
{
    RuntimeMaintenance,
    AppUpdate,
}

/// <summary>Published, UI-neutral state for update/runtime interlocking.</summary>
public sealed record ProductMaintenanceState(
    ProductMaintenanceOwner? ActiveOwner,
    bool CanCancelActive = false,
    bool IsWaitingForRuntimeTermination = false)
{
    public static ProductMaintenanceState Idle { get; } = new((ProductMaintenanceOwner?)null);
    public bool IsIdle => ActiveOwner is null;
}

/// <summary>Raised before a competing product-maintenance operation starts.</summary>
public sealed class ProductMaintenanceConflictException(ProductMaintenanceOwner activeOwner)
    : InvalidOperationException($"Product maintenance is already owned by {activeOwner}.")
{
    public ProductMaintenanceOwner ActiveOwner { get; } = activeOwner;
}

/// <summary>
/// A small process-lifetime gate for Runtime maintenance and Velopack apply.
/// A lease is held for the whole durable operation, not for the lifetime of a
/// settings/update page, so cancellation, faults, and shutdown cannot leave a
/// stale owner behind.
/// </summary>
public sealed class ProductMaintenanceCoordinator
{
    private readonly object _gate = new();
    private ProductMaintenanceOwner? _activeOwner;
    private Action? _activeCancel;
    private TaskCompletionSource? _activeCompletion;
    private bool _isWaitingForRuntimeTermination;

    public ProductMaintenanceState State
    {
        get
        {
            lock (_gate)
            {
                return new ProductMaintenanceState(
                    _activeOwner,
                    _activeCancel is not null,
                    _isWaitingForRuntimeTermination);
            }
        }
    }

    public event Action? StateChanged;

    public IDisposable Acquire(ProductMaintenanceOwner owner, Action? cancelActiveOperation = null)
    {
        lock (_gate)
        {
            if (_activeOwner is { } active)
            {
                throw new ProductMaintenanceConflictException(active);
            }
            _activeOwner = owner;
            _activeCancel = cancelActiveOperation;
            _activeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        StateChanged?.Invoke();
        return new Lease(this, owner);
    }

    /// <summary>Requests cancellation only for an active Runtime operation.</summary>
    public bool TryCancelRuntimeMaintenance()
    {
        Action? cancel;
        lock (_gate)
        {
            if (_activeOwner != ProductMaintenanceOwner.RuntimeMaintenance)
            {
                return false;
            }
            cancel = _activeCancel;
        }
        cancel?.Invoke();
        return cancel is not null;
    }

    /// <summary>
    /// Requests Runtime cancellation and waits until its owning operation has
    /// released the durable lease. The caller can surface this state instead
    /// of racing apply/restart against a still-exiting child process.
    /// </summary>
    public async Task<bool> CancelRuntimeMaintenanceAndWaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Action? cancel;
        Task completion;
        lock (_gate)
        {
            if (_activeOwner != ProductMaintenanceOwner.RuntimeMaintenance ||
                _activeCancel is null || _activeCompletion is null)
            {
                return false;
            }
            _isWaitingForRuntimeTermination = true;
            cancel = _activeCancel;
            completion = _activeCompletion.Task;
        }
        StateChanged?.Invoke();
        cancel();
        try
        {
            await completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            lock (_gate)
            {
                _isWaitingForRuntimeTermination = false;
            }
            StateChanged?.Invoke();
        }
    }

    private void Release(ProductMaintenanceOwner owner)
    {
        lock (_gate)
        {
            if (_activeOwner != owner)
            {
                throw new InvalidOperationException("Product maintenance lease ownership was lost.");
            }
            _activeOwner = null;
            _activeCancel = null;
            _activeCompletion?.TrySetResult();
            _activeCompletion = null;
        }
        StateChanged?.Invoke();
    }

    private sealed class Lease(ProductMaintenanceCoordinator owner, ProductMaintenanceOwner operation)
        : IDisposable
    {
        private ProductMaintenanceCoordinator? _owner = owner;

        public void Dispose()
        {
            ProductMaintenanceCoordinator? current = Interlocked.Exchange(ref _owner, null);
            current?.Release(operation);
        }
    }
}
