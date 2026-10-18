using VibeOCR.App.Features.Pins;
using VibeOCR.App.Workbench;
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

  public void Dispose()
  {
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
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
  public void RecoveryRoundTripsContentAndPhysicalPositionAndSkipsBrokenRecords()
  {
    var store = new PinSnapshotStore(root);
    Guid good = Guid.NewGuid(), bad = Guid.NewGuid();
    var record = new PinSnapshotStore.Recovery(good, "Image", "", -1200, 45, 640, 480, true, true, true);
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

}
