using VibeOCR.ProductLayout;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// Product-owned paths for the VibeOCR desktop application.
/// </summary>
/// <remarks>
/// Concrete model and Python environment paths are deliberately absent. The
/// Backend Runtime Installer remains their sole owner. This layout only
/// resolves the product's bound runtime manifest and installer entry points.
/// </remarks>
public sealed record PortableLayout(
    string Profile,
    string InstallRoot,
    string DataRoot,
    string OutputRoot,
    string ConfigFile,
    string ProductEntry,
    string AppEntry,
    string WebAssetsRoot,
    string ComponentLock,
    string RuntimeManifest,
    string RuntimeInstaller,
    string? PortableLayoutManifest)
{
    public static PortableLayout Resolve(
        string executable,
        string profile,
        string? portableLayoutManifest = null,
        string? installRootOverride = null,
        string? userDataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (profile is not ("production" or "winui-dev"))
        {
            throw new ArgumentException($"Unsupported profile: {profile}.", nameof(profile));
        }

        string candidate = Path.GetFullPath(executable);
        string extension = Path.GetExtension(candidate);
        string installRoot = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".app", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(candidate)
            ? Path.GetDirectoryName(candidate)!
            : candidate;
        ResolvedProductLayout? productLayout = string.IsNullOrWhiteSpace(installRootOverride)
            ? null
            : ResolvedProductLayout.Open(installRootOverride);
        if (productLayout is not null)
        {
            installRoot = productLayout.InstallRoot;
        }
        string productionDataRoot = string.IsNullOrWhiteSpace(userDataRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeOCR")
            : Path.GetFullPath(userDataRoot);
        string dataRoot = profile == "production"
            ? productionDataRoot
            : Path.Combine(installRoot, "data", "profiles", profile);
        string scopedRoot = dataRoot;
        string? explicitManifest = string.IsNullOrWhiteSpace(portableLayoutManifest)
            ? null
            : Path.GetFullPath(portableLayoutManifest);

        return new PortableLayout(
            profile,
            installRoot,
            dataRoot,
            Path.Combine(scopedRoot, "output"),
            Path.Combine(scopedRoot, "config", "app_settings.json"),
            productLayout?.PublicEntry ?? Path.Combine(installRoot, "VibeOCR.exe"),
            productLayout?.AppEntry ?? candidate,
            productLayout?.WebAssetsRoot ?? Path.Combine(installRoot, "WebAssets"),
            productLayout?.ComponentLock ?? Path.Combine(installRoot, "component-lock.json"),
            productLayout?.RuntimeManifest ??
                Path.Combine(installRoot, "backend", "runtime-manifest.json"),
            productLayout?.RuntimeInstaller ?? Path.Combine(
                installRoot,
                "runtime-installer",
                "vibeocr-runtime-installer.exe"),
            explicitManifest);
    }
}
