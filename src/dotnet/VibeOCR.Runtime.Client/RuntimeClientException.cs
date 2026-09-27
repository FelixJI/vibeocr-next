using System.Text.Json;
using VibeOCR.Contracts.HttpV2;

namespace VibeOCR.Runtime.Client;

/// <summary>A typed error returned by the local Runtime API.</summary>
public class RuntimeClientException : Exception
{
    public RuntimeClientException(
        HttpV2ErrorCode code,
        string message,
        bool retryable,
        IDictionary<string, JsonElement>? detail = null)
        : this(code, message, retryable, detail, retryAfter: null)
    {
    }

    public RuntimeClientException(
        HttpV2ErrorCode code,
        string message,
        bool retryable,
        IDictionary<string, JsonElement>? detail,
        int? retryAfter)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
        RetryAfter = retryAfter;
        Detail = detail ?? new Dictionary<string, JsonElement>();
    }

    public HttpV2ErrorCode Code { get; }

    public bool Retryable { get; }

    public int? RetryAfter { get; }

    public IDictionary<string, JsonElement> Detail { get; }
}
