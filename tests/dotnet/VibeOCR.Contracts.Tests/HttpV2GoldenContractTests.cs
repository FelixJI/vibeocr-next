// Phase 1 (.NET side) + Phase 7B golden contract tests for the HTTP v2 protocol.
//
// These prove .NET and Python agree on the same v2 wire payloads (plan §1
// exit criterion: "Python 与 C# golden 100% 一致"). Fixtures are loaded from
// the Python package's runtime_contracts/golden/golden.json and errors.json, exactly
// like the v1 tests read protocol/v1. Round-trips use structural DeepEquals
// (order/whitespace-insensitive), mirroring GoldenContractTests.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeOCR.Contracts.HttpV2;
using Xunit;

namespace VibeOCR.Contracts.Tests;

public sealed class HttpV2GoldenContractTests
{
    private static readonly string GoldenDirectory = FindV2Directory();

    // ------------------------------------------------------------------
    // JobSnapshot golden round-trips
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("job_snapshot_running")]
    [InlineData("job_snapshot_completed_with_errors")]
    [InlineData("job_snapshot_cancelled")]
    public void JobSnapshotsRoundTrip(string key)
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty(key);
        AssertDeepRoundTrip(fixture, json => HttpV2Json.Deserialize<JobSnapshot>(json)!);
    }

    [Fact]
    public void JobRefRoundTrips()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("job_ref");
        AssertDeepRoundTrip(fixture, json => HttpV2Json.Deserialize<JobRef>(json)!);
    }

    // ------------------------------------------------------------------
    // Error payload golden round-trips
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("error_validation")]
    [InlineData("error_oom")]
    [InlineData("error_cancelled")]
    public void ErrorPayloadsRoundTrip(string key)
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty(key);
        AssertDeepRoundTrip(fixture, json => HttpV2Json.Deserialize<HttpV2ErrorPayload>(json)!);
    }

    // ------------------------------------------------------------------
    // Residency / settings golden round-trips
    // ------------------------------------------------------------------

    [Fact]
    public void ResidencyStatusRoundTrips()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("residency_status");
        AssertDeepRoundTrip(fixture, json => HttpV2Json.Deserialize<ResidencyStatus>(json)!);
    }

    [Fact]
    public void RuntimeStatusRoundTripsTypedProfileAndProgress()
    {
        var status = new RuntimeStatusSnapshot
        {
            InstanceId = "runtime-1",
            ServiceState = RuntimeServiceState.Maintenance,
            BackendVersion = "0.9.0",
            Profile = new RuntimeProfileStatus
            {
                ProfileId = "win-x64-cpu",
                Accelerator = RuntimeAccelerator.Cpu,
                Components =
                [
                    new RuntimeComponentStatus
                    {
                        ComponentId = "ocr_engine",
                        DisplayName = "OCR engine",
                        State = RuntimeComponentState.Installing,
                        Version = "3.3.2",
                        DesiredState = RuntimeComponentDesiredState.Ready,
                        DesiredVersion = "3.3.2",
                        ActualState = RuntimeComponentActualState.Drifted,
                        ActualVersion = "3.3.1",
                        DriftReason = RuntimeDriftReason.VersionMismatch,
                        Repairable = true,
                    },
                ],
            },
            Maintenance = new RuntimeMaintenanceStatus
            {
                OperationId = "install-1",
                Sequence = 2,
                Operation = RuntimeMaintenanceOperation.Ensure,
                OperationState = RuntimeOperationState.Running,
                Phase = RuntimeMaintenancePhase.InstallProfile,
                ProfileId = "win-x64-cpu",
                ComponentId = "ocr_engine",
                RequestedComponentIds = ["ocr_engine"],
                EffectiveComponentIds = ["ocr_engine", "runtime_base"],
                UpdatedAt = "2026-08-05T12:00:00Z",
                Progress = new ProgressSnapshot
                {
                    Unit = ProgressUnit.Bytes,
                    Current = 50,
                    Total = 100,
                    EstimatedRemainingSeconds = 2.5,
                },
                MessageCode = "runtime.install.profile",
            },
            Source = new RuntimeSourceIdentity
            {
                BackendVersion = "0.9.0",
                BackendSourceSha = new string('a', 40),
                RuntimeManifestSha256 = new string('b', 64),
                ProtocolVersion = "2.3.0",
                ProtocolManifestSha256 = new string('c', 64),
            },
        };
        string json = HttpV2Json.Serialize(status);
        RuntimeStatusSnapshot parsed =
            HttpV2Json.Deserialize<RuntimeStatusSnapshot>(json)!;
        Assert.Equal("ocr_engine", parsed.Profile.Components.Single().ComponentId);
        Assert.Equal(100, parsed.Maintenance!.Progress!.Total);
        Assert.Equal(2.5, parsed.Maintenance.Progress.EstimatedRemainingSeconds);
        Assert.Equal(RuntimeDriftReason.VersionMismatch,
            parsed.Profile.Components.Single().DriftReason);
        Assert.Equal(2, parsed.Maintenance.EffectiveComponentIds!.Count);
        Assert.Equal(new string('a', 40), parsed.Source!.BackendSourceSha);
    }

    [Theory]
    [InlineData("progress_bytes_large")]
    [InlineData("progress_bytes_unknown")]
    [InlineData("progress_bytes_zero")]
    public void MeasuredBytesRoundTripAcrossBindings(string key)
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty(key);
        string json = fixture.GetRawText();
        var typed = HttpV2Json.Deserialize<ProgressSnapshot>(json)!;
        var host = JsonSerializer.Deserialize<VibeOCR.Runtime.Contracts.Generated.Host.ProgressSnapshot>(json)!;
        var wire = JsonSerializer.Deserialize<VibeOCR.Runtime.Contracts.Generated.Wire.ProgressSnapshot>(json)!;
        long current = fixture.GetProperty("current").GetInt64();
        long? total = fixture.TryGetProperty("total", out JsonElement value) ? value.GetInt64() : null;
        Assert.Equal(current, typed.Current);
        Assert.Equal(current, host.Current);
        Assert.Equal(current, wire.Current);
        Assert.Equal(total, typed.Total);
        Assert.Equal(total, host.Total);
        Assert.Equal(total, wire.Total);
        Assert.Equal(typed.EstimatedRemainingSeconds, host.EstimatedRemainingSeconds);
        Assert.Equal(typed.EstimatedRemainingSeconds, wire.EstimatedRemainingSeconds);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(HttpV2Json.Serialize(typed))));
        Assert.Equal(current, JsonSerializer.Deserialize<VibeOCR.Runtime.Contracts.Generated.Host.ProgressSnapshot>(JsonSerializer.Serialize(host))!.Current);
        Assert.Equal(total, JsonSerializer.Deserialize<VibeOCR.Runtime.Contracts.Generated.Wire.ProgressSnapshot>(JsonSerializer.Serialize(wire))!.Total);
    }

    [Fact]
    public void IndeterminateProgressOmitsTotal()
    {
        var progress = new ProgressSnapshot
        {
            Unit = ProgressUnit.Bytes,
            Current = 1024,
        };
        JsonNode payload = JsonNode.Parse(HttpV2Json.Serialize(progress))!;
        Assert.False(payload.AsObject().ContainsKey("total"));
    }

    [Fact]
    public void SettingsSnapshotRoundTrips()
    {
        JsonElement fixture = LoadGolden().RootElement.GetProperty("settings_snapshot");
        AssertDeepRoundTrip(fixture, json => HttpV2Json.Deserialize<SettingsSnapshot>(json)!);
    }

    // ------------------------------------------------------------------
    // Error registry ↔ wire enum + retryable flag
    // ------------------------------------------------------------------

    [Fact]
    public void ErrorRegistryMatchesTheWireEnumAndRetryPolicy()
    {
        using JsonDocument registry = LoadErrorsRegistry();
        JsonElement[] entries = registry.RootElement.GetProperty("codes").EnumerateArray().ToArray();

        string[] registered = entries.Select(e => e.GetProperty("code").GetString()!).ToArray();
        string[] declared = Enum.GetValues<HttpV2ErrorCode>()
            .Select<HttpV2ErrorCode, string>(WireName)
            .ToArray();
        Assert.Equal(registered.Order(), declared.Order());
        Assert.Equal(46, registered.Length);

        foreach (JsonElement entry in entries)
        {
            HttpV2ErrorCode code = ParseCode(entry.GetProperty("code").GetString()!);
            string expectedCategory = entry.GetProperty("category").GetString()!;
            ErrorCategory category = Enum.GetValues<ErrorCategory>()
                .First(c => WireName<ErrorCategory>(c) == expectedCategory);
            Assert.Equal(expectedCategory, WireName<ErrorCategory>(category));
            Assert.Equal(entry.GetProperty("retryable").GetBoolean(), IsRetryable(code));
        }
    }

    [Fact]
    public void ExistingErrorCategoryNumericValuesRemainStable()
    {
        Assert.Equal(0, (int)ErrorCategory.Validation);
        Assert.Equal(1, (int)ErrorCategory.Auth);
        Assert.Equal(2, (int)ErrorCategory.NotFound);
        Assert.Equal(3, (int)ErrorCategory.Conflict);
        Assert.Equal(4, (int)ErrorCategory.Cancelled);
        Assert.Equal(5, (int)ErrorCategory.Oom);
        Assert.Equal(6, (int)ErrorCategory.Transient);
        Assert.Equal(7, (int)ErrorCategory.BackendUnavailable);
        Assert.Equal(8, (int)ErrorCategory.Internal);
        Assert.Equal(9, (int)ErrorCategory.Capability);
        Assert.Equal(10, (int)ErrorCategory.Identity);
    }

    [Fact]
    public void SchemaVersionIsTwo()
    {
        Assert.Equal(2, HttpV2Schema.Version);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void AssertDeepRoundTrip(JsonElement expected, Func<string, object> deserialize)
    {
        object value = deserialize(expected.GetRawText());
        JsonNode expectedNode = JsonNode.Parse(expected.GetRawText())!;
        JsonNode actualNode = JsonNode.Parse(HttpV2Json.Serialize(value, value.GetType()))!;
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode),
            $"round-trip mismatch for {value.GetType().Name}:\nexpected: {expectedNode}\nactual:   {actualNode}");
    }

    private static JsonDocument LoadGolden() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDirectory, "golden", "golden.json")));

    private static JsonDocument LoadErrorsRegistry() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDirectory, "errors.json")));

    private static string WireName<T>(T value) where T : struct, Enum =>
        typeof(T).GetField(value.ToString())?
            .GetCustomAttributes(typeof(JsonStringEnumMemberNameAttribute), false)
            .Cast<JsonStringEnumMemberNameAttribute>()
            .SingleOrDefault()?.Name ?? value.ToString();

    private static HttpV2ErrorCode ParseCode(string wire) =>
        Enum.GetValues<HttpV2ErrorCode>().First(c => WireName(c) == wire);

    private static bool IsRetryable(HttpV2ErrorCode code) => code switch
    {
        HttpV2ErrorCode.QuotaExceeded => false,
        HttpV2ErrorCode.ValidationError => false,
        HttpV2ErrorCode.Unauthorized => false,
        HttpV2ErrorCode.ForbiddenLoopback => false,
        HttpV2ErrorCode.JobNotFound => false,
        HttpV2ErrorCode.ResourceNotFound => false,
        HttpV2ErrorCode.JobNotCancellable => false,
        HttpV2ErrorCode.JobNotRetryable => false,
        HttpV2ErrorCode.InputExpired => false,
        HttpV2ErrorCode.PinCapacityConflict => false,
        HttpV2ErrorCode.Cancelled => false,
        HttpV2ErrorCode.AdapterProtocolViolation => false,
        HttpV2ErrorCode.ProtocolMismatch => false,
        HttpV2ErrorCode.RuntimeOperationNotFound => false,
        HttpV2ErrorCode.RuntimeOperationIdConflict => false,
        HttpV2ErrorCode.RuntimeCommandIdConflict => false,
        HttpV2ErrorCode.RuntimeOperationNotCancellable => false,
        HttpV2ErrorCode.RuntimeOperationNotRetryable => false,
        HttpV2ErrorCode.RuntimeCursorExpired => false,
        HttpV2ErrorCode.RuntimeCapabilityUnavailable => false,
        HttpV2ErrorCode.RuntimeIdentityMismatch => false,
        HttpV2ErrorCode.RuntimeInstallFailed => false,
        HttpV2ErrorCode.RuntimeInstallPlanStale => false,
        HttpV2ErrorCode.RuntimeInstallPlanBlocked => false,
        HttpV2ErrorCode.OcrEngineUnknown => false,
        HttpV2ErrorCode.OcrEngineUnavailable => false,
        HttpV2ErrorCode.OcrEnginePreparationRequired => false,
        HttpV2ErrorCode.OcrEngineNotValidForPipeline => false,
        HttpV2ErrorCode.OcrEngineLanguageUnavailable => false,
        HttpV2ErrorCode.DownloadSourceUnknown => false,
        HttpV2ErrorCode.RuntimeComponentUnknown => false,
        HttpV2ErrorCode.RecognitionModeUnknown => false,
        HttpV2ErrorCode.RecognitionModeUnavailable => false,
        HttpV2ErrorCode.RecognitionModeLifecycleUnsupported => false,
        HttpV2ErrorCode.RecognitionModePipelineMismatch => false,
        HttpV2ErrorCode.MineruConfigUnavailable => false,
        HttpV2ErrorCode.MineruConfigMigrationRequired => false,
        HttpV2ErrorCode.MineruTierUnavailable => false,
        HttpV2ErrorCode.MineruTierPreparationRequired => false,
        // Retryable: OOM, transient, draining, backend unavailable, internal.
        _ => true,
    };

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
}
