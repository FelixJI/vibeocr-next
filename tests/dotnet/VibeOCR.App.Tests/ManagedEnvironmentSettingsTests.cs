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
    Assert.Equal("安装已中断；原环境保持不变，详情可在该环境记录中查看。", settings.Status);
    Assert.Equal("environment", settings.Snapshot?.Environments[0].Id);
    Assert.Equal("install_interrupted",
      settings.Snapshot?.Environments[0].LastInstallFailure?.ReasonCode);
  }

  [Fact]
  public async Task FindCompatibleValidatesCatalogAndInvalidationClearsResult()
  {
    var manager = new MutableManager();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, new ProductMaintenanceCoordinator());
    await settings.RefreshAsync(TestContext.Current.CancellationToken);

    await settings.FindCompatibleAsync("rapidocr-cpu", TestContext.Current.CancellationToken);

    Assert.Equal("rapidocr-cpu", settings.Compatibility?.Recipe);
    Assert.Equal("environment", settings.Compatibility?.Selected?.EnvironmentId);
    Assert.Contains("可直接复用", settings.Status);
    // 未知配方在宿主按 Runtime 目录 fail closed 拒绝，不透传给管理器。
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
      settings.FindCompatibleAsync("made-up-recipe", TestContext.Current.CancellationToken));
    // 与预览同一失效入口：选择变化后旧兼容结果不得冒充新选择。
    settings.InvalidatePlan();
    Assert.Null(settings.Compatibility);
    Assert.Null(settings.Plan);
  }

  [Fact]
  public async Task CancelClickDuringPostInstallRefreshIsIgnoredAndInstallStaysCommitted()
  {
    var manager = new RefreshGateManager();
    var maintenance = new ProductMaintenanceCoordinator();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, maintenance);
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    Task install = settings.InstallAsync("plan", "pypi", CancellationToken.None);
    await manager.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5),
      TestContext.Current.CancellationToken);

    // 安装事务一结束取消入口立即关闭；刷新期间的取消点击必须是 no-op。
    Assert.False(settings.CanCancelInstall);
    settings.CancelInstall();
    manager.RefreshGate.SetResult();
    await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    Assert.False(manager.RefreshTokenCancelled);
    Assert.True(install.IsCompletedSuccessfully);
    Assert.Equal("依赖已安装；请切换环境以验证并启动服务。", settings.Status);
    ManagedEnvironment committed = settings.Snapshot!.Environments[0];
    Assert.Equal("installed", committed.Status);
    Assert.Equal(2, committed.Revision);
    Assert.True(maintenance.State.IsIdle);
    Assert.False(settings.CanCancelInstall);
  }

  [Fact]
  public async Task RefreshCancellationAfterCommittedInstallKeepsInstalledResult()
  {
    var manager = new RefreshGateManager { CancelRefreshByToken = true };
    var maintenance = new ProductMaintenanceCoordinator();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, maintenance);
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    using var user = new CancellationTokenSource();
    Task install = settings.InstallAsync("plan", "pypi", user.Token);
    await manager.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5),
      TestContext.Current.CancellationToken);
    Assert.False(settings.CanCancelInstall);

    user.Cancel();
    await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // #123：提交成功后的刷新取消不得改判安装结果，也不得拖垮操作。
    Assert.True(install.IsCompletedSuccessfully);
    Assert.Equal("依赖已安装；结果刷新已取消，请刷新列表查看最新状态。", settings.Status);
    Assert.DoesNotContain("安装已取消", settings.Status);
    Assert.True(maintenance.State.IsIdle);
    Assert.False(settings.CanCancelInstall);
    Assert.False(settings.IsBusy);
  }

  [Fact]
  public async Task CancelRacingNaturalInstallFailurePreservesDurableFailure()
  {
    var manager = new WaitingManager { CancelFailureReasonCode = "venv_creation_failed" };
    var maintenance = new ProductMaintenanceCoordinator();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, maintenance);
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    Task install = settings.InstallAsync("plan", "pypi", TestContext.Current.CancellationToken);
    await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    await settings.CancelAndWaitForInstallAsync();
    await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // 失败先于取消落盘：真实失败必须保留，不得改称取消或环境未变。
    Assert.True(install.IsCompletedSuccessfully);
    Assert.Contains("安装已经失败（venv_creation_failed）", settings.Status);
    Assert.DoesNotContain("原环境保持不变", settings.Status);
    Assert.Equal("venv_creation_failed",
      settings.Snapshot?.Environments[0].LastInstallFailure?.ReasonCode);
    Assert.Equal("plan", settings.Snapshot?.Environments[0].LastInstallFailure?.PlanId);
    Assert.True(maintenance.State.IsIdle);
    Assert.False(settings.CanCancelInstall);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("previous-plan")]
  public async Task CancelWithoutMatchingDurableRecordStaysUnconfirmed(string? failurePlanId)
  {
    var manager = new WaitingManager
    {
      CancelFailureReasonCode = null,
      Failure = failurePlanId is null ? null : new ManagedEnvironmentInstallFailure(
        "failed", 1, "rapidocr-cpu", "install_interrupted", "preview_again", "Previous attempt",
        PlanId: failurePlanId),
    };
    var maintenance = new ProductMaintenanceCoordinator();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, maintenance);
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    Task install = settings.InstallAsync("plan", "pypi", TestContext.Current.CancellationToken);
    await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    await settings.CancelAndWaitForInstallAsync();
    await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // #123：revision 未变但缺本次记录，不能声称“已取消/未提交更改”。
    Assert.True(install.IsCompletedSuccessfully);
    Assert.Equal("已请求取消；本次安装结果未确认，请刷新列表查看。", settings.Status);
    Assert.Equal(failurePlanId, settings.Snapshot?.Environments[0].LastInstallFailure?.PlanId);
    Assert.Equal(1, settings.Snapshot?.Environments[0].Revision);
    Assert.True(maintenance.State.IsIdle);
  }

  [Fact]
  public async Task CancelAfterCommitDoesNotClaimSuccessOrCancellation()
  {
    var manager = new WaitingManager { CommitBeforeCancel = true, CancelFailureReasonCode = null };
    var maintenance = new ProductMaintenanceCoordinator();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, maintenance);
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    Task install = settings.InstallAsync("plan", "pypi", TestContext.Current.CancellationToken);
    await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    await settings.CancelAndWaitForInstallAsync();
    await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // revision+1/recipe 相同不能证明本次 plan 成功：未关联成功一律未确认。
    Assert.True(install.IsCompletedSuccessfully);
    Assert.Equal("已请求取消；本次安装结果未确认，请刷新列表查看。", settings.Status);
    ManagedEnvironment durable = settings.Snapshot!.Environments[0];
    Assert.Equal("installed", durable.Status);
    Assert.Equal(2, durable.Revision);
    Assert.True(maintenance.State.IsIdle);
  }

  [Fact]
  public async Task CancelConfirmReadFailureReportsUnconfirmedResult()
  {
    var manager = new WaitingManager { FailListAfterCancel = true };
    var maintenance = new ProductMaintenanceCoordinator();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, maintenance);
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    Task install = settings.InstallAsync("plan", "pypi", TestContext.Current.CancellationToken);
    await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    await settings.CancelAndWaitForInstallAsync();
    await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // 读取失败：如实未确认，不虚构环境未变，也不拖垮操作。
    Assert.True(install.IsCompletedSuccessfully);
    Assert.Contains("已请求取消", settings.Status);
    Assert.Contains("最终状态读取失败", settings.Status);
    Assert.Contains("结果未确认", settings.Status);
    Assert.DoesNotContain("安装已取消", settings.Status);
    Assert.True(maintenance.State.IsIdle);
    Assert.False(settings.CanCancelInstall);
    Assert.False(settings.IsBusy);
  }

  [Fact]
  public async Task CancelConfirmReadIsBoundedByDeadlineAndTimesOutUnconfirmed()
  {
    var manager = new WaitingManager { BlockListAfterCancel = true };
    var maintenance = new ProductMaintenanceCoordinator();
    var settings = new ManagedEnvironmentSettings(manager, (_, _) => Task.CompletedTask,
      () => null, maintenance)
    { InstallCancelConfirmTimeout = TimeSpan.FromMilliseconds(200) };
    await settings.PreviewAsync("environment", "rapidocr-cpu", "pypi",
      TestContext.Current.CancellationToken);

    Task install = settings.InstallAsync("plan", "pypi", TestContext.Current.CancellationToken);
    await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    // 不用无界的 CancelAndWaitForInstallAsync：旧实现会无限读取，
    // 测试自身用 WaitAsync 时限保证有界失败。
    settings.CancelInstall();
    await install.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // 读取预算由 deadline 约束：超时转未确认，不无限等下去。
    Assert.True(install.IsCompletedSuccessfully);
    Assert.Contains("已请求取消", settings.Status);
    Assert.Contains("结果未确认", settings.Status);
    Assert.DoesNotContain("安装已取消", settings.Status);
    Assert.True(maintenance.State.IsIdle);
    Assert.False(settings.IsBusy);
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
      Assert.Contains("仅用于安装依赖与下载模型", settings.Status);
      Assert.Contains("不会下载或安装", settings.Status);
      Assert.Contains("下次启动", settings.Status);

      // 全局保存（environmentId=null）同样改变运行中环境的模型来源解析：
      // 有活动会话且请求包含模型来源时提示下次启动生效；未触碰模型来源
      // 时不叠加提示。
      await settings.SetSourcesAsync(null, "pypi", null,
        TestContext.Current.CancellationToken);
      Assert.Equal("<global>|pypi|<null>", manager.SourceSaves[1]);
      Assert.DoesNotContain("下次启动", settings.Status);

      await settings.SetSourcesAsync(null, "tuna-pypi", "modelscope",
        TestContext.Current.CancellationToken);
      Assert.Equal("<global>|tuna-pypi|modelscope", manager.SourceSaves[2]);
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

    // 与生产 list payload 一致：配方目录随列表同步，宿主据此核验未知配方。
    private static readonly IReadOnlyList<ManagedEnvironmentRecipe> Catalog =
    [
      new ManagedEnvironmentRecipe("rapidocr-cpu", "RapidOCR · CPU",
        ["text"], "cpu", "cpu"),
    ];

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
      Task.FromResult(new ManagedEnvironmentList("environment", 1, [.. environments],
        Recipes: Catalog));

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
        DefaultSourceIds: packageSourceId is null ? [] : [packageSourceId], Recipes: Catalog));
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

    public Task<ManagedEnvironmentQueryResult> FindCompatibleEnvironmentAsync(
      string recipe, CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentQueryResult(
        recipe,
        Recipes: Catalog,
        Selected: new ManagedEnvironmentSelection("environment", 1, recipe, "active_environment"),
        Environments:
        [
          new ManagedEnvironmentQueryMatch("environment", "活动环境", 1, "installed",
            Recipe: recipe, Active: true, Selected: true,
            ReasonCode: "selected_active_environment"),
        ]));

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

  /// <summary>
  /// 可控时序的冻结管理器：安装可阻塞到取消，取消时可模拟持久层已
  /// 写入的终态（中断投影、先于取消的自然失败、已提交记录、读取故障）。
  /// </summary>
  private sealed class WaitingManager : IManagedEnvironmentClient
  {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Cancelled { get; private set; }
    public string? ActiveId { get; init; }
    public Exception? InstallError { get; init; }
    public ManagedEnvironmentInstallFailure? Failure { get; set; }
    public string? CancelFailureReasonCode { get; init; } = "install_interrupted";
    public bool CommitBeforeCancel { get; init; }
    public bool FailListAfterCancel { get; init; }
    public bool BlockListAfterCancel { get; init; }

    private ManagedEnvironment current = new("environment", "保留环境", 1, "venv", "empty", "python",
      "ready", "empty", "unavailable", "not_checked", "not_started", null);

    public async Task<ManagedEnvironmentList> ListEnvironmentsAsync(
      CancellationToken cancellationToken = default)
    {
      if (BlockListAfterCancel && Cancelled)
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      if (FailListAfterCancel && Cancelled)
        throw new HttpRequestException("environments list unavailable");
      return new ManagedEnvironmentList(ActiveId, 0,
        [current with { LastInstallFailure = Failure }],
        Recipes: [new ManagedEnvironmentRecipe("rapidocr-cpu", "RapidOCR · CPU")]);
    }

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
      if (CommitBeforeCancel)
        current = current with
        {
          Revision = plan.EnvironmentRevision + 1,
          Status = "installed",
          DependencyState = "installed",
          Recipe = plan.Recipe,
        };
      try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
      catch (OperationCanceledException)
      {
        Cancelled = true;
        if (CancelFailureReasonCode is { } reason)
          Failure = new ManagedEnvironmentInstallFailure(
            "failed", plan.EnvironmentRevision, plan.Recipe, reason,
            reason == "install_interrupted" ? "preview_again" : "check_directory_permissions",
            reason == "install_interrupted"
              ? "Dependency installation was interrupted."
              : "could not prepare candidate environment: venv exit 1",
            null, ["tuna-pypi"], PlanId: plan.PlanId);
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

  /// <summary>
  /// 控制安装提交后结果刷新的时序：install 返回即标记已安装并进入刷新
  /// 阻塞点，验证取消入口关闭与刷新取消不改判安装结果。
  /// </summary>
  private sealed class RefreshGateManager : IManagedEnvironmentClient
  {
    public TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RefreshGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool RefreshTokenCancelled { get; private set; }
    public bool CancelRefreshByToken { get; init; }

    private ManagedEnvironment current = new("environment", "环境", 1, "venv", "empty", "python",
      "ready", "empty", "unavailable", "not_checked", "not_started", null);
    private int installReturned;

    public async Task<ManagedEnvironmentList> ListEnvironmentsAsync(
      CancellationToken cancellationToken = default)
    {
      if (Volatile.Read(ref installReturned) == 0)
        return new ManagedEnvironmentList(null, 0, [current],
          Recipes: [new ManagedEnvironmentRecipe("rapidocr-cpu", "RapidOCR · CPU")]);
      RefreshEntered.TrySetResult();
      if (CancelRefreshByToken)
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      await RefreshGate.Task.WaitAsync(cancellationToken);
      RefreshTokenCancelled = cancellationToken.IsCancellationRequested;
      return new ManagedEnvironmentList(null, 0, [current],
        Recipes: [new ManagedEnvironmentRecipe("rapidocr-cpu", "RapidOCR · CPU")]);
    }

    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
      string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentPlan("plan", environmentId, 1, 0, recipe,
        sourceIds ?? ["tuna-pypi"], RequestedSourceIds: sourceIds));

    public Task<ManagedEnvironment> InstallEnvironmentAsync(
      ManagedEnvironmentPlan plan, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default)
    {
      current = current with
      {
        Revision = plan.EnvironmentRevision + 1,
        Status = "installed",
        DependencyState = "installed",
        Recipe = plan.Recipe,
      };
      Volatile.Write(ref installReturned, 1);
      return Task.FromResult(current);
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
