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
    ArgumentException.ThrowIfNullOrWhiteSpace(tsv);
    ArgumentException.ThrowIfNullOrWhiteSpace(html);
    var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
    package.SetText(tsv);
    package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
    await SetContentWithRetryAsync(package, cancellationToken);
  }

  public Task WriteTextAsync(string text, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(text);
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

  public sealed record TableCell(
    int Row,
    int Column,
    int Rowspan,
    int Colspan,
    string Text,
    bool IsHeader);

  public static bool TryBuildTable(JsonElement block, out string? tsv, out string? html)
  {
    tsv = null;
    html = null;
    if (!TryReadCells(block, out IReadOnlyList<TableCell>? cells, out int rows, out int columns))
      return false;
    string[,] grid = new string[rows, columns];
    foreach (TableCell cell in cells)
      grid[cell.Row, cell.Column] = SafeSpreadsheetText(cell.Text);
    var tsvBuilder = new StringBuilder();
    for (int row = 0; row < rows; row++)
    {
      for (int column = 0; column < columns; column++)
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
    for (int row = 0; row < rows; row++)
    {
      htmlBuilder.Append("<tr>");
      foreach (TableCell cell in cells.Where(cell => cell.Row == row).OrderBy(cell => cell.Column))
      {
        htmlBuilder.Append('<').Append(cell.IsHeader ? "th" : "td")
          .Append(" rowspan=\"").Append(cell.Rowspan)
          .Append("\" colspan=\"").Append(cell.Colspan)
          .Append("\">").Append(EscapeHtml(grid[cell.Row, cell.Column]))
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

  private static string EscapeHtml(string text) => text
    .Replace("&", "&amp;", StringComparison.Ordinal)
    .Replace("<", "&lt;", StringComparison.Ordinal)
    .Replace(">", "&gt;", StringComparison.Ordinal)
    .Replace("\"", "&quot;", StringComparison.Ordinal);
}
