using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeOCR.Platform.Bootstrap;

public sealed record ManagedEnvironment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("python")] string Python,
    [property: JsonPropertyName("python_state")] string PythonState,
    [property: JsonPropertyName("dependency_state")] string DependencyState,
    [property: JsonPropertyName("engine_state")] string EngineState,
    [property: JsonPropertyName("model_state")] string ModelState,
    [property: JsonPropertyName("service_state")] string ServiceState,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("configured_recognition_types")] IReadOnlyList<string>? ConfiguredRecognitionTypes = null,
    [property: JsonPropertyName("target_device")] string? TargetDevice = null,
    [property: JsonPropertyName("actual_device")] string? ActualDevice = null,
    [property: JsonPropertyName("python_version")] string? PythonVersion = null,
    [property: JsonPropertyName("abi")] string? Abi = null,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("disk_bytes")] long DiskBytes = 0,
    [property: JsonPropertyName("recipe")] string? Recipe = null,
    [property: JsonPropertyName("last_install_failure")] ManagedEnvironmentInstallFailure? LastInstallFailure = null);

public sealed record ManagedEnvironmentInstallFailure(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("environment_revision")] int EnvironmentRevision,
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("next_action")] string NextAction,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record ManagedEnvironmentList(
    [property: JsonPropertyName("active_id")] string? ActiveId,
    [property: JsonPropertyName("active_revision")] int ActiveRevision,
    [property: JsonPropertyName("environments")] IReadOnlyList<ManagedEnvironment> Environments,
    [property: JsonPropertyName("package_source_ids")] IReadOnlyList<string>? PackageSourceIds = null);

public sealed record ManagedEnvironmentPlan(
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("environment_id")] string EnvironmentId,
    [property: JsonPropertyName("environment_revision")] int EnvironmentRevision,
    [property: JsonPropertyName("active_revision")] int ActiveRevision,
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string>? Dependencies = null,
    [property: JsonPropertyName("requested_recipe")] string? RequestedRecipe = null);

public sealed record PreparedEnvironmentSwitch(
    [property: JsonPropertyName("environment_id")] string EnvironmentId,
    [property: JsonPropertyName("environment_revision")] int EnvironmentRevision,
    [property: JsonPropertyName("active_id")] string? ActiveId,
    [property: JsonPropertyName("active_revision")] int ActiveRevision,
    [property: JsonPropertyName("python")] string Python,
    [property: JsonPropertyName("requires_supervisor")] bool RequiresSupervisor,
    [property: JsonPropertyName("launch")] RuntimeLaunch? Launch);

public sealed record CommittedEnvironmentSwitch(
    [property: JsonPropertyName("active_id")] string ActiveId,
    [property: JsonPropertyName("active_revision")] int ActiveRevision);

public sealed record StartedEnvironmentHealth(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("instance_id")] string InstanceId);

public interface IManagedEnvironmentClient
{
    Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default);
    Task<ManagedEnvironment> CreateEnvironmentAsync(string name, CancellationToken cancellationToken = default);
    Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
        string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
        CancellationToken cancellationToken = default);
    Task<ManagedEnvironment> InstallEnvironmentAsync(
        ManagedEnvironmentPlan plan, CancellationToken cancellationToken = default);
    Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(
        string environmentId, CancellationToken cancellationToken = default);
    Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
        PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
        CancellationToken cancellationToken = default);
    Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(
        string environmentId, CancellationToken cancellationToken = default);
    Task DeleteEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default);
}

public sealed partial class RuntimeInstallerClient : IManagedEnvironmentClient
{
    public Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<ManagedEnvironmentList>("list", [], cancellationToken);

    public Task<ManagedEnvironment> CreateEnvironmentAsync(string name, CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<ManagedEnvironment>("create", new() { ["name"] = name }, cancellationToken);

    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
        string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object?> { ["environment_id"] = environmentId, ["recipe"] = recipe };
        if (sourceIds is not null) fields["source_ids"] = sourceIds;
        return InvokeEnvironmentAsync<ManagedEnvironmentPlan>("preview_install", fields, cancellationToken);
    }

    public Task<ManagedEnvironment> InstallEnvironmentAsync(
        ManagedEnvironmentPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return InvokeEnvironmentAsync<ManagedEnvironment>("install", new()
        {
            ["plan_id"] = plan.PlanId,
            ["environment_id"] = plan.EnvironmentId,
            ["recipe"] = plan.Recipe,
            ["source_ids"] = plan.SourceIds,
        }, cancellationToken);
    }

    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(
        string environmentId, CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<PreparedEnvironmentSwitch>("prepare_switch",
            new() { ["environment_id"] = environmentId }, cancellationToken);

    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
        PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var fields = new Dictionary<string, object?> { ["prepared"] = prepared };
        if (startedHealth is not null) fields["started_health"] = startedHealth;
        return InvokeEnvironmentAsync<CommittedEnvironmentSwitch>("commit_switch", fields, cancellationToken);
    }

    public Task<ManagedEnvironment> RepairEmptyEnvironmentAsync(
        string environmentId, CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<ManagedEnvironment>("repair_empty",
            new() { ["environment_id"] = environmentId }, cancellationToken);

    public async Task DeleteEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default) =>
        await InvokeEnvironmentAsync<JsonElement>("delete",
            new() { ["environment_id"] = environmentId }, cancellationToken).ConfigureAwait(false);

    private async Task<T> InvokeEnvironmentAsync<T>(
        string action, Dictionary<string, object?> fields, CancellationToken cancellationToken)
    {
        var request = BindingRequest();
        request.Remove("accelerator");
        request["request_kind"] = "environment";
        request["action"] = action;
        foreach ((string key, object? value) in fields) request[key] = value;
        RuntimeInstallerProcessResult result = await _runner.RunAsync(StartInfo(request), cancellationToken)
            .ConfigureAwait(false);
        string? json = FinalEnvelopeJson(result.StandardOutput);
        RuntimeHostError? error = ParseHostError(json);
        if (result.ExitCode != 0 || error is not null)
            throw new RuntimeInstallerException(error?.Message ?? "运行环境操作失败。", error);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json ?? "");
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("protocol_version", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int protocol) || protocol != 2 ||
                !root.TryGetProperty("response_kind", out JsonElement kind) || kind.ValueKind != JsonValueKind.String ||
                kind.GetString() != "environment" ||
                !root.TryGetProperty("action", out JsonElement returnedAction) ||
                returnedAction.ValueKind != JsonValueKind.String || returnedAction.GetString() != action ||
                !root.TryGetProperty("result", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object)
                throw new RuntimeInstallerException("运行环境响应协议无效。");
            return payload.Deserialize<T>(JsonOptions)
                ?? throw new RuntimeInstallerException("运行环境响应为空。");
        }
        catch (JsonException exception)
        {
            throw new RuntimeInstallerException($"运行环境响应无效：{exception.Message}");
        }
    }
}
