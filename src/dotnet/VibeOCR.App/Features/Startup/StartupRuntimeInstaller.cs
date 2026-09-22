using VibeOCR.Platform.Bootstrap;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.App.Features.Startup;

internal static class StartupRuntimeInstaller
{
    internal static async Task<RuntimeLaunch> EnsureBaseRuntimeAsync(
        IRuntimeInstallerClient installer,
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken) =>
        await installer.EnsureAsync(
            await installer.ReadStartupSelectionAsync(cancellationToken),
            $"startup-{Guid.NewGuid():N}",
            progress,
            cancellationToken);
}
