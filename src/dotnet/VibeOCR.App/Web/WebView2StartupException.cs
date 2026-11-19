using System.Runtime.InteropServices;

namespace VibeOCR.App.Web;

/// <summary>
/// 系统 WebView2 (Evergreen) 运行库初始化失败（环境创建或 CoreWebView2
/// 初始化）的窄产品引导边界。只包装可恢复的启动失败（COM/文件加载类，
/// 典型为系统运行库未安装或损坏），原始异常保留为
/// <see cref="Exception.InnerException"/> 供日志；取消、OOM 等无关异常
/// 不包装，也不触发任何联网安装。
/// </summary>
public sealed class WebView2StartupException : InvalidOperationException
{
    internal const string RepairGuidance =
        "请从微软官网安装 WebView2 Evergreen 运行库后重试："
        + "https://developer.microsoft.com/microsoft-edge/webview2/";

    internal WebView2StartupException(string message, Exception? cause)
        : base(message, cause)
    {
    }

    /// <summary>是否为可恢复的 WebView2 启动失败（COM/文件加载类）；
    /// 取消、OOM 等无关异常返回 false，按原路径上抛。</summary>
    internal static bool IsRecoverableStartupFailure(Exception error) =>
        error is COMException
            or FileNotFoundException
            or DllNotFoundException
            or FileLoadException;
}
