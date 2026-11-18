using System.Diagnostics;
using System.Text.Json;
using VibeOCR.App.Inference;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App;

/// <summary>
/// T3（#328）页面选择规范：all（≤64）/first:N/pages:a,b,c（均限首 64 页公开
/// 复选框窗口）。fail-closed：越界、空集、非法 token、>64 的 all 均拒绝。
/// </summary>
internal static class PdfPipelinePageSelection
{
  internal const int MaxSelectableWindow = 64;

  internal static int[] Resolve(string spec, int pageCount)
  {
    if (pageCount <= 0)
      throw new InvalidOperationException($"Pipeline page selection requires a positive page count: {pageCount}");
    string trimmed = spec.Trim();
    if (trimmed == "all")
    {
      if (pageCount > MaxSelectableWindow)
        throw new InvalidOperationException(
            $"'all' requires pageCount<={MaxSelectableWindow} (公开选择控件只覆盖首窗口); pageCount={pageCount}");
      return Enumerable.Range(0, pageCount).ToArray();
    }
    if (trimmed.StartsWith("first:", StringComparison.Ordinal))
    {
      if (!int.TryParse(trimmed["first:".Length..], out int count) || count <= 0 ||
          count > pageCount || count > MaxSelectableWindow)
        throw new InvalidOperationException($"Invalid pipeline page selection '{spec}' for pageCount={pageCount}");
      return Enumerable.Range(0, count).ToArray();
    }
    if (trimmed.StartsWith("pages:", StringComparison.Ordinal))
    {
      string[] tokens = trimmed["pages:".Length..].Split(
          ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      if (tokens.Length == 0)
        throw new InvalidOperationException($"Invalid pipeline page selection '{spec}'");
      List<int> indexes = tokens
          .Select(token => int.TryParse(token, out int value)
            ? value
            : throw new InvalidOperationException($"Invalid page index '{token}' in '{spec}'"))
          .Distinct()
          .Order()
          .ToList();
      if (indexes.Any(index => index < 0 || index >= pageCount || index >= MaxSelectableWindow))
        throw new InvalidOperationException(
            $"Pipeline page selection '{spec}' leaves the first {MaxSelectableWindow}-page selection window (pageCount={pageCount})");
      return indexes.ToArray();
    }
    throw new InvalidOperationException($"Unknown pipeline page selection spec: {spec}");
  }

  /// <summary>当前串行合同：4 页一批一个通用 job（PdfViewModel.Chunk(4)）。</summary>
  internal static int ExpectedBatches(int pageCount) => (pageCount + 3) / 4;
}

/// <summary>每类 wire 请求的窗口统计；未测得字段保持 null，不落 0。</summary>
internal sealed record PdfPipelineKindStat(
    int Count, int Completed, int Pending, int Faulted, int Cancelled,
    double? DurationSumMs, double? DurationMedianMs, double? DurationMaxMs,
    long? BytesTotal, int EventsWithBytes, long? PixelsTotal, int EventsParsedPng,
    int CompletedMissingBytes);

/// <summary>
/// T3（#328）阶段测量的纯统计/校验：全部基于 PdfSmokeRequestProbe 的原始
/// 事件，输入窗口由宿主 Stopwatch 域的毫秒边界指定。缺失终态（pending）、
/// 失败/取消、job 数与串行合同不符、无业务渲染等都显式产出错误，由调用方
/// fail-closed；不存在的度量保持 null（NOT_MEASURED），不得写 0。
/// </summary>
internal static class PdfPipelinePhaseStats
{
  internal static List<PdfSmokeRequestProbe.RequestEvent> Window(
      IReadOnlyList<PdfSmokeRequestProbe.RequestEvent> events, double fromMs, double toMs) =>
    events.Where(request => request.StartedAtMs >= fromMs && request.StartedAtMs < toMs).ToList();

  internal static IReadOnlyDictionary<string, PdfPipelineKindStat> ByKind(
      IReadOnlyList<PdfSmokeRequestProbe.RequestEvent> events)
  {
    var result = new Dictionary<string, PdfPipelineKindStat>();
    foreach (IGrouping<string, PdfSmokeRequestProbe.RequestEvent> group in events.GroupBy(request => request.Kind))
    {
      List<double> durations = group
          .Where(request => request.Outcome == "completed" && request.DurationMs is not null)
          .Select(request => request.DurationMs!.Value).ToList();
      List<long> bytes = group.Where(request => request.Bytes is not null)
          .Select(request => request.Bytes!.Value).ToList();
      List<long> pixels = group.Where(request => request.Pixels is not null)
          .Select(request => request.Pixels!.Value).ToList();
      result[group.Key] = new PdfPipelineKindStat(
          group.Count(),
          group.Count(request => request.Outcome == "completed"),
          group.Count(request => request.Outcome == "pending"),
          group.Count(request => request.Outcome == "faulted"),
          group.Count(request => request.Outcome == "cancelled"),
          durations.Count == 0 ? null : durations.Sum(),
          Median(durations),
          durations.Count == 0 ? null : durations.Max(),
          bytes.Count == 0 ? null : bytes.Sum(),
          bytes.Count,
          pixels.Count == 0 ? null : pixels.Sum(),
          pixels.Count,
          group.Count(request => request.Outcome == "completed" && request.Bytes is null));
    }
    return result;
  }

  /// <summary>OCR 窗口合同校验：错误列表为空才允许作为有效样本。</summary>
  internal static List<string> ValidateOcrWindow(
      IReadOnlyList<PdfSmokeRequestProbe.RequestEvent> events, int expectedBatches, int pageCount)
  {
    var errors = new List<string>();
    List<PdfSmokeRequestProbe.RequestEvent> submits = events
        .Where(request => request.Kind == "job_submit").ToList();
    if (submits.Count != expectedBatches)
      errors.Add($"job_submit count {submits.Count} != expected serial batches {expectedBatches} for {pageCount} pages");
    if (submits.Any(request => request.Outcome != "completed"))
      errors.Add("job_submit with non-completed outcome: " + JsonSerializer.Serialize(
          submits.Where(request => request.Outcome != "completed")
            .Select(request => new { request.Outcome, request.Error })));
    List<PdfSmokeRequestProbe.RequestEvent> writes = events
        .Where(request => request.Kind == "add_text_layers").ToList();
    if (writes.Any(request => request.Outcome != "completed"))
      errors.Add("add_text_layers with non-completed outcome: " + JsonSerializer.Serialize(
          writes.Where(request => request.Outcome != "completed")
            .Select(request => new { request.Outcome, request.Error })));
    if (pageCount > 0 && events.All(request => request.Kind != "render_preview"))
      errors.Add("no render_preview wire requests in OCR window; business render evidence missing");
    if (events.Any(request => request.Outcome == "pending"))
      errors.Add("requests still pending at evidence time: " + JsonSerializer.Serialize(
          events.Where(request => request.Outcome == "pending")
            .Select(request => new { request.Kind, request.Page })));
    if (events.Any(request => request.Outcome == "faulted"))
      errors.Add("faulted requests in OCR window: " + JsonSerializer.Serialize(
          events.Where(request => request.Outcome == "faulted")
            .Select(request => new { request.Kind, request.Page, request.Error })));
    if (events.Any(request => request.Outcome == "cancelled"))
      errors.Add("cancelled requests in OCR window: " + JsonSerializer.Serialize(
          events.Where(request => request.Outcome == "cancelled")
            .Select(request => new { request.Kind, request.Page })));
    return errors;
  }

  internal static double? Median(IReadOnlyList<double> values)
  {
    if (values.Count == 0) return null;
    List<double> sorted = values.Order().ToList();
    return sorted.Count % 2 == 1
      ? sorted[sorted.Count / 2]
      : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
  }

  /// <summary>前台帧间隔统计；空集保持 null（NOT_MEASURED），不以 0 充当。</summary>
  internal static (int Count, double? MedianGapMs, double? MaxGapMs, int GapsOverThresholdMs)
      FrameGaps(IReadOnlyList<double> gaps, double thresholdMs = 250.0)
  {
    if (gaps.Count == 0) return (0, null, null, 0);
    return (gaps.Count, Median(gaps), gaps.Max(), gaps.Count(gap => gap > thresholdMs));
  }
}

/// <summary>
/// Goal #325/#328 T3 串行阶段测量基线（INPUT_KIND=pdf_pipeline）。维持现有
/// 串行算法，只观测：通过公开产品操作（打开 PDF→选择引擎/页面→添加选中页
/// 文字层→另存为）触发真实 OCR/写层/保存；同进程第一遍保留冷 worker 成本，
/// 关闭并重开同一 fixture 做第二遍稳态（输入等价由重开同一文件保证）。
/// 阶段时间来自宿主 Stopwatch（公开动作与里程）与 PdfSmokeRequestProbe 的
/// 真实 wire 事件（presence/render/submit/observe/write/save，含真实传输
/// bytes 与 PNG IHDR 像素）；前台帧尾来自浏览器双 RAF 帧机会。两域原始时间
/// 互不比较；未测得项显式 NOT_MEASURED，不写 0。
/// </summary>
public sealed partial class MainWindow
{
  private const string PdfPipelineObserverScript = """
    (() => {
      if (window.__t3PipelineObserver) return 'already';
      const encoder = new TextEncoder();
      const state = {
        marks: {}, phase: 'pre_open', frameGaps: [], totalFrames: 0,
        lastFrameAt: null, disposed: false,
        visibilitySamples: { visible: 0, hidden: 0 },
        focusSamples: { focused: 0, blurred: 0 },
        stateMessages: {
          total: 0, bytesEstimate: 0, errors: 0, listener: false, byPhase: {},
        },
        messageHandler: null,
      };
      function frameLoop() {
        if (state.disposed) return;
        requestAnimationFrame(() => requestAnimationFrame(() => {
          if (state.disposed) return;
          const now = performance.now();
          const gap = state.lastFrameAt === null ? null : now - state.lastFrameAt;
          state.lastFrameAt = now;
          state.totalFrames += 1;
          const visibility = document.visibilityState === 'visible' ? 'visible' : 'hidden';
          state.visibilitySamples[visibility] += 1;
          const focus = document.hasFocus() ? 'focused' : 'blurred';
          state.focusSamples[focus] += 1;
          if (gap !== null) {
            state.frameGaps.push({ at: now, phase: state.phase, gapMs: gap,
              visibility, hasFocus: focus });
            if (state.frameGaps.length > 768) state.frameGaps.shift();
          }
          frameLoop();
        }));
      }
      const observer = {
        mark(name) {
          if (state.marks[name] === undefined) state.marks[name] = performance.now();
          return true;
        },
        setPhase(name) { state.phase = String(name); return true; },
        snapshot() {
          return JSON.stringify({
            marks: { ...state.marks }, phase: state.phase,
            frames: { total: state.totalFrames, gaps: state.frameGaps.slice(-512) },
            visibilitySamples: { ...state.visibilitySamples },
            focusSamples: { ...state.focusSamples },
            stateMessages: {
              total: state.stateMessages.total,
              bytesEstimate: state.stateMessages.bytesEstimate,
              errors: state.stateMessages.errors,
              listener: state.stateMessages.listener,
              byPhase: { ...state.stateMessages.byPhase },
            },
          });
        },
        dispose() {
          state.disposed = true;
          if (state.messageHandler && window.chrome && chrome.webview &&
              chrome.webview.removeEventListener) {
            chrome.webview.removeEventListener('message', state.messageHandler);
          }
          state.messageHandler = null;
          return true;
        },
      };
      Object.defineProperty(window, '__t3PipelineObserver', {
        value: observer, configurable: false, enumerable: false,
      });
      if (window.chrome && chrome.webview && chrome.webview.addEventListener) {
        state.stateMessages.listener = true;
        state.messageHandler = (event) => {
          try {
            const data = event && event.data;
            if (!data || data.type !== 'app.state' || !data.payload ||
                data.payload.scope !== 'pdf') return;
            const bytes = encoder.encode(JSON.stringify(data.payload)).length;
            state.stateMessages.total += 1;
            state.stateMessages.bytesEstimate += bytes;
            const bucket = state.stateMessages.byPhase[state.phase] ||
              (state.stateMessages.byPhase[state.phase] = { count: 0, bytesEstimate: 0 });
            bucket.count += 1;
            bucket.bytesEstimate += bytes;
          } catch (error) {
            state.stateMessages.errors += 1;
          }
        };
        chrome.webview.addEventListener('message', state.messageHandler);
      }
      frameLoop();
      return 'installed';
    })()
    """;

  private sealed record PdfPipelineFrameEntry(
      double At, string Phase, double GapMs, string Visibility, bool HasFocus);

  private sealed record PdfPipelineStateMessagePhase(int Count, long BytesEstimate);

  private sealed record PdfPipelineBrowserSnapshot(
      IReadOnlyDictionary<string, double> Marks, string Phase, int FramesTotal,
      IReadOnlyList<PdfPipelineFrameEntry> FrameGaps,
      IReadOnlyDictionary<string, int> VisibilitySamples,
      IReadOnlyDictionary<string, int> FocusSamples,
      long StateMessagesTotal, long StateMessagesBytesEstimate,
      bool StateMessagesListener, int StateMessagesErrors,
      IReadOnlyDictionary<string, PdfPipelineStateMessagePhase> StateMessagesByPhase);

  private static long PdfPipelineNow() => Stopwatch.GetTimestamp();

  private static double PdfPipelineToMs(long timestamp) =>
    timestamp * 1000.0 / Stopwatch.Frequency;

  private static double PdfPipelineElapsedMs(long start, long end) =>
    (end - start) * 1000.0 / Stopwatch.Frequency;

  private async Task<bool> MarkPdfPipelineBrowserAsync(string name)
  {
    string result = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      $"window.__t3PipelineObserver?.mark({JsonSerializer.Serialize(name)}) === true");
    return result == "true";
  }

  private async Task SetPdfPipelinePhaseAsync(string phase)
  {
    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      $"window.__t3PipelineObserver?.setPhase({JsonSerializer.Serialize(phase)})");
  }

  private async Task<PdfPipelineBrowserSnapshot> PdfPipelineSnapshotAsync()
  {
    string raw = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      "window.__t3PipelineObserver ? window.__t3PipelineObserver.snapshot() : null");
    if (raw == "null")
      throw new InvalidOperationException("Pipeline browser observer is not installed.");
    using JsonDocument outer = JsonDocument.Parse(raw);
    string json = outer.RootElement.GetString()
      ?? throw new InvalidOperationException("Pipeline browser snapshot is empty.");
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement root = document.RootElement;
    var marks = new Dictionary<string, double>();
    foreach (JsonProperty mark in root.GetProperty("marks").EnumerateObject())
      marks[mark.Name] = mark.Value.GetDouble();
    var gaps = new List<PdfPipelineFrameEntry>();
    foreach (JsonElement gap in root.GetProperty("frames").GetProperty("gaps").EnumerateArray())
    {
      gaps.Add(new PdfPipelineFrameEntry(
          gap.GetProperty("at").GetDouble(),
          gap.GetProperty("phase").GetString() ?? "",
          gap.GetProperty("gapMs").GetDouble(),
          gap.GetProperty("visibility").GetString() ?? "",
          gap.GetProperty("hasFocus").GetBoolean()));
    }
    var visibility = new Dictionary<string, int>();
    foreach (JsonProperty sample in root.GetProperty("visibilitySamples").EnumerateObject())
      visibility[sample.Name] = (int)sample.Value.GetDouble();
    var focus = new Dictionary<string, int>();
    foreach (JsonProperty sample in root.GetProperty("focusSamples").EnumerateObject())
      focus[sample.Name] = (int)sample.Value.GetDouble();
    var byPhase = new Dictionary<string, PdfPipelineStateMessagePhase>();
    foreach (JsonProperty phase in root.GetProperty("stateMessages").GetProperty("byPhase").EnumerateObject())
    {
      byPhase[phase.Name] = new PdfPipelineStateMessagePhase(
          (int)phase.Value.GetProperty("count").GetDouble(),
          phase.Value.GetProperty("bytesEstimate").GetInt64());
    }
    return new PdfPipelineBrowserSnapshot(
        marks,
        root.GetProperty("phase").GetString() ?? "",
        (int)root.GetProperty("frames").GetProperty("total").GetDouble(),
        gaps,
        visibility,
        focus,
        root.GetProperty("stateMessages").GetProperty("total").GetInt64(),
        root.GetProperty("stateMessages").GetProperty("bytesEstimate").GetInt64(),
        root.GetProperty("stateMessages").GetProperty("listener").GetBoolean(),
        (int)root.GetProperty("stateMessages").GetProperty("errors").GetDouble(),
        byPhase);
  }

  /// <summary>公开复选框精确选择目标页集合（含反选非目标页），等待宿主确认。</summary>
  private async Task SelectPdfPipelinePagesAsync(int[] targets, int pageCount)
  {
    var desired = new HashSet<int>(targets);
    int windowEnd = Math.Min(pageCount, PdfPipelinePageSelection.MaxSelectableWindow);
    for (int index = 0; index < windowEnd; index++)
    {
      string selector = $"[aria-label='选择第 {index + 1} 页']";
      await WaitForSmokeDomAsync(
        $"!!document.querySelector({JsonSerializer.Serialize(selector)})", TimeSpan.FromSeconds(30));
      bool want = desired.Contains(index);
      string applied = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        $$"""
        (() => { const e = document.querySelector({{JsonSerializer.Serialize(selector)}});
            if (!e) return 'missing';
            if (!!e.checked !== {{JsonSerializer.Serialize(want)}}) e.click();
            return e.checked === {{JsonSerializer.Serialize(want)}} ? 'ok' : 'pending'; })()
        """);
      if (applied is not "\"ok\"" and not "\"pending\"")
        throw new InvalidOperationException($"Pipeline page checkbox failed at page {index + 1}: {applied}");
    }
    int[] ordered = targets.Order().ToArray();
    await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.SelectedPages is { } selected &&
        selected.Order().SequenceEqual(ordered),
      TimeSpan.FromSeconds(30));
  }

  private static object PdfPipelinePageFacts(PdfWorkbenchState state, int[] targets) => new
  {
    pages = targets.Select(index => state.Pages?.FirstOrDefault(page => page.Index == index))
      .Select(page => page is null ? null : new
      {
        page.Index,
        page.StatusCode,
        page.AddedThisSession,
        page.Detected,
        page.HasTextLayer,
        page.DetectionState,
        page.RecognitionRevision,
        page.Width,
        page.Height,
        page.PageKey,
        page.ContentGeneration,
      }).ToList(),
  };

  /// <summary>终态 job 快照：真实 worker/引擎/实例归属证明（supervisor 域时间戳只作参考）。</summary>
  private async Task<List<object>> CollectPdfPipelineJobEvidenceAsync(
      ProductRuntimeSession session, IReadOnlyList<PdfSmokeRequestProbe.RequestEvent> window)
  {
    var jobs = new List<object>();
    foreach (PdfSmokeRequestProbe.RequestEvent submit in window.Where(request =>
        request.Kind == "job_submit" && request.Outcome == "completed"))
    {
      string jobId = submit.Detail ?? throw new InvalidOperationException(
        $"job_submit event is missing its job id detail: page={submit.Page}");
      JobUpdate observed = await session.Client.ObserveAsync(jobId, 0, CancellationToken.None);
      JobSnapshot snapshot = observed.Snapshot;
      if (snapshot.JobId != jobId)
        throw new InvalidOperationException($"Observed job {snapshot.JobId} does not match submitted {jobId}.");
      if (snapshot.InstanceId != session.InstanceId)
        throw new InvalidOperationException(
          $"Job {jobId} belongs to supervisor {snapshot.InstanceId}, expected {session.InstanceId}.");
      jobs.Add(new
      {
        job_id = jobId,
        snapshot.State,
        engine = snapshot.Pipeline?.Engine?.ToString(),
        pipeline = snapshot.Pipeline?.PipelineId,
        snapshot.InstanceId,
        snapshot.EnvironmentId,
        snapshot.CreatedAt,
        snapshot.StartedAt,
        snapshot.FinishedAt,
        snapshot.Summary,
        items = snapshot.Items.Count,
        upload_bytes_wire = submit.Bytes,
        timestamps_note = "CreatedAt/StartedAt/FinishedAt 为 supervisor 域字符串时间戳，仅作提交→开跑→终态参考，不与宿主/浏览器域相减",
      });
    }
    return jobs;
  }

  /// <summary>单类阶段统计；缺测时保持 NOT_MEASURED 语义，不造 0。</summary>
  private static object PdfPipelinePhaseStat(
      string kind, IReadOnlyDictionary<string, PdfPipelineKindStat> byKind, string note,
      string missingNote) =>
    byKind.TryGetValue(kind, out PdfPipelineKindStat? stat)
      ? new { present = true, stat, note }
      : new { present = false, note = missingNote };

  private async Task<object> RunPaddlePdfPipelineAsync(
      string mode, string fixture, ProductRuntimeSession session, int timeoutMinutes)
  {
    if (mode != "paddle_text")
      throw new InvalidOperationException(
        $"pdf_pipeline smoke requires the plain OCR mode paddle_text, got {mode}.");
    string pagesSpec = PaddleSmokeEnv("VIBEOCR_T3_PIPELINE_PAGES") ?? "all";
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      RecordPaddleSmokeStage("pdf pipeline: install observer");
      await NavigateSmokeAsync("PDF", "button");
      string installed = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        PdfPipelineObserverScript);
      if (installed is not "\"installed\"")
        throw new InvalidOperationException($"Pipeline observer install failed: {installed}");

      var fixtureBefore = new FileInfo(fixture);
      object passCold = await RunPdfPipelinePassAsync(
          "cold", mode, fixture, pagesSpec, session, timeoutMinutes, probe);
      object passSteady = await RunPdfPipelinePassAsync(
          "steady", mode, fixture, pagesSpec, session, timeoutMinutes, probe);
      var fixtureAfter = new FileInfo(fixture);
      if (fixtureAfter.Length != fixtureBefore.Length ||
          fixtureAfter.LastWriteTimeUtc != fixtureBefore.LastWriteTimeUtc)
        throw new InvalidOperationException(
          "Pipeline fixture changed during the run; dirty document must not write the source file.");

      RecordPaddleSmokeStage("pdf pipeline: measurements complete");
      var (events, maxInflight, overflow) = probe.Snapshot();
      if (overflow)
        throw new InvalidOperationException(
          "Pipeline request probe event buffer overflowed; counts would be truncated — failing explicitly.");
      if (events.Any(request => request.Outcome == "faulted"))
        throw new InvalidOperationException("Pipeline run recorded faulted wire requests; failing explicitly.");
      SettingsSnapshot settings = await session.Client.GetSettingsAsync(CancellationToken.None);
      paddleSmokeOutcome = "passed";
      return new
      {
        input_kind = "pdf_pipeline",
        sample_status = "valid",
        performance_eligible = true,
        config = new
        {
          device = PaddleSmokeDevice,
          provider_settings = settings.RecognitionDevice,
          model_preset = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_PRESET") ?? "small",
          mode,
          pipeline = "OCR",
          supervisor = session.InstanceId,
          process_pid = Environment.ProcessId,
          page_spec = pagesSpec,
          fixture = new { path = fixture, bytes = fixtureBefore.Length },
          fixture_untouched = new
          {
            bytes_before = fixtureBefore.Length,
            bytes_after = fixtureAfter.Length,
            last_write_utc_before = fixtureBefore.LastWriteTimeUtc,
            last_write_utc_after = fixtureAfter.LastWriteTimeUtc,
          },
          timeout_minutes = timeoutMinutes,
          synthetic_only = true,
        },
        passes = new[] { passCold, passSteady },
        requests = new
        {
          note = "DeferredInferenceClient 冒烟探针记录的 App→Runtime wire 请求；job_observe 为宿主到 supervisor 的长轮询往返，不代表设备等待",
          max_inflight = maxInflight,
          counts = events.GroupBy(request => request.Kind)
            .ToDictionary(group => group.Key, group => group.Count()),
          outcomes = events.GroupBy(request => request.Outcome)
            .ToDictionary(group => group.Key, group => group.Count()),
          events = events.Select(request => new
          {
            request.Kind,
            request.Session,
            request.Page,
            request.Param,
            request.StartedAtMs,
            request.EndedAtMs,
            request.DurationMs,
            request.Outcome,
            request.Error,
            request.Bytes,
            request.Pixels,
            request.Detail,
          }),
        },
        clock_domains = new
        {
          browser = "performance.now() ms（动作 mark、双 RAF 帧机会、app.state 事件）",
          host = "System.Diagnostics.Stopwatch.GetTimestamp（公开动作里程与 wire 请求计时）",
          supervisor = "job 快照 CreatedAt/StartedAt/FinishedAt 字符串时间戳（仅参考）",
          rule = "三域原始时间永不互相比较；每处差值都取自同一域",
        },
        frame_witness_semantics = "双 RAF 是渲染机会（frame opportunity）而非实际 paint 证明；帧尾统计含 visibility/focus 事实",
        measurement_limits = new
        {
          active_batch = "活动批次由 wire 事件（presence/render/submit/observe/write）与 job 快照刻画；worker 进程内 native 阶段不可见",
          terminal_retention = "NOT_MEASURED：supervisor 终态结果/_outcomes 留存（results 与 _outcomes 引用同一 payload，不重复计份）、JobRegistry purge 仅标记不清内存，本 checkpoint 不测量不修改",
          document_metadata = "NOT_MEASURED：文档镜像/元数据留存大小未测；App 侧仅记录宿主 working set 采样",
          disk_staging = "NOT_MEASURED（进程内）：worker/supervisor 磁盘 staging（含原 PNG 终态留存、retry 仅需 failed/cancelled）的峰值与留存由外部 monitor/磁盘事后采样另行披露，不以进程内数据冒充",
          private_bytes_cpu = "NOT_MEASURED：process probe 仅采样 working set；private bytes/CPU 时间未采样，不以 WS 或 wall time 冒充设备等待",
          state_message_bytes = "估计值：浏览器侧对 app.state/pdf payload 重编码估字节数，非精确 wire 字节",
        },
      };
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "window.__t3PipelineObserver?.dispose() === true");
    }
  }

  private async Task<object> RunPdfPipelinePassAsync(
      string label, string mode, string fixture, string pagesSpec,
      ProductRuntimeSession session, int timeoutMinutes, PdfSmokeRequestProbe probe)
  {
    RecordPaddleSmokeStage($"pdf pipeline {label}: open document");
    await SetPdfPipelinePhaseAsync($"{label}:open");
    await MarkPdfPipelineBrowserAsync($"{label}_open_click");
    long openClickAt = PdfPipelineNow();
    long pickConfirmAt = 0;
    await ClickWorkbenchSmokeButtonAsync("打开 PDF");
    await CompletePaddleOpenPickerAsync(fixture, onBeforeConfirm: async () =>
    {
      await MarkPdfPipelineBrowserAsync($"{label}_pick_confirm");
      pickConfirmAt = PdfPipelineNow();
    });
    PdfWorkbenchState opened = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.PageCount > 0 &&
        state.PageInspectStatusCode == "pdf.inspect.ready",
      TimeSpan.FromMinutes(2));
    long readyAt = PdfPipelineNow();
    if (opened.ProcessingSettings is not { RenderDpi: > 0 } pipelineSettings)
      throw new InvalidOperationException("Pipeline render DPI/MaxPixels unavailable.");
    int[] targets = PdfPipelinePageSelection.Resolve(pagesSpec, opened.PageCount);

    RecordPaddleSmokeStage($"pdf pipeline {label}: select engine and pages");
    await SetPdfPipelinePhaseAsync($"{label}:select");
    await WaitForPaddleModeAsync("#pdf-task-engine", mode);
    await SelectSmokeValueAsync("#pdf-task-engine", mode);
    if (pagesSpec == "all" && opened.PageCount == targets.Length)
    {
      await ClickWorkbenchSmokeButtonAsync("全选页面");
    }
    else
    {
      await SelectPdfPipelinePagesAsync(targets, opened.PageCount);
    }
    PdfWorkbenchState selected = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.TaskEngine == mode &&
        state.CanAddTextLayer && state.SelectedPages is { } pages &&
        pages.Order().SequenceEqual(targets.Order()),
      TimeSpan.FromSeconds(30));
    long selectedAt = PdfPipelineNow();
    long revisionBefore = selected.Revision;
    int detectedAtStart = selected.DetectedCount;
    long workingSetAtStart = Environment.WorkingSet;

    RecordPaddleSmokeStage($"pdf pipeline {label}: add text layers via public button");
    await SetPdfPipelinePhaseAsync($"{label}:ocr");
    await MarkPdfPipelineBrowserAsync($"{label}_ocr_click");
    long ocrClickAt = PdfPipelineNow();
    await ClickWorkbenchSmokeButtonAsync("添加选中页文字层");
    PdfWorkbenchState terminal = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && targets.All(index =>
        state.Pages?.FirstOrDefault(page => page.Index == index)?.StatusCode
          is "pdf.page.done" or "pdf.page.failed"),
      TimeSpan.FromMinutes(timeoutMinutes));
    long terminalAt = PdfPipelineNow();
    long workingSetAtTerminal = Environment.WorkingSet;
    var failedPages = targets
      .Where(index => terminal.Pages?.FirstOrDefault(page => page.Index == index)?.StatusCode != "pdf.page.done")
      .ToList();
    if (failedPages.Count > 0)
      throw new InvalidOperationException(
        $"Pipeline OCR failed on pages {string.Join(',', failedPages)}: {terminal.Summary}");
    if (!terminal.IsModified)
      throw new InvalidOperationException(
        "Pipeline OCR must leave the document dirty (explicit save only).");
    double ocrFromMs = PdfPipelineToMs(ocrClickAt);
    double ocrToMs = PdfPipelineToMs(terminalAt);
    var (allEvents, _, _) = probe.Snapshot();
    List<PdfSmokeRequestProbe.RequestEvent> window =
      PdfPipelinePhaseStats.Window(allEvents, ocrFromMs, ocrToMs);
    List<string> errors = PdfPipelinePhaseStats.ValidateOcrWindow(
        window, PdfPipelinePageSelection.ExpectedBatches(targets.Length), targets.Length);
    if (errors.Count > 0)
      throw new InvalidOperationException(
        $"Pipeline OCR window validation failed for {label}: " + JsonSerializer.Serialize(errors));
    List<object> jobs = await CollectPdfPipelineJobEvidenceAsync(session, window);

    RecordPaddleSmokeStage($"pdf pipeline {label}: save via public picker");
    await SetPdfPipelinePhaseAsync($"{label}:save");
    long saveStartAt = PdfPipelineNow();
    object saved = await RunPaddlePdfSaveAsync($"pipeline-{label}");
    long savedAt = PdfPipelineNow();
    PdfWorkbenchState savedState = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && !state.IsModified, TimeSpan.FromMinutes(2));

    RecordPaddleSmokeStage($"pdf pipeline {label}: close document");
    await SetPdfPipelinePhaseAsync($"{label}:close");
    long closeStartAt = PdfPipelineNow();
    await ClickWorkbenchSmokeButtonAsync("关闭文档");
    await WaitForPaddlePdfAsync(
      state => state.PageCount == 0 && (state.Documents?.Count ?? 0) == 0,
      TimeSpan.FromSeconds(30));
    long closedAt = PdfPipelineNow();

    PdfPipelineBrowserSnapshot browser = await PdfPipelineSnapshotAsync();
    if (!browser.StateMessagesListener)
      throw new InvalidOperationException("Pipeline state message listener unavailable; byte metric cannot be measured.");
    if (browser.StateMessagesErrors > 0)
      throw new InvalidOperationException(
        $"Pipeline state message measurement had {browser.StateMessagesErrors} errors; failing explicitly.");
    List<double> ocrGaps = browser.FrameGaps
      .Where(gap => gap.Phase == $"{label}:ocr").Select(gap => gap.GapMs).ToList();
    (int gapCount, double? gapMedian, double? gapMax, int gapOver250) =
      PdfPipelinePhaseStats.FrameGaps(ocrGaps);
    IReadOnlyDictionary<string, PdfPipelineKindStat> byKind =
      PdfPipelinePhaseStats.ByKind(window);

    return new
    {
      label,
      keeps_cold_worker_cost = label == "cold",
      input_equivalence = $"重开同一 fixture 文件（{fixture}），新会话独立统计",
      host_ms = new
      {
        open_click_to_pick_confirm = PdfPipelineElapsedMs(openClickAt, pickConfirmAt),
        pick_confirm_to_ready = PdfPipelineElapsedMs(pickConfirmAt, readyAt),
        ready_to_selected = PdfPipelineElapsedMs(readyAt, selectedAt),
        selected_to_ocr_click = PdfPipelineElapsedMs(selectedAt, ocrClickAt),
        ocr_click_to_terminal = PdfPipelineElapsedMs(ocrClickAt, terminalAt),
        save_click_to_saved = PdfPipelineElapsedMs(saveStartAt, savedAt),
        close_to_settled = PdfPipelineElapsedMs(closeStartAt, closedAt),
        pick_confirm_to_ocr_terminal_total = PdfPipelineElapsedMs(pickConfirmAt, terminalAt),
      },
      settings = new
      {
        pipelineSettings.RenderDpi,
        pipelineSettings.MaxPixels,
        page_geometry = targets.Select(index => opened.Pages?.FirstOrDefault(page => page.Index == index))
          .Where(page => page is not null)
          .Select(page => new { page!.Index, page.Width, page.Height }).ToList(),
      },
      detection = new
      {
        detected_count_at_ocr_start = detectedAtStart,
        detected_count_at_terminal = terminal.DetectedCount,
        page_count = opened.PageCount,
        selected_pages = targets,
      },
      phases = new
      {
        presence = PdfPipelinePhaseStat("load_targeted", byKind,
          "EnsurePresence 定向 presence 请求", "NOT_MEASURED: 本窗口无 load_targeted wire 请求（presence 可能已满足）"),
        render = PdfPipelinePhaseStat("render_preview", byKind,
          "业务页 PNG 渲染（RenderPdfPreviewDemand）；bytes 为真实传输字节，pixels 为 PNG IHDR 实测",
          "NOT_MEASURED: 窗口内无业务渲染请求（校验已拒绝此情形）"),
        submit = PdfPipelinePhaseStat("job_submit", byKind,
          "每批一个通用 job；Bytes 为 multipart 上传内容字节合计", "NOT_MEASURED"),
        observe = PdfPipelinePhaseStat("job_observe", byKind,
          "宿主→supervisor observe 轮询/长轮询 wire 往返；不是设备等待，设备/CPU 等待 NOT_MEASURED",
          "NOT_MEASURED"),
        write = PdfPipelinePhaseStat("add_text_layers", byKind,
          "Save=false 批内写层；不可取消收尾", "NOT_MEASURED（无写层请求；addTextLayer 场景不应出现）"),
      },
      jobs,
      pages = PdfPipelinePageFacts(terminal, targets),
      document = new
      {
        revision_before = revisionBefore,
        revision_after = terminal.Revision,
        is_modified_after_ocr = true,
        added_count = terminal.AddedCount,
        saved = new
        {
          saved,
          save_wire = PdfPipelinePhaseStat("save_operation", byKind, "公开另存为操作 wire（save_operation）", "NOT_MEASURED"),
          is_modified_after_save = savedState.IsModified,
        },
      },
      browser = new
      {
        marks = browser.Marks,
        frames_total_since_install = browser.FramesTotal,
        frame_gaps_during_ocr = new
        {
          count = gapCount,
          median_gap_ms = gapMedian,
          max_gap_ms = gapMax,
          gaps_over_250ms = gapOver250,
          not_measured_when = "count=0 且 median/max 为 null 表示该阶段无帧机会样本，不写 0",
        },
        visibility_samples = browser.VisibilitySamples,
        focus_samples = browser.FocusSamples,
        state_messages = new
        {
          total = browser.StateMessagesTotal,
          bytes_estimate = browser.StateMessagesBytesEstimate,
          by_phase = browser.StateMessagesByPhase,
          method = "chrome.webview 只读监听 app.state/pdf payload 重编码估计；非精确 wire 字节",
        },
      },
      host_process_working_set_bytes = new
      {
        at_selection = workingSetAtStart,
        at_ocr_terminal = workingSetAtTerminal,
        note = "宿主进程 Environment.WorkingSet 采样；进程树峰值由外部 monitor 采样，private bytes NOT_MEASURED",
      },
    };
  }
}
