using System.Runtime.InteropServices.WindowsRuntime;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Web;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;

namespace VibeOCR.App.Tests;

// 使用真实 DataPackage、StorageFile 与 Windows BitmapDecoder；不写系统剪贴板或抢占前台。
public sealed class SystemImportReaderTests
{
  private static CancellationToken Token => TestContext.Current.CancellationToken;

  private static Task<byte[]> PngAsync(uint width = 2, uint height = 2) =>
    ImageBytesAsync(BitmapEncoder.PngEncoderId, width, height);

  private static async Task<byte[]> ImageBytesAsync(Guid encoderId, uint width, uint height, bool noise = false)
  {
    using var stream = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
    byte[] pixels = new byte[checked((int)(width * height * 4))];
    uint random = 1;
    for (int i = 0; i < pixels.Length; i += 4)
    {
      for (int channel = 0; channel < 3; channel++)
      {
        random = unchecked(random * 1664525 + 1013904223);
        pixels[i + channel] = noise ? (byte)(random >> 24) : (byte)(channel == 0 ? 255 : 0);
      }
      pixels[i + 3] = noise ? (byte)255 : (byte)128;
    }
    encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight,
      width, height, 96, 96, pixels);
    await encoder.FlushAsync();
    using var reader = new DataReader(stream.GetInputStreamAt(0));
    await reader.LoadAsync((uint)stream.Size);
    byte[] bytes = new byte[checked((int)stream.Size)];
    reader.ReadBytes(bytes);
    return bytes;
  }

  // Pillow-generated two-frame inputs: first RGBA red/128, then blue/255 (100 ms each).
  [Theory]
  [InlineData("png", "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAACGFjVEwAAAACAAAAAPONk3AAAAAaZmNUTAAAAAAAAAACAAAAAQAAAAAAAAAAAAEACgAA+Sm2eQAAABFJREFUeJxj/M/A0MDAwMAAAAqKAYHtVsU+AAAAGmZjVEwAAAABAAAAAgAAAAEAAAAAAAAAAAABAAoAAGJaXK0AAAAVZmRBVAAAAAJ4nGNkYPj/n4GBgQEACwcCAEgTs00AAAAASUVORK5CYII=")]
  [InlineData("webp", "UklGRoQAAABXRUJQVlA4WAoAAAASAAAAAQAAAAAAQU5JTQYAAAAAAAAAAABBTk1GKAAAAAAAAAAAAAEAAAAAAGQAAAJWUDhMDwAAAC8BAAAQBxD9jwIGIqL/AQBBTk1GKAAAAAAAAAAAAAEAAAAAAGQAAABWUDhMDwAAAC8BAAAABxDR//4HIqL/AQA=")]
  public async Task AnimatedStorageItemPublishesOnlyStaticFirstFrame(string extension, string encoded)
  {
    byte[] source = Convert.FromBase64String(encoded);
    using var sourceStream = await WebWorkbenchHost.BufferBytesAsync(source, Token);
    try { _ = await BitmapDecoder.CreateAsync(sourceStream); }
    catch (System.Runtime.InteropServices.COMException) when (extension == "webp")
    {
      TestContext.Current.TestOutputHelper!.WriteLine("Windows WebP decoder unavailable; import must reject this input.");
      StorageFile unsupported = await StreamedFileAsync("animation.webp", source);
      var rejected = new DataPackage();
      rejected.SetStorageItems([unsupported]);
      SystemImportRejectException error = await Assert.ThrowsAsync<SystemImportRejectException>(() =>
        InputService.CreateImportReader().ReadPackageForImportAsync(rejected.GetView(), Token));
      Assert.Equal("invalid", error.Reason);
      return;
    }
    StorageFile file = await StreamedFileAsync($"animation.{extension}", source);
    var package = new DataPackage();
    package.SetStorageItems([file]);
    ImportedSystemContent? content = await InputService.CreateImportReader()
      .ReadPackageForImportAsync(package.GetView(), Token);
    SystemImportImageItem image = Assert.Single(content!.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.NotEqual(source, image.Data);
    Assert.DoesNotContain("acTL", System.Text.Encoding.ASCII.GetString(image.Data));
    using var output = await WebWorkbenchHost.BufferBytesAsync(image.Data, Token);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(output);
    Assert.Equal(BitmapDecoder.PngDecoderId, decoder.DecoderInformation.CodecId);
    Assert.Equal(1u, decoder.FrameCount);
    PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8,
      BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
      ColorManagementMode.DoNotColorManage);
    Assert.Equal(new byte[] { 255, 0, 0, 128 }, pixels.DetachPixelData()[..4]);
  }

  private static Task<StorageFile> StreamedFileAsync(string name, byte[] bytes) =>
    StorageFile.CreateStreamedFileAsync(name, async request =>
    {
      using Stream output = request.AsStreamForWrite();
      await output.WriteAsync(bytes, Token);
    }, null).AsTask(Token);

  [Fact]
  public async Task JpegExifOrientationIsAppliedOnceToStaticPng()
  {
    byte[] jpeg = await ImageBytesAsync(BitmapEncoder.JpegEncoderId, 2, 1);
    // APP1/Exif: IFD0 Orientation=6 (90 degrees clockwise).
    byte[] exif = Convert.FromHexString("FFE100224578696600004D4D002A00000008000101120003000000010006000000000000");
    byte[] oriented = [.. jpeg[..2], .. exif, .. jpeg[2..]];
    var package = new DataPackage();
    package.SetStorageItems([await StreamedFileAsync("rotated.jpg", oriented)]);
    SystemImportImageItem image = Assert.Single((await InputService.CreateImportReader()
      .ReadPackageForImportAsync(package.GetView(), Token))!.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(1, image.PixelWidth);
    Assert.Equal(2, image.PixelHeight);
    using var output = await WebWorkbenchHost.BufferBytesAsync(image.Data, Token);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(output);
    Assert.Equal(1u, decoder.PixelWidth);
    Assert.Equal(2u, decoder.PixelHeight);
    Assert.Equal(decoder.PixelWidth, decoder.OrientedPixelWidth);
    Assert.Equal(decoder.PixelHeight, decoder.OrientedPixelHeight);
  }

  [Fact]
  public async Task NormalizedEncodingStillEnforcesSingleImageByteLimit()
  {
    byte[] jpeg = await ImageBytesAsync(BitmapEncoder.JpegEncoderId, 2400, 2400, noise: true);
    Assert.True(jpeg.Length < InputService.MaximumImportImageBytes);
    var package = new DataPackage();
    package.SetStorageItems([await StreamedFileAsync("noise.jpg", jpeg)]);
    SystemImportRejectException error = await Assert.ThrowsAsync<SystemImportRejectException>(() =>
      InputService.CreateImportReader().ReadPackageForImportAsync(package.GetView(), Token));
    Assert.Equal("oversize", error.Reason);
  }

  [Fact]
  public async Task NormalizedEncodingStillEnforcesBatchByteLimit()
  {
    byte[] jpeg = await ImageBytesAsync(BitmapEncoder.JpegEncoderId, 1900, 1900, noise: true);
    Assert.True(jpeg.LongLength * 8 < InputService.MaximumImportBatchBytes);
    Assert.True(1900L * 1900 * 8 < InputService.MaximumImportBatchPixels);
    StorageFile file = await StreamedFileAsync("batch-noise.jpg", jpeg);
    var package = new DataPackage();
    package.SetStorageItems(Enumerable.Repeat<IStorageItem>(file, 8));
    SystemImportRejectException error = await Assert.ThrowsAsync<SystemImportRejectException>(() =>
      InputService.CreateImportReader().ReadPackageForImportAsync(package.GetView(), Token));
    Assert.Equal("budget", error.Reason);
    Assert.Contains("资源总体积", error.Message);
  }

  [Fact]
  public async Task BrokeredStorageFileWithoutPathUsesAuthorizedNameAndRealDecoder()
  {
    byte[] png = await PngAsync();
    StorageFile file = await StorageFile.CreateStreamedFileAsync("brokered.png", async request =>
    {
      using Stream output = request.AsStreamForWrite();
      await output.WriteAsync(png, Token);
    }, null);
    Assert.Equal(string.Empty, file.Path);
    var package = new DataPackage();
    package.SetStorageItems([file]);
    ImportedSystemContent? content = await InputService.CreateImportReader().ReadPackageForImportAsync(package.GetView(), Token);
    SystemImportImageItem image = Assert.Single(content!.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(new byte[] { 137, 80, 78, 71 }, image.Data[..4]);
    Assert.Equal(2, image.PixelWidth);
  }
  [Fact]
  public async Task StorageItemsEncodedBudgetRejectsWholeBatchBeforeReadingFifthImage()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-import-bytes-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      string path = Path.Combine(root, "padded.png");
      await File.WriteAllBytesAsync(path, await PngAsync(), Token);
      using (FileStream file = File.OpenWrite(path)) file.SetLength(InputService.MaximumImportImageBytes);
      StorageFile storage = await StorageFile.GetFileFromPathAsync(path);
      var package = new DataPackage();
      package.SetStorageItems(Enumerable.Repeat<IStorageItem>(storage, 5));
      SystemImportRejectException error = await Assert.ThrowsAsync<SystemImportRejectException>(() =>
        InputService.CreateImportReader().ReadPackageForImportAsync(package.GetView(), Token));
      Assert.Equal("budget", error.Reason);
    }
    finally { Directory.Delete(root, recursive: true); }
  }
  [Fact]
  public async Task BitmapHasPriorityAndRetainsPngAlphaThroughRealDecoder()
  {
    byte[] png = await PngAsync();
    using var stream = await WebWorkbenchHost.BufferBytesAsync(png, Token);
    var package = new DataPackage();
    package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
    package.SetText("不重复导入此文本");
    ImportedSystemContent? content = await InputService.CreateImportReader()
      .ReadPackageForImportAsync(package.GetView(), Token);
    Assert.NotNull(content);
    Assert.Equal(ImportedSystemContent.BitmapSource, content.Source);
    Assert.Null(content.Text);
    SystemImportImageItem image = Assert.Single(content.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(2, image.PixelWidth);
    Assert.Equal(2, image.PixelHeight);
    Assert.Equal(new byte[] { 137, 80, 78, 71 }, image.Data[..4]);
    using var decodedMemory = new MemoryStream(image.Data);
    using var decodedStream = decodedMemory.AsRandomAccessStream();
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(decodedStream);
    PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8,
      BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
      ColorManagementMode.DoNotColorManage);
    Assert.Equal(new byte[] { 255, 0, 0, 128 }, pixels.DetachPixelData()[..4]);
  }

  [Theory]
  [InlineData("text")]
  [InlineData("bitmap")]
  public async Task CancelledPackageIsNeverRead(string format)
  {
    var package = new DataPackage();
    using var memory = new MemoryStream(await PngAsync());
    using var stream = memory.AsRandomAccessStream();
    if (format == "text") package.SetText("取消");
    else package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      InputService.CreateImportReader().ReadPackageForImportAsync(package.GetView(), cancellation.Token));
  }

  [Theory]
  [InlineData(4097, false)]
  [InlineData(129, true)]
  public async Task TextLimitsRejectWithoutTruncation(int count, bool lines)
  {
    var package = new DataPackage();
    package.SetText(lines ? string.Join('\n', Enumerable.Repeat("a", count)) : new string('中', count));
    SystemImportRejectException error = await Assert.ThrowsAsync<SystemImportRejectException>(() =>
      InputService.CreateImportReader().ReadPackageForImportAsync(package.GetView(), Token));
    Assert.Equal("oversize", error.Reason);
  }

  [Theory]
  [InlineData("valid")]
  [InlineData("bad")]
  [InlineData("oversize")]
  [InlineData("side")]
  [InlineData("folder")]
  [InlineData("batch")]
  [InlineData("pixels")]
  public async Task StorageItemsAreAuthorizedDecodedAndRejectedAsOneBatch(string kind)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-import-reader-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      string first = Path.Combine(root, "first.png");
      await File.WriteAllBytesAsync(first, kind == "pixels" ? await PngAsync(4000, 4000) : await PngAsync(), Token);
      var files = new List<IStorageItem> { await StorageFile.GetFileFromPathAsync(first) };
      if (kind == "pixels") { files.Add(files[0]); files.Add(files[0]); }
      else if (kind == "folder") files.Add(await StorageFolder.GetFolderFromPathAsync(root));
      else if (kind == "batch")
      {
        for (int i = 0; i < 16; i++) files.Add(files[0]);
      }
      else if (kind != "valid")
      {
        string second = Path.Combine(root, "second.png");
        await File.WriteAllBytesAsync(second, kind == "side" ? await PngAsync(8193, 1) : [1, 2, 3], Token);
        if (kind == "oversize")
        {
          using var oversized = File.OpenWrite(second);
          oversized.SetLength(InputService.MaximumImportImageBytes + 1);
        }
        files.Add(await StorageFile.GetFileFromPathAsync(second));
      }
      var package = new DataPackage();
      package.SetStorageItems(files);
      package.SetText("文件路径附带文本不产生文字对象");
      Task<ImportedSystemContent?> read = InputService.CreateImportReader()
        .ReadPackageForImportAsync(package.GetView(), Token);
      if (kind == "valid")
      {
        ImportedSystemContent? content = await read;
        Assert.NotNull(content);
        Assert.Equal(ImportedSystemContent.StorageItemsSource, content.Source);
        Assert.Null(content.Text);
        Assert.Equal(2, Assert.Single(content.Images).PixelWidth);
      }
      else
      {
        SystemImportRejectException error = await Assert.ThrowsAsync<SystemImportRejectException>(() => read);
        Assert.Equal(kind switch { "oversize" or "side" => "oversize", "batch" => "batch", "pixels" => "budget", _ => "invalid" }, error.Reason);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}
