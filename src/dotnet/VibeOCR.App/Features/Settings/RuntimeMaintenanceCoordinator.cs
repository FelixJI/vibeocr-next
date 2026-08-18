using VibeOCR.App.ViewModels;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.Platform.Bootstrap;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.App.Features.Settings;

/// <summary>UI projection of the durable maintenance operation.</summary>
public sealed record RuntimeMaintenanceState(
    bool IsRunning,
    string StatusCode,
    string? OperationId,
    IReadOnlyList<string> RequestedComponentIds,
    IReadOnlyList<string> EffectiveComponentIds,
    IReadOnlyList<string> RequestedSourceIds,
    IReadOnlyList<string> EffectiveSourceIds,
    bool CanCancel,
    bool CanRetry)
{
    public static RuntimeMaintenanceState Idle { get; } = new(
        false,
        "idle",
        null,
        [],
        [],
        [],
        [],
        CanCancel: false,
        CanRetry: false);

    public static RuntimeMaintenanceState Unavailable { get; } = new(
        false,
        "unavailable",
        null,
        [],
        [],
        [],
        [],
        CanCancel: false,
        CanRetry: false);
}

/// <summary>
/// Orchestrates durable runtime maintenance operations on top of the existing
/// <see cref="IRuntimeInstallerClient"/>: explicit-intent ensure (including
/// base-only), cancellation through the durable v2 flow, and retry that
/// reuses the source operation's normalized intent unless a new install is
/// started. Requested/effective component ids from Backend snapshots are the
/// installation truth, including requested/effective source ids echoed by the
/// Runtime maintenance snapshot.
/// </summary>
public sealed class RuntimeMaintenanceCoordinator
{
    private readonly Func<IRuntimeInstallerClient> _installer;
    private readonly RuntimeStatusViewModel _runtimeStatus;
    private readonly ProductMaintenanceCoordinator _productMaintenance;
    private CancellationTokenSource? _active;
    private RuntimeMaintenanceState _state = RuntimeMaintenanceState.Idle;
    private string? _lastRetryableOperationId;

    public RuntimeMaintenanceCoordinator(
        Func<IRuntimeInstallerClient> installer,
        RuntimeStatusViewModel runtimeStatus,
        ProductMaintenanceCoordinator? productMaintenance = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _runtimeStatus = runtimeStatus ?? throw new ArgumentNullException(nameof(runtimeStatus));
        _productMaintenance = productMaintenance ?? new ProductMaintenanceCoordinator();
    }

    public RuntimeMaintenanceState State => _state;

    public event Action? StateChanged;

    /// <summary>
    /// Start an ensure operation with an explicit normalized intent. Empty
    /// feature selection sends an empty install list (base only); null/empty
    /// sources delegate to the Backend settings default.
    /// </summary>
    public async Task InstallAsync(
        RuntimeSelectionService selection,
        string accelerator,
        IReadOnlyCollection<string>? featureIds,
        IReadOnlyCollection<string>? sourceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        if (_state.IsRunning)
        {
            throw new InvalidOperationException("A runtime maintenance operation is already running.");
        }
        IReadOnlyList<string> componentIds = selection.SelectComponentIds(
            accelerator,
            featureIds is { Count: > 0 } ? featureIds : []);
        IReadOnlyList<string>? downloadSourceIds = sourceIds is { Count: > 0 }
            ? [.. sourceIds]
            : null;
        var intent = new RuntimeInstallSelection
        {
            InstallComponentIds = componentIds,
            DownloadSourceIds = downloadSourceIds,
        };
        string operationId = $"ui-{Guid.NewGuid():N}";
        _lastRetryableOperationId = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable productLease = _productMaintenance.Acquire(
            ProductMaintenanceOwner.RuntimeMaintenance,
            linked.Cancel);
        _active = linked;
        SetState(new RuntimeMaintenanceState(
            true,
            "running",
            operationId,
            componentIds,
            [],
            intent.DownloadSourceIds ?? [],
            [],
            CanCancel: true,
            CanRetry: false));
        IProgress<Host.RuntimeMaintenanceEvent> progress =
            new SynchronousProgress(ApplyEvent);
        try
        {
            IRuntimeInstallerClient installer = _installer();
            await installer.EnsureAsync(
                intent,
                operationId,
                progress,
                linked.Token).ConfigureAwait(false);
            ApplySources(installer.LastMaintenanceSources);
            SetState(new RuntimeMaintenanceState(
                false,
                "succeeded",
                operationId,
                _state.RequestedComponentIds,
                _state.EffectiveComponentIds,
                _state.RequestedSourceIds,
                _state.EffectiveSourceIds,
                CanCancel: false,
                CanRetry: false));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            SetState(new RuntimeMaintenanceState(
                false,
                "cancelled",
                operationId,
                _state.RequestedComponentIds,
                _state.EffectiveComponentIds,
                _state.RequestedSourceIds,
                _state.EffectiveSourceIds,
                CanCancel: false,
                CanRetry: true));
            _lastRetryableOperationId = operationId;
            throw;
        }
        catch (RuntimeInstallerException)
        {
            SetState(new RuntimeMaintenanceState(
                false,
                "failed",
                operationId,
                _state.RequestedComponentIds,
                _state.EffectiveComponentIds,
                _state.RequestedSourceIds,
                _state.EffectiveSourceIds,
                CanCancel: false,
                CanRetry: true));
            _lastRetryableOperationId = operationId;
            throw;
        }
        catch (Exception)
        {
            SetState(_state with { IsRunning = false, StatusCode = "failed", CanCancel = false, CanRetry = true });
            _lastRetryableOperationId = operationId;
            throw;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _active, null, linked), linked))
            {
                linked.Dispose();
            }
        }
    }

    /// <summary>Cancel the running operation; the client performs the durable cancel.</summary>
    public void Cancel() => _active?.Cancel();

    /// <summary>
    /// Retry the last failed/cancelled operation without a new selection; the
    /// omitted selection reuses the source operation's normalized intent.
    /// </summary>
    public async Task RetryAsync(CancellationToken cancellationToken)
    {
        string? sourceOperationId = _lastRetryableOperationId;
        if (_state.IsRunning || sourceOperationId is null)
        {
            throw new InvalidOperationException("No retryable runtime maintenance operation.");
        }
        string newOperationId = $"ui-{Guid.NewGuid():N}";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable productLease = _productMaintenance.Acquire(
            ProductMaintenanceOwner.RuntimeMaintenance,
            linked.Cancel);
        _active = linked;
        SetState(new RuntimeMaintenanceState(
            true,
            "running",
            newOperationId,
            _state.RequestedComponentIds,
            _state.EffectiveComponentIds,
            _state.RequestedSourceIds,
            _state.EffectiveSourceIds,
            CanCancel: true,
            CanRetry: false));
        try
        {
            RuntimeHostEnvelope envelope = await _installer().RetryAsync(
                sourceOperationId,
                newOperationId,
                selection: null,
                linked.Token).ConfigureAwait(false);
            Host.RuntimeMaintenanceSnapshot snapshot = envelope.Maintenance
                ?? throw new RuntimeInstallerException(
                    "Runtime retry returned no maintenance snapshot.");
            _lastRetryableOperationId = null;
            SetState(new RuntimeMaintenanceState(
                false,
                snapshot.OperationState == Host.RuntimeOperationState.Succeeded
                    ? "succeeded"
                    : "failed",
                newOperationId,
                snapshot.RequestedComponentIds ?? _state.RequestedComponentIds,
                snapshot.EffectiveComponentIds ?? [],
                envelope.MaintenanceSources?.RequestedSourceIds ?? _state.RequestedSourceIds,
                envelope.MaintenanceSources?.EffectiveSourceIds ?? [],
                CanCancel: false,
                CanRetry: snapshot.OperationState != Host.RuntimeOperationState.Succeeded));
            if (snapshot.OperationState != Host.RuntimeOperationState.Succeeded)
            {
                _lastRetryableOperationId = newOperationId;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            _lastRetryableOperationId = newOperationId;
            SetState(_state with { IsRunning = false, StatusCode = "cancelled", CanCancel = false, CanRetry = true });
            throw;
        }
        catch (Exception)
        {
            _lastRetryableOperationId = newOperationId;
            SetState(_state with { IsRunning = false, StatusCode = "failed", CanCancel = false, CanRetry = true });
            throw;
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _active, null, linked), linked))
            {
                linked.Dispose();
            }
        }
    }

    /// <summary>
    /// Applies events inline: operation state must reflect every snapshot
    /// before the installer call returns, independent of any sync context.
    /// </summary>
    private sealed class SynchronousProgress(
        Action<Host.RuntimeMaintenanceEvent> handler) : IProgress<Host.RuntimeMaintenanceEvent>
    {
        public void Report(Host.RuntimeMaintenanceEvent value) => handler(value);
    }

    private void ApplyEvent(Host.RuntimeMaintenanceEvent update)
    {
        _runtimeStatus.ApplyMaintenance(update);
        Host.RuntimeMaintenanceSnapshot snapshot = update.Snapshot;
        SetState(_state with
        {
            RequestedComponentIds =
                snapshot.RequestedComponentIds ?? _state.RequestedComponentIds,
            EffectiveComponentIds =
                snapshot.EffectiveComponentIds ?? _state.EffectiveComponentIds,
        });
    }

    private void SetState(RuntimeMaintenanceState value)
    {
        _state = value;
        StateChanged?.Invoke();
    }

    private void ApplySources(RuntimeMaintenanceSourceSnapshot? sources)
    {
        if (sources is null) return;
        SetState(_state with
        {
            RequestedSourceIds = sources.RequestedSourceIds,
            EffectiveSourceIds = sources.EffectiveSourceIds,
        });
    }
}
