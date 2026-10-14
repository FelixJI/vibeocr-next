using System.Text.Json;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;
using VibeOCR.Platform.Bootstrap;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// #110 桌面剩余范围：结构化结果（表格/公式/图片）在批量项与 PDF 页的
/// 预览资源发布，以及表格 HTML/TSV、公式 LaTeX 的原生剪贴板复制边界。
/// </summary>
public sealed class StructuredResultWorkbenchTests
{
  private static readonly byte[] AssetPng = [1, 2, 3, 4];
  /// <summary>真实可解码 1×1 PNG：识别输入 fixture（ResultImage 像素契约）。</summary>
  private static readonly byte[] RecognitionPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

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
    Assert.Contains("第一行<br style=\"mso-data-placement:same-cell\">第二行", html);
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
  public void TableRangeClipboardClipsMergedCellsToSelection()
  {
    using JsonDocument document = JsonDocument.Parse("""
      {"type":"table","table":{"schema_version":1,"row_count":3,"column_count":3,"cells":[
        {"row":0,"column":0,"rowspan":2,"colspan":2,"text":"合并","is_header":true},
        {"row":0,"column":2,"rowspan":1,"colspan":1,"text":"右上"},
        {"row":2,"column":0,"rowspan":1,"colspan":1,"text":"左下"},
        {"row":2,"column":2,"rowspan":1,"colspan":1,"text":"=1+1"}]}}
      """);

    // 只选合并单元格锚点（0,0）：rowspan/colspan 裁剪到 1×1，不外溢。
    Assert.True(StructuredResultClipboard.TryBuildTableRange(
      document.RootElement, 0, 0, 0, 0, out string? mergedTsv, out string? mergedHtml));
    Assert.Equal("合并\n", mergedTsv);
    Assert.Contains("<th rowspan=\"1\" colspan=\"1\">合并</th>", mergedHtml);

    // 未相交的单元格不进入负载，公式样文本仍前置单引号。
    Assert.True(StructuredResultClipboard.TryBuildTableRange(
      document.RootElement, 2, 2, 2, 2, out string? formulaTsv, out string? formulaHtml));
    Assert.Equal("'=1+1\n", formulaTsv);
    Assert.Contains("'=1+1", formulaHtml);

    // 整表（默认裁剪边界）与旧 TryBuildTable 语义等价。
    Assert.True(StructuredResultClipboard.TryBuildTable(
      document.RootElement, out string? wholeTsv, out string? wholeHtml));
    Assert.Contains("合并", wholeTsv);
    Assert.Contains("rowspan=\"2\"", wholeHtml);

    // 非法范围拒绝。
    Assert.False(StructuredResultClipboard.TryBuildTableRange(
      document.RootElement, 2, 1, 0, 1, out _, out _));
  }

  [Fact]
  public void TableClipboardBoundsMaterializedGridArea()
  {
    // 回归（PR225 review）：合法 1000×1000 稀疏表（仅 1 个单元格）会让
    // 旧实现分配两个 int[,]、一个 string[,] 并生成百万 td，宿主 UI 冻结；
    // 超限面积必须 fail fast 拒绝，而不是物化无界网格。
    using JsonDocument sparse = JsonDocument.Parse(
      """{"type":"table","table":{"schema_version":1,"row_count":1000,"column_count":1000,"cells":[{"row":0,"column":0,"rowspan":1,"colspan":1,"text":"唯一值"}]}}""");

    Assert.False(StructuredResultClipboard.TryBuildTable(
      sparse.RootElement, out string? tsv, out string? html));
    Assert.Null(tsv);
    Assert.Null(html);
    // 范围复制不得成为绕过面：覆盖整表的子范围同样拒绝。
    Assert.False(StructuredResultClipboard.TryBuildTableRange(
      sparse.RootElement, 0, 999, 0, 999, out _, out _));
    // 面积上限内的子范围仍可复制，稀疏位置保持空占位列对齐。
    Assert.True(StructuredResultClipboard.TryBuildTableRange(
      sparse.RootElement, 0, 0, 0, 9, out string? rangeTsv, out string? rangeHtml));
    Assert.Equal("唯一值\t\t\t\t\t\t\t\t\t\n", rangeTsv);
    Assert.Contains("<td rowspan=\"1\" colspan=\"1\">唯一值</td>", rangeHtml);

    // 边界：100×100=10000 格恰在上限内，整表复制保持可用。
    using JsonDocument bounded = JsonDocument.Parse(
      """{"type":"table","table":{"schema_version":1,"row_count":100,"column_count":100,"cells":[{"row":0,"column":0,"rowspan":1,"colspan":1,"text":"左上"}]}}""");
    Assert.True(StructuredResultClipboard.TryBuildTable(
      bounded.RootElement, out string? wholeTsv, out string? wholeHtml));
    Assert.Equal(100, wholeTsv!.Split('\n').Length - 1);
    Assert.Contains("左上", wholeHtml);
  }

  [Fact]
  public void LinesTextModesMatchClassicJoinSemantics()
  {
    using JsonDocument document = JsonDocument.Parse("""
      {"type":"lines","lines":[
        {"text":"第一段第一行","bbox":[10,10,200,30]},
        {"text":"第一段第二行","bbox":[10,34,200,54]},
        {"text":"第二段首行","bbox":[10,140,200,160]},
        {"text":"无框行"}]}
      """);

    Assert.True(StructuredResultClipboard.TryBuildLinesText(
      document.RootElement, StructuredResultClipboard.LineModeRaw, out string? raw));
    Assert.Equal("第一段第一行\n第一段第二行\n第二段首行\n无框行", raw);
    Assert.True(StructuredResultClipboard.TryBuildLinesText(
      document.RootElement, StructuredResultClipboard.LineModeMerged, out string? merged));
    Assert.Equal("第一段第一行第一段第二行第二段首行无框行", merged);
    // 智能分段：140-54=86 > 1.5×(54-34)=30 分段；无框行不产生边界。
    Assert.True(StructuredResultClipboard.TryBuildLinesText(
      document.RootElement, StructuredResultClipboard.LineModeSmart, out string? smart));
    Assert.Equal("第一段第一行第一段第二行\n\n第二段首行无框行", smart);

    using JsonDocument invalid = JsonDocument.Parse("""{"type":"lines","lines":[]}""");
    Assert.False(StructuredResultClipboard.TryBuildLinesText(
      invalid.RootElement, StructuredResultClipboard.LineModeRaw, out _));
  }

  [Fact]
  public async Task LinesResultSupportsModeCopyInlineEditAndStaleRejection()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-lines-result-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      var client = new PlainLinesInferenceClient();
      var inputs = new FixedRecognitionInputs();
      var recognition = new RecognitionViewModel(client, inputs) { TaskEngine = "rapidocr" };
      var clipboard = new RecordingLinesClipboard();
      var settings = new SettingsViewModel(client);
      await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
      var clock = new CallbackTimeProvider();
      using var broker = new WorkbenchResourceBroker(resourceRoot, clock);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => recognition,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => settings,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotationStore,
        structuredClipboard: clipboard,
        inferenceAttached: () => true);

      WorkbenchResourceReference structured;
      var published = new TaskCompletionSource<RecognitionWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      handler.StateChanged += state =>
      {
        if (state is RecognitionWorkbenchState recognitionState &&
          !recognitionState.IsBusy)
        {
          published.TrySetResult(recognitionState);
        }
      };
      await handler.ExecuteAsync(
        new SelectRecognitionImageCommand(),
        TestContext.Current.CancellationToken);
      RecognitionWorkbenchState completed = await published.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.True(completed.StructuredResult is not null, $"{completed.StatusCode}: {recognition.Status}");
      structured = completed.StructuredResult!;
      Assert.NotNull(completed.ResultImage);
      await using (WorkbenchResourceResponse image = await broker.OpenAsync(new Uri(completed.ResultImage!.Url), TestContext.Current.CancellationToken))
      {
        using var bytes = new MemoryStream();
        await image.Content.CopyToAsync(bytes, TestContext.Current.CancellationToken);
        // ResultImage 统一重编码为无方向 PNG：按解码像素与输入一致断言。
        (byte[] pixels, uint width, uint height) = await DecodePngPixelsAsync(bytes.ToArray());
        (byte[] inputPixels, uint inputWidth, uint inputHeight) = await DecodePngPixelsAsync(RecognitionPng);
        Assert.Equal(inputWidth, width);
        Assert.Equal(inputHeight, height);
        Assert.Equal(inputPixels, pixels);
      }

      // 模式复制：宿主从权威资源预计算 modes 取文本（merged = 无分隔拼接）。
      WorkbenchCommandOutcome smartCopy = await handler.ExecuteAsync(
        new CopyRecognitionLinesCommand(structured.Url, "merged"),
        TestContext.Current.CancellationToken);
      Assert.Null(smartCopy.Error);
      Assert.Equal("第一段行一第二段行二", clipboard.Text);

      // 行内联编辑：成功后重发布新结构化资源，raw 复制反映编辑后权威文本。
      WorkbenchCommandOutcome edit = await handler.ExecuteAsync(
        new UpdateRecognitionLineCommand(structured.Url, 0, "第一段行一", "编辑后的行"),
        TestContext.Current.CancellationToken);
      Assert.Null(edit.Error);
      RecognitionWorkbenchState editedState = Assert.IsType<RecognitionWorkbenchState>(
        Assert.Single(edit.States));
      Assert.NotNull(editedState.StructuredResult);
      Assert.NotEqual(structured.Url, editedState.StructuredResult!.Url);
      WorkbenchCommandOutcome rawCopy = await handler.ExecuteAsync(
        new CopyRecognitionLinesCommand(editedState.StructuredResult.Url, "raw"),
        TestContext.Current.CancellationToken);
      Assert.Null(rawCopy.Error);
      Assert.Equal("编辑后的行\n第二段行二", clipboard.Text);

      string sessionDirectory = Path.Combine(resourceRoot, "session");
      string parkedDirectory = Path.Combine(resourceRoot, "parked-session");
      Directory.Move(sessionDirectory, parkedDirectory);
      File.WriteAllText(sessionDirectory, "force publication failure");
      try
      {
        WorkbenchCommandOutcome failedEdit = await handler.ExecuteAsync(
          new UpdateRecognitionLineCommand(editedState.StructuredResult.Url, 0, "编辑后的行", "不可提交"),
          TestContext.Current.CancellationToken);
        Assert.NotNull(failedEdit.Error);
        Assert.Equal("编辑后的行\n第二段行二", recognition.ResultText);
      }
      finally
      {
        File.Delete(sessionDirectory);
        Directory.Move(parkedDirectory, sessionDirectory);
      }

      // 过期拒绝：旧资源 URL 在编辑后即失效（fail closed）。
      WorkbenchCommandOutcome stale = await handler.ExecuteAsync(
        new CopyRecognitionLinesCommand(structured.Url, "raw"),
        TestContext.Current.CancellationToken);
      Assert.NotNull(stale.Error);
      // 文本漂移拒绝：期望旧行不一致时不改写权威数据。
      WorkbenchCommandOutcome drift = await handler.ExecuteAsync(
        new UpdateRecognitionLineCommand(
          editedState.StructuredResult.Url, 0, "不匹配的旧文本", "新文本"),
        TestContext.Current.CancellationToken);
      Assert.NotNull(drift.Error);
      int resourcesAfterCancel = -1;
      clock.Callback = () =>
      {
        handler.ExecuteAsync(new CancelRecognitionCommand(), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        resourcesAfterCancel = broker.ActiveResourceCount;
      };
      WorkbenchCommandOutcome lateEdit = await handler.ExecuteAsync(
        new UpdateRecognitionLineCommand(editedState.StructuredResult.Url, 0, "编辑后的行", "迟到内容"),
        TestContext.Current.CancellationToken);
      Assert.NotNull(lateEdit.Error);
      Assert.Null(recognition.Result);
      Assert.Null(recognition.ResultInput);
      Assert.Equal(resourcesAfterCancel, broker.ActiveResourceCount);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task CapturedRecognitionBindsSubmittedImageWithoutReplacingEditorBaseline()
  {
    var inputs = new FixedRecognitionInputs();
    var viewModel = new RecognitionViewModel(new PlainLinesInferenceClient(), inputs);
    await viewModel.OpenImageForEditAsync(TestContext.Current.CancellationToken);
    RecognitionInput baseline = viewModel.CurrentInput!;
    var revised = new RecognitionInput([9, 8, 7], "image/png", "revised.png", "capture");
    await viewModel.RecognizeCapturedInputAsync(revised, TestContext.Current.CancellationToken);
    Assert.Same(baseline, viewModel.CurrentInput);
    Assert.Same(revised, viewModel.ResultInput);
    viewModel.InvalidateResult();
    Assert.Null(viewModel.ResultInput);
  }

  [Fact]
  public async Task PreparedLineEditPreservesSourceIndexesAndOnlyCommitsCurrentResult()
  {
    using JsonDocument raw = JsonDocument.Parse("""
      [null,{"text":42},{"text":"  "},{"text":"重复","bbox":["x",0,20,30]},
      {"text":"重复","score":"unknown"},{"text":"末行\n第二行"}]
      """);
    var viewModel = new RecognitionViewModel(new PlainLinesInferenceClient
    {
      Blocks = [.. raw.RootElement.EnumerateArray().Select(block => block.Clone())],
    }, new FixedRecognitionInputs());
    await viewModel.RecognizeFileAsync(TestContext.Current.CancellationToken);
    RecognizeResponse original = viewModel.Result!;
    Assert.True(viewModel.TryPrepareLineEdit(1, "重复", "校对", out RecognizeResponse? prepared, out _));
    Assert.Same(original, viewModel.Result);
    Assert.Equal(6, prepared!.RawBlocks!.Length);
    Assert.Equal("重复", prepared.RawBlocks[3].GetProperty("text").GetString());
    Assert.Equal("校对", prepared.RawBlocks[4].GetProperty("text").GetString());
    Assert.Equal("  \n重复\n校对\n末行\n第二行", prepared.MarkdownText);
    System.Text.Json.Nodes.JsonObject block = RecognitionLinesBlock.BuildBlock(prepared.RawBlocks!, prepared.Text)!;
    using JsonDocument projected = JsonDocument.Parse(block.ToJsonString());
    Assert.True(projected.RootElement.GetProperty("lines")[1].GetProperty("is_manually_edited").GetBoolean());
    Assert.False(projected.RootElement.GetProperty("lines")[0].TryGetProperty("bbox", out _));
    Assert.True(viewModel.TryCommitLineEdit(original, prepared));
    Assert.False(viewModel.TryCommitLineEdit(original, prepared));
  }

  [Theory]
  [InlineData(50, "甲乙")]
  [InlineData(51, "甲\n\n乙")]
  public void SmartParagraphBoundaryUsesStrictGapAndKeepsRawText(int secondTop, string expected)
  {
    JsonElement[] blocks = [
      JsonSerializer.SerializeToElement(new { text = "甲", bbox = new[] { 0, 0, 20, 20 } }),
      JsonSerializer.SerializeToElement(new { text = "乙", bbox = new[] { 0, secondTop, 20, secondTop + 20 } }),
    ];
    var projected = RecognitionLinesBlock.BuildBlock(blocks, "原始\n文本")!;
    Assert.Equal("原始\n文本", projected["modes"]!["raw"]!.GetValue<string>());
    Assert.Equal(expected, projected["modes"]!["smart"]!.GetValue<string>());
    Assert.Equal("甲", blocks[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task LinesProjectionSortsByEngineOrderAndDuplicateEditsHitRightSource()
  {
    // Classic _sort_indexed：任一 order!=-1 即按 (order,bbox) 升序，
    // 上游乱序时投影重排；编辑按行序命中源块，重复文本不错位。
    JsonElement[] blocks =
    [
      JsonSerializer.SerializeToElement(new { text = "重复", order = 2, score = 0.2, bbox = new[] { 0, 40, 100, 60 } }),
      JsonSerializer.SerializeToElement(new { text = "重复", order = 0, score = 0.9, bbox = new[] { 0, 0, 100, 20 } }),
      JsonSerializer.SerializeToElement(new { text = "重复", order = 1, score = 0.5, bbox = new[] { 0, 20, 100, 40 } }),
    ];
    var projected = RecognitionLinesBlock.BuildBlock(blocks, "原始\n三行")!;
    Assert.Equal(0.9, projected["lines"]![0]!["score"]!.GetValue<double>());
    Assert.Equal(0.5, projected["lines"]![1]!["score"]!.GetValue<double>());
    Assert.Equal(0.2, projected["lines"]![2]!["score"]!.GetValue<double>());
    // raw 原文保留上游 raw_text，不随行序重排。
    Assert.Equal("原始\n三行", projected["modes"]!["raw"]!.GetValue<string>());

    var viewModel = new RecognitionViewModel(new PlainLinesInferenceClient
    {
      Blocks = [.. blocks.Select(block => block.Clone())],
    }, new FixedRecognitionInputs());
    await viewModel.RecognizeFileAsync(TestContext.Current.CancellationToken);
    Assert.True(viewModel.TryPrepareLineEdit(0, "重复", "修订甲", out RecognizeResponse? first, out _));
    Assert.Equal("修订甲", first!.RawBlocks![1].GetProperty("text").GetString());
    Assert.True(first.RawBlocks[1].GetProperty("is_manually_edited").GetBoolean());
    Assert.Equal("重复", first.RawBlocks[0].GetProperty("text").GetString());
    Assert.Equal("重复", first.RawBlocks[2].GetProperty("text").GetString());
    Assert.True(viewModel.TryPrepareLineEdit(1, "重复", "修订乙", out RecognizeResponse? second, out _));
    Assert.Equal("修订乙", second!.RawBlocks![2].GetProperty("text").GetString());
    // raw 与 raw_blocks 保留上游数组顺序（不随行序重排）。
    Assert.Equal("重复\n修订甲\n重复", first.Text);
  }

  [Fact]
  public void LinesProjectionWithoutOrderSortsByBboxYThenXAndKeepsNoBboxLastStable()
  {
    // Classic：全无有效 order 时按 bbox (y1,x1) 升序（同 y 再按 x），
    // 无 bbox 排最后且保持源数组相对顺序。
    JsonElement[] blocks =
    [
      JsonSerializer.SerializeToElement(new { text = "底部", bbox = new[] { 0, 200, 100, 220 } }),
      JsonSerializer.SerializeToElement(new { text = "右上", bbox = new[] { 50, 10, 150, 30 } }),
      JsonSerializer.SerializeToElement(new { text = "左上", bbox = new[] { 0, 10, 100, 30 } }),
      JsonSerializer.SerializeToElement(new { text = "无框甲" }),
      JsonSerializer.SerializeToElement(new { text = "无框乙" }),
    ];
    var projected = RecognitionLinesBlock.BuildBlock(blocks, "原始")!;
    Assert.Equal(
      ["左上", "右上", "底部", "无框甲", "无框乙"],
      projected["lines"]!.AsArray().Select(line => line!["text"]!.GetValue<string>()).ToArray());
    Assert.Equal(50, projected["lines"]![1]!["bbox"]![0]!.GetValue<double>());
  }

  [Fact]
  public void LinesProjectionTreatsInvalidOrderOrBboxAsClassicDefaults()
  {
    // 无效 order（非整数）按 Classic 缺省 -1 参与 (order,bbox) 序（先于
    // 0）；退化 bbox（零宽）视为无 bbox 排最后，且不进投影行。
    JsonElement[] blocks =
    [
      JsonSerializer.SerializeToElement(new { text = "序零", order = 0, bbox = new[] { 0, 100, 100, 120 } }),
      JsonSerializer.SerializeToElement(new { text = "缺序低位", bbox = new[] { 0, 200, 100, 220 } }),
      JsonSerializer.SerializeToElement(new { text = "坏序高位", order = "x", bbox = new[] { 0, 50, 100, 70 } }),
      JsonSerializer.SerializeToElement(new { text = "退化框", order = 1, bbox = new[] { 0, 0, 0, 10 } }),
    ];
    var projected = RecognitionLinesBlock.BuildBlock(blocks, "原始")!;
    Assert.Equal(
      ["坏序高位", "缺序低位", "序零", "退化框"],
      projected["lines"]!.AsArray().Select(line => line!["text"]!.GetValue<string>()).ToArray());
    Assert.False(projected["lines"]![0]!.AsObject().ContainsKey("order"));
    Assert.False(projected["lines"]![3]!.AsObject().ContainsKey("bbox"));
  }

  [Fact]
  public void LinesProjectionKeepsStableOrderAndIgnoresEmptyOrDegenerateGeometry()
  {
    using JsonDocument blocks = JsonDocument.Parse("""
      [{"text":""},{"text":"先\n内行"},{"text":"后","bbox":[0,0,0,10]},
      {"text":"末","bbox":[0,0,20,20],"score":0.3}]
      """);
    var projected = RecognitionLinesBlock.BuildBlock([.. blocks.RootElement.EnumerateArray()], "原始")!;
    // Classic 契约：全无 order 时按 bbox (y1,x1) 升序；空文本剔除，退化
    // bbox 视为无框、排在有效 bbox 之后并保持源序稳定。
    Assert.Equal(
      ["末", "先\n内行", "后"],
      projected["lines"]!.AsArray().Select(line => line!["text"]!.GetValue<string>()).ToArray());
    Assert.True(projected["lines"]![0]!.AsObject().ContainsKey("bbox"));
    Assert.False(projected["lines"]![1]!.AsObject().ContainsKey("bbox"));
    Assert.Equal("末先\n内行后", projected["modes"]!["smart"]!.GetValue<string>());
    Assert.Equal(3, projected["lines"]!.AsArray().Count);
  }

  [Fact]
  public void TableSelectionIncludesExternalMergeIntersectionAndSparseEmptyCell()
  {
    using JsonDocument table = JsonDocument.Parse("""
      {"type":"table","table":{"schema_version":1,
      "row_count":2,"column_count":3,"cells":[{"row":0,"column":0,"rowspan":2,"colspan":2,
      "text":"合并","is_header":true}]}}
      """);
    Assert.True(StructuredResultClipboard.TryBuildTableRange(table.RootElement, 1, 1, 1, 2,
      out string? text, out string? html));
    Assert.Equal("合并\t\n", text);
    Assert.Equal("<table><tr><th rowspan=\"1\" colspan=\"1\">合并</th><td rowspan=\"1\" colspan=\"1\"></td></tr></table>", html);
  }

  [Fact]
  public async Task LineEditPreservesBlankBlockSkeletonAndEmbeddedNewlines()
  {
    using JsonDocument blocks = JsonDocument.Parse("""
      [{"text":""},{"text":"重复"},{"text":" \t "},{"text":"重复\n续行"},{"text":""}]
      """);
    var viewModel = new RecognitionViewModel(new PlainLinesInferenceClient
    {
      Blocks = [.. blocks.RootElement.EnumerateArray().Select(block => block.Clone())],
    }, new FixedRecognitionInputs());
    await viewModel.RecognizeFileAsync(TestContext.Current.CancellationToken);
    RecognizeResponse original = viewModel.Result!;
    Assert.True(viewModel.TryPrepareLineEdit(1, "重复\n续行", "修订\n续行", out RecognizeResponse? prepared, out _));
    Assert.Equal("\n重复\n \t \n修订\n续行\n", prepared!.RawText);
    Assert.Equal(prepared.RawText, prepared.MarkdownText);
    Assert.Equal("", prepared.RawBlocks![0].GetProperty("text").GetString());
    Assert.Equal(" \t ", prepared.RawBlocks[2].GetProperty("text").GetString());
    Assert.Equal("重复", prepared.RawBlocks[1].GetProperty("text").GetString());
    Assert.Same(original, viewModel.Result);
  }

  [Fact]
  public async Task HandlerCopiesSelectedCellsAndRejectsExpiredResultWithoutWritingAgain()
  {
    using JsonDocument content = JsonDocument.Parse("""
      [null,{"type":"table","table":{"schema_version":1,"row_count":2,"column_count":3,
      "cells":[{"row":0,"column":0,"rowspan":2,"colspan":2,"text":"合并","is_header":true}]}}]
      """);
    await using ResultHandlerFixture fixture = await ResultHandlerFixture.CreateAsync(new PlainLinesInferenceClient
    {
      ContentBlocks = [.. content.RootElement.EnumerateArray().Select(block => block.Clone())],
    });
    string uri = fixture.Completed.StructuredResult!.Url;
    WorkbenchCommandOutcome copied = await fixture.Handler.ExecuteAsync(
      new CopyStructuredCellsCommand(uri, 1, 1, 1, 1, 2), TestContext.Current.CancellationToken);
    Assert.Null(copied.Error);
    Assert.Equal("合并\t\n", fixture.Clipboard.Tsv);
    Assert.Contains("<th rowspan=\"1\" colspan=\"1\">合并</th>", fixture.Clipboard.Html);
    Assert.Contains("<td rowspan=\"1\" colspan=\"1\"></td>", fixture.Clipboard.Html);
    string? before = fixture.Clipboard.Html;
    await fixture.Handler.ExecuteAsync(new CancelRecognitionCommand(), TestContext.Current.CancellationToken);
    WorkbenchCommandOutcome stale = await fixture.Handler.ExecuteAsync(
      new CopyStructuredCellsCommand(uri, 1, 1, 1, 1, 2), TestContext.Current.CancellationToken);
    Assert.NotNull(stale.Error);
    Assert.Equal(before, fixture.Clipboard.Html);
  }

  [Theory]
  [InlineData("merged", "docx", "第一段行一第二段行二")]
  [InlineData("smart", "xlsx", "第一段行一第二段行二")]
  [InlineData("line", "markdown", "第一段行一\n第二段行二")]
  [InlineData("raw", "text", "第一段行一\n第二段行二")]
  public async Task HandlerExportsSelectedTextModeThroughInference(string mode, string format, string expected)
  {
    await using ResultHandlerFixture fixture = await ResultHandlerFixture.CreateAsync(new PlainLinesInferenceClient());
    WorkbenchCommandOutcome exported = await fixture.Handler.ExecuteAsync(
      new ExportRecognitionResultCommand(format, mode), TestContext.Current.CancellationToken);
    Assert.Null(exported.Error);
    ExportRequest request = Assert.IsType<ExportRequest>(fixture.Client.ExportRequest);
    Assert.Equal(expected, request.RawText);
    Assert.Equal(expected, request.MarkdownText);
    Assert.Empty(request.ContentBlocks!);
    Assert.Equal(format == "text" ? "txt" : format, request.Format);
    Assert.Equal("recognition.exported", Assert.IsType<RecognitionWorkbenchState>(Assert.Single(exported.States)).StatusCode);
  }

  [Fact]
  public async Task HandlerPickerCancellationKeepsResultAndReportsExportCancelled()
  {
    await using ResultHandlerFixture fixture = await ResultHandlerFixture.CreateAsync(new PlainLinesInferenceClient(), cancelPicker: true);
    WorkbenchCommandOutcome cancelled = await fixture.Handler.ExecuteAsync(
      new ExportRecognitionResultCommand("docx", "merged"), TestContext.Current.CancellationToken);
    Assert.Null(cancelled.Error);
    RecognitionWorkbenchState state = Assert.IsType<RecognitionWorkbenchState>(Assert.Single(cancelled.States));
    Assert.Equal("recognition.exportCancelled", state.StatusCode);
    Assert.Equal(fixture.Completed.Result, state.Result);
    Assert.Equal(fixture.Completed.StructuredResult, state.StructuredResult);
    Assert.Null(fixture.Client.ExportRequest);
    Assert.True(fixture.ViewModel.HasResult);
  }

  private sealed class ResultHandlerFixture : IAsyncDisposable
  {
    private readonly string root = Path.Combine(Path.GetTempPath(), $"vibeocr-result-handler-{Guid.NewGuid():N}");
    private readonly WorkbenchResourceBroker broker;
    private readonly WorkbenchAnnotationStore annotations;
    internal PlainLinesInferenceClient Client { get; }
    internal RecognitionViewModel ViewModel { get; }
    internal RecordingStructuredClipboard Clipboard { get; } = new();
    internal DesktopWorkbenchCommandHandler Handler { get; }
    internal RecognitionWorkbenchState Completed { get; private set; } = null!;

    private ResultHandlerFixture(PlainLinesInferenceClient client, bool cancelPicker, SettingsViewModel settings)
    {
      Directory.CreateDirectory(root);
      Client = client;
      ViewModel = new RecognitionViewModel(client, new FixedRecognitionInputs()) { TaskEngine = "rapidocr" };
      broker = new WorkbenchResourceBroker(root);
      annotations = new WorkbenchAnnotationStore(root);
      Handler = new DesktopWorkbenchCommandHandler(
        () => ViewModel, static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
        () => settings, static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])), broker, root, static () => 0, annotations,
        structuredClipboard: Clipboard, inferenceAttached: () => true,
        resultActionPlatform: new FixtureResultActionPlatform(cancelPicker ? null : Path.Combine(root, "export-output")));
    }

    internal static async Task<ResultHandlerFixture> CreateAsync(PlainLinesInferenceClient client, bool cancelPicker = false)
    {
      var settings = new SettingsViewModel(client);
      await settings.LoadSelectionAsync(TestContext.Current.CancellationToken);
      var fixture = new ResultHandlerFixture(client, cancelPicker, settings);
      try
      {
        var completed = new TaskCompletionSource<RecognitionWorkbenchState>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.StateChanged += state =>
        {
          if (state is RecognitionWorkbenchState { IsBusy: false } recognition) completed.TrySetResult(recognition);
        };
        await fixture.Handler.ExecuteAsync(new SelectRecognitionImageCommand(), TestContext.Current.CancellationToken);
        fixture.Completed = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Completed.Result);
        return fixture;
      }
      catch
      {
        await fixture.DisposeAsync();
        throw;
      }
    }

    public async ValueTask DisposeAsync()
    {
      await Handler.DisposeAsync();
      annotations.Dispose();
      broker.Dispose();
      Directory.Delete(root, recursive: true);
    }
  }

  private sealed class FixtureResultActionPlatform(string? path) : IResultActionPlatform
  {
    public Task WriteClipboardAsync(RecognitionResultContent result, ResultCopyFormat format, CancellationToken cancellationToken) =>
      throw new NotSupportedException();
    public Task<string?> PickExportPathAsync(ResultExportFormat format, CancellationToken cancellationToken) => Task.FromResult(path);
    public Task<bool> ConfirmOverwriteAsync(string target, CancellationToken cancellationToken) => Task.FromResult(true);
  }

  private sealed class CallbackTimeProvider : TimeProvider
  {
    internal Action? Callback;
    public override DateTimeOffset GetUtcNow()
    {
      Action? callback = Callback;
      Callback = null;
      callback?.Invoke();
      return DateTimeOffset.UtcNow;
    }
  }

  /// <summary>解码 PNG 像素（存储网格）：ResultImage 断言用。</summary>
  private static async Task<(byte[] Pixels, uint Width, uint Height)> DecodePngPixelsAsync(
    byte[] png)
  {
    using var stream = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(stream))
    {
      writer.WriteBytes(png);
      await writer.StoreAsync();
      writer.DetachStream();
    }
    stream.Seek(0);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
    byte[] pixels = (await decoder.GetPixelDataAsync(
      BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
      ExifOrientationMode.IgnoreExifOrientation,
      ColorManagementMode.DoNotColorManage)).DetachPixelData();
    return (pixels, decoder.PixelWidth, decoder.PixelHeight);
  }

  private sealed class FixedRecognitionInputs : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) => Acquire();

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => Acquire();

    private static Task<RecognitionInput?> Acquire() => Task.FromResult<RecognitionInput?>(
      new RecognitionInput(RecognitionPng, "image/png", "file.png", "file"));
  }

  /// <summary>返回带 text_blocks（含 score/bbox）的纯文本结果。</summary>
  private sealed class PlainLinesInferenceClient : InferenceClientStub
  {
    public JsonElement[]? Blocks { get; init; }
    public JsonElement[]? ContentBlocks { get; init; }
    public ExportRequest? ExportRequest { get; private set; }

    public override Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
      ExportRequest = request;
      return Task.FromResult(new ExportResult(request.OutputPath, 1));
    }

    public override Task<VibeOCR.Runtime.Contracts.Generated.Wire.Health> GetHealthAsync(
      CancellationToken cancellationToken) => Task.FromResult(new VibeOCR.Runtime.Contracts.Generated.Wire.Health
      {
        SchemaVersion = 2, InstanceId = "lines-test", ProtocolVersion = 2,
        Ready = true, Draining = false, Capabilities = [],
      });

    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(new SettingsSnapshot());

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken) => Task.FromResult(new JobRef
      {
        JobId = "job-lines",
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
        Events = Array.Empty<StageEvent>(),
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
              ["content_list"] = JsonSerializer.SerializeToElement(ContentBlocks ?? []),
              ["raw_text"] = JsonSerializer.SerializeToElement("第一段行一\n第二段行二"),
              ["markdown_text"] = JsonSerializer.SerializeToElement("第一段行一\n第二段行二"),
              ["text_blocks"] = Blocks is not null ? JsonSerializer.SerializeToElement(Blocks) : JsonSerializer.SerializeToElement(new object[]
              {
                new { text = "第一段行一", score = 0.9, bbox = new[] { 10, 10, 200, 30 } },
                new { text = "第二段行二", score = 0.5, bbox = new[] { 10, 34, 200, 54 } },
              }),
            },
          },
        ],
        ThroughSequence = afterSequence,
      });
  }

  private sealed class RecordingLinesClipboard : IStructuredClipboardPlatform
  {
    public string? Text { get; private set; }

    public Task WriteTableAsync(string tsv, string html, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("lines results never write tables.");

    public Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
      Text = text;
      return Task.CompletedTask;
    }
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

  [Fact]
  public async Task TransientAssetFetchFailuresDegradePerImageWithoutFailingTheResult()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-structured-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      string png = CreateTempPng();
      // 逐图降级：瞬断传输/类型化传输错误/非受控 PNG/网关脱离都只把该图
      // 标记为不可用；旧实现会让 HttpRequestException 等直接透出，把
      // 已成功的识别误报成整次失败。
      var client = new AssetFaultInferenceClient((assetId, _) => assetId switch
      {
        "asset-http" => Task.FromException<byte[]>(
          new HttpRequestException("connection reset")),
        "asset-runtime" => Task.FromException<byte[]>(
          new VibeOCR.Runtime.Client.RuntimeClientException(
            HttpV2ErrorCode.InternalError, "runtime asset call failed", retryable: true)),
        "asset-invalid" => Task.FromException<byte[]>(
          new InvalidDataException("Result asset is not a bounded PNG.")),
        "asset-detached" => Task.FromException<byte[]>(
          new VibeOCR.App.Inference.InferenceClientNotAttachedException("not attached")),
        "asset-timeout" => Task.FromException<byte[]>(new TaskCanceledException(
          "HTTP request timed out", new TimeoutException(), CancellationToken.None)),
        _ => Task.FromResult(AssetPng),
      })
      {
        Blocks =
        [
          Block("asset-http"),
          Block("asset-runtime"),
          Block("asset-invalid"),
          Block("asset-detached"),
          Block("asset-timeout"),
          new
          {
            type = "image",
            image = new
            {
              available = true,
              job_id = "job-1",
              item_id = "it-0",
              resource = LeakResource,
            },
          },
          Block("asset-ok"),
        ],
      };
      var deferred = new VibeOCR.App.Inference.DeferredInferenceClient();
      deferred.Attach(client);
      var batch = new BatchViewModel(deferred, new DiskBatchFileSource());
      batch.AddFiles([png]);
      await batch.StartAsync(TestContext.Current.CancellationToken);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = CreateBatchStructuredHandler(
        () => batch, resourceRoot, broker, annotationStore);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SetBatchWindowCommand(0),
        TestContext.Current.CancellationToken);
      Assert.Null(outcome.Error);
      WorkbenchResourceReference? structured = Assert.Single(
        Assert.IsType<BatchWorkbenchState>(Assert.Single(outcome.States)).Items!)
        .StructuredResult;
      Assert.NotNull(structured);
      // 五个故障 id + 一个成功 id；缺 asset id 不发起取回。
      Assert.Equal(6, client.AssetFetches);

      using JsonDocument document = await ReadStructuredJsonAsync(broker, structured!.Url);
      JsonElement root = document.RootElement;
      foreach (int index in new[] { 0, 1, 2, 3, 4 })
      {
        JsonElement image = root[index].GetProperty("image");
        Assert.False(image.GetProperty("available").GetBoolean());
        Assert.Equal("结果图片已失效或无法读取", image.GetProperty("reason").GetString());
        Assert.False(image.TryGetProperty("resource", out _), $"block {index} keeps a resource");
      }
      // 缺 asset id：wire 原样携带的不可信 resource 不得透传。
      JsonElement unbound = root[5].GetProperty("image");
      Assert.False(unbound.TryGetProperty("resource", out _));
      // 取回成功：宿主权威 resource 覆盖 wire 副本。
      JsonElement replaced = root[6].GetProperty("image");
      Assert.True(replaced.GetProperty("available").GetBoolean());
      string url = replaced.GetProperty("resource").GetProperty("url").GetString()!;
      var uri = new Uri(url);
      Assert.Equal("https", uri.Scheme);
      Assert.Equal("app.vibeocr", uri.Host);
      Assert.StartsWith("/__resource/", uri.AbsolutePath);
      Assert.NotEqual(LeakUrl, url);
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  [Fact]
  public async Task CancelledAssetFetchStillPropagates()
  {
    string resourceRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-structured-{Guid.NewGuid():N}");
    Directory.CreateDirectory(resourceRoot);
    try
    {
      string png = CreateTempPng();
      var client = new AssetFaultInferenceClient((_, cancellationToken) =>
        Task.FromException<byte[]>(new OperationCanceledException(cancellationToken)))
      {
        Blocks = [Block("asset-cancel")],
      };
      var deferred = new VibeOCR.App.Inference.DeferredInferenceClient();
      deferred.Attach(client);
      var batch = new BatchViewModel(deferred, new DiskBatchFileSource());
      batch.AddFiles([png]);
      await batch.StartAsync(TestContext.Current.CancellationToken);
      using var broker = new WorkbenchResourceBroker(resourceRoot);
      using var annotationStore = new WorkbenchAnnotationStore(resourceRoot);
      await using var handler = CreateBatchStructuredHandler(
        () => batch, resourceRoot, broker, annotationStore);

      // 逐图降级不得吞取消：取消仍从命令边界原样透出（ExecuteAsync 为 ValueTask）。
      await Assert.ThrowsAsync<OperationCanceledException>(async () => await handler.ExecuteAsync(
        new SetBatchWindowCommand(0),
        TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(resourceRoot, recursive: true);
    }
  }

  private const string LeakUrl = "https://untrusted.example/leak.png";

  /// <summary>批量结构化发布路径的公共构造：仅 batch factory 因测试而异。</summary>
  private static DesktopWorkbenchCommandHandler CreateBatchStructuredHandler(
    Func<BatchViewModel> batchFactory,
    string resourceRoot,
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotationStore) => new(
    static () => throw new InvalidOperationException(),
    batchFactory,
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

  private static object LeakResource => new
  {
    url = LeakUrl,
    mediaType = "image/png",
    byteLength = 4,
  };

  private static object Block(string assetId) => new
  {
    type = "image",
    image = new
    {
      available = true,
      job_id = "job-1",
      item_id = "it-0",
      asset_id = assetId,
      resource = LeakResource,
    },
  };

  private static async Task<JsonDocument> ReadStructuredJsonAsync(
    WorkbenchResourceBroker broker,
    string url)
  {
    await using WorkbenchResourceResponse resource = await broker.OpenAsync(
      new Uri(url), TestContext.Current.CancellationToken);
    return await JsonDocument.ParseAsync(
      resource.Content, cancellationToken: TestContext.Current.CancellationToken);
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
  private class StructuredOutcomeInferenceClient : InferenceClientStub
  {
    protected IReadOnlyList<JobItem> _items = [];

    public int AssetFetches { get; protected set; }

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

  /// <summary>
  /// Reuses <see cref="StructuredOutcomeInferenceClient"/>'s submit/items
  /// fixture; outcome blocks and result-asset fetch faults are configurable.
  /// </summary>
  private sealed class AssetFaultInferenceClient(
    Func<string, CancellationToken, Task<byte[]>> fetch)
    : StructuredOutcomeInferenceClient
  {
    public IReadOnlyList<object> Blocks { get; init; } = [];

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
          Payload = new Dictionary<string, JsonElement>
          {
            ["raw_text"] = JsonSerializer.SerializeToElement("结构化结果"),
            ["content_list"] = JsonSerializer.SerializeToElement(Blocks),
          },
        }).ToArray(),
      });

    public override Task<byte[]> FetchResultAssetAsync(
      string jobId, string itemId, string assetId, CancellationToken cancellationToken)
    {
      AssetFetches++;
      return fetch(assetId, cancellationToken);
    }
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
