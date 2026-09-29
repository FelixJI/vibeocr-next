using System.Text.Json;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// #110 桌面剩余范围：结构化结果（表格/公式/图片）在批量项与 PDF 页的
/// 预览资源发布，以及表格 HTML/TSV、公式 LaTeX 的原生剪贴板复制边界。
/// </summary>
public sealed class StructuredResultWorkbenchTests
{
  private static readonly byte[] AssetPng = [1, 2, 3, 4];

  [Fact]
  public void TableClipboardPayloadPreservesMergesAndGuardsFormulaText()
  {
    using JsonDocument document = JsonDocument.Parse("""
      {"type":"table","table":{"schema_version":1,"row_count":2,"column_count":2,
        "cells":[
          {"row":0,"column":0,"rowspan":2,"colspan":1,"text":"中文","is_header":true},
          {"row":0,"column":1,"rowspan":1,"colspan":1,"text":"=SUM(A1)"},
          {"row":1,"column":1,"rowspan":1,"colspan":1,"text":"a<b\"c"}]}}
      """);

    Assert.True(StructuredResultClipboard.TryBuildTable(
      document.RootElement,
      out string? tsv,
      out string? html));

    // TSV：合并单元格占位为空，公式样文本前置单引号。空单元格以制表符占位。
    Assert.Equal("中文\t'=SUM(A1)\n\t\"a<b\"\"c\"\n", tsv);
    // HTML：合并关系保留在 th 上，值本身转义。公式样文本同样前置单引号。
    Assert.Contains("<th rowspan=\"2\" colspan=\"1\">中文</th>", html);
    Assert.Contains("'=SUM(A1)", html);
    Assert.Contains("a&lt;b&quot;c", html);
  }

  [Fact]
  public void TableClipboardRejectsOutOfBoundsAndMalformedTables()
  {
    using JsonDocument outOfBounds = JsonDocument.Parse(
      """{"type":"table","table":{"schema_version":1,"row_count":1,"column_count":1,"cells":[{"row":0,"column":0,"rowspan":2,"colspan":1,"text":"x"}]}}""");
    using JsonDocument wrongSchema = JsonDocument.Parse(
      """{"type":"table","table":{"schema_version":2,"row_count":1,"column_count":1,"cells":[]}}""");
    using JsonDocument notATable = JsonDocument.Parse("""{"type":"text","text":"x"}""");

    Assert.False(StructuredResultClipboard.TryBuildTable(
      outOfBounds.RootElement, out string? tsv, out string? html));
    Assert.False(StructuredResultClipboard.TryBuildTable(
      wrongSchema.RootElement, out _, out _));
    Assert.False(StructuredResultClipboard.TryBuildTable(
      notATable.RootElement, out _, out _));
    Assert.Null(tsv);
    Assert.Null(html);
    foreach (string malformed in new[]
    {
      """{"type":42,"table":{}}""",
      """{"type":"table","table":{"schema_version":1,"row_count":1.5,"column_count":1,"cells":[]}}""",
      """{"type":"table","table":{"schema_version":1,"row_count":2147483648,"column_count":1,"cells":[]}}""",
      """{"type":"table","table":{"schema_version":1,"row_count":1,"column_count":1,"cells":[{"row":2147483647,"column":0,"rowspan":2,"colspan":1,"text":"x"}]}}""",
    })
    {
      using JsonDocument invalid = JsonDocument.Parse(malformed);
      Assert.False(StructuredResultClipboard.TryBuildTable(invalid.RootElement, out _, out _));
    }
  }

  [Fact]
  public void TableClipboardKeepsMultilineValuesInsideOneCell()
  {
    using JsonDocument document = JsonDocument.Parse("""
      {"type":"table","table":{"schema_version":1,"row_count":1,"column_count":2,
        "cells":[
          {"row":0,"column":1,"rowspan":1,"colspan":1,"text":"  =1+1"},
          {"row":0,"column":0,"rowspan":1,"colspan":1,"text":"第一行\n第二行\t\"引号\""}]}}
      """);
    Assert.True(StructuredResultClipboard.TryBuildTable(document.RootElement, out string? tsv, out string? html));
    Assert.Equal("\"第一行\n第二行\t\"\"引号\"\"\"\t'  =1+1\n", tsv);
    Assert.True(html!.IndexOf("第一行", StringComparison.Ordinal) < html.IndexOf("=1+1", StringComparison.Ordinal));
  }
  [Fact]
  public void FormulaClipboardExtractsRawLatexOnly()
  {
    using JsonDocument formula = JsonDocument.Parse(
      """{"type":"formula","text":"\\frac{1}{2}"}""");
    using JsonDocument table = JsonDocument.Parse(
      """{"type":"table","table":{"schema_version":1,"row_count":1,"column_count":1,"cells":[{"row":0,"column":0,"rowspan":1,"colspan":1,"text":"t"}]}}""");

    Assert.True(StructuredResultClipboard.TryGetFormulaText(
      formula.RootElement, out string? latex));
    Assert.Equal("\\frac{1}{2}", latex);
    Assert.False(StructuredResultClipboard.TryGetFormulaText(
      table.RootElement, out _));
  }

  [Fact]
  public async Task BatchCompletedItemPublishesStructuredResultAndCopiesNatively()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-structured-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      string png = CreateTempPng();
      var client = new StructuredOutcomeInferenceClient();
      var deferred = new VibeOCR.App.Inference.DeferredInferenceClient();
      deferred.Attach(client);
      var batch = new BatchViewModel(deferred, new DiskBatchFileSource());
      batch.AddFiles([png]);
      await batch.StartAsync(TestContext.Current.CancellationToken);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      var clipboard = new RecordingStructuredClipboard();
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        () => batch,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        structuredClipboard: clipboard);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SetBatchWindowCommand(0),
        TestContext.Current.CancellationToken);

      BatchWorkbenchState state = Assert.IsType<BatchWorkbenchState>(
        Assert.Single(outcome.States));
      WorkbenchResourceReference? structured = Assert.Single(state.Items!)
        .StructuredResult;
      Assert.NotNull(structured);
      Assert.Equal("application/json; charset=utf-8", structured.MediaType);
      // 授权图片资产经 FetchResultAssetAsync 取回并发布。
      Assert.Equal(1, client.AssetFetches);

      WorkbenchCommandOutcome tableCopy = await handler.ExecuteAsync(
        new CopyStructuredResultCommand(structured!.Url, 0, "table"),
        TestContext.Current.CancellationToken);
      Assert.Null(tableCopy.Error);
      Assert.NotNull(clipboard.Tsv);
      Assert.Contains("'=SUM(A1)", clipboard.Tsv);
      Assert.Contains("<th rowspan=\"2\" colspan=\"1\">中文</th>", clipboard.Html);

      WorkbenchCommandOutcome latexCopy = await handler.ExecuteAsync(
        new CopyStructuredResultCommand(structured!.Url, 1, "latex"),
        TestContext.Current.CancellationToken);
      Assert.Null(latexCopy.Error);
      Assert.Equal("\\frac{1}{2}", clipboard.Text);
      Assert.True(broker.Revoke(new WorkbenchResourceLease(new Uri(structured.Url), DateTimeOffset.MaxValue)));
      WorkbenchCommandOutcome revoked = await handler.ExecuteAsync(
        new CopyStructuredResultCommand(structured.Url, 0, "table"),
        TestContext.Current.CancellationToken);
      Assert.Equal("desktop_command_failed", revoked.Error?.Code);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task PdfOcrPagePublishesStructuredResultForTheVisibleWindow()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-structured-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new StructuredOutcomeInferenceClient();
      var pdf = new PdfViewModel(client, new PickerFreePdfFileSource());
      await pdf.OpenPathAsync("doc.pdf", TestContext.Current.CancellationToken);
      await pdf.StartOcrAsync([0], overwrite: false, TestContext.Current.CancellationToken);
      Assert.Equal(PdfPageState.Done, pdf.Pages[0].State);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      var clipboard = new RecordingStructuredClipboard();
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => pdf,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        structuredClipboard: clipboard);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SetPdfWindowCommand(0),
        TestContext.Current.CancellationToken);

      PdfWorkbenchState state = Assert.IsType<PdfWorkbenchState>(
        Assert.Single(outcome.States));
      WorkbenchResourceReference? structured = Assert.Single(state.Pages!)
        .StructuredResult;
      Assert.NotNull(structured);

      WorkbenchCommandOutcome latexCopy = await handler.ExecuteAsync(
        new CopyStructuredResultCommand(structured!.Url, 1, "latex"),
        TestContext.Current.CancellationToken);
      Assert.Null(latexCopy.Error);
      Assert.Equal("\\frac{1}{2}", clipboard.Text);
      Assert.True(broker.Revoke(new WorkbenchResourceLease(new Uri(structured.Url), DateTimeOffset.MaxValue)));
      WorkbenchCommandOutcome revoked = await handler.ExecuteAsync(
        new CopyStructuredResultCommand(structured.Url, 0, "table"),
        TestContext.Current.CancellationToken);
      Assert.Equal("desktop_command_failed", revoked.Error?.Code);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task StructuredCopyFailsClosedForUnknownResourcesAndStaleBlocks()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-structured-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        structuredClipboard: new RecordingStructuredClipboard());

      WorkbenchCommandOutcome unknown = await handler.ExecuteAsync(
        new CopyStructuredResultCommand(
          "https://app.vibeocr/__resource/0123456789abcdef0123456789abcdef", 0, "table"),
        TestContext.Current.CancellationToken);
      Assert.Equal("desktop_command_failed", unknown.Error?.Code);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  private static string CreateTempPng()
  {
    string path = Path.Combine(
      Path.GetTempPath(), $"vibeocr-structured-{Guid.NewGuid():N}.png");
    File.WriteAllBytes(path, [1, 2, 3]);
    return path;
  }

  private sealed class DiskBatchFileSource : IBatchFileSource
  {
    public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<string>>([]);

    public Task<(byte[] Data, string MediaType)> ReadAsync(
      string path, CancellationToken cancellationToken) =>
      Task.FromResult((File.ReadAllBytes(path), "image/png"));
  }

  private sealed class PickerFreePdfFileSource : IPdfFileSource
  {
    public Task<string?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<string?>(null);
  }

  /// <summary>
  /// Fake supervisor returning one structured outcome (table + formula +
  /// authorized image asset) for every submitted item.
  /// </summary>
  private sealed class StructuredOutcomeInferenceClient : InferenceClientStub
  {
    private IReadOnlyList<JobItem> _items = [];

    public int AssetFetches { get; private set; }

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      _items = request.Items.Select((item, index) => new JobItem
      {
        ItemId = $"it-{index}",
        ClientItemKey = item.ClientItemKey,
        Ordinal = item.Ordinal,
        DisplayName = item.DisplayName,
        State = ItemState.Queued,
      }).ToArray();
      return Task.FromResult(new JobRef
      {
        JobId = "job-1",
        Items = _items,
      });
    }

    public override Task<JobUpdate> ObserveAsync(
      string jobId,
      int afterSequence,
      CancellationToken cancellationToken) => Task.FromResult(new JobUpdate
      {
        Events = [],
        ThroughSequence = afterSequence + 1,
        Snapshot = new JobSnapshot
        {
          JobId = jobId,
          Kind = JobKind.Recognition,
          Priority = JobPriority.Background,
          State = JobState.Completed,
          Items = _items,
          EventSequence = afterSequence + 1,
        },
        Outcomes = _items.Select(item => new ItemOutcome
        {
          ItemId = item.ItemId,
          State = ItemState.Succeeded,
          Attempt = 1,
          PayloadType = "ocr.v1",
          Payload = StructuredPayload(),
        }).ToArray(),
      });

    public override Task<byte[]> FetchResultAssetAsync(
      string jobId, string itemId, string assetId, CancellationToken cancellationToken)
    {
      AssetFetches++;
      return Task.FromResult(AssetPng);
    }

    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(
      string path, string? password, CancellationToken cancellationToken) =>
      Task.FromResult(new PdfSessionOpenResult("session-1", 1, path));

    public override Task<byte[]> RenderPdfPageAsync(
      string sessionId, int page, int size, CancellationToken cancellationToken) =>
      Task.FromResult(AssetPng);

    private static Dictionary<string, JsonElement> StructuredPayload() => new()
    {
      ["raw_text"] = JsonSerializer.SerializeToElement("结构化结果"),
      ["content_list"] = JsonSerializer.SerializeToElement(new object[]
      {
        new
        {
          type = "table",
          table = new
          {
            schema_version = 1,
            row_count = 2,
            column_count = 2,
            cells = new object[]
            {
              new { row = 0, column = 0, rowspan = 2, colspan = 1, text = "中文", is_header = true },
              new { row = 0, column = 1, rowspan = 1, colspan = 1, text = "=SUM(A1)" },
              new { row = 1, column = 1, rowspan = 1, colspan = 1, text = "值" },
            },
          },
        },
        new { type = "formula", text = "\\frac{1}{2}" },
        new
        {
          type = "image",
          image = new { available = true, job_id = "job-1", item_id = "it-0", asset_id = "asset-1" },
        },
      }),
    };
  }

  private sealed class RecordingStructuredClipboard : IStructuredClipboardPlatform
  {
    public string? Tsv { get; private set; }
    public string? Html { get; private set; }
    public string? Text { get; private set; }

    public Task WriteTableAsync(string tsv, string html, CancellationToken cancellationToken)
    {
      Tsv = tsv;
      Html = html;
      return Task.CompletedTask;
    }

    public Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
      Text = text;
      return Task.CompletedTask;
    }
  }
}
