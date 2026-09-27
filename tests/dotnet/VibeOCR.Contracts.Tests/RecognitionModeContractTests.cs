using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using Xunit;

namespace VibeOCR.Contracts.Tests;

public sealed class RecognitionModeContractTests
{
    [Fact]
    public void RecognitionModeUsesStableWireIds()
    {
        Assert.Equal(
            "\"rapid_text\"",
            JsonSerializer.Serialize(RecognitionMode.RapidText));
        Assert.Equal(
            RecognitionMode.PaddleDocumentVl,
            JsonSerializer.Deserialize<RecognitionMode>("\"paddle_document_vl\""));
    }

    [Fact]
    public void LegacyPipelineSpecDoesNotInventRecognitionMode()
    {
        var json = HttpV2Json.Serialize(new PipelineSpec
        {
            Name = "OCR",
            TtlSeconds = 120,
        });

        Assert.DoesNotContain("recognition_mode", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResidencyEntryNamesModeAndConcreteResource()
    {
        var value = new ResidencyEntry
        {
            Pipeline = "OCR",
            RecognitionMode = RecognitionMode.PaddleText,
            ResourceKind = RecognitionResourceKind.Model,
            ResourceId = "paddleocr.text.server-v5",
            Kind = ResidencyKind.Pinned,
        };

        var json = HttpV2Json.Serialize(value);
        var parsed = HttpV2Json.Deserialize<ResidencyEntry>(json)!;

        Assert.Equal(RecognitionMode.PaddleText, parsed.RecognitionMode);
        Assert.Equal(RecognitionResourceKind.Model, parsed.ResourceKind);
        Assert.Equal("paddleocr.text.server-v5", parsed.ResourceId);
    }

    [Fact]
    public void GoldenCatalogDeclaresMineruExplicitPreloadWithoutPinning()
    {
        JsonElement lifecycle = FindGoldenMode("mineru_document")
            .GetProperty("lifecycle");

        Assert.Equal(
            "process_keep_alive",
            lifecycle.GetProperty("kind").GetString());
        Assert.True(lifecycle.GetProperty("supports_preload").GetBoolean());
        Assert.True(lifecycle.GetProperty("supports_ttl").GetBoolean());
        Assert.False(lifecycle.GetProperty("supports_pinning").GetBoolean());
        Assert.True(lifecycle.GetProperty("supports_release").GetBoolean());
    }

    [Fact]
    public void GoldenMineruConfigCapabilityShippedWithThePublishedRelease()
    {
        JsonElement descriptor = FindCapabilityDescriptor(
            "ocr.mineru-config.v1");

        Assert.Equal("2.8.1", descriptor.GetProperty("introduced_in").GetString());
    }

    private static JsonElement FindCapabilityDescriptor(string name)
    {
        JsonElement descriptors = LoadRuntimeApi().RootElement
            .GetProperty("health")
            .GetProperty("capability_descriptors");
        return descriptors.EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == name);
    }

    private static JsonElement FindGoldenMode(string modeId)
    {
        JsonElement modes = FindCapabilityDescriptor("ocr.recognition-modes.v1")
            .GetProperty("recognition_mode_catalog")
            .GetProperty("modes");
        return modes.EnumerateArray().Single(
            item => item.GetProperty("id").GetString() == modeId);
    }

    private static JsonDocument LoadRuntimeApi()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
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
                return JsonDocument.Parse(File.ReadAllText(
                    Path.Combine(candidate, "golden", "runtime-api.json")));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate vibeocr/runtime_contracts from test output.");
    }
}
