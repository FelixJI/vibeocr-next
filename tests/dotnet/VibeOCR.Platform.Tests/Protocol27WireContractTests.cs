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

    [Fact]
    public void MaintenanceStatusEchoesRequestedAndEffectiveSelections()
    {
        const string json = """
            {
              "operation_id": "op-1",
              "sequence": 3,
              "operation": "ensure",
              "operation_state": "running",
              "phase": "install_profile",
              "profile_id": "win-x64-cpu",
              "updated_at": "2026-08-17T00:00:00Z",
              "requested_component_ids": ["document_parsing"],
              "effective_component_ids": ["document_parsing", "gpu_runtime"],
              "requested_download_source_ids": ["tuna-pypi"],
              "effective_download_source_ids": ["tuna-pypi"]
            }
            """;

        RuntimeMaintenanceStatus status = JsonSerializer.Deserialize(
            json,
            HttpV2JsonContext.Default.RuntimeMaintenanceStatus)!;

        Assert.Equal(["document_parsing"], status.RequestedComponentIds);
        Assert.Equal(["document_parsing", "gpu_runtime"], status.EffectiveComponentIds);
        Assert.Equal(["tuna-pypi"], status.RequestedDownloadSourceIds);
        Assert.Equal(["tuna-pypi"], status.EffectiveDownloadSourceIds);
    }

    [Fact]
    public void MaintenanceRequestAndCommandCarryOptionalSelectionsWithOmission()
    {
        string request = JsonSerializer.Serialize(
            new RuntimeMaintenanceRequest { Operation = RuntimeMaintenanceOperation.Ensure },
            HttpV2JsonContext.Default.RuntimeMaintenanceRequest);
        Assert.DoesNotContain("install_component_ids", request);
        Assert.DoesNotContain("download_source_ids", request);
        Assert.DoesNotContain("component_ids", request);

        string baseOnly = JsonSerializer.Serialize(
            new RuntimeMaintenanceRequest
            {
                Operation = RuntimeMaintenanceOperation.Ensure,
                InstallComponentIds = [],
            },
            HttpV2JsonContext.Default.RuntimeMaintenanceRequest);
        Assert.Contains("\"install_component_ids\":[]", baseOnly);

        string command = JsonSerializer.Serialize(
            new RuntimeMaintenanceCommand
            {
                CommandId = "command-1",
                Command = RuntimeMaintenanceCommandKind.Retry,
                TargetOperationId = "op-1",
                NewOperationId = "op-2",
                DownloadSourceIds = ["tuna-pypi"],
            },
            HttpV2JsonContext.Default.RuntimeMaintenanceCommand);
        Assert.Contains("\"download_source_ids\":[\"tuna-pypi\"]", command);
        Assert.DoesNotContain("install_component_ids", command);
    }
}
