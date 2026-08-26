using VibeOCR.Platform.Bootstrap;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.App.Features.Startup;

internal static class StartupRuntimeInstaller
{
    internal static Task<RuntimeLaunch> EnsureBaseRuntimeAsync(
        IRuntimeInstallerClient installer,
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken) =>
        installer.EnsureAsync(
            new RuntimeInstallSelection
            {
                InstallComponentIds = [],
            },
            $"startup-{Guid.NewGuid():N}",
            progress,
            cancellationToken);
}
