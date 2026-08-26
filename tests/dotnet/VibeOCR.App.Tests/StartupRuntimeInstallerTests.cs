using VibeOCR.App.Features.Startup;
using VibeOCR.Platform.Bootstrap;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class StartupRuntimeInstallerTests
{
    [Fact]
    public async Task StartupEnsureSelectsBaseRuntimeOnly()
    {
        var installer = new RecordingInstaller();

        await StartupRuntimeInstaller.EnsureBaseRuntimeAsync(
            installer,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(installer.Selection);
        Assert.NotNull(installer.Selection.InstallComponentIds);
        Assert.Empty(installer.Selection.InstallComponentIds);
        Assert.StartsWith("startup-", installer.OperationId, StringComparison.Ordinal);
    }

    private sealed class RecordingInstaller : IRuntimeInstallerClient
    {
        public RuntimeInstallSelection? Selection { get; private set; }
        public string? OperationId { get; private set; }

        public Task<RuntimeInspection> InspectAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeLaunch> EnsureAsync(CancellationToken cancellationToken = default) =>
            LaunchAsync();

        public Task<RuntimeLaunch> RepairAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeLaunch> EnsureAsync(
            string operationId,
            IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
            CancellationToken cancellationToken = default) =>
            LaunchAsync();

        public Task<RuntimeLaunch> EnsureAsync(
            RuntimeInstallSelection? selection,
            string operationId,
            IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Selection = selection;
            OperationId = operationId;
            return LaunchAsync();
        }

        public Task<RuntimeLaunch> RepairAsync(
            string operationId,
            IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeHostEnvelope> CancelAsync(
            string operationId,
            string commandId,
            long? expectedSequence = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeHostEnvelope> RetryAsync(
            string operationId,
            string newOperationId,
            RuntimeInstallSelection? selection,
            string commandId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RuntimeMaintenanceObserveEnvelope> ObserveAsync(
            string operationId,
            long afterSequence,
            int limit = 128,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        private static Task<RuntimeLaunch> LaunchAsync() => Task.FromResult(new RuntimeLaunch(
            @"C:\store\python.exe",
            "vibeocr.backend.supervisor.main",
            @"C:\store",
            @"C:\store\models",
            new Dictionary<string, string>()));
    }
}
