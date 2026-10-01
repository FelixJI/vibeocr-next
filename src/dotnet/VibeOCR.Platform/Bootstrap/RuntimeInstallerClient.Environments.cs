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
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string>? SourceIds = null,
    [property: JsonPropertyName("override_source_ids")] IReadOnlyList<string>? OverrideSourceIds = null,
    [property: JsonPropertyName("unknown_source_ids")] IReadOnlyList<string>? UnknownSourceIds = null,
    [property: JsonPropertyName("resolved_sources")] IReadOnlyList<ManagedEnvironmentResolvedSource>? ResolvedSources = null,
    [property: JsonPropertyName("resolved_source_ids")] IReadOnlyList<string>? ResolvedSourceIds = null,
    [property: JsonPropertyName("last_install_failure")] ManagedEnvironmentInstallFailure? LastInstallFailure = null);

/// <summary>目录内下载源：id/kind/展示名与脱敏端点（模型源实际下载端点未知）。</summary>
public sealed record ManagedDownloadSource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("endpoint")] string Endpoint);

/// <summary>单环境每 kind 的解析结果：id 为 null 表示产品默认（模型源无覆盖时官方原生默认）。</summary>
public sealed record ManagedEnvironmentResolvedSource(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("display_name")] string? DisplayName = null,
    [property: JsonPropertyName("origin")] string Origin = "product_default");

public sealed record ManagedEnvironmentInstallFailure(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("environment_revision")] int EnvironmentRevision,
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("next_action")] string NextAction,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("requested_source_ids")] IReadOnlyList<string>? RequestedSourceIds = null,
    [property: JsonPropertyName("effective_source_ids")] IReadOnlyList<string>? EffectiveSourceIds = null,
    // 只读回放操作启动时冻结的 plan_id；历史记录缺失时为 null，
    // 调用方据此把失败/中断终态绑定回发起的安装计划，不得凭 revision 冒认。
    [property: JsonPropertyName("plan_id")] string? PlanId = null);

public sealed record ManagedEnvironmentList(
    [property: JsonPropertyName("active_id")] string? ActiveId,
    [property: JsonPropertyName("active_revision")] int ActiveRevision,
    [property: JsonPropertyName("environments")] IReadOnlyList<ManagedEnvironment> Environments,
    [property: JsonPropertyName("sources")] IReadOnlyList<ManagedDownloadSource>? Sources = null,
    [property: JsonPropertyName("default_source_ids")] IReadOnlyList<string>? DefaultSourceIds = null,
    [property: JsonPropertyName("unknown_default_source_ids")] IReadOnlyList<string>? UnknownDefaultSourceIds = null,
    [property: JsonPropertyName("source_config_revision")] int SourceConfigRevision = 0,
    [property: JsonPropertyName("package_source_ids")] IReadOnlyList<string>? PackageSourceIds = null,
    [property: JsonPropertyName("recipes")] IReadOnlyList<ManagedEnvironmentRecipe>? Recipes = null);

public sealed record ManagedEnvironmentPlan(
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("environment_id")] string EnvironmentId,
    [property: JsonPropertyName("environment_revision")] int EnvironmentRevision,
    [property: JsonPropertyName("active_revision")] int ActiveRevision,
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string>? Dependencies = null,
    [property: JsonPropertyName("requested_recipe")] string? RequestedRecipe = null,
    [property: JsonPropertyName("requested_source_ids")] IReadOnlyList<string>? RequestedSourceIds = null,
    [property: JsonPropertyName("source_config_revision")] int SourceConfigRevision = 0,
    [property: JsonPropertyName("sources")] IReadOnlyList<ManagedEnvironmentPlanSource>? Sources = null,
    [property: JsonPropertyName("dependency_origin")] string? DependencyOrigin = null,
    [property: JsonPropertyName("python_origin")] string? PythonOrigin = null,
    [property: JsonPropertyName("runtime_wheel_origin")] string? RuntimeWheelOrigin = null);

/// <summary>计划内单来源：请求/继承标记、用途与脱敏端点。</summary>
public sealed record ManagedEnvironmentPlanSource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("requested")] bool Requested,
    [property: JsonPropertyName("inherited_from")] string InheritedFrom,
    [property: JsonPropertyName("usage")] string Usage,
    [property: JsonPropertyName("actual_endpoint")] string? ActualEndpoint = null);

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

/// <summary>Runtime 权威配方目录条目：仅搬运 Runtime 投影的锁/用途/设备与来源真值，C# 不另算依赖图。</summary>
public sealed record ManagedEnvironmentRecipe(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("configured_recognition_types")] IReadOnlyList<string>? ConfiguredRecognitionTypes = null,
    [property: JsonPropertyName("accelerator")] string? Accelerator = null,
    [property: JsonPropertyName("target_device")] string? TargetDevice = null,
    [property: JsonPropertyName("python_version")] string? PythonVersion = null,
    [property: JsonPropertyName("abi")] string? Abi = null,
    [property: JsonPropertyName("platform")] string? Platform = null,
    [property: JsonPropertyName("scope_id")] string? ScopeId = null,
    [property: JsonPropertyName("component_ids")] IReadOnlyList<string>? ComponentIds = null,
    [property: JsonPropertyName("recipe_lock")] string? RecipeLock = null,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string>? Dependencies = null,
    [property: JsonPropertyName("dependency_origin")] string? DependencyOrigin = null,
    [property: JsonPropertyName("python_origin")] string? PythonOrigin = null,
    [property: JsonPropertyName("runtime_wheel_origin")] string? RuntimeWheelOrigin = null);

/// <summary>兼容环境查询命中：被选中的环境与选择依据（优先活动，其次稳定 id 序）。</summary>
public sealed record ManagedEnvironmentSelection(
    [property: JsonPropertyName("environment_id")] string EnvironmentId,
    [property: JsonPropertyName("environment_revision")] int EnvironmentRevision,
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("selection_reason")] string SelectionReason);

/// <summary>单环境对查询的评估：selected 标记选中项；reason_code 说明缺失/不兼容/未验证原因。</summary>
public sealed record ManagedEnvironmentQueryMatch(
    [property: JsonPropertyName("environment_id")] string EnvironmentId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("recipe")] string? Recipe = null,
    [property: JsonPropertyName("active")] bool Active = false,
    [property: JsonPropertyName("selected")] bool Selected = false,
    [property: JsonPropertyName("reason_code")] string? ReasonCode = null);

/// <summary>find_compatible 结果：请求配方、完整配方目录、选中环境与逐环境评估，均为 Runtime 真值。</summary>
public sealed record ManagedEnvironmentQueryResult(
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("recipe_lock")] string? RecipeLock = null,
    [property: JsonPropertyName("recipes")] IReadOnlyList<ManagedEnvironmentRecipe>? Recipes = null,
    [property: JsonPropertyName("selected")] ManagedEnvironmentSelection? Selected = null,
    [property: JsonPropertyName("environments")] IReadOnlyList<ManagedEnvironmentQueryMatch>? Environments = null);

public interface IManagedEnvironmentClient
{
    Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default);
    Task<ManagedEnvironment> CreateEnvironmentAsync(string name, CancellationToken cancellationToken = default);
    Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
        string? environmentId, string? packageSourceId, string? modelSourceId,
        CancellationToken cancellationToken = default);
    Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
        string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
        CancellationToken cancellationToken = default);
    Task<ManagedEnvironmentQueryResult> FindCompatibleEnvironmentAsync(
        string recipe, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 Runtime 不支持兼容环境查询，请先更新 Runtime。");
    Task<ManagedEnvironment> InstallEnvironmentAsync(
        ManagedEnvironmentPlan plan, IReadOnlyList<string>? sourceIds = null,
        CancellationToken cancellationToken = default);
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

    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
        string? environmentId, string? packageSourceId, string? modelSourceId,
        CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<ManagedEnvironmentList>("set_sources", new()
        {
            ["environment_id"] = environmentId,
            ["package_source_id"] = packageSourceId,
            ["model_source_id"] = modelSourceId,
        }, cancellationToken);

    public Task<ManagedEnvironmentPlan> PreviewEnvironmentInstallAsync(
        string environmentId, string recipe, IReadOnlyList<string>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object?> { ["environment_id"] = environmentId, ["recipe"] = recipe };
        // source_ids 省略 ≠ null：省略表示本次显式“跟随配置”，null 用于确认阶段。
        if (sourceIds is not null) fields["source_ids"] = sourceIds;
        return InvokeEnvironmentAsync<ManagedEnvironmentPlan>("preview_install", fields, cancellationToken);
    }

    public Task<ManagedEnvironmentQueryResult> FindCompatibleEnvironmentAsync(
        string recipe, CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<ManagedEnvironmentQueryResult>("find_compatible",
            new() { ["recipe"] = recipe }, cancellationToken);

    public Task<ManagedEnvironment> InstallEnvironmentAsync(
        ManagedEnvironmentPlan plan, IReadOnlyList<string>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return InvokeEnvironmentAsync<ManagedEnvironment>("install", new()
        {
            ["plan_id"] = plan.PlanId,
            ["environment_id"] = plan.EnvironmentId,
            ["recipe"] = plan.Recipe,
            ["source_ids"] = sourceIds,
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
