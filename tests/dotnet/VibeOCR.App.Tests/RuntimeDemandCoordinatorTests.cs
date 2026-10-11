using System.Reflection;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
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
  [Theory]
  [InlineData(false, "none")]
  [InlineData(true, "none")]
  [InlineData(false, "caller")]
  [InlineData(false, "shutdown")]
  public async Task NewDemandDrainsCancelledOwnerWithoutInheritingItsBusinessFailure(bool failCleanup, string cancellation)
  {
    var entered = Signal();
    var release = Signal();
    using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    using var oldCaller = new CancellationTokenSource();
    using var newCaller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    var businessFailure = new InvalidOperationException("departing owner's preparation failed");
    var cleanupFailure = new IOException("departing owner's cleanup failed");
    int connects = 0;
    var coordinator = new RuntimeDemandCoordinator(async (_, _) =>
    {
      if (++connects == 1)
      {
        entered.TrySetResult();
        await release.Task;
        throw businessFailure;
      }
    }, () => failCleanup ? Task.FromException(cleanupFailure) : Task.CompletedTask,
      new ProductMaintenanceCoordinator(), shutdown.Token);
    Task oldDemand = coordinator.EnsureReadyAsync(oldCaller.Token);
    await entered.Task;
    oldCaller.Cancel();
    Task newDemand = coordinator.EnsureReadyAsync(newCaller.Token);
    try
    {
      Assert.False(newDemand.IsCompleted);
      if (cancellation == "caller") newCaller.Cancel();
      else if (cancellation == "shutdown") shutdown.Cancel();
      if (cancellation != "none")
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newDemand.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
      release.TrySetResult();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldDemand);
      if (cancellation != "none")
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newDemand);
      else if (failCleanup)
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => newDemand));
      else
        await newDemand;
      Assert.Equal(cancellation == "none" && !failCleanup ? 2 : 1, connects);
    }
    finally
    {
      release.TrySetResult();
      try { await oldDemand; } catch (OperationCanceledException) { }
      try { await newDemand; } catch (Exception) { }
    }
  }

  [Theory]
  [InlineData("empty")]
  [InlineData("healthy")]
  [InlineData("failed")]
  [InlineData("cancelled")]
  public async Task MaintenanceRestoreFollowsLastCommittedEnvironmentIntent(string outcome)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-demand-intent-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    Directory.CreateDirectory(Path.GetDirectoryName(layout.ComponentLock)!);
    File.WriteAllText(layout.ComponentLock, "{\"required_capabilities\":[\"test.capability\"]}");
    var manager = new ConnectionListManager();
    var connection = new ManagedRuntimeConnection(manager, layout, new RuntimeStatusViewModel(), (_, _) => { });
    await using ManagedEnvironmentSession original = StubSession("old");
    await using ManagedEnvironmentSession restored = StubSession("restored");
    int connects = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) =>
    {
      connects++;
      if (connects == 1) PublishSession(connection, original);
      else if (outcome != "empty") PublishSession(connection, restored);
      return Task.CompletedTask;
    }, connection.DisconnectAsync, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken, connection);
    using var caller = new CancellationTokenSource();
    try
    {
      await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
      Task switching = coordinator.SwitchAsync(async ct =>
      {
        if (outcome == "empty") await connection.SwitchAsync("empty", ct);
        else if (outcome == "failed") throw new InvalidOperationException("activation failed before commit");
        else if (outcome == "cancelled") { caller.Cancel(); ct.ThrowIfCancellationRequested(); }
      }, caller.Token);
      if (outcome == "failed") await Assert.ThrowsAsync<InvalidOperationException>(() => switching);
      else if (outcome == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switching);
      else await switching;
      if (outcome == "empty")
      {
        Assert.Null(connection.Session);
        Assert.Equal("empty", Assert.Single(manager.PreparedIds));
        Assert.Equal(1, manager.Commits);
      }
      else Assert.Same(original, connection.Session);
      long owner = await coordinator.PauseAsync(TestContext.Current.CancellationToken);
      await coordinator.RestoreAsync(owner);
      Assert.Equal(outcome == "empty" ? 1 : 2, connects);
      if (outcome == "empty") Assert.Null(connection.Session);
      else Assert.Same(restored, connection.Session);
    }
    finally { await connection.DisconnectAsync(); Directory.Delete(root, recursive: true); }
  }

  [Theory]
  [InlineData("healthy")]
  [InlineData("failed")]
  [InlineData("stop")]
  [InlineData("maintenance")]
  [InlineData("new-switch")]
  [InlineData("cleanup")]
  public async Task ExitRecoveryWaitsForObservedSwitchWithoutTakingItsOwnership(string outcome)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-exit-switch-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    var connection = new ManagedRuntimeConnection(new ConnectionListManager(), layout, new RuntimeStatusViewModel(), (_, _) => { });
    await using ManagedEnvironmentSession original = StubSession("exited-A");
    await using ManagedEnvironmentSession target = StubSession("target-B");
    await using ManagedEnvironmentSession restored = StubSession("restored-A");
    var entered = Signal();
    var release = Signal();
    var cleanupFailure = new IOException("switch candidate cleanup failed");
    int connects = 0;
    int recovering = 0;
    int disconnects = 0;
    CancellationToken activationToken = default;
    using var switchCaller = new CancellationTokenSource();
    var coordinator = new RuntimeDemandCoordinator((_, _) =>
    {
      PublishSession(connection, ++connects == 1 ? original : restored);
      return Task.CompletedTask;
    }, async () =>
    {
      await connection.DisconnectAsync();
      if (outcome == "cleanup" && ++disconnects == 1) throw cleanupFailure;
    }, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken, connection);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Task switching = coordinator.SwitchAsync(async ct =>
    {
      activationToken = ct;
      entered.TrySetResult();
      await release.Task;
      ct.ThrowIfCancellationRequested();
      if (outcome == "failed") throw new InvalidOperationException("B failed; A remains published");
      PublishSession(connection, target);
      if (outcome == "cleanup") switchCaller.Cancel();
    }, switchCaller.Token);
    await entered.Task;
    Task recovery = coordinator.RecoverAsync(() => ReferenceEquals(connection.Session, original), () => recovering++);
    Task? superseding = null;
    try
    {
      Assert.False(activationToken.IsCancellationRequested);
      Assert.False(recovery.IsCompleted);
      if (outcome == "stop") superseding = coordinator.StopAsync();
      else if (outcome == "maintenance") superseding = coordinator.PauseAsync(TestContext.Current.CancellationToken);
      else if (outcome == "new-switch") superseding = coordinator.SwitchAsync(_ =>
      {
        PublishSession(connection, target);
        return Task.CompletedTask;
      }, TestContext.Current.CancellationToken);
      release.TrySetResult();
      if (outcome == "failed") await Assert.ThrowsAsync<InvalidOperationException>(() => switching);
      else if (outcome is "stop" or "maintenance" or "new-switch" or "cleanup")
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switching);
      else await switching;
      if (superseding is not null) await superseding;
      if (outcome == "cleanup")
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => recovery));
      else await recovery;
      Assert.Equal(outcome == "failed" ? 2 : 1, connects);
      Assert.Equal(outcome == "failed" ? 1 : 0, recovering);
      if (outcome is "healthy" or "new-switch") Assert.Same(target, connection.Session);
      else if (outcome == "failed") Assert.Same(restored, connection.Session);
      else Assert.Null(connection.Session);
      if (outcome is "stop" or "maintenance")
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
    }
    finally
    {
      release.TrySetResult();
      try { await switching; } catch (Exception) { }
      try { await recovery; } catch (Exception) { }
      if (superseding is not null) { try { await superseding; } catch (Exception) { } }
      await connection.DisconnectAsync();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ExitRecoveryOnlyProjectsAndReconnectsItsCurrentSession(bool replaced)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-exit-current-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    var connection = new ManagedRuntimeConnection(new ConnectionListManager(), layout, new RuntimeStatusViewModel(), (_, _) => { });
    await using ManagedEnvironmentSession original = StubSession("exited-A");
    await using ManagedEnvironmentSession target = StubSession("healthy-B");
    await using ManagedEnvironmentSession restored = StubSession("restored-A");
    int connects = 0;
    int recovering = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) =>
    {
      PublishSession(connection, ++connects == 1 ? original : restored);
      return Task.CompletedTask;
    }, connection.DisconnectAsync, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken, connection);
    try
    {
      await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
      if (replaced) PublishSession(connection, target);
      await coordinator.RecoverAsync(() => ReferenceEquals(connection.Session, original), () => recovering++);
      Assert.Equal(replaced ? 1 : 2, connects);
      Assert.Equal(replaced ? 0 : 1, recovering);
      Assert.Same(replaced ? target : restored, connection.Session);
    }
    finally { await connection.DisconnectAsync(); }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task QueuedExitCallbackRechecksSessionAndAlwaysReleasesItsSchedule(bool replaced)
  {
    await using ManagedEnvironmentSession trigger = StubSession("A");
    await using ManagedEnvironmentSession replacement = StubSession("B");
    ManagedEnvironmentSession current = trigger;
    int recoveries = 0;
    int completions = 0;
    Func<Task> queued = () => App.RunCurrentSupervisorRecoveryAsync(() => ReferenceEquals(current, trigger),
      () => { recoveries++; return Task.CompletedTask; }, () => completions++);
    if (replaced) current = replacement;
    await queued();
    Assert.Equal(replaced ? 0 : 1, recoveries);
    Assert.Equal(1, completions);
  }

  [Fact]
  public async Task NewSessionExitCanScheduleWhileOldCallbackIsPendingAndOldCompletionCannotClearIt()
  {
    await using ManagedEnvironmentSession original = StubSession("A");
    await using ManagedEnvironmentSession replacement = StubSession("B");
    ManagedEnvironmentSession? scheduled = null;
    ManagedEnvironmentSession current = original;
    Assert.True(App.TryScheduleSupervisorRecovery(ref scheduled, original, () => current));
    Assert.False(App.TryScheduleSupervisorRecovery(ref scheduled, original, () => current));
    current = replacement;
    Assert.True(App.TryScheduleSupervisorRecovery(ref scheduled, replacement, () => current));
    Assert.False(App.TryScheduleSupervisorRecovery(ref scheduled, original, () => current));
    App.CompleteSupervisorRecovery(ref scheduled, original);
    Assert.Same(replacement, scheduled);
    Assert.False(App.TryScheduleSupervisorRecovery(ref scheduled, replacement, () => current));
    App.CompleteSupervisorRecovery(ref scheduled, replacement);
    Assert.Null(scheduled);
  }
  [Fact]
  public async Task AcceptedExitRecoveryReportsCleanupFailureAfterDisconnectClearsSession()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-exit-cleanup-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    var connection = new ManagedRuntimeConnection(new ConnectionListManager(), layout, new RuntimeStatusViewModel(), (_, _) => { });
    await using ManagedEnvironmentSession original = StubSession("exited-A");
    var cleanupFailure = new IOException("disposal failed after publish(null)");
    int connects = 0;
    Exception? reported = null;
    var coordinator = new RuntimeDemandCoordinator((_, _) =>
    {
      connects++;
      PublishSession(connection, original);
      return Task.CompletedTask;
    }, async () =>
    {
      await connection.DisconnectAsync();
      throw cleanupFailure;
    }, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken, connection);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => coordinator.RecoverAsync(
      () => ReferenceEquals(connection.Session, original), recoveryFailed: error => reported = error)));
    Assert.Null(connection.Session);
    Assert.Same(cleanupFailure, reported);
    Assert.Equal(1, connects);
  }
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ExitRecoveryWaitsThroughBothSwitchStages(bool failTarget)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-exit-drain-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    var connection = new ManagedRuntimeConnection(new ConnectionListManager(), layout, new RuntimeStatusViewModel(), (_, _) => { });
    await using ManagedEnvironmentSession original = StubSession("exited-A");
    await using ManagedEnvironmentSession target = StubSession("target-B");
    await using ManagedEnvironmentSession restored = StubSession("restored-A");
    int connects = 0;
    int projections = 0;
    var oldEntered = Signal();
    var oldRelease = Signal();
    var targetEntered = Signal();
    var targetRelease = Signal();
    CancellationToken targetToken = default;
    var coordinator = new RuntimeDemandCoordinator((_, _) =>
    {
      PublishSession(connection, ++connects == 1 ? original : restored);
      return Task.CompletedTask;
    }, connection.DisconnectAsync, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken, connection);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Task previous = coordinator.SwitchAsync(async ct =>
    {
      oldEntered.TrySetResult();
      await oldRelease.Task;
      ct.ThrowIfCancellationRequested();
    }, TestContext.Current.CancellationToken);
    await oldEntered.Task;
    Task switching = coordinator.SwitchAsync(async ct =>
    {
      targetToken = ct;
      targetEntered.TrySetResult();
      await targetRelease.Task;
      ct.ThrowIfCancellationRequested();
      if (failTarget) throw new InvalidOperationException("B activation failed");
      PublishSession(connection, target);
    }, TestContext.Current.CancellationToken);
    Task recovery = coordinator.RecoverAsync(() => ReferenceEquals(connection.Session, original), () => projections++);
    try
    {
      Assert.False(recovery.IsCompleted);
      oldRelease.TrySetResult();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previous);
      await targetEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
      Assert.False(targetToken.IsCancellationRequested);
      Assert.False(recovery.IsCompleted);
      targetRelease.TrySetResult();
      if (failTarget) await Assert.ThrowsAsync<InvalidOperationException>(() => switching);
      else await switching;
      await recovery;
      Assert.Equal(failTarget ? 2 : 1, connects);
      Assert.Equal(failTarget ? 1 : 0, projections);
      Assert.Same(failTarget ? restored : target, connection.Session);
    }
    finally
    {
      oldRelease.TrySetResult();
      targetRelease.TrySetResult();
      try { await previous; } catch (Exception) { }
      try { await switching; } catch (Exception) { }
      try { await recovery; } catch (Exception) { }
      await connection.DisconnectAsync();
    }
  }
  [Theory]
  [InlineData("failed")]
  [InlineData("healthy")]
  [InlineData("cleanup")]
  public async Task ExitRecoveryWaitsForSharedAttemptAfterSwitchCallerCancels(string outcome)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-exit-shared-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    var connection = new ManagedRuntimeConnection(new ConnectionListManager(), layout, new RuntimeStatusViewModel(), (_, _) => { });
    await using ManagedEnvironmentSession original = StubSession("exited-A");
    await using ManagedEnvironmentSession target = StubSession("target-B");
    await using ManagedEnvironmentSession restored = StubSession("restored-A");
    var entered = Signal();
    var release = Signal();
    var businessFailure = new InvalidOperationException("B failed while a shared demand still owns its attempt");
    var cleanupFailure = new IOException("late shared attempt cleanup failed");
    using var switchCaller = new CancellationTokenSource();
    using var demandCaller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    CancellationToken activationToken = default;
    int connects = 0;
    int projections = 0;
    Exception? reported = null;
    var coordinator = new RuntimeDemandCoordinator((_, _) =>
    {
      PublishSession(connection, ++connects == 1 ? original : restored);
      return Task.CompletedTask;
    }, async () =>
    {
      await connection.DisconnectAsync();
      if (outcome == "cleanup") throw cleanupFailure;
    }, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken, connection);
    await coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Task switching = coordinator.SwitchAsync(async ct =>
    {
      activationToken = ct;
      entered.TrySetResult();
      await release.Task;
      if (outcome == "failed") throw businessFailure;
      PublishSession(connection, target);
      if (outcome == "cleanup") demandCaller.Cancel();
    }, switchCaller.Token);
    await entered.Task;
    Task demand = coordinator.EnsureReadyAsync(demandCaller.Token);
    Task recovery = coordinator.RecoverAsync(() => ReferenceEquals(connection.Session, original),
      () => projections++, error => reported = error);
    try
    {
      Assert.False(demand.IsCompleted);
      Assert.False(recovery.IsCompleted);
      switchCaller.Cancel();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        switching.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
      Assert.False(activationToken.IsCancellationRequested);
      // The command caller departed promptly; the legitimate computation still
      // owns the activation, and its real terminal outcome owns exit recovery.
      release.TrySetResult();
      if (outcome == "failed") Assert.Same(businessFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => demand));
      else if (outcome == "cleanup") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => demand);
      else await demand;
      if (outcome == "cleanup")
      {
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => recovery));
        Assert.Same(cleanupFailure, reported);
        Assert.Null(connection.Session);
      }
      else
      {
        await recovery;
        Assert.Null(reported);
        Assert.Same(outcome == "failed" ? restored : target, connection.Session);
      }
      Assert.Equal(outcome == "failed" ? 2 : 1, connects);
      Assert.Equal(outcome == "failed" ? 1 : 0, projections);
    }
    finally
    {
      release.TrySetResult();
      try { await switching; } catch (Exception) { }
      try { await demand; } catch (Exception) { }
      try { await recovery; } catch (Exception) { }
      await connection.DisconnectAsync();
    }
  }
  private static ManagedEnvironmentSession StubSession(string instance) => new("environment", 1,
    new InferenceSupervisorProcess(new InferenceSupervisorOptions("unused", [], Path.GetTempPath(),
      Path.Combine(Path.GetTempPath(), "unused.log"), TimeSpan.FromSeconds(1), new HashSet<string>()), "test"),
    new InferenceHttpClient(new Uri("http://127.0.0.1:1"), "test"),
    new QrCodeHttpClient(new Uri("http://127.0.0.1:1"), "test"), new RuntimeStatusSnapshot
    {
      InstanceId = instance, BackendVersion = "test", ServiceState = RuntimeServiceState.Ready,
      Profile = new RuntimeProfileStatus { ProfileId = "test", Accelerator = RuntimeAccelerator.Cpu },
    });

  private static void PublishSession(ManagedRuntimeConnection connection, ManagedEnvironmentSession session) =>
    typeof(ManagedRuntimeConnection).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!
      .Invoke(connection, [session]);
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
