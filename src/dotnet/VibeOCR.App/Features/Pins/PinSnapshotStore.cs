using System.Buffers.Binary;
using System.Text.Json;
using VibeOCR.App.Services;
using VibeOCR.App.Workbench;

namespace VibeOCR.App.Features.Pins;

/// <summary>
/// #238 pin owned 快照存储：每张贴图一个独立固定子目录
/// <c>&lt;DataRoot&gt;/pins/&lt;pinId&gt;</c>，内含编辑工程的 .vibp 与最终
/// 合成 PNG。两份文件都经既有原子写入能力落盘；目录名严格为 Guid(N)，
/// 清理只删除本应用拥有的该子目录，绝不递归触碰其他路径。
/// </summary>
internal sealed class PinSnapshotStore(string dataRoot) : IPinSnapshotWriter
{
  internal sealed record Recovery(Guid Id, string Kind, string Content, int X, int Y,
    int Width, int Height, bool Collapsed = false, bool PassThrough = false, bool Grouped = false);
  private sealed record Snapshot(string Generation);
  private const string PointerFileName = "current.json";
  private const string RecoveryFileName = "window.json";

  private readonly string root = Path.Combine(dataRoot, "pins");

  internal static string RootFor(string dataRoot) => Path.Combine(dataRoot, "pins");

  private string DirectoryFor(Guid pinId) => Path.Combine(root, pinId.ToString("N"));

  public async Task WriteSnapshotAsync(
    Guid pinId,
    VibpProjectManifest manifest,
    IReadOnlyList<VibpProjectEntry> entries,
    byte[] finalPng,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(finalPng);
    if (finalPng.Length > PinHistoryStore.MaximumEntryBytes || SniffPngSize(finalPng) == (0, 0))
      throw new InvalidOperationException("Pin snapshot PNG is invalid or exceeds the byte budget.");
    Directory.CreateDirectory(root);
    EnsureOwnedDirectory(root);
    string directory = DirectoryFor(pinId);
    Directory.CreateDirectory(directory);
    EnsureOwnedDirectory(directory);
    string generation = Guid.NewGuid().ToString("N");
    string project = Path.Combine(directory, generation + ".vibp");
    string image = Path.Combine(directory, generation + ".png");
    try
    {
      await VibpProjectFile.WriteAsync(project, manifest, entries, cancellationToken);
      AtomicFile.Write(image, stream => stream.Write(finalPng), cancellationToken);
      // The pointer is the sole commit: readers see the old pair or the new pair.
      byte[] pointer = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(generation));
      AtomicFile.Write(Path.Combine(directory, PointerFileName), stream => stream.Write(pointer), cancellationToken);
    }
    catch
    {
      TryDelete(project);
      TryDelete(image);
      throw;
    }
    try
    {
      foreach (string path in Directory.EnumerateFiles(directory))
      {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name != generation && Guid.TryParseExact(name, "N", out _) &&
          Path.GetExtension(path) is ".vibp" or ".png") TryDelete(path);
      }
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    { AppLog.Warn($"Pin snapshot committed; old generation cleanup failed: {error.Message}"); }
  }

  private string? SnapshotPath(Guid pinId, string extension)
  {
    if (Directory.Exists(root)) EnsureOwnedDirectory(root);
    string directory = DirectoryFor(pinId);
    if (!Directory.Exists(directory)) return null;
    EnsureOwnedDirectory(directory);
    string pointer = Path.Combine(directory, PointerFileName);
    if (!File.Exists(pointer)) return null;
    Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(pointer));
    if (snapshot is null || !Guid.TryParseExact(snapshot.Generation, "N", out _)) return null;
    string path = Path.Combine(directory, snapshot.Generation + extension);
    return File.Exists(path) ? path : null;
  }

  public string? GetSnapshotPngPath(Guid pinId) => SnapshotPath(pinId, ".png") ??
    (File.Exists(Path.Combine(DirectoryFor(pinId), "image.png"))
      ? Path.Combine(DirectoryFor(pinId), "image.png") : null);

  public string? GetSnapshotProjectPath(Guid pinId) => SnapshotPath(pinId, ".vibp");

  internal void SaveRecovery(Recovery record, byte[]? png = null)
  {
    if (record.Width < 1 || record.Height < 1 || record.Content.Length > 256 * 1024) throw new InvalidOperationException("Invalid pin bounds.");
    Directory.CreateDirectory(root);
    EnsureOwnedDirectory(root);
    string directory = DirectoryFor(record.Id);
    Directory.CreateDirectory(directory);
    EnsureOwnedDirectory(directory);
    if (png is not null)
    {
      if (png.Length > PinHistoryStore.MaximumEntryBytes || SniffPngSize(png) == (0, 0))
        throw new InvalidOperationException("Invalid pin image.");
      AtomicFile.Write(Path.Combine(directory, "image.png"), stream => stream.Write(png));
    }
    AtomicFile.WriteAllText(Path.Combine(directory, RecoveryFileName), JsonSerializer.Serialize(record));
  }

  internal IReadOnlyList<Recovery> LoadRecovery()
  {
    List<Recovery> result = [];
    if (!Directory.Exists(root)) return result;
    EnsureOwnedDirectory(root);
    foreach (string directory in Directory.EnumerateDirectories(root))
    {
      if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)) continue;
      try
      {
        EnsureOwnedDirectory(directory);
        string path = Path.Combine(directory, RecoveryFileName);
        if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024) continue;
        Recovery? record = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(path));
        if (record is not null && record.Id == id && record.Width is > 0 and <= 32768 &&
          record.Height is > 0 and <= 32768 && record.Content is { Length: <= 262144 } &&
          record.Kind is "Image" or "Text" or "Color" or "Formula" or "FileText") result.Add(record);
      }
      catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
      { AppLog.Warn($"Skipped damaged pin recovery: {id}: {error.Message}"); }
      if (result.Count == 4) break;
    }
    return result;
  }

  internal static void EnsureOwnedDirectory(string directory)
  {
    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
      throw new IOException("Pin owned directory crosses a reparse point.");
  }

  private static void TryDelete(string path)
  {
    try { File.Delete(path); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
  }

  public void DeleteSnapshot(Guid pinId)
  {
    if (Directory.Exists(root)) EnsureOwnedDirectory(root);
    string directory = DirectoryFor(pinId);
    if (!Directory.Exists(directory)) return;
    EnsureOwnedDirectory(directory);
    foreach (string path in Directory.EnumerateFiles(directory))
    {
      string name = Path.GetFileName(path);
      if (name is PointerFileName or RecoveryFileName or "image.png" ||
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) &&
        Path.GetExtension(path) is ".png" or ".vibp") TryDelete(path);
    }
    try { Directory.Delete(directory); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
  }

  /// <summary>PNG 头级尺寸探针（IHDR 宽高）；非 PNG/损坏返回 (0,0)。</summary>
  internal static (int Width, int Height) SniffPngSize(ReadOnlySpan<byte> png)
  {
    ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    if (png.Length < 24 || !png[..8].SequenceEqual(signature))
    {
      return (0, 0);
    }
    uint width = BinaryPrimitives.ReadUInt32BigEndian(png[16..20]);
    uint height = BinaryPrimitives.ReadUInt32BigEndian(png[20..24]);
    if (width is 0 or > 32_768 || height is 0 or > 32_768)
    {
      return (0, 0);
    }
    return ((int)width, (int)height);
  }
}
