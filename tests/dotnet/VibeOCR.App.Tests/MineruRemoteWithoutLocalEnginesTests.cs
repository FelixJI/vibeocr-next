using System.Text.Json;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 未安装任何可选识别组件时的 MinerU 远程模式契约：
/// 远程模式宿主入口（PrepareRemoteHostCommand）只复用 Runtime 权威事务
/// （兼容查询/离线安装/切换），会话内重试复用同一专用环境 id，不按名称
/// 复用用户环境、不触碰用户空环境；准备期间跨步骤禁重入；入口打通后
/// 同一会话可完成“保存远程配置 → 触发真实预加载”链路；失败状态经
/// RunAsync seam 用户可见；远程准备失败仅对 Runtime 白名单化的
/// ValidationError reason 给出操作建议，不回显服务正文/URL/Key。
/// </summary>
public sealed class MineruRemoteWithoutLocalEnginesTests
{
  // ------------------------------------------------------------------
  // 宿主入口编排
  // ------------------------------------------------------------------

  [Fact]
  public async Task PrepareRemoteHostReusesInstalledBaseEnvironmentWithoutReinstall()
  {
    var inference = new RemoteHostInferenceClient();
    var manager = new RemoteHostManager(
      new ManagedEnvironmentList("base", 1,
        [ManagedEnvironment("base", "基础环境", "installed")],
        Recipes: Catalog()));
    manager.Compatibility = new ManagedEnvironmentQueryResult(
      "rapidocr-cpu",
      Selected: new ManagedEnvironmentSelection("base", 1, "rapidocr-cpu", "active_environment"));
    var environments = new ManagedEnvironmentSettings(manager,
      (id, _) => { manager.SetActive(id); return Task.CompletedTask; },
      () => null, new ProductMaintenanceCoordinator());
    await using var handler = CreateHandler(inference, environments, out string root);
    try
    {
      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(new PrepareRemoteHostCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(outcome.Error);
      await WaitUntilAsync(() => manager.Switches.Count == 1,
        $"switch was not started: {environments.Status}");

      // 已有可复用环境：只切换启动，不新建、不预览、不安装。
      Assert.Equal(["base"], manager.Switches);
      Assert.Empty(manager.CreatedNames);
      Assert.Empty(manager.Previews);
      Assert.Empty(manager.Installs);
      Assert.Equal("base", manager.List.ActiveId);
    }
    finally { await handler.DisposeAsync(); Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task PrepareRemoteHostCreatesOwnEnvironmentAndLeavesUserEnvironmentsAlone()
  {
    var inference = new RemoteHostInferenceClient();
    var manager = new RemoteHostManager(
      new ManagedEnvironmentList("user", 1,
        [ManagedEnvironment("user", "我的环境", "empty")],
        Recipes: Catalog()));
    var environments = new ManagedEnvironmentSettings(manager,
      (id, _) => { manager.SetActive(id); return Task.CompletedTask; },
      () => null, new ProductMaintenanceCoordinator());
    await using var handler = CreateHandler(inference, environments, out string root);
    try
    {
      await handler.ExecuteAsync(new PrepareRemoteHostCommand(),
        TestContext.Current.CancellationToken);
      await WaitUntilAsync(() => manager.Switches.Count == 1, "switch was not started");

      // 只使用本次创建的专用环境：create → 离线基础配方预览 → 安装 → 切换。
      Assert.Equal(["远程基础服务"], manager.CreatedNames);
      string target = Assert.Single(manager.Switches);
      Assert.NotEqual("user", target);
      Assert.Equal([(target, "rapidocr-cpu")], manager.Previews);
      Assert.Equal([("plan-1", target)], manager.Installs);
      ManagedEnvironment user = Assert.Single(
        manager.List.Environments, item => item.Id == "user");
      Assert.Equal("empty", user.Status);
      Assert.Equal(target, manager.List.ActiveId);
    }
    finally { await handler.DisposeAsync(); Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task PrepareRemoteHostRetriesReuseSessionEnvironmentWithoutPileUp()
  {
    var inference = new RemoteHostInferenceClient();
    var manager = new RemoteHostManager(
      new ManagedEnvironmentList(null, 0, [], Recipes: Catalog()))
    { InstallThrows = new InvalidOperationException("offline pack unavailable") };
    var environments = new ManagedEnvironmentSettings(manager,
      (id, _) => { manager.SetActive(id); return Task.CompletedTask; },
      () => null, new ProductMaintenanceCoordinator());
    await using var handler = CreateHandler(inference, environments, out string root);
    try
    {
      await handler.ExecuteAsync(new PrepareRemoteHostCommand(),
        TestContext.Current.CancellationToken);
      await WaitUntilAsync(
        () => !environments.IsBusy &&
          environments.Status.Contains("安装未完成", StringComparison.Ordinal),
        "install failure was not projected");
      Assert.Empty(manager.Switches);

      // 会话内重试：复用同一专用环境 id 续装，不追加“远程基础服务 2”。
      manager.InstallThrows = null;
      await handler.ExecuteAsync(new PrepareRemoteHostCommand(),
        TestContext.Current.CancellationToken);
      await WaitUntilAsync(() => manager.Switches.Count == 1, "retry switch was not started");

      Assert.Equal(["远程基础服务"], manager.CreatedNames);
      string target = Assert.Single(manager.Switches);
      Assert.Equal(target, manager.CreatedTargetId);
      Assert.Equal(target, manager.List.ActiveId);
      Assert.Equal("installed", Assert.Single(
        manager.List.Environments, item => item.Id == target).Status);
    }
    finally { await handler.DisposeAsync(); Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task SecondClickWhilePreparingDoesNotStartASecondChain()
  {
    var inference = new RemoteHostInferenceClient();
    var manager = new RemoteHostManager(
      new ManagedEnvironmentList(null, 0, [], Recipes: Catalog()))
    {
      FindGate = new TaskCompletionSource<ManagedEnvironmentQueryResult>(
        TaskCreationOptions.RunContinuationsAsynchronously),
    };
    var environments = new ManagedEnvironmentSettings(manager,
      (id, _) => { manager.SetActive(id); return Task.CompletedTask; },
      () => null, new ProductMaintenanceCoordinator());
    await using var handler = CreateHandler(inference, environments, out string root);
    SettingsWorkbenchState? published = null;
    handler.StateChanged += state =>
    {
      if (state is SettingsWorkbenchState snapshot) published = snapshot;
    };
    try
    {
      WorkbenchCommandOutcome first = await handler.ExecuteAsync(
        new PrepareRemoteHostCommand(), TestContext.Current.CancellationToken);
      Assert.Null(first.Error);
      // 首次回执即投影准备中的忙碌：跨步骤空窗也锁定环境区。
      SettingsWorkbenchState firstState = Assert.IsType<SettingsWorkbenchState>(published);
      Assert.True(firstState.EnvironmentBusy == true);

      // 第一次仍在兼容查询挂起：第二次点击必须被拒绝，不新建、不安装。
      WorkbenchCommandOutcome second = await handler.ExecuteAsync(
        new PrepareRemoteHostCommand(), TestContext.Current.CancellationToken);
      Assert.NotNull(second.Error);
      Assert.Empty(manager.CreatedNames);
      Assert.Empty(manager.Installs);
      Assert.Empty(manager.Switches);

      manager.FindGate.SetResult(new ManagedEnvironmentQueryResult("rapidocr-cpu"));
      await WaitUntilAsync(() => manager.Switches.Count == 1, "first chain did not finish");
      Assert.Equal(["远程基础服务"], manager.CreatedNames);
      Assert.Single(manager.Installs);
    }
    finally
    {
      manager.FindGate.TrySetResult(new ManagedEnvironmentQueryResult("rapidocr-cpu"));
      await handler.DisposeAsync();
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task FailedBaseInstallDoesNotSwitchEnvironment()
  {
    var inference = new RemoteHostInferenceClient();
    var manager = new RemoteHostManager(
      new ManagedEnvironmentList(null, 0, [], Recipes: Catalog()))
    { InstallThrows = new InvalidOperationException("offline pack unavailable") };
    var environments = new ManagedEnvironmentSettings(manager,
      (id, _) => { manager.SetActive(id); return Task.CompletedTask; },
      () => null, new ProductMaintenanceCoordinator());
    await using var handler = CreateHandler(inference, environments, out string root);
    try
    {
      await handler.ExecuteAsync(new PrepareRemoteHostCommand(),
        TestContext.Current.CancellationToken);
      await WaitUntilAsync(
        () => environments.Status.Contains("安装未完成", StringComparison.Ordinal),
        "install failure was not projected");

      // 安装失败：不切换、活动环境不变；失败经状态 seam 用户可见。
      Assert.Empty(manager.Switches);
      Assert.Null(manager.List.ActiveId);
    }
    finally { await handler.DisposeAsync(); Directory.Delete(root, recursive: true); }
  }

  // ------------------------------------------------------------------
  // 完整链路：空环境 → 入口创建 base 并启动 → 保存远程配置 → 可预加载
  // ------------------------------------------------------------------

  [Fact]
  public async Task RemoteHostEntryEnablesSaveRemoteAndPreloadChain()
  {
    var inference = new RemoteHostInferenceClient();
    var manager = new RemoteHostManager(
      new ManagedEnvironmentList("user", 1,
        [ManagedEnvironment("user", "我的环境", "empty")],
        Recipes: Catalog()));
    // 切换完成后目录声明远程能力与模式目录（Runtime 真实行为：supervisor
    // 承载后 capability 始终声明，无需本地 mineru 包）。
    var environments = new ManagedEnvironmentSettings(manager,
      (id, _) =>
      {
        manager.SetActive(id);
        inference.SwitchToRemoteCapableCatalog();
        return Task.CompletedTask;
      },
      () => null, new ProductMaintenanceCoordinator());
    await using var handler = CreateHandler(inference, environments, out string root,
      out SettingsViewModel settings);
    try
    {
      // 入口启用前：当前目录未声明远程能力，保存远程配置 fail closed。
      await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
      await settings.SetMineruConnectionAsync(
        "remote", "https://mineru.example.com", "key", TestContext.Current.CancellationToken);
      Assert.Equal(0, inference.UpdateCalls);

      await handler.ExecuteAsync(new PrepareRemoteHostCommand(),
        TestContext.Current.CancellationToken);
      await WaitUntilAsync(() => manager.Switches.Count == 1, "switch was not started");

      // 入口启动宿主后：同一会话内即可保存远程配置（真实 PUT settings）。
      await settings.RefreshSelectionAsync(TestContext.Current.CancellationToken);
      await settings.SetMineruConnectionAsync(
        "remote", "https://mineru.example.com", "key", TestContext.Current.CancellationToken);
      Assert.Equal(1, inference.UpdateCalls);
      Assert.True(settings.MineruConnection?.IsRemote);
      JsonElement connection = inference.LastUpdate!.Extra["mineru_connection"];
      Assert.Equal("remote", connection.GetProperty("mode").GetString());
      Assert.Equal(
        "https://mineru.example.com", connection.GetProperty("api_url").GetString());

      // 保存后可触发真实预加载请求（pipelines=MinerU、mineru_document）。
      await settings.PrepareMineruRemoteAsync(TestContext.Current.CancellationToken);
      Assert.Equal(1, inference.PreloadCalls);
      Assert.Equal([Wire.RecognitionModeId.MineruDocument], inference.LastPreloadModes);
      Assert.Contains("准备已执行", settings.Status);
    }
    finally { await handler.DisposeAsync(); Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public void BridgeParsesPrepareRemoteHostCommand()
  {
    Guid sessionId = Guid.NewGuid();
    string payload = JsonSerializer.Serialize(new
    {
      version = 2,
      kind = "request",
      id = Guid.NewGuid(),
      type = "app.command",
      payload = new
      {
        sessionId,
        command = new { scope = "settings", action = "prepareRemoteHost", arguments = new { } },
      },
    });
    Assert.IsType<PrepareRemoteHostCommand>(
      WorkbenchBridgeCodec.ParseCommand(payload, sessionId).Command);
  }

  // ------------------------------------------------------------------
  // 无宿主指引与白名单错误本地化（SettingsViewModel）
  // ------------------------------------------------------------------

  [Fact]
  public async Task MineruConfigWithoutHostExplainsRemoteEntryInsteadOfEndlessRefresh()
  {
    var manager = new RemoteHostManager(
      new ManagedEnvironmentList("user", 1,
        [ManagedEnvironment("user", "我的环境", "empty")],
        Recipes: Catalog()));
    var environments = new ManagedEnvironmentSettings(manager,
      (_, _) => Task.CompletedTask, () => null, new ProductMaintenanceCoordinator());
    await environments.RefreshAsync(TestContext.Current.CancellationToken);
    var viewModel = new SettingsViewModel(new DeferredInferenceClient(), environments: environments);

    await viewModel.SetMineruConnectionAsync(
      "remote", "https://mineru.example.com", "key", TestContext.Current.CancellationToken);

    // 无宿主时刷新永远无效：如实指路远程模式入口，不再误导“先刷新”。
    Assert.Contains("启用远程模式", viewModel.Status);
    Assert.DoesNotContain("请先刷新运行时", viewModel.Status);
  }

  [Fact]
  public async Task MineruConfigStillWaitingWithoutSnapshotKeepsOriginalHint()
  {
    var viewModel = new SettingsViewModel(new DeferredInferenceClient());
    await viewModel.SetMineruConnectionAsync(
      "remote", "https://mineru.example.com", "key", TestContext.Current.CancellationToken);
    // 环境快照未同步（服务启动中）：保留原有等待提示。
    Assert.Equal("运行时目录尚未加载，请先刷新运行时", viewModel.Status);
  }

  public static TheoryData<string, HttpV2ErrorCode> PreparationFailureCases => new()
  {
    { "mineru_api_endpoint_incompatible", HttpV2ErrorCode.ValidationError },
    { "mineru_api_authentication_failed", HttpV2ErrorCode.ValidationError },
    { "some_other_reason", HttpV2ErrorCode.ValidationError },
    { "mineru_api_endpoint_incompatible", HttpV2ErrorCode.InternalError },
  };

  [Theory]
  [MemberData(nameof(PreparationFailureCases))]
  public void LocalizeMineruPreparationFailureMapsOnlyWhitelistedValidationErrorReasons(
    string reason, HttpV2ErrorCode code)
  {
    var exception = new InferenceClientException(
      code, "upstream said no", retryable: false,
      new Dictionary<string, JsonElement>
      {
        ["reason"] = JsonSerializer.SerializeToElement(reason),
      });

    string status = SettingsViewModel.LocalizeMineruPreparationFailure(exception);

    if (code == HttpV2ErrorCode.ValidationError &&
        reason == "mineru_api_endpoint_incompatible")
    {
      Assert.Contains("根地址", status);
      Assert.Contains("/file_parse", status);
    }
    else if (code == HttpV2ErrorCode.ValidationError &&
             reason == "mineru_api_authentication_failed")
    {
      Assert.Contains("API Key", status);
    }
    else
    {
      // 非白名单 reason 或非 ValidationError：回落既有 v2 本地化。
      Assert.Equal("操作失败", status);
    }
    // 不回显服务正文。
    Assert.DoesNotContain("upstream said", status);
  }

  [Fact]
  public void LocalMineruInstallHintIsRetained()
  {
    Assert.Contains("远程模式无需安装", SettingsViewModel.FeatureDisplayName("mineru"));
  }

  // ------------------------------------------------------------------
  // Helpers
  // ------------------------------------------------------------------

  private static async Task WaitUntilAsync(Func<bool> condition, string message)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (!condition())
    {
      if (DateTime.UtcNow > deadline) throw new TimeoutException(message);
      await Task.Delay(25);
    }
  }

  private static DesktopWorkbenchCommandHandler CreateHandler(
    RemoteHostInferenceClient inference,
    ManagedEnvironmentSettings environments,
    out string root,
    out SettingsViewModel settings)
  {
    root = Path.Combine(Path.GetTempPath(), $"vibeocr-remote-host-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var broker = new WorkbenchResourceBroker(root);
    var annotations = new WorkbenchAnnotationStore(root);
    settings = new SettingsViewModel(inference, environments: environments);
    SettingsViewModel viewModel = settings;
    return new DesktopWorkbenchCommandHandler(
      () => new RecognitionViewModel(inference, new SignallingInputService()),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      () => viewModel,
      () => new ShellViewModel(
        new StubHotkeyRegistrar(), new StubStartupRegistrar()),
      static () => throw new InvalidOperationException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])),
      broker,
      root,
      static () => 0,
      annotations);
  }

  private static DesktopWorkbenchCommandHandler CreateHandler(
    RemoteHostInferenceClient inference,
    ManagedEnvironmentSettings environments,
    out string root) => CreateHandler(inference, environments, out root, out _);

  private static ManagedEnvironment ManagedEnvironment(
    string id, string name, string status) =>
    new(id, name, 1, "venv", status, "python", "ready", status, "unavailable",
      "not_applicable", "not_started", null);

  private static IReadOnlyList<ManagedEnvironmentRecipe> Catalog() =>
  [
    new ManagedEnvironmentRecipe("rapidocr-cpu", "RapidOCR · CPU",
      DependencyOrigin: "bundled_pack"),
    new ManagedEnvironmentRecipe("paddleocr-cpu", "PaddleOCR · CPU",
      DependencyOrigin: "online_index"),
  ];

  private sealed class RemoteHostManager(ManagedEnvironmentList list) : IManagedEnvironmentClient
  {
    public ManagedEnvironmentList List { get; private set; } = list;
    public ManagedEnvironmentQueryResult? Compatibility { get; set; }
    public TaskCompletionSource<ManagedEnvironmentQueryResult>? FindGate { get; init; }
    public List<string> CreatedNames { get; } = [];
    public string? CreatedTargetId { get; private set; }
    public List<(string EnvironmentId, string Recipe)> Previews { get; } = [];
    public List<(string PlanId, string EnvironmentId)> Installs { get; } = [];
    public List<string> Switches { get; } = [];
    public Exception? InstallThrows { get; set; }
    private int findCalls;

    public void SetActive(string id)
    {
      Switches.Add(id);
      List = List with { ActiveId = id };
    }

    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(
      CancellationToken cancellationToken = default) => Task.FromResult(List);

    public async Task<ManagedEnvironmentQueryResult> FindCompatibleEnvironmentAsync(
      string recipe, CancellationToken cancellationToken = default)
    {
      // 首次查询可被测试挂起，模拟长查询/安装期间的重复点击。
      if (Interlocked.Increment(ref findCalls) == 1 && FindGate is not null)
      {
        return await FindGate.Task.WaitAsync(cancellationToken);
      }
      return Compatibility ?? new ManagedEnvironmentQueryResult(recipe);
    }

    public Task<ManagedEnvironment> CreateEnvironmentAsync(
      string name, CancellationToken cancellationToken = default)
    {
      CreatedNames.Add(name);
      string id = $"created-{CreatedNames.Count}";
      CreatedTargetId = id;
      var created = ManagedEnvironment(id, name, "empty");
      List = List with { Environments = [.. List.Environments, created] };
      return Task.FromResult(created);
    }

    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
      string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default)
    {
      Previews.Add((environmentId, recipe));
      return Task.FromResult(new ManagedEnvironmentPlan(
        "plan-1", environmentId, 1, List.ActiveRevision, recipe, []));
    }

    public Task<ManagedEnvironment> InstallEnvironmentAsync(
      ManagedEnvironmentPlan plan, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default)
    {
      Installs.Add((plan.PlanId, plan.EnvironmentId));
      if (InstallThrows is not null) throw InstallThrows;
      List = List with
      {
        Environments =
        [
          .. List.Environments.Select(item => item.Id == plan.EnvironmentId
            ? item with { Status = "installed" }
            : item),
        ],
      };
      return Task.FromResult(
        ManagedEnvironment(plan.EnvironmentId, "远程基础服务", "installed"));
    }

    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(
      string environmentId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PreparedEnvironmentSwitch(
        environmentId, 1, List.ActiveId, List.ActiveRevision, "python", true, null));

    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
      PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
      CancellationToken cancellationToken = default)
    {
      Switches.Add(prepared.EnvironmentId);
      List = List with { ActiveId = prepared.EnvironmentId };
      return Task.FromResult(new CommittedEnvironmentSwitch(prepared.EnvironmentId, 1));
    }

    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
      string? environmentId, string? packageSourceId, string? modelSourceId,
      CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(
      string environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteEnvironmentAsync(
      string environmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class RemoteHostInferenceClient : InferenceClientStub, IInferenceClient
  {
    public int UpdateCalls { get; private set; }
    public int PreloadCalls { get; private set; }
    public IReadOnlyList<Wire.RecognitionModeId>? LastPreloadModes { get; private set; }
    public SettingsSnapshot? LastUpdate { get; private set; }
    private SettingsSnapshot Settings { get; set; } = new();

    public Wire.Health Health { get; set; } = EngineOnlyHealth();

    /// <summary>宿主启动后的目录：声明远程能力与全部模式（Runtime 真实行为）。</summary>
    public void SwitchToRemoteCapableCatalog() => Health = RemoteCapableHealth();

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Health);
    public override Task<SettingsSnapshot> GetSettingsAsync(
      CancellationToken cancellationToken) => Task.FromResult(Settings);
    public override Task<SettingsSnapshot> UpdateSettingsAsync(
      SettingsSnapshot snapshot, CancellationToken cancellationToken)
    {
      UpdateCalls++;
      LastUpdate = snapshot;
      Settings = snapshot;
      return Task.FromResult(snapshot);
    }
    public override Task<ResidencyStatus> GetResidencyAsync(
      CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());
    public Task<ResidencyStatus> PreloadRuntimeAsync(
      Wire.RuntimePreloadRequest request, CancellationToken cancellationToken)
    {
      PreloadCalls++;
      LastPreloadModes = request.RecognitionModes?.ToArray();
      return Task.FromResult(new ResidencyStatus());
    }
    public Task<RuntimeStatusSnapshot> GetRuntimeStatusAsync(
      CancellationToken cancellationToken) => Task.FromResult(new RuntimeStatusSnapshot
      {
        InstanceId = "sup-remote-host",
        ServiceState = RuntimeServiceState.Ready,
        BackendVersion = "0.14.0",
        Profile = new RuntimeProfileStatus
        {
          ProfileId = "win-x64-cpu",
          Accelerator = RuntimeAccelerator.Cpu,
          Components = [],
        },
      });

    private static Wire.Health EngineOnlyHealth() => new()
    {
      SchemaVersion = 2,
      InstanceId = "sup-remote-host",
      ProtocolVersion = 2,
      Ready = true,
      Draining = false,
      Capabilities = [RuntimeSelectionService.EngineSelectionCapability],
      CapabilityDescriptors = [new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.EngineSelectionCapability,
        Lifecycle = "active", IntroducedIn = "2.0.0", DeprecatedIn = null,
        SunsetAt = null, Replacement = null,
        OcrEngineCatalog = new Wire.OcrEngineCatalog
        {
          Engines = [new Wire.OcrEngineDescriptor
          {
            Id = Wire.OcrEngineId.Rapidocr,
            Availability = Wire.OcrEngineAvailability.Unavailable,
            IncludedInBase = true,
            ReasonCode = "engine_not_installed",
            RequiredComponent = null,
          }],
        },
      }],
    };
  }

  private static Wire.Health RemoteCapableHealth() => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-remote-prepare",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities =
    [
      RuntimeSelectionService.MineruRemoteApiCapability,
      RuntimeSelectionService.RecognitionModesCapability,
    ],
    CapabilityDescriptors =
    [
      new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.RecognitionModesCapability,
        Lifecycle = "active",
        IntroducedIn = "2.9.0",
        DeprecatedIn = null,
        SunsetAt = null,
        Replacement = null,
        RecognitionModeCatalog = new Wire.RecognitionModeCatalog
        {
          Modes =
          [
            Mode(Wire.RecognitionModeId.RapidText),
            Mode(Wire.RecognitionModeId.WindowsText),
            Mode(Wire.RecognitionModeId.PaddleText),
            Mode(Wire.RecognitionModeId.PaddleStructure),
            Mode(Wire.RecognitionModeId.PaddleDocumentVl),
            Mode(Wire.RecognitionModeId.MineruDocument, mineru: true),
            Mode(Wire.RecognitionModeId.PaddleTable),
            Mode(Wire.RecognitionModeId.PaddleFormula),
          ],
        },
      },
    ],
  };

  private static Wire.RecognitionModeDescriptor Mode(
    Wire.RecognitionModeId id, bool mineru = false)
  {
    var (family, pipeline, engine, provisioning, lifecycle) = id switch
    {
      Wire.RecognitionModeId.RapidText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Rapidocr,
        Wire.RecognitionModeProvisioning.BaseRuntime, Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.WindowsText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Windows,
        Wire.RecognitionModeProvisioning.OperatingSystem, Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.PaddleText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Paddleocr,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleStructure => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PPStructureV3, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleDocumentVl => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PaddleOCRVL, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.MineruDocument => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.MinerU, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.PaddleTable => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.TABLERECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleFormula => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.FORMULARECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    // 目录如实保持 preparation_required（本地组件缺失/远程未验证 tier），
    // 不得因远程配置谎标 ready；测试仅消费目录形状。
    return new Wire.RecognitionModeDescriptor
    {
      Id = id,
      Family = family,
      PipelineId = pipeline,
      Engine = engine,
      Provisioning = provisioning,
      Availability = Wire.RecognitionModeAvailability.PreparationRequired,
      ReasonCode = mineru ? "runtime_component_missing" : "runtime_component_missing",
      RequiredComponent = mineru ? "mineru-cpu" : "rapidocr-base",
      SupportedOptions = [],
      Lifecycle = new Wire.RecognitionModeLifecycle
      {
        Kind = lifecycle,
        SupportsPreload = mineru,
        SupportsTtl = mineru,
        SupportsPinning = false,
        SupportsRelease = mineru,
      },
    };
  }

  private sealed class StubHotkeyRegistrar : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict)
    {
      conflict = null;
      return true;
    }

    public void Unregister() { }
  }

  private sealed class StubStartupRegistrar : IStartupRegistrar
  {
    public bool SetEnabled(bool enabled) => true;
  }

  private sealed class SignallingInputService : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => Task.FromResult<RecognitionInput?>(null);
  }}
