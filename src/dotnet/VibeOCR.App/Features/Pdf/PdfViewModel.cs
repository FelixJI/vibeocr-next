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
  private TaskCompletionSource? _settlement;
  public Task WaitForSettlementAsync() => _settlement?.Task ?? Task.CompletedTask;
  public async Task CancelAndSettleAsync() { Cancel(); await WaitForSettlementAsync(); await _cancelSignal; }
  private void BeginRun() { if (_inflight++ == 0) _settlement = new(TaskCreationOptions.RunContinuationsAsynchronously); }
  private void EndRun() { if (--_inflight == 0) { Phase = "idle"; _settlement?.TrySetResult(); } }
  private bool _modelAvailable;
  private bool _requiresReopen;
  private string? _unclosedSession;
  public bool IsSettling => _inflight > 0 || _requiresReopen;
  public long Revision { get; private set; }
  public IReadOnlyList<int?>? LastPageMapping { get; private set; }
  public bool IsModified { get; private set; }
  public string Phase { get; private set; } = "idle";
  public int ProgressCurrent { get; private set; }
  public int ProgressTotal { get; private set; }
  public int DetectedCount => Pages.Count(page => page.Detected);
  public int TextLayerCount => Pages.Count(page => page.Detected && page.HasTextLayer);
  public int AddedCount => Pages.Count(page => page.AddedThisSession);
  public string Summary { get; private set; } = "";
  public PdfProcessingSettings ProcessingSettings { get; private set; } = new();
  public bool CanInspectPage { get; private set; }
  public bool CanCorrectText { get; private set; }
  public bool CanCopyExport { get; private set; }
  public void SetInspectionCapabilities(IReadOnlyCollection<string> capabilities)
  {
    CanCopyExport = capabilities.Contains(VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.PDF_COPY_EXPORT_V1);
    CanInspectPage = capabilities.Contains(VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.PDF_PAGE_INSPECT_V1);
    CanCorrectText = CanInspectPage && capabilities.Contains(VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.PDF_BLOCK_EDIT_V1);
  }
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
  public bool HasRemoteSession => HasSession || _unclosedSession is not null;

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

  public Task<string?> PickFileAsync(CancellationToken ct) => files.PickFileAsync(ct);

  public async Task OpenAsync(CancellationToken ct) { string? path = await files.PickFileAsync(ct); if (path is null) { Status = "已取消选择"; return; } await OpenPathAsync(path, ct); }

  public async Task OpenPathAsync(string path, CancellationToken ct)
  {
    string? previousSession = _modelAvailable ? SessionId : null;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    BeginRun(); Changed();
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
      LastPageMapping = null;
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
    string session = SessionId; long revision = Revision; long generation = Volatile.Read(ref _generation);
    try { byte[] image = await inference.RenderPdfPageAsync(session, pageIndex, 160, ct); return SessionId == session && Revision == revision && generation == Volatile.Read(ref _generation) ? image : null; }
    catch { return null; }
  }

  /// <summary>当前页高清预览（真实 DPI，受后端 16M 像素预算约束）；过期会话/修订返回 null，迟到图不替换新选页。</summary>
  public async Task<byte[]?> RenderPreviewImageAsync(int pageIndex, int dpi, CancellationToken ct)
  {
    if (SessionId is null) return null;
    string session = SessionId; long revision = Revision; long generation = Volatile.Read(ref _generation);
    try { byte[] image = await inference.RenderPdfPreviewAsync(session, pageIndex, dpi, ct); return SessionId == session && Revision == revision && generation == Volatile.Read(ref _generation) ? image : null; }
    catch { return null; }
  }

  /// <summary>当前页检查 payload（OCR 块/原生文字层显示空间归一化投影）；过期返回 null。</summary>
  public async Task<Wire.PageInspectResponse?> FetchPageInspectAsync(int pageIndex, CancellationToken ct)
  {
    if (SessionId is null) return null;
    string session = SessionId; long revision = Revision; long generation = Volatile.Read(ref _generation);
    if (!CanInspectPage) return null;
    try { Wire.PageInspectResponse payload = await inference.InspectPdfPageAsync(session, pageIndex, ct); return SessionId == session && Revision == revision && generation == Volatile.Read(ref _generation) ? payload : null; }
    catch { return null; }
  }

  /// <summary>
  /// 提交单块文字编辑：复用全 PDF 写门（与 MutateAsync 同构），送出前在门内
  /// 核对调用者预期 session/revision；真正写入不可取消，收尾后释放门。
  /// 无变化明确 noop（不推进修订/dirty）；证据缺失/失联按未确认处理，不假
  /// 成功也不假称未提交；迟到返回不污染新会话。
  /// </summary>
  public async Task<PdfBlockEditResult> UpdateBlockTextAsync(
    string expectedSession,
    long expectedRevision,
    int page,
    int blockIndex,
    string newText,
    string? expectedOldText,
    CancellationToken ct)
  {
    if (!CanCorrectText) return new(false, false, "当前 Runtime 不支持原子文字校正");
    if (SessionId is null) return new(false, false, "请先打开 PDF 文档");
    if (string.IsNullOrWhiteSpace(newText)) return new(false, false, "新文本不能为空");
    if (IsSettling) { Summary = "后台操作尚未收尾，请等待完成后再编辑"; Changed(); return new(false, false, Summary); }
    string session = SessionId;
    long revision = Revision;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    BeginRun(); Changed();
    TerminalIssue = null; IsBusy = true; Status = "正在更新块文字";
    bool submitted = false;
    void Unconfirmed()
    {
      if (SessionId == session && submitted)
      {
        _requiresReopen = true;
        Summary = "块编辑写入结果未确认，请关闭并重新打开文档复检";
        Changed();
      }
    }
    try
    {
      run.Token.ThrowIfCancellationRequested();
      // 提交前在写门内核对调用者预期与当前权威状态（handler 早检查不能替代）
      if (SessionId != expectedSession || Revision != expectedRevision)
      {
        Summary = "页面或块状态已变化，请刷新后重试"; Changed();
        return new(false, false, "编辑目标已过期，请刷新后重试");
      }
      if (page < 0 || page >= Pages.Count) return new(false, false, "页面选择已过期，请重新选择");
      Phase = "write"; Changed();
      submitted = true;
      Wire.PdfMutationResponse response = await inference.UpdatePdfBlockTextAsync(session, new Wire.UpdateBlockTextRequest
      {
        Page = page,
        BlockIndex = blockIndex,
        NewText = newText,
        ExpectedOldText = expectedOldText,
        PdfSettings = ProcessingSettings.ToWire(),
      }, CancellationToken.None);
      if (SessionId != session || revision != Revision) return new(false, false, "编辑目标已过期，请刷新后重试");
      if (response.Extra is { } extra &&
          extra.TryGetValue("changed", out JsonElement changedFlag) &&
          changedFlag.ValueKind is JsonValueKind.True or JsonValueKind.False)
      {
        if (!changedFlag.GetBoolean())
        {
          if (response.Diff is not null) ApplyDiff(response.Diff);
          Status = "块文字未变化";
          if (generation == Volatile.Read(ref _generation)) Changed();
          return new(false, true, null);
        }
        if (response.Diff?.ReplacedPages?.Any(replaced => replaced.PageIndex == page) != true)
        {
          Unconfirmed();
          return new(false, false, "块更新缺少页结果证据，请重开文档复检");
        }
        Revision++;
        IsModified = true;
        ApplyDiff(response.Diff!);
        Pages[page].CorrectedAfterRecognition = Pages[page].Result is not null || Pages[page].ResultReference is not null;
        Status = "块文字已更新，尚未保存";
        Summary = Status;
        if (generation == Volatile.Read(ref _generation)) Changed();
        return new(true, false, null);
      }
      // 缺 changed 证据：不假成功，也不推进修订/dirty —— 结果未确认
      Unconfirmed();
      return new(false, false, "块更新结果未确认，请重开文档复检");
    }
    catch (OperationCanceledException)
    {
      Unconfirmed();
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; Changed(); }
      return new(false, false, "编辑已取消，请复检后重试");
    }
    catch (InferenceClientException e)
    {
      if (e.Code is not (HttpV2ErrorCode.ValidationError or HttpV2ErrorCode.Unauthorized or HttpV2ErrorCode.ForbiddenLoopback or HttpV2ErrorCode.ResourceNotFound or HttpV2ErrorCode.RuntimeCapabilityUnavailable or HttpV2ErrorCode.RuntimeOperationNotFound)) Unconfirmed();
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); Changed(); }
      return new(false, false, "块更新未提交或未确认，请复检后重试");
    }
    catch (Exception error) when (SessionId == session)
    {
      // 传输失败可能已写入：不宣称未提交，按未确认处理（仅原会话）
      Unconfirmed();
      TerminalIssue = PdfIssueKind.Failed;
      Status = "块更新失败，结果未确认，请复检后重试";
      Changed();
      return new(false, false, $"{Status}（{error.GetType().Name}: {error.Message}）");
    }
    catch (Exception error)
    {
      return new(false, false, $"编辑结果未确认，请复检后重试（{error.GetType().Name}）");
    }
    finally { await FinishRunAsync(generation, run); }
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
    if (angle is not (90 or -90 or 180 or 270)) { Summary = "无效的旋转角度"; Changed(); return; }
    pages = pages.Distinct().ToArray();
    await MutateAsync(token => inference.RotatePdfPagesAsync(SessionId!, pages, angle, token), "正在旋转", ct,
      onSuccess: () => RotateResults(pages, angle));
  }

  public async Task DeletePagesAsync(int[] pages, CancellationToken ct)
  {
    if (IsSettling) { Status = "后台操作尚未收尾"; return; }
    if (SessionId is null || pages.Length == 0) return;
    if (!ValidatePages(pages)) return;
    int[] deleted = pages.Distinct().Order().ToArray();
    if (deleted.Length == PageCount) { Summary = "PDF 必须保留至少一页，不能删除最后一页"; Changed(); return; }
    await MutateAsync(
        token => inference.DeletePdfPagesAsync(SessionId!, deleted, token),
        "正在删除页面", ct, _ => Enumerable.Range(0, Pages.Count).Where(index => !deleted.Contains(index)).Select(index => (int?)index).ToArray());
  }

  public Task OrientPagesAsync(int[] pages, bool landscape, CancellationToken ct)
  {
    if (!ValidatePages(pages)) return Task.CompletedTask;
    int[] targets = pages.Distinct().Where(index => landscape ? Pages[index].Width < Pages[index].Height : Pages[index].Width > Pages[index].Height).ToArray();
    if (targets.Length == 0) { Summary = "页面已满足方向，无需旋转"; Changed(); return Task.CompletedTask; }
    return RotateAsync(targets, 90, ct);
  }

  public Task InsertBlankAsync(int afterIndex, double width, double height, CancellationToken ct)
  {
    if (!ValidInsertion(afterIndex) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
      throw new ArgumentException("Invalid PDF insertion position or size.");
    return MutateAsync(token => inference.InsertPdfBlankAsync(SessionId!, afterIndex, width, height, token),
      "正在插入空白页", ct, _ => InsertionMapping(afterIndex, 1));
  }

  public async Task InsertFromAsync(int afterIndex, CancellationToken ct)
  {
    if (IsSettling || !ValidInsertion(afterIndex)) return;
    string? session = SessionId; long revision = Revision;
    string? source = await files.PickFileAsync(ct);
    if (source is null || SessionId != session || Revision != revision || IsSettling) return;
    int oldCount = PageCount;
    await MutateAsync(token => inference.InsertPdfFromAsync(session!, source, afterIndex, token),
      "正在插入 PDF", ct, result => InsertionMapping(afterIndex, result.PageCount - oldCount));
  }

  public Task ReorderAsync(int[] order, CancellationToken ct)
  {
    if (order.Length != PageCount || !order.Order().SequenceEqual(Enumerable.Range(0, PageCount)))
      throw new ArgumentException("PDF page order must be a permutation.");
    if (order.SequenceEqual(Enumerable.Range(0, PageCount))) return Task.CompletedTask;
    return MutateAsync(token => inference.ReorderPdfAsync(SessionId!, order, token),
      "正在重排页面", ct, _ => order.Select(index => (int?)index).ToArray());
  }

  private bool ValidInsertion(int afterIndex) => SessionId is not null && afterIndex >= -1 && afterIndex < PageCount;
  private int?[] InsertionMapping(int afterIndex, int inserted)
  {
    if (inserted < 1) throw new InvalidOperationException("Inserted PDF page count is invalid.");
    var mapping = Enumerable.Range(0, Pages.Count).Select(index => (int?)index).ToList();
    mapping.InsertRange(afterIndex + 1, Enumerable.Repeat<int?>(null, inserted)); return mapping.ToArray();
  }

  private void RotateResults(int[] pages, int angle)
  {
    foreach (int index in pages)
    {
      PdfPageViewModel page = Pages[index];
      if (page.Result?.PreprocAngle is { } old) page.Result = page.Result with { PreprocAngle = (old + angle + 360) % 360 };
      if (page.ResultReference is { } reference) page.ResultReference = reference with { RotationOffset = (reference.RotationOffset + angle + 360) % 360 };
    }
  }

  public async Task CorrectOrientationAsync(int[] pages, CancellationToken ct)
  {
    if (SessionId is null || IsSettling || !ValidatePages(pages)) return;
    pages = pages.Distinct().Order().ToArray();
    if (pages.Length == 0) { Summary = "未选择处理页面"; Changed(); return; }
    RecognitionModeOption? mode = _recognitionMode;
    if (mode is null || mode.Availability != "ready")
    {
      Summary = "自动文字朝向需要已配置且就绪的 Paddle 通用文字 OCR 环境，请在组件设置中检查"; Changed(); return;
    }
    if (mode.Engine != OcrEngine.PaddleOcr || mode.PipelineId != "OCR" || !mode.SupportedOptions.Contains("use_doc_orientation_classify", StringComparer.Ordinal))
    {
      Summary = "当前引擎/模式不支持文字朝向，请在组件设置中选择 Paddle 通用文字 OCR"; Changed(); return;
    }
    if (!mode.SupportedOptions.Contains("local_models_only", StringComparer.Ordinal))
    {
      Summary = "当前运行环境不支持仅使用本地模型的文字朝向，请在组件设置中更新运行环境"; Changed(); return;
    }
    var options = new Dictionary<string, JsonElement>(((_options ?? new PaddleModeOptions()) with
      { UseDocOrientationClassify = true, UseDocUnwarping = false }).ToWire(mode), StringComparer.Ordinal)
      { ["local_models_only"] = JsonSerializer.SerializeToElement(true) };
    string session = SessionId; long revision = Revision;
    CancelActiveRun(); long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct); _activeRun = run; BeginRun();
    IsBusy = true; TerminalIssue = null;
    int corrected = 0, unchanged = 0, failed = 0, completed = 0;
    string detail = "";
    void Progress() { ProgressCurrent = completed; ProgressTotal = pages.Length; Summary = $"已纠正 {corrected} 页、无需处理 {unchanged} 页、失败 {failed} 页、未处理 {pages.Length - completed} 页{detail}"; Changed(); }
    try
    {
      await inference.ResetPdfCancelAsync(session, CancellationToken.None);
      Progress();
      foreach (int index in pages)
      {
        run.Token.ThrowIfCancellationRequested();
        if (SessionId != session || Revision != revision) return;
        Pages[index].State = PdfPageState.Processing;
        try
        {
          Phase = "render"; Progress();
          byte[] image = await inference.RenderPdfPreviewAsync(session, index,
            ProcessingSettings.DpiFor(Pages[index].Width, Pages[index].Height), CancellationToken.None);
          run.Token.ThrowIfCancellationRequested();
          Phase = "ocr"; Progress();
          InferenceJobRun job = await _jobs.RunRecognitionAsync("OCR", JobPriority.Background,
            [new InferenceUploadInput($"page-{index}", $"page-{index + 1}.png", "image/png", image)],
            options, run.Token, OcrEngine.PaddleOcr, waitForCancellation: true);
          run.Token.ThrowIfCancellationRequested();
          if (SessionId != session || Revision != revision || generation != Volatile.Read(ref _generation)) return;
          ItemOutcome outcome = job.OutcomesByClientItemKey[$"page-{index}"];
          if (outcome.State != ItemState.Succeeded)
          {
            failed++;
            detail = outcome.ErrorDetail.TryGetValue("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "local_models_not_prepared"
              ? "；本地模型未准备，请先在识别页面显式运行 Paddle OCR 准备模型，或打开组件设置检查"
              : $"；第 {index + 1} 页方向检测失败";
            Pages[index].State = PdfPageState.Failed;
          }
          else
          {
            RecognizeResponse result = RecognitionOutcomeMapper.ToResponse(outcome, "OCR");
            if (result.DocOrientationAngle is not (0 or 90 or 180 or 270))
            { failed++; detail = $"；第 {index + 1} 页未返回可判定的方向角度"; Pages[index].State = PdfPageState.Failed; }
            else if (string.IsNullOrWhiteSpace(result.Text) || !HasValidBlocks(result.RawBlocks))
            { failed++; detail = $"；第 {index + 1} 页无可判定文字"; Pages[index].State = PdfPageState.Failed; }
            else if (result.DocOrientationAngle == 0)
            { unchanged++; Pages[index].State = PdfPageState.Done; }
            else
            {
              Phase = "rotate"; Progress();
              int angle = -result.DocOrientationAngle.Value;
              PdfMutateResult mutation = await inference.RotatePdfPagesAsync(session, [index], angle, CancellationToken.None);
              if (SessionId != session || Revision != revision) return;
              RotateResults([index], angle); Revision++; revision = Revision; IsModified = true; LastPageMapping = null;
              if (mutation.Diff is not null) ApplyDiff(mutation.Diff);
              else await RefreshModelAsync(CancellationToken.None);
              corrected++; Pages[index].State = PdfPageState.Done;
            }
          }
          completed++; Progress();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
          failed++; completed++; Pages[index].State = PdfPageState.Failed;
          detail = $"；第 {index + 1} 页{(Phase == "render" ? "渲染" : Phase == "rotate" ? "旋转" : "方向检测")}失败";
          if (Phase == "rotate") { _requiresReopen = true; detail += "，写入结果未确认，请重开文档复检"; }
          else { MarkUnconfirmedOcr(session, revision); if (_requiresReopen) detail += "，后台终态未确认，请重开文档复检"; }
          Progress(); if (_requiresReopen) break;
        }
      }
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = failed > 0 ? PdfIssueKind.Failed : null; Status = "文字朝向处理完成"; }
    }
    catch (OperationCanceledException) { TerminalIssue = PdfIssueKind.Cancelled; detail = "；已取消，已完成页保留"; Progress(); }
    catch (Exception) { MarkUnconfirmedOcr(session, revision); TerminalIssue = PdfIssueKind.Failed; detail = "；方向操作失败"; Progress(); }
    finally
    {
      if (SessionId == session)
      {
        foreach (int index in pages) if (Pages[index].State == PdfPageState.Processing) Pages[index].State = PdfPageState.None;
        if (_modelAvailable)
          try { await RefreshModelAsync(CancellationToken.None); }
          catch (Exception) { _requiresReopen = true; detail = "；后台结果回读失败，请重开文档复检"; }
        Progress();
      }
      await FinishRunAsync(generation, run);
    }
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
    BeginRun(); Changed();
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
            Pages[idx].RecognitionRevision = Revision;
            Pages[idx].CorrectedAfterRecognition = false;
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

  public Task<PdfSaveResult> SaveAsync(string path, CancellationToken ct) => SaveCoreAsync(path, false, false, ct);
  public Task<PdfSaveResult> SaveAsAsync(string path, CancellationToken ct) => SaveCoreAsync(path, false, true, ct);
  public Task<PdfSaveResult> ExportCopyAsync(string path, long revision, PdfProcessingSettings settings, CancellationToken ct)
  {
    if (Revision != revision) return Task.FromResult(new PdfSaveResult(PdfSaveDisposition.Rejected, path, "文档修订已变化"));
    return SaveCoreAsync(path, true, false, ct, settings);
  }
  private async Task<PdfSaveResult> SaveCoreAsync(string path, bool copy, bool rebind, CancellationToken ct, PdfProcessingSettings? settings = null)
  {
    if (SessionId is null || IsSettling) { Summary = "后台操作尚未收尾，请等待完成"; Changed(); return new(PdfSaveDisposition.Rejected, path, Summary); }
    if ((copy || rebind) && !CanCopyExport) return new(PdfSaveDisposition.Rejected, path, "当前 Runtime 不支持副本导出和目标切换");
    string session = SessionId;
    long revision = Revision;
    path = Path.GetFullPath(path);
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    BeginRun(); Changed();
    TerminalIssue = null; IsBusy = true; Status = copy ? "正在导出副本" : "正在保存";
    bool submitted = false;
    try
    {
      run.Token.ThrowIfCancellationRequested(); Phase = "save"; Changed(); submitted = true;
      string saved = await inference.SavePdfOperationAsync(session, path, (settings ?? ProcessingSettings).ToWire(), copy, rebind, !copy, CancellationToken.None);
      if (string.IsNullOrWhiteSpace(saved) || !string.Equals(Path.GetFullPath(saved), path, StringComparison.OrdinalIgnoreCase)
        || SessionId != session || Revision != revision)
        throw new InvalidOperationException("保存结果未确认");
      if (!copy) { IsModified = false; if (rebind) FilePath = path; }
      Summary = copy ? "副本已导出，当前文档修改状态保留" : "当前修订已保存";
      Status = $"已保存到 {saved}"; Changed();
      return new(PdfSaveDisposition.Saved, saved);
    }
    catch (OperationCanceledException) when (!submitted)
    { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; return new(PdfSaveDisposition.Cancelled, path); }
    catch (InferenceClientException e) when (e.Code is HttpV2ErrorCode.ValidationError or HttpV2ErrorCode.Unauthorized or HttpV2ErrorCode.ForbiddenLoopback or HttpV2ErrorCode.ResourceNotFound or HttpV2ErrorCode.RuntimeCapabilityUnavailable or HttpV2ErrorCode.RuntimeOperationNotFound)
    { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); return new(PdfSaveDisposition.Failed, path, Status); }
    catch (Exception)
    {
      _requiresReopen = submitted && !copy;
      TerminalIssue = PdfIssueKind.Failed; Status = submitted ? "保存结果未确认，保留修改与目标" : "保存失败";
      return new(submitted ? PdfSaveDisposition.Unconfirmed : PdfSaveDisposition.Failed, path, Status);
    }
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
    BeginRun(); Changed();
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
    finally { EndRun(); Changed(); }
  }

  private void MarkUnconfirmedOcr(string session, long revision)
  {
    if (Phase != "ocr" || SessionId != session || Revision != revision) return;
    _requiresReopen = true;
    Summary = "识别后台终态未确认，请关闭并重新打开文档";
  }

  private async Task MutateAsync(Func<CancellationToken, Task<PdfMutateResult>> action, string runningStatus, CancellationToken ct,
    Func<PdfMutateResult, int?[]>? mapping = null, Action? onSuccess = null)
  {
    if (SessionId is null) return;
    if (IsSettling) { Summary = "后台操作尚未收尾，请等待完成"; Changed(); return; }
    string session = SessionId;
    long revision = Revision;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    BeginRun(); Changed();
    TerminalIssue = null; IsBusy = true; Status = runningStatus;
    bool submitted = false;
    void Unconfirmed() { if (SessionId == session && submitted) { _requiresReopen = true; Summary = "页面写入结果未确认，请关闭并重新打开文档复检"; Changed(); } }
    try
    {
      run.Token.ThrowIfCancellationRequested(); Phase = "write"; Changed();
      submitted = true;
      PdfMutateResult result = await action(CancellationToken.None);
      if (SessionId != session || Revision != revision) return;
      LastPageMapping = mapping?.Invoke(result);
      if (LastPageMapping is not null) RemapPages(LastPageMapping);
      onSuccess?.Invoke(); Revision++; IsModified = true;
      if (result.Diff is not null) ApplyDiff(result.Diff);
      else { PageCount = result.PageCount; await RefreshModelAsync(CancellationToken.None); }
      Summary = "页面操作完成，尚未保存";
      if (generation == Volatile.Read(ref _generation)) Status = "完成";
      Changed();
    }
    catch (OperationCanceledException) { Unconfirmed(); if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; } }
    catch (InferenceClientException e)
    {
      if (e.Code is not (HttpV2ErrorCode.ValidationError or HttpV2ErrorCode.Unauthorized or HttpV2ErrorCode.ForbiddenLoopback or HttpV2ErrorCode.ResourceNotFound or HttpV2ErrorCode.RuntimeCapabilityUnavailable or HttpV2ErrorCode.RuntimeOperationNotFound)) Unconfirmed();
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); }
    }
    catch (Exception) when (SessionId == session) { Unconfirmed(); TerminalIssue = PdfIssueKind.Failed; Status = "操作失败"; }
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
    EndRun(); Changed();
  }

  private bool ValidatePages(int[] pages)
  {
    if (pages.All(index => index >= 0 && index < Pages.Count)) return true;
    TerminalIssue = PdfIssueKind.Failed;
    Status = "请选择有效的 PDF 页面";
    return false;
  }

  private void RemapPages(IReadOnlyList<int?> mapping)
  {
    PdfPageViewModel[] old = Pages.ToArray();
    int selected = SelectedPage;
    Pages.Clear();
    for (int index = 0; index < mapping.Count; index++)
    {
      PdfPageViewModel source = mapping[index] is { } original ? old[original] : new PdfPageViewModel { Index = index, Detected = true };
      Pages.Add(new PdfPageViewModel
      {
        Index = index,
        State = source.State, OcrText = source.OcrText, Result = source.Result,
        RecognitionRevision = source.RecognitionRevision, CorrectedAfterRecognition = source.CorrectedAfterRecognition,
        ResultReference = source.ResultReference, HasTextLayer = source.HasTextLayer, Detected = source.Detected,
        Width = source.Width, Height = source.Height, Rotation = source.Rotation,
        AddedThisSession = source.AddedThisSession,
      });
    }
    SelectedPage = mapping.Select((value, index) => (value, index)).Where(pair => pair.value == selected).Select(pair => pair.index).FirstOrDefault(-1);
    PageCount = Pages.Count;
  }

  public async Task DeleteTextLayersAsync(int[] pages, CancellationToken ct)
  {
    if (SessionId is null || !ValidatePages(pages) || IsSettling) return;
    string session = SessionId;
    pages = pages.Distinct().Where(index => Pages[index].Detected && Pages[index].HasTextLayer).ToArray();
    if (pages.Length == 0) { Summary = "所选页面无文字层，已跳过"; Changed(); return; }
    CancelActiveRun(); long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct); _activeRun = run; BeginRun();
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
            if (page.Result.PreprocAngle is { } angle)
              page.Result = page.Result with { PreprocAngle = (angle + page.ResultReference.RotationOffset) % 360 };
            page.OcrText = page.Result.Text;
          }
        }
        if (!update.More) break;
        if (update.ThroughSequence <= sequence) throw new InvalidOperationException("PDF result stream did not advance.");
        sequence = update.ThroughSequence;
      }
    }
  }
  public void TrimResultCache(int start, int count)
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
      long generation = Volatile.Read(ref _generation); long revision = Revision;
      Wire.PdfDocumentMirror model = await inference.GetPdfModelAsync(session, ct);
      if (SessionId == session && Revision == revision && generation == Volatile.Read(ref _generation)) ApplyModel(model);
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

/// <summary>单块文字编辑提交结果：applied/noop 二选一，失败携带用户可读原因。</summary>
public sealed record PdfBlockEditResult(bool Applied, bool Noop, string? Error);

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
  public long? RecognitionRevision { get; set; }
  public bool CorrectedAfterRecognition { get; set; }
  internal PdfResultReference? ResultReference { get; set; }
  public PdfPageState State { get => _state; set { if (_state != value) { _state = value; PropertyChanged?.Invoke(this, new(nameof(State))); } } }
  public string OcrText { get => _ocrText; set { if (_ocrText != value) { _ocrText = value; PropertyChanged?.Invoke(this, new(nameof(OcrText))); } } }
}

public interface IPdfFileSource { Task<string?> PickFileAsync(CancellationToken cancellationToken); }

internal sealed record PdfResultReference(string JobId, string ItemId, string Pipeline, int RotationOffset = 0);

public enum PdfSaveDisposition { Saved, Cancelled, Rejected, Failed, Unconfirmed }
public sealed record PdfSaveResult(PdfSaveDisposition Disposition, string Target, string? Error = null)
{ public bool Saved => Disposition == PdfSaveDisposition.Saved; }
