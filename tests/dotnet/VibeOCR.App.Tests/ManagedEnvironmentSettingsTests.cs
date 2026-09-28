using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Features.Settings;
using VibeOCR.Platform.Bootstrap;
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
    Assert.Equal("安装已取消；原环境保持不变。", settings.Status);
    Assert.Equal("environment", settings.Snapshot?.Environments[0].Id);
  }

  private sealed class WaitingManager : IManagedEnvironmentClient
  {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Cancelled { get; private set; }

    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentList(null, 0, [
        new ManagedEnvironment("environment", "保留环境", 1, "venv", "empty", "python",
          "ready", "empty", "unavailable", "not_checked", "not_started", null)
      ]));

    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
      string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
      CancellationToken cancellationToken = default) =>
      Task.FromResult(new ManagedEnvironmentPlan("plan", environmentId, 1, 0, recipe,
        sourceIds ?? []));

    public async Task<ManagedEnvironment> InstallEnvironmentAsync(
      ManagedEnvironmentPlan plan, CancellationToken cancellationToken = default)
    {
      Started.SetResult();
      try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
      catch (OperationCanceledException)
      {
        Cancelled = true;
        throw;
      }
      throw new InvalidOperationException("Install unexpectedly completed.");
    }

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
