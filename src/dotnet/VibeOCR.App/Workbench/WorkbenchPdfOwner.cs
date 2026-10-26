using System.ComponentModel;
using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Services;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using Windows.Storage.Pickers;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchPdfOwner(Func<PdfDocumentEntry, Task<PdfCloseDecision>> confirmPdfClose, Func<string, Task<bool>>? flushPdfPreview, PortableLayout? optionsLayout, Func<PdfViewModel> pdfFactory, Func<Task<string?>>? pickPdfFolder, Func<Task<string?>>? pickPdfSavePath, Func<nint> windowHandle, WorkbenchSettingsOwner settingsOwner, WorkbenchResourcesOwner resources, WorkbenchSelectionOwner selectionPolicy, WorkbenchOperations operations)
{
  internal WorkbenchCommand BindCommand(WorkbenchCommand command)
  {
    if (command is PdfBoundCommand bound)
    {
      PdfDocumentEntry? entry = pdfWorkspace.Find(bound.DocumentId);
      if (entry is null)
      {
        if (bound.DocumentId != pdfWorkspace.EmptyDocumentId || pdfWorkspace.Active is not null ||
          bound.Command is not (SetPdfTaskEngineCommand or SetPdfTaskModelPresetCommand or SetPdfProcessingSettingsCommand) || pdfExitReview)
          throw new InvalidOperationException("文档已关闭");
      }
      else
      {
        // 纯预览状态（缩放/滚动/草稿）允许写入已知的非活动条目：交互中的
        // 最后位置/草稿不因切换文档丢失；未知/关闭中的文档仍在上面拒绝。
        if (bound.Command is not (ActivatePdfDocumentCommand or SetPdfPreviewPositionCommand) && entry != pdfWorkspace.Active)
          throw new InvalidOperationException("活动文档已变化，请重新操作");
        if ((pdfExitReview || pdfWorkspace.Documents.Any(item => item.Closing)) &&
          !(bound.Command is SetPdfPreviewPositionCommand && ReferenceEquals(pdfClosingPreviewDrain, entry)))
          throw new InvalidOperationException("文档正在关闭确认中");
        if (bound.Revision is { } expected && expected != entry.Model.Revision && bound.Command is not (ActivatePdfDocumentCommand or CancelPdfCommand or CancelPdfExportCommand or SetPdfPreviewPositionCommand))
          throw new InvalidOperationException("文档修订已变化，请重新操作");
      }
      pdfContext.Value = entry; command = bound.Command;
    }
    else pdfContext.Value = pdfWorkspace.Active;
    return command;
  }
  internal PdfDocumentEntry? CaptureContext() => pdfContext.Value;
  internal void RestoreContext(PdfDocumentEntry? previous) => pdfContext.Value = previous;
  internal WorkbenchState? NormalizeOutcome(WorkbenchState? state) => state is PdfWorkbenchState && ActivePdf is { } active && !ReferenceEquals(pdf, active) ? PdfState(active) : state;

  internal bool Handles(WorkbenchCommand command) => command is SetPdfTaskEngineCommand or SetPdfTaskModelPresetCommand or ActivatePdfDocumentCommand or SavePdfAsCommand or ExportPdfDocumentsCommand or CancelPdfExportCommand or SetPdfPreviewPositionCommand or OpenPdfCommand or OpenDroppedPdfCommand or RotatePdfCommand or OrientPdfCommand or CorrectPdfOrientationCommand or InsertPdfBlankCommand or InsertPdfFromCommand or MovePdfPageCommand or ClosePdfCommand or DeletePdfPagesCommand or OcrPdfPagesCommand or AddPdfTextLayersCommand or DeletePdfTextLayersCommand or CancelPdfCommand or SetPdfProcessingSettingsCommand or SavePdfCommand or SelectPdfPagesCommand or SelectAllPdfPagesCommand or SelectPdfPageCommand or SetCurrentPdfPageCommand or UpdatePdfBlockTextCommand or RetryPdfPageInspectCommand or SetPdfWindowCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => command switch
  {
    SetPdfTaskEngineCommand pdfTaskEngine => SetPdfTaskEngine(pdfTaskEngine),
    SetPdfTaskModelPresetCommand preset => SetPdfTaskModelPreset(preset),
    ActivatePdfDocumentCommand => await ActivatePdfAsync(PdfEntry!, cancellationToken),
    SavePdfAsCommand => await SavePdfAsync(cancellationToken, true),
    ExportPdfDocumentsCommand export => await ExportPdfDocumentsAsync(export, cancellationToken),
    CancelPdfExportCommand => CancelPdfExport(),
    SetPdfPreviewPositionCommand position => SetPdfPreviewPosition(position),
    OpenPdfCommand => await OpenPdfAsync(cancellationToken),
    OpenDroppedPdfCommand dropped => await OpenDroppedPdfAsync(
      dropped,
      cancellationToken),
    RotatePdfCommand rotate => await RotatePdfAsync(rotate, cancellationToken),
    OrientPdfCommand orient => await OrientPdfAsync(orient, cancellationToken),
    CorrectPdfOrientationCommand correct => await StartPdfOrientationAsync(correct, cancellationToken),
    InsertPdfBlankCommand blank => await ChangePdfStructureAsync(blank, cancellationToken),
    InsertPdfFromCommand insert => await ChangePdfStructureAsync(insert, cancellationToken),
    MovePdfPageCommand move => await ChangePdfStructureAsync(move, cancellationToken),
    ClosePdfCommand => await ClosePdfAsync(cancellationToken),
    DeletePdfPagesCommand => await DeletePdfPagesAsync(cancellationToken),
    OcrPdfPagesCommand => await StartPdfOcr(cancellationToken),
    AddPdfTextLayersCommand add => await StartPdfOcr(cancellationToken, add),
    DeletePdfTextLayersCommand delete => StartPdfDeleteLayers(delete, cancellationToken),
    CancelPdfCommand => CancelPdf(),
    SetPdfProcessingSettingsCommand change => SetPdfProcessingSettings(change),
    SavePdfCommand => await SavePdfAsync(cancellationToken),
    SelectPdfPagesCommand select => await SelectPdfPagesAsync(select, cancellationToken),
    SelectAllPdfPagesCommand select => await SelectPdfPagesAsync(new SelectPdfPagesCommand(select.Selected && pdf is not null ? Enumerable.Range(0, pdf.PageCount).ToArray() : []), cancellationToken),
    SelectPdfPageCommand select => await SelectPdfPageAsync(select, cancellationToken),
    SetCurrentPdfPageCommand setCurrent => await SetCurrentPdfPageAsync(setCurrent, cancellationToken),
    UpdatePdfBlockTextCommand edit => await UpdatePdfBlockTextAsync(edit, cancellationToken),
    RetryPdfPageInspectCommand => await RetryPdfPageInspectAsync(cancellationToken),
    SetPdfWindowCommand window => await SetPdfWindowAsync(
      window,
      cancellationToken),
    _ => throw new InvalidOperationException("Unsupported Pdf workbench command."),
  };

  private readonly HashSet<Task> pdfBackgroundOperations = [];
  private bool pdfExitReview;
  private PdfDocumentEntry? pdfClosingPreviewDrain;
  private readonly PdfWorkspace pdfWorkspace = new();
  // Capture document ownership at command entry; the async flow retains it across pickers/awaits.
  private readonly AsyncLocal<PdfDocumentEntry?> pdfContext = new();
  private PdfDocumentEntry? PdfEntry => pdfContext.Value ?? pdfWorkspace.Active;
  private PdfViewModel? ActivePdf => pdfWorkspace.Active?.Model;
  private readonly HashSet<int> emptyPdfSelection = [];
  private HashSet<int> selectedPdfPages => PdfEntry?.SelectedPages ?? emptyPdfSelection;
  public IReadOnlyList<PdfDocumentEntry> PdfDocuments => pdfWorkspace.Documents;
  private readonly Dictionary<int, WorkbenchResourceReference> pdfThumbnails = [];
  // 当前页检查/高清预览资源：单页缓存，键含 session/revision/page 与渲染设置；换页/关
  // 闭/修订变更时显式 revoke（ReleaseResource = broker.Revoke + 删除发布文件）。
  private readonly SemaphoreSlim pdfPageResourceGate = new(1, 1);
  private WorkbenchResourceReference? pdfPagePreview;
  private WorkbenchResourceReference? pdfPageInspect;
  private string? pdfPageResourceKey;
  private long pdfPageResourceGeneration;
  private string pdfPageInspectStatus = "pdf.inspect.none";
  private readonly Dictionary<int, (RecognizeResponse Result, WorkbenchResourceReference Reference)> pdfStructured = [];
  private string? pdfTaskModelPreset { get => PdfEntry?.TaskModelPreset; set { if (PdfEntry is { } entry) entry.TaskModelPreset = value; } }
  private string? pdfTaskEngine { get => PdfEntry?.TaskEngine; set { if (PdfEntry is { } entry) entry.TaskEngine = value; } }
  private PdfViewModel? pdf { get => PdfEntry?.Model; set { if (value is not null) { var entry = pdfWorkspace.For(value) ?? pdfWorkspace.Add(value); pdfContext.Value = entry; } } }
  private long pdfGeneration;
  private long pdfWindowGeneration;
  private int pdfWindowStart { get => PdfEntry?.WindowStart ?? 0; set { if (PdfEntry is { } entry) entry.WindowStart = value; } }
  private void SynchronizePdfMode(bool requireUsable = false)
  {
    RecognitionSelectionSnapshot? snapshot = settingsOwner.CurrentModel?.RecognitionSelection;
    if (settingsOwner.CurrentModel is not null) foreach (PdfDocumentEntry entry in pdfWorkspace.Documents) entry.Model.SetInspectionCapabilities(snapshot?.Catalog.Health.Capabilities ?? []);
    if (pdf is null || snapshot?.Catalog.SupportsRecognitionModes is not true)
    {
      if (requireUsable) WorkbenchSelectionOwner.EnsureUsableModeOverrideOrThrow(snapshot?.Catalog, pdfTaskEngine);
      pdf?.SetRecognitionMode(null, taskModeId: pdfTaskEngine);
      return;
    }
    RuntimeSelectionService selection = snapshot.Catalog;
    string? effectiveId = WorkbenchSelectionOwner.EffectiveModeIdOrDefault(
      snapshot, pdfTaskEngine, requireUsable);
    RecognitionModeOption? mode = string.IsNullOrWhiteSpace(effectiveId) ? null : requireUsable
      ? selection.SelectRecognitionMode(effectiveId)
      : WorkbenchSelectionOwner.TryFindRecognitionMode(selection, effectiveId);
    MineruConfig? config = requireUsable
      ? snapshot.MineruConfigFor(mode?.Id)
      : WorkbenchSelectionOwner.TryProjectMineruConfig(snapshot, mode?.Id, out MineruConfig? projected)
        ? projected : null;
    pdf.SetRecognitionMode(mode, selectionPolicy.GetTaskModeOptions(mode, pdfTaskModelPreset), pdfTaskEngine);
  }

  private PdfWorkbenchState SetPdfTaskModelPreset(SetPdfTaskModelPresetCommand command)
  {
    WorkbenchSelectionOwner.ValidateTaskModelPreset(command.Preset);
    pdf ??= CreatePdfViewModel();
    pdfTaskModelPreset = command.Preset;
    SynchronizePdfMode();
    return PdfState(pdf);
  }

  private PdfWorkbenchState SetPdfTaskEngine(SetPdfTaskEngineCommand command)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsBusy) throw new InvalidOperationException("A running PDF OCR cannot change recognition mode.");
    RecognitionSelectionSnapshot? snapshot = settingsOwner.CurrentModel?.RecognitionSelection;
    RuntimeSelectionService? selection = snapshot?.Catalog;
    if (command.Engine is not null)
    {
      if (snapshot is null || selection?.SupportsRecognitionModes is not true)
        throw new RuntimeSelectionException(RuntimeSelectionErrorKind.CapabilityMissing,
          "The runtime does not provide recognition modes.");
      RecognitionModeOption mode = selection.SelectRecognitionMode(command.Engine);
      snapshot.MineruConfigFor(mode.Id);
    }
    pdfTaskEngine = command.Engine;
    SynchronizePdfMode();
    return PdfState(pdf);
  }

  private PdfViewModel CreatePdfViewModel()
  {
    PdfViewModel model = pdfFactory();
    if (optionsLayout is not null) model.SetProcessingSettings(PdfProcessingSettings.Load(optionsLayout));
    model.PropertyChanged += OnPdfPropertyChanged;
    model.PagesRemapped += OnPdfPagesRemapped;
    pdfWorkspace.Add(model);
    return model;
  }
  private void OnPdfPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
  {
    if (args.PropertyName is null && sender is PdfViewModel && ActivePdf is { } active) operations.Publish(PdfState(active));
  }
  private PdfWorkbenchState? SetPdfProcessingSettings(SetPdfProcessingSettingsCommand command)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsSettling) return PdfState(pdf);
    command.Settings.Validate();
    if (optionsLayout is not null) command.Settings.Save(optionsLayout);
    PdfViewModel viewModel = pdf;
    bool renderChanged = viewModel.ProcessingSettings.RenderDpi != command.Settings.RenderDpi
      || viewModel.ProcessingSettings.MaxPixels != command.Settings.MaxPixels;
    // 先废弃旧资源，让设置变更引发的同步状态通知也不能携带旧分辨率图。
    if (renderChanged) ReleasePdfPageResources();
    viewModel.SetProcessingSettings(command.Settings);
    if (!renderChanged || !viewModel.CanInspectPage || viewModel.SessionId is null
      || viewModel.SelectedPage < 0 || viewModel.SelectedPage >= viewModel.PageCount)
      return PdfState(viewModel);
    string key = PdfPageResourceKey(viewModel);
    pdfPageResourceKey = key;
    pdfPageInspectStatus = "pdf.inspect.loading";
    long generation = Volatile.Read(ref pdfPageResourceGeneration);
    return PublishPdfStartThenTrack(PdfState(viewModel), async () =>
    {
      await EnsurePdfPageResourcesAsync(viewModel, CancellationToken.None);
      if (ReferenceEquals(ActivePdf, viewModel) && PdfPageResourceKey(viewModel) == key
        && generation == Volatile.Read(ref pdfPageResourceGeneration))
        operations.Publish(PdfState(viewModel));
    });
  }
  private PdfWorkbenchState CancelPdf()
  {
    pdf ??= CreatePdfViewModel(); pdf.Cancel(); return PdfState(pdf);
  }
  private PdfWorkbenchState? StartPdfDeleteLayers(DeletePdfTextLayersCommand command, CancellationToken ct)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsSettling || command.Revision != pdf.Revision)
      throw new InvalidOperationException("文档修订已变化，请重新确认删除范围。");
    int[] pages = command.Pages.ToArray();
    long generation = Interlocked.Increment(ref pdfGeneration);
    return PublishPdfStartThenTrack(PdfState(pdf) with { IsBusy = true }, async () =>
    {
      await pdf.DeleteTextLayersAsync(pages, ct);
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        ReleasePdfWindowResources();
        operations.Publish(await PdfStateAsync(pdf, CancellationToken.None));
      }
    });
  }

  private async Task<PdfWorkbenchState> OpenPdfAsync(CancellationToken ct)
  {
    // 退出审阅中拒绝新的打开入口：新会话不会进入退出快照，未经确认即被留下。
    if (pdfExitReview || pdfWorkspace.Documents.Any(entry => entry.Closing)) throw new InvalidOperationException("正在关闭确认中，不能打开新 PDF。");
    pdf ??= CreatePdfViewModel();
    string? path = await pdf.PickFileAsync(ct);
    return path is null ? PdfState(ActivePdf ?? pdf) : await AddPdfPathAsync(path, ct);
  }
  private Task<PdfWorkbenchState> OpenDroppedPdfAsync(OpenDroppedPdfCommand command, CancellationToken ct) => AddPdfPathAsync(command.Path, ct);
  private async Task<PdfWorkbenchState> AddPdfPathAsync(string path, CancellationToken ct)
  {
    // 实际加入入口重查：退出审阅开始前已打开的 picker 迟到返回同样拒绝。
    if (pdfExitReview || pdfWorkspace.Documents.Any(entry => entry.Closing)) throw new InvalidOperationException("正在关闭确认中，不能打开新 PDF。");
    // 在实际切换点排空：picker 等待期间继续输入、原生拖入和重复打开均走这里。
    PdfDocumentEntry? leaving = pdfWorkspace.Active;
    if (leaving is { Model.HasRemoteSession: true } && flushPdfPreview is not null)
    {
      if (!await flushPdfPreview(leaving.Id) || pdfWorkspace.Active != leaving || pdfExitReview || pdfWorkspace.Documents.Any(entry => entry.Closing))
        throw new InvalidOperationException("最后的预览草稿未确认，保留当前文档，请重试。");
    }
    path = Path.GetFullPath(path);
    if (pdfWorkspace.FindTarget(path) is { } duplicate)
    {
      if (!duplicate.Model.HasRemoteSession && !duplicate.Model.IsSettling)
        await duplicate.Model.OpenPathAsync(path, ct);
      return await ActivatePdfAsync(duplicate, ct);
    }
    if (pdfWorkspace.Documents.Count >= PdfWorkspace.MaxDocuments && pdf is not { HasRemoteSession: false, IsSettling: false }) throw new InvalidOperationException("最多同时打开 16 份 PDF，请先关闭文档。");
    ActivePdf?.TrimResultCache(0, 0);
    ReleasePdfWindowResources(force: true);
    PdfViewModel model = pdf is { HasRemoteSession: false, IsSettling: false } empty ? empty : CreatePdfViewModel();
    PdfDocumentEntry entry = pdfWorkspace.For(model)!;
    entry.RequestedPath = path;
    pdfWorkspace.Activate(entry); pdfContext.Value = entry;
    Interlocked.Increment(ref pdfGeneration);
    try { await model.OpenPathAsync(path, ct); }
    catch (Exception error) { entry.CloseError = error.Message; AppLog.Error("PDF open failed", error); }
    if (!model.HasRemoteSession)
    {
      // Preparation cancellation/failure keeps the authorized input visible for
      // a later explicit retry. No worker session has been created at this point.
      if (model.RuntimePreparationIncomplete)
      {
        entry.CloseError = model.Summary;
        return await PdfStateAsync(model, CancellationToken.None);
      }
      bool wasActive = pdfWorkspace.Active == entry;
      model.PropertyChanged -= OnPdfPropertyChanged;
      model.PagesRemapped -= OnPdfPagesRemapped;
      pdfWorkspace.Remove(entry); pdfContext.Value = pdfWorkspace.Active;
      PdfWorkbenchState retained = ActivePdf is { } previous ? await PdfStateAsync(previous, ct) : EmptyPdfState();
      string issue = PdfStatusCode(model);
      return wasActive ? retained with { StatusCode = issue == "pdf.empty" ? "pdf.failed" : issue, Summary = "打开未完成，其他文档保持原状态。" } : retained;
    }
    if (!model.HasSession) entry.CloseError = model.Summary;
    ResetPdfSelection(selectFirstPage: true);
    return await PdfStateAsync(model, ct);
  }
  private async Task<PdfWorkbenchState> ActivatePdfAsync(PdfDocumentEntry entry, CancellationToken ct)
  {
    if (pdfWorkspace.Active != entry)
    {
      ActivePdf?.TrimResultCache(0, 0);
      ReleasePdfWindowResources(force: true);
      Interlocked.Increment(ref pdfGeneration);
      pdfWorkspace.Activate(entry);
    }
    pdfContext.Value = entry;
    return await PdfStateAsync(entry.Model, ct);
  }
  private async Task<PdfWorkbenchState> RotatePdfAsync(
    RotatePdfCommand command,
    CancellationToken cancellationToken)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsSettling) return PdfState(pdf);
    Interlocked.Increment(ref pdfGeneration);
    PdfViewModel viewModel = pdf;
    int[] pages = PdfRange(viewModel, command.Range);
    if (pages.Length > 0)
    {
      await viewModel.RotateAsync(pages, command.Degrees, cancellationToken);
      ReleasePdfWindowResources();
    }
    return await PdfStateAsync(viewModel, cancellationToken);
  }

  private async Task<PdfWorkbenchState> ClosePdfAsync(CancellationToken ct)
  {
    PdfDocumentEntry? entry = PdfEntry;
    if (entry is null) return EmptyPdfState();
    if (await ClosePdfEntryAsync(entry, ct) && ActivePdf is { } active) return await PdfStateAsync(active, ct);
    return ActivePdf is { } remaining ? PdfState(remaining) : EmptyPdfState();
  }
  private async Task<bool> ClosePdfEntryAsync(PdfDocumentEntry entry, CancellationToken ct)
  {
    if (pdfWorkspace.Documents.Any(item => item.Closing) || pdfWorkspace.Exporting) return false;
    entry.Closing = true;
    PdfPreviewPosition? discardedDraft = null;
    bool closed = false;
    try
    {
      await entry.Model.CancelAndSettleAsync();
      if (entry == pdfWorkspace.Active && entry.Model.HasRemoteSession && flushPdfPreview is not null)
      {
        pdfClosingPreviewDrain = entry;
        try { if (!await flushPdfPreview(entry.Id)) return false; }
        finally { pdfClosingPreviewDrain = null; }
      }
      if (entry.HasUnsubmittedDraft)
      {
        // 保存只包含已提交 PDF；必须先明确放弃局部草稿，不能伪称保存了草稿。
        if (await confirmPdfClose(entry) != PdfCloseDecision.Discard) return false;
        discardedDraft = entry.Preview;
        entry.Preview = entry.Preview with { Block = null, Draft = "", OriginalText = null };
      }
      if (entry.Model.IsModified)
      {
        PdfCloseDecision decision = await confirmPdfClose(entry);
        if (decision == PdfCloseDecision.Cancel) return false;
        if (decision == PdfCloseDecision.Save)
        {
          if (entry.Model.FilePath is not { } target || !(await entry.Model.SaveAsync(target, ct)).Saved) return false;
        }
      }
      await entry.Model.CloseSessionAsync(CancellationToken.None);
      entry.Model.PropertyChanged -= OnPdfPropertyChanged;
      entry.Model.PagesRemapped -= OnPdfPagesRemapped;
      if (pdfWorkspace.Active == entry) ReleasePdfWindowResources();
      pdfWorkspace.Remove(entry); entry.CloseError = null; closed = true;
      pdfContext.Value = pdfWorkspace.Active;
      Interlocked.Increment(ref pdfGeneration);
      return true;
    }
    catch (Exception error) { entry.CloseError = error.Message; return false; }
    finally
    {
      // 取消后续模型确认或远端关闭失败时保留草稿；仅关闭成功才真正丢弃。
      if (!closed && discardedDraft is not null) entry.Preview = discardedDraft;
      entry.Closing = false;
      if (ActivePdf is { } active) operations.Publish(PdfState(active));
    }
  }
  public async Task<bool> RequestCloseAllPdfAsync()
  {
    if (pdfExitReview) return false;
    pdfExitReview = true;
    try
    {
      pdfWorkspace.CancelExport();
      foreach (PdfDocumentEntry entry in pdfWorkspace.Documents) entry.Model.Cancel();
      Task[] operations; lock (pdfBackgroundOperations) operations = pdfBackgroundOperations.ToArray();
      await Task.WhenAll(operations);
      await Task.WhenAll(pdfWorkspace.Documents.Select(entry => entry.Model.WaitForSettlementAsync()));
      foreach (PdfDocumentEntry entry in pdfWorkspace.Documents.ToArray())
        if (!await ClosePdfEntryAsync(entry, CancellationToken.None)) return false;
      return true;
    }
    finally { pdfExitReview = false; }
  }

  private int[] PdfRange(PdfViewModel model, string range) => range == "all"
    ? Enumerable.Range(0, model.PageCount).ToArray() : SelectedPdfPages(model);

  private async Task<PdfWorkbenchState> OrientPdfAsync(OrientPdfCommand command, CancellationToken ct)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsSettling) return PdfState(pdf);
    Interlocked.Increment(ref pdfGeneration);
    await pdf.OrientPagesAsync(PdfRange(pdf, command.Range), command.Landscape, ct);
    ReleasePdfWindowResources();
    return await PdfStateAsync(pdf, ct);
  }

  private async Task<PdfWorkbenchState?> StartPdfOrientationAsync(CorrectPdfOrientationCommand command, CancellationToken ct)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsSettling) return PdfState(pdf);
    PdfViewModel model = pdf; string? session = model.SessionId; long revision = model.Revision;
    if (!await selectionPolicy.EnsureSelectionLoadedForSubmitAsync(ct) || !ReferenceEquals(ActivePdf, model) || model.SessionId != session || model.Revision != revision || model.IsSettling)
      return PdfState(model);
    SynchronizePdfMode();
    int[] pages = PdfRange(model, command.Range);
    long generation = Interlocked.Increment(ref pdfGeneration);
    return PublishPdfStartThenTrack(PdfState(model) with { IsBusy = true }, async () =>
    {
      await model.CorrectOrientationAsync(pages, ct);
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        ReleasePdfWindowResources();
        operations.Publish(await PdfStateAsync(model, CancellationToken.None));
      }
    });
  }

  private async Task<PdfWorkbenchState> ChangePdfStructureAsync(WorkbenchCommand command, CancellationToken ct)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsSettling) return PdfState(pdf);
    long expected = command switch { InsertPdfBlankCommand value => value.Revision, InsertPdfFromCommand value => value.Revision, MovePdfPageCommand value => value.Revision, _ => -1 };
    if (expected != pdf.Revision) throw new InvalidOperationException("PDF structure command is stale.");
    Interlocked.Increment(ref pdfGeneration);
    switch (command)
    {
      case InsertPdfBlankCommand blank: await pdf.InsertBlankAsync(blank.AfterIndex, blank.Width, blank.Height, ct); break;
      case InsertPdfFromCommand insert: await pdf.InsertFromAsync(insert.AfterIndex, ct); break;
      case MovePdfPageCommand move:
        if (move.FromIndex < 0 || move.FromIndex >= pdf.PageCount || move.ToIndex < 0 || move.ToIndex >= pdf.PageCount)
          throw new ArgumentException("PDF move index is invalid.");
        var order = Enumerable.Range(0, pdf.PageCount).ToList();
        int moving = order[move.FromIndex]; order.RemoveAt(move.FromIndex); order.Insert(move.ToIndex, moving);
        await pdf.ReorderAsync(order.ToArray(), ct); break;
    }
    return await PdfStateAsync(pdf, ct);
  }

  private void OnPdfPagesRemapped(PdfViewModel model, IReadOnlyList<int?> mapping)
  {
    // 重映射按 For(model) 归属：已移除 sender 完全 no-op；仅活跃文档的结构变更
    // 回收其窗口资源（不依赖 AsyncLocal 环境），不触碰其他文档。mapping 非幂等，
    // 仅此一处生效。
    if (pdfWorkspace.For(model) is not { } entry) return;
    HashSet<int> selection = entry.SelectedPages;
    int[] selected = mapping.Select((old, index) => (old, index)).Where(pair => pair.old.HasValue && selection.Contains(pair.old.Value)).Select(pair => pair.index).ToArray();
    selection.Clear(); foreach (int index in selected) selection.Add(index);
    if (ReferenceEquals(pdfWorkspace.Active, entry)) ReleasePdfWindowResources(force: true);
  }

  private async Task<PdfWorkbenchState> DeletePdfPagesAsync(
    CancellationToken cancellationToken)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsSettling) return PdfState(pdf);
    Interlocked.Increment(ref pdfGeneration);
    PdfViewModel viewModel = pdf;
    int[] pages = SelectedPdfPages(viewModel);
    if (pages.Length > 0) await viewModel.DeletePagesAsync(pages, cancellationToken);
    return await PdfStateAsync(viewModel, cancellationToken);
  }

  private async Task<PdfWorkbenchState> OcrPdfPagesAsync(
    CancellationToken cancellationToken, AddPdfTextLayersCommand? add = null)
  {
    pdf ??= CreatePdfViewModel();
    PdfViewModel viewModel = pdf;
    int[] pages = add?.Range is "all" or "unlayered"
      ? viewModel.Pages.Where(page => add.Range == "all" || !page.HasTextLayer).Select(page => page.Index).ToArray()
      : SelectedPdfPages(viewModel);
    {
      await viewModel.StartOcrAsync(pages, overwrite: add?.Overwrite == true, cancellationToken, addTextLayer: add is not null);
    }
    return PdfState(viewModel);
  }

  private async Task<PdfWorkbenchState?> StartPdfOcr(CancellationToken cancellationToken, AddPdfTextLayersCommand? add = null)
  {
    pdf ??= CreatePdfViewModel();
    if (pdf.IsSettling) return PdfState(pdf);
    // 与单次/批量相同：提交前加载权威目录并严格协商可用模式；用户已显式
    // 选择 Paddle 模式而目录/环境不可用时，这里会抛出可恢复的
    // RecognitionModeUnavailableException，拒绝按默认 OCR 静默提交。
    if (!await selectionPolicy.EnsureSelectionLoadedForSubmitAsync(cancellationToken))
    {
      // 环境切换窗口：取消本次提交，不以空快照提交（Rapid 回退）。
      Interlocked.Increment(ref pdfGeneration);
      return PdfState(pdf);
    }
    SynchronizePdfMode(requireUsable: true);
    long generation = Interlocked.Increment(ref pdfGeneration);
    return PublishPdfStartThenTrack(
      PdfState(pdf) with { IsBusy = true, StatusCode = PdfStatusCode(pdf, isBusy: true) },
      () => CompletePdfOcrAsync(generation, cancellationToken, add));
  }

  private async Task CompletePdfOcrAsync(
    long generation,
    CancellationToken cancellationToken, AddPdfTextLayersCommand? add = null)
  {
    try
    {
      PdfWorkbenchState state = await OcrPdfPagesAsync(cancellationToken, add);
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        ReleasePdfWindowResources();
        operations.PublishTerminal((await PdfStateAsync(pdf!, cancellationToken)) with
        {
          StatusCode = state.StatusCode,
        });
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        operations.PublishTerminal(PdfState(pdf!));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("PDF OCR workbench operation failed", error);
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        operations.PublishTerminal(PdfState(pdf!));
      }
    }
  }

  private async Task<PdfWorkbenchState> SavePdfAsync(CancellationToken ct, bool saveAs = false)
  {
    PdfDocumentEntry entry = PdfEntry ?? throw new InvalidOperationException("请先打开 PDF");
    string? session = entry.Model.SessionId; long revision = entry.Model.Revision;
    string? path = entry.Model.FilePath;
    if (saveAs)
    {
      if (pickPdfSavePath is not null) path = await pickPdfSavePath();
      else
      {
        var picker = new FileSavePicker
        {
          SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
          SuggestedFileName = Path.GetFileNameWithoutExtension(path ?? "document")
        };
        picker.FileTypeChoices.Add("PDF", [".pdf"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        path = (await picker.PickSaveFileAsync())?.Path;
      }
      if (path is null) return PdfState(entry.Model);
      if (pdfWorkspace.FindTarget(path) is { } owner && owner != entry) throw new InvalidOperationException("目标属于另一打开文档，请选择其他路径。");
    }
    if (path is null) return PdfState(entry.Model);
    if (entry.Model.SessionId != session || entry.Model.Revision != revision || entry.Closing) throw new InvalidOperationException("保存选择期间文档已变化，请重新保存。");
    if (saveAs) pdfWorkspace.ReserveSaveAsTarget(entry, path);
    PdfSaveResult? result = null;
    try
    {
      result = saveAs ? await entry.Model.SaveAsAsync(path, ct) : await entry.Model.SaveAsync(path, ct);
      if (!result.Saved) throw new InvalidOperationException(result.Error ?? "保存未完成");
      return PdfState(entry.Model);
    }
    finally
    {
      // 未确认可能已提交到新目标：在真正关闭该条目前继续占用目标。
      // 预约成功后才进入此 finally，同条目的第二次调用不能释放第一份预约。
      if (saveAs && result?.Disposition != PdfSaveDisposition.Unconfirmed) pdfWorkspace.ReleaseSaveAsTarget(entry);
    }
  }
  private async Task<PdfWorkbenchState?> ExportPdfDocumentsAsync(ExportPdfDocumentsCommand command, CancellationToken ct)
  {
    if (pdfWorkspace.Documents.Any(entry => entry.Model.HasSession && !entry.Model.CanCopyExport)) throw new InvalidOperationException("当前 Runtime 不支持副本导出");
    PdfExportItem[] plan = pdfWorkspace.CreateExportPlan(command.ModifiedOnly, command.Retry);
    string? directory;
    if (pickPdfFolder is not null) directory = await pickPdfFolder();
    else
    {
      var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
      picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
      directory = (await picker.PickSingleFolderAsync())?.Path;
    }
    if (directory is null || ActivePdf is null) return ActivePdf is { } model ? PdfState(model) : EmptyPdfState();
    return PublishPdfStartThenTrack(PdfState(ActivePdf), () => pdfWorkspace.ExportAsync(directory, command.ModifiedOnly, command.Retry,
      () => { if (ActivePdf is { } active) operations.Publish(PdfState(active)); }, ct, plan));
  }
  private PdfWorkbenchState CancelPdfExport() { pdfWorkspace.CancelExport(); return ActivePdf is { } active ? PdfState(active) : EmptyPdfState(); }
  private PdfWorkbenchState? SetPdfPreviewPosition(SetPdfPreviewPositionCommand command)
  {
    // 纯预览状态命令：按修订/页码校验后写入所属条目，过期迟到值直接忽略；
    // 不回发状态，避免高频位置更新每条都回声完整 PDF 状态。
    if (PdfEntry is not { } entry) return null;
    if (command.Position.Revision != entry.Model.Revision ||
      command.Position.Page < 0 || command.Position.Page >= entry.Model.PageCount) return null;
    entry.Preview = command.Position;
    return null;
  }
  private async Task<PdfWorkbenchState> SelectPdfPagesAsync(SelectPdfPagesCommand command, CancellationToken cancellationToken)
  {
    pdf ??= CreatePdfViewModel();
    if (command.Pages.Any(page => page < 0 || page >= pdf.PageCount))
    {
      throw new InvalidOperationException("The PDF page selection is stale.");
    }
    selectedPdfPages.Clear();
    foreach (int page in command.Pages)
    {
      selectedPdfPages.Add(page);
    }
    pdf.SelectedPage = selectedPdfPages.Order().FirstOrDefault(-1);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> SelectPdfPageAsync(SelectPdfPageCommand command, CancellationToken cancellationToken)
  {
    pdf ??= CreatePdfViewModel();
    if (command.Page < 0 || command.Page >= pdf.PageCount) throw new InvalidOperationException("The PDF page selection is stale.");
    if (command.Selected) selectedPdfPages.Add(command.Page); else selectedPdfPages.Remove(command.Page);
    pdf.SelectedPage = selectedPdfPages.Order().FirstOrDefault(-1);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> SetPdfWindowAsync(
    SetPdfWindowCommand command,
    CancellationToken cancellationToken)
  {
    pdf ??= CreatePdfViewModel();
    pdfWindowStart = DesktopWorkbenchCommandHandler.ClampWindowStart(command.Start, pdf.PageCount, 64);
    Interlocked.Increment(ref pdfWindowGeneration);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private PdfWorkbenchState? PublishPdfStartThenTrack(PdfWorkbenchState start, Func<Task> operation)
  {
    operations.Publish(start);
    Task task = operation(); lock (pdfBackgroundOperations) pdfBackgroundOperations.Add(task);
    operations.Track(task);
    _ = task.ContinueWith(done => { lock (pdfBackgroundOperations) pdfBackgroundOperations.Remove(done); },
      CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    return null;
  }

  internal PdfWorkbenchState EmptyPdfState() => new(false, "pdf.empty", 0, -1,
    Engines: PdfEngines(), TaskEngine: pdfTaskEngine, DocumentId: pdfWorkspace.EmptyDocumentId, Documents: []);
  private PdfWorkbenchState PdfState(PdfViewModel viewModel)
  {
    PdfDocumentEntry? previous = pdfContext.Value;
    pdfContext.Value = pdfWorkspace.For(viewModel);
    try { return PdfStateCore(viewModel); }
    finally { pdfContext.Value = previous; }
  }
  private PdfWorkbenchState PdfStateCore(PdfViewModel viewModel)
  {
    if (ReferenceEquals(ActivePdf, viewModel) && pdfPageResourceKey != PdfPageResourceKey(viewModel))
      ReleasePdfPageResources();
    SynchronizePdfMode();
    pdfWindowStart = DesktopWorkbenchCommandHandler.ClampWindowStart(pdfWindowStart, viewModel.PageCount, 64);
    return new PdfWorkbenchState(
      viewModel.IsBusy || viewModel.IsSettling,
      PdfStatusCode(viewModel),
      viewModel.PageCount,
      viewModel.SelectedPage,
      selectedPdfPages.Order().ToArray(),
      Enumerable.Range(
        pdfWindowStart,
        Math.Min(viewModel.PageCount - pdfWindowStart, 64))
      .Select(index => new PdfWorkbenchPage(
        index,
        PdfPageStatus(viewModel, index),
        pdfThumbnails.GetValueOrDefault(index),
        index < viewModel.Pages.Count &&
          viewModel.Pages[index].State == PdfPageState.Done &&
          viewModel.Pages[index].Result is not null
          ? pdfStructured.GetValueOrDefault(index).Reference
          : null,
        viewModel.Pages[index].Detected, viewModel.Pages[index].HasTextLayer, viewModel.Pages[index].AddedThisSession,
        viewModel.Pages[index].Rotation, viewModel.Pages[index].Width, viewModel.Pages[index].Height,
        viewModel.Pages[index].RecognitionRevision, viewModel.Pages[index].CorrectedAfterRecognition))
      .ToArray(),
      pdfWindowStart,
      PdfEngines(),
      pdfTaskEngine, viewModel.Revision, viewModel.IsModified, viewModel.DetectedCount, viewModel.TextLayerCount,
      viewModel.AddedCount, viewModel.Phase, viewModel.ProgressCurrent, viewModel.ProgressTotal, viewModel.Summary,
      viewModel.CanAddTextLayer, viewModel.ProcessingSettings,
      pdfPagePreview,
      pdfPageInspect,
      pdfPageInspectStatus, viewModel.SessionId, viewModel.CanInspectPage, viewModel.CanCorrectText,
      PdfEntry?.Id, pdfWorkspace.Documents.Where(entry => entry.Model.HasRemoteSession || entry.Model.IsBusy).Select(entry => new PdfDocumentSummary(entry.Id,
        entry.DisplayName, entry.Model.PageCount, entry.Model.IsModified,
        entry.Model.IsBusy || entry.Model.IsSettling, entry.Model.Phase, entry.CloseError is not null)).ToArray(),
      PdfEntry?.Preview, viewModel.CanCopyExport, pdfWorkspace.Exporting,
      pdfWorkspace.ExportItems.Select(item => new PdfExportSummary(item.DocumentId, item.Name, item.Revision,
        item.Status, item.Output is null ? null : Path.GetFileName(item.Output), item.Error)).ToArray(), pdfWorkspace.ExportGeneration);
  }

  /// <summary>
  /// PDF workbench 状态码：上次已结束操作的原生语义问题码（open/save/
  /// mutate/OCR 的失败/取消）优先于 PageCount 会话投影，不再被 pdf.empty/
  /// pdf.open 抹掉；运行中沿用既有会话投影。仅消费固定语义码，不解析中文
  /// Status，也不把含本地保存路径/异常细节的 Status 发送给 Web。
  /// </summary>
  private static string PdfStatusCode(PdfViewModel viewModel, bool? isBusy = null)
  {
    if (isBusy ?? viewModel.IsBusy)
    {
      return viewModel.PageCount > 0 ? "pdf.open" : "pdf.empty";
    }
    return viewModel.TerminalIssue switch
    {
      PdfIssueKind.BackendUnavailable => "pdf.backendUnavailable",
      PdfIssueKind.OutOfMemory => "pdf.outOfMemory",
      PdfIssueKind.Cancelled => "pdf.cancelled",
      PdfIssueKind.Failed => "pdf.failed",
      _ => viewModel.PageCount > 0 ? "pdf.open" : "pdf.empty",
    };
  }

  private IReadOnlyList<RecognitionEngineChoice>? PdfEngines()
  {
    string? defaultId = selectionPolicy.RuntimeDefaultModeId;
    return selectionPolicy.RecognitionEngines(pdfTaskModelPreset)?.Select(choice => choice with
    {
      // IsDefault 从目录投影继承；Selected 仅显式选择或（未覆盖时）默认。
      Selected = choice.Engine == pdfTaskEngine ||
        (pdfTaskEngine is null && choice.Engine == defaultId),
      IsTaskOverride = choice.Engine == pdfTaskEngine,
    }).ToArray();
  }

  private static string PdfPageResourceKey(PdfViewModel viewModel) =>
    $"{viewModel.SessionId}:{viewModel.Revision}:{viewModel.SelectedPage}:{viewModel.ProcessingSettings.RenderDpi}:{viewModel.ProcessingSettings.MaxPixels}";

  /// <summary>释放当前页检查/高清预览资源（revoke + 删除发布文件），不触碰 160px 缩略图缓存。</summary>
  private void RemovePdfStructured(int index)
  { if (pdfStructured.Remove(index, out var cached)) resources.ReleaseResource(cached.Reference); }
  private void ReleasePdfWindowResources(bool force = false)
  {
    if (!force && pdfContext.Value is { } owner && owner != pdfWorkspace.Active) return;
    Interlocked.Increment(ref pdfWindowGeneration);
    foreach (WorkbenchResourceReference reference in pdfThumbnails.Values) resources.ReleaseResource(reference);
    foreach (var value in pdfStructured.Values) resources.ReleaseResource(value.Reference);
    pdfThumbnails.Clear(); pdfStructured.Clear(); ReleasePdfPageResources(force);
  }
  private void ReleasePdfPageResources(bool force = false)
  {
    if (!force && pdfContext.Value is { } owner && owner != pdfWorkspace.Active) return;
    Interlocked.Increment(ref pdfPageResourceGeneration);
    if (pdfPagePreview is { } preview) resources.ReleaseResource(preview);
    if (pdfPageInspect is { } inspect) resources.ReleaseResource(inspect);
    pdfPagePreview = null;
    pdfPageInspect = null;
    pdfPageResourceKey = null;
    pdfPageInspectStatus = "pdf.inspect.none";
  }

  /// <summary>
  /// 确保当前选中页的检查 payload 与高清预览资源就绪：仅单页拉取，迟到结果
  /// 不覆盖新选页/新修订；任一失败进入可重试失败态（pdf.inspect.failed）。
  /// </summary>
  private async Task EnsurePdfPageResourcesAsync(
    PdfViewModel viewModel, CancellationToken cancellationToken)
  {
    if (!ReferenceEquals(ActivePdf, viewModel) || pdfWorkspace.For(viewModel)?.Closing == true) return;
    await pdfPageResourceGate.WaitAsync(cancellationToken);
    try { await LoadPdfPageResourcesAsync(viewModel, cancellationToken); }
    finally { pdfPageResourceGate.Release(); }
  }

  private async Task LoadPdfPageResourcesAsync(
    PdfViewModel viewModel, CancellationToken cancellationToken)
  {
    if (!ReferenceEquals(ActivePdf, viewModel)) return;
    SynchronizePdfMode();
    if (!viewModel.CanInspectPage) { ReleasePdfPageResources(); return; }
    int page = viewModel.SelectedPage;
    string? session = viewModel.SessionId;
    if (session is null || page < 0 || page >= viewModel.PageCount)
    {
      ReleasePdfPageResources();
      return;
    }
    long revision = viewModel.Revision;
    PdfProcessingSettings renderSettings = viewModel.ProcessingSettings;
    string key = PdfPageResourceKey(viewModel);
    if (pdfPageResourceKey != key)
    {
      ReleasePdfPageResources();
      pdfPageResourceKey = key;
      pdfPageInspectStatus = "pdf.inspect.loading";
    }
    if (pdfPagePreview is not null && pdfPageInspect is not null) return;
    long generation = Volatile.Read(ref pdfGeneration);
    long resourceGeneration = Volatile.Read(ref pdfPageResourceGeneration);
    bool CurrentResources() => ReferenceEquals(ActivePdf, viewModel)
      && viewModel.SessionId == session
      && viewModel.Revision == revision && viewModel.SelectedPage == page
      && pdfPageResourceKey == key && PdfPageResourceKey(viewModel) == key
      && resourceGeneration == Volatile.Read(ref pdfPageResourceGeneration)
      && generation == Volatile.Read(ref pdfGeneration);

    WorkbenchResourceReference? inspectReference = null;
    WorkbenchResourceReference? previewReference = null;
    try
    {
      VibeOCR.Runtime.Contracts.Generated.Wire.PageInspectResponse? inspect =
        await viewModel.FetchPageInspectAsync(page, cancellationToken);
      if (!CurrentResources()) return;
      if (inspect is null) { pdfPageInspectStatus = "pdf.inspect.failed"; return; }
      double width = page < viewModel.Pages.Count ? viewModel.Pages[page].Width : 0;
      double height = page < viewModel.Pages.Count ? viewModel.Pages[page].Height : 0;
      int dpi = width > 0 && height > 0
        ? renderSettings.DpiFor(width, height)
        : renderSettings.RenderDpi;
      byte[]? preview = await viewModel.RenderPreviewImageAsync(page, dpi, cancellationToken);
      if (!CurrentResources()) return;
      if (preview is not { Length: > 0 }) { pdfPageInspectStatus = "pdf.inspect.failed"; return; }
      inspectReference = await resources.PublishBytesAsync(JsonSerializer.SerializeToUtf8Bytes(inspect),
        "application/json; charset=utf-8", ".json", cancellationToken);
      previewReference = await resources.PublishBytesAsync(preview, "image/png", ".png", cancellationToken);
      if (!CurrentResources()) return;
      pdfPageInspect = inspectReference;
      pdfPagePreview = previewReference;
      inspectReference = null;
      previewReference = null;
      pdfPageInspectStatus = "pdf.inspect.ready";
    }
    catch (Exception)
    {
      if (CurrentResources()) pdfPageInspectStatus = "pdf.inspect.failed";
    }
    finally
    {
      if (inspectReference is not null) resources.ReleaseResource(inspectReference);
      if (previewReference is not null) resources.ReleaseResource(previewReference);
    }
  }
  private async Task<PdfWorkbenchState> SetCurrentPdfPageAsync(
    SetCurrentPdfPageCommand command, CancellationToken cancellationToken)
  {
    pdf ??= CreatePdfViewModel();
    if (command.Page < 0 || command.Page >= pdf.PageCount)
      throw new InvalidOperationException("The PDF page selection is stale.");
    pdf.SelectedPage = command.Page;
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> UpdatePdfBlockTextAsync(
    UpdatePdfBlockTextCommand command, CancellationToken cancellationToken)
  {
    pdf ??= CreatePdfViewModel();
    // 预期快照在进入写门前捕获；真正的核对在 ViewModel 写门内送出前执行。
    string? expectedSession = pdf.SessionId;
    long expectedRevision = pdf.Revision;
    if (expectedSession is null || command.SessionId != expectedSession || command.Revision != expectedRevision)
      throw new InvalidOperationException("The PDF block edit is stale; refresh the page and retry.");
    if (command.Page < 0 || command.Page >= pdf.PageCount || command.Page != pdf.SelectedPage)
      throw new InvalidOperationException("The PDF page selection is stale.");
    PdfBlockEditResult result = await pdf.UpdateBlockTextAsync(
      expectedSession, expectedRevision, command.Page, command.BlockIndex,
      command.NewText, command.ExpectedOldText, cancellationToken);
    if (!result.Applied && !result.Noop && result.Error is not null)
      throw new InvalidOperationException(result.Error);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> RetryPdfPageInspectAsync(CancellationToken cancellationToken)
  {
    pdf ??= CreatePdfViewModel();
    ReleasePdfPageResources();
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> PdfStateAsync(PdfViewModel viewModel, CancellationToken cancellationToken)
  {
    PdfDocumentEntry? previous = pdfContext.Value;
    pdfContext.Value = pdfWorkspace.For(viewModel);
    try { return await PdfResourcesStateAsync(viewModel, cancellationToken); }
    finally { pdfContext.Value = previous; }
  }
  private async Task<PdfWorkbenchState> PdfResourcesStateAsync(
    PdfViewModel viewModel,
    CancellationToken cancellationToken)
  {
    string? session = viewModel.SessionId; long revision = viewModel.Revision; long generation = Volatile.Read(ref pdfGeneration);
    long windowGeneration = Volatile.Read(ref pdfWindowGeneration);
    int windowStart = DesktopWorkbenchCommandHandler.ClampWindowStart(pdfWindowStart, viewModel.PageCount, 64);
    bool Current() => ReferenceEquals(ActivePdf, viewModel) && viewModel.SessionId == session && viewModel.Revision == revision && generation == Volatile.Read(ref pdfGeneration) && windowGeneration == Volatile.Read(ref pdfWindowGeneration);
    if (!Current()) return PdfState(viewModel);
    pdfWindowStart = windowStart;
    int visiblePageEnd = Math.Min(viewModel.PageCount, windowStart + 64);
    // 冻结窗口在 await 前回收旧范围；翻窗只使资源请求过期，不影响 OCR 任务。
    foreach (int cachedIndex in pdfThumbnails.Keys.Where(index => index < windowStart || index >= visiblePageEnd).ToArray()) { resources.ReleaseResource(pdfThumbnails[cachedIndex]); pdfThumbnails.Remove(cachedIndex); }
    foreach (int cachedIndex in pdfStructured.Keys.Where(index => index < windowStart || index >= visiblePageEnd).ToArray()) RemovePdfStructured(cachedIndex);
    for (int index = windowStart; index < visiblePageEnd; index++)
    {
      if (pdfThumbnails.ContainsKey(index)) continue;
      byte[]? thumbnail = await viewModel.RenderThumbnailAsync(index, cancellationToken);
      if (!Current()) return PdfState(viewModel);
      if (thumbnail is { Length: > 0 })
      {
        WorkbenchResourceReference reference = await resources.PublishBytesAsync(
          thumbnail,
          "image/png",
          ".png",
          cancellationToken);
        if (!Current()) { resources.ReleaseResource(reference); return PdfState(viewModel); }
        if (!pdfThumbnails.TryAdd(index, reference)) resources.ReleaseResource(reference);
      }
    }
    await viewModel.PrepareResultsAsync(windowStart, 64, cancellationToken);
    if (!Current()) return PdfState(viewModel);
    // 已完成页的结构化结果与缩略图同窗口发布：以 Result 对象身份缓存，
    // 旋转/删除/重开文档时整体失效，与单次/批量共用同一发布路径。
    for (int index = windowStart; index < visiblePageEnd; index++)
    {
      if (index >= viewModel.Pages.Count) break;
      RecognizeResponse? result = viewModel.Pages[index].Result;
      if (result?.ContentBlocks is not { Length: > 0 })
      {
        RemovePdfStructured(index);
        continue;
      }
      if (pdfStructured.TryGetValue(index, out var cached) &&
        ReferenceEquals(cached.Result, result))
      {
        continue;
      }
      WorkbenchResourceReference? reference = await resources.PublishStructuredResultAsync(
        result.ContentBlocks,
        viewModel.FetchResultAssetAsync,
        cancellationToken);
      if (!Current()) { if (reference is not null) resources.ReleaseResource(reference); return PdfState(viewModel); }
      if (reference is null)
      {
        RemovePdfStructured(index);
      }
      else
      {
        RemovePdfStructured(index);
        pdfStructured[index] = (result, reference);
      }
    }
    await EnsurePdfPageResourcesAsync(viewModel, cancellationToken);
    if (!Current()) return PdfState(viewModel);
    return PdfState(viewModel);
  }

  private int[] SelectedPdfPages(PdfViewModel viewModel) => selectedPdfPages
    .Where(page => page >= 0 && page < viewModel.PageCount)
    .Order()
    .ToArray();

  private void ResetPdfSelection(bool selectFirstPage)
  {
    selectedPdfPages.Clear();
    ReleasePdfWindowResources();
    pdfWindowStart = 0;
    if (selectFirstPage && pdf is { PageCount: > 0 })
    {
      selectedPdfPages.Add(0);
      pdf.SelectedPage = 0;
    }
    else if (pdf is not null)
    {
      pdf.SelectedPage = -1;
    }
  }

  private static string PdfPageStatus(PdfViewModel viewModel, int index) =>
    index < viewModel.Pages.Count
      ? $"pdf.page.{viewModel.Pages[index].State.ToString().ToLowerInvariant()}"
      : "pdf.page.none";

  internal async Task RefreshCatalogStateAsync(CancellationToken cancellationToken)
  {
    PdfViewModel? activeModel = ActivePdf;
    PdfWorkbenchState pdfProjection = activeModel is null ? EmptyPdfState() : await PdfStateAsync(activeModel, cancellationToken);
    operations.Publish(ReferenceEquals(ActivePdf, activeModel) ? pdfProjection : ActivePdf is { } replacement ? PdfState(replacement) : EmptyPdfState());
  }
  internal void Stop()
  {
    Interlocked.Increment(ref pdfGeneration);
    foreach (PdfDocumentEntry entry in pdfWorkspace.Documents) entry.Model.Cancel();
  }
  internal void Release() => pdfStructured.Clear();
}
