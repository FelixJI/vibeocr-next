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

  private sealed record IndexFile(bool Enabled, string? LastKey, IReadOnlyList<IndexEntry> Entries,
    IReadOnlyList<string>? PendingCleanup = null);

  private readonly object gate = new();
  private readonly string root;
  private readonly TimeProvider timeProvider;
  private bool enabled;
  private string? lastKey;
  private string? lastError;
  private List<IndexEntry> entries = [];
  private readonly HashSet<string> verified = [];
  // 删除前将归属保留在索引中，文件占用或进程退出后仍可重试。
  private readonly HashSet<string> pendingCleanup = [];
  internal Task Initialization { get; }

  internal PinHistoryStore(string dataRoot, TimeProvider? timeProvider = null, Func<byte[], Task<bool>>? validateLoadedImage = null)
  {
    root = Path.Combine(dataRoot, "screenshots");
    this.timeProvider = timeProvider ?? TimeProvider.System;
    LoadMetadata();
    IndexEntry[] candidates = [.. entries];
    Initialization = candidates.Length == 0 ? Task.CompletedTask :
      Task.Run(() => ValidateLoadedAsync(candidates, validateLoadedImage));
  }

  internal static string RootFor(string dataRoot) => Path.Combine(dataRoot, "screenshots");

  /// <summary>加载索引：损坏的索引文件按空历史处理（不抛出）；单条损坏
  /// （缺图/尺寸不符）跳过该条且不阻止其它条目。</summary>
  private void LoadMetadata()
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
      List<string> dropped = [];
      foreach (IndexEntry entry in index.Entries ?? [])
      {
        try
        {
          if (entry is null) continue;
          if (!Guid.TryParseExact(entry.Id, "N", out _)) continue;
          if (entry.Bytes is < 1 or > MaximumEntryBytes ||
            entry.Width < 1 || entry.Height < 1 || valid.Any(item => item.Id == entry.Id))
          {
            // 合法索引已确认归属（Guid(N)+索引内条目），丢弃时其 owned 目录
            // 一并清理；重复 id 保留给在册条目，不在此清理。
            if (!valid.Any(item => item.Id == entry.Id)) dropped.Add(entry.Id);
            continue;
          }
          valid.Add(entry);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
      }
      enabled = index.Enabled;
      lastKey = index.LastKey;
      entries = valid.OrderBy(entry => entry.CreatedAt).ToList();
      while (entries.Count > MaximumEntries || entries.Sum(entry => entry.Bytes) > MaximumTotalBytes)
      {
        dropped.Add(entries[0].Id);
        entries.RemoveAt(0);
      }
      // 合法索引携带的待清理身份（上次删除失败且已确认归属的 Guid(N)）：
      // 重载后 ClearAll 可重试；在册条目不重复挂起；索引整体损坏不破归属。
      pendingCleanup.Clear();
      pendingCleanup.UnionWith((index.PendingCleanup ?? [])
        .Where(id => Guid.TryParseExact(id, "N", out _)));
      pendingCleanup.RemoveWhere(id => entries.Any(entry => entry.Id == id));
      // 索引合法时才执行丢弃清理；整个索引损坏不猜归属，条目目录全部保留。
      // 同 id 后续在册的（重复条目保留后者等情形）不清理，避免误删活条目目录。
      dropped.RemoveAll(id => entries.Any(entry => entry.Id == id));
      if (dropped.Count > 0)
      {
        lock (gate)
        {
          pendingCleanup.UnionWith(dropped);
          PersistCleanupAndRetry();
        }
      }
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

  private async Task ValidateLoadedAsync(IndexEntry[] candidates, Func<byte[], Task<bool>>? validate)
  {
    foreach (IndexEntry entry in candidates)
    {
      lock (gate) { if (!entries.Any(current => current.Id == entry.Id)) continue; }
      bool valid = false;
      try
      {
        PinSnapshotStore.EnsureOwnedDirectory(root);
        PinSnapshotStore.EnsureOwnedDirectory(Path.Combine(root, entry.Id));
        if (new FileInfo(ImagePath(entry.Id)).Length == entry.Bytes)
        {
          byte[] png = await File.ReadAllBytesAsync(ImagePath(entry.Id)).ConfigureAwait(false);
          if (validate is null) await PinSnapshotStore.ValidatePngAsync(png, CancellationToken.None).ConfigureAwait(false);
          bool decoded = validate is null || await validate(png).ConfigureAwait(false);
          (int width, int height) = PinSnapshotStore.SniffPngSize(png);
          valid = decoded && width == entry.Width && height == entry.Height;
        }
      }
      catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
      { AppLog.Warn($"Skipped damaged screenshot history: {entry.Id}: {error.Message}"); }
      lock (gate)
      {
        // 删除/清空/淘汰发生后，迟到验证不能复活条目；启用和新录入保持当前真值。
        if (!entries.Any(current => current.Id == entry.Id)) continue;
        if (valid) verified.Add(entry.Id);
        else
        {
          entries = entries.Where(current => current.Id != entry.Id).ToList();
          verified.Remove(entry.Id);
          pendingCleanup.Add(entry.Id);
          PersistCleanupAndRetry();
        }
      }
    }
  }

  private void SaveIndex()
  {
    Directory.CreateDirectory(root);
    PinSnapshotStore.EnsureOwnedDirectory(root);
    AtomicFile.WriteAllText(
      Path.Combine(root, IndexFileName),
      JsonSerializer.Serialize(
        new IndexFile(enabled, lastKey, entries, [.. pendingCleanup]), IndexJson));
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
      HashSet<string> previousPending = [.. pendingCleanup];
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
        pendingCleanup.UnionWith(evicted);
        SaveIndex();
        verified.Add(id);
        lastError = null;
      }
      catch (Exception error) when (error is IOException or UnauthorizedAccessException)
      {
        entries = previous;
        lastKey = previousKey;
        lastError = $"截图已成功，但历史保存失败：{error.Message}";
        verified.Remove(id);
        pendingCleanup.Clear();
        pendingCleanup.UnionWith(previousPending);
        // 未提交的新文件直接回滚；只有回滚失败才需要持久保存清理身份。
        if (!TryDeleteOwnedEntry(id))
        {
          pendingCleanup.Add(id);
          PersistCleanupAndRetry();
        }
        return;
      }
      verified.ExceptWith(evicted);
      RetryPendingCleanup();
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
      IndexEntry[] available = [.. entries.Where(entry => verified.Contains(entry.Id))];
      IEnumerable<IndexEntry> filtered = available.OrderByDescending(entry => entry.CreatedAt);
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
        available.Length + pendingCleanup.Count,
        available.Sum(entry => entry.Bytes),
        MaximumEntries,
        MaximumTotalBytes,
        [.. list], pendingCleanup.Count > 0
          ? $"有 {pendingCleanup.Count} 条历史尚未清理；请检查文件占用、权限或目录内额外文件后重试清空。"
          : lastError);
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
      entry = entries.FirstOrDefault(item => item.Id == entryId && verified.Contains(item.Id));
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
    if (!Guid.TryParseExact(entryId, "N", out _)) return false;
    lock (gate)
    {
      List<IndexEntry> previous = entries;
      entries = entries.Where(entry => entry.Id != entryId).ToList();
      if (entries.Count == previous.Count) return false;
      pendingCleanup.Add(entryId);
      try { SaveIndex(); }
      catch
      {
        entries = previous;
        pendingCleanup.Remove(entryId);
        throw;
      }
      verified.Remove(entryId);
      RetryPendingCleanup();
      return true;
    }
  }

  public int ClearAll()
  {
    lock (gate)
    {
      List<IndexEntry> previous = entries;
      HashSet<string> previousPending = [.. pendingCleanup];
      pendingCleanup.UnionWith(entries.Select(entry => entry.Id));
      int requested = pendingCleanup.Count;
      entries = [];
      try { SaveIndex(); }
      catch
      {
        entries = previous;
        pendingCleanup.Clear();
        pendingCleanup.UnionWith(previousPending);
        throw;
      }
      verified.Clear();
      RetryPendingCleanup();
      if (pendingCleanup.Count == 0) lastError = null;
      return requested - pendingCleanup.Count;
    }
  }

  // 调用方持有 gate；登记成功后才删除，失败时原索引仍保留归属。
  private void PersistCleanupAndRetry()
  {
    try { SaveIndex(); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
      AppLog.Warn($"Screenshot history cleanup registration failed: {error.Message}");
      return;
    }
    RetryPendingCleanup();
  }

  private void RetryPendingCleanup()
  {
    bool changed = false;
    foreach (string id in pendingCleanup.ToArray())
      if (TryDeleteOwnedEntry(id)) changed |= pendingCleanup.Remove(id);
    if (!changed) return;
    try { SaveIndex(); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
      // 磁盘上的旧待清理记录仍安全；下次对不存在的目录重试即可。
      AppLog.Warn($"Screenshot history cleanup completion failed to persist: {error.Message}");
    }
  }

  /// <summary>仅删除 owned 根下 Guid(N) 点状子目录；任何其它路径不触碰。
  /// 不吞 IO/权限失败（抛出=未清成）；verified/pending 状态由调用方在 gate
  /// 内维护，避免与 Projection/录入并发访问共享集合。</summary>
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
    File.Delete(Path.Combine(directory, ImageFileName));
    Directory.Delete(directory);
  }

  /// <summary>共享安全删除：成功（或目录已不存在/非 owned id）返回 true；
  /// IO/权限失败返回 false，由调用方保留待清理身份，后续 ClearAll 重试。</summary>
  private bool TryDeleteOwnedEntry(string entryId)
  {
    try
    {
      DeleteEntryDirectory(entryId);
      return true;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
    {
      AppLog.Warn($"Screenshot history directory cleanup failed: {entryId}: {error.Message}");
      return false;
    }
  }
}
