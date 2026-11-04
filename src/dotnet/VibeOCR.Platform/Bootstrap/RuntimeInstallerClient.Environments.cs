using System.Diagnostics;
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
    [property: JsonPropertyName("last_install_failure")] ManagedEnvironmentInstallFailure? LastInstallFailure = null,
    [property: JsonPropertyName("engine")] string? Engine = null,
    [property: JsonPropertyName("family")] string? Family = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("operation_blocks")] IReadOnlyDictionary<string, string?>? OperationBlocks = null);

/// <summary>目录内下载源：id/kind/展示名与脱敏端点（模型源实际下载端点未知）。</summary>
public sealed record ManagedDownloadSource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("is_default")] bool IsDefault = false,
    [property: JsonPropertyName("provider")] string? Provider = null);

/// <summary>单环境每 kind 的权威解析结果；模型偏好与原生下载器的实际端点分别表达。</summary>
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
    [property: JsonPropertyName("recipes")] IReadOnlyList<ManagedEnvironmentRecipe>? Recipes = null,
    [property: JsonPropertyName("resolved_default_sources")] IReadOnlyList<ManagedEnvironmentResolvedSource>? ResolvedDefaultSources = null,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities = null);

public sealed record PreparedEnvironmentSwitch(
    [property: JsonPropertyName("environment_id")] string EnvironmentId,
    [property: JsonPropertyName("environment_revision")] int EnvironmentRevision,
    [property: JsonPropertyName("active_id")] string? ActiveId,
    [property: JsonPropertyName("active_revision")] int ActiveRevision,
    [property: JsonPropertyName("python")] string Python,
    [property: JsonPropertyName("requires_supervisor")] bool RequiresSupervisor,
    [property: JsonPropertyName("launch")] RuntimeLaunch? Launch,
    [property: JsonPropertyName("selected_device")] string? SelectedDevice = null);

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
    [property: JsonPropertyName("supported_devices")] IReadOnlyList<string>? SupportedDevices = null,
    [property: JsonPropertyName("python_version")] string? PythonVersion = null,
    [property: JsonPropertyName("abi")] string? Abi = null,
    [property: JsonPropertyName("platform")] string? Platform = null,
    [property: JsonPropertyName("scope_id")] string? ScopeId = null,
    [property: JsonPropertyName("component_ids")] IReadOnlyList<string>? ComponentIds = null,
    [property: JsonPropertyName("recipe_lock")] string? RecipeLock = null,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string>? Dependencies = null,
    [property: JsonPropertyName("dependency_origin")] string? DependencyOrigin = null,
    [property: JsonPropertyName("python_origin")] string? PythonOrigin = null,
    [property: JsonPropertyName("runtime_wheel_origin")] string? RuntimeWheelOrigin = null,
    [property: JsonPropertyName("engine")] string? Engine = null,
    [property: JsonPropertyName("family")] string? Family = null,
    [property: JsonPropertyName("family_display_name")] string? FamilyDisplayName = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("supports_remote_host")] bool SupportsRemoteHost = false,
    [property: JsonPropertyName("default_remote_host")] bool DefaultRemoteHost = false);

public sealed record ManagedCleanupItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("environment_id")] string? EnvironmentId,
    [property: JsonPropertyName("logical_bytes")] long? LogicalBytes,
    [property: JsonPropertyName("can_clean")] bool CanClean,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
    [property: JsonPropertyName("last_error")] string? LastError = null,
    [property: JsonPropertyName("path_count")] int PathCount = 0);

public sealed record ManagedCleanupPlan(
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("items")] IReadOnlyList<ManagedCleanupItem> Items,
    [property: JsonPropertyName("size_kind")] string SizeKind,
    [property: JsonPropertyName("warning")] string Warning,
    [property: JsonPropertyName("last_result")] ManagedCleanupResult? LastResult = null);

public sealed record ManagedCleanupResultItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("removed_logical_bytes")] long RemovedLogicalBytes);

public sealed record ManagedCleanupResult(
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("items")] IReadOnlyList<ManagedCleanupResultItem> Items,
    [property: JsonPropertyName("size_kind")] string SizeKind);

public interface IManagedEnvironmentSwitchReservation : IAsyncDisposable
{
    PreparedEnvironmentSwitch Prepared { get; }
    Task<CommittedEnvironmentSwitch> CommitAsync(StartedEnvironmentHealth? health, CancellationToken cancellationToken = default);
}

public interface IManagedEnvironmentClient
{
    bool SupportsEnvironmentCleanup => false;
    Task<ManagedCleanupPlan> PreviewEnvironmentCleanupAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 Runtime 不支持空间清理，请更新运行组件。");
    Task<ManagedCleanupResult> RunEnvironmentCleanupAsync(string planId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 Runtime 不支持空间清理，请更新运行组件。");
    Task<IManagedEnvironmentSwitchReservation> BeginEnvironmentSwitchAsync(string environmentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 Runtime 不支持受保护的环境切换，请更新运行组件。");
    Task<IManagedEnvironmentSwitchReservation> BeginEnvironmentSwitchAsync(string environmentId, string? device, CancellationToken cancellationToken = default) =>
        device is null ? BeginEnvironmentSwitchAsync(environmentId, cancellationToken)
            : throw new NotSupportedException("当前 Runtime 不支持独立设备选择。");
    Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default);
    Task<ManagedEnvironmentList> InitializeDefaultEnvironmentAsync(CancellationToken cancellationToken = default) =>
        ListEnvironmentsAsync(cancellationToken);
    Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
        string? environmentId, string? paddleocrModelSourceId,
        string? mineruModelSourceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 Runtime 客户端不支持独立模型来源，请先更新 Runtime。");
    Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
        string? environmentId, string? modelSourceId,
        CancellationToken cancellationToken = default);
    Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(
        string environmentId, CancellationToken cancellationToken = default);
    Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
        PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
        CancellationToken cancellationToken = default);
    Task DeleteEnvironmentAsync(string environmentId, CancellationToken cancellationToken = default);
}

public sealed partial class RuntimeInstallerClient : IManagedEnvironmentClient
{
    public bool SupportsEnvironmentCleanup { get; private set; }

    public Task<ManagedCleanupPlan> PreviewEnvironmentCleanupAsync(CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<ManagedCleanupPlan>("preview_cleanup", [], cancellationToken);

    public Task<ManagedCleanupResult> RunEnvironmentCleanupAsync(string planId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<ManagedCleanupResult>("run_cleanup", new() { ["plan_id"] = planId, ["item_ids"] = itemIds }, cancellationToken);

    public Task<IManagedEnvironmentSwitchReservation> BeginEnvironmentSwitchAsync(string environmentId, CancellationToken cancellationToken = default) =>
        BeginEnvironmentSwitchAsync(environmentId, null, cancellationToken);

    public async Task<IManagedEnvironmentSwitchReservation> BeginEnvironmentSwitchAsync(string environmentId, string? device, CancellationToken cancellationToken = default)
    {
        var request = BindingRequest();
        request.Remove("accelerator");
        request["request_kind"] = "environment";
        request["action"] = "prepare_switch";
        if (device is not null) request["device"] = device;
        request["environment_id"] = environmentId;
        ProcessStartInfo startInfo = StartInfo(request);
        startInfo.RedirectStandardInput = true;
        startInfo.ArgumentList.Add("--environment-switch-control");
        return await EnvironmentSwitchReservation.StartAsync(startInfo, environmentId, cancellationToken,
            _runner is RuntimeInstallerCommandRunner runner ? runner.ObserveCommand : null).ConfigureAwait(false);
    }

  // Startup already returns a fully probed list. Share only that in-flight
  // result, and order an earlier refresh before initialization, so this
  // client does not compete with itself for Runtime's registry lock.
  private readonly object environmentReadSync = new();
  private readonly SemaphoreSlim environmentReadGate = new(1, 1);
  private Task<ManagedEnvironmentList>? environmentInitialization;

  public Task<ManagedEnvironmentList> ListEnvironmentsAsync(CancellationToken cancellationToken = default)
  {
    Task admission;
    lock (environmentReadSync)
    {
      if (environmentInitialization is { } initialization)
        return initialization.WaitAsync(cancellationToken);
      admission = environmentReadGate.WaitAsync(cancellationToken);
    }
    return ReadEnvironmentListAsync("list", admission, cancellationToken);
  }

  public Task<ManagedEnvironmentList> InitializeDefaultEnvironmentAsync(CancellationToken cancellationToken = default)
  {
    Task admission;
    TaskCompletionSource<ManagedEnvironmentList> completion;
    lock (environmentReadSync)
    {
      if (environmentInitialization is { } initialization)
        return initialization.WaitAsync(cancellationToken);
      completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
      environmentInitialization = completion.Task;
      admission = environmentReadGate.WaitAsync(cancellationToken);
    }
    _ = CompleteEnvironmentInitializationAsync(completion, admission, cancellationToken);
    return completion.Task;
  }

  private async Task<ManagedEnvironmentList> ReadEnvironmentListAsync(
    string action, Task admission, CancellationToken cancellationToken)
  {
    await admission.ConfigureAwait(false);
    try
    {
      return await InvokeEnvironmentAsync<ManagedEnvironmentList>(action, [], cancellationToken).ConfigureAwait(false);
    }
    finally { environmentReadGate.Release(); }
  }

  private async Task CompleteEnvironmentInitializationAsync(
    TaskCompletionSource<ManagedEnvironmentList> completion, Task admission, CancellationToken cancellationToken)
  {
    try
    {
      ManagedEnvironmentList result;
      try
      {
        result = await ReadEnvironmentListAsync("initialize_default", admission, cancellationToken).ConfigureAwait(false);
      }
      finally
      {
        // Clear before publishing completion, including synchronous
        // failures and cancellation while queued. Never cache health.
        lock (environmentReadSync)
        {
          if (ReferenceEquals(environmentInitialization, completion.Task))
            environmentInitialization = null;
        }
      }
      completion.TrySetResult(result);
    }
    catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
    catch (Exception error) { completion.TrySetException(error); }
  }

    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
        string? environmentId, string? modelSourceId,
        CancellationToken cancellationToken = default) =>
        // 统一模型源经 model_source_id 发送：Runtime 负责把它展开为每引擎
        // 的标准来源 ID（paddleocr-modelscope / mineru-modelscope），
        // 直接转调独立来源重载会因 ID 形态不符被拒绝。
        InvokeEnvironmentAsync<ManagedEnvironmentList>("set_sources", new()
        {
            ["environment_id"] = environmentId,
            ["model_source_id"] = modelSourceId,
        }, cancellationToken);

    public Task<ManagedEnvironmentList> SetEnvironmentSourcesAsync(
        string? environmentId, string? paddleocrModelSourceId,
        string? mineruModelSourceId, CancellationToken cancellationToken = default) =>
        InvokeEnvironmentAsync<ManagedEnvironmentList>("set_sources", new()
        {
            ["environment_id"] = environmentId,
            ["paddleocr_model_source_id"] = paddleocrModelSourceId,
            ["mineru_model_source_id"] = mineruModelSourceId,
        }, cancellationToken);

    public Task<PreparedEnvironmentSwitch> PrepareEnvironmentSwitchAsync(
        string environmentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("环境切换必须使用持有目标锁的 reservation 会话。");

    public Task<CommittedEnvironmentSwitch> CommitEnvironmentSwitchAsync(
        PreparedEnvironmentSwitch prepared, StartedEnvironmentHealth? startedHealth = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("环境切换必须由 reservation 会话提交。");

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
        ProcessStartInfo startInfo = StartInfo(request);
        if (action is "initialize_default" or "run_cleanup")
        {
            startInfo.RedirectStandardInput = true;
            startInfo.ArgumentList.Add("--environment-cancel-control");
        }
        RuntimeInstallerProcessResult result = await _runner.RunAsync(startInfo, cancellationToken)
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
            T value = payload.Deserialize<T>(JsonOptions)
                ?? throw new RuntimeInstallerException("运行环境响应为空。");
            if (value is ManagedCleanupPlan cleanupPlan && (string.IsNullOrEmpty(cleanupPlan.PlanId) || cleanupPlan.PlanId.Length != 32 || !cleanupPlan.PlanId.All(Uri.IsHexDigit) ||
                cleanupPlan.SizeKind != "logical_bytes" || cleanupPlan.Items is null ||
                cleanupPlan.Items.Any(item => item is null) ||
                cleanupPlan.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != cleanupPlan.Items.Count ||
                cleanupPlan.Items.Any(item => string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 1024 || item.LogicalBytes < 0 ||
                    item.Label is null || item.Reason is null || item.Paths is null || item.Paths.Count > 8 || item.PathCount < item.Paths.Count)))
                throw new RuntimeInstallerException("空间清理预览响应无效。");
            if (value is ManagedCleanupResult cleanupResult && (cleanupResult.PlanId != fields["plan_id"] as string ||
                cleanupResult.SizeKind != "logical_bytes" || cleanupResult.Items is null || cleanupResult.Items.Count > 2048 ||
                cleanupResult.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.State is not ("deleted" or "failed" or "cancelled") ||
                    item.RemovedLogicalBytes < 0 || item.Detail is null) ||
                cleanupResult.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != cleanupResult.Items.Count ||
                !cleanupResult.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal).SetEquals((IReadOnlyList<string>)fields["item_ids"]!)))
                throw new RuntimeInstallerException("空间清理结果响应无效。");
            if (value is ManagedEnvironmentList list)
            {
                SupportsEnvironmentCleanup = list.Capabilities?.Contains("environment.cleanup.v1", StringComparer.Ordinal) == true &&
                    list.Capabilities.Contains("environment.switch-reservation.v1", StringComparer.Ordinal);
            }
            return value;
        }
        catch (JsonException exception)
        {
            throw new RuntimeInstallerException($"运行环境响应无效：{exception.Message}");
        }
    }
}
