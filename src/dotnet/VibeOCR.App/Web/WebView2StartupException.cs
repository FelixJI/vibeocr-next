using System.Runtime.InteropServices;

namespace VibeOCR.App.Web;

/// <summary>
/// #321：随包 WebView2 运行库初始化失败（环境创建或 CoreWebView2 初始化）
/// 的窄产品报修边界。只包装随包运行库在位时可恢复的启动失败（COM/文件
/// 加载类），原始异常保留为 <see cref="Exception.InnerException"/> 供日志；
/// 取消、OOM 等无关异常不包装，也不触发任何系统运行库回退或联网重试。
/// </summary>
public sealed class WebView2StartupException : InvalidOperationException
{
    internal const string RepairGuidance =
        "请通过应用内更新或重新获取完整安装包修复；应用不会回退使用系统运行库，也不会联网安装。";

    internal WebView2StartupException(string message, Exception? cause)
        : base(message, cause)
    {
    }

    /// <summary>是否为可恢复的随包 WebView2 启动失败（COM/文件加载类）；
    /// 取消、OOM 等无关异常返回 false，按原路径上抛。</summary>
    internal static bool IsRecoverableStartupFailure(Exception error) =>
        error is COMException
            or FileNotFoundException
            or DllNotFoundException
            or FileLoadException;
}
