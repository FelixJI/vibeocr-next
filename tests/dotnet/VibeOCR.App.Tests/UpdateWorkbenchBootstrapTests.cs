using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
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

/// <summary>
/// 关于/更新基础快照与更新命令的可用性回归：Runtime 未启动、目录请求
/// 挂起或失败时，bootstrap 握手仍必须完成，且 about/update 初始快照与
/// 检查/切源命令不依赖任何 Runtime 初始化结果。
/// </summary>
public sealed class UpdateWorkbenchBootstrapTests
{
  [Fact]
  public async Task ShellSwitchesWorkWithoutCreatingRuntimeSettings()
  {
    string root = NewRoot();
    try
    {
      var inference = new UnattachedInferenceClient();
      await using HandlerFixture fixture = Create(root, inference, new SettingsViewModel(inference),
        attached: false, settingsFactory: () => throw new InvalidOperationException("Runtime unavailable"));
      WorkbenchCommandOutcome startup = await fixture.Handler.ExecuteAsync(
        new SetStartupCommand(true), TestContext.Current.CancellationToken);
      Assert.Null(startup.Error);
      Assert.True(Assert.IsType<SettingsWorkbenchState>(Assert.Single(startup.States)).StartupEnabled);
      WorkbenchCommandOutcome tray = await fixture.Handler.ExecuteAsync(
        new SetMinimizeToTrayCommand(true), TestContext.Current.CancellationToken);
      Assert.Null(tray.Error);
      var state = Assert.IsType<SettingsWorkbenchState>(Assert.Single(tray.States));
      Assert.True(state.StartupEnabled);
      Assert.True(state.MinimizeToTray);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task BootstrapCompletesWhenAttachedCatalogRequestHangs()
  {
    string root = NewRoot();
    try
    {
      var inference = new HangingHealthInferenceClient();
      var settings = new SettingsViewModel(inference);
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: true);
      await using var application = new WorkbenchApplication(
        DesktopWorkbenchCommandHandler.Capabilities,
        WorkbenchRoute.Recognition,
        fixture.Handler);

      // 旧实现无限等待目录请求：这里的受限等待证明握手被上限约束。
      WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
        TestContext.Current.CancellationToken).AsTask()
        .WaitAsync(Timeout.Add(TimeSpan.FromSeconds(25)), TestContext.Current.CancellationToken);

      Assert.Contains("about", bootstrap.States.Select(state => state.Scope));
      Assert.Contains("update", bootstrap.States.Select(state => state.Scope));
      UpdateWorkbenchState update = Assert.IsType<UpdateWorkbenchState>(
        bootstrap.States.Single(state => state.Scope == "update").State);
      Assert.Equal("update.unchecked", update.StatusCode);
      Assert.False(update.UpdateAvailable);
      Assert.NotNull(update.Sources);
      Assert.Contains(update.Sources!, source => source.Id == "direct");
      AboutWorkbenchState about = Assert.IsType<AboutWorkbenchState>(
        bootstrap.States.Single(state => state.Scope == "about").State);
      Assert.Equal("MIT", about.License);
      Assert.Equal("https://github.com/FelixJI/vibeocr-next", about.ProjectUrl);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task UpdateCheckCommandWorksWhileCatalogLoadHangs()
  {
    string root = NewRoot();
    try
    {
      var inference = new HangingHealthInferenceClient();
      var settings = new SettingsViewModel(inference);
      var coordinator = new StaticUpdateCoordinator(
        new UpdateCheckResult(UpdateCheckStatus.Error, ErrorMessage: "无法连接更新源。"));
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: true, coordinator: coordinator);
      // 先让 bootstrap 进入挂起的目录加载，再验证更新命令不依赖它。
      await using var application = new WorkbenchApplication(
        DesktopWorkbenchCommandHandler.Capabilities,
        WorkbenchRoute.Recognition,
        fixture.Handler);
      await application.BootstrapAsync(TestContext.Current.CancellationToken).AsTask()
        .WaitAsync(Timeout.Add(TimeSpan.FromSeconds(25)), TestContext.Current.CancellationToken);

      var terminal = new TaskCompletionSource<UpdateWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.Handler.StateChanged += state =>
      {
        if (state is UpdateWorkbenchState update && !update.IsBusy)
          terminal.TrySetResult(update);
      };
      WorkbenchCommandOutcome check = await fixture.Handler.ExecuteAsync(
        new CheckUpdateCommand(), TestContext.Current.CancellationToken);

      // 回执立即返回，终态经状态邮箱发布。
      Assert.Null(check.Error);
      UpdateWorkbenchState state = await terminal.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.Equal("update.error", state.StatusCode);
      Assert.Contains("无法连接更新源", state.StatusMessage);
      Assert.False(state.UpdateAvailable);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task InitialUpdateStateStaysUncheckedWithoutWiredUpdateService()
  {
    string root = NewRoot();
    try
    {
      var inference = new UnattachedInferenceClient();
      var settings = new SettingsViewModel(inference);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      // 更新 VM 尚未接线（等价于桌面壳初始化之前的时序）；其余工厂可用，
      // 证明初始快照不依赖更新服务。
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => new ShellViewModel(
          new NoopHotkeyRegistrar(), new NoopStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, root, static () => 0, annotations);

      UpdateWorkbenchState update = Assert.IsType<UpdateWorkbenchState>(
        handler.InitialStates.Single(state => state.Scope == "update"));

      Assert.Equal("update.unchecked", update.StatusCode);
      Assert.False(update.UpdateAvailable);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task BootstrapCompletesWhenAttachedCatalogRequestFailsFast()
  {
    string root = NewRoot();
    try
    {
      // 已 attach 且目录请求立即拋 HTTP 类异常：基础快照同样不得被阻断。
      var inference = new FailingHealthInferenceClient();
      var settings = new SettingsViewModel(inference);
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: true);
      await using var application = new WorkbenchApplication(
        DesktopWorkbenchCommandHandler.Capabilities,
        WorkbenchRoute.Recognition,
        fixture.Handler);

      WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
        TestContext.Current.CancellationToken).AsTask()
        .WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);

      Assert.Contains("update", bootstrap.States.Select(state => state.Scope));
      Assert.Contains("about", bootstrap.States.Select(state => state.Scope));
      UpdateWorkbenchState update = Assert.IsType<UpdateWorkbenchState>(
        bootstrap.States.Single(state => state.Scope == "update").State);
      Assert.Equal("update.unchecked", update.StatusCode);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task CatalogGateRecoversAfterBootstrapPrefetchCancellation()
  {
    string root = NewRoot();
    try
    {
      // 首次请求等待令牌取消（模拟挂起被超时取消），随后目录成功：
      // 证明取消真实传播并释放 SettingsViewModel 单飞门。
      var inference = new RecoveringHealthInferenceClient();
      var settings = new SettingsViewModel(inference);
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: true);
      await using var application = new WorkbenchApplication(
        DesktopWorkbenchCommandHandler.Capabilities,
        WorkbenchRoute.Recognition,
        fixture.Handler);
      await application.BootstrapAsync(
        TestContext.Current.CancellationToken).AsTask()
        .WaitAsync(Timeout.Add(TimeSpan.FromSeconds(25)), TestContext.Current.CancellationToken);
      Assert.Equal(1, inference.HealthCalls);

      // 第二次经同一单飞门加载目录：首请求被取消后必须能真实重入并成功。
      WorkbenchCommandOutcome refresh = await fixture.Handler.ExecuteAsync(
        new RefreshRuntimeCommand(), TestContext.Current.CancellationToken)
        .AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

      Assert.Null(refresh.Error);
      Assert.Equal(2, inference.HealthCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(false, true)]
  [InlineData(true, false)]
  [InlineData(true, true)]
  public async Task BusyUpdateCommandsPreserveTheActiveOperationTerminalState(
    bool downloading, bool requestDownload)
  {
    string root = NewRoot();
    var coordinator = new BlockingUpdateCoordinator(downloading);
    try
    {
      var inference = new UnattachedInferenceClient();
      await using HandlerFixture fixture = Create(root, inference,
        new SettingsViewModel(inference), attached: false, coordinator: coordinator);
      var terminal = new TaskCompletionSource<UpdateWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      string expectedStatus = downloading ? "update.downloaded" : "update.current";
      fixture.Handler.StateChanged += state =>
      {
        if (state is UpdateWorkbenchState { IsBusy: false } update &&
            update.StatusCode == expectedStatus)
          terminal.TrySetResult(update);
      };

      await fixture.Handler.ExecuteAsync(new CheckUpdateCommand(), TestContext.Current.CancellationToken);
      if (downloading)
        await fixture.Handler.ExecuteAsync(new DownloadUpdateCommand(), TestContext.Current.CancellationToken);
      await coordinator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

      WorkbenchCommandOutcome duplicate = await fixture.Handler.ExecuteAsync(
        requestDownload ? new DownloadUpdateCommand() : new CheckUpdateCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(duplicate.Error);
      Assert.True(Assert.IsType<UpdateWorkbenchState>(Assert.Single(duplicate.States)).IsBusy);

      coordinator.Release();
      UpdateWorkbenchState completed = await terminal.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.Equal(expectedStatus, completed.StatusCode);
    }
    finally
    {
      coordinator.Release();
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task CheckUpdatePublishesBusySnapshotImmediatelyAndStaysCancellable()
  {
    string root = NewRoot();
    try
    {
      var coordinator = new BlockingUpdateCoordinator();
      var inference = new UnattachedInferenceClient();
      var settings = new SettingsViewModel(inference);
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: false, coordinator: coordinator);
      var busyPublished = new TaskCompletionSource<UpdateWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      var cancelled = new TaskCompletionSource<UpdateWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.Handler.StateChanged += state =>
      {
        if (state is not UpdateWorkbenchState update) return;
        if (update.IsBusy && update.StatusCode == "update.checking")
          busyPublished.TrySetResult(update);
        if (!update.IsBusy && update.StatusCode == "update.cancelled")
          cancelled.TrySetResult(update);
      };

      WorkbenchCommandOutcome started = await fixture.Handler.ExecuteAsync(
        new CheckUpdateCommand(), TestContext.Current.CancellationToken);

      // 回执立即返回，busy 快照经既有状态邮箱发布，不等最终结果。
      Assert.Empty(started.States);
      UpdateWorkbenchState busy = await busyPublished.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.Equal("update.checking", busy.StatusCode);
      Assert.True(busy.IsBusy);
      await coordinator.Started.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

      WorkbenchCommandOutcome cancel = await fixture.Handler.ExecuteAsync(
        new CancelUpdateCommand(), TestContext.Current.CancellationToken);
      Assert.Null(cancel.Error);
      coordinator.Release();
      UpdateWorkbenchState terminal = await cancelled.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.False(terminal.IsBusy);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task UpdateServiceWiringRecoversAfterInitialSnapshotWithoutService()
  {
    string root = NewRoot();
    try
    {
      var inference = new UnattachedInferenceClient();
      var settings = new SettingsViewModel(inference);
      bool wired = false;
      var coordinator = new StaticUpdateCoordinator(
        new UpdateCheckResult(UpdateCheckStatus.Latest, "0.3.0"));
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: false, updateFactory: () => wired
          ? new UpdateViewModel(coordinator)
          : throw new InvalidOperationException("Update service is unavailable."));

      // 初始快照在未接线时发布基础“未检查”，不缓存失败。
      UpdateWorkbenchState initial = Assert.IsType<UpdateWorkbenchState>(
        fixture.Handler.InitialStates.Single(state => state.Scope == "update"));
      Assert.Equal("update.unchecked", initial.StatusCode);

      // 接线后首次命令必须能创建真实模型并成功完成检查。
      wired = true;
      var terminal = new TaskCompletionSource<UpdateWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      fixture.Handler.StateChanged += state =>
      {
        if (state is UpdateWorkbenchState update && !update.IsBusy)
          terminal.TrySetResult(update);
      };
      WorkbenchCommandOutcome check = await fixture.Handler.ExecuteAsync(
        new CheckUpdateCommand(), TestContext.Current.CancellationToken).AsTask()
        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

      Assert.Null(check.Error);
      UpdateWorkbenchState terminalState = await terminal.Task.WaitAsync(
        TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      Assert.Equal("update.current", terminalState.StatusCode);
      Assert.False(terminalState.IsBusy);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }



  [Fact]
  public async Task SetUpdateSourcePersistsSelectionAndResetsCheckResult()
  {
    string root = NewRoot();
    try
    {
      string configFile = Path.Combine(root, "app_settings.json");
      var coordinator = VelopackUpdateCoordinator.Create(configFile);
      var inference = new UnattachedInferenceClient();
      var settings = new SettingsViewModel(inference);
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: false, coordinator: coordinator);

      WorkbenchCommandOutcome outcome = await fixture.Handler.ExecuteAsync(
        new SetUpdateSourceCommand(
          "custom", "url_prefix", "https://mirror.example.com/vibeocr"),
        TestContext.Current.CancellationToken);

      Assert.Null(outcome.Error);
      UpdateWorkbenchState state = Assert.IsType<UpdateWorkbenchState>(
        Assert.Single(outcome.States));
      Assert.Equal("update.unchecked", state.StatusCode);
      Assert.False(state.UpdateAvailable);
      Assert.Equal(
        "https://mirror.example.com/vibeocr/" + VelopackFeedFactory.DirectBaseUrl,
        state.DetectionEndpoint);
      Assert.NotNull(state.Sources);
      Assert.Contains(state.Sources!, source => source.Id == "custom" && source.Selected);
      // 持久化到既有配置文件，且是权威真源。
      Assert.True(File.Exists(configFile));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SetUpdateSourceInvalidUrlIsRejectedWithUnderstandableProblem()
  {
    string root = NewRoot();
    try
    {
      var coordinator = VelopackUpdateCoordinator.Create(
        Path.Combine(root, "app_settings.json"));
      var inference = new UnattachedInferenceClient();
      var settings = new SettingsViewModel(inference);
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: false, coordinator: coordinator);

      WorkbenchCommandOutcome outcome = await fixture.Handler.ExecuteAsync(
        new SetUpdateSourceCommand(
          "custom", "url_prefix", "https://user:pass@mirror.example.com/"),
        TestContext.Current.CancellationToken);

      Assert.NotNull(outcome.Error);
      Assert.Equal("update_source_invalid", outcome.Error.Code);
      Assert.Equal(WorkbenchProblemCategory.InvalidCommand, outcome.Error.Category);
      Assert.Equal("workbench.error.updateSourceInvalid", outcome.Error.MessageKey);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SetUpdateSourceIsRefusedWhileUpdateCheckIsBusy()
  {
    string root = NewRoot();
    try
    {
      var coordinator = new BlockingUpdateCoordinator();
      var inference = new UnattachedInferenceClient();
      var settings = new SettingsViewModel(inference);
      await using HandlerFixture fixture = Create(root, inference, settings,
        attached: false, coordinator: coordinator);
      Task<WorkbenchCommandOutcome> check = fixture.Handler.ExecuteAsync(
        new CheckUpdateCommand(), TestContext.Current.CancellationToken).AsTask();
      await coordinator.Started.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

      WorkbenchCommandOutcome outcome = await fixture.Handler.ExecuteAsync(
        new SetUpdateSourceCommand("direct"),
        TestContext.Current.CancellationToken);

      Assert.NotNull(outcome.Error);
      Assert.Equal("update_source_busy", outcome.Error.Code);
      Assert.Equal(WorkbenchProblemCategory.Conflict, outcome.Error.Category);
      coordinator.Release();
      await check;
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private static TimeSpan Timeout => DesktopWorkbenchCommandHandler.BootstrapCatalogTimeout;

  private static string NewRoot()
  {
    string root = Path.Combine(Path.GetTempPath(),
      $"vibeocr-update-boot-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return root;
  }

  private sealed record HandlerFixture(
    DesktopWorkbenchCommandHandler Handler,
    WorkbenchResourceBroker Broker,
    WorkbenchAnnotationStore Annotations) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      await Handler.DisposeAsync();
      Broker.Dispose();
      Annotations.Dispose();
    }
  }

  private static HandlerFixture Create(
    string root,
    InferenceClientStub inference,
    SettingsViewModel settings,
    bool attached,
    IUpdateCoordinator? coordinator = null,
    Func<UpdateViewModel>? updateFactory = null,
    Func<SettingsViewModel>? settingsFactory = null)
  {
    var broker = new WorkbenchResourceBroker(root);
    var annotations = new WorkbenchAnnotationStore(root);
    DesktopWorkbenchCommandHandler handler = new(
      () => new RecognitionViewModel(inference, new NullInputService()),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      settingsFactory ?? (() => settings),
      static () => new ShellViewModel(
        new NoopHotkeyRegistrar(), new NoopStartupRegistrar()),
      updateFactory ?? (() => new UpdateViewModel(coordinator ?? new StaticUpdateCoordinator(
        new UpdateCheckResult(UpdateCheckStatus.Latest, "0.3.0")))),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])),
      broker, root, static () => 0, annotations,
      inferenceAttached: () => attached);
    return new HandlerFixture(handler, broker, annotations);
  }

  /// <summary>已 attach 但健康/目录请求永不返回：模拟挂起的 Runtime 目录握手。</summary>
  private sealed class HangingHealthInferenceClient : InferenceClientStub
  {
    private readonly TaskCompletionSource<Wire.Health> _health = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      _health.Task.WaitAsync(cancellationToken);
  }

  /// <summary>已 attach 且目录请求立即拋 HTTP 类异常：基础快照不得被阻断。</summary>
  private sealed class FailingHealthInferenceClient : InferenceClientStub
  {
    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      throw new HttpRequestException("模拟目录请求网络失败");
  }

  /// <summary>
  /// 首次目录请求挂起直到令牌取消（模拟 bootstrap 预取超时取消），后续
  /// 请求成功：验证取消真实传播并释放设置单飞门。
  /// </summary>
  private sealed class RecoveringHealthInferenceClient : InferenceClientStub
  {
    private int healthCalls;

    public int HealthCalls => Volatile.Read(ref healthCalls);

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken)
    {
      if (Interlocked.Increment(ref healthCalls) == 1)
      {
        return new TaskCompletionSource<Wire.Health>(
          TaskCreationOptions.RunContinuationsAsynchronously)
          .Task.WaitAsync(cancellationToken);
      }
      return Task.FromResult(Healthy());
    }

    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new SettingsSnapshot());

    public override Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken) =>
      throw new NotSupportedException();

    private static Wire.Health Healthy() => new()
    {
      SchemaVersion = 2,
      InstanceId = "recovering",
      ProtocolVersion = 2,
      Ready = true,
      Draining = false,
      Capabilities = [RuntimeSelectionService.EngineSelectionCapability],
      CapabilityDescriptors =
      [
        new Wire.CapabilityDescriptor
        {
          Name = RuntimeSelectionService.EngineSelectionCapability,
          Lifecycle = "active",
          IntroducedIn = "2.6.0",
          DeprecatedIn = null,
          SunsetAt = null,
          Replacement = null,
          OcrEngineCatalog = new Wire.OcrEngineCatalog
          {
            Engines =
            [
              new Wire.OcrEngineDescriptor
              {
                Id = Wire.OcrEngineId.Windows,
                Availability = Wire.OcrEngineAvailability.Ready,
                IncludedInBase = true,
                ReasonCode = null,
                RequiredComponent = null,
              },
            ],
          },
        },
      ],
    };
  }

  private sealed class UnattachedInferenceClient : InferenceClientStub
  {
  }

  private sealed class NullInputService : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
  }

  private sealed class StaticUpdateCoordinator(
    UpdateCheckResult checkResult) : IUpdateCoordinator
  {
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken) =>
      Task.FromResult(checkResult);

    public Task<UpdateApplyResult> DownloadAndApplyAsync(
      IProgress<int>? progress,
      CancellationToken cancellationToken) =>
      Task.FromResult(new UpdateApplyResult(UpdateApplyStatus.Failed));

    public UpdateSourceCatalog LoadSources() => new(
      [new UpdateSourceOption(
        "direct", "direct", "官方直连（GitHub）",
        VelopackFeedFactory.DirectBaseUrl, Selected: true)],
      VelopackFeedFactory.DirectBaseUrl);

    public UpdateSourceCatalog SelectSource(string sourceId, string? kind, string? url) =>
      LoadSources();
  }

  private sealed class BlockingUpdateCoordinator(bool blockDownload = false) : IUpdateCoordinator
  {
    public TaskCompletionSource Started { get; } = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(
      TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
      if (blockDownload)
        return new UpdateCheckResult(UpdateCheckStatus.Available, "0.3.0");
      Started.TrySetResult();
      await _release.Task.WaitAsync(cancellationToken);
      return new UpdateCheckResult(UpdateCheckStatus.Latest, "0.3.0");
    }

    public async Task<UpdateApplyResult> DownloadAndApplyAsync(
      IProgress<int>? progress,
      CancellationToken cancellationToken)
    {
      if (blockDownload)
      {
        Started.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
      }
      return new UpdateApplyResult(UpdateApplyStatus.Downloaded);
    }

    public UpdateSourceCatalog LoadSources() => new(
      [new UpdateSourceOption(
        "direct", "direct", "官方直连（GitHub）",
        VelopackFeedFactory.DirectBaseUrl, Selected: true)],
      VelopackFeedFactory.DirectBaseUrl);

    public UpdateSourceCatalog SelectSource(string sourceId, string? kind, string? url) =>
      LoadSources();
  }

  private sealed class NoopHotkeyRegistrar : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict)
    {
      conflict = null;
      return true;
    }

    public void Unregister() { }
  }

  private sealed class NoopStartupRegistrar : IStartupRegistrar
  {
    public bool IsEnabled => false;
    public bool SetEnabled(bool enabled) => true;
  }
}
