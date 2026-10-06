using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Workbench;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PinnedTextMaskTests
{
  [Fact]
  public void FullCoverBoxesAreDetectedWithoutSamplingError()
  {
    Assert.True(PinnedTextMask.CoversEntireImage(
    [
      new WorkbenchExclusionBox(0, 0, 1000, 400),
      new WorkbenchExclusionBox(0, 400, 1000, 600),
    ]));
    Assert.True(PinnedTextMask.CoversEntireImage(
    [
      new WorkbenchExclusionBox(0, 0, 500, 1000),
      new WorkbenchExclusionBox(500, 0, 500, 400),
      new WorkbenchExclusionBox(500, 400, 500, 600),
    ]));
  }

  [Fact]
  public void PartialCoverBoxesAreNotTreatedAsFullCover()
  {
    Assert.False(PinnedTextMask.CoversEntireImage(
    [
      new WorkbenchExclusionBox(0, 0, 1000, 500),
    ]));
    Assert.False(PinnedTextMask.CoversEntireImage(
    [
      new WorkbenchExclusionBox(0, 0, 400, 1000),
      new WorkbenchExclusionBox(0, 500, 600, 500),
    ]));
    Assert.False(PinnedTextMask.CoversEntireImage(
    [
      new WorkbenchExclusionBox(0, 0, 1000, 0),
    ]));
    Assert.False(PinnedTextMask.CoversEntireImage([]));
  }

  [Fact]
  public void PaintWhiteBakesOpaqueWhiteWithOutwardRounding()
  {
    // 8×6：归一化 [100,200]² 外扩取整为 x∈[0,2)、y∈[0,2)。
    byte[] pixels = new byte[8 * 6 * 4];
    PinnedTextMask.PaintWhite(pixels, 8, 6,
    [
      new WorkbenchExclusionBox(100, 100, 100, 100),
    ]);
    for (int y = 0; y < 6; y++)
    {
      for (int x = 0; x < 8; x++)
      {
        int offset = (y * 8 + x) * 4;
        bool masked = x < 2 && y < 2;
        Assert.Equal(masked, pixels[offset + 3] == 0xff);
        if (masked)
        {
          Assert.Equal(0xff, pixels[offset]);
          Assert.Equal(0xff, pixels[offset + 1]);
          Assert.Equal(0xff, pixels[offset + 2]);
        }
      }
    }
  }

  [Fact]
  public async Task CreateMaskedPngWritesRealWhitePixelsAsync()
  {
    string input = await WritePngAsync(
      width: 8, height: 6, seed: 0x10);
    try
    {
      string masked = await PinnedTextMask.CreateMaskedPngAsync(
        input,
        [new WorkbenchExclusionBox(0, 0, 250, 1000)],
        TestContext.Current.CancellationToken);
      try
      {
        Assert.EndsWith(".png", masked, StringComparison.OrdinalIgnoreCase);
        var decoded = await ReadPngPixelsAsync(masked);
        Assert.Equal(8u, decoded.width);
        Assert.Equal(6u, decoded.height);
        byte[] pixels = decoded.data;
        for (int y = 0; y < decoded.height; y++)
        {
          for (int x = 0; x < decoded.width; x++)
          {
            int offset = (int)((y * decoded.width + x) * 4);
            if (x < 2)
            {
              Assert.Equal(0xff, pixels[offset]);
              Assert.Equal(0xff, pixels[offset + 1]);
              Assert.Equal(0xff, pixels[offset + 2]);
              Assert.Equal(0xff, pixels[offset + 3]);
            }
            else
            {
              Assert.Equal(0x10, pixels[offset]);
              Assert.Equal(0x20, pixels[offset + 1]);
              Assert.Equal(0x30, pixels[offset + 2]);
            }
          }
        }
      }
      finally { File.Delete(masked); }
    }
    finally { File.Delete(input); }
  }

  [Fact]
  public async Task CreateMaskedPngCleansUpOnFailureAsync()
  {
    // 失败恢复：非图像输入必须抛错且不遗留临时遮罩文件（调用方拿不到路径）。
    string invalid = Path.Combine(
      Path.GetTempPath(), $"vibeocr-mask-invalid-{Guid.NewGuid():N}.bin");
    await File.WriteAllBytesAsync(invalid, [1, 2, 3, 4, 5, 6, 7, 8],
      TestContext.Current.CancellationToken);
    int before = Directory.GetFiles(
      Path.GetTempPath(), "vibeocr-pin-mask-*.png").Length;
    try
    {
      Exception? thrown = await Record.ExceptionAsync(() =>
        PinnedTextMask.CreateMaskedPngAsync(
          invalid,
          [new WorkbenchExclusionBox(0, 0, 500, 500)],
          TestContext.Current.CancellationToken));
      Assert.NotNull(thrown);
      int after = Directory.GetFiles(
        Path.GetTempPath(), "vibeocr-pin-mask-*.png").Length;
      Assert.Equal(before, after);
    }
    finally { File.Delete(invalid); }
  }

  private static async Task<string> WritePngAsync(uint width, uint height, byte seed)
  {
    byte[] pixels = new byte[width * height * 4];
    for (int i = 0; i < pixels.Length; i += 4)
    {
      pixels[i] = seed;
      pixels[i + 1] = 0x20;
      pixels[i + 2] = 0x30;
      pixels[i + 3] = 0xff;
    }
    string path = Path.Combine(
      Path.GetTempPath(), $"vibeocr-mask-src-{Guid.NewGuid():N}.png");
    using var stream = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
      BitmapEncoder.PngEncoderId, stream);
    encoder.SetPixelData(
      BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
      width, height, 96, 96, pixels);
    await encoder.FlushAsync();
    using (Stream file = File.Create(path))
    {
      using IInputStream read = stream.GetInputStreamAt(0);
      using Stream content = read.AsStreamForRead();
      await content.CopyToAsync(file, 81920, TestContext.Current.CancellationToken);
    }
    return path;
  }

  private static async Task<(byte[] data, uint width, uint height)> ReadPngPixelsAsync(
    string path)
  {
    byte[] png = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
    using var decode = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(decode))
    {
      writer.WriteBytes(png);
      await writer.StoreAsync();
      writer.DetachStream();
    }
    decode.Seek(0);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(decode);
    return ((await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
      BitmapAlphaMode.Straight, new BitmapTransform(),
      ExifOrientationMode.IgnoreExifOrientation,
      ColorManagementMode.DoNotColorManage)).DetachPixelData(),
      decoder.PixelWidth, decoder.PixelHeight);
  }
}
