using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class RecognitionResultActionTests
{
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ExportRefusesChangedResultAfterPickerOrOverwriteConfirmation(bool changeDuringOverwrite)
  {
    string path = Path.Combine(Path.GetTempPath(), $"vibeocr-export-{Guid.NewGuid():N}.txt");
    await File.WriteAllTextAsync(path, "existing", TestContext.Current.CancellationToken);
    try
    {
      bool current = true;
      var client = new ExportClient();
      var platform = new ExportPlatform(path,
        () => { if (!changeDuringOverwrite) current = false; },
        () => { if (changeDuringOverwrite) current = false; });
      var actions = new ResultActions(client, platform);
      actions.SetResult(new RecognizeResponse { Text = "old", RawText = "old" });
      await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ExportAsync(
        ResultExportFormat.Text, TestContext.Current.CancellationToken, stillCurrent: () => current));
      Assert.Null(client.Request);
      Assert.Equal("existing", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }
    finally { File.Delete(path); }
  }

  [Theory]
  [InlineData(ResultExportFormat.Text)]
  [InlineData(ResultExportFormat.Html)]
  [InlineData(ResultExportFormat.Docx)]
  [InlineData(ResultExportFormat.Xlsx)]
  public async Task ModeExportUsesHostTextAndNoRawLineBlocks(ResultExportFormat format)
  {
    var client = new ExportClient();
    var actions = new ResultActions(client, new ExportPlatform("not-existing.txt", () => { }, () => { }));
    actions.SetResult(new RecognizeResponse
    {
      Text = "old", RawText = "old", MarkdownText = "old", HtmlText = "old",
      RawBlocks = [System.Text.Json.JsonSerializer.SerializeToElement(new { text = "old" })],
    });
    await actions.ExportAsync(format, TestContext.Current.CancellationToken, "校对<正文>");
    Assert.NotNull(client.Request);
    Assert.Equal("校对<正文>", client.Request.RawText);
    Assert.Equal("校对<正文>", client.Request.MarkdownText);
    Assert.Equal("<pre>校对&lt;正文&gt;</pre>", client.Request.HtmlText);
    Assert.Empty(client.Request.ContentBlocks!);
  }

  [Fact]
  public async Task CancellingExportKeepsResultAndDoesNotSendWriteRequest()
  {
    var client = new ExportClient();
    var actions = new ResultActions(client, new ExportPlatform(null, () => { }, () => { }));
    actions.SetResult(new RecognizeResponse { Text = "保留结果" });
    ExportResult? result = await actions.ExportAsync(ResultExportFormat.Text, TestContext.Current.CancellationToken);
    Assert.Null(result);
    Assert.Null(client.Request);
    Assert.True(actions.HasResult);
  }

  private sealed class ExportClient : InferenceClientStub
  {
    internal ExportRequest? Request;
    public override Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
      Request = request;
      return Task.FromResult(new ExportResult(request.OutputPath, 1));
    }
  }

  private sealed class ExportPlatform(string? path, Action picker, Action confirm) : IResultActionPlatform
  {
    public Task WriteClipboardAsync(RecognitionResultContent result, ResultCopyFormat format, CancellationToken cancellationToken) =>
      throw new NotSupportedException();
    public Task<string?> PickExportPathAsync(ResultExportFormat format, CancellationToken cancellationToken)
    {
      picker();
      return Task.FromResult<string?>(path);
    }
    public Task<bool> ConfirmOverwriteAsync(string target, CancellationToken cancellationToken)
    {
      confirm();
      return Task.FromResult(true);
    }
  }
}

public sealed class RecognitionResultBridgeTests
{
  [Fact]
  public void ResultReferenceImageAndNullableConfidenceCrossBridge()
  {
    var image = new VibeOCR.App.Workbench.WorkbenchResourceReference(
      "https://app.vibeocr/__resource/0123456789abcdef0123456789abcdef", "image/png", 3);
    var state = new VibeOCR.App.Workbench.RecognitionWorkbenchState(false, "recognition.completed",
      ResultImage: image, TextLayer: new VibeOCR.App.Workbench.RecognitionTextLayerState(
        "ready", null, null, null, null, image, Lines: [new("低置信", 0, 0, 10, 10, Score: 0.3), new("未知", 0, 0, 10, 10)]));
    string json = VibeOCR.App.Web.WorkbenchBridgeCodec.SerializeState(Guid.NewGuid(),
      new VibeOCR.App.Workbench.WorkbenchStateEnvelope(1, "recognition",
        VibeOCR.App.Workbench.WorkbenchStateChange.Replace, state));
    using var document = System.Text.Json.JsonDocument.Parse(json);
    var projected = document.RootElement.GetProperty("payload").GetProperty("state");
    Assert.Equal(image.Url, projected.GetProperty("resultImage").GetProperty("url").GetString());
    var lines = projected.GetProperty("textLayer").GetProperty("lines");
    Assert.Equal(0.3, lines[0].GetProperty("score").GetDouble());
    Assert.Equal(System.Text.Json.JsonValueKind.Null, lines[1].GetProperty("score").ValueKind);
  }

  [Theory]
  [InlineData("copyLines", "{\"resourceUri\":\"https://app.vibeocr/__resource/0123456789abcdef0123456789abcdef\",\"mode\":12}")]
  [InlineData("updateLine", "{\"resourceUri\":\"https://app.vibeocr/__resource/0123456789abcdef0123456789abcdef\",\"lineIndex\":1.5,\"expectedOldText\":\"old\",\"newText\":\"new\"}")]
  [InlineData("updateLine", "{\"resourceUri\":\"https://app.vibeocr/__resource/0123456789abcdef0123456789abcdef\",\"lineIndex\":2147483648,\"expectedOldText\":\"old\",\"newText\":\"new\"}")]
  public void InvalidResultArgumentKindsStayInsideProtocolBoundary(string action, string arguments)
  {
    Guid session = Guid.NewGuid();
    string json = System.Text.Json.JsonSerializer.Serialize(new
    {
      version = 2, kind = "request", id = Guid.NewGuid(), type = "app.command",
      payload = new { sessionId = session, command = new
      { scope = "recognition", action, arguments = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(arguments) } },
    });
    Assert.Throws<VibeOCR.App.Web.WorkbenchBridgeProtocolException>(() =>
      VibeOCR.App.Web.WorkbenchBridgeCodec.ParseCommand(json, session));
  }
}
