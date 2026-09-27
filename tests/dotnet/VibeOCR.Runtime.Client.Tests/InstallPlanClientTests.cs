// Client-level tests for the runtime.install-plan.v1 extension: wire path,
// payload shape, typed error conversion, and the local plan_id guards
// (mirroring tests/contracts/v2/test_install_plan.py).
using System.Net;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Runtime.Client;
using Xunit;

namespace VibeOCR.Runtime.Client.Tests;

public sealed class InstallPlanClientTests
{
    private const string PlanJson = """
        {
          "schema_version": 2,
          "plan": {
            "plan_id": "plan-7f3a91c2e8d4",
            "expires_at": "2026-09-19T12:34:56Z",
            "accelerator": "cpu",
            "profile_id": "portable-cpu",
            "requested_component_ids": ["paddleocr-cpu", "mineru-cpu"],
            "effective_component_ids": ["paddleocr-cpu", "mineru-cpu", "shared-base-cpu"],
            "requested_download_source_ids": ["pypi-tuna"],
            "effective_download_source_ids": ["pypi-tuna"],
            "source": {
              "backend_version": "2.8.2",
              "backend_source_sha": "0123456789abcdef0123456789abcdef01234567",
              "runtime_manifest_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "protocol_version": "2.8.2",
              "protocol_manifest_sha256": "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
            },
            "components": [
              {"component_id": "paddleocr-cpu", "action": "install", "dependency_state": "satisfied", "reason_codes": []},
              {"component_id": "mineru-cpu", "action": "install", "dependency_state": "satisfied", "reason_codes": []},
              {"component_id": "shared-base-cpu", "action": "retain", "dependency_state": "satisfied", "reason_codes": ["dependency_closure"]}
            ],
            "blockers": [],
            "cost": {"download_bytes": 157286400, "additional_disk_bytes": 524288000, "unknown_reason_codes": []}
          },
          "negotiated_capabilities": ["runtime.install-plan.v1"]
        }
        """;

    [Fact]
    public async Task PreviewsInstallPlanOnItsOwnWireEndpointAsync()
    {
        var handler = new FakeHandler(PlanJson);
        await using var client = new RuntimeHttpClient(
            new Uri("http://127.0.0.1:1"),
            "session-token",
            handler);

        RuntimeInstallPlanResponse response = await client.PreviewRuntimeInstallPlanAsync(
            new RuntimeInstallPlanRequest
            {
                RequiredCapabilities = new[] { "runtime.install-plan.v1" },
                Accelerator = RuntimeAccelerator.Cpu,
                InstallComponentIds = new[] { "paddleocr-cpu", "mineru-cpu" },
                DownloadSourceIds = new[] { "pypi-tuna" },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("/v2/runtime/install-plan", handler.Path);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("session-token", handler.AuthorizationParameter);
        Assert.Contains("\"required_capabilities\":[\"runtime.install-plan.v1\"]", handler.RequestBody);
        Assert.Contains("\"accelerator\":\"cpu\"", handler.RequestBody);

        Assert.Equal("plan-7f3a91c2e8d4", response.Plan.PlanId);
        Assert.Equal(RuntimeAccelerator.Cpu, response.Plan.Accelerator);
        Assert.Equal(3, response.Plan.Components.Count);
        Assert.Equal(RuntimeInstallPlanAction.Retain, response.Plan.Components[2].Action);
        Assert.Empty(response.Plan.Blockers);
        Assert.Equal(157286400L, response.Plan.Cost.DownloadBytes);
        Assert.Equal(new[] { "runtime.install-plan.v1" }, response.NegotiatedCapabilities);
    }

    [Fact]
    public async Task RejectsEmptyDownloadSourceSelectionLocallyAsync()
    {
        await using var client = new RuntimeHttpClient(
            new Uri("http://127.0.0.1:1"),
            "token",
            new FakeHandler(PlanJson));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PreviewRuntimeInstallPlanAsync(
                new RuntimeInstallPlanRequest
                {
                    RequiredCapabilities = new[] { "runtime.install-plan.v1" },
                    DownloadSourceIds = Array.Empty<string>(),
                },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConvertsStalePlanConflictToTypedExceptionAsync()
    {
        var handler = new FakeHandler(
            """
            {"schema_version":2,"instance_id":"sup-1","code":"RUNTIME_INSTALL_PLAN_STALE","message":"stale","category":"conflict","retryable":false,"detail":{"plan_id":"plan-7f3a91c2e8d4"},"job_id":null}
            """,
            HttpStatusCode.Conflict);
        await using var client = new RuntimeHttpClient(
            new Uri("http://127.0.0.1:1"),
            "token",
            handler);

        RuntimeClientException error = await Assert.ThrowsAsync<RuntimeClientException>(
            () => client.PreviewRuntimeInstallPlanAsync(
                new RuntimeInstallPlanRequest
                {
                    RequiredCapabilities = new[] { "runtime.install-plan.v1" },
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpV2ErrorCode.RuntimeInstallPlanStale, error.Code);
        Assert.False(error.Retryable);
        Assert.Equal("plan-7f3a91c2e8d4", error.Detail!["plan_id"].GetString());
    }

    [Fact]
    public async Task StartMaintenancePlanGuardsRunBeforeTheWireAsync()
    {
        await using var client = new RuntimeHttpClient(
            new Uri("http://127.0.0.1:1"),
            "token",
            new FakeHandler("{}"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StartRuntimeMaintenanceAsync(
                new RuntimeMaintenanceRequest
                {
                    Operation = RuntimeMaintenanceOperation.Inspect,
                    OperationId = "op-1",
                    PlanId = "plan-1",
                },
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StartRuntimeMaintenanceAsync(
                new RuntimeMaintenanceRequest
                {
                    Operation = RuntimeMaintenanceOperation.Ensure,
                    PlanId = "plan-1",
                },
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StartRuntimeMaintenanceAsync(
                new RuntimeMaintenanceRequest
                {
                    Operation = RuntimeMaintenanceOperation.Ensure,
                    OperationId = "op-1",
                    PlanId = "plan-1",
                    InstallComponentIds = new[] { "paddleocr-cpu" },
                },
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.StartRuntimeMaintenanceAsync(
                new RuntimeMaintenanceRequest
                {
                    Operation = RuntimeMaintenanceOperation.Ensure,
                    OperationId = "op-1",
                    PlanId = "plan-1",
                    ProfileId = "portable-cpu",
                },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CommandPlanGuardsRejectCancelAndSelectionMixAsync()
    {
        await using var client = new RuntimeHttpClient(
            new Uri("http://127.0.0.1:1"),
            "token",
            new FakeHandler("{}"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CommandRuntimeMaintenanceAsync(
                new RuntimeMaintenanceCommand
                {
                    CommandId = "command-1",
                    Command = RuntimeMaintenanceCommandKind.Cancel,
                    TargetOperationId = "op-1",
                    PlanId = "plan-1",
                },
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CommandRuntimeMaintenanceAsync(
                new RuntimeMaintenanceCommand
                {
                    CommandId = "command-2",
                    Command = RuntimeMaintenanceCommandKind.Retry,
                    TargetOperationId = "op-1",
                    NewOperationId = "op-2",
                    PlanId = "plan-1",
                    DownloadSourceIds = new[] { "pypi-tuna" },
                },
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("ensure")]
    [InlineData("retry")]
    public async Task OldRuntimeReceivesNoPlanRequest(string kind)
    {
        var handler = new OldRuntimeHandler();
        await using var client = new RuntimeHttpClient(new Uri("http://127.0.0.1:9"), "token", handler);
        var capabilities = new[] { "runtime.install-plan.v1" };
        var error = await Assert.ThrowsAsync<RuntimeClientException>(async () =>
        {
            if (kind == "preview")
                await client.PreviewRuntimeInstallPlanAsync(new RuntimeInstallPlanRequest
                { RequiredCapabilities = capabilities }, TestContext.Current.CancellationToken);
            else if (kind == "ensure")
                await client.StartRuntimeMaintenanceAsync(new RuntimeMaintenanceRequest
                { Operation = RuntimeMaintenanceOperation.Ensure, OperationId = "op", PlanId = "plan",
                  RequiredCapabilities = capabilities }, TestContext.Current.CancellationToken);
            else
                await client.CommandRuntimeMaintenanceAsync(new RuntimeMaintenanceCommand
                { Command = RuntimeMaintenanceCommandKind.Retry, CommandId = "cmd", TargetOperationId = "old",
                  NewOperationId = "new", PlanId = "plan", RequiredCapabilities = capabilities },
                    TestContext.Current.CancellationToken);
        });
        Assert.Equal(HttpV2ErrorCode.RuntimeCapabilityUnavailable, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("ensure")]
    [InlineData("retry")]
    public async Task MissingRequiredCapabilityIsRejectedBeforeAnyNetworkCall(string kind)
    {
        var handler = new OldRuntimeHandler();
        await using var client = new RuntimeHttpClient(new Uri("http://127.0.0.1:9"), "token", handler);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            if (kind == "preview")
                await client.PreviewRuntimeInstallPlanAsync(new RuntimeInstallPlanRequest
                { RequiredCapabilities = Array.Empty<string>() }, TestContext.Current.CancellationToken);
            else if (kind == "ensure")
                await client.StartRuntimeMaintenanceAsync(new RuntimeMaintenanceRequest
                { Operation = RuntimeMaintenanceOperation.Ensure, OperationId = "op", PlanId = "plan" },
                    TestContext.Current.CancellationToken);
            else
                await client.CommandRuntimeMaintenanceAsync(new RuntimeMaintenanceCommand
                { Command = RuntimeMaintenanceCommandKind.Retry, CommandId = "cmd", TargetOperationId = "old",
                  NewOperationId = "new", PlanId = "plan" }, TestContext.Current.CancellationToken);
        });
        Assert.Equal(0, handler.Calls);
    }

    private sealed class OldRuntimeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"capabilities\":[\"runtime.maintenance.v2\"]}") });
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public FakeHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public string? Path { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"capabilities\":[\"runtime.install-plan.v1\"]}"),
                };
            }
            Path = request.RequestUri?.AbsolutePath;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            };
        }
    }
}
