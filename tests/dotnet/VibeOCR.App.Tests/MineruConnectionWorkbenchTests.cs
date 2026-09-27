using System.Text.Json;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Shell;
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
/// MinerU 连接的设置闭环契约：能力门控（ocr.mineru-remote-api.v1）、
/// extra.mineru_connection 读写、失败保持原生效配置，以及桥接命令与
/// 状态投影不泄漏 API Key 明文。
/// </summary>
public sealed class MineruConnectionWorkbenchTests
{
  [Fact]
  public async Task LoadSnapshotProjectsMineruConnectionFromBackendSettings()
  {
    var fake = new MineruInferenceClient
    {
      Health = MineruHealth(remoteCapability: true),
      Settings = RemoteConnectionSnapshot(),
    };
    var viewModel = new SettingsViewModel(fake);

    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(viewModel.MineruConnection);
    Assert.True(viewModel.MineruConnection.Supported);
    Assert.True(viewModel.MineruConnection.IsRemote);
    Assert.Equal("https://mineru.example.com", viewModel.MineruConnection.ApiUrl);
    Assert.True(viewModel.MineruConnection.HasApiKey);
  }

  [Fact]
  public async Task SetRemotePersistsConnectionAndReloadsCatalog()
  {
    var fake = new MineruInferenceClient
    {
      Health = MineruHealth(remoteCapability: true),
    };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);
    int healthCalls = fake.HealthCalls;

    await viewModel.SetMineruConnectionAsync(
      "remote",
      "https://mineru.example.com",
      "key-1",
      TestContext.Current.CancellationToken);

    JsonElement connection = fake.LastUpdate!.Extra["mineru_connection"];
    Assert.Equal("remote", connection.GetProperty("mode").GetString());
    Assert.Equal(
      "https://mineru.example.com",
      connection.GetProperty("api_url").GetString());
    Assert.Equal("key-1", connection.GetProperty("api_key").GetString());
    // 实际写入成功后回读刷新能力目录。
    Assert.True(fake.HealthCalls > healthCalls);
    Assert.Equal(1, fake.UpdateCalls);
    Assert.True(viewModel.MineruConnection?.IsRemote);
    Assert.Equal(
      "已保存 MinerU 远程配置（保存不验证服务连通性）",
      viewModel.Status);
    // API Key 明文不得进入用户可见状态文案。
    Assert.DoesNotContain("key-1", viewModel.Status);
  }

  [Fact]
  public async Task SetRemoteKeepsStoredKeyWhenApiKeyOmitted()
  {
    var fake = new MineruInferenceClient
    {
      Health = MineruHealth(remoteCapability: true),
      Settings = RemoteConnectionSnapshot(),
    };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    // 未编辑 Key：宿主读回当前设置后合并，不得清除已存凭据。
    await viewModel.SetMineruConnectionAsync(
      "remote",
      "https://new.example.com",
      null,
      TestContext.Current.CancellationToken);

    JsonElement connection = fake.LastUpdate!.Extra["mineru_connection"];
    Assert.Equal("https://new.example.com", connection.GetProperty("api_url").GetString());
    Assert.Equal("stored-key", connection.GetProperty("api_key").GetString());
    Assert.True(viewModel.MineruConnection?.HasApiKey);
  }

  [Fact]
  public async Task SetLocalWritesModeOnlyWithoutRemoteFields()
  {
    var fake = new MineruInferenceClient
    {
      Health = MineruHealth(remoteCapability: true),
    };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    await viewModel.SetMineruConnectionAsync(
      "local",
      apiUrl: "https://ignored.example.com",
      apiKey: "ignored",
      TestContext.Current.CancellationToken);

    JsonElement connection = fake.LastUpdate!.Extra["mineru_connection"];
    Assert.Equal("local", connection.GetProperty("mode").GetString());
    Assert.False(connection.TryGetProperty("api_url", out _));
    Assert.False(connection.TryGetProperty("api_key", out _));
    Assert.Equal("已保存 MinerU 本地配置", viewModel.Status);
    Assert.False(viewModel.MineruConnection?.IsRemote);
  }

  [Fact]
  public async Task OldBackendWithoutCapabilityCannotSaveRemoteConfiguration()
  {
    var fake = new MineruInferenceClient
    {
      Health = MineruHealth(remoteCapability: false),
    };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    await viewModel.SetMineruConnectionAsync(
      "remote",
      "https://mineru.example.com",
      string.Empty,
      TestContext.Current.CancellationToken);

    // 旧 Backend 不接受远程写入，也不得假成功。
    Assert.Equal(0, fake.UpdateCalls);
    Assert.Contains("ocr.mineru-remote-api.v1", viewModel.Status);
    Assert.False(viewModel.MineruConnection?.IsRemote);
  }

  [Fact]
  public async Task InvalidRemoteInputFailsClosedKeepingEffectiveConfig()
  {
    var fake = new MineruInferenceClient
    {
      Health = MineruHealth(remoteCapability: true),
      Settings = RemoteConnectionSnapshot(),
    };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    await viewModel.SetMineruConnectionAsync(
      "remote",
      "ftp://mineru.example.com",
      string.Empty,
      TestContext.Current.CancellationToken);

    Assert.Equal(0, fake.UpdateCalls);
    Assert.Contains("http(s)", viewModel.Status);
    // 失败保持原生效配置。
    Assert.Equal("https://mineru.example.com", viewModel.MineruConnection?.ApiUrl);
    Assert.True(viewModel.MineruConnection?.IsRemote);
  }

  [Fact]
  public async Task BackendRejectionKeepsEffectiveConfigAndLocalizesError()
  {
    var fake = new MineruInferenceClient
    {
      Health = MineruHealth(remoteCapability: true),
      UpdateThrows = new InferenceClientException(
        HttpV2ErrorCode.RuntimeCapabilityUnavailable,
        "capability missing",
        retryable: false),
    };
    var viewModel = new SettingsViewModel(fake);
    await viewModel.LoadSnapshotAsync(TestContext.Current.CancellationToken);

    await viewModel.SetMineruConnectionAsync(
      "remote",
      "https://mineru.example.com",
      string.Empty,
      TestContext.Current.CancellationToken);

    Assert.Equal(1, fake.UpdateCalls);
    Assert.Equal("当前 Backend 不支持该能力", viewModel.Status);
    Assert.False(viewModel.MineruConnection?.IsRemote);
  }

  [Fact]
  public void BridgeParsesMineruConnectionCommands()
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

    SetMineruConnectionCommand local = Assert.IsType<SetMineruConnectionCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        Command("setMineruConnection", """{"mode":"local"}"""), sessionId).Command);
    Assert.Equal("local", local.Mode);
    Assert.Null(local.ApiUrl);
    Assert.Null(local.ApiKey);

    SetMineruConnectionCommand remote = Assert.IsType<SetMineruConnectionCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        Command(
          "setMineruConnection",
          """{"mode":"remote","apiUrl":"https://mineru.example.com","apiKey":"key-1"}"""),
        sessionId).Command);
    Assert.Equal("remote", remote.Mode);
    Assert.Equal("https://mineru.example.com", remote.ApiUrl);
    Assert.Equal("key-1", remote.ApiKey);

    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        Command("setMineruConnection", """{"mode":"cluster"}"""), sessionId));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        Command(
          "setMineruConnection",
          """{"mode":"local","apiUrl":"https://mineru.example.com","apiKey":""}"""),
        sessionId));
    SetMineruConnectionCommand unchangedKey = Assert.IsType<SetMineruConnectionCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        Command(
          "setMineruConnection",
          """{"mode":"remote","apiUrl":"https://mineru.example.com"}"""),
        sessionId).Command);
    Assert.Null(unchangedKey.ApiKey);
    Assert.IsType<PrepareMineruConnectionCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        Command("prepareMineruConnection", "{}"), sessionId).Command);
    SetBatchTaskEngineCommand batch = Assert.IsType<SetBatchTaskEngineCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        JsonSerializer.Serialize(new
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
              scope = "batch",
              action = "setTaskEngine",
              arguments = new { engine = "mineru_document" },
            },
          },
        }), sessionId).Command);
    Assert.Equal("mineru_document", batch.Engine);
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        Command(
          "setMineruConnection",
          """{"mode":"remote","apiUrl":"https://mineru.example.com","apiKey":"a\nb"}"""),
        sessionId));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        Command(
          "setMineruConnection",
          """{"mode":"remote","apiUrl":"","apiKey":""}"""),
        sessionId));
  }

  [Fact]
  public void SettingsStateSerializesMineruConnectionWithoutSecrets()
  {
    var state = new SettingsWorkbenchState(
      WorkbenchTheme.Light,
      false,
      "settings.ready",
      "cpu",
      false,
      "Ctrl+Alt+Q",
      MineruConnection: new SettingsMineruConnectionState(
        true,
        "remote",
        "https://mineru.example.com",
        HasApiKey: true));
    string payload = WorkbenchBridgeCodec.SerializeState(
      Guid.NewGuid(),
      new WorkbenchStateEnvelope(3, "settings", WorkbenchStateChange.Replace, state));
    Assert.Contains(
      "\"mineruConnection\":{\"supported\":true,\"mode\":\"remote\","
        + "\"apiUrl\":\"https://mineru.example.com\",\"hasApiKey\":true}",
      payload);
  }

  [Fact]
  public async Task SetMineruConnectionCommandRoutesThroughTheWorkbenchHandler()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-mineru-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var fake = new MineruInferenceClient
      {
        Health = MineruHealth(remoteCapability: true),
      };
      var settings = new SettingsViewModel(fake);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(fake, new EmptyRecognitionInput()),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        () => new ShellViewModel(
          new StubHotkeyRegistrar(),
          new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore);

      WorkbenchCommandOutcome refresh = await handler.ExecuteAsync(
        new RefreshRuntimeCommand(),
        TestContext.Current.CancellationToken);
      Assert.Null(refresh.Error);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SetMineruConnectionCommand(
          "remote",
          "https://mineru.example.com",
          "key-1"),
        TestContext.Current.CancellationToken);

      Assert.Null(outcome.Error);
      SettingsWorkbenchState state = Assert.IsType<SettingsWorkbenchState>(
        Assert.Single(outcome.States));
      Assert.NotNull(state.MineruConnection);
      Assert.True(state.MineruConnection.Supported);
      Assert.Equal("remote", state.MineruConnection.Mode);
      Assert.Equal("https://mineru.example.com", state.MineruConnection.ApiUrl);
      Assert.True(state.MineruConnection.HasApiKey);
      Assert.Equal(1, fake.UpdateCalls);
      await handler.DisposeAsync();
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task RemoteConnectionChangePublishesReadyRecognitionCatalogAndNavigationKeepsIt(
    bool prepare)
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-mineru-catalog-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var fake = new MineruInferenceClient
      {
        Health = MineruModeHealth(ready: false),
        HealthAfterUpdate = prepare ? null : MineruModeHealth(ready: true),
        HealthAfterPreload = prepare ? MineruModeHealth(ready: true) : null,
        Settings = prepare ? RemoteConnectionSnapshot() : new SettingsSnapshot(),
      };
      var settings = new SettingsViewModel(fake);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(fake, new EmptyRecognitionInput { HasInput = true }),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore);
      await using var application = new WorkbenchApplication(
        ["recognition.engine"], WorkbenchRoute.Settings, handler);
      WorkbenchBootstrap initial = await application.BootstrapAsync(
        TestContext.Current.CancellationToken);
      AssertMineruMode(Assert.IsAssignableFrom<WorkbenchState>(
          initial.States.Single(state => state.Scope == "recognition").State),
        "preparation_required", requiresDownload: true);
      AssertMineruMode(Assert.IsAssignableFrom<WorkbenchState>(
          initial.States.Single(state => state.Scope == "batch").State),
        "preparation_required", requiresDownload: true);
      var completed = new TaskCompletionSource<RecognitionWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState { StatusCode: "recognition.completed" } result)
          completed.TrySetResult(result);
      };
      WorkbenchCommandReceipt started = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new SelectRecognitionImageCommand()),
        TestContext.Current.CancellationToken);
      Assert.True(started.Ok);
      RecognitionWorkbenchState previous = await completed.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.NotNull(previous.Input);
      Assert.NotNull(previous.Result);
      initial = await application.BootstrapAsync(TestContext.Current.CancellationToken);
      await using IAsyncEnumerator<WorkbenchStateEnvelope> updates = application
        .SubscribeAsync(initial.Revision, TestContext.Current.CancellationToken)
        .GetAsyncEnumerator(TestContext.Current.CancellationToken);

      WorkbenchCommand command = prepare
        ? new PrepareMineruConnectionCommand()
        : new SetMineruConnectionCommand("remote", "https://mineru.example.com", null);
      WorkbenchCommandReceipt changed = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), command),
        TestContext.Current.CancellationToken);
      Assert.True(changed.Ok);
      Assert.Equal(prepare ? 1 : 0, fake.PreloadCalls);
      RecognitionWorkbenchState? refreshed = null;
      while (await updates.MoveNextAsync().AsTask().WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
      {
        if (updates.Current.State is RecognitionWorkbenchState recognition)
        {
          refreshed = recognition;
          break;
        }
      }
      Assert.NotNull(refreshed);
      AssertMineruMode(refreshed, "ready", requiresDownload: false);
      Assert.Equal("recognition.completed", refreshed.StatusCode);
      Assert.NotNull(refreshed.Input);
      Assert.NotNull(refreshed.Result);
      BatchWorkbenchState? refreshedBatch = null;
      while (await updates.MoveNextAsync().AsTask().WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
      {
        if (updates.Current.State is BatchWorkbenchState batch)
        {
          refreshedBatch = batch;
          break;
        }
      }
      Assert.NotNull(refreshedBatch);
      AssertMineruMode(refreshedBatch, "ready", requiresDownload: false);

      WorkbenchCommandReceipt selected = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(),
          new SetTaskEngineCommand("mineru_document")),
        TestContext.Current.CancellationToken);
      Assert.True(selected.Ok);

      WorkbenchCommandReceipt navigated = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(),
          new NavigateWorkbenchCommand(WorkbenchRoute.Recognition)),
        TestContext.Current.CancellationToken);
      Assert.True(navigated.Ok);
      WorkbenchBootstrap afterNavigation = await application.BootstrapAsync(
        TestContext.Current.CancellationToken);
      Assert.Equal(WorkbenchRoute.Recognition, afterNavigation.Route);
      AssertMineruMode(Assert.IsAssignableFrom<WorkbenchState>(
          afterNavigation.States.Single(state => state.Scope == "recognition").State),
        "ready", requiresDownload: false);
      AssertMineruMode(Assert.IsAssignableFrom<WorkbenchState>(
          afterNavigation.States.Single(state => state.Scope == "batch").State),
        "ready", requiresDownload: false);
      BatchWorkbenchState batchAfterNavigation = Assert.IsType<BatchWorkbenchState>(
        afterNavigation.States.Single(state => state.Scope == "batch").State);
      Assert.False(Assert.Single(batchAfterNavigation.Engines!,
        engine => engine.Engine == "mineru_document").IsTaskOverride);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  private static void AssertMineruMode(
    WorkbenchState state, string availability, bool requiresDownload)
  {
    IReadOnlyList<RecognitionEngineChoice>? engines = state switch
    {
      RecognitionWorkbenchState recognition => recognition.Engines,
      BatchWorkbenchState batch => batch.Engines,
      _ => throw new InvalidOperationException("Expected recognition or batch state."),
    };
    RecognitionEngineChoice mineru = Assert.Single(engines!,
      engine => engine.Engine == "mineru_document");
    Assert.Equal(availability, mineru.Availability);
    Assert.Equal(requiresDownload, mineru.RequiresDownload);
  }

  private static Wire.Health MineruModeHealth(bool ready) => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-1",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities =
    [
      RuntimeSelectionService.MineruRemoteApiCapability,
      RuntimeSelectionService.RecognitionModesCapability,
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
            Mode(Wire.RecognitionModeId.RapidText, ready),
            Mode(Wire.RecognitionModeId.WindowsText, ready),
            Mode(Wire.RecognitionModeId.PaddleText, ready),
            Mode(Wire.RecognitionModeId.PaddleStructure, ready),
            Mode(Wire.RecognitionModeId.PaddleDocumentVl, ready),
            Mode(Wire.RecognitionModeId.MineruDocument, ready),
            Mode(Wire.RecognitionModeId.PaddleTable, ready),
            Mode(Wire.RecognitionModeId.PaddleFormula, ready),
          ],
        },
      },
    ],
  };

  private static Wire.RecognitionModeDescriptor Mode(Wire.RecognitionModeId id, bool ready)
  {
    var (family, pipeline, engine, provisioning, lifecycle) = id switch
    {
      Wire.RecognitionModeId.RapidText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Rapidocr,
        Wire.RecognitionModeProvisioning.BaseRuntime, Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.WindowsText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Windows,
        Wire.RecognitionModeProvisioning.OperatingSystem, Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.PaddleText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Paddleocr,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleStructure => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PPStructureV3, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleDocumentVl => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PaddleOCRVL, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.MineruDocument => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.MinerU, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.PaddleTable => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.TABLERECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleFormula => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.FORMULARECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent, Wire.RecognitionModeLifecycleKind.ModelResidency),
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
      Availability = mineru && !ready
        ? Wire.RecognitionModeAvailability.PreparationRequired
        : Wire.RecognitionModeAvailability.Ready,
      ReasonCode = mineru && !ready ? "runtime_component_missing" : null,
      RequiredComponent = mineru && !ready ? "mineru-cpu" : null,
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

  private static Wire.Health MineruHealth(bool remoteCapability) => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-1",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities = remoteCapability
      ? [RuntimeSelectionService.MineruRemoteApiCapability]
      : [],
  };

  private static SettingsSnapshot RemoteConnectionSnapshot() => new()
  {
    Extra = new Dictionary<string, JsonElement>
    {
      ["mineru_connection"] = JsonSerializer.SerializeToElement(new
      {
        mode = "remote",
        api_url = "https://mineru.example.com",
        api_key = "stored-key",
      }),
    },
  };

  private sealed class MineruInferenceClient : InferenceClientStub, IInferenceClient
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

    public Wire.Health? HealthAfterUpdate { get; init; }

    public Wire.Health? HealthAfterPreload { get; init; }

    public InferenceClientException? UpdateThrows { get; init; }

    public int UpdateCalls { get; private set; }

    public int HealthCalls { get; private set; }

    public int PreloadCalls { get; private set; }

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken) => Task.FromResult(new JobRef
      {
        JobId = "job-mineru-test",
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

    public override Task<JobUpdate> ObserveAsync(
      string jobId, int afterSequence, CancellationToken cancellationToken) =>
      Task.FromResult(new JobUpdate
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
            State = ItemState.Succeeded,
            Attempt = 1,
            PayloadType = "ocr.v1",
            Payload = new Dictionary<string, JsonElement>
            {
              ["raw_text"] = JsonSerializer.SerializeToElement("recognized text"),
            },
          },
        ],
        ThroughSequence = afterSequence,
      });

    public override Task<ResidencyStatus> GetResidencyAsync(
      CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken)
    {
      HealthCalls++;
      return Task.FromResult(Health);
    }

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
      if (HealthAfterUpdate is not null) Health = HealthAfterUpdate;
      return Task.FromResult(settings);
    }

    public Task<ResidencyStatus> PreloadRuntimeAsync(
      Wire.RuntimePreloadRequest request,
      CancellationToken cancellationToken)
    {
      PreloadCalls++;
      if (HealthAfterPreload is not null) Health = HealthAfterPreload;
      return Task.FromResult(new ResidencyStatus());
    }
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

  private sealed class EmptyRecognitionInput : IInputService
  {
    public bool HasInput { get; init; }

    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(HasInput
        ? new RecognitionInput([1, 2, 3, 4], "image/png", "image.png", "file")
        : null);

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => Task.FromResult<RecognitionInput?>(null);
  }
}
