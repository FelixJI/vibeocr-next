using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Inference;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// Selection UI seams: catalog-driven maintenance state, task-level recognition
/// mode selection, and the workbench bridge commands that drive them.
/// </summary>
public sealed class SelectionUiTests
{
    [Fact]
    public async Task SettingsLoadProjectsSourcesAndFeatures()
    {
        var fake = new SelectionInferenceClient { Health = SelectionHealth() };
        fake.Settings = new SettingsSnapshot
        {
            DownloadSourceIds = ["tuna-pypi"],
        };
        var viewModel = new SettingsViewModel(fake);

        await viewModel.LoadSnapshotAsync(CancellationToken.None);

        Assert.Equal(
            ["tuna-pypi"],
            viewModel.Sources.Where(source => source.Selected).Select(source => source.Id));
        // 默认 pending 加速器是 cpu:只有 CPU 变体进入功能列表。
        Assert.Equal(["document_parsing"], viewModel.Features.Select(feature => feature.FeatureId));
    }

    [Fact]
    public async Task ConcurrentSelectionLoadsShareOneCompleteCatalogSnapshot()
    {
        var fake = new DelayedSelectionInferenceClient { Health = SelectionHealth() };
        var viewModel = new SettingsViewModel(fake);

        Task first = viewModel.LoadSelectionAsync(CancellationToken.None);
        await fake.HealthRequested.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task second = viewModel.LoadSelectionAsync(CancellationToken.None);
        fake.ReleaseHealth.TrySetResult(true);

        await Task.WhenAll(first, second);

        Assert.NotNull(viewModel.Selection);
        Assert.Equal(1, fake.HealthCalls);
        RecognitionSelectionSnapshot snapshot = Assert.IsType<RecognitionSelectionSnapshot>(
            viewModel.RecognitionSelection);
        Assert.Same(viewModel.Selection, snapshot.Catalog);
    }

    [Fact]
    public async Task SetSourceWritesValidatedSelectionToBackendSettings()
    {
        var fake = new SelectionInferenceClient { Health = SelectionHealth() };
        fake.Settings = new SettingsSnapshot { DownloadSourceIds = ["tuna-pypi"] };
        var viewModel = new SettingsViewModel(fake);
        await viewModel.LoadSnapshotAsync(CancellationToken.None);

        await viewModel.SetSourceAsync("package_index", "pypi", CancellationToken.None);

        Assert.Equal(["pypi"], fake.LastUpdate?.DownloadSourceIds);
        Assert.Equal(["pypi"], viewModel.Sources.Where(s => s.Selected).Select(s => s.Id));
        Assert.Equal("已保存下载源偏好", viewModel.Status);

        await viewModel.SetSourceAsync("package_index", null, CancellationToken.None);
        Assert.Null(fake.LastUpdate?.DownloadSourceIds);

        await viewModel.SetSourceAsync("package_index", "aliyun-pypi", CancellationToken.None);
        Assert.Contains("未知下载源", viewModel.Status);
        Assert.Equal(2, fake.UpdateCalls);
    }

    [Fact]
    public async Task AcceleratorAndFeatureSelectionStagePendingChoices()
    {
        var fake = new SelectionInferenceClient { Health = SelectionHealth() };
        var viewModel = new SettingsViewModel(fake);
        await viewModel.LoadSnapshotAsync(CancellationToken.None);

        viewModel.SetPendingAccelerator("nvidia_cuda");
        Assert.Equal(
            ["document_parsing", "gpu_runtime"],
            viewModel.Features.Select(feature => feature.FeatureId));

        viewModel.SetFeatureEnabled("gpu_runtime", true);
        Assert.Equal(["gpu_runtime"], viewModel.PendingFeatureIds);
        Assert.Equal("cpu", viewModel.Backend);

        viewModel.SetFeatureEnabled("quantum_parsing", true);
        Assert.Contains("未知功能", viewModel.Status);
        Assert.Equal(["gpu_runtime"], viewModel.PendingFeatureIds);
    }

    [Fact]
    public async Task RecognitionTaskModeOverridesRuntimeDefault()
    {
        var fake = new CompletedRecognitionClient();
        var viewModel = new RecognitionViewModel(fake, new StubInputs());
        Assert.Null(viewModel.EffectiveEngine);

        viewModel.TaskEngine = "windows";
        Assert.Equal(OcrEngine.Windows, viewModel.EffectiveEngine);

        await viewModel.RecognizeViaSupervisorAsync(
            ct => Task.FromResult<RecognitionInput?>(new RecognitionInput([1, 2, 3], "image/png", "a.png", "test")),
            CancellationToken.None);
        Assert.Equal(OcrEngine.Windows, fake.LastRequest?.Pipeline.Engine);

        viewModel.TaskEngine = null;
        Assert.Null(viewModel.EffectiveEngine);
        await viewModel.RecognizeViaSupervisorAsync(
            ct => Task.FromResult<RecognitionInput?>(new RecognitionInput([4, 5, 6], "image/png", "b.png", "test")),
            CancellationToken.None);
        Assert.Null(fake.LastRequest?.Pipeline.Engine);
    }

    [Fact]
    public async Task RecognitionWithoutTaskModeUsesRuntimeDefault()
    {
        var fake = new CompletedRecognitionClient();
        var viewModel = new RecognitionViewModel(fake, new StubInputs());

        await viewModel.RecognizeViaSupervisorAsync(
            ct => Task.FromResult<RecognitionInput?>(new RecognitionInput([1], "image/png", "a.png", "test")),
            CancellationToken.None);

        Assert.Null(fake.LastRequest?.Pipeline.Engine);
    }

    [Fact]
    public void BridgeParsesSelectionCommandsAndSerializesSelectionState()
    {
        Guid sessionId = Guid.NewGuid();
        string Command(string scope, string action, string arguments) =>
            JsonSerializer.Serialize(new
            {
                version = 2,
                kind = "request",
                id = Guid.NewGuid(),
                type = "app.command",
                payload = new
                {
                    sessionId,
                    command = new { scope, action, arguments = JsonDocument.Parse(arguments).RootElement },
                },
            });

        Assert.IsType<SetDownloadSourceCommand>(
            WorkbenchBridgeCodec.ParseCommand(Command("settings", "setSource", """{"kind":"package_index","sourceId":"pypi"}"""), sessionId).Command);
        Assert.IsType<SetDownloadSourceCommand>(
            WorkbenchBridgeCodec.ParseCommand(Command("settings", "setSource", """{"kind":"package_index"}"""), sessionId).Command);
        Assert.IsType<SetAcceleratorCommand>(
            WorkbenchBridgeCodec.ParseCommand(Command("settings", "setAccelerator", """{"accelerator":"nvidia_cuda"}"""), sessionId).Command);
        Assert.IsType<SetRuntimeFeatureCommand>(
            WorkbenchBridgeCodec.ParseCommand(Command("settings", "setFeature", """{"featureId":"gpu_runtime","enabled":true}"""), sessionId).Command);
        Assert.IsType<SetTaskEngineCommand>(
            WorkbenchBridgeCodec.ParseCommand(Command("recognition", "setTaskEngine", """{"engine":"windows"}"""), sessionId).Command);
        Assert.IsType<SetTaskEngineCommand>(
            WorkbenchBridgeCodec.ParseCommand(Command("recognition", "setTaskEngine", "{}"), sessionId).Command);

        Assert.Throws<WorkbenchBridgeProtocolException>(
            () => WorkbenchBridgeCodec.ParseCommand(
                Command("settings", "setAccelerator", """{"accelerator":"tpu"}"""), sessionId));
        var state = new SettingsWorkbenchState(
            WorkbenchTheme.Dark,
            false,
            "settings.ready",
            "cpu",
            false,
            "Ctrl+Alt+Q",
            [new SettingsSourceOptionState("package_index", "tuna-pypi", "TUNA", true)],
            "nvidia_cuda",
            true,
            [new SettingsFeatureOptionState("gpu_runtime", "CUDA", "nvidia_cuda", false)]);
        string payload = WorkbenchBridgeCodec.SerializeState(
            sessionId,
            new WorkbenchStateEnvelope(7, "settings", WorkbenchStateChange.Replace, state));
        Assert.DoesNotContain("\"engines\"", payload);
        Assert.DoesNotContain("selectedEngine", payload);
        Assert.Contains("\"sources\":[", payload);
        Assert.Contains("\"pendingBackend\":\"nvidia_cuda\"", payload);
        Assert.Contains("\"canSwitchBackend\":true", payload);
        Assert.Contains("\"features\":[", payload);
        // 桥接投影不泄漏 endpoint 或内部依赖信息。
        Assert.DoesNotContain("endpoint", payload);
        Assert.DoesNotContain("component_id", payload);
    }

    [Fact]
    public async Task ModelRegistrySourcesCanBeSelectedWithoutExposingEndpoints()
    {
        var fake = new SelectionInferenceClient
        {
            Health = SelectionHealth(),
            Settings = new SettingsSnapshot
            {
                DownloadSourceIds = ["tuna-pypi", "huggingface", "legacy-source"],
            },
        };
        var viewModel = new SettingsViewModel(fake);
        await viewModel.LoadSnapshotAsync(CancellationToken.None);

        Assert.Equal(
            ["package_index", "package_index", "model_registry", "model_registry"],
            viewModel.Sources.Select(source => source.Kind));

        await viewModel.SetSourceAsync("package_index", "pypi", CancellationToken.None);
        Assert.Equal(
            ["huggingface", "legacy-source", "pypi"],
            fake.LastUpdate?.DownloadSourceIds);

        await viewModel.SetSourceAsync("model_registry", "modelscope", CancellationToken.None);
        Assert.Equal(2, fake.UpdateCalls);
        Assert.Equal(
            ["legacy-source", "pypi", "modelscope"],
            fake.LastUpdate?.DownloadSourceIds);
        Assert.Equal(
            ["pypi", "modelscope"],
            viewModel.Sources.Where(source => source.Selected).Select(source => source.Id));
        Assert.Equal("已保存下载源偏好", viewModel.Status);
    }

    private static Wire.Health SelectionHealth() => new()
    {
        SchemaVersion = 2,
        InstanceId = "sup-1",
        ProtocolVersion = 2,
        Ready = true,
        Draining = false,
        Capabilities =
        [
            RuntimeSelectionService.EngineSelectionCapability,
            RuntimeSelectionService.DownloadSourceCapability,
            RuntimeSelectionService.ComponentSelectionCapability,
        ],
        CapabilityDescriptors =
        [
            new Wire.CapabilityDescriptor
            {
                Name = RuntimeSelectionService.EngineSelectionCapability,
                Lifecycle = "active",
                IntroducedIn = "2.6.0",
                DeprecatedIn = null,
                SunsetAt = null,
                Replacement = null,
                OcrEngineCatalog = new Wire.OcrEngineCatalog
                {
                    Engines =
                    [
                        new Wire.OcrEngineDescriptor
                        {
                            Id = Wire.OcrEngineId.Rapidocr,
                            Availability = Wire.OcrEngineAvailability.Ready,
                            IncludedInBase = true,
                            ReasonCode = null,
                            RequiredComponent = null,
                        },
                        new Wire.OcrEngineDescriptor
                        {
                            Id = Wire.OcrEngineId.Windows,
                            Availability = Wire.OcrEngineAvailability.Unavailable,
                            IncludedInBase = true,
                            ReasonCode = "language_pack_missing",
                            RequiredComponent = null,
                        },
                        new Wire.OcrEngineDescriptor
                        {
                            Id = Wire.OcrEngineId.Paddleocr,
                            Availability = Wire.OcrEngineAvailability.PreparationRequired,
                            IncludedInBase = false,
                            ReasonCode = null,
                            RequiredComponent = "paddle-engine",
                        },
                    ],
                },
            },
            new Wire.CapabilityDescriptor
            {
                Name = RuntimeSelectionService.DownloadSourceCapability,
                Lifecycle = "active",
                IntroducedIn = "2.7.0",
                DeprecatedIn = null,
                SunsetAt = null,
                Replacement = null,
                DownloadSourceCatalog = new Wire.DownloadSourceCatalog
                {
                    Sources =
                    [
                        new Wire.DownloadSourceDescriptor
                        {
                            Kind = "package_index",
                            Id = "tuna-pypi",
                            Endpoint = "https://mirrors.tuna.example/pypi/simple",
                        },
                        new Wire.DownloadSourceDescriptor
                        {
                            Kind = "package_index",
                            Id = "pypi",
                            Endpoint = "https://pypi.org/simple",
                        },
                        new Wire.DownloadSourceDescriptor
                        {
                            Kind = "model_registry",
                            Id = "huggingface",
                            Endpoint = "https://huggingface.co",
                        },
                        new Wire.DownloadSourceDescriptor
                        {
                            Kind = "model_registry",
                            Id = "modelscope",
                            Endpoint = "https://www.modelscope.cn",
                        },
                        new Wire.DownloadSourceDescriptor
                        {
                            Kind = "future_registry",
                            Id = "legacy-source",
                            Endpoint = "https://legacy.example.invalid",
                        },
                    ],
                },
            },
            new Wire.CapabilityDescriptor
            {
                Name = RuntimeSelectionService.ComponentSelectionCapability,
                Lifecycle = "active",
                IntroducedIn = "2.7.0",
                DeprecatedIn = null,
                SunsetAt = null,
                Replacement = null,
                ComponentVariantCatalog = new Wire.ComponentVariantCatalog
                {
                    Variants =
                    [
                        new Wire.ComponentVariantDescriptor
                        {
                            FeatureId = "document_parsing",
                            Accelerator = "cpu",
                            ComponentId = "document_parsing",
                        },
                        new Wire.ComponentVariantDescriptor
                        {
                            FeatureId = "document_parsing",
                            Accelerator = "nvidia_cuda",
                            ComponentId = "document_parsing",
                        },
                        new Wire.ComponentVariantDescriptor
                        {
                            FeatureId = "gpu_runtime",
                            Accelerator = "nvidia_cuda",
                            ComponentId = "gpu_runtime",
                        },
                    ],
                },
            },
        ],
    };

    private class SelectionInferenceClient : InferenceClientStub
    {
        public Wire.Health Health { get; set; } = new()
        {
            SchemaVersion = 2,
            InstanceId = "sup-1",
            ProtocolVersion = 2,
            Ready = true,
            Draining = false,
            Capabilities = [],
        };

        public SettingsSnapshot Settings { get; set; } = new();

        public SettingsSnapshot? LastUpdate { get; private set; }

        public int UpdateCalls { get; private set; }

        public override Task<ResidencyStatus> GetResidencyAsync(
            CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());

        public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Health);

        public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Settings);

        public override Task<SettingsSnapshot> UpdateSettingsAsync(
            SettingsSnapshot settings,
            CancellationToken cancellationToken)
        {
            UpdateCalls++;
            LastUpdate = settings;
            Settings = settings;
            return Task.FromResult(settings);
        }
    }

    private sealed class DelayedSelectionInferenceClient : SelectionInferenceClient
    {
        public TaskCompletionSource<bool> HealthRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseHealth { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int HealthCalls { get; private set; }

        public override async Task<Wire.Health> GetHealthAsync(
            CancellationToken cancellationToken)
        {
            HealthCalls++;
            HealthRequested.TrySetResult(true);
            await ReleaseHealth.Task.WaitAsync(cancellationToken);
            return Health;
        }
    }

    private sealed class StubInputs : IInputService
    {
        public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RecognitionInput?>(null);

        public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RecognitionInput?>(null);

        public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RecognitionInput?>(null);

        public Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<RecognitionInput?>(null);
    }

private sealed class CompletedRecognitionClient : InferenceClientStub
{
    public SubmitRequest? LastRequest { get; private set; }

    public override Task<JobRef> SubmitAsync(
        SubmitRequest request,
        IReadOnlyDictionary<string, SubmitUpload> uploads,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new JobRef
        {
            JobId = "job-1",
            Items =
            [
                new JobItem
                {
                    ItemId = "it-0",
                    ClientItemKey = request.Items[0].ClientItemKey,
                    Ordinal = 0,
                    DisplayName = request.Items[0].DisplayName,
                    State = ItemState.Queued,
                },
            ],
        });
    }

    public override Task<JobUpdate> ObserveAsync(
        string jobId,
        int afterSequence,
        CancellationToken cancellationToken) => Task.FromResult(new JobUpdate
    {
        Snapshot = new JobSnapshot
        {
            JobId = jobId,
            Kind = JobKind.Recognition,
            Priority = JobPriority.Interactive,
            State = JobState.Completed,
        },
        Events = [],
        Outcomes =
        [
            new ItemOutcome
            {
                ItemId = "it-0",
                Attempt = 1,
                State = ItemState.Succeeded,
                Payload = new Dictionary<string, JsonElement>
                {
                    ["raw_text"] = JsonSerializer.SerializeToElement("识别结果"),
                },
            },
        ],
        ThroughSequence = 1,
        More = false,
    });
}

}
