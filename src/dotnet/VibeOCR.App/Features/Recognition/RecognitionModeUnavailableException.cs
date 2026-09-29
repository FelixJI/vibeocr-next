namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// 显式选择的识别模式在当前运行环境不可用（模式目录缺失、模式不存在或未就绪）。
/// 提交必须以此失败并可恢复，并指向具体环境；不允许静默回退通用文字识别。
/// 仅未选择模式（Runtime 默认）或旧版引擎 id 的兼容路径可以默认管线提交。
/// </summary>
public sealed class RecognitionModeUnavailableException(string message)
    : InvalidOperationException(message);
