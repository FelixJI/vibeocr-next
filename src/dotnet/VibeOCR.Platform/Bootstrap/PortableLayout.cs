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
            LocksRoot,
            WebView2Root,
        })
        {
            EnsureContainedDirectory(directory);
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
            EnsureContainedDirectory(StateRoot);
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
        catch (PortableLayoutException error)
        {
            throw new PortableLayoutException(
                $"无法使用状态目录 {StateRoot}。请把 VibeOCR 移动到安全且可写的目录后重试；"
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
        WritePortableMetadataFileAtomically(manifestPath, payload + Environment.NewLine);
    }

    /// <summary>
    /// The state root must stay inside the install root after full path
    /// resolution, and no path component may be a reparse point escaping it.
    /// </summary>
    private void ValidateContainment()
    {
        string installRoot = NormalizeDirectory(InstallRoot);
        string stateRoot = NormalizeDirectory(StateRoot);
        if (!IsStrictDescendant(installRoot, stateRoot))
        {
            throw new PortableLayoutException(
                $"状态目录必须位于安装目录内：{stateRoot} 不在 {installRoot} 之下。");
        }
        RejectEscapingReparsePoints(installRoot, stateRoot);
    }

    private static void RejectEscapingReparsePoints(string installRoot, string stateRoot)
    {
        string relative = Path.GetRelativePath(installRoot, stateRoot);
        string current = installRoot;
        foreach (string segment in relative.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists ||
                !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }
            FileSystemInfo? resolved = directory.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null || !IsDescendantOrSelf(installRoot, NormalizeDirectory(resolved.FullName)))
            {
                throw new PortableLayoutException(
                    $"状态目录包含指向安装目录外或无法解析的重解析点：{directory.FullName}。");
            }
        }
    }

    /// <summary>
    /// Creates one state directory only after validating the complete existing
    /// path chain, and validates it again after creation. Callers never need a
    /// separate validate-then-create sequence.
    /// </summary>
    private void EnsureContainedDirectory(string directory)
    {
        string fullDirectory = NormalizeDirectory(directory);
        string installRoot = NormalizeDirectory(InstallRoot);
        if (!IsDescendantOrSelf(installRoot, fullDirectory))
        {
            throw new PortableLayoutException($"目录不在安装目录内：{fullDirectory}。");
        }
        RejectEscapingReparsePoints(installRoot, fullDirectory);
        Directory.CreateDirectory(fullDirectory);
        RejectEscapingReparsePoints(installRoot, fullDirectory);
    }

    /// <summary>
    /// Owns the check/open/write/rename sequence for product-controlled
    /// metadata. A caller cannot validate a path and later write it through a
    /// separate TOCTOU seam; the final parent chain is checked before opening
    /// the temporary file and again before promotion.
    /// </summary>
    public void WriteStateFileAtomically(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        WriteStateFileAtomically(path, new System.Text.UTF8Encoding(false).GetBytes(contents));
    }

    /// <summary>
    /// Atomically writes product-controlled bytes under the portable state
    /// root. Binary callers, such as the migration backup, retain the same
    /// containment and reparse-point guarantees as text metadata callers.
    /// </summary>
    public void WriteStateFileAtomically(string path, byte[] contents)
        => WriteContainedFileAtomically(path, StateRoot, contents);

    private void WritePortableMetadataFileAtomically(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        WriteContainedFileAtomically(
            path,
            InstallRoot,
            new System.Text.UTF8Encoding(false).GetBytes(contents));
    }

    private void WriteContainedFileAtomically(string path, string permittedRoot, byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        string fullPath = Path.GetFullPath(path);
        if (!IsDescendantOrSelf(NormalizeDirectory(permittedRoot), fullPath))
        {
            throw new PortableLayoutException("状态文件必须位于 Portable state 根目录内。");
        }
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new PortableLayoutException("元数据路径缺少父目录。");
        EnsureContainedDirectory(directory);
        string installRoot = NormalizeDirectory(InstallRoot);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            RejectEscapingReparsePoints(installRoot, NormalizeDirectory(directory));
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            }))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            RejectEscapingReparsePoints(installRoot, NormalizeDirectory(directory));
            File.Move(temporary, fullPath, overwrite: true);
            RejectEscapingReparsePoints(installRoot, NormalizeDirectory(directory));
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsStrictDescendant(string root, string candidate) =>
        !string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) &&
        IsDescendantOrSelf(root, candidate);

    private static bool IsDescendantOrSelf(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
            relative is not ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string NormalizeDirectory(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
