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

public sealed record RuntimeInspection(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("runtime_root")] string RuntimeRoot,
    [property: JsonPropertyName("accelerator")] string Accelerator,
    [property: JsonPropertyName("manifest_sha256")] string ManifestSha256,
    [property: JsonPropertyName("backend_version")] string BackendVersion,
    [property: JsonPropertyName("integrity")] string Integrity,
    [property: JsonPropertyName("source")] RuntimeSourceIdentity? Source = null);

public sealed record RuntimeSourceIdentity(
    [property: JsonPropertyName("backend_version")] string BackendVersion,
    [property: JsonPropertyName("backend_source_sha")] string BackendSourceSha,
    [property: JsonPropertyName("runtime_manifest_sha256")] string RuntimeManifestSha256,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("protocol_manifest_sha256")] string ProtocolManifestSha256);

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
public sealed record RuntimeInstallSelection
{
    private IReadOnlyList<string>? _installComponentIds;
    private IReadOnlyList<string>? _downloadSourceIds;

    /// <summary>
    /// Manual install scope as stable component ids. Null omits the wire
    /// field (the Backend applies its default set, retry reuses the source
    /// operation's normalized intent); an empty list explicitly selects the
    /// base-only scope.
    /// </summary>
    public IReadOnlyList<string>? InstallComponentIds
    {
        get => _installComponentIds;
        init
        {
            ValidateIds(value, allowEmpty: true, nameof(InstallComponentIds));
            _installComponentIds = value;
        }
    }

    /// <summary>
    /// Download source ids snapshotted into the operation. Null delegates to
    /// the Backend settings/defaults (retry reuses the source operation's
    /// intent); when present the wire format requires a non-empty array with
    /// at most one id per source kind.
    /// </summary>
    public IReadOnlyList<string>? DownloadSourceIds
    {
        get => _downloadSourceIds;
        init
        {
            ValidateIds(value, allowEmpty: false, nameof(DownloadSourceIds));
            _downloadSourceIds = value;
        }
    }

    private static void ValidateIds(
        IReadOnlyList<string>? ids,
        bool allowEmpty,
        string name)
    {
        if (ids is null)
        {
            return;
        }
        if (!allowEmpty && ids.Count == 0)
        {
            throw new ArgumentException(
                $"{name} must not be empty; omit the selection to use Backend defaults.",
                name);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                throw new ArgumentException(
                    $"{name} must contain unique non-blank ids.", name);
            }
        }
    }
}

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
    IReadOnlyList<string> NegotiatedCapabilities => Array.Empty<string>();
    IReadOnlyList<RuntimeCapabilityDescriptor> CapabilityDescriptors =>
        Array.Empty<RuntimeCapabilityDescriptor>();

    Task<RuntimeInspection> InspectAsync(CancellationToken cancellationToken = default);
    Task<RuntimeLaunch> EnsureAsync(CancellationToken cancellationToken = default);
    Task<RuntimeLaunch> RepairAsync(CancellationToken cancellationToken = default);
    Task<RuntimeLaunch> EnsureAsync(
        string operationId,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Ensure with an explicit install intent. <see cref="RuntimeInstallSelection"/>
    /// carries <c>install_component_ids</c>/<c>download_source_ids</c>; repair
    /// keeps using <see cref="RepairComponentsAsync"/> with
    /// <c>component_ids</c> and the two field families never mix.
    /// </summary>
    Task<RuntimeLaunch> EnsureAsync(
        RuntimeInstallSelection? selection,
        string operationId,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default);
    Task<RuntimeLaunch> RepairAsync(
        string operationId,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default);
    Task<RuntimeHostEnvelope> CancelAsync(
        string operationId,
        string commandId,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default);
    Task<RuntimeHostEnvelope> CancelAsync(
        string operationId,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default) =>
        CancelAsync(
            operationId,
            Guid.NewGuid().ToString(),
            expectedSequence,
            cancellationToken);
    Task<RuntimeHostEnvelope> RetryAsync(
        string operationId,
        string newOperationId,
        string commandId,
        CancellationToken cancellationToken = default) =>
        RetryAsync(
            operationId,
            newOperationId,
            selection: null,
            commandId,
            cancellationToken);
    Task<RuntimeHostEnvelope> RetryAsync(
        string operationId,
        string newOperationId,
        CancellationToken cancellationToken = default) =>
        RetryAsync(
            operationId,
            newOperationId,
            selection: null,
            cancellationToken);
    /// <summary>
    /// Retry with an explicit replacement intent: a null
    /// <see cref="RuntimeInstallSelection"/> (or null members) reuses the
    /// source operation's normalized intent, explicit values replace it.
    /// </summary>
    Task<RuntimeHostEnvelope> RetryAsync(
        string operationId,
        string newOperationId,
        RuntimeInstallSelection? selection,
        CancellationToken cancellationToken = default) =>
        RetryAsync(
            operationId,
            newOperationId,
            selection,
            Guid.NewGuid().ToString(),
            cancellationToken);
    Task<RuntimeHostEnvelope> RetryAsync(
        string operationId,
        string newOperationId,
        RuntimeInstallSelection? selection,
        string commandId,
        CancellationToken cancellationToken = default);
    Task<RuntimeMaintenanceObserveEnvelope> ObserveAsync(
        string operationId,
        long afterSequence,
        int limit = 128,
        CancellationToken cancellationToken = default);

    Task<RuntimeLaunch> EnsureAsync(
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken = default) =>
        EnsureAsync(cancellationToken);

    Task<RuntimeLaunch> RepairAsync(
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken = default) =>
        RepairAsync(cancellationToken);

    Host.RuntimeProfileDescriptor? ReadProfileDescriptor() => null;

    RuntimeMaintenanceSourceSnapshot? LastMaintenanceSources => null;
}

/// <summary>
/// UI-neutral source projection retained by the external-process adapter.
/// Protocol 2.7.1's Host generated snapshot omits these emitted wire fields;
/// App code consumes this model and never parses JSON itself.
/// </summary>
public sealed record RuntimeMaintenanceSourceSnapshot(
    IReadOnlyList<string> RequestedSourceIds,
    IReadOnlyList<string> EffectiveSourceIds);

public sealed record RuntimeHostError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("canonical_code")] string? CanonicalCode = null,
    [property: JsonPropertyName("category")] string? Category = null,
    [property: JsonPropertyName("retry_after")] int? RetryAfter = null,
    [property: JsonPropertyName("detail")] IReadOnlyDictionary<string, JsonElement>? Detail = null);

public sealed record RuntimeCapabilityDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("lifecycle")] string Lifecycle,
    [property: JsonPropertyName("introduced_in")] string IntroducedIn,
    [property: JsonPropertyName("deprecated_in")] string? DeprecatedIn,
    [property: JsonPropertyName("sunset_at")] string? SunsetAt,
    [property: JsonPropertyName("replacement")] string? Replacement);

public sealed record RuntimeHostEnvelope(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("operation")] string? Operation,
    [property: JsonPropertyName("state")] RuntimeInspection? State,
    [property: JsonPropertyName("launch")] RuntimeLaunch? Launch,
    [property: JsonPropertyName("error")] RuntimeHostError? Error,
    [property: JsonPropertyName("profile")] Host.RuntimeProfileDescriptor? Profile = null,
    [property: JsonPropertyName("maintenance")] Host.RuntimeMaintenanceSnapshot? Maintenance = null,
    [property: JsonPropertyName("negotiated_capabilities")] string[]? NegotiatedCapabilities = null,
    [property: JsonPropertyName("capability_descriptors")] RuntimeCapabilityDescriptor[]? CapabilityDescriptors = null,
    RuntimeMaintenanceSourceSnapshot? MaintenanceSources = null);

public sealed record RuntimeMaintenanceObserveEnvelope(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("request_kind")] string RequestKind,
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("snapshot")] Host.RuntimeMaintenanceSnapshot Snapshot,
    [property: JsonPropertyName("events")] Host.RuntimeMaintenanceEvent[] Events,
    [property: JsonPropertyName("oldest_sequence")] long OldestSequence,
    [property: JsonPropertyName("through_sequence")] long ThroughSequence,
    [property: JsonPropertyName("more")] bool More,
    [property: JsonPropertyName("replay_expires_at")] string? ReplayExpiresAt);

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
public sealed class RuntimeInstallerClient : IRuntimeInstallerClient
{
    private RuntimeMaintenanceSourceSnapshot? _lastMaintenanceSources;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly RuntimeInstallerConfiguration _configuration;
    private readonly IRuntimeInstallerCommandRunner _runner;
    public string? LastOperationId { get; private set; }
    public IReadOnlyList<string> NegotiatedCapabilities { get; private set; } =
        Array.Empty<string>();
    public IReadOnlyList<RuntimeCapabilityDescriptor> CapabilityDescriptors { get; private set; } =
        Array.Empty<RuntimeCapabilityDescriptor>();
    public RuntimeMaintenanceSourceSnapshot? LastMaintenanceSources => _lastMaintenanceSources;

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
        string operationId,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default) =>
        InvokeLaunchAsync(
            "ensure",
            progress,
            cancellationToken,
            operationId: operationId);

    public Task<RuntimeLaunch> EnsureAsync(
        RuntimeInstallSelection? selection,
        string operationId,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        return InvokeLaunchAsync(
            "ensure",
            progress,
            cancellationToken,
            installComponentIds: selection?.InstallComponentIds,
            downloadSourceIds: selection?.DownloadSourceIds,
            operationId: operationId);
    }

    public Task<RuntimeLaunch> RepairAsync(
        string operationId,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default) =>
        InvokeLaunchAsync(
            "repair",
            progress,
            cancellationToken,
            operationId: operationId);

    public Task<RuntimeLaunch> EnsureAsync(
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken = default) =>
        InvokeLaunchAsync("ensure", progress, cancellationToken);

    public Task<RuntimeLaunch> RepairAsync(
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken = default) =>
        InvokeLaunchAsync("repair", progress, cancellationToken);

    public Task<RuntimeLaunch> RepairComponentsAsync(
        IReadOnlyCollection<string> componentIds,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default) =>
        InvokeRepairComponentsAsync(
            operationId: null,
            componentIds,
            progress,
            cancellationToken);

    public Task<RuntimeLaunch> RepairComponentsAsync(
        string operationId,
        IReadOnlyCollection<string> componentIds,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default) =>
        InvokeRepairComponentsAsync(
            operationId,
            componentIds,
            progress,
            cancellationToken);

    private Task<RuntimeLaunch> InvokeRepairComponentsAsync(
        string? operationId,
        IReadOnlyCollection<string> componentIds,
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken) =>
        InvokeLaunchAsync(
            "repair",
            progress,
            cancellationToken,
            componentIds: componentIds,
            requiredCapabilities:
            [
                "runtime.maintenance.v2",
                "runtime.component-repair.v1",
            ],
            operationId: operationId);

    public Task<RuntimeHostEnvelope> CancelAsync(
        string operationId,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default) =>
        CancelAsync(
            operationId,
            Guid.NewGuid().ToString(),
            expectedSequence,
            cancellationToken);

    public Task<RuntimeHostEnvelope> CancelAsync(
        string operationId,
        string commandId,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        var request = BindingRequest();
        request["request_kind"] = "command";
        request["command_id"] = commandId;
        request["command"] = "cancel";
        request["target_operation_id"] = operationId;
        if (expectedSequence is not null) request["expected_sequence"] = expectedSequence.Value;
        return InvokeControlAsync<RuntimeHostEnvelope>(
            request,
            cancellationToken,
            expectedOperationId: operationId);
    }

    public Task<RuntimeHostEnvelope> RetryAsync(
        string operationId,
        string newOperationId,
        CancellationToken cancellationToken = default) =>
        RetryAsync(
            operationId,
            newOperationId,
            selection: null,
            cancellationToken);

    public Task<RuntimeHostEnvelope> RetryAsync(
        string operationId,
        string newOperationId,
        string commandId,
        CancellationToken cancellationToken = default) =>
        RetryAsync(
            operationId,
            newOperationId,
            selection: null,
            commandId,
            cancellationToken);

    public Task<RuntimeHostEnvelope> RetryAsync(
        string operationId,
        string newOperationId,
        RuntimeInstallSelection? selection,
        CancellationToken cancellationToken = default) =>
        RetryAsync(
            operationId,
            newOperationId,
            selection,
            Guid.NewGuid().ToString(),
            cancellationToken);

    public Task<RuntimeHostEnvelope> RetryAsync(
        string operationId,
        string newOperationId,
        RuntimeInstallSelection? selection,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newOperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        _lastMaintenanceSources = null;
        var request = BindingRequest();
        request["request_kind"] = "command";
        request["command_id"] = commandId;
        request["command"] = "retry";
        request["target_operation_id"] = operationId;
        request["new_operation_id"] = newOperationId;
        ApplySelection(
            request,
            selection?.InstallComponentIds,
            selection?.DownloadSourceIds);
        return InvokeControlAsync<RuntimeHostEnvelope>(
            request,
            cancellationToken,
            expectedOperationId: newOperationId);
    }

    public Task<RuntimeMaintenanceObserveEnvelope> ObserveAsync(
        string operationId,
        long afterSequence,
        int limit = 128,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(limit));
        var request = BindingRequest();
        request["request_kind"] = "observe";
        request["operation_id"] = operationId;
        request["after_sequence"] = afterSequence;
        request["limit"] = limit;
        return InvokeControlAsync<RuntimeMaintenanceObserveEnvelope>(
            request,
            cancellationToken,
            expectedRequestKind: "observe",
            expectedOperationId: operationId,
            afterSequence: afterSequence);
    }

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
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? componentIds = null,
        IReadOnlyCollection<string>? requiredCapabilities = null,
        IReadOnlyCollection<string>? installComponentIds = null,
        IReadOnlyCollection<string>? downloadSourceIds = null,
        string? operationId = null)
    {
        RuntimeHostEnvelope envelope = await InvokeAsync(
            operation,
            progress,
            cancellationToken,
            componentIds,
            requiredCapabilities,
            installComponentIds,
            downloadSourceIds,
            operationId).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? componentIds = null,
        IReadOnlyCollection<string>? requiredCapabilities = null,
        IReadOnlyCollection<string>? installComponentIds = null,
        IReadOnlyCollection<string>? downloadSourceIds = null,
        string? operationId = null)
    {
        bool supportsV2 = SupportsCapability("runtime.maintenance.v2");
        if (!supportsV2 && operationId is not null)
        {
            throw new RuntimeInstallerException(
                "Runtime Host does not support caller-provided operation ids.");
        }
        _lastMaintenanceSources = null;
        operationId = supportsV2 ? operationId ?? Guid.NewGuid().ToString() : null;
        LastOperationId = operationId;
        ProcessStartInfo startInfo = BuildStartInfo(
            operation,
            operationId,
            componentIds,
            requiredCapabilities,
            installComponentIds,
            downloadSourceIds);
        int streamedEvents = 0;
        long lastSequence = 0;
        bool replayRequired = false;
        void HandleOutputLine(string line)
        {
            if (TryParseMaintenanceEvent(line, operation, out Host.RuntimeMaintenanceEvent? update)
                && update is not null)
            {
                long sequence = update.Snapshot.Sequence;
                if (supportsV2)
                {
                    if (sequence <= Volatile.Read(ref lastSequence)) return;
                    if (sequence != Volatile.Read(ref lastSequence) + 1)
                    {
                        replayRequired = true;
                        return;
                    }
                    Interlocked.Exchange(ref lastSequence, sequence);
                }
                Interlocked.Increment(ref streamedEvents);
                progress?.Report(update);
            }
        }
        using var ownedCancellation = supportsV2 ? new CancellationTokenSource() : null;
        Task<RuntimeInstallerProcessResult> run = _runner.RunAsync(
            startInfo,
            HandleOutputLine,
            ownedCancellation?.Token ?? cancellationToken);
        RuntimeInstallerProcessResult result;
        bool cancellationWasRequested = false;
        try
        {
            result = supportsV2
                ? await run.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            supportsV2 && operationId is not null && cancellationToken.IsCancellationRequested)
        {
            cancellationWasRequested = true;
            long sequence = Volatile.Read(ref lastSequence);
            await CancelAsync(
                operationId,
                $"cancel-{operationId}",
                sequence > 0 ? sequence : null,
                CancellationToken.None).ConfigureAwait(false);
            Host.RuntimeMaintenanceSnapshot terminal = await AwaitTerminalSnapshotAsync(
                operationId,
                sequence,
                progress).ConfigureAwait(false);
            try
            {
                result = await run.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (TimeoutException) when (
                terminal.OperationState == Host.RuntimeOperationState.Cancelled)
            {
                ownedCancellation?.Cancel();
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                throw new OperationCanceledException(cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new RuntimeInstallerException(
                    "Runtime reached a terminal state but its installer process did not exit.");
            }
            if (terminal.OperationState == Host.RuntimeOperationState.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        if (Volatile.Read(ref streamedEvents) == 0)
        {
            foreach (string line in OutputLines(result.StandardOutput))
            {
                HandleOutputLine(line);
            }
        }
        if (supportsV2 && replayRequired && operationId is not null)
        {
            await ReplayAsync(
                operationId,
                Volatile.Read(ref lastSequence),
                progress,
                cancellationWasRequested ? CancellationToken.None : cancellationToken)
                .ConfigureAwait(false);
        }
        string? envelopeJson = FinalEnvelopeJson(result.StandardOutput);
        if (result.ExitCode != 0)
        {
            string detail = ParseError(envelopeJson) ??
                result.StandardError.Trim();
            RuntimeHostError? error = ParseHostError(envelopeJson);
            throw new RuntimeInstallerException(
                $"Runtime Installer {operation} failed with exit code {result.ExitCode}: {detail}",
                error);
        }

        try
        {
            RuntimeHostEnvelope? value = JsonSerializer.Deserialize<RuntimeHostEnvelope>(
                envelopeJson ?? string.Empty,
                JsonOptions);
            using JsonDocument rawEnvelope = JsonDocument.Parse(envelopeJson ?? string.Empty);
            RuntimeMaintenanceSourceSnapshot? sources = ExtractMaintenanceSources(
                rawEnvelope.RootElement);
            if (value is not null && sources is not null)
            {
                value = value with { MaintenanceSources = sources };
            }
            if (value is null || value.ProtocolVersion != 2 ||
                !string.Equals(value.Operation, operation, StringComparison.Ordinal) ||
                !value.Ok)
            {
                throw new RuntimeInstallerException(
                    value?.Error?.Message ?? $"Runtime Host {operation} returned an invalid envelope.");
            }
            IReadOnlyCollection<string> requestedCapabilities =
                SupportsCapability("runtime.capability-metadata.v1") &&
                requiredCapabilities is { Count: > 0 }
                    ? requiredCapabilities
                    : Array.Empty<string>();
            if (requestedCapabilities.Count > 0 &&
                (value.NegotiatedCapabilities is null ||
                    requestedCapabilities.Except(value.NegotiatedCapabilities).Any()))
            {
                throw new RuntimeInstallerException(
                    $"Runtime Host {operation} did not negotiate every required capability.");
            }
            RuntimeCapabilityDescriptor[] descriptors = value.CapabilityDescriptors ?? [];
            if (descriptors.Any(descriptor =>
                string.IsNullOrWhiteSpace(descriptor.Name) ||
                string.IsNullOrWhiteSpace(descriptor.IntroducedIn) ||
                descriptor.Lifecycle is not ("active" or "deprecated")))
            {
                throw new RuntimeInstallerException(
                    $"Runtime Host {operation} returned invalid capability metadata.");
            }
            NegotiatedCapabilities = value.NegotiatedCapabilities?.ToArray() ?? [];
            CapabilityDescriptors = descriptors.ToArray();
            _lastMaintenanceSources = sources;
            return value;
        }
        catch (JsonException error)
        {
            throw new RuntimeInstallerException(
                $"Runtime Installer {operation} returned invalid JSON: {error.Message}");
        }
    }

    private async Task<Host.RuntimeMaintenanceSnapshot> AwaitTerminalSnapshotAsync(
        string operationId,
        long afterSequence,
        IProgress<Host.RuntimeMaintenanceEvent>? progress)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        long cursor = afterSequence;
        try
        {
            while (true)
            {
                RuntimeMaintenanceObserveEnvelope page = await ObserveAsync(
                    operationId,
                    cursor,
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                foreach (Host.RuntimeMaintenanceEvent update in page.Events)
                {
                    if (update.Snapshot.Sequence <= cursor) continue;
                    cursor = update.Snapshot.Sequence;
                    progress?.Report(update);
                }
                if (!page.More && cursor >= page.Snapshot.Sequence &&
                    page.Snapshot.OperationState is (
                        Host.RuntimeOperationState.Succeeded or
                        Host.RuntimeOperationState.Failed or
                        Host.RuntimeOperationState.Cancelled))
                {
                    return page.Snapshot;
                }
                cursor = Math.Max(cursor, page.ThroughSequence);
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new RuntimeInstallerException(
                "Runtime cancellation was not confirmed by a terminal snapshot.");
        }
    }

    private async Task ReplayAsync(
        string operationId,
        long afterSequence,
        IProgress<Host.RuntimeMaintenanceEvent>? progress,
        CancellationToken cancellationToken)
    {
        long cursor = afterSequence;
        while (true)
        {
            RuntimeMaintenanceObserveEnvelope page = await ObserveAsync(
                operationId,
                cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (Host.RuntimeMaintenanceEvent update in page.Events)
            {
                long sequence = update.Snapshot.Sequence;
                if (sequence <= cursor) continue;
                if (sequence != cursor + 1)
                {
                    throw new RuntimeInstallerException(
                        "Runtime maintenance replay sequence is not contiguous.");
                }
                cursor = sequence;
                progress?.Report(update);
            }
            if (!page.More) return;
            if (page.ThroughSequence <= afterSequence || page.ThroughSequence != cursor)
            {
                throw new RuntimeInstallerException(
                    "Runtime maintenance replay cursor did not advance.");
            }
            afterSequence = cursor;
        }
    }

    private ProcessStartInfo BuildStartInfo(
        string operation,
        string? operationId = null,
        IReadOnlyCollection<string>? componentIds = null,
        IReadOnlyCollection<string>? requiredCapabilities = null,
        IReadOnlyCollection<string>? installComponentIds = null,
        IReadOnlyCollection<string>? downloadSourceIds = null)
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
        Dictionary<string, object?> request = BindingRequest();
        request["operation"] = operation;
        if (SupportsCapability("runtime.maintenance.v2"))
        {
            if (componentIds is { Count: > 0 } &&
                !SupportsCapability("runtime.component-repair.v1"))
            {
                throw new RuntimeInstallerException(
                    "Runtime Host does not support component-scoped repair.");
            }
            if (requiredCapabilities is { Count: > 0 } &&
                !SupportsCapability("runtime.capability-metadata.v1"))
            {
                throw new RuntimeInstallerException(
                    "Runtime Host does not support capability negotiation metadata.");
            }
            request["accepted_event_streams"] = new[] { "ndjson.v2" };
            request["operation_id"] = operationId ?? Guid.NewGuid().ToString();
            if (componentIds is { Count: > 0 }) request["component_ids"] = componentIds;
            if (requiredCapabilities is { Count: > 0 })
            {
                request["required_capabilities"] = requiredCapabilities;
            }
            ApplySelection(request, installComponentIds, downloadSourceIds);
        }
        else if (SupportsMaintenanceEvents())
        {
            request["accepted_event_streams"] = new[] { "ndjson.v1" };
        }
        AddOption(startInfo, "--request-json", JsonSerializer.Serialize(request));
        return startInfo;
    }

    private void ApplySelection(
        Dictionary<string, object?> request,
        IReadOnlyCollection<string>? installComponentIds,
        IReadOnlyCollection<string>? downloadSourceIds)
    {
        if (installComponentIds is not null)
        {
            if (!SupportsCapability("runtime.component-selection.v1"))
            {
                throw new RuntimeInstallerException(
                    "Runtime Host does not support explicit component selection.");
            }
            request["install_component_ids"] = installComponentIds;
        }
        if (downloadSourceIds is { Count: > 0 })
        {
            if (!SupportsCapability("runtime.download-sources.v1"))
            {
                throw new RuntimeInstallerException(
                    "Runtime Host does not support download source selection.");
            }
            request["download_source_ids"] = downloadSourceIds;
        }
    }

    private bool SupportsMaintenanceEvents()
        => SupportsCapability("runtime.maintenance.v1");

    private bool SupportsCapability(string capability)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(_configuration.RuntimeManifest));
            return document.RootElement
                .GetProperty("capabilities")
                .EnumerateArray()
                .Any(item => item.GetString() == capability);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
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

    private async Task<T> InvokeControlAsync<T>(
        Dictionary<string, object?> request,
        CancellationToken cancellationToken,
        string? expectedRequestKind = null,
        string? expectedOperationId = null,
        long? afterSequence = null)
    {
        ProcessStartInfo startInfo = StartInfo(request);
        RuntimeInstallerProcessResult result = await _runner.RunAsync(
            startInfo,
            cancellationToken).ConfigureAwait(false);
        string? envelopeJson = FinalEnvelopeJson(result.StandardOutput);
        RuntimeHostError? error = ParseHostError(envelopeJson);
        if (result.ExitCode != 0)
        {
            throw new RuntimeInstallerException(
                error?.Message ?? result.StandardError.Trim(),
                error);
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(envelopeJson ?? string.Empty);
            ValidateControlEnvelope(
                document.RootElement,
                expectedRequestKind,
                expectedOperationId,
                afterSequence);
            T? value = JsonSerializer.Deserialize<T>(envelopeJson ?? string.Empty, JsonOptions);
            if (value is RuntimeHostEnvelope host)
            {
                RuntimeMaintenanceSourceSnapshot? sources = ExtractMaintenanceSources(
                    document.RootElement);
                if (sources is not null)
                {
                    host = host with { MaintenanceSources = sources };
                    _lastMaintenanceSources = sources;
                    value = (T)(object)host;
                }
            }
            return value ?? throw new RuntimeInstallerException("Runtime control returned no envelope.");
        }
        catch (JsonException exception)
        {
            throw new RuntimeInstallerException(
                $"Runtime control returned invalid JSON: {exception.Message}");
        }
        catch (Exception exception) when (
            (exception is InvalidOperationException &&
                exception is not RuntimeInstallerException) ||
            exception is KeyNotFoundException)
        {
            throw new RuntimeInstallerException(
                $"Runtime control returned an invalid envelope: {exception.Message}");
        }
    }

    private static void ValidateControlEnvelope(
        JsonElement root,
        string? expectedRequestKind,
        string? expectedOperationId,
        long? afterSequence)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("protocol_version", out JsonElement protocolVersion) ||
            protocolVersion.ValueKind != JsonValueKind.Number ||
            !protocolVersion.TryGetInt32(out int version) ||
            version != 2 ||
            !root.TryGetProperty("ok", out JsonElement ok) ||
            ok.ValueKind is not JsonValueKind.True)
        {
            throw new RuntimeInstallerException("Runtime control returned an invalid envelope.");
        }
        if (expectedRequestKind is not null &&
            (!root.TryGetProperty("request_kind", out JsonElement requestKind) ||
                requestKind.GetString() != expectedRequestKind))
        {
            throw new RuntimeInstallerException("Runtime control response kind mismatch.");
        }
        JsonElement operationId;
        if (expectedRequestKind == "observe")
        {
            if (!root.TryGetProperty("operation_id", out operationId) ||
                operationId.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("snapshot", out JsonElement snapshot) ||
                snapshot.ValueKind != JsonValueKind.Object ||
                !snapshot.TryGetProperty("operation_id", out JsonElement snapshotOperationId) ||
                snapshotOperationId.ValueKind != JsonValueKind.String ||
                snapshotOperationId.GetString() != expectedOperationId)
            {
                throw new RuntimeInstallerException(
                    "Runtime observe response has no valid snapshot.");
            }
            ValidateObserveCursor(root, expectedOperationId, afterSequence ?? 0);
        }
        else
        {
            if (!root.TryGetProperty("maintenance", out JsonElement maintenance) ||
                maintenance.ValueKind != JsonValueKind.Object ||
                !maintenance.TryGetProperty("operation_id", out operationId) ||
                operationId.ValueKind != JsonValueKind.String)
            {
                throw new RuntimeInstallerException(
                    "Runtime command response has no maintenance snapshot.");
            }
        }
        if (expectedOperationId is not null && operationId.GetString() != expectedOperationId)
        {
            throw new RuntimeInstallerException("Runtime control operation id mismatch.");
        }
    }

    private static void ValidateObserveCursor(
        JsonElement root,
        string? expectedOperationId,
        long afterSequence)
    {
        if (!root.TryGetProperty("oldest_sequence", out JsonElement oldest) ||
            oldest.ValueKind != JsonValueKind.Number ||
            !oldest.TryGetInt64(out long oldestSequence) ||
            oldestSequence < 1 ||
            !root.TryGetProperty("through_sequence", out JsonElement through) ||
            through.ValueKind != JsonValueKind.Number ||
            !through.TryGetInt64(out long throughSequence) ||
            throughSequence < 0 ||
            !root.TryGetProperty("more", out JsonElement more) ||
            more.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("events", out JsonElement events) ||
            events.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("snapshot", out JsonElement rootSnapshot) ||
            rootSnapshot.ValueKind != JsonValueKind.Object ||
            !rootSnapshot.TryGetProperty("sequence", out JsonElement snapshotSequenceElement) ||
            snapshotSequenceElement.ValueKind != JsonValueKind.Number ||
            !snapshotSequenceElement.TryGetInt64(out long snapshotSequence) ||
            snapshotSequence < 1 ||
            snapshotSequence < throughSequence ||
            oldestSequence > snapshotSequence)
        {
            throw new RuntimeInstallerException("Runtime observe cursor is invalid.");
        }
        long cursor = afterSequence;
        foreach (JsonElement update in events.EnumerateArray())
        {
            if (update.ValueKind != JsonValueKind.Object ||
                !update.TryGetProperty("snapshot", out JsonElement snapshot) ||
                snapshot.ValueKind != JsonValueKind.Object ||
                !snapshot.TryGetProperty("sequence", out JsonElement sequenceElement) ||
                sequenceElement.ValueKind != JsonValueKind.Number ||
                !sequenceElement.TryGetInt64(out long sequence) ||
                !snapshot.TryGetProperty("operation_id", out JsonElement operationId) ||
                operationId.ValueKind != JsonValueKind.String)
            {
                throw new RuntimeInstallerException(
                    "Runtime observe event snapshot is invalid.");
            }
            if (sequence != cursor + 1 ||
                operationId.GetString() != expectedOperationId)
            {
                throw new RuntimeInstallerException(
                    "Runtime observe events are not contiguous.");
            }
            cursor = sequence;
        }
        if ((events.GetArrayLength() > 0 && throughSequence != cursor) ||
            (events.GetArrayLength() == 0 && throughSequence > afterSequence) ||
            (events.GetArrayLength() == 0 && more.ValueKind == JsonValueKind.True))
        {
            throw new RuntimeInstallerException(
                "Runtime observe through_sequence mismatch.");
        }
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

    private static RuntimeMaintenanceSourceSnapshot? ExtractMaintenanceSources(
        JsonElement envelope)
    {
        if (!envelope.TryGetProperty("maintenance", out JsonElement maintenance) ||
            maintenance.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        IReadOnlyList<string>? requested = ReadOptionalIds(
            maintenance,
            "requested_download_source_ids");
        IReadOnlyList<string>? effective = ReadOptionalIds(
            maintenance,
            "effective_download_source_ids");
        return requested is null && effective is null
            ? null
            : new RuntimeMaintenanceSourceSnapshot(requested ?? [], effective ?? []);
    }

    private static IReadOnlyList<string>? ReadOptionalIds(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new RuntimeInstallerException($"Runtime Host field '{name}' must be an array.");
        }
        var ids = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            string? id = item.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new RuntimeInstallerException(
                    $"Runtime Host field '{name}' contains a blank source id.");
            }
            ids.Add(id);
        }
        return ids;
    }

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
