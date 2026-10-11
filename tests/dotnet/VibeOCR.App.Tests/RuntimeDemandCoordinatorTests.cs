using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class RuntimeDemandCoordinatorTests
{
  [Fact]
  public async Task ConcurrentDemandsSharePreparationAndCancelIndependently()
  {
    int calls = 0;
    var finish = Signal();
    var maintenance = new ProductMaintenanceCoordinator();
    var coordinator = new RuntimeDemandCoordinator(async (_, token) =>
    {
      calls++;
      await finish.Task.WaitAsync(token);
    }, () => Task.CompletedTask, maintenance, TestContext.Current.CancellationToken);
    Assert.Equal(0, calls);
    using var firstCancellation = new CancellationTokenSource();
    Task first = coordinator.EnsureReadyAsync(firstCancellation.Token);
    Task second = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Assert.Equal(1, calls);
    firstCancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    Assert.False(second.IsCompleted);
    finish.SetResult();
    await second;
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Assert.Equal(1, calls);
    Assert.True(maintenance.State.IsIdle);
  }

  [Fact]
  public async Task FinalCancellationWaitsForCleanupAndKeepsLeaseUntilCleanupFinishes()
  {
    var cleanupStarted = Signal();
    var cleanupFinished = Signal();
    var maintenance = new ProductMaintenanceCoordinator();
    int calls = 0;
    var coordinator = new RuntimeDemandCoordinator(async (_, token) =>
    {
      if (++calls == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }, async () =>
    {
      cleanupStarted.TrySetResult();
      await cleanupFinished.Task;
    }, maintenance, TestContext.Current.CancellationToken);
    using var cancellation = new CancellationTokenSource();
    Task first = coordinator.EnsureReadyAsync(cancellation.Token);
    cancellation.Cancel();
    await cleanupStarted.Task;
    Assert.False(first.IsCompleted);
    Assert.Equal(ProductMaintenanceOwner.RuntimeMaintenance, maintenance.State.ActiveOwner);
    Assert.Throws<ProductMaintenanceConflictException>(() => maintenance.Acquire(ProductMaintenanceOwner.AppUpdate));
    Task next = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Assert.Equal(1, calls);
    cleanupFinished.SetResult();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    await next;
    Assert.Equal(2, calls);
    Assert.True(maintenance.State.IsIdle);
  }

  [Fact]
  public async Task CancellationBeforeCommitCannotPublishReady()
  {
    using var cancellation = new CancellationTokenSource();
    int calls = 0;
    int disconnects = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) =>
    {
      if (++calls == 1) cancellation.Cancel();
      return Task.CompletedTask;
    }, () => { disconnects++; return Task.CompletedTask; },
      new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken);
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.EnsureReadyAsync(cancellation.Token));
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Assert.Equal(2, calls);
    Assert.Equal(1, disconnects);
  }

  [Fact]
  public async Task MaintenanceAndRecoveryReuseEnabledRuntimeWithoutDefaultInitialization()
  {
    List<bool> initializes = [];
    var maintenance = new ProductMaintenanceCoordinator();
    var coordinator = new RuntimeDemandCoordinator((initialize, _) =>
    {
      initializes.Add(initialize);
      return Task.CompletedTask;
    }, () => Task.CompletedTask, maintenance, TestContext.Current.CancellationToken);
    await coordinator.PauseAsync(TestContext.Current.CancellationToken);
    await coordinator.RestoreAsync();
    Assert.Empty(initializes);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    using (maintenance.Acquire(ProductMaintenanceOwner.RuntimeMaintenance))
    {
      await coordinator.PauseAsync(TestContext.Current.CancellationToken);
      await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
      await coordinator.RestoreAsync(maintenanceLeaseHeld: true);
    }
    await coordinator.RecoverAsync();
    Assert.Equal([true, false, false], initializes);
  }

  [Theory]
  [InlineData(ProductMaintenanceOwner.AppUpdate)]
  [InlineData(ProductMaintenanceOwner.RuntimeMaintenance)]
  public async Task CompetingMaintenanceDoesNotDisconnectItsResources(ProductMaintenanceOwner owner)
  {
    var maintenance = new ProductMaintenanceCoordinator();
    int disconnects = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) => throw new Xunit.Sdk.XunitException("unexpected connection"),
      () => { disconnects++; return Task.CompletedTask; }, maintenance, TestContext.Current.CancellationToken);
    using IDisposable lease = maintenance.Acquire(owner);
    await Assert.ThrowsAsync<ProductMaintenanceConflictException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
    Assert.Equal(0, disconnects);
  }

  [Fact]
  public async Task UpdateCancellationWaitsForPreparationLeaseAndCleanup()
  {
    var maintenance = new ProductMaintenanceCoordinator();
    var entered = Signal();
    int cleanups = 0;
    var coordinator = new RuntimeDemandCoordinator(async (_, ct) =>
    {
      entered.TrySetResult();
      await Task.Delay(Timeout.Infinite, ct);
    }, () => { cleanups++; return Task.CompletedTask; }, maintenance, TestContext.Current.CancellationToken);
    Task request = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    await entered.Task;
    Assert.Throws<ProductMaintenanceConflictException>(() => maintenance.Acquire(ProductMaintenanceOwner.AppUpdate));
    Assert.True(await maintenance.CancelRuntimeMaintenanceAndWaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    Assert.Equal(1, cleanups);
    using IDisposable update = maintenance.Acquire(ProductMaintenanceOwner.AppUpdate);
  }

  [Fact]
  public async Task FailedPreparationReleasesLeaseAndNextDemandCanRetry()
  {
    var maintenance = new ProductMaintenanceCoordinator();
    int attempts = 0;
    int cleanups = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) =>
      ++attempts == 1 ? Task.FromException(new InvalidOperationException("offline install failed")) : Task.CompletedTask,
      () => { cleanups++; return Task.CompletedTask; }, maintenance, TestContext.Current.CancellationToken);
    await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
    Assert.True(maintenance.State.IsIdle);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Assert.Equal(2, attempts);
    Assert.Equal(1, cleanups);
    Assert.True(maintenance.State.IsIdle);
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  [InlineData(true, true)]
  public async Task PauseAndStopDrainFailedSwitchAndPreserveDisconnectFailure(bool stop, bool failDisconnect)
  {
    var entered = Signal();
    var release = Signal();
    int disconnects = 0;
    var disconnectFailure = new IOException("disconnect failed");
    var coordinator = new RuntimeDemandCoordinator((_, _) => Task.CompletedTask,
      () => { disconnects++; return failDisconnect ? Task.FromException(disconnectFailure) : Task.CompletedTask; },
      new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken);
    Task switching = coordinator.SwitchAsync(async _ =>
    {
      entered.TrySetResult();
      await release.Task;
      throw new InvalidOperationException("switch failed before commit");
    }, TestContext.Current.CancellationToken);
    await entered.Task;
    Task pausing = stop ? coordinator.StopAsync() : coordinator.PauseAsync(TestContext.Current.CancellationToken);
    release.TrySetResult();
    await Assert.ThrowsAsync<InvalidOperationException>(() => switching);
    if (failDisconnect)
      Assert.Same(disconnectFailure, await Assert.ThrowsAsync<IOException>(() => pausing));
    else
      await pausing;
    Assert.Equal(1, disconnects);
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  [InlineData(true, true)]
  public async Task PauseAndStopPreserveAttemptCleanupFailureAfterSessionIsCleared(bool stop, bool failSecondDisconnect)
  {
    var entered = Signal();
    var release = Signal();
    var firstFailure = new IOException("attempt cleanup failed after clearing session");
    var secondFailure = new IOException("pause disconnect failed");
    var maintenance = new ProductMaintenanceCoordinator();
    bool attached = false;
    int disconnects = 0;
    var coordinator = new RuntimeDemandCoordinator(async (_, _) =>
    {
      attached = true;
      entered.TrySetResult();
      await release.Task; // Attach has finished; Pause cancels before coordinator commit.
    }, () =>
    {
      if (++disconnects == 1)
      {
        attached = false; // ManagedRuntimeConnection publishes null before disposal.
        return Task.FromException(firstFailure);
      }
      Assert.False(attached);
      return failSecondDisconnect ? Task.FromException(secondFailure) : Task.CompletedTask;
    }, maintenance, TestContext.Current.CancellationToken);
    Task demand = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    await entered.Task;
    Task pausing = stop ? coordinator.StopAsync() : coordinator.PauseAsync(TestContext.Current.CancellationToken);
    release.TrySetResult();
    AggregateException demandFailure = await Assert.ThrowsAsync<AggregateException>(() => demand);
    Assert.Contains(firstFailure, demandFailure.InnerExceptions);
    if (failSecondDisconnect)
    {
      AggregateException pauseFailure = await Assert.ThrowsAsync<AggregateException>(() => pausing);
      Assert.Collection(pauseFailure.InnerExceptions,
        error => Assert.Same(firstFailure, error), error => Assert.Same(secondFailure, error));
    }
    else
      Assert.Same(firstFailure, await Assert.ThrowsAsync<IOException>(() => pausing));
    Assert.Equal(2, disconnects);
    Assert.False(attached);
    Assert.True(maintenance.State.IsIdle);
  }

  [Fact]
  public async Task DemandSharingEmptyEnvironmentSwitchReportsNotReadyWithoutRetry()
  {
    var entered = Signal();
    var release = Signal();
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-demand-empty-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    var manager = new RuntimeInstallerClient(RuntimeInstallerConfiguration.ForNext(layout));
    var connection = new ManagedRuntimeConnection(manager, layout, new RuntimeStatusViewModel(), (_, _) => { });
    int connects = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) => { connects++; return Task.CompletedTask; },
      connection.DisconnectAsync, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken, connection);
    Task switching = coordinator.SwitchAsync(async _ =>
    {
      entered.TrySetResult();
      await release.Task;
    }, TestContext.Current.CancellationToken);
    await entered.Task;
    Task demand = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    release.TrySetResult();
    await switching;
    await Assert.ThrowsAsync<InvalidOperationException>(() => demand);
    Assert.Null(connection.Session);
    Assert.Equal(0, connects);
  }

  [Theory]
  [InlineData("ensure")]
  [InlineData("switch")]
  [InlineData("recover")]
  public async Task ShellOnlyRejectsAllComputingLifecycleEntries(string operation)
  {
    int connects = 0;
    int switches = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) => { connects++; return Task.CompletedTask; },
      () => Task.CompletedTask, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken,
      shellOnly: true);
    Task action = operation switch
    {
      "ensure" => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken),
      "switch" => coordinator.SwitchAsync(_ => { switches++; return Task.CompletedTask; }, TestContext.Current.CancellationToken),
      _ => coordinator.RecoverAsync(),
    };
    await Assert.ThrowsAsync<InvalidOperationException>(() => action);
    Assert.Equal(0, connects);
    Assert.Equal(0, switches);
  }

  private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

  [Fact]
  public async Task CompletionAndReentrantDemandWaitForLeaseReleaseNotification()
  {
    var maintenance = new ProductMaintenanceCoordinator();
    var finish = Signal();
    Task? request = null;
    Task? reentrant = null;
    int connections = 0;
    var coordinator = new RuntimeDemandCoordinator(async (_, ct) =>
    {
      connections++;
      await finish.Task.WaitAsync(ct);
    }, () => Task.CompletedTask, maintenance, TestContext.Current.CancellationToken);
    maintenance.StateChanged += () =>
    {
      if (!maintenance.State.IsIdle) return;
      Assert.False(request!.IsCompleted);
      reentrant = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
      Assert.False(reentrant.IsCompleted);
    };
    request = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    finish.TrySetResult();
    await request;
    await Assert.IsAssignableFrom<Task>(reentrant);
    Assert.Equal(1, connections);
    Assert.True(maintenance.State.IsIdle);
  }
}
