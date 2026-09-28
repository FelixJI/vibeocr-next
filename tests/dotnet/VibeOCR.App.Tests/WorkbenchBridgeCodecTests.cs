using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;
using System.Text.Json;
using System.Text;

namespace VibeOCR.App.Tests;

public sealed class WorkbenchBridgeCodecTests
{
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
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(json.Replace("\"pypi\"", "\"untrusted\""), sessionId));
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
        "无法保存悬浮工具栏设置，原设置已保留：disk full"));
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
    Assert.Equal("userHidden", toolbar.GetProperty("visibility").GetString());
    Assert.Contains(
      "原设置已保留",
      toolbar.GetProperty("error").GetString());
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
          $$"""{"resourceUri":"{{resourceUri}}","sessionId":"{{sessionId}}","revision":3}"""),
        sessionId).Command);
    Assert.Equal(resourceUri, recognize.ResourceUri);
    Assert.Equal(sessionId, recognize.SessionId);
    Assert.Equal(3, recognize.Revision);

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
          $$"""{"resourceUri":"file:///tmp/out.png","sessionId":"{{sessionId}}","revision":0}"""),
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
      ("recognition", "captureScreenshotTextSession", "{}", typeof(CaptureScreenshotTextSessionCommand)),
      ("recognition", "closeScreenshotSession", "{}", typeof(CloseScreenshotSessionCommand)),
      ("recognition", "copyAnnotatedImage", "{\"resourceUri\":\"https://app.vibeocr/__annotation/00000000000000000000000000000000\"}", typeof(CopyAnnotatedImageCommand)),
      ("recognition", "saveAnnotatedImage", "{\"resourceUri\":\"https://app.vibeocr/__annotation/11111111111111111111111111111111\"}", typeof(SaveAnnotatedImageCommand)),
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
      ("settings", "setTheme", "{\"theme\":\"dark\"}", typeof(SetThemeCommand)),
      ("settings", "setActionHotkey", "{\"actionId\":\"clipboard_recognize\",\"hotkey\":\"Ctrl+Alt+C\"}", typeof(SetActionHotkeyCommand)),
      ("settings", "resetActionHotkey", "{\"actionId\":\"screenshot_recognize\"}", typeof(ResetActionHotkeyCommand)),
      ("settings", "setFloatingToolbarEnabled", "{\"enabled\":true}", typeof(SetFloatingToolbarEnabledCommand)),
      ("settings", "setFloatingToolbarLayout", "{\"edge\":\"left\",\"autoHide\":false}", typeof(SetFloatingToolbarLayoutCommand)),
      ("settings", "showFloatingToolbar", "{}", typeof(ShowFloatingToolbarCommand)),
      ("settings", "hideFloatingToolbar", "{}", typeof(HideFloatingToolbarCommand)),
      ("update", "check", "{}", typeof(CheckUpdateCommand)),
      ("diagnostics", "export", "{}", typeof(ExportDiagnosticsCommand)),
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
      ("setActionHotkey", "{\"actionId\":\"clipboard_recognize\",\"hotkey\":\"\"}"),
      ("setActionHotkey", "{\"actionId\":\" \"}"),
      ("resetActionHotkey", "{\"actionId\":\"\"}"),
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
