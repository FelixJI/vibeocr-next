using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;
using System.Text.Json;
using System.Text;

namespace VibeOCR.App.Tests;

public sealed class WorkbenchBridgeCodecTests
{
  [Theory]
  [InlineData("rapidocr-cpu")]
  [InlineData("文字识别 CPU-2")]
  [InlineData("0123456789abcdef0123456789abcdef")]
  public void EnvironmentCommandsAcceptRuntimeReadableIds(string environmentId)
  {
    Guid sessionId = Guid.NewGuid();
    string arguments = JsonSerializer.Serialize(new { environmentId, recipe = "rapidocr-cpu" });
    var command = Assert.IsType<PreviewEnvironmentInstallCommand>(
      WorkbenchBridgeCodec.ParseCommand(CommandJson(sessionId, "settings", "previewEnvironmentInstall", arguments), sessionId).Command);
    Assert.Equal(environmentId, command.EnvironmentId);
  }

  [Theory]
  [InlineData("../environment")]
  [InlineData("..")]
  [InlineData("environment\\other")]
  [InlineData("C:environment")]
  [InlineData("environment.")]
  public void EnvironmentCommandsRejectPathSegments(string environmentId)
  {
    Guid sessionId = Guid.NewGuid();
    string arguments = JsonSerializer.Serialize(new { environmentId, recipe = "rapidocr-cpu" });
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(CommandJson(sessionId, "settings", "previewEnvironmentInstall", arguments), sessionId));
  }

  [Fact]
  public void EnvironmentPreviewBindsSelectedPackageSource()
  {
    Guid sessionId = Guid.NewGuid();
    string environmentId = Guid.NewGuid().ToString("N");
    string json = $$"""
      {
        "version": 2,
        "kind": "request",
        "id": "{{Guid.NewGuid()}}",
        "type": "app.command",
        "payload": {
          "sessionId": "{{sessionId}}",
          "command": {
            "scope": "settings",
            "action": "previewEnvironmentInstall",
            "arguments": {
              "environmentId": "{{environmentId}}",
              "recipe": "rapidocr-cpu",
              "sourceId": "pypi"
            }
          }
        }
      }
      """;
    var preview = Assert.IsType<PreviewEnvironmentInstallCommand>(
      WorkbenchBridgeCodec.ParseCommand(json, sessionId).Command);
    Assert.Equal("pypi", preview.SourceId);
    // 桥只校验 id 形状，不硬编码目录成员；目录成员由管理器 fail closed。
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(json.Replace("\"pypi\"", "\"un trusted!\""), sessionId));
  }

  [Fact]
  public void EnvironmentPreviewAndConfirmAllowFollowWithoutExplicitSource()
  {
    Guid sessionId = Guid.NewGuid();
    string environmentId = Guid.NewGuid().ToString("N");
    string previewJson = $$"""
      {
        "version": 2,
        "kind": "request",
        "id": "{{Guid.NewGuid()}}",
        "type": "app.command",
        "payload": {
          "sessionId": "{{sessionId}}",
          "command": {
            "scope": "settings",
            "action": "previewEnvironmentInstall",
            "arguments": {
              "environmentId": "{{environmentId}}",
              "recipe": "rapidocr-cpu"
            }
          }
        }
      }
      """;
    var preview = Assert.IsType<PreviewEnvironmentInstallCommand>(
      WorkbenchBridgeCodec.ParseCommand(previewJson, sessionId).Command);
    Assert.Null(preview.SourceId);

    string confirmJson = $$"""
      {
        "version": 2,
        "kind": "request",
        "id": "{{Guid.NewGuid()}}",
        "type": "app.command",
        "payload": {
          "sessionId": "{{sessionId}}",
          "command": {
            "scope": "settings",
            "action": "confirmEnvironmentInstall",
            "arguments": {
              "planId": "{{new string('a', 32)}}"
            }
          }
        }
      }
      """;
    var confirm = Assert.IsType<ConfirmEnvironmentInstallCommand>(
      WorkbenchBridgeCodec.ParseCommand(confirmJson, sessionId).Command);
    Assert.Null(confirm.SourceId);
  }

  [Fact]
  public void SetEnvironmentSourcesRoutesGlobalAndScopedSaves()
  {
    Guid sessionId = Guid.NewGuid();
    string environmentId = Guid.NewGuid().ToString("N");
    string globalJson = $$"""
      {
        "version": 2,
        "kind": "request",
        "id": "{{Guid.NewGuid()}}",
        "type": "app.command",
        "payload": {
          "sessionId": "{{sessionId}}",
          "command": {
            "scope": "settings",
            "action": "setEnvironmentSources",
            "arguments": {
              "packageSourceId": "pypi",
              "modelSourceId": null
            }
          }
        }
      }
      """;
    var global = Assert.IsType<SetEnvironmentSourcesCommand>(
      WorkbenchBridgeCodec.ParseCommand(globalJson, sessionId).Command);
    Assert.Null(global.EnvironmentId);
    Assert.Equal("pypi", global.PackageSourceId);
    Assert.Null(global.ModelSourceId);
    string independentJson = globalJson.Replace("\"modelSourceId\": null",
      "\"paddleocrModelSourceId\": \"paddleocr-bos\", \"mineruModelSourceId\": \"mineru-modelscope\"");
    var independent = Assert.IsType<SetEnvironmentSourcesCommand>(
      WorkbenchBridgeCodec.ParseCommand(independentJson, sessionId).Command);
    Assert.True(independent.IndependentModelSources);
    Assert.Equal("paddleocr-bos", independent.PaddleocrModelSourceId);
    Assert.Equal("mineru-modelscope", independent.MineruModelSourceId);
    Assert.Null(independent.ModelSourceId);


    string scopedJson = $$"""
      {
        "version": 2,
        "kind": "request",
        "id": "{{Guid.NewGuid()}}",
        "type": "app.command",
        "payload": {
          "sessionId": "{{sessionId}}",
          "command": {
            "scope": "settings",
            "action": "setEnvironmentSources",
            "arguments": {
              "environmentId": "{{environmentId}}",
              "packageSourceId": null,
              "modelSourceId": "modelscope"
            }
          }
        }
      }
      """;
    var scoped = Assert.IsType<SetEnvironmentSourcesCommand>(
      WorkbenchBridgeCodec.ParseCommand(scopedJson, sessionId).Command);
    Assert.Equal(environmentId, scoped.EnvironmentId);
    Assert.Null(scoped.PackageSourceId);
    Assert.Equal("modelscope", scoped.ModelSourceId);

    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        globalJson.Replace("\"modelscope\"", "\"bad id\"").Replace("\"pypi\"", "\"bad id\""),
        sessionId));
  }

  [Fact]
  public void SettingsStateCarriesActionHotkeysAndFloatingToolbar()
  {
    var state = new SettingsWorkbenchState(
      WorkbenchTheme.Light,
      false,
      "settings.ready",
      "cpu",
      false,
      HotkeyActions:
      [
        new SettingsHotkeyActionState(
          "screenshot_recognize",
          "快捷截图识别",
          "Ctrl+Alt+Q",
          RegisteredHotkey: null,
          "快捷键注册失败：该组合可能已被其他应用占用。",
          "Ctrl+Alt+Q"),
      ],
      FloatingToolbar: new SettingsFloatingToolbarState(
        true,
        "left",
        false,
        "userHidden",
        "无法保存悬浮工具栏设置，原设置已保留：disk full", PeekPixels: 8));
    using JsonDocument json = JsonDocument.Parse(WorkbenchBridgeCodec.SerializeState(Guid.NewGuid(),
      new WorkbenchStateEnvelope(1, "settings", WorkbenchStateChange.Replace, state)));
    JsonElement settings = json.RootElement.GetProperty("payload").GetProperty("state");
    JsonElement action = Assert.Single(settings.GetProperty("hotkeyActions").EnumerateArray());
    Assert.Equal("screenshot_recognize", action.GetProperty("actionId").GetString());
    Assert.Equal("快捷截图识别", action.GetProperty("displayName").GetString());
    Assert.Equal("Ctrl+Alt+Q", action.GetProperty("configuredHotkey").GetString());
    // 配置已存但未注册生效必须如实区分，不得冒称当前生效。
    Assert.Null(action.GetProperty("registeredHotkey").GetString());
    Assert.Contains("已被其他应用占用", action.GetProperty("error").GetString());
    Assert.Equal("Ctrl+Alt+Q", action.GetProperty("defaultHotkey").GetString());
    JsonElement toolbar = settings.GetProperty("floatingToolbar");
    Assert.True(toolbar.GetProperty("enabled").GetBoolean());
    Assert.Equal("left", toolbar.GetProperty("edge").GetString());
    Assert.False(toolbar.GetProperty("autoHide").GetBoolean());
    Assert.Equal(300, toolbar.GetProperty("lingerMs").GetInt32());
    Assert.Equal(8, toolbar.GetProperty("peekPixels").GetInt32());
    Assert.Equal("system", toolbar.GetProperty("theme").GetString());
    Assert.Equal("userHidden", toolbar.GetProperty("visibility").GetString());
    Assert.Contains(
      "原设置已保留",
      toolbar.GetProperty("error").GetString());
  }
  [Fact]
  public void SettingsStateProjectsEnvironmentCatalogHardwareAndCompatibility()
  {
    var state = new SettingsWorkbenchState(
      WorkbenchTheme.Light,
      false,
      "settings.ready",
      "cpu",
      false,
      EnvironmentRecipes:
      [
        new SettingsEnvironmentRecipeState(
          "rapidocr-cpu", "RapidOCR · CPU",
          ["text"], "cpu", "cpu", "3.13.15", "cp313", "win_amd64",
          "base", ["runtime_host", "rapidocr-base"], "locked-sha",
          ["rapidocr==3.7.0"], "bundled_pack", "product_bundle", "product_bundle"),
      ],
      EnvironmentHardware: new SettingsEnvironmentHardwareState(
        "unsupported", "nvidia_driver_incompatible", "527.00"),
      EnvironmentCompatibility: new SettingsEnvironmentCompatibilityState(
        "rapidocr-cpu",
        SelectedEnvironmentId: "abc",
        SelectedEnvironmentRevision: 2,
        SelectionReason: "active_environment",
        Environments:
        [
          new SettingsEnvironmentQueryMatchState(
            "abc", "用户环境", 2, "installed", Active: true, Selected: true,
            ReasonCode: "selected_active_environment"),
        ]));
    using JsonDocument json = JsonDocument.Parse(WorkbenchBridgeCodec.SerializeState(
      Guid.NewGuid(),
      new WorkbenchStateEnvelope(9, "settings", WorkbenchStateChange.Replace, state)));
    JsonElement settings = json.RootElement.GetProperty("payload").GetProperty("state");
    // 配方目录/硬件/兼容结果都只投影 Runtime 真值，前台不另算依赖或检测硬件。
    JsonElement recipe = Assert.Single(settings.GetProperty("environmentRecipes").EnumerateArray());
    Assert.Equal("rapidocr-cpu", recipe.GetProperty("id").GetString());
    Assert.Equal("RapidOCR · CPU", recipe.GetProperty("displayName").GetString());
    Assert.Equal("cpu", recipe.GetProperty("accelerator").GetString());
    JsonElement hardware = settings.GetProperty("environmentHardware");
    Assert.Equal("unsupported", hardware.GetProperty("nvidiaDriverStatus").GetString());
    Assert.Equal("nvidia_driver_incompatible", hardware.GetProperty("nvidiaDriverReason").GetString());
    Assert.Equal("527.00", hardware.GetProperty("nvidiaDriverVersion").GetString());
    JsonElement compatibility = settings.GetProperty("environmentCompatibility");
    Assert.Equal("rapidocr-cpu", compatibility.GetProperty("recipe").GetString());
    Assert.Equal("abc", compatibility.GetProperty("selectedEnvironmentId").GetString());
    JsonElement match = Assert.Single(compatibility.GetProperty("environments").EnumerateArray());
    Assert.Equal("selected_active_environment", match.GetProperty("reasonCode").GetString());
  }

  [Fact]
  public void FindCompatibleEnvironmentDecodesRecipeAndRejectsUnknownIds()
  {
    Guid sessionId = Guid.NewGuid();
    string json = $$"""
      {
        "version": 2,
        "kind": "request",
        "id": "{{Guid.NewGuid()}}",
        "type": "app.command",
        "payload": {
          "sessionId": "{{sessionId}}",
          "command": {
            "scope": "settings",
            "action": "findCompatibleEnvironment",
            "arguments": {
              "recipe": "rapidocr+mineru-cuda"
            }
          }
        }
      }
      """;
    var find = Assert.IsType<FindCompatibleEnvironmentCommand>(
      WorkbenchBridgeCodec.ParseCommand(json, sessionId).Command);
    Assert.Equal("rapidocr+mineru-cuda", find.Recipe);
    // 桥只做协议 id 类型守卫；未知输入拒绝，不降 input validation。
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        json.Replace("rapidocr+mineru-cuda", "made-up-recipe"), sessionId));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        json.Replace("\"recipe\": \"rapidocr+mineru-cuda\"", "\"environmentId\": \"abc\""),
        sessionId));
  }

  [Fact]
  public void SettingsStateProjectsProgressActivityAndNullableBackend()
  {
    var state = new SettingsWorkbenchState(
      WorkbenchTheme.Light,
      false,
      "settings.ready",
      Backend: null,
      StartupEnabled: false);
    Assert.False(state.ProgressActive);
    string payload = WorkbenchBridgeCodec.SerializeState(
      Guid.NewGuid(),
      new WorkbenchStateEnvelope(4, "settings", WorkbenchStateChange.Replace, state));
    // 未读取真实快照：backend 为 null，空闲无活动操作。
    Assert.Contains("\"backend\":null", payload);
    Assert.Contains("\"progressActive\":false", payload);

    string activePayload = WorkbenchBridgeCodec.SerializeState(
      Guid.NewGuid(),
      new WorkbenchStateEnvelope(5, "settings", WorkbenchStateChange.Replace,
        state with { Backend = "nvidia_cuda", ProgressActive = true, ProgressPercent = 62.5 }));
    Assert.Contains("\"backend\":\"nvidia_cuda\"", activePayload);
    Assert.Contains("\"progressActive\":true", activePayload);
    Assert.Contains("\"progressPercent\":62.5", activePayload);
  }

  [Fact]
  public void ParseCommandProducesTypedNavigateCommand()
  {
    Guid sessionId = Guid.NewGuid();
    Guid commandId = Guid.NewGuid();
    string json = $$"""
      {
        "version": 2,
        "kind": "request",
        "id": "{{commandId}}",
        "type": "app.command",
        "payload": {
          "sessionId": "{{sessionId}}",
          "command": {
            "scope": "shell",
            "action": "navigate",
            "arguments": { "route": "pdf" }
          }
        }
      }
      """;

    WorkbenchCommandEnvelope envelope = WorkbenchBridgeCodec.ParseCommand(
      json,
      sessionId);

    Assert.Equal(commandId, envelope.Id);
    NavigateWorkbenchCommand command = Assert.IsType<NavigateWorkbenchCommand>(
      envelope.Command);
    Assert.Equal(WorkbenchRoute.Pdf, command.Route);
  }

  [Fact]
  public void ParseBootstrapRequiresAnExactEmptyRequest()
  {
    Guid requestId = Guid.NewGuid();
    string json = $$"""
      {
        "version": 2,
        "kind": "request",
        "id": "{{requestId}}",
        "type": "app.bootstrap",
        "payload": {}
      }
      """;

    Assert.Equal(requestId, WorkbenchBridgeCodec.ParseBootstrapRequest(json));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseBootstrapRequest(json.Replace("{}", "{\"extra\":true}")));
  }

  [Fact]
  public async Task SerializeBootstrapMatchesTheWebContract()
  {
    await using var application = new WorkbenchApplication(
      ["recognition.capture"],
      WorkbenchRoute.Recognition);
    WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
      TestContext.Current.CancellationToken);

    string json = WorkbenchBridgeCodec.SerializeBootstrap(
      Guid.Parse("11111111-1111-1111-1111-111111111111"),
      bootstrap);
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement root = document.RootElement;

    Assert.Equal("response", root.GetProperty("kind").GetString());
    Assert.Equal("app.bootstrap", root.GetProperty("type").GetString());
    JsonElement payload = root.GetProperty("payload");
    Assert.Equal(bootstrap.SessionId.ToString(), payload.GetProperty("sessionId").GetString());
    Assert.Equal("recognition", payload.GetProperty("route").GetString());
    Assert.Equal("system", payload.GetProperty("theme").GetString());
    Assert.Equal("recognition.capture", payload.GetProperty("capabilities")[0].GetString());
    Assert.Equal(JsonValueKind.Object, payload.GetProperty("features").ValueKind);
  }

  [Fact]
  public void SerializeReceiptAndStateKeepCorrelationAndRevision()
  {
    Guid id = Guid.NewGuid();
    Guid sessionId = Guid.NewGuid();
    var receipt = new WorkbenchCommandReceipt(id, 7, null);
    var state = new WorkbenchStateEnvelope(
      7,
      "shell",
      WorkbenchStateChange.Replace,
      new ShellWorkbenchState(WorkbenchRoute.Diagnostics));

    using JsonDocument response = JsonDocument.Parse(
      WorkbenchBridgeCodec.SerializeReceipt(receipt));
    Assert.Equal(id.ToString(), response.RootElement.GetProperty("id").GetString());
    Assert.True(response.RootElement.GetProperty("payload").GetProperty("ok").GetBoolean());

    using JsonDocument @event = JsonDocument.Parse(
      WorkbenchBridgeCodec.SerializeState(sessionId, state));
    JsonElement payload = @event.RootElement.GetProperty("payload");
    Assert.Equal(sessionId.ToString(), payload.GetProperty("sessionId").GetString());
    Assert.Equal(7, payload.GetProperty("revision").GetInt64());
    Assert.Equal("diagnostics", payload.GetProperty("state").GetProperty("route").GetString());
  }

  [Fact]
  public void ParseScreenshotSessionCommandsValidateSessionFields()
  {
    Guid sessionId = Guid.NewGuid();
    const string resourceUri =
      "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef";

    NotifyScreenshotSessionRevisionCommand notify = Assert.IsType<NotifyScreenshotSessionRevisionCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "notifyScreenshotRevision",
          $$"""{"sessionId":"{{sessionId}}","revision":3}"""),
        sessionId).Command);
    Assert.Equal(sessionId, notify.SessionId);
    Assert.Equal(3, notify.Revision);

    RecognizeScreenshotImageCommand recognize = Assert.IsType<RecognizeScreenshotImageCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "recognizeScreenshotImage",
          $$"""{"resourceUri":"{{resourceUri}}","sessionId":"{{sessionId}}","revision":3,"excludeBoxes":[{"x":0,"y":0,"width":500,"height":200}]}"""),
        sessionId).Command);
    Assert.Equal(resourceUri, recognize.ResourceUri);
    Assert.Equal(sessionId, recognize.SessionId);
    Assert.Equal(3, recognize.Revision);
    Assert.Single(recognize.ExcludeBoxes);
    Assert.Equal(500, recognize.ExcludeBoxes[0].Width);

    CopyScreenshotImageCommand copy = Assert.IsType<CopyScreenshotImageCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "copyScreenshotImage",
          $$"""{"resourceUri":"{{resourceUri}}","sessionId":"{{sessionId}}","revision":0}"""),
        sessionId).Command);
    Assert.Equal(0, copy.Revision);

    SaveScreenshotImageCommand save = Assert.IsType<SaveScreenshotImageCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "saveScreenshotImage",
          $$"""{"resourceUri":"{{resourceUri}}","sessionId":"{{sessionId}}","revision":0}"""),
        sessionId).Command);
    Assert.Equal(sessionId, save.SessionId);

    // 负数/越界 revision、缺失字段与越权 URI 一律 fail closed。
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "notifyScreenshotRevision",
          $$"""{"sessionId":"{{sessionId}}","revision":-1}"""),
        sessionId));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "copyScreenshotImage",
          $$"""{"sessionId":"{{sessionId}}","revision":0}"""),
        sessionId));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "recognizeScreenshotImage",
          $$"""{"resourceUri":"file:///tmp/out.png","sessionId":"{{sessionId}}","revision":0,"excludeBoxes":[]}"""),
        sessionId));
  }

  [Fact]
  public void SerializeRecognitionStateCarriesScreenshotSession()
  {
    var state = new RecognitionWorkbenchState(
      false,
      "recognition.session",
      Input: new WorkbenchResourceReference(
        "https://app.vibeocr/__resource/session-input",
        "image/png",
        4),
      Result: null,
      ScreenshotSession: new RecognitionScreenshotSessionState(
        "0123456789abcdef0123456789abcdef",
        7,
        true));
    using JsonDocument json = JsonDocument.Parse(WorkbenchBridgeCodec.SerializeState(
      Guid.NewGuid(),
      new WorkbenchStateEnvelope(1, "recognition", WorkbenchStateChange.Replace, state)));
    JsonElement session = json.RootElement.GetProperty("payload").GetProperty("state").GetProperty("screenshotSession");
    Assert.Equal("0123456789abcdef0123456789abcdef", session.GetProperty("sessionId").GetString());
    Assert.Equal(7, session.GetProperty("revision").GetInt64());
    Assert.True(session.GetProperty("textSelectionRequested").GetBoolean());
  }

  [Fact]
  public void ParseCommandSupportsTheClosedWebActionSet()
  {
    Guid sessionId = Guid.NewGuid();
    (string Scope, string Action, string Arguments, Type Type)[] cases =
    [
      ("recognition", "selectImage", "{}", typeof(SelectRecognitionImageCommand)),
      ("recognition", "readClipboard", "{}", typeof(ReadRecognitionClipboardCommand)),
      ("recognition", "captureScreen", "{}", typeof(CaptureRecognitionScreenCommand)),
      ("recognition", "captureScreenshotSession", "{}", typeof(CaptureScreenshotSessionCommand)),
      ("recognition", "captureScrollingScreenshot", "{}", typeof(CaptureScrollingScreenshotCommand)),
      ("recognition", "captureScreenshotTextSession", "{}", typeof(CaptureScreenshotTextSessionCommand)),
      ("recognition", "closeScreenshotSession", "{}", typeof(CloseScreenshotSessionCommand)),
      ("recognition", "copyAnnotatedImage", "{\"resourceUri\":\"https://app.vibeocr/__annotation/00000000000000000000000000000000\"}", typeof(CopyAnnotatedImageCommand)),
      ("recognition", "saveAnnotatedImage", "{\"resourceUri\":\"https://app.vibeocr/__annotation/11111111111111111111111111111111\"}", typeof(SaveAnnotatedImageCommand)),
      ("recognition", "copyStructured", "{\"resourceUri\":\"https://app.vibeocr/__resource/22222222222222222222222222222222\",\"blockIndex\":1,\"format\":\"latex\"}", typeof(CopyStructuredResultCommand)),
      ("batch", "addFiles", "{}", typeof(AddBatchFilesCommand)),
      ("batch", "exportAll", "{\"format\":\"markdown\"}", typeof(ExportBatchCommand)),
      ("batch", "setWindow", "{\"start\":40}", typeof(SetBatchWindowCommand)),
      ("pdf", "open", "{}", typeof(OpenPdfCommand)),
      ("pdf", "rotate", "{}", typeof(RotatePdfCommand)),
      ("pdf", "setWindow", "{\"start\":64}", typeof(SetPdfWindowCommand)),
      ("qrcode", "generate", "{\"text\":\"hello\"}", typeof(GenerateQrCodeCommand)),
      ("qrcode", "decode", "{}", typeof(DecodeQrCodeCommand)),
      ("qrcode", "cancel", "{}", typeof(CancelQrCodeCommand)),
      ("about", "openProject", "{}", typeof(OpenProjectPageCommand)),
      ("settings", "refreshRuntime", "{}", typeof(RefreshRuntimeCommand)),
      ("settings", "beginHotkeyRecording", "{\"recordingId\":\"12345678-1234-1234-1234-123456789abc\"}", typeof(BeginHotkeyRecordingCommand)),
      ("settings", "endHotkeyRecording", "{\"recordingId\":\"12345678-1234-1234-1234-123456789abc\"}", typeof(EndHotkeyRecordingCommand)),
      ("settings", "setTheme", "{\"theme\":\"dark\"}", typeof(SetThemeCommand)),
      ("settings", "setActionHotkey", "{\"actionId\":\"clipboard_recognize\",\"hotkey\":\"Ctrl+Alt+C\"}", typeof(SetActionHotkeyCommand)),
      ("settings", "resetActionHotkey", "{\"actionId\":\"screenshot_recognize\"}", typeof(ResetActionHotkeyCommand)),
      ("settings", "setFloatingToolbarEnabled", "{\"enabled\":true}", typeof(SetFloatingToolbarEnabledCommand)),
      ("settings", "setFloatingToolbarLayout", "{\"edge\":\"left\",\"autoHide\":false}", typeof(SetFloatingToolbarLayoutCommand)),
      ("settings", "setFloatingToolbarPreferences", "{\"lingerMs\":100,\"theme\":\"light\"}", typeof(SetFloatingToolbarPreferencesCommand)),
      ("settings", "showFloatingToolbar", "{}", typeof(ShowFloatingToolbarCommand)),
      ("settings", "hideFloatingToolbar", "{}", typeof(HideFloatingToolbarCommand)),
      ("update", "check", "{}", typeof(CheckUpdateCommand)),
      ("diagnostics", "export", "{}", typeof(ExportDiagnosticsCommand)),
      ("diagnostics", "copy", "{}", typeof(CopyDiagnosticsCommand)),
    ];

    foreach ((string scope, string action, string arguments, Type type) in cases)
    {
      using JsonDocument argumentDocument = JsonDocument.Parse(arguments);
      string json = JsonSerializer.Serialize(new
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
            scope,
            action,
            arguments = argumentDocument.RootElement,
          },
        },
      });
      Assert.IsType(type, WorkbenchBridgeCodec.ParseCommand(json, sessionId).Command);
    }
  }

  [Fact]
  public void ParseAnnotatedImageCommandsAcceptOnlyOpaqueSameOriginUris()
  {
    Guid sessionId = Guid.NewGuid();
    const string resourceUri =
      "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef";
    CopyAnnotatedImageCommand copy = Assert.IsType<CopyAnnotatedImageCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "copyAnnotatedImage",
          $$"""{"resourceUri":"{{resourceUri}}"}"""),
        sessionId).Command);
    Assert.Equal(resourceUri, copy.ResourceUri);

    string[] invalid =
    [
      "C:/Users/example/annotation.png",
      "data:image/png;base64,iVBORw0KGgo=",
      "https://app.vibeocr/__resource/0123456789abcdef0123456789abcdef",
      "https://app.vibeocr/__annotation/0123456789abcdef0123456789abcdef?download=1",
    ];
    foreach (string value in invalid)
    {
      Assert.Throws<WorkbenchBridgeProtocolException>(() =>
        WorkbenchBridgeCodec.ParseCommand(
          CommandJson(
            sessionId,
            "recognition",
            "saveAnnotatedImage",
            JsonSerializer.Serialize(new { resourceUri = value })),
          sessionId));
    }
  }

  [Fact]
  public void ParseCommandAcceptsAPathFreeBatchReorder()
  {
    Guid sessionId = Guid.NewGuid();
    Guid itemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    string json = CommandJson(
      sessionId,
      "batch",
      "moveItem",
      $$"""{"itemId":"{{itemId}}","delta":-1}""");

    MoveBatchItemCommand command = Assert.IsType<MoveBatchItemCommand>(
      WorkbenchBridgeCodec.ParseCommand(json, sessionId).Command);

    Assert.Equal(itemId, command.ItemId);
    Assert.Equal(-1, command.Delta);
    Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ParseCommandRejectsRemovedBatchConcurrencyAction()
  {
    Guid sessionId = Guid.NewGuid();
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(sessionId, "batch", "setConcurrency", "{\"concurrency\":2}"),
        sessionId));
  }

  [Theory]
  [InlineData("docx")]
  [InlineData("xlsx")]
  public void ParseCommandAcceptsClassicDocumentExportFormats(string format)
  {
    Guid sessionId = Guid.NewGuid();
    ExportRecognitionResultCommand command = Assert.IsType<ExportRecognitionResultCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "export",
          $$"""{"format":"{{format}}"}"""),
        sessionId).Command);

    Assert.Equal(format, command.Format);
  }

  [Fact]
  public void ParseCodeOptionsAndRejectInvalidOrExtraFields()
  {
    Guid session = Guid.NewGuid();
    GenerateQrCodeCommand generate = Assert.IsType<GenerateQrCodeCommand>(WorkbenchBridgeCodec.ParseCommand(
      CommandJson(session, "qrcode", "generate", "{\"text\":\"590123412345\",\"format\":\"ean13\",\"captionMode\":\"custom\",\"captionText\":\"说明\"}"), session).Command);
    Assert.Equal(new GenerateQrCodeCommand("590123412345", "ean13", "custom", "说明"), generate);
    Assert.True(Assert.IsType<DecodeCurrentQrCodeCommand>(WorkbenchBridgeCodec.ParseCommand(
      CommandJson(session, "qrcode", "decodeCurrent", "{\"force\":true}"), session).Command).Force);
    Assert.IsType<CopyQrCodeImageCommand>(WorkbenchBridgeCodec.ParseCommand(
      CommandJson(session, "qrcode", "copyImage", "{}"), session).Command);
    foreach (string arguments in new[] {
      "{\"text\":\"x\",\"format\":\"unknown\",\"captionMode\":\"off\",\"captionText\":\"\"}",
      "{\"text\":\"x\",\"format\":\"qrcode\",\"captionMode\":\"unknown\",\"captionText\":\"\"}",
      "{\"text\":\"x\",\"format\":\"qrcode\",\"captionMode\":\"off\",\"captionText\":\"\",\"path\":\"x\"}" })
      Assert.Throws<WorkbenchBridgeProtocolException>(() => WorkbenchBridgeCodec.ParseCommand(
        CommandJson(session, "qrcode", "generate", arguments), session));
  }

  [Fact]
  public void ParseCommandAcceptsPdfSelectionAndOnlySafeQrUrls()
  {
    Guid sessionId = Guid.NewGuid();
    SelectPdfPagesCommand selection = Assert.IsType<SelectPdfPagesCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(sessionId, "pdf", "selectPages", "{\"pages\":[0,2,7]}"),
        sessionId).Command);
    Assert.Equal([0, 2, 7], selection.Pages);

    OpenQrCodeUrlCommand open = Assert.IsType<OpenQrCodeUrlCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "qrcode",
          "openUrl",
          "{\"url\":\"https://example.test/result\"}"),
        sessionId).Command);
    Assert.Equal("https://example.test/result", open.Url);
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "qrcode",
          "openUrl",
          "{\"url\":\"javascript:alert(1)\"}"),
        sessionId));
  }

  [Fact]
  public void SerializeFeatureStatesExposeOpaqueDataWithoutLocalPaths()
  {
    Guid sessionId = Guid.NewGuid();
    var batch = new BatchWorkbenchState(
      false,
      1,
      1,
      0,
      [new BatchWorkbenchItem(Guid.NewGuid(), "invoice.png", "batch.item.completed", "合计 42")]);
    string json = WorkbenchBridgeCodec.SerializeState(
      sessionId,
      new WorkbenchStateEnvelope(4, "batch", WorkbenchStateChange.Replace, batch));

    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement state = document.RootElement.GetProperty("payload").GetProperty("state");
    Assert.Equal("invoice.png", state.GetProperty("items")[0].GetProperty("name").GetString());
    Assert.False(state.TryGetProperty("concurrency", out _));
    Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("base64", json, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ParseStructuredCopyAcceptsOnlyOpaqueResourcesAndKnownFormats()
  {
    Guid sessionId = Guid.NewGuid();
    CopyStructuredResultCommand command = Assert.IsType<CopyStructuredResultCommand>(
      WorkbenchBridgeCodec.ParseCommand(
        CommandJson(
          sessionId,
          "recognition",
          "copyStructured",
          """{"resourceUri":"https://app.vibeocr/__resource/33333333333333333333333333333333","blockIndex":0,"format":"table"}"""),
        sessionId).Command);
    Assert.Equal(
      "https://app.vibeocr/__resource/33333333333333333333333333333333",
      command.ResourceUri);
    Assert.Equal(0, command.BlockIndex);
    Assert.Equal("table", command.Format);

    string[] invalid =
    [
      """{"resourceUri":"https://app.vibeocr/__annotation/33333333333333333333333333333333","blockIndex":0,"format":"table"}""",
      """{"resourceUri":"file:///tmp/out.json","blockIndex":0,"format":"table"}""",
      """{"resourceUri":"https://app.vibeocr/__resource/33333333333333333333333333333333","blockIndex":-1,"format":"table"}""",
      """{"resourceUri":"https://app.vibeocr/__resource/33333333333333333333333333333333","blockIndex":0,"format":"html"}""",
      """{"resourceUri":"https://app.vibeocr/__resource/33333333333333333333333333333333","format":"table"}""",
    ];
    foreach (string arguments in invalid)
    {
      Assert.Throws<WorkbenchBridgeProtocolException>(() =>
        WorkbenchBridgeCodec.ParseCommand(
          CommandJson(sessionId, "recognition", "copyStructured", arguments),
          sessionId));
    }
  }

  [Fact]
  public void SerializeCollectionWindowsAndAboutMetadata()
  {
    Guid sessionId = Guid.NewGuid();
    string batchJson = WorkbenchBridgeCodec.SerializeState(
      sessionId,
      new WorkbenchStateEnvelope(
        5,
        "batch",
        WorkbenchStateChange.Replace,
        new BatchWorkbenchState(false, 80, 0, 0, [], 40)));
    using JsonDocument batch = JsonDocument.Parse(batchJson);
    Assert.Equal(
      40,
      batch.RootElement.GetProperty("payload").GetProperty("state")
        .GetProperty("windowStart").GetInt32());
    // 结构化预览与导出不完整标志必须进入 wire，否则 Web 端无法呈现。
    string structuredBatchJson = WorkbenchBridgeCodec.SerializeState(
      sessionId,
      new WorkbenchStateEnvelope(
        7,
        "batch",
        WorkbenchStateChange.Replace,
        new BatchWorkbenchState(
          false,
          1,
          1,
          0,
          [new BatchWorkbenchItem(
            Guid.NewGuid(),
            "table.png",
            "batch.item.completed",
            "表格",
            new WorkbenchResourceReference(
              "https://app.vibeocr/__resource/44444444444444444444444444444444",
              "application/json; charset=utf-8",
              128))],
          ExportIncomplete: true)));
    using JsonDocument structuredBatch = JsonDocument.Parse(structuredBatchJson);
    JsonElement batchState = structuredBatch.RootElement
      .GetProperty("payload").GetProperty("state");
    Assert.Equal(
      "https://app.vibeocr/__resource/44444444444444444444444444444444",
      batchState.GetProperty("items")[0].GetProperty("structuredResult")
        .GetProperty("url").GetString());
    Assert.True(batchState.GetProperty("exportIncomplete").GetBoolean());

    string pdfJson = WorkbenchBridgeCodec.SerializeState(
      sessionId,
      new WorkbenchStateEnvelope(
        8,
        "pdf",
        WorkbenchStateChange.Replace,
        new PdfWorkbenchState(
          false,
          "pdf.open",
          1,
          0,
          [0],
          [new PdfWorkbenchPage(
            0,
            "pdf.page.done",
            null,
            new WorkbenchResourceReference(
              "https://app.vibeocr/__resource/55555555555555555555555555555555",
              "application/json; charset=utf-8",
              64))],
          Engines: [new RecognitionEngineChoice(
            "paddle_table",
            "表格",
            true,
            true,
            "ready",
            false)],
          TaskEngine: "paddle_table")));
    using JsonDocument pdf = JsonDocument.Parse(pdfJson);
    JsonElement pdfState = pdf.RootElement.GetProperty("payload").GetProperty("state");
    Assert.Equal(
      "https://app.vibeocr/__resource/55555555555555555555555555555555",
      pdfState.GetProperty("pages")[0].GetProperty("structuredResult")
        .GetProperty("url").GetString());
    Assert.Equal(
      "paddle_table",
      pdfState.GetProperty("taskEngine").GetString());
    Assert.Equal(
      "paddle_table",
      pdfState.GetProperty("engines")[0].GetProperty("engine").GetString());

    string recognitionJson = WorkbenchBridgeCodec.SerializeState(
      sessionId,
      new WorkbenchStateEnvelope(
        9,
        "recognition",
        WorkbenchStateChange.Replace,
        new RecognitionWorkbenchState(
          false,
          "recognition.completed",
          StructuredResult: new WorkbenchResourceReference(
            "https://app.vibeocr/__resource/66666666666666666666666666666666",
            "application/json; charset=utf-8",
            32))));
    using JsonDocument recognition = JsonDocument.Parse(recognitionJson);
    Assert.Equal(
      "https://app.vibeocr/__resource/66666666666666666666666666666666",
      recognition.RootElement.GetProperty("payload").GetProperty("state")
        .GetProperty("structuredResult").GetProperty("url").GetString());

    string aboutJson = WorkbenchBridgeCodec.SerializeState(
      sessionId,
      new WorkbenchStateEnvelope(
        6,
        "about",
        WorkbenchStateChange.Replace,
        new AboutWorkbenchState(
          "0.2.0",
          "Proprietary",
          "https://github.com/felji/VibeOCR")));
    using JsonDocument about = JsonDocument.Parse(aboutJson);
    JsonElement state = about.RootElement.GetProperty("payload").GetProperty("state");
    Assert.Equal("0.2.0", state.GetProperty("version").GetString());
    Assert.Equal("Proprietary", state.GetProperty("license").GetString());
  }

  [Fact]
  public void BoundedFeatureWindowFitsTheBridgeForWorstCaseUnicode()
  {
    BatchWorkbenchItem[] items = Enumerable.Range(0, 40)
      .Select(index => new BatchWorkbenchItem(
        Guid.NewGuid(),
        new string('文', 80),
        "batch.item.completed",
        new string('字', 120)))
      .ToArray();
    string json = WorkbenchBridgeCodec.SerializeState(
      Guid.NewGuid(),
      new WorkbenchStateEnvelope(
        99,
        "batch",
        WorkbenchStateChange.Replace,
        new BatchWorkbenchState(false, 500, 40, 0, items)));

    Assert.True(Encoding.UTF8.GetByteCount(json) < WorkbenchBridgeCodec.MaxMessageBytes);
  }

  [Fact]
  public void ShellActionCommandsRejectInvalidEdgesHotkeysAndActionIds()
  {
    Guid sessionId = Guid.NewGuid();
    (string Action, string Arguments)[] invalid =
    [
      ("setFloatingToolbarLayout", "{\"edge\":\"diagonal\",\"autoHide\":true}"),
      ("setFloatingToolbarPreferences", "{\"lingerMs\":99,\"theme\":\"system\"}"),
      ("setFloatingToolbarPreferences", "{\"lingerMs\":5001,\"theme\":\"dark\"}"),
      ("setFloatingToolbarPreferences", "{\"lingerMs\":300.5,\"theme\":\"light\"}"),
      ("setFloatingToolbarPreferences", "{\"lingerMs\":300,\"theme\":\"unknown\"}"),
      ("setFloatingToolbarPreferences", "{}"),
      ("setFloatingToolbarPreferences", "{\"peekPixels\":0}"),
      ("setFloatingToolbarPreferences", "{\"peekPixels\":21}"),
      ("setFloatingToolbarPreferences", "{\"peekPixels\":2.5}"),
      ("setFloatingToolbarPreferences", "{\"peekPixels\":null}"),
      ("setFloatingToolbarPreferences", "{\"lingerMs\":null}"),
      ("setFloatingToolbarPreferences", "{\"theme\":null}"),
      ("setFloatingToolbarPreferences", "{\"lingerMs\":99}"),
      ("setFloatingToolbarPreferences", "{\"lingerMs\":5001}"),
      ("setFloatingToolbarPreferences", "{\"lingerMs\":300.5}"),
      ("setFloatingToolbarPreferences", "{\"theme\":\"unknown\"}"),
      ("setFloatingToolbarPreferences", "{\"theme\":\"dark\",\"enabled\":true}"),
      ("setActionHotkey", "{\"actionId\":\"clipboard_recognize\",\"hotkey\":\"\"}"),
      ("setActionHotkey", "{\"actionId\":\" \"}"),
      ("resetActionHotkey", "{\"actionId\":\"\"}"),
      ("beginHotkeyRecording", "{\"recordingId\":\"bad-id\"}"),
      ("endHotkeyRecording", "{}"),
      ("endHotkeyRecording", "{\"recordingId\":\"12345678-1234-1234-1234-123456789abc\",\"extra\":true}"),
    ];

    foreach ((string action, string arguments) in invalid)
    {
      Assert.Throws<WorkbenchBridgeProtocolException>(() =>
        WorkbenchBridgeCodec.ParseCommand(
          CommandJson(sessionId, "settings", action, arguments),
          sessionId));
    }

    // 禁用快捷键不带 hotkey 字段是合法形状。
    WorkbenchCommandEnvelope disable = WorkbenchBridgeCodec.ParseCommand(
      CommandJson(
        sessionId,
        "settings",
        "setActionHotkey",
        "{\"actionId\":\"clipboard_recognize\"}"),
      sessionId);
    SetActionHotkeyCommand command = Assert.IsType<SetActionHotkeyCommand>(disable.Command);
    Assert.Equal("clipboard_recognize", command.ActionId);
    Assert.Null(command.Hotkey);
  }

  [Theory]
  [InlineData("{\"lingerMs\":100}", 100, null)]
  [InlineData("{\"lingerMs\":5000}", 5000, null)]
  [InlineData("{\"theme\":\"system\"}", null, "system")]
  [InlineData("{\"theme\":\"light\"}", null, "light")]
  [InlineData("{\"theme\":\"dark\"}", null, "dark")]
  public void ToolbarPreferencesAcceptSingleFieldPatches(string arguments, int? lingerMs, string? theme)
  {
    Guid sessionId = Guid.NewGuid();
    WorkbenchCommandEnvelope envelope = WorkbenchBridgeCodec.ParseCommand(
      CommandJson(sessionId, "settings", "setFloatingToolbarPreferences", arguments), sessionId);
    SetFloatingToolbarPreferencesCommand command = Assert.IsType<SetFloatingToolbarPreferencesCommand>(envelope.Command);
    Assert.Equal(lingerMs, command.LingerMs);
    Assert.Equal(theme, command.Theme);
  }

  [Fact]
  public void ToolbarPreferencesAcceptPeekPixelsPatch()
  {
    Guid sessionId = Guid.NewGuid();
    WorkbenchCommandEnvelope envelope = WorkbenchBridgeCodec.ParseCommand(
      CommandJson(sessionId, "settings", "setFloatingToolbarPreferences", "{\"peekPixels\":8}"), sessionId);
    var command = Assert.IsType<SetFloatingToolbarPreferencesCommand>(envelope.Command);
    Assert.Equal(8, command.PeekPixels);
    Assert.Null(command.LingerMs);
    Assert.Null(command.Theme);
  }

  private static string CommandJson(
    Guid sessionId,
    string scope,
    string action,
    string arguments)
  {
    using JsonDocument argumentDocument = JsonDocument.Parse(arguments);
    return JsonSerializer.Serialize(new
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
          scope,
          action,
          arguments = argumentDocument.RootElement,
        },
      },
    });
  }
}
