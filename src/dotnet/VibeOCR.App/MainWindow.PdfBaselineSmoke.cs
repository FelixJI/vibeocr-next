using System.Diagnostics;
using System.Text.Json;
using VibeOCR.App.Inference;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App;

/// <summary>发布身份代次（App.Tests 可直接测试的纯数据）。</summary>
internal sealed record PdfBaselinePublication(
  double At, string? SessionId, string? DocumentId, int? Page, long? Revision, string PreviewUrl);

/// <summary>
/// 首图/最终图归属的纯函数判定：要求 session+document+page+revision 存在且
/// 完全一致，发布时间不晚于见证成帧；缺字段一律拒绝，不同 session/revision/
/// 页不得匹配。无状态，不依赖 MainWindow 实例。
/// </summary>
internal static class PdfBaselineOwnership
{
  internal static bool StrictIdentity(
      PdfBaselinePublication publication, string session, string document,
      int page, long revision) =>
    session.Length > 0 && document.Length > 0 &&
    publication.SessionId == session && publication.DocumentId == document &&
    publication.Page == page && publication.Revision == revision;

  internal static (bool Ok, string? Reason) ValidateWitness(
      IReadOnlyList<PdfBaselinePublication> publications, string session, string document,
      int page, long revision, string witnessSrc, double frameOpportunityAt)
  {
    if (session.Length == 0 || document.Length == 0)
      return (false, "session/document identity missing");
    List<PdfBaselinePublication> exact = publications
      .Where(publication => StrictIdentity(publication, session, document, page, revision)
        && publication.PreviewUrl == witnessSrc)
      .ToList();
    if (exact.Count == 0)
      return (false, $"witness src not published under exact identity page={page} revision={revision}");
    if (!exact.Any(publication => publication.At <= frameOpportunityAt))
      return (false, "publication not bound before witness frame");
    return (true, null);
  }

  internal static void RequireIncompleteDetection(int pageCount, int detectedCount)
  {
    if (pageCount < 100 || detectedCount < 0 || detectedCount >= pageCount)
      throw new InvalidOperationException("Controlled PDF browsing requires >=100 pages and unfinished detection.");
  }

  internal static bool IsPublishedGeneration(
      IReadOnlyList<PdfBaselinePublication> publications, string session, string document,
      int page, long revision, string url) =>
    publications.Any(publication =>
      StrictIdentity(publication, session, document, page, revision) &&
      publication.PreviewUrl == url);

  /// <summary>最终 host/DOM/当前见证一致性（纯判定）：finalUrl 缺失、DOM src 不
  /// 一致、未解码/未布局、或未在发布历史中均拒绝；不做无条件空通过。</summary>
  internal static (bool Ok, string? Reason) FinalAgreement(
      IReadOnlyList<PdfBaselinePublication> publications, string session, string document,
      int page, long revision, string? finalUrl, string? domSrc,
      bool domComplete, bool domConnected, bool domHasLayout, bool hasFramedWitnessForUrl)
  {
    if (finalUrl is null || finalUrl.Length == 0)
      return (false, "final host PagePreview.Url missing");
    if (domSrc != finalUrl)
      return (false, $"final DOM src mismatch: dom={domSrc ?? "<null>"}");
    if (!domComplete || !domConnected || !domHasLayout)
      return (false, "final DOM frame not decoded/connected/laid out");
    if (!IsPublishedGeneration(publications, session, document, page, revision, finalUrl))
      return (false, "final url not published under exact identity");
    if (!hasFramedWitnessForUrl)
      return (false, "no framed witness entry for final url");
    return (true, null);
  }
}

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
        allKnown: { first: {} },
        publications: [], publicationKeys: {}, publicationsOverflow: false,
        loadListener: null,
        stateMessages: {
          phases: {}, totalCount: 0, totalBytesEstimate: 0,
          listenerAvailable: false, measurementErrors: 0,
        },
      };
      function normalizedAttrSrc(img) {
        const raw = img.getAttribute('src') || img.currentSrc || '';
        try { return new URL(raw, location.href).href; } catch (error) { return raw; }
      }
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
      function record(img, fullSrc) {
        const entry = {
          alt: img.getAttribute('alt') || '',
          fullSrc,
          srcSetAt: performance.now(), decodeStartedAt: null,
          decodedAt: null, frameOpportunityAt: null, decodeError: null,
          superseded: false, naturalWidth: 0, naturalHeight: 0,
          clientWidth: null, clientHeight: null, dprAtFrame: null,
          visibilityAtFrame: null, hasFocusAtFrame: null,
        };
        state.images.push(entry);
        if (state.images.length > 96) state.images.splice(0, state.images.length - 96);
        img.__t2entry = entry;
        runDecode(img, entry);
      }
      function runDecode(img, entry) {
        const presenting = () =>
          (img.currentSrc || img.getAttribute('src') || '') === entry.fullSrc;
        // 当前代次校验：旧代次的迟到回调永远不得翻活。
        const isCurrent = () =>
          !state.disposed && img.__t2entry === entry &&
          document.querySelector('.pdf-inspection-sheet img') === img;
        if (entry.decodeStartedAt === null) entry.decodeStartedAt = performance.now();
        img.decode().then(() => {
          if (!isCurrent() || !presenting()) {
            entry.superseded = true;
            return;
          }
          entry.naturalWidth = img.naturalWidth;
          entry.naturalHeight = img.naturalHeight;
          entry.decodedAt = performance.now();
          const firstFrame = performance.now();
          requestAnimationFrame(() => requestAnimationFrame(() => {
            if (!isCurrent() || !presenting() ||
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
            entry.superseded = false;
            if (state.awaiting?.requireClarity) sampleAwaiting(state.awaiting, firstFrame, entry.frameOpportunityAt);
          }));
        }, (error) => {
          if (!isCurrent() || !presenting()) {
            entry.superseded = true;
            return;
          }
          entry.decodeError = String((error && error.name) || error);
        });
      }
      function syncImg(img) {
        const src = normalizedAttrSrc(img);
        if (img.__t2src !== src) {
          img.__t2src = src;
          record(img, src);
          return;
        }
        // 同代次补救：早 decode 因 currentSrc 滞后被置 superseded，load 追上后
        // 重试一次；不覆盖原 srcSetAt、不产生重复条目；真正换过代次的旧回调
        // 不会被翻活（runDecode 内 isCurrent 校验）。
        const entry = img.__t2entry;
        if (entry && entry.fullSrc === src && entry.superseded &&
            entry.decodedAt === null && entry.frameOpportunityAt === null &&
            (img.currentSrc || img.getAttribute('src') || '') === src) {
          entry.superseded = false;
          runDecode(img, entry);
        }
      }
      function sampleAwaiting(request, first, at) {
        if (state.disposed || state.awaiting !== request) return;
        const img = document.querySelector('.pdf-inspection-sheet img');
        const facts = currentImgFacts(img);
        const visibilityState = document.visibilityState;
        const hasFocus = document.hasFocus();
        const entry = img?.__t2entry;
        const dpr = devicePixelRatio;
        const ok = !!facts && facts.alt.includes(request.alt) &&
          facts.complete && facts.naturalWidth > 0 && facts.connected && facts.hasLayout &&
          (!request.requireClarity || (
            entry && entry.fullSrc === facts.src && !entry.superseded &&
            entry.decodedAt !== null && entry.frameOpportunityAt !== null &&
            visibilityState === 'visible' && hasFocus &&
            facts.clientWidth > 0 && facts.clientHeight > 0 &&
            facts.naturalWidth + facts.naturalWidth * 0.02 >= facts.clientWidth * dpr &&
            facts.naturalHeight + facts.naturalHeight * 0.02 >= facts.clientHeight * dpr));
        const keys = Object.keys(state.pulses);
        if (keys.length > 32) delete state.pulses[keys[0]];
        state.pulses[request.tag] = {
          at, rafGapMs: at - first, matched: Boolean(ok), requestedAlt: request.alt,
          visibilityState, hasFocus, current: facts,
          witness: request.requireClarity && ok ? { ...entry } : undefined,
          dpr: request.requireClarity ? dpr : undefined,
        };
        if (ok) state.awaiting = null;
      }
      function runAwaiting() {
        const request = state.awaiting;
        if (!request || state.disposed) return;
        const first = performance.now();
        requestAnimationFrame(() => requestAnimationFrame(() => {
          sampleAwaiting(request, first, performance.now());
          if (state.awaiting === request) runAwaiting();
        }));
      }
      const baseline = {
        mark(name) {
          if (state.marks[name] === undefined) state.marks[name] = performance.now();
          return true;
        },
        setPhase(name) { state.phase = String(name); return true; },
        awaitFrame(tag, altFragment, requireClarity = false) {
          if (state.disposed) return false;
          state.awaiting = { tag: String(tag), alt: String(altFragment), requireClarity };
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
            allKnownFirst: { ...state.allKnown.first },
            publications: state.publications.slice(-96),
            publicationsOverflow: state.publicationsOverflow,
            dpr: devicePixelRatio,
            viewport: { width: innerWidth, height: innerHeight },
          });
        },
        dispose() {
          state.disposed = true;
          if (state.observer) { state.observer.disconnect(); state.observer = null; }
          if (state.loadListener && document.removeEventListener) {
            document.removeEventListener('load', state.loadListener, true);
          }
          state.loadListener = null;
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
            // 真实 codec 形状：payload = { sessionId(bridge), revision, scope, change,
            // state: statePayload }；PDF 字段在 payload.state 内，不能取 payload 顶层。
            const payload = data.payload;
            const inner = payload.state;
            if (inner && typeof inner === 'object' &&
                Number.isFinite(inner.pageCount) && inner.pageCount > 0 &&
                inner.detectedCount === inner.pageCount) {
              const key = inner.sessionId || inner.documentId || '';
              if (key && !state.allKnown.first[key]) {
                const keys = Object.keys(state.allKnown.first);
                if (keys.length >= 8) delete state.allKnown.first[keys[0]];
                state.allKnown.first[key] = {
                  at: performance.now(),
                  sessionId: inner.sessionId || null,
                  documentId: inner.documentId || null,
                  pageCount: inner.pageCount,
                  detectedCount: inner.detectedCount,
                };
              }
            }
            // 发布序列（有界）：按完整 (session,document,page,revision,url) 代次
            // 去重，只保留图像发布身份代次；重复状态不增长，容量 96 与图像历史
            // 匹配，溢出为显式证据缺口。
            if (inner && typeof inner === 'object' && inner.pagePreview &&
                typeof inner.pagePreview.url === 'string' && inner.pagePreview.url) {
              const key = `${inner.sessionId ?? ''}|${inner.documentId ?? ''}|` +
                `${inner.selectedPage ?? ''}|${inner.revision ?? ''}|${inner.pagePreview.url}`;
              if (!state.publicationKeys[key]) {
                state.publicationKeys[key] = true;
                if (state.publications.length >= 96) {
                  const dropped = state.publications.shift();
                  if (dropped) delete state.publicationKeys[
                    `${dropped.sessionId ?? ''}|${dropped.documentId ?? ''}|` +
                    `${dropped.page ?? ''}|${dropped.revision ?? ''}|${dropped.previewUrl}`];
                  state.publicationsOverflow = true;
                }
                state.publications.push({
                  at: performance.now(),
                  sessionId: inner.sessionId || null,
                  documentId: inner.documentId || null,
                  page: Number.isFinite(inner.selectedPage) ? inner.selectedPage : null,
                  revision: Number.isFinite(inner.revision) ? inner.revision : null,
                  previewUrl: inner.pagePreview.url,
                });
              }
            }
          } catch (error) {
            state.stateMessages.measurementErrors += 1;
          }
        };
        chrome.webview.addEventListener('message', state.messageHandler);
      }
      function scan() {
        document.querySelectorAll('.pdf-inspection-sheet img').forEach(syncImg);
      }
      state.observer = new MutationObserver(scan);
      state.observer.observe(document.body, {
        childList: true, subtree: true, attributes: true, attributeFilter: ['src'],
      });
      // 单个 document 捕获 load 监听：不逐 img 挂钩，dispose 统一移除；
      // 换代补记与同代次补救都由 syncImg 统一处理。
      if (document.addEventListener && document.removeEventListener) {
        state.loadListener = (event) => {
          if (state.disposed) return;
          const target = event && event.target;
          if (target && target.tagName === 'IMG') syncImg(target);
        };
        document.addEventListener('load', state.loadListener, true);
      }
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
    string VisibilityState, bool HasFocus, PdfBaselineBrowserCurrent? Current,
    PdfBaselineBrowserImage? Witness, double? Dpr);

  private sealed record PdfBaselineStateMessagePhase(int Count, long BytesEstimate);

  private sealed record PdfBaselineAllKnown(
    double At, string? SessionId, string? DocumentId, int PageCount, int DetectedCount);

  private sealed record PdfBaselineBrowserSnapshot(
    IReadOnlyDictionary<string, double> Marks, string Phase,
    IReadOnlyDictionary<string, PdfBaselinePulse> Pulses,
    IReadOnlyList<PdfBaselineBrowserImage> Images, int ImageCount,
    PdfBaselineBrowserCurrent? Current,
    IReadOnlyDictionary<string, PdfBaselineStateMessagePhase> StateMessagePhases,
    int StateMessageTotalCount, long StateMessageTotalBytes,
    bool StateMessageListenerAvailable, int StateMessageMeasurementErrors,
    IReadOnlyDictionary<string, PdfBaselineAllKnown> AllKnownFirst,
    IReadOnlyList<PdfBaselinePublication> Publications, bool PublicationsOverflow,
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
          ? ReadCurrent(current) : null,
        value.TryGetProperty("witness", out JsonElement witness) && witness.ValueKind == JsonValueKind.Object
          ? ReadImage(witness) : null,
        OptionalNumber(value, "dpr"));
    }
    var images = new List<PdfBaselineBrowserImage>();
    foreach (JsonElement image in root.GetProperty("images").EnumerateArray())
    {
      images.Add(ReadImage(image));
    }
    var phases = new Dictionary<string, PdfBaselineStateMessagePhase>();
    foreach (JsonProperty phase in root.GetProperty("stateMessages").GetProperty("phases").EnumerateObject())
    {
      phases[phase.Name] = new PdfBaselineStateMessagePhase(
        (int)phase.Value.GetProperty("count").GetDouble(),
        phase.Value.GetProperty("bytesEstimate").GetInt64());
    }
    var allKnown = new Dictionary<string, PdfBaselineAllKnown>();
    if (root.TryGetProperty("allKnownFirst", out JsonElement allKnownElement) &&
        allKnownElement.ValueKind == JsonValueKind.Object)
    {
      foreach (JsonProperty known in allKnownElement.EnumerateObject())
      {
        allKnown[known.Name] = new PdfBaselineAllKnown(
          known.Value.GetProperty("at").GetDouble(),
          known.Value.TryGetProperty("sessionId", out JsonElement session) && session.ValueKind == JsonValueKind.String
            ? session.GetString() : null,
          known.Value.TryGetProperty("documentId", out JsonElement documentElement) && documentElement.ValueKind == JsonValueKind.String
            ? documentElement.GetString() : null,
          (int)known.Value.GetProperty("pageCount").GetDouble(),
          (int)known.Value.GetProperty("detectedCount").GetDouble());
      }
    }
    var publications = new List<PdfBaselinePublication>();
    if (root.TryGetProperty("publications", out JsonElement publicationsElement) &&
        publicationsElement.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement publication in publicationsElement.EnumerateArray())
      {
        publications.Add(new PdfBaselinePublication(
          publication.GetProperty("at").GetDouble(),
          publication.TryGetProperty("sessionId", out JsonElement pubSession) && pubSession.ValueKind == JsonValueKind.String
            ? pubSession.GetString() : null,
          publication.TryGetProperty("documentId", out JsonElement pubDocument) && pubDocument.ValueKind == JsonValueKind.String
            ? pubDocument.GetString() : null,
          publication.TryGetProperty("page", out JsonElement pubPage) && pubPage.ValueKind == JsonValueKind.Number
            ? (int)pubPage.GetDouble() : null,
          publication.TryGetProperty("revision", out JsonElement pubRevision) && pubRevision.ValueKind == JsonValueKind.Number
            ? pubRevision.GetInt64() : null,
          publication.GetProperty("previewUrl").GetString() ?? ""));
      }
    }
    bool publicationsOverflow = root.TryGetProperty("publicationsOverflow", out JsonElement overflowElement) &&
      overflowElement.ValueKind == JsonValueKind.True;
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
      allKnown,
      publications,
      publicationsOverflow,
      root.GetProperty("dpr").GetDouble(),
      (int)root.GetProperty("viewport").GetProperty("width").GetDouble(),
      (int)root.GetProperty("viewport").GetProperty("height").GetDouble());

    static PdfBaselineBrowserImage ReadImage(JsonElement image) => new(
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
        : image.TryGetProperty("hasFocusAtFrame", out JsonElement focusFalse) && focusFalse.ValueKind == JsonValueKind.False ? false : null);

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
  /// <summary>按宿主 session/document 取浏览器域首次 all-known 记录；缺失显式 FAIL。</summary>
  private static PdfBaselineAllKnown PdfBaselineAllKnownFor(
    PdfBaselineBrowserSnapshot snapshot, PdfWorkbenchState state, string phase)
  {
    string session = state.SessionId ?? "";
    string documentId = state.DocumentId ?? "";
    if (session.Length > 0 && snapshot.AllKnownFirst.TryGetValue(session, out var bySession))
      return bySession;
    if (documentId.Length > 0 && snapshot.AllKnownFirst.TryGetValue(documentId, out var byDocument))
      return byDocument;
    throw new InvalidOperationException(
      $"PDF baseline web-state all-known record missing for {phase}: session={session} " +
      $"document={documentId}; known keys={JsonSerializer.Serialize(snapshot.AllKnownFirst.Keys)}");
  }

  /// <summary>宿主域 load 请求完成参考。completed 但缺 EndedAtMs/DurationMs 视为
  /// 证据缺口显式返回，不用 0 充当结束。</summary>
  private static object? PdfBaselineLoadEndReference(
    IReadOnlyList<PdfSmokeRequestProbe.RequestEvent> events, string? session,
    long hostPickConfirmTimestamp)
  {
    List<PdfSmokeRequestProbe.RequestEvent> scoped = events
      .Where(request => request.Kind == "load_detect" && request.Session == session)
      .ToList();
    if (scoped.Count == 0) return null;
    if (scoped.Any(request => request.Outcome == "completed" &&
        (request.EndedAtMs is null || request.DurationMs is null)))
    {
      return new
      {
        evidence_gap = "completed load_detect missing EndedAtMs/DurationMs; refusing to fabricate an end time",
      };
    }
    List<PdfSmokeRequestProbe.RequestEvent> completed = scoped
      .Where(request => request.Outcome == "completed" && request.EndedAtMs is not null)
      .ToList();
    if (completed.Count == 0) return null;
    double end = completed.Max(request => request.EndedAtMs!.Value);
    double pickConfirmMs = hostPickConfirmTimestamp * 1000.0 / Stopwatch.Frequency;
    return new
    {
      pick_confirm_to_load_request_end_ms = end - pickConfirmMs,
      note = "宿主 Stopwatch 域内 App→Runtime /load 请求完成参考；不是检测完成耗时，也不与浏览器域原始时间比较",
    };
  }

  private async Task<PdfBaselineImageWait> WaitForPdfBaselineImageAsync(
    string phase, int expectedPageIndex, string afterMark, string pulseTag,
    TimeSpan timeout, bool requireNew, bool requireClarity = false, bool requireLandscape = false,
    bool captureFirstClearFrame = false)
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
      snapshot.Pulses.TryGetValue(pulseTag, out var candidatePulse);
      // T3 使用浏览器冻结的首清晰帧；之后的 DOM 换图不改写该时间。
      PdfBaselineBrowserCurrent? current = captureFirstClearFrame ? candidatePulse?.Current : snapshot.Current;
      if (candidatePulse is not null && (!captureFirstClearFrame || !double.IsNaN(markValue))
          && candidatePulse.At >= (double.IsNaN(markValue) ? double.NegativeInfinity : markValue)
          && candidatePulse.Matched
          && candidatePulse.Current is { Complete: true, NaturalWidth: > 0, HasLayout: true }
          && current is not null &&
          current.Alt.Contains(altFragment, StringComparison.Ordinal) &&
          candidatePulse.Current.Src == current.Src)
        pulse = candidatePulse;
      bool liveMatches = current is not null && current.Alt.Contains(altFragment, StringComparison.Ordinal) &&
        current.Complete && current.NaturalWidth > 0;
      PdfBaselineBrowserImage? currentWitness = captureFirstClearFrame
        ? pulse?.Witness
        : matching.Where(image => image.FullSrc == current?.Src)
            .OrderByDescending(image => image.SrcSetAt).FirstOrDefault();
      if (captureFirstClearFrame)
      {
        if (currentWitness is not { DecodedAt: not null, FrameOpportunityAt: not null, Superseded: false } ||
            currentWitness.FullSrc != current?.Src || pulse?.Dpr is not > 0)
          currentWitness = null;
        firstNew = currentWitness?.SrcSetAt >= markValue ? currentWitness : null;
      }
      // pulse 必须不早于当前 witness 的帧机会：防止旧 pulse（React 更新前的帧，
      // 尺寸/布局可能不同）配新 witness；不满足则走重发帧请求路径。firstNew
      // 仍取 mark 后最早事件，不因当前 src 升级错失首图。
      double witnessFrameAt = currentWitness?.FrameOpportunityAt ?? double.NegativeInfinity;
      if ((!requireNew || firstNew is not null) && pulse is not null && liveMatches
          && currentWitness is not null && pulse.At >= witnessFrameAt
          && (!requireLandscape || pulse.Current!.ClientWidth > pulse.Current.ClientHeight)
          && (!requireClarity || PdfBaselineClarity(
            phase, currentWitness, pulse, snapshot, snapshot.Dpr).Status == "REACHED"))
      {
        return new PdfBaselineImageWait(firstNew, currentWitness, snapshot, pulse, firstNew is not null);
      }
      if (!captureFirstClearFrame && (DateTime.UtcNow - lastFrameRequest).TotalMilliseconds >= 500)
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

  /// <summary>历史首图按自身发布身份核验（严格 session+document+page+revision，
  /// 发布时间不晚于见证成帧）；溢出即证据缺口显式失败；不用终态 HQ URL 否定
  /// 历史低清首图。最终图一致性另由 FinalAgreement 有界等待核验。</summary>
  private static void ValidatePdfBaselineResourceOwnership(
    string step, PdfWorkbenchState state, PdfBaselineImageWait wait,
    PdfBaselineBrowserSnapshot lateSnapshot, int expectedPageIndex)
  {
    if (wait.Snapshot.PublicationsOverflow || lateSnapshot.PublicationsOverflow)
      throw new InvalidOperationException(
        $"PDF baseline publication history overflowed at {step}; ownership evidence gap — failing explicitly.");
    string session = state.SessionId ?? "";
    string document = state.DocumentId ?? "";
    PdfBaselineBrowserImage witness = wait.NewWitness ?? wait.Witness;
    double frameAt = witness.FrameOpportunityAt ?? double.NegativeInfinity;
    (bool ok, string? reason) = PdfBaselineOwnership.ValidateWitness(
      wait.Snapshot.Publications, session, document, expectedPageIndex,
      state.Revision, witness.FullSrc, frameAt);
    if (!ok)
    {
      throw new InvalidOperationException(
        $"PDF baseline resource ownership mismatch at {step}: {reason}; " +
        $"identity=session:{session}/document:{document}/page:{expectedPageIndex}/revision:{state.Revision}; " +
        $"witness={witness.FullSrc}; published=" +
        JsonSerializer.Serialize(wait.Snapshot.Publications.Concat(lateSnapshot.Publications)
          .Select(publication => new
          {
            publication.SessionId, publication.DocumentId,
            publication.Page, publication.Revision, publication.PreviewUrl,
          })));
    }
  }

  /// <summary>最终图一致性：有界等待 host PagePreview.Url 与当前 DOM src/已解码
  /// /已布局/发布身份/已成帧见证全部一致；finalUrl 缺失不是空通过。不用此晚
  /// 等待重算首图时间。</summary>
  private async Task ValidatePdfBaselineFinalImageAsync(
    string step, PdfWorkbenchState state, int expectedPageIndex, TimeSpan timeout)
  {
    DateTime deadline = DateTime.UtcNow + timeout;
    PdfWorkbenchState current = state;
    while (true)
    {
      PdfBaselineBrowserSnapshot snapshot = await PdfBaselineSnapshotAsync();
      string? finalUrl = current.PagePreview?.Url;
      bool hasFramedWitness = finalUrl is not null && snapshot.Images.Any(image =>
        !image.Superseded && image.FullSrc == finalUrl &&
        image.DecodedAt is not null && image.FrameOpportunityAt is not null);
      (bool ok, string? reason) = PdfBaselineOwnership.FinalAgreement(
        snapshot.Publications, state.SessionId ?? "", state.DocumentId ?? "",
        expectedPageIndex, state.Revision, finalUrl,
        snapshot.Current?.Src,
        snapshot.Current?.Complete ?? false,
        snapshot.Current?.Connected ?? false,
        snapshot.Current?.HasLayout == true && current.SelectedPage == expectedPageIndex &&
          snapshot.Current.Alt.Contains($"第 {expectedPageIndex + 1} 页", StringComparison.Ordinal),
        hasFramedWitness);
      if (ok) return;
      if (DateTime.UtcNow >= deadline)
      {
        throw new TimeoutException(
          $"PDF baseline final image agreement missing for {step}: {reason}; " +
          $"finalUrl={finalUrl ?? "<null>"}; dom={snapshot.Current?.Src ?? "<null>"}; " +
          $"identity=session:{current.SessionId}/document:{current.DocumentId}/" +
          $"page:{expectedPageIndex}/revision:{current.Revision}");
      }
      current = await WaitForPaddlePdfAsync(_ => true, TimeSpan.FromSeconds(30));
      await Task.Delay(100);
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
    double dpr = pulse.Dpr ?? witness.DprAtFrame ?? dprFallback;
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
    string delaySetting = PaddleSmokeEnv("VIBEOCR_PDF_DEMAND_TEST_DELAY_MS") ?? "0";
    if (!int.TryParse(delaySetting, out int detectionDelayMs) || detectionDelayMs is < 0 or > 500)
      throw new InvalidOperationException("Invalid controlled PDF detection delay.");
    object? delayedDetection = detectionDelayMs == 0 ? null
      : await VerifyPdfBrowsingBeforeDetectionAsync(coldState, cold, detectionDelayMs);
    PdfWorkbenchState detected = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.DetectedCount == state.PageCount && state.PageCount > 0,
      TimeSpan.FromMinutes(timeoutMinutes));
    long detectionDoneAt = PdfBaselineNow();
    PdfBaselineBrowserSnapshot coldFinal = await PdfBaselineSnapshotAsync();
    ValidatePdfBaselineResourceOwnership("cold_first_image", detected, cold, coldFinal, 0);
    await ValidatePdfBaselineFinalImageAsync("cold_first_image", detected, 0, TimeSpan.FromSeconds(30));
    coldFinal = await PdfBaselineSnapshotAsync();
    PdfBaselineAllKnown coldAllKnown = PdfBaselineAllKnownFor(coldFinal, detected, "cold");
    if (detectionDelayMs > 0 && coldAllKnown.At - PdfBaselineMark(coldFinal, "file_pick_confirm") <
        (detected.PageCount - 1) * (double)detectionDelayMs)
      throw new InvalidOperationException("Controlled detection delay was not observed across the document.");

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
    // 只在非性能样本等待高清，保持冻结基线的默认操作序列不变。
    PdfBaselineImageWait? zoomClear = null;
    if (detectionDelayMs > 0 || PaddleSmokeEnv("VIBEOCR_PDF_DEMAND_BROWSE_STRESS") == "1")
    {
      zoomClear = await WaitForPdfBaselineImageAsync(
        "controlled-zoom-clear", 0, "warm_zoom_in", "zoom_in", TimeSpan.FromSeconds(30),
        requireNew: false, requireClarity: true);
      if (zoomClear.Pulse.Current!.ClientWidth < zoomIn.Pulse.Current!.ClientWidth ||
          zoomClear.Pulse.Current.ClientHeight < zoomIn.Pulse.Current.ClientHeight)
        throw new InvalidOperationException("Controlled zoom clarity lost the enlarged viewport.");
      ValidatePdfBaselineResourceOwnership("controlled-zoom-clear", warmReturnState,
        zoomClear with { NewWitness = null }, zoomClear.Snapshot, 0);
      await ValidatePdfBaselineFinalImageAsync(
        "controlled-zoom-clear", warmReturnState, 0, TimeSpan.FromSeconds(30));
    }
    long zoomOutMarkAt = PdfBaselineNow();
    await MarkPdfBaselineBrowserAsync("warm_zoom_out");
    await ClickPdfBaselineZoomAsync("缩小页面");
    await RequestPdfBaselineFrameAsync("zoom_out", 0);
    PdfBaselineImageWait zoomOut = await WaitForPdfBaselineImageAsync(
      "warm-zoom-out", 0, "warm_zoom_out", "zoom_out", TimeSpan.FromSeconds(30), requireNew: false);
    long warmDoneAt = PdfBaselineNow();
    bool browseStress = PaddleSmokeEnv("VIBEOCR_PDF_DEMAND_BROWSE_STRESS") == "1";
    object? browsing = browseStress ? await RunPdfBrowseStressAsync(detected.DocumentId) : null;

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
    PdfBaselineBrowserSnapshot reopenFinal = await PdfBaselineSnapshotAsync();
    ValidatePdfBaselineResourceOwnership("reopen_first_image", reopenedDetected, reopen, reopenFinal, 0);
    await ValidatePdfBaselineFinalImageAsync("reopen_first_image", reopenedDetected, 0, TimeSpan.FromSeconds(30));
    reopenFinal = await PdfBaselineSnapshotAsync();
    PdfBaselineAllKnown reopenAllKnown = PdfBaselineAllKnownFor(reopenFinal, reopenedDetected, "reopen");

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
      await CompletePaddleOpenPickerAsync(second, onBeforeConfirm: async () =>
      {
        await MarkPdfBaselineBrowserAsync("second_doc_pick_confirm");
      });
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
      PdfBaselineBrowserSnapshot secondFinal = await PdfBaselineSnapshotAsync();
      ValidatePdfBaselineResourceOwnership("second_doc_first_image", secondSettled, secondWait, secondFinal, 0);
      await ValidatePdfBaselineFinalImageAsync("second_doc_first_image", secondSettled, 0, TimeSpan.FromSeconds(30));
      secondFinal = await PdfBaselineSnapshotAsync();
      PdfBaselineAllKnown secondAllKnown = PdfBaselineAllKnownFor(secondFinal, secondSettled, "second_doc");

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
        "reactivated_first_image", reactivatedState, reactivated, reactivated.Snapshot, 0);
      await ValidatePdfBaselineFinalImageAsync(
        "reactivated_first_image", reactivatedState, 0, TimeSpan.FromSeconds(30));
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
            pick_confirm_to_decoded_ms = secondWait.NewWitness.DecodedAt.Value - PdfBaselineMark(secondWait.Snapshot, "second_doc_pick_confirm"),
            boundary = "open_clicked 包含文件选择对话框；pick_confirm 排除对话框填写，与 cold/reopen 同口径",
            witness = secondWait.NewWitness,
            frame = FrameFacts(secondWait.Pulse),
          },
          host = new
          {
            open_to_first_image_ms = PdfBaselineElapsedMs(secondOpenAt, secondFirstImageAt),
            observed_after_first_image_ms = PdfBaselineElapsedMs(secondFirstImageAt, secondDetectionDone),
            web_state_first_all_known_pick_confirm_to_ms = secondAllKnown.At - PdfBaselineMark(secondFinal, "second_doc_pick_confirm"),
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
    if (finalSnapshot.PublicationsOverflow)
      throw new InvalidOperationException("PDF publication history overflowed; ownership evidence incomplete.");
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
    // 清晰度升级与首图分离：取 cold 阶段结束快照（早于 warm_start，天然限定
    // 首图后、warm 前时间窗），严格 identity（session+document+page0+revision）
    // 发布过且更高自然像素；不把其它页/重开 session 的高清图冒充 cold 升级；
    // 差值仍为 decode 域，首图口径不变。
    string coldSession = detected.SessionId ?? "";
    string coldDocument = detected.DocumentId ?? "";
    long coldRevision = detected.Revision;
    var upgrades = coldFinal.Images
      .Where(image => image.DecodedAt is not null && image.FrameOpportunityAt is not null
        && !image.Superseded && image.SrcSetAt > coldWitness.SrcSetAt
        && PdfBaselineOwnership.IsPublishedGeneration(
            coldFinal.Publications, coldSession, coldDocument, 0, coldRevision, image.FullSrc)
        && image.NaturalWidth * image.NaturalHeight > coldWitness.NaturalWidth * coldWitness.NaturalHeight)
      .Select(image => new
      {
        image.Alt,
        image.NaturalWidth,
        image.NaturalHeight,
        first_image_to_decoded_ms = image.DecodedAt!.Value - coldWitness.DecodedAt!.Value,
      }).Take(8).ToList();

    RecordPaddleSmokeStage("pdf baseline: measurements complete");
    return new
    {
      input_kind = "pdf_baseline",
      sample_status = "valid",
      performance_eligible = detectionDelayMs == 0 && !browseStress,
      continuous_browsing = browsing,
      controlled_detection = delayedDetection,
      controlled_zoom_clarity = zoomClear is null ? null : new
      {
        first_frame = WarmFacts(zoomIn, "warm_zoom_in"),
        clear_frame = WarmFacts(zoomClear, "warm_zoom_in"),
        clarity = PdfBaselineClarity("controlled-zoom-clear", zoomClear.Witness,
          zoomClear.Pulse, zoomClear.Snapshot, zoomClear.Snapshot.Dpr),
        note = "行为样本独立等待高清；不纳入默认序列的性能聚合比较",
      },
      resource_publications = finalSnapshot.Publications,
      demand_scheduler = commandHandler.Pdf.DemandDiagnostics,
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
        artificial_detection_delay_ms = detectionDelayMs,
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
          first_thumbnail_note = "缩略图不计入首图；本观测器只追踪当前页检查图，不采集缩略图帧事件",
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
        web_state_first_all_known = new
        {
          pick_confirm_to_all_known_ms = coldAllKnown.At - PdfBaselineMark(coldFinal, "file_pick_confirm"),
          semantics = "浏览器 app.state/pdf 事件中该 session 首次 pageCount>0 且 detectedCount==pageCount 的 performance.now；为 Web 状态首次全部已知，非 native 检测完成瞬间；与 pick_confirm 同域差；后续轮询/新 session 不覆盖",
          record = coldAllKnown,
        },
        host_wait = new
        {
          observed_after_first_image_ms = PdfBaselineElapsedMs(coldFirstImageAt, detectionDoneAt),
          note = "宿主后验轮询等待：旧实现检测多在首图前已完成，此值不是检测完成耗时",
        },
        host_load_request_end = PdfBaselineLoadEndReference(events, detected.SessionId, pickConfirmAt),
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
        return_page0_requests = events.Where(request => request.Session == detected.SessionId
            && request.Page == 0
            && request.StartedAtMs >= warmReturnMarkAt * 1000.0 / Stopwatch.Frequency
            && request.StartedAtMs < zoomInMarkAt * 1000.0 / Stopwatch.Frequency)
          .GroupBy(request => request.Kind).ToDictionary(group => group.Key, group => group.Count()),
        return_requests_scope = "从选择返回第0页到缩放前；含尚未完成请求，同档位缓存命中应无 render_preview/inspect_page",
      },
      reopen = new
      {
        host = new
        {
          close_to_first_image_ms = PdfBaselineElapsedMs(reopenStartAt, reopenFirstImageAt),
          pick_confirm_to_first_image_ms = PdfBaselineElapsedMs(reopenPickConfirmAt, reopenFirstImageAt),
          picker_done_to_first_image_ms = PdfBaselineElapsedMs(reopenFileSelectedAt, reopenFirstImageAt),
          first_image_to_all_known_state_ms = PdfBaselineElapsedMs(reopenFirstImageAt, reopenDetectionDone),
          observed_after_first_image_ms = PdfBaselineElapsedMs(reopenDetectionStart, reopenDetectionDone),
        },
        browser = new
        {
          pick_confirm_to_decoded_ms = reopenWitness.DecodedAt!.Value - PdfBaselineMark(reopen.Snapshot, "reopen_pick_confirm"),
          pick_confirm_to_all_known_ms = reopenAllKnown.At - PdfBaselineMark(reopenFinal, "reopen_pick_confirm"),
          all_known_semantics = "Web 状态首次全部已知（非 native 检测完成瞬间）",
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
        max_inflight_scope = "全部 HTTP 请求（含取消控制）；2普通+1自动检测门禁以 demand_scheduler 的实际租约峰值为准",
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
          request.EndedAtMs,
          request.DurationMs,
          request.Outcome,
          request.Error,
        }),
      },
    };
  }

  private async Task<object> RunPdfBrowseStressAsync(string? documentId)
  {
    await SetPdfBaselinePhaseAsync("continuous_browsing");
    var visits = new List<object>();
    for (int cycle = 0; cycle < 3; cycle++)
    {
      foreach (int page in new[] { 7, 15, 23, 31, 39, 47, 55, 0 })
      {
        string mark = $"browse_{cycle}_{page}";
        await MarkPdfBaselineBrowserAsync(mark);
        string selector = $"button[aria-label='检查第 {page + 1} 页']";
        string clicked = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
          $$"""(() => { const b = document.querySelector({{JsonSerializer.Serialize(selector)}}); if (!b) return false; b.scrollIntoView({block:'center'}); b.click(); return true; })()""");
        if (clicked != "true") throw new InvalidOperationException("Continuous PDF page button missing.");
        await RequestPdfBaselineFrameAsync(mark, page);
        PdfBaselineImageWait frame = await WaitForPdfBaselineImageAsync(
          mark, page, mark, mark, TimeSpan.FromSeconds(30), requireNew: false);
        PdfWorkbenchState state = await WaitForPaddlePdfAsync(
          value => value.DocumentId == documentId && value.SelectedPage == page,
          TimeSpan.FromSeconds(10));
        if (state.PageCount < 100) throw new InvalidOperationException("Continuous PDF fixture must have >=100 pages.");
        ValidatePdfBaselineResourceOwnership(mark, state, frame, frame.Snapshot, page);
        visits.Add(new { cycle, page, frame = FrameFacts(frame.Pulse) });
      }
    }
    return new { visits, memory_source = "driver process-tree samples; entire run peak includes this phase", performance_eligible = false };
  }

  private async Task<object> VerifyPdfBrowsingBeforeDetectionAsync(
    PdfWorkbenchState firstState, PdfBaselineImageWait first, int delayMs)
  {
    PdfBaselineOwnership.RequireIncompleteDetection(firstState.PageCount, firstState.DetectedCount);
    ValidatePdfBaselineResourceOwnership("delayed_first", firstState, first, first.Snapshot, 0);
    string screenshot = ValidatePaddleSmokeOwnedPath(
      Path.ChangeExtension(RequiredPaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_HEALTH"), ".before-detection.png"),
      "delayed PDF screenshot");
    using (new FileStream(screenshot, FileMode.CreateNew)) { }
    Windows.Storage.StorageFile preview = await Windows.Storage.StorageFile.GetFileFromPathAsync(screenshot);
    using (Windows.Storage.Streams.IRandomAccessStream stream =
      await preview.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
    {
      await WorkbenchWebView.CoreWebView2.CapturePreviewAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
      await stream.FlushAsync();
    }
    var flips = new List<object>();
    foreach (int page in new[] { 55, 1, 0 })
    {
      string mark = $"delayed_flip_{page}";
      await MarkPdfBaselineBrowserAsync(mark);
      string selector = $"button[aria-label='检查第 {page + 1} 页']";
      string clicked = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        $$"""(() => { const b = document.querySelector({{JsonSerializer.Serialize(selector)}}); if (!b) return false; b.scrollIntoView({block:'center'}); b.click(); return true; })()""");
      if (clicked != "true") throw new InvalidOperationException("Delayed PDF page button missing.");
      await RequestPdfBaselineFrameAsync(mark, page);
      PdfBaselineImageWait frame = await WaitForPdfBaselineImageAsync(
        mark, page, mark, mark, TimeSpan.FromSeconds(30), requireNew: false,
        requireClarity: page == 55, requireLandscape: page == 55);
      PdfWorkbenchState state = await WaitForPaddlePdfAsync(
        value => value.SelectedPage == page && value.DocumentId == firstState.DocumentId,
        TimeSpan.FromSeconds(10));
      PdfBaselineOwnership.RequireIncompleteDetection(state.PageCount, state.DetectedCount);
      ValidatePdfBaselineResourceOwnership(mark, state, frame, frame.Snapshot, page);
      PdfWorkbenchPage? geometry = state.Pages?.FirstOrDefault(value => value.Index == page);
      if (page == 55 && (geometry is null || geometry.Width <= geometry.Height ||
          geometry.Height <= 0 || geometry.Detected || state.DetectedCount >= 55))
        throw new InvalidOperationException("Undetected landscape page geometry did not arrive before background detection.");
      flips.Add(new { page, state.DetectedCount, geometry, frame = FrameFacts(frame.Pulse), publications = frame.Snapshot.Publications });
    }
    return new
    {
      delay_ms = delayMs,
      first_state = PdfBaselineStateFacts(firstState),
      first_frame = FrameFacts(first.Pulse),
      screenshot,
      flips,
      note = "仅受控行为样本，不纳入性能对比；截图为 WebView 内容，不证明未被系统弹窗遮挡。",
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

  /// <summary>
  /// baseline 专用收尾：在公共 CompletePaddleModesSmokeAsync 完成 CapturePreview/
  /// Flush 之后、写入 passed health 之前调用（仅 INPUT_KIND=pdf_baseline）。通过
  /// 公开“关闭文档”逐个关闭本次合成文档，不绕过 flush/close 保护；等待宿主文档
  /// 数减少/新活跃文档与 DOM 可交互，最终 PageCount==0/文档空且“打开 PDF”
  /// 可达。超时沿既有失败路径写 failed health 并保留阶段名。
  /// </summary>
  private async Task CleanupPaddlePdfBaselineDocumentsAsync()
  {
    RecordPaddleSmokeStage("pdf baseline: cleanup documents");
    PdfWorkbenchState before = await WaitForPaddlePdfAsync(
      _ => true, TimeSpan.FromSeconds(30));
    int remaining = before.Documents?.Count ?? (before.PageCount > 0 ? 1 : 0);
    for (int index = 0; index < remaining; index++)
    {
      await ClickWorkbenchSmokeButtonAsync("关闭文档");
      int expectedLeft = remaining - index - 1;
      PdfWorkbenchState afterClose = await WaitForPaddlePdfAsync(state => expectedLeft == 0
          ? state.PageCount == 0 && (state.Documents?.Count ?? 0) == 0
          : state.Documents?.Count == expectedLeft && state.PageCount > 0 &&
            state.DocumentId is not null && state.PagePreview is not null &&
            state.PageInspectStatusCode == "pdf.inspect.ready",
        TimeSpan.FromSeconds(60));
      if (expectedLeft == 0) continue;
      // 新活跃文档的 DOM/img 资源已注册且可交互才继续下一close：避免宿主计数
      // 已减而投影未切换的 close-all 同类竞态。此时 observer 已随 RunCore
      // 返回 dispose，改用直接 DOM 核对：当前 img src == 宿主 PagePreview.Url、
      // 已解码且有布局，再等一帧。
      string expectedUrl = JsonSerializer.Serialize(afterClose.PagePreview?.Url ?? "");
      await WaitForSmokeDomAsync(
        "(() => { const img = document.querySelector('.pdf-inspection-sheet img'); " +
        $"return !!img && img.src === {expectedUrl} && img.complete && " +
        "img.naturalWidth > 0 && img.getClientRects().length > 0; })()",
        TimeSpan.FromSeconds(30));
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        "window.__t2CleanupReady = false; requestAnimationFrame(() => " +
        "requestAnimationFrame(() => { window.__t2CleanupReady = true; }));");
      await WaitForSmokeDomAsync("window.__t2CleanupReady === true", TimeSpan.FromSeconds(30));
    }
    await WaitForPaddlePdfAsync(
      state => state.PageCount == 0 && (state.Documents?.Count ?? 0) == 0,
      TimeSpan.FromSeconds(30));
    await WaitForSmokeDomAsync(
      "[...document.querySelectorAll('button')].some(b => b.textContent.trim() === '打开 PDF')",
      TimeSpan.FromSeconds(30));
    RecordPaddleSmokeStage("pdf baseline: complete");
    paddleSmokeOutcome = "passed";
  }

  private static object WarmFacts(PdfBaselineImageWait wait, string mark) => new
  {
    new_image_observed = wait.NewImageObserved,
    mark_to_decoded_ms = wait.NewImageObserved
      ? wait.NewWitness!.DecodedAt!.Value - PdfBaselineMark(wait.Snapshot, mark) : (double?)null,
    mark_to_frame_opportunity_ms = wait.Pulse.At - PdfBaselineMark(wait.Snapshot, mark),
    frame = FrameFacts(wait.Pulse),
    witness = wait.Witness,
  };
}
