using VibeOCR.App.Features.Pins;
using VibeOCR.App.Workbench;
using VibeOCR.App.Web;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// #238 截图历史存储契约：默认关闭、captureKey 去重（一次成功一条）、
/// 双上限最旧先淘汰、单条超界明确拒绝、损坏条目跳过、检索/删除/清空只
/// 触碰 owned 目录清单。
/// </summary>
public sealed class PinHistoryStoreTests : IDisposable
{
  private readonly string root = Path.Combine(
    Path.GetTempPath(), $"vibeocr-pin-history-{Guid.NewGuid():N}");

  private static PinHistoryStore LoadedStore(string dataRoot, TimeProvider? time = null)
  {
    var store = new PinHistoryStore(dataRoot, time);
    store.Initialization.GetAwaiter().GetResult();
    return store;
  }

  private sealed class FrozenTime : TimeProvider
  {
    public DateTimeOffset Now { get; set; } =
      new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
  }

  private static byte[] PngWithTag(byte tag, int? length = null)
  {
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    byte[] data = new byte[length is { } total ? total - png.Length - 12 : 6];
    Array.Fill(data, (byte)'A');
    data[0] = (byte)'t'; data[1] = (byte)'a'; data[2] = (byte)'g'; data[3] = 0;
    data[4] = (byte)('A' + tag % 26);
    byte[] type = "tEXt"u8.ToArray();
    using MemoryStream result = new();
    result.Write(png.AsSpan(0, png.Length - 12));
    Span<byte> number = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
    result.Write(number); result.Write(type); result.Write(data);
    uint crc = WorkbenchAnnotationStore.PngCrc32.Update(0xffffffffu, type);
    crc = WorkbenchAnnotationStore.PngCrc32.Update(crc, data) ^ 0xffffffffu;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, crc);
    result.Write(number); result.Write(png.AsSpan(png.Length - 12));
    return result.ToArray();
  }

  private PinHistoryStore Store(FrozenTime? time = null)
  {
    time ??= new FrozenTime();
    return LoadedStore(root, time);
  }

  public void Dispose()
  {
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
  }

  [Fact]
  public void DisabledByDefaultAndEnablePersists()
  {
    var time = new FrozenTime();
    var store = LoadedStore(root, time);
    Assert.False(store.IsEnabled);
    store.RecordSuccessfulCapture("k1", PngWithTag(1));
    Assert.Equal(0, store.Projection(null).Count);
    store.SetEnabled(true);
    Assert.True(store.IsEnabled);
    // 开关持久化；关闭不清条目。
    Assert.True(LoadedStore(root, time).IsEnabled);
  }

  [Fact]
  public void RecordsOncePerCaptureKeyAndEvictsOldestFirst()
  {
    var time = new FrozenTime();
    var store = LoadedStore(root, time);
    store.SetEnabled(true);
    DateTimeOffset firstAt = time.Now;
    store.RecordSuccessfulCapture("capture-1", PngWithTag(1));
    // 同一 captureKey 二次到达（形状A/B或重试）：只录一条。
    store.RecordSuccessfulCapture("capture-1", PngWithTag(2));
    Assert.Equal(1, store.Projection(null).Count);

    DateTimeOffset secondAt = default;
    for (int index = 2; index <= PinHistoryStore.MaximumEntries; index++)
    {
      time.Now += TimeSpan.FromSeconds(1);
      if (index == 2) secondAt = time.Now;
      store.RecordSuccessfulCapture($"capture-{index}", PngWithTag((byte)index));
    }
    Assert.Equal(PinHistoryStore.MaximumEntries, store.Projection(null).Count);
    // 第 201 条：最旧（capture-1）被淘汰，总数不超上限。
    time.Now += TimeSpan.FromSeconds(1);
    store.RecordSuccessfulCapture("capture-new", PngWithTag(9));
    PinHistoryState projection = store.Projection(null);
    Assert.Equal(PinHistoryStore.MaximumEntries, projection.Count);
    // capture-1（firstAt）已不在；capture-2（secondAt）仍保留；列表按新到旧。
    Assert.DoesNotContain(firstAt, projection.Entries.Select(entry => entry.CreatedAt));
    Assert.Contains(secondAt, projection.Entries.Select(entry => entry.CreatedAt));
    Assert.Equal(projection.Entries.OrderByDescending(entry => entry.CreatedAt).Select(e => e.CreatedAt),
      projection.Entries.Select(e => e.CreatedAt));
  }

  [Fact]
  public void OversizeEntryIsRefusedWithoutBreakingOthers()
  {
    var store = LoadedStore(root, new FrozenTime());
    store.SetEnabled(true);
    store.RecordSuccessfulCapture("ok", PngWithTag(3));
    // 单条超界（>64MiB）：明确拒绝且不影响已有条目/后续录制。
    byte[] oversize = new byte[PinHistoryStore.MaximumEntryBytes + 1];
    byte[] header = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    for (int i = 0; i < header.Length; i++) oversize[i] = header[i];
    store.RecordSuccessfulCapture("huge", oversize);
    PinHistoryState projection = store.Projection(null);
    Assert.Equal(1, projection.Count);
    store.RecordSuccessfulCapture("ok2", PngWithTag(4));
    Assert.Equal(2, store.Projection(null).Count);
  }

  [Fact]
  public void SearchMatchesDateAndEnrichedRecognizedText()
  {
    var time = new FrozenTime();
    var store = LoadedStore(root, time);
    store.SetEnabled(true);
    store.RecordSuccessfulCapture("a", PngWithTag(1));
    time.Now += TimeSpan.FromDays(1);
    store.RecordSuccessfulCapture("b", PngWithTag(2));
    string firstId = store.Projection(null).Entries.Last().EntryId;
    store.EnrichRecognizedText("a", "发票金额 123 元");
    // enrich 只覆盖空文本，且不覆盖既有文本。
    store.EnrichRecognizedText("a", "second");
    Assert.Equal(1, store.Projection(null).Entries.Count(
      entry => entry.RecognizedText == "发票金额 123 元"));
    Assert.Single(store.Projection("发票").Entries);
    Assert.Single(store.Projection("2026-01-01").Entries);
    Assert.Single(store.Projection("2026-01-02").Entries);
    Assert.Empty(store.Projection("不存在的词").Entries);
    Assert.Equal(firstId, store.Projection("发票").Entries.Single().EntryId);
    // Count/TotalBytes 是全量容量真值，不随检索词缩水。
    Assert.Equal(2, store.Projection("发票").Count);
    Assert.Equal(2 * (long)PngWithTag(1).Length, store.Projection("发票").TotalBytes);
  }

  [Fact]
  public void CorruptEntryIsSkippedWithoutBlockingOthers()
  {
    var time = new FrozenTime();
    var store = LoadedStore(root, time);
    store.SetEnabled(true);
    store.RecordSuccessfulCapture("a", PngWithTag(1));
    store.RecordSuccessfulCapture("b", PngWithTag(2));
    // 破坏其中一条的图像文件后重载：坏条跳过，其它仍可用。
    string badId = LoadedStore(root, time).Projection(null).Entries.Last().EntryId;
    File.Delete(Path.Combine(PinHistoryStore.RootFor(root), badId, "image.png"));
    var reloaded = LoadedStore(root, time);
    PinHistoryState projection = reloaded.Projection(null);
    Assert.Equal(1, projection.Count);
    Assert.Null(reloaded.TryReadImage(badId));
    // 坏条目仍可显式删除（只删 owned 目录清单内条目）。
    Assert.True(reloaded.DeleteEntry(projection.Entries.Single().EntryId));
    Assert.Equal(0, reloaded.Projection(null).Count);
  }

  [Fact]
  public void CorruptionAfterIhdrWithSameLengthIsRejectedOnReloadAndReopen()
  {
    var store = Store(); store.SetEnabled(true);
    store.RecordSuccessfulCapture("good", PngWithTag(1));
    store.RecordSuccessfulCapture("damaged", PngWithTag(2));
    string id = store.Projection(null).Entries[0].EntryId;
    string path = Path.Combine(PinHistoryStore.RootFor(root), id, "image.png");
    byte[] damaged = File.ReadAllBytes(path);
    Array.Clear(damaged, 24, damaged.Length - 24);
    File.WriteAllBytes(path, damaged);
    Assert.Null(store.TryReadImage(id));
    Assert.Single(Store().Projection(null).Entries);
    store.RecordSuccessfulCapture("invalid-new", damaged);
    Assert.Equal(2, store.Projection(null).Count);
    Assert.NotNull(store.Projection(null).Error);
  }

  [Fact]
  public void ClearAndDeleteOnlyTouchOwnedListedDirectories()
  {
    var store = LoadedStore(root, new FrozenTime());
    store.SetEnabled(true);
    store.RecordSuccessfulCapture("a", PngWithTag(1));
    string entryId = store.Projection(null).Entries.Single().EntryId;
    // owned 根下的非清单目录不得被清空触碰。
    string decoy = Path.Combine(PinHistoryStore.RootFor(root), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(decoy);
    File.WriteAllText(Path.Combine(decoy, "user.png"), "keep");

    Assert.True(store.DeleteEntry(entryId));
    Assert.False(Directory.Exists(Path.Combine(PinHistoryStore.RootFor(root), entryId)));
    Assert.True(Directory.Exists(decoy));

    store.RecordSuccessfulCapture("b", PngWithTag(2));
    Assert.Equal(1, store.ClearAll());
    Assert.Equal(0, store.Projection(null).Count);
    Assert.True(Directory.Exists(decoy));
    Assert.True(File.Exists(Path.Combine(decoy, "user.png")));
  }
  [Fact]
  public void FailedIndexCommitRetainsPreviousEntriesAndReportsTheCaptureFailure()
  {
    var store = Store();
    store.SetEnabled(true);
    store.RecordSuccessfulCapture("first", PngWithTag(1));
    string index = Path.Combine(PinHistoryStore.RootFor(root), "index.json");
    File.Delete(index);
    Directory.CreateDirectory(index);
    store.RecordSuccessfulCapture("second", PngWithTag(2));
    Assert.Equal(1, store.Projection(null).Count);
    Assert.NotNull(store.Projection(null).Error);
    Assert.Single(Directory.EnumerateDirectories(PinHistoryStore.RootFor(root)),
      path => Guid.TryParseExact(Path.GetFileName(path), "N", out _));
  }

  [Fact]
  public void NonAdjacentDuplicateKeysAndCorruptImagesDoNotBecomeNewHistory()
  {
    var store = Store();
    store.SetEnabled(true);
    store.RecordSuccessfulCapture("first", PngWithTag(1));
    store.RecordSuccessfulCapture("second", PngWithTag(2));
    store.RecordSuccessfulCapture("first", PngWithTag(3));
    Assert.Equal(2, store.Projection(null).Count);
    string bad = store.Projection(null).Entries[0].EntryId;
    File.WriteAllText(Path.Combine(PinHistoryStore.RootFor(root), bad, "image.png"), "corrupt");
    Assert.Single(Store().Projection(null).Entries);
  }

  [Fact]
  public void AggregateByteBudgetEvictsOldestEvenBeforeTheCountLimit()
  {
    var store = Store();
    store.SetEnabled(true);
    byte[] image = PngWithTag(1, (int)PinHistoryStore.MaximumEntryBytes);
    store.RecordSuccessfulCapture("oldest", image);
    store.EnrichRecognizedText("oldest", "oldest-marker");
    for (int index = 0; index < 4; index++) store.RecordSuccessfulCapture($"new-{index}", image);
    PinHistoryState history = store.Projection(null);
    Assert.Equal(4, history.Count);
    Assert.Equal(PinHistoryStore.MaximumTotalBytes, history.TotalBytes);
    Assert.Empty(store.Projection("oldest-marker").Entries);
  }

  [Fact]
  public void FullHistoryWithRecognizedCjkTextFitsTheBridgeBudgetAndSearchesFullText()
  {
    var store = Store();
    store.SetEnabled(true);
    for (int index = 0; index < PinHistoryStore.MaximumEntries; index++)
    {
      string key = $"capture-{index}";
      store.RecordSuccessfulCapture(key, PngWithTag(1));
      store.EnrichRecognizedText(key, new string('中', 256) + "末尾检索词");
    }
    PinHistoryState history = store.Projection("末尾检索词");
    Assert.Equal(PinHistoryStore.MaximumEntries, history.Entries.Count);
    string wire = WorkbenchBridgeCodec.SerializeState(Guid.NewGuid(), new WorkbenchStateEnvelope(
      1, "pinEdit", WorkbenchStateChange.Replace, new PinEditWorkbenchState(false, "pinEdit.ready", History: history)));
    Assert.True(System.Text.Encoding.UTF8.GetByteCount(wire) < WorkbenchBridgeCodec.MaxMessageBytes);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  [InlineData(3)]
  [InlineData(4)]
  public void InvalidDimensionsAndTruncatedImagesDoNotBlockHistory(int corruption)
  {
    var store = Store(); store.SetEnabled(true);
    store.RecordSuccessfulCapture("good", PngWithTag(1));
    store.RecordSuccessfulCapture("damaged", PngWithTag(2));
    string id = store.Projection(null).Entries[0].EntryId;
    string path = Path.Combine(PinHistoryStore.RootFor(root), id, "image.png");
    byte[] damaged = File.ReadAllBytes(path);
    if (corruption == 0) damaged[19] = 0;
    else if (corruption == 1) damaged[23] = 0;
    else if (corruption is 2 or 3)
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
        damaged.AsSpan(corruption == 2 ? 16 : 20, 4), 32769);
    else damaged = damaged[..20];
    File.WriteAllBytes(path, damaged);
    Assert.Null(store.TryReadImage(id));
    Assert.Single(Store().Projection(null).Entries);
    store.RecordSuccessfulCapture("invalid-new", damaged);
    Assert.Equal(2, store.Projection(null).Count);
    Assert.NotNull(store.Projection(null).Error);
  }


  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task BackgroundValidationDoesNotBlockOrOverwriteConcurrentOperations(bool clear)
  {
    var seed = LoadedStore(root); seed.SetEnabled(true);
    seed.RecordSuccessfulCapture("a", PngWithTag(1));
    seed.RecordSuccessfulCapture("b", PngWithTag(2));
    string deleted = seed.Projection(null).Entries[0].EntryId;
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var loading = new PinHistoryStore(root, validateLoadedImage: async png =>
    {
      started.TrySetResult(); await release.Task;
      await PinSnapshotStore.ValidatePngAsync(png, CancellationToken.None); return true;
    });
    try
    {
      await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      Assert.False(loading.Initialization.IsCompleted);
      Assert.Empty(loading.Projection(null).Entries);
      Assert.True(loading.IsEnabled);
      Assert.True(loading.DeleteEntry(deleted));
      loading.RecordSuccessfulCapture("new", PngWithTag(3));
      Assert.Single(loading.Projection(null).Entries);
      loading.SetEnabled(false);
      if (clear) Assert.Equal(2, loading.ClearAll());
    }
    finally { release.TrySetResult(true); }
    await loading.Initialization;
    Assert.False(loading.IsEnabled);
    Assert.Equal(clear ? 0 : 2, loading.Projection(null).Count);
    Assert.DoesNotContain(loading.Projection(null).Entries, entry => entry.EntryId == deleted);
    var reloaded = LoadedStore(root);
    Assert.False(reloaded.IsEnabled);
    Assert.Equal(loading.Projection(null).Entries.Select(entry => entry.EntryId), reloaded.Projection(null).Entries.Select(entry => entry.EntryId));
  }

}
