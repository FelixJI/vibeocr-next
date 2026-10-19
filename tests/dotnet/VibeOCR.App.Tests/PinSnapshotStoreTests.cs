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
  public async Task StartupRetriesObsoleteGenerationWhileThePinStaysOpen(string extension)
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Image", "", 0, 0, 640, 480), Png);
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
    string old = (extension == ".png" ? store.GetSnapshotPngPath(id) : store.GetSnapshotProjectPath(id))!;
    using (var locked = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
      await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
      Assert.True(File.Exists(old));
      var whileLocked = new PinSnapshotStore(root);
      Assert.Single(whileLocked.LoadRecovery());
      await whileLocked.CleanupRecoveryAsync();
    }
    string current = store.GetSnapshotPngPath(id)!;
    string directory = Path.GetDirectoryName(current)!;
    string unknown = Path.Combine(directory, "user-note.txt");
    File.WriteAllText(unknown, "keep");
    string nested = Path.Combine(directory, "user-folder");
    Directory.CreateDirectory(nested);
    File.WriteAllBytes(Path.Combine(nested, Guid.NewGuid().ToString("N") + ".png"), Png);
    var restarted = new PinSnapshotStore(root);
    Assert.Single(restarted.LoadRecovery());
    await restarted.CleanupRecoveryAsync();
    Assert.False(File.Exists(old));
    Assert.Equal(Png, File.ReadAllBytes(current));
    Assert.True(File.Exists(store.GetSnapshotProjectPath(id)));
    Assert.True(File.Exists(Path.Combine(directory, "image.png")));
    Assert.True(File.Exists(unknown));
    Assert.Single(Directory.EnumerateFiles(nested));
  }

  [Fact]
  public async Task CleanupTreatsUppercaseCurrentGenerationAsTheSameOwnedPair()
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Image", "", 0, 0, 640, 480), Png);
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
    string current = store.GetSnapshotPngPath(id)!;
    string directory = Path.GetDirectoryName(current)!;
    string obsolete = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
    File.WriteAllBytes(obsolete, Png);
    File.WriteAllText(Path.Combine(directory, "current.json"),
      System.Text.Json.JsonSerializer.Serialize(new { Generation = Path.GetFileNameWithoutExtension(current).ToUpperInvariant() }));
    var restarted = new PinSnapshotStore(root);
    Assert.Single(restarted.LoadRecovery());
    await restarted.CleanupRecoveryAsync();
    Assert.True(File.Exists(current));
    Assert.True(File.Exists(store.GetSnapshotProjectPath(id)));
    Assert.False(File.Exists(obsolete));
  }

  [Fact]
  public async Task LoadRecoveryDoesNotWaitForObsoleteGenerationValidation()
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Text", "restored-first", 0, 0, 640, 480));
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
    string directory = Path.GetDirectoryName(store.GetSnapshotPngPath(id))!;
    File.WriteAllBytes(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png"), Png);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    store.BeforeCleanupValidation = () => { entered.TrySetResult(); return released.Task; };
    Task<IReadOnlyList<PinSnapshotStore.Recovery>> loading = Task.Run(store.LoadRecovery);
    bool returned;
    try
    {
      await Task.WhenAny(loading, entered.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      returned = loading.IsCompleted;
    }
    finally { released.TrySetResult(); await loading; }
    Assert.True(returned, "Loading recovery waited for full obsolete-generation validation.");
    Assert.Equal("restored-first", Assert.Single(await loading).Content);
  }

  [Fact]
  public async Task BackgroundCleanupIsSingleFlightAndCannotRaceANewSnapshotWrite()
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Image", "", 0, 0, 640, 480), Png);
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
    string old = store.GetSnapshotPngPath(id)!;
    string obsolete = Path.Combine(Path.GetDirectoryName(old)!, Guid.NewGuid().ToString("N") + ".png");
    File.WriteAllBytes(obsolete, Png);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    store.BeforeCleanupValidation = () => { entered.TrySetResult(); return released.Task; };
    Task cleanup = store.CleanupRecoveryAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    Assert.Same(cleanup, store.CleanupRecoveryAsync());
    using var canceled = new CancellationTokenSource();
    Task canceledSave = store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, canceled.Token);
    canceled.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledSave);
    Task save = store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
    try
    {
      Assert.False(save.IsCompleted);
      Assert.Equal(old, store.GetSnapshotPngPath(id));
    }
    finally { released.TrySetResult(); }
    await cleanup;
    await save;
    Assert.NotEqual(old, store.GetSnapshotPngPath(id));
    Assert.Equal(Png, File.ReadAllBytes(store.GetSnapshotPngPath(id)!));
    await VibpProjectFile.ValidateArchiveAsync(store.GetSnapshotProjectPath(id)!, TestContext.Current.CancellationToken);
    Assert.False(File.Exists(old));
    Assert.False(File.Exists(obsolete));
  }

  [Fact]
  public void LegacyFormulaRecoveryCanBeSavedUnchangedOrProgressivelyShortened()
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    string directory = Path.Combine(PinSnapshotStore.RootFor(root), id.ToString("N"));
    Directory.CreateDirectory(directory);
    string legacy = new('中', 4096);
    var original = new PinSnapshotStore.Recovery(id, "Formula", legacy, 0, 0, 640, 400);
    File.WriteAllText(Path.Combine(directory, "window.json"), System.Text.Json.JsonSerializer.Serialize(original));
    var moved = original with { X = 200, Y = 300 };
    store.SaveRecovery(moved);
    Assert.Equal(moved, Assert.Single(store.LoadRecovery()));
    var shortened = moved with { Content = legacy[..^1], X = 250 };
    store.SaveRecovery(shortened);
    Assert.Equal(shortened, Assert.Single(store.LoadRecovery()));
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(moved));
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(shortened with { Content = new string('文', 4095) }));
    Assert.Equal(shortened, Assert.Single(store.LoadRecovery()));
    store.SaveRecovery(shortened with { Content = "x+1" });
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(shortened));
  }

  [Theory]
  [InlineData("中", 1366)]
  [InlineData("😀", 1025)]
  [InlineData("x", 4097)]
  public void NewFormulaRecoveryRefusesSourcesBeyondTheRenderByteBudget(string unit, int count)
  {
    var store = new PinSnapshotStore(root);
    string source = string.Concat(Enumerable.Repeat(unit, count));
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(new(Guid.NewGuid(), "Formula", source, 0, 0, 640, 400)));
  }

  [Theory]
  [InlineData("wrong-id")]
  [InlineData("wrong-kind")]
  [InlineData("bad-bounds")]
  [InlineData("bad-json")]
  public void LegacyFormulaExceptionRequiresAValidExistingFormulaRecord(string invalid)
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    string directory = Path.Combine(PinSnapshotStore.RootFor(root), id.ToString("N"));
    Directory.CreateDirectory(directory);
    var candidate = new PinSnapshotStore.Recovery(id, "Formula", new string('中', 4096), 0, 0, 640, 400);
    var previous = invalid switch
    {
      "wrong-id" => candidate with { Id = Guid.NewGuid() },
      "wrong-kind" => candidate with { Kind = "Text" },
      "bad-bounds" => candidate with { Width = 0 },
      _ => candidate,
    };
    File.WriteAllText(Path.Combine(directory, "window.json"), invalid == "bad-json" ? "{" : System.Text.Json.JsonSerializer.Serialize(previous));
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(candidate));
  }

  [Theory]
  [InlineData("pointer")]
  [InlineData("png")]
  [InlineData("project")]
  public async Task DamagedCurrentPairDoesNotDeletePreviousGenerationsOrFallback(string damaged)
  {
    var store = new PinSnapshotStore(root);
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Image", "", 0, 0, 640, 480), Png);
    await store.WriteSnapshotAsync(id, Manifest(), Entries(), Png, TestContext.Current.CancellationToken);
    string current = store.GetSnapshotPngPath(id)!;
    string directory = Path.GetDirectoryName(current)!;
    string old = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
    File.WriteAllBytes(old, Png);
    string target = damaged switch { "pointer" => Path.Combine(directory, "current.json"), "project" => store.GetSnapshotProjectPath(id)!, _ => current };
    File.WriteAllText(target, "corrupt");
    var restarted = new PinSnapshotStore(root);
    _ = restarted.LoadRecovery();
    await restarted.CleanupRecoveryAsync();
    Assert.Equal(Png, File.ReadAllBytes(old));
    Assert.Equal(Png, File.ReadAllBytes(Path.Combine(directory, "image.png")));
  }

  [Fact]
  public void FormulaRecoveryKeepsUtf8BoundaryAndLoadsLegacyOversizeWithoutDroppingText()
  {
    var store = new PinSnapshotStore(root);
    string exact = string.Concat(Enumerable.Repeat("😀", 1024));
    Guid id = Guid.NewGuid();
    store.SaveRecovery(new(id, "Formula", exact, 0, 0, 640, 400));
    Assert.Equal(exact, Assert.Single(store.LoadRecovery()).Content);
    string legacy = new('中', 4096);
    string path = Path.Combine(PinSnapshotStore.RootFor(root), id.ToString("N"), "window.json");
    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new PinSnapshotStore.Recovery(id, "Formula", legacy, 0, 0, 640, 400)));
    Assert.Equal(legacy, Assert.Single(new PinSnapshotStore(root).LoadRecovery()).Content);
    Assert.Throws<InvalidOperationException>(() => store.SaveRecovery(new(id, "Formula", "\ud800", 0, 0, 640, 400)));
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
    var restarted = new PinSnapshotStore(root);
    Assert.Empty(restarted.LoadRecovery());
    await restarted.CleanupRecoveryAsync();
    Assert.False(File.Exists(image));
    Assert.False(Directory.Exists(Path.GetDirectoryName(image)));
  }

  [Fact]
  public async Task StartupRetriesCleanupEvenAfterFourWindowsHaveBeenRecovered()
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
    await store.CleanupRecoveryAsync();
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
