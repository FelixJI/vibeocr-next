using System.Net;
using System.Text;
using System.Text.Json;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.Settings;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class ManagedEnvironmentSettingsTests
{
  [Fact]
  public void RunningCatalogAndResidencyDistinguishPendingFromReadyModel()
  {
    var environment = new ManagedEnvironment("environment", "Paddle", 2, "venv", "installed", "python",
      "ready", "installed", "unverified", "not_checked", "not_started", null,
      Recipe: "paddleocr-cpu");
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
    Assert.Equal("pending", pending.ModelState);
    Assert.Null(pending.ActualDevice);

    ManagedEnvironment unrelated = ManagedEnvironmentSettings.ProjectRunning(environment, health,
      new ResidencyStatus { VramUsedMb = 4096, Entries = [new ResidencyEntry
      {
        Pipeline = "MinerU", Kind = ResidencyKind.SoftTtl,
        RecognitionMode = RecognitionMode.MineruDocument,
        ResourceKind = RecognitionResourceKind.Process,
      }] }, status);
    Assert.Equal("pending", unrelated.ModelState);
    Assert.Null(unrelated.ActualDevice);

    ManagedEnvironment evicted = ManagedEnvironmentSettings.ProjectRunning(environment, health,
      new ResidencyStatus { Entries = [new ResidencyEntry
      {
        Pipeline = "OCR", Kind = ResidencyKind.Evicted,
        RecognitionMode = RecognitionMode.PaddleText,
        ResourceKind = RecognitionResourceKind.Model,
      }] }, status);
    Assert.Equal("pending", evicted.ModelState);

    ManagedEnvironment ready = ManagedEnvironmentSettings.ProjectRunning(environment, health,
      new ResidencyStatus { Entries = [new ResidencyEntry
      {
        Pipeline = "OCR", Kind = ResidencyKind.SoftTtl,
        RecognitionMode = RecognitionMode.PaddleText,
        ResourceKind = RecognitionResourceKind.Model,
      }] },
      status);
    Assert.Equal("ready", ready.ModelState);
    Assert.Null(ready.ActualDevice);
    Assert.Equal("ready", ready.ServiceState);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("pypi")]
  public async Task StaleConfirmationCannotInstallTheCurrentPreview(string? sourceId)
  {
    var manager = new WaitingManager();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, new ProductMaintenanceCoordinator());
    await settings.PreviewAsync("environment", "rapidocr-cpu", sourceId,
      TestContext.Current.CancellationToken);

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
      settings.InstallAsync("previous-plan", sourceId, TestContext.Current.CancellationToken));

    Assert.False(manager.Started.Task.IsCompleted);
    Assert.Equal("plan", settings.Plan?.PlanId);
  }
  [Fact]
  public async Task CancelInstallStopsManagerAndReleasesProductLease()
  {
    var manager = new WaitingManager();
    var maintenance = new ProductMaintenanceCoordinator();
    int installAttempts = 0;
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, maintenance, installAttempted: () => installAttempts++);
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    Task install = settings.InstallAsync("plan", "pypi", TestContext.Current.CancellationToken);
    await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    Assert.True(settings.CanCancelInstall);
    Assert.Equal(ProductMaintenanceOwner.RuntimeMaintenance, maintenance.State.ActiveOwner);
    await settings.CancelAndWaitForInstallAsync();
    await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    Assert.True(manager.Cancelled);
    Assert.Equal(1, installAttempts);
    Assert.False(settings.CanCancelInstall);
    Assert.True(maintenance.State.IsIdle);
    Assert.Equal("安装已取消；原环境保持不变，取消详情可在该环境记录中查看。", settings.Status);
    Assert.Equal("environment", settings.Snapshot?.Environments[0].Id);
  }

  [Fact]
  public async Task FailedInstallRefreshesDurableReasonWithoutShowingRawManagerError()
  {
    var failure = new ManagedEnvironmentInstallFailure("failed", 1, "rapidocr-cpu",
      "network_error", "check_source_and_retry", "下载源连接失败。");
    var manager = new WaitingManager
    {
      InstallError = new InvalidOperationException("https://user:secret@example.invalid C:/private"),
      Failure = failure,
    };
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, new ProductMaintenanceCoordinator());
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
      settings.InstallAsync("plan", "pypi", TestContext.Current.CancellationToken));
    Assert.Equal("安装未完成；失败原因请查看该环境记录。", settings.Status);
    Assert.Same(failure, settings.Snapshot?.Environments[0].LastInstallFailure);
    await settings.RefreshAsync(TestContext.Current.CancellationToken);
    Assert.Same(failure, settings.Snapshot?.Environments[0].LastInstallFailure);
  }

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
      Assert.Equal("not_applicable", active.ModelState);
      Assert.Equal("ready", active.ServiceState);

      // 旧代码在 Create/Install/Delete/Repair 后直接重置为管理器列表，
      // 把仍在运行的活动环境回退成 unverified/not_checked/not_started。
      await settings.CreateAsync("新建环境", TestContext.Current.CancellationToken);
      Assert.Equal("ready", ActiveEnvironment(settings).ServiceState);
      Assert.Contains(settings.Snapshot!.Environments,
        item => item.Id == "fresh" && item.ServiceState == "not_started");

      await settings.PreviewAsync("fresh", "rapidocr-cpu", "pypi",
        TestContext.Current.CancellationToken);
      await settings.InstallAsync("plan", "pypi", TestContext.Current.CancellationToken);
      Assert.Equal("ready", ActiveEnvironment(settings).ServiceState);
      Assert.Equal("installed",
        settings.Snapshot!.Environments.Single(item => item.Id == "fresh").Status);

      await settings.RepairEmptyAsync("fresh", TestContext.Current.CancellationToken);
      Assert.Equal("ready", ActiveEnvironment(settings).ServiceState);

      await settings.DeleteAsync("fresh", TestContext.Current.CancellationToken);
      Assert.Equal("ready", ActiveEnvironment(settings).ServiceState);
      Assert.DoesNotContain(settings.Snapshot!.Environments, item => item.Id == "fresh");
    }
    finally
    {
      process.Dispose();
      DeleteScriptDirectory(root);
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
      var settings = new ManagedEnvironmentSettings(new MutableManager(),
        (_, _) => Task.CompletedTask, () => ("environment", 1),
        new ProductMaintenanceCoordinator(), () => current);

      // 启动竞争：设置页在 Supervisor 激活前刷新，只有管理器投影。
      await settings.RefreshAsync(TestContext.Current.CancellationToken);
      Assert.Equal("not_started", ActiveEnvironment(settings).ServiceState);

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

      await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
        TestContext.Current.CancellationToken);
      Assert.NotNull(settings.Plan);

      // 只写配置：不下载/不安装/不重启；运行中环境的模型偏好标注下次启动生效。
      await settings.SetSourcesAsync("environment", null, "modelscope",
        TestContext.Current.CancellationToken);
      Assert.Equal("environment|<null>|modelscope", manager.SourceSaves.Single());
      Assert.Null(settings.Plan);
      Assert.Contains("不会下载或安装", settings.Status);
      Assert.Contains("下次启动", settings.Status);

      // 全局默认保存不针对运行中的环境，不叠加下次启动提示。
      await settings.SetSourcesAsync(null, "pypi", null,
        TestContext.Current.CancellationToken);
      Assert.Equal("<global>|pypi|<null>", manager.SourceSaves[1]);
      Assert.DoesNotContain("下次启动", settings.Status);
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

  private static Wire.Health RapidReadyHealth() => new()
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
          Id = Wire.OcrEngineId.Rapidocr,
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
        "/v2/health" => JsonSerializer.Serialize(RapidReadyHealth()),
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
  /// 并支持创建/安装/修复/删除变更。
  /// </summary>
  private sealed class MutableManager : IManagedEnvironmentClient
  {
    public List<string> SourceSaves { get; } = [];

    private readonly List<ManagedEnvironment> environments =
    [
      new ManagedEnvironment("environment", "活动环境", 1, "venv", "installed", "python",
        "ready", "installed", "unverified", "not_checked", "not_started", null,
        Recipe: "rapidocr-cpu",
        ResolvedSources:
        [
          new ManagedEnvironmentResolvedSource("package_index", "tuna-pypi", "TUNA PyPI 镜像", "product_default"),
          new ManagedEnvironmentResolvedSource("model_registry", null, null, "product_default"),
        ]),
    ];

    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(
      CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentList("environment", 1, [.. environments]));

    public Task<ManagedEnvironment> CreateEnvironmentAsync(
      string name, CancellationToken cancellationToken = default)
    {
      var created = new ManagedEnvironment("fresh", name, 1, "venv", "empty", "python",
        "ready", "empty", "unverified", "not_checked", "not_started", null);
      environments.Add(created);
      return Task.FromResult(created);
    }

    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
      string? environmentId, string? packageSourceId, string? modelSourceId,
      CancellationToken cancellationToken = default)
    {
      SourceSaves.Add(
        $"{environmentId ?? "<global>"}|{packageSourceId ?? "<null>"}|{modelSourceId ?? "<null>"}");
      return Task.FromResult(new ManagedEnvironmentList("environment", 1, [.. environments],
        DefaultSourceIds: packageSourceId is null ? [] : [packageSourceId]));
    }

    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
      string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentPlan("plan", environmentId, 1, 1, recipe,
        sourceIds ?? ["tuna-pypi"], RequestedSourceIds: sourceIds,
        Sources:
        [
          new ManagedEnvironmentPlanSource(
            (sourceIds ?? ["tuna-pypi"])[0], "package_index",
            sourceIds is null ? "TUNA PyPI 镜像" : "PyPI 官方源",
            "https://example.invalid", sourceIds is not null,
            sourceIds is null ? "product_default" : "environment_override",
            "online_index"),
        ],
        DependencyOrigin: "online_index", PythonOrigin: "product_bundle",
        RuntimeWheelOrigin: "product_bundle"));

    public Task<ManagedEnvironment> InstallEnvironmentAsync(
      ManagedEnvironmentPlan plan, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default)
    {
      int index = environments.FindIndex(item => item.Id == plan.EnvironmentId);
      environments[index] = environments[index] with
      {
        Status = "installed",
        DependencyState = "installed",
      };
      return Task.FromResult(environments[index]);
    }

    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(
      string environmentId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
      PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(
      string environmentId, CancellationToken cancellationToken = default) =>
      Task.FromResult(environments.Single(item => item.Id == environmentId));

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

  private sealed class WaitingManager : IManagedEnvironmentClient
  {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Cancelled { get; private set; }
    public string? ActiveId { get; init; }
    public Exception? InstallError { get; init; }
    public ManagedEnvironmentInstallFailure? Failure { get; init; }

    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentList(ActiveId, 0, [
        new ManagedEnvironment("environment", "保留环境", 1, "venv", "empty", "python",
          "ready", "empty", "unavailable", "not_checked", "not_started", null,
          LastInstallFailure: Failure)
      ]));

    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
      string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentPlan("plan", environmentId, 1, 0, recipe,
        sourceIds ?? ["tuna-pypi"], RequestedSourceIds: sourceIds));

    public async Task<ManagedEnvironment> InstallEnvironmentAsync(
      ManagedEnvironmentPlan plan, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default)
    {
      if (InstallError is not null) throw InstallError;
      Started.SetResult();
      try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
      catch (OperationCanceledException)
      {
        Cancelled = true;
        throw;
      }
      throw new InvalidOperationException("Install unexpectedly completed.");
    }

    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
      string? environmentId, string? packageSourceId, string? modelSourceId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ManagedEnvironment> CreateEnvironmentAsync(string name, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(string environmentId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
      PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(string environmentId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }
}
