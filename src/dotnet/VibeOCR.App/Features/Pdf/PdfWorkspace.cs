namespace VibeOCR.App.Features.Pdf;

public enum PdfCloseDecision { Save, Discard, Cancel }
public sealed record PdfPreviewPosition(double? Zoom = null, double Left = 0, double Top = 0, bool ShowBoxes = true,
  int? Block = null, string Draft = "", long Revision = -1, int Page = -1);
public sealed class PdfDocumentEntry(PdfViewModel model)
{
  public string Id { get; } = Guid.NewGuid().ToString("N");
  public PdfViewModel Model { get; } = model;
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
  public IReadOnlyList<PdfDocumentEntry> Documents => documents;
  public PdfDocumentEntry? Active { get; private set; }
  public string EmptyDocumentId { get; private set; } = Guid.NewGuid().ToString("N");
  public IReadOnlyList<PdfExportItem> ExportItems { get; private set; } = [];
  public bool Exporting { get; private set; }
  public long ExportGeneration { get; private set; }
  private bool cancelExport;
  public PdfDocumentEntry Add(PdfViewModel model)
  {
    if (documents.Count >= MaxDocuments) throw new InvalidOperationException("最多同时打开 16 份 PDF，请先关闭文档。");
    var entry = new PdfDocumentEntry(model); documents.Add(entry); Active = entry; return entry;
  }
  public PdfDocumentEntry? Find(string id) => documents.FirstOrDefault(entry => entry.Id == id);
  public PdfDocumentEntry? For(PdfViewModel model) => documents.FirstOrDefault(entry => ReferenceEquals(entry.Model, model));
  public PdfDocumentEntry? FindTarget(string path) => documents.FirstOrDefault(entry => entry.Model.FilePath is { } target
    && string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
  public void Activate(PdfDocumentEntry entry) { if (!documents.Contains(entry)) throw new InvalidOperationException("文档已关闭"); Active = entry; }
  public void Remove(PdfDocumentEntry entry) { documents.Remove(entry); if (Active == entry) Active = documents.LastOrDefault(); if (documents.Count == 0) EmptyDocumentId = Guid.NewGuid().ToString("N"); }
  public void CancelExport() => cancelExport = true;
  public PdfExportItem[] CreateExportPlan(bool modifiedOnly, bool retry) => retry
    ? ExportItems.Where(item => item.Status != "saved").ToArray()
    : documents.Where(entry => entry.Model.HasSession && (!modifiedOnly || entry.Model.IsModified))
      .Select(entry => new PdfExportItem(entry.Id, Path.GetFileName(entry.Model.FilePath ?? "document.pdf"), entry.Model.Revision, entry.Model.ProcessingSettings)).ToArray();
  public async Task ExportAsync(string directory, bool modifiedOnly, bool retry, Action changed, CancellationToken ct, PdfExportItem[]? snapshot = null)
  {
    if (Exporting) throw new InvalidOperationException("副本导出尚未收尾");
    var previous = ExportItems;
    var plan = snapshot ?? CreateExportPlan(modifiedOnly, retry);
    var items = retry ? previous.ToList() : plan.ToList();
    ExportGeneration++; ExportItems = items; Exporting = true; cancelExport = false; changed();
    var targets = new HashSet<string>(retry ? previous.Where(item => item.Status == "saved" && item.Output is not null).Select(item => item.Output!) : [], StringComparer.OrdinalIgnoreCase);
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
        items[index] = item with { Status = result.Saved ? "saved" : result.Disposition == PdfSaveDisposition.Cancelled ? "cancelled" : "failed", Output = result.Saved ? result.Target : null, Error = result.Error };
        changed();
      }
    }
    finally { Exporting = false; changed(); }
  }
}
