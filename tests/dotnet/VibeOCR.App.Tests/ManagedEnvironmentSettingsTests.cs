using System.Net;
using System.Text;
using System.Text.Json;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ManagedEnvironmentSettingsTests
{
  [Fact]
  public async Task SameEnvironmentDeviceSelectionReachesLifecycleOwner()
  {
    string? selected = null;
    var settings = new ManagedEnvironmentSettings(new MutableManager(),
      (_, _) => throw new Xunit.Sdk.XunitException("device selection must reach lifecycle"),
      () => ("environment", 1), new ProductMaintenanceCoordinator(),
      activateDevice: (id, device, _) => { Assert.Equal("environment", id); selected = device; return Task.CompletedTask; });
    await settings.SwitchAsync("environment", TestContext.Current.CancellationToken, "cpu");
    Assert.Equal("cpu", selected);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CommittedSwitchSurvivesPostCommitListRefreshFailure(bool cancelRefresh)
  {
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    var manager = new MutableManager();
    int activations = 0;
    var settings = new ManagedEnvironmentSettings(manager, (_, _) =>
    {
      activations++;
      manager.BeforeList = () =>
      {
        if (cancelRefresh)
        {
          cancellation.Cancel();
          return Task.FromCanceled(cancellation.Token);
        }
        return Task.FromException(new HttpRequestException("post-commit list unavailable"));
      };
      return Task.CompletedTask;
    }, () => null, new ProductMaintenanceCoordinator());
    await settings.RefreshAsync(cancellation.Token);
    ManagedEnvironmentList original = Assert.IsType<ManagedEnvironmentList>(settings.Snapshot);

    await settings.SwitchAsync("environment", cancellation.Token);

    Assert.Equal(1, activations);
    Assert.False(settings.IsBusy);
    Assert.Same(original, settings.Snapshot);
    Assert.Contains("已切换", settings.Status, StringComparison.Ordinal);
    Assert.Contains("刷新", settings.Status, StringComparison.Ordinal);
    Assert.Contains(cancelRefresh ? "取消" : "post-commit list unavailable", settings.Status, StringComparison.Ordinal);
  }
  [Fact]
  public async Task ReadOnlyEnvironmentListUsesExistingRefreshWithoutPreparingRuntime()
  {
    int lists = 0;
    var manager = new MutableManager { BeforeList = () => { lists++; return Task.CompletedTask; } };
    var environments = new ManagedEnvironmentSettings(manager,
      (_, _) => throw new Xunit.Sdk.XunitException("list must not switch"), () => null,
      new ProductMaintenanceCoordinator());
    var inference = new DeferredInferenceClient();
    var settings = new SettingsViewModel(inference, environments: environments);
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-readonly-list-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
        () => settings, () => new ShellViewModel(new ListHotkeys(), new ListStartup()),
        static () => throw new InvalidOperationException(), new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, root, static () => 0, annotations, inferenceAttached: () => false,
        ensureRuntimeReady: _ => throw new Xunit.Sdk.XunitException("list must not prepare"));
      WorkbenchCommandOutcome result = await handler.ExecuteAsync(new RefreshEnvironmentListCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(result.Error);
      Assert.Equal(1, lists);
      Assert.NotEmpty(environments.Snapshot!.Environments);
      Assert.False(inference.IsAttached);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  private sealed class ListHotkeys : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict) { conflict = null; return true; }
    public void Unregister() { }
  }
  private sealed class ListStartup : IStartupRegistrar
  {
    public bool IsEnabled => false;
    public bool SetEnabled(bool enabled) => true;
  }

  [Fact]
  public void RunningCatalogAndResidencyDistinguishUncheckedFromReadyModel()
  {
    var environment = new ManagedEnvironment("environment", "Paddle", 2, "venv", "installed", "python",
      "ready", "installed", "unverified", "not_checked", "not_started", null,
      Recipe: "opaque-identifier.v2", Engine: "paddle", Family: "paddle-onnx");
    var health = new Wire.Health
    {
      SchemaVersion = 2, InstanceId = "sup", ProtocolVersion = 2, Ready = true,
      Draining = false, Capabilities = [RuntimeSelectionService.EngineSelectionCapability],
      CapabilityDescriptors = [new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.EngineSelectionCapability,
        Lifecycle = "active", IntroducedIn = "2.0.0", DeprecatedIn = null,
        SunsetAt = null, Replacement = null,
        OcrEngineCatalog = new Wire.OcrEngineCatalog { Engines = [new Wire.OcrEngineDescriptor
        {
          Id = Wire.OcrEngineId.Paddleocr,
          Availability = Wire.OcrEngineAvailability.Ready,
          IncludedInBase = false, ReasonCode = null, RequiredComponent = null,
        }] },
      }],
    };
    var status = new RuntimeStatusSnapshot
    {
      InstanceId = "sup", ServiceState = RuntimeServiceState.Ready,
      BackendVersion = "0.7.0",
      Profile = new RuntimeProfileStatus { ProfileId = "win-x64-cpu", Accelerator = RuntimeAccelerator.Cpu },
    };

    ManagedEnvironment pending = ManagedEnvironmentSettings.ProjectRunning(
      environment, health, new ResidencyStatus(), status);
    Assert.Equal("ready", pending.EngineState);
    Assert.Equal("not_checked", pending.ModelState);
    Assert.Null(pending.ActualDevice);

    ManagedEnvironment unrelated = ManagedEnvironmentSettings.ProjectRunning(environment, health,
      new ResidencyStatus { VramUsedMb = 4096, Entries = [new ResidencyEntry
      {
        Pipeline = "MinerU", Kind = ResidencyKind.SoftTtl,
        RecognitionMode = RecognitionMode.MineruDocument,
        ResourceKind = RecognitionResourceKind.Process,
      }] }, status);
    Assert.Equal("not_checked", unrelated.ModelState);
    Assert.Null(unrelated.ActualDevice);

    ManagedEnvironment evicted = ManagedEnvironmentSettings.ProjectRunning(environment, health,
      new ResidencyStatus { Entries = [new ResidencyEntry
      {
        Pipeline = "OCR", Kind = ResidencyKind.Evicted,
        RecognitionMode = RecognitionMode.PaddleText,
        ResourceKind = RecognitionResourceKind.Model,
      }] }, status);
    Assert.Equal("not_checked", evicted.ModelState);

    ManagedEnvironment runningWithoutDiskFact = ManagedEnvironmentSettings.ProjectRunning(environment, health,
      new ResidencyStatus { Entries = [new ResidencyEntry
      {
        Pipeline = "PaddleOCR", Kind = ResidencyKind.Idle,
        ResourceId = "paddle", ResourceKind = RecognitionResourceKind.Process,
      }] }, status);
    Assert.Equal("not_checked", runningWithoutDiskFact.ModelState);

    ManagedEnvironment ready = ManagedEnvironmentSettings.ProjectRunning(environment, health,
      new ResidencyStatus { Entries = [new ResidencyEntry
      {
        Pipeline = "PaddleOCR", Kind = ResidencyKind.Evicted,
        ResourceId = "paddle", ModelsPrepared = true,
        ResourceKind = RecognitionResourceKind.Process,
      }] },
      status);
    Assert.Equal("ready", ready.ModelState);
    Assert.Null(ready.ActualDevice);
    Assert.Equal("ready", ready.ServiceState);
  }

  [Theory]
  [InlineData("rapidocr-cpu")]
  [InlineData("rapidocr-cuda")]
  [InlineData("rapidocr+mineru-cpu")]
  [InlineData("rapidocr+mineru-cuda")]
  [InlineData("paddleocr-cpu")]
  [InlineData("paddleocr-cuda")]
  public void RetiredEnvironmentCannotBorrowReadyCatalogOrEngine(string recipe)
  {
    var environment = new ManagedEnvironment("environment", "Retired", 2, "venv", "installed", "python",
      "ready", "installed", "unavailable", "not_checked", "not_started", null, Recipe: recipe);
    foreach (Wire.Health health in new[] { DefaultRecognitionModeWorkbenchTests.DefaultModeHealth(), PaddleReadyHealth() })
    {
      ManagedEnvironment projected = ManagedEnvironmentSettings.ProjectRunning(
        environment, health, new ResidencyStatus(), ReadyStatus());
      Assert.Equal("unavailable", projected.EngineState);
      Assert.Equal("not_checked", projected.ModelState);
      Assert.Null(projected.ActualDevice);
    }
  }

  [Theory]
  [InlineData("mineru-cpu")]
  [InlineData("mineru-cuda")]
  public void MineruEnvironmentUsesItsOwnModeReadiness(string recipe)
  {
    var environment = new ManagedEnvironment("environment", "MinerU", 2, "venv", "installed", "python",
      "ready", "installed", "unverified", "not_checked", "not_started", null, Recipe: recipe, Engine: "mineru", Family: "mineru");
    ManagedEnvironment projected = ManagedEnvironmentSettings.ProjectRunning(environment,
      DefaultRecognitionModeWorkbenchTests.DefaultModeHealth(), new ResidencyStatus(), ReadyStatus());
    Assert.Equal("preparation_required", projected.EngineState);
  }

  [Fact]
  public async Task CleanupRequiresExplicitCurrentSelectionAndKeepsPartialResult()
  {
    var manager = new MutableManager();
    var settings = NewSettings(manager);
    await settings.RefreshAsync(TestContext.Current.CancellationToken);
    await settings.PreviewCleanupAsync(TestContext.Current.CancellationToken);
    await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CleanupAsync(new string('b', 32), ["residual:fixture"], TestContext.Current.CancellationToken));
    await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CleanupAsync(new string('a', 32), ["environment:protected"], TestContext.Current.CancellationToken));
    Assert.Equal(0, manager.CleanupCalls);
    await settings.CleanupAsync(new string('a', 32), ["residual:fixture"], TestContext.Current.CancellationToken);
    Assert.Equal("failed", Assert.Single(settings.CleanupResult!.Items).State);
    Assert.Equal(12, Assert.Single(settings.CleanupResult.Items).RemovedLogicalBytes);
    Assert.Contains("部分完成", settings.Status);
    Assert.Null(settings.CleanupPlan);
    Assert.False(settings.CanCancelCleanup);
    Assert.NotNull(settings.Snapshot);
  }

  [Fact]
  public async Task CleanupPagesReachEveryCandidateAndRejectHiddenSelection()
  {
    ManagedCleanupItem[] items = Enumerable.Range(0, 2050).Select(index => new ManagedCleanupItem(
      $"item:{index}", index < 200 ? "unknown" : "residual", $"候选 {index}", null, 12, index >= 200,
      new string('原', 500), [new string('路', 500)], PathCount: 1)).ToArray();
    var previous = new ManagedCleanupResult(new string('b', 32), Enumerable.Range(0, 200).Select(index =>
      new ManagedCleanupResultItem($"previous:{index}", "failed", new string('错', 4000), 0)).ToArray(), "logical_bytes");
    var manager = new MutableManager { CleanupPreview = new ManagedCleanupPlan(new string('a', 32), items,
      "logical_bytes", "逻辑字节", previous) };
    var settings = NewSettings(manager);
    await settings.PreviewCleanupAsync(TestContext.Current.CancellationToken);
    await Assert.ThrowsAsync<InvalidOperationException>(() => settings.CleanupAsync(new string('a', 32), ["item:2049"], TestContext.Current.CancellationToken));
    Assert.Equal(0, manager.CleanupCalls);
    var seen = new HashSet<string>();
    var resultIds = new HashSet<string>();
    Guid session = Guid.NewGuid();
    for (int page = 0; page < settings.CleanupPageCount; page++)
    {
      await settings.SetCleanupPageAsync(page, TestContext.Current.CancellationToken);
      ManagedCleanupPlan visible = settings.CleanupPlanPage!;
      Assert.Null(visible.LastResult);
      Assert.InRange(visible.Items.Count, 1, ManagedEnvironmentSettings.CleanupPageSize);
      foreach (ManagedCleanupItem item in visible.Items) Assert.True(seen.Add(item.Id));
      foreach (ManagedCleanupResultItem item in settings.CleanupResultPage!.Items) Assert.True(resultIds.Add(item.Id));
      var state = new SettingsWorkbenchState(WorkbenchTheme.Light, false, "settings.ready", "cpu", false,
        EnvironmentCleanupPlan: visible, EnvironmentCleanupResult: settings.CleanupResultPage,
        EnvironmentCleanupPage: page, EnvironmentCleanupPageCount: settings.CleanupPageCount);
      var envelope = new WorkbenchStateEnvelope(page, "settings", WorkbenchStateChange.Replace, state);
      WorkbenchBridgeCodec.SerializeState(session, envelope);
      WorkbenchBridgeCodec.SerializeBootstrap(Guid.NewGuid(), new WorkbenchBootstrap(2, session, page,
        WorkbenchRoute.Settings, [envelope], new HashSet<string> { "runtime.environments" }));
    }
    Assert.Equal(2050, seen.Count);
    Assert.Equal(200, resultIds.Count);
    Assert.Equal(500, manager.CleanupPreview.Items[0].Paths[0].Length);
    await settings.CleanupAsync(new string('a', 32), ["item:2049"], TestContext.Current.CancellationToken);
    Assert.Equal(1, manager.CleanupCalls);
  }

  [Fact]
  public async Task CleanupCancellationReleasesLeaseAndKeepsRecoveryEntry()
  {
    var manager = new MutableManager();
    var maintenance = new ProductMaintenanceCoordinator();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask, () => null, maintenance);
    await settings.RefreshAsync(TestContext.Current.CancellationToken);
    await settings.PreviewCleanupAsync(TestContext.Current.CancellationToken);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    manager.BeforeCleanup = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
    Task cleanup = settings.CleanupAsync(new string('a', 32), ["residual:fixture"], TestContext.Current.CancellationToken);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    Assert.True(settings.CanCancelCleanup);
    settings.CancelCleanup();
    await cleanup.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    Assert.False(settings.CanCancelCleanup);
    Assert.True(maintenance.State.IsIdle);
    Assert.Contains("重新检查", settings.Status);
    Assert.Null(settings.CleanupPlan);
  }

  private static ManagedEnvironmentSettings NewSettings(IManagedEnvironmentClient manager) =>
    new(manager, (_, _) => Task.CompletedTask, () => null, new ProductMaintenanceCoordinator());

  [Fact]
  public async Task DisconnectedRunningProbePreservesEnvironmentAndReturnsRecoverableError()
  {
    var manager = new WaitingManager { ActiveId = "environment" };
    var transport = new UnavailableTransport();
    var client = new InferenceHttpClient(new Uri("http://127.0.0.1:12345"), "test", transport);
    var qr = new QrCodeHttpClient(new Uri("http://127.0.0.1:12345"), "test");
    var process = new InferenceSupervisorProcess(new InferenceSupervisorOptions(
      "unused", [], Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "unused.log"),
      TimeSpan.FromSeconds(1), new HashSet<string>()), "test");
    await using var session = new ManagedEnvironmentSession("environment", 1, process, client, qr,
      new RuntimeStatusSnapshot
      {
        InstanceId = "test",
        BackendVersion = "test",
        ServiceState = RuntimeServiceState.Ready,
        Profile = new RuntimeProfileStatus { ProfileId = "test", Accelerator = RuntimeAccelerator.Cpu }
      });
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => ("environment", 1), new ProductMaintenanceCoordinator(), () => session);

    InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      settings.RefreshAsync(TestContext.Current.CancellationToken));

    Assert.IsType<HttpRequestException>(error.InnerException);
    Assert.Equal(1, transport.Calls);
    Assert.Equal("environment", settings.Snapshot?.ActiveId);
    // 严格刷新失败如实报错，同时活动环境标注未核验而非回退 not_started。
    Assert.Equal("unverified", settings.Snapshot?.Environments[0].ServiceState);
    Assert.Equal("无法读取活动环境状态，请刷新或重新切换环境。", settings.Status);
    Assert.False(settings.IsBusy);
  }

  [Fact]
  public async Task MutatingOperationsKeepRunningEvidenceForActiveEnvironment()
  {
    string root = CreateReadyEchoScript();
    InferenceSupervisorProcess process = CreateReadyEchoProcess(root);
    try
    {
      await process.StartAsync(TestContext.Current.CancellationToken);
      var client = new InferenceHttpClient(
        new Uri("http://127.0.0.1:1"), "test", new HealthyTransport());
      var qr = new QrCodeHttpClient(new Uri("http://127.0.0.1:1"), "test");
      await using var session = new ManagedEnvironmentSession(
        "environment", 1, process, client, qr, ReadyStatus());
      var manager = new MutableManager();
      var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
        () => ("environment", 1), new ProductMaintenanceCoordinator(), () => session);

      await settings.RefreshAsync(TestContext.Current.CancellationToken);
      ManagedEnvironment active = ActiveEnvironment(settings);
      Assert.Equal("ready", active.EngineState);
      // Paddle ONNX 环境的模型在 residency 出现前保持 not_checked，
      // 不因随包 small 默认模型流报 ready。
      Assert.Equal("not_checked", active.ModelState);
      Assert.Equal("ready", active.ServiceState);

      // 变更（删除非活动环境）后的列表重读不得把仍在运行的活动环境
      // 回退成 unverified/not_checked/not_started。
      await settings.DeleteAsync("spare", TestContext.Current.CancellationToken);
      Assert.Equal("ready", ActiveEnvironment(settings).ServiceState);
      Assert.DoesNotContain(settings.Snapshot!.Environments, item => item.Id == "spare");
    }
    finally
    {
      process.Dispose();
      DeleteScriptDirectory(root);
    }
  }

  [Fact]
  public async Task PostActivationRefreshUsesAttemptCancellationToReleaseSettingsSwitchGate()
  {
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var project = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var projectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var settingsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var beginSwitch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var maintenance = new ProductMaintenanceCoordinator();
    ManagedEnvironmentSettings settings = null!;
    var coordinator = new RuntimeDemandCoordinator(async (_, token) =>
    {
      connected.TrySetResult();
      await project.Task;
      projectionEntered.TrySetResult();
      await App.RefreshEnvironmentSettingsAfterActivationAsync(settings, token);
      token.ThrowIfCancellationRequested();
    }, () => Task.CompletedTask, maintenance, TestContext.Current.CancellationToken);
    settings = new ManagedEnvironmentSettings(new MutableManager(), async (_, token) =>
    {
      settingsEntered.TrySetResult();
      await beginSwitch.Task;
      await coordinator.SwitchAsync(_ => Task.CompletedTask, token);
    }, () => null, maintenance);
    await settings.RefreshAsync(TestContext.Current.CancellationToken);
    Task demand = coordinator.EnsureReadyAsync(TestContext.Current.CancellationToken);
    await connected.Task;
    Task switching = settings.SwitchAsync("environment", TestContext.Current.CancellationToken);
    try
    {
      await settingsEntered.Task;
      project.TrySetResult();
      await projectionEntered.Task;
      Assert.False(demand.IsCompleted);
      beginSwitch.TrySetResult();
      await switching.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => demand);
      Assert.False(settings.IsBusy);
      Assert.Contains("已切换", settings.Status, StringComparison.Ordinal);
    }
    finally
    {
      project.TrySetResult();
      beginSwitch.TrySetResult();
      try { await demand; } catch (Exception) { }
      try { await switching; } catch (Exception) { }
    }
  }
  [Fact]
  public async Task PostActivationRefreshProjectsRunningEvidenceAndSurvivesProbeFailure()
  {
    string root = CreateReadyEchoScript();
    InferenceSupervisorProcess process = CreateReadyEchoProcess(root);
    try
    {
      await process.StartAsync(TestContext.Current.CancellationToken);
      var client = new InferenceHttpClient(
        new Uri("http://127.0.0.1:1"), "test", new HealthyTransport());
      var qr = new QrCodeHttpClient(new Uri("http://127.0.0.1:1"), "test");
      await using var session = new ManagedEnvironmentSession(
        "environment", 1, process, client, qr, ReadyStatus());
      await using var disconnected = new ManagedEnvironmentSession(
        "environment", 1,
        new InferenceSupervisorProcess(new InferenceSupervisorOptions(
          "unused", [], root, Path.Combine(root, "unused.log"),
          TimeSpan.FromSeconds(1), new HashSet<string>()), "test"),
        new InferenceHttpClient(new Uri("http://127.0.0.1:12345"), "test",
          new UnavailableTransport()),
        new QrCodeHttpClient(new Uri("http://127.0.0.1:12345"), "test"),
        ReadyStatus());
      ManagedEnvironmentSession? current = null;
      int lists = 0;
      var manager = new MutableManager { BeforeList = () => { lists++; return Task.CompletedTask; } };
      var settings = new ManagedEnvironmentSettings(manager,
        (_, _) => Task.CompletedTask, () => ("environment", 1),
        new ProductMaintenanceCoordinator(), () => current);

      await App.RefreshEnvironmentSettingsAfterActivationAsync(settings, TestContext.Current.CancellationToken);
      // 首次启动快照尚未读取（null）也必须刷新：否则激活后环境列表
      // 永不装载（预构建树冒烟回归）。
      Assert.Equal(1, lists);

      // 启动竞争：设置页在 Supervisor 激活前刷新，只有管理器投影。
      await settings.RefreshAsync(TestContext.Current.CancellationToken);
      Assert.Equal("not_started", ActiveEnvironment(settings).ServiceState);
      Assert.Equal(2, lists);

      // 激活完成后的 App 侧刷新补齐真实运行证据。
      current = session;
      await App.RefreshEnvironmentSettingsAfterActivationAsync(
        settings, CancellationToken.None);
      ManagedEnvironment projected = ActiveEnvironment(settings);
      Assert.Equal("ready", projected.EngineState);
      Assert.Equal("ready", projected.ServiceState);
      Assert.Equal(1, projected.Revision);

      // 刷新失败必须非致命：探针断开不抛出，也不拆已健康会话；活动环境
      // 如实标注未核验而不是伪造 ready 或回退 not_started。
      current = disconnected;
      await App.RefreshEnvironmentSettingsAfterActivationAsync(
        settings, CancellationToken.None);
      Assert.False(settings.IsBusy);
      Assert.Equal("无法读取活动环境状态，请刷新或重新切换环境。", settings.Status);
      ManagedEnvironment unverifiable = ActiveEnvironment(settings);
      Assert.Equal("unverified", unverifiable.EngineState);
      Assert.Equal("unverified", unverifiable.ServiceState);
      Assert.Equal("无法读取活动环境状态，请刷新或重新切换环境。", unverifiable.Reason);
    }
    finally
    {
      process.Dispose();
      DeleteScriptDirectory(root);
    }
  }

  [Fact]
  public async Task SetSourcesSavesWithoutDownloadingAndMarksRunningModelNextLaunch()
  {
    string root = CreateReadyEchoScript();
    InferenceSupervisorProcess process = CreateReadyEchoProcess(root);
    try
    {
      await process.StartAsync(TestContext.Current.CancellationToken);
      var client = new InferenceHttpClient(
        new Uri("http://127.0.0.1:1"), "test", new HealthyTransport());
      var qr = new QrCodeHttpClient(new Uri("http://127.0.0.1:1"), "test");
      await using var session = new ManagedEnvironmentSession(
        "environment", 1, process, client, qr, ReadyStatus());
      var manager = new MutableManager();
      var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
        () => ("environment", 1), new ProductMaintenanceCoordinator(), () => session);

      // 只写配置：不下载/不重启；运行中环境的模型偏好标注下次启动生效。
      await settings.SetSourcesAsync("environment", "modelscope",
        TestContext.Current.CancellationToken);
      Assert.Equal("environment|modelscope", manager.SourceSaves.Single());
      Assert.Contains("仅用于模型下载", settings.Status);
      Assert.Contains("不会改变当前运行中的服务", settings.Status);
      Assert.Contains("下次启动", settings.Status);

      // 全局保存（environmentId=null）同样改变运行中环境的模型来源解析：
      // 有活动会话且请求包含模型来源时提示下次启动生效；清除模型来源
      // 时不叠加提示。
      await settings.SetSourcesAsync(null, null,
        TestContext.Current.CancellationToken);
      Assert.Equal("<global>|<null>", manager.SourceSaves[1]);
      Assert.DoesNotContain("下次启动", settings.Status);

      await settings.SetSourcesAsync(null, "modelscope",
        TestContext.Current.CancellationToken);
      Assert.Equal("<global>|modelscope", manager.SourceSaves[2]);
      Assert.Contains("下载来源已保存并统一所有环境", settings.Status);
      Assert.Contains("下次启动", settings.Status);
    }
    finally
    {
      process.Dispose();
      DeleteScriptDirectory(root);
    }
  }

  private static ManagedEnvironment ActiveEnvironment(ManagedEnvironmentSettings settings) =>
    settings.Snapshot?.Environments.Single(item => item.Id == "environment")
      ?? throw new InvalidOperationException("Active environment is missing.");

  private static RuntimeStatusSnapshot ReadyStatus() => new()
  {
    InstanceId = "sup-test",
    ServiceState = RuntimeServiceState.Ready,
    BackendVersion = "0.7.0",
    Profile = new RuntimeProfileStatus
    {
      ProfileId = "win-x64-cpu",
      Accelerator = RuntimeAccelerator.Cpu,
    },
  };

  private static Wire.Health PaddleReadyHealth() => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-test",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities = [RuntimeSelectionService.EngineSelectionCapability],
    CapabilityDescriptors = [new Wire.CapabilityDescriptor
    {
      Name = RuntimeSelectionService.EngineSelectionCapability,
      Lifecycle = "active",
      IntroducedIn = "2.0.0",
      DeprecatedIn = null,
      SunsetAt = null,
      Replacement = null,
      OcrEngineCatalog = new Wire.OcrEngineCatalog
      {
        Engines = [new Wire.OcrEngineDescriptor
        {
          Id = Wire.OcrEngineId.Paddleocr,
          Availability = Wire.OcrEngineAvailability.Ready,
          IncludedInBase = false,
          ReasonCode = null,
          RequiredComponent = null,
        }],
      },
    }],
  };

  private static string CreateReadyEchoScript()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-managed-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    const string envelope =
      """{"ready":true,"pid":4321,"port":5432,"instance_id":"sup-test","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["ocr.recognition.v2"]}""";
    File.WriteAllLines(Path.Combine(root, "fake-supervisor.cmd"),
      ["@echo off", $"echo {envelope}", "ping 127.0.0.1 -n 60 >nul"]);
    return root;
  }

  private static InferenceSupervisorProcess CreateReadyEchoProcess(string root)
  {
    string commandPrompt = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    return new InferenceSupervisorProcess(new InferenceSupervisorOptions(
      commandPrompt, ["/d", "/s", "/c", "fake-supervisor.cmd"], root,
      Path.Combine(root, "supervisor.log"), TimeSpan.FromSeconds(5),
      new HashSet<string>()), "tok");
  }

  /// <summary>
  /// 与 VibeOCR.Platform.Tests.TestDirectory 相同的清理语义：仅对有真实成因的
  /// sharing violation（fake supervisor 进程树句柄释放延迟）做有界等待；
  /// 其余异常如实传播，不吞错。
  /// </summary>
  private static void DeleteScriptDirectory(string root)
  {
    const int sharingViolationHResult = unchecked((int)0x80070020);
    for (int attempt = 1; ; attempt++)
    {
      try
      {
        Directory.Delete(root, recursive: true);
        return;
      }
      catch (IOException error) when (
          error.HResult == sharingViolationHResult && attempt < 20)
      {
        Thread.Sleep(TimeSpan.FromMilliseconds(50));
      }
    }
  }

  [Fact]
  public async Task InitialListLoadFactsFollowAttemptAndSnapshotNotStatusText()
  {
    // 启动期首次读取可能失败：typed 事实与 Status 文案无关——已尝试但
    // 无快照，界面不得自动循环重试；显式重试成功后快照就位。
    var manager = new MutableManager { BeforeList = () => Task.FromException(new HttpRequestException("startup busy")) };
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, new ProductMaintenanceCoordinator());
    Assert.False(settings.SnapshotLoaded);
    Assert.False(settings.ListFetchAttempted);
    var error = await Assert.ThrowsAsync<HttpRequestException>(
      () => settings.RefreshAsync(TestContext.Current.CancellationToken));
    Assert.Contains("startup busy", error.Message, StringComparison.Ordinal);
    Assert.True(settings.ListFetchAttempted);
    Assert.False(settings.SnapshotLoaded);

    manager.BeforeList = null;
    await settings.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.True(settings.ListFetchAttempted);
    Assert.True(settings.SnapshotLoaded);
  }

  /// <summary>
  /// 回应 /v2/health、/v2/runtime/residency、/v2/runtime/status 的真实探针
  /// 路由，返回与就绪包一致的 sup-test 实例证据。
  /// </summary>
  private sealed class HealthyTransport : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      string json = request.RequestUri?.AbsolutePath switch
      {
        "/v2/health" => JsonSerializer.Serialize(PaddleReadyHealth()),
        "/v2/runtime/residency" => HttpV2Json.Serialize(new ResidencyStatus()),
        "/v2/runtime/status" => HttpV2Json.Serialize(ReadyStatus()),
        _ => throw new HttpRequestException($"Unexpected probe path: {request.RequestUri}"),
      };
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
      });
    }
  }

  /// <summary>
  /// 模拟冻结管理器：每次 list 都返回未叠加运行证据的持久状态，
  /// 并支持来源设置/删除变更。
  /// </summary>
  private sealed class MutableManager : IManagedEnvironmentClient
  {
    public List<string> SourceSaves { get; } = [];
    public bool SupportsEnvironmentCleanup => true;
    public int CleanupCalls { get; private set; }
    public Func<CancellationToken, Task>? BeforeCleanup { get; set; }
    public ManagedCleanupPlan? CleanupPreview { get; set; }
    public Task<ManagedCleanupPlan> PreviewEnvironmentCleanupAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(CleanupPreview ?? new ManagedCleanupPlan(new string('a', 32),
        [new ManagedCleanupItem("residual:fixture", "residual", "fixture", null, 12, true, "owned", ["fixture"], PathCount: 1),
         new ManagedCleanupItem("environment:protected", "environment", "protected", "environment", null, false, "active", [])],
        "logical_bytes", "不保证物理释放"));
    public async Task<ManagedCleanupResult> RunEnvironmentCleanupAsync(string planId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken = default)
    {
      CleanupCalls++;
      if (BeforeCleanup is not null) await BeforeCleanup(cancellationToken);
      return new ManagedCleanupResult(planId, [new ManagedCleanupResultItem(itemIds[0], "failed", "file in use", 12)], "logical_bytes");
    }
    public Func<Task>? BeforeList { get; set; }

    // 与生产 list payload 一致：配方目录随列表同步，宿主据此核验未知配方。
    private static readonly IReadOnlyList<ManagedEnvironmentRecipe> Catalog =
    [
      new ManagedEnvironmentRecipe("paddleocr-ort-directml", "PaddleOCR ONNX + MinerU · Windows DirectML",
        ["text"], "cpu", "directml"),
    ];

    private readonly List<ManagedEnvironment> environments =
    [
      new ManagedEnvironment("environment", "活动环境", 1, "venv", "installed", "python",
        "ready", "installed", "unverified", "not_checked", "not_started", null,
        Recipe: "paddleocr-ort-directml", Engine: "paddle", Family: "paddle-onnx",
        ResolvedSources:
        [
          new ManagedEnvironmentResolvedSource("paddleocr_model_registry", "paddleocr-modelscope", "ModelScope", "product_default"),
          new ManagedEnvironmentResolvedSource("mineru_model_registry", "mineru-modelscope", "ModelScope", "product_default"),
        ]),
      new ManagedEnvironment("spare", "备用环境", 1, "venv", "installed", "python",
        "ready", "installed", "unverified", "not_checked", "not_started", null,
        Recipe: "paddleocr-ort-directml", Engine: "paddle", Family: "paddle-onnx"),
    ];

    public async Task<ManagedEnvironmentList> ListEnvironmentsAsync(
      CancellationToken cancellationToken = default)
    {
      if (BeforeList is not null) await BeforeList();
      return new ManagedEnvironmentList("environment", 1, [.. environments], Recipes: Catalog);
    }

    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
      string? environmentId, string? modelSourceId,
      CancellationToken cancellationToken = default)
    {
      SourceSaves.Add(
        $"{environmentId ?? "<global>"}|{modelSourceId ?? "<null>"}");
      return Task.FromResult(new ManagedEnvironmentList("environment", 1, [.. environments],
        DefaultSourceIds: modelSourceId is null ? [] : [modelSourceId], Recipes: Catalog));
    }

    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(
      string environmentId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
      PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteEnvironmentAsync(string environmentId,
      CancellationToken cancellationToken = default)
    {
      environments.RemoveAll(item => item.Id == environmentId);
      return Task.CompletedTask;
    }
  }

  private sealed class UnavailableTransport : HttpMessageHandler
  {
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      Calls++;
      return Task.FromException<HttpResponseMessage>(new HttpRequestException("Disconnected"));
    }
  }

  /// <summary>
  /// 可控时序的冻结管理器：按需模拟活动指针与列表内容。
  /// </summary>
  private sealed class WaitingManager : IManagedEnvironmentClient
  {
    public string? ActiveId { get; init; }

    private ManagedEnvironment current = new("environment", "保留环境", 1, "venv", "empty", "python",
      "ready", "empty", "unavailable", "not_checked", "not_started", null);

    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(
      CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentList(ActiveId, 0, [current],
        Recipes: [new ManagedEnvironmentRecipe("paddleocr-ort-directml", "PaddleOCR ONNX · CPU")]));

    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
      string? environmentId, string? modelSourceId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(string environmentId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
      PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }
}
