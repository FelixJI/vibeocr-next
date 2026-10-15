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

  private static async Task<byte[]> PngAsync(uint width = 2, uint height = 2)
  {
    using var stream = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
    byte[] pixels = new byte[checked((int)(width * height * 4))];
    for (int i = 0; i < pixels.Length; i += 4)
    {
      pixels[i] = 255;
      pixels[i + 3] = 128;
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
    Assert.Equal(png, image.Data);
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
    Assert.Equal(png, image.Data);
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