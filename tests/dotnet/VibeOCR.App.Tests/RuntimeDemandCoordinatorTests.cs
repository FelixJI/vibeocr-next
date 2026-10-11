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
    long firstOwner = await coordinator.PauseAsync(TestContext.Current.CancellationToken);
    await coordinator.RestoreAsync(firstOwner);
    Assert.Empty(initializes);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    using (maintenance.Acquire(ProductMaintenanceOwner.RuntimeMaintenance))
    {
      long owner = await coordinator.PauseAsync(TestContext.Current.CancellationToken);
      await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
      await coordinator.RestoreAsync(owner, maintenanceLeaseHeld: true);
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

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  public async Task SwitchDrainsFailedPreparationAndKeepsCleanupFailure(bool installerFailure, bool failCleanup)
  {
    var entered = Signal();
    var release = Signal();
    Exception businessFailure = installerFailure ? new RuntimeInstallerException("old installer failed")
      : new InvalidOperationException("old preparation failed");
    var cleanupFailure = new IOException("old attempt cleanup failed");
    var maintenance = new ProductMaintenanceCoordinator();
    int connects = 0;
    int activations = 0;
    int disconnects = 0;
    var coordinator = new RuntimeDemandCoordinator(async (_, _) =>
    {
      connects++;
      entered.TrySetResult();
      await release.Task;
      throw businessFailure;
    }, () =>
    {
      disconnects++;
      return failCleanup ? Task.FromException(cleanupFailure) : Task.CompletedTask;
    }, maintenance, TestContext.Current.CancellationToken);
    Task demand = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    await entered.Task;
    Task switching = coordinator.SwitchAsync(_ =>
    {
      activations++;
      Assert.Equal(1, disconnects);
      return Task.CompletedTask;
    }, TestContext.Current.CancellationToken);
    release.TrySetResult();
    Exception requestFailure = await Assert.ThrowsAnyAsync<Exception>(() => demand);
    if (failCleanup)
    {
      Assert.Collection(Assert.IsType<AggregateException>(requestFailure).InnerExceptions,
        error => Assert.Same(businessFailure, error), error => Assert.Same(cleanupFailure, error));
      Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => switching));
      Assert.Equal(0, activations);
    }
    else
    {
      Assert.Same(businessFailure, requestFailure);
      await switching;
      Assert.Equal(1, activations);
    }
    Assert.Equal(1, connects);
    Assert.Equal(1, disconnects);
    Assert.True(maintenance.State.IsIdle);
  }

  [Theory]
  [InlineData("caller")]
  [InlineData("shutdown")]
  [InlineData("pause")]
  public async Task SwitchDuringFailedPreparationDrainHonorsNewLifecycleOwner(string boundary)
  {
    var entered = Signal();
    var release = Signal();
    using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    using var caller = new CancellationTokenSource();
    var businessFailure = new InvalidOperationException("old preparation failed");
    int activations = 0;
    var coordinator = new RuntimeDemandCoordinator(async (_, _) =>
    {
      entered.TrySetResult();
      await release.Task;
      throw businessFailure;
    }, () => Task.CompletedTask, new ProductMaintenanceCoordinator(), shutdown.Token);
    Task demand = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    await entered.Task;
    Task switching = coordinator.SwitchAsync(_ => { activations++; return Task.CompletedTask; }, caller.Token);
    Task? pausing = null;
    if (boundary == "caller") caller.Cancel();
    else if (boundary == "shutdown") shutdown.Cancel();
    else pausing = coordinator.PauseAsync(TestContext.Current.CancellationToken);
    release.TrySetResult();
    if (boundary == "shutdown")
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => demand);
    else
      Assert.Same(businessFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => demand));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switching);
    Assert.Equal(0, activations);
    if (pausing is not null)
    {
      await pausing;
      await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
    }
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

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task StaleMaintenanceRestoreCannotResumeNewPauseOrStop(bool stop)
  {
    int connects = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) => { connects++; return Task.CompletedTask; },
      () => Task.CompletedTask, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    long maintenanceOwner = await coordinator.PauseAsync(TestContext.Current.CancellationToken);
    if (stop) await coordinator.StopAsync();
    else await coordinator.PauseAsync(TestContext.Current.CancellationToken);

    await coordinator.RestoreAsync(maintenanceOwner);

    Assert.Equal(1, connects);
    await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task StaleMaintenanceRestoreDoesNotJoinExplicitSwitchAttempt()
  {
    var entered = Signal();
    var release = Signal();
    int connects = 0;
    int activations = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) => { connects++; return Task.CompletedTask; },
      () => Task.CompletedTask, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    long maintenanceOwner = await coordinator.PauseAsync(TestContext.Current.CancellationToken);
    Task switching = coordinator.SwitchAsync(async _ =>
    {
      activations++;
      entered.TrySetResult();
      await release.Task;
    }, TestContext.Current.CancellationToken);
    try
    {
      await entered.Task;
      Task restoring = coordinator.RestoreAsync(maintenanceOwner);
      Assert.True(restoring.IsCompletedSuccessfully);
      await restoring;
      Assert.False(switching.IsCompleted);
    }
    finally { release.TrySetResult(); await switching; }
    Assert.Equal(1, connects);
    Assert.Equal(1, activations);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task RecoverySupersededByNewTransitionCannotChangeItsState(bool switchEnvironment)
  {
    var disconnectEntered = Signal();
    var disconnectRelease = Signal();
    int disconnects = 0;
    int connects = 0;
    int activations = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) => { connects++; return Task.CompletedTask; }, async () =>
    {
      if (++disconnects == 1)
      {
        disconnectEntered.TrySetResult();
        await disconnectRelease.Task;
      }
    }, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Task recovery = coordinator.RecoverAsync();
    try
    {
      await disconnectEntered.Task;
      if (switchEnvironment)
        await coordinator.SwitchAsync(_ => { activations++; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
      else
        await coordinator.PauseAsync(TestContext.Current.CancellationToken);
      disconnectRelease.TrySetResult();
      await recovery;
      if (switchEnvironment)
        await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
      else
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
      Assert.Equal(1, connects);
      Assert.Equal(switchEnvironment ? 1 : 0, activations);
    }
    finally { disconnectRelease.TrySetResult(); await recovery; }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CommittedAppSwitchReportsCatalogRefreshFailureSeparately(bool cancelRefresh)
  {
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    int activations = 0;
    int connects = 0;
    int disconnects = 0;
    Exception? refreshFailure = null;
    var coordinator = new RuntimeDemandCoordinator((_, _) => { connects++; return Task.CompletedTask; },
      () => { disconnects++; return Task.CompletedTask; }, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken);

    await App.SwitchManagedEnvironmentAndRefreshAsync(coordinator,
      _ => { activations++; return Task.CompletedTask; },
      _ =>
      {
        if (cancelRefresh)
        {
          cancellation.Cancel();
          return Task.FromCanceled(cancellation.Token);
        }
        return Task.FromException(new HttpRequestException("catalog unavailable after commit"));
      }, error => refreshFailure = error, cancellation.Token);

    Assert.Equal(1, activations);
    Assert.NotNull(refreshFailure);
    if (cancelRefresh) Assert.IsAssignableFrom<OperationCanceledException>(refreshFailure);
    else Assert.IsType<HttpRequestException>(refreshFailure);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Assert.Equal(0, connects);
    Assert.Equal(0, disconnects);
  }
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task ConnectionUsesInitializeResultAndReadsListOnlyForRecovery(bool initialize)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-connect-list-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    Directory.CreateDirectory(Path.GetDirectoryName(layout.ComponentLock)!);
    File.WriteAllText(layout.ComponentLock, "{\"required_capabilities\":[\"test.capability\"]}");
    var manager = new ConnectionListManager();
    int publications = 0;
    var connection = new ManagedRuntimeConnection(manager, layout, new RuntimeStatusViewModel(), (_, _) => publications++);
    try
    {
      Assert.False(await connection.ConnectAsync(initialize, TestContext.Current.CancellationToken));
      Assert.Equal(initialize ? 1 : 0, manager.Initializes);
      Assert.Equal(initialize ? 0 : 1, manager.Lists);
      Assert.Equal(initialize ? "initialized" : "listed", Assert.Single(manager.PreparedIds));
      Assert.Equal(1, manager.Commits);
      Assert.Equal(1, manager.ReservationDisposals);
      Assert.Equal(1, publications);
      Assert.Null(connection.Session);
    }
    finally { await connection.DisconnectAsync(); Directory.Delete(root, recursive: true); }
  }

  private sealed class ConnectionListManager : IManagedEnvironmentClient
  {
    public int Initializes { get; private set; }
    public int Lists { get; private set; }
    public int Commits { get; private set; }
    public int ReservationDisposals { get; private set; }
    public List<string> PreparedIds { get; } = [];
    public Task<ManagedEnvironmentList> InitializeDefaultEnvironmentAsync(CancellationToken cancellationToken = default)
    {
      Initializes++;
      return Task.FromResult(new ManagedEnvironmentList("initialized", 1, []));
    }
    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default)
    {
      Lists++;
      return Task.FromResult(new ManagedEnvironmentList("listed", 2, []));
    }
    public Task<IManagedEnvironmentSwitchReservation> BeginEnvironmentSwitchAsync(string environmentId, CancellationToken cancellationToken = default)
    {
      PreparedIds.Add(environmentId);
      return Task.FromResult<IManagedEnvironmentSwitchReservation>(new Reservation(this,
        new PreparedEnvironmentSwitch(environmentId, 1, null, 0, "unused", false, null)));
    }
    private sealed class Reservation(ConnectionListManager owner, PreparedEnvironmentSwitch prepared) : IManagedEnvironmentSwitchReservation
    {
      public PreparedEnvironmentSwitch Prepared => prepared;
      public Task<CommittedEnvironmentSwitch> CommitAsync(StartedEnvironmentHealth? health, CancellationToken cancellationToken = default)
      {
        Assert.Null(health);
        owner.Commits++;
        return Task.FromResult(new CommittedEnvironmentSwitch(prepared.EnvironmentId, 1));
      }
      public ValueTask DisposeAsync() { owner.ReservationDisposals++; return ValueTask.CompletedTask; }
    }
    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(string environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(PreparedEnvironmentSwitch prepared,
      StartedEnvironmentHealth? startedHealth = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironment> CreateEnvironmentAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(string? environmentId, string? packageSourceId,
      string? modelSourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(string environmentId, string recipe,
      IReadOnlyList<string>? sourceIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironment> InstallEnvironmentAsync(ManagedEnvironmentPlan plan,
      IReadOnlyList<string>? sourceIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
