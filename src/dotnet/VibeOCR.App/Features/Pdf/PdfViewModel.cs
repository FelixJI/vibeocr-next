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
  private long _generation;
  private bool _isBusy;
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
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    TerminalIssue = null; IsBusy = true; Status = "正在打开";
    try
    {
      PdfSessionOpenResult result = await inference.OpenPdfSessionAsync(path, null, run.Token);
      if (generation != Volatile.Read(ref _generation)) return;
      SessionId = result.SessionId; FilePath = result.FilePath; PageCount = result.PageCount;
      Pages.Clear(); for (int i = 0; i < PageCount; i++) Pages.Add(new PdfPageViewModel { Index = i });
      SelectedPage = -1;
      Status = $"已打开 {PageCount} 页";
    }
    catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; } }
    catch (InferenceClientException e) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); } }
    catch (Exception) when (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Status = "打开失败"; }
    finally
    {
      FinishRun(generation, run);
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
    if (SessionId is null || pages.Length == 0) { Status = "请先选中要旋转的页面"; return; }
    if (!ValidatePages(pages)) return;
    await MutateAsync(async token => (await inference.RotatePdfPagesAsync(SessionId!, pages, angle, token)).PageCount, "正在旋转", ct);
  }

  public async Task DeletePagesAsync(int[] pages, CancellationToken ct)
  {
    if (SessionId is null || pages.Length == 0) return;
    if (!ValidatePages(pages)) return;
    int[] deleted = pages.Distinct().Order().ToArray();
    await MutateAsync(
        async token => (await inference.DeletePdfPagesAsync(SessionId!, deleted, token)).PageCount,
        "正在删除页面", ct, () => RemovePages(deleted));
  }

  public async Task StartOcrAsync(int[] pages, bool overwrite, CancellationToken ct)
  {
    if (SessionId is null || pages.Length == 0) { Status = "请先打开 PDF"; return; }
    if (!ValidatePages(pages)) return;
    pages = pages.Distinct().ToArray();
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
    TerminalIssue = null; IsBusy = true; Status = "正在识别";
    foreach (int idx in pages) if (idx < Pages.Count) Pages[idx].State = PdfPageState.Processing;
    try
    {
      // The PDF session remains the bounded render/mutate module. Until
      // a production PDF_OCR executor exists, all selected page images
      // enter one generic RECOGNITION job; the UI never creates one job
      // per page and never uses the unimplemented pdf_page.v1 path.
      var inputs = new InferenceUploadInput[pages.Length];
      for (int i = 0; i < pages.Length; i++)
      {
        int pageIndex = pages[i];
        byte[] image = await inference.RenderPdfPageAsync(
            sessionId,
            pageIndex,
            1024,
            run.Token);
        run.Token.ThrowIfCancellationRequested();
        inputs[i] = new InferenceUploadInput(
            $"page-{pageIndex}",
            $"page-{pageIndex + 1}.png",
            "image/png",
            image);
      }

      InferenceJobRun job = await _jobs.RunRecognitionAsync(
          pipeline,
          JobPriority.Background,
          inputs,
          options: options,
          cancellationToken: run.Token,
          engine: engine);
      JobSnapshot snap = job.Snapshot;
      if (generation != Volatile.Read(ref _generation)) return;
      if (snap.State is JobState.Cancelled) { TerminalIssue = PdfIssueKind.Cancelled; foreach (int idx in pages) if (idx < Pages.Count) Pages[idx].State = PdfPageState.None; Status = "已取消"; return; }
      int s = 0, f = 0;
      foreach (int idx in pages)
      {
        if (generation != Volatile.Read(ref _generation)) return;
        if (idx < 0 || idx >= Pages.Count) continue;
        ItemOutcome outcome = job.OutcomesByClientItemKey[$"page-{idx}"];
        if (outcome.State is ItemState.Succeeded)
        {
          RecognizeResponse result = RecognitionOutcomeMapper.ToResponse(outcome, pipeline);
          Pages[idx].Result = result;
          Pages[idx].OcrText = result.Text;
          Pages[idx].State = PdfPageState.Done;
          s++;
        }
        else if (outcome.State is ItemState.Cancelled)
        {
          Pages[idx].State = PdfPageState.None;
        }
        else
        {
          Pages[idx].State = PdfPageState.Failed;
          f++;
        }
      }
      Status = $"OCR 完成：成功 {s} 页，失败 {f} 页";
    }
    catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; foreach (int idx in pages) if (idx < Pages.Count) Pages[idx].State = PdfPageState.None; Status = "已取消"; } }
    catch (InferenceClientException e) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); } }
    catch (Exception) when (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Status = "OCR 失败"; }
    finally
    {
      if (generation == Volatile.Read(ref _generation))
        foreach (int idx in pages)
          if (Pages[idx].State == PdfPageState.Processing)
            Pages[idx].State = PdfPageState.Failed;
      FinishRun(generation, run);
    }
  }

  public async Task SaveAsync(string path, CancellationToken ct)
  {
    if (SessionId is null) return;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    TerminalIssue = null; IsBusy = true; Status = "正在保存";
    try { string saved = await inference.SavePdfAsync(SessionId, path, run.Token); if (generation == Volatile.Read(ref _generation)) Status = $"已保存到 {saved}"; }
    catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; } }
    catch (InferenceClientException e) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); } }
    catch (Exception) when (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Status = "保存失败"; }
    finally { FinishRun(generation, run); }
  }

  public void Cancel() { CancelActiveRun(); TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; }
  public void CloseSession() { CancelActiveRun(); SessionId = null; FilePath = null; PageCount = 0; Pages.Clear(); SelectedPage = -1; TerminalIssue = null; Status = "请选择 PDF"; }

  private async Task MutateAsync(Func<CancellationToken, Task<int>> action, string runningStatus, CancellationToken ct, Action? onSuccess = null)
  {
    if (SessionId is null) return;
    CancelActiveRun();
    long generation = Volatile.Read(ref _generation);
    var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
    _activeRun = run;
    TerminalIssue = null; IsBusy = true; Status = runningStatus;
    try { int count = await action(run.Token); if (generation == Volatile.Read(ref _generation)) { onSuccess?.Invoke(); PageCount = count; Status = "完成"; } }
    catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Cancelled; Status = "已取消"; } }
    catch (InferenceClientException e) { if (generation == Volatile.Read(ref _generation)) { TerminalIssue = IssueFromV2(e.Code); Status = LocalizeV2(e.Code); } }
    catch (Exception) when (generation == Volatile.Read(ref _generation)) { TerminalIssue = PdfIssueKind.Failed; Status = "操作失败"; }
    finally { FinishRun(generation, run); }
  }

  private void CancelActiveRun()
  {
    Interlocked.Increment(ref _generation);
    var run = Interlocked.Exchange(ref _activeRun, null);
    run?.Cancel();
    // The operation owns disposal: a cancelled await may still access its token.
    IsBusy = false;
    foreach (PdfPageViewModel page in Pages)
      if (page.State == PdfPageState.Processing) page.State = PdfPageState.None;
  }

  private void FinishRun(long generation, CancellationTokenSource run)
  {
    if (generation == Volatile.Read(ref _generation)) IsBusy = false;
    Interlocked.CompareExchange(ref _activeRun, null, run);
    run.Dispose();
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
      });
    SelectedPage = selected < 0 || remaining.Length == 0 ? -1
        : Math.Min(selected - deleted.Count(index => index < selected), remaining.Length - 1);
  }

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
  public RecognizeResponse? Result { get; internal set; }
  public PdfPageState State { get => _state; set { if (_state != value) { _state = value; PropertyChanged?.Invoke(this, new(nameof(State))); } } }
  public string OcrText { get => _ocrText; set { if (_ocrText != value) { _ocrText = value; PropertyChanged?.Invoke(this, new(nameof(OcrText))); } } }
}

public interface IPdfFileSource { Task<string?> PickFileAsync(CancellationToken cancellationToken); }
