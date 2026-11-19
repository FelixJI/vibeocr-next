using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.ProductLayout;

namespace VibeOCR.App.Web;

/// <summary>
/// 所有 WebView2 宿主在创建任何 CoreWebView2 之前先经过这里取得同一个
/// <see cref="CoreWebView2Environment"/>。
/// </summary>
/// <remarks>
/// 产品运行库为系统 Evergreen WebView2（不再随包分发 Fixed Version）：
/// 无随包目录时直接创建系统环境，外部显式 env 覆盖（测试/隔离）优先；
/// userData 默认 <see cref="PortableLayout.WebView2Root"/>（App 启动时
/// 注入 Loader 官方环境变量，外部已设则不动）。兼容保留：安装目录旁存在
/// <c>WebView2Runtime\msedgewebview2.exe</c>（旧候选/手动放置）时仍优先
/// 该目录。Evergreen 环境的可恢复初始化失败（COM/文件加载，典型为未安装
/// 或损坏的系统运行库）包装为固定的 <see cref="WebView2StartupException"/>
/// 安装引导提示；取消/OOM 等无关失败按原异常透传。
/// </remarks>
public static class WebView2ProductEnvironment
{
    public const string BundledFolderName = "WebView2Runtime";
    public const string BrowserExecutableName = "msedgewebview2.exe";

    private static readonly object Gate = new();
    private static PortableLayout? configuredLayout;
    private static Task<CoreWebView2Environment>? environmentTask;

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

    /// <summary>是否存在兼容随包 Fixed Version 运行库目录（旧候选/手动放置）。</summary>
    public static bool HasBundledRuntime => ResolveBundledRuntimeFolder(AppContext.BaseDirectory) is not null;

    /// <summary>惰性创建并缓存的 WebView2 环境（WinUI STA 线程）。</summary>
    public static Task<CoreWebView2Environment> EnsureCreatedAsync()
    {
        lock (Gate)
        {
            if (environmentTask is { } existing)
            {
                return existing;
            }
            // 创建在调用方（WinUI STA）线程同步进入，首个 await 后挂起；
            // 并发宿主复用同一任务，失败任务不重试（修复靠重启/安装）。
            Task<CoreWebView2Environment> creation = CreateAsync();
            environmentTask = creation;
            return creation;
        }
    }

    /// <summary>所有 WebView2 宿主（主窗/编辑器/Pin/Pin 编辑层/验收
    /// smoke）共用的初始化边界：先取得同一环境，再初始化 CoreWebView2。
    /// 可恢复的初始化失败（COM/文件加载，典型为系统 Evergreen 未安装或
    /// 损坏）包装为固定的 <see cref="WebView2StartupException"/> 安装引导
    /// 提示；取消/OOM 等无关失败按原异常透传。</summary>
    public static async Task InitializeCoreWebView2Async(WebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        CoreWebView2Environment environment = await EnsureCreatedAsync();
        try
        {
            await webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception error) when (WrapHostInitializationFailure(error) is { } guidance)
        {
            throw guidance;
        }
    }

    /// <summary>宿主初始化失败的窄包装判定：可恢复启动失败（COM/文件加载，
    /// 典型为系统 Evergreen WebView2 未安装/损坏）生成固定安装引导异常
    /// （不展示实现类型名，原始异常保留供日志）；取消/OOM 等无关失败返回
    /// null 按原异常透传。</summary>
    internal static WebView2StartupException? WrapHostInitializationFailure(
        Exception error) =>
        WebView2StartupException.IsRecoverableStartupFailure(error)
            ? new WebView2StartupException(
                "VibeOCR 依赖的系统 WebView2 (Evergreen) 运行库初始化失败。"
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

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        string? browserFolder = ResolveBundledRuntimeFolder(AppContext.BaseDirectory);
        if (browserFolder is null)
        {
            // 系统 Evergreen；userData 显式传参（代码决定，不依赖进程 env
            // 注入时序；外部显式 env 覆盖仍优先——见 ResolveDevUserDataFolder）。
            try
            {
                return await CoreWebView2Environment.CreateWithOptionsAsync(
                    null,
                    ResolveDevUserDataFolder(),
                    new CoreWebView2EnvironmentOptions());
            }
            catch (Exception error) when (WrapHostInitializationFailure(error) is { } guidance)
            {
                throw guidance;
            }
        }
        // 兼容随包目录（旧候选/手动放置）：显式传参创建。
        try
        {
            return await CoreWebView2Environment.CreateWithOptionsAsync(
                browserFolder,
                ResolveDevUserDataFolder(),
                new CoreWebView2EnvironmentOptions());
        }
        catch (Exception error) when (WrapHostInitializationFailure(error) is { } guidance)
        {
            throw guidance;
        }
    }

    /// <summary>userData：外部显式覆盖（smoke/隔离）优先，其次 Configure
    /// 的布局旧默认 <c>state/webview2</c>。</summary>
    private static string? ResolveDevUserDataFolder()
    {
        string? external =
            Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
        if (!string.IsNullOrWhiteSpace(external))
        {
            return external;
        }
        lock (Gate)
        {
            return configuredLayout is { } layout ? layout.WebView2Root : null;
        }
    }
}
