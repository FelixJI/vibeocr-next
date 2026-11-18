// T2 基线观测脚本契约测试：从 MainWindow.PdfBaselineSmoke.cs 的单一事实源
// 提取 PdfBaselineObserverScript，在最小 fake DOM 中验证行为合同。
// 运行：node --test tests/node/pdf_baseline_observer.test.mjs（锁定 Node）。
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import path from "node:path";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const source = readFileSync(
  path.join(repoRoot, "src/dotnet/VibeOCR.App/MainWindow.PdfBaselineSmoke.cs"),
  "utf8",
);
const marker = 'PdfBaselineObserverScript = """';
const start = source.indexOf(marker);
assert.notEqual(start, -1, "observer script marker missing");
const scriptStart = start + marker.length;
const scriptEnd = source.indexOf('    """', scriptStart);
assert.notEqual(scriptEnd, -1, "observer script terminator missing");
const observerScript = source.slice(scriptStart, scriptEnd);

class FakeMutationObserver {
  constructor(callback) {
    this.callback = callback;
    this.disconnected = false;
    this.instances.push(this);
  }
  observe() {}
  disconnect() {
    this.disconnected = true;
  }
}
FakeMutationObserver.prototype.instances = [];

function makeImage({ alt, src, naturalWidth = 800, naturalHeight = 600 }) {
  return {
    _alt: alt,
    currentSrc: src,
    attrSrc: null,
    tagName: "IMG",
    _listeners: {},
    naturalWidth,
    naturalHeight,
    clientWidth: 400,
    clientHeight: 300,
    complete: false,
    isConnected: true,
    getAttribute(name) {
      if (name === "alt") return this._alt;
      if (name === "src") return this.attrSrc ?? this.currentSrc;
      return null;
    },
    addEventListener(type, handler) {
      (this._listeners[type] ??= []).push(handler);
    },
    dispatch(type) {
      for (const handler of [...(this._listeners[type] ?? [])]) handler({ type });
    },
    getClientRects() {
      return this.isConnected ? [{}] : [];
    },
    decode() {
      this.complete = true;
      return Promise.resolve();
    },
  };
}

function createHarness() {
  FakeMutationObserver.prototype.instances = [];
  const state = {
    images: [],
    inspectionImage: null,
    rafQueue: [],
    now: 0,
    listeners: { message: [] },
    visibilityState: "visible",
    hasFocus: true,
  };
  const document = {
    querySelector(selector) {
      return selector === ".pdf-inspection-sheet img" ? state.inspectionImage : null;
    },
    querySelectorAll(selector) {
      return selector === ".pdf-inspection-sheet img"
        ? state.images.filter((image) => image.isConnected)
        : [];
    },
    body: {},
  };
  state.documentListeners = [];
  document.addEventListener = (type, handler) => {
    state.documentListeners.push({ type, handler });
  };
  document.removeEventListener = (type, handler) => {
    state.documentListeners = state.documentListeners.filter(
      (entry) => !(entry.type === type && entry.handler === handler),
    );
  };
  Object.defineProperty(document, "visibilityState", { get: () => state.visibilityState });
  document.hasFocus = () => state.hasFocus;
  // 模拟捕获阶段：document 捕获监听先于目标自身监听触发。
  const dispatchLoad = (img) => {
    for (const { type, handler } of [...state.documentListeners]) {
      if (type === "load") handler({ type, target: img });
    }
    for (const handler of [...(img._listeners.load ?? [])]) handler({ type: "load", target: img });
  };
  const requestAnimationFrame = (callback) => state.rafQueue.push(callback);
  const chrome = {
    webview: {
      addEventListener: (name, handler) => state.listeners[name].push(handler),
      removeEventListener: (name, handler) => {
        state.listeners[name] = state.listeners[name].filter((item) => item !== handler);
      },
    },
  };
  const window = {
    document,
    devicePixelRatio: 2,
    MutationObserver: FakeMutationObserver,
    requestAnimationFrame,
    chrome,
  };
  Object.assign(globalThis, {
    window,
    document,
    performance: { now: () => state.now },
    requestAnimationFrame,
    devicePixelRatio: window.devicePixelRatio,
    MutationObserver: FakeMutationObserver,
    chrome,
    TextEncoder,
    URL,
    location: { href: "https://app.vibeocr/test" },
    innerWidth: 1200,
    innerHeight: 800,
  });
  const baseline = new Function(`${observerScript}\nreturn window.__t2PdfBaseline;`)();
  const pumpFrames = (count) => {
    for (let frame = 0; frame < count; frame++) {
      const queue = state.rafQueue.splice(0);
      state.now += 16;
      for (const callback of queue) callback(state.now);
    }
  };
  const triggerScan = () => {
    for (const observer of FakeMutationObserver.prototype.instances) {
      if (!observer.disconnected) observer.callback();
    }
  };
  const flush = () => new Promise((resolve) => setImmediate(resolve));
  const snapshot = () => JSON.parse(baseline.snapshot());
  return { state, baseline, pumpFrames, triggerScan, flush, snapshot, dispatchLoad };
}

test("install is idempotent and returns installed", () => {
  const { baseline } = createHarness();
  assert.ok(baseline);
  const again = new Function(
    `${observerScript}\nreturn window.__t2PdfBaseline ? 'already' : 'reinstalled';`,
  )();
  assert.equal(again, "already");
});

test("decode completion records decoded entry with double-RAF frame opportunity", async () => {
  const { state, triggerScan, flush, pumpFrames, snapshot } = createHarness();
  const image = makeImage({ alt: "第 1 页", src: "blob:page-0" });
  state.images.push(image);
  state.inspectionImage = image;
  triggerScan();
  await flush();
  pumpFrames(2);
  const entry = snapshot().images.at(-1);
  assert.ok(entry, "entry recorded");
  assert.equal(entry.fullSrc, "blob:page-0");
  assert.notEqual(entry.decodedAt, null, "decodedAt recorded via img.decode()");
  assert.notEqual(entry.frameOpportunityAt, null, "double-RAF frame opportunity recorded");
  assert.equal(entry.superseded, false);
});

test("src change supersedes the old entry instead of mutating it", async () => {
  const { state, triggerScan, flush, pumpFrames, snapshot } = createHarness();
  const image = makeImage({ alt: "第 1 页", src: "blob:page-0" });
  state.images.push(image);
  state.inspectionImage = image;
  let releaseDecodes = [];
  image.decode = () =>
    new Promise((resolve) => {
      releaseDecodes.push(resolve);
    });
  triggerScan();
  assert.equal(snapshot().images.length, 1);
  image.currentSrc = "blob:page-1";
  image._alt = "第 2 页";
  triggerScan();
  assert.equal(snapshot().images.length, 2, "new entry for new src");
  for (const release of releaseDecodes) release();
  await flush();
  pumpFrames(2);
  const [oldEntry, newEntry] = snapshot().images;
  assert.equal(oldEntry.fullSrc, "blob:page-0");
  assert.equal(oldEntry.superseded, true, "late decode for replaced src is superseded");
  assert.equal(oldEntry.decodedAt, null, "superseded entry never claims decode");
  assert.equal(newEntry.fullSrc, "blob:page-1");
});

test("awaitFrame retries until current page matches and records visibility facts", () => {
  const { state, baseline, pumpFrames, snapshot } = createHarness();
  const image = makeImage({ alt: "第 1 页", src: "blob:page-0" });
  state.inspectionImage = image;
  image.complete = true;
  assert.equal(baseline.awaitFrame("warm", "第 2 页"), true);
  pumpFrames(2);
  assert.equal(snapshot().pulses.warm.matched, false, "wrong page does not match");
  image._alt = "第 2 页";
  image.currentSrc = "blob:page-1";
  pumpFrames(2);
  const pulse = snapshot().pulses.warm;
  assert.equal(pulse.matched, true, "matched after page switch");
  assert.equal(pulse.visibilityState, "visible");
  assert.equal(pulse.hasFocus, true);
  assert.ok(pulse.current.alt.includes("第 2 页"));
});

test("clear-frame mode keeps waiting after a low-resolution match and freezes the first clear frame", async () => {
  const { state, baseline, triggerScan, flush, pumpFrames, snapshot } = createHarness();
  baseline.mark("turn");
  assert.equal(baseline.awaitFrame("turn", "第 2 页", true), true);

  const image = makeImage({ alt: "第 2 页", src: "blob:low", naturalHeight: 300 });
  state.images.push(image);
  state.inspectionImage = image;
  triggerScan();
  await flush();
  pumpFrames(4);
  assert.equal(snapshot().pulses.turn.matched, false, "width alone is insufficient at DPR 2");

  image.currentSrc = "blob:clear";
  image.naturalHeight = 600;
  let finishDecode;
  image.decode = () => new Promise((resolve) => { finishDecode = resolve; });
  triggerScan();
  pumpFrames(2);
  assert.equal(snapshot().pulses.turn.matched, false, "clear source is still decoding");
  finishDecode();
  await flush();
  pumpFrames(2);
  const first = snapshot().pulses.turn;
  assert.equal(first.matched, true);
  assert.equal(first.current.src, "blob:clear");
  assert.equal(first.at, first.witness.frameOpportunityAt);
  assert.ok(first.at - snapshot().marks.turn < 500);

  image.currentSrc = "blob:newer";
  triggerScan();
  await flush();
  pumpFrames(4);
  assert.equal(snapshot().current.src, "blob:newer");
  assert.deepEqual(snapshot().pulses.turn, first, "subsequent frames cannot overwrite the witness");
});

test("webview message listener filters real app.state/pdf envelope per phase", () => {
  const { state, baseline, snapshot } = createHarness();
  const dispatch = (payload) => {
    for (const handler of state.listeners.message) handler({ data: payload });
  };
  const pdfState = (runtimeSession, pages) => ({
    type: "app.state",
    payload: {
      sessionId: "bridge-7",
      revision: 3,
      scope: "pdf",
      change: "pages",
      state: { sessionId: runtimeSession, documentId: `doc-${runtimeSession}`, pageCount: pages, detectedCount: pages },
    },
  });
  baseline.setPhase("cold_open");
  dispatch(pdfState("s1", 1));
  dispatch(pdfState("s1", 2));
  dispatch({
    type: "app.state",
    payload: { sessionId: "bridge-7", revision: 4, scope: "batch", change: "items", state: { items: [] } },
  });
  dispatch({ type: "command.receipt", payload: { scope: "pdf" } });
  baseline.setPhase("warm");
  dispatch(pdfState("s1", 3));
  const facts = snapshot().stateMessages;
  assert.equal(facts.listenerAvailable, true);
  assert.equal(facts.measurementErrors, 0);
  assert.equal(facts.totalCount, 3, "only app.state/pdf payloads counted");
  assert.equal(facts.phases.cold_open.count, 2);
  assert.equal(facts.phases.warm.count, 1);
  assert.ok(facts.phases.cold_open.bytesEstimate > facts.phases.warm.bytesEstimate);
});

test("same-page different-src pulse cannot validate a swapped frame", () => {
  const { state, baseline, pumpFrames, snapshot } = createHarness();
  const image = makeImage({ alt: "第 1 页", src: "blob:doc-a-page-0" });
  state.inspectionImage = image;
  image.complete = true;
  assert.equal(baseline.awaitFrame("react", "第 1 页"), true);
  pumpFrames(2);
  const stalePulse = snapshot().pulses.react;
  assert.equal(stalePulse.matched, true);
  assert.equal(stalePulse.current.src, "blob:doc-a-page-0");
  // 同页号但资源已切换：旧 pulse 的 src 不再等于当前 DOM src，不能当作帧见证。
  image.currentSrc = "blob:doc-b-page-0";
  const after = snapshot();
  assert.equal(after.current.src, "blob:doc-b-page-0");
  assert.notEqual(stalePulse.current.src, after.current.src);
  // 未解码/未布局的当前帧同样不满足 ok 条件。
  image.complete = false;
  baseline.awaitFrame("react2", "第 1 页");
  pumpFrames(2);
  assert.equal(snapshot().pulses.react2.matched, false, "incomplete frame must not match");
});

test("dispose disconnects observer, stops frame waits and removes the message listener", () => {
  const { state, baseline, pumpFrames, snapshot } = createHarness();
  assert.equal(baseline.dispose(), true);
  const observer = FakeMutationObserver.prototype.instances[0];
  assert.ok(observer.disconnected, "mutation observer disconnected");
  assert.equal(baseline.awaitFrame("after", "第 1 页"), false);
  pumpFrames(4);
  assert.equal(snapshot().pulses.after, undefined, "no pulses after dispose");
  const before = state.listeners.message.length;
  for (const handler of [...state.listeners.message]) handler({ data: { type: "app.state", payload: { scope: "pdf" } } });
  const counted = snapshot().stateMessages.totalCount;
  assert.equal(before, 0, "listener removed by dispose");
  assert.equal(counted, 0, "no counting after dispose");
});

test("first all-known pdf state event is pinned per runtime session inside nested state", () => {
  const { state, baseline, snapshot } = createHarness();
  const dispatch = (payload) => {
    for (const handler of state.listeners.message) handler({ data: payload });
  };
  const pdfState = (runtimeSession, pageCount, detectedCount) => ({
    type: "app.state",
    payload: {
      sessionId: "bridge-9",
      revision: 5,
      scope: "pdf",
      change: "pages",
      state: {
        sessionId: runtimeSession,
        documentId: `doc-${runtimeSession}`,
        pageCount,
        detectedCount,
      },
    },
  });
  state.now = 100;
  dispatch(pdfState("s1", 6, 6));
  state.now = 500;
  dispatch(pdfState("s1", 6, 6));
  dispatch(pdfState("s2", 3, 3));
  const allKnown = snapshot().allKnownFirst;
  assert.equal(allKnown.s1.at, 100, "first event pinned; later polls must not overwrite");
  assert.equal(allKnown.s1.pageCount, 6);
  assert.equal(allKnown.s2.at, 500, "new runtime session gets its own record");
  assert.equal(
    allKnown["bridge-9"],
    undefined,
    "bridge session id must never be used as the key",
  );
  dispatch(pdfState("s3", 5, 2));
  assert.equal(snapshot().allKnownFirst.s3, undefined, "partial detection never recorded");
});

test("publications dedupe identical states and stay bounded across generations", () => {
  const { state, baseline, snapshot } = createHarness();
  const dispatch = (payload) => {
    for (const handler of state.listeners.message) handler({ data: payload });
  };
  const pdfState = (runtimeSession, previewUrl, page = 0, revision = 1) => ({
    type: "app.state",
    payload: {
      sessionId: "bridge-11",
      revision,
      scope: "pdf",
      change: "preview",
      state: {
        sessionId: runtimeSession,
        documentId: `doc-${runtimeSession}`,
        selectedPage: page,
        revision,
        pageCount: 4,
        detectedCount: 4,
        pagePreview: previewUrl === null ? null : { url: previewUrl, mediaType: "image/png", byteLength: 10 },
      },
    },
  });
  state.now = 10;
  dispatch(pdfState("s1", "https://app.vibeocr/__resource/low-res"));
  state.now = 20;
  dispatch(pdfState("s1", "https://app.vibeocr/__resource/hq", 0, 2));
  let facts = snapshot();
  assert.equal(facts.publications.length, 2);
  assert.equal(facts.publications[0].previewUrl, "https://app.vibeocr/__resource/low-res");
  assert.equal(facts.publications[0].sessionId, "s1");
  assert.equal(facts.publications[0].page, 0);
  assert.equal(facts.publications[0].revision, 1);
  assert.equal(facts.publications[1].previewUrl, "https://app.vibeocr/__resource/hq");
  assert.equal(facts.publications[1].revision, 2);
  assert.equal(facts.publicationsOverflow, false);
  // 同一发布代次重复 200 次不增长不溢出（长篇渐进检测合法场景）。
  for (let index = 0; index < 200; index++) {
    dispatch(pdfState("s1", "https://app.vibeocr/__resource/hq", 0, 2));
  }
  facts = snapshot();
  assert.equal(facts.publications.length, 2, "identical states deduped");
  assert.equal(facts.publicationsOverflow, false);
  // 无 preview 不入发布序列。
  dispatch(pdfState("s1", null));
  assert.equal(snapshot().publications.length, 2);
  for (let index = 0; index < 120; index++) {
    dispatch(pdfState("s2", `https://app.vibeocr/__resource/r${index}`, index % 4, 1));
  }
  facts = snapshot();
  assert.equal(facts.publications.length, 96, "bounded publication history");
  assert.equal(facts.publicationsOverflow, true, "overflow is an explicit evidence gap");
});

test("attr-leading currentSrc generation swap records via load fallback and retries lagged decode", async () => {
  const { state, dispatchLoad, triggerScan, flush, pumpFrames, snapshot } = createHarness();
  const image = makeImage({ alt: "第 1 页", src: "blob:doc-a-page-0" });
  state.images.push(image);
  state.inspectionImage = image;
  const resolvers = [];
  image.decode = () =>
    new Promise((resolve) => {
      resolvers.push(resolve);
    });
  triggerScan();
  let current = snapshot();
  assert.equal(current.images.length, 1);
  // 属性换新代次而 currentSrc 滞后：无 mutation 回调，document 捕获 load 补记。
  image.attrSrc = "blob:doc-b-page-0";
  dispatchLoad(image);
  current = snapshot();
  assert.equal(current.images.length, 2, "load fallback records the missed generation");
  const second = current.images[1];
  assert.equal(second.fullSrc, "blob:doc-b-page-0");
  // 旧代次早 decode 完成：非当前代次，永不翻活。
  resolvers[0]();
  await flush();
  pumpFrames(2);
  current = snapshot();
  assert.equal(current.images[0].superseded, true);
  assert.equal(current.images[0].decodedAt, null);
  // 新代次首次 decode 在 currentSrc 追上前完成：置 superseded，不产生 decodedAt。
  resolvers[1]();
  await flush();
  pumpFrames(2);
  current = snapshot();
  assert.equal(current.images[1].superseded, true, "lagging currentSrc supersedes first attempt");
  assert.equal(current.images[1].decodedAt, null);
  // currentSrc 追上 + 同代次 load：补救重试成功，不新增条目、不重置 srcSetAt。
  image.currentSrc = "blob:doc-b-page-0";
  dispatchLoad(image);
  current = snapshot();
  assert.equal(current.images.length, 2, "retry does not duplicate entries");
  resolvers[2]();
  await flush();
  pumpFrames(2);
  current = snapshot();
  assert.equal(current.images.length, 2);
  assert.equal(current.images[0].decodedAt, null, "old generation never revives");
  assert.equal(current.images[0].superseded, true);
  assert.notEqual(current.images[1].decodedAt, null, "current generation retried decode");
  assert.notEqual(current.images[1].frameOpportunityAt, null);
  assert.equal(current.images[1].srcSetAt, second.srcSetAt, "srcSetAt preserved");
});

test("t2_pdf_baseline driver self-check passes (exit priority + report isolation)", () => {
  const script = path.join(repoRoot, "scripts", "t2_pdf_baseline.ps1");
  const result = spawnSync("pwsh", ["-NoProfile", "-File", script, "-SelfCheck"], {
    encoding: "utf8",
    cwd: repoRoot,
  });
  assert.equal(
    result.status,
    0,
    `driver self-check failed:\n${result.stdout}\n${result.stderr}`,
  );
});

test("controlled detection samples reject default short/mixed kinds", () => {
  const script = path.join(repoRoot, "scripts", "t2_pdf_baseline.ps1");
  const result = spawnSync("pwsh", ["-NoProfile", "-File", script,
    "-SelfCheck", "-DetectionDelayMs", "500"], { encoding: "utf8", cwd: repoRoot });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /requires Kinds=scan_long/);
});

test("t3 driver rejects path collisions and invalid monitor evidence", () => {
  const script = path.join(repoRoot, "scripts", "t3_pdf_pipeline.ps1");
  const result = spawnSync("pwsh", ["-NoProfile", "-File", script, "-SelfCheck"], {
    encoding: "utf8",
    cwd: repoRoot,
  });
  assert.equal(
    result.status,
    0,
    `t3 driver self-check failed:\n${result.stdout}\n${result.stderr}`,
  );
});
