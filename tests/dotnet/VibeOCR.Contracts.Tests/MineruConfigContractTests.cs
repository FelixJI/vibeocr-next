// Cross-language contract tests for the ocr.mineru-config.v1 extension.
//
// They prove the handwritten HttpV2 mirror and the generated Wire bindings
// agree with the Python-side golden payloads in runtime_contracts/golden/
// golden.json (see tests/contracts/v2/test_mineru_config.py).
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Runtime.Contracts.Generated;
using WireCapabilityDescriptor =
    VibeOCR.Runtime.Contracts.Generated.Wire.CapabilityDescriptor;
using WireMineruConfigCatalog =
    VibeOCR.Runtime.Contracts.Generated.Wire.MineruConfigCatalog;
using WireMineruTierAvailability =
    VibeOCR.Runtime.Contracts.Generated.Wire.MineruTierAvailability;
using WireMineruTierDescriptor =
    VibeOCR.Runtime.Contracts.Generated.Wire.MineruTierDescriptor;
using WireMineruTierId =
    VibeOCR.Runtime.Contracts.Generated.Wire.MineruTierId;
using Xunit;

namespace VibeOCR.Contracts.Tests;

public sealed class MineruConfigContractTests
{
    private static readonly string V2Directory = FindV2Directory();

    [Fact]
    public void PipelineSelectionMineruGoldenRoundTripsThroughHttpV2Mirror()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("pipeline_selection_mineru");

        var selection = HttpV2Json.Deserialize<HttpV2.PipelineSelection>(fixture.GetRawText())!;
        Assert.Equal("MinerU", selection.PipelineId);
        Assert.Equal(MineruTier.Basic, selection.Mineru!.Tier);
        Assert.Equal(MineruOcrMode.Auto, selection.Mineru.OcrMode);
        Assert.Equal("all", selection.Mineru.PageRange);
        Assert.Equal("ch", selection.Mineru.Language);
        AssertDeepRoundTrip(
            fixture,
            json => HttpV2Json.Deserialize<HttpV2.PipelineSelection>(json)!,
            value => HttpV2Json.Serialize(value, value.GetType()));
    }

    [Fact]
    public void OmittingMineruKeepsLegacyWireShape()
    {
        const string legacyJson = """
            {"pipeline_id": "MinerU", "options_version": 1,
             "options": {"backend": "hybrid-engine", "effort": "medium"}}
            """;
        var selection = HttpV2Json.Deserialize<HttpV2.PipelineSelection>(legacyJson)!;
        Assert.Null(selection.Mineru);
        Assert.Equal(2, selection.Options.Count);

        var roundTrip = JsonNode.Parse(
            HttpV2Json.Serialize(selection, typeof(HttpV2.PipelineSelection)))!;
        Assert.Null(roundTrip["mineru"]);
        Assert.Equal("hybrid-engine", roundTrip["options"]!["backend"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("999")]
    public void MineruRequestEnumsRejectNumericJson(string json)
    {
        Assert.Throws<JsonException>(() => DeserializeTier(json));
        Assert.Throws<JsonException>(() => DeserializeMode(json));
    }

    [Fact]
    public void OmittedMineruFieldsDeserializeToProtocolDefaults()
    {
        var config = HttpV2Json.Deserialize<MineruConfig>("""{"tier":"basic"}""")!;
        Assert.Equal(MineruOcrMode.Auto, config.OcrMode);
        Assert.Equal("all", config.PageRange);
        Assert.Equal("ch", config.Language);
    }

    [Theory]
    [InlineData("\" basic \"")]
    [InlineData("\"basic,standard\"")]
    public void MineruTierRejectsNonCanonicalStrings(string json)
    {
        Assert.Throws<JsonException>(() => DeserializeTier(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WireMineruTierId>(json));
    }

    [Theory]
    [InlineData("\" txt \"")]
    [InlineData("\"auto,ocr\"")]
    public void MineruOcrModeRejectsNonCanonicalStrings(string json)
    {
        Assert.Throws<JsonException>(() => DeserializeMode(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<
            VibeOCR.Runtime.Contracts.Generated.Wire.MineruOcrMode>(json));
    }

    [Fact]
    public void MineruTierRemainsRequiredOnDeserialization()
    {
        Assert.Throws<JsonException>(() => HttpV2Json.Deserialize<MineruConfig>("{}"));
    }

    [Fact]
    public void MineruConfigRejectsUnknownRequestFields()
    {
        Assert.Throws<JsonException>(() => HttpV2Json.Deserialize<MineruConfig>(
            """{"tier":"basic","extra":1}"""));
    }

    [Fact]
    public void MineruDefaultsSerializeTheEffectiveValues()
    {
        var selection = new HttpV2.PipelineSelection
        {
            PipelineId = "MinerU",
            Mineru = new MineruConfig { Tier = MineruTier.Standard },
        };
        var node = JsonNode.Parse(
            HttpV2Json.Serialize(selection, typeof(HttpV2.PipelineSelection)))!;
        Assert.Equal("standard", node["mineru"]!["tier"]!.GetValue<string>());
        Assert.Equal("auto", node["mineru"]!["ocr_mode"]!.GetValue<string>());
        Assert.Equal("all", node["mineru"]!["page_range"]!.GetValue<string>());
        Assert.Equal("ch", node["mineru"]!["language"]!.GetValue<string>());
    }

    [Fact]
    public void MineruEnumsUsePinnedWireStrings()
    {
        Assert.Equal("\"basic\"", JsonSerializer.Serialize(MineruTier.Basic));
        Assert.Equal(MineruTier.Flash, DeserializeTier("\"flash\""));
        Assert.Equal(MineruTier.Standard, DeserializeTier("\"standard\""));
        Assert.Equal(MineruTier.Advanced, DeserializeTier("\"advanced\""));
        Assert.Throws<JsonException>(() => DeserializeTier("\"auto\""));
        Assert.Equal(MineruOcrMode.Txt, DeserializeMode("\"txt\""));
        Assert.Equal(MineruOcrMode.Ocr, DeserializeMode("\"ocr\""));
        Assert.Throws<JsonException>(() => DeserializeMode("\"text\""));
    }

    private static MineruTier? DeserializeTier(string json) =>
        JsonSerializer.Deserialize<MineruTier>(json, HttpV2JsonContext.Default.Options);

    private static MineruOcrMode? DeserializeMode(string json) =>
        JsonSerializer.Deserialize<MineruOcrMode>(json, HttpV2JsonContext.Default.Options);

    [Fact]
    public void MineruConfigCatalogGoldenRoundTripsThroughGeneratedWireBinding()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("mineru_config_catalog");

        var catalog = JsonSerializer.Deserialize<WireMineruConfigCatalog>(fixture.GetRawText())!;
        Assert.Equal(4, catalog.Tiers.Count);
        Assert.Equal(WireMineruTierId.Basic, catalog.DefaultTier);
        Assert.Equal(WireMineruTierId.Flash, catalog.Tiers[0].Id);
        Assert.Equal(WireMineruTierAvailability.Ready, catalog.Tiers[0].Availability);
        Assert.Null(catalog.Tiers[0].ReasonCode);
        Assert.Equal(
            WireMineruTierAvailability.PreparationRequired,
            catalog.Tiers[2].Availability);
        Assert.Equal(
            "MINERU_TIER_PREPARATION_REQUIRED",
            catalog.Tiers[2].ReasonCode);
        Assert.Equal(
            WireMineruTierAvailability.Unavailable,
            catalog.Tiers[3].Availability);
        Assert.Equal(2, catalog.Languages.Count);

        AssertDeepRoundTrip(
            fixture,
            json => JsonSerializer.Deserialize<WireMineruConfigCatalog>(json)!,
            value => JsonSerializer.Serialize(value, value.GetType()));
    }

    [Fact]
    public void GeneratedWireBindingsRejectNonWireValues()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<WireMineruTierId>("\"ultra\""));
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<WireMineruTierId>("\"0\""));
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<MineruOcrMode>("\"text\""));
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<WireMineruTierAvailability>("\"partial\""));
    }

    [Fact]
    public void GeneratedPipelineSelectionExposesOptionalMineruBlock()
    {
        string wirePath = Path.Combine(
            V2Directory, "generated", "RuntimeWireTypes.g.cs");
        string csharpWire = File.ReadAllText(wirePath);
        Assert.Contains("public sealed record PipelineSelection", csharpWire);
        string pipelineRecord = ExtractRecord(csharpWire, "PipelineSelection");
        Assert.Contains("[JsonPropertyName(\"mineru\")]", pipelineRecord);
        Assert.Contains("public MineruConfig? Mineru { get; init; }", pipelineRecord);

        string configRecord = ExtractRecord(csharpWire, "MineruConfig");
        Assert.Contains("public required MineruTierId Tier { get; init; }", configRecord);
        Assert.Contains("public MineruOcrMode? OcrMode { get; init; }", configRecord);

        int enumStart = csharpWire.IndexOf(
            "public enum MineruTierId", StringComparison.Ordinal);
        Assert.True(enumStart >= 0, "public enum MineruTierId is missing");
        string enumBlock = csharpWire[
            enumStart..csharpWire.IndexOf("}", enumStart, StringComparison.Ordinal)];
        foreach (string value in new[] { "flash", "basic", "standard", "advanced" })
        {
            Assert.Contains($"\"{value}\"", enumBlock);
        }
    }

    [Fact]
    public void CapabilityDescriptorCarriesOptionalMineruConfigCatalog()
    {
        var catalog = new WireMineruConfigCatalog
        {
            DefaultTier = WireMineruTierId.Basic,
            Tiers =
            [
                new WireMineruTierDescriptor
                {
                    Id = WireMineruTierId.Basic,
                    Availability = WireMineruTierAvailability.Ready,
                    ReasonCode = null,
                },
            ],
            Languages = ["ch", "en"],
        };
        var descriptor = new WireCapabilityDescriptor
        {
            Name = "ocr.mineru-config.v1",
            Lifecycle = "active",
            IntroducedIn = "2.8.1",
            DeprecatedIn = null,
            SunsetAt = null,
            Replacement = null,
            MineruConfigCatalog = catalog,
        };

        string json = JsonSerializer.Serialize(descriptor);
        Assert.Contains("\"mineru_config_catalog\"", json);
        Assert.Contains("\"basic\"", json);

        var parsed = JsonSerializer.Deserialize<WireCapabilityDescriptor>(json)!;
        Assert.Equal(
            WireMineruTierId.Basic,
            parsed.MineruConfigCatalog!.Tiers[0].Id);

        string legacyJson = """
            {"name": "ocr.recognition.v2", "lifecycle": "active", "introduced_in": "2.0.0",
             "deprecated_in": null, "sunset_at": null, "replacement": null}
            """;
        var legacy = JsonSerializer.Deserialize<WireCapabilityDescriptor>(legacyJson)!;
        Assert.Null(legacy.MineruConfigCatalog);
    }

    [Fact]
    public void MineruCapabilityAndErrorCodesAreRegisteredInGeneratedBindings()
    {
        Assert.Equal("ocr.mineru-config.v1", RuntimeProtocol.OCR_MINERU_CONFIG_V1);
        Assert.Contains(
            RuntimeProtocol.OCR_MINERU_CONFIG_V1,
            RuntimeProtocol.AllCapabilities);

        foreach ((HttpV2ErrorCode code, string wire) in new[]
                 {
                     (HttpV2ErrorCode.MineruConfigUnavailable, "MINERU_CONFIG_UNAVAILABLE"),
                     (HttpV2ErrorCode.MineruConfigMigrationRequired, "MINERU_CONFIG_MIGRATION_REQUIRED"),
                     (HttpV2ErrorCode.MineruTierUnavailable, "MINERU_TIER_UNAVAILABLE"),
                     (HttpV2ErrorCode.MineruTierPreparationRequired, "MINERU_TIER_PREPARATION_REQUIRED"),
                 })
        {
            Assert.Equal(
                $"\"{wire}\"",
                JsonSerializer.Serialize(code, HttpV2JsonContext.Default.Options));
        }

        string csharpProtocol = File.ReadAllText(
            Path.Combine(V2Directory, "generated", "RuntimeProtocol.g.cs"));
        Assert.Contains("MINERU_CONFIG_UNAVAILABLE", csharpProtocol);
        Assert.Contains("MINERU_CONFIG_MIGRATION_REQUIRED", csharpProtocol);
        Assert.Contains("MINERU_TIER_UNAVAILABLE", csharpProtocol);
        Assert.Contains("MINERU_TIER_PREPARATION_REQUIRED", csharpProtocol);
    }

    [Fact]
    public void GeneratedRuntimeHostWireTypesCarryTheCatalogSeam()
    {
        string hostWire = File.ReadAllText(
            Path.Combine(V2Directory, "generated", "RuntimeHostWireTypes.g.cs"));
        Assert.Contains("record MineruConfigCatalog", hostWire);
        Assert.Contains("record MineruTierDescriptor", hostWire);
        Assert.Contains("enum MineruTierId", hostWire);
    }

    private static string ExtractRecord(string source, string recordName)
    {
        string marker = $"public sealed record {recordName}";
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{marker} is missing");
        int end = source.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{marker} is not closed");
        return source[start..end];
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
