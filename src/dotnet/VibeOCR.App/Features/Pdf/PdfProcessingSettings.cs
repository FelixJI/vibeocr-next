using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Configuration;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App.Features.Pdf;

public sealed record PdfProcessingSettings(
    int RenderDpi = 300, int MaxPixels = 16_000_000, double FontSizeRatio = 0.8,
    int FontSizeRetryCount = 5, double FontSizeShrinkFactor = 0.75,
    double MinFontSize = 4, bool CompressOnSave = true, bool CleanOnSave = false)
{
  private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
  public void Validate()
  {
    if (RenderDpi is < 72 or > 600 || MaxPixels is < 100_000 or > 16_000_000 ||
        !double.IsFinite(FontSizeRatio) || FontSizeRatio is < 0.1 or > 2 ||
        FontSizeRetryCount is < 0 or > 10 || !double.IsFinite(FontSizeShrinkFactor) ||
        FontSizeShrinkFactor is <= 0 or >= 1 || !double.IsFinite(MinFontSize) || MinFontSize is < 1 or > 72)
      throw new ArgumentException("PDF 参数超出有效范围。");
  }
  public IReadOnlyDictionary<string, JsonElement> ToWire()
  {
    Validate();
    return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(this, Options))!;
  }
  internal static PdfProcessingSettings Load(PortableLayout layout)
  {
    var value = AppSettingsStore.ReadForUpdate(layout)["pdf_processing"]?.Deserialize<PdfProcessingSettings>(Options) ?? new();
    value.Validate();
    return value;
  }
  internal void Save(PortableLayout layout)
  {
    Validate();
    JsonObject root = AppSettingsStore.ReadForUpdate(layout);
    root["pdf_processing"] = JsonSerializer.SerializeToNode(this, Options);
    AppSettingsStore.Write(layout, root);
  }
  public int DpiFor(double width, double height)
  {
    Validate();
    if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
      throw new ArgumentException("页面缺少可靠尺寸。");
    int dpi = Math.Min(RenderDpi, (int)Math.Floor(72 * Math.Sqrt(MaxPixels / (width * height))));
    while (dpi >= 72 && Math.Ceiling(width * dpi / 72) * Math.Ceiling(height * dpi / 72) > MaxPixels) dpi--;
    if (dpi < 72) throw new ArgumentException("页面在最低 72 DPI 仍超过像素预算。");
    return dpi;
  }
}
