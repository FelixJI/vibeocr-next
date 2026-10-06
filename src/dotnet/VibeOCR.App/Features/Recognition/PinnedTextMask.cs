using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using VibeOCR.App.Workbench;

namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// 贴图取字的临时遮罩输入：按 [0,1000] 归一化排除矩形把不透明白色像素
/// 烘焙进解码后的 BGRA8 像素（左上 floor、右下 ceil 外扩取整，与前端
/// exclusionNaturalRects 同语义，边缘不残留原文像素）。产物仅供 OCR
/// 消费；贴图显示、复制与保存始终使用未烘焙原图。
/// </summary>
internal static class PinnedTextMask
{
  private const double NormalizedRange = 1000;

  /// <summary>
  /// 扫描线压缩判定排除矩形并集是否完整覆盖 [0,1000]² 图像（无采样误差）。
  /// 覆盖即拒绝取字，贴图本身不受影响。
  /// </summary>
  internal static bool CoversEntireImage(IReadOnlyList<WorkbenchExclusionBox> boxes)
  {
    if (boxes.Count == 0) return false;
    double[] edges = [0, NormalizedRange, .. boxes
      .Where(box => box.Width > 0 && box.Height > 0)
      .SelectMany(box => new[] { box.X, box.X + box.Width })];
    double[] sorted = [.. edges.Distinct().Order()];
    double coveredArea = 0;
    for (int index = 0; index < sorted.Length - 1; index++)
    {
      double slabLeft = Math.Max(0, sorted[index]);
      double slabRight = Math.Min(NormalizedRange, sorted[index + 1]);
      if (slabRight <= slabLeft) continue;
      (double From, double To)[] spans = [.. boxes
        .Where(box =>
          box.Width > 0 && box.Height > 0 &&
          box.X <= slabLeft && box.X + box.Width >= slabRight)
        .Select(box => (
          From: Math.Max(0, box.Y),
          To: Math.Min(NormalizedRange, box.Y + box.Height)))
        .Where(span => span.To > span.From)
        .Order()];
      double coveredHeight = 0;
      double reach = double.NegativeInfinity;
      foreach ((double from, double to) in spans)
      {
        if (from > reach)
        {
          coveredHeight += to - from;
          reach = to;
        }
        else if (to > reach)
        {
          coveredHeight += to - reach;
          reach = to;
        }
      }
      coveredArea += (slabRight - slabLeft) * coveredHeight;
    }
    return coveredArea >= NormalizedRange * NormalizedRange - 1e-6;
  }

  /// <summary>把不透明白色写入 BGRA8 像素；矩形越界裁剪到图像内，空矩形跳过。</summary>
  internal static void PaintWhite(
    byte[] pixels, uint width, uint height,
    IReadOnlyList<WorkbenchExclusionBox> boxes)
  {
    foreach (WorkbenchExclusionBox box in boxes)
    {
      if (box.Width <= 0 || box.Height <= 0 || width == 0 || height == 0) continue;
      int left = Math.Max(0, (int)Math.Floor(box.X / NormalizedRange * width));
      int top = Math.Max(0, (int)Math.Floor(box.Y / NormalizedRange * height));
      int right = Math.Min((int)width,
        (int)Math.Ceiling((box.X + box.Width) / NormalizedRange * width));
      int bottom = Math.Min((int)height,
        (int)Math.Ceiling((box.Y + box.Height) / NormalizedRange * height));
      for (int y = top; y < bottom; y++)
      {
        int rowBase = (int)(y * width * 4);
        for (int x = left; x < right; x++)
        {
          int offset = rowBase + x * 4;
          pixels[offset] = 0xff;
          pixels[offset + 1] = 0xff;
          pixels[offset + 2] = 0xff;
          pixels[offset + 3] = 0xff;
        }
      }
    }
  }

  /// <summary>
  /// 生成遮罩 PNG 字节（内存中）：OCR 输入用；无临时文件。
  /// 解码遵循 EXIF 方向，与贴图显示一致；输出固定为 PNG。
  /// </summary>
  internal static async Task<byte[]> CreateMaskedPngBytesAsync(
    string sourcePath,
    IReadOnlyList<WorkbenchExclusionBox> boxes,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(sourcePath);
    ArgumentNullException.ThrowIfNull(boxes);
    if (boxes.Count == 0)
      throw new ArgumentException("至少需要一个排除矩形。", nameof(boxes));
    cancellationToken.ThrowIfCancellationRequested();
    StorageFile file = await StorageFile.GetFileFromPathAsync(sourcePath);
    using IRandomAccessStream input = await file.OpenReadAsync();
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input)
      .AsTask(cancellationToken);
    uint width = decoder.OrientedPixelWidth;
    uint height = decoder.OrientedPixelHeight;
    byte[] pixels = (await decoder.GetPixelDataAsync(
      BitmapPixelFormat.Bgra8,
      BitmapAlphaMode.Straight,
      new BitmapTransform(),
      ExifOrientationMode.RespectExifOrientation,
      ColorManagementMode.DoNotColorManage).AsTask(cancellationToken))
      .DetachPixelData();
    if (pixels.Length < (long)width * height * 4)
      throw new InvalidOperationException("图像像素不完整，无法生成遮罩输入。");
    PaintWhite(pixels, width, height, boxes);
    using var encoded = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
      BitmapEncoder.PngEncoderId, encoded).AsTask(cancellationToken);
    encoder.SetPixelData(
      BitmapPixelFormat.Bgra8,
      BitmapAlphaMode.Straight,
      width,
      height,
      96,
      96,
      pixels);
    await encoder.FlushAsync().AsTask(cancellationToken);
    using IInputStream read = encoded.GetInputStreamAt(0);
    using Stream source = read.AsStreamForRead();
    using var output = new MemoryStream();
    await source.CopyToAsync(output, 81920, cancellationToken);
    return output.ToArray();
  }

  /// <summary>生成临时遮罩 PNG 副本；调用方在识别结束后删除临时文件。</summary>
  internal static async Task<string> CreateMaskedPngAsync(
    string sourcePath,
    IReadOnlyList<WorkbenchExclusionBox> boxes,
    CancellationToken cancellationToken)
  {
    byte[] bytes = await CreateMaskedPngBytesAsync(sourcePath, boxes, cancellationToken);
    string target = Path.Combine(
      Path.GetTempPath(), $"vibeocr-pin-mask-{Guid.NewGuid():N}.png");
    try
    {
      await File.WriteAllBytesAsync(target, bytes, cancellationToken);
      return target;
    }
    catch
    {
      // 创建函数自清理：取消/失败不遗留临时文件（调用方拿不到路径）。
      try { File.Delete(target); }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
      throw;
    }
  }
}
