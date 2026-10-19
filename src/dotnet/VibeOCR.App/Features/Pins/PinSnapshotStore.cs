using System.Buffers.Binary;
using System.Text.Json;
using VibeOCR.App.Services;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Windows.Graphics;

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
    int Width, int Height, bool Collapsed = false, bool PassThrough = false, bool Grouped = false,
    int? ExpandedClientWidth = null, int? ExpandedClientHeight = null,
    IReadOnlyList<WorkbenchExclusionBox>? ExcludeBoxes = null);
  private sealed record Snapshot(string Generation, IReadOnlyList<WorkbenchExclusionBox>? ExcludeBoxes = null);
  internal const int MaximumRecoveryBytes = 2 * 1024 * 1024;
  internal const int MaximumRecoveryCharacters = 262144;

  /// <summary>当前虚拟桌面的全部工作区：复用 Platform 既有 EnumerateMonitors
  /// 枚举物理边界，逐台经 DisplayArea.GetFromPoint(Nearest) 取 WorkArea（与
  /// ImageEditWindow/MainWindow 相同的单对象投影路径，不用 FindAll 向量投影）。</summary>
  internal static List<RectInt32> CurrentWorkAreas()
  {
    List<RectInt32> areas = [];
    foreach (VibeOCR.Platform.Windows.ScreenMonitorTarget monitor in
      VibeOCR.Platform.Windows.ScreenTargetQuery.EnumerateMonitors())
    {
      Windows.Graphics.PointInt32 center = new(
        monitor.Bounds.X + Math.Max(1, monitor.Bounds.Width / 2),
        monitor.Bounds.Y + Math.Max(1, monitor.Bounds.Height / 2));
      RectInt32 work = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
        center, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
      if (!areas.Contains(work)) areas.Add(work);
    }
    return areas;
  }

  /// <summary>恢复位置约束：顶部拖动区（band，高 bandHeight）与任一当前工作区
  /// 保持至少 minimumVisible 的可拖交集时原样保留（合法负坐标/跨屏不乱挪；
  /// 仅正文相交不算可拖）；否则就近吸附到该工作区左上角偏移 16px，尺寸不变。
  /// 几何端点/差值先提升 long，避免边界值 int 溢出；距离比较有界安全，不求平方。</summary>
  internal static RectInt32 ConstrainRestoredGeometry(
    RectInt32 saved, IReadOnlyList<RectInt32> workAreas, int bandHeight, int minimumVisible)
  {
    if (workAreas.Count == 0) return saved;
    foreach (RectInt32 area in workAreas)
    {
      long bandOverlapWidth = Math.Min((long)saved.X + saved.Width, (long)area.X + area.Width) -
        Math.Max(saved.X, area.X);
      long bandOverlapHeight = Math.Min((long)saved.Y + bandHeight, (long)area.Y + area.Height) -
        Math.Max(saved.Y, area.Y);
      if (bandOverlapWidth >= minimumVisible && bandOverlapHeight >= minimumVisible) return saved;
    }
    RectInt32 nearest = workAreas[0];
    (long Spread, long Sum) best = (long.MaxValue, long.MaxValue);
    foreach (RectInt32 area in workAreas)
    {
      long dx = Math.Max(0, saved.X >= area.X
        ? (long)saved.X - area.X - area.Width
        : (long)area.X - saved.X - saved.Width);
      long dy = Math.Max(0, saved.Y >= area.Y
        ? (long)saved.Y - area.Y - area.Height
        : (long)area.Y - saved.Y - saved.Height);
      (long, long) current = (Math.Max(dx, dy), dx + dy);
      if (current.CompareTo(best) < 0)
      {
        best = current;
        nearest = area;
      }
    }
    return new RectInt32(
      (int)Math.Clamp((long)nearest.X + 16, int.MinValue, int.MaxValue),
      (int)Math.Clamp((long)nearest.Y + 16, int.MinValue, int.MaxValue),
      saved.Width, saved.Height);
  }
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
    CancellationToken cancellationToken,
    IReadOnlyList<WorkbenchExclusionBox>? excludeBoxes = null)
  {
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(finalPng);
    await ValidatePngAsync(finalPng, cancellationToken);
    WorkbenchExclusionBox.ValidateCollection(excludeBoxes ?? []);
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
      byte[] pointer = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(generation, excludeBoxes ?? []));
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
    Snapshot? snapshot = ReadSnapshot(pinId);
    if (snapshot is null || !Guid.TryParseExact(snapshot.Generation, "N", out _)) return null;
    string path = Path.Combine(directory, snapshot.Generation + extension);
    return File.Exists(path) ? path : null;
  }

  public string? GetSnapshotPngPath(Guid pinId) => SnapshotPath(pinId, ".png") ??
    (File.Exists(Path.Combine(DirectoryFor(pinId), "image.png"))
      ? Path.Combine(DirectoryFor(pinId), "image.png") : null);

  public string? GetSnapshotProjectPath(Guid pinId) => SnapshotPath(pinId, ".vibp");

  private Snapshot? ReadSnapshot(Guid pinId)
  {
    string path = Path.Combine(DirectoryFor(pinId), PointerFileName);
    if (!File.Exists(path)) return null;
    EnsureOwnedDirectory(root);
    EnsureOwnedDirectory(DirectoryFor(pinId));
    if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Invalid pin metadata.");
    Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
    WorkbenchExclusionBox.ValidateCollection(snapshot?.ExcludeBoxes ?? []);
    return snapshot;
  }

  internal IReadOnlyList<WorkbenchExclusionBox>? GetSnapshotExclusions(Guid pinId) => ReadSnapshot(pinId)?.ExcludeBoxes;

  internal PinHistoryImage? ReadSnapshotPng(Guid pinId)
  {
    string? path = GetSnapshotPngPath(pinId);
    if (path is null || new FileInfo(path).Length > PinHistoryStore.MaximumEntryBytes) return null;
    byte[] png = File.ReadAllBytes(path);
    return TryValidatePng(png, out int width, out int height) ? new(png, width, height) : null;
  }

  internal static async Task ValidatePngAsync(byte[] png, CancellationToken cancellationToken)
  {
    if (png.LongLength is < 1 or > PinHistoryStore.MaximumEntryBytes)
      throw new InvalidDataException("Pin PNG exceeds the byte budget.");
    using MemoryStream structure = new(png, writable: false);
    try
    {
      await WorkbenchAnnotationStore.ValidatePngStructureCoreAsync(structure, cancellationToken);
    }
    catch (WorkbenchAnnotationAccessException error)
    {
      throw new InvalidDataException("Invalid pin PNG structure.", error);
    }
    await InputService.ValidateImageAsync(png, cancellationToken);
  }

  internal static bool TryValidatePng(byte[] png, out int width, out int height)
  {
    width = height = 0;
    try
    {
      // Stores have a synchronous API. Decode on MTA so a UI caller never blocks its own continuation.
      Task.Run(() => ValidatePngAsync(png, CancellationToken.None)).GetAwaiter().GetResult();
      (width, height) = SniffPngSize(png);
      return width > 0 && height > 0;
    }
    catch (InvalidDataException) { return false; }
  }


  internal void SaveRecovery(Recovery record, byte[]? png = null)
  {
    if (record.Width < 1 || record.Height < 1 || record.Content.Length > MaximumRecoveryCharacters) throw new InvalidOperationException("Invalid pin bounds.");
    WorkbenchExclusionBox.ValidateCollection(record.ExcludeBoxes ?? []);
    byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(record);
    if (serialized.Length > MaximumRecoveryBytes) throw new InvalidOperationException("Pin recovery exceeds the serialized byte budget.");
    Directory.CreateDirectory(root);
    EnsureOwnedDirectory(root);
    string directory = DirectoryFor(record.Id);
    Directory.CreateDirectory(directory);
    EnsureOwnedDirectory(directory);
    if (png is not null)
    {
      if (!TryValidatePng(png, out _, out _))
        throw new InvalidOperationException("Invalid pin image.");
      AtomicFile.Write(Path.Combine(directory, "image.png"), stream => stream.Write(png));
    }
    AtomicFile.Write(Path.Combine(directory, RecoveryFileName), stream => stream.Write(serialized));
  }

  internal IReadOnlyList<Recovery> LoadRecovery()
  {
    List<Recovery> result = [];
    // 根检查/枚举的存储异常（ACL、瞬时 I/O、reparse 防护）不从 Loaded 启动
    // 路径逸出：降级为空/部分恢复集，仅跳过不可恢复的贴图。
    try
    {
      // 测试接缝优先于存在性检查（真实根不可读场景的启动链注入）。
      if (RecoveryDirectoryEnumeration is null)
      {
        if (!Directory.Exists(root)) return result;
        EnsureOwnedDirectory(root);
      }
      foreach (string directory in (RecoveryDirectoryEnumeration ?? Directory.EnumerateDirectories)(root))
      {
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)) continue;
        try
        {
          EnsureOwnedDirectory(directory);
          string path = Path.Combine(directory, RecoveryFileName);
          if (!File.Exists(path) || new FileInfo(path).Length > MaximumRecoveryBytes) continue;
          Recovery? record = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(path));
          if (record is not null) WorkbenchExclusionBox.ValidateCollection(record.ExcludeBoxes ?? []);
          if (record?.Kind == "Image" && ReadSnapshotPng(id) is null) continue;
          if (record is not null && record.Id == id && record.Width is > 0 and <= 32768 &&
            record.Height is > 0 and <= 32768 && record.Content is { Length: <= MaximumRecoveryCharacters } &&
            record.Kind is "Image" or "Text" or "Color" or "Formula" or "FileText") result.Add(record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { AppLog.Warn($"Skipped damaged pin recovery: {id}: {error.Message}"); }
        if (result.Count == 4) break;
      }
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
    { AppLog.Warn($"Pin recovery root unavailable: {error.Message}"); }
    return result;
  }

  /// <summary>测试接缝：恢复根目录枚举注入（存储异常降级回归）；生产为 null。</summary>
  internal Func<string, IEnumerable<string>>? RecoveryDirectoryEnumeration { get; set; }

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
