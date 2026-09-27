using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Runtime.Contracts.Generated;

namespace VibeOCR.Runtime.Client;

/// <summary>
/// Protocol-owned HTTP transport. Product adapters supply generated operation
/// paths and remain responsible only for product-specific DTO projection.
/// </summary>
public sealed class RuntimeHttpClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseUrl;

    public RuntimeHttpClient(
        Uri baseUrl,
        string sessionToken,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        if (!IsSafeBaseUrl(baseUrl))
        {
            throw new ArgumentException(
                "Runtime client refuses non-loopback base URL.",
                nameof(baseUrl));
        }

        _baseUrl = baseUrl;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUrl;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", sessionToken);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseUrl => _http.BaseAddress!;

    public Task<HttpResponseMessage> GetAsync(
        string path,
        CancellationToken cancellationToken) =>
        _http.GetAsync(RequireRelativePath(path), cancellationToken);

    public Task<HttpResponseMessage> PostAsync(
        string path,
        HttpContent? content,
        CancellationToken cancellationToken) =>
        _http.PostAsync(RequireRelativePath(path), content, cancellationToken);

    public Task<HttpResponseMessage> PutAsync(
        string path,
        HttpContent? content,
        CancellationToken cancellationToken) =>
        _http.PutAsync(RequireRelativePath(path), content, cancellationToken);

    public StringContent CreateJsonContent<T>(
        T value,
        JsonSerializerOptions? options = null) =>
        new(
            JsonSerializer.Serialize(value, options),
            Encoding.UTF8,
            "application/json");

    public MultipartFormDataContent CreateMultipartContent(
        string manifestJson,
        IReadOnlyDictionary<string, RuntimeUpload> uploads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        ArgumentNullException.ThrowIfNull(uploads);

        var form = new MultipartFormDataContent();
        form.Add(
            new StringContent(manifestJson, Encoding.UTF8, "application/json"),
            "manifest");
        foreach ((string name, RuntimeUpload upload) in uploads)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                form.Dispose();
                throw new ArgumentException(
                    "Multipart field names must be non-empty.",
                    nameof(uploads));
            }

            var bytes = new ByteArrayContent(upload.Content.ToArray());
            bytes.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(upload.MediaType)
                    ? "application/octet-stream"
                    : upload.MediaType);
            form.Add(bytes, name, upload.FileName);
        }

        return form;
    }

    public async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(typeInfo);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        T? value = await JsonSerializer
            .DeserializeAsync(stream, typeInfo, cancellationToken)
            .ConfigureAwait(false);
        return value ?? throw new RuntimeClientException(
            HttpV2ErrorCode.AdapterProtocolViolation,
            "Runtime API returned an empty JSON response.",
            retryable: false);
    }

    public async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<byte[]> ReadBinaryAsync(
        HttpResponseMessage response,
        string expectedMediaType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMediaType);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string? actual = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(actual, expectedMediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeClientException(
                HttpV2ErrorCode.AdapterProtocolViolation,
                $"Runtime API returned media type '{actual ?? "<missing>"}'; "
                    + $"expected '{expectedMediaType}'.",
                retryable: false);
        }

        return await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<T> ReadNdjsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(typeInfo);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(
                mediaType,
                "application/x-ndjson",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeClientException(
                HttpV2ErrorCode.AdapterProtocolViolation,
                $"Runtime API returned media type '{mediaType ?? "<missing>"}'; "
                    + "expected 'application/x-ndjson'.",
                retryable: false);
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            T? value = JsonSerializer.Deserialize(line, typeInfo);
            yield return value ?? throw new RuntimeClientException(
                HttpV2ErrorCode.AdapterProtocolViolation,
                "Runtime API returned an empty NDJSON item.",
                retryable: false);
        }
    }

    public async Task<RuntimeMaintenanceReceipt> StartRuntimeMaintenanceAsync(
        RuntimeMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DownloadSourceIds is { Count: 0 })
        {
            throw new ArgumentException(
                "download_source_ids must be non-empty when provided.",
                nameof(request));
        }
        if (request.Operation != RuntimeMaintenanceOperation.Ensure
            && (request.InstallComponentIds is not null
                || request.DownloadSourceIds is not null))
        {
            throw new ArgumentException(
                "Install and download source selection require ensure.",
                nameof(request));
        }
        if (request.PlanId is not null)
        {
            if (request.Operation != RuntimeMaintenanceOperation.Ensure)
            {
                throw new ArgumentException(
                    "plan_id requires ensure.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.OperationId))
            {
                throw new ArgumentException(
                    "plan_id requires an explicit operation_id.", nameof(request));
            }
            if (request.ProfileId is not null
                || request.ComponentIds is not null
                || request.InstallComponentIds is not null
                || request.DownloadSourceIds is not null)
            {
                throw new ArgumentException(
                    "plan_id is mutually exclusive with profile_id, component_ids, "
                    + "install_component_ids and download_source_ids.",
                    nameof(request));
            }
        }
        if (request.PlanId is not null)
        {
            ValidateInstallPlanCapability(request.RequiredCapabilities);
            await RequireInstallPlanCapabilityAsync(cancellationToken).ConfigureAwait(false);
        }
        using StringContent content = new(
            HttpV2Json.Serialize(request), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await PostAsync(
            RuntimeOperationPaths.StartRuntimeMaintenance,
            content,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(
            response,
            HttpV2JsonContext.Default.RuntimeMaintenanceReceipt,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateInstallPlanCapability(IReadOnlyList<string>? requiredCapabilities)
    {
        if (requiredCapabilities is null || !requiredCapabilities.Contains("runtime.install-plan.v1"))
        {
            throw new ArgumentException("plan requests require runtime.install-plan.v1 capability.");
        }
    }

    private async Task RequireInstallPlanCapabilityAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await GetAsync(
            RuntimeOperationPaths.GetRuntimeHealth, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument health = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!health.RootElement.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind != JsonValueKind.Array
            || !capabilities.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String && value.GetString() == "runtime.install-plan.v1"))
        {
            throw new RuntimeClientException(HttpV2ErrorCode.RuntimeCapabilityUnavailable,
                "Runtime does not support runtime.install-plan.v1", retryable: false);
        }
    }

    /// <summary>
    /// Preview a read-only install plan (runtime.install-plan.v1). Installs
    /// nothing, downloads nothing, changes no active environment, and creates
    /// no maintenance operation.
    /// </summary>
    public async Task<RuntimeInstallPlanResponse> PreviewRuntimeInstallPlanAsync(
        RuntimeInstallPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DownloadSourceIds is { Count: 0 })
        {
            throw new ArgumentException(
                "download_source_ids must be non-empty when provided.",
                nameof(request));
        }
        ValidateInstallPlanCapability(request.RequiredCapabilities);
        await RequireInstallPlanCapabilityAsync(cancellationToken).ConfigureAwait(false);
        using StringContent content = new(
            HttpV2Json.Serialize(request), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await PostAsync(
            RuntimeOperationPaths.PreviewRuntimeInstallPlan,
            content,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(
            response,
            HttpV2JsonContext.Default.RuntimeInstallPlanResponse,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeMaintenanceReceipt> CommandRuntimeMaintenanceAsync(
        RuntimeMaintenanceCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Command == RuntimeMaintenanceCommandKind.Retry
            && string.IsNullOrWhiteSpace(command.NewOperationId))
        {
            throw new ArgumentException("Retry requires new_operation_id.", nameof(command));
        }
        if (command.DownloadSourceIds is { Count: 0 })
        {
            throw new ArgumentException(
                "download_source_ids must be non-empty when provided.",
                nameof(command));
        }
        if (command.Command != RuntimeMaintenanceCommandKind.Retry
            && (command.InstallComponentIds is not null
                || command.DownloadSourceIds is not null))
        {
            throw new ArgumentException(
                "Install and download source selection require retry.",
                nameof(command));
        }
        if (command.PlanId is not null)
        {
            if (command.Command != RuntimeMaintenanceCommandKind.Retry)
            {
                throw new ArgumentException(
                    "plan_id requires retry.", nameof(command));
            }
            if (command.InstallComponentIds is not null
                || command.DownloadSourceIds is not null)
            {
                throw new ArgumentException(
                    "plan_id is mutually exclusive with install_component_ids "
                    + "and download_source_ids.",
                    nameof(command));
            }
        }
        if (command.PlanId is not null)
        {
            ValidateInstallPlanCapability(command.RequiredCapabilities);
            await RequireInstallPlanCapabilityAsync(cancellationToken).ConfigureAwait(false);
        }
        using StringContent content = new(
            HttpV2Json.Serialize(command), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await PostAsync(
            RuntimeOperationPaths.CommandRuntimeMaintenance,
            content,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(
            response,
            HttpV2JsonContext.Default.RuntimeMaintenanceReceipt,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeMaintenanceUpdate> ObserveRuntimeMaintenanceAsync(
        string operationId,
        int afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (afterSequence < 0 || limit is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }
        string path = BindOperationPath(
            RuntimeOperationPaths.ObserveRuntimeMaintenance,
            operationId)
            + $"?after_sequence={afterSequence}&limit={limit}";
        using HttpResponseMessage response = await GetAsync(path, cancellationToken)
            .ConfigureAwait(false);
        RuntimeMaintenanceUpdate update = await ReadJsonAsync(
            response,
            HttpV2JsonContext.Default.RuntimeMaintenanceUpdate,
            cancellationToken).ConfigureAwait(false);
        ValidateMaintenanceCursor(update, operationId, afterSequence);
        return update;
    }

    private static void ValidateMaintenanceCursor(
        RuntimeMaintenanceUpdate update,
        string operationId,
        int afterSequence)
    {
        bool invalid = update.OperationId != operationId
            || update.Snapshot.OperationId != operationId
            || update.OldestSequence < 1
            || update.ThroughSequence < 0
            || update.Snapshot.Sequence < update.ThroughSequence
            || update.OldestSequence > update.Snapshot.Sequence;
        if (update.Events.Count == 0)
        {
            invalid |= update.ThroughSequence > afterSequence || update.More;
        }
        else
        {
            invalid |= update.Events[0].Sequence != afterSequence + 1
                || update.Events[^1].Sequence != update.ThroughSequence;
            for (int index = 0; index < update.Events.Count; index++)
            {
                RuntimeMaintenanceEvent current = update.Events[index];
                invalid |= current.Sequence != current.Snapshot.Sequence
                    || current.Snapshot.OperationId != operationId;
                if (index > 0)
                {
                    invalid |= current.Sequence != update.Events[index - 1].Sequence + 1;
                }
            }
        }
        if (invalid)
        {
            throw new RuntimeClientException(
                HttpV2ErrorCode.AdapterProtocolViolation,
                "Runtime maintenance cursor response is invalid",
                retryable: false);
        }
    }

    public Task<RuntimeMaintenanceReceipt> InspectRuntimeAsync(
        string? operationId,
        CancellationToken cancellationToken) =>
        StartRuntimeMaintenanceAsync(
            new RuntimeMaintenanceRequest
            {
                OperationId = operationId,
                Operation = RuntimeMaintenanceOperation.Inspect,
            },
            cancellationToken);

    public Task<RuntimeMaintenanceReceipt> EnsureRuntimeAsync(
        string? operationId,
        CancellationToken cancellationToken) =>
        EnsureRuntimeAsync(operationId, null, null, cancellationToken);

    public Task<RuntimeMaintenanceReceipt> EnsureRuntimeAsync(
        string? operationId,
        IReadOnlyList<string>? installComponentIds,
        IReadOnlyList<string>? downloadSourceIds,
        CancellationToken cancellationToken) =>
        StartRuntimeMaintenanceAsync(
            new RuntimeMaintenanceRequest
            {
                OperationId = operationId,
                Operation = RuntimeMaintenanceOperation.Ensure,
                InstallComponentIds = installComponentIds,
                DownloadSourceIds = downloadSourceIds,
            },
            cancellationToken);

    public Task<RuntimeMaintenanceReceipt> RepairRuntimeAsync(
        string? operationId,
        IReadOnlyList<string>? componentIds,
        CancellationToken cancellationToken) =>
        StartRuntimeMaintenanceAsync(
            new RuntimeMaintenanceRequest
            {
                OperationId = operationId,
                Operation = RuntimeMaintenanceOperation.Repair,
                ComponentIds = componentIds,
            },
            cancellationToken);

    public Task<RuntimeMaintenanceReceipt> CancelRuntimeAsync(
        string operationId,
        string commandId,
        int? expectedSequence,
        CancellationToken cancellationToken) =>
        CommandRuntimeMaintenanceAsync(
            new RuntimeMaintenanceCommand
            {
                CommandId = commandId,
                Command = RuntimeMaintenanceCommandKind.Cancel,
                TargetOperationId = operationId,
                ExpectedSequence = expectedSequence,
            },
            cancellationToken);

    public Task<RuntimeMaintenanceReceipt> RetryRuntimeAsync(
        string operationId,
        string commandId,
        string newOperationId,
        int? expectedSequence,
        CancellationToken cancellationToken) =>
        RetryRuntimeAsync(
            operationId,
            commandId,
            newOperationId,
            expectedSequence,
            null,
            null,
            cancellationToken);

    public Task<RuntimeMaintenanceReceipt> RetryRuntimeAsync(
        string operationId,
        string commandId,
        string newOperationId,
        int? expectedSequence,
        IReadOnlyList<string>? installComponentIds,
        IReadOnlyList<string>? downloadSourceIds,
        CancellationToken cancellationToken) =>
        CommandRuntimeMaintenanceAsync(
            new RuntimeMaintenanceCommand
            {
                CommandId = commandId,
                Command = RuntimeMaintenanceCommandKind.Retry,
                TargetOperationId = operationId,
                NewOperationId = newOperationId,
                ExpectedSequence = expectedSequence,
                InstallComponentIds = installComponentIds,
                DownloadSourceIds = downloadSourceIds,
            },
            cancellationToken);

    public async IAsyncEnumerable<RuntimeMaintenanceEvent> StreamRuntimeMaintenanceAsync(
        string operationId,
        int afterSequence,
        string mediaType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }
        if (mediaType is not "application/x-ndjson" and not "text/event-stream")
        {
            throw new ArgumentException("Unsupported Runtime event media type.", nameof(mediaType));
        }
        string path = BindOperationPath(
            RuntimeOperationPaths.StreamRuntimeMaintenanceEvents,
            operationId) + $"?after_sequence={afterSequence}";
        using var request = new HttpRequestMessage(HttpMethod.Get, RequireRelativePath(path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        using HttpResponseMessage response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string? actual = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(actual, mediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeClientException(
                HttpV2ErrorCode.AdapterProtocolViolation,
                $"Runtime API returned media type '{actual ?? "<missing>"}'; expected '{mediaType}'.",
                retryable: false);
        }
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)
                || (mediaType == "text/event-stream" && !line.StartsWith("data:", StringComparison.Ordinal)))
            {
                continue;
            }
            string json = mediaType == "text/event-stream" ? line[5..].TrimStart() : line;
            RuntimeMaintenanceEvent? value = HttpV2Json.Deserialize<RuntimeMaintenanceEvent>(json);
            yield return value ?? throw new RuntimeClientException(
                HttpV2ErrorCode.AdapterProtocolViolation,
                "Runtime API returned an empty event stream item.",
                retryable: false);
        }
    }

    public async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        try
        {
            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            HttpV2ErrorPayload? payload = HttpV2Json.Deserialize<HttpV2ErrorPayload>(body);
            if (payload is not null)
            {
                throw new RuntimeClientException(
                    payload.Code,
                    payload.Message,
                    payload.Retryable,
                    payload.Detail,
                    payload.RetryAfter);
            }
        }
        catch (RuntimeClientException)
        {
            throw;
        }
        catch
        {
            // Preserve a stable typed boundary for malformed server responses.
        }

        throw new RuntimeClientException(
            HttpV2ErrorCode.InternalError,
            $"Unexpected HTTP {(int)response.StatusCode} from Runtime API.",
            retryable: true);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string BindOperationPath(string template, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return template.Replace(
            "{operation_id}",
            Uri.EscapeDataString(operationId),
            StringComparison.Ordinal);
    }

    private Uri RequireRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\')
            || path.Any(char.IsControl)
            || path.Contains('{')
            || path.Contains('}'))
        {
            throw new ArgumentException(
                "Runtime path must be a bound root-relative operation path.",
                nameof(path));
        }

        if (!Uri.TryCreate(_baseUrl, path, out Uri? resolved)
            || !IsLoopback(resolved)
            || !string.Equals(
                resolved.Scheme,
                _baseUrl.Scheme,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                resolved.Host,
                _baseUrl.Host,
                StringComparison.OrdinalIgnoreCase)
            || resolved.Port != _baseUrl.Port)
        {
            throw new ArgumentException(
                "Runtime path escaped the configured loopback authority.",
                nameof(path));
        }

        return resolved;
    }

    private static bool IsSafeBaseUrl(Uri uri) =>
        uri.IsAbsoluteUri
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsolutePath == "/"
        && IsLoopback(uri);

    private static bool IsLoopback(Uri uri) =>
        uri.Scheme is "http" or "https"
        && uri.IsLoopback;
}

public sealed record RuntimeUpload(
    string FileName,
    ReadOnlyMemory<byte> Content,
    string MediaType = "application/octet-stream");
