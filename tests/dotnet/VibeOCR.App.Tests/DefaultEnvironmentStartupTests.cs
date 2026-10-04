using VibeOCR.App.Features.Maintenance;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class DefaultEnvironmentStartupTests
{
  [Fact]
  public async Task DefaultInitializationCanBeCancelledBeforeAnUpdateAcquiresMaintenance()
  {
    var maintenance = new ProductMaintenanceCoordinator();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var manager = new DefaultManager(async token =>
    {
      started.SetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
      return new ManagedEnvironmentList(null, 0, []);
    });
    Task initialization = App.PrepareDefaultEnvironmentAsync(
      manager, maintenance, TestContext.Current.CancellationToken);
    await started.Task;
    Assert.Equal(ProductMaintenanceOwner.RuntimeMaintenance, maintenance.State.ActiveOwner);
    Assert.Throws<ProductMaintenanceConflictException>(() =>
      maintenance.Acquire(ProductMaintenanceOwner.AppUpdate));
    Assert.True(await maintenance.CancelRuntimeMaintenanceAndWaitAsync(
      TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
    Assert.True(maintenance.State.IsIdle);
    using IDisposable update = maintenance.Acquire(ProductMaintenanceOwner.AppUpdate);
    Assert.Equal(ProductMaintenanceOwner.AppUpdate, maintenance.State.ActiveOwner);
  }

  [Fact]
  public async Task FailedInitializationReleasesMaintenanceForRetry()
  {
    var maintenance = new ProductMaintenanceCoordinator();
    var manager = new DefaultManager(_ =>
      Task.FromException<ManagedEnvironmentList>(new InvalidOperationException("offline install failed")));
    await Assert.ThrowsAsync<InvalidOperationException>(() => App.PrepareDefaultEnvironmentAsync(
      manager, maintenance, TestContext.Current.CancellationToken));
    Assert.True(maintenance.State.IsIdle);
    using IDisposable retry = maintenance.Acquire(ProductMaintenanceOwner.RuntimeMaintenance);
  }

  [Theory]
  [InlineData(ProductMaintenanceOwner.AppUpdate)]
  [InlineData(ProductMaintenanceOwner.RuntimeMaintenance)]
  public async Task DefaultInitializationDoesNotCompeteWithActiveMaintenance(ProductMaintenanceOwner owner)
  {
    var maintenance = new ProductMaintenanceCoordinator();
    using IDisposable update = maintenance.Acquire(owner);
    bool called = false;
    var manager = new DefaultManager(_ =>
    {
      called = true;
      return Task.FromResult(new ManagedEnvironmentList(null, 0, []));
    });
    await App.PrepareDefaultEnvironmentAsync(
      manager, maintenance, TestContext.Current.CancellationToken);
    Assert.False(called);
    Assert.Equal(owner, maintenance.State.ActiveOwner);
  }

  private sealed class DefaultManager(Func<CancellationToken, Task<ManagedEnvironmentList>> initialize)
    : IManagedEnvironmentClient
  {
    public Task<ManagedEnvironmentList> InitializeDefaultEnvironmentAsync(CancellationToken cancellationToken = default) => initialize(cancellationToken);
    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironment> CreateEnvironmentAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(string? environmentId, string? packageSourceId, string? modelSourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironment> InstallEnvironmentAsync(ManagedEnvironmentPlan plan, IReadOnlyList<string>? sourceIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(string environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }
}
