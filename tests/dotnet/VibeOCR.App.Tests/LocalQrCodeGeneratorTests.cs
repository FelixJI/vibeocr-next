using VibeOCR.App.Features.QrCode;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;
using ZXing;
using ZXing.Common;

namespace VibeOCR.App.Tests;

public sealed class LocalQrCodeGeneratorTests
{
  private static async Task<(byte[] Pixels, uint Width, uint Height)> DecodePngAsync(string base64Png)
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
    return (pixels, decoder.PixelWidth, decoder.PixelHeight);
  }

  /// <summary>Decodes the real BGRA pixels of the generated PNG with the pinned ZXing reader.</summary>
  private static async Task<string?> DecodeWithReaderAsync(string base64Png)
  {
    (byte[] bgra, uint width, uint height) = await DecodePngAsync(base64Png);
    byte[] rgb = new byte[width * height * 3];
    for (int i = 0; i < width * height; i++)
    {
      rgb[i * 3] = bgra[i * 4 + 2];
      rgb[i * 3 + 1] = bgra[i * 4 + 1];
      rgb[i * 3 + 2] = bgra[i * 4];
    }
    var reader = new BarcodeReaderGeneric { Options = new DecodingOptions { TryHarder = true } };
    return reader.Decode(new RGBLuminanceSource(rgb, (int)width, (int)height))?.Text;
  }
  [Theory]
  [InlineData("hello")]
  [InlineData("中文🙂")]
  public async Task GeneratesCenteredPngWithQuietZoneAsync(string text)
  {
    var image = await LocalQrCodeGenerator.GenerateAsync(text, "qrcode", TestContext.Current.CancellationToken);
    Assert.Equal("image/png", image.MediaType);
    byte[] png = Convert.FromBase64String(image.Base64Png);
    Assert.Equal([137, 80, 78, 71], png[..4]);
    using var stream = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(stream))
    {
      writer.WriteBytes(png);
      await writer.StoreAsync();
      writer.DetachStream();
    }
    stream.Seek(0);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
    Assert.Equal((uint)300, decoder.PixelWidth);
    Assert.Equal((uint)300, decoder.PixelHeight);
    byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
      BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
      ColorManagementMode.DoNotColorManage)).DetachPixelData();
    Assert.All(Enumerable.Range(0, 300), x =>
    {
      Assert.Equal((byte)255, pixels[x * 4]);
      Assert.Equal((byte)255, pixels[((299 * 300) + x) * 4]);
    });
    Assert.Contains(Enumerable.Range(0, 300 * 300), i => pixels[i * 4] == 0);
  }

  [Fact]
  public async Task RejectsInvalidFormatTextAndOversizedContentAsync()
  {
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("hello", "datamatrix", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync(" ", "qrcode", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync(new string('x', 10000), "qrcode", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("\ud800", "qrcode", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("hello", "", CancellationToken.None));
  }

  [Theory]
  [InlineData('1', 3000)]
  [InlineData('A', 2500)]
  public async Task PreservesHighCapacityNumericAndAlphanumericModesAsync(char character, int length)
  {
    var image = await LocalQrCodeGenerator.GenerateAsync(
      new string(character, length), "qrcode", TestContext.Current.CancellationToken);
    Assert.Equal("image/png", image.MediaType);
    Assert.NotEmpty(Convert.FromBase64String(image.Base64Png));
  }

  [Fact]
  public async Task RoundTripsQrUnicodeThroughRealDecodedPixelsAsync()
  {
    string payload = "中文🙂 hello QR";
    var image = await LocalQrCodeGenerator.GenerateAsync(payload, "qrcode", TestContext.Current.CancellationToken);
    Assert.Equal(payload, await DecodeWithReaderAsync(image.Base64Png));
  }

  [Theory]
  [InlineData("ABC-1234")]
  [InlineData("\t line\nbreak\r")]
  public async Task RoundTripsCode128ThroughRealDecodedPixelsAsync(string payload)
  {
    var image = await LocalQrCodeGenerator.GenerateAsync(payload, "code128", TestContext.Current.CancellationToken);
    (byte[] _, uint width, uint height) = await DecodePngAsync(image.Base64Png);
    // One-dimensional codes keep a horizontal aspect with side quiet zones, not a square.
    Assert.True(width > height, $"expected wide barcode, got {width}x{height}");
    Assert.Equal(payload, await DecodeWithReaderAsync(image.Base64Png));
  }

  [Fact]
  public async Task RoundTripsEan13From12DigitsWithComputedChecksumAsync()
  {
    var image = await LocalQrCodeGenerator.GenerateAsync("400638133393", "ean13", TestContext.Current.CancellationToken);
    // ZXing appends the computed check digit when encoding 12 digits (verified against 0.16.11).
    Assert.Equal("4006381333931", await DecodeWithReaderAsync(image.Base64Png));
  }

  [Fact]
  public async Task RoundTripsEan13WithValid13DigitsAsync()
  {
    var image = await LocalQrCodeGenerator.GenerateAsync("4006381333931", "ean13", TestContext.Current.CancellationToken);
    (byte[] _, uint width, uint height) = await DecodePngAsync(image.Base64Png);
    Assert.True(width > height, $"expected wide barcode, got {width}x{height}");
    Assert.Equal("4006381333931", await DecodeWithReaderAsync(image.Base64Png));
  }

  [Fact]
  public async Task Ean13RejectsBadInputWithoutSilentFixAsync()
  {
    ArgumentException wrongChecksum = await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("4006381333932", "ean13", CancellationToken.None));
    Assert.Contains("校验位不正确", wrongChecksum.Message);
    Assert.Contains("1", wrongChecksum.Message);
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("40063813339", "ean13", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("40063813339311", "ean13", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("40063a133393", "ean13", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("40063813339X1", "ean13", CancellationToken.None));
  }

  [Fact]
  public async Task Ean13RejectsUnicodeDigitsAsNonAsciiInputAsync()
  {
    // EAN-13 is defined over ASCII 0-9 only; other Unicode digit glyphs are invalid.
    foreach (string payload in new[] { "٤٠٠٦٣٨١٣٣٣٩٣", "４００６３８１３３３９３" })
    {
      ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
        LocalQrCodeGenerator.GenerateAsync(payload, "ean13", CancellationToken.None));
      Assert.Contains("EAN-13 内容必须是 12 位数字", error.Message);
    }
  }

  [Fact]
  public async Task Code128RejectsNonAsciiWithClearMessageAsync()
  {
    foreach (string payload in new[] { "café", "中文", "emoji🙂", "euro€" })
    {
      ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
        LocalQrCodeGenerator.GenerateAsync(payload, "code128", CancellationToken.None));
      Assert.Contains("ASCII", error.Message);
    }
  }

  [Theory]
  [InlineData("code128", "ABC-1234")]
  [InlineData("ean13", "400638133393")]
  // NOTE: a 21-module (V1) QR with payload "中文🙂" is NOT detectable by the pinned
  // ZXing.Net 0.16.11 *detector* (content/mask-specific limitation; fails at every
  // scale even without a caption), although the pixels decode fine with
  // PureBarcode=true and were independently verified with the runtime's real
  // decoder (pyzbar/zbar). The caption test therefore uses a V2 payload the
  // pinned detector reliably finds.
  [InlineData("qrcode", "中文🙂 hello QR")]
  public async Task CaptionRendersOutsideCodeAndQuietZoneWithoutTouchingPayloadAsync(string format, string payload)
  {
    string caption = "产品编号：很长的一段中文说明文字，用于验证自动换行排版能力。\n第二行独立说明，不影响编码内容。";
    var plain = await LocalQrCodeGenerator.GenerateAsync(payload, format, TestContext.Current.CancellationToken);
    var captioned = await LocalQrCodeGenerator.GenerateAsync(
      payload, format, new QrCodeCaption(QrCodeCaptionMode.Custom, caption), TestContext.Current.CancellationToken);
    (byte[] plainPixels, uint width, uint plainHeight) = await DecodePngAsync(plain.Base64Png);
    (byte[] captionPixels, uint captionWidth, uint captionHeight) = await DecodePngAsync(captioned.Base64Png);
    Assert.Equal(width, captionWidth);
    Assert.True(captionHeight > plainHeight + 8, $"caption strip missing: {captionHeight} vs {plainHeight}");
    // The code and its quiet zone stay byte-identical; the caption lives below them.
    Assert.Equal(
      plainPixels.AsSpan(0, (int)(width * plainHeight * 4)).ToArray(),
      captionPixels.AsSpan(0, (int)(width * plainHeight * 4)).ToArray());
    int stripStart = (int)(width * (plainHeight + 8) * 4);
    Assert.Contains(Enumerable.Range(0, (int)(width * (captionHeight - plainHeight - 8))),
      i => captionPixels[stripStart + i * 4] < 128);
    // The captioned image still decodes to the same payload: text never replaces pixels.
    string expectedDecoded = payload == "400638133393" ? "4006381333931" : payload;
    Assert.Equal(expectedDecoded, await DecodeWithReaderAsync(captioned.Base64Png));
  }

  [Fact]
  public async Task Version1QrPixelsDecodeThroughPureBarcodeDespiteDetectorLimitAsync()
  {
    // Documents the ZXing.Net 0.16.11 detector limitation from the pinned fact
    // check: this V1 payload is not *detected* at any scale, but the very same
    // real pixels round-trip with PureBarcode=true, and the runtime decoder
    // (pyzbar/zbar) reads it (verified against the exact generator layout).
    var image = await LocalQrCodeGenerator.GenerateAsync("中文🙂", "qrcode", TestContext.Current.CancellationToken);
    (byte[] bgra, uint width, uint height) = await DecodePngAsync(image.Base64Png);
    byte[] rgb = new byte[width * height * 3];
    for (int i = 0; i < width * height; i++)
    {
      rgb[i * 3] = bgra[i * 4 + 2];
      rgb[i * 3 + 1] = bgra[i * 4 + 1];
      rgb[i * 3 + 2] = bgra[i * 4];
    }
    var reader = new BarcodeReaderGeneric
    {
      Options = new DecodingOptions { TryHarder = true, PureBarcode = true },
    };
    Assert.Equal("中文🙂", reader.Decode(new RGBLuminanceSource(rgb, (int)width, (int)height))?.Text);
  }

  [Fact]
  public async Task PayloadCaptionShowsEncodedTextAndCustomBlankFallsBackToNoCaptionAsync()
  {
    var payloadCaption = await LocalQrCodeGenerator.GenerateAsync(
      "CAP-42", "code128", new QrCodeCaption(QrCodeCaptionMode.Payload), TestContext.Current.CancellationToken);
    (byte[] pixels, uint width, uint height) = await DecodePngAsync(payloadCaption.Base64Png);
    Assert.True(height > 80);
    Assert.Contains(Enumerable.Range(0, (int)(width * height)),
      i => pixels[i * 4] < 128 && i / (double)width > 80);
    Assert.Equal("CAP-42", await DecodeWithReaderAsync(payloadCaption.Base64Png));

    var blankCustom = await LocalQrCodeGenerator.GenerateAsync(
      "CAP-42", "code128", new QrCodeCaption(QrCodeCaptionMode.Custom, "  "), TestContext.Current.CancellationToken);
    (_, _, uint blankHeight) = await DecodePngAsync(blankCustom.Base64Png);
    Assert.Equal((uint)80, blankHeight);
  }

  [Fact]
  public async Task LargeQrPayloadWithMinimumTargetWidthExpandsCanvasAndStillDecodesAsync()
  {
    // Regression: clamping the canvas to 120px used to write modules past the
    // pixel buffer (negative left/top) for payloads wider than the floor. The
    // canvas must instead expand to matrix + quiet zone at a decodable scale.
    string payload = new string('x', 1200);
    var image = await LocalQrCodeGenerator.GenerateAsync(
      payload, "qrcode", QrCodeCaption.None, TestContext.Current.CancellationToken, 120);
    (byte[] _, uint width, uint height) = await DecodePngAsync(image.Base64Png);
    Assert.True(width > 120, $"canvas should expand past the floor, got {width}");
    Assert.Equal(width, height);
    Assert.Equal(payload, await DecodeWithReaderAsync(image.Base64Png));
  }

  [Fact]
  public async Task AppendCaptionExtendsExistingPngWithoutTouchingCodePixelsAsync()
  {
    var generated = await LocalQrCodeGenerator.GenerateAsync("APP-1", "qrcode", TestContext.Current.CancellationToken);
    var captioned = await LocalQrCodeGenerator.AppendCaptionAsync(
      generated, "追加的底部说明", TestContext.Current.CancellationToken);
    (byte[] plainPixels, uint width, uint plainHeight) = await DecodePngAsync(generated.Base64Png);
    (byte[] captionPixels, uint captionWidth, uint captionHeight) = await DecodePngAsync(captioned.Base64Png);
    Assert.Equal(width, captionWidth);
    Assert.True(captionHeight > plainHeight);
    Assert.Equal(
      plainPixels.AsSpan(0, (int)(width * plainHeight * 4)).ToArray(),
      captionPixels.AsSpan(0, (int)(width * plainHeight * 4)).ToArray());
    Assert.Equal("APP-1", await DecodeWithReaderAsync(captioned.Base64Png));
  }

  [Theory]
  [InlineData("qrcode", 480)]
  [InlineData("code128", 480)]
  [InlineData("qrcode", 5000)]
  public async Task OptionalBoundedWidthProducesDecodableImagesAsync(string format, int requestedWidth)
  {
    string payload = format switch
    {
      "ean13" => "400638133393",
      "code128" => "sized test 42",
      _ => "sized 测试 42",
    };
    var image = await LocalQrCodeGenerator.GenerateAsync(
      payload, format, QrCodeCaption.None, TestContext.Current.CancellationToken, requestedWidth);
    (byte[] _, uint width, uint _) = await DecodePngAsync(image.Base64Png);
    if (format == "qrcode")
    {
      Assert.Equal((uint)Math.Clamp(requestedWidth, 120, 1600), width);
    }
    else
    {
      Assert.True(width <= Math.Clamp(requestedWidth, 120, 1600), $"width {width} exceeds bounded request");
    }
    string expected = payload == "400638133393" ? "4006381333931" : payload;
    Assert.Equal(expected, await DecodeWithReaderAsync(image.Base64Png));
  }

  [Fact]
  public async Task SavesActualPngAndJpegAndPreservesExistingFileOnFailureAsync()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-qr-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      byte[] png = Convert.FromBase64String((await LocalQrCodeGenerator.GenerateAsync(
        "save test", "qrcode", TestContext.Current.CancellationToken)).Base64Png);
      var platform = new QrCodeSavePlatform(() => 0);
      string pngPath = Path.Combine(root, "code.png");
      string jpegPath = Path.Combine(root, "code.jpg");
      await platform.WriteFileAsync(pngPath, png, TestContext.Current.CancellationToken);
      await platform.WriteFileAsync(jpegPath, png, TestContext.Current.CancellationToken);
      Assert.Equal([137, 80, 78, 71], (await File.ReadAllBytesAsync(pngPath, TestContext.Current.CancellationToken))[..4]);
      Assert.Equal([255, 216], (await File.ReadAllBytesAsync(jpegPath, TestContext.Current.CancellationToken))[..2]);
      byte[] original = await File.ReadAllBytesAsync(jpegPath, TestContext.Current.CancellationToken);
      await Assert.ThrowsAnyAsync<Exception>(() => platform.WriteFileAsync(jpegPath,
        [1, 2, 3], TestContext.Current.CancellationToken));
      Assert.Equal(original, await File.ReadAllBytesAsync(jpegPath, TestContext.Current.CancellationToken));
      using var cancelled = new CancellationTokenSource();
      cancelled.Cancel();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        platform.WriteFileAsync(pngPath, png, cancelled.Token));
      Assert.Equal(png, await File.ReadAllBytesAsync(pngPath, TestContext.Current.CancellationToken));
    }
    finally { Directory.Delete(root, recursive: true); }
  }
}
