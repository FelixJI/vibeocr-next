// Typed exception for the v2 supervisor client. Mirrors the Python
// InferenceClientError: never carries a backend traceback, always a typed code.
using System.Text.Json;
using VibeOCR.Contracts.HttpV2;

namespace VibeOCR.Platform.Inference;

/// <summary>
/// Raised by <see cref="IInferenceClient"/> on any non-success response.
/// The UI maps <see cref="Code"/> to user-visible behaviour.
/// </summary>
public sealed class InferenceClientException : Exception
{
    /// <summary>
    /// #321：预构建随包 Runtime 的产品闭包损坏（Python 侧
    /// product_runtime_damaged）。UI 据此提示重新获取完整产品，不触发
    /// 在线准备，也不把它当成其它引擎的通用初始化失败。
    /// </summary>
    public const string ProductRuntimeDamagedReason = "product_runtime_damaged";

    public InferenceClientException(HttpV2ErrorCode code, string message, bool retryable, IDictionary<string, JsonElement>? detail = null)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
        Detail = detail ?? new Dictionary<string, JsonElement>();
    }

    /// <summary>Typed error code matching the v2 errors.json registry.</summary>
    public HttpV2ErrorCode Code { get; }

    /// <summary>Whether the UI may auto-retry once (transient codes).</summary>
    public bool Retryable { get; }

    /// <summary>Free-form typed detail from the server.</summary>
    public IDictionary<string, JsonElement> Detail { get; }

    /// <summary>服务端 problem detail 的稳定 reason_code；无则 null。</summary>
    public string? ReasonCode =>
        Detail.TryGetValue("reason_code", out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>随包 Runtime 产品闭包损坏（product_runtime_damaged）。</summary>
    public bool IsProductRuntimeDamaged =>
        string.Equals(
            ReasonCode,
            ProductRuntimeDamagedReason,
            StringComparison.Ordinal);
}
