// Cross-language contract tests for the runtime.component-selection.v1
// extension.
//
// They prove the handwritten HttpV2 mirror and the generated Wire/Host
// bindings agree with the Python-side golden payloads in
// runtime_contracts/golden/golden.json (see
// tests/contracts/v2/test_component_selection.py).
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.Contracts.Tests;

public sealed class ComponentSelectionContractTests
{
    private static readonly string V2Directory = FindV2Directory();

    [Fact]
    public void ComponentVariantCatalogGoldenRoundTripsThroughGeneratedWireBinding()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("component_variant_catalog");

        var catalog = JsonSerializer.Deserialize<Wire.ComponentVariantCatalog>(fixture.GetRawText())!;
        Assert.Equal(4, catalog.Variants.Count);
        Assert.Equal("paddleocr", catalog.Variants[0].FeatureId);
        Assert.Equal("cpu", catalog.Variants[0].Accelerator);
        Assert.Equal("paddleocr-cpu", catalog.Variants[0].ComponentId);
        Assert.Equal("nvidia_cuda", catalog.Variants[1].Accelerator);
        Assert.Equal("mineru", catalog.Variants[2].FeatureId);
        Assert.Equal("mineru-cuda", catalog.Variants[3].ComponentId);

        AssertDeepRoundTrip(
            fixture,
            json => JsonSerializer.Deserialize<Wire.ComponentVariantCatalog>(json)!,
            value => JsonSerializer.Serialize(value, value.GetType()));
    }

    [Fact]
    public void InstallSelectionRoundTripsThroughHttpV2Mirror()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("install_selection");

        var request = new RuntimeMaintenanceRequest
        {
            Operation = RuntimeMaintenanceOperation.Ensure,
            InstallComponentIds = JsonSerializer.Deserialize<string[]>(fixture.GetRawText())!,
        };

        var node = JsonNode.Parse(HttpV2Json.Serialize(request, typeof(RuntimeMaintenanceRequest)))!;
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(fixture.GetRawText()), node!["install_component_ids"]));
    }

    [Fact]
    public void OmittingInstallComponentIdsKeepsLegacyWireShape()
    {
        var request = new RuntimeMaintenanceRequest
        {
            Operation = RuntimeMaintenanceOperation.Ensure,
        };
        var node = JsonNode.Parse(HttpV2Json.Serialize(request, typeof(RuntimeMaintenanceRequest)))!;
        Assert.Null(node["install_component_ids"]);
        Assert.Equal("ensure", node!["operation"]!.GetValue<string>());

        var command = new RuntimeMaintenanceCommand
        {
            CommandId = "cmd-1",
            Command = RuntimeMaintenanceCommandKind.Retry,
            TargetOperationId = "op-1",
            NewOperationId = "op-2",
        };
        var commandNode = JsonNode.Parse(HttpV2Json.Serialize(command, typeof(RuntimeMaintenanceCommand)))!;
        Assert.Null(commandNode["install_component_ids"]);
    }

    [Fact]
    public void CapabilityDescriptorCarriesOptionalVariantCatalog()
    {
        var catalog = new Wire.ComponentVariantCatalog
        {
            Variants =
            [
                new Wire.ComponentVariantDescriptor
                {
                    FeatureId = "mineru",
                    Accelerator = "nvidia_cuda",
                    ComponentId = "mineru-cuda",
                },
            ],
        };
        var descriptor = new Wire.CapabilityDescriptor
        {
            Name = "runtime.component-selection.v1",
            Lifecycle = "active",
            IntroducedIn = "2.7.0",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            ComponentVariantCatalog = catalog,
        };

        string json = JsonSerializer.Serialize(descriptor);
        Assert.Contains("\"component_variant_catalog\"", json);
        Assert.Contains("\"mineru-cuda\"", json);

        var parsed = JsonSerializer.Deserialize<Wire.CapabilityDescriptor>(json)!;
        Assert.Equal("mineru-cuda", parsed.ComponentVariantCatalog!.Variants[0].ComponentId);

        string legacyJson = """
            {"name": "ocr.recognition.v2", "lifecycle": "active", "introduced_in": "2.0.0",
             "deprecated_in": null, "sunset_at": null, "replacement": null}
            """;
        var legacy = JsonSerializer.Deserialize<Wire.CapabilityDescriptor>(legacyJson)!;
        Assert.Null(legacy.ComponentVariantCatalog);
    }

    [Fact]
    public void GeneratedHostBindingTypesTheAcceleratorEnum()
    {
        var descriptor = new Host.ComponentVariantDescriptor
        {
            FeatureId = "paddleocr",
            Accelerator = Host.Accelerator.NvidiaCuda,
            ComponentId = "paddleocr-cuda",
        };
        string json = JsonSerializer.Serialize(descriptor);
        Assert.Contains("\"nvidia_cuda\"", json);

        var parsed = JsonSerializer.Deserialize<Host.ComponentVariantDescriptor>(json)!;
        Assert.Equal(Host.Accelerator.NvidiaCuda, parsed.Accelerator);
    }

    [Fact]
    public void GeneratedHostRequestCarriesOptionalInstallSelection()
    {
        var request = new Host.RuntimeHostRequest
        {
            ProtocolVersion = 2,
            Operation = Host.RuntimeHostOperation.Ensure,
            ProductRoot = "C:/VibeOCR",
            ComponentLock = "C:/VibeOCR/component-lock.json",
            RuntimeManifest = "C:/VibeOCR/backend/runtime-manifest.json",
            InstallComponentIds = ["paddleocr-cpu", "mineru-cpu"],
        };

        string json = JsonSerializer.Serialize(request);
        Assert.Contains("\"install_component_ids\"", json);

        var parsed = JsonSerializer.Deserialize<Host.RuntimeHostRequest>(json)!;
        Assert.Equal(["paddleocr-cpu", "mineru-cpu"], parsed.InstallComponentIds);

        const string legacyJson = """
            {"protocol_version": 2, "operation": "ensure", "product_root": "C:/VibeOCR",
             "component_lock": "C:/VibeOCR/component-lock.json",
             "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json"}
            """;
        var legacy = JsonSerializer.Deserialize<Host.RuntimeHostRequest>(legacyJson)!;
        Assert.Null(legacy.InstallComponentIds);
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
