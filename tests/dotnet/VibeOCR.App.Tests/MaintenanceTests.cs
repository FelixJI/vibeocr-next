using VibeOCR.App.Features.Settings;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// N4 maintenance orchestration: explicit-intent ensure (base-only vs
/// feature closure), requested/effective echo, durable cancel/retry via the
/// existing installer client, and the workbench projection.
/// </summary>
public sealed class MaintenanceTests : IDisposable
{
    private readonly string _configFile =
        Path.Combine(Path.GetTempPath(), $"vibeocr-maintenance-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task InstallSendsExplicitIntentAndEchoesRequestedEffective()
    {
        var fake = new FakeRuntimeInstallerClient();
        var settings = await LoadedSettingsAsync(fake, sources: ["tuna-pypi"]);
        settings.SetPendingAccelerator("nvidia_cuda");
        settings.SetFeatureEnabled("document_parsing", true);
        settings.SetFeatureEnabled("gpu_runtime", true);

        await settings.InstallPendingAsync(CancellationToken.None);

        Assert.NotNull(fake.LastIntent);
        Assert.Equal(
            ["document_parsing", "gpu_runtime"],
            fake.LastIntent!.InstallComponentIds);
        Assert.Equal(["tuna-pypi"], fake.LastIntent.DownloadSourceIds);
        Assert.False(string.IsNullOrEmpty(fake.LastOperationId));
        Assert.False(settings.Maintenance.State.IsRunning);
        Assert.Equal("succeeded", settings.Maintenance.State.StatusCode);
        Assert.Equal(
            ["document_parsing", "gpu_runtime"],
            settings.Maintenance.State.RequestedComponentIds);
        // dependency closure 扩大时如实回显。
        Assert.Equal(
            ["document_parsing", "gpu_runtime", "runtime_host"],
            settings.Maintenance.State.EffectiveComponentIds);
        Assert.Equal(["tuna-pypi"], settings.Maintenance.State.RequestedSourceIds);
    }

    [Fact]
    public async Task InstallWithoutFeaturesSendsExplicitBaseOnlyList()
    {
        var fake = new FakeRuntimeInstallerClient();
        var settings = await LoadedSettingsAsync(fake, sources: null);

        await settings.InstallPendingAsync(CancellationToken.None);

        Assert.NotNull(fake.LastIntent!.InstallComponentIds);
        Assert.Empty(fake.LastIntent.InstallComponentIds);
        Assert.Null(fake.LastIntent.DownloadSourceIds);
    }

    [Fact]
    public async Task UnknownFeatureStagingIsRejectedWithoutInstallerCall()
    {
        var fake = new FakeRuntimeInstallerClient();
        var settings = await LoadedSettingsAsync(fake, sources: null);

        settings.SetPendingAccelerator("cpu");
        settings.SetFeatureEnabled("quantum_parsing", true);
        Assert.Contains("未知功能", settings.Status);
        Assert.Empty(settings.PendingFeatureIds);
        // 未暂存任何功能 → 显式 base-only,而不是猜测安装范围。
        await settings.InstallPendingAsync(CancellationToken.None);

        Assert.NotNull(fake.LastIntent!.InstallComponentIds);
        Assert.Empty(fake.LastIntent.InstallComponentIds);
    }

    [Fact]
    public async Task FailedInstallIsRetryableAndRetryReusesSourceIntent()
    {
        var fake = new FakeRuntimeInstallerClient { FailNextInstall = true };
        var settings = await LoadedSettingsAsync(fake, sources: null);
        settings.SetFeatureEnabled("document_parsing", true);
        string? failedOperationId = null;
        fake.EnsureStarted = operationId => failedOperationId = operationId;

        await settings.InstallPendingAsync(CancellationToken.None);
        Assert.Equal("failed", settings.Maintenance.State.StatusCode);
        Assert.True(settings.Maintenance.State.CanRetry);

        fake.FailNextInstall = false;
        await settings.RetryMaintenanceAsync(CancellationToken.None);
        Assert.Equal(failedOperationId, fake.RetrySourceOperationId);
        Assert.Null(fake.RetrySelection);
        Assert.Equal("succeeded", settings.Maintenance.State.StatusCode);
        Assert.False(settings.Maintenance.State.CanRetry);
    }

    [Fact]
    public async Task CancelStopsTheRunningOperation()
    {
        var fake = new FakeRuntimeInstallerClient { HangOnInstall = true };
        var settings = await LoadedSettingsAsync(fake, sources: null);
        Task install = settings.InstallPendingAsync(CancellationToken.None);
        await fake.InstallStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        settings.CancelMaintenance();
        await install;

        Assert.Equal("cancelled", settings.Maintenance.State.StatusCode);
        Assert.Contains("已取消", settings.Status);
        Assert.True(settings.Maintenance.State.CanRetry);
    }

    [Fact]
    public async Task RetryCancellationReturnsToTerminalStateAndCanRunAgain()
    {
        var fake = new FakeRuntimeInstallerClient { FailNextInstall = true };
        var settings = await LoadedSettingsAsync(fake, sources: null);
        await settings.InstallPendingAsync(CancellationToken.None);
        fake.HangOnRetry = true;

        Task retry = settings.RetryMaintenanceAsync(CancellationToken.None);
        await fake.RetryStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        settings.CancelMaintenance();
        await retry;

        Assert.Equal("cancelled", settings.Maintenance.State.StatusCode);
        Assert.True(settings.Maintenance.State.CanRetry);
        fake.HangOnRetry = false;
        await settings.RetryMaintenanceAsync(CancellationToken.None);
        Assert.Equal("succeeded", settings.Maintenance.State.StatusCode);
    }

    [Fact]
    public async Task RetryUnexpectedFailureReturnsToTerminalStateAndCanRunAgain()
    {
        var fake = new FakeRuntimeInstallerClient { FailNextInstall = true };
        var settings = await LoadedSettingsAsync(fake, sources: null);
        await settings.InstallPendingAsync(CancellationToken.None);
        fake.FailNextRetryUnexpectedly = true;

        await Assert.ThrowsAsync<IOException>(() =>
            settings.RetryMaintenanceAsync(CancellationToken.None));
        Assert.Equal("failed", settings.Maintenance.State.StatusCode);
        Assert.True(settings.Maintenance.State.CanRetry);

        fake.FailNextRetryUnexpectedly = false;
        await settings.RetryMaintenanceAsync(CancellationToken.None);
        Assert.Equal("succeeded", settings.Maintenance.State.StatusCode);
    }

    [Fact]
    public void BridgeParsesMaintenanceCommandsAndSerializesTheOperationState()
    {
        Guid sessionId = Guid.NewGuid();
        string Command(string scope, string action) =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 2,
                kind = "request",
                id = Guid.NewGuid(),
                type = "app.command",
                payload = new
                {
                    sessionId,
                    command = new
                    {
                        scope,
                        action,
                        arguments = System.Text.Json.JsonDocument.Parse("{}").RootElement,
                    },
                },
            });

        Assert.IsType<InstallRuntimeCommand>(
            WorkbenchBridgeCodecTestsHelper.Parse(Command("settings", "installRuntime"), sessionId).Command);
        Assert.IsType<CancelRuntimeMaintenanceCommand>(
            WorkbenchBridgeCodecTestsHelper.Parse(Command("settings", "cancelRuntimeMaintenance"), sessionId).Command);
        Assert.IsType<RetryRuntimeMaintenanceCommand>(
            WorkbenchBridgeCodecTestsHelper.Parse(Command("settings", "retryRuntimeMaintenance"), sessionId).Command);

        var state = new SettingsWorkbenchState(
            WorkbenchTheme.System,
            false,
            "settings.ready",
            "cpu",
            false,
            "Ctrl+Alt+Q",
            Maintenance: new SettingsMaintenanceState(
                true,
                "running",
                "ui-op-1",
                ["document_parsing"],
                ["document_parsing", "gpu_runtime"],
                ["tuna-pypi"],
                ["pypi"],
                CanCancel: true,
                CanRetry: false));
        string payload = WorkbenchBridgeCodecTestsHelper.SerializeState(
            sessionId,
            new WorkbenchStateEnvelope(3, "settings", WorkbenchStateChange.Replace, state));
        Assert.Contains("\"maintenance\":{", payload);
        Assert.Contains("\"statusCode\":\"running\"", payload);
        Assert.Contains("\"requestedComponentIds\":[\"document_parsing\"]", payload);
        Assert.Contains("\"effectiveComponentIds\":[\"document_parsing\",\"gpu_runtime\"]", payload);
        Assert.Contains("\"requestedSourceIds\":[\"tuna-pypi\"]", payload);
        Assert.Contains("\"effectiveSourceIds\":[\"pypi\"]", payload);
        Assert.Contains("\"canCancel\":true", payload);
        Assert.DoesNotContain("endpoint", payload);
    }

    private async Task<SettingsViewModel> LoadedSettingsAsync(
        FakeRuntimeInstallerClient installer,
        IReadOnlyList<string>? sources)
    {
        var fake = new SelectionHealthClient(sources);
        var settings = new SettingsViewModel(
            fake,
            configFile: _configFile,
            installerFactory: () => installer);
        await settings.LoadSnapshotAsync(CancellationToken.None);
        return settings;
    }

    private sealed class SelectionHealthClient : InferenceClientStub
    {
        private readonly IReadOnlyList<string>? _sources;

        public SelectionHealthClient(IReadOnlyList<string>? sources) => _sources = sources;

        public override Task<ResidencyStatus> GetResidencyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ResidencyStatus());

        public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(MaintenanceHealth());

        public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsSnapshot
            {
                DownloadSourceIds = _sources is { Count: > 0 } ? _sources : null,
            });

        private static Wire.Health MaintenanceHealth() => new()
        {
            SchemaVersion = 2,
            InstanceId = "sup-1",
            ProtocolVersion = 2,
            Ready = true,
            Draining = false,
            Capabilities =
            [
                RuntimeSelectionService.DownloadSourceCapability,
                RuntimeSelectionService.ComponentSelectionCapability,
            ],
            CapabilityDescriptors =
            [
                new Wire.CapabilityDescriptor
                {
                    Name = RuntimeSelectionService.DownloadSourceCapability,
                    Lifecycle = "active",
                    IntroducedIn = "2.7.0",
                    DeprecatedIn = null,
                    SunsetAt = null,
                    Replacement = null,
                    DownloadSourceCatalog = new Wire.DownloadSourceCatalog
                    {
                        Sources =
                        [
                            new Wire.DownloadSourceDescriptor
                            {
                                Kind = "package_index",
                                Id = "tuna-pypi",
                                Endpoint = "https://mirrors.tuna.example/pypi/simple",
                            },
                        ],
                    },
                },
                new Wire.CapabilityDescriptor
                {
                    Name = RuntimeSelectionService.ComponentSelectionCapability,
                    Lifecycle = "active",
                    IntroducedIn = "2.7.0",
                    DeprecatedIn = null,
                    SunsetAt = null,
                    Replacement = null,
                    ComponentVariantCatalog = new Wire.ComponentVariantCatalog
                    {
                        Variants =
                        [
                            new Wire.ComponentVariantDescriptor
                            {
                                FeatureId = "document_parsing",
                                Accelerator = "cpu",
                                ComponentId = "document_parsing",
                            },
                            new Wire.ComponentVariantDescriptor
                            {
                                FeatureId = "document_parsing",
                                Accelerator = "nvidia_cuda",
                                ComponentId = "document_parsing",
                            },
                            new Wire.ComponentVariantDescriptor
                            {
                                FeatureId = "gpu_runtime",
                                Accelerator = "nvidia_cuda",
                                ComponentId = "gpu_runtime",
                            },
                        ],
                    },
                },
            ],
        };
    }

    private sealed class FakeRuntimeInstallerClient : IRuntimeInstallerClient
    {
        public RuntimeInstallSelection? LastIntent { get; private set; }
        public string? LastOperationId { get; private set; }
        public string? RetrySourceOperationId { get; private set; }
        public RuntimeInstallSelection? RetrySelection { get; private set; }
        public bool FailNextInstall { get; set; }
        public bool HangOnInstall { get; set; }
        public bool HangOnRetry { get; set; }
        public bool FailNextRetryUnexpectedly { get; set; }
        public Action<string>? EnsureStarted { get; set; }
        public TaskCompletionSource<bool> InstallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> RetryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RuntimeInspection> InspectAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeLaunch> EnsureAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeLaunch> RepairAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeLaunch> EnsureAsync(
            string operationId,
            IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeLaunch> RepairAsync(
            string operationId,
            IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public async Task<RuntimeLaunch> EnsureAsync(
            RuntimeInstallSelection? selection,
            string operationId,
            IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastIntent = selection;
            LastOperationId = operationId;
            EnsureStarted?.Invoke(operationId);
            if (HangOnInstall)
            {
                InstallStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }
            if (FailNextInstall)
            {
                progress?.Report(Event(operationId, Host.RuntimeOperationState.Failed,
                    requested: selection?.InstallComponentIds ?? [],
                    effective: []));
                throw new RuntimeInstallerException("install failed");
            }
            progress?.Report(Event(operationId, Host.RuntimeOperationState.Running,
                requested: selection?.InstallComponentIds ?? [],
                effective: selection?.InstallComponentIds ?? []));
            // 模拟依赖闭包扩大:effective 额外包含 runtime_host。
            progress?.Report(Event(operationId, Host.RuntimeOperationState.Succeeded,
                requested: selection?.InstallComponentIds ?? [],
                effective: [.. (selection?.InstallComponentIds ?? []), "runtime_host"]));
            return new RuntimeLaunch(
                @"C:\store\python.exe",
                "vibeocr.backend.supervisor.main",
                @"C:\store",
                @"C:\store\models",
                new Dictionary<string, string>());
        }

        public Task<RuntimeHostEnvelope> CancelAsync(
            string operationId,
            string commandId,
            long? expectedSequence = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public async Task<RuntimeHostEnvelope> RetryAsync(
            string operationId,
            string newOperationId,
            RuntimeInstallSelection? selection,
            string commandId,
            CancellationToken cancellationToken = default)
        {
            RetrySourceOperationId = operationId;
            RetrySelection = selection;
            LastOperationId = newOperationId;
            if (HangOnRetry)
            {
                RetryStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (FailNextRetryUnexpectedly)
            {
                throw new IOException("retry transport failed");
            }
            return new RuntimeHostEnvelope(
                2,
                true,
                "ensure",
                State: null,
                Launch: new RuntimeLaunch(
                    @"C:\store\python.exe",
                    "vibeocr.backend.supervisor.main",
                    @"C:\store",
                    @"C:\store\models",
                    new Dictionary<string, string>()),
                Error: null,
                Profile: null,
                Maintenance: new Host.RuntimeMaintenanceSnapshot
                {
                    OperationId = newOperationId,
                    Sequence = 3,
                    Operation = Host.RuntimeHostOperation.Ensure,
                    OperationState = Host.RuntimeOperationState.Succeeded,
                    Phase = Host.RuntimeMaintenancePhase.CommitRuntime,
                    ProfileId = "win-x64-cpu",
                    RequestedComponentIds = ["document_parsing"],
                    EffectiveComponentIds = ["document_parsing"],
                    UpdatedAt = "2026-08-18T00:00:00Z",
                },
                NegotiatedCapabilities: null,
                CapabilityDescriptors: null);
        }

        public Task<RuntimeMaintenanceObserveEnvelope> ObserveAsync(
            string operationId,
            long afterSequence,
            int limit = 128,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        private static Host.RuntimeMaintenanceEvent Event(
            string operationId,
            Host.RuntimeOperationState state,
            IReadOnlyList<string> requested,
            IReadOnlyList<string> effective) => new()
        {
            ProtocolVersion = 2,
            EventVersion = 1,
            EventType = Host.RuntimeMaintenanceEventType.Snapshot,
            Sequence = 1,
            Operation = Host.RuntimeHostOperation.Ensure,
            Snapshot = new Host.RuntimeMaintenanceSnapshot
            {
                OperationId = operationId,
                Sequence = 1,
                Operation = Host.RuntimeHostOperation.Ensure,
                OperationState = state,
                Phase = Host.RuntimeMaintenancePhase.InstallProfile,
                ProfileId = "win-x64-cpu",
                RequestedComponentIds = requested,
                EffectiveComponentIds = effective,
                UpdatedAt = "2026-08-18T00:00:00Z",
            },
            MessageCode = "runtime.installing",
        };
    }

    public void Dispose()
    {
        if (File.Exists(_configFile))
        {
            File.Delete(_configFile);
        }
    }
}

internal static class WorkbenchBridgeCodecTestsHelper
{
    public static WorkbenchCommandEnvelope Parse(string json, Guid sessionId) =>
        VibeOCR.App.Web.WorkbenchBridgeCodec.ParseCommand(json, sessionId);

    public static string SerializeState(Guid sessionId, WorkbenchStateEnvelope state) =>
        VibeOCR.App.Web.WorkbenchBridgeCodec.SerializeState(sessionId, state);
}
