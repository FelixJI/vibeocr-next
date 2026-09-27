// Cross-language contract tests for the ocr.engine-selection.v1 extension.
//
// They prove the handwritten HttpV2 mirror and the generated Wire bindings
// agree with the Python-side golden payloads in runtime_contracts/golden/
// golden.json (see tests/contracts/v2/test_ocr_engine_selection.py).
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.Contracts.Tests;

public sealed class OcrEngineSelectionContractTests
{
    private static readonly string V2Directory = FindV2Directory();

    [Fact]
    public void PipelineSelectionEngineGoldenRoundTripsThroughHttpV2Mirror()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("pipeline_selection_engine");

        var selection = HttpV2Json.Deserialize<HttpV2.PipelineSelection>(fixture.GetRawText())!;
        Assert.Equal(OcrEngine.Windows, selection.Engine);
        Assert.Equal("OCR", selection.PipelineId);
        AssertDeepRoundTrip(
            fixture,
            json => HttpV2Json.Deserialize<HttpV2.PipelineSelection>(json)!,
            value => HttpV2Json.Serialize(value, value.GetType()));
    }

    [Fact]
    public void OmittingEngineKeepsLegacyWireShape()
    {
        const string legacyJson = """
            {"pipeline_id": "OCR", "options_version": 1, "options": {}}
            """;
        var selection = HttpV2Json.Deserialize<HttpV2.PipelineSelection>(legacyJson)!;
        Assert.Null(selection.Engine);

        var roundTrip = JsonNode.Parse(
            HttpV2Json.Serialize(selection, typeof(HttpV2.PipelineSelection)))!;
        Assert.Null(roundTrip["engine"]);
    }

    [Fact]
    public void OcrEngineEnumUsesPinnedWireStrings()
    {
        Assert.Equal("\"windows\"", JsonSerializer.Serialize(OcrEngine.Windows));
        Assert.Equal(
            OcrEngine.RapidOcr,
            DeserializeEngine("\"rapidocr\""));
        Assert.Equal(
            OcrEngine.PaddleOcr,
            DeserializeEngine("\"paddleocr\""));
        Assert.Throws<JsonException>(
            () => DeserializeEngine("\"rapid-ocr\""));
    }

    private static OcrEngine? DeserializeEngine(string json) =>
        JsonSerializer.Deserialize<OcrEngine>(json, HttpV2JsonContext.Default.Options);

    [Fact]
    public void OcrEngineCatalogGoldenRoundTripsThroughGeneratedWireBinding()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("ocr_engine_catalog");

        var catalog = JsonSerializer.Deserialize<OcrEngineCatalog>(fixture.GetRawText())!;
        Assert.Equal(3, catalog.Engines.Count);
        Assert.Equal(OcrEngineId.Rapidocr, catalog.Engines[0].Id);
        Assert.Equal(OcrEngineAvailability.Ready, catalog.Engines[0].Availability);
        Assert.Equal(
            OcrEngineAvailability.PreparationRequired,
            catalog.Engines[1].Availability);
        Assert.Equal("winrt-ocr", catalog.Engines[1].RequiredComponent);
        Assert.Equal(OcrEngineAvailability.Unavailable, catalog.Engines[2].Availability);
        Assert.Null(catalog.Engines[2].RequiredComponent);

        AssertDeepRoundTrip(
            fixture,
            json => JsonSerializer.Deserialize<OcrEngineCatalog>(json)!,
            value => JsonSerializer.Serialize(value, value.GetType()));
    }

    [Fact]
    public void GeneratedEngineEnumsRejectNonWireValues()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<OcrEngineId>("\"rapid-ocr\""));
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<OcrEngineId>("0"));
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<OcrEngineAvailability>("\"partial\""));
    }

    [Fact]
    public void CapabilityDescriptorCarriesOptionalEngineCatalog()
    {
        var catalog = new OcrEngineCatalog
        {
            Engines =
            [
                new OcrEngineDescriptor
                {
                    Id = OcrEngineId.Rapidocr,
                    Availability = OcrEngineAvailability.Ready,
                    IncludedInBase = true,
                    ReasonCode = null,
                    RequiredComponent = null,
                },
            ],
        };
        var descriptor = new CapabilityDescriptor
        {
            Name = "ocr.engine-selection.v1",
            Lifecycle = "active",
            IntroducedIn = "2.6.0",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            OcrEngineCatalog = catalog,
        };

        string json = JsonSerializer.Serialize(descriptor);
        Assert.Contains("\"ocr_engine_catalog\"", json);
        Assert.Contains("\"rapidocr\"", json);

        var parsed = JsonSerializer.Deserialize<CapabilityDescriptor>(json)!;
        Assert.Equal(OcrEngineId.Rapidocr, parsed.OcrEngineCatalog!.Engines[0].Id);

        string legacyJson = """
            {"name": "ocr.recognition.v2", "lifecycle": "active", "introduced_in": "2.0.0",
             "deprecated_in": null, "sunset_at": null, "replacement": null}
            """;
        var legacy = JsonSerializer.Deserialize<CapabilityDescriptor>(legacyJson)!;
        Assert.Null(legacy.OcrEngineCatalog);
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
