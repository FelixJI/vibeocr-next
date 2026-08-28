using System;

namespace VibeOCR.Bootstrapper;

internal static class BootstrapperArtifactSmoke
{
    internal static bool IsRequested() => IsRequested(
        Environment.GetEnvironmentVariable("VIBEOCR_NEXT_TEST_MODE"),
        Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_VELOPACK_UPDATE"),
        Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_NONCE"));

    internal static bool IsRequested(string? mode, string? update, string? nonce) =>
        string.Equals(mode, "artifact-smoke", StringComparison.Ordinal) &&
        string.Equals(update, "1", StringComparison.Ordinal) &&
        Guid.TryParseExact(nonce, "N", out _);
}
