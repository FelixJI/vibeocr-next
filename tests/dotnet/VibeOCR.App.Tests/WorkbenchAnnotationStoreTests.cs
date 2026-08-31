using System.Buffers.Binary;
using System.Text;
using VibeOCR.App.Web;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WorkbenchAnnotationStoreTests : IDisposable
{
  private static readonly byte[] Png = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
  private readonly string resourceRoot = Path.Combine(
    Path.GetTempPath(),
    $"vibeocr-annotation-store-{Guid.NewGuid():N}");

  public WorkbenchAnnotationStoreTests()
  {
    Directory.CreateDirectory(resourceRoot);
  }

  [Fact]
  public async Task UploadReturnsOpaqueSameOriginUriAndTakeIsOneShot()
  {
    using WorkbenchAnnotationStore store = new(resourceRoot);
    await using MemoryStream upload = new(Png);

    WorkbenchAnnotationLease lease = await store.UploadPngAsync(
      upload,
      TestContext.Current.CancellationToken);

    Assert.StartsWith(
      "https://app.vibeocr/__annotation/",
      lease.ResourceUri.AbsoluteUri,
      StringComparison.Ordinal);
    Assert.DoesNotContain(resourceRoot, lease.ResourceUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    string path;
    using (WorkbenchAnnotationFile annotation = store.Take(lease.ResourceUri))
    {
      path = annotation.Path;
      Assert.Equal(Png, await File.ReadAllBytesAsync(
        path,
        TestContext.Current.CancellationToken));
      Assert.Throws<WorkbenchAnnotationAccessException>(() => store.Take(lease.ResourceUri));
    }
    Assert.False(File.Exists(path));
  }

  [Fact]
  public async Task UploadParsesPngChunksAndRejectsMalformedOrOversizedImages()
  {
    using WorkbenchAnnotationStore store = new(resourceRoot);
    byte[][] invalidImages =
    [
      [.. Png.AsSpan(0, 8), 1, 2, 3, 4],
      BuildPng(1, 1, includeHeader: false),
      Png[..^12],
      BuildPng(1, 1, duplicateHeader: true),
      BuildPng(1, 1, invalidHeaderLength: true),
      BuildPng(0, 1),
      BuildPng(32_769, 1),
      BuildPng(20_000, 20_000),
    ];

    foreach (byte[] invalidImage in invalidImages)
    {
      await using MemoryStream upload = new(invalidImage);
      await Assert.ThrowsAsync<WorkbenchAnnotationAccessException>(() =>
        store.UploadPngAsync(upload, TestContext.Current.CancellationToken));
    }

    await using MemoryStream validUpload = new(Png);
    WorkbenchAnnotationLease valid = await store.UploadPngAsync(
      validUpload,
      TestContext.Current.CancellationToken);
    using WorkbenchAnnotationFile annotation = store.Take(valid.ResourceUri);
    Assert.Equal(Png, await File.ReadAllBytesAsync(
      annotation.Path,
      TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task UploadRejectsNonPngAndOversizedContentWithoutLeavingFiles()
  {
    using WorkbenchAnnotationStore store = new(
      resourceRoot,
      maximumPngBytes: Png.Length);
    await using MemoryStream invalid = new(Encoding.UTF8.GetBytes("not a png"));
    await Assert.ThrowsAsync<WorkbenchAnnotationAccessException>(() =>
      store.UploadPngAsync(invalid, TestContext.Current.CancellationToken));
    await using MemoryStream oversized = new([.. Png, 4]);
    await Assert.ThrowsAsync<WorkbenchAnnotationAccessException>(() =>
      store.UploadPngAsync(oversized, TestContext.Current.CancellationToken));

    Assert.Empty(Directory.EnumerateFiles(resourceRoot, "*", SearchOption.AllDirectories));
  }

  [Fact]
  public async Task ExpiredLeaseIsRejectedAndDeleted()
  {
    AdjustableTimeProvider clock = new(
      new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    using WorkbenchAnnotationStore store = new(resourceRoot, clock);
    await using MemoryStream upload = new(Png);
    WorkbenchAnnotationLease lease = await store.UploadPngAsync(
      upload,
      TestContext.Current.CancellationToken);
    clock.Advance(WorkbenchAnnotationStore.DefaultLifetime + TimeSpan.FromSeconds(1));

    Assert.Throws<WorkbenchAnnotationAccessException>(() => store.Take(lease.ResourceUri));
    Assert.Empty(Directory.EnumerateFiles(resourceRoot, "*", SearchOption.AllDirectories));
  }

  [Fact]
  public async Task UploadSweepsExpiredLeaseBeforeApplyingEntryQuota()
  {
    AdjustableTimeProvider clock = new(
      new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    using WorkbenchAnnotationStore store = new(
      resourceRoot,
      clock,
      maximumUnconsumedEntries: 1);
    await using MemoryStream firstUpload = new(Png);
    WorkbenchAnnotationLease first = await store.UploadPngAsync(
      firstUpload,
      TestContext.Current.CancellationToken);
    clock.Advance(WorkbenchAnnotationStore.DefaultLifetime + TimeSpan.FromSeconds(1));

    await using MemoryStream secondUpload = new(Png);
    WorkbenchAnnotationLease second = await store.UploadPngAsync(
      secondUpload,
      TestContext.Current.CancellationToken);

    Assert.Throws<WorkbenchAnnotationAccessException>(() => store.Take(first.ResourceUri));
    using WorkbenchAnnotationFile current = store.Take(second.ResourceUri);
    Assert.Single(Directory.EnumerateFiles(resourceRoot, "*", SearchOption.AllDirectories));
  }

  [Fact]
  public async Task UploadEnforcesEntryAndAggregateCompressedByteQuotas()
  {
    using (WorkbenchAnnotationStore entryLimited = new(
      resourceRoot,
      maximumUnconsumedEntries: 1))
    {
      WorkbenchAnnotationLease first = await UploadAsync(entryLimited, Png);
      await Assert.ThrowsAsync<WorkbenchAnnotationAccessException>(() =>
        UploadAsync(entryLimited, Png));
      using WorkbenchAnnotationFile consumed = entryLimited.Take(first.ResourceUri);
      WorkbenchAnnotationLease afterConsume = await UploadAsync(entryLimited, Png);
      using WorkbenchAnnotationFile consumedAgain = entryLimited.Take(
        afterConsume.ResourceUri);
    }

    using WorkbenchAnnotationStore bytesLimited = new(
      resourceRoot,
      maximumPngBytes: Png.Length,
      maximumUnconsumedEntries: 2,
      maximumSessionPngBytes: Png.Length);
    WorkbenchAnnotationLease withinQuota = await UploadAsync(bytesLimited, Png);
    await Assert.ThrowsAsync<WorkbenchAnnotationAccessException>(() =>
      UploadAsync(bytesLimited, Png));
    using WorkbenchAnnotationFile released = bytesLimited.Take(withinQuota.ResourceUri);
    WorkbenchAnnotationLease afterRelease = await UploadAsync(bytesLimited, Png);
    using WorkbenchAnnotationFile final = bytesLimited.Take(afterRelease.ResourceUri);
  }

  [Fact]
  public async Task ConcurrentUploadCannotStageBytesBeyondSessionQuota()
  {
    using WorkbenchAnnotationStore store = new(
      resourceRoot,
      maximumPngBytes: Png.Length,
      maximumUnconsumedEntries: 2,
      maximumSessionPngBytes: Png.Length);
    await using BlockingPngStream blocked = new(Png);
    Task<WorkbenchAnnotationLease> firstUpload = store.UploadPngAsync(
      blocked,
      TestContext.Current.CancellationToken);
    await blocked.WaitUntilBlockedAsync();

    await Assert.ThrowsAsync<WorkbenchAnnotationAccessException>(() =>
      UploadAsync(store, Png));
    FileInfo[] staged = [.. Directory
      .EnumerateFiles(resourceRoot, "*", SearchOption.AllDirectories)
      .Select(path => new FileInfo(path))];
    Assert.Single(staged);
    Assert.True(staged.Sum(file => file.Length) <= Png.Length);

    blocked.Complete();
    WorkbenchAnnotationLease lease = await firstUpload;
    using WorkbenchAnnotationFile consumed = store.Take(lease.ResourceUri);
  }

  [Fact]
  public async Task CancelledUploadReleasesStagedByteReservation()
  {
    using WorkbenchAnnotationStore store = new(
      resourceRoot,
      maximumPngBytes: Png.Length,
      maximumSessionPngBytes: Png.Length);
    await using BlockingPngStream blocked = new(Png);
    using CancellationTokenSource cancellation = new();
    Task<WorkbenchAnnotationLease> cancelledUpload = store.UploadPngAsync(
      blocked,
      cancellation.Token);
    await blocked.WaitUntilBlockedAsync();

    cancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledUpload);
    WorkbenchAnnotationLease retry = await UploadAsync(store, Png);
    using WorkbenchAnnotationFile consumed = store.Take(retry.ResourceUri);
  }

  [Fact]
  public async Task DisposeReleasesReservationAndActiveUploadCleansTemporaryFile()
  {
    WorkbenchAnnotationStore store = new(
      resourceRoot,
      maximumPngBytes: Png.Length,
      maximumSessionPngBytes: Png.Length);
    await using BlockingPngStream blocked = new(Png);
    Task<WorkbenchAnnotationLease> upload = store.UploadPngAsync(
      blocked,
      TestContext.Current.CancellationToken);
    await blocked.WaitUntilBlockedAsync();

    store.Dispose();
    blocked.Complete();
    await Assert.ThrowsAsync<ObjectDisposedException>(() => upload);
    Assert.Empty(Directory.EnumerateFiles(resourceRoot, "*", SearchOption.AllDirectories));
  }

  [Theory]
  [InlineData("http://app.vibeocr/__annotation/00000000000000000000000000000000")]
  [InlineData("https://other.vibeocr/__annotation/00000000000000000000000000000000")]
  [InlineData("https://app.vibeocr/__annotation/00000000000000000000000000000000?x=1")]
  [InlineData("https://app.vibeocr/__annotation/../00000000000000000000000000000000")]
  public void ResourceUriRejectsAnythingOutsideExactOpaqueRoute(string value) =>
    Assert.False(WorkbenchAnnotationStore.IsResourceUri(new Uri(value)));

  private static async Task<WorkbenchAnnotationLease> UploadAsync(
    WorkbenchAnnotationStore store,
    byte[] content)
  {
    await using MemoryStream upload = new(content);
    return await store.UploadPngAsync(upload, TestContext.Current.CancellationToken);
  }

  private static byte[] BuildPng(
    uint width,
    uint height,
    bool includeHeader = true,
    bool duplicateHeader = false,
    bool invalidHeaderLength = false)
  {
    using MemoryStream png = new();
    png.Write(Png.AsSpan(0, 8));
    byte[] header = new byte[invalidHeaderLength ? 12 : 13];
    if (header.Length == 13)
    {
      BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
      BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
      header[8] = 8;
      header[9] = 6;
    }
    if (includeHeader)
    {
      WriteChunk(png, "IHDR", header);
    }
    if (duplicateHeader)
    {
      WriteChunk(png, "IHDR", header);
    }
    WriteChunk(png, "IDAT", []);
    WriteChunk(png, "IEND", []);
    return png.ToArray();
  }

  private static void WriteChunk(Stream destination, string type, byte[] data)
  {
    Span<byte> length = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
    destination.Write(length);
    destination.Write(Encoding.ASCII.GetBytes(type));
    destination.Write(data);
    destination.Write([0, 0, 0, 0]);
  }

  public void Dispose()
  {
    Directory.Delete(resourceRoot, recursive: true);
  }

  private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration) => utcNow += duration;
  }

  private sealed class BlockingPngStream(byte[] content) : Stream
  {
    private readonly TaskCompletionSource blocked = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private int readCount;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => content.Length;

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public Task WaitUntilBlockedAsync() => blocked.Task;

    public void Complete() => completed.TrySetResult();

    public override int Read(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default)
    {
      if (Interlocked.Increment(ref readCount) == 1)
      {
        content.AsSpan().CopyTo(buffer.Span);
        return content.Length;
      }
      blocked.TrySetResult();
      await completed.Task.WaitAsync(cancellationToken);
      return 0;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
      throw new NotSupportedException();

    public override void SetLength(long value) =>
      throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        completed.TrySetResult();
      }
      base.Dispose(disposing);
    }
  }
}
