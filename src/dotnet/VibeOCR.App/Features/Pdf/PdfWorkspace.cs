namespace VibeOCR.App.Features.Pdf;

public enum PdfCloseDecision { Save, Discard, Cancel }
public sealed record PdfPreviewPosition(double? Zoom = null, double Left = 0, double Top = 0, bool ShowBoxes = true,
  int? Block = null, string Draft = "", long Revision = -1, int Page = -1, string? OriginalText = null);
public sealed class PdfDocumentEntry(PdfViewModel model)
{
  public string Id { get; } = Guid.NewGuid().ToString("N");
  public PdfViewModel Model { get; } = model;
  public int DisplayOrdinal { get; internal init; }
  public string DisplayName
  {
    get
    {
      string path = Model.FilePath ?? RequestedPath ?? "打开中";
      string parent = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
      if (parent.Length > 32) parent = parent[..32] + "…";
      return $"{Path.GetFileName(path)} · {parent} · #{DisplayOrdinal}";
    }
  }
  public bool HasUnsubmittedDraft => Preview.Block is >= 0 && Preview.Revision == Model.Revision &&
    Preview.Page >= 0 && Preview.Page < Model.PageCount && Preview.OriginalText is { } original && Preview.Draft != original;

  public HashSet<int> SelectedPages { get; } = [];
  public int WindowStart { get; set; }
  public string? TaskEngine { get; set; }
  public string? RequestedPath { get; set; }
  public PdfPreviewPosition Preview { get; set; } = new();
  public bool Closing { get; set; }
  public string? CloseError { get; set; }
}
public sealed record PdfExportItem(string DocumentId, string Name, long Revision, PdfProcessingSettings Settings,
  string Status = "not_started", string? Output = null, string? Error = null);

/// <summary>PDF 文档归属及逐文件副本导出；不持有第二套 PDF 模型或写入器。</summary>
public sealed class PdfWorkspace
{
  public const int MaxDocuments = 16;
  private readonly List<PdfDocumentEntry> documents = [];
  private readonly Dictionary<PdfDocumentEntry, string> saveAsTargets = [];
  public IReadOnlyList<PdfDocumentEntry> Documents => documents;
  public PdfDocumentEntry? Active { get; private set; }
  public string EmptyDocumentId { get; private set; } = Guid.NewGuid().ToString("N");
  public IReadOnlyList<PdfExportItem> ExportItems { get; private set; } = [];
  public bool Exporting { get; private set; }
  public long ExportGeneration { get; private set; }
  private bool cancelExport;
  private int nextDisplayOrdinal;
  public PdfDocumentEntry Add(PdfViewModel model)
  {
    if (documents.Count >= MaxDocuments) throw new InvalidOperationException("最多同时打开 16 份 PDF，请先关闭文档。");
    var entry = new PdfDocumentEntry(model) { DisplayOrdinal = ++nextDisplayOrdinal }; documents.Add(entry); Active = entry; return entry;
  }
  public PdfDocumentEntry? Find(string id) => documents.FirstOrDefault(entry => entry.Id == id);
  public PdfDocumentEntry? For(PdfViewModel model) => documents.FirstOrDefault(entry => ReferenceEquals(entry.Model, model));
  /// <summary>
  /// 按规范路径（OrdinalIgnoreCase）判定目标归属：优先当前保存目标
  /// Model.FilePath；尚无当前目标（正在打开或仅剩可重试的未关闭会话）时
  /// 退回 RequestedPath，避免同一文档重复打开/重复占用槽位。SaveAs 成功
  /// 后 FilePath 已指向新目标，旧源 RequestedPath 不再抢占，可独立重开。
  /// </summary>
  public PdfDocumentEntry? FindTarget(string path)
  {
    string target = Path.GetFullPath(path);
    return documents.FirstOrDefault(entry => saveAsTargets.TryGetValue(entry, out string? reserved) &&
      string.Equals(reserved, target, StringComparison.OrdinalIgnoreCase))
      ?? documents.FirstOrDefault(entry => entry.Model.FilePath is { } current
      ? string.Equals(Path.GetFullPath(current), target, StringComparison.OrdinalIgnoreCase)
      : entry.RequestedPath is { } requested &&
        string.Equals(Path.GetFullPath(requested), target, StringComparison.OrdinalIgnoreCase));
  }
  public void ReserveSaveAsTarget(PdfDocumentEntry entry, string path)
  {
    if (!documents.Contains(entry) || saveAsTargets.ContainsKey(entry))
      throw new InvalidOperationException("文档已有未收尾或未确认的保存目标。");
    if (FindTarget(path) is { } owner && owner != entry)
      throw new InvalidOperationException("目标属于另一打开文档，请选择其他路径。");
    saveAsTargets.Add(entry, Path.GetFullPath(path));
  }
  public void ReleaseSaveAsTarget(PdfDocumentEntry entry) => saveAsTargets.Remove(entry);
  public void Activate(PdfDocumentEntry entry) { if (!documents.Contains(entry)) throw new InvalidOperationException("文档已关闭"); Active = entry; }
  public void Remove(PdfDocumentEntry entry) { saveAsTargets.Remove(entry); documents.Remove(entry); if (Active == entry) Active = documents.LastOrDefault(); if (documents.Count == 0) EmptyDocumentId = Guid.NewGuid().ToString("N"); }
  public void CancelExport() => cancelExport = true;
  /// <summary>仅真正失败/取消/未开始的项可重试；saved 与 unconfirmed（服务端可能已
  /// 提交副本但响应丢失）不重发，避免盲目重试生成重复副本。</summary>
  public PdfExportItem[] CreateExportPlan(bool modifiedOnly, bool retry) => retry
    ? ExportItems.Where(item => item.Status is "failed" or "cancelled" or "not_started").ToArray()
    : documents.Where(entry => entry.Model.HasSession && (!modifiedOnly || entry.Model.IsModified))
      .Select(entry => new PdfExportItem(entry.Id, Path.GetFileName(entry.Model.FilePath ?? "document.pdf"), entry.Model.Revision, entry.Model.ProcessingSettings)).ToArray();
  public async Task ExportAsync(string directory, bool modifiedOnly, bool retry, Action changed, CancellationToken ct, PdfExportItem[]? snapshot = null)
  {
    if (Exporting) throw new InvalidOperationException("副本导出尚未收尾");
    var previous = ExportItems;
    var plan = snapshot ?? CreateExportPlan(modifiedOnly, retry);
    var items = retry ? previous.ToList() : plan.ToList();
    ExportGeneration++; ExportItems = items; Exporting = true; cancelExport = false; changed();
    var targets = new HashSet<string>(retry ? previous.Where(item => (item.Status == "saved" || item.Status == "unconfirmed") && item.Output is not null).Select(item => item.Output!) : [], StringComparer.OrdinalIgnoreCase);
    try
    {
      foreach (PdfExportItem item in plan)
      {
        int index = items.FindIndex(candidate => candidate.DocumentId == item.DocumentId);
        if (cancelExport || ct.IsCancellationRequested)
        { items[index] = item with { Status = "cancelled" }; changed(); break; }
        PdfDocumentEntry? entry = Find(item.DocumentId);
        if (entry is null || entry.Closing)
        { items[index] = item with { Status = "failed", Error = "文档已关闭" }; changed(); continue; }
        string target = Path.Combine(directory, item.Name);
        for (int suffix = 1; File.Exists(target) || Directory.Exists(target) || targets.Contains(target) || FindTarget(target) is not null; suffix++)
          target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(item.Name)}_{suffix}.pdf");
        targets.Add(target); items[index] = item with { Status = "saving", Output = target }; changed();
        PdfSaveResult result = await entry.Model.ExportCopyAsync(target, item.Revision, item.Settings, CancellationToken.None);
        // Unconfirmed：副本提交后响应丢失，服务端可能已写入真实目标。保留
        // result.Target 与独立状态，明确告知用户检查该输出且不自动重试。
        items[index] = item with
        {
          Status = result.Disposition switch
          {
            PdfSaveDisposition.Saved => "saved",
            PdfSaveDisposition.Unconfirmed => "unconfirmed",
            PdfSaveDisposition.Cancelled => "cancelled",
            _ => "failed",
          },
          Output = result.Disposition is PdfSaveDisposition.Saved or PdfSaveDisposition.Unconfirmed
            ? result.Target
            : null,
          Error = result.Disposition == PdfSaveDisposition.Unconfirmed
            ? $"结果未确认，请检查输出 {Path.GetFileName(result.Target)}；未自动重试。"
            : result.Error,
        };
        changed();
      }
    }
    finally { Exporting = false; changed(); }
  }
}
