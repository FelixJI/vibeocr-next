using VibeOCR.ProductLayout;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// The portable state root is not writable. Fail closed: the user must move
/// the product directory; the app never falls back to LocalAppData, the user
/// profile, or the system temp directory and never requests elevation.
/// </summary>
public sealed class PortableLayoutException : InvalidOperationException
{
    public PortableLayoutException(string message) : base(message)
    {
    }
}

/// <summary>
/// Product-owned paths for the VibeOCR desktop application. Everything the
/// product mutates lives under <c>&lt;portable-root&gt;/state</c>; the stable
/// install root keeps only Velopack payload and read-only assets.
/// </summary>
/// <remarks>
/// Concrete model and Python environment paths are deliberately absent. The
/// Backend Runtime Installer remains their sole owner; the runtime share it
/// uses is anchored on this layout's state root via the generated
/// portable-layout manifest.
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
    /// <summary>Stable mutable root; identical to <see cref="DataRoot"/>.</summary>
    public string StateRoot => DataRoot;
    public string CacheRoot => Path.Combine(DataRoot, "cache");
    public string LogsRoot => Path.Combine(DataRoot, "logs");
    public string RuntimesRoot => Path.Combine(DataRoot, "runtimes");
    public string ModelsRoot => Path.Combine(DataRoot, "models");
    public string UpdateRoot => Path.Combine(DataRoot, "update");
    public string TempRoot => Path.Combine(DataRoot, "temp");
    public string LocksRoot => Path.Combine(DataRoot, "locks");
    public string WebView2Root => Path.Combine(DataRoot, "webview2");

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
        // 生产可变状态固定在便携根下的 state/,不再默认 LocalApplicationData;
        // userDataRoot 仅作为测试/隔离注入。
        string dataRoot = !string.IsNullOrWhiteSpace(userDataRoot)
            ? Path.GetFullPath(userDataRoot)
            : profile == "production"
                ? Path.Combine(installRoot, "state")
                : Path.Combine(installRoot, "data", "profiles", profile);
        string scopedRoot = dataRoot;
        string? explicitManifest = string.IsNullOrWhiteSpace(portableLayoutManifest)
            ? null
            : Path.GetFullPath(portableLayoutManifest);
        // 生产默认在便携根生成 portable-layout.json,把安装器的运行时/模型
        // 存储锚定到 state/;显式传入(测试/隔离)优先。
        string? resolvedManifest = explicitManifest ??
            (profile == "production"
                ? Path.Combine(installRoot, "portable-layout.json")
                : null);

        var layout = new PortableLayout(
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
            resolvedManifest);
        layout.ValidateContainment();
        return layout;
    }

    /// <summary>
    /// Whether the Backend Runtime Installer receives an explicit shared
    /// layout anchored on the portable state root (production).
    /// </summary>
    public bool UsesSharedRuntimeStore =>
        Profile == "production" && PortableLayoutManifest is not null;

    /// <summary>
    /// Prepare the portable state root: verify containment, run the
    /// create/write/rename/delete probe, create the stable state directories,
    /// and (production) write the idempotent portable-layout manifest that
    /// anchors the installer's runtime/model store on <c>state</c>.
    /// </summary>
    public void EnsurePortableState()
    {
        ValidateContainment();
        if (Profile != "production")
        {
            return;
        }
        ProbeWritableStateRoot();
        foreach (string directory in new[]
        {
            StateRoot,
            CacheRoot,
            Path.GetDirectoryName(ConfigFile)!,
            LogsRoot,
            ModelsRoot,
            OutputRoot,
            UpdateRoot,
            TempRoot,
            LocksRoot,
            WebView2Root,
        })
        {
            Directory.CreateDirectory(directory);
        }
        WritePortableLayoutManifest();
    }

    /// <summary>
    /// Fail closed unless the state root supports create/write/rename/delete
    /// for this process. Never falls back to another location.
    /// </summary>
    public void ProbeWritableStateRoot()
    {
        try
        {
            Directory.CreateDirectory(StateRoot);
            string probe = Path.Combine(StateRoot, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            string renamed = probe + ".renamed";
            File.Move(probe, renamed);
            string readBack = File.ReadAllText(renamed);
            File.Delete(renamed);
            if (readBack != "probe")
            {
                throw new IOException("probe content mismatch");
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PortableLayoutException(
                $"无法写入状态目录 {StateRoot}。请把 VibeOCR 移动到当前用户可写的目录后重试；"
                + $"应用不会请求管理员权限，也不会改用其他目录。原因：{error.Message}");
        }
    }

    private void WritePortableLayoutManifest()
    {
        if (PortableLayoutManifest is not { } manifestPath)
        {
            return;
        }
        const string payload =
            "{\"products\":{\"next\":{\"component_lock\":\"app/metadata/component-lock.json\",\"root\":\".\"}},\"schema_version\":1,\"shared_root\":\"state\"}";
        string? existing = File.Exists(manifestPath)
            ? File.ReadAllText(manifestPath).Trim()
            : null;
        if (existing == payload)
        {
            return;
        }
        string directory = Path.GetDirectoryName(manifestPath)!;
        Directory.CreateDirectory(directory);
        // 原子替换,避免安装器读到半写状态。
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(manifestPath)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, payload + Environment.NewLine);
        File.Move(temporary, manifestPath, overwrite: true);
    }

    /// <summary>
    /// The state root must stay inside the install root after full path
    /// resolution, and no path component may be a reparse point escaping it.
    /// </summary>
    private void ValidateContainment()
    {
        string installRoot = Path.GetFullPath(InstallRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string stateRoot = Path.GetFullPath(StateRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!stateRoot.StartsWith(
                installRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PortableLayoutException(
                $"状态目录必须位于安装目录内：{stateRoot} 不在 {installRoot} 之下。");
        }
        RejectEscapingReparsePoints(installRoot, stateRoot);
    }

    private static void RejectEscapingReparsePoints(string installRoot, string stateRoot)
    {
        DirectoryInfo? directory = new(stateRoot);
        string normalizedInstallRoot = installRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (directory is not null &&
            directory.FullName
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .StartsWith(normalizedInstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            string? linkTarget = directory.LinkTarget;
            if (linkTarget is not null)
            {
                string resolved = Path.GetFullPath(
                    linkTarget,
                    directory.Parent?.FullName ?? Path.GetPathRoot(directory.FullName) ?? "/");
                if (!resolved.StartsWith(normalizedInstallRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PortableLayoutException(
                        $"状态目录包含指向安装目录外的链接：{directory.FullName} → {resolved}。");
                }
            }
            directory = directory.Parent;
        }
    }
}
