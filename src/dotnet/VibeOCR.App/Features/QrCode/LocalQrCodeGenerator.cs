using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Features.QrCode;

internal static class LocalQrCodeGenerator
{
  private const int ImageSize = 300;
  private const int QuietZone = 4;

  public static Task<QrCodeGeneratedImage> GenerateAsync(
    string data, string format, CancellationToken cancellationToken) =>
    Task.Run(async () =>
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (format != "qrcode")
        throw new ArgumentException("仅支持二维码格式。", nameof(format));
      if (string.IsNullOrWhiteSpace(data))
        throw new ArgumentException("请输入要编码的内容。", nameof(data));
      try { _ = new UTF8Encoding(false, true).GetByteCount(data); }
      catch (EncoderFallbackException error)
      {
        throw new ArgumentException("文本包含无效的 Unicode 字符。", nameof(data), error);
      }
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
      int scale = ImageSize / (matrix.Width + QuietZone * 2);
      int left = (ImageSize - (matrix.Width + QuietZone * 2) * scale) / 2 + QuietZone * scale;
      int top = (ImageSize - (matrix.Height + QuietZone * 2) * scale) / 2 + QuietZone * scale;
      byte[] pixels = new byte[ImageSize * ImageSize * 4];
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
            int offset = ((top + y * scale + dy) * ImageSize + left + x * scale + dx) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
          }
        }
      }
      using var stream = new InMemoryRandomAccessStream();
      BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
      encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
        ImageSize, ImageSize, 96, 96, pixels);
      await encoder.FlushAsync();
      cancellationToken.ThrowIfCancellationRequested();
      using var reader = new DataReader(stream.GetInputStreamAt(0));
      uint length = checked((uint)stream.Size);
      await reader.LoadAsync(length);
      byte[] png = new byte[length];
      reader.ReadBytes(png);
      cancellationToken.ThrowIfCancellationRequested();
      return new QrCodeGeneratedImage(Convert.ToBase64String(png), "image/png");
    }, cancellationToken);
}
