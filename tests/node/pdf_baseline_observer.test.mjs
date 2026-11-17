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
    naturalWidth,
    naturalHeight,
    clientWidth: 400,
    clientHeight: 300,
    complete: false,
    isConnected: true,
    getAttribute(name) {
      return name === "alt" ? this._alt : name === "src" ? this.currentSrc : null;
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
  Object.defineProperty(document, "visibilityState", { get: () => state.visibilityState });
  document.hasFocus = () => state.hasFocus;
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
  return { state, baseline, pumpFrames, triggerScan, flush, snapshot };
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

test("webview message listener filters real app.state/pdf envelope per phase", () => {
  const { state, baseline, snapshot } = createHarness();
  const dispatch = (payload) => {
    for (const handler of state.listeners.message) handler({ data: payload });
  };
  baseline.setPhase("cold_open");
  dispatch({ type: "app.state", payload: { scope: "pdf", pages: 1 } });
  dispatch({ type: "app.state", payload: { scope: "pdf", pages: 2 } });
  dispatch({ type: "app.state", payload: { scope: "batch", items: [] } });
  dispatch({ type: "command.receipt", payload: { scope: "pdf" } });
  baseline.setPhase("warm");
  dispatch({ type: "app.state", payload: { scope: "pdf", pages: 3 } });
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
