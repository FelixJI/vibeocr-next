namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// 显式选择的识别模式在当前运行环境不可用（模式目录缺失、模式不存在或未就绪）。
/// 提交必须以此失败并可恢复，并指向具体环境；不允许静默回退通用文字识别。
/// 仅未选择模式（Runtime 默认）或旧版引擎 id 的兼容路径可以默认管线提交。
/// </summary>
/// <remarks>#321 R3-1：目录携带的稳定 reason（如 product_runtime_damaged）
/// 经 <see cref="ReasonCode"/> 贯穿到工作台终态；无 reason 的本地拒绝保持
/// 原通用语义。</remarks>
public sealed class RecognitionModeUnavailableException(
    string message,
    bool retired = false,
    string? reasonCode = null)
    : InvalidOperationException(message)
{
    public bool IsRetired { get; } = retired;

    /// <summary>目录条目的稳定 reason；本地构造的拒绝为 null。</summary>
    public string? ReasonCode { get; } = reasonCode;
}
