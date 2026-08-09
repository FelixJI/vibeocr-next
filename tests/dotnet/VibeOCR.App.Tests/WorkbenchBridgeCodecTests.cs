using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;
using System.Text.Json;
using System.Text;

namespace VibeOCR.App.Tests;

public sealed class WorkbenchBridgeCodecTests
{
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
  public void ParseCommandSupportsTheClosedWebActionSet()
  {
    Guid sessionId = Guid.NewGuid();
    (string Scope, string Action, string Arguments, Type Type)[] cases =
    [
      ("recognition", "selectImage", "{}", typeof(SelectRecognitionImageCommand)),
      ("recognition", "readClipboard", "{}", typeof(ReadRecognitionClipboardCommand)),
      ("recognition", "captureScreen", "{}", typeof(CaptureRecognitionScreenCommand)),
      ("batch", "addFiles", "{}", typeof(AddBatchFilesCommand)),
      ("batch", "exportMarkdown", "{}", typeof(ExportBatchMarkdownCommand)),
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
      [new BatchWorkbenchItem(Guid.NewGuid(), "invoice.png", "batch.item.completed", "合计 42")],
      2);
    string json = WorkbenchBridgeCodec.SerializeState(
      sessionId,
      new WorkbenchStateEnvelope(4, "batch", WorkbenchStateChange.Replace, batch));

    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement state = document.RootElement.GetProperty("payload").GetProperty("state");
    Assert.Equal("invoice.png", state.GetProperty("items")[0].GetProperty("name").GetString());
    Assert.Equal(2, state.GetProperty("concurrency").GetInt32());
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
        new BatchWorkbenchState(false, 80, 0, 0, [], 2, 40)));
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
        new BatchWorkbenchState(false, 500, 40, 0, items, 8)));

    Assert.True(Encoding.UTF8.GetByteCount(json) < WorkbenchBridgeCodec.MaxMessageBytes);
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
