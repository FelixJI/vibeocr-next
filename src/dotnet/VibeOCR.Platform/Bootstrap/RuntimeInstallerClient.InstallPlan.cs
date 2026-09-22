using System.Text.Json;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.Platform.Bootstrap;

public sealed partial class RuntimeInstallerClient
{
    private const string InstallPlanCapability = "runtime.install-plan.v1";
    public bool SupportsInstallPlan => SupportsCapability(InstallPlanCapability) &&
        SupportsCapability("runtime.maintenance.v2");

    public async Task<Host.RuntimeInstallPlan> PreviewInstallAsync(
        RuntimeInstallSelection selection,
        string accelerator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!SupportsInstallPlan)
            throw new NotSupportedException("当前 Runtime 不支持安装预览，请先更新 Runtime。");
        if (accelerator is not ("cpu" or "nvidia_cuda"))
            throw new ArgumentException("未知计算设备。", nameof(accelerator));
        Dictionary<string, object?> request = BindingRequest();
        request["request_kind"] = "install_plan";
        request["accelerator"] = accelerator;
        request["required_capabilities"] = new[] { InstallPlanCapability };
        ApplySelection(request, selection.InstallComponentIds, selection.DownloadSourceIds);
        RuntimeInstallerProcessResult result = await _runner.RunAsync(StartInfo(request), cancellationToken)
            .ConfigureAwait(false);
        string? json = FinalEnvelopeJson(result.StandardOutput);
        RuntimeHostError? error = ParseHostError(json);
        if (result.ExitCode != 0 || error is not null)
            throw new RuntimeInstallerException(error?.Message ?? "无法读取安装计划。", error);
        try
        {
            Host.RuntimeInstallPlanResponse response = JsonSerializer.Deserialize<Host.RuntimeInstallPlanResponse>(
                json ?? "", JsonOptions) ?? throw new RuntimeInstallerException("安装计划响应为空。");
            if (response.ProtocolVersion != 2 || response.ResponseKind != "install_plan" ||
                response.Plan is null || string.IsNullOrWhiteSpace(response.Plan.PlanId) ||
                !DateTimeOffset.TryParse(response.Plan.ExpiresAt, out _) ||
                response.Plan.Components is null || response.Plan.Blockers is null ||
                response.Plan.Cost is null || response.Plan.EffectiveComponentIds is null ||
                response.Plan.EffectiveDownloadSourceIds is null)
                throw new RuntimeInstallerException("安装计划响应无效。");
            return response.Plan;
        }
        catch (JsonException exception)
        {
            throw new RuntimeInstallerException($"安装计划响应无效：{exception.Message}");
        }
    }

    public Task<RuntimeLaunch> ConfirmInstallAsync(
        string planId,
        string operationId,
        IProgress<Host.RuntimeMaintenanceEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (!SupportsInstallPlan)
            throw new NotSupportedException("当前 Runtime 不支持安装预览，请先更新 Runtime。");
        return InvokeLaunchAsync("ensure", progress, cancellationToken,
            requiredCapabilities: [InstallPlanCapability], operationId: operationId, planId: planId);
    }
}
