using VibeOCR.App.Features.Maintenance;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ProductMaintenanceCoordinatorTests
{
    [Fact]
    public async Task CancelAndWaitHoldsUpdateUntilRuntimeLeaseReleases()
    {
        var coordinator = new ProductMaintenanceCoordinator();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable runtime = coordinator.Acquire(
            ProductMaintenanceOwner.RuntimeMaintenance,
            () => cancelled.TrySetResult());

        Task<bool> wait = coordinator.CancelRuntimeMaintenanceAndWaitAsync(
            TimeSpan.FromSeconds(2), CancellationToken.None);
        await cancelled.Task;
        Assert.True(coordinator.State.IsWaitingForRuntimeTermination);
        Assert.Throws<ProductMaintenanceConflictException>(
            () => coordinator.Acquire(ProductMaintenanceOwner.AppUpdate));

        runtime.Dispose();
        Assert.True(await wait);
        using IDisposable update = coordinator.Acquire(ProductMaintenanceOwner.AppUpdate);
    }

    [Fact]
    public void RuntimeLeaseBlocksUpdateAndReleasesAfterCancellation()
    {
        var coordinator = new ProductMaintenanceCoordinator();
        bool cancelled = false;

        using (coordinator.Acquire(
            ProductMaintenanceOwner.RuntimeMaintenance,
            () => cancelled = true))
        {
            ProductMaintenanceConflictException conflict = Assert.Throws<
                ProductMaintenanceConflictException>(() => coordinator.Acquire(
                    ProductMaintenanceOwner.AppUpdate));
            Assert.Equal(ProductMaintenanceOwner.RuntimeMaintenance, conflict.ActiveOwner);
            Assert.True(coordinator.State.CanCancelActive);
            Assert.True(coordinator.TryCancelRuntimeMaintenance());
            Assert.True(cancelled);
        }

        using IDisposable update = coordinator.Acquire(ProductMaintenanceOwner.AppUpdate);
        Assert.Equal(ProductMaintenanceOwner.AppUpdate, coordinator.State.ActiveOwner);
    }
}
