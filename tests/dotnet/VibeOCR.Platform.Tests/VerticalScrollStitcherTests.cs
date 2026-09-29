using System.Threading;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class VerticalScrollStitcherTests
{
  private const int Width = 48;
  private const int FrameHeight = 96;

  // 行首像素编码行号，其余像素随 x/y 变化，保证任意两行内容都不同（可辨识、无周期）。
  private static byte[] RenderDocument(int width, int height)
  {
    byte[] pixels = new byte[checked(width * height * 4)];
    int rowBytes = width * 4;
    for (int y = 0; y < height; y++)
    {
      int offset = y * rowBytes;
      pixels[offset + 0] = (byte)(y & 0xFF);
      pixels[offset + 1] = (byte)((y >> 8) & 0xFF);
      pixels[offset + 2] = 0x5A;
      pixels[offset + 3] = 0xFF;
      for (int x = 4; x < rowBytes; x += 4)
      {
        pixels[offset + x + 0] = (byte)(x ^ y);
        pixels[offset + x + 1] = (byte)(0xC3 ^ (y >> 3));
        pixels[offset + x + 2] = 0x96;
        pixels[offset + x + 3] = 0xFF;
      }
    }

    return pixels;
  }

  private static CapturedFrame FrameFromDocument(byte[] document, int width, int top, int frameHeight)
  {
    return FrameFromDocument(document, width, top, frameHeight, width * 4);
  }

  private static CapturedFrame FrameFromDocument(
    byte[] document,
    int width,
    int top,
    int frameHeight,
    int stride)
  {
    int rowBytes = width * 4;
    byte[] pixels = new byte[checked(stride * frameHeight)];
    for (int y = 0; y < frameHeight; y++)
    {
      Buffer.BlockCopy(document, (top + y) * rowBytes, pixels, y * stride, rowBytes);
    }

    return new CapturedFrame(pixels, width, frameHeight, stride, "BGRA8");
  }

  // 纵向周期图案：第 y 行使用 pattern[(y + offset) % period]，8 种行图案互不相同。
  private static CapturedFrame PeriodicFrame(int width, int height, int period, int offset)
  {
    int rowBytes = width * 4;
    byte[] pixels = new byte[checked(rowBytes * height)];
    for (int y = 0; y < height; y++)
    {
      int pattern = ((y + offset) % period + period) % period;
      int row = y * rowBytes;
      for (int x = 0; x < rowBytes; x += 4)
      {
        pixels[row + x + 0] = (byte)(pattern * 29 + x);
        pixels[row + x + 1] = (byte)(pattern * 53 + 7);
        pixels[row + x + 2] = (byte)(pattern * 101 + 13);
        pixels[row + x + 3] = 0xFF;
      }
    }

    return new CapturedFrame(pixels, width, height, rowBytes, "BGRA8");
  }

  private static CapturedFrame ConstantFrame(int width, int height, byte value)
  {
    return new CapturedFrame(
      Enumerable.Repeat(value, width * height * 4).ToArray(), width, height, width * 4, "BGRA8");
  }

  [Fact]
  public void ConstructorStoresFirstFrameAndBuildsExactCopy()
  {
    byte[] document = RenderDocument(Width, FrameHeight);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));

    Assert.Equal(Width, stitcher.Width);
    Assert.Equal(FrameHeight, stitcher.Height);
    Assert.Equal(1, stitcher.FrameCount);

    CapturedFrame built = stitcher.BuildFrame();
    Assert.Equal(Width, built.Width);
    Assert.Equal(FrameHeight, built.Height);
    Assert.Equal(Width * 4, built.Stride);
    Assert.Equal("BGRA8", built.PixelFormat);
    Assert.Equal(document, built.Pixels);
  }

  [Fact]
  public void ConsecutiveDownwardScrollStitchesExactDocument()
  {
    const int firstScroll = 40;
    const int secondScroll = 17;
    byte[] document = RenderDocument(Width, FrameHeight + firstScroll + secondScroll);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));

    ScrollAppendResult first = stitcher.Append(FrameFromDocument(document, Width, firstScroll, FrameHeight), TestContext.Current.CancellationToken);
    Assert.Equal(ScrollAppendStatus.Added, first.Status);
    Assert.Equal(firstScroll, first.AddedRows);

    ScrollAppendResult second = stitcher.Append(
      FrameFromDocument(document, Width, firstScroll + secondScroll, FrameHeight),
      TestContext.Current.CancellationToken);
    Assert.Equal(ScrollAppendStatus.Added, second.Status);
    Assert.Equal(secondScroll, second.AddedRows);

    Assert.Equal(3, stitcher.FrameCount);
    Assert.Equal(FrameHeight + firstScroll + secondScroll, stitcher.Height);
    CapturedFrame built = stitcher.BuildFrame();
    Assert.Equal(FrameHeight + firstScroll + secondScroll, built.Height);
    Assert.Equal(document, built.Pixels);
  }

  [Fact]
  public void DuplicateFrameDoesNotGrowStitch()
  {
    byte[] document = RenderDocument(Width, FrameHeight);
    CapturedFrame first = FrameFromDocument(document, Width, 0, FrameHeight);
    var stitcher = new VerticalScrollStitcher(first);

    Assert.Equal(new ScrollAppendResult(ScrollAppendStatus.Duplicate, 0), stitcher.Append(first, TestContext.Current.CancellationToken));
    Assert.Equal(
      new ScrollAppendResult(ScrollAppendStatus.Duplicate, 0),
      stitcher.Append(FrameFromDocument(document, Width, 0, FrameHeight), TestContext.Current.CancellationToken));

    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(FrameHeight, stitcher.Height);

    // 重复帧不占用帧额度，后续滚动仍可继续。
    byte[] scrolled = RenderDocument(Width, FrameHeight + 20);
    Assert.Equal(
      ScrollAppendStatus.Added,
      stitcher.Append(FrameFromDocument(scrolled, Width, 20, FrameHeight), TestContext.Current.CancellationToken).Status);
    Assert.Equal(2, stitcher.FrameCount);
  }

  [Fact]
  public void ReverseScrollIsRejectedWithoutGrowth()
  {
    // 首帧取文档 32..128 行，下一帧取 12..108 行：内容向上滚动了 20 行。
    byte[] document = RenderDocument(Width, 160);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 32, FrameHeight));

    ScrollAppendResult result = stitcher.Append(FrameFromDocument(document, Width, 12, FrameHeight), TestContext.Current.CancellationToken);
    Assert.Equal(ScrollAppendStatus.Reverse, result.Status);
    Assert.Equal(0, result.AddedRows);
    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(FrameHeight, stitcher.Height);

    // 拒绝后状态可用：正确向下滚动仍被接受。
    Assert.Equal(
      ScrollAppendStatus.Added,
      stitcher.Append(FrameFromDocument(document, Width, 52, FrameHeight), TestContext.Current.CancellationToken).Status);
  }

  [Fact]
  public void SkippedOrDisjointScrollIsRejected()
  {
    byte[] document = RenderDocument(Width, 296);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));

    // 跳步：滚动 80 行超过 maxScroll=64（minOverlap=max(32,24)=32），仅 16 行重叠不可靠。
    Assert.Equal(
      ScrollAppendStatus.NoOverlap,
      stitcher.Append(FrameFromDocument(document, Width, 80, FrameHeight), TestContext.Current.CancellationToken).Status);
    // 完全无重叠的跳步。
    Assert.Equal(
      ScrollAppendStatus.NoOverlap,
      stitcher.Append(FrameFromDocument(document, Width, 200, FrameHeight), TestContext.Current.CancellationToken).Status);

    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(FrameHeight, stitcher.Height);
    Assert.Equal(
      ScrollAppendStatus.Added,
      stitcher.Append(FrameFromDocument(document, Width, 30, FrameHeight), TestContext.Current.CancellationToken).Status);
  }

  [Fact]
  public void PeriodicContentIsAmbiguous()
  {
    // 周期 8、位移 10：候选偏移 2、10、18… 全部可完整验证，必须判为歧义而不是错拼。
    var stitcher = new VerticalScrollStitcher(PeriodicFrame(Width, FrameHeight, 8, 0));

    ScrollAppendResult result = stitcher.Append(PeriodicFrame(Width, FrameHeight, 8, 10), TestContext.Current.CancellationToken);
    Assert.Equal(ScrollAppendStatus.Ambiguous, result.Status);
    Assert.Equal(0, result.AddedRows);
    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(FrameHeight, stitcher.Height);

    // 歧义拒绝不改变状态，普通内容仍可继续。
    byte[] document = RenderDocument(Width, FrameHeight + 25);
    var plain = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));
    Assert.Equal(
      ScrollAppendStatus.Added,
      plain.Append(FrameFromDocument(document, Width, 25, FrameHeight), TestContext.Current.CancellationToken).Status);
  }

  [Fact]
  public void VaryingOverlapsAppendExactRows()
  {
    // 动态重叠：最小 1 行、最大 maxScroll=64 行、中间值，各自 AddedRows 精确。
    byte[] document = RenderDocument(Width, FrameHeight + 1 + 64 + 33);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));

    Assert.Equal(1, stitcher.Append(FrameFromDocument(document, Width, 1, FrameHeight), TestContext.Current.CancellationToken).AddedRows);
    Assert.Equal(64, stitcher.Append(FrameFromDocument(document, Width, 65, FrameHeight), TestContext.Current.CancellationToken).AddedRows);
    Assert.Equal(33, stitcher.Append(FrameFromDocument(document, Width, 98, FrameHeight), TestContext.Current.CancellationToken).AddedRows);

    Assert.Equal(4, stitcher.FrameCount);
    Assert.Equal(FrameHeight + 98, stitcher.Height);
    Assert.Equal(document, stitcher.BuildFrame().Pixels);
  }

  [Fact]
  public void PaddedStrideFramesAreAcceptedAndStitched()
  {
    byte[] document = RenderDocument(Width, FrameHeight + 20);
    var stitcher = new VerticalScrollStitcher(
      FrameFromDocument(document, Width, 0, FrameHeight, stride: Width * 4 + 64));

    Assert.Equal(
      ScrollAppendStatus.Added,
      stitcher.Append(FrameFromDocument(document, Width, 20, FrameHeight, stride: Width * 4 + 64), TestContext.Current.CancellationToken).Status);
    Assert.Equal(document, stitcher.BuildFrame().Pixels);
  }

  [Fact]
  public void MutatingInputFramesAfterAppendDoesNotCorruptState()
  {
    byte[] document = RenderDocument(Width, FrameHeight + 40 + 17);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));

    CapturedFrame second = FrameFromDocument(document, Width, 40, FrameHeight);
    Assert.Equal(ScrollAppendStatus.Added, stitcher.Append(second, TestContext.Current.CancellationToken).Status);
    second.Pixels.AsSpan().Fill(0xAB);

    CapturedFrame third = FrameFromDocument(document, Width, 57, FrameHeight);
    Assert.Equal(ScrollAppendStatus.Added, stitcher.Append(third, TestContext.Current.CancellationToken).Status);
    third.Pixels.AsSpan().Fill(0xCD);

    Assert.Equal(document, stitcher.BuildFrame().Pixels);
  }

  [Fact]
  public void ConstructorRejectsConstantFrame()
  {
    InvalidDataException error = Assert.Throws<InvalidDataException>(
      () => new VerticalScrollStitcher(ConstantFrame(Width, FrameHeight, 0xFF)));
    Assert.Contains("vertical", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ConstructorRejectsFrameWithoutVerticalVariation()
  {
    // 每行内部有横向图案、但所有行完全相同：纵向无区分，同样必须显式拒绝。
    int rowBytes = Width * 4;
    byte[] row = new byte[rowBytes];
    for (int x = 0; x < rowBytes; x++)
    {
      row[x] = (byte)(x * 7 + 3);
    }

    byte[] pixels = new byte[rowBytes * FrameHeight];
    for (int y = 0; y < FrameHeight; y++)
    {
      Buffer.BlockCopy(row, 0, pixels, y * rowBytes, rowBytes);
    }

    var frame = new CapturedFrame(pixels, Width, FrameHeight, rowBytes, "BGRA8");
    Assert.Throws<InvalidDataException>(() => new VerticalScrollStitcher(frame));
  }

  [Fact]
  public void LowTextureFrameIsRejectedWithoutGrowth()
  {
    byte[] document = RenderDocument(Width, FrameHeight + 30);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));

    ScrollAppendResult result = stitcher.Append(ConstantFrame(Width, FrameHeight, 0x11), TestContext.Current.CancellationToken);
    Assert.Equal(ScrollAppendStatus.LowTexture, result.Status);
    Assert.Equal(0, result.AddedRows);
    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(FrameHeight, stitcher.Height);

    Assert.Equal(
      ScrollAppendStatus.Added,
      stitcher.Append(FrameFromDocument(document, Width, 30, FrameHeight), TestContext.Current.CancellationToken).Status);
    Assert.Equal(document, stitcher.BuildFrame().Pixels);
  }

  [Fact]
  public void ConstructorRejectsNonBgraFormat()
  {
    byte[] pixels = new byte[Width * FrameHeight * 4];
    Assert.Throws<ArgumentException>(
      () => new VerticalScrollStitcher(new CapturedFrame(pixels, Width, FrameHeight, Width * 4, "RGBA8")));
  }

  [Fact]
  public void ConstructorRejectsNonPositiveDimensions()
  {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => new VerticalScrollStitcher(new CapturedFrame([], 0, FrameHeight, 0, "BGRA8")));
    Assert.Throws<ArgumentOutOfRangeException>(
      () => new VerticalScrollStitcher(new CapturedFrame([], Width, -1, 0, "BGRA8")));
  }

  [Fact]
  public void ConstructorRejectsStrideBelowRowSize()
  {
    byte[] pixels = new byte[(Width * 4 - 1) * FrameHeight];
    Assert.Throws<ArgumentOutOfRangeException>(
      () => new VerticalScrollStitcher(new CapturedFrame(pixels, Width, FrameHeight, Width * 4 - 1, "BGRA8")));
  }

  [Fact]
  public void ConstructorRejectsMismatchedBufferLength()
  {
    byte[] pixels = new byte[(Width * 4 * FrameHeight) - 1];
    Assert.Throws<ArgumentException>(
      () => new VerticalScrollStitcher(new CapturedFrame(pixels, Width, FrameHeight, Width * 4, "BGRA8")));
  }

  [Fact]
  public void ConstructorRejectsFramesExceedingHardLimits()
  {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => new VerticalScrollStitcher(
        new CapturedFrame([], VerticalScrollStitcher.MaximumDimension + 7232, 1, 0, "BGRA8")));
    Assert.Throws<ArgumentOutOfRangeException>(
      () => new VerticalScrollStitcher(
        new CapturedFrame([], 3000, 3000, 12000, "BGRA8")));
  }

  [Fact]
  public void AppendValidatesFrameContract()
  {
    byte[] document = RenderDocument(Width, FrameHeight + 30);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));

    byte[] otherWidth = RenderDocument(Width + 1, FrameHeight);
    Assert.Throws<ArgumentException>(
      () => stitcher.Append(FrameFromDocument(otherWidth, Width + 1, 0, FrameHeight), TestContext.Current.CancellationToken));
    byte[] otherHeight = RenderDocument(Width, FrameHeight - 1);
    Assert.Throws<ArgumentException>(
      () => stitcher.Append(FrameFromDocument(otherHeight, Width, 0, FrameHeight - 1), TestContext.Current.CancellationToken));
    byte[] pixels = new byte[Width * FrameHeight * 4];
    Assert.Throws<ArgumentException>(
      () => stitcher.Append(new CapturedFrame(pixels, Width, FrameHeight, Width * 4, "BGRX8"), TestContext.Current.CancellationToken));

    // 校验失败不改变已接受状态。
    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(FrameHeight, stitcher.Height);
  }

  [Fact]
  public void AppendThrowsWhenTokenAlreadyCancelled()
  {
    byte[] document = RenderDocument(Width, FrameHeight + 30);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));
    using var source = new CancellationTokenSource();
    source.Cancel();

    Assert.Throws<OperationCanceledException>(
      () => stitcher.Append(FrameFromDocument(document, Width, 30, FrameHeight), source.Token));
    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(FrameHeight, stitcher.Height);
  }

  [Fact]
  public void AppendHonorsCancellationDuringCandidateScanAndKeepsState()
  {
    CapturedFrame first = PeriodicFrame(Width, FrameHeight, 8, 0);
    var stitcher = new VerticalScrollStitcher(first);
    CapturedFrame next = PeriodicFrame(Width, FrameHeight, 8, 4);
    using var source = new CancellationTokenSource();
    int visited = 0;

    // 已完成一个候选后同步取消；不假设线程池定时器会在某个毫秒内运行。
    Assert.Throws<OperationCanceledException>(() => stitcher.AppendCore(next, source.Token, scroll =>
    {
      visited = scroll;
      if (scroll == 2) source.Cancel();
    }));
    Assert.Equal(2, visited);
    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(FrameHeight, stitcher.Height);
    Assert.Equal(first.Pixels, stitcher.BuildFrame().Pixels);

    // 同一输入不取消时仍完整判定歧义，取消没有改变已接受的图像或匹配语义。
    Assert.Equal(ScrollAppendStatus.Ambiguous, stitcher.Append(next, TestContext.Current.CancellationToken).Status);
    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(first.Pixels, stitcher.BuildFrame().Pixels);
  }

  [Fact]
  public void AppendStopsAtMaximumFrames()
  {
    const int docHeight = FrameHeight + 200; // 200 次向下 1 行的帧 + 冗余。
    byte[] document = RenderDocument(Width, docHeight);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));
    for (int top = 1; top < 200; top++)
    {
      Assert.Equal(
        ScrollAppendStatus.Added,
        stitcher.Append(FrameFromDocument(document, Width, top, FrameHeight), TestContext.Current.CancellationToken).Status);
    }

    Assert.Equal(200, stitcher.FrameCount);
    Assert.Equal(FrameHeight + 199, stitcher.Height);

    // 新内容被额度上限拒绝，重复帧仍不算新帧。
    Assert.Equal(
      ScrollAppendStatus.LimitReached,
      stitcher.Append(FrameFromDocument(document, Width, 200, FrameHeight), TestContext.Current.CancellationToken).Status);
    Assert.Equal(
      ScrollAppendStatus.Duplicate,
      stitcher.Append(FrameFromDocument(document, Width, 199, FrameHeight), TestContext.Current.CancellationToken).Status);
    Assert.Equal(200, stitcher.FrameCount);
    Assert.Equal(FrameHeight + 199, stitcher.Height);

    CapturedFrame built = stitcher.BuildFrame();
    Assert.Equal(FrameHeight + 199, built.Height);
    Assert.Equal(document.AsSpan(0, (FrameHeight + 199) * Width * 4).ToArray(), built.Pixels);
  }

  [Fact]
  public void AppendStopsAtMaximumPixels()
  {
    // 1024x2048 帧：5 次 +1024 后总计 7,340,032 像素，再加 1024 行到 8,388,608 超上限。
    const int width = 1024;
    const int height = 2048;
    const int docHeight = height + 1024 * 5 + 1024;
    byte[] document = RenderDocument(width, docHeight);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, width, 0, height));

    for (int i = 1; i <= 5; i++)
    {
      Assert.Equal(
        ScrollAppendStatus.Added,
        stitcher.Append(FrameFromDocument(document, width, i * 1024, height), TestContext.Current.CancellationToken).Status);
    }

    Assert.Equal(height + 1024 * 5, stitcher.Height);
    Assert.Equal(
      ScrollAppendStatus.LimitReached,
      stitcher.Append(FrameFromDocument(document, width, 1024 * 6, height), TestContext.Current.CancellationToken).Status);
    Assert.Equal(height + 1024 * 5, stitcher.Height);

    // 上限拒绝后小步追加仍允许，且输出精确。
    Assert.Equal(
      ScrollAppendStatus.Added,
      stitcher.Append(FrameFromDocument(document, width, 1024 * 5 + 1, height), TestContext.Current.CancellationToken).Status);
    Assert.Equal(7, stitcher.FrameCount);
    int stitchedHeight = height + 1024 * 5 + 1;
    Assert.Equal(stitchedHeight, stitcher.Height);
    Assert.Equal(
      document.AsSpan(0, stitchedHeight * width * 4).ToArray(),
      stitcher.BuildFrame().Pixels);
  }

  [Fact]
  public void AppendStopsAtMaximumDimension()
  {
    // 高度已达 32768 维度上限：任何追加都应前置拒绝，无需扫描。
    const int width = 8;
    const int height = 32768;
    byte[] document = RenderDocument(width, height + 8);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, width, 0, height));

    Assert.Equal(
      ScrollAppendStatus.LimitReached,
      stitcher.Append(FrameFromDocument(document, width, 8, height), TestContext.Current.CancellationToken).Status);
    Assert.Equal(1, stitcher.FrameCount);
    Assert.Equal(height, stitcher.Height);
  }

  [Fact]
  public void BuildFrameReturnsIndependentCopy()
  {
    byte[] document = RenderDocument(Width, FrameHeight + 20);
    var stitcher = new VerticalScrollStitcher(FrameFromDocument(document, Width, 0, FrameHeight));
    stitcher.Append(FrameFromDocument(document, Width, 20, FrameHeight), TestContext.Current.CancellationToken);

    CapturedFrame first = stitcher.BuildFrame();
    CapturedFrame second = stitcher.BuildFrame();
    Assert.NotSame(first.Pixels, second.Pixels);
    first.Pixels.AsSpan().Fill(0x00);
    Assert.Equal(document, second.Pixels);
  }

  [Theory]
  [InlineData(1, 32)]
  [InlineData(64, 32)]
  [InlineData(128, 32)]
  [InlineData(200, 50)]
  [InlineData(400, 100)]
  public void GetMinimumOverlapMatchesContract(int frameHeight, int expected)
  {
    Assert.Equal(expected, VerticalScrollStitcher.GetMinimumOverlap(frameHeight));
  }

  [Fact]
  public void GetMinimumOverlapRejectsNonPositiveHeight()
  {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => VerticalScrollStitcher.GetMinimumOverlap(0));
  }
}
