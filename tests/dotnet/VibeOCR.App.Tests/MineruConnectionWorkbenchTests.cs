using System.Text.Json;
using VibeOCR.App.Features.Settings;
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
        static () => throw new InvalidOperationException(),
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

  private sealed class MineruInferenceClient : InferenceClientStub
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

    public int HealthCalls { get; private set; }

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
      return Task.FromResult(settings);
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
}
