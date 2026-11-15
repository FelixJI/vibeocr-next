using Microsoft.Web.WebView2.Core;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.ProductLayout;

namespace VibeOCR.App.Web;

/// <summary>
/// #321 产品私有 WebView2 运行库绑定：所有 WebView2 宿主在创建任何
/// CoreWebView2 之前先经过这里取得同一个 <see cref="CoreWebView2Environment"/>。
/// </summary>
/// <remarks>
/// 发布模式（<c>VibeOCRProductWebView2=true</c> 编译，唯一来源为
/// build-release.ps1 的显式 MSBuild 属性）：运行库目录与版本由
/// <c>AppContext.BaseDirectory</c> 下随包 <c>WebView2Runtime\msedgewebview2.exe</c>
/// 决定且必须精确等于 lock 生成常量；完全忽略继承的
/// <c>WEBVIEW2_USER_DATA_FOLDER</c>/<c>WEBVIEW2_BROWSER_EXECUTABLE_FOLDER</c>，userData
/// 固定为 <see cref="PortableLayout.WebView2FixedRoot"/>（进程 env 与
/// CreateWithOptionsAsync 传同值以压制注册表重定向）；缺文件/版本失配/ACL/COM
/// 失败都明确报错，绝不回退系统运行库、绝不联网；首次使用不下载不安装。
/// 未打包 dev/test（默认构建）：无随包目录时允许系统 Evergreen，外部显式
/// env 覆盖（测试/隔离）优先；有随包目录的 dev 树仍优先随包目录。
/// </remarks>
public static class WebView2ProductEnvironment
{
    public const string BundledFolderName = "WebView2Runtime";
    public const string BrowserExecutableName = "msedgewebview2.exe";
    private const string LayoutDescriptorRelative = @"metadata\product-layout.json";

    private static readonly object Gate = new();
    private static PortableLayout? configuredLayout;
    private static Task<CoreWebView2Environment>? environmentTask;

    /// <summary>是否为显式发布模式构建（<c>VIBEOCR_PRODUCT_WEBVIEW2</c>）。
    /// 诊断与 Bootstrapper 语义以同一模式为准，不看 Profile。</summary>
    public static bool IsProductBuild
    {
        get
        {
#if VIBEOCR_PRODUCT_WEBVIEW2
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>App 启动早期（任何 WebView 创建前）记录便携布局。</summary>
    public static void Configure(PortableLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (Gate)
        {
            configuredLayout = layout;
        }
    }

    internal static void ResetForTest()
    {
        lock (Gate)
        {
            configuredLayout = null;
            environmentTask = null;
        }
    }

    /// <summary>#321：为 <see cref="PortableLayout.EnsurePortableState"/> 选定
    /// 生产 WebView2 用户数据目录。发布模式：随包目录/版本必须存在且与锁一致，
    /// 失败在状态准备之前抛出（不会先创建旧 UDF）；userData 固定为并列的
    /// <c>state/webview2-fixed/&lt;版本&gt;</c>，旧 <c>state/webview2</c> 不打开、
    /// 不迁移、不删除。dev/test：无随包目录返回 null 保持旧默认。</summary>
    public static string? PrepareProductionUserData(PortableLayout layout) =>
        PrepareProductionUserData(layout, AppContext.BaseDirectory);

    internal static string? PrepareProductionUserData(
        PortableLayout layout,
        string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(layout);
#if VIBEOCR_PRODUCT_WEBVIEW2
        var (_, version) = ResolveRequiredBundledRuntime(baseDirectory);
        return layout.WebView2FixedRoot(version);
#else
        return ResolveBundledRuntimeFolder(baseDirectory) is { } devFolder
            ? layout.WebView2FixedRoot(BundledRuntimeVersionOrThrow(devFolder))
            : null;
#endif
    }

    /// <summary>是否存在随包 Fixed Version 运行库目录。</summary>
    public static bool HasBundledRuntime => ResolveBundledRuntimeFolder(AppContext.BaseDirectory) is not null;

    /// <summary>惰性创建并缓存产品绑定的 WebView2 环境（WinUI STA 线程）。</summary>
    public static Task<CoreWebView2Environment> EnsureCreatedAsync()
    {
        lock (Gate)
        {
            if (environmentTask is { } existing)
            {
                return existing;
            }
            // 创建在调用方（WinUI STA）线程同步进入，首个 await 后挂起；
            // 并发宿主复用同一任务，失败任务不重试（修复靠重启/更新）。
            Task<CoreWebView2Environment> creation = CreateAsync();
            environmentTask = creation;
            return creation;
        }
    }

    internal static string? ResolveBundledRuntimeFolder(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string folder = Path.Combine(
            Path.GetFullPath(baseDirectory), BundledFolderName);
        try
        {
            return ResolvedProductLayout.HasSafeWebView2Executable(Path.Combine(folder, BrowserExecutableName))
                ? folder
                : null;
        }
        catch (InvalidDataException error)
        {
            throw new InvalidOperationException(error.Message, error);
        }
    }

    internal static bool IsPublishedProductLayout(string baseDirectory) =>
        File.Exists(Path.Combine(
            Path.GetFullPath(baseDirectory), LayoutDescriptorRelative));

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        string baseDirectory = AppContext.BaseDirectory;
#if VIBEOCR_PRODUCT_WEBVIEW2
        // 发布模式：与 PrepareProductionUserData 同一校验链，任何缺失/失配
        // 都在创建环境之前失败；不读取继承 env（F2）。
        var (folder, version) = ResolveRequiredBundledRuntime(baseDirectory);
        string userDataFolder = RequiredFixedUserDataFolder(version);
        ApplyAppContainerPermissions(folder);
        // 进程内覆盖与显式传参同值：压制继承 env 与注册表重定向（F2）。
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", folder);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
        return await CoreWebView2Environment.CreateWithOptionsAsync(
            folder,
            userDataFolder,
            new CoreWebView2EnvironmentOptions());
#else
        string? browserFolder = ResolveBundledRuntimeFolder(baseDirectory);
        if (browserFolder is null)
        {
            // 未打包 dev/test：允许系统 Evergreen；外部显式 env 覆盖仍生效。
            return await CoreWebView2Environment.CreateAsync();
        }
        ApplyAppContainerPermissions(browserFolder);
        string? userDataFolder = ResolveDevUserDataFolder(browserFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", browserFolder);
        if (userDataFolder is not null)
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
        }
        return await CoreWebView2Environment.CreateWithOptionsAsync(
            browserFolder,
            userDataFolder,
            new CoreWebView2EnvironmentOptions());
#endif
    }

    private static string RequiredFixedUserDataFolder(string version)
    {
        lock (Gate)
        {
            if (configuredLayout is not { } layout)
            {
                throw new InvalidOperationException(
                    "VibeOCR 便携布局尚未初始化，无法绑定 WebView2 用户数据目录。");
            }
            return layout.WebView2FixedRoot(version);
        }
    }

    /// <summary>dev/test 模式 userData：外部显式覆盖（smoke/隔离）优先，
    /// 其次 Configure 的布局（有随包目录的 dev 树用并列 Fixed 目录）。</summary>
    private static string? ResolveDevUserDataFolder(string browserFolder)
    {
        string? external =
            Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
        if (!string.IsNullOrWhiteSpace(external))
        {
            return external;
        }
        lock (Gate)
        {
            if (configuredLayout is not { } layout)
            {
                return null;
            }
            return layout.WebView2FixedRoot(BundledRuntimeVersionOrThrow(browserFolder));
        }
    }

    private static void ApplyAppContainerPermissions(string folder)
    {
        try
        {
            // shortcut: Fixed 156 needs these ACEs only on Windows 10; review when upgrading the runtime.
            WebView2RuntimePermissions.GrantAppContainerReadAndExecute(
                folder, requireAppContainerPermissions: !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
        }
        catch (WebView2RuntimePermissionException error)
        {
            throw new InvalidOperationException(
                "VibeOCR 随包 WebView2 运行库目录权限配置失败：" + error.Message, error);
        }
    }

#if VIBEOCR_PRODUCT_WEBVIEW2
    private static (string Folder, string Version) ResolveRequiredBundledRuntime(string baseDirectory)
    {
        string folder = ResolveBundledRuntimeFolder(baseDirectory)
            ?? throw new InvalidOperationException(
                "VibeOCR 随包 WebView2 运行库缺失或损坏（" +
                Path.Combine(Path.GetFullPath(baseDirectory), BundledFolderName) +
                "）。请通过应用内更新或重新获取完整安装包修复；应用不会回退使用系统运行库，也不会联网安装。");
        return (folder, BundledRuntimeVersionOrThrow(folder, WebView2LockVersion.LockedRuntimeVersion));
    }
#endif

    internal static string BundledRuntimeVersionOrThrow(string browserFolder, string? expectedVersion = null)
    {
        try
        {
            return ResolvedProductLayout.ReadWebView2ExecutableVersion(
                Path.Combine(browserFolder, BrowserExecutableName), expectedVersion);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidOperationException(error.Message, error);
        }
    }
}
