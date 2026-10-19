using VibeOCR.App.Features.Pins;
using VibeOCR.App.Workbench;
using Windows.Graphics;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// #238 pin owned 快照存储契约：目录固定为 DataRoot/pins/&lt;pinId&gt;，
/// 原子写入 .vibp+PNG；删除只触碰该点状子目录；PNG 头尺寸探针边界。
/// </summary>
public sealed class PinSnapshotStoreTests : IDisposable
{
  private readonly string root = Path.Combine(
    Path.GetTempPath(), $"vibeocr-pin-snapshot-{Guid.NewGuid():N}");

  private static readonly byte[] Png = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

  private static VibpProjectManifest Manifest(int width = 1, int height = 1) => new()
  {
    SchemaVersion = VibpProjectLimits.SchemaVersion,
    Canvas = new VibpCanvas { Width = width, Height = height },
    Source = new VibpSourceResource
    {
      MediaType = "image/png",
      Width = width,
      Height = height,
      Length = Png.LongLength,
    },
    State = new VibpEditorState { Rotation = 0, Marks = [] },
    Assets = [],
  };

  private static List<VibpProjectEntry> Entries() =>
    [new("source.png", Png.LongLength, () => new MemoryStream(Png, writable: false))];

  [Fact]
  public void RecoveryRootEnumerationFailureDegradesToEmptyInsteadOfCrashingStartup()
  {
    // ACL/瞬时 I/O 等根级枚举失败不得从 Loaded 启动路径逸出：降级为空恢复集。
    Directory.CreateDirectory(root);
    var store = new PinSnapshotStore(root);
    store.RecoveryDirectoryEnumeration = _ => throw new UnauthorizedAccessException("denied");
    Assert.Empty(store.LoadRecovery());
  }

  [Fact]
  public void ConstrainRestoredGeometryKeepsVisibleBandIncludingNegativeAndCrossScreen()
  {
    RectInt32[] dual = [new(0, 0, 1920, 1080), new(-1920, 0, 1920, 1080)];
    // 已与任一工作区保有可拖 band 交集的合法坐标（含负值/跨屏）不乱挪。
    Assert.Equal(new RectInt32(-1500, 100, 300, 200),
      PinSnapshotStore.ConstrainRestoredGeometry(new RectInt32(-1500, 100, 300, 200), dual, 36, 16));
    Assert.Equal(new RectInt32(1900, 100, 300, 200),
      PinSnapshotStore.ConstrainRestoredGeometry(new RectInt32(1900, 100, 300, 200), dual, 36, 16));
    // band 刚好压线：顶部 16px 在屏内即可拖，保留。
    Assert.Equal(new RectInt32(100, -20, 300, 500),
      PinSnapshotStore.ConstrainRestoredGeometry(new RectInt32(100, -20, 300, 500), [new(0, 0, 1920, 1080)], 36, 16));
  }

  [Fact]
  public void ConstrainRestoredGeometryClampsWhenOnlyTheBodyIntersectsOrFullyOffscreen()
  {
    // band 全在屏外、仅正文相交：不可拖，必须吸附（root 回归样本）。
    Assert.Equal(new RectInt32(16, 16, 300, 500),
      PinSnapshotStore.ConstrainRestoredGeometry(new RectInt32(100, -200, 300, 500), [new(0, 0, 1920, 1080)], 32, 16));
    // 完全落在当前虚拟桌面外：就近吸附，尺寸不变。
    Assert.Equal(new RectInt32(16, 16, 300, 200),
      PinSnapshotStore.ConstrainRestoredGeometry(new RectInt32(-5000, -4000, 300, 200), [new(0, 0, 1920, 1080)], 36, 16));
    RectInt32[] dual = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
    Assert.Equal(new RectInt32(1936, 16, 300, 200),
      PinSnapshotStore.ConstrainRestoredGeometry(new RectInt32(2100, -3000, 300, 200), dual, 36, 16));
  }

  [Fact]
  public void ConstrainRestoredGeometryStaysSafeAtExtremeCoordinates()
  {
    // 几何端点用 int 近似极值时不得回绕：仍应判为屏外并吸附。
    Assert.Equal(new RectInt32(16, 16, 300, 200),
      PinSnapshotStore.ConstrainRestoredGeometry(
        new RectInt32(int.MaxValue - 10, int.MaxValue - 10, 300, 200), [new(0, 0, 1920, 1080)], 36, 16));
    Assert.Equal(new RectInt32(16, 16, 300, 200),
      PinSnapshotStore.ConstrainRestoredGeometry(
        new RectInt32(int.MinValue, int.MinValue, 300, 200), [new(0, 0, 1920, 1080)], 36, 16));
  }

  public void Dispose()
  {
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
  }

  [Theory]
  [InlineData("corrupt-json")]
  [InlineData("invalid-exclusions")]
  public async Task CorruptPointerFallsBackToSavedPngAndWindowMetadata(string corruption)
  {
    // 指针损坏（JSON/排除框无效）≠丢失恢复：无 generation、工程 null、PNG
    // 回退 image.png、恢复用已验证 window.json 的 ExcludeBoxes；损坏文件
    // 保留供重试/取证；owned/reparse/size 防护不受影响。
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Image", "", 0, 0, 640, 480, ExcludeBoxes: [new(0, 0, 10, 10)]), Png);
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png,
      TestContext.Current.CancellationToken, [new(0, 0, 20, 20)]);
    string directory = Path.Combine(PinSnapshotStore.RootFor(root), id.ToString("N"));
    string pointer = Path.Combine(directory, "current.json");
    File.WriteAllText(pointer, corruption == "corrupt-json" ? "{ not valid json"
      : """{"Generation":"x","ExcludeBoxes":[{"X":-5,"Y":0,"Width":10,"Height":10}]}""");

    string? pngPath = store.GetSnapshotPngPath(id);
    Assert.NotNull(pngPath);
    Assert.Equal(Path.Combine(directory, "image.png"), pngPath);
    Assert.Null(store.GetSnapshotProjectPath(id));
    Assert.Null(store.GetSnapshotExclusions(id));
    Assert.True(File.Exists(pointer));

    IReadOnlyList<Features.Pins.PinSnapshotStore.Recovery> recovered = new PinSnapshotStore(root).LoadRecovery();
    Features.Pins.PinSnapshotStore.Recovery record = Assert.Single(recovered, item => item.Id == id);
    Assert.NotNull(record.ExcludeBoxes);
    Assert.Equal(new WorkbenchExclusionBox(0, 0, 10, 10), Assert.Single(record.ExcludeBoxes));
  }

  [Fact]
  public async Task WriteThenReadSnapshotPathsAndBytes()
  {
    var store = new PinSnapshotStore(root);
    Guid pinId = Guid.NewGuid();
    await store.WriteSnapshotAsync(pinId, Manifest(), Entries(), Png,
      TestContext.Current.CancellationToken);
    string? pngPath = store.GetSnapshotPngPath(pinId);
    Assert.NotNull(pngPath);
    Assert.True(File.Exists(pngPath!));
    Assert.Equal(Png, await File.ReadAllBytesAsync(pngPath!, TestContext.Current.CancellationToken));
    Assert.True(File.Exists(store.GetSnapshotProjectPath(pinId)));
    // PNG 校验失败时不允许快照图替换：写入抛出，路径不变。
    await Assert.ThrowsAnyAsync<Exception>(() => store.WriteSnapshotAsync(
      pinId, Manifest(), Entries(), [],
      TestContext.Current.CancellationToken));
  }

  [Fact]
  public void DeleteSnapshotRemovesOnlyItsOwnDirectory()
  {
    var store = new PinSnapshotStore(root);
    Guid pinId = Guid.NewGuid();
    string pinDir = Path.Combine(PinSnapshotStore.RootFor(root), pinId.ToString("N"));
    Directory.CreateDirectory(pinDir);
    File.WriteAllText(Path.Combine(pinDir, "image.png"), "x");
    // 同根下非该 pin 的文件与目录不得被触碰。
    string sibling = Path.Combine(PinSnapshotStore.RootFor(root), "keep.txt");
    string decoyDir = Path.Combine(PinSnapshotStore.RootFor(root), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(decoyDir);
    File.WriteAllText(Path.Combine(decoyDir, "keep.png"), "x");
    File.WriteAllText(sibling, "keep");

    store.DeleteSnapshot(pinId);

    Assert.False(Directory.Exists(pinDir));
    Assert.True(File.Exists(sibling));
    Assert.True(Directory.Exists(decoyDir));
    // 幂等：再次删除为无害 no-op。
    store.DeleteSnapshot(pinId);
  }

  [Theory]
  [InlineData(".png")]
  [InlineData(".vibp")]
  public async Task FailedOwnedFileDeletionIsRetriedAtStartupWithoutRestoringTheClosedPin(string extension)
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Image", "", 0, 0, 640, 480), Png);
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
    string image = (extension == ".png" ? store.GetSnapshotPngPath(id) : store.GetSnapshotProjectPath(id))!;
    using (var locked = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
      store.DeleteSnapshot(id);
      Assert.True(File.Exists(image));
      Assert.Empty(new PinSnapshotStore(root).LoadRecovery());
    }
    Assert.Empty(new PinSnapshotStore(root).LoadRecovery());
    Assert.False(File.Exists(image));
    Assert.False(Directory.Exists(Path.GetDirectoryName(image)));
  }

  [Fact]
  public void StartupRetriesCleanupEvenAfterFourWindowsHaveBeenRecovered()
  {
    var store = new PinSnapshotStore(root);
    Guid closed = Guid.NewGuid();
    store.SaveRecovery(new(closed, "Image", "", 0, 0, 640, 480), Png);
    string image = store.GetSnapshotPngPath(closed)!;
    using (var locked = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read))
      store.DeleteSnapshot(closed);
    var live = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
    foreach (Guid id in live) store.SaveRecovery(new(id, "Text", "keep", 0, 0, 640, 480));
    store.RecoveryDirectoryEnumeration = _ => live.Append(closed).Select(id =>
      Path.Combine(PinSnapshotStore.RootFor(root), id.ToString("N")));
    Assert.Equal(4, store.LoadRecovery().Count);
    Assert.False(File.Exists(image));
  }

  [Fact]
  public void FailedCleanupRegistrationPreservesRecoveryAndDoesNotEscapeClose()
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Image", "", 0, 0, 640, 480), Png);
    string image = store.GetSnapshotPngPath(id)!;
    Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(image)!, "delete.pending"));
    Assert.Null(Record.Exception(() => store.DeleteSnapshot(id)));
    Assert.True(File.Exists(image));
    Assert.Equal(id, Assert.Single(new PinSnapshotStore(root).LoadRecovery()).Id);
  }

  [Fact]
  public void SniffPngSizeReadsHeaderAndRejectsInvalidInput()
  {
    Assert.Equal((1, 1), PinSnapshotStore.SniffPngSize(Png));
    Assert.Equal((0, 0), PinSnapshotStore.SniffPngSize([]));
    Assert.Equal((0, 0), PinSnapshotStore.SniffPngSize([1, 2, 3]));
    // IHDR 声明零尺寸：拒绝。
    byte[] zero = [.. Png];
    zero[19] = 0;
    zero[23] = 0;
    Assert.Equal((0, 0), PinSnapshotStore.SniffPngSize(zero));
  }
  [Fact]
  public async Task FailedReplacementRetainsTheCommittedProjectImagePair()
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
    string? project = store.GetSnapshotProjectPath(id), image = store.GetSnapshotPngPath(id);
    await Assert.ThrowsAsync<IOException>(() => store.WriteSnapshotAsync(id, Manifest(),
      [new("source.png", Png.Length, () => throw new IOException("source read failed"))], Png,
      TestContext.Current.CancellationToken));
    Assert.Equal(project, store.GetSnapshotProjectPath(id));
    Assert.Equal(image, store.GetSnapshotPngPath(id));
    Assert.Equal(Png, File.ReadAllBytes(image!));
    Assert.Equal(3, Directory.EnumerateFiles(Path.GetDirectoryName(image!)!).Count());
  }

  [Fact]
  public async Task SnapshotCommitsExclusionsWithItsImageAndProject()
  {
    var store = new PinSnapshotStore(root); Guid id = Guid.NewGuid();
    WorkbenchExclusionBox[] boxes = [new(0, 0, 250, 1000)];
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken, boxes);
    Assert.Equal(boxes, store.GetSnapshotExclusions(id));
    await Assert.ThrowsAsync<IOException>(() => store.WriteSnapshotAsync(id, Manifest(),
      [new("source.png", Png.Length, () => throw new IOException("read failed"))], Png,
      TestContext.Current.CancellationToken, []));
    Assert.Equal(boxes, store.GetSnapshotExclusions(id));
  }

  [Fact]
  public void CorruptRecoveryPngWithIntactIhdrDoesNotBecomeAPin()
  {
    var store = new PinSnapshotStore(root); Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Image", "", 0, 0, 640, 480), Png);
    byte[] corrupt = [.. Png]; Array.Clear(corrupt, 24, corrupt.Length - 24);
    File.WriteAllBytes(store.GetSnapshotPngPath(id)!, corrupt);
    Assert.Null(store.ReadSnapshotPng(id));
    Assert.Empty(store.LoadRecovery());
  }

  [Fact]
  public async Task RealDecoderRejectsEmptyIdatEvenWhenPngStructureIsValid()
  {
    int typeAt = Png.AsSpan().IndexOf("IDAT"u8);
    using MemoryStream invalid = new();
    invalid.Write(Png.AsSpan(0, typeAt - 4));
    invalid.Write(new byte[4]); invalid.Write("IDAT"u8);
    byte[] crc = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc,
      VibeOCR.App.Web.WorkbenchAnnotationStore.PngCrc32.Update(0xffffffffu, "IDAT"u8) ^ 0xffffffffu);
    invalid.Write(crc); invalid.Write(Png.AsSpan(Png.Length - 12));
    byte[] corrupt = invalid.ToArray();
    invalid.Position = 0;
    await VibeOCR.App.Web.WorkbenchAnnotationStore.ValidatePngStructureCoreAsync(invalid, TestContext.Current.CancellationToken);
    Assert.False(PinSnapshotStore.TryValidatePng(corrupt, out _, out _));
  }

  [Fact]
  public void InvalidRecoveryExclusionsAreSkippedWithoutBlockingOtherPins()
  {
    var store = new PinSnapshotStore(root); Guid good = Guid.NewGuid(), bad = Guid.NewGuid();
    var record = new PinSnapshotStore.Recovery(good, "Image", "", 0, 0, 640, 480);
    store.SaveRecovery(record, Png);
    store.SaveRecovery(record with { Id = bad }, Png);
    string window = Path.Combine(PinSnapshotStore.RootFor(root), bad.ToString("N"), "window.json");
    File.WriteAllText(window, System.Text.Json.JsonSerializer.Serialize(record with
      { Id = bad, ExcludeBoxes = [new(-1, 0, 10, 10)] }));
    Assert.Equal(good, Assert.Single(store.LoadRecovery()).Id);
  }

  [Fact]
  public void RecoveryRoundTripsContentAndPhysicalPositionAndSkipsBrokenRecords()
  {
    var store = new PinSnapshotStore(root);
    Guid good = Guid.NewGuid(), bad = Guid.NewGuid();
    var record = new PinSnapshotStore.Recovery(good, "Image", "", -1200, 45, 640, 480, true, true, true, 638, 478);
    store.SaveRecovery(record, Png);
    string badDir = Path.Combine(PinSnapshotStore.RootFor(root), bad.ToString("N"));
    Directory.CreateDirectory(badDir);
    File.WriteAllText(Path.Combine(badDir, "window.json"), "broken");
    Assert.Equal(record, Assert.Single(new PinSnapshotStore(root).LoadRecovery()));
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(store.GetSnapshotPngPath(good)!)!, "user.txt"), "keep");
    store.DeleteSnapshot(good);
    Assert.True(File.Exists(Path.Combine(PinSnapshotStore.RootFor(root), good.ToString("N"), "user.txt")));
    Assert.True(Directory.Exists(badDir));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  [InlineData(3)]
  [InlineData(4)]
  [InlineData(5)]
  public void InvalidDimensionsAndTruncatedStructuresDoNotBlockRecovery(int corruption)
  {
    byte[] damaged = [.. Png];
    if (corruption == 0) damaged[19] = 0;
    else if (corruption == 1) damaged[23] = 0;
    else if (corruption is 2 or 3)
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
        damaged.AsSpan(corruption == 2 ? 16 : 20, 4), 32769);
    else damaged = damaged[..(corruption == 4 ? 20 : 35)];
    Assert.False(PinSnapshotStore.TryValidatePng(damaged, out _, out _));
    var store = new PinSnapshotStore(root);
    Guid good = Guid.NewGuid(), bad = Guid.NewGuid();
    var record = new PinSnapshotStore.Recovery(good, "Image", "", 0, 0, 640, 480);
    store.SaveRecovery(record, Png);
    store.SaveRecovery(record with { Id = bad }, Png);
    File.WriteAllBytes(store.GetSnapshotPngPath(bad)!, damaged);
    Assert.Null(store.ReadSnapshotPng(bad));
    Assert.Equal(good, Assert.Single(store.LoadRecovery()).Id);
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(record with { Id = Guid.NewGuid() }, damaged));
  }


  [Theory]
  [InlineData("中")]
  [InlineData("😀")]
  [InlineData("\\\"\n")]
  public void LargeUnicodeRecoveryRoundTripsAtCharacterBudget(string unit)
  {
    var store = new PinSnapshotStore(root); Guid id = Guid.NewGuid();
    string content = string.Concat(Enumerable.Repeat(unit, PinSnapshotStore.MaximumRecoveryCharacters / unit.Length)) +
      new string('x', PinSnapshotStore.MaximumRecoveryCharacters % unit.Length);
    var record = new PinSnapshotStore.Recovery(id, "Text", content, 0, 0, 640, 480);
    store.SaveRecovery(record);
    Assert.Equal(content, Assert.Single(store.LoadRecovery()).Content);
    string path = Path.Combine(PinSnapshotStore.RootFor(root), id.ToString("N"), "window.json");
    byte[] committed = File.ReadAllBytes(path);
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(record with { Content = content + "x" }));
    Assert.Equal(committed, File.ReadAllBytes(path));
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(record with { Kind = new string('中', PinSnapshotStore.MaximumRecoveryBytes) }));
    Assert.Equal(committed, File.ReadAllBytes(path));
  }

  [Fact]
  public void RecoveryReadUsesTheSameExactSerializedByteBudget()
  {
    var store = new PinSnapshotStore(root); Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Text", "中文😀\\\"", 0, 0, 640, 480));
    string path = Path.Combine(PinSnapshotStore.RootFor(root), id.ToString("N"), "window.json");
    byte[] json = File.ReadAllBytes(path);
    byte[] boundary = new byte[PinSnapshotStore.MaximumRecoveryBytes];
    Array.Fill(boundary, (byte)' '); json.CopyTo(boundary, 0); File.WriteAllBytes(path, boundary);
    Assert.Single(store.LoadRecovery());
    File.WriteAllBytes(path, [.. boundary, (byte)' ']);
    Assert.Empty(store.LoadRecovery());
  }

}
