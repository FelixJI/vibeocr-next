// Cross-language contract tests for the runtime.install-plan.v1 extension.
//
// They prove the handwritten HttpV2 mirror and the generated Wire/Host
// bindings agree with the Python-side golden payloads in
// runtime_contracts/golden/golden.json (see
// tests/contracts/v2/test_install_plan.py).
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.Contracts.Tests;

public sealed class InstallPlanContractTests
{
    private static readonly string V2Directory = FindV2Directory();

    [Fact]
    public void PlanRejectsDuplicateComponentIdsWithConflictingActions()
    {
        JsonObject plan = JsonNode.Parse(LoadGolden().RootElement.GetProperty("install_plan").GetRawText())!.AsObject();
        JsonArray components = plan["components"]!.AsArray();
        JsonNode duplicate = components[0]!.DeepClone();
        duplicate["action"] = "remove";
        components.Add(duplicate);
        Assert.Throws<JsonException>(() => HttpV2Json.Deserialize<RuntimeInstallPlan>(plan.ToJsonString()));
    }

    [Fact]
    public void LargeCostsRoundTripAcrossHandwrittenAndGeneratedBindings()
    {
        const string json = "{\"download_bytes\":3221225472,\"additional_disk_bytes\":4294967296,\"unknown_reason_codes\":[]}";
        var wire = JsonSerializer.Deserialize<Wire.RuntimeInstallPlanCost>(json)!;
        var host = JsonSerializer.Deserialize<Host.RuntimeInstallPlanCost>(json)!;
        var typed = HttpV2Json.Deserialize<RuntimeInstallPlanCost>(json)!;
        Assert.Equal(3221225472L, wire.DownloadBytes);
        Assert.Equal(4294967296L, host.AdditionalDiskBytes);
        Assert.Equal(typed.DownloadBytes, JsonSerializer.Deserialize<Host.RuntimeInstallPlanCost>(JsonSerializer.Serialize(host))!.DownloadBytes);
        Assert.Equal(typed.AdditionalDiskBytes, JsonSerializer.Deserialize<Wire.RuntimeInstallPlanCost>(JsonSerializer.Serialize(wire))!.AdditionalDiskBytes);
    }

    [Theory]
    [InlineData("plan_id")]
    [InlineData("profile_id")]
    [InlineData("effective_component_ids")]
    [InlineData("effective_download_source_ids")]
    [InlineData("components")]
    [InlineData("blockers")]
    public void KnownPlanFieldsCannotBeMissingOrNull(string field)
    {
        JsonObject plan = JsonNode.Parse(LoadGolden().RootElement.GetProperty("install_plan").GetRawText())!.AsObject();
        plan[field] = null;
        Assert.Throws<JsonException>(() => HttpV2Json.Deserialize<RuntimeInstallPlan>(plan.ToJsonString()));
        plan.Remove(field);
        Assert.Throws<JsonException>(() => HttpV2Json.Deserialize<RuntimeInstallPlan>(plan.ToJsonString()));
    }

    [Theory]
    [InlineData("{\"download_bytes\":null,\"additional_disk_bytes\":0,\"unknown_reason_codes\":[]}")]
    [InlineData("{\"download_bytes\":null,\"additional_disk_bytes\":0}")]
    [InlineData("{\"download_bytes\":-1,\"additional_disk_bytes\":0,\"unknown_reason_codes\":[]}")]
    public void UnknownOrInvalidCostCannotLookLikeKnownZero(string json)
    {
        Assert.Throws<JsonException>(() => HttpV2Json.Deserialize<RuntimeInstallPlanCost>(json));
    }

    [Fact]
    public void PlanResponseStillAcceptsUnknownOptionalFields()
    {
        JsonObject plan = JsonNode.Parse(LoadGolden().RootElement.GetProperty("install_plan").GetRawText())!.AsObject();
        plan["future_optional"] = true;
        var parsed = HttpV2Json.Deserialize<RuntimeInstallPlan>(plan.ToJsonString())!;
        Assert.Equal("plan-7f3a91c2e8d4", parsed.PlanId);
    }

    [Theory]
    [InlineData("\"pip_args\":[]")]
    [InlineData("\"accelerator\":0")]
    [InlineData("\"accelerator\":null")]
    [InlineData("\"install_component_ids\":null")]
    [InlineData("\"download_source_ids\":null")]
    [InlineData("\"download_source_ids\":[]")]
    [InlineData("\"install_component_ids\":[\"engine\",\"engine\"]")]
    public void PreviewRequestRejectsInvalidWireSelections(string field)
    {
        string json = "{\"required_capabilities\":[\"runtime.install-plan.v1\"]," + field + "}";
        Assert.Throws<JsonException>(() => HttpV2Json.Deserialize<RuntimeInstallPlanRequest>(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"required_capabilities\":null}")]
    [InlineData("{\"required_capabilities\":[]}")]
    [InlineData("{\"required_capabilities\":[\"runtime.maintenance.v2\"]}")]
    public void PreviewRequestRequiresExplicitCapabilityOnRead(string json)
    {
        Assert.Throws<JsonException>(() => HttpV2Json.Deserialize<RuntimeInstallPlanRequest>(json));
    }

    [Fact]
    public void PreviewRequestKeepsOmissionAndEmptyScopeDistinctOnRead()
    {
        var omitted = HttpV2Json.Deserialize<RuntimeInstallPlanRequest>(
            "{\"required_capabilities\":[\"runtime.install-plan.v1\"]}")!;
        var empty = HttpV2Json.Deserialize<RuntimeInstallPlanRequest>(
            "{\"required_capabilities\":[\"runtime.install-plan.v1\"],\"install_component_ids\":[]}")!;
        Assert.Null(omitted.InstallComponentIds);
        Assert.Empty(empty.InstallComponentIds!);
        Assert.False(JsonNode.Parse(HttpV2Json.Serialize(omitted))!.AsObject().ContainsKey("install_component_ids"));
        Assert.Empty(JsonNode.Parse(HttpV2Json.Serialize(empty))!["install_component_ids"]!.AsArray());
    }

    [Fact]
    public void InstallPlanRequestGoldenRoundTripsThroughHandwrittenMirror()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("install_plan_request");

        var request = HttpV2Json.Deserialize<RuntimeInstallPlanRequest>(fixture.GetRawText())!;
        Assert.Equal(RuntimeAccelerator.Cpu, request.Accelerator);
        Assert.Equal(new[] { "paddleocr-cpu", "mineru-cpu" }, request.InstallComponentIds);
        Assert.Equal(new[] { "pypi-tuna" }, request.DownloadSourceIds);
        Assert.Equal(new[] { "runtime.install-plan.v1" }, request.RequiredCapabilities);

        AssertDeepRoundTrip(
            fixture,
            json => HttpV2Json.Deserialize<RuntimeInstallPlanRequest>(json)!,
            value => HttpV2Json.Serialize(value, value.GetType()));
    }

    [Fact]
    public void OmittedSelectionSerializesWithoutWireFields()
    {
        var request = new RuntimeInstallPlanRequest
        {
            RequiredCapabilities = new[] { "runtime.install-plan.v1" },
        };
        string json = HttpV2Json.Serialize(request);
        JsonNode node = JsonNode.Parse(json)!;
        Assert.Null(node["accelerator"]);
        Assert.Null(node["install_component_ids"]);
        Assert.Null(node["download_source_ids"]);
        Assert.NotNull(node["required_capabilities"]);
    }

    [Fact]
    public void InstallPlanGoldenRoundTripsThroughHandwrittenMirror()
    {
        foreach (string key in new[]
                 {
                     "install_plan",
                     "install_plan_omitted_selection",
                     "install_plan_gpu_empty_selection",
                     "install_plan_blocked",
                 })
        {
            JsonElement fixture = LoadGolden().RootElement.GetProperty(key);
            var plan = HttpV2Json.Deserialize<RuntimeInstallPlan>(fixture.GetRawText())!;
            Assert.Equal(fixture.GetProperty("plan_id").GetString(), plan.PlanId);

            AssertDeepRoundTrip(
                fixture,
                json => HttpV2Json.Deserialize<RuntimeInstallPlan>(json)!,
                value => HttpV2Json.Serialize(value, value.GetType()));
        }
    }

    [Fact]
    public void NullableRequestEchoKeepsNullDistinctFromEmpty()
    {
        JsonElement omitted = LoadGolden().RootElement.GetProperty("install_plan_omitted_selection");
        var plan = HttpV2Json.Deserialize<RuntimeInstallPlan>(omitted.GetRawText())!;
        Assert.Null(plan.RequestedComponentIds);
        Assert.Null(plan.RequestedDownloadSourceIds);

        JsonElement gpu = LoadGolden().RootElement.GetProperty("install_plan_gpu_empty_selection");
        var empty = HttpV2Json.Deserialize<RuntimeInstallPlan>(gpu.GetRawText())!;
        Assert.NotNull(empty.RequestedComponentIds);
        Assert.Empty(empty.RequestedComponentIds!);
        Assert.Null(empty.RequestedDownloadSourceIds);
    }

    [Fact]
    public void BlockedPlanKeepsActionsBlockersAndUnknownCostHonest()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("install_plan_blocked");
        var plan = HttpV2Json.Deserialize<RuntimeInstallPlan>(fixture.GetRawText())!;

        Assert.Equal(RuntimeInstallPlanAction.Replace, plan.Components[0].Action);
        Assert.Equal(RuntimeInstallPlanAction.Install, plan.Components[1].Action);
        Assert.Equal("insufficient_disk_space", plan.Blockers[0].Code);
        Assert.Equal("mineru-cpu", plan.Blockers[0].ComponentId);
        Assert.Equal("free_space", plan.Blockers[0].NextAction);
        Assert.Null(plan.Cost.DownloadBytes);
        Assert.NotNull(plan.Cost.AdditionalDiskBytes);
        Assert.Equal(new[] { "model_registry_source_size_unknown" }, plan.Cost.UnknownReasonCodes);
    }

    [Fact]
    public void RemovalRowsAreAffectedOldComponentsOutsideTheEffectiveClosure()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("install_plan_omitted_selection");
        var plan = HttpV2Json.Deserialize<RuntimeInstallPlan>(fixture.GetRawText())!;

        RuntimeInstallPlanComponent removal = plan.Components.Single(
            component => component.Action is RuntimeInstallPlanAction.Remove);
        Assert.Equal("mineru-cpu", removal.ComponentId);
        Assert.DoesNotContain(removal.ComponentId, plan.EffectiveComponentIds!);
        Assert.Equal(new[] { "not_in_selected_scope" }, removal.ReasonCodes);
    }

    [Fact]
    public void GeneratedWireAndHostBindingsExposeThePlanContract()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("install_plan");

        var wirePlan = JsonSerializer.Deserialize<Wire.RuntimeInstallPlan>(fixture.GetRawText())!;
        Assert.Equal("plan-7f3a91c2e8d4", wirePlan.PlanId);
        Assert.Equal("cpu", wirePlan.Accelerator);
        Assert.NotNull(wirePlan.RequestedComponentIds);
        Assert.Equal(3, wirePlan.EffectiveComponentIds!.Count);

        string responseJson = JsonSerializer.Serialize(
            new Wire.RuntimeInstallPlanResponse
            {
                SchemaVersion = 2,
                Plan = wirePlan,
                NegotiatedCapabilities = new[] { "runtime.install-plan.v1" },
            });
        JsonNode response = JsonNode.Parse(responseJson)!;
        Assert.Equal(2, (int)response["schema_version"]!);
        Assert.NotNull(response["plan"]);

        var hostPlan = JsonSerializer.Deserialize<Host.RuntimeInstallPlan>(fixture.GetRawText())!;
        Assert.Equal(wirePlan.PlanId, hostPlan.PlanId);
        Assert.Equal(wirePlan.ExpectedComponentIdsCount(), hostPlan.ExpectedComponentIdsCount());
    }

    [Fact]
    public void GeneratedHostTypesCarryInstallPlanRequestAndResponseKinds()
    {
        string generated = File.ReadAllText(Path.Combine(
            V2Directory, "generated", "RuntimeHostWireTypes.g.cs"));

        Assert.Contains("public sealed record RuntimeInstallPlanRequest", generated);
        Assert.Contains("\"request_kind\"", generated);
        Assert.Contains("public sealed record RuntimeInstallPlanResponse", generated);
        Assert.Contains("\"response_kind\"", generated);
        Assert.Contains("public sealed record RuntimeInstallPlanCost", generated);

        // The request/response kind consts live in the authoritative schema;
        // the legacy Host operation enum must stay closed without them.
        using JsonDocument host = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(V2Directory, "runtime-host.schema.json")));
        JsonElement operation = host.RootElement.GetProperty("$defs")
            .GetProperty("RuntimeHostOperation");
        Assert.Equal(
            new[] { "inspect", "ensure", "repair" },
            operation.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void MaintenanceMirrorCarriesPlanIdWithMirrorSemantics()
    {
        var request = new RuntimeMaintenanceRequest
        {
            Operation = RuntimeMaintenanceOperation.Ensure,
            OperationId = "op-confirm-1",
            PlanId = "plan-7f3a91c2e8d4",
            RequiredCapabilities = new[] { "runtime.install-plan.v1" },
        };
        JsonNode node = JsonNode.Parse(HttpV2Json.Serialize(request))!;
        Assert.Equal("plan-7f3a91c2e8d4", (string?)node["plan_id"]);

        var legacy = new RuntimeMaintenanceRequest
        {
            Operation = RuntimeMaintenanceOperation.Ensure,
        };
        Assert.Null(JsonNode.Parse(HttpV2Json.Serialize(legacy))!["plan_id"]);

        var retry = new RuntimeMaintenanceCommand
        {
            CommandId = "command-1",
            Command = RuntimeMaintenanceCommandKind.Retry,
            TargetOperationId = "op-confirm-1",
            NewOperationId = "op-confirm-2",
            PlanId = "plan-fresh-1",
            RequiredCapabilities = new[] { "runtime.install-plan.v1" },
        };
        node = JsonNode.Parse(HttpV2Json.Serialize(retry))!;
        Assert.Equal("plan-fresh-1", (string?)node["plan_id"]);

        var status = new RuntimeMaintenanceStatus
        {
            OperationId = "op-confirm-1",
            Sequence = 1,
            Operation = RuntimeMaintenanceOperation.Ensure,
            OperationState = RuntimeOperationState.Queued,
            Phase = RuntimeMaintenancePhase.ValidateBinding,
            ProfileId = "portable-cpu",
            UpdatedAt = "2026-09-19T12:00:00Z",
            PlanId = "plan-7f3a91c2e8d4",
        };
        node = JsonNode.Parse(HttpV2Json.Serialize(status))!;
        Assert.Equal("plan-7f3a91c2e8d4", (string?)node["plan_id"]);
    }

    [Fact]
    public void InstallPlanErrorsAre409ConflictsWithCanonicalHostMapping()
    {
        using JsonDocument registry = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(V2Directory, "errors.json")));
        JsonElement[] entries = registry.RootElement.GetProperty("codes").EnumerateArray()
            .Where(entry => entry.GetProperty("code").GetString()!.StartsWith("RUNTIME_INSTALL_PLAN_"))
            .ToArray();

        Assert.Equal(2, entries.Length);
        foreach (JsonElement entry in entries)
        {
            Assert.Equal("conflict", entry.GetProperty("category").GetString());
            Assert.Equal(409, entry.GetProperty("http_status").GetInt32());
            Assert.False(entry.GetProperty("retryable").GetBoolean());
            // Host outer code stays invalid_request; the canonical code rides
            // error.canonical_code on the Host failure envelope.
            Assert.Equal("invalid_request", entry.GetProperty("runtime_host_code").GetString());
        }

        Assert.Equal(
            "RUNTIME_INSTALL_PLAN_STALE",
            WireName(HttpV2ErrorCode.RuntimeInstallPlanStale));
        Assert.Equal(
            "RUNTIME_INSTALL_PLAN_BLOCKED",
            WireName(HttpV2ErrorCode.RuntimeInstallPlanBlocked));
    }

    [Fact]
    public void StalePlanErrorGoldenRoundTrips()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("error_install_plan_stale");

        var payload = HttpV2Json.Deserialize<HttpV2ErrorPayload>(fixture.GetRawText())!;
        Assert.Equal(HttpV2ErrorCode.RuntimeInstallPlanStale, payload.Code);
        Assert.Equal(ErrorCategory.Conflict, payload.Category);
        Assert.False(payload.Retryable);

        AssertDeepRoundTrip(
            fixture,
            json => HttpV2Json.Deserialize<HttpV2ErrorPayload>(json)!,
            value => HttpV2Json.Serialize(value, value.GetType()));
    }

    private static string WireName<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var member = typeof(TEnum).GetMember(value.ToString()!)[0];
        return member.GetCustomAttributes(false)
            .OfType<JsonStringEnumMemberNameAttribute>()
            .Single()
            .Name;
    }

    private static JsonDocument LoadGolden()
    {
        string path = Path.Combine(V2Directory, "golden", "golden.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindV2Directory()
    {
        foreach (string? seed in new[]
                 {
                     Environment.GetEnvironmentVariable("VIBEOCR_REPOSITORY_ROOT"),
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory,
                 })
        {
            DirectoryInfo? directory = string.IsNullOrWhiteSpace(seed) ? null : new(seed);
            while (directory is not null)
            {
                string candidate = Path.Combine(
                    directory.FullName,
                    "contracts",
                    "runtime",
                    "python",
                    "vibeocr",
                    "runtime_contracts");
                if (File.Exists(Path.Combine(candidate, "errors.json")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate vibeocr/runtime_contracts from test output.");
    }

    private static void AssertDeepRoundTrip(
        JsonElement expected,
        Func<string, object> deserialize,
        Func<object, string> serialize)
    {
        object value = deserialize(expected.GetRawText());
        JsonNode expectedNode = JsonNode.Parse(expected.GetRawText())!;
        JsonNode actualNode = JsonNode.Parse(serialize(value))!;
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode),
            $"round-trip mismatch for {value.GetType().Name}:\n" +
            $"expected: {expectedNode}\nactual:   {actualNode}");
    }
}

file static class WirePlanExtensions
{
    // Both generated bindings must agree on the effective closure size; a tiny
    // local helper avoids duplicating the count expression at each call site.
    internal static int ExpectedComponentIdsCount(this Wire.RuntimeInstallPlan plan) =>
        plan.EffectiveComponentIds?.Count ?? 0;

    internal static int ExpectedComponentIdsCount(this Host.RuntimeInstallPlan plan) =>
        plan.EffectiveComponentIds?.Count ?? 0;
}
