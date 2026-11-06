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
  [InlineData("ensure")]
  [InlineData("recover")]
  public async Task ShellOnlyRejectsAllComputingLifecycleEntries(string operation)
  {
    int connects = 0;
    var coordinator = new RuntimeDemandCoordinator((_, _) => { connects++; return Task.CompletedTask; },
      () => Task.CompletedTask, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken,
      shellOnly: true);
    Task action = operation switch
    {
      "ensure" => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken),
      _ => coordinator.RecoverAsync(),
    };
    await Assert.ThrowsAsync<InvalidOperationException>(() => action);
    Assert.Equal(0, connects);
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
  public async Task RecoverySupersededByNewTransitionCannotChangeItsState()
  {
    var disconnectEntered = Signal();
    var disconnectRelease = Signal();
    int disconnects = 0;
    int connects = 0;
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
      await coordinator.PauseAsync(TestContext.Current.CancellationToken);
      disconnectRelease.TrySetResult();
      await recovery;
      await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
      Assert.Equal(1, connects);
    }
    finally { disconnectRelease.TrySetResult(); await recovery; }
  }

  [Fact]
  public async Task MissingProductRuntimeFailsFirstDemandWithoutPublishingASession()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-missing-product-{Guid.NewGuid():N}");
    var layout = PortableLayout.Resolve(root, "production");
    int publications = 0;
    var connection = new ManagedRuntimeConnection(layout, new RuntimeStatusViewModel(), (_, _) => publications++);
    var maintenance = new ProductMaintenanceCoordinator();
    var coordinator = new RuntimeDemandCoordinator(async (_, token) => { await connection.ConnectAsync(token); },
      connection.DisconnectAsync, maintenance, TestContext.Current.CancellationToken, connection);
    await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken));
    Assert.Equal(0, publications);
    Assert.Null(connection.Session);
    Assert.True(maintenance.State.IsIdle);
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
  [InlineData(false)]
  [InlineData(true)]
  public async Task ExitRecoveryWaitsForPublishedProductSessionStartupAndPropagatesCleanup(bool failCleanup)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-product-recovery-{Guid.NewGuid():N}");
    var connection = new ManagedRuntimeConnection(PortableLayout.Resolve(root, "production"), new RuntimeStatusViewModel(), (_, _) => { });
    await using var original = StubSession("first");
    await using var restored = StubSession("restored");
    var published = Signal();
    var finish = Signal();
    int connects = 0;
    int disconnects = 0;
    int recoveries = 0;
    var cleanup = new IOException("owned cleanup failed");
    var coordinator = new RuntimeDemandCoordinator(async (_, _) =>
    {
      PublishSession(connection, ++connects == 1 ? original : restored);
      if (connects != 1) return;
      published.SetResult();
      await finish.Task;
      if (failCleanup) throw new InvalidOperationException("startup failed");
    }, async () =>
    {
      disconnects++;
      await connection.DisconnectAsync();
      if (failCleanup) throw cleanup;
    }, new ProductMaintenanceCoordinator(), TestContext.Current.CancellationToken, connection);
    Task startup = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    await published.Task;
    Task shared = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    Assert.Equal(1, connects);
    Task recovery = coordinator.RecoverAsync(() => ReferenceEquals(connection.Session, original), () => recoveries++);
    Assert.False(recovery.IsCompleted);
    Assert.Equal(0, disconnects);
    finish.SetResult();
    if (failCleanup)
    {
      Assert.Contains(cleanup, (await Assert.ThrowsAsync<AggregateException>(() => startup)).InnerExceptions);
      Assert.Contains(cleanup, (await Assert.ThrowsAsync<AggregateException>(() => shared)).InnerExceptions);
      Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(() => recovery));
      Assert.Equal(0, recoveries);
    }
    else
    {
      await Task.WhenAll(startup, shared);
      await recovery;
      Assert.Equal(2, connects);
      Assert.Same(restored, connection.Session);
      Assert.Equal(1, recoveries);
    }
    await connection.DisconnectAsync();
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ExitRecoveryOnlyProjectsAndReconnectsItsCurrentSession(bool replaced)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-exit-current-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(Path.Combine(root, "VibeOCR.exe"), "winui-dev");
    var connection = new ManagedRuntimeConnection(layout, new RuntimeStatusViewModel(), (_, _) => { });
    await using ProductRuntimeSession original = StubSession("exited-A");
    await using ProductRuntimeSession target = StubSession("healthy-B");
    await using ProductRuntimeSession restored = StubSession("restored-A");
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
    await using ProductRuntimeSession trigger = StubSession("A");
    await using ProductRuntimeSession replacement = StubSession("B");
    ProductRuntimeSession current = trigger;
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
    await using ProductRuntimeSession original = StubSession("A");
    await using ProductRuntimeSession replacement = StubSession("B");
    ProductRuntimeSession? scheduled = null;
    ProductRuntimeSession current = original;
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
    var connection = new ManagedRuntimeConnection(layout, new RuntimeStatusViewModel(), (_, _) => { });
    await using ProductRuntimeSession original = StubSession("exited-A");
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
  [Fact]
  public async Task BackgroundStartupSharesAttemptWithFirstDemandAndShutdownCancelsBoth()
  {
    // App 首次启动后台 EnsureReady（shutdown 令牌）与首个计算需求共享同一
    // 初始化尝试；应用关闭令牌取消时两者一并取消并只排空一次清理。
    var maintenance = new ProductMaintenanceCoordinator();
    var entered = Signal();
    var release = Signal();
    using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    int connects = 0;
    int disconnects = 0;
    var coordinator = new RuntimeDemandCoordinator(async (_, ct) =>
    {
      connects++;
      entered.TrySetResult();
      await release.Task.WaitAsync(ct);
    }, () => { disconnects++; return Task.CompletedTask; }, maintenance, shutdown.Token);
    Task background = coordinator.EnsureReadyAsync(shutdown.Token);
    await entered.Task;
    Task demand = coordinator.EnsureReadyAsync(caller.Token);
    Assert.Equal(1, connects);
    shutdown.Cancel();
    release.TrySetResult();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => background);
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => demand);
    Assert.Equal(1, disconnects);
    Assert.True(maintenance.State.IsIdle);
  }

  private static ProductRuntimeSession StubSession(string instance) => new(
    new InferenceSupervisorProcess(new InferenceSupervisorOptions("unused", [], Path.GetTempPath(),
      Path.Combine(Path.GetTempPath(), "unused.log"), TimeSpan.FromSeconds(1), new HashSet<string>()), "test"),
    new InferenceHttpClient(new Uri("http://127.0.0.1:1"), "test"),
    new QrCodeHttpClient(new Uri("http://127.0.0.1:1"), "test"), new RuntimeStatusSnapshot
    {
      InstanceId = instance, BackendVersion = "test", ServiceState = RuntimeServiceState.Ready,
      Profile = new RuntimeProfileStatus { ProfileId = "test", Accelerator = RuntimeAccelerator.Cpu },
    });

  private static void PublishSession(ManagedRuntimeConnection connection, ProductRuntimeSession? session) =>
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
