// Contract: the imageEdit copy/save handoffs prove POST 201 + the product's
// resourceUri + exactly one public command + its receipt through the shared
// pre-click observer, whose serialized installer must run isolated from any
// Node closure (CDP network observation is unreliable on this page).
import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const { observeSceneCopyTerminal, settleSceneCopyTimeline, assertClipboardReadback } = await import(
  pathToFileURL(path.join(scriptDir, '..', '..', 'scripts',
    'smoke_native_actions.mjs')).href);

setTimeout(() => {
  console.error('smoke_runtime_demand_tools contract test hung for 60s.');
  process.exit(1);
}, 60000).unref();

// Fake page exposing the Playwright surface the shared observer needs;
// evaluate simulates serialization - only the function SOURCE and a
// structured-cloneable argument cross the boundary.
function fakeWorkbenchPage() {
  const page = new EventEmitter();
  page.bindings = new Map();
  page.installerSource = null;
  page.installerArg = undefined;
  page.exposeBinding = async (name, handler) => { page.bindings.set(name, handler); };
  page.evaluate = async (fn, arg) => {
    page.installerSource = `(${fn.toString()})`;
    page.installerArg = arg === undefined ? undefined : JSON.parse(JSON.stringify(arg));
  };
  return page;
}

// Run the captured installer from its serialized source inside an isolated
// vm context: Node-module closures do not exist there, so any in-page
// reference to them throws ReferenceError, the observer's passive catch
// swallows it and the command stays unobserved (failing the flows below).
// `window` and `Blob` are the only page globals provided.
const installIntoWindow = (page, window, emit) => {
  window.__sceneCopyObserver = emit;
  new vm.Script(page.installerSource)
    .runInContext(vm.createContext({ window, Blob }))(page.installerArg);
};

// Shared mount seam: one observer + serialized/isolated install against a
// fake workbench window; every flow below must fail closed, never hang.
const mountObserver = async (actions, fetchImpl) => {
  const page = fakeWorkbenchPage();
  const observed = await observeSceneCopyTerminal(page, { actions });
  const emit = (event) => page.bindings.get('__sceneCopyObserver')(null, event);
  const inbound = [];
  const posted = [];
  const channel = {
    postMessage: function (message) { posted.push(message); return 'post-ok'; },
    addEventListener(type, listener) { if (type === 'message') inbound.push(listener); },
    removeEventListener() {},
  };
  const previousWindow = globalThis.window;
  globalThis.window = { fetch: fetchImpl, chrome: { webview: channel } };
  const originalPost = channel.postMessage;
  installIntoWindow(page, window, emit);
  return {
    page, observed, emit, channel, inbound, posted,
    originalFetch: fetchImpl, originalPost,
    restore: () => {
      window.__nativeActionsRestoreFetch?.();
      globalThis.window = previousWindow;
    },
  };
};

const settleMicrotasks = async (until, limit = 200) => {
  for (let index = 0; index < limit && !until(); index++)
    await new Promise((resolve) => setImmediate(resolve));
};

const pngMagic = [137, 80, 78, 71, 13, 10, 26, 10];
const annotationUri = (digit) => `https://app.vibeocr/__annotation/${digit.repeat(32)}`;
const imageEditJudgeOptions = (action) =>
  ({ action, requireScope: 'imageEdit', requireClose: false });

// 1. Judge semantics: default (no options) keeps the exact scene rules;
// the options only add selection (action/scope) and drop the close demands
// for non-terminal pages; every other fail-closed rule stays.
test('shared judge keeps scene defaults and adds bounded imageEdit options', () => {
  const copyUri = annotationUri('c');
  const saveUri = annotationUri('d');
  const layerUri = annotationUri('e');
  const upload = (seq, order, resourceUri, status = 201, overrides = {}) => ({ seq,
    requestAt: 1000, requestOrder: order, responseStatus: status, responseAt: 1000,
    responseOrder: order + 1, resourceUri, uriAt: 1000, uriOrder: order + 2,
    error: null, blobMeta: { mimeType: 'image/png', byteLength: 8, problem: null },
    ...overrides });
  const command = (envelopeId, order, action, resourceUri, scope, overrides = {}) =>
    ({ envelopeId, scope, action, at: 1000, order, resourceUri, ...overrides });
  const imageEditTimeline = (overrides = {}) => ({
    uploads: [upload(1, 1, copyUri), upload(2, 6, layerUri), upload(3, 11, saveUri)],
    copyCommands: [command('cmd-copy', 4, 'copyScreenshotImage', copyUri, 'imageEdit'),
      command('cmd-save', 14, 'saveScreenshotImage', saveUri, 'imageEdit')],
    copyReceipts: [], closedAt: null, closedOrder: null, crashedAt: null, ...overrides,
  });
  const imageEditJudge = (overrides, options) => settleSceneCopyTimeline(
    imageEditTimeline(overrides), 'ImageEdit handoff', options);
  // 非终结模式:粘贴建立的会话使复制/保存走 copyScreenshotImage/
  // saveScreenshotImage 会话变体;按 action+scope 选唯一 command/upload,
  // 201 与顺序纪律不变。
  assert.equal(imageEditJudge(null, imageEditJudgeOptions('copyScreenshotImage')), true);
  assert.equal(imageEditJudge(null, imageEditJudgeOptions('saveScreenshotImage')), true);
  // 双命令时间线上默认语义(不带 action)必须仍失败;默认仍要求 close。
  assert.throws(() => settleSceneCopyTimeline(imageEditTimeline(), 'Scene copy'),
    /expected exactly one public copyScreenshotImage command/);
  assert.throws(() => imageEditJudge(null, { action: 'copyScreenshotImage' }),
    /did not close after the accepted copy/);
  // scope 选出不匹配、重复同 action 命令、缺 resourceUri、异资源/重复关联、
  // 非 201、乱序、失败回执均不变地致命。
  assert.throws(() => imageEditJudge(
    { copyCommands: [command('cmd-copy', 4, 'copyScreenshotImage', copyUri, 'pinEdit'),
      command('cmd-save', 14, 'saveScreenshotImage', saveUri, 'imageEdit')] },
    imageEditJudgeOptions('copyScreenshotImage')),
  /expected exactly one public copyScreenshotImage command/);
  assert.throws(() => imageEditJudge(
    { copyCommands: [command('cmd-copy', 4, 'copyScreenshotImage', copyUri, 'imageEdit'),
      command('cmd-copy-2', 8, 'copyScreenshotImage', copyUri, 'imageEdit'),
      command('cmd-save', 14, 'saveScreenshotImage', saveUri, 'imageEdit')] },
    imageEditJudgeOptions('copyScreenshotImage')),
  /expected exactly one public copyScreenshotImage command/);
  assert.throws(() => imageEditJudge(
    { copyCommands: [command('cmd-copy', 4, 'copyScreenshotImage', null, 'imageEdit'),
      command('cmd-save', 14, 'saveScreenshotImage', saveUri, 'imageEdit')] },
    imageEditJudgeOptions('copyScreenshotImage')),
  /carried no resourceUri/);
  assert.throws(() => imageEditJudge(
    { copyCommands: [command('cmd-copy', 4, 'copyScreenshotImage', annotationUri('0'),
      'imageEdit'), command('cmd-save', 14, 'saveScreenshotImage', saveUri, 'imageEdit')] },
    imageEditJudgeOptions('copyScreenshotImage')),
  /exactly one accepted upload/);
  assert.throws(() => imageEditJudge(
    { uploads: [upload(1, 1, copyUri), upload(2, 6, copyUri),
      upload(3, 11, saveUri)] },
    imageEditJudgeOptions('copyScreenshotImage')),
  /exactly one accepted upload/);
  assert.throws(() => imageEditJudge(
    { uploads: [upload(1, 1, copyUri, 500), upload(2, 6, layerUri),
      upload(3, 11, saveUri)] },
    imageEditJudgeOptions('copyScreenshotImage')),
  /was not accepted/);
  assert.throws(() => imageEditJudge(
    { copyCommands: [command('cmd-copy', 1, 'copyScreenshotImage', copyUri, 'imageEdit'),
      command('cmd-save', 14, 'saveScreenshotImage', saveUri, 'imageEdit')],
      uploads: [upload(1, 2, copyUri), upload(2, 6, layerUri), upload(3, 11, saveUri)] },
    imageEditJudgeOptions('copyScreenshotImage')),
  /closed before the public copy was dispatched/);
  assert.throws(() => imageEditJudge(
    { copyReceipts: [{ envelopeId: 'cmd-copy', at: 1000, order: 5, ok: false,
      problemCode: 'desktop_command_failed' }] },
    imageEditJudgeOptions('copyScreenshotImage')),
  /host rejected the public copy command/);
  // 场景默认语义回归:带 close 的单命令时间线按原规则通过/失败。
  const sceneTimeline = {
    uploads: [upload(1, 1, copyUri)],
    copyCommands: [command('cmd-scene', 4, 'copyScreenshotImage', copyUri, 'recognition')],
    copyReceipts: [], closedAt: 1000, closedOrder: 8, crashedAt: null,
  };
  assert.equal(settleSceneCopyTimeline(sceneTimeline, 'Scene copy'), true);
  assert.throws(() => settleSceneCopyTimeline(
    { ...sceneTimeline, closedAt: null, closedOrder: null }, 'Scene copy'),
  /did not close after the accepted copy/);
  assert.throws(() => settleSceneCopyTimeline({ ...sceneTimeline, crashedAt: 1000 },
    'Scene copy'), /crashed instead of closing/);
});

// 2. Real async two-action handoff through the actual shared installer in
// its isolated context (real timer-resolved promises, real json body reads):
// both imageEdit actions proven per action from ONE installation; the
// interleaved automatic text-layer upload and its recognition-scope command
// never count; Promise identities and the postMessage return value are
// forwarded unchanged; the restore hook leaves the page surfaces intact.
test('real-async shared observer serves both imageEdit handoffs from one install', async () => {
  const copyUri = annotationUri('a');
  const layerUri = annotationUri('b');
  const saveUri = annotationUri('f');
  const createdFetchPromises = [];
  const returnedFetchPromises = [];
  const createdJsonPromises = [];
  const heldJsonPromises = [];
  const postResults = [];
  let nextResourceUri = copyUri;
  const originalFetch = function (...args) {
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
  const mounted = await mountObserver(['copyScreenshotImage', 'saveScreenshotImage'],
    originalFetch);
  const { observed, channel, inbound, posted } = mounted;
  // The watched actions crossed the boundary as a serialized plain array.
  assert.deepEqual(mounted.page.installerArg,
    ['copyScreenshotImage', 'saveScreenshotImage']);
  // 镜像 uploadAnnotatedImage + actions.run:真实 await 链,每个 Promise 都在
  // 后续真实轮次结算。
  const productHandoff = async (id, scope, action) => {
    const started = window.fetch('/__annotation', { method: 'POST',
      headers: { 'Content-Type': 'image/png' },
      body: new Blob([Uint8Array.from(pngMagic)], { type: 'image/png' }) });
    returnedFetchPromises.push(started);
    const response = await started;
    if (!response.ok) throw new Error('annotated image upload failed');
    const jsonPromise = response.json();
    heldJsonPromises.push(jsonPromise);
    const payload = await jsonPromise;
    postResults.push(channel.postMessage({ version: 2, kind: 'request', id,
      type: 'app.command', payload: { sessionId: 'bridge',
        command: { scope, action, arguments: { resourceUri: payload.resourceUri } } } }));
  };
  const deliverReceipt = (id, ok) => {
    for (const listener of inbound)
      listener({ data: { version: 2, kind: 'response', id, type: 'app.command',
        payload: { revision: 1, ok, problem: ok ? null
          : { code: 'desktop_command_failed', category: 'Desktop',
              retryable: false, messageKey: 'failed' } } } });
  };
  try {
    // 公开复制:等待者先挂起,事件到达后结算;非终结判定 + 成功回执。
    const copyCommandPending = observed.waitForCommand('copyScreenshotImage', 1000);
    await productHandoff('cmd-copy', 'imageEdit', 'copyScreenshotImage');
    const copyCommand = await copyCommandPending;
    assert.equal(copyCommand.action, 'copyScreenshotImage');
    assert.equal(copyCommand.scope, 'imageEdit');
    assert.equal(copyCommand.resourceUri, copyUri);
    assert.equal(settleSceneCopyTimeline(observed.timeline, 'ImageEdit copy',
      imageEditJudgeOptions('copyScreenshotImage')), true);
    const copyUpload = observed.timeline.uploads.find(
      (item) => item.resourceUri === copyUri);
    assert.equal(copyUpload.responseStatus, 201);
    assert.equal(copyUpload.blobMeta.byteLength, pngMagic.length);
    assert.equal((await observed.waitForUploadBlob(copyUpload.seq)).byteLength,
      pngMagic.length);
    const copyReceiptPending = observed.waitForReceipt('cmd-copy', 1000);
    deliverReceipt('cmd-copy', true);
    assert.equal((await copyReceiptPending).ok, true);
    // 插入一次自动文字层上传与 recognition 命令:命令不计入观察,上传仍被
    // 记录但从不被认领;其回执也不进入时间线。
    nextResourceUri = layerUri;
    await productHandoff('cmd-layer', 'recognition', 'prepareScreenshotTextLayer');
    deliverReceipt('cmd-layer', true);
    // 真实保存流:201 + resourceUri + 公开 saveScreenshotImage 命令 + 回执。
    nextResourceUri = saveUri;
    await productHandoff('cmd-save', 'imageEdit', 'saveScreenshotImage');
    const saveCommand = await observed.waitForCommand('saveScreenshotImage', 1000);
    assert.equal(saveCommand.resourceUri, saveUri);
    assert.equal(settleSceneCopyTimeline(observed.timeline, 'ImageEdit save',
      imageEditJudgeOptions('saveScreenshotImage')), true);
    const saveReceiptPending = observed.waitForReceipt('cmd-save', 1000);
    deliverReceipt('cmd-save', true);
    assert.equal((await saveReceiptPending).ok, true);
    // 原 fetch/json Promise 对象未改变;postMessage 返回值原样转发。
    assert.equal(createdFetchPromises.length, 3);
    for (let index = 0; index < createdFetchPromises.length; index++)
      assert.strictEqual(returnedFetchPromises[index], createdFetchPromises[index]);
    for (let index = 0; index < createdJsonPromises.length; index++)
      assert.strictEqual(heldJsonPromises[index], createdJsonPromises[index]);
    assert.deepEqual(postResults, ['post-ok', 'post-ok', 'post-ok']);
    assert.deepEqual(posted.map((message) => message.payload.command.action),
      ['copyScreenshotImage', 'prepareScreenshotTextLayer', 'saveScreenshotImage']);
    // 只有被观察的两个公开命令与两份回执进入时间线;真实顺序
    // request < response(结算即刻) < uri 交接 < 命令。
    assert.deepEqual(observed.timeline.copyCommands.map((item) => item.action),
      ['copyScreenshotImage', 'saveScreenshotImage']);
    assert.deepEqual(observed.timeline.copyReceipts.map((item) => item.envelopeId),
      ['cmd-copy', 'cmd-save']);
    assert.equal(observed.timeline.uploads.length, 3);
    for (const pair of [['copyScreenshotImage', copyUri], ['saveScreenshotImage', saveUri]]) {
      const command = observed.timeline.copyCommands.find(
        (item) => item.action === pair[0]);
      const uploadRecord = observed.timeline.uploads.find(
        (item) => item.resourceUri === pair[1]);
      assert(uploadRecord.requestOrder < uploadRecord.responseOrder &&
        uploadRecord.responseOrder < uploadRecord.uriOrder &&
        uploadRecord.uriOrder < command.order,
      `real async handoff order violated: ${JSON.stringify(observed.timeline)}`);
    }
    const layerUpload = observed.timeline.uploads.find(
      (item) => item.resourceUri === layerUri);
    assert(layerUpload && layerUpload.requestOrder > copyUpload.requestOrder,
      'text-layer upload must stay independently ordered');
    // 首条命令仍结算 received(默认语义不变);非终结页面从不结算 ended。
    await observed.received;
    // 还原后不再观测任何流量(桥信封 id 单调唯一,还原后的流量使用全新
    // id,正如真实桥一样)。
    window.__nativeActionsRestoreFetch();
    assert.equal(window.fetch, originalFetch);
    assert.equal(channel.postMessage, mounted.originalPost);
    const recordsAfterRestore = observed.timeline.uploads.length +
      observed.timeline.copyCommands.length + observed.timeline.copyReceipts.length;
    await window.fetch('/__annotation', { method: 'POST',
      body: new Blob([Uint8Array.from(pngMagic)], { type: 'image/png' }) });
    channel.postMessage({ version: 2, kind: 'request', id: 'cmd-after-restore',
      type: 'app.command', payload: { sessionId: 'bridge',
        command: { scope: 'imageEdit', action: 'copyScreenshotImage',
          arguments: { resourceUri: copyUri } } } });
    deliverReceipt('cmd-after-restore', true);
    assert.equal(observed.timeline.uploads.length +
      observed.timeline.copyCommands.length + observed.timeline.copyReceipts.length,
      recordsAfterRestore);
  } finally {
    mounted.restore();
  }
});

// 3. Non-201 upload: the status is recorded from the real fetch resolution
// and the judge fails closed with no command observed; bounded deadline
// waits resolve null (missing evidence fails closed) without hanging, and
// an already-observed command resolves immediately from the timeline.
test('non-201 upload and unobserved command fail closed without hanging', async () => {
  const mounted = await mountObserver(['copyScreenshotImage', 'saveScreenshotImage'],
    function () {
      return new Promise((resolve) => setTimeout(() => resolve({
        status: 500, ok: false,
        json: () => new Promise((resolveJson, rejectJson) =>
          rejectJson(new Error('body parse failed'))),
      }), 5));
    });
  const { observed, emit } = mounted;
  try {
    const response = await window.fetch('/__annotation', { method: 'POST',
      headers: { 'Content-Type': 'image/png' },
      body: new Blob([Uint8Array.from(pngMagic)], { type: 'image/png' }) });
    assert.equal(response.status, 500);
    await settleMicrotasks(() => observed.timeline.uploads.length === 1 &&
      observed.timeline.uploads[0].responseOrder !== null);
    const upload = observed.timeline.uploads[0];
    assert.equal(upload.responseStatus, 500);
    assert.equal(upload.blobMeta.byteLength, pngMagic.length);
    assert.equal(await observed.waitForCommand('copyScreenshotImage', 20), null);
    assert.equal(await observed.waitForReceipt('cmd-copy', 20), null);
    assert.throws(() => settleSceneCopyTimeline(observed.timeline, 'ImageEdit copy',
      imageEditJudgeOptions('copyScreenshotImage')),
    /expected exactly one public copyScreenshotImage command/);
    // 已观察事件立即结算:直接 emit 的命令记录可被同步取回。
    emit({ kind: 'copy-command', envelopeId: 'cmd-ghost', action: 'saveScreenshotImage',
      scope: 'imageEdit', resourceUri: annotationUri('1') });
    assert.equal((await observed.waitForCommand('saveScreenshotImage', 20)).envelopeId,
      'cmd-ghost');
  } finally {
    mounted.restore();
  }
});

// 4. Failure receipt for the public command never passes: the bounded
// receipt wait resolves ok=false so the smoke fails closed on it, and the
// judge (options or default) treats it as fatal.
test('failure receipt for the public command never passes', async () => {
  const uri = annotationUri('2');
  const mounted = await mountObserver(['copyScreenshotImage', 'saveScreenshotImage'],
    function () {
      return new Promise((resolve) => setTimeout(() => resolve({
        status: 201, ok: true,
        json: () => new Promise((resolveJson) =>
          setTimeout(() => resolveJson({ resourceUri: uri }), 3)),
      }), 5));
    });
  const { observed, channel, inbound } = mounted;
  try {
    const started = window.fetch('/__annotation', { method: 'POST',
      headers: { 'Content-Type': 'image/png' },
      body: new Blob([Uint8Array.from(pngMagic)], { type: 'image/png' }) });
    await (await started).json();
    channel.postMessage({ version: 2, kind: 'request', id: 'cmd-copy',
      type: 'app.command', payload: { sessionId: 'bridge',
        command: { scope: 'imageEdit', action: 'copyScreenshotImage',
          arguments: { resourceUri: uri } } } });
    await observed.waitForCommand('copyScreenshotImage', 1000);
    for (const listener of inbound)
      listener({ data: { version: 2, kind: 'response', id: 'cmd-copy',
        type: 'app.command', payload: { revision: 1, ok: false,
          problem: { code: 'desktop_command_failed', category: 'Desktop',
            retryable: false, messageKey: 'failed' } } } });
    const receipt = await observed.waitForReceipt('cmd-copy', 1000);
    assert.equal(receipt.ok, false);
    assert.equal(receipt.problemCode, 'desktop_command_failed');
    assert.throws(() => settleSceneCopyTimeline(observed.timeline, 'ImageEdit copy',
      imageEditJudgeOptions('copyScreenshotImage')),
    /host rejected the public copy command/);
  } finally {
    mounted.restore();
  }
});

// 5. Serialization isolation (the browser-closure contract): the installer
// runs from its serialized source in an isolated vm context - a Node-closure
// reference throws there, the passive catch swallows it and no command is
// ever recorded, so this test fails on any closure-dependent installer.
test('installer survives serialization into an isolated context', async () => {
  const uri = annotationUri('3');
  const mounted = await mountObserver(['copyScreenshotImage', 'saveScreenshotImage'],
    function () { return Promise.resolve({ status: 201, ok: true }); });
  const { observed, channel } = mounted;
  assert.deepEqual(mounted.page.installerArg,
    ['copyScreenshotImage', 'saveScreenshotImage']);
  const send = (id, scope, action, resourceUri) =>
    channel.postMessage({ version: 2, kind: 'request', id, type: 'app.command',
      payload: { sessionId: 'bridge',
        command: { scope, action, arguments: { resourceUri } } } });
  try {
    send('cmd-copy', 'imageEdit', 'copyScreenshotImage', uri);
    const command = await observed.waitForCommand('copyScreenshotImage', 200);
    assert(command, `command not observed across the serialization boundary: ${
      JSON.stringify(observed.timeline)}`);
    assert.equal(command.resourceUri, uri);
    // 不在 action 集内的命令仍不记录;无会话变体 copyAnnotatedImage 不得作
    // 为兑底被接受——粘贴会话页只可能是会话变体。
    send('cmd-other', 'recognition', 'prepareScreenshotTextLayer', uri);
    send('cmd-legacy', 'imageEdit', 'copyAnnotatedImage', uri);
    assert.equal(await observed.waitForCommand('prepareScreenshotTextLayer', 20), null);
    assert.equal(await observed.waitForCommand('copyAnnotatedImage', 20), null);
  } finally {
    mounted.restore();
  }
});

// 6. Task #318 scene clipboard readback (shared judge export): the scene
// copy timeline proves command/upload/close only; the Windows Clipboard
// itself is proven by the workbench paste readback, decoded in the SAME
// WebView as this revision's export and compared byte-for-byte. A stale QR
// image (this smoke's own pre-copy clipboard content, wrong dimensions), a
// mirror or an alpha-only change keeps aggregate totals and must still fail.
test('scene clipboard readback judge rejects stale and rearranged clipboard images', () => {
  const rgbaImage = (width, height, pixels) => ({ width, height,
    rgbaBase64: Buffer.from(Uint8Array.from(pixels.flat())).toString('base64') });
  // 2x2 场景合成图:标注橙 + fixture 明/暗像素。
  const composite = [[243, 139, 53, 255], [240, 240, 240, 255],
    [16, 16, 16, 255], [240, 240, 240, 255]];
  const revision = { width: 2, height: 2, expectedRgba: rgbaImage(2, 2, composite) };
  assert.equal(assertClipboardReadback({ mediaType: 'image/png',
    rgba: rgbaImage(2, 2, composite) }, revision, 'Scene clipboard readback'), true);
  // 复制未落剪贴板时,剪贴板仍是本冒烟自己先前复制的 QR 合成图:
  // 尺寸与本次选区不符,必须拒绝。
  assert.throws(() => assertClipboardReadback({ mediaType: 'image/png',
    rgba: rgbaImage(2, 2, composite) },
  { width: 320, height: 320, expectedRgba: revision.expectedRgba },
  'Scene clipboard readback'), /instead of the selected 320x320/);
  // 同尺寸同颜色总量:行交换(镜像类重排)不能通过。
  assert.throws(() => assertClipboardReadback({ mediaType: 'image/png',
    rgba: rgbaImage(2, 2, [composite[1], composite[0], composite[3], composite[2]]) },
  revision, 'Scene clipboard readback'), /differs from this revision's export at pixel 0 \(red\)/);
  // 仅 alpha 变化不能通过。
  assert.throws(() => assertClipboardReadback({ mediaType: 'image/png',
    rgba: rgbaImage(2, 2, [[243, 139, 53, 254], composite[1], composite[2], composite[3]]) },
  revision, 'Scene clipboard readback'), /differs from this revision's export at pixel 0 \(alpha\)/);
});

console.log('smoke_runtime_demand_tools contract: passed');
