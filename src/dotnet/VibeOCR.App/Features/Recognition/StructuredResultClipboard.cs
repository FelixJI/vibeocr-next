using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// 结构化结果的原生剪贴板平台接缝：Web 工作台经既有 bridge 命令请求，
/// 宿主在此写入系统剪贴板（表格为 HTML+TSV 双格式，公式为纯文本
/// LaTeX）。与 AnnotatedImagePlatform/ResultActions 相同的 busy 重试与
/// ClipboardBusyException 错误边界，不引入新的 IPC。
/// </summary>
public interface IStructuredClipboardPlatform
{
  Task WriteTableAsync(string tsv, string html, CancellationToken cancellationToken);

  Task WriteTextAsync(string text, CancellationToken cancellationToken);
}

public sealed class WindowsStructuredClipboardPlatform : IStructuredClipboardPlatform
{
  public async Task WriteTableAsync(
    string tsv,
    string html,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(tsv);
    ArgumentException.ThrowIfNullOrWhiteSpace(html);
    var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
    package.SetText(tsv);
    package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
    await SetContentWithRetryAsync(package, cancellationToken);
  }

  public Task WriteTextAsync(string text, CancellationToken cancellationToken)
  {
    // 空文本是合法复制结果（如空 raw 模式）：写入空字符串即清空剪贴板
    // 文本；仅 null 拒绝。复用同一 busy 重试/取消边界。
    ArgumentNullException.ThrowIfNull(text);
    var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
    package.SetText(text);
    return SetContentWithRetryAsync(package, cancellationToken);
  }

  private static async Task SetContentWithRetryAsync(
    DataPackage package,
    CancellationToken cancellationToken)
  {
    for (int attempt = 0; ; attempt++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return;
      }
      catch (COMException) when (attempt < 4)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken);
      }
      catch (COMException error)
      {
        throw new ClipboardBusyException(error);
      }
    }
  }
}

/// <summary>
/// 结构化区块到剪贴板负载（TSV/HTML/LaTeX）的宿主端构建器。区块数据来自
/// 宿主发布的结构化结果资源文件，单元格严格校验行列/合并边界；公式样
/// 文本以单引号前缀写入，避免粘贴进电子表格后被当作公式执行。
/// </summary>
public static class StructuredResultClipboard
{
  public const int MaxRows = 1000;
  public const int MaxColumns = 1000;

  /// <summary>限制物化网格面积，与 Web 预览上限一致；稀疏表同样受限。</summary>
  public const int MaxGridArea = 10_000;

  public sealed record TableCell(
    int Row,
    int Column,
    int Rowspan,
    int Colspan,
    string Text,
    bool IsHeader);

  public static bool TryBuildTable(JsonElement block, out string? tsv, out string? html) =>
    TryBuildTableRange(block, 0, int.MaxValue, 0, int.MaxValue, out tsv, out html);

  /// <summary>
  /// 构建表格子范围的 TSV/HTML：cell 矩形与选择矩形取交集，交集左上映射
  /// 为新锚点并裁剪 span（范围外跨入的合并单元格也参与）；未被任何交集
  /// 覆盖的稀疏位置输出空 td，保持列对齐。整表复制共用本构建器；交集
  /// 面积超过 <see cref="MaxGridArea"/> 时拒绝，不物化无界网格。
  /// </summary>
  public static bool TryBuildTableRange(
    JsonElement block,
    int rowStart,
    int rowEnd,
    int columnStart,
    int columnEnd,
    out string? tsv,
    out string? html)
  {
    tsv = null;
    html = null;
    if (rowStart < 0 || columnStart < 0 || rowEnd < rowStart || columnEnd < columnStart)
      return false;
    if (!TryReadCells(block, out IReadOnlyList<TableCell>? cells, out int rows, out int columns))
      return false;
    int lastRow = Math.Min(rowEnd, rows - 1);
    int lastColumn = Math.Min(columnEnd, columns - 1);
    if (lastRow < rowStart || lastColumn < columnStart)
      return false;
    int gridRows = lastRow - rowStart + 1;
    int gridColumns = lastColumn - columnStart + 1;
    // 整表与范围共用此入口，必须在分配网格前拒绝超限面积。
    if ((long)gridRows * gridColumns > MaxGridArea)
      return false;
    // 交集锚点（选择网格坐标）；-1 表示被同一次交集覆盖，-2 表示未覆盖。
    int[,] anchorRow = new int[gridRows, gridColumns];
    int[,] anchorColumn = new int[gridRows, gridColumns];
    string[,] grid = new string[gridRows, gridColumns];
    Dictionary<(int Row, int Column), TableCell> anchors = [];
    for (int row = 0; row < gridRows; row++)
      for (int column = 0; column < gridColumns; column++)
      {
        anchorRow[row, column] = -2;
        anchorColumn[row, column] = -2;
      }
    foreach (TableCell cell in cells)
    {
      int interRowStart = Math.Max(cell.Row, rowStart);
      int interRowEnd = Math.Min(cell.Row + cell.Rowspan - 1, lastRow);
      int interColumnStart = Math.Max(cell.Column, columnStart);
      int interColumnEnd = Math.Min(cell.Column + cell.Colspan - 1, lastColumn);
      if (interRowStart > interRowEnd || interColumnStart > interColumnEnd)
        continue;
      int anchorGridRow = interRowStart - rowStart;
      int anchorGridColumn = interColumnStart - columnStart;
      int spanRows = interRowEnd - interRowStart + 1;
      int spanColumns = interColumnEnd - interColumnStart + 1;
      anchors[(anchorGridRow, anchorGridColumn)] = cell;
      grid[anchorGridRow, anchorGridColumn] = SafeSpreadsheetText(cell.Text);
      for (int row = 0; row < spanRows; row++)
        for (int column = 0; column < spanColumns; column++)
        {
          if (anchorRow[anchorGridRow + row, anchorGridColumn + column] != -2) return false;
          anchorRow[anchorGridRow + row, anchorGridColumn + column] = anchorGridRow;
          anchorColumn[anchorGridRow + row, anchorGridColumn + column] = anchorGridColumn;
        }
    }
    var tsvBuilder = new StringBuilder();
    for (int row = 0; row < gridRows; row++)
    {
      for (int column = 0; column < gridColumns; column++)
      {
        if (column > 0) tsvBuilder.Append('\t');
        string value = grid[row, column] ?? string.Empty;
        tsvBuilder.Append(value.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
          ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
          : value);
      }
      tsvBuilder.Append('\n');
    }
    var htmlBuilder = new StringBuilder("<table>");
    for (int row = 0; row < gridRows; row++)
    {
      htmlBuilder.Append("<tr>");
      for (int column = 0; column < gridColumns; column++)
      {
        if (anchorRow[row, column] >= 0 &&
          (anchorRow[row, column] != row || anchorColumn[row, column] != column))
          continue; // 被裁剪后的合并单元格覆盖：由其锚点节点表达 span。
        TableCell cell = anchors.TryGetValue((row, column), out TableCell? anchor)
          ? anchor
          : new TableCell(rowStart + row, columnStart + column, 1, 1, string.Empty, false);
        int spanRows = Math.Min(cell.Row + cell.Rowspan - 1, lastRow) - Math.Max(cell.Row, rowStart) + 1;
        int spanColumns = Math.Min(cell.Column + cell.Colspan - 1, lastColumn) - Math.Max(cell.Column, columnStart) + 1;
        htmlBuilder.Append('<').Append(cell.IsHeader ? "th" : "td")
          .Append(" rowspan=\"").Append(spanRows)
          .Append("\" colspan=\"").Append(spanColumns)
          .Append("\">").Append(EscapeHtml(grid[row, column] ?? string.Empty))
          .Append("</").Append(cell.IsHeader ? "th" : "td").Append('>');
      }
      htmlBuilder.Append("</tr>");
    }
    htmlBuilder.Append("</table>");
    tsv = tsvBuilder.ToString();
    html = htmlBuilder.ToString();
    return true;
  }

  public static bool TryGetFormulaText(JsonElement block, out string? latex)
  {
    latex = null;
    return block.ValueKind == JsonValueKind.Object &&
      block.TryGetProperty("type", out JsonElement type) &&
      type.ValueKind == JsonValueKind.String &&
      type.GetString() == "formula" &&
      block.TryGetProperty("text", out JsonElement text) &&
      text.ValueKind == JsonValueKind.String &&
      (latex = text.GetString()) is not null;
  }

  internal static bool TryReadBlockText(JsonElement block, out string? text)
  {
    text = null;
    return block.ValueKind == JsonValueKind.Object &&
      block.TryGetProperty("text", out JsonElement value) &&
      value.ValueKind == JsonValueKind.String &&
      (text = value.GetString()) is not null;
  }

  /// <summary>
  /// 读取 text_blocks/lines 行的 bbox：4 个有限数、0≤x1<x2≤1000 且
  /// 0≤y1<y2≤1000；缺失/畸形/退化/越界视为无 bbox（文本保留），与
  /// Classic 的 bbox=None 同等对待。
  /// </summary>
  internal static bool TryReadBlockBbox(JsonElement block, out double[]? bbox)
  {
    bbox = null;
    if (!block.TryGetProperty("bbox", out JsonElement element) ||
      element.ValueKind != JsonValueKind.Array ||
      element.GetArrayLength() != 4)
      return false;
    double[] coordinates = new double[4];
    for (int index = 0; index < 4; index++)
    {
      if (element[index].ValueKind != JsonValueKind.Number ||
        !element[index].TryGetDouble(out double coordinate) ||
        !double.IsFinite(coordinate))
        return false;
      coordinates[index] = coordinate;
    }
    if (coordinates[0] < 0 || coordinates[1] < 0 ||
      coordinates[2] > 1000 || coordinates[3] > 1000 ||
      coordinates[0] >= coordinates[2] || coordinates[1] >= coordinates[3])
      return false;
    bbox = coordinates;
    return true;
  }

  /// <summary>
  /// Classic text_layout._sort_indexed 行序映射：过滤空文本块后返回按
  /// Classic 序排列的源块索引——任一块 order 非 -1（缺省/非整数为 -1）
  /// 时按 (order, bbox) 升序，否则按 bbox (y1,x1) 升序；无/无效 bbox
  /// 排最后，等键保持源数组相对顺序（稳定）。行投影与行编辑共用本
  /// 映射，重复文本的编辑仍命中正确源块。
  /// </summary>
  internal static int[] OrderLineSourceIndexes(JsonElement[] blocks)
  {
    List<(int Index, int Order, double Y, double X)> entries = [];
    for (int index = 0; index < blocks.Length; index++)
    {
      JsonElement block = blocks[index];
      if (!TryReadBlockText(block, out string? text) || string.IsNullOrWhiteSpace(text))
        continue;
      int order = -1;
      if (block.TryGetProperty("order", out JsonElement element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out int wireOrder))
        order = wireOrder;
      double y = double.PositiveInfinity, x = double.PositiveInfinity;
      if (TryReadBlockBbox(block, out double[]? bbox))
      {
        y = bbox![1];
        x = bbox[0];
      }
      entries.Add((index, order, y, x));
    }
    // OrderBy 稳定：等键保持源数组相对顺序，与 Python sorted 一致。
    IEnumerable<(int Index, int Order, double Y, double X)> sorted =
      entries.Any(entry => entry.Order != -1)
        ? entries.OrderBy(entry => entry.Order).ThenBy(entry => entry.Y).ThenBy(entry => entry.X)
        : entries.OrderBy(entry => entry.Y).ThenBy(entry => entry.X);
    return [.. sorted.Select(entry => entry.Index)];
  }

  /// <summary>行块文本排版模式（与 Classic keep/merge/smart 对齐）。</summary>
  public const string LineModeRaw = "raw";
  public const string LineModeLine = "line";
  public const string LineModeMerged = "merged";
  public const string LineModeSmart = "smart";
  private const double ParagraphGapFactor = 1.5;

  /// <summary>
  /// 从 lines 区块（宿主发布的权威行数据）按模式拼接纯文本：raw/line
  /// 逐行换行（与 Runtime raw_text 同一拼接），merged 合并成一段，smart
  /// 按 bbox 垂直间距（&gt;1.5×前行高）分段、段间空行。行序由发布时的
  /// Classic 行序映射确定（本方法不重排）；缺/无效 bbox 的行不产生
  /// 分段边界，但文本保留（bbox 校验与投影共享）。
  /// </summary>
  public static bool TryBuildLinesText(JsonElement block, string mode, out string? text)
  {
    text = null;
    if (block.ValueKind != JsonValueKind.Object ||
      !block.TryGetProperty("lines", out JsonElement lines) ||
      lines.ValueKind != JsonValueKind.Array)
      return false;
    List<(string Value, double[]? Box)> parsed = [];
    foreach (JsonElement line in lines.EnumerateArray())
    {
      if (line.ValueKind != JsonValueKind.Object ||
        !line.TryGetProperty("text", out JsonElement value) ||
        value.ValueKind != JsonValueKind.String)
        return false;
      TryReadBlockBbox(line, out double[]? box);
      parsed.Add((value.GetString()!, box));
    }
    if (parsed.Count == 0) return false;
    text = mode switch
    {
      LineModeMerged => string.Concat(parsed.Select(line => line.Value)),
      LineModeSmart => string.Join("\n\n", SplitIntoParagraphs(parsed)),
      _ => string.Join("\n", parsed.Select(line => line.Value)),
    };
    return true;
  }

  private static IEnumerable<string> SplitIntoParagraphs(
    IReadOnlyList<(string Value, double[]? Box)> lines)
  {
    var current = new List<string> { lines[0].Value };
    for (int index = 1; index < lines.Count; index++)
    {
      double[]? previous = lines[index - 1].Box;
      double[]? currentBox = lines[index].Box;
      bool paragraphBreak = false;
      if (previous is not null && currentBox is not null)
      {
        double height = previous[3] - previous[1];
        paragraphBreak = height > 0 &&
          currentBox[1] - previous[3] > ParagraphGapFactor * height;
      }
      if (paragraphBreak)
      {
        yield return string.Concat(current);
        current = [lines[index].Value];
      }
      else current.Add(lines[index].Value);
    }
    yield return string.Concat(current);
  }

  /// <summary>
  /// 电子表格公式注入防护：以 = + - @ 开头的单元格文本前置单引号，
  /// 值本身不修改，粘贴端按文本解释。
  /// </summary>
  public static string SafeSpreadsheetText(string text)
  {
    ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();
    return !trimmed.IsEmpty && trimmed[0] is '=' or '+' or '-' or '@'
      ? "'" + text
      : text;
  }

  private static bool TryReadCells(
    JsonElement block,
    out IReadOnlyList<TableCell> cells,
    out int rows,
    out int columns)
  {
    cells = [];
    rows = 0;
    columns = 0;
    if (block.ValueKind != JsonValueKind.Object ||
      !block.TryGetProperty("type", out JsonElement type) ||
      type.ValueKind != JsonValueKind.String ||
      type.GetString() != "table" ||
      !block.TryGetProperty("table", out JsonElement table) ||
      table.ValueKind != JsonValueKind.Object ||
      !table.TryGetProperty("schema_version", out JsonElement schema) ||
      schema.ValueKind != JsonValueKind.Number ||
      !schema.TryGetInt32(out int version) || version != 1 ||
      !table.TryGetProperty("row_count", out JsonElement rowCount) ||
      !table.TryGetProperty("column_count", out JsonElement columnCount) ||
      rowCount.ValueKind != JsonValueKind.Number ||
      columnCount.ValueKind != JsonValueKind.Number ||
      !rowCount.TryGetInt32(out rows) || !columnCount.TryGetInt32(out columns))
      return false;
    if (rows is < 1 or > MaxRows || columns is < 1 or > MaxColumns ||
      !table.TryGetProperty("cells", out JsonElement cellArray) ||
      cellArray.ValueKind != JsonValueKind.Array)
      return false;
    var parsed = new List<TableCell>();
    foreach (JsonElement element in cellArray.EnumerateArray())
    {
      if (element.ValueKind != JsonValueKind.Object ||
        !element.TryGetProperty("row", out JsonElement row) ||
        !element.TryGetProperty("column", out JsonElement column) ||
        !element.TryGetProperty("rowspan", out JsonElement rowspan) ||
        !element.TryGetProperty("colspan", out JsonElement colspan) ||
        row.ValueKind != JsonValueKind.Number ||
        column.ValueKind != JsonValueKind.Number ||
        rowspan.ValueKind != JsonValueKind.Number ||
        colspan.ValueKind != JsonValueKind.Number ||
        !row.TryGetInt32(out int cellRow) ||
        !column.TryGetInt32(out int cellColumn) ||
        !rowspan.TryGetInt32(out int cellRowspan) ||
        !colspan.TryGetInt32(out int cellColspan))
        return false;
      if (cellRow < 0 || cellColumn < 0 || cellRowspan < 1 || cellColspan < 1 ||
        cellRow >= rows || cellColumn >= columns ||
        cellRowspan > rows - cellRow || cellColspan > columns - cellColumn ||
        !element.TryGetProperty("text", out JsonElement text) ||
        text.ValueKind != JsonValueKind.String)
        return false;
      bool isHeader = element.TryGetProperty("is_header", out JsonElement header) &&
        header.ValueKind == JsonValueKind.True;
      parsed.Add(new TableCell(
        cellRow,
        cellColumn,
        cellRowspan,
        cellColspan,
        text.GetString()!,
        isHeader));
    }
    cells = parsed;
    return true;
  }

  // Excel 需 same-cell 标记，否则 HTML 换行会拆成新的表格行。
  private static string EscapeHtml(string text) => text
    .Replace("&", "&amp;", StringComparison.Ordinal)
    .Replace("<", "&lt;", StringComparison.Ordinal)
    .Replace(">", "&gt;", StringComparison.Ordinal)
    .Replace("\"", "&quot;", StringComparison.Ordinal)
    .ReplaceLineEndings("<br style=\"mso-data-placement:same-cell\">");
}
