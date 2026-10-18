using System.Text.Json;
using System.Text.Json.Serialization;
using VibeOCR.App.Services;
using VibeOCR.App.Workbench;

namespace VibeOCR.App.Features.Pins;

/// <summary>
/// #238 截图历史存储（宿主 owned）：默认关闭；启用后记录成功截图会话
/// （含未钉图），同一 captureKey 一次成功只录一条。条数 200 / 总字节
/// 256MiB 双上限，超限最旧先淘汰；单条超界明确拒绝（不影响截图流程）。
/// 条目目录为 <c>&lt;DataRoot&gt;/screenshots/&lt;entryId&gt;</c>，索引
/// 经 AtomicFile 原子写；损坏条目加载时跳过（不阻止其它条目），删除/
/// 清空只触碰本应用 owned 目录清单内的固定子目录。
/// </summary>
internal sealed class PinHistoryStore : IPinHistoryRecorder
{
  public const int MaximumEntries = 200;
  public const long MaximumTotalBytes = 256L << 20;
  public const long MaximumEntryBytes = 64L << 20;
  private const string IndexFileName = "index.json";
  private const string ImageFileName = "image.png";

  private static readonly JsonSerializerOptions IndexJson = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
  };

  private sealed record IndexEntry(
    string Id,
    string Key,
    DateTimeOffset CreatedAt,
    int Width,
    int Height,
    long Bytes,
    string? RecognizedText = null);

  private sealed record IndexFile(bool Enabled, string? LastKey, IReadOnlyList<IndexEntry> Entries);

  private readonly object gate = new();
  private readonly string root;
  private readonly TimeProvider timeProvider;
  private bool enabled;
  private string? lastKey;
  private string? lastError;
  private List<IndexEntry> entries = [];

  internal PinHistoryStore(string dataRoot, TimeProvider? timeProvider = null)
  {
    root = Path.Combine(dataRoot, "screenshots");
    this.timeProvider = timeProvider ?? TimeProvider.System;
    Load();
  }

  internal static string RootFor(string dataRoot) => Path.Combine(dataRoot, "screenshots");

  /// <summary>加载索引：损坏的索引文件按空历史处理（不抛出）；单条损坏
  /// （缺图/尺寸不符）跳过该条且不阻止其它条目。</summary>
  private void Load()
  {
    try
    {
      string path = Path.Combine(root, IndexFileName);
      if (!File.Exists(path))
      {
        return;
      }
      PinSnapshotStore.EnsureOwnedDirectory(root);
      if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new JsonException();
      using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
      IndexFile index = JsonSerializer.Deserialize<IndexFile>(
        document.RootElement.GetRawText(), IndexJson) ??
        throw new JsonException();
      List<IndexEntry> valid = [];
      foreach (IndexEntry entry in index.Entries ?? [])
      {
        try
        {
          if (entry is null) continue;
          if (!Guid.TryParseExact(entry.Id, "N", out _) || entry.Bytes is < 1 or > MaximumEntryBytes ||
            entry.Width < 1 || entry.Height < 1 || valid.Any(item => item.Id == entry.Id)) continue;
          PinSnapshotStore.EnsureOwnedDirectory(Path.Combine(root, entry.Id));
          if (!File.Exists(ImagePath(entry.Id)) || new FileInfo(ImagePath(entry.Id)).Length != entry.Bytes ||
            !PinSnapshotStore.TryValidatePng(File.ReadAllBytes(ImagePath(entry.Id)), out int width, out int height) ||
            width != entry.Width || height != entry.Height) continue;
          valid.Add(entry);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
      }
      enabled = index.Enabled;
      lastKey = index.LastKey;
      entries = valid.OrderBy(entry => entry.CreatedAt).ToList();
      while (entries.Count > MaximumEntries || entries.Sum(entry => entry.Bytes) > MaximumTotalBytes)
        entries.RemoveAt(0);
    }
    catch (Exception error) when (
      error is JsonException or IOException or UnauthorizedAccessException)
    {
      // 索引损坏：从空历史恢复（保留条目目录，用户可清理），不阻止应用。
      enabled = false;
      lastKey = null;
      entries = [];
    }
  }

  private void SaveIndex()
  {
    Directory.CreateDirectory(root);
    PinSnapshotStore.EnsureOwnedDirectory(root);
    AtomicFile.WriteAllText(
      Path.Combine(root, IndexFileName),
      JsonSerializer.Serialize(
        new IndexFile(enabled, lastKey, entries), IndexJson));
  }

  private string ImagePath(string entryId) => Path.Combine(root, entryId, ImageFileName);

  public bool IsEnabled
  {
    get { lock (gate) return enabled; }
  }

  public void SetEnabled(bool value)
  {
    lock (gate)
    {
      if (enabled == value)
      {
        return;
      }
      bool previous = enabled;
      enabled = value;
      try { SaveIndex(); lastError = null; }
      catch { enabled = previous; throw; }
    }
  }

  public void RecordSuccessfulCapture(string captureKey, byte[] png)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(captureKey);
    ArgumentNullException.ThrowIfNull(png);
    if (png.Length < 1 || png.Length > MaximumEntryBytes)
    {
      lock (gate) lastError = "截图未录入历史：单图必须在 64MiB 以内。";
      return;
    }
    lock (gate)
    {
      if (!enabled || entries.Any(entry => entry.Key == captureKey))
      {
        return;
      }
      if (!PinSnapshotStore.TryValidatePng(png, out int width, out int height))
      {
        lastError = "截图未录入历史：PNG 内容或尺寸无效。";
        return;
      }
      List<IndexEntry> previous = entries;
      string? previousKey = lastKey;
      string id = Guid.NewGuid().ToString("N");
      List<IndexEntry> next = [.. previous, new IndexEntry(id, captureKey,
        timeProvider.GetUtcNow().ToLocalTime(), width, height, png.LongLength)];
      List<string> evicted = [];
      while (next.Count > MaximumEntries || next.Sum(entry => entry.Bytes) > MaximumTotalBytes)
      {
        evicted.Add(next[0].Id);
        next.RemoveAt(0);
      }
      try
      {
        Directory.CreateDirectory(root);
        PinSnapshotStore.EnsureOwnedDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, id));
        PinSnapshotStore.EnsureOwnedDirectory(Path.Combine(root, id));
        AtomicFile.Write(ImagePath(id), stream => stream.Write(png));
        entries = next;
        lastKey = captureKey;
        SaveIndex();
        lastError = null;
      }
      catch (Exception error) when (error is IOException or UnauthorizedAccessException)
      {
        entries = previous;
        lastKey = previousKey;
        lastError = $"截图已成功，但历史保存失败：{error.Message}";
        DeleteEntryDirectory(id);
        return;
      }
      foreach (string oldId in evicted) DeleteEntryDirectory(oldId);
    }
  }

  public void ReportRecordingFailure(string message)
  {
    lock (gate) lastError = message;
  }

  public void EnrichRecognizedText(string captureKey, string text)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(captureKey);
    if (string.IsNullOrWhiteSpace(text) || text.Length > 16_384)
    {
      return;
    }
    lock (gate)
    {
      IndexEntry? target = entries.FirstOrDefault(
        entry => entry.Key == captureKey && string.IsNullOrEmpty(entry.RecognizedText));
      if (target is null)
      {
        return;
      }
      int index = entries.IndexOf(target);
      entries[index] = target with { RecognizedText = text };
      try { SaveIndex(); }
      catch { entries[index] = target; throw; }
    }
  }

  public PinHistoryState Projection(string? query)
  {
    lock (gate)
    {
      IEnumerable<IndexEntry> filtered = entries.OrderByDescending(entry => entry.CreatedAt);
      if (!string.IsNullOrWhiteSpace(query))
      {
        string needle = query.Trim();
        filtered = filtered.Where(entry =>
          EntryName(entry.CreatedAt).Contains(needle, StringComparison.OrdinalIgnoreCase) ||
          (entry.RecognizedText?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
      }
      // Keep the 200-entry bridge projection below 64KiB, including escaped CJK.
      IEnumerable<PinHistoryEntryState> list = filtered.Select(entry => new PinHistoryEntryState(
        entry.Id, entry.CreatedAt, entry.Width, entry.Height, entry.Bytes,
        entry.RecognizedText is { } text ? text[..Math.Min(12, text.Length)] : null));
      return new PinHistoryState(
        enabled,
        entries.Count,
        entries.Sum(entry => entry.Bytes),
        MaximumEntries,
        MaximumTotalBytes,
        [.. list], lastError);
    }
  }

  /// <summary>条目显示名：固定本地时间格式（无用户命名面）。</summary>
  internal static string EntryName(DateTimeOffset createdAt) =>
    createdAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

  public PinHistoryImage? TryReadImage(string entryId)
  {
    IndexEntry? entry;
    lock (gate)
    {
      entry = entries.FirstOrDefault(item => item.Id == entryId);
      if (entry is null || !Guid.TryParseExact(entryId, "N", out _))
      {
        return null;
      }
    }
    try
    {
      PinSnapshotStore.EnsureOwnedDirectory(root);
      PinSnapshotStore.EnsureOwnedDirectory(Path.Combine(root, entryId));
      if (new FileInfo(ImagePath(entryId)).Length != entry.Bytes) return null;
      byte[] png = File.ReadAllBytes(ImagePath(entryId));
      if (png.Length < 1 || png.Length > MaximumEntryBytes)
      {
        return null;
      }
      if (!PinSnapshotStore.TryValidatePng(png, out int width, out int height) ||
        width != entry.Width || height != entry.Height) return null;
      return new PinHistoryImage(png, width, height);
    }
    catch (Exception error) when (
      error is IOException or UnauthorizedAccessException)
    {
      return null;
    }
  }

  public bool DeleteEntry(string entryId)
  {
    if (!Guid.TryParseExact(entryId, "N", out _))
    {
      return false;
    }
    lock (gate)
    {
      List<IndexEntry> previous = entries;
      entries = entries.Where(entry => entry.Id != entryId).ToList();
      int removed = previous.Count - entries.Count;
      if (removed == 0)
      {
        return false;
      }
      try { SaveIndex(); }
      catch { entries = previous; throw; }
      DeleteEntryDirectory(entryId);
      return true;
    }
  }

  public int ClearAll()
  {
    lock (gate)
    {
      List<string> ids = [.. entries.Select(entry => entry.Id)];
      List<IndexEntry> previous = entries;
      entries = [];
      try { SaveIndex(); }
      catch { entries = previous; throw; }
      foreach (string id in ids) DeleteEntryDirectory(id);
      return ids.Count;
    }
  }

  /// <summary>仅删除 owned 根下 Guid(N) 点状子目录；任何其它路径不触碰。</summary>
  private void DeleteEntryDirectory(string entryId)
  {
    if (!Guid.TryParseExact(entryId, "N", out _))
    {
      return;
    }
    if (Directory.Exists(root)) PinSnapshotStore.EnsureOwnedDirectory(root);
    string directory = Path.Combine(root, entryId);
    if (!Directory.Exists(directory))
    {
      return;
    }
    PinSnapshotStore.EnsureOwnedDirectory(directory);
    try { File.Delete(Path.Combine(directory, ImageFileName)); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
    try { Directory.Delete(directory); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
  }
}
