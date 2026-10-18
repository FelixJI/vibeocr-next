using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VibeOCR.App.Workbench;

/// <summary>.vibp 工程包读写遇到的格式/边界错误；打开路径失败时
/// 当前工程与原文件保持不变，由调用方（宿主 owner）负责不替换会话。</summary>
public sealed class VibpProjectFormatException(
  string message,
  Exception? innerException = null) : Exception(message, innerException);

/// <summary>.vibp v1 工程包资源边界常量（manifest/entry/字节/像素预算）。</summary>
public static class VibpProjectLimits
{
  public const int SchemaVersion = 1;
  public const string ProjectExtension = ".vibp";
  public const string ManifestEntryName = "manifest.json";

  public const long MaximumManifestBytes = 256L * 1024;
  public const long MaximumFileBytes = 192L << 20;
  public const long MaximumDecompressedBytes = 192L << 20;
  /// <summary>含 manifest.json 在内的 entry 总数上限；也是补丁个数的显式数量边界。</summary>
  public const int MaximumEntries = 128;

  public const long MaximumSourceBytes = 64L << 20;
  public const long MaximumSourcePixels = 100_000_000;
  public const int MaximumImageSide = 32_768;

  public const int MaximumAssets = 16;
  public const long MaximumAssetPixels = 32_000_000;
  public const long MaximumAssetAggregatePixels = 32_000_000;
  public const long MaximumAssetAggregateBytes = 64L << 20;

  public const long MaximumPatchPixels = 1_000_000;
  public const long MaximumPatchAggregatePixels = 8_000_000;
  public const long MaximumPatchAggregateBytes = 64L << 20;

  // 有限数值边界：不设数量预算的字段仍必须是有限且有界数字/长度，
  // 防止把超范围坐标或超长字符串塞进 256 KiB 语义层。
  public const double MaximumCoordinateMagnitude = 10_000_000;
  public const double MaximumScalarMagnitude = 1_000;
  public const double MaximumTextWidth = 10_000_000;
  public const int MaximumMarks = 4_096;
  public const int MaximumMarkPoints = 65_536;
  public const int MaximumMarkTextLength = 8_192;
  public const int MaximumFontFamilyLength = 256;
}

/// <summary>逻辑画布 frame：恢复时必须严格等于保存值，不做坐标重投影。</summary>
public sealed record VibpCanvas
{
  public required int Width { get; init; }
  public required int Height { get; init; }
}

public sealed record VibpPoint
{
  public required double X { get; init; }
  public required double Y { get; init; }
}

public sealed record VibpCrop
{
  public required VibpPoint Start { get; init; }
  public required VibpPoint End { get; init; }
}

public sealed record VibpSize
{
  public required int Width { get; init; }
  public required int Height { get; init; }
}

/// <summary>宿主会话持有的源图（未旋转原始字节）；dims 由编码头核实。</summary>
public sealed record VibpSourceResource
{
  public required string MediaType { get; init; }
  public required int Width { get; init; }
  public required int Height { get; init; }
  public required long Length { get; init; }
}

/// <summary>导入图片资产（宿主导入路径统一重编码为 PNG）。</summary>
public sealed record VibpAssetResource
{
  public required string Id { get; init; }
  public required int Width { get; init; }
  public required int Height { get; init; }
  public required long Length { get; init; }
}

/// <summary>已应用像素修补：原图（未旋转）整数空间矩形，补丁 PNG 尺寸等于矩形尺寸；
/// Length 为该补丁 entry 的声明字节数，读取时按实际流式字节数核对。</summary>
public sealed record VibpPatch
{
  public required int Id { get; init; }
  public required int X { get; init; }
  public required int Y { get; init; }
  public required int Width { get; init; }
  public required int Height { get; init; }
  public required long Length { get; init; }
}

public sealed record VibpFillStyle
{
  public string? Color { get; init; }
  public double? Opacity { get; init; }
  public bool? Enabled { get; init; }
}

public sealed record VibpTextOutlineStyle
{
  public required string Color { get; init; }
  public required double Width { get; init; }
}

/// <summary>标记样式：与 web 编辑器 AnnotationStyle 同字段集；
/// 不持久化临时选择/草稿/剪贴板/预览状态。</summary>
public sealed record VibpMarkStyle
{
  public string? Color { get; init; }
  public double? StrokeWidth { get; init; }
  public double? FontSize { get; init; }
  /// <summary>马赛克/模糊强度：连续值 [1,3]（旧档位 1/2/3 为兼容锚点）。</summary>
  public double? Intensity { get; init; }
  /// <summary>自由笔刷效果（马赛克/模糊）的笔刷直径（显示单位 px）。</summary>
  public double? BrushSize { get; init; }
  public double? StrokeOpacity { get; init; }
  public VibpFillStyle? Fill { get; init; }
  public string? LineStyle { get; init; }
  public double? CornerRadius { get; init; }
  public string? ArrowStyle { get; init; }
  public bool? ArrowDouble { get; init; }
  public string? FontFamily { get; init; }
  public bool? FontBold { get; init; }
  public bool? FontItalic { get; init; }
  public bool? FontUnderline { get; init; }
  public string? TextAlign { get; init; }
  public VibpFillStyle? TextBackground { get; init; }
  public VibpTextOutlineStyle? TextOutline { get; init; }
  public string? NumberFormat { get; init; }
  public string? NumberShape { get; init; }
  public string? CalloutStyle { get; init; }
}

public sealed record VibpMark
{
  public required string Id { get; init; }
  public required string Tool { get; init; }
  public required VibpPoint Start { get; init; }
  public required VibpPoint End { get; init; }
  public string? Text { get; init; }
  public string? AssetId { get; init; }
  public double? TextWidth { get; init; }
  public VibpMarkStyle? Style { get; init; }
  public IReadOnlyList<VibpPoint>? Points { get; init; }
  public int? Ordinal { get; init; }
  public string? GroupId { get; init; }
  public bool? Locked { get; init; }
  public int? Rotation { get; init; }
  public bool? FlipX { get; init; }
  public bool? FlipY { get; init; }
  public double? ObjectScale { get; init; }
}

/// <summary>编辑器持久状态：当前 rotation/crop/resize/marks/applied patches。</summary>
public sealed record VibpEditorState
{
  public required int Rotation { get; init; }
  public VibpCrop? Crop { get; init; }
  public VibpSize? Resize { get; init; }
  public required IReadOnlyList<VibpMark> Marks { get; init; }
  public IReadOnlyList<VibpPatch> Patches { get; init; } = [];
}

/// <summary>.vibp v1 manifest；schemaVersion 之外的字段集合即 v1 全部语义。</summary>
public sealed record VibpProjectManifest
{
  public required int SchemaVersion { get; init; }
  public required VibpCanvas Canvas { get; init; }
  public required VibpSourceResource Source { get; init; }
  public required IReadOnlyList<VibpAssetResource> Assets { get; init; } = [];
  public required VibpEditorState State { get; init; }
}

internal static class VibpProjectRules
{
  internal static readonly JsonSerializerOptions ManifestJson = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    WriteIndented = true,
  };

  private static readonly Regex SixHexColor =
    new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
  private static readonly Regex AssetIdPattern =
    new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
  private static readonly Regex MarkIdPattern =
    new("^mark-[1-9][0-9]{0,8}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
  private static readonly Regex GroupIdPattern =
    new("^group-[1-9][0-9]{0,8}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

  private static readonly HashSet<string> MarkTools =
  [
    "rectangle", "ellipse", "arrow", "line", "polyline", "text", "callout",
    "mosaic", "blur", "pen", "highlighter", "numbering", "image", "exclude",
    "solid",
  ];
  private static readonly HashSet<string> PointTools = ["pen", "highlighter", "polyline"];
  private static readonly HashSet<string> TextTools = ["text", "callout"];
  private static readonly HashSet<string> LineStyles = ["solid", "dashed", "dotted"];
  private static readonly HashSet<string> ArrowStyles = ["open", "hollow", "solid"];
  private static readonly HashSet<string> TextAligns = ["left", "center", "right"];
  private static readonly HashSet<string> NumberFormats = ["number", "upper", "lower"];
  private static readonly HashSet<string> NumberShapes = ["circle", "square", "diamond", "none"];
  private static readonly HashSet<string> CalloutStyles = ["bubble", "arrow"];
  private static readonly HashSet<string> SourceMediaTypes =
    ["image/png", "image/jpeg", "image/bmp", "image/webp"];

  /// <summary>声明式语义校验（读写共用）：有限数值、白名单枚举、唯一 id、
  /// asset/patch 引用闭合、几何与像素预算。任何违例抛 VibpProjectFormatException。</summary>
  public static void ValidateManifest(VibpProjectManifest manifest)
  {
    ArgumentNullException.ThrowIfNull(manifest);
    if (manifest.SchemaVersion != VibpProjectLimits.SchemaVersion)
    {
      throw new VibpProjectFormatException(
        $"Unsupported .vibp schemaVersion {manifest.SchemaVersion}; expected {VibpProjectLimits.SchemaVersion}.");
    }
    if (manifest.Canvas is null || manifest.Source is null ||
        manifest.State is null || manifest.Assets is null)
    {
      throw new VibpProjectFormatException(
        "Project manifest is missing or nullifies a required section.");
    }
    if (manifest.State.Marks is null || manifest.State.Patches is null)
    {
      throw new VibpProjectFormatException(
        "Project manifest is missing or nullifies a required state list.");
    }
    foreach (VibpAssetResource asset in manifest.Assets)
    {
      if (asset is null)
      {
        throw new VibpProjectFormatException(
          "Project manifest contains a null asset entry.");
      }
    }
    foreach (VibpMark mark in manifest.State.Marks)
    {
      if (mark is null)
      {
        throw new VibpProjectFormatException(
          "Project manifest contains a null mark entry.");
      }
    }
    foreach (VibpPatch patch in manifest.State.Patches)
    {
      if (patch is null)
      {
        throw new VibpProjectFormatException(
          "Project manifest contains a null patch entry.");
      }
    }
    ValidateCanvas(manifest.Canvas);
    ValidateSource(manifest.Source);
    ValidateAssetsAndRefs(manifest);
    ValidateState(manifest.State, manifest.Source, manifest.Assets);
    ValidateEntryBudget(manifest);
  }

  /// <summary>由 manifest 派生的精确资源 entry 白名单（不含 manifest.json），
  /// 与声明长度一一对应，供写入闭合与读取白名单校验共用。</summary>
  public static List<(string Name, long DeclaredLength)> ExpectedResourceEntries(
    VibpProjectManifest manifest)
  {
    List<(string, long)> entries =
      [(SourceEntryName(manifest.Source.MediaType), manifest.Source.Length)];
    foreach (VibpAssetResource asset in manifest.Assets)
    {
      entries.Add(($"asset-{asset.Id}.png", asset.Length));
    }
    foreach (VibpPatch patch in manifest.State.Patches)
    {
      entries.Add(($"patch-{patch.Id}.png", patch.Length));
    }
    return entries;
  }

  public static List<string> ExpectedResourceEntryNames(VibpProjectManifest manifest) =>
    [.. ExpectedResourceEntries(manifest).Select(entry => entry.Name)];

  public static string SourceEntryName(string mediaType) => mediaType switch
  {
    "image/png" => "source.png",
    "image/jpeg" => "source.jpg",
    "image/bmp" => "source.bmp",
    "image/webp" => "source.webp",
    _ => throw new VibpProjectFormatException($"Unsupported source media type: {mediaType}"),
  };

  private static void ValidateCanvas(VibpCanvas canvas)
  {
    if (canvas.Width is < 1 or > VibpProjectLimits.MaximumImageSide ||
        canvas.Height is < 1 or > VibpProjectLimits.MaximumImageSide)
    {
      throw new VibpProjectFormatException(
        "Project canvas frame is outside the supported bounds.");
    }
  }

  private static void ValidateSource(VibpSourceResource source)
  {
    if (!SourceMediaTypes.Contains(source.MediaType))
    {
      throw new VibpProjectFormatException(
        $"Unsupported source media type: {source.MediaType}");
    }
    ValidateDimensions("source", source.Width, source.Height,
      VibpProjectLimits.MaximumSourcePixels);
    if (source.Length < 1 || source.Length > VibpProjectLimits.MaximumSourceBytes)
    {
      throw new VibpProjectFormatException(
        "Project source entry length is outside the byte budget.");
    }
  }

  private static void ValidateAssetsAndRefs(VibpProjectManifest manifest)
  {
    if (manifest.Assets.Count > VibpProjectLimits.MaximumAssets)
    {
      throw new VibpProjectFormatException(
        $"Project declares more than {VibpProjectLimits.MaximumAssets} image assets.");
    }
    HashSet<string> ids = new(StringComparer.Ordinal);
    long pixels = 0;
    long bytes = 0;
    foreach (VibpAssetResource asset in manifest.Assets)
    {
      if (asset.Id is null || !AssetIdPattern.IsMatch(asset.Id) || !ids.Add(asset.Id))
      {
        throw new VibpProjectFormatException(
          "Project asset id is malformed, null or duplicated.");
      }
      ValidateDimensions($"asset {asset.Id}", asset.Width, asset.Height,
        VibpProjectLimits.MaximumAssetPixels);
      if (asset.Length < 1 || asset.Length > VibpProjectLimits.MaximumAssetAggregateBytes)
      {
        throw new VibpProjectFormatException(
          $"Project asset {asset.Id} entry length is outside the byte budget.");
      }
      pixels += (long)asset.Width * asset.Height;
      bytes += asset.Length;
      if (pixels > VibpProjectLimits.MaximumAssetAggregatePixels ||
          bytes > VibpProjectLimits.MaximumAssetAggregateBytes)
      {
        throw new VibpProjectFormatException(
          "Project image assets exceed the aggregate pixel or byte budget.");
      }
    }
  }

  private static void ValidateState(
    VibpEditorState state,
    VibpSourceResource source,
    IReadOnlyList<VibpAssetResource> assets)
  {
    if (state.Rotation is not (0 or 90 or 180 or 270))
    {
      throw new VibpProjectFormatException(
        "Project state rotation must be a multiple of 90 degrees.");
    }
    if (state.Resize is { } resize &&
        (resize.Width is < 1 or > VibpProjectLimits.MaximumImageSide ||
         resize.Height is < 1 or > VibpProjectLimits.MaximumImageSide))
    {
      throw new VibpProjectFormatException(
        "Project output resize is outside the supported bounds.");
    }
    if (state.Crop is { } crop)
    {
      ValidatePoint("crop start", crop.Start);
      ValidatePoint("crop end", crop.End);
    }
    if (state.Marks.Count > VibpProjectLimits.MaximumMarks)
    {
      throw new VibpProjectFormatException(
        $"Project declares more than {VibpProjectLimits.MaximumMarks} marks.");
    }

    HashSet<string> assetIds = [.. assets.Select(asset => asset.Id)];
    HashSet<string> referencedAssets = new(StringComparer.Ordinal);
    HashSet<string> markIds = new(StringComparer.Ordinal);
    int totalPoints = 0;
    foreach (VibpMark mark in state.Marks)
    {
      ValidateMark(mark, assetIds, referencedAssets, markIds, ref totalPoints);
    }
    foreach (string assetId in assetIds)
    {
      if (!referencedAssets.Contains(assetId))
      {
        throw new VibpProjectFormatException(
          $"Project asset {assetId} is not referenced by any image mark.");
      }
    }

    HashSet<int> patchIds = [];
    long patchPixels = 0;
    long patchBytes = 0;
    foreach (VibpPatch patch in state.Patches)
    {
      if (patch.Id < 1 || !patchIds.Add(patch.Id))
      {
        throw new VibpProjectFormatException(
          "Project patch id is malformed or duplicated.");
      }
      if (patch.X < 0 || patch.Y < 0 || patch.Width < 1 || patch.Height < 1 ||
          patch.X + (long)patch.Width > source.Width ||
          patch.Y + (long)patch.Height > source.Height)
      {
        throw new VibpProjectFormatException(
          $"Project patch {patch.Id} rectangle is outside the source image bounds.");
      }
      long pixels = (long)patch.Width * patch.Height;
      if (pixels > VibpProjectLimits.MaximumPatchPixels)
      {
        throw new VibpProjectFormatException(
          $"Project patch {patch.Id} exceeds the per-patch pixel budget.");
      }
      patchPixels += pixels;
      if (patchPixels > VibpProjectLimits.MaximumPatchAggregatePixels)
      {
        throw new VibpProjectFormatException(
          "Project patches exceed the aggregate pixel budget.");
      }
      if (patch.Length < 1 || patch.Length > VibpProjectLimits.MaximumPatchAggregateBytes)
      {
        throw new VibpProjectFormatException(
          $"Project patch {patch.Id} entry length is outside the byte budget.");
      }
      patchBytes += patch.Length;
      if (patchBytes > VibpProjectLimits.MaximumPatchAggregateBytes)
      {
        throw new VibpProjectFormatException(
          "Project patches exceed the aggregate encoded byte budget.");
      }
    }
  }

  private static void ValidateMark(
    VibpMark mark,
    HashSet<string> assetIds,
    HashSet<string> referencedAssets,
    HashSet<string> markIds,
    ref int totalPoints)
  {
    if (mark.Id is null || !MarkIdPattern.IsMatch(mark.Id) || !markIds.Add(mark.Id))
    {
      throw new VibpProjectFormatException(
        "Project mark id is malformed, null or duplicated.");
    }
    if (!MarkTools.Contains(mark.Tool))
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} uses unknown tool {mark.Tool}.");
    }
    ValidatePoint($"mark {mark.Id} start", mark.Start);
    ValidatePoint($"mark {mark.Id} end", mark.End);

    if (TextTools.Contains(mark.Tool))
    {
      if (mark.Text is null)
      {
        throw new VibpProjectFormatException(
          $"Project mark {mark.Id} is missing text content.");
      }
      if (mark.Text.Length > VibpProjectLimits.MaximumMarkTextLength)
      {
        throw new VibpProjectFormatException(
          $"Project mark {mark.Id} text exceeds the length bound.");
      }
    }
    else if (mark.Text is not null)
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} carries text for tool {mark.Tool}.");
    }

    if (mark.Tool == "image")
    {
      if (mark.AssetId is null || !assetIds.Contains(mark.AssetId))
      {
        throw new VibpProjectFormatException(
          $"Project image mark {mark.Id} references an unknown asset.");
      }
      referencedAssets.Add(mark.AssetId);
    }
    else if (mark.AssetId is not null)
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} carries an asset reference for tool {mark.Tool}.");
    }

    if (mark.Tool == "text")
    {
      if (mark.TextWidth is null ||
          mark.TextWidth < 0 || mark.TextWidth > VibpProjectLimits.MaximumTextWidth)
      {
        throw new VibpProjectFormatException(
          $"Project text mark {mark.Id} has an invalid textWidth.");
      }
    }
    else if (mark.TextWidth is not null)
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} carries textWidth for tool {mark.Tool}.");
    }

    if (PointTools.Contains(mark.Tool))
    {
      if (mark.Points is null || mark.Points.Count < 2)
      {
        throw new VibpProjectFormatException(
          $"Project mark {mark.Id} requires at least two trace points.");
      }
      totalPoints += mark.Points.Count;
      if (totalPoints > VibpProjectLimits.MaximumMarkPoints)
      {
        throw new VibpProjectFormatException(
          "Project marks exceed the aggregate trace point budget.");
      }
      foreach (VibpPoint point in mark.Points)
      {
        ValidatePoint($"mark {mark.Id} point", point);
      }
    }
    else if (mark.Points is not null)
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} carries trace points for tool {mark.Tool}.");
    }

    if (mark.Tool == "numbering")
    {
      if (mark.Ordinal is null or < 1)
      {
        throw new VibpProjectFormatException(
          $"Project numbering mark {mark.Id} has an invalid ordinal.");
      }
    }
    else if (mark.Ordinal is not null)
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} carries an ordinal for tool {mark.Tool}.");
    }

    if (mark.GroupId is not null && !GroupIdPattern.IsMatch(mark.GroupId))
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} has a malformed group id.");
    }
    if (mark.Rotation is not null and not (0 or 90 or 180 or 270))
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} rotation must be a multiple of 90 degrees.");
    }
    if (mark.ObjectScale is not null &&
        (mark.ObjectScale <= 0 || mark.ObjectScale > VibpProjectLimits.MaximumScalarMagnitude))
    {
      throw new VibpProjectFormatException(
        $"Project mark {mark.Id} has an out-of-range objectScale.");
    }
    if (mark.Style is not null)
    {
      ValidateStyle(mark.Id, mark.Style);
    }
  }

  private static void ValidateStyle(string markId, VibpMarkStyle style)
  {
    ValidateOptionalColor(markId, style.Color);
    ValidateOptionalPositive(markId, "strokeWidth", style.StrokeWidth);
    ValidateOptionalPositive(markId, "fontSize", style.FontSize);
    if (style.Intensity is not null &&
        (style.Intensity < 1 || style.Intensity > 3 || !double.IsFinite(style.Intensity.Value)))
    {
      throw new VibpProjectFormatException(
        $"Project mark {markId} intensity must be within 1-3.");
    }
    ValidateOptionalPositive(markId, "brushSize", style.BrushSize);
    ValidateOptionalPercent(markId, "strokeOpacity", style.StrokeOpacity);
    ValidateOptionalNonNegative(markId, "cornerRadius", style.CornerRadius);
    ValidateOptionalMember(markId, "lineStyle", style.LineStyle, LineStyles);
    ValidateOptionalMember(markId, "arrowStyle", style.ArrowStyle, ArrowStyles);
    ValidateOptionalMember(markId, "textAlign", style.TextAlign, TextAligns);
    ValidateOptionalMember(markId, "numberFormat", style.NumberFormat, NumberFormats);
    ValidateOptionalMember(markId, "numberShape", style.NumberShape, NumberShapes);
    ValidateOptionalMember(markId, "calloutStyle", style.CalloutStyle, CalloutStyles);
    if (style.FontFamily is not null &&
        (style.FontFamily.Length < 1 || style.FontFamily.Length > VibpProjectLimits.MaximumFontFamilyLength))
    {
      throw new VibpProjectFormatException(
        $"Project mark {markId} fontFamily is outside the length bound.");
    }
    ValidateFill(markId, style.Fill);
    ValidateFill(markId, style.TextBackground);
    if (style.TextOutline is { } outline)
    {
      if (outline.Color is null || !SixHexColor.IsMatch(outline.Color))
      {
        throw new VibpProjectFormatException(
          $"Project mark {markId} text outline color is malformed.");
      }
      if (outline.Width <= 0 || outline.Width > VibpProjectLimits.MaximumScalarMagnitude)
      {
        throw new VibpProjectFormatException(
          $"Project mark {markId} text outline width is out of range.");
      }
    }
  }

  private static void ValidateFill(string markId, VibpFillStyle? fill)
  {
    if (fill is null)
    {
      return;
    }
    ValidateOptionalColor(markId, fill.Color);
    ValidateOptionalPercent(markId, "fill opacity", fill.Opacity);
  }

  private static void ValidateOptionalColor(string markId, string? color)
  {
    if (color is not null && !SixHexColor.IsMatch(color))
    {
      throw new VibpProjectFormatException(
        $"Project mark {markId} color {color} is malformed.");
    }
  }

  private static void ValidateOptionalPositive(string markId, string field, double? value)
  {
    if (value is not null && (value <= 0 || value > VibpProjectLimits.MaximumScalarMagnitude))
    {
      throw new VibpProjectFormatException(
        $"Project mark {markId} {field} is out of range.");
    }
  }

  private static void ValidateOptionalNonNegative(string markId, string field, double? value)
  {
    if (value is not null && (value < 0 || value > VibpProjectLimits.MaximumScalarMagnitude))
    {
      throw new VibpProjectFormatException(
        $"Project mark {markId} {field} is out of range.");
    }
  }

  private static void ValidateOptionalPercent(string markId, string field, double? value)
  {
    if (value is not null && (value < 0 || value > 100))
    {
      throw new VibpProjectFormatException(
        $"Project mark {markId} {field} must be within 0-100.");
    }
  }

  private static void ValidateOptionalMember(
    string markId, string field, string? value, HashSet<string> allowed)
  {
    if (value is not null && !allowed.Contains(value))
    {
      throw new VibpProjectFormatException(
        $"Project mark {markId} {field} value {value} is not supported.");
    }
  }

  private static void ValidatePoint(string field, VibpPoint? point)
  {
    if (point is null)
    {
      throw new VibpProjectFormatException(
        $"Project {field} is missing.");
    }
    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
        Math.Abs(point.X) > VibpProjectLimits.MaximumCoordinateMagnitude ||
        Math.Abs(point.Y) > VibpProjectLimits.MaximumCoordinateMagnitude)
    {
      throw new VibpProjectFormatException(
        $"Project {field} coordinate is not finite or out of range.");
    }
  }

  private static void ValidateDimensions(string field, int width, int height, long maximumPixels)
  {
    if (width < 1 || height < 1 ||
        width > VibpProjectLimits.MaximumImageSide || height > VibpProjectLimits.MaximumImageSide ||
        (long)width * height > maximumPixels)
    {
      throw new VibpProjectFormatException(
        $"Project {field} dimensions exceed the supported image bounds.");
    }
  }

  private static void ValidateEntryBudget(VibpProjectManifest manifest)
  {
    // entry 总数（含 manifest.json 与 source）是补丁个数的显式边界；
    // 像素预算不承担数量预算职责。
    long entries = 2 + (long)manifest.Assets.Count + manifest.State.Patches.Count;
    if (entries > VibpProjectLimits.MaximumEntries)
    {
      throw new VibpProjectFormatException(
        $"Project declares more than {VibpProjectLimits.MaximumEntries} entries.");
    }
  }
}
