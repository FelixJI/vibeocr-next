// Behavioral contract for the seams exported by scripts/smoke_native_actions.mjs:
// the owned-process cleanup (issues #167/#169), the capture-scene canvas
// physical→CSS geometry mapping (issue #216), and the pre-click passive
// scene-copy observer (goal316/318) that keeps upload/copy-command/receipt/
// close evidence in Node even when the scene retires instantly. The children
// here are real short-lived node processes spawned from process.execPath; no
// desktop windows are touched (the native close path is only exercised
// through stopOwned's bounded fallback on a windowless child). Finally blocks
// terminate only the ChildProcess handles this test itself spawned.
import assert from 'node:assert/strict';
import { ChildProcess, spawn } from 'node:child_process';
import { EventEmitter, once } from 'node:events';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const { stopOwned, closeAppNormally, assertCaptureCanvasLayout, settleSceneCopyTimeline,
  observeSceneCopyTerminal, raceDeadline } = await import(
  pathToFileURL(path.join(scriptDir, '..', '..', 'scripts', 'smoke_native_actions.mjs')).href);

setTimeout(() => {
  console.error('smoke_native_actions contract test hung for 60s.');
  process.exit(1);
}, 60000).unref();

function spawnIdle() {
  return spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'],
    { stdio: 'ignore', windowsHide: true });
}

async function dispose(child) {
  if (child.exitCode === null && child.signalCode === null) child.kill();
  if (child.exitCode === null && child.signalCode === null) await once(child, 'exit');
}

// Cases 4-7 snapshot the exit/error listener counts before the cleanup call
// and assert they are restored afterwards, directly proving that waitExit's
// aborted once(signal) wait and ownedKill's temporary capture unsubscribe.
const listenerSnapshot = (child) =>
  ({ exit: child.listenerCount('exit'), error: child.listenerCount('error') });
function assertListenersRestored(child, before) {
  assert.equal(child.listenerCount('exit'), before.exit, 'waitExit leaked an exit listener.');
  assert.equal(child.listenerCount('error'), before.error, 'waitExit or ownedKill leaked an error listener.');
}

// 1. A normally exited child must make stopOwned a no-op that resolves.
{
  const child = spawn(process.execPath, ['-e', ''], { stdio: 'ignore', windowsHide: true });
  await once(child, 'exit');
  assert.equal(child.exitCode, 0);
  try {
    await stopOwned(child, 'close', { AppPid: child.pid });
    await closeAppNormally(child, { AppPid: child.pid });
  } finally {
    await dispose(child);
  }
  console.log('case1 normally-exited: ok');
}

// A completed app with a nonzero exit must fail acceptance even though cleanup is done.
{
  const child = spawn(process.execPath, ['-e', 'process.exit(5)'], { stdio: 'ignore', windowsHide: true });
  await once(child, 'exit');
  await assert.rejects(closeAppNormally(child, { AppPid: child.pid }), /Normal app close exited 5/);
  console.log('normal-close-nonzero-fails: ok');
}

// 2. An already SIGTERM-exited child (exit event already fired, exitCode null,
// signalCode SIGTERM) must still count as done: stopOwned resolves instead of
// waiting for an exit event that can never fire again.
{
  const child = spawnIdle();
  child.kill();
  await once(child, 'exit');
  assert.equal(child.exitCode, null);
  assert.equal(child.signalCode, 'SIGTERM');
  try {
    await stopOwned(child, 'close', { AppPid: child.pid });
    await assert.rejects(closeAppNormally(child, { AppPid: child.pid }), /received signal SIGTERM/);
  } finally {
    await dispose(child);
  }
  assert.equal(child.exitCode, null);
  assert.equal(child.signalCode, 'SIGTERM');
  console.log('case2 signal-exited: ok');
}

// 3. A still-alive owned windowless child must be terminated through the real
// handle: native close fails without an owned HWND and the bounded fallback
// settles the same handle by signal.
{
  const child = spawnIdle();
  try {
    await stopOwned(child, 'close', { AppPid: child.pid });
    assert.equal(child.exitCode, null);
    assert.equal(child.signalCode, 'SIGTERM');
  } finally {
    await dispose(child);
  }
  console.log('case3 alive-owned-cleanup: ok');
}

// 4. Deadline-positive (the observed pinned-smoke WinUI failure): the owned
// process really exited and the same handle answers dead, but the public
// exitCode/signalCode fields are still null because the JS exit notification
// never arrived. The old code waited for an exit event that cannot fire a
// second time and failed the whole run; the endpoint probe must confirm the
// real exit. Only the two public fields are masked; the real handle and pid
// of this owned child are kept.
{
  const child = spawn(process.execPath, ['-e', ''], { stdio: 'ignore', windowsHide: true });
  await once(child, 'exit');
  const { exitCode, signalCode } = child;
  child.exitCode = null;
  child.signalCode = null;
  const listeners = listenerSnapshot(child);
  try {
    await stopOwned(child, 'close', { AppPid: child.pid });
  } finally {
    child.exitCode = exitCode;
    child.signalCode = signalCode;
    try { assertListenersRestored(child, listeners); } finally { await dispose(child); }
  }
  console.log('case4 exit-notification-late: ok');
}

// 5. Fail-closed error contract: a synchronous kill error on the real owned
// ChildProcess.kill seam must fail cleanup instead of passing as a clean
// exit. The injection only controls the method's return and emission; the
// finally block restores the original method and still kills the live child
// through its original handle only.
{
  const child = spawnIdle();
  const originalKill = child.kill.bind(child);
  child.kill = () => {
    child.emit('error', new Error('injected owned-handle kill failure'));
    return false;
  };
  const listeners = listenerSnapshot(child);
  try {
    await assert.rejects(stopOwned(child, 'close', { AppPid: child.pid }),
      /injected owned-handle kill failure/);
  } finally {
    child.kill = originalKill;
    try { assertListenersRestored(child, listeners); } finally { await dispose(child); }
  }
  console.log('case5 kill-error-fail-closed: ok');
}

// 6. Fail-closed alive contract: after the original grace and force 5s
// deadlines, a still-alive answer from the same owned handle must fail
// cleanup instead of trusting the missing exit event. Only the actual
// termination kill is suppressed; the signal-0 query reaches the real
// still-alive handle as a genuine negative control.
{
  const child = spawnIdle();
  const originalKill = child.kill.bind(child);
  child.kill = (signal) => (signal === 0 ? originalKill(0) : true);
  const listeners = listenerSnapshot(child);
  try {
    await assert.rejects(stopOwned(child, 'close', { AppPid: child.pid }),
      /survived cleanup/);
  } finally {
    child.kill = originalKill;
    try { assertListenersRestored(child, listeners); } finally { await dispose(child); }
  }
  console.log('case6 endpoint-alive-fail-closed: ok');
}

// 7. Fail-closed endpoint-error contract: an accepted termination whose
// signal-0 endpoint query synchronously emits an error and answers false
// must still fail cleanup; a bare false is never enough without the
// no-error condition on the original handle.
{
  const child = spawnIdle();
  const originalKill = child.kill.bind(child);
  child.kill = (signal) => {
    if (signal !== 0) return true;
    child.emit('error', new Error('injected endpoint query failure'));
    return false;
  };
  const listeners = listenerSnapshot(child);
  try {
    await assert.rejects(stopOwned(child, 'close', { AppPid: child.pid }),
      /endpoint probe failed: injected endpoint query failure/);
  } finally {
    child.kill = originalKill;
    try { assertListenersRestored(child, listeners); } finally { await dispose(child); }
  }
  console.log('case7 endpoint-error-fail-closed: ok');
}

// 8. A real ChildProcess object that has never spawned has no owned process
// handle; reject it before attempting native close or a signal query.
await assert.rejects(stopOwned(new ChildProcess(), 'close', {}), /positive pid/);
console.log('case8 unspawned-child-fail-closed: ok');

// 9. A 140x42 crop maps exactly through the 175% viewport scale: no CSS size
// floor may reject a legitimate small selection.
{
  const layout = assertCaptureCanvasLayout({
    box: { x: 200, y: 160, width: 80, height: 24 },
    selection: { left: 350, top: 280, width: 140, height: 42 },
    desktopClient: { X: 0, Y: 0, Width: 1920, Height: 1080 },
    viewport: { width: 1097, height: 617 },
  });
  assert.equal(layout.expected.width, 140 * (1097 / 1920));
  assert.equal(layout.scale.y, 617 / 1080);
  assert.deepEqual(layout.measured, { x: 200, y: 160, width: 80, height: 24 });
  console.log('case9 small-crop-175dpi-mapping: ok');
}

// 10. A canvas 60 CSS px off the selection's on-screen spot must fail.
{
  assert.throws(() => assertCaptureCanvasLayout({
    box: { x: 260, y: 160, width: 80, height: 24 },
    selection: { left: 350, top: 280, width: 140, height: 42 },
    desktopClient: { X: 0, Y: 0, Width: 1920, Height: 1080 },
    viewport: { width: 1097, height: 617 },
  }), /canvas x is 260/);
  console.log('case10 wrong-position-fails: ok');
}

// 11. A distorted aspect ratio must fail despite positive, usable sizes.
{
  assert.throws(() => assertCaptureCanvasLayout({
    box: { x: 200, y: 160, width: 80, height: 40 },
    selection: { left: 350, top: 280, width: 140, height: 42 },
    desktopClient: { X: 0, Y: 0, Width: 1920, Height: 1080 },
    viewport: { width: 1097, height: 617 },
  }), /canvas height is 40/);
  console.log('case11 wrong-ratio-fails: ok');
}

// 12. A virtual desktop extending left of the primary has negative screen
// coordinates; the selection still maps through its relative position.
{
  const layout = assertCaptureCanvasLayout({
    box: { x: 1330, y: 750, width: 70, height: 21 },
    selection: { left: 100, top: 60, width: 140, height: 42 },
    desktopClient: { X: -2560, Y: -1440, Width: 5120, Height: 2880 },
    viewport: { width: 2560, height: 1440 },
  });
  assert.deepEqual(layout.expected, { x: 1330, y: 750, width: 70, height: 21 });
  console.log('case12 negative-desktop-origin: ok');
}

// 13. Illegal geometry must fail closed before any tolerance comparison.
{
  const valid = {
    box: { x: 1330, y: 750, width: 70, height: 21 },
    selection: { left: 100, top: 60, width: 140, height: 42 },
    desktopClient: { X: -2560, Y: -1440, Width: 5120, Height: 2880 },
    viewport: { width: 2560, height: 1440 },
  };
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, selection: { ...valid.selection, left: -3000 },
  }), /escaped the frozen desktop/);
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, selection: { ...valid.selection, height: 0 },
  }), /selection geometry is invalid/);
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, desktopClient: { ...valid.desktopClient, Width: 0 },
  }), /client geometry is invalid/);
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, viewport: { ...valid.viewport, width: Number.NaN },
  }), /viewport is invalid/);
  assert.throws(() => assertCaptureCanvasLayout({ ...valid, box: null }),
    /positive finite visual bounds/);
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, box: { x: 1330, y: 750, width: 70, height: 0 },
  }), /positive finite visual bounds/);
  console.log('case13 illegal-geometry-fails: ok');
}

// 14. Scene-terminal copy judge (goal316/318): pass requires one public
// copyScreenshotImage command naming exactly one accepted 201 upload with a
// normal close; automatic text-layer uploads (a different resourceUri) never
// count as the copy. Duplicate copy commands, duplicate association, non-201,
// missing upload, failure receipts, crash, deadline-exceeded and out-of-order
// close all fail closed; a success receipt is welcome but never required.
{
  const copyUri = `https://app.vibeocr/__annotation/${'c'.repeat(32)}`;
  const layerUri = `https://app.vibeocr/__annotation/${'d'.repeat(32)}`;
  const upload = (seq, order, resourceUri, status = 201) => ({ seq, requestAt: 1000,
    requestOrder: order, responseStatus: status, resourceUri, responseAt: 1000,
    responseOrder: order + 1, error: null,
    blobMeta: { mimeType: 'image/png', byteLength: 8, problem: null } });
  const judgeTimeline = (overrides = {}) => ({
    uploads: [upload(1, 1, copyUri), upload(2, 5, layerUri)],
    copyCommands: [{ envelopeId: 'cmd-copy', at: 1000, order: 3, resourceUri: copyUri }],
    copyReceipts: [], closedAt: 1000, closedOrder: 7, crashedAt: null, ...overrides,
  });
  // 同一毫秒内完成全部事件:判定只看到达序,不看壁钟时间。
  assert.equal(settleSceneCopyTimeline(judgeTimeline()), true);
  assert.equal(settleSceneCopyTimeline(judgeTimeline({ copyReceipts: [
    { envelopeId: 'cmd-copy', at: 1000, order: 4, ok: true, problemCode: null }] })), true);
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline({ copyCommands: [] })),
    /exactly one public copyScreenshotImage command/);
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline({ copyCommands: [
    judgeTimeline().copyCommands[0], { envelopeId: 'cmd-copy-2', at: 1000, order: 4, resourceUri: copyUri }] })),
    /exactly one public copyScreenshotImage command/);
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline({ copyCommands: [
    { envelopeId: 'cmd-copy', at: 1000, order: 3, resourceUri: null }] })),
    /carried no resourceUri/);
  // 自动文字层的上传(不同 resourceUri)不能顶替复制上传。
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline(
    { uploads: [upload(1, 1, layerUri)] })), /exactly one accepted upload/);
  // 同一资源被两个上传认领:重复关联 fail closed。
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline(
    { uploads: [upload(1, 1, copyUri), upload(2, 5, copyUri)] })), /exactly one accepted upload/);
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline(
    { uploads: [upload(1, 1, copyUri, 500)] })), /was not accepted/);
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline({ crashedAt: 1000 })),
    /crashed instead of closing/);
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline({ closedAt: null, closedOrder: null })),
    /did not close after the accepted copy/);
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline({ noTerminalEvent: true })),
    /exceeded its deadline/);
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline({ closedOrder: 2 })),
    /closed before the public copy was dispatched/);
  assert.throws(() => settleSceneCopyTimeline(null), /timeline evidence is missing/);
  // 失败回执(若收到)永远不能通过。
  assert.throws(() => settleSceneCopyTimeline(judgeTimeline({ copyReceipts: [
    { envelopeId: 'cmd-copy', at: 1000, order: 4, ok: false,
      problemCode: 'desktop_command_failed' }] })),
    /host rejected the public copy command/);
  console.log('case14 scene-copy-judge-lifecycle: ok');
}

// Fake page exposing the Playwright surface the observer needs: captured
// exposeBinding/evaluate plus plain EventEmitter close/crash events.
function fakeScenePage() {
  const page = new EventEmitter();
  page.bindings = new Map();
  page.installer = null;
  page.exposeBinding = async (name, handler) => { page.bindings.set(name, handler); };
  page.evaluate = async (fn) => { page.installer = fn; };
  return page;
}

const settleMicrotasks = async (until, limit = 200) => {
  for (let index = 0; index < limit && !until(); index++)
    await new Promise((resolve) => setImmediate(resolve));
};

// 15. Real async json-fulfillment ordering (root pre-review regression):
// the product flow below mirrors uploadAnnotatedImage with real
// timer-resolved promises and real json body reads - no pre-arranged
// synchronous events. Status/responseOrder must be stamped when the fetch
// Promise resolves (strictly before the product's await continuation), and
// the resourceUri handover (pass-through wrap of response.json on the same
// returned Promise) must precede the public copy command even though the
// json body settles in a later real turn. Promise identities are unchanged,
// the copy pixels come from the resource-matching upload (never the
// text-layer Blob), the timeline keeps only Blob meta (bytes stay in the
// Node-side map), and an instant close keeps every record.
{
  const copyUri = `https://app.vibeocr/__annotation/${'a'.repeat(32)}`;
  const layerUri = `https://app.vibeocr/__annotation/${'b'.repeat(32)}`;
  const page = fakeScenePage();
  const observed = await observeSceneCopyTerminal(page);
  const emit = (event) => page.bindings.get('__sceneCopyObserver')(null, event);
  const pngMagic = [137, 80, 78, 71, 13, 10, 26, 10];
  const fetchCalls = [];
  const createdFetchPromises = [];
  const returnedFetchPromises = [];
  const createdJsonPromises = [];
  const heldJsonPromises = [];
  const posted = [];
  const inbound = [];
  let nextResourceUri = copyUri;
  const originalFetch = function (...args) {
    fetchCalls.push(args);
    const created = new Promise((resolve) => setTimeout(() => resolve({
      status: 201, ok: true,
      json: () => {
        const jsonPromise = new Promise((resolveJson) =>
          setTimeout(() => resolveJson({ resourceUri: nextResourceUri }), 3));
        createdJsonPromises.push(jsonPromise);
        return jsonPromise;
      },
    }), 5));
    createdFetchPromises.push(created);
    return created;
  };
  const channel = {
    postMessage: function (...args) { posted.push(args); return 'post-ok'; },
    addEventListener(type, listener) { if (type === 'message') inbound.push(listener); },
    removeEventListener() {},
  };
  const previousWindow = globalThis.window;
  globalThis.window = { fetch: originalFetch, chrome: { webview: channel } };
  window.__sceneCopyObserver = emit;
  page.installer();
  // 镜像 uploadAnnotatedImage:真实 await 链,每个 Promise 都在后续真实轮次结算。
  const productCopyFlow = async (bytes) => {
    const started = window.fetch('/__annotation', { method: 'POST',
      headers: { 'Content-Type': 'image/png' },
      body: new Blob([Uint8Array.from(bytes)], { type: 'image/png' }) });
    returnedFetchPromises.push(started);
    const response = await started;
    if (!response.ok) throw new Error('annotated image upload failed');
    const jsonPromise = response.json();
    heldJsonPromises.push(jsonPromise);
    const payload = await jsonPromise;
    channel.postMessage({ version: 2, kind: 'request', id: 'cmd-copy', type: 'app.command',
      payload: { sessionId: 'bridge', command: { scope: 'recognition',
        action: 'copyScreenshotImage',
        arguments: { resourceUri: payload.resourceUri, sessionId: 'scene', revision: 2 } } } });
  };
  try {
    await productCopyFlow(pngMagic);
    // 自动文字层:不同资源的第二次上传与 prepare 命令都不得计入复制。
    nextResourceUri = layerUri;
    const layerStarted = window.fetch('/__annotation', { method: 'POST',
      headers: { 'Content-Type': 'image/png' },
      body: new Blob([Uint8Array.from([...pngMagic, 1, 2, 3, 4])], { type: 'image/png' }) });
    returnedFetchPromises.push(layerStarted);
    const response = await layerStarted;
    const layerJson = response.json();
    heldJsonPromises.push(layerJson);
    const payload = await layerJson;
    channel.postMessage({ version: 2, kind: 'request', id: 'cmd-layer', type: 'app.command',
      payload: { sessionId: 'bridge', command: { scope: 'recognition',
        action: 'prepareScreenshotTextLayer',
        arguments: { resourceUri: payload.resourceUri, sessionId: 'scene', revision: 2 } } } });
    // 原 fetch/json Promise 对象未改变:包装器返回的正是原始实现创建的对象。
    assert.equal(fetchCalls.length, 2);
    for (let index = 0; index < createdFetchPromises.length; index++)
      assert.strictEqual(returnedFetchPromises[index], createdFetchPromises[index]);
    for (let index = 0; index < createdJsonPromises.length; index++)
      assert.strictEqual(heldJsonPromises[index], createdJsonPromises[index]);
    // 场景快速关闭:成功回执可缺席,证据已全部由 Node 持有。
    page.emit('close');
    await observed.ended;
    await observed.received;
    await settleMicrotasks(() => observed.timeline.uploads.every(
      (upload) => upload.blobMeta && upload.uriOrder !== null));
    assert.equal(observed.timeline.copyCommands.length, 1);
    assert.equal(observed.timeline.uploads.length, 2);
    // 真实异步履行顺序:request < response(结算即刻) < uri 交接 < 复制命令。
    const copyUpload = observed.timeline.uploads.find((item) => item.resourceUri === copyUri);
    const layerUpload = observed.timeline.uploads.find((item) => item.resourceUri === layerUri);
    const command = observed.timeline.copyCommands[0];
    assert(copyUpload.requestOrder < copyUpload.responseOrder &&
      copyUpload.responseOrder < copyUpload.uriOrder &&
      copyUpload.uriOrder < command.order,
    `real async copy order violated: ${JSON.stringify(observed.timeline)}`);
    assert(layerUpload.uriOrder > command.order,
      'layer upload must stay independently ordered');
    assert.equal(settleSceneCopyTimeline(observed.timeline), true);
    // timeline 只留 meta:bytes 不进入 timeline/错误串/health,由 Node 侧单独持有。
    assert(!JSON.stringify(observed.timeline).includes('1,2,3,4,5,6,7,8'));
    assert(!JSON.stringify(observed.timeline).includes('137,80,78,71'));
    assert.equal(copyUpload.blobMeta.byteLength, 8);
    assert.equal(layerUpload.blobMeta.byteLength, 12);
    // 复制像素必须来自对应 resourceUri 的上传(8 字节),不是文字层 Blob(12 字节)。
    assert.equal((await observed.waitForUploadBlob(copyUpload.seq)).byteLength, 8);
    assert.equal((await observed.waitForUploadBlob(layerUpload.seq)).byteLength, 12);
    // 还原后不再观测任何流量。
    window.__nativeActionsRestoreFetch();
    assert.equal(window.fetch, originalFetch);
    const recordsAfterRestore = observed.timeline.uploads.length +
      observed.timeline.copyCommands.length;
    await window.fetch('/__annotation', { method: 'POST',
      headers: { 'Content-Type': 'image/png' },
      body: new Blob([Uint8Array.from(pngMagic)], { type: 'image/png' }) });
    channel.postMessage({ version: 2, kind: 'request', id: 'cmd-copy', type: 'app.command',
      payload: { sessionId: 'bridge', command: { scope: 'recognition',
        action: 'copyScreenshotImage', arguments: { resourceUri: copyUri } } } });
    assert.equal(observed.timeline.uploads.length + observed.timeline.copyCommands.length,
      recordsAfterRestore);
  } finally {
    window.__nativeActionsRestoreFetch?.();
    globalThis.window = previousWindow;
  }
  console.log('case15a real-async-json-ordering: ok');
}

// 15b. A failure receipt for the public copy command can never pass.
{
  const page = fakeScenePage();
  const observed = await observeSceneCopyTerminal(page);
  const emit = (event) => page.bindings.get('__sceneCopyObserver')(null, event);
  const uri = `https://app.vibeocr/__annotation/${'e'.repeat(32)}`;
  emit({ kind: 'upload', seq: 1 });
  emit({ kind: 'upload-response', seq: 1, status: 201 });
  emit({ kind: 'upload-uri', seq: 1, resourceUri: uri });
  emit({ kind: 'copy-command', envelopeId: 'cmd-copy', resourceUri: uri });
  emit({ kind: 'copy-receipt', envelopeId: 'cmd-copy', ok: false,
    problemCode: 'desktop_command_failed' });
  page.emit('close');
  await observed.ended;
  assert.throws(() => settleSceneCopyTimeline(observed.timeline),
    /host rejected the public copy command/);
  console.log('case15b failure-receipt-fails: ok');
}

// 15c. A close that arrives before the public copy evidence is illegal
// ordering, not a success.
{
  const page = fakeScenePage();
  const observed = await observeSceneCopyTerminal(page);
  const emit = (event) => page.bindings.get('__sceneCopyObserver')(null, event);
  const uri = `https://app.vibeocr/__annotation/${'f'.repeat(32)}`;
  page.emit('close');
  emit({ kind: 'upload', seq: 1 });
  emit({ kind: 'upload-response', seq: 1, status: 201 });
  emit({ kind: 'upload-uri', seq: 1, resourceUri: uri });
  emit({ kind: 'copy-command', envelopeId: 'cmd-copy', resourceUri: uri });
  await observed.ended;
  assert.throws(() => settleSceneCopyTimeline(observed.timeline),
    /closed before the public copy was dispatched/);
  console.log('case15c close-before-copy-fails: ok');
}

// 15d. The Node-side Blob waiter stays pending until the per-upload Blob
// evidence arrives, then resolves with exactly that record.
{
  const page = fakeScenePage();
  const observed = await observeSceneCopyTerminal(page);
  const emit = (event) => page.bindings.get('__sceneCopyObserver')(null, event);
  emit({ kind: 'upload', seq: 1 });
  let blob = 'pending';
  const waiting = observed.waitForUploadBlob(1).then((value) => { blob = value; return value; });
  await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(blob, 'pending');
  emit({ kind: 'upload-blob', seq: 1,
    blob: { mimeType: 'image/png', byteLength: 3, bytes: [1, 2, 3], problem: null } });
  assert.equal((await waiting).byteLength, 3);
  assert.deepEqual(await observed.waitForUploadBlob(1),
    { mimeType: 'image/png', byteLength: 3, bytes: [1, 2, 3], problem: null });
  // timeline 只存 meta,完整 bytes 留在 Node 侧证据 map。
  assert.deepEqual(observed.timeline.uploads[0].blobMeta,
    { mimeType: 'image/png', byteLength: 3, problem: null });
  assert(!JSON.stringify(observed.timeline).includes('1,2,3'));
  console.log('case15d upload-blob-waiter: ok');
}

// 16. raceDeadline settles false on expiry while clear() prevents both
// firing and a dangling timer.
{
  const expired = raceDeadline(1);
  assert.equal(await expired.promise, false);
  const cleared = raceDeadline(20000);
  cleared.clear();
  assert.equal(cleared.promise instanceof Promise, true);
  console.log('case16 race-deadline: ok');
}

console.log('smoke_native_actions contract: passed');
