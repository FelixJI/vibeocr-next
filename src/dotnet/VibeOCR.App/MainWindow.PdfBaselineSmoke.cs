using System.Diagnostics;
using System.Text.Json;
using VibeOCR.App.Inference;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App;

/// <summary>
/// Goal #325/#327 T2 优化前原生基线观测（INPUT_KIND=pdf_baseline）。只观测、
/// 不优化：浏览器端 performance.now 记录动作 mark、img.decode() 完成与双 RAF
/// 帧机会（非实际 paint，含可见性事实）；宿主 Stopwatch 独立记录 wire 请求。
/// 两域原始时间互不比较。首图/重开/新文档必须为动作后的新 img 事件；缓存重访
/// 无新解码时以动作后匹配当前页的帧机会见证。缺失/超时显式 FAIL，null 不作 0。
/// </summary>
public sealed partial class MainWindow
{
  private const string PdfBaselineObserverScript = """
    (() => {
      if (window.__t2PdfBaseline) return 'already';
      const encoder = new TextEncoder();
      const state = {
        marks: {}, images: [], pulses: {}, awaiting: null, observer: null,
        disposed: false, phase: 'pre_open', messageHandler: null,
        stateMessages: {
          phases: {}, totalCount: 0, totalBytesEstimate: 0,
          listenerAvailable: false, measurementErrors: 0,
        },
      };
      function currentImgFacts(img) {
        if (!img) return null;
        return {
          alt: img.getAttribute('alt') || '',
          src: img.currentSrc || img.getAttribute('src') || '',
          clientWidth: img.clientWidth, clientHeight: img.clientHeight,
          naturalWidth: img.naturalWidth, naturalHeight: img.naturalHeight,
          complete: img.complete, connected: img.isConnected,
          hasLayout: img.getClientRects().length > 0,
        };
      }
      function record(img) {
        const entry = {
          alt: img.getAttribute('alt') || '',
          fullSrc: img.currentSrc || img.getAttribute('src') || '',
          srcSetAt: performance.now(), decodeStartedAt: null,
          decodedAt: null, frameOpportunityAt: null, decodeError: null,
          superseded: false, naturalWidth: 0, naturalHeight: 0,
          clientWidth: null, clientHeight: null, dprAtFrame: null,
          visibilityAtFrame: null, hasFocusAtFrame: null,
        };
        state.images.push(entry);
        if (state.images.length > 96) state.images.splice(0, state.images.length - 96);
        const liveSrc = () => img.currentSrc || img.getAttribute('src') || '';
        const isCurrentInspectionImage = () =>
          !state.disposed &&
          document.querySelector('.pdf-inspection-sheet img') === img;
        entry.decodeStartedAt = performance.now();
        img.decode().then(() => {
          if (!isCurrentInspectionImage() || liveSrc() !== entry.fullSrc) {
            entry.superseded = true;
            return;
          }
          entry.naturalWidth = img.naturalWidth;
          entry.naturalHeight = img.naturalHeight;
          entry.decodedAt = performance.now();
          const firstFrame = performance.now();
          requestAnimationFrame(() => requestAnimationFrame(() => {
            if (!isCurrentInspectionImage() || liveSrc() !== entry.fullSrc ||
                !img.isConnected || img.getClientRects().length === 0) {
              entry.superseded = true;
              return;
            }
            entry.clientWidth = img.clientWidth;
            entry.clientHeight = img.clientHeight;
            entry.dprAtFrame = devicePixelRatio;
            entry.visibilityAtFrame = document.visibilityState;
            entry.hasFocusAtFrame = document.hasFocus();
            entry.frameOpportunityAt = performance.now();
            entry.rafGapMs = entry.frameOpportunityAt - firstFrame;
          }));
        }, (error) => {
          if (!isCurrentInspectionImage() || liveSrc() !== entry.fullSrc) {
            entry.superseded = true;
            return;
          }
          entry.decodeError = String((error && error.name) || error);
        });
      }
      function runAwaiting() {
        if (!state.awaiting || state.disposed) return;
        const first = performance.now();
        requestAnimationFrame(() => requestAnimationFrame(() => {
          const request = state.awaiting;
          if (!request) return;
          const img = document.querySelector('.pdf-inspection-sheet img');
          const facts = currentImgFacts(img);
          const ok = !!facts && facts.alt.includes(request.alt) &&
            facts.complete && facts.naturalWidth > 0 && facts.connected && facts.hasLayout;
          const keys = Object.keys(state.pulses);
          if (keys.length > 32) delete state.pulses[keys[0]];
          state.pulses[request.tag] = {
            at: performance.now(), rafGapMs: performance.now() - first,
            matched: ok, requestedAlt: request.alt,
            visibilityState: document.visibilityState, hasFocus: document.hasFocus(),
            current: facts,
          };
          if (ok) state.awaiting = null; else runAwaiting();
        }));
      }
      const baseline = {
        mark(name) {
          if (state.marks[name] === undefined) state.marks[name] = performance.now();
          return true;
        },
        setPhase(name) { state.phase = String(name); return true; },
        awaitFrame(tag, altFragment) {
          if (state.disposed) return false;
          state.awaiting = { tag: String(tag), alt: String(altFragment) };
          runAwaiting();
          return true;
        },
        snapshot() {
          return JSON.stringify({
            marks: { ...state.marks },
            phase: state.phase,
            pulses: { ...state.pulses },
            images: state.images.slice(-48),
            imageCount: state.images.length,
            current: currentImgFacts(document.querySelector('.pdf-inspection-sheet img')),
            stateMessages: {
              phases: { ...state.stateMessages.phases },
              totalCount: state.stateMessages.totalCount,
              totalBytesEstimate: state.stateMessages.totalBytesEstimate,
              listenerAvailable: state.stateMessages.listenerAvailable,
              measurementErrors: state.stateMessages.measurementErrors,
            },
            dpr: devicePixelRatio,
            viewport: { width: innerWidth, height: innerHeight },
          });
        },
        dispose() {
          state.disposed = true;
          if (state.observer) { state.observer.disconnect(); state.observer = null; }
          if (state.messageHandler && window.chrome && chrome.webview &&
              chrome.webview.removeEventListener) {
            chrome.webview.removeEventListener('message', state.messageHandler);
          }
          state.messageHandler = null;
          state.awaiting = null;
          return true;
        },
      };
      Object.defineProperty(window, '__t2PdfBaseline', {
        value: baseline, configurable: false, enumerable: false,
      });
      if (window.chrome && chrome.webview && chrome.webview.addEventListener) {
        state.stateMessages.listenerAvailable = true;
        state.messageHandler = (event) => {
          try {
            const data = event && event.data;
            if (!data || data.type !== 'app.state' || !data.payload ||
                data.payload.scope !== 'pdf') return;
            const text = JSON.stringify(data.payload);
            if (typeof text !== 'string' || text.length === 0) return;
            const bytes = encoder.encode(text).length;
            const bucket = state.stateMessages.phases[state.phase] ||
              (state.stateMessages.phases[state.phase] = { count: 0, bytesEstimate: 0 });
            bucket.count += 1;
            bucket.bytesEstimate += bytes;
            state.stateMessages.totalCount += 1;
            state.stateMessages.totalBytesEstimate += bytes;
          } catch (error) {
            state.stateMessages.measurementErrors += 1;
          }
        };
        chrome.webview.addEventListener('message', state.messageHandler);
      }
      function scan() {
        document.querySelectorAll('.pdf-inspection-sheet img').forEach((img) => {
          const src = img.currentSrc || img.getAttribute('src') || '';
          if (img.__t2src !== src) {
            img.__t2src = src;
            record(img);
          }
        });
      }
      state.observer = new MutationObserver(scan);
      state.observer.observe(document.body, {
        childList: true, subtree: true, attributes: true, attributeFilter: ['src'],
      });
      scan();
      return 'installed';
    })()
    """;

  private sealed record PdfBaselineBrowserImage(
    string Alt, string FullSrc, double SrcSetAt,
    double? DecodedAt, double? FrameOpportunityAt, string? DecodeError, bool Superseded,
    int NaturalWidth, int NaturalHeight, int? ClientWidth, int? ClientHeight,
    double? DprAtFrame, string? VisibilityAtFrame, bool? HasFocusAtFrame);

  private sealed record PdfBaselineBrowserCurrent(
    string Alt, string Src, int ClientWidth, int ClientHeight,
    int NaturalWidth, int NaturalHeight, bool Complete, bool Connected, bool HasLayout);

  private sealed record PdfBaselinePulse(
    double At, double RafGapMs, bool Matched, string RequestedAlt,
    string VisibilityState, bool HasFocus, PdfBaselineBrowserCurrent? Current);

  private sealed record PdfBaselineStateMessagePhase(int Count, long BytesEstimate);

  private sealed record PdfBaselineBrowserSnapshot(
    IReadOnlyDictionary<string, double> Marks, string Phase,
    IReadOnlyDictionary<string, PdfBaselinePulse> Pulses,
    IReadOnlyList<PdfBaselineBrowserImage> Images, int ImageCount,
    PdfBaselineBrowserCurrent? Current,
    IReadOnlyDictionary<string, PdfBaselineStateMessagePhase> StateMessagePhases,
    int StateMessageTotalCount, long StateMessageTotalBytes,
    bool StateMessageListenerAvailable, int StateMessageMeasurementErrors,
    double Dpr, int ViewportWidth, int ViewportHeight);

  private sealed record PdfBaselineImageWait(
    PdfBaselineBrowserImage? NewWitness, PdfBaselineBrowserImage Witness,
    PdfBaselineBrowserSnapshot Snapshot, PdfBaselinePulse Pulse, bool NewImageObserved);

  private sealed record PdfBaselineClarityReport(
    string Step, string Status, object Detail);

  private static long PdfBaselineNow() => Stopwatch.GetTimestamp();

  private static double PdfBaselineElapsedMs(long start, long end) =>
    (end - start) * 1000.0 / Stopwatch.Frequency;

  private static double PdfBaselineMark(PdfBaselineBrowserSnapshot snapshot, string mark) =>
    snapshot.Marks.TryGetValue(mark, out double value) ? value : double.NaN;

  private async Task<bool> MarkPdfBaselineBrowserAsync(string name)
  {
    string result = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      $"window.__t2PdfBaseline?.mark({JsonSerializer.Serialize(name)}) === true");
    return result == "true";
  }

  private async Task SetPdfBaselinePhaseAsync(string phase)
  {
    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      $"window.__t2PdfBaseline?.setPhase({JsonSerializer.Serialize(phase)})");
  }

  private async Task RequestPdfBaselineFrameAsync(string tag, int expectedPageIndex)
  {
    await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      $"window.__t2PdfBaseline?.awaitFrame({JsonSerializer.Serialize(tag)}, " +
      JsonSerializer.Serialize($"第 {expectedPageIndex + 1} 页") + ") === true");
  }

  private async Task<PdfBaselineBrowserSnapshot> PdfBaselineSnapshotAsync()
  {
    string raw = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      "window.__t2PdfBaseline ? window.__t2PdfBaseline.snapshot() : null");
    if (raw == "null")
      throw new InvalidOperationException("Browser baseline observer is not installed.");
    using JsonDocument outer = JsonDocument.Parse(raw);
    string json = outer.RootElement.GetString()
      ?? throw new InvalidOperationException("Browser baseline snapshot is empty.");
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement root = document.RootElement;
    var marks = new Dictionary<string, double>();
    foreach (JsonProperty mark in root.GetProperty("marks").EnumerateObject())
      marks[mark.Name] = mark.Value.GetDouble();
    var pulses = new Dictionary<string, PdfBaselinePulse>();
    foreach (JsonProperty pulse in root.GetProperty("pulses").EnumerateObject())
    {
      JsonElement value = pulse.Value;
      pulses[pulse.Name] = new PdfBaselinePulse(
        value.GetProperty("at").GetDouble(),
        value.GetProperty("rafGapMs").GetDouble(),
        value.GetProperty("matched").GetBoolean(),
        value.GetProperty("requestedAlt").GetString() ?? "",
        value.GetProperty("visibilityState").GetString() ?? "",
        value.GetProperty("hasFocus").GetBoolean(),
        value.TryGetProperty("current", out JsonElement current) && current.ValueKind == JsonValueKind.Object
          ? ReadCurrent(current) : null);
    }
    var images = new List<PdfBaselineBrowserImage>();
    foreach (JsonElement image in root.GetProperty("images").EnumerateArray())
    {
      images.Add(new PdfBaselineBrowserImage(
        image.GetProperty("alt").GetString() ?? "",
        image.GetProperty("fullSrc").GetString() ?? "",
        image.GetProperty("srcSetAt").GetDouble(),
        OptionalNumber(image, "decodedAt"),
        OptionalNumber(image, "frameOpportunityAt"),
        OptionalText(image, "decodeError"),
        image.GetProperty("superseded").GetBoolean(),
        (int)image.GetProperty("naturalWidth").GetDouble(),
        (int)image.GetProperty("naturalHeight").GetDouble(),
        OptionalInt(image, "clientWidth"),
        OptionalInt(image, "clientHeight"),
        OptionalNumber(image, "dprAtFrame"),
        OptionalText(image, "visibilityAtFrame"),
        image.TryGetProperty("hasFocusAtFrame", out JsonElement focus) && focus.ValueKind == JsonValueKind.True ? true
          : image.TryGetProperty("hasFocusAtFrame", out JsonElement focusFalse) && focusFalse.ValueKind == JsonValueKind.False ? false : null));
    }
    var phases = new Dictionary<string, PdfBaselineStateMessagePhase>();
    foreach (JsonProperty phase in root.GetProperty("stateMessages").GetProperty("phases").EnumerateObject())
    {
      phases[phase.Name] = new PdfBaselineStateMessagePhase(
        (int)phase.Value.GetProperty("count").GetDouble(),
        phase.Value.GetProperty("bytesEstimate").GetInt64());
    }
    return new PdfBaselineBrowserSnapshot(
      marks,
      root.GetProperty("phase").GetString() ?? "",
      pulses,
      images,
      (int)root.GetProperty("imageCount").GetDouble(),
      root.TryGetProperty("current", out JsonElement live) && live.ValueKind == JsonValueKind.Object
        ? ReadCurrent(live) : null,
      phases,
      (int)root.GetProperty("stateMessages").GetProperty("totalCount").GetDouble(),
      root.GetProperty("stateMessages").GetProperty("totalBytesEstimate").GetInt64(),
      root.GetProperty("stateMessages").GetProperty("listenerAvailable").GetBoolean(),
      (int)root.GetProperty("stateMessages").GetProperty("measurementErrors").GetDouble(),
      root.GetProperty("dpr").GetDouble(),
      (int)root.GetProperty("viewport").GetProperty("width").GetDouble(),
      (int)root.GetProperty("viewport").GetProperty("height").GetDouble());

    static PdfBaselineBrowserCurrent ReadCurrent(JsonElement current) => new(
      current.GetProperty("alt").GetString() ?? "",
      current.GetProperty("src").GetString() ?? "",
      (int)current.GetProperty("clientWidth").GetDouble(),
      (int)current.GetProperty("clientHeight").GetDouble(),
      (int)current.GetProperty("naturalWidth").GetDouble(),
      (int)current.GetProperty("naturalHeight").GetDouble(),
      current.GetProperty("complete").GetBoolean(),
      current.GetProperty("connected").GetBoolean(),
      current.TryGetProperty("hasLayout", out JsonElement layout) && layout.ValueKind == JsonValueKind.True);

    static double? OptionalNumber(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        ? value.GetDouble() : null;

    static string? OptionalText(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;

    static int? OptionalInt(JsonElement element, string name) =>
      element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        ? (int)value.GetDouble() : null;
  }

  /// <summary>
  /// 等待当前选定页的实际检查图。requireNew（冷开/reopen/新文档）：必须出现
  /// afterMark 之后首个新 img 条目（decode 完成且帧机会成立），且动作后帧见
  /// 证（awaitFrame）确认当前 DOM 匹配目标页；超时 FAIL。非 requireNew
  /// （warm 重访/缩放/reactivate）：缓存无新解码可接受，但必须有动作后匹配
  /// 当前页的帧机会见证与已解码当前帧。旧 RAF/旧历史帧不算新见证。
  /// </summary>
  private async Task<PdfBaselineImageWait> WaitForPdfBaselineImageAsync(
    string phase, int expectedPageIndex, string afterMark, string pulseTag,
    TimeSpan timeout, bool requireNew)
  {
    string altFragment = $"第 {expectedPageIndex + 1} 页";
    using var cancellation = new CancellationTokenSource(timeout);
    PdfBaselineBrowserSnapshot? last = null;
    DateTime lastFrameRequest = DateTime.MinValue;
    while (true)
    {
      PdfBaselineBrowserSnapshot snapshot = await PdfBaselineSnapshotAsync();
      last = snapshot;
      double markValue = PdfBaselineMark(snapshot, afterMark);
      List<PdfBaselineBrowserImage> matching = snapshot.Images
        .Where(image => image.DecodedAt is not null && image.FrameOpportunityAt is not null
          && !image.Superseded && image.NaturalWidth > 0
          && image.Alt.Contains(altFragment, StringComparison.Ordinal))
        .ToList();
      PdfBaselineBrowserImage? firstNew = matching
        .Where(image => !double.IsNaN(markValue) && image.SrcSetAt >= markValue)
        .OrderBy(image => image.SrcSetAt)
        .FirstOrDefault();
      // 帧见证当前性：pulse 必须在动作 mark 之后，且其 src 与当前 DOM src 一致；
      // witness 的 FullSrc 也必须与当前 DOM src 完全一致，不拿旧 pulse/旧图配新图。
      PdfBaselinePulse? pulse = null;
      if (snapshot.Pulses.TryGetValue(pulseTag, out var candidatePulse)
          && candidatePulse.At >= (double.IsNaN(markValue) ? double.NegativeInfinity : markValue)
          && candidatePulse.Matched
          && candidatePulse.Current is { Complete: true, NaturalWidth: > 0, HasLayout: true }
          && snapshot.Current is { } liveNow
          && liveNow.Alt.Contains(altFragment, StringComparison.Ordinal)
          && candidatePulse.Current.Src == liveNow.Src)
        pulse = candidatePulse;
      bool liveMatches = snapshot.Current is { } live2
        && live2.Alt.Contains(altFragment, StringComparison.Ordinal) && live2.Complete
        && live2.NaturalWidth > 0;
      PdfBaselineBrowserImage? currentWitness = matching
        .Where(image => image.FullSrc == snapshot.Current?.Src)
        .OrderByDescending(image => image.SrcSetAt).FirstOrDefault();
      if ((!requireNew || firstNew is not null) && pulse is not null && liveMatches
          && currentWitness is not null)
      {
        return new PdfBaselineImageWait(firstNew, currentWitness, snapshot, pulse, firstNew is not null);
      }
      if ((DateTime.UtcNow - lastFrameRequest).TotalMilliseconds >= 500)
      {
        // pulse 缺失/过期/src 不一致：重发帧请求（页面侧会重试到当前帧）。
        await RequestPdfBaselineFrameAsync(pulseTag, expectedPageIndex);
        lastFrameRequest = DateTime.UtcNow;
      }
      try { await Task.Delay(100, cancellation.Token); }
      catch (OperationCanceledException)
      {
        string detail = JsonSerializer.Serialize(new
        {
          phase,
          expected_page = expectedPageIndex,
          require_new = requireNew,
          after_mark = afterMark,
          after_mark_value = markValue,
          pulse_tag = pulseTag,
          pulse = snapshot.Pulses.GetValueOrDefault(pulseTag),
          live_current = snapshot.Current,
          imageCount = last.ImageCount,
          images = last.Images.Select(image => new
          {
            image.Alt,
            image.FullSrc,
            image.SrcSetAt,
            image.DecodedAt,
            image.FrameOpportunityAt,
            image.DecodeError,
            image.Superseded,
          }),
          marks = last.Marks,
        });
        throw new TimeoutException($"PDF baseline image witness missing for {phase}: {detail}");
      }
    }
  }

  /// <summary>宿主资源归属：最终 hostState.PagePreview.Url 必须与该步快照内
  /// 某个非 superseded 已解码条目的 FullSrc 完全一致且 alt 匹配；不只核页码。
  /// 缺 PagePreview 或无匹配即 FAIL，不把错文档资源当有效样本。</summary>
  private static void ValidatePdfBaselineResourceOwnership(
    string step, PdfWorkbenchState state, PdfBaselineImageWait wait, int expectedPageIndex)
  {
    string altFragment = $"第 {expectedPageIndex + 1} 页";
    string? previewUrl = state.PagePreview?.Url;
    bool owned = previewUrl is not null && previewUrl == wait.Witness.FullSrc && wait.Snapshot.Images.Any(image =>
      !image.Superseded && image.DecodedAt is not null &&
      image.FullSrc == previewUrl &&
      image.Alt.Contains(altFragment, StringComparison.Ordinal));
    if (!owned)
    {
      throw new InvalidOperationException(
        $"PDF baseline resource ownership mismatch at {step}: host PagePreview.Url=" +
        $"{previewUrl ?? "<null>"}; witness candidates=" +
        JsonSerializer.Serialize(wait.Snapshot.Images
          .Where(image => !image.Superseded && image.DecodedAt is not null)
          .Select(image => new { image.Alt, image.FullSrc })));
    }
  }

  private async Task ClickPdfBaselineZoomAsync(string ariaLabel)
  {
    string result = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      $$"""(() => { const button = document.querySelector("button[aria-label='{{ariaLabel}}']"); if (!button) return false; button.click(); return true; })()""");
    if (result != "true")
      throw new InvalidOperationException($"Baseline zoom button is missing: {ariaLabel}");
  }

  private static object PdfBaselineStateFacts(PdfWorkbenchState state) => new
  {
    state.PageCount,
    state.DetectedCount,
    state.SelectedPage,
    state.IsBusy,
    state.StatusCode,
    state.PageInspectStatusCode,
    state.Phase,
    state.Summary,
    state.SessionId,
    state.DocumentId,
    window_start = state.WindowStart,
    render_dpi = state.ProcessingSettings?.RenderDpi,
    max_pixels = state.ProcessingSettings?.MaxPixels,
    documents = state.Documents?.Select(document => new { document.DocumentId, document.PageCount }),
  };

  /// <summary>
  /// 清晰度：逐维比较（naturalWidth/Height ≥ 各自 clientWidth/Height×DPR 的
  /// 98%），不用总面积掩盖单维不足；MaxPixels 取自宿主真实
  /// ProcessingSettings.MaxPixels，仅作界限披露。显示尺寸缺失记 UNKNOWN，
  /// 并使 clarity_evidence=incomplete，不得当作有效完整清晰度证据。
  /// </summary>
  private static PdfBaselineClarityReport PdfBaselineClarity(
    string step, PdfBaselineBrowserImage witness, PdfBaselinePulse pulse,
    PdfBaselineBrowserSnapshot snapshot, double dprFallback)
  {
    PdfBaselineBrowserCurrent? live = pulse.Current?.Src == witness.FullSrc ? pulse.Current : null;
    double dpr = witness.DprAtFrame ?? dprFallback;
    int clientWidth = live?.ClientWidth ?? witness.ClientWidth ?? 0;
    int clientHeight = live?.ClientHeight ?? witness.ClientHeight ?? 0;
    int naturalWidth = live is { NaturalWidth: > 0 } ? live.NaturalWidth : witness.NaturalWidth;
    int naturalHeight = live is { NaturalHeight: > 0 } ? live.NaturalHeight : witness.NaturalHeight;
    double targetWidth = clientWidth * dpr;
    double targetHeight = clientHeight * dpr;
    string status = clientWidth <= 0 || clientHeight <= 0 || naturalWidth <= 0 || naturalHeight <= 0
      ? "UNKNOWN"
      : naturalWidth + naturalWidth * 0.02 >= targetWidth &&
        naturalHeight + naturalHeight * 0.02 >= targetHeight ? "REACHED" : "NOT_REACHED";
    return new PdfBaselineClarityReport(step, status, new
    {
      step,
      status,
      natural = new { width = naturalWidth, height = naturalHeight },
      target = new
      {
        css = new { width = clientWidth, height = clientHeight },
        device_px = new { width = targetWidth, height = targetHeight },
        dpr,
        per_dimension = "宽/高各自对比，不以总面积掩盖单维不足",
      },
      visibility = new
      {
        visibility_state = pulse.VisibilityState,
        has_focus = pulse.HasFocus,
        note = "帧机会为双 RAF 渲染机会而非实际 paint 证明；窗口被系统弹窗遮挡时不得视为屏幕可见帧",
      },
      frame_source = live is null ? "witness_entry" : "pulse_current",
    });
  }

  private async Task<object> RunPaddlePdfBaselineAsync(
    string mode, string fixture, ProductRuntimeSession session, int timeoutMinutes)
  {
    string? second = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_SECOND_FIXTURE");
    if (!string.IsNullOrWhiteSpace(second))
      second = ValidatePaddleSmokeOwnedPath(second, "baseline second fixture");
    var probe = new PdfSmokeRequestProbe();
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      return await RunPaddlePdfBaselineCoreAsync(mode, fixture, second, session, probe, timeoutMinutes);
    }
    finally
    {
      DeferredInferenceClient.SetPdfSmokeProbe(null);
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "window.__t2PdfBaseline?.dispose() === true");
    }
  }

  private async Task<object> RunPaddlePdfBaselineCoreAsync(
    string mode, string fixture, string? second, ProductRuntimeSession session,
    PdfSmokeRequestProbe probe, int timeoutMinutes)
  {
    RecordPaddleSmokeStage("pdf baseline: open document");
    await NavigateSmokeAsync("PDF", "button");
    string installed = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      PdfBaselineObserverScript);
    if (installed is not "\"installed\"")
      throw new InvalidOperationException($"Baseline observer install failed: {installed}");
    await SetPdfBaselinePhaseAsync("cold_open");

    // file_pick_confirm 在真正提交确认前设置（排除 picker 填写耗时）；
    // open_pdf_clicked 含 picker 时间，两者口径同时披露。
    await MarkPdfBaselineBrowserAsync("open_pdf_clicked");
    long openClickAt = PdfBaselineNow();
    long pickConfirmAt = 0;
    await ClickWorkbenchSmokeButtonAsync("打开 PDF");
    await CompletePaddleOpenPickerAsync(fixture, onBeforeConfirm: async () =>
    {
      await MarkPdfBaselineBrowserAsync("file_pick_confirm");
      pickConfirmAt = PdfBaselineNow();
    });
    long fileSelectedAt = PdfBaselineNow();
    await MarkPdfBaselineBrowserAsync("file_selected");
    await RequestPdfBaselineFrameAsync("cold", 0);
    PdfBaselineImageWait cold = await WaitForPdfBaselineImageAsync(
      "cold-first-image", 0, "file_pick_confirm", "cold", TimeSpan.FromMinutes(2), requireNew: true);
    if (!cold.Snapshot.StateMessageListenerAvailable)
      throw new InvalidOperationException(
        "PDF state message listener unavailable; state-byte metric cannot be measured (reported as unavailable, not 0).");
    long coldFirstImageAt = PdfBaselineNow();
    PdfBaselineBrowserImage coldWitness = cold.NewWitness!;
    PdfWorkbenchState coldState = await WaitForPaddlePdfAsync(
      state => state.PageCount > 0, TimeSpan.FromSeconds(30));
    if (coldState.SelectedPage != 0)
      throw new InvalidOperationException(
        $"Baseline first image page mismatch: selected={coldState.SelectedPage}");
    if (coldState.ProcessingSettings is not { RenderDpi: > 0 } baselineSettings)
      throw new InvalidOperationException("Baseline render DPI/MaxPixels unavailable.");
    int coldDpi = baselineSettings.RenderDpi;
    int maxPixels = baselineSettings.MaxPixels;
    double pickConfirmToDecoded = coldWitness.DecodedAt!.Value - PdfBaselineMark(cold.Snapshot, "file_pick_confirm");
    PdfWorkbenchState detected = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.DetectedCount == state.PageCount && state.PageCount > 0,
      TimeSpan.FromMinutes(timeoutMinutes));
    long detectionDoneAt = PdfBaselineNow();
    ValidatePdfBaselineResourceOwnership("cold_first_image", detected, cold, 0);

    await SetPdfBaselinePhaseAsync("warm_revisits");
    long warmStartAt = PdfBaselineNow();
    await MarkPdfBaselineBrowserAsync("warm_start");
    int warmTarget = Math.Min(detected.PageCount - 1, Math.Max(1, detected.PageCount / 2));
    if (warmTarget >= detected.WindowStart + 64)
    {
      await ClickWorkbenchSmokeButtonAsync("下一组");
      await WaitForPaddlePdfAsync(state => state.WindowStart > 0, TimeSpan.FromSeconds(30));
    }
    string chooseSelector = $"button[aria-label='检查第 {warmTarget + 1} 页']";
    await WaitForSmokeDomAsync(
      $"!!document.querySelector({JsonSerializer.Serialize(chooseSelector)})",
      TimeSpan.FromSeconds(30));
    long warmPageMarkAt = PdfBaselineNow();
    await MarkPdfBaselineBrowserAsync("warm_page_selected");
    // 公开 setCurrentPage 入口（PdfPage 检查按钮）；scrollIntoView 确保实际可见。
    // 旧版按钮需缩略图先到达，如实计入等待，不为 smoke 改业务选择语义。
    string clicked = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      $$"""(() => { const button = document.querySelector("button[aria-label='检查第 {{warmTarget + 1}} 页']"); if (!button) return false; button.scrollIntoView({ block: 'center' }); button.click(); return true; })()""");
    if (clicked != "true")
      throw new InvalidOperationException($"Baseline setCurrentPage button missing for page {warmTarget + 1}");
    await RequestPdfBaselineFrameAsync("warm_page", warmTarget);
    PdfWorkbenchState warmPageState = await WaitForPaddlePdfAsync(
      state => state.SelectedPage == warmTarget, TimeSpan.FromSeconds(30));
    PdfBaselineImageWait warmPage = await WaitForPdfBaselineImageAsync(
      $"warm-page-{warmTarget}", warmTarget, "warm_page_selected", "warm_page",
      TimeSpan.FromMinutes(2), requireNew: false);
    long warmPageWitnessAt = PdfBaselineNow();

    long warmReturnMarkAt = PdfBaselineNow();
    await MarkPdfBaselineBrowserAsync("warm_return_page0");
    if (warmTarget >= 64)
    {
      await ClickWorkbenchSmokeButtonAsync("上一组");
      await WaitForPaddlePdfAsync(state => state.WindowStart == 0, TimeSpan.FromSeconds(30));
    }
    string returnSelector = "button[aria-label='检查第 1 页']";
    await WaitForSmokeDomAsync(
      $"!!document.querySelector({JsonSerializer.Serialize(returnSelector)})",
      TimeSpan.FromSeconds(30));
    string returned = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      """(() => { const button = document.querySelector("button[aria-label='检查第 1 页']"); if (!button) return false; button.scrollIntoView({ block: 'center' }); button.click(); return true; })()""");
    if (returned != "true")
      throw new InvalidOperationException("Baseline setCurrentPage button missing for page 1");
    await RequestPdfBaselineFrameAsync("warm_return", 0);
    PdfWorkbenchState warmReturnState = await WaitForPaddlePdfAsync(
      state => state.SelectedPage == 0, TimeSpan.FromSeconds(30));
    PdfBaselineImageWait warmReturn = await WaitForPdfBaselineImageAsync(
      "warm-return-page-0", 0, "warm_return_page0", "warm_return",
      TimeSpan.FromMinutes(2), requireNew: false);
    long warmReturnWitnessAt = PdfBaselineNow();

    long zoomInMarkAt = PdfBaselineNow();
    await MarkPdfBaselineBrowserAsync("warm_zoom_in");
    await ClickPdfBaselineZoomAsync("放大页面");
    await RequestPdfBaselineFrameAsync("zoom_in", 0);
    PdfBaselineImageWait zoomIn = await WaitForPdfBaselineImageAsync(
      "warm-zoom-in", 0, "warm_zoom_in", "zoom_in", TimeSpan.FromSeconds(30), requireNew: false);
    long zoomInWitnessAt = PdfBaselineNow();
    long zoomOutMarkAt = PdfBaselineNow();
    await MarkPdfBaselineBrowserAsync("warm_zoom_out");
    await ClickPdfBaselineZoomAsync("缩小页面");
    await RequestPdfBaselineFrameAsync("zoom_out", 0);
    PdfBaselineImageWait zoomOut = await WaitForPdfBaselineImageAsync(
      "warm-zoom-out", 0, "warm_zoom_out", "zoom_out", TimeSpan.FromSeconds(30), requireNew: false);
    long warmDoneAt = PdfBaselineNow();

    await SetPdfBaselinePhaseAsync("reopen");
    long reopenStartAt = PdfBaselineNow();
    await ClickWorkbenchSmokeButtonAsync("关闭文档");
    await WaitForPaddlePdfAsync(state => state.PageCount == 0, TimeSpan.FromSeconds(30));
    await MarkPdfBaselineBrowserAsync("reopen_open_clicked");
    long reopenPickConfirmAt = 0;
    await ClickWorkbenchSmokeButtonAsync("打开 PDF");
    await CompletePaddleOpenPickerAsync(fixture, onBeforeConfirm: async () =>
    {
      await MarkPdfBaselineBrowserAsync("reopen_pick_confirm");
      reopenPickConfirmAt = PdfBaselineNow();
    });
    long reopenFileSelectedAt = PdfBaselineNow();
    await MarkPdfBaselineBrowserAsync("reopen_file_selected");
    await RequestPdfBaselineFrameAsync("reopen", 0);
    PdfBaselineImageWait reopen = await WaitForPdfBaselineImageAsync(
      "reopen-first-image", 0, "reopen_pick_confirm", "reopen",
      TimeSpan.FromMinutes(2), requireNew: true);
    long reopenFirstImageAt = PdfBaselineNow();
    PdfBaselineBrowserImage reopenWitness = reopen.NewWitness!;
    PdfWorkbenchState reopenedState = await WaitForPaddlePdfAsync(
      state => state.PageCount > 0 && state.SessionId != detected.SessionId,
      TimeSpan.FromSeconds(30));
    long reopenDetectionStart = PdfBaselineNow();
    PdfWorkbenchState reopenedDetected = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.DetectedCount == state.PageCount && state.PageCount > 0,
      TimeSpan.FromMinutes(timeoutMinutes));
    long reopenDetectionDone = PdfBaselineNow();
    ValidatePdfBaselineResourceOwnership("reopen_first_image", reopenedDetected, reopen, 0);

    object? reactivation = null;
    PdfBaselineClarityReport? secondClarity = null;
    PdfBaselineClarityReport? reactivatedClarity = null;
    if (second is not null)
    {
      await SetPdfBaselinePhaseAsync("cross_document");
      string firstDocumentId = reopenedDetected.DocumentId
        ?? throw new InvalidOperationException("Baseline first document id is missing.");
      long secondOpenAt = PdfBaselineNow();
      await MarkPdfBaselineBrowserAsync("second_doc_open_clicked");
      await ClickWorkbenchSmokeButtonAsync("添加 PDF");
      await CompletePaddleOpenPickerAsync(second);
      await RequestPdfBaselineFrameAsync("second_doc", 0);
      PdfBaselineImageWait secondWait = await WaitForPdfBaselineImageAsync(
        "second-doc-first-image", 0, "second_doc_open_clicked", "second_doc",
        TimeSpan.FromMinutes(2), requireNew: true);
      long secondFirstImageAt = PdfBaselineNow();
      secondClarity = PdfBaselineClarity(
        "second_doc_first_image", secondWait.NewWitness!, secondWait.Pulse, secondWait.Snapshot, secondWait.Snapshot.Dpr);
      PdfWorkbenchState secondState = await WaitForPaddlePdfAsync(
        state => state.Documents?.Count == 2 && state.DocumentId != firstDocumentId,
        TimeSpan.FromSeconds(30));
      PdfWorkbenchState secondSettled = await WaitForPaddlePdfAsync(
        state => !state.IsBusy && state.DetectedCount == state.PageCount && state.PageCount > 0,
        TimeSpan.FromMinutes(timeoutMinutes));
      long secondDetectionDone = PdfBaselineNow();
      ValidatePdfBaselineResourceOwnership("second_doc_first_image", secondSettled, secondWait, 0);

      long reactivateAt = PdfBaselineNow();
      await MarkPdfBaselineBrowserAsync("reactivate_first_doc");
      await WaitForSmokeDomAsync(
        "document.querySelectorAll('.pdf-documents button').length > 0", TimeSpan.FromSeconds(30));
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "document.querySelectorAll('.pdf-documents button')[0].click()");
      await RequestPdfBaselineFrameAsync("reactivated", 0);
      PdfWorkbenchState reactivatedState = await WaitForPaddlePdfAsync(
        state => state.DocumentId == firstDocumentId && state.PageInspectStatusCode == "pdf.inspect.ready",
        TimeSpan.FromSeconds(30));
      PdfBaselineImageWait reactivated = await WaitForPdfBaselineImageAsync(
        "reactivated-first-image", 0, "reactivate_first_doc", "reactivated",
        TimeSpan.FromSeconds(30), requireNew: false);
      long reactivatedFirstImageAt = PdfBaselineNow();
      ValidatePdfBaselineResourceOwnership(
        "reactivated_first_image", reactivatedState, reactivated, 0);
      reactivatedClarity = PdfBaselineClarity(
        "reactivated_first_image", reactivated.Witness, reactivated.Pulse, reactivated.Snapshot, reactivated.Snapshot.Dpr);
      reactivation = new
      {
        second_document = new
        {
          path = second,
          browser = new
          {
            open_clicked_to_decoded_ms = secondWait.NewWitness!.DecodedAt!.Value - PdfBaselineMark(secondWait.Snapshot, "second_doc_open_clicked"),
            witness = secondWait.NewWitness,
            frame = FrameFacts(secondWait.Pulse),
          },
          host = new
          {
            open_to_first_image_ms = PdfBaselineElapsedMs(secondOpenAt, secondFirstImageAt),
            detection_wait_ms = PdfBaselineElapsedMs(secondFirstImageAt, secondDetectionDone),
          },
          state_after_open = PdfBaselineStateFacts(secondState),
        },
        reactivation = new
        {
          host = new { reactivate_to_ready_ms = PdfBaselineElapsedMs(reactivateAt, reactivatedFirstImageAt) },
          browser = new
          {
            new_image_observed = reactivated.NewImageObserved,
            frame = FrameFacts(reactivated.Pulse),
            witness = reactivated.Witness,
          },
          state_after_reactivate = PdfBaselineStateFacts(reactivatedState),
        },
      };
    }

    PdfBaselineBrowserSnapshot finalSnapshot = await PdfBaselineSnapshotAsync();
    if (finalSnapshot.StateMessageMeasurementErrors > 0)
      throw new InvalidOperationException(
        $"PDF state message measurement had {finalSnapshot.StateMessageMeasurementErrors} errors; failing explicitly instead of undercounting.");
    var (events, maxInflight, overflow) = probe.Snapshot();
    if (overflow)
      throw new InvalidOperationException(
        "PDF request probe event buffer overflowed; counts would be truncated — failing explicitly.");
    var clarity = new List<PdfBaselineClarityReport>
    {
      PdfBaselineClarity("cold_first_image", coldWitness, cold.Pulse, cold.Snapshot, cold.Snapshot.Dpr),
      PdfBaselineClarity("warm_target_page", warmPage.Witness, warmPage.Pulse, warmPage.Snapshot, warmPage.Snapshot.Dpr),
      PdfBaselineClarity("warm_return_page0", warmReturn.Witness, warmReturn.Pulse, warmReturn.Snapshot, warmReturn.Snapshot.Dpr),
      PdfBaselineClarity("warm_zoom_in", zoomIn.Witness, zoomIn.Pulse, zoomIn.Snapshot, zoomIn.Snapshot.Dpr),
      PdfBaselineClarity("warm_zoom_out", zoomOut.Witness, zoomOut.Pulse, zoomOut.Snapshot, zoomOut.Snapshot.Dpr),
      PdfBaselineClarity("reopen_first_image", reopenWitness, reopen.Pulse, reopen.Snapshot, reopen.Snapshot.Dpr),
    };
    if (secondClarity is not null) clarity.Add(secondClarity);
    if (reactivatedClarity is not null) clarity.Add(reactivatedClarity);
    bool clarityComplete = clarity.All(report => report.Status != "UNKNOWN");
    // 清晰度升级与首图分离：首图之后出现更高自然像素的检查图条目。
    var upgrades = cold.Snapshot.Images
      .Where(image => image.DecodedAt is not null && image.FrameOpportunityAt is not null
        && !image.Superseded && image.SrcSetAt > coldWitness.SrcSetAt
        && image.NaturalWidth * image.NaturalHeight > coldWitness.NaturalWidth * coldWitness.NaturalHeight)
      .Select(image => new
      {
        image.Alt,
        image.NaturalWidth,
        image.NaturalHeight,
        first_image_to_decoded_ms = image.DecodedAt!.Value - coldWitness.DecodedAt!.Value,
      }).Take(8).ToList();
    paddleSmokeOutcome = "passed";
    return new
    {
      input_kind = "pdf_baseline",
      sample_status = "valid",
      sample_status_rules = new
      {
        valid = "必需见证（首图 decode+帧机会+页归属、检测收尾、reopen、请求探针无溢出）全部成立",
        clarity_not_reached = "NOT_REACHED 为旧实现不足的如实记录，不降阈值、不算同图重绘升级、不使样本无效",
        clarity_unknown = "UNKNOWN 使 clarity_evidence=incomplete，不得当作有效完整清晰度证据",
        invalid = "缺帧/错页/观测失败 → 异常路径 health=failed；null 永不变 0 或通过",
      },
      clarity_evidence = clarityComplete ? "complete" : "incomplete",
      clarity_not_reached_steps = clarity.Where(r => r.Status == "NOT_REACHED").Select(r => r.Step).ToArray(),
      clarity,
      clarity_upgrades_after_first_image = upgrades,
      clock_domains = new
      {
        browser = "performance.now() ms（动作 mark、img.decode 完成、双 RAF 帧机会均在此域内取差）",
        host = "System.Diagnostics.Stopwatch.GetTimestamp（wire 请求与阶段里程独立域）",
        rule = "两域原始时间永不互相比较",
      },
      frame_witness_semantics = "双 RAF 是渲染机会（frame opportunity），不是实际 paint 证明；每步附 visibilityState/hasFocus/rafGap，窗口被系统弹窗遮挡时不得称为屏幕可见帧",
      state_messages = new
      {
        finalSnapshot.StateMessageTotalCount,
        bytes_estimate = finalSnapshot.StateMessageTotalBytes,
        phases = finalSnapshot.StateMessagePhases,
        measurement_errors = finalSnapshot.StateMessageMeasurementErrors,
        method = "chrome.webview 只读监听，严格过滤 type=app.state 且 payload.scope=pdf 后对 payload 重编码（TextEncoder(JSON.stringify(payload))）估算，非精确 wire 字节、非轮询；监听缺失/错误显式 FAIL",
      },
      config = new
      {
        device = PaddleSmokeDevice,
        model_preset = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_PRESET") ?? "small",
        render_dpi = coldDpi,
        max_pixels_app_settings = maxPixels,
        preview_worker_hard_limit = new
        {
          pixels = 16_000_000,
          source = "pdf_backend_process.py:199 预览 worker 独立硬限；非 wire 读取，与 App 设置分属两层",
        },
        supervisor = session.InstanceId,
        process_pid = Environment.ProcessId,
        page_count = detected.PageCount,
        synthetic_only = true,
        ocr_performed = false,
        artificial_delays = "none",
      },
      cold_first_image = new
      {
        browser = new
        {
          file_pick_confirm_to_decoded_ms = pickConfirmToDecoded,
          file_pick_confirm_to_frame_ms = coldWitness.FrameOpportunityAt!.Value - PdfBaselineMark(cold.Snapshot, "file_pick_confirm"),
          decode_to_frame_ms = coldWitness.FrameOpportunityAt!.Value - coldWitness.DecodedAt!.Value,
          witness = coldWitness,
          frame = FrameFacts(cold.Pulse),
          boundary = "file_pick_confirm 于真正提交确认前设置（排除 picker 填写）；open_pdf_clicked 含 picker 时间另披露",
          first_thumbnail_note = "缩略图不计入首图；快照含其条目供单独分析",
        },
        host = new
        {
          open_click_ms = PdfBaselineElapsedMs(openClickAt, pickConfirmAt),
          picker_done_to_first_image_ms = PdfBaselineElapsedMs(fileSelectedAt, coldFirstImageAt),
          first_image_state = PdfBaselineStateFacts(coldState),
        },
      },
      detection = new
      {
        host = new
        {
          pick_confirm_to_detection_settled_ms = PdfBaselineElapsedMs(pickConfirmAt, detectionDoneAt),
          first_image_to_detection_settled_ms = PdfBaselineElapsedMs(coldFirstImageAt, detectionDoneAt),
        },
        state_at_settle = PdfBaselineStateFacts(detected),
      },
      warm = new
      {
        host = new
        {
          total_ms = PdfBaselineElapsedMs(warmStartAt, warmDoneAt),
          revisit_target_page = warmTarget,
          page_selected_to_witness_ms = PdfBaselineElapsedMs(warmPageMarkAt, warmPageWitnessAt),
          return_to_witness_ms = PdfBaselineElapsedMs(warmReturnMarkAt, warmReturnWitnessAt),
          zoom_in_to_witness_ms = PdfBaselineElapsedMs(zoomInMarkAt, zoomInWitnessAt),
          zoom_out_to_witness_ms = PdfBaselineElapsedMs(zoomOutMarkAt, warmDoneAt),
        },
        browser = new
        {
          target_page = WarmFacts(warmPage, "warm_page_selected"),
          return_page0 = WarmFacts(warmReturn, "warm_return_page0"),
          zoom_in = WarmFacts(zoomIn, "warm_zoom_in"),
          zoom_out = WarmFacts(zoomOut, "warm_zoom_out"),
          warm_note = "缓存重访可无新解码（new_image_observed=false）；帧机会来自动作后 awaitFrame 当前帧见证",
        },
        state_after_target = PdfBaselineStateFacts(warmPageState),
        state_after_return = PdfBaselineStateFacts(warmReturnState),
      },
      reopen = new
      {
        host = new
        {
          close_to_first_image_ms = PdfBaselineElapsedMs(reopenStartAt, reopenFirstImageAt),
          pick_confirm_to_first_image_ms = PdfBaselineElapsedMs(reopenPickConfirmAt, reopenFirstImageAt),
          picker_done_to_first_image_ms = PdfBaselineElapsedMs(reopenFileSelectedAt, reopenFirstImageAt),
          first_image_to_detection_settled_ms = PdfBaselineElapsedMs(reopenFirstImageAt, reopenDetectionDone),
          detection_wait_ms = PdfBaselineElapsedMs(reopenDetectionStart, reopenDetectionDone),
        },
        browser = new
        {
          pick_confirm_to_decoded_ms = reopenWitness.DecodedAt!.Value - PdfBaselineMark(reopen.Snapshot, "reopen_pick_confirm"),
          witness = reopenWitness,
          frame = FrameFacts(reopen.Pulse),
        },
        state_after_reopen = PdfBaselineStateFacts(reopenedState),
        state_at_settle = PdfBaselineStateFacts(reopenedDetected),
        note = "reopen 为新会话独立统计",
      },
      reactivation,
      requests = new
      {
        note = "DeferredInferenceClient 冒烟探针记录的 App→Runtime PDF wire 请求；不含 worker 进程内 native 调用",
        max_inflight = maxInflight,
        counts = events.GroupBy(request => request.Kind)
          .ToDictionary(group => group.Key, group => group.Count()),
        outcomes = events.GroupBy(request => request.Outcome)
          .ToDictionary(group => group.Key, group => group.Count()),
        sessions = events.Select(request => request.Session).Where(id => id.Length > 0).Distinct().Count(),
        queue_baseline_note = "现有 App→Runtime PDF wire 受 Deferred gate/写门约束；worker 内部队列深度不在本观测范围，未知不虚造",
        events = events.Select(request => new
        {
          request.Kind,
          request.Session,
          request.Page,
          request.Param,
          request.StartedAtMs,
          request.DurationMs,
          request.Outcome,
          request.Error,
        }),
      },
    };
  }

  private static object FrameFacts(PdfBaselinePulse pulse) => new
  {
    pulse.At,
    pulse.RafGapMs,
    pulse.Matched,
    pulse.VisibilityState,
    pulse.HasFocus,
    current = pulse.Current,
  };

  private static object WarmFacts(PdfBaselineImageWait wait, string mark) => new
  {
    new_image_observed = wait.NewImageObserved,
    mark_to_decoded_ms = wait.NewImageObserved
      ? wait.NewWitness!.DecodedAt!.Value - PdfBaselineMark(wait.Snapshot, mark) : (double?)null,
    frame = FrameFacts(wait.Pulse),
    witness = wait.Witness,
  };
}
