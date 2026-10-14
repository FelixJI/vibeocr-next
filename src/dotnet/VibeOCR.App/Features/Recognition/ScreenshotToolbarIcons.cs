using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace VibeOCR.App.Features.Recognition;

// 缺少合适系统字形的动作使用自绘路径，避免日历、亮度等误导图标。
internal static class ScreenshotToolbarIcons
{
  internal const string DefaultKey = "textRecognition";

  // 官方字形确证的字体键（与 Web 编辑器 TOOL_ICONS 语义对应）。
  private static readonly IReadOnlyDictionary<string, string> Glyphs =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      // 编辑工具。
      ["pen"] = "\uE70F",        // Edit（铅笔）
      ["highlighter"] = "\uE7E6", // Highlight（荧光笔）
      ["text"] = "\uE8D2",       // Font（文字）
      ["numbering"] = "\u2460",  // ①：普通 Unicode，需文本字体。
      ["exclude"] = "\uE72E",    // Blocked（屏蔽区域）
      ["inpaint"] = "\uE75C",    // EraseTool（去水印）
      ["textSelect"] = "\uE8B3", // SelectAll
      // 识别 + 输出 + 会话控制。
      ["textRecognition"] = "\uE8A5", // Document
      ["formulaRecognition"] = "\uE8EF", // Calculator
      ["settings"] = "\uE713",   // Setting
      ["copy"] = "\uE8C8",       // Copy
      ["save"] = "\uE74E",       // Save
      ["pin"] = "\uE718",        // Pin
      ["reselect"] = "\uE72C",   // Refresh
      ["exit"] = "\uE8BB",       // ChromeClose（退出）
      ["magnifier"] = "\uE721",  // Search
      ["confirm"] = "\uE73E",    // CheckMark
      ["undo"] = "\uE7A7",       // Undo
      ["redo"] = "\uE7A6",       // Redo
    };

  // 官方字体缺语义对应（或曾核实误导）的自绘键：20×20 视图框。
  private static readonly IReadOnlyDictionary<string, string> Paths =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      // even-odd 填充保留中心空白。
      ["rectangle"] = "F0 M2,5.5 H18 V15 H2 Z M4,7.5 H16 V13 H4 Z",
      // 椭圆：环形描边。
      ["ellipse"] =
        "F0 M3,10 a7,7 0 1 0 14,0 a7,7 0 1 0 -14,0 Z M5.5,10 a4.5,4.5 0 1 0 9,0 a4.5,4.5 0 1 0 -9,0 Z",
      // 箭头：右上方向实心箭（含箭头两翼）。
      ["arrow"] = "M17,3 H10.6 L12.9,5.3 4.4,13.8 6.2,15.6 14.7,7.1 17,9.4 Z",
      // 马赛克：3×3 实心方块阵（像素块遮挡）。
      ["mosaic"] =
        "M2,2 h4 v4 h-4 z M8,2 h4 v4 h-4 z M14,2 h4 v4 h-4 z M2,8 h4 v4 h-4 z M8,8 h4 v4 h-4 z M14,8 h4 v4 h-4 z M2,14 h4 v4 h-4 z M8,14 h4 v4 h-4 z M14,14 h4 v4 h-4 z",
      // 模糊：水滴（与 Web 编辑器 Droplets 语义一致）。
      ["blur"] =
        "M10,2.6 C13.4,7 15.8,9.7 15.8,12.9 A5.8,5.8 0 0 1 4.2,12.9 C4.2,9.7 6.6,7 10,2.6 Z",
      // 表格识别：外框 + 表头分隔 + 两列分隔（非零填充并集）。
      ["tableRecognition"] =
        "F1 M2.5,4 H17.5 V5.8 H2.5 Z M2.5,14.2 H17.5 V16 H2.5 Z M2.5,4 H4.3 V16 H2.5 Z M15.7,4 H17.5 V16 H15.7 Z M4.3,7.2 H15.7 V8.6 H4.3 Z M8.2,5.8 H9.6 V14.2 H8.2 Z M11.9,5.8 H13.3 V14.2 H11.9 Z",
      // 长截图：取景框 + 向下滚动箭头（evenodd：环 + 内部箭头）。
      ["scrollCapture"] =
        "F0 M2.5,2.5 H17.5 V17.5 H2.5 Z M4.5,4.5 H15.5 V15.5 H4.5 Z M8.9,6 H11.1 V10.2 L13.2,8.1 14.6,9.5 10,14.1 5.4,9.5 6.8,8.1 8.9,10.2 Z",
    };

  /// <summary>字体键的官方字形；自绘键返回 null（自检/回归测试用）。</summary>
  internal static string? TryGetGlyph(string key) =>
    Glyphs.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value) ? value : null;

  /// <summary>自绘键的 20×20 路径数据；字体键返回 null（自检/回归测试用）。</summary>
  internal static string? TryGetPath(string key) =>
    Paths.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value) ? value : null;

  /// <summary>目录是否包含该键（字体字形或自绘路径，至少其一）。</summary>
  internal static bool HasIcon(string key) =>
    TryGetGlyph(key) is not null || TryGetPath(key) is not null;

  /// <summary>按稳定键创建 20px 图标；自绘路径优先，未知键回落默认图标，不抛错。</summary>
  internal static IconElement CreateIcon(string key)
  {
    string resolved = HasIcon(key) ? key : DefaultKey;
    if (Paths.TryGetValue(resolved, out string? path))
      // WinUI3 Geometry 无 Parse：经 XamlReader 解析 Data 字符串（picker
      // 每次截图仅建一次按钮，无性能顾虑）。
      return (PathIcon)XamlReader.Load(
        $"<PathIcon xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{path}'/>");
    var icon = new FontIcon
    {
      Glyph = Glyphs[resolved],
      FontSize = 20,
    };
    // 编号是普通 Unicode 字符，需文本字体而非系统图标字体。
    if (resolved == "numbering") icon.FontFamily = new FontFamily("Segoe UI");
    return icon;
  }
}
