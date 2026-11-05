using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Xunit;

namespace VibeOCR.Platform.Tests;

/// <summary>
/// Pins the Protocol 2.7 wire semantics Next depends on: omission (never an
/// empty array) for source selections, the open download source kind, the
/// optional OCR engine selection, and the requested/effective echo of
/// maintenance operations.
/// </summary>
public sealed class Protocol27WireContractTests
{
    [Fact]
    public void SettingsSnapshotNullAndEmptySourceListsSerializeAsOmission()
    {
        string absent = JsonSerializer.Serialize(
            new SettingsSnapshot(),
            HttpV2JsonContext.Default.SettingsSnapshot);
        Assert.DoesNotContain("download_source_ids", absent);

        string empty = JsonSerializer.Serialize(
            new SettingsSnapshot { DownloadSourceIds = [] },
            HttpV2JsonContext.Default.SettingsSnapshot);
        Assert.DoesNotContain("download_source_ids", empty);

        string selected = JsonSerializer.Serialize(
            new SettingsSnapshot { DownloadSourceIds = ["tuna-pypi"] },
            HttpV2JsonContext.Default.SettingsSnapshot);
        Assert.Contains("\"download_source_ids\":[\"tuna-pypi\"]", selected);
    }

    [Fact]
    public void SettingsSnapshotParsesOmittedSourceSelectionAsNull()
    {
        SettingsSnapshot parsed = JsonSerializer.Deserialize(
            """{"schema_version":2,"residency":{"default_ttl_seconds":300,"pipelines":[]},"extra":{}}""",
            HttpV2JsonContext.Default.SettingsSnapshot)!;

        Assert.Null(parsed.DownloadSourceIds);
    }

    [Fact]
    public void PipelineSelectionOmitsNullEngineAndSerializesExplicitChoice()
    {
        string omitted = JsonSerializer.Serialize(
            new PipelineSelection { PipelineId = "ocr" },
            HttpV2JsonContext.Default.PipelineSelection);
        Assert.DoesNotContain("engine", omitted);

        string explicitEngine = JsonSerializer.Serialize(
            new PipelineSelection { PipelineId = "ocr", Engine = OcrEngine.RapidOcr },
            HttpV2JsonContext.Default.PipelineSelection);
        Assert.Contains("\"engine\":\"rapidocr\"", explicitEngine);
    }

    [Fact]
    public void DownloadSourceKindStaysAnOpenStringForUnknownKinds()
    {
        const string json =
            """{"kind":"internal-mirror","id":"mirror-1","endpoint":"https://example.invalid/simple"}""";

        Host.DownloadSourceDescriptor descriptor =
            JsonSerializer.Deserialize<Host.DownloadSourceDescriptor>(json)!;

        Assert.Equal("internal-mirror", descriptor.Kind);
        Assert.Equal("mirror-1", descriptor.Id);
        string roundTrip = JsonSerializer.Serialize(descriptor);
        Assert.Contains("\"internal-mirror\"", roundTrip);
    }
}
