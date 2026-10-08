using System.Text.Json;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeOCR.App.Inference;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Features.Pdf;

public sealed class PdfViewModel(
    IInferenceClient inference,
    IPdfFileSource files) : INotifyPropertyChanged
{
  private readonly InferenceJobRunner _jobs = new(inference);
  private CancellationTokenSource? _activeRun;
  private Task _cancelSignal = Task.CompletedTask;
  private long _generation;
  private bool _isBusy;
  private int _inflight;
  private bool _modelAvailable;
  private bool _requiresReopen;
  private string? _unclosedSession;
  public bool IsSettling => _inflight > 0 || _requiresReopen;
  public long Revision { get; private set; }
  public bool IsModified { get; private set; }
  public string Phase { get; private set; } = "idle";
  public int ProgressCurrent { get; private set; }
  public int ProgressTotal { get; private set; }
  public int DetectedCount => Pages.Count(page => page.Detected);
  public int TextLayerCount => Pages.Count(page => page.Detected && page.HasTextLayer);
  public int AddedCount => Pages.Count(page => page.AddedThisSession);
  public string Summary { get; private set; } = "";
  public PdfProcessingSettings ProcessingSettings { get; private set; } = new();
  public bool CanAddTextLayer => (_recognitionMode?.PipelineId ?? "OCR") == "OCR" && _options?.UseDocUnwarping != true;
  public void SetProcessingSettings(PdfProcessingSettings settings) { settings.Validate(); ProcessingSettings = settings; Changed(); }

  private string _status = "请选择 PDF";
  private string? _sessionId;
  private string? _filePath;
  private int _pageCount;
  private int _selectedPage = -1;
  private RecognitionModeOption? _recognitionMode;
  private string? _taskModeId;
  private PaddleModeOptions? _options;
  private PdfIssueKind? _issue;

  public event PropertyChangedEventHandler? PropertyChanged;
  public ObservableCollection<PdfPageViewModel> Pages { get; } = [];
  public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
  public string Status { get => _status; private set => SetField(ref _status, value); }
  /// <summary>
  /// 上一次已结束操作的原生语义问题码；null 表示无待呈现问题（成功/空闲/关闭）。
  /// workbench 据此映射固定 pdf.* 状态码，不解析 <see cref="Status"/> 中文文案
  /// （其中含本地保存路径/异常细节，不得直接发往 Web）。
  /// </summary>
  public PdfIssueKind? TerminalIssue { get => _issue; private set => SetField(ref _issue, value); }
  public string? SessionId { get => _sessionId; private set => SetField(ref _sessionId, value); }
  public string? FilePath { get => _filePath; private set => SetField(ref _filePath, value); }
  public int PageCount { get => _pageCount; private set => SetField(ref _pageCount, value); }
  public int SelectedPage { get => _selectedPage; set => SetField(ref _selectedPage, value); }
  public bool HasSession => _sessionId is not null;

  /// <summary>
  /// 绑定 PDF OCR 的任务级识别模式；taskModeId 是用户显式选择的模式 id。
  /// 非空而 mode 为 null（目录缺失/环境切换）时 StartOcrAsync 必须拒绝，
  /// 不静默回退通用文字 OCR——PDF 与单次/批量共享同一模式合同。
  /// 选项原样冻结，提交时按绑定模式严格 ToWire：不支持或越界字段明确
  /// 拒绝提交，不静默丢弃（#110 AC2）。
  /// </summary>
  public void SetRecognitionMode(
      RecognitionModeOption? mode,
      PaddleModeOptions? options = null,
      string? taskModeId = null)
  {
    _options = mode is null ? null : options;
    _recognitionMode = mode;
    _taskModeId = taskModeId;
  }

  public async Task OpenAsync(CancellationToken ct) { string? path = await files.PickFileAsync(ct); if (path is null) { Status = "已取消选择"; return; } await OpenPathAsync(path, ct); }

  public async Task OpenPathAsync(string path, CancellationToken ct)
  {
    string? previousSession = _modelAvailable ? SessionId : null;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    _inflight++; Changed();
    TerminalIssue = null; IsBusy = true; Status = "正在打开";
    try
    {
      if (_unclosedSession is not null)
      {
        await inference.ClosePdfSessionAsync(_unclosedSession, CancellationToken.None);
        _unclosedSession = null;
      }
      if (previousSession is not null) await inference.ClosePdfSessionAsync(previousSession, CancellationToken.None);
      run.Token.ThrowIfCancellationRequested();
      // Opening creates a worker resource: await the real response even after local cancellation.
      PdfSessionOpenResult result = await inference.OpenPdfSessionAsync(path, null, CancellationToken.None);
      if (generation != Volatile.Read(ref _generation) || run.IsCancellationRequested)
      {
        if (result.Model is not null)
        {
          try { await inference.ClosePdfSessionAsync(result.SessionId, CancellationToken.None); }
          catch (Exception)
          {
            _unclosedSession = result.SessionId;
            _requiresReopen = true;
            Summary = "迟到会话关闭失败，请关闭文档重试释放";
            throw;
          }
        }
        return;
      }
      SessionId = result.SessionId; FilePath = result.FilePath; PageCount = result.PageCount;
      Pages.Clear(); for (int i = 0; i < PageCount; i++) Pages.Add(new PdfPageViewModel { Index = i });
      SelectedPage = -1;
      Revision++; IsModified = false; Summary = ""; _requiresReopen = false; _modelAvailable = result.Model is not null;
      if (result.Model is not null) ApplyModel(result.Model);
      Phase = "load"; Changed();
      await inference.ResetPdfCancelAsync(result.SessionId, CancellationToken.None);
      await inference.LoadPdfAsync(result.SessionId, progress => {
        if (generation != Volatile.Read(ref _generation)) return;
        UpdateProgress(progress);
        if (progress.TryGetProperty("page_payload", out JsonElement payload) && payload.ValueKind == JsonValueKind.Object &&
            payload.Deserialize<Wire.PdfPageInfoMirror>() is { } page) ApplyPage(page, detected: true);
      }, CancellationToken.None);
      if (generation != Volatile.Read(ref _generation)) return;
      Phase = "idle";
      Status = $"已打开 {PageCount} 页";
    }
    catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; } }
    catch (InferenceClientException e) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); } }
    catch (Exception) when (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Status = "打开失败"; }
    finally
    {
      await FinishRunAsync(generation, run);
    }
  }

  public async Task<byte[]?> RenderThumbnailAsync(int pageIndex, CancellationToken ct)
  {
    if (SessionId is null) return null;
    try { return await inference.RenderPdfPageAsync(SessionId, pageIndex, 160, ct); }
    catch { return null; }
  }

  /// <summary>授权取回某页识别结果的图片资产（与单次/批量同一接缝）。</summary>
  public Task<byte[]> FetchResultAssetAsync(
      string jobId, string itemId, string assetId, CancellationToken ct) =>
      inference.FetchResultAssetAsync(jobId, itemId, assetId, ct);

  public async Task RotateAsync(int[] pages, int angle, CancellationToken ct)
  {
    if (IsSettling) { Status = "后台操作尚未收尾"; return; }
    if (SessionId is null || pages.Length == 0) { Status = "请先选中要旋转的页面"; return; }
    if (!ValidatePages(pages)) return;
    await MutateAsync(async token => (await inference.RotatePdfPagesAsync(SessionId!, pages, angle, token)).PageCount, "正在旋转", ct);
  }

  public async Task DeletePagesAsync(int[] pages, CancellationToken ct)
  {
    if (IsSettling) { Status = "后台操作尚未收尾"; return; }
    if (SessionId is null || pages.Length == 0) return;
    if (!ValidatePages(pages)) return;
    int[] deleted = pages.Distinct().Order().ToArray();
    await MutateAsync(
        async token => (await inference.DeletePdfPagesAsync(SessionId!, deleted, token)).PageCount,
        "正在删除页面", ct, () => RemovePages(deleted));
  }

  public async Task StartOcrAsync(int[] pages, bool overwrite, CancellationToken ct, bool addTextLayer = false)
  {
    if (IsSettling) { Summary = "后台操作尚未收尾，请等待完成"; Changed(); return; }
    if (SessionId is null || pages.Length == 0) { Summary = "未选择处理页面"; Changed(); Status = "请先打开 PDF"; return; }
    if (!ValidatePages(pages)) return;
    pages = pages.Distinct().Order().ToArray();
    int skippedExisting = 0;
    if (addTextLayer)
    {
      if (!_modelAvailable || DetectedCount != PageCount) { Summary = "请等待全部页面文字层检测完成"; Changed(); return; }
      if (!CanAddTextLayer) { Summary = "当前模式无法提供可逆页坐标，请使用文字 OCR 且关闭文档去扭曲；提取/解析仍可使用"; Changed(); return; }
      if (!overwrite) { int requested = pages.Length; pages = pages.Where(index => !Pages[index].HasTextLayer).ToArray(); skippedExisting = requested - pages.Length; }
      if (pages.Length == 0) { Summary = "全部页面已有文字层，已跳过"; Changed(); return; }
    }
    PdfProcessingSettings settings = ProcessingSettings;
    long revision = Revision;
    string sessionId = SessionId;
    if (_taskModeId is not null && _recognitionMode is null)
    {
      throw new RecognitionModeUnavailableException(
          $"PDF 识别模式 {_taskModeId} 在当前环境不可用，已拒绝按通用文字识别执行；请在设置中检查运行环境后重试。");
    }
    string pipeline = _recognitionMode?.PipelineId ?? "OCR";
    OcrEngine? engine = _recognitionMode?.Engine;
    // 提交冻结点（页面渲染 await 前）：按绑定的模式对原始 typed 选项
    // 严格 ToWire；不支持或越界字段在这里明确拒绝提交，不静默丢弃后
    // 仍渲染/提交（#110 AC2）。
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? options;
    try
    {
      options = _options?.ToWire(_recognitionMode!);
    }
    catch (ArgumentException error)
    {
      Status = $"识别选项无效，已拒绝提交：{error.Message}";
      return;
    }
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    _inflight++; Changed();
    TerminalIssue = null; IsBusy = true; Status = "正在识别";
    foreach (int idx in pages) if (idx < Pages.Count) Pages[idx].State = PdfPageState.Processing;
    try
    {
      await inference.ResetPdfCancelAsync(sessionId, CancellationToken.None);
      int s = 0, f = 0;
      // 一次仅保留四页图片/结果，一个批次一个通用 job，不创建逐页顶层 job。
      foreach (int[] batch in pages.Chunk(4))
      {
        var inputs = new InferenceUploadInput[batch.Length];
        for (int i = 0; i < batch.Length; i++)
        {
          run.Token.ThrowIfCancellationRequested();
          int idx = batch[i];
          Phase = "render"; ProgressCurrent = Array.IndexOf(pages, idx); ProgressTotal = pages.Length; Changed();
          int dpi = _modelAvailable ? settings.DpiFor(Pages[idx].Width, Pages[idx].Height) : settings.RenderDpi;
          byte[] image = await inference.RenderPdfPreviewAsync(sessionId, idx, dpi, run.Token);
          run.Token.ThrowIfCancellationRequested();
          inputs[i] = new InferenceUploadInput($"page-{idx}", $"page-{idx + 1}.png", "image/png", image);
        }
        Phase = "ocr"; Changed();
        InferenceJobRun job = await _jobs.RunRecognitionAsync(pipeline, JobPriority.Background, inputs,
            options: options, cancellationToken: run.Token, engine: engine, waitForCancellation: true,
            progress: snapshot => { if (generation == Volatile.Read(ref _generation)) { ProgressCurrent = snapshot.Summary.Succeeded + snapshot.Summary.Failed; ProgressTotal = batch.Length; Changed(); } });
        if (generation != Volatile.Read(ref _generation) || revision != Revision) return;
        var writes = new List<Wire.BatchAddTextLayerPage>();
        foreach (int idx in batch)
        {
          ItemOutcome outcome = job.OutcomesByClientItemKey[$"page-{idx}"];
          if (outcome.State == ItemState.Succeeded)
          {
            RecognizeResponse result = RecognitionOutcomeMapper.ToResponse(outcome, pipeline);
            Pages[idx].Result = result; Pages[idx].OcrText = result.Text;
            Pages[idx].ResultReference = new PdfResultReference(job.Snapshot.JobId, outcome.ItemId, pipeline);
            if (!addTextLayer) { Pages[idx].State = PdfPageState.Done; s++; }
            else if (result.PreprocAngle is 0 or 90 or 180 or 270 && HasValidBlocks(result.RawBlocks))
            {
              writes.Add(new Wire.BatchAddTextLayerPage {
                Page = idx, OcrResult = new Dictionary<string, JsonElement> {
                  ["text_blocks"] = JsonSerializer.SerializeToElement(result.RawBlocks),
                  ["preproc_angle"] = JsonSerializer.SerializeToElement(result.PreprocAngle.Value),
                } });
            }
            else { Pages[idx].State = PdfPageState.Failed; f++; }
          }
          else { Pages[idx].State = outcome.State == ItemState.Cancelled ? PdfPageState.None : PdfPageState.Failed; f++; }
        }
        run.Token.ThrowIfCancellationRequested();
        if (writes.Count > 0)
        {
          Phase = "write"; ProgressCurrent = 0; ProgressTotal = writes.Count; Changed();
          // 不以本地 HTTP 等待取消模拟后台停止。worker 返回后再释放写门。
          Wire.PdfMutationResponse response = await inference.AddPdfTextLayersAsync(sessionId,
            new Wire.BatchAddTextLayerRequest { Pages = writes, Overwrite = overwrite, Save = false, PdfSettings = settings.ToWire() }, CancellationToken.None);
          if (SessionId != sessionId || revision != Revision) return;
          ApplyDiff(response.Diff);
          foreach (var write in writes)
          {
            int count = 0;
            if (response.Extra?.TryGetValue("results", out JsonElement results) == true &&
                results.TryGetProperty(write.Page.ToString(System.Globalization.CultureInfo.InvariantCulture), out JsonElement counts) && counts.ValueKind == JsonValueKind.Array)
              count = counts[0].GetInt32();
            if (count > 0) { Pages[write.Page].AddedThisSession = true; Pages[write.Page].State = PdfPageState.Done; s++; }
            else { Pages[write.Page].State = PdfPageState.Failed; f++; }
          }
        }
        if (generation != Volatile.Read(ref _generation)) { if (addTextLayer && s > 0) Revision++; return; }
        TrimResultCache(Math.Max(0, batch[^1] - 63), 64);
        ProgressCurrent = batch.Length; ProgressTotal = batch.Length; Changed();
      }
      if (addTextLayer && s > 0) Revision++;
      Summary = addTextLayer ? $"已添加 {s} 页、跳过 {skippedExisting} 页、失败 {f} 页；{(IsModified ? "尚未保存" : "当前修订已保存")}" : $"已提取/解析 {s} 页、失败 {f} 页";
      Changed();
      Status = $"OCR 完成：成功 {s} 页，失败 {f} 页";
    }
    catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; foreach (int idx in pages) if (idx < Pages.Count) Pages[idx].State = PdfPageState.None; Status = "已取消"; } }
    catch (InferenceClientException e)
    {
      MarkUnconfirmedOcr(sessionId, revision);
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); }
    }
    catch (Exception)
    {
      MarkUnconfirmedOcr(sessionId, revision);
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Status = "OCR 失败"; }
    }
    finally
    {
      if (generation == Volatile.Read(ref _generation))
        foreach (int idx in pages)
          if (Pages[idx].State == PdfPageState.Processing)
            Pages[idx].State = PdfPageState.Failed;
      if (_modelAvailable && SessionId == sessionId)
      {
        try { await RefreshModelAsync(CancellationToken.None); }
        catch (Exception) { if (SessionId == sessionId) { _requiresReopen = true; Summary = "后台结果回读失败，请关闭并重新打开文档复检"; } }
      }
      await FinishRunAsync(generation, run);
    }
  }

  public async Task SaveAsync(string path, CancellationToken ct)
  {
    if (SessionId is null) return;
    if (IsSettling) { Summary = "后台操作尚未收尾，请等待完成"; Changed(); return; }
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    _inflight++; Changed();
    TerminalIssue = null; IsBusy = true; Status = "正在保存";
    try {
      run.Token.ThrowIfCancellationRequested(); Phase = "save"; Changed();
      string saved = await inference.SavePdfWithSettingsAsync(SessionId, path, ProcessingSettings.ToWire(), CancellationToken.None);
      if (generation == Volatile.Read(ref _generation)) { IsModified = false; Summary = "当前修订已保存"; Status = $"已保存到 {saved}"; Changed(); }
    }
    catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; } }
    catch (InferenceClientException e) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); } }
    catch (Exception) when (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Status = "保存失败"; }
    finally { await FinishRunAsync(generation, run); }
  }

  public void Cancel()
  {
    if (Phase == "save") { Summary = "保存已进入不可取消的提交阶段，等待实际结果"; Changed(); return; }
    CancelActiveRun(); TerminalIssue = PdfIssueKind.Cancelled;
    Summary = IsSettling ? "已请求取消，等待后台实际收尾；已完成写入保留" : "已取消";
    Status = "已取消"; Changed();
  }
  public void CloseSession() { CancelActiveRun(); SessionId = null; FilePath = null; PageCount = 0; Pages.Clear(); _requiresReopen = false; _modelAvailable = false; IsModified = false; Summary = ""; SelectedPage = -1; TerminalIssue = null; Status = "请选择 PDF"; }

  public async Task CloseSessionAsync(CancellationToken ct)
  {
    string? session = _modelAvailable ? SessionId : null;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    _inflight++; Changed();
    try
    {
      if (_unclosedSession is not null)
      {
        await inference.ClosePdfSessionAsync(_unclosedSession, CancellationToken.None);
        _unclosedSession = null;
      }
      if (session is not null) await inference.ClosePdfSessionAsync(session, CancellationToken.None);
      if (generation == Volatile.Read(ref _generation)) CloseSession();
    }
    catch (Exception)
    {
      if (generation == Volatile.Read(ref _generation))
      {
        _requiresReopen = true;
        Summary = "文档关闭失败，保留会话以便重试释放";
      }
      throw;
    }
    finally { _inflight--; Changed(); }
  }

  private void MarkUnconfirmedOcr(string session, long revision)
  {
    if (Phase != "ocr" || SessionId != session || Revision != revision) return;
    _requiresReopen = true;
    Summary = "识别后台终态未确认，请关闭并重新打开文档";
  }

  private async Task MutateAsync(Func<CancellationToken, Task<int>> action, string runningStatus, CancellationToken ct, Action? onSuccess = null)
  {
    if (SessionId is null) return;
    if (IsSettling) { Summary = "后台操作尚未收尾，请等待完成"; Changed(); return; }
    string session = SessionId;
    long revision = Revision;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    _inflight++; Changed();
    TerminalIssue = null; IsBusy = true; Status = runningStatus;
    try { run.Token.ThrowIfCancellationRequested(); int count = await action(CancellationToken.None); if (SessionId == session && Revision == revision) { onSuccess?.Invoke(); PageCount = count; Revision++; IsModified = true; await RefreshModelAsync(CancellationToken.None); if (generation == Volatile.Read(ref _generation)) Status = "完成"; Changed(); } }
    catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; } }
    catch (InferenceClientException e) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); } }
    catch (Exception) when (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Status = "操作失败"; }
    finally { await FinishRunAsync(generation, run); }
  }

  private void CancelActiveRun()
  {
    Interlocked.Increment(ref _generation);
    var run = Interlocked.Exchange(ref _activeRun, null);
    run?.Cancel();
    if (run is not null && SessionId is { } session) _cancelSignal = SignalCancelAsync(session, Volatile.Read(ref _generation));
    // The operation owns disposal: a cancelled await may still access its token.
    IsBusy = false;
    foreach (PdfPageViewModel page in Pages)
      if (page.State == PdfPageState.Processing) page.State = PdfPageState.None;
  }

  private async Task FinishRunAsync(long generation, CancellationTokenSource run)
  {
    await _cancelSignal;
    if (generation == Volatile.Read(ref _generation)) IsBusy = false;
    Interlocked.CompareExchange(ref _activeRun, null, run);
    run.Dispose();
    _inflight--; if (_inflight == 0) Phase = "idle"; Changed();
  }

  private bool ValidatePages(int[] pages)
  {
    if (pages.All(index => index >= 0 && index < Pages.Count)) return true;
    TerminalIssue = PdfIssueKind.Failed;
    Status = "请选择有效的 PDF 页面";
    return false;
  }

  private void RemovePages(int[] deleted)
  {
    var removed = deleted.ToHashSet();
    PdfPageViewModel[] remaining = Pages.Where(page => !removed.Contains(page.Index)).ToArray();
    int selected = SelectedPage;
    Pages.Clear();
    for (int index = 0; index < remaining.Length; index++)
      Pages.Add(new PdfPageViewModel
      {
        Index = index,
        State = remaining[index].State,
        OcrText = remaining[index].OcrText,
        Result = remaining[index].Result,
        ResultReference = remaining[index].ResultReference,
        HasTextLayer = remaining[index].HasTextLayer, Detected = remaining[index].Detected,
        Width = remaining[index].Width, Height = remaining[index].Height, Rotation = remaining[index].Rotation,
        AddedThisSession = remaining[index].AddedThisSession,
      });
    SelectedPage = selected < 0 || remaining.Length == 0 ? -1
        : Math.Min(selected - deleted.Count(index => index < selected), remaining.Length - 1);
  }

  public async Task DeleteTextLayersAsync(int[] pages, CancellationToken ct)
  {
    if (SessionId is null || !ValidatePages(pages) || IsSettling) return;
    string session = SessionId;
    pages = pages.Distinct().Where(index => Pages[index].Detected && Pages[index].HasTextLayer).ToArray();
    if (pages.Length == 0) { Summary = "所选页面无文字层，已跳过"; Changed(); return; }
    CancelActiveRun(); long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct); _activeRun = run; _inflight++;
    IsBusy = true; TerminalIssue = null;
    try
    {
      run.Token.ThrowIfCancellationRequested();
      await inference.ResetPdfCancelAsync(session, CancellationToken.None);
      await inference.DeletePdfTextLayersAsync(session, pages, progress => {
        if (generation == Volatile.Read(ref _generation)) UpdateProgress(progress);
      }, CancellationToken.None);
      if (generation != Volatile.Read(ref _generation)) return;
      await RefreshModelAsync(CancellationToken.None); Revision++;
      foreach (int index in pages) { Pages[index].AddedThisSession = false; Pages[index].Result = null; Pages[index].ResultReference = null; Pages[index].State = PdfPageState.None; }
      int[] residual = pages.Where(index => Pages[index].HasTextLayer).ToArray();
      Summary = residual.Length == 0 ? "文字层删除完成，尚未保存" : $"删除后残留页：{string.Join(", ", residual.Select(index => index + 1))}；尚未保存";
    }
    catch (OperationCanceledException) { if (SessionId == session) TerminalIssue = PdfIssueKind.Cancelled; }
    catch (Exception) { if (SessionId == session && generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Summary = "删除文字层失败，请复检后重试"; } }
    finally
    {
      if (SessionId == session) { try { await RefreshModelAsync(CancellationToken.None); } catch (Exception) { if (SessionId == session) { _requiresReopen = true; Summary = "删除结果复检失败，请重开文档检测"; } } }
      await FinishRunAsync(generation, run);
    }
  }

  /// <summary>保留当前窗口结果；其他页只保留 supervisor job/item 引用。</summary>
  public async Task PrepareResultsAsync(int start, int count, CancellationToken ct)
  {
    string? session = SessionId;
    long revision = Revision;
    TrimResultCache(start, count);
    var needed = Pages.Skip(start).Take(count).Where(page => page.Result is null && page.ResultReference is not null)
        .GroupBy(page => page.ResultReference!.JobId).ToArray();
    foreach (var group in needed)
    {
      int sequence = 0;
      while (true)
      {
        JobUpdate update = await inference.ObserveAsync(group.Key, sequence, ct);
        if (SessionId != session || Revision != revision) return;
        foreach (PdfPageViewModel page in group)
        {
          ItemOutcome? outcome = update.Outcomes.FirstOrDefault(item => item.ItemId == page.ResultReference!.ItemId);
          if (outcome?.State == ItemState.Succeeded)
          {
            page.Result = RecognitionOutcomeMapper.ToResponse(outcome, page.ResultReference!.Pipeline);
            page.OcrText = page.Result.Text;
          }
        }
        if (!update.More) break;
        if (update.ThroughSequence <= sequence) throw new InvalidOperationException("PDF result stream did not advance.");
        sequence = update.ThroughSequence;
      }
    }
  }
  private void TrimResultCache(int start, int count)
  {
    foreach (PdfPageViewModel page in Pages)
      if (page.Index < start || page.Index >= start + count) { page.Result = null; page.OcrText = ""; }
  }

  private async Task SignalCancelAsync(string session, long generation)
  {
    try { await inference.CancelPdfAsync(session, CancellationToken.None); }
    catch (Exception) { if (SessionId == session && generation == Volatile.Read(ref _generation)) { Summary = "取消请求未确认，等待后台实际收尾"; Changed(); } }
  }
  private async Task RefreshModelAsync(CancellationToken ct)
  {
    if (_modelAvailable && SessionId is { } session)
    {
      long generation = Volatile.Read(ref _generation);
      Wire.PdfDocumentMirror model = await inference.GetPdfModelAsync(session, ct);
      if (SessionId == session && generation == Volatile.Read(ref _generation)) ApplyModel(model);
    }
  }
  private void ApplyModel(Wire.PdfDocumentMirror model)
  {
    if (model.Pages is null) throw new InvalidOperationException("PDF model pages missing.");
    PageCount = model.Pages.Count; IsModified = model.IsModified == true;
    foreach (var page in model.Pages) ApplyPage(page, detected: Pages.ElementAtOrDefault(page.PageIndex)?.Detected == true);
    Changed();
  }
  private void ApplyDiff(Wire.ModelDiff diff)
  {
    if (diff.FullModel is not null) ApplyModel(diff.FullModel);
    foreach (var page in diff.ReplacedPages ?? []) ApplyPage(page, detected: true);
    if (diff.ModifiedFlag is { } modified) IsModified = modified;
    Changed();
  }
  private void ApplyPage(Wire.PdfPageInfoMirror page, bool detected)
  {
    if (page.PageIndex < 0 || page.PageIndex >= Pages.Count) throw new InvalidOperationException("PDF model index mismatch.");
    PdfPageViewModel target = Pages[page.PageIndex];
    target.AddedThisSession = page.HasOcrTextLayer == true || page.OcrTextBlocks is { Count: > 0 };
    if (target.HasTextLayer && page.HasTextLayer != true) { target.Result = null; target.ResultReference = null; target.OcrText = ""; target.State = PdfPageState.None; }
    target.Detected = detected; target.HasTextLayer = page.HasTextLayer == true; target.Rotation = page.Rotation ?? 0;
    if (page.Rect is { Count: 4 } rect) { target.Width = rect[2].GetDouble() - rect[0].GetDouble(); target.Height = rect[3].GetDouble() - rect[1].GetDouble(); }
    _modelAvailable = true; Changed();
  }
  private void UpdateProgress(JsonElement progress)
  {
    Phase = progress.GetProperty("phase").GetString() ?? "idle";
    ProgressCurrent = progress.GetProperty("current").GetInt32(); ProgressTotal = progress.GetProperty("total").GetInt32(); Changed();
  }
  private static bool HasValidBlocks(JsonElement[]? blocks) => blocks is not null && blocks.Any(block =>
    block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()) &&
    block.TryGetProperty("bbox", out var bbox) && bbox.ValueKind == JsonValueKind.Array && bbox.GetArrayLength() == 4 &&
    bbox.EnumerateArray().All(value => value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble()) && value.GetDouble() is >= 0 and <= 1000) &&
    bbox[2].GetDouble() > bbox[0].GetDouble() && bbox[3].GetDouble() > bbox[1].GetDouble());
  private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

  private static string LocalizeV2(HttpV2ErrorCode code) => code switch { HttpV2ErrorCode.OutOfMemory => "内存或显存不足", HttpV2ErrorCode.BackendUnavailable or HttpV2ErrorCode.TransientBackend => "Supervisor 暂不可用", HttpV2ErrorCode.Cancelled => "已取消", _ => "操作失败" };
  private static PdfIssueKind IssueFromV2(HttpV2ErrorCode code) => code switch { HttpV2ErrorCode.OutOfMemory => PdfIssueKind.OutOfMemory, HttpV2ErrorCode.BackendUnavailable or HttpV2ErrorCode.TransientBackend => PdfIssueKind.BackendUnavailable, HttpV2ErrorCode.Cancelled => PdfIssueKind.Cancelled, _ => PdfIssueKind.Failed };
  private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}

/// <summary>PDF 操作的原生语义终态问题码；v2 错误映射与 <see cref="PdfViewModel"/> 的 LocalizeV2 一致。</summary>
public enum PdfIssueKind { Cancelled, Failed, BackendUnavailable, OutOfMemory }

public enum PdfPageState { None, Processing, Done, Failed }

public sealed class PdfPageViewModel : INotifyPropertyChanged
{
  private PdfPageState _state = PdfPageState.None;
  private string _ocrText = string.Empty;
  public event PropertyChangedEventHandler? PropertyChanged;
  public int Index { get; init; }
  public bool Detected { get; internal set; }
  public bool HasTextLayer { get; internal set; }
  public bool AddedThisSession { get; internal set; }
  public int Rotation { get; internal set; }
  public double Width { get; internal set; }
  public double Height { get; internal set; }
  public RecognizeResponse? Result { get; internal set; }
  internal PdfResultReference? ResultReference { get; set; }
  public PdfPageState State { get => _state; set { if (_state != value) { _state = value; PropertyChanged?.Invoke(this, new(nameof(State))); } } }
  public string OcrText { get => _ocrText; set { if (_ocrText != value) { _ocrText = value; PropertyChanged?.Invoke(this, new(nameof(OcrText))); } } }
}

public interface IPdfFileSource { Task<string?> PickFileAsync(CancellationToken cancellationToken); }

internal sealed record PdfResultReference(string JobId, string ItemId, string Pipeline);
