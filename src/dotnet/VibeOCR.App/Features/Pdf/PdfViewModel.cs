using System.Text.Json;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeOCR.App.Inference;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Features.Pdf;

public sealed class PdfViewModel(
    IInferenceClient inference,
    IPdfFileSource files,
    Func<CancellationToken, Task>? ensureRuntimeReady = null) : INotifyPropertyChanged
{
  private readonly InferenceJobRunner _jobs = new(inference);
  private PdfDemandCoordinator? _demand;
  private long _nextPageKey;
  private int _detectedCount, _textLayerCount, _addedCount;
  private bool UsesDemand => CanDemandPages && _demand is not null;
  internal bool CanIssueDemandReads => _modelAvailable && !_requiresReopen;
  internal bool HasBusinessRun => _inflight > 0;
  internal bool HasUnknownPresence => _detectedCount < Pages.Count &&
    Pages.Any(page => page.DetectionState == PdfDetectionState.Unknown);
  internal IInferenceClient Inference => inference;
  internal void AttachDemand(PdfDemandCoordinator demand) => _demand = demand;
  private long NextPageKey() => ++_nextPageKey;

  private async ValueTask<IAsyncDisposable?> AcquireWriteAsync(
      string session, long revision, long generation, CancellationToken ct)
  {
    ct.ThrowIfCancellationRequested();
    if (!UsesDemand) return NoopWriteLease.Instance;
    IAsyncDisposable? lease = await _demand!.AcquireWriteAsync(this, session, ct);
    if (lease is null) return null;
    if (SessionId == session && Revision == revision && generation == Volatile.Read(ref _generation) &&
        !ct.IsCancellationRequested) return lease;
    await lease.DisposeAsync();
    ct.ThrowIfCancellationRequested();
    return null;
  }

  private sealed class NoopWriteLease : IAsyncDisposable
  {
    public static readonly NoopWriteLease Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
  private CancellationTokenSource? _activeRun;
  private Task _cancelSignal = Task.CompletedTask;
  private long _generation;
  private long _resultCacheGeneration;
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
  /// <summary>页结构重建后、任何携带新结构的发布前同步触发；宿主选择集须在此完成重映射。</summary>
  public event Action<PdfViewModel, IReadOnlyList<int?>>? PagesRemapped;
  public bool IsModified { get; private set; }
  public string Phase { get; private set; } = "idle";
  public int ProgressCurrent { get; private set; }
  public int ProgressTotal { get; private set; }
  public int DetectedCount => UsesDemand ? _detectedCount : Pages.Count(page => page.Detected);
  public int TextLayerCount => UsesDemand ? _textLayerCount : Pages.Count(page => page.Detected && page.HasTextLayer);
  public int AddedCount => UsesDemand ? _addedCount : Pages.Count(page => page.AddedThisSession);
  public string Summary { get; private set; } = "";
  public PdfProcessingSettings ProcessingSettings { get; private set; } = new();
  public bool CanInspectPage { get; private set; }
  public bool CanCorrectText { get; private set; }
  public bool CanCopyExport { get; private set; }
  /// <summary>pdf.demand.v1 能力：严格为 true 时启用按需调度新路径；legacy 保持旧路径。</summary>
  public bool CanDemandPages { get; private set; }
  public void SetInspectionCapabilities(IReadOnlyCollection<string> capabilities)
  {
    CanCopyExport = capabilities.Contains(VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.PDF_COPY_EXPORT_V1);
    CanInspectPage = capabilities.Contains(VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.PDF_PAGE_INSPECT_V1);
    CanCorrectText = CanInspectPage && capabilities.Contains(VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.PDF_BLOCK_EDIT_V1);
    if (!HasSession) CanDemandPages = capabilities.Contains(VibeOCR.Runtime.Contracts.Generated.RuntimeProtocol.PDF_DEMAND_V1);
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
  internal bool RuntimePreparationIncomplete { get; private set; }

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
    RuntimePreparationIncomplete = false;
    TerminalIssue = null; IsBusy = true; Status = "正在打开";
    if (!HasSession) FilePath = path;
    try
    {
      if (ensureRuntimeReady is not null)
      {
        Status = "正在准备运行环境";
        RuntimePreparationIncomplete = true;
        await ensureRuntimeReady(run.Token);
        run.Token.ThrowIfCancellationRequested();
        if (generation == Volatile.Read(ref _generation)) RuntimePreparationIncomplete = false;
      }
      if (_unclosedSession is not null)
      {
        await inference.ClosePdfSessionAsync(_unclosedSession, CancellationToken.None);
        _unclosedSession = null;
      }
      if (previousSession is not null)
      {
        await using var lease = await AcquireWriteAsync(previousSession, Revision, generation, run.Token);
        if (lease is null) throw new InvalidOperationException("读端尚未确认暂停，旧文档未关闭");
        await inference.ClosePdfSessionAsync(previousSession, CancellationToken.None);
        if (_demand is not null) await _demand.WaitForReadDrainAsync(previousSession);
        _demand?.Forget(previousSession);
      }
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
      Pages.Clear(); _detectedCount = _textLayerCount = _addedCount = 0; for (int i = 0; i < PageCount; i++) Pages.Add(new PdfPageViewModel { Index = i, PageKey = NextPageKey(), DetectionState = PdfDetectionState.Unknown });
      SelectedPage = -1;
      Revision++; IsModified = false; Summary = ""; _requiresReopen = false; _modelAvailable = result.Model is not null;
      if (result.Model is not null) ApplyModel(result.Model);
      if (UsesDemand && _modelAvailable)
      {
        Status = $"已打开 {PageCount} 页";
        return;
      }
      Phase = "load"; Changed();
      await inference.ResetPdfCancelAsync(result.SessionId, CancellationToken.None);
      await inference.LoadPdfAsync(result.SessionId, progress => {
        if (generation != Volatile.Read(ref _generation)) return;
        UpdateProgress(progress);
        if (progress.TryGetProperty("page_payload", out JsonElement payload) && payload.ValueKind == JsonValueKind.Object &&
            payload.Deserialize<Wire.PdfPageInfoMirror>() is { } page) { ApplyPage(page, detected: true); Changed(); }
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
  /// 当前页检查的已确认几何回填：与 ApplyPage 同一显示空间参考系，仅更新
  /// Width/Height/Rotation；不改变 presence 状态（unknown 不冒充 known）、
  /// 文字层/OCR/编辑元数据/dirty，也不推进修订。调用方已核验 session/读
  /// epoch；这里再以页身份（PageKey/ContentGeneration）丢弃迟到结果。
  /// </summary>
  internal void ApplyInspectGeometry(int pageIndex, long pageKey, long contentGeneration, Wire.PageInspectResponse inspect)
  {
    if (SessionId is null || pageIndex < 0 || pageIndex >= Pages.Count) return;
    if (inspect.Rect is not { Count: 4 } rect || Number(rect[0]) is not { } x0 || Number(rect[1]) is not { } y0 ||
        Number(rect[2]) is not { } x1 || Number(rect[3]) is not { } y1) return;
    double width = x1 - x0, height = y1 - y0;
    int rotation = ((inspect.Rotation % 360) + 360) % 360;
    if (!(width > 0) || !(height > 0)) return;
    PdfPageViewModel page = Pages[pageIndex];
    if (page.PageKey != pageKey || page.ContentGeneration != contentGeneration) return;
    if (page.Width == width && page.Height == height && page.Rotation == rotation) return;
    page.Width = width;
    page.Height = height;
    page.Rotation = rotation;
    Changed();
  }

  private static double? Number(JsonElement value) =>
    value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;

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
    IAsyncDisposable? lease = null;
    async Task Unconfirmed()
    {
      if (SessionId == session && submitted)
      {
        if (UsesDemand) await ReconcileUnconfirmedAsync([page]);
        else _requiresReopen = true;
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
      // 写栅栏：ACK 失败/身份过期不发写、不标 unconfirmed（submitted 保持 false）。
      lease = await AcquireWriteAsync(session, revision, generation, run.Token);
      if (lease is null)
      {
        Summary = SessionId == session && Revision == revision ? "读端尚未确认暂停，本次校正未发送" : "页面已变化，本次校正未发送";
        Changed();
        return new(false, false, Summary);
      }
      Phase = "write"; Changed();
      run.Token.ThrowIfCancellationRequested();
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
          await Unconfirmed();
          return new(false, false, "块更新缺少页结果证据，请重开文档复检");
        }
        Revision++;
        IsModified = true;
        InvalidateContent([page]);
        ApplyDiff(response.Diff!);
        Pages[page].CorrectedAfterRecognition = Pages[page].Result is not null || Pages[page].ResultReference is not null;
        Status = "块文字已更新，尚未保存";
        Summary = Status;
        if (generation == Volatile.Read(ref _generation)) Changed();
        return new(true, false, null);
      }
      // 缺 changed 证据：不假成功，也不推进修订/dirty —— 结果未确认
      await Unconfirmed();
      return new(false, false, "块更新结果未确认，请重开文档复检");
    }
    catch (OperationCanceledException)
    {
      await Unconfirmed();
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; Changed(); }
      return new(false, false, "编辑已取消，请复检后重试");
    }
    catch (InferenceClientException e)
    {
      if (e.Code is not (HttpV2ErrorCode.ValidationError or HttpV2ErrorCode.Unauthorized or HttpV2ErrorCode.ForbiddenLoopback or HttpV2ErrorCode.ResourceNotFound or HttpV2ErrorCode.RuntimeCapabilityUnavailable or HttpV2ErrorCode.RuntimeOperationNotFound)) await Unconfirmed();
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); Changed(); }
      return new(false, false, "块更新未提交或未确认，请复检后重试");
    }
    catch (Exception error) when (SessionId == session)
    {
      // 传输失败可能已写入：不宣称未提交，按未确认处理（仅原会话）
      await Unconfirmed();
      TerminalIssue = PdfIssueKind.Failed;
      Status = "块更新失败，结果未确认，请复检后重试";
      Changed();
      return new(false, false, $"{Status}（{error.GetType().Name}: {error.Message}）");
    }
    catch (Exception error)
    {
      return new(false, false, $"编辑结果未确认，请复检后重试（{error.GetType().Name}）");
    }
    finally
    {
      if (lease is not null) await lease.DisposeAsync();
      await FinishRunAsync(generation, run);
    }
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
      onSuccess: () => { RotateResults(pages, angle); InvalidateContent(pages); }, uncertainPages: pages);
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
    if (!ValidatePages(pages) || SessionId is not { } session) return Task.CompletedTask;
    int[] requested = pages.Distinct().ToArray();
    int[] targets = [];
    return MutateAsync(token => inference.RotatePdfPagesAsync(session, targets, 90, token), "正在旋转", ct,
      onSuccess: () => { RotateResults(targets, 90); InvalidateContent(targets); },
      prepare: async token =>
      {
        if (!await EnsurePresenceAsync(session, requested, token)) return false;
        if (requested.Any(index => !Pages[index].HasReliableGeometry))
        {
          Summary = "页面几何尚未就绪，请等待检测完成后重试"; Changed(); return false;
        }
        targets = requested.Where(index => landscape ? Pages[index].Width < Pages[index].Height : Pages[index].Width > Pages[index].Height).ToArray();
        if (targets.Length > 0) return true;
        Summary = "页面已满足方向，无需旋转"; Changed(); return false;
      }, uncertainPages: requested);
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
        bool submitted = false;
        try
        {
          if (!await EnsurePresenceAsync(session, [index], run.Token))
          {
            failed++; completed++; Pages[index].State = PdfPageState.Failed;
            detail = $"；第 {index + 1} 页检测失败"; Progress(); continue;
          }
          Phase = "render"; Progress();
          byte[] image = await RenderBusinessPreviewAsync(session, index,
            ProcessingSettings.DpiFor(Pages[index].Width, Pages[index].Height), UsesDemand ? run.Token : CancellationToken.None);
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
              await using var lease = await AcquireWriteAsync(session, revision, generation, run.Token);
              if (lease is null)
              {
                failed++; completed++; Pages[index].State = PdfPageState.Failed;
                detail = "；读端尚未确认暂停，旋转未发送";
                Progress(); continue;
              }
              submitted = true;
              try
              {
                PdfMutateResult mutation = await inference.RotatePdfPagesAsync(session, [index], angle, CancellationToken.None);
                if (SessionId != session || Revision != revision) return;
                RotateResults([index], angle); Revision++; revision = Revision; IsModified = true; LastPageMapping = null;
                InvalidateContent([index]);
                if (mutation.Diff is not null) ApplyDiff(mutation.Diff);
                else await RefreshModelAsync(CancellationToken.None);
              }
              catch (Exception) when (UsesDemand && SessionId == session)
              {
                await ReconcileUnconfirmedAsync([index]);
                throw;
              }
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
          if (submitted) { _requiresReopen = true; detail += "，写入结果未确认，请重开文档复检"; }
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
        if (_modelAvailable && !UsesDemand)
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
      if (!UsesDemand && (!_modelAvailable || DetectedCount != PageCount)) { Summary = "请等待全部页面文字层检测完成"; Changed(); return; }
      if (!CanAddTextLayer) { Summary = "当前模式无法提供可逆页坐标，请使用文字 OCR 且关闭文档去扭曲；提取/解析仍可使用"; Changed(); return; }
      if (!UsesDemand && !overwrite) { int requested = pages.Length; pages = pages.Where(index => !Pages[index].HasTextLayer).ToArray(); skippedExisting = requested - pages.Length; }
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
      var merged = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
      if (_options is not null)
        foreach (var pair in _options.ToWire(_recognitionMode!)) merged.Add(pair.Key, pair.Value);
      options = merged.Count == 0 ? null : merged;
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
      foreach (int[] targets in pages.Chunk(4))
      {
        int[] batch = targets;
        if (!await EnsurePresenceAsync(sessionId, targets, run.Token))
        {
          f += targets.Length;
          foreach (int page in targets) Pages[page].State = PdfPageState.Failed;
          continue;
        }
        if (UsesDemand && addTextLayer && !overwrite)
        {
          batch = targets.Where(index => !Pages[index].HasTextLayer).ToArray();
          skippedExisting += targets.Length - batch.Length;
          foreach (int index in targets.Except(batch)) Pages[index].State = PdfPageState.None;
        }
        if (batch.Length == 0) continue;
        var inputs = new InferenceUploadInput[batch.Length];
        for (int i = 0; i < batch.Length; i++)
        {
          run.Token.ThrowIfCancellationRequested();
          int idx = batch[i];
          Phase = "render"; ProgressCurrent = Array.IndexOf(pages, idx); ProgressTotal = pages.Length; Changed();
          int dpi = _modelAvailable ? settings.DpiFor(Pages[idx].Width, Pages[idx].Height) : settings.RenderDpi;
          byte[] image = await RenderBusinessPreviewAsync(sessionId, idx, dpi, run.Token);
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
          await using var lease = await AcquireWriteAsync(sessionId, revision, generation, run.Token);
          if (lease is null)
          {
            foreach (int idx in batch) Pages[idx].State = PdfPageState.None;
            Summary = "读端尚未确认暂停，本批文字层未发送"; Changed();
            return;
          }
          try
          {
            Wire.PdfMutationResponse response = await inference.AddPdfTextLayersAsync(sessionId,
              new Wire.BatchAddTextLayerRequest { Pages = writes, Overwrite = overwrite, Save = false, PdfSettings = settings.ToWire() }, CancellationToken.None);
            if (SessionId != sessionId || revision != Revision) return;
            var written = new List<int>();
            foreach (var write in writes)
            {
              if (response.Extra?.TryGetValue("results", out JsonElement results) == true &&
                  results.ValueKind == JsonValueKind.Object &&
                  results.TryGetProperty(write.Page.ToString(System.Globalization.CultureInfo.InvariantCulture), out JsonElement counts) &&
                  counts.ValueKind == JsonValueKind.Array && counts.GetArrayLength() > 0 &&
                  counts[0].ValueKind == JsonValueKind.Number && counts[0].TryGetInt32(out int count) && count > 0)
              {
                written.Add(write.Page);
                Pages[write.Page].State = PdfPageState.Done;
                s++;
              }
              else { Pages[write.Page].State = PdfPageState.Failed; f++; }
            }
            if (written.Count > 0)
            {
              Revision++; revision = Revision;
              InvalidateContent(written);
            }
            if (response.Diff is not null) ApplyDiff(response.Diff);
            else if (_modelAvailable) await RefreshModelAsync(CancellationToken.None);
            foreach (int index in written)
            {
              CountPage(Pages[index], -1);
              Pages[index].AddedThisSession = true;
              CountPage(Pages[index], 1);
            }
            Changed();
          }
          catch (Exception) when (UsesDemand && SessionId == sessionId)
          {
            await ReconcileUnconfirmedAsync(writes.Select(write => write.Page));
            throw;
          }
        }
        if (generation != Volatile.Read(ref _generation)) return;
        TrimResultCache(Math.Max(0, batch[^1] - 63), 64);
        ProgressCurrent = batch.Length; ProgressTotal = batch.Length; Changed();
      }
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
      if (_modelAvailable && SessionId == sessionId && !UsesDemand)
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
    PdfProcessingSettings saveSettings = settings ?? ProcessingSettings;
    long revision = Revision;
    path = Path.GetFullPath(path);
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    BeginRun(); Changed();
    TerminalIssue = null; IsBusy = true; Status = copy ? "正在导出副本" : "正在保存";
    bool submitted = false;
    IAsyncDisposable? lease = null;
    try
    {
      run.Token.ThrowIfCancellationRequested();
      // 保存含 copy 也是写：过写栅栏；ACK 失败不发保存（Rejected，非 unconfirmed）。
      lease = await AcquireWriteAsync(session, revision, generation, run.Token);
      if (lease is null)
      {
        Summary = SessionId == session && Revision == revision ? "读端尚未确认暂停，本次保存未发送" : "文档已变化，本次保存未发送";
        Changed();
        return new(PdfSaveDisposition.Rejected, path, Summary);
      }
      Phase = "save"; Changed();
      run.Token.ThrowIfCancellationRequested();
      submitted = true;
      string saved = await inference.SavePdfOperationAsync(session, path, saveSettings.ToWire(), copy, rebind, !copy, CancellationToken.None);
      if (string.IsNullOrWhiteSpace(saved) || !string.Equals(Path.GetFullPath(saved), path, StringComparison.OrdinalIgnoreCase)
        || SessionId != session || Revision != revision)
        throw new InvalidOperationException("保存结果未确认");
      if (!copy) { IsModified = false; if (rebind) FilePath = path; }
      if (UsesDemand && !copy && saveSettings.CleanOnSave) InvalidateContent(Enumerable.Range(0, Pages.Count));
      Summary = copy ? "副本已导出，当前文档修改状态保留" : "当前修订已保存";
      Status = $"已保存到 {saved}"; Changed();
      return new(PdfSaveDisposition.Saved, saved);
    }
    catch (OperationCanceledException) when (!submitted)
    { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; return new(PdfSaveDisposition.Cancelled, path); }
    catch (InferenceClientException e) when (submitted && SessionId == session && Revision == revision
      && TrySaveOutcome(e, path, out bool committed, out bool usable, out string? recovery))
    {
      _requiresReopen = !usable;
      if (committed)
      {
        if (!copy) { IsModified = false; if (rebind) FilePath = path; }
        if (UsesDemand && !copy && saveSettings.CleanOnSave) InvalidateContent(Enumerable.Range(0, Pages.Count));
        Summary = copy ? "副本已写入磁盘，当前修改状态保留" : "当前修订已写入磁盘";
        if (!usable) Summary += "，请重开已保存文档";
        Status = $"已保存到 {path}";
        return new(PdfSaveDisposition.Saved, path, Summary);
      }
      TerminalIssue = PdfIssueKind.Failed;
      Status = "保存未提交，保留修改与目标";
      Summary = usable ? "可重试或另存到其他路径" : "会话不可用，请从恢复位置恢复修改";
      if (recovery is not null)
      {
        if (usable) Summary = "目标存在未完成保存，请先打开目标恢复，或另存到其他路径";
        Summary += $"；恢复位置：{recovery}";
      }
      return new(PdfSaveDisposition.Failed, path, Status);
    }
    catch (InferenceClientException e) when (e.Code is HttpV2ErrorCode.ValidationError or HttpV2ErrorCode.Unauthorized or HttpV2ErrorCode.ForbiddenLoopback or HttpV2ErrorCode.ResourceNotFound or HttpV2ErrorCode.RuntimeCapabilityUnavailable or HttpV2ErrorCode.RuntimeOperationNotFound)
    { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); return new(PdfSaveDisposition.Failed, path, Status); }
    catch (Exception)
    {
      _requiresReopen = submitted && !copy;
      if (UsesDemand && _requiresReopen) await ReconcileUnconfirmedAsync(Enumerable.Range(0, Pages.Count));
      TerminalIssue = PdfIssueKind.Failed; Status = submitted ? "保存结果未确认，保留修改与目标" : "保存失败";
      return new(submitted ? PdfSaveDisposition.Unconfirmed : PdfSaveDisposition.Failed, path, Status);
    }
    finally
    {
      if (lease is not null) await lease.DisposeAsync();
      await FinishRunAsync(generation, run);
    }
  }

  private static bool TrySaveOutcome(InferenceClientException error, string path,
    out bool committed, out bool usable, out string? recovery)
  {
    committed = usable = false; recovery = null;
    var detail = error.Detail;
    if (error.Code != HttpV2ErrorCode.InternalError || error.ReasonCode != "pdf_save_outcome"
      || detail.Keys.Any(key => key is not ("reason_code" or "committed" or "session_usable" or "path" or "recovery_path"))
      || !detail.TryGetValue("committed", out JsonElement saved) || saved.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
      || !detail.TryGetValue("session_usable", out JsonElement active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
      || !detail.TryGetValue("path", out JsonElement target) || target.ValueKind != JsonValueKind.String)
      return false;
    try
    {
      string? targetPath = target.GetString();
      if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath)
        || !string.Equals(Path.GetFullPath(targetPath), path, StringComparison.OrdinalIgnoreCase)) return false;
      if (detail.TryGetValue("recovery_path", out JsonElement location))
      {
        if (location.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(location.GetString())
          || !Path.IsPathFullyQualified(location.GetString()!)) return false;
        recovery = Path.GetFullPath(location.GetString()!);
      }
    }
    catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    committed = saved.GetBoolean(); usable = active.GetBoolean(); return true;
  }

  public void Cancel()
  {
    // 空闲读取不属于可取消任务；保留预览代次及已收尾的未确认写入状态。
    if (_inflight == 0 && _activeRun is null) return;
    if (Phase == "save") { Summary = "保存已进入不可取消的提交阶段，等待实际结果"; Changed(); return; }
    CancelActiveRun(); TerminalIssue = PdfIssueKind.Cancelled;
    Summary = IsSettling ? "已请求取消，等待后台实际收尾；已完成写入保留" : "已取消";
    Status = "已取消"; Changed();
  }
  public void CloseSession() { RuntimePreparationIncomplete = false; CancelActiveRun(); SessionId = null; FilePath = null; PageCount = 0; Pages.Clear(); _detectedCount = _textLayerCount = _addedCount = 0; _requiresReopen = false; _modelAvailable = false; IsModified = false; Summary = ""; SelectedPage = -1; TerminalIssue = null; Status = "请选择 PDF"; }

  public async Task CloseSessionAsync(CancellationToken ct)
  {
    string? session = _modelAvailable ? SessionId : null;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    BeginRun(); Changed();
    IAsyncDisposable? lease = null;
    try
    {
      // Owner 已完成 dirty/draft 确认。作用域水位确认后才发送关闭。
      if (session is not null)
      {
        lease = await AcquireWriteAsync(session, Revision, generation, ct);
        if (lease is null) throw new InvalidOperationException("读端尚未确认暂停，本次关闭未发送");
        ct.ThrowIfCancellationRequested();
      }
      if (_unclosedSession is not null)
      {
        await inference.ClosePdfSessionAsync(_unclosedSession, CancellationToken.None);
        _unclosedSession = null;
      }
      if (session is not null)
      {
        await inference.ClosePdfSessionAsync(session, CancellationToken.None);
        if (_demand is not null) await _demand.WaitForReadDrainAsync(session);
      }
      if (generation == Volatile.Read(ref _generation))
      {
        CloseSession();
        if (session is not null) _demand?.Forget(session);
      }
    }
    catch (Exception)
    {
      if (generation == Volatile.Read(ref _generation))
      {
        if (session is null || lease is not null) _requiresReopen = true;
        Summary = "文档关闭失败，保留会话以便重试释放";
      }
      throw;
    }
    finally
    {
      if (lease is not null) await lease.DisposeAsync();
      EndRun(); Changed();
    }
  }

  private async Task ReconcileUnconfirmedAsync(IEnumerable<int> possiblyChanged, bool structure = false)
  {
    _requiresReopen = true;
    InvalidateContent(possiblyChanged, structure);
    if (structure || !_modelAvailable) return;
    try { await RefreshModelAsync(CancellationToken.None); }
    catch (Exception) { }
  }

  private void MarkUnconfirmedOcr(string session, long revision)
  {
    if (Phase != "ocr" || SessionId != session || Revision != revision) return;
    _requiresReopen = true;
    Summary = "识别后台终态未确认，请关闭并重新打开文档";
  }

  private async Task MutateAsync(Func<CancellationToken, Task<PdfMutateResult>> action, string runningStatus, CancellationToken ct,
    Func<PdfMutateResult, int?[]>? mapping = null, Action? onSuccess = null,
    Func<CancellationToken, Task<bool>>? prepare = null, int[]? uncertainPages = null)
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
    IAsyncDisposable? lease = null;
    async Task Unconfirmed()
    {
      if (SessionId != session || !submitted) return;
      if (UsesDemand) await ReconcileUnconfirmedAsync(uncertainPages ?? Enumerable.Range(0, Pages.Count), mapping is not null);
      else _requiresReopen = true;
      Summary = "页面写入结果未确认，请关闭并重新打开文档复检"; Changed();
    }
    try
    {
      run.Token.ThrowIfCancellationRequested();
      // 写栅栏：暂停读发送方→all_reads ACK→核验会话/修订；ACK 失败不发写、
      // 不标 unconfirmed write（submitted 保持 false）。
      if (prepare is not null && !await prepare(run.Token)) return;
      lease = await AcquireWriteAsync(session, revision, generation, run.Token);
      if (lease is null)
      {
        Summary = SessionId == session && Revision == revision ? "读端尚未确认暂停，本次写入未发送" : "页面已变化，本次写入未发送";
        Changed();
        return;
      }
      Phase = "write"; Changed();
      run.Token.ThrowIfCancellationRequested();
      submitted = true;
      PdfMutateResult result = await action(CancellationToken.None);
      if (SessionId != session || Revision != revision) return;
      LastPageMapping = mapping?.Invoke(result);
      if (LastPageMapping is not null)
      {
        RemapPages(LastPageMapping); _demand?.Invalidate(this, structure: true);
        PagesRemapped?.Invoke(this, LastPageMapping);
      }
      onSuccess?.Invoke(); Revision++; IsModified = true;
      if (result.Diff is not null) ApplyDiff(result.Diff);
      else { PageCount = result.PageCount; await RefreshModelAsync(CancellationToken.None); }
      Summary = "页面操作完成，尚未保存";
      if (generation == Volatile.Read(ref _generation)) Status = "完成";
      Changed();
    }
    catch (OperationCanceledException) { await Unconfirmed(); if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; } }
    catch (InferenceClientException e)
    {
      if (e.Code is not (HttpV2ErrorCode.ValidationError or HttpV2ErrorCode.Unauthorized or HttpV2ErrorCode.ForbiddenLoopback or HttpV2ErrorCode.ResourceNotFound or HttpV2ErrorCode.RuntimeCapabilityUnavailable or HttpV2ErrorCode.RuntimeOperationNotFound)) await Unconfirmed();
      if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); }
    }
    catch (Exception) when (SessionId == session) { await Unconfirmed(); TerminalIssue = PdfIssueKind.Failed; Status = "操作失败"; }
    finally
    {
      if (lease is not null) await lease.DisposeAsync();
      await FinishRunAsync(generation, run);
    }
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
      // 存活页沿用 PageKey/ContentGeneration/DetectionState；新插入页新键、
      // 代次 0、presence unknown（不用结构重排首笔记为 known）。
      PdfPageViewModel source = mapping[index] is { } original ? old[original] : new PdfPageViewModel { Index = index, Detected = !UsesDemand, PageKey = NextPageKey(), DetectionState = UsesDemand ? PdfDetectionState.Unknown : PdfDetectionState.Known };
      Pages.Add(new PdfPageViewModel
      {
        Index = index,
        State = source.State, OcrText = source.OcrText, Result = source.Result,
        RecognitionRevision = source.RecognitionRevision, CorrectedAfterRecognition = source.CorrectedAfterRecognition,
        ResultReference = source.ResultReference, HasTextLayer = source.HasTextLayer, Detected = source.Detected,
        Width = source.Width, Height = source.Height, Rotation = source.Rotation,
        PageKey = source.PageKey, ContentGeneration = source.ContentGeneration,
        DetectionState = source.DetectionState == PdfDetectionState.Detecting ? PdfDetectionState.Unknown : source.DetectionState,
        AddedThisSession = source.AddedThisSession,
      });
    }
    SelectedPage = mapping.Select((value, index) => (value, index)).Where(pair => pair.value == selected).Select(pair => pair.index).FirstOrDefault(-1);
    PageCount = Pages.Count;
    Recount();
  }

  public async Task DeleteTextLayersAsync(int[] pages, CancellationToken ct)
  {
    if (SessionId is not { } session || !ValidatePages(pages) || IsSettling) return;
    int[] requested = pages.Distinct().ToArray();
    long revision = Revision;
    CancelActiveRun(); long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct); _activeRun = run; BeginRun();
    IsBusy = true; TerminalIssue = null;
    IAsyncDisposable? lease = null;
    bool submitted = false;
    var committed = new HashSet<int>();
    var uncertain = new HashSet<int>();
    var acknowledged = new HashSet<int>();
    bool terminal = false, applied = false;
    void ApplyCommits()
    {
      if (applied || committed.Count == 0) return;
      applied = true;
      Revision++;
      InvalidateContent(committed);
      foreach (int index in committed)
      {
        PdfPageViewModel page = Pages[index];
        CountPage(page, -1);
        page.AddedThisSession = false; page.Result = null; page.ResultReference = null;
        page.OcrText = ""; page.State = PdfPageState.None;
        CountPage(page, 1);
      }
    }
    try
    {
      if (!await EnsurePresenceAsync(session, requested, run.Token)) return;
      if (requested.Any(index => !Pages[index].Detected))
      {
        Summary = "部分页面文字层状态未知，请等待检测完成后重试"; Changed(); return;
      }
      pages = requested.Where(index => Pages[index].HasTextLayer).ToArray();
      if (pages.Length == 0) { Summary = "所选页面无文字层，已跳过"; Changed(); return; }
      var targets = pages.ToHashSet();
      lease = await AcquireWriteAsync(session, revision, generation, run.Token);
      if (lease is null)
      {
        Summary = "读端尚未确认暂停，本次删除未发送"; Changed(); return;
      }
      await inference.ResetPdfCancelAsync(session, CancellationToken.None);
      run.Token.ThrowIfCancellationRequested();
      if (SessionId != session || Revision != revision || generation != Volatile.Read(ref _generation)) return;
      submitted = true;
      await inference.DeletePdfTextLayersAsync(session, pages, progress =>
      {
        // Cancellation stops future work. Acknowledged page commits still apply.
        if (SessionId != session || Revision != revision) return;
        if (progress.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
          terminal = message.GetString() == "done";
        if (progress.TryGetProperty("page_index", out var page) && page.ValueKind == JsonValueKind.Number &&
            page.TryGetInt32(out int index) && targets.Contains(index))
        {
          if (progress.TryGetProperty("page_payload", out var payload) && payload.ValueKind == JsonValueKind.Array &&
              payload.GetArrayLength() >= 3 && payload[1].ValueKind == JsonValueKind.Number &&
              payload[1].TryGetInt32(out int rounds))
          {
            acknowledged.Add(index);
            if (rounds > 0) committed.Add(index);
          }
          else uncertain.Add(index);
        }
        if (generation == Volatile.Read(ref _generation)) UpdateProgress(progress);
      }, CancellationToken.None);
      if (SessionId != session || Revision != revision) return;
      if (!UsesDemand && generation == Volatile.Read(ref _generation)) committed.UnionWith(pages);
      ApplyCommits();
      if (UsesDemand && !terminal) throw new InvalidDataException("PDF delete stream lacks a terminal response.");
      if (uncertain.Count > 0) await ReconcileUnconfirmedAsync(uncertain);
      else await RefreshModelAsync(CancellationToken.None);
      if (generation == Volatile.Read(ref _generation))
      {
        int[] residual = pages.Where(index => Pages[index].HasTextLayer).ToArray();
        Summary = uncertain.Count > 0 ? "部分删除结果未确认，请重开文档复检"
          : residual.Length == 0 ? "文字层删除完成，尚未保存"
          : $"删除后残留页：{string.Join(", ", residual.Select(index => index + 1))}；尚未保存";
        if (uncertain.Count > 0) TerminalIssue = PdfIssueKind.Failed;
      }
    }
    catch (OperationCanceledException) when (!submitted) { if (SessionId == session) TerminalIssue = PdfIssueKind.Cancelled; }
    catch (Exception)
    {
      if (SessionId == session && submitted)
      {
        ApplyCommits();
        await ReconcileUnconfirmedAsync(pages.Where(index => !acknowledged.Contains(index)));
      }
      if (SessionId == session && generation == Volatile.Read(ref _generation))
      { TerminalIssue = PdfIssueKind.Failed; Summary = "删除文字层失败，请复检后重试"; }
    }
    finally
    {
      if (lease is not null) await lease.DisposeAsync();
      await FinishRunAsync(generation, run);
    }
  }

  /// <summary>保留当前窗口结果；其他页只保留 supervisor job/item 引用。</summary>
  public async Task PrepareResultsAsync(int start, int count, CancellationToken ct)
  {
    string? session = SessionId;
    long revision = Revision;
    TrimResultCache(start, count);
    long cacheGeneration = Volatile.Read(ref _resultCacheGeneration);
    var needed = Pages.Skip(start).Take(count).Where(page => page.Result is null && page.ResultReference is not null)
        .Select(page => (Page: page, Reference: page.ResultReference!))
        .GroupBy(item => item.Reference.JobId).ToArray();
    foreach (var group in needed)
    {
      int sequence = 0;
      while (true)
      {
        JobUpdate update = await inference.ObserveAsync(group.Key, sequence, ct);
        if (SessionId != session || Revision != revision || cacheGeneration != Volatile.Read(ref _resultCacheGeneration)) return;
        foreach (var (page, reference) in group)
        {
          // 同修订下的新 OCR job/清层也可能更换引用；旧回读不能借新归属回填。
          if (page.ResultReference != reference) continue;
          ItemOutcome? outcome = update.Outcomes.FirstOrDefault(item => item.ItemId == reference.ItemId);
          if (outcome?.State == ItemState.Succeeded)
          {
            page.Result = RecognitionOutcomeMapper.ToResponse(outcome, reference.Pipeline);
            if (page.Result.PreprocAngle is { } angle)
              page.Result = page.Result with { PreprocAngle = (angle + reference.RotationOffset) % 360 };
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
    // 淘汰窗口或切走文档时，使等待 supervisor 回读的旧窗口先于回填失效。
    Interlocked.Increment(ref _resultCacheGeneration);
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
    foreach (var page in diff.ReplacedPages ?? [])
      ApplyPage(page, detected: !UsesDemand || Pages.ElementAtOrDefault(page.PageIndex)?.Detected == true);
    if (diff.ModifiedFlag is { } modified) IsModified = modified;
    Changed();
  }

  private void ApplyPage(Wire.PdfPageInfoMirror page, bool detected)
  {
    if (page.PageIndex < 0 || page.PageIndex >= Pages.Count) throw new InvalidOperationException("PDF model index mismatch.");
    PdfPageViewModel target = Pages[page.PageIndex];
    CountPage(target, -1);
    target.AddedThisSession = page.HasOcrTextLayer == true || page.OcrTextBlocks is { Count: > 0 };
    if (target.HasTextLayer && page.HasTextLayer != true) { target.Result = null; target.ResultReference = null; target.OcrText = ""; target.State = PdfPageState.None; }
    target.Detected = detected; target.HasTextLayer = detected && page.HasTextLayer == true; target.Rotation = page.Rotation ?? 0;
    if (detected) target.DetectionState = PdfDetectionState.Known;
    if (page.Rect is { Count: 4 } rect) { target.Width = rect[2].GetDouble() - rect[0].GetDouble(); target.Height = rect[3].GetDouble() - rect[1].GetDouble(); }
    CountPage(target, 1);
    _modelAvailable = true;
  }

  private void CountPage(PdfPageViewModel page, int delta)
  {
    if (page.Detected) _detectedCount += delta;
    if (page.Detected && page.HasTextLayer) _textLayerCount += delta;
    if (page.AddedThisSession) _addedCount += delta;
  }

  private void Recount()
  {
    _detectedCount = _textLayerCount = _addedCount = 0;
    foreach (PdfPageViewModel page in Pages) CountPage(page, 1);
  }

  private void InvalidateContent(IEnumerable<int> pages, bool structure = false)
  {
    foreach (int index in pages.Distinct())
      if (index >= 0 && index < Pages.Count) Pages[index].ContentGeneration++;
    _demand?.Invalidate(this, structure);
  }

  private Task<byte[]> RenderBusinessPreviewAsync(string session, int page, int dpi, CancellationToken ct) =>
    UsesDemand ? _demand!.RenderBusinessPreviewAsync(this, session, page, dpi, ct)
      : inference.RenderPdfPreviewAsync(session, page, dpi, ct);

  private async Task<bool> EnsurePresenceAsync(string session, int[] pages, CancellationToken ct)
  {
    if (!UsesDemand) return true;
    int[] missing = pages.Where(index => Pages[index].DetectionState != PdfDetectionState.Known ||
      !Pages[index].HasReliableGeometry).ToArray();
    if (missing.Length == 0) return true;
    Phase = "load"; ProgressCurrent = 0; ProgressTotal = missing.Length; Changed();
    bool ok;
    try { ok = await _demand!.EnsurePresenceAsync(this, session, missing, ct); }
    catch (Exception) when (!ct.IsCancellationRequested) { ok = false; }
    ct.ThrowIfCancellationRequested();
    if (!ok)
    {
      TerminalIssue = PdfIssueKind.Failed;
      Summary = "部分目标页检测失败，请重试";
      Changed();
    }
    return ok;
  }

  internal async Task<bool> ReadPresenceAsync(string session, long epoch, int[]? pages, Func<bool> current)
  {
    bool foreground = pages is not null;
    int[] targets = pages ?? Enumerable.Range(0, Pages.Count)
      .Where(index => Pages[index].DetectionState is PdfDetectionState.Unknown or PdfDetectionState.Detecting)
      .ToArray();
    if (!current() || targets.Length == 0) return false;
    var requested = targets.ToHashSet();
    var received = new HashSet<int>();
    var tracking = new HashSet<PdfPageViewModel>();
    void Detecting(int index)
    {
      var page = Pages[index];
      if (page.DetectionState is not PdfDetectionState.Known)
      {
        page.DetectionState = PdfDetectionState.Detecting;
        page.DetectionEpoch = epoch;
        tracking.Add(page);
      }
    }
    string? terminal = null;
    int next = 0;
    Detecting(targets[next++]);
    try
    {
      await inference.LoadPdfDemandAsync(session, new Wire.PdfDemandLoadRequest
      {
        Read = new() { Scope = Wire.PdfReadScope.Background, Epoch = epoch },
        Pages = pages,
      }, progress =>
      {
        // The response can settle the issued read even after its UI owner changed.
        if (progress.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.String)
        {
          string? outcome = msg.GetString();
          if (outcome is "done" or "cancelled" or "incomplete") terminal = outcome;
        }
        if (!current()) return;
        if (progress.TryGetProperty("page_payload", out JsonElement payload) && payload.ValueKind == JsonValueKind.Object &&
            payload.Deserialize<Wire.PdfPageInfoMirror>() is { } page && requested.Contains(page.PageIndex))
        {
          ApplyPage(page, detected: true);
          received.Add(page.PageIndex);
          while (next < targets.Length && received.Contains(targets[next])) next++;
          if (next < targets.Length) Detecting(targets[next++]);
          if (!foreground && (received.Count <= 2 || received.Count % 8 == 0 || received.Count == targets.Length)) Changed();
        }
        if (foreground) UpdateProgress(progress);
      }, CancellationToken.None);
      if (terminal is null) throw new InvalidDataException("PDF presence stream ended without a terminal response.");
      return current() && terminal == "done" && targets.All(index => Pages[index].HasReliableGeometry);
    }
    finally
    {
      // Cancelled or superseded attempts leave unresolved pages eligible for a later
      // read. A completed stream that omitted a target records a real failed page.
      bool stillCurrent = current();
      if (stillCurrent && terminal is not "cancelled")
        foreach (int index in targets)
          if (!received.Contains(index) && Pages[index].DetectionState != PdfDetectionState.Known)
            Pages[index].DetectionState = PdfDetectionState.Failed;
      foreach (PdfPageViewModel page in tracking)
        if (page.DetectionState == PdfDetectionState.Detecting && page.DetectionEpoch == epoch)
          page.DetectionState = PdfDetectionState.Unknown;
      if (SessionId == session) Changed();
    }
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

/// <summary>文字层 presence 检测四态：unknown 与 failed 不得当作无字层。
/// legacy 全篇 /load 证明时为 known；新增页 unknown。</summary>
public enum PdfDetectionState { Unknown, Detecting, Known, Failed }

public sealed class PdfPageViewModel : INotifyPropertyChanged
{
  private PdfPageState _state = PdfPageState.None;
  private string _ocrText = string.Empty;
  public event PropertyChangedEventHandler? PropertyChanged;
  public int Index { get; init; }
  /// <summary>页生命期稳定身份：重映射沿用；新页新键。</summary>
  public long PageKey { get; internal set; }
  /// <summary>内容/几何代次：仅受影响页递增，不用全局 UI revision。</summary>
  public long ContentGeneration { get; internal set; }
  public PdfDetectionState DetectionState { get; internal set; } = PdfDetectionState.Unknown;
  internal long DetectionEpoch { get; set; }
  /// <summary>几何可靠（真实 Rect + 已检测）：业务前置门槛。</summary>
  public bool HasReliableGeometry => Width > 0 && Height > 0 && Detected;
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
