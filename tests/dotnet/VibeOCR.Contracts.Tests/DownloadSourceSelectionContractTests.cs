// Cross-language contract tests for the runtime.download-sources.v1 extension.
//
// They prove the handwritten HttpV2 mirror and the generated Wire/Host
// bindings agree with the Python-side golden payloads in
// runtime_contracts/golden/golden.json (see
// tests/contracts/v2/test_download_source_selection.py).
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;

namespace VibeOCR.Contracts.Tests;

public sealed class DownloadSourceSelectionContractTests
{
    private static readonly string V2Directory = FindV2Directory();

    [Fact]
    public void DownloadSourceCatalogGoldenRoundTripsThroughGeneratedWireBinding()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("download_source_catalog");

        var catalog = JsonSerializer.Deserialize<Wire.DownloadSourceCatalog>(fixture.GetRawText())!;
        Assert.Equal(4, catalog.Sources.Count);
        Assert.Equal("package_index", catalog.Sources[0].Kind);
        Assert.Equal("pypi-official", catalog.Sources[0].Id);
        Assert.Equal("https://pypi.org/simple", catalog.Sources[0].Endpoint);
        Assert.Equal("package_index", catalog.Sources[1].Kind);
        Assert.Equal("model_registry", catalog.Sources[2].Kind);
        Assert.Equal("huggingface", catalog.Sources[2].Id);
        Assert.Equal("https://huggingface.co", catalog.Sources[2].Endpoint);
        Assert.Equal("model_registry", catalog.Sources[3].Kind);
        Assert.Equal("modelscope", catalog.Sources[3].Id);
        Assert.Equal("https://www.modelscope.cn", catalog.Sources[3].Endpoint);

        AssertDeepRoundTrip(
            fixture,
            json => JsonSerializer.Deserialize<Wire.DownloadSourceCatalog>(json)!,
            value => JsonSerializer.Serialize(value, value.GetType()));
    }

    [Fact]
    public void LegacyModelRegistryCatalogRoundTripsThroughGeneratedWireBinding()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("legacy_download_source_catalog");

        var catalog = JsonSerializer.Deserialize<Wire.DownloadSourceCatalog>(fixture.GetRawText())!;
        Assert.Single(catalog.Sources);
        Assert.Equal("model_registry", catalog.Sources[0].Kind);
        Assert.Equal("legacy-models", catalog.Sources[0].Id);
        Assert.Equal("https://models.example.invalid", catalog.Sources[0].Endpoint);

        AssertDeepRoundTrip(
            fixture,
            json => JsonSerializer.Deserialize<Wire.DownloadSourceCatalog>(json)!,
            value => JsonSerializer.Serialize(value, value.GetType()));
    }

    [Fact]
    public void SettingsSelectionRoundTripsThroughHttpV2Mirror()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("download_source_selection");

        string[] selection = JsonSerializer.Deserialize<string[]>(fixture.GetRawText())!;
        var snapshot = new HttpV2.SettingsSnapshot
        {
            Residency = new SettingsResidency(),
            Extra = new Dictionary<string, JsonElement>(),
            DownloadSourceIds = selection,
        };

        var node = JsonNode.Parse(
            HttpV2Json.Serialize(snapshot, typeof(HttpV2.SettingsSnapshot)))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture.GetRawText()), node!["download_source_ids"]));

        var roundTrip = HttpV2Json.Deserialize<HttpV2.SettingsSnapshot>(node.ToJsonString())!;
        Assert.Equal(selection, roundTrip.DownloadSourceIds);
    }

    [Fact]
    public void OmittingDownloadSourceIdsKeepsLegacyWireShape()
    {
        const string legacyJson = """
            {"schema_version": 2, "residency": {"default_ttl_seconds": 300, "pipelines": []}, "extra": {}}
            """;
        var snapshot = HttpV2Json.Deserialize<HttpV2.SettingsSnapshot>(legacyJson)!;
        Assert.Null(snapshot.DownloadSourceIds);

        var roundTrip = JsonNode.Parse(
            HttpV2Json.Serialize(snapshot, typeof(HttpV2.SettingsSnapshot)))!;
        Assert.Null(roundTrip["download_source_ids"]);
    }

    [Fact]
    public void EmptySettingsSelectionNormalizesToGoldenOmission()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("settings_snapshot_empty_selection");
        var snapshot = new HttpV2.SettingsSnapshot
        {
            Residency = new SettingsResidency(),
            Extra = new Dictionary<string, JsonElement>(),
            DownloadSourceIds = [],
        };

        var payload = JsonNode.Parse(
            HttpV2Json.Serialize(snapshot, typeof(HttpV2.SettingsSnapshot)))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture.GetRawText()), payload));
        Assert.False(payload.AsObject().ContainsKey("download_source_ids"));
    }

    [Fact]
    public void GeneratedSourceKindsPreserveUnknownResponseValues()
    {
        Assert.Equal("package_index", JsonSerializer.Deserialize<string>("\"package_index\""));
        Assert.Equal("future_registry", JsonSerializer.Deserialize<string>("\"future_registry\""));
    }

    [Fact]
    public void CapabilityDescriptorCarriesOptionalSourceCatalog()
    {
        var catalog = new Wire.DownloadSourceCatalog
        {
            Sources =
            [
                new Wire.DownloadSourceDescriptor
                {
                    Kind = "package_index",
                    Id = "pypi-official",
                    Endpoint = "https://pypi.org/simple",
                },
            ],
        };
        var descriptor = new Wire.CapabilityDescriptor
        {
            Name = "runtime.download-sources.v1",
            Lifecycle = "active",
            IntroducedIn = "2.7.0",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            DownloadSourceCatalog = catalog,
        };

        string json = JsonSerializer.Serialize(descriptor);
        Assert.Contains("\"download_source_catalog\"", json);
        Assert.Contains("\"pypi-official\"", json);

        var parsed = JsonSerializer.Deserialize<Wire.CapabilityDescriptor>(json)!;
        Assert.Equal("pypi-official", parsed.DownloadSourceCatalog!.Sources[0].Id);

        string legacyJson = """
            {"name": "ocr.recognition.v2", "lifecycle": "active", "introduced_in": "2.0.0",
             "deprecated_in": null, "sunset_at": null, "replacement": null}
            """;
        var legacy = JsonSerializer.Deserialize<Wire.CapabilityDescriptor>(legacyJson)!;
        Assert.Null(legacy.DownloadSourceCatalog);
    }

    [Fact]
    public void GeneratedHostRequestCarriesOptionalSelection()
    {
        var request = new Host.RuntimeHostRequest
        {
            ProtocolVersion = 2,
            Operation = Host.RuntimeHostOperation.Ensure,
            ProductRoot = "C:/VibeOCR",
            ComponentLock = "C:/VibeOCR/component-lock.json",
            RuntimeManifest = "C:/VibeOCR/backend/runtime-manifest.json",
            DownloadSourceIds = ["pypi-tuna", "hf-mirror"],
        };

        string json = JsonSerializer.Serialize(request);
        Assert.Contains("\"download_source_ids\"", json);

        var parsed = JsonSerializer.Deserialize<Host.RuntimeHostRequest>(json)!;
        Assert.Equal(["pypi-tuna", "hf-mirror"], parsed.DownloadSourceIds);

        const string legacyJson = """
            {"protocol_version": 2, "operation": "ensure", "product_root": "C:/VibeOCR",
             "component_lock": "C:/VibeOCR/component-lock.json",
             "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json"}
            """;
        var legacy = JsonSerializer.Deserialize<Host.RuntimeHostRequest>(legacyJson)!;
        Assert.Null(legacy.DownloadSourceIds);
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
