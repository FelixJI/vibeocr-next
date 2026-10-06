using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 默认识别模式（extra.default_recognition_mode）的设置闭环契约：能力
/// 门禁、目录 ready 校验、extra 读改写保留其他键、失败保旧、无效回显
/// fail closed，以及任务页继承绑定与优先级（显式 override > 已提交默认）。
/// </summary>
public sealed class DefaultRecognitionModeWorkbenchTests
{
  [Fact]
  public async Task LoadSnapshotProjectsCommittedDefaultFromBackendSettings()
  {
    var fake = new DefaultModeInferenceClient
    {
      Health = DefaultModeHealth(),
      Settings = Snapshot("windows_text"),
    };
    var viewModel = new SettingsViewModel(fake);

    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(viewModel.DefaultRecognitionMode);
    Assert.True(viewModel.DefaultRecognitionMode.Supported);
    Assert.True(viewModel.DefaultRecognitionMode.Stored);
    Assert.Equal("windows_text", viewModel.DefaultRecognitionMode.ModeId);
    Assert.Equal(
      "windows_text",
      viewModel.RecognitionSelection?.DefaultRecognitionModeId);
  }

  [Fact]
  public async Task LoadSnapshotWithoutCapabilityProjectsReadOnlyState()
  {
    Wire.Health health = DefaultModeHealth();
    var fake = new DefaultModeInferenceClient
    {
      Health = health with
      {
        Capabilities = [RuntimeSelectionService.RecognitionModesCapability],
      },
      Settings = Snapshot("windows_text"),
    };
    var viewModel = new SettingsViewModel(fake);

    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    // 能力未声明：不解读键，也不向旧 Backend 写未知键。
    Assert.NotNull(viewModel.DefaultRecognitionMode);
    Assert.False(viewModel.DefaultRecognitionMode.Supported);
    Assert.Null(viewModel.DefaultRecognitionMode.ModeId);
    Assert.Equal(
      RuntimeDefaultModeBinding.NotApplicable,
      viewModel.RecognitionSelection?.DefaultMode);
    Assert.Null(viewModel.RecognitionSelection?.DefaultRecognitionModeId);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public async Task UnparsableEchoedValueMarksSnapshotInvalidAndRefusesSubmit(string? storedValue)
  {
    // Runtime 原样回显损坏的持久值，null、空字符串与空白都必须拒绝，
    // 不得当作“无默认”静默回退 RapidOCR，提交协商必须拒绝并指向修复。
    var fake = new DefaultModeInferenceClient { Health = DefaultModeHealth() };
    fake.Settings = new SettingsSnapshot
    {
      Extra = new Dictionary<string, JsonElement>
      {
        ["default_recognition_mode"] = JsonSerializer.SerializeToElement(storedValue),
      },
    };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    Assert.Equal(
      RuntimeDefaultModeBinding.Invalid,
      viewModel.RecognitionSelection?.DefaultMode);
    Assert.Equal(storedValue, viewModel.RecognitionSelection?.DefaultRecognitionModeId);

    RecognitionSelectionSnapshot snapshot = viewModel.RecognitionSelection!;
    RecognitionModeUnavailableException refused = Assert.Throws<
      RecognitionModeUnavailableException>(() =>
        DesktopWorkbenchCommandHandler.EffectiveModeIdOrDefault(
          snapshot, taskEngine: null, requireUsable: true));
    Assert.Contains("设置 · 识别默认模式", refused.Message);
    // 状态投影不抛：不绑定也不猜默认。
    Assert.Null(DesktopWorkbenchCommandHandler.EffectiveModeIdOrDefault(
      snapshot, taskEngine: null, requireUsable: false));
    // 显式覆盖仍优先于无效默认。
    Assert.Equal(
      "rapid_text",
      DesktopWorkbenchCommandHandler.EffectiveModeIdOrDefault(
        snapshot, taskEngine: "rapid_text", requireUsable: true));
  }

  [Fact]
  public void MissingOrUnreadProjectionRefusesSubmitWithoutGuessingADefault()
  {
    foreach (RuntimeDefaultModeBinding binding in new[]
    {
      RuntimeDefaultModeBinding.Invalid,
      RuntimeDefaultModeBinding.Unread,
    })
    {
      var snapshot = new RecognitionSelectionSnapshot(null!, binding);
      Assert.Throws<RecognitionModeUnavailableException>(() =>
        DesktopWorkbenchCommandHandler.EffectiveModeIdOrDefault(
          snapshot, taskEngine: null, requireUsable: true));
      Assert.Null(DesktopWorkbenchCommandHandler.EffectiveModeIdOrDefault(
        snapshot, taskEngine: null, requireUsable: false));
    }
  }

  [Fact]
  public void NotApplicableAndNullSnapshotKeepLegacySemantics()
  {
    Assert.Null(DesktopWorkbenchCommandHandler.EffectiveModeIdOrDefault(
      new RecognitionSelectionSnapshot(null!),
      taskEngine: null, requireUsable: true));
    Assert.Null(DesktopWorkbenchCommandHandler.EffectiveModeIdOrDefault(
      null, taskEngine: null, requireUsable: true));
    Assert.Equal(
      "windows_text",
      DesktopWorkbenchCommandHandler.EffectiveModeIdOrDefault(
        new RecognitionSelectionSnapshot(
          null!, RuntimeDefaultModeBinding.Bound, "windows_text"),
        taskEngine: null,
        requireUsable: true));
  }

  [Fact]
  public async Task SetDefaultPersistsModeAndPreservesOtherExtraKeys()
  {
    var fake = new DefaultModeInferenceClient { Health = DefaultModeHealth() };
    fake.Settings = Snapshot("rapid_text", keep: "mineru_connection");
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    await viewModel.SetDefaultRecognitionModeAsync(
      "windows_text", TestContext.Current.CancellationToken);

    Assert.Equal(1, fake.UpdateCalls);
    JsonElement extra = fake.LastUpdate!.Extra["default_recognition_mode"];
    Assert.Equal("windows_text", extra.GetString());
    Assert.True(fake.LastUpdate.Extra.ContainsKey("mineru_connection"));
    Assert.Equal("已保存默认识别模式：Windows OCR（系统内置）", viewModel.Status);
    Assert.Equal("windows_text", viewModel.DefaultRecognitionMode?.ModeId);
    Assert.Equal(
      "windows_text",
      viewModel.RecognitionSelection?.DefaultRecognitionModeId);
  }

  [Fact]
  public async Task SetDefaultFailsClosedWithoutCapabilityOrCatalog()
  {
    Wire.Health health = DefaultModeHealth();
    var withoutCapability = new DefaultModeInferenceClient
    {
      Health = health with
      {
        Capabilities = [RuntimeSelectionService.RecognitionModesCapability],
      },
    };
    var viewModel = new SettingsViewModel(withoutCapability);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    await viewModel.SetDefaultRecognitionModeAsync(
      "windows_text", TestContext.Current.CancellationToken);
    Assert.Equal(0, withoutCapability.UpdateCalls);
    Assert.Contains(
      RuntimeSelectionService.DefaultRecognitionModeCapability,
      viewModel.Status);

    var unloadedFake = new DefaultModeInferenceClient { Health = DefaultModeHealth() };
    var unloaded = new SettingsViewModel(unloadedFake);
    await unloaded.SetDefaultRecognitionModeAsync(
      "windows_text", TestContext.Current.CancellationToken);
    Assert.Equal(0, unloadedFake.UpdateCalls);
    Assert.Equal("运行时目录尚未加载，请先刷新运行时", unloaded.Status);
  }

  [Theory]
  [InlineData("not_a_mode", "未知引擎，请重新选择")]
  [InlineData("mineru_document", "需要先准备对应组件")]
  public async Task SetDefaultRejectsUnknownAndNotReadyModesFailClosed(
    string modeId, string statusFragment)
  {
    var fake = new DefaultModeInferenceClient { Health = DefaultModeHealth() };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    await viewModel.SetDefaultRecognitionModeAsync(
      modeId, TestContext.Current.CancellationToken);

    Assert.Equal(0, fake.UpdateCalls);
    Assert.Contains(statusFragment, viewModel.Status);
    Assert.Null(viewModel.DefaultRecognitionMode?.ModeId);
  }

  [Fact]
  public async Task SetDefaultFailureKeepsPreviousProjection()
  {
    var fake = new DefaultModeInferenceClient
    {
      Health = DefaultModeHealth(),
      Settings = Snapshot("windows_text"),
      UpdateThrows = new InferenceClientException(
        HttpV2ErrorCode.BackendUnavailable, "offline", retryable: true),
    };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    await viewModel.SetDefaultRecognitionModeAsync(
      "rapid_text", TestContext.Current.CancellationToken);

    Assert.Equal(1, fake.UpdateCalls);
    Assert.Equal("windows_text", viewModel.DefaultRecognitionMode?.ModeId);
    Assert.Equal(
      "windows_text",
      viewModel.RecognitionSelection?.DefaultRecognitionModeId);
    Assert.Contains("Supervisor", viewModel.Status);
  }

  [Fact]
  public async Task InheritOptionBindsCommittedDefaultUntilOverridden()
  {
    var fake = new DefaultModeInferenceClient
    {
      Health = DefaultModeHealth(),
      Settings = Snapshot("windows_text"),
    };
    var recognition = new RecognitionViewModel(fake, new IdleInputService());
    var settings = new SettingsViewModel(fake);
    await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
    string root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-default-mode-{Guid.NewGuid():N}");
    string resourceRoot = Path.Combine(root, "resources");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotations = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker, resourceRoot, static () => 0, annotations);
      // 目录加载后继承绑定默认：Selected 标记默认项，IsTaskOverride 仅对
      // 显式选择为真，TaskEngine 保持 null（仍是“跟随默认”）。
      WorkbenchCommandOutcome inherited = await handler.ExecuteAsync(
        new SetTaskEngineCommand(null), TestContext.Current.CancellationToken);
      var state = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(inherited.States));
      Assert.Null(state.TaskEngine);
      RecognitionEngineChoice windows = Assert.Single(
        state.Engines!, engine => engine.Engine == "windows_text");
      Assert.True(windows.Selected);
      Assert.False(windows.IsTaskOverride);
      RecognitionEngineChoice rapid = Assert.Single(
        state.Engines!, engine => engine.Engine == "rapid_text");
      Assert.False(rapid.Selected);

      // 显式覆盖优先于默认。
      WorkbenchCommandOutcome overridden = await handler.ExecuteAsync(
        new SetTaskEngineCommand("rapid_text"), TestContext.Current.CancellationToken);
      var overrideState = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(overridden.States));
      Assert.Equal("rapid_text", overrideState.TaskEngine);
      RecognitionEngineChoice rapidOverride = Assert.Single(
        overrideState.Engines!, engine => engine.Engine == "rapid_text");
      Assert.True(rapidOverride.Selected);
      Assert.True(rapidOverride.IsTaskOverride);
      RecognitionEngineChoice windowsAfter = Assert.Single(
        overrideState.Engines!, engine => engine.Engine == "windows_text");
      Assert.False(windowsAfter.Selected);
      // 默认标识独立于本次选择：覆盖后仍标记已提交默认。
      Assert.True(windowsAfter.IsDefault);
      Assert.False(rapidOverride.IsDefault);

      // 清除覆盖后回到默认绑定。
      WorkbenchCommandOutcome cleared = await handler.ExecuteAsync(
        new SetTaskEngineCommand(null), TestContext.Current.CancellationToken);
      var clearedState = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(cleared.States));
      Assert.Null(clearedState.TaskEngine);
      Assert.NotNull(clearedState.Engines);
      Assert.NotEmpty(clearedState.Engines);
      Assert.True(Assert.Single(
        clearedState.Engines!, engine => engine.Engine == "windows_text").Selected);

      // 保存新默认后继承项立即切换，不重置用户选择。
      fake.Settings = Snapshot("windows_text");
      WorkbenchCommandOutcome saved = await handler.ExecuteAsync(
        new SetDefaultRecognitionModeCommand("rapid_text"),
        TestContext.Current.CancellationToken);
      Assert.Null(saved.Error);
      Assert.IsType<SettingsWorkbenchState>(Assert.Single(saved.States));
      Assert.Equal(
        "rapid_text",
        settings.RecognitionSelection?.DefaultRecognitionModeId);
      Assert.Equal(
        RuntimeDefaultModeBinding.Bound,
        settings.RecognitionSelection?.DefaultMode);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void BridgeParsesDefaultRecognitionModeCommand()
  {
    Guid sessionId = Guid.NewGuid();
    string Command(string arguments) => JsonSerializer.Serialize(new
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
          action = "setDefaultRecognitionMode",
          arguments = JsonDocument.Parse(arguments).RootElement,
        },
      },
    });

    SetDefaultRecognitionModeCommand parsed = Assert.IsType<SetDefaultRecognitionModeCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        Command("""{"mode":"windows_text"}"""), sessionId).Command);
    Assert.Equal("windows_text", parsed.ModeId);

    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        Command("""{"mode":""}"""), sessionId));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        Command("""{"mode":42}"""), sessionId));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        Command("""{"mode":"rapid_text","extra":1}"""), sessionId));
  }

  [Fact]
  public void SettingsStateSerializesDefaultRecognitionModeProjection()
  {
    var state = new SettingsWorkbenchState(
      WorkbenchTheme.Light,
      false,
      "settings.ready",
      "cpu",
      false,
      DefaultRecognitionMode: new SettingsDefaultRecognitionModeState(
        true, "windows_text", Stored: true),
      RecognitionModes:
      [
        new SettingsRecognitionModeOptionState(
          "rapid_text", "快速 OCR（RapidOCR）", "ready", null),
        new SettingsRecognitionModeOptionState(
          "paddle_text", "通用 OCR（PaddleOCR）", "preparation_required",
          "runtime_component_missing"),
      ]);
    string payload = WorkbenchBridgeCodec.SerializeState(
      Guid.NewGuid(),
      new WorkbenchStateEnvelope(3, "settings", WorkbenchStateChange.Replace, state));
    using JsonDocument document = JsonDocument.Parse(payload);
    JsonElement settings = document.RootElement.GetProperty("payload")
      .GetProperty("state");
    JsonElement projected = settings.GetProperty("defaultRecognitionMode");
    Assert.True(projected.GetProperty("supported").GetBoolean());
    Assert.Equal("windows_text", projected.GetProperty("modeId").GetString());
    Assert.True(projected.GetProperty("stored").GetBoolean());
    JsonElement modes = settings.GetProperty("recognitionModes");
    Assert.Equal(2, modes.GetArrayLength());
    Assert.Equal("paddle_text", modes[1].GetProperty("id").GetString());
    Assert.Equal(
      "preparation_required",
      modes[1].GetProperty("availability").GetString());
  }

  private static SettingsSnapshot Snapshot(
    string? defaultMode = null,
    string? keep = null)
  {
    var extra = new Dictionary<string, JsonElement>();
    if (defaultMode is not null)
    {
      extra["default_recognition_mode"] =
        JsonSerializer.SerializeToElement(defaultMode);
    }
    if (keep is not null)
    {
      extra[keep] = JsonSerializer.SerializeToElement(new { mode = "local" });
    }
    return new SettingsSnapshot { Extra = extra };
  }

  private static Wire.Health DefaultModeHealth() => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-1",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities =
    [
      RuntimeSelectionService.DefaultRecognitionModeCapability,
      RuntimeSelectionService.RecognitionModesCapability,
    ],
    CapabilityDescriptors =
    [
      new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.RecognitionModesCapability,
        Lifecycle = "active",
        IntroducedIn = "2.8.0",
        DeprecatedIn = null,
        SunsetAt = null,
        Replacement = null,
        RecognitionModeCatalog = new Wire.RecognitionModeCatalog
        {
          Modes =
          [
            Mode(Wire.RecognitionModeId.RapidText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.WindowsText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.PaddleText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.PaddleStructure,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.PaddleDocumentVl,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.MineruDocument,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleTable,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.PaddleFormula,
              Wire.RecognitionModeAvailability.Ready),
          ],
        },
      },
    ],
  };

  private static Wire.RecognitionModeDescriptor Mode(
    Wire.RecognitionModeId id,
    Wire.RecognitionModeAvailability availability)
  {
    var (family, pipeline, engine, provisioning, lifecycle) = id switch
    {
      Wire.RecognitionModeId.RapidText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Rapidocr,
        Wire.RecognitionModeProvisioning.BaseRuntime,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.WindowsText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Windows,
        Wire.RecognitionModeProvisioning.OperatingSystem,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.MineruDocument => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.MinerU, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.PaddleText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Paddleocr,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleStructure => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PPStructureV3, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleDocumentVl => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PaddleOCRVL, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleTable => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.TABLERECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleFormula => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.FORMULARECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    bool mineru = id == Wire.RecognitionModeId.MineruDocument;
    return new Wire.RecognitionModeDescriptor
    {
      Id = id,
      Family = family,
      PipelineId = pipeline,
      Engine = engine,
      Provisioning = provisioning,
      Availability = availability,
      ReasonCode = availability == Wire.RecognitionModeAvailability.Ready
        ? null
        : "runtime_component_missing",
      RequiredComponent = availability == Wire.RecognitionModeAvailability.Ready
        ? null
        : "mineru-cpu",
      SupportedOptions = [],
      Lifecycle = new Wire.RecognitionModeLifecycle
      {
        Kind = lifecycle,
        SupportsPreload = mineru,
        SupportsTtl = mineru,
        SupportsPinning = false,
        SupportsRelease = mineru,
      },
    };
  }

  private sealed class StubHotkeyRegistrar : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict)
    {
      conflict = null;
      return true;
    }

    public void Unregister() { }
  }

  private sealed class StubStartupRegistrar : IStartupRegistrar
  {
    public bool SetEnabled(bool enabled) => true;
  }

  private sealed class IdleInputService : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
  }

  private sealed class DefaultModeInferenceClient : InferenceClientStub
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

    public InferenceClientException? UpdateThrows { get; init; }

    public int UpdateCalls { get; private set; }

    public override Task<Wire.Health> GetHealthAsync(
      CancellationToken cancellationToken) => Task.FromResult(Health);

    public override Task<ResidencyStatus> GetResidencyAsync(
      CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());

    public override Task<SettingsSnapshot> GetSettingsAsync(
      CancellationToken cancellationToken) => Task.FromResult(Settings);

    public override Task<SettingsSnapshot> UpdateSettingsAsync(
      SettingsSnapshot settings,
      CancellationToken cancellationToken)
    {
      UpdateCalls++;
      if (UpdateThrows is not null)
      {
        throw UpdateThrows;
      }
      LastUpdate = settings;
      Settings = settings;
      return Task.FromResult(settings);
    }
  }
}
