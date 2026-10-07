using System.Text.Json;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 全局 MinerU 识别偏好的设置闭环契约：extra.mineru_recognition 读写、
/// 目录 fail closed（未知 tier/语言不降级）、快照消费 API
/// RecognitionSelectionSnapshot.MineruConfigFor，以及桥接命令/状态投影。
/// </summary>
public sealed class MineruRecognitionWorkbenchTests
{
    [Fact]
    public async Task LoadSnapshotProjectsPreferenceAndSnapshotResolvesIt()
    {
        var fake = new MineruConfigClient
        {
            Settings = SnapshotWithRecognition(new
            {
                tier = "flash",
                ocr_mode = "txt",
                page_range = "2-4",
                language = "korean",
            }),
        };
        var viewModel = new SettingsViewModel(fake);

        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(viewModel.MineruRecognition);
        Assert.True(viewModel.MineruRecognition.Supported);
        Assert.True(viewModel.MineruRecognition.Stored);
        Assert.Equal(MineruTier.Flash, viewModel.MineruRecognition.Tier);
        Assert.Equal(MineruOcrMode.Txt, viewModel.MineruRecognition.OcrMode);
        Assert.Equal("2-4", viewModel.MineruRecognition.PageRange);
        Assert.Equal("korean", viewModel.MineruRecognition.Language);

        MineruConfig? config = viewModel.RecognitionSelection?.MineruConfigFor("mineru_document");
        Assert.NotNull(config);
        Assert.Equal(MineruTier.Flash, config.Tier);
        Assert.Equal(MineruOcrMode.Txt, config.OcrMode);
        Assert.Equal("2-4", config.PageRange);
        Assert.Equal("korean", config.Language);
        // 非 mineru 模式不受偏好影响。
        Assert.Null(viewModel.RecognitionSelection?.MineruConfigFor("paddle_text"));
    }

    [Fact]
    public async Task SavePersistsValuesAndRepublishesSnapshotImmediately()
    {
        var fake = new MineruConfigClient
        {
            Settings = new SettingsSnapshot
            {
                Extra = new Dictionary<string, JsonElement>
                {
                    ["mineru_connection"] = JsonSerializer.SerializeToElement(new
                    {
                        mode = "local",
                    }),
                },
            },
        };
        var viewModel = new SettingsViewModel(fake);
        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        await viewModel.SetMineruRecognitionAsync(
            "flash",
            "txt",
            "1-3",
            "korean",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.UpdateCalls);
        JsonElement stored = fake.LastUpdate!.Extra["mineru_recognition"];
        Assert.Equal("flash", stored.GetProperty("tier").GetString());
        Assert.Equal("txt", stored.GetProperty("ocr_mode").GetString());
        Assert.Equal("1-3", stored.GetProperty("page_range").GetString());
        Assert.Equal("korean", stored.GetProperty("language").GetString());
        // 其他 extra 键原样保留。
        Assert.Equal(
            "local",
            fake.LastUpdate.Extra["mineru_connection"].GetProperty("mode").GetString());
        Assert.Equal("已保存 MinerU 识别参数", viewModel.Status);
        // 保存后无需目录刷新，快照即时携带新偏好。
        MineruConfig? config = viewModel.RecognitionSelection?.MineruConfigFor("mineru_document");
        Assert.NotNull(config);
        Assert.Equal(MineruTier.Flash, config.Tier);
        Assert.Equal("1-3", config.PageRange);
    }

    [Fact]
    public async Task SaveRejectsTierOrLanguageOutsideTheCatalogWithoutWriting()
    {
        var fake = new MineruConfigClient();
        var viewModel = new SettingsViewModel(fake);
        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        // advanced 不在目录（目录只声明 basic/flash）。
        await viewModel.SetMineruRecognitionAsync(
            "advanced", "auto", "all", "ch", TestContext.Current.CancellationToken);
        Assert.Equal(0, fake.UpdateCalls);
        Assert.Equal("未知引擎，请重新选择", viewModel.Status);

        // korean 不在目录语言列表（只声明 ch）。
        await viewModel.SetMineruRecognitionAsync(
            "basic", "auto", "all", "japanese", TestContext.Current.CancellationToken);
        Assert.Equal(0, fake.UpdateCalls);

        // 非法页码范围在写入前拒绝。
        await viewModel.SetMineruRecognitionAsync(
            "basic", "auto", "0-3", "ch", TestContext.Current.CancellationToken);
        Assert.Equal(0, fake.UpdateCalls);
        Assert.StartsWith("页码范围", viewModel.Status);
    }

    [Fact]
    public async Task UnparseableStoredPreferenceFailsClosedInsteadOfDowngrading()
    {
        var fake = new MineruConfigClient
        {
            Settings = SnapshotWithRecognition(new
            {
                tier = "turbo",
                ocr_mode = "auto",
                page_range = "all",
                language = "ch",
            }),
        };
        var viewModel = new SettingsViewModel(fake);

        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.MineruRecognition?.Invalid is true);
        RuntimeSelectionException error = Assert.Throws<RuntimeSelectionException>(
            () => viewModel.RecognitionSelection!.MineruConfigFor("mineru_document"));
        Assert.Equal(RuntimeSelectionErrorKind.InvalidCatalogEntry, error.Kind);
        Assert.Contains("mineru_recognition_tier", error.Message);
    }

    [Fact]
    public async Task SavingDefaultRecognitionModeKeepsStoredMineruPreference()
    {
        var fake = new MineruConfigClient
        {
            Settings = SnapshotWithRecognition(new
            {
                tier = "flash",
                ocr_mode = "txt",
                page_range = "2-4",
                language = "korean",
            }),
        };
        var viewModel = new SettingsViewModel(fake);
        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

        // 保存默认识别模式会重发布任务目录快照：已存 MinerU 偏好不得被
        // 清掉（回归：可选参数缺省曾把偏好/Invalid 标记抹平）。
        await viewModel.SetDefaultRecognitionModeAsync(
            "rapid_text", TestContext.Current.CancellationToken);

        Assert.Equal("已保存默认识别模式：快速 OCR（RapidOCR）", viewModel.Status);
        MineruConfig? config = viewModel.RecognitionSelection?.MineruConfigFor("mineru_document");
        Assert.NotNull(config);
        Assert.Equal(MineruTier.Flash, config.Tier);
        Assert.Equal(MineruOcrMode.Txt, config.OcrMode);
        Assert.Equal("2-4", config.PageRange);
        Assert.Equal("korean", config.Language);
        // mineru_recognition 键未被默认模式写入破坏。
        Assert.Equal(
            "flash",
            fake.LastUpdate!.Extra["mineru_recognition"].GetProperty("tier").GetString());
    }

    [Fact]
    public async Task SavingDefaultRecognitionModeKeepsInvalidPreferenceFailClosed()
    {
        var fake = new MineruConfigClient
        {
            Settings = SnapshotWithRecognition(new
            {
                tier = "turbo",
                ocr_mode = "auto",
                page_range = "all",
                language = "ch",
            }),
        };
        var viewModel = new SettingsViewModel(fake);
        await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.MineruRecognition?.Invalid is true);

        await viewModel.SetDefaultRecognitionModeAsync(
            "rapid_text", TestContext.Current.CancellationToken);

        // Invalid fail-closed 标记同样不得被重发布抹掉。
        Assert.True(viewModel.MineruRecognition?.Invalid is true);
        Assert.Equal(
            RuntimeSelectionErrorKind.InvalidCatalogEntry,
            Assert.Throws<RuntimeSelectionException>(
                () => viewModel.RecognitionSelection!.MineruConfigFor("mineru_document")).Kind);
    }

    [Fact]
    public void CodecParsesSetMineruRecognitionAndRejectsInvalidPayloads()
    {
        Guid sessionId = Guid.NewGuid();
        string Command(string action, string arguments) => JsonSerializer.Serialize(new
        {
            version = 2,
            kind = "request",
            id = Guid.NewGuid(),
            type = "app.command",
            payload = new
            {
                sessionId,
                command = new
                {
                    scope = "settings",
                    action,
                    arguments = JsonDocument.Parse(arguments).RootElement,
                },
            },
        });

        SetMineruRecognitionCommand valid = Assert.IsType<SetMineruRecognitionCommand>(
            WorkbenchBridgeCodec.ParseCommand(
                Command(
                    "setMineruRecognition",
                    """{"tier":"flash","ocrMode":"txt","pageRange":"1-3,r1","language":"ch"}"""),
                sessionId).Command);
        Assert.Equal("flash", valid.Tier);
        Assert.Equal("txt", valid.OcrMode);
        Assert.Equal("1-3,r1", valid.PageRange);
        Assert.Equal("ch", valid.Language);

        Assert.Throws<WorkbenchBridgeProtocolException>(() =>
          WorkbenchBridgeCodec.ParseCommand(
            Command(
              "setMineruRecognition",
              """{"tier":"turbo","ocrMode":"auto","pageRange":"all","language":"ch"}"""),
            sessionId));
        Assert.Throws<WorkbenchBridgeProtocolException>(() =>
          WorkbenchBridgeCodec.ParseCommand(
            Command(
              "setMineruRecognition",
              """{"tier":"basic","ocrMode":"hybrid","pageRange":"all","language":"ch"}"""),
            sessionId));
        Assert.Throws<WorkbenchBridgeProtocolException>(() =>
          WorkbenchBridgeCodec.ParseCommand(
            Command(
              "setMineruRecognition",
              """{"tier":"basic","ocrMode":"auto","pageRange":"0","language":"ch"}"""),
            sessionId));
        Assert.Throws<WorkbenchBridgeProtocolException>(() =>
          WorkbenchBridgeCodec.ParseCommand(
            Command(
              "setMineruRecognition",
              """{"tier":"basic","ocrMode":"auto","pageRange":"all","language":""}"""),
            sessionId));
        Assert.Throws<WorkbenchBridgeProtocolException>(() =>
          WorkbenchBridgeCodec.ParseCommand(
            Command(
              "setMineruRecognition",
              """{"tier":"basic","ocrMode":"auto","pageRange":"all"}"""),
            sessionId));
    }

    [Fact]
    public void SettingsStateSerializesMineruRecognitionProjection()
    {
        var state = new SettingsWorkbenchState(
          WorkbenchTheme.Light,
          false,
          "settings.ready",
          "cpu",
          false,
          MineruConnection: new SettingsMineruConnectionState(true, "local", "", false),
          MineruRecognition: new SettingsMineruRecognitionState(
            true,
            true,
            "flash",
            "txt",
            "1-3",
            "ch",
            Invalid: false,
            InvalidReason: null,
            Tiers:
            [
              new SettingsMineruTierOptionState("basic", "ready", null),
              new SettingsMineruTierOptionState("flash", "preparation_required", null),
            ],
            Languages: ["ch", "korean"],
            DefaultTier: "basic"));
        string payload = WorkbenchBridgeCodec.SerializeState(
          Guid.NewGuid(),
          new WorkbenchStateEnvelope(3, "settings", WorkbenchStateChange.Replace, state));
        Assert.Contains("\"mineruRecognition\":{", payload);
        Assert.Contains("\"tier\":\"flash\"", payload);
        Assert.Contains("\"ocrMode\":\"txt\"", payload);
        Assert.Contains("\"pageRange\":\"1-3\"", payload);
        Assert.Contains("\"defaultTier\":\"basic\"", payload);
        Assert.Contains("\"availability\":\"preparation_required\"", payload);
        Assert.Contains("\"languages\":[\"ch\",\"korean\"]", payload);
    }

    private static SettingsSnapshot SnapshotWithRecognition(object value) => new()
    {
        Extra = new Dictionary<string, JsonElement>
        {
            ["mineru_recognition"] = JsonSerializer.SerializeToElement(value),
        },
    };

    private static Wire.Health MineruConfigHealth() => new()
    {
        SchemaVersion = 2,
        InstanceId = "sup-1",
        ProtocolVersion = 2,
        Ready = true,
        Draining = false,
        Capabilities =
        [
            RuntimeSelectionService.RecognitionModesCapability,
            RuntimeSelectionService.MineruConfigCapability,
            RuntimeSelectionService.DefaultRecognitionModeCapability,
        ],
        CapabilityDescriptors =
        [
            new Wire.CapabilityDescriptor
            {
                Name = RuntimeSelectionService.RecognitionModesCapability,
                Lifecycle = "active",
                IntroducedIn = "2.9.0",
                DeprecatedIn = null,
                SunsetAt = null,
                Replacement = null,
                RecognitionModeCatalog = new Wire.RecognitionModeCatalog
                {
                    Modes =
                    [
                        // FromWire 要求全部 8 个稳定模式恰好各声明一次。
                        Mode(Wire.RecognitionModeId.RapidText,
                            Wire.RecognitionModeFamily.Text,
                            Wire.ExecutionPipelineId.OCR,
                            Wire.OcrEngineId.Rapidocr,
                            Wire.RecognitionModeProvisioning.BaseRuntime,
                            Wire.RecognitionModeLifecycleKind.Unmanaged),
                        Mode(Wire.RecognitionModeId.WindowsText,
                            Wire.RecognitionModeFamily.Text,
                            Wire.ExecutionPipelineId.OCR,
                            Wire.OcrEngineId.Windows,
                            Wire.RecognitionModeProvisioning.OperatingSystem,
                            Wire.RecognitionModeLifecycleKind.Unmanaged),
                        Mode(Wire.RecognitionModeId.PaddleText,
                            Wire.RecognitionModeFamily.Text,
                            Wire.ExecutionPipelineId.OCR,
                            Wire.OcrEngineId.Paddleocr,
                            Wire.RecognitionModeProvisioning.AdvancedComponent,
                            Wire.RecognitionModeLifecycleKind.ModelResidency),
                        Mode(Wire.RecognitionModeId.PaddleStructure,
                            Wire.RecognitionModeFamily.Document,
                            Wire.ExecutionPipelineId.PPStructureV3,
                            null,
                            Wire.RecognitionModeProvisioning.AdvancedComponent,
                            Wire.RecognitionModeLifecycleKind.ModelResidency),
                        Mode(Wire.RecognitionModeId.PaddleDocumentVl,
                            Wire.RecognitionModeFamily.Document,
                            Wire.ExecutionPipelineId.PaddleOCRVL,
                            null,
                            Wire.RecognitionModeProvisioning.AdvancedComponent,
                            Wire.RecognitionModeLifecycleKind.ModelResidency),
                        Mode(Wire.RecognitionModeId.MineruDocument,
                            Wire.RecognitionModeFamily.Document,
                            Wire.ExecutionPipelineId.MinerU,
                            null,
                            Wire.RecognitionModeProvisioning.AdvancedComponent,
                            Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
                        Mode(Wire.RecognitionModeId.PaddleTable,
                            Wire.RecognitionModeFamily.Specialized,
                            Wire.ExecutionPipelineId.TABLERECOGNITION,
                            null,
                            Wire.RecognitionModeProvisioning.AdvancedComponent,
                            Wire.RecognitionModeLifecycleKind.ModelResidency),
                        Mode(Wire.RecognitionModeId.PaddleFormula,
                            Wire.RecognitionModeFamily.Specialized,
                            Wire.ExecutionPipelineId.FORMULARECOGNITION,
                            null,
                            Wire.RecognitionModeProvisioning.AdvancedComponent,
                            Wire.RecognitionModeLifecycleKind.ModelResidency),
                    ],
                },
            },
            new Wire.CapabilityDescriptor
            {
                Name = RuntimeSelectionService.MineruConfigCapability,
                Lifecycle = "active",
                IntroducedIn = "2.9.0",
                DeprecatedIn = null,
                SunsetAt = null,
                Replacement = null,
                MineruConfigCatalog = new Wire.MineruConfigCatalog
                {
                    DefaultTier = Wire.MineruTierId.Basic,
                    Tiers =
                    [
                        new Wire.MineruTierDescriptor
                        {
                            Id = Wire.MineruTierId.Basic,
                            Availability = Wire.MineruTierAvailability.Ready,
                            ReasonCode = null,
                        },
                        new Wire.MineruTierDescriptor
                        {
                            Id = Wire.MineruTierId.Flash,
                            Availability = Wire.MineruTierAvailability.PreparationRequired,
                            ReasonCode = null,
                        },
                    ],
                    Languages = ["ch", "korean"],
                },
            },
        ],
    };

    private static Wire.RecognitionModeDescriptor Mode(
        Wire.RecognitionModeId id,
        Wire.RecognitionModeFamily family,
        Wire.ExecutionPipelineId pipeline,
        Wire.OcrEngineId? engine,
        Wire.RecognitionModeProvisioning provisioning,
        Wire.RecognitionModeLifecycleKind lifecycleKind) => new()
    {
        Id = id,
        Family = family,
        PipelineId = pipeline,
        Engine = engine,
        Provisioning = provisioning,
        Availability = Wire.RecognitionModeAvailability.Ready,
        ReasonCode = null,
        RequiredComponent = provisioning == Wire.RecognitionModeProvisioning.AdvancedComponent
            ? "advanced-component"
            : null,
        SupportedOptions = [],
        Lifecycle = new Wire.RecognitionModeLifecycle
        {
            Kind = lifecycleKind,
            SupportsPreload = false,
            SupportsTtl = false,
            SupportsPinning = false,
            SupportsRelease = false,
        },
    };

    private sealed class MineruConfigClient : InferenceClientStub
    {
        public Wire.Health Health { get; set; } = MineruConfigHealth();

        public SettingsSnapshot Settings { get; set; } = new();

        public SettingsSnapshot? LastUpdate { get; private set; }

        public int UpdateCalls { get; private set; }

        public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Health);

        public override Task<SettingsSnapshot> GetSettingsAsync(
            CancellationToken cancellationToken) => Task.FromResult(Settings);

        public override Task<SettingsSnapshot> UpdateSettingsAsync(
            SettingsSnapshot settings,
            CancellationToken cancellationToken)
        {
            UpdateCalls++;
            LastUpdate = settings;
            Settings = settings;
            return Task.FromResult(settings);
        }

        public override Task<ResidencyStatus> GetResidencyAsync(
            CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());
    }
}
