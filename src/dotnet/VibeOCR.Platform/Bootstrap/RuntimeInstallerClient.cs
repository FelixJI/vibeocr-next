using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

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
            Path.Combine(
                layout.InstallRoot,
                "runtime-installer",
                "vibeocr-runtime-installer.exe");
        string? selectedAccelerator = accelerator ??
            Environment.GetEnvironmentVariable("VIBEOCR_RUNTIME_ACCELERATOR");
        return new RuntimeInstallerConfiguration(
            installer,
            layout.InstallRoot,
            Path.Combine(layout.InstallRoot, "component-lock.json"),
            Path.Combine(layout.InstallRoot, "backend", "runtime-manifest.json"),
            selectedAccelerator,
            layout.PortableLayoutManifest,
            layout.PortableLayoutManifest is null ? null : "next");
    }
}

public sealed record RuntimeInspection(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("runtime_root")] string RuntimeRoot,
    [property: JsonPropertyName("accelerator")] string Accelerator,
    [property: JsonPropertyName("manifest_sha256")] string ManifestSha256,
    [property: JsonPropertyName("backend_version")] string BackendVersion,
    [property: JsonPropertyName("integrity")] string Integrity);

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

public interface IRuntimeInstallerClient
{
    Task<RuntimeInspection> InspectAsync(CancellationToken cancellationToken = default);
    Task<RuntimeLaunch> EnsureAsync(CancellationToken cancellationToken = default);
    Task<RuntimeLaunch> RepairAsync(CancellationToken cancellationToken = default);

    Task<RuntimeLaunch> EnsureAsync(
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken = default) =>
        EnsureAsync(cancellationToken);

    Task<RuntimeLaunch> RepairAsync(
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken = default) =>
        RepairAsync(cancellationToken);

    Host.RuntimeProfileDescriptor? ReadProfileDescriptor() => null;
}

public sealed record RuntimeHostError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable);

public sealed record RuntimeHostEnvelope(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("operation")] string? Operation,
    [property: JsonPropertyName("state")] RuntimeInspection? State,
    [property: JsonPropertyName("launch")] RuntimeLaunch? Launch,
    [property: JsonPropertyName("error")] RuntimeHostError? Error,
    [property: JsonPropertyName("profile")] Host.RuntimeProfileDescriptor? Profile = null,
    [property: JsonPropertyName("maintenance")] Host.RuntimeMaintenanceSnapshot? Maintenance = null);

public sealed class RuntimeInstallerException : InvalidOperationException
{
    public RuntimeInstallerException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thin process adapter around the Backend Runtime Installer JSON CLI.
/// </summary>
/// <remarks>
/// This client intentionally knows nothing about Python packages, indexes,
/// lock-file contents, runtime directories, or model directories.
/// </remarks>
public sealed class RuntimeInstallerClient : IRuntimeInstallerClient
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

    public Task<RuntimeInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        InvokeStateAsync(cancellationToken);

    public Task<RuntimeLaunch> EnsureAsync(CancellationToken cancellationToken = default) =>
        InvokeLaunchAsync("ensure", progress: null, cancellationToken);

    public Task<RuntimeLaunch> RepairAsync(CancellationToken cancellationToken = default) =>
        InvokeLaunchAsync("repair", progress: null, cancellationToken);

    public Task<RuntimeLaunch> EnsureAsync(
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken = default) =>
        InvokeLaunchAsync("ensure", progress, cancellationToken);

    public Task<RuntimeLaunch> RepairAsync(
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken = default) =>
        InvokeLaunchAsync("repair", progress, cancellationToken);

    public Host.RuntimeProfileDescriptor? ReadProfileDescriptor()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(_configuration.RuntimeManifest));
            JsonElement profiles = document.RootElement.GetProperty("profiles");
            string profileId = _configuration.Accelerator == "nvidia_cuda"
                ? profiles.EnumerateObject()
                    .Select(item => item.Name)
                    .First(name => name.Contains("cu", StringComparison.OrdinalIgnoreCase))
                : profiles.EnumerateObject()
                    .Select(item => item.Name)
                    .First(name => name.EndsWith("cpu", StringComparison.OrdinalIgnoreCase));
            JsonElement profile = profiles.GetProperty(profileId);
            Host.Accelerator accelerator = _configuration.Accelerator == "nvidia_cuda"
                ? Host.Accelerator.NvidiaCuda
                : Host.Accelerator.Cpu;
            Host.RuntimeComponentDescriptor[] components = profile
                .GetProperty("components")
                .EnumerateArray()
                .Select(component => new Host.RuntimeComponentDescriptor
                {
                    ComponentId = component.GetProperty("component_id").GetString()
                        ?? throw new JsonException("component_id must be a string."),
                    DisplayName = component.GetProperty("display_name").GetString()
                        ?? throw new JsonException("display_name must be a string."),
                    Version = component.TryGetProperty("version", out JsonElement version)
                        ? version.GetString()
                        : null,
                })
                .ToArray();
            return new Host.RuntimeProfileDescriptor
            {
                ProfileId = profileId,
                Accelerator = accelerator,
                Components = components,
            };
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<RuntimeInspection> InvokeStateAsync(
        CancellationToken cancellationToken)
    {
        RuntimeHostEnvelope envelope = await InvokeAsync(
            "inspect",
            progress: null,
            cancellationToken).ConfigureAwait(false);
        return envelope.State ?? throw new RuntimeInstallerException(
            "Runtime Host inspect response has no state.");
    }

    private async Task<RuntimeLaunch> InvokeLaunchAsync(
        string operation,
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken)
    {
        RuntimeHostEnvelope envelope = await InvokeAsync(
            operation,
            progress,
            cancellationToken).ConfigureAwait(false);
        RuntimeLaunch launch = envelope.Launch ?? throw new RuntimeInstallerException(
            $"Runtime Host {operation} response has no launch contract.");
        if (string.IsNullOrWhiteSpace(launch.PythonExecutable) ||
            string.IsNullOrWhiteSpace(launch.SupervisorModule) ||
            string.IsNullOrWhiteSpace(launch.WorkingDirectory) ||
            string.IsNullOrWhiteSpace(launch.ModelRoot) ||
            launch.Environment is null ||
            !Path.IsPathFullyQualified(launch.PythonExecutable) ||
            !Path.IsPathFullyQualified(launch.WorkingDirectory) ||
            !Path.IsPathFullyQualified(launch.ModelRoot))
        {
            throw new RuntimeInstallerException(
                $"Runtime Installer returned an invalid {operation} launch contract.");
        }
        return launch;
    }

    private async Task<RuntimeHostEnvelope> InvokeAsync(
        string operation,
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = BuildStartInfo(operation);
        int streamedEvents = 0;
        void HandleOutputLine(string line)
        {
            if (TryParseMaintenanceEvent(line, operation, out Host.RuntimeMaintenanceEvent? update)
                && update is not null)
            {
                Interlocked.Increment(ref streamedEvents);
                progress?.Report(update);
            }
        }
        RuntimeInstallerProcessResult result = await _runner.RunAsync(
            startInfo,
            HandleOutputLine,
            cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref streamedEvents) == 0)
        {
            foreach (string line in OutputLines(result.StandardOutput))
            {
                HandleOutputLine(line);
            }
        }
        string? envelopeJson = FinalEnvelopeJson(result.StandardOutput);
        if (result.ExitCode != 0)
        {
            string detail = ParseError(envelopeJson) ??
                result.StandardError.Trim();
            throw new RuntimeInstallerException(
                $"Runtime Installer {operation} failed with exit code {result.ExitCode}: {detail}");
        }

        try
        {
            RuntimeHostEnvelope? value = JsonSerializer.Deserialize<RuntimeHostEnvelope>(
                envelopeJson ?? string.Empty,
                JsonOptions);
            if (value is null || value.ProtocolVersion != 2 ||
                !string.Equals(value.Operation, operation, StringComparison.Ordinal) ||
                !value.Ok)
            {
                throw new RuntimeInstallerException(
                    value?.Error?.Message ?? $"Runtime Host {operation} returned an invalid envelope.");
            }
            return value;
        }
        catch (JsonException error)
        {
            throw new RuntimeInstallerException(
                $"Runtime Installer {operation} returned invalid JSON: {error.Message}");
        }
    }

    private ProcessStartInfo BuildStartInfo(
        string operation)
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
        var request = new Dictionary<string, object?>
        {
            ["protocol_version"] = 2,
            ["operation"] = operation,
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
        if (SupportsMaintenanceEvents())
        {
            request["accepted_event_streams"] = new[] { "ndjson.v1" };
        }
        AddOption(startInfo, "--request-json", JsonSerializer.Serialize(request));
        return startInfo;
    }

    private bool SupportsMaintenanceEvents()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(_configuration.RuntimeManifest));
            return document.RootElement
                .GetProperty("capabilities")
                .EnumerateArray()
                .Any(item => item.GetString() == "runtime.maintenance.v1");
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
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

    private static bool TryParseMaintenanceEvent(
        string line,
        string operation,
        out Host.RuntimeMaintenanceEvent? update)
    {
        update = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("event_version", out JsonElement eventVersion)
                || eventVersion.GetInt32() != 1)
            {
                return false;
            }
            Host.RuntimeMaintenanceEvent? value =
                JsonSerializer.Deserialize<Host.RuntimeMaintenanceEvent>(line, JsonOptions);
            if (value is null || value.ProtocolVersion != 2 ||
                !string.Equals(
                    JsonSerializer.Serialize(value.Operation, JsonOptions).Trim('"'),
                    operation,
                    StringComparison.Ordinal))
            {
                return false;
            }
            update = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> OutputLines(string standardOutput) =>
        standardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

public sealed class RuntimeInstallerCommandRunner : IRuntimeInstallerCommandRunner
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
        VerifyBoundExecutable(startInfo);
        using Process process = Process.Start(startInfo) ??
            throw new RuntimeInstallerException("Could not start Runtime Installer.");
        var stdoutBuffer = new StringBuilder();
        Task stdout = ReadStandardOutputAsync(
            process.StandardOutput,
            stdoutBuffer,
            standardOutputLine,
            cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        return new RuntimeInstallerProcessResult(
            process.ExitCode,
            stdoutBuffer.ToString(),
            await stderr.ConfigureAwait(false));
    }

    private static async Task ReadStandardOutputAsync(
        StreamReader reader,
        StringBuilder buffer,
        Action<string>? standardOutputLine,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            buffer.AppendLine(line);
            standardOutputLine?.Invoke(line);
        }
    }

    private static void VerifyBoundExecutable(ProcessStartInfo startInfo)
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
                    .GetProperty("backend")
                    .GetProperty("runtime_manifest_sha256"),
                "backend.runtime_manifest_sha256");
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
