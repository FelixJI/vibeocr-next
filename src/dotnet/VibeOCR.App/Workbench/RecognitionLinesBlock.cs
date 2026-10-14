using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Recognition;

namespace VibeOCR.App.Workbench;

/// <summary>纯文本 OCR 的稳定行序投影与预计算排版；展示、编辑和复制共用。</summary>
internal static class RecognitionLinesBlock
{
  public const int MaximumLines = 10_000;

  /// <summary>
  /// 构建行数组（Classic text_layout 行序：order 优先、缺省按 bbox
  /// (y1,x1)、无 bbox 稳定在后）；无有效行返回 null。score 缺失表示上游未提供置信度
  /// （unknown，不伪装 0%/100%）；bbox 无效或缺失时省略，行文本保留（bbox 校验与
  /// 行序映射均与 StructuredResultClipboard 的行编辑共享）。
  /// </summary>
  public static JsonArray? Build(JsonElement[] blocks)
  {
    ArgumentNullException.ThrowIfNull(blocks);
    int[] sourceIndexes = StructuredResultClipboard.OrderLineSourceIndexes(blocks);
    JsonArray? lines = null;
    foreach (int sourceIndex in sourceIndexes)
    {
      JsonElement block = blocks[sourceIndex];
      if (!StructuredResultClipboard.TryReadBlockText(block, out string? text) || string.IsNullOrWhiteSpace(text))
      {
        continue;
      }
      if (lines is null) lines = [];
      if (lines.Count >= MaximumLines) return null;
      JsonObject line = new() { ["text"] = text };
      if (block.TryGetProperty("score", out JsonElement score) &&
        score.ValueKind == JsonValueKind.Number &&
        score.TryGetDouble(out double value) && double.IsFinite(value) && value is >= 0 and <= 1)
      {
        line["score"] = value;
      }
      if (StructuredResultClipboard.TryReadBlockBbox(block, out double[]? bbox))
      {
        line["bbox"] = new JsonArray(bbox![0], bbox[1], bbox[2], bbox[3]);
      }
      if (block.TryGetProperty("order", out JsonElement order) &&
        order.ValueKind == JsonValueKind.Number &&
        order.TryGetInt32(out int orderValue) && orderValue >= 0)
      {
        line["order"] = orderValue;
      }
      if (block.TryGetProperty("is_manually_edited", out JsonElement edited) && edited.ValueKind == JsonValueKind.True)
        line["is_manually_edited"] = true;
      lines.Add(line);
    }
    return lines;
  }

  /// <summary>raw 保留上游原文，其余模式由宿主按权威行数据计算。</summary>
  public static JsonObject? BuildBlock(JsonElement[] blocks, string rawText)
  {
    JsonArray? lines = Build(blocks);
    if (lines is null) return null;
    JsonObject block = new()
    {
      ["type"] = "lines",
      ["schema_version"] = 1,
      ["lines"] = lines,
    };
    JsonElement projection = JsonSerializer.SerializeToElement(new { lines });
    JsonObject modes = new() { ["raw"] = rawText };
    foreach (string mode in new[]
      {
        StructuredResultClipboard.LineModeLine,
        StructuredResultClipboard.LineModeMerged,
        StructuredResultClipboard.LineModeSmart,
      })
    {
      if (StructuredResultClipboard.TryBuildLinesText(projection, mode, out string? text) &&
        text is not null)
        modes[mode] = text;
    }
    block["modes"] = modes;
    return block;
  }
}
