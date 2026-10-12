using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace VibeOCR.App.Features.Recognition;

/// <summary>选区动作栏复用 WinUI FontIcon；保留默认字体族的系统回退和主题前景。</summary>
internal static class ScreenshotToolbarIcons
{
  internal const string DefaultKey = "textRecognition";

  private static readonly IReadOnlyDictionary<string, string> Glyphs =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      // 编辑工具：与 Web 编辑器 TOOL_ICONS 语义一一对应（矩形/椭圆/箭头/
      // 画笔/荧光笔/文字/序号/马赛克/模糊/屏蔽/去水印/取字）。
      ["rectangle"] = "\uE922",
      ["ellipse"] = "\uEA3A",
      ["arrow"] = "\uE74A",
      ["pen"] = "\uE70F",
      ["highlighter"] = "\uE7E6",
      ["text"] = "\uE8D2",
      ["numbering"] = "\u2460",
      // shortcut: 系统网格字形近似马赛克，系统库提供专用字形时替换。
      ["mosaic"] = "\uE75F",
      // shortcut: 系统光晕字形近似模糊，系统库提供专用字形时替换。
      ["blur"] = "\uE706",
      ["exclude"] = "\uE72E",
      ["inpaint"] = "\uE75C",
      ["textSelect"] = "\uE8B3",
      // 识别三分类 + 长截图/设置/本地输出。
      ["textRecognition"] = "\uE8A5",
      ["tableRecognition"] = "\uE787",
      ["formulaRecognition"] = "\uE8EF",
      ["scrollCapture"] = "\uE70D",
      ["settings"] = "\uE713",
      ["copy"] = "\uE8C8",
      ["save"] = "\uE74E",
      ["pin"] = "\uE718",
      // 会话控制。
      ["reselect"] = "\uE72C",
      ["exit"] = "\uE8BB",
      ["magnifier"] = "\uE721",
      ["confirm"] = "\uE73E",
      ["undo"] = "\uE7A7",
      ["redo"] = "\uE7A6",
    };

  /// <summary>返回该键的非空官方字形；未知键返回 null（自检/回归测试用）。</summary>
  internal static string? TryGetGlyph(string key) =>
    Glyphs.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value) ? value : null;

  /// <summary>目录是否包含该键且字形非空。</summary>
  internal static bool HasIcon(string key) => TryGetGlyph(key) is not null;

  /// <summary>按稳定键创建 20px 字形图标；未知键回落默认图标，不抛错。</summary>
  internal static FontIcon CreateIcon(string key)
  {
    var icon = new FontIcon
    {
      Glyph = TryGetGlyph(key) ?? Glyphs[DefaultKey],
      FontSize = 20,
    };
    // 编号是普通 Unicode 字符，需文本字体而非系统图标字体。
    if (key == "numbering") icon.FontFamily = new FontFamily("Segoe UI");
    return icon;
  }
}
