using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls;
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
    // 环境来源在创建时同步绑定（true=随包 Fixed；false=dev 系统 Evergreen），
    // 供宿主初始化失败包装判定；catch 时不再重新读取可能已变化的文件。
    private static bool environmentIsBundled;

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
            environmentIsBundled = false;
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

    /// <summary>#321：所有 WebView2 宿主（主窗/编辑器/Pin/Pin 编辑层/验收
    /// smoke）共用的初始化边界：先取得同一产品环境，再初始化 CoreWebView2。
    /// 只有环境实际绑定随包运行库时，可恢复的初始化失败（COM/文件加载）才
    /// 包装为固定的 <see cref="WebView2StartupException"/> 产品报修提示；dev
    /// 系统 Evergreen 环境的同类失败、取消/OOM 均按原异常透传。来源以环境
    /// 创建时绑定的结果为准，catch 时不重新读取运行库目录；发布模式环境
    /// 必须来自随包目录（缺失即 fail fast），不回退系统运行库、不联网重试。</summary>
    public static async Task InitializeCoreWebView2Async(WebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        CoreWebView2Environment environment = await EnsureCreatedAsync();
        // 来源在环境创建时同步绑定；catch 时不再读取文件系统。
        bool bundled = Volatile.Read(ref environmentIsBundled);
        try
        {
            await webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception error) when (WrapHostInitializationFailure(bundled, error) is { } repair)
        {
            throw repair;
        }
    }

    /// <summary>宿主初始化失败的窄包装判定：仅随包环境 + 可恢复启动失败才
    /// 生成固定产品报修异常（不展示实现类型名，原始异常保留供日志）；
    /// 系统 Evergreen 环境、取消/OOM 等无关失败返回 null 按原异常透传。</summary>
    internal static WebView2StartupException? WrapHostInitializationFailure(
        bool bundledEnvironment,
        Exception error) =>
        bundledEnvironment && WebView2StartupException.IsRecoverableStartupFailure(error)
            ? new WebView2StartupException(
                "VibeOCR 随包 WebView2 运行库初始化失败。"
                + WebView2StartupException.RepairGuidance,
                error)
            : null;

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
        environmentIsBundled = true;
        return await CreateBundledEnvironmentAsync(folder, userDataFolder);
#else
        string? browserFolder = ResolveBundledRuntimeFolder(baseDirectory);
        if (browserFolder is null)
        {
            // 未打包 dev/test：允许系统 Evergreen；外部显式 env 覆盖仍生效。
            // 系统 Evergreen 失败不属于随包缺损，不包装产品报修文案。
            environmentIsBundled = false;
            return await CoreWebView2Environment.CreateAsync();
        }
        ApplyAppContainerPermissions(browserFolder);
        string? userDataFolder = ResolveDevUserDataFolder(browserFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", browserFolder);
        if (userDataFolder is not null)
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
        }
        environmentIsBundled = true;
        return await CreateBundledEnvironmentAsync(browserFolder, userDataFolder);
#endif
    }

    /// <summary>随包运行库在位时的环境创建：可恢复启动失败（COM/文件加载）
    /// 包装为固定产品报修异常（不展示实现类型名），原始异常保留供日志。</summary>
    private static async Task<CoreWebView2Environment> CreateBundledEnvironmentAsync(
        string folder,
        string? userDataFolder)
    {
        try
        {
            return await CoreWebView2Environment.CreateWithOptionsAsync(
                folder,
                userDataFolder,
                new CoreWebView2EnvironmentOptions());
        }
        catch (Exception error) when (WrapHostInitializationFailure(bundledEnvironment: true, error) is { } repair)
        {
            throw repair;
        }
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
            ?? throw new WebView2StartupException(
                "VibeOCR 随包 WebView2 运行库缺失或损坏（"
                + Path.Combine(Path.GetFullPath(baseDirectory), BundledFolderName)
                + "）。" + WebView2StartupException.RepairGuidance,
                cause: null);
        var missingStartup = ResolvedProductLayout.MissingWebView2RuntimeStartupFiles(folder);
        if (missingStartup.Count > 0)
        {
            throw new WebView2StartupException(
                "VibeOCR 随包 WebView2 运行库缺失或损坏（缺少 "
                + string.Join("、", missingStartup)
                + "）。" + WebView2StartupException.RepairGuidance,
                cause: null);
        }
        // W17-2：在位但无效/截断/非 x64 的启动库同样在创建环境前报修
        //（App 直启时也在状态准备/WebView 创建前 fail closed）。
        try
        {
            foreach (string name in ResolvedProductLayout.WebView2RuntimeStartupFiles)
            {
                ResolvedProductLayout.EnsureValidX64DllImageHeader(Path.Combine(folder, name));
            }
        }
        catch (InvalidDataException error)
        {
            throw new WebView2StartupException(
                error.Message + "。" + WebView2StartupException.RepairGuidance,
                error);
        }
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
