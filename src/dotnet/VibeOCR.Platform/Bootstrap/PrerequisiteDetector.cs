using System.Diagnostics;
using System.Runtime.InteropServices;
using VibeOCR.ProductLayout;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.System;

namespace VibeOCR.Platform.Bootstrap;

public enum PrerequisiteKind
{
    DotNetRuntime,
    WindowsAppRuntime,
    WebView2Runtime,
    RuntimePython,
}

public sealed record PrerequisiteSnapshot(
    string? DotNetVersion,
    string? WindowsAppRuntimeVersion,
    string? WebView2Version,
    bool RuntimePythonPresent)
{
    /// <summary>System probe snapshot (winui-dev only): production uses
    /// <see cref="BundledProductProbe"/> so installed system components can
    /// never mask a damaged bundled payload.</summary>
    public static PrerequisiteSnapshot CaptureSystem(PortableLayout layout) =>
        WindowsPrerequisiteProbe.Capture(layout);
}

public sealed record PrerequisiteStatus(
    PrerequisiteKind Kind,
    bool IsInstalled,
    string? InstalledVersion,
    string MinimumVersion,
    string RepairUri);

public sealed record PrerequisiteReport(IReadOnlyList<PrerequisiteStatus> Items)
{
    public bool IsReady => Items.All(item => item.IsInstalled);
    public IEnumerable<PrerequisiteStatus> Missing => Items.Where(item => !item.IsInstalled);
}

public sealed class PrerequisiteDetector
{
    // Framework-dependent winui-dev floor (Directory.Packages.props
    // Microsoft.WindowsAppSDK); production readiness never consults the
    // installed system runtime, only the bundled payload.
    public const string MinimumDotNetVersion = "10.0.0";
    public const string MinimumWindowsAppRuntimeVersion = "2.7.0";
    public const string BundledMinimum = "Bundled";
    public const string ProductRepairUri = "repair://vibeocr/product";
    private readonly Func<PortableLayout, PrerequisiteSnapshot>? _systemCapture;

    public PrerequisiteDetector()
    {
    }

    // Test seam for the winui-dev system snapshot; production always probes
    // the real bundled payload below.
    internal PrerequisiteDetector(Func<PortableLayout, PrerequisiteSnapshot> systemCapture)
    {
        _systemCapture = systemCapture;
    }

    public PrerequisiteReport Detect(PortableLayout layout) => Detect(layout, bundledProduct: false);

    /// <summary>诊断语义随产品模式（App 构建/布局）而不是 Profile；
    /// 产品模式按随包载荷判定 .NET/WinAppRuntime/Python（已安装系统组件
    /// 不掩盖损坏）；WebView2 一律检查系统 Evergreen（产品不再随包分发）。</summary>
    public PrerequisiteReport Detect(PortableLayout layout, bool bundledProduct)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return bundledProduct
            ? BundledProductProbe.Report(layout)
            : SystemReport((_systemCapture ?? PrerequisiteSnapshot.CaptureSystem)(layout));
    }

    private static PrerequisiteReport SystemReport(PrerequisiteSnapshot snapshot) => new(
    [
        Status(
            PrerequisiteKind.DotNetRuntime,
            snapshot.DotNetVersion,
            MinimumDotNetVersion,
            "https://dotnet.microsoft.com/download/dotnet/10.0"),
        Status(
            PrerequisiteKind.WindowsAppRuntime,
            snapshot.WindowsAppRuntimeVersion,
            MinimumWindowsAppRuntimeVersion,
            "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads"),
        new PrerequisiteStatus(
            PrerequisiteKind.WebView2Runtime,
            !string.IsNullOrWhiteSpace(snapshot.WebView2Version),
            snapshot.WebView2Version,
            "Evergreen",
            "https://developer.microsoft.com/microsoft-edge/webview2/"),
        new PrerequisiteStatus(
            PrerequisiteKind.RuntimePython,
            snapshot.RuntimePythonPresent,
            snapshot.RuntimePythonPresent ? "Bundled" : null,
            "Bundled",
            ProductRepairUri),
    ]);

    private static PrerequisiteStatus Status(
        PrerequisiteKind kind,
        string? installed,
        string minimum,
        string repairUri) =>
        new(kind, IsCompatible(installed, minimum), installed, minimum, repairUri);

    private static bool IsCompatible(string? installed, string minimum)
    {
        string normalized = installed?.Split(['-', '+'], 2)[0] ?? string.Empty;
        return Version.TryParse(normalized, out Version? actual) &&
            Version.TryParse(minimum, out Version? required) &&
            actual >= required;
    }
}

/// <summary>
/// Production readiness probe over the bundled payload: the self-contained
/// .NET runtime files, the self-contained Windows App SDK runtime DLL, the
/// bundled WebView2 Fixed Version runtime and the bound CPython tree.
/// </summary>
public static class BundledProductProbe
{
    public sealed record Snapshot(
        string? DotNetVersion,
        string? WindowsAppRuntimeVersion,
        string? WebView2Version,
        bool RuntimePythonPresent);

    public static Snapshot Capture(PortableLayout layout)
    {
        string appRoot = Path.GetDirectoryName(layout.AppEntry)
            ?? throw new InvalidOperationException("应用入口缺少目录。");
        return new Snapshot(
            FindBundledDotNetVersion(appRoot, layout.AppEntry),
            FindBundledWindowsAppRuntimeVersion(appRoot),
            // WebView2 一律探测系统 Evergreen（产品不随包分发运行库）。
            WindowsPrerequisiteProbe.FindWebView2Version(),
            File.Exists(layout.RuntimePython));
    }

    public static PrerequisiteReport Report(PortableLayout layout)
    {
        Snapshot snapshot = Capture(layout);
        return new PrerequisiteReport(
        [
            new PrerequisiteStatus(
                PrerequisiteKind.DotNetRuntime,
                snapshot.DotNetVersion is not null,
                snapshot.DotNetVersion,
                PrerequisiteDetector.BundledMinimum,
                PrerequisiteDetector.ProductRepairUri),
            new PrerequisiteStatus(
                PrerequisiteKind.WindowsAppRuntime,
                snapshot.WindowsAppRuntimeVersion is not null,
                snapshot.WindowsAppRuntimeVersion,
                PrerequisiteDetector.BundledMinimum,
                PrerequisiteDetector.ProductRepairUri),
            new PrerequisiteStatus(
                PrerequisiteKind.WebView2Runtime,
                !string.IsNullOrWhiteSpace(snapshot.WebView2Version),
                snapshot.WebView2Version,
                "Evergreen",
                "https://developer.microsoft.com/microsoft-edge/webview2/"),
            new PrerequisiteStatus(
                PrerequisiteKind.RuntimePython,
                snapshot.RuntimePythonPresent,
                snapshot.RuntimePythonPresent ? "Bundled" : null,
                PrerequisiteDetector.BundledMinimum,
                PrerequisiteDetector.ProductRepairUri),
        ]);
    }

    // 随包 Windows App SDK 启动闭包（ResolvedProductLayout.
    // MissingWindowsAppRuntimeFiles，与 Bootstrapper 自检一致）。
    private static string? FindBundledWindowsAppRuntimeVersion(string appRoot) =>
        ResolvedProductLayout.MissingWindowsAppRuntimeFiles(appRoot).Count == 0
            ? FindFileVersion(Path.Combine(appRoot, "Microsoft.WindowsAppRuntime.dll"))
            : null;

    // 随包 .NET 启动闭包 + 共享 runtimeconfig 语义（与 Bootstrapper 一致）。
    private static string? FindBundledDotNetVersion(string appRoot, string appEntry) =>
        BundledRuntimeConfig.MissingClrStartupFiles(appRoot).Count == 0
            ? BundledRuntimeConfig.FindNetCoreAppVersion(
                Path.ChangeExtension(appEntry, ".runtimeconfig.json"))
            : null;

    private static string? FindFileVersion(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        string version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        return string.IsNullOrWhiteSpace(version) ? "Bundled" : version;
    }
}

internal static class WindowsPrerequisiteProbe
{
    public static PrerequisiteSnapshot Capture(PortableLayout layout)
    {
        return new PrerequisiteSnapshot(
            DotNetRuntimeProbe.FindVersion(),
            FindWindowsAppRuntimeVersion(),
            FindWebView2Version(),
            File.Exists(layout.RuntimePython));
    }

    private static string? FindWindowsAppRuntimeVersion()
    {
        try
        {
            var manager = new PackageManager();
            // Multiple runtime packages (older majors, CBS bootstrappers) can
            // coexist; the highest compatible one decides readiness so an old
            // install cannot shadow the version the app actually needs.
            Package? package = manager.FindPackagesForUser(string.Empty)
                .Where(item => IsCompatibleWindowsAppRuntimePackage(
                    item.Id.Name,
                    item.Id.Version.Major,
                    item.Id.Version.Minor))
                .Where(item => item.Id.Architecture is ProcessorArchitecture.X64 or ProcessorArchitecture.Neutral)
                .Where(item => item.Status.VerifyIsOK())
                .OrderByDescending(item => item.Id.Version.Major)
                .ThenByDescending(item => item.Id.Version.Minor)
                .ThenByDescending(item => item.Id.Version.Build)
                .ThenByDescending(item => item.Id.Version.Revision)
                .FirstOrDefault();
            if (package is null)
            {
                return null;
            }

            PackageVersion version = package.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or TypeLoadException)
        {
            return null;
        }
    }

    private static readonly Version MinimumWindowsAppRuntimePackageBand =
        Version.Parse(PrerequisiteDetector.MinimumWindowsAppRuntimeVersion);

    internal static bool IsCompatibleWindowsAppRuntimePackage(
        string name,
        ushort major,
        ushort minor) =>
        (name.Equals("Microsoft.WindowsAppRuntime.2", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Microsoft.WindowsAppRuntime.CBS.2", StringComparison.OrdinalIgnoreCase)) &&
        (major > MinimumWindowsAppRuntimePackageBand.Major ||
            (major == MinimumWindowsAppRuntimePackageBand.Major &&
                minor >= MinimumWindowsAppRuntimePackageBand.Minor));

    internal static string? FindWebView2Version()
    {
        try
        {
            int result = GetAvailableCoreWebView2BrowserVersionString(null, out nint value);
            if (result < 0 || value == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(value);
            }
            finally
            {
                Marshal.FreeCoTaskMem(value);
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetAvailableCoreWebView2BrowserVersionString(
        string? browserExecutableFolder,
        out nint versionInfo);
}
