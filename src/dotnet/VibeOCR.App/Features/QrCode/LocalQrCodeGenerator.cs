using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Features.QrCode;

/// <summary>Caption placement below the generated code, outside the code and its quiet zone.</summary>
public enum QrCodeCaptionMode
{
  /// <summary>No caption (default); the image contains only the code and its quiet zone.</summary>
  Off = 0,
  /// <summary>Shows the encoded payload; never changes what is encoded.</summary>
  Payload = 1,
  /// <summary>Shows an independent description text; never changes the payload.</summary>
  Custom = 2,
}

/// <summary>Caption options for local QR/barcode generation.</summary>
public sealed record QrCodeCaption(QrCodeCaptionMode Mode, string? CustomText = null)
{
  public static QrCodeCaption None { get; } = new(QrCodeCaptionMode.Off);
}

/// <summary>
/// Local (offline) QR/barcode generation: QR Code, Code 128 and EAN-13 rendered
/// through ZXing.Net onto a BGRA canvas and encoded as a real PNG.
/// Verified against the pinned ZXing.Net 0.16.11 behaviour:
/// Code 128 accepts ASCII 0–127 only; EAN-13 accepts 12 digits (checksum is
/// appended by the encoder) or 13 digits with a valid checksum and never
/// silently rewrites a wrong check digit; QR uses UTF-8 with a four-module
/// quiet zone and integer module scaling.
/// </summary>
internal static class LocalQrCodeGenerator
{
  private const int QrImageSize = 300;
  private const int QrQuietZoneModules = 4;
  private const int OneDBarPixels = 2;
  private const int OneDRowHeight = 60;
  private const int OneDQuietVerticalPixels = 10;
  private const int MinimumImageWidth = 120;
  private const int MaximumImageWidth = 1600;
  /// <summary>Minimum module size in pixels; 1px modules sit at the sampling limit
  /// of real decoders, so the canvas expands to at least this scale.</summary>
  private const int MinimumQrModulePixels = 2;
  private const int CaptionGapPixels = 8;

  public static Task<QrCodeGeneratedImage> GenerateAsync(
    string data, string format, CancellationToken cancellationToken) =>
    GenerateAsync(data, format, QrCodeCaption.None, cancellationToken);

  /// <param name="caption">Optional caption placed below the code and its quiet zone.</param>
  /// <param name="targetWidth">
  /// Optional bounded target width (clamped) used for QR canvas sizing and 1D bar
  /// width scaling; module proportions stay integral.
  /// </param>
  public static Task<QrCodeGeneratedImage> GenerateAsync(
    string data, string format, QrCodeCaption caption, CancellationToken cancellationToken, int? targetWidth = null) =>
    Task.Run(async () =>
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (string.IsNullOrWhiteSpace(format))
        throw new ArgumentException("请选择编码格式。", nameof(format));
      if (string.IsNullOrWhiteSpace(data))
        throw new ArgumentException("请输入要编码的内容。", nameof(data));
      try { _ = new UTF8Encoding(false, true).GetByteCount(data); }
      catch (EncoderFallbackException error)
      {
        throw new ArgumentException("文本包含无效的 Unicode 字符。", nameof(data), error);
      }
      data = NormalizePayload(data, format);
      (byte[] Pixels, int Width, int Height) image = format.Trim().ToLowerInvariant() switch
      {
        "qrcode" => RenderQrCode(data, targetWidth, cancellationToken),
        "code128" => RenderOneDimensional(data, BarcodeFormat.CODE_128, ValidateCode128, targetWidth),
        "ean13" => RenderOneDimensional(data, BarcodeFormat.EAN_13, ValidateEan13, targetWidth),
        _ => throw new ArgumentException($"不支持的编码格式：{format}。当前支持 qrcode、code128、ean13。", nameof(format)),
      };
      string? captionText = ResolveCaptionText(caption, data);
      if (captionText is not null)
      {
        (image.Pixels, image.Height) = AppendCaptionStrip(image.Pixels, image.Width, image.Height, captionText);
        cancellationToken.ThrowIfCancellationRequested();
      }
      byte[] png = await EncodePngAsync(image.Pixels, image.Width, image.Height, cancellationToken);
      return new QrCodeGeneratedImage(Convert.ToBase64String(png), "image/png");
    }, cancellationToken);

  /// <summary>
  /// Append a wrapped caption strip below an already generated PNG without
  /// touching the code pixels or its quiet zone. Used by the view model so the
  /// caption never changes the encoded payload.
  /// </summary>
  public static Task<QrCodeGeneratedImage> AppendCaptionAsync(
    QrCodeGeneratedImage image, string captionText, CancellationToken cancellationToken) =>
    Task.Run(async () =>
    {
      ArgumentNullException.ThrowIfNull(image);
      cancellationToken.ThrowIfCancellationRequested();
      if (string.IsNullOrWhiteSpace(captionText)) return image;
      (byte[] Pixels, uint Width, uint Height) source = await DecodePngAsync(image.Base64Png, cancellationToken);
      (byte[] pixels, int height) = AppendCaptionStrip(
        source.Pixels, checked((int)source.Width), checked((int)source.Height), captionText);
      if (ReferenceEquals(pixels, source.Pixels)) return image;
      byte[] png = await EncodePngAsync(pixels, checked((int)source.Width), height, cancellationToken);
      return new QrCodeGeneratedImage(Convert.ToBase64String(png), image.MediaType);
    }, cancellationToken);

  private static (byte[] Pixels, int Width, int Height) RenderQrCode(
    string data, int? targetWidth, CancellationToken cancellationToken)
  {
    var hints = new Dictionary<EncodeHintType, object>
    {
      [EncodeHintType.CHARACTER_SET] = "UTF-8",
      [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
      [EncodeHintType.MARGIN] = 0,
    };
    BitMatrix matrix;
    try { matrix = new QRCodeWriter().encode(data, BarcodeFormat.QR_CODE, 0, 0, hints); }
    catch (WriterException error)
    {
      throw new ArgumentException("内容过长，无法生成二维码。", nameof(data), error);
    }
    // The canvas never shrinks below the bounded request, but must still fit the
    // whole matrix plus its four-module quiet zone at a decodable scale instead of
    // writing past the pixel buffer (regression: large payload + 120px floor).
    int size = Math.Max(
      Math.Clamp(targetWidth ?? QrImageSize, MinimumImageWidth, MaximumImageWidth),
      (matrix.Width + QrQuietZoneModules * 2) * MinimumQrModulePixels);
    int scale = Math.Max(1, size / (matrix.Width + QrQuietZoneModules * 2));
    int left = (size - (matrix.Width + QrQuietZoneModules * 2) * scale) / 2 + QrQuietZoneModules * scale;
    int top = (size - (matrix.Height + QrQuietZoneModules * 2) * scale) / 2 + QrQuietZoneModules * scale;
    byte[] pixels = new byte[size * size * 4];
    Array.Fill(pixels, (byte)255);
    for (int y = 0; y < matrix.Height; y++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      for (int x = 0; x < matrix.Width; x++)
      {
        if (!matrix[x, y]) continue;
        for (int dy = 0; dy < scale; dy++)
        for (int dx = 0; dx < scale; dx++)
        {
          int offset = ((top + y * scale + dy) * size + left + x * scale + dx) * 4;
          pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
        }
      }
    }
    return (pixels, size, size);
  }

  private static (byte[] Pixels, int Width, int Height) RenderOneDimensional(
    string data, BarcodeFormat format, Action<string> validate, int? targetWidth)
  {
    validate(data);
    var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
    BitMatrix matrix;
    try { matrix = new MultiFormatWriter().encode(data, format, 0, 0, hints); }
    catch (WriterException error)
    {
      throw new ArgumentException("内容无法编码为条形码。", nameof(data), error);
    }
    int barPixels = targetWidth is int requested
      ? Math.Clamp(requested / Math.Max(1, matrix.Width), 1, 8)
      : OneDBarPixels;
    // EAN-13 requires a quiet zone of at least 11 modules; Code 128 of 10.
    int quietSide = Math.Max(11 * barPixels, 20);
    int width = matrix.Width * barPixels + quietSide * 2;
    int height = OneDRowHeight + OneDQuietVerticalPixels * 2;
    byte[] pixels = new byte[width * height * 4];
    Array.Fill(pixels, (byte)255);
    for (int x = 0; x < matrix.Width; x++)
    {
      if (!matrix[x, 0]) continue;
      for (int dx = 0; dx < barPixels; dx++)
      {
        for (int y = OneDQuietVerticalPixels; y < OneDQuietVerticalPixels + OneDRowHeight; y++)
        {
          int offset = (y * width + quietSide + x * barPixels + dx) * 4;
          pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
        }
      }
    }
    return (pixels, width, height);
  }

  private static void ValidateCode128(string data)
  {
    foreach (char character in data)
    {
      if (character > 0x7F)
        throw new ArgumentException("Code 128 仅支持 ASCII 字符（0–127），不支持中文或其他非 ASCII 字符。", nameof(data));
    }
  }

  private static void ValidateEan13(string data)
  {
    // EAN-13 is defined over ASCII digits only; char.IsDigit would also accept
    // other Unicode digit characters (e.g. Arabic-Indic) which are not valid EAN.
    if (data.Length is not (12 or 13) || !data.All(c => c is >= '0' and <= '9'))
      throw new ArgumentException("EAN-13 内容必须是 12 位数字（自动计算校验位）或 13 位数字。", nameof(data));
    if (data.Length == 13)
    {
      int expected = ComputeEan13CheckDigit(data[..12]);
      int actual = data[12] - '0';
      if (actual != expected)
        throw new ArgumentException(
          $"EAN-13 校验位不正确：第 13 位应为 {expected}，实际为 {actual}。请修正校验位，或仅输入前 12 位由系统计算。",
          nameof(data));
    }
  }

  internal static string NormalizePayload(string data, string format)
  {
    if (!string.Equals(format.Trim(), "ean13", StringComparison.OrdinalIgnoreCase)) return data;
    ValidateEan13(data);
    return data.Length == 12 ? data + ComputeEan13CheckDigit(data) : data;
  }

  private static int ComputeEan13CheckDigit(string twelveDigits)
  {
    int sum = 0;
    for (int i = 0; i < twelveDigits.Length; i++)
    {
      int digit = twelveDigits[i] - '0';
      sum += (i % 2 == 0 ? 1 : 3) * digit;
    }
    return (10 - sum % 10) % 10;
  }

  private static string? ResolveCaptionText(QrCodeCaption caption, string data) => caption.Mode switch
  {
    QrCodeCaptionMode.Off => null,
    QrCodeCaptionMode.Payload => data,
    QrCodeCaptionMode.Custom when string.IsNullOrWhiteSpace(caption.CustomText) => null,
    QrCodeCaptionMode.Custom => caption.CustomText,
    _ => null,
  };

  private static (byte[] Pixels, int Height) AppendCaptionStrip(
    byte[] pixels, int width, int height, string text)
  {
    int innerWidth = width - 2 * QrCodeCaptionRenderer.SidePadding;
    if (innerWidth < 24) return (pixels, height);
    int textHeight = QrCodeCaptionRenderer.MeasureHeight(innerWidth, text);
    byte[] strip = QrCodeCaptionRenderer.Render(width, textHeight, text);
    int newHeight = height + CaptionGapPixels + textHeight;
    byte[] composed = new byte[width * newHeight * 4];
    Array.Fill(composed, (byte)255);
    Array.Copy(pixels, 0, composed, 0, Math.Min(pixels.Length, composed.Length));
    Array.Copy(strip, 0, composed, (width * (height + CaptionGapPixels) * 4), strip.Length);
    return (composed, newHeight);
  }

  private static async Task<byte[]> EncodePngAsync(
    byte[] pixels, int width, int height, CancellationToken cancellationToken)
  {
    using var stream = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
      checked((uint)width), checked((uint)height), 96, 96, pixels);
    await encoder.FlushAsync();
    cancellationToken.ThrowIfCancellationRequested();
    return await ReadStreamBytesAsync(stream, cancellationToken);
  }

  private static async Task<(byte[] Pixels, uint Width, uint Height)> DecodePngAsync(
    string base64Png, CancellationToken cancellationToken)
  {
    byte[] png = Convert.FromBase64String(base64Png);
    using var stream = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(stream))
    {
      writer.WriteBytes(png);
      await writer.StoreAsync();
      writer.DetachStream();
    }
    stream.Seek(0);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
    byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
      BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
      ColorManagementMode.DoNotColorManage)).DetachPixelData();
    cancellationToken.ThrowIfCancellationRequested();
    return (pixels, decoder.PixelWidth, decoder.PixelHeight);
  }

  private static async Task<byte[]> ReadStreamBytesAsync(
    InMemoryRandomAccessStream stream, CancellationToken cancellationToken)
  {
    using var reader = new DataReader(stream.GetInputStreamAt(0));
    uint length = checked((uint)stream.Size);
    await reader.LoadAsync(length);
    byte[] png = new byte[length];
    reader.ReadBytes(png);
    cancellationToken.ThrowIfCancellationRequested();
    return png;
  }
}
