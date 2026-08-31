using VibeOCR.Bootstrapper;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class BootstrapperArtifactSmokeTests
{
    [Fact]
    public void ArtifactSmokeBypassRequiresEveryAuthenticatedSignal()
    {
        string nonce = Guid.NewGuid().ToString("N");

        Assert.True(BootstrapperArtifactSmoke.IsRequested("artifact-smoke", "1", nonce));
        Assert.False(BootstrapperArtifactSmoke.IsRequested("production", "1", nonce));
        Assert.False(BootstrapperArtifactSmoke.IsRequested("artifact-smoke", "0", nonce));
        Assert.False(BootstrapperArtifactSmoke.IsRequested("artifact-smoke", "1", "not-a-guid"));
    }
}
