using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>
/// Complete invocation binding for the Backend-owned Runtime Installer.
/// </summary>
public sealed record RuntimeInstallerConfiguration(
    string Executable,
    string ProductRoot,
    string ComponentLock,
    string RuntimeManifest,
    string? Accelerator,
    string? PortableLayoutManifest = null,
    string? ProductId = null)
{
    public static RuntimeInstallerConfiguration ForNext(
        PortableLayout layout,
        string? accelerator = null,
        string? executable = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string installer = executable ??
            Environment.GetEnvironmentVariable("VIBEOCR_RUNTIME_INSTALLER") ??
            layout.RuntimeInstaller;
        string? selectedAccelerator = accelerator ??
            Environment.GetEnvironmentVariable("VIBEOCR_RUNTIME_ACCELERATOR");
        return new RuntimeInstallerConfiguration(
            installer,
            layout.InstallRoot,
            layout.ComponentLock,
            layout.RuntimeManifest,
            selectedAccelerator,
            layout.PortableLayoutManifest,
            layout.PortableLayoutManifest is null ? null : "next");
    }
}

public sealed record RuntimeLaunch(
    [property: JsonPropertyName("python_executable")] string PythonExecutable,
    [property: JsonPropertyName("supervisor_module")] string SupervisorModule,
    [property: JsonPropertyName("working_directory")] string WorkingDirectory,
    [property: JsonPropertyName("model_root")] string ModelRoot,
    [property: JsonPropertyName("environment")]
    IReadOnlyDictionary<string, string> Environment);

public sealed record RuntimeInstallerProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Explicit install intent for ensure and retry maintenance operations.
/// Only stable component and download source ids are accepted here; source
/// endpoints and Python package names belong to the Backend.
/// </summary>
public interface IRuntimeInstallerCommandRunner
{
    Task<RuntimeInstallerProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken);

    Task<RuntimeInstallerProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        Action<string>? standardOutputLine,
        CancellationToken cancellationToken) =>
        RunAsync(startInfo, cancellationToken);
}

public sealed record RuntimeHostError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("canonical_code")] string? CanonicalCode = null,
    [property: JsonPropertyName("category")] string? Category = null,
    [property: JsonPropertyName("retry_after")] int? RetryAfter = null,
    [property: JsonPropertyName("detail")] IReadOnlyDictionary<string, JsonElement>? Detail = null);

/// <summary>Runtime Installer CLI 最终响应封套；错误分支之外的字段按调用方需要解析。</summary>
public sealed record RuntimeHostEnvelope(
    [property: JsonPropertyName("error")] RuntimeHostError? Error = null);

public sealed class RuntimeInstallerException : InvalidOperationException
{
    public RuntimeInstallerException(string message, RuntimeHostError? error = null)
        : base(message)
    {
        CanonicalCode = error?.CanonicalCode;
        Category = error?.Category;
        Retryable = error?.Retryable ?? false;
        RetryAfter = error?.RetryAfter;
        Detail = error?.Detail;
    }

    public string? CanonicalCode { get; }
    public string? Category { get; }
    public bool Retryable { get; }
    public int? RetryAfter { get; }
    public IReadOnlyDictionary<string, JsonElement>? Detail { get; }
}

/// <summary>
/// Thin process adapter around the Backend Runtime Installer JSON CLI.
/// </summary>
/// <remarks>
/// This client intentionally knows nothing about Python packages, indexes,
/// lock-file contents, runtime directories, or model directories.
/// </remarks>
public sealed partial class RuntimeInstallerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly RuntimeInstallerConfiguration _configuration;
    private readonly IRuntimeInstallerCommandRunner _runner;

    public RuntimeInstallerClient(RuntimeInstallerConfiguration configuration)
        : this(configuration, new RuntimeInstallerCommandRunner())
    {
    }

    public RuntimeInstallerClient(
        RuntimeInstallerConfiguration configuration,
        IRuntimeInstallerCommandRunner runner)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ValidateConfiguration(configuration);
    }

    private Dictionary<string, object?> BindingRequest()
    {
        var request = new Dictionary<string, object?>
        {
            ["protocol_version"] = 2,
            ["product_root"] = _configuration.ProductRoot,
            ["component_lock"] = _configuration.ComponentLock,
            ["runtime_manifest"] = _configuration.RuntimeManifest,
            ["accelerator"] = _configuration.Accelerator,
        };
        if (_configuration.PortableLayoutManifest is not null)
        {
            request["layout_manifest"] = _configuration.PortableLayoutManifest;
            request["product_id"] = _configuration.ProductId;
        }
        return request;
    }

    private ProcessStartInfo StartInfo(Dictionary<string, object?> request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _configuration.Executable,
            WorkingDirectory = _configuration.ProductRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        AddOption(startInfo, "--request-json", JsonSerializer.Serialize(request));
        return startInfo;
    }

    private static void AddOption(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string? ParseError(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(envelopeJson);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.TryGetProperty("message", out JsonElement message))
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static RuntimeHostError? ParseHostError(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson)) return null;
        try
        {
            RuntimeHostEnvelope? envelope = JsonSerializer.Deserialize<RuntimeHostEnvelope>(
                envelopeJson,
                JsonOptions);
            return envelope?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>提取最终协议封套：进度事件行（event_version）被逐行排除。</summary>
    private static string? FinalEnvelopeJson(string standardOutput)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput);
            if (!document.RootElement.TryGetProperty("event_version", out _))
            {
                return standardOutput;
            }
        }
        catch (JsonException)
        {
        }
        foreach (string line in OutputLines(standardOutput).Reverse())
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("event_version", out _))
                {
                    return line;
                }
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    private static IEnumerable<string> OutputLines(string standardOutput) =>
        standardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ValidateConfiguration(RuntimeInstallerConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ProductRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ComponentLock);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.RuntimeManifest);
        if (configuration.Accelerator is not null &&
            configuration.Accelerator is not ("cpu" or "nvidia_cuda"))
        {
            throw new ArgumentException("Accelerator must be cpu or nvidia_cuda.");
        }
        if ((configuration.PortableLayoutManifest is null) !=
            (configuration.ProductId is null))
        {
            throw new ArgumentException(
                "PortableLayoutManifest and ProductId must be supplied together.");
        }
    }
}

public sealed class RuntimeInstallerCommandRunner(Action<string>? commandStarting = null) : IRuntimeInstallerCommandRunner
{
    public async Task<RuntimeInstallerProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken) =>
        await RunAsync(startInfo, standardOutputLine: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<RuntimeInstallerProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        Action<string>? standardOutputLine,
        CancellationToken cancellationToken)
    {
        // Verification and CreateProcess are synchronous and must not block the UI caller.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyBoundExecutable(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        // Observe every real invocation, including short-lived read-only commands.
        // Only the validated action is exposed; request contents remain private.
        ObserveCommand(startInfo);
        return await RunProcessAsync(startInfo, standardOutputLine, cancellationToken)
            .ConfigureAwait(false);
    }

    internal void ObserveCommand(ProcessStartInfo startInfo)
    {
        if (commandStarting is null) return;
        using JsonDocument request = JsonDocument.Parse(RequireOption(startInfo, "--request-json"));
        JsonElement root = request.RootElement;
        string action = root.TryGetProperty("action", out JsonElement value) ? value.GetString() ?? "unknown"
            : root.TryGetProperty("command", out value) ? value.GetString() ?? "unknown"
            : root.TryGetProperty("request_kind", out value) ? value.GetString() ?? "unknown" : "unknown";
        commandStarting(action.Length <= 64 && action.All(character => character is >= 'a' and <= 'z' or '_')
            ? action : "unknown");
    }

    internal static async Task<RuntimeInstallerProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        Action<string>? standardOutputLine,
        CancellationToken cancellationToken,
        Action<Process>? processStarted = null)
    {
        using Process process = Process.Start(startInfo) ??
            throw new RuntimeInstallerException("Could not start Runtime Installer.");
        bool environmentCancellation = startInfo.ArgumentList.Contains("--environment-cancel-control");
        bool maintenanceCancellation = startInfo.ArgumentList.Contains("--maintenance-cancel-control");
        var receipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutBuffer = new StringBuilder();
        using var streamsLifetime = new CancellationTokenSource();
        Task stdout = ReadStandardOutputAsync(
            process.StandardOutput,
            stdoutBuffer,
            standardOutputLine,
            streamsLifetime.Token,
            environmentCancellation || maintenanceCancellation ? receipt : null);
        Task<string> stderr = ReadBoundedErrorAsync(process.StandardError, streamsLifetime.Token);
        Task exited = process.WaitForExitAsync();
        try
        {
            processStarted?.Invoke(process);
            try
            {
                await exited.WaitAsync(cancellationToken).ConfigureAwait(false);
                await stdout.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                bool safeToTerminate = true;
                if (environmentCancellation)
                {
                    try
                    {
                        safeToTerminate = await ConfirmEnvironmentCancellationAsync(process, exited, stdout, receipt)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
                    {
                        await StopProcessAsync(process).ConfigureAwait(false);
                        throw new RuntimeInstallerException($"运行环境取消未获确认：{error.Message}");
                    }
                }
                else if (maintenanceCancellation)
                {
                    // A timeout or failed control pipe carries no safe
                    // confirmation: never terminate a possibly committing run.
                    // Keep the process, drain its output, and await its real
                    // exit so the authoritative outcome survives.
                    safeToTerminate = await ConfirmMaintenanceStartupCancellationAsync(
                        process, exited, stdout, receipt).ConfigureAwait(false);
                }
                if (safeToTerminate)
                {
                    await StopProcessAsync(process).ConfigureAwait(false);
                    throw;
                }
                // A terminal commit won the race. Preserve its actual envelope,
                // including a natural failure, despite the caller's cancelled token.
            }
            // Exit must be observed before pipes and ExitCode: a child that
            // closes its output early while still running must not produce a
            // premature exit code.
            await exited.ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            return new RuntimeInstallerProcessResult(
                process.ExitCode,
                stdoutBuffer.ToString(),
                await stderr.ConfigureAwait(false));
        }
        finally
        {
            await streamsLifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (streamsLifetime.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<bool> ConfirmMaintenanceStartupCancellationAsync(
        Process process,
        Task exited,
        Task stdout,
        TaskCompletionSource<bool> safeToTerminate)
    {
        if (!process.HasExited)
        {
            try
            {
                await process.StandardInput.WriteLineAsync("cancel").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is IOException or
                ObjectDisposedException or
                InvalidOperationException)
            {
                // 控制通道不可用不丢弃可能在途的回执：继续同时观察回执与
                // 真实退出；未确认状态始终不授权终止。
            }
        }
        // 持续同时观察回执与真实退出（不设握手超时）：初始化/锁等待可能让
        // 回执晚于任意固定窗口，迟到有效 pre_registration 仍执行既有
        // bounded kill/reap；未确认/registered 始终不授权终止。
        Task first = await Task.WhenAny(safeToTerminate.Task, exited)
            .ConfigureAwait(false);
        if (first == safeToTerminate.Task && safeToTerminate.Task.IsCompletedSuccessfully)
        {
            // pre_registration → true（fence 已封死，可终止）；
            // registered → false（只能协作 durable 取消）。
            return safeToTerminate.Task.Result;
        }
        // A receipt can still be in flight after exit. The caller also awaits
        // stdout, so drain it before deciding instead of dropping a late receipt.
        await stdout.ConfigureAwait(false);
        return safeToTerminate.Task.IsCompletedSuccessfully && safeToTerminate.Task.Result;
    }

    private static async Task<bool> ConfirmEnvironmentCancellationAsync(
        Process process, Task exited, Task stdout, TaskCompletionSource<bool> receipt)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        if (!process.HasExited)
        {
            try
            {
                await process.StandardInput.WriteLineAsync("cancel").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException) when (process.HasExited)
            {
                // The final envelope still decides the completed operation.
            }
        }
        Task first = await Task.WhenAny(receipt.Task, exited).WaitAsync(timeout).ConfigureAwait(false);
        if (first == receipt.Task && await receipt.Task.ConfigureAwait(false)) return true;
        await exited.WaitAsync(timeout).ConfigureAwait(false);
        await stdout.WaitAsync(timeout).ConfigureAwait(false);
        return receipt.Task.IsCompletedSuccessfully && receipt.Task.Result;
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        catch (TimeoutException)
        {
            throw new RuntimeInstallerException("运行环境安装进程未能在取消后退出，取消状态尚未确认。");
        }
    }

    private static async Task ReadStandardOutputAsync(
        StreamReader reader,
        StringBuilder buffer,
        Action<string>? standardOutputLine,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? cancellationReceipt = null)
    {
        bool hasFinal = false;
        bool incompleteEnvelope = false;
        await foreach (string line in ReadBoundedLinesAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            if (cancellationReceipt is not null)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("environment_cancel", out JsonElement receipt) &&
                            receipt.ValueKind == JsonValueKind.String &&
                            receipt.GetString() is "accepted" or "rejected")
                        {
                            cancellationReceipt.TrySetResult(receipt.GetString() == "accepted");
                            continue;
                        }
                        if (root.TryGetProperty("maintenance_cancel", out JsonElement startup) &&
                            startup.ValueKind == JsonValueKind.String &&
                            startup.GetString() is "pre_registration" or "registered")
                        {
                            cancellationReceipt.TrySetResult(
                                startup.GetString() == "pre_registration");
                            continue;
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }
            if (line == "[output truncated: long line]")
            {
                buffer.Clear();
                hasFinal = false;
                incompleteEnvelope = true;
            }
            // Events are consumed live; only final envelopes belong in the result.
            bool isEvent = false;
            bool isFinal = false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                isEvent = document.RootElement.ValueKind == JsonValueKind.Object &&
                                document.RootElement.TryGetProperty("event_version", out _);
                isFinal = document.RootElement.ValueKind == JsonValueKind.Object &&
                                document.RootElement.TryGetProperty("protocol_version", out _);
            }
            catch (JsonException) { }
            if (!isEvent)
            {
                if (isFinal)
                {
                    buffer.Clear();
                    buffer.AppendLine(line);
                    hasFinal = true;
                    incompleteEnvelope = false;
                }
                else if (!hasFinal)
                {
                    if (buffer.Length > 16000) buffer.Clear();
                    buffer.AppendLine(line.Length > 4000 ? "[output truncated: long line]" : line);
                }
            }
            try { standardOutputLine?.Invoke(line); }
            catch (Exception) { /* Observers cannot prevent pipe draining or cancellation. */ }
        }
        if (incompleteEnvelope)
            throw new RuntimeInstallerException("Runtime Host 输出超出单行上限，最终响应未获完整确认。");
    }

    private static async IAsyncEnumerable<string> ReadBoundedLinesAsync(
                    StreamReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        const int maxLine = 1_048_576;
        char[] chunk = new char[4096];
        var line = new StringBuilder();
        bool dropping = false;
        int count;
        while ((count = await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            for (int index = 0; index < count; index++)
            {
                char value = chunk[index];
                if (value == '\n')
                {
                    yield return dropping ? "[output truncated: long line]" : line.ToString().TrimEnd('\r');
                    line.Clear();
                    dropping = false;
                }
                else if (!dropping)
                {
                    if (line.Length == maxLine) { dropping = true; line.Clear(); }
                    else line.Append(value);
                }
            }
        }
        if (dropping) yield return "[output truncated: long line]";
        else if (line.Length > 0) yield return line.ToString();
    }

    private static async Task<string> ReadBoundedErrorAsync(StreamReader reader, CancellationToken token)
    {
        var tail = new StringBuilder();
        await foreach (string line in ReadBoundedLinesAsync(reader, token).ConfigureAwait(false))
        {
            tail.AppendLine(line.Length > 4000 ? "[stderr truncated: long line]" : line);
            if (tail.Length > 16000) tail.Remove(0, tail.Length - 16000);
        }
        return tail.ToString();
    }

    internal static void VerifyBoundExecutable(ProcessStartInfo startInfo)
    {
        try
        {
            string requestJson = RequireOption(startInfo, "--request-json");
            using JsonDocument request = JsonDocument.Parse(requestJson);
            string componentLockPath = request.RootElement
                .GetProperty("component_lock").GetString() ??
                throw new InvalidDataException("component_lock is missing.");
            string runtimeManifestPath = request.RootElement
                .GetProperty("runtime_manifest").GetString() ??
                throw new InvalidDataException("runtime_manifest is missing.");

            byte[] componentLockBytes = File.ReadAllBytes(componentLockPath);
            byte[] runtimeManifestBytes = File.ReadAllBytes(runtimeManifestPath);
            using JsonDocument componentLock = JsonDocument.Parse(componentLockBytes);
            byte[] expectedManifestSha256 = ParseSha256(
                componentLock.RootElement
                    .GetProperty("product")
                    .GetProperty("runtime_manifest_sha256"),
                "product.runtime_manifest_sha256");
            byte[] actualManifestSha256 = SHA256.HashData(runtimeManifestBytes);
            if (!CryptographicOperations.FixedTimeEquals(
                actualManifestSha256,
                expectedManifestSha256))
            {
                throw new RuntimeInstallerException(
                    "Runtime manifest SHA-256 mismatch.");
            }

            using JsonDocument runtimeManifest = JsonDocument.Parse(
                runtimeManifestBytes);
            byte[] expectedExecutableSha256 = ParseSha256(
                runtimeManifest.RootElement
                .GetProperty("installer")
                .GetProperty("executable_sha256"),
                "installer.executable_sha256");
            using FileStream stream = File.OpenRead(startInfo.FileName);
            byte[] actualExecutableSha256 = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(
                actualExecutableSha256,
                expectedExecutableSha256))
            {
                throw new RuntimeInstallerException(
                    "Runtime Installer executable SHA-256 mismatch.");
            }
        }
        catch (RuntimeInstallerException)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            KeyNotFoundException or
            InvalidOperationException or
            ArgumentException)
        {
            throw new RuntimeInstallerException(
                $"Could not verify Runtime Installer trust chain: {error.Message}");
        }
    }

    private static string RequireOption(
        ProcessStartInfo startInfo,
        string option)
    {
        string? value = null;
        for (int index = 0; index < startInfo.ArgumentList.Count; index++)
        {
            if (!startInfo.ArgumentList[index].Equals(
                option,
                StringComparison.Ordinal))
            {
                continue;
            }
            if (value is not null)
            {
                throw new InvalidDataException(
                    $"{option} argument is duplicated.");
            }
            if (index + 1 >= startInfo.ArgumentList.Count)
            {
                throw new InvalidDataException(
                    $"{option} argument has no value.");
            }
            value = startInfo.ArgumentList[index + 1];
        }
        return value ??
            throw new InvalidDataException($"{option} argument is missing.");
    }

    private static byte[] ParseSha256(JsonElement value, string field)
    {
        string? digest = value.GetString();
        if (digest is null || digest.Length != 64)
        {
            throw new InvalidDataException(
                $"{field} must be a lowercase SHA-256 digest.");
        }
        foreach (char character in digest)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                throw new InvalidDataException(
                    $"{field} must be a lowercase SHA-256 digest.");
            }
        }
        return Convert.FromHexString(digest);
    }
}
