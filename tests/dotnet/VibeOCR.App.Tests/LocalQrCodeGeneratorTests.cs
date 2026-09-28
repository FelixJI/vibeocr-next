using VibeOCR.App.Features.QrCode;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class LocalQrCodeGeneratorTests
{
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
      LocalQrCodeGenerator.GenerateAsync("hello", "code128", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync(" ", "qrcode", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync(new string('x', 10000), "qrcode", CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() =>
      LocalQrCodeGenerator.GenerateAsync("\ud800", "qrcode", CancellationToken.None));
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
