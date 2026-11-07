// Run only on an explicitly authorized interactive Windows desktop. This script
// drives the shipped WinUI/WebView2 application and sends real keyboard input.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { promisify } from 'node:util';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import readline from 'node:readline';
import crypto from 'node:crypto';

const execFileAsync = promisify(execFile);
const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const requireWebAssets = createRequire(path.resolve(scriptDir,
  '../src/dotnet/VibeOCR.App/WebAssets/package.json'));
const { chromium, expect } = requireWebAssets('@playwright/test');
const nativeScript = path.join(scriptDir, 'native_actions_fixture.ps1');
const hotkey = 'Ctrl+Alt+Shift+F10';
const recognizeHotkey = 'Ctrl+Alt+Shift+F11';
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function taskbarState() {
  const { stdout } = await execFileAsync('pwsh', ['-NoProfile', '-NonInteractive',
    '-File', path.join(scriptDir, 'scroll_capture_fixture.ps1'), '-Action', 'taskbar-state'],
  { timeout: 20000, windowsHide: true });
  return Number(stdout.trim());
}

function option(name) {
  const index = process.argv.indexOf(name);
  assert(index > 0 && index + 1 < process.argv.length,
    `Required option ${name} is missing.`);
  return process.argv[index + 1];
}

function contained(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

async function freeLocalPort() {
  const server = net.createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const port = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  return port;
}

async function native(action, options = {}) {
  const pointerAction = ['mouse-move', 'mouse-down', 'mouse-up', 'mouse-right', 'frame', 'geometry'].includes(action);
  const script = pointerAction ? path.join(scriptDir, 'scroll_capture_fixture.ps1') : nativeScript;
  const args = ['-NoProfile', '-NonInteractive', '-File', script,
    '-Action', action];
  for (const [key, value] of Object.entries(options))
    args.push(`-${key}`, String(value));
  const { stdout } = await execFileAsync('pwsh', args, {
    timeout: 15000, windowsHide: true, maxBuffer: 1024 * 1024,
  });
  return stdout.trim();
}

async function windows(pid) {
  const output = await native('windows', { AppPid: pid });
  if (!output || output === 'null') return [];
  const value = JSON.parse(output);
  return Array.isArray(value) ? value : [value];
}

async function waitForWindows(pid, predicate, timeoutMs = 10000) {
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    const matches = (await windows(pid)).filter(predicate);
    if (matches.length) return matches;
    await delay(180);
  }
  throw new Error(`Owned window did not appear for process ${pid}.`);
}

function area(window) {
  const r = window.Bounds;
  return Math.max(0, r.Right - r.Left) * Math.max(0, r.Bottom - r.Top);
}

function inside(window, x, y) {
  const r = window.Bounds;
  return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
}

function parseRect(value) {
  const [left, top, right, bottom] = value.split(',').map(Number);
  assert([left, top, right, bottom].every(Number.isSafeInteger) &&
    right > left && bottom > top, 'Synthetic fixture geometry is invalid.');
  return { left, top, right, bottom, width: right - left, height: bottom - top };
}

// Capture-scene canvas geometry: the physical selection must map exactly
// through the measured editor WebView viewport (no window.vibeocrCaptureScene).
function assertCaptureCanvasLayout({ box, selection, desktopClient, viewport }) {
  const tolerance = 2;
  const finite = Number.isFinite;
  assert(finite(desktopClient?.X) && finite(desktopClient?.Y) &&
    finite(desktopClient?.Width) && desktopClient.Width > 0 &&
    finite(desktopClient?.Height) && desktopClient.Height > 0,
    `Frozen desktop client geometry is invalid: ${JSON.stringify(desktopClient)}.`);
  assert(finite(selection?.left) && finite(selection?.top) &&
    finite(selection?.width) && selection.width > 0 &&
    finite(selection?.height) && selection.height > 0,
    `Physical selection geometry is invalid: ${JSON.stringify(selection)}.`);
  const left = selection.left - desktopClient.X;
  const top = selection.top - desktopClient.Y;
  assert(left >= 0 && top >= 0 &&
    left + selection.width <= desktopClient.Width &&
    top + selection.height <= desktopClient.Height,
    'Physical selection escaped the frozen desktop client.');
  assert(finite(viewport?.width) && viewport.width > 0 &&
    finite(viewport?.height) && viewport.height > 0,
    `Editor WebView viewport is invalid: ${JSON.stringify(viewport)}.`);
  assert(box && finite(box.x) && finite(box.y) &&
    finite(box.width) && box.width > 0 && finite(box.height) && box.height > 0,
    `Editor canvas has no positive finite visual bounds: ${JSON.stringify(box)}.`);
  const scale = { x: viewport.width / desktopClient.Width,
    y: viewport.height / desktopClient.Height };
  const expected = { x: left * scale.x, y: top * scale.y,
    width: selection.width * scale.x, height: selection.height * scale.y };
  for (const field of ['x', 'y', 'width', 'height'])
    assert(Math.abs(box[field] - expected[field]) <= tolerance,
      `Editor canvas ${field} is ${box[field]} instead of the physical→CSS ` +
      `mapping ${expected[field]} (±${tolerance} CSS px).`);
  return { measured: { ...box }, expected, scale, tolerance,
    selection: { ...selection }, desktopClient: { ...desktopClient },
    viewport: { ...viewport } };
}

// Scene-terminal copy judge (goal316/318): one public copyScreenshotImage
// command whose resourceUri names exactly one accepted 201 upload, then a
// normal close. Text-layer uploads (different resourceUri) never count; no
// command, duplicates, non-201, failure receipts, crash, deadline-exceeded or
// out-of-order close fail closed. A success receipt is never required.
function settleSceneCopyTimeline(timeline, context = 'Scene copy') {
  assert(timeline && typeof timeline === 'object', `${context}: timeline evidence is missing.`);
  const evidence = JSON.stringify(timeline);
  assert.equal(timeline.crashedAt ?? null, null,
    `${context}: scene crashed instead of closing: ${evidence}`);
  assert(Array.isArray(timeline.copyCommands) && timeline.copyCommands.length === 1,
    `${context}: expected exactly one public copyScreenshotImage command: ${evidence}`);
  const command = timeline.copyCommands[0];
  assert(typeof command.resourceUri === 'string' && command.resourceUri.length > 0,
    `${context}: public copy command carried no resourceUri: ${evidence}`);
  const associated = (timeline.uploads ?? []).filter(
    (upload) => upload.resourceUri === command.resourceUri);
  assert.equal(associated.length, 1,
    `${context}: the copy resource must match exactly one accepted upload: ${evidence}`);
  const upload = associated[0];
  assert.equal(upload.responseStatus ?? null, 201,
    `${context}: public annotation upload was not accepted: ${evidence}`);
  assert(timeline.closedAt !== null && timeline.closedAt !== undefined,
    `${context}: scene did not close after the accepted copy: ${evidence}`);
  assert(Number.isSafeInteger(upload.requestOrder) && Number.isSafeInteger(upload.responseOrder) &&
    Number.isSafeInteger(command.order) && Number.isSafeInteger(timeline.closedOrder) &&
    upload.requestOrder < upload.responseOrder && upload.responseOrder < command.order &&
    command.order < timeline.closedOrder,
  `${context}: scene closed before the public copy was dispatched: ${evidence}`);
  assert(!(timeline.copyReceipts ?? []).some((receipt) => receipt.ok === false),
    `${context}: the host rejected the public copy command: ${evidence}`);
  assert(!timeline.noTerminalEvent, `${context}: scene close exceeded its deadline: ${evidence}`);
  return true;
}

// Pre-click passive scene-copy observation (goal316/318), awaited before
// the copy click: an in-page wrapper keeps every __annotation POST, its real
// response status (stamped the moment the fetch Promise resolves), the
// product-side json resourceUri (observed through a pass-through wrap of
// response.json, so the handover precedes the product's await continuation
// without an extra clone parse), the same immutable Blob's bytes (Node-held
// per upload; the timeline keeps only meta), the outgoing public copy
// command and receipts for it; CDP page events only carry close/crash
// (network events proved lossy when the scene retires quickly). Wrappers
// forward arguments, the fetch Promise object and the json Promise object
// unchanged and never delay the command. Works on any object exposing the
// Playwright page event/binding surface (unit-testable with a plain
// EventEmitter plus captured exposeBinding/evaluate).
async function observeSceneCopyTerminal(page) {
  let eventOrder = 0;
  let acceptCommand;
  const received = new Promise((resolve) => { acceptCommand = resolve; });
  const timeline = { uploads: [], copyCommands: [], copyReceipts: [],
    closedAt: null, closedOrder: null, crashedAt: null };
  const uploadBySeq = new Map();
  const blobEvidence = new Map();
  const blobWaiters = new Map();
  const settleBlobWaiters = (seq) => {
    const waiters = blobWaiters.get(seq);
    if (!waiters) return;
    blobWaiters.delete(seq);
    const blob = blobEvidence.get(seq) ?? null;
    for (const resolve of waiters) resolve(blob);
  };
  const ended = new Promise((resolve) => {
    page.on('close', () => {
      timeline.closedAt = Date.now(); timeline.closedOrder = ++eventOrder; resolve();
    });
    page.on('crash', () => { timeline.crashedAt = Date.now(); resolve(); });
  });
  await page.exposeBinding('__sceneCopyObserver', (_source, event) => {
    if (!event || typeof event !== 'object') return;
    if (event.kind === 'upload') {
      const record = { seq: event.seq, requestAt: Date.now(), requestOrder: ++eventOrder,
        responseStatus: null, responseAt: null, responseOrder: null,
        resourceUri: null, uriAt: null, uriOrder: null,
        error: null, blobMeta: null };
      timeline.uploads.push(record);
      uploadBySeq.set(event.seq, record);
    } else if (event.kind === 'upload-response') {
      const record = uploadBySeq.get(event.seq);
      if (record && record.responseOrder === null) {
        record.responseStatus = event.status ?? null;
        record.responseAt = Date.now();
        record.responseOrder = ++eventOrder;
      }
    } else if (event.kind === 'upload-uri') {
      const record = uploadBySeq.get(event.seq);
      if (record && record.uriOrder === null) {
        record.resourceUri = event.resourceUri ?? null;
        record.uriAt = Date.now();
        record.uriOrder = ++eventOrder;
      }
    } else if (event.kind === 'upload-error') {
      const record = uploadBySeq.get(event.seq);
      if (record) record.error = event.error ?? 'upload failed';
    } else if (event.kind === 'upload-blob') {
      const record = uploadBySeq.get(event.seq);
      blobEvidence.set(event.seq, event.blob ?? null);
      if (record) {
        record.blobMeta = event.blob ? { mimeType: event.blob.mimeType ?? null,
          byteLength: event.blob.byteLength ?? null, problem: event.blob.problem ?? null } : null;
      }
      settleBlobWaiters(event.seq);
    } else if (event.kind === 'copy-command') {
      timeline.copyCommands.push({ envelopeId: event.envelopeId, at: Date.now(),
        order: ++eventOrder, resourceUri: event.resourceUri ?? null });
      acceptCommand();
    } else if (event.kind === 'copy-receipt') {
      timeline.copyReceipts.push({ envelopeId: event.envelopeId, at: Date.now(),
        order: ++eventOrder, ok: event.ok ?? null, problemCode: event.problemCode ?? null });
    }
  });
  await page.evaluate(() => {
    const emit = (event) => {
      try {
        // exposeBinding returns a Promise: a quickly retiring page can make
        // the observation channel reject; digest only that delivery failure
        // (Node still fails closed on missing evidence) without touching any
        // product Promise.
        const delivery = window.__sceneCopyObserver(event);
        if (delivery && typeof delivery.catch === 'function') delivery.catch(() => {});
      } catch { /* passive; Node keeps the timeline */ }
    };
    const state = { uploadSeq: 0, copyIds: {} };
    const originalFetch = window.fetch;
    const pushBlob = (seq, body) => {
      if (!(body instanceof Blob))
        emit({ kind: 'upload-blob', seq,
          blob: { mimeType: null, byteLength: null, bytes: null,
            problem: 'upload body was not a Blob' } });
      else if (body.size > 8 * 1024 * 1024)
        emit({ kind: 'upload-blob', seq,
          blob: { mimeType: body.type, byteLength: body.size, bytes: null,
            problem: 'synthetic PNG exceeded 8 MiB' } });
      else body.arrayBuffer().then(
        (buffer) => emit({ kind: 'upload-blob', seq,
          blob: { mimeType: body.type, byteLength: body.size,
            bytes: Array.from(new Uint8Array(buffer)), problem: null } }),
        () => emit({ kind: 'upload-blob', seq,
          blob: { mimeType: body.type, byteLength: body.size, bytes: null,
            problem: 'Blob observation failed' } }));
    };
    window.fetch = function (input, init) {
      const isUpload = input === '/__annotation' && init && init.method === 'POST';
      let seq = 0;
      if (isUpload) {
        seq = ++state.uploadSeq;
        emit({ kind: 'upload', seq });
        pushBlob(seq, init.body);
      }
      const promise = originalFetch.apply(this, arguments);
      if (isUpload)
        promise.then((response) => {
          // Status and responseOrder are stamped at fetch-Promise resolve,
          // strictly before the product's await continuation (this observer
          // registers first), so a later json body read can never reorder
          // them (real-probe regression: command seq 6, clone-json seq 7).
          emit({ kind: 'upload-response', seq, status: response?.status ?? null });
          try {
            if (response && typeof response.json === 'function') {
              const originalJson = response.json;
              response.json = function () {
                const jsonPromise = originalJson.apply(this, arguments);
                // Registered before the product's await attaches to the same
                // returned Promise: the resourceUri handover always precedes
                // the copy command, with no extra clone parse.
                jsonPromise.then((payload) => {
                  emit({ kind: 'upload-uri', seq,
                    resourceUri: payload && typeof payload === 'object' &&
                      typeof payload.resourceUri === 'string' ? payload.resourceUri : null });
                }, () => {});
                return jsonPromise;
              };
            }
          } catch { /* passive */ }
        }, (error) => emit({ kind: 'upload-error', seq,
          error: `${error?.name ?? 'Error'}: ${error?.message ?? 'upload failed'}` }));
      return promise;
    };
    const channel = window.chrome && window.chrome.webview;
    const originalPost = channel && typeof channel.postMessage === 'function'
      ? channel.postMessage : null;
    if (originalPost)
      channel.postMessage = function (message) {
        try {
          const command = message && message.kind === 'request' && message.type === 'app.command' &&
            message.payload ? message.payload.command : null;
          if (command && command.action === 'copyScreenshotImage') {
            state.copyIds[message.id] = true;
            emit({ kind: 'copy-command', envelopeId: message.id,
              resourceUri: command.arguments &&
                typeof command.arguments.resourceUri === 'string'
                ? command.arguments.resourceUri : null });
          }
        } catch { /* passive */ }
        return originalPost.apply(this, arguments);
      };
    if (channel && typeof channel.addEventListener === 'function')
      channel.addEventListener('message', ({ data }) => {
        if (!data || data.kind !== 'response' || data.type !== 'app.command' ||
            !Object.prototype.hasOwnProperty.call(state.copyIds, data.id)) return;
        const problem = data.payload && data.payload.problem;
        emit({ kind: 'copy-receipt', envelopeId: data.id,
          ok: typeof (data.payload && data.payload.ok) === 'boolean'
            ? data.payload.ok : null,
          problemCode: problem && typeof problem.code === 'string' ? problem.code : null });
      });
    window.__nativeActionsRestoreFetch = () => {
      window.fetch = originalFetch;
      if (originalPost) channel.postMessage = originalPost;
      delete window.__nativeActionsRestoreFetch;
    };
  });
  return {
    timeline, received, ended,
    // Resolves with the Node-held Blob evidence of one upload (bytes or the
    // bounded problem record) from the side map - never the timeline, which
    // keeps only meta; only the harness waits on it, never the page.
    waitForUploadBlob: (seq) => new Promise((resolve) => {
      if (blobEvidence.has(seq)) { resolve(blobEvidence.get(seq) ?? null); return; }
      const waiters = blobWaiters.get(seq) ?? [];
      waiters.push(resolve);
      blobWaiters.set(seq, waiters);
    }),
  };
}

// Bounded race timer: a lost race is always cleared by the caller in a
// finally block, so an expired-or-cleared timer never keeps the Node process
// alive after the smoke finished.
function raceDeadline(milliseconds) {
  let timer;
  const promise = new Promise((resolve) => {
    timer = setTimeout(() => resolve(false), milliseconds);
  });
  return { promise, clear: () => clearTimeout(timer) };
}

async function firstLine(child, timeoutMs) {
  const lines = readline.createInterface({ input: child.stdout });
  let stderr = '';
  child.stderr.on('data', (chunk) => { stderr = (stderr + chunk).slice(-1000); });
  try {
    return await Promise.race([
      once(lines, 'line').then(([line]) => line),
      once(child, 'exit').then(([code]) => {
        throw new Error(`Synthetic fixture exited ${code}: ${stderr}`);
      }),
      delay(timeoutMs).then(() => { throw new Error('Synthetic fixture startup timed out.'); }),
    ]);
  } finally {
    lines.close();
  }
}

async function startFixture() {
  const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File', nativeScript,
    '-Action', 'fixture'], {
    stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
  });
  try {
    const fields = (await firstLine(child, 15000)).split('|');
    assert.equal(fields.length, 12, 'Synthetic fixture must report only its native handles and rectangles.');
    const fixture = {
      child,
      pid: Number(fields[0]), threadId: Number(fields[1]),
      root: Number(fields[2]), group: Number(fields[3]),
      button: Number(fields[4]), edit: Number(fields[5]),
      background: Number(fields[6]),
      rootRect: parseRect(fields[7]),
      buttonRect: parseRect(fields[9]),
      editRect: parseRect(fields[10]),
      backgroundRect: parseRect(fields[11]),
    };
    assert.equal(fixture.pid, child.pid, 'Synthetic fixture PID does not match its process.');
    assert(Number.isSafeInteger(fixture.root) && Number.isSafeInteger(fixture.threadId));
    return fixture;
  } catch (error) {
    await forceStop(child);
    throw error;
  }
}

// A signal exit leaves exitCode null; its already-fired exit event will not recur.
function exited(child) {
  return child.exitCode !== null || child.signalCode !== null;
}

async function waitExit(child, timeoutMs) {
  if (!child || exited(child)) return true;
  // events.once also watches 'error' while waiting; an abandoned race leaves
  // that listener behind to swallow a later synchronous kill error as an
  // unhandled rejection. Aborting the wait unsubscribes both of its own
  // listeners; the timer is always dropped and other subscriptions stay.
  const deadline = new AbortController();
  const timer = setTimeout(() => deadline.abort(), timeoutMs);
  try {
    await once(child, 'exit', { signal: deadline.signal });
    return true;
  } catch (error) {
    if (deadline.signal.aborted) return false;
    throw error;
  } finally {
    deadline.abort();
    clearTimeout(timer);
  }
}

// Private seam over the original ChildProcess.kill() on the spawn-owned
// handle: a failed kill can emit a transient synchronous 'error', so capture
// it for the fail-closed decision and always unsubscribe again.
function ownedKill(child, signal) {
  const errors = [];
  const onError = (error) => errors.push(error);
  child.once('error', onError);
  try {
    return { accepted: child.kill(signal), errors };
  } finally {
    child.removeListener('error', onError);
  }
}

async function forceStop(child) {
  // Same-handle termination only works for a successfully spawned process;
  // an unspawned PID-less object must be rejected, not reported as cleaned.
  assert(Number.isSafeInteger(child?.pid) && child.pid > 0,
    'Owned cleanup requires a spawned ChildProcess with a positive pid.');
  if (exited(child)) return;
  // ChildProcess keeps the Windows process handle. A fresh PID/PPID tree can
  // belong to another instance after exit; terminate only this owned handle.
  // The app's Supervisor descendants remain owned by its kill-on-close Job.
  const kill = ownedKill(child);
  assert.equal(kill.errors.length, 0,
    `Owned kill of process ${child.pid} failed: ${kill.errors.map((error) => error.message).join('; ')}`);
  if (await waitExit(child, 5000)) return;
  // The force notification deadline passed without a JS exit event (the
  // observed WinUI failure mode). One signal-0 endpoint probe on the same
  // spawn-owned Windows handle is the only extra evidence; only a no-error
  // "exited" answer on the original handle confirms actual termination.
  // Exceptions, any error and a still-alive answer all fail closed.
  assert.equal(process.platform, 'win32',
    'signal-0 endpoint confirmation requires the Windows original-handle contract.');
  const probe = ownedKill(child, 0);
  console.warn(`Owned process ${child.pid}: exit notification missing ` +
    `(exitCode=${child.exitCode}, signalCode=${child.signalCode}); same-handle signal-0 probe ` +
    `accepted=${probe.accepted}, errors=${probe.errors.length}.`);
  assert.equal(probe.errors.length, 0,
    `Owned process ${child.pid} endpoint probe failed: ${probe.errors.map((error) => error.message).join('; ')}`);
  assert.equal(probe.accepted, false, `Owned process ${child.pid} survived cleanup.`);
}
async function stopOwned(child, closeAction, options) {
  if (!child) return;
  assert(Number.isSafeInteger(child.pid) && child.pid > 0,
    'Owned cleanup requires a spawned ChildProcess with a positive pid.');
  if (exited(child)) return;
  // The native close/quit actions post to an owned window handle. A
  // windowless child has no HWND at all; the fixture would only reject the
  // useless handle-0 request after a full pwsh start, so skip the futile
  // native attempt and go straight to the bounded same-handle kill path.
  // Production callers always pass a real owned Handle and are unchanged.
  if (options?.Handle)
    try { await native(closeAction, options); } catch { /* bounded PID cleanup below */ }
  if (await waitExit(child, 5000)) return;
  await forceStop(child);
}

async function closeAppNormally(child, options) {
  assert(Number.isSafeInteger(child?.pid) && child.pid > 0,
    'Normal app close requires a spawned ChildProcess with a positive pid.');
  if (!exited(child)) await native('close', options);
  assert(await waitExit(child, 30000), 'Normal app close timed out after 30 seconds.');
  assert.equal(child.signalCode, null, `Normal app close received signal ${child.signalCode}.`);
  assert.equal(child.exitCode, 0, `Normal app close exited ${child.exitCode}.`);
}

async function connectPage(child, port) {
  const until = Date.now() + 25000;
  let lastError;
  while (Date.now() < until) {
    if (child.exitCode !== null)
      throw new Error(`Isolated app exited before WebView2 CDP became available: ${child.exitCode}.`);
    let browser;
    try {
      browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, {
        timeout: 1200,
      });
      const context = browser.contexts()[0];
      assert(context, 'CDP returned no WebView2 context.');
      let page = context.pages().find((item) =>
        item.url().startsWith('https://app.vibeocr/index.html'));
      if (!page && context.pages().length) {
        page = context.pages()[0];
        await page.waitForURL(/^https:\/\/app\.vibeocr\/index\.html/, { timeout: 8000 });
      }
      if (!page) {
        page = await context.waitForEvent('page', { timeout: 8000 });
        await page.waitForURL(/^https:\/\/app\.vibeocr\/index\.html/, { timeout: 8000 });
      }
      await page.getByRole('link', { name: '设置' }).waitFor({ timeout: 10000 });
      return { browser, page };
    } catch (error) {
      lastError = error;
      if (browser) await browser.close().catch(() => {});
      await delay(250);
    }
  }
  throw new Error(`Isolated WebView2 CDP did not become ready: ${lastError?.message}`);
}

async function launchApp(candidate, webviewData, instanceId) {
  const port = await freeLocalPort();
  const executable = path.join(candidate, 'app', 'VibeOCR.WinUI.exe');
  const child = spawn(executable,
    ['--shell-only', '--profile', 'production', '--install-root', candidate], {
      cwd: path.dirname(executable), stdio: 'ignore',
      env: {
        ...process.env,
        VIBEOCR_SELF_TEST_SMOKE: 'native-actions-e2e',
        VIBEOCR_SELF_TEST_INSTANCE: instanceId,
        WEBVIEW2_USER_DATA_FOLDER: webviewData,
        WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:
          `--remote-debugging-port=${port} --remote-debugging-address=127.0.0.1`,
      },
    });
  try {
    const { browser, page } = await connectPage(child, port);
    const main = (await waitForWindows(child.pid, (item) => item.Visible && area(item) > 20000))
      .sort((a, b) => area(b) - area(a))[0];
    return { child, browser, page, main, port };
  } catch (error) {
    try { await forceStop(child); }
    catch (cleanupError) {
      throw new AggregateError([error, cleanupError],
        `Isolated app startup and owned PID cleanup both failed: ${error.message}; ${cleanupError.message}`);
    }
    throw error;
  }
}

async function openSettings(page) {
  await page.getByRole('link', { name: '设置' }).click();
  await page.getByRole('heading', { name: '设置' }).waitFor();
}

async function toolbarStatus(page, label) {
  await page.getByText(`当前状态：${label}`, { exact: false }).waitFor();
}

async function setCheckbox(page, name, checked) {
  const checkbox = page.getByRole('checkbox', { name });
  if (await checkbox.isChecked() !== checked) await checkbox.click();
  // Controlled settings wait for the native bridge acknowledgement.
  await expect(checkbox).toBeChecked({ checked });
}

async function focusRecorder(app, input) {
  // CDP input focus does not establish Windows foreground ownership.
  const bounds = JSON.parse(await native('webview-bounds', {
    AppPid: app.child.pid, Handle: app.main.Handle,
  }));
  const box = await input.boundingBox();
  assert(box, 'Recorder has no visible bounds.');
  const view = await app.page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
  const sx = (bounds.Right - bounds.Left) / view.width;
  const sy = (bounds.Bottom - bounds.Top) / view.height;
  assert(sx > 0 && sy > 0 && Math.abs(sx - sy) < 0.03,
    'Owned WebView must have a uniform measured scale.');
  await native('focus-fixture', {
    FixturePid: app.child.pid, Handle: app.main.Handle,
    X: Math.round(bounds.Left + (box.x + box.width / 2) * sx),
    Y: Math.round(bounds.Top + (box.y + box.height / 2) * sy),
  });
}

async function configure(app, evidence) {
  const { page } = app;
  await openSettings(page);
  await page.evaluate(() => {
    window.__hotkeyRecordingEvents = [];
    for (const type of ['keydown', 'keyup', 'focusin', 'focusout']) {
      document.addEventListener(type, event => {
        const input = event.target;
        if (!(input instanceof HTMLInputElement) || !input.closest('.hotkey-recorder')) return;
        window.__hotkeyRecordingEvents.push({
          type, key: event.key, code: event.code, trusted: event.isTrusted,
          ctrl: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, meta: event.metaKey,
          value: input.value, status: input.closest('.hotkey-recorder').innerText,
        });
        window.__hotkeyRecordingEvents = window.__hotkeyRecordingEvents.slice(-32);
      }, true);
    }
  });
  evidence.stage = 'edit-first-recording';
  const row = page.locator('.hotkey-action-row').filter({ hasText: '截图编辑' });
  const editInput = row.getByRole('textbox', { name: '截图编辑新快捷键' });
  await editInput.click();
  await row.getByRole('status').filter({ hasText: '请按下新的组合键' }).waitFor();
  await focusRecorder(app, editInput);
  // FixturePid is the existing hotkey command's foreground-owner guard; here it owns the app.
  await native('hotkey', { FixturePid: app.child.pid });
  await expect(editInput).toHaveValue(hotkey);
  await row.getByRole('button', { name: '应用 截图编辑' }).click();
  await row.getByText(`当前生效：${hotkey}`).waitFor();
  evidence.stage = 'edit-registered-rerecording';
  // Re-record our now-registered F10 from an empty draft: seeing the complete
  // combo proves the old OS registration released it to this real WebView2 input.
  await row.getByRole('button', { name: '清空 截图编辑' }).click();
  await expect(editInput).toHaveValue('');
  await editInput.click();
  await row.getByRole('status').filter({ hasText: '请按下新的组合键' }).waitFor();
  await focusRecorder(app, editInput);
  await native('hotkey', { FixturePid: app.child.pid });
  await expect(editInput).toHaveValue(hotkey);
  assert((await windows(app.child.pid)).some(item =>
    item.Handle === app.main.Handle && item.Visible),
  'Recording an already-registered key unexpectedly hid the main window.');
  evidence.stage = 'edit-rerecording-escape';
  evidence.beforeEscape = JSON.parse(await native('probe', {
    AppPid: app.child.pid, FixturePid: app.child.pid, X: 0, Y: 0,
  }));
  evidence.webviewBeforeEscape = await page.evaluate(() => ({
    focused: document.hasFocus(), visibility: document.visibilityState,
    activeElement: document.activeElement?.id,
    events: window.__hotkeyRecordingEvents,
  }));
  await native('escape', { AppPid: app.child.pid });
  evidence.afterEscape = JSON.parse(await native('probe', {
    AppPid: app.child.pid, FixturePid: app.child.pid, X: 0, Y: 0,
  }));
  await expect(editInput).toHaveValue('');
  await row.getByText(`当前生效：${hotkey}`).waitFor();

  const recognizeRow = page.locator('.hotkey-action-row').filter({ hasText: '快捷截图识别' });
  evidence.stage = 'recognition-recording';
  const recognizeInput = recognizeRow.getByRole('textbox', { name: '快捷截图识别新快捷键' });
  await recognizeInput.click();
  await recognizeRow.getByRole('status').filter({ hasText: '请按下新的组合键' }).waitFor();
  await focusRecorder(app, recognizeInput);
  await native('recognize-hotkey', { ForegroundPid: app.child.pid });
  await expect(recognizeInput).toHaveValue(recognizeHotkey);
  await recognizeRow.getByRole('button', { name: '应用 快捷截图识别' }).click();
  await recognizeRow.getByText(`当前生效：${recognizeHotkey}`).waitFor();

  await setCheckbox(page, '启用悬浮工具栏', true);
  await setCheckbox(page, '鼠标离开后自动收起', false);
  await page.getByRole('button', { name: '显示', exact: true }).click();
  await toolbarStatus(page, '显示中');
  await page.getByRole('button', { name: '隐藏', exact: true }).click();
  await toolbarStatus(page, '已主动隐藏（鼠标路过不恢复）');
}

async function verifyRestart(page) {
  await openSettings(page);
  const row = page.locator('.hotkey-action-row').filter({ hasText: '截图编辑' });
  await row.getByText(`当前生效：${hotkey}`).waitFor();
  const recognizeRow = page.locator('.hotkey-action-row').filter({ hasText: '快捷截图识别' });
  await recognizeRow.getByText(`当前生效：${recognizeHotkey}`).waitFor();
  assert(await page.getByRole('checkbox', { name: '启用悬浮工具栏' }).isChecked());
  assert(!(await page.getByRole('checkbox', { name: '鼠标离开后自动收起' }).isChecked()));
  await toolbarStatus(page, '已主动隐藏（鼠标路过不恢复）');
  await page.getByRole('button', { name: '显示', exact: true }).click();
  await toolbarStatus(page, '显示中');
}

async function watchScreenshotRevision(page) {
  await page.evaluate(() => {
    window.__nativeActionsEvidence = { sessionId: null, revision: null, inputUrl: null };
    window.chrome.webview.addEventListener('message', ({ data }) => {
      if (data?.kind !== 'event' || data?.type !== 'app.state' ||
          data?.payload?.scope !== 'recognition') return;
      const session = data.payload.state?.screenshotSession;
      if (typeof session?.sessionId === 'string' && Number.isSafeInteger(session.revision))
        window.__nativeActionsEvidence = {
          sessionId: session.sessionId, revision: session.revision, inputUrl: data.payload.state?.input?.url ?? null,
        };
    });
  });
}

async function canvasOrange(page) {
  return page.getByLabel('图片检查画布').evaluate((canvas) => {
    const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
    let orange = 0, nonBackground = 0;
    for (let i = 0; i < data.length; i += 4) {
      if (data[i] > 210 && data[i + 1] > 90 && data[i + 1] < 190 && data[i + 2] < 110)
        orange++;
      if (data[i] !== 22 || data[i + 1] !== 22 || data[i + 2] !== 22)
        nonBackground++;
    }
    return { orange, nonBackground };
  });
}

async function pngPixels(page, png, mediaType = 'image/png') {
  return page.evaluate(async ({ encoded, mediaType }) => {
    const bytes = Uint8Array.from(atob(encoded), (letter) => letter.charCodeAt(0));
    const image = await createImageBitmap(new Blob([bytes], { type: mediaType }));
    const canvas = document.createElement('canvas');
    canvas.width = image.width;
    canvas.height = image.height;
    const context = canvas.getContext('2d');
    context.drawImage(image, 0, 0);
    const { data } = context.getImageData(0, 0, image.width, image.height);
    let orange = 0, dark = 0, light = 0;
    for (let i = 0; i < data.length; i += 4) {
      if (data[i] > 210 && data[i + 1] > 90 && data[i + 1] < 190 && data[i + 2] < 110)
        orange++;
      if (data[i] < 100 && data[i + 1] < 100 && data[i + 2] < 100) dark++;
      if (data[i] > 170 && data[i + 1] > 170 && data[i + 2] > 170) light++;
    }
    return { width: image.width, height: image.height, orange, dark, light };
  }, { encoded: png.toString('base64'), mediaType });
}

const handleCursorShapes = ['sizeNWSE', 'sizeNS', 'sizeNESW', 'sizeWE', 'sizeWE', 'sizeNESW', 'sizeNS', 'sizeNWSE'];
const magnifierLabelPattern = /^(-?\d+), (-?\d+)\r?\n#([0-9A-F]{6})\r?\nRGB (\d+), (\d+), (\d+)$/;

function manualHandleAnchors(rect) {
  const midX = rect.left + rect.width / 2;
  const midY = rect.top + rect.height / 2;
  const right = rect.left + rect.width;
  const bottom = rect.top + rect.height;
  return [
    { x: rect.left, y: rect.top }, { x: midX, y: rect.top }, { x: right, y: rect.top },
    { x: rect.left, y: midY }, { x: right, y: midY },
    { x: rect.left, y: bottom }, { x: midX, y: bottom }, { x: right, y: bottom },
  ];
}

// Real-pointer acceptance evidence for the frozen product picker: eight-handle
// hit with per-direction cursors, hover invariance, click/drag no-confirm,
// exact resize geometry, screen-edge clamping and a tiny 4x4 probe. It only
// reuses the owned-window primitives and always returns to the exact manual
// selection so the shipped pixel assertions stay valid. Returned magnifier
// observations are cross-checked against the final BMP crop by the caller.
async function verifyManualHandleEvidence(app, overlay, desktop, button, gesture) {
  const appPid = app.child.pid;
  const overlayHandle = overlay.Handle;
  const desktopX = desktop.X;
  const desktopY = desktop.Y;
  const rect = { left: button.left + 4, top: button.top + 4,
    width: button.width - 8, height: button.height - 8 };
  assert(rect.width > 24 && rect.width % 2 === 0 && rect.height > 24 && rect.height % 2 === 0,
    'Handle evidence needs an even, comfortably sized manual selection.');
  const manualLabel = (width, height) => `手动 · ${width} × ${height} px · Enter`;
  const point = (x, y) => ({ AppPid: appPid, X: x, Y: y });
  const pickerVisible = async () => (await windows(appPid))
    .some((item) => item.Handle === overlayHandle && item.Visible);
  const readPickerLabel = () => native('selection', { AppPid: appPid, Handle: overlayHandle });
  const observations = [];
  const record = (event, data) => gesture.events.push({ event, ...data, observedAt: new Date().toISOString() });

  async function observe(hover, cursorShape, sample, context, tolerance = 0) {
    await native('mouse-move', point(hover.x, hover.y));
    await delay(150);
    const cursor = JSON.parse(await native('cursor'));
    assert(cursor.shown && Math.abs(cursor.x - hover.x) <= 1 && Math.abs(cursor.y - hover.y) <= 1,
      `OS cursor is not resting at ${hover.x},${hover.y}: ${JSON.stringify(cursor)}`);
    assert(cursor[cursorShape] === true,
      `${context} did not show the ${cursorShape} cursor: ${JSON.stringify(cursor)}`);
    const label = await native('magnifier', { AppPid: appPid, Handle: overlayHandle });
    const match = magnifierLabelPattern.exec(label);
    assert(match, `${context} magnifier label is not frozen-pixel evidence: ${JSON.stringify(label)}`);
    const observed = {
      context,
      pointer: { x: hover.x, y: hover.y },
      sample: { x: Number(match[1]), y: Number(match[2]) },
      hex: `#${match[3]}`,
      rgb: [Number(match[4]), Number(match[5]), Number(match[6])],
      cursor: { shape: cursorShape, handle: cursor.handle },
    };
    for (let channel = 0; channel < 3; channel++)
      assert.equal(parseInt(observed.hex.slice(1 + channel * 2, 3 + channel * 2), 16),
        observed.rgb[channel], `${context} magnifier hex and RGB channels disagree.`);
    assert(Math.abs(observed.sample.x - sample.x) <= tolerance &&
      Math.abs(observed.sample.y - sample.y) <= tolerance,
      `${context} magnifier sampled ${observed.sample.x},${observed.sample.y} ` +
      `instead of ${sample.x},${sample.y}.`);
    observations.push(observed);
    record('pointer-observe', observed);
    return observed;
  }

  async function dragHandle(from, to) {
    await native('mouse-move', point(from.x, from.y));
    await native('mouse-down', point(from.x, from.y));
    try { await native('mouse-move', point(to.x, to.y)); }
    finally { await native('mouse-up', point(to.x, to.y)); }
    await delay(150);
    return readPickerLabel();
  }

  // Eight-handle hit: every anchor switches the OS cursor to its resize
  // direction and snaps the magnifier to the frozen anchor pixel, while pure
  // hovering leaves the selection untouched.
  const anchors = manualHandleAnchors(rect);
  const inclusive = [
    { x: rect.left, y: rect.top }, { x: rect.left + rect.width / 2, y: rect.top },
    { x: rect.left + rect.width - 1, y: rect.top },
    { x: rect.left, y: rect.top + rect.height / 2 },
    { x: rect.left + rect.width - 1, y: rect.top + rect.height / 2 },
    { x: rect.left, y: rect.top + rect.height - 1 },
    { x: rect.left + rect.width / 2, y: rect.top + rect.height - 1 },
    { x: rect.left + rect.width - 1, y: rect.top + rect.height - 1 },
  ];
  for (let index = 0; index < anchors.length; index++)
    await observe({ x: anchors[index].x + 2, y: anchors[index].y + 2 },
      handleCursorShapes[index], inclusive[index], `handle-${index}`);
  const afterHover = await readPickerLabel();
  assert(afterHover.includes(manualLabel(rect.width, rect.height)),
    `Hovering the eight handles changed the selection: ${afterHover}`);

  // Outside both the handles and selection, the magnifier samples the pointer.
  const offHandle = { x: rect.left + rect.width / 2, y: rect.top - 20 };
  assert(offHandle.y > desktopY, 'Manual selection touches the desktop top edge.');
  const outsideObservation = await observe(offHandle, 'cross', offHandle, 'off-selection', 1);
  outsideObservation.crossCheck = false; // This frozen pixel is outside the final crop.

  // Pressing and releasing on a handle must never confirm the selection.
  await native('mouse-move', point(anchors[0].x + 2, anchors[0].y + 2));
  await native('mouse-down', point(anchors[0].x + 2, anchors[0].y + 2));
  assert(await pickerVisible(), 'A handle press closed the picker.');
  await native('mouse-up', point(anchors[0].x + 2, anchors[0].y + 2));
  await delay(150);
  assert(await pickerVisible(), 'A handle click confirmed the selection.');
  const afterHandleClick = await readPickerLabel();
  assert(afterHandleClick.includes(manualLabel(rect.width, rect.height)),
    `A handle click changed the selection: ${afterHandleClick}`);
  record('handle-click-no-confirm', { label: afterHandleClick });

  // One resize drag per cursor group. Exact resulting sizes prove each edge
  // moves only in its own direction and the drag never jumps at its start.
  let current = { ...rect };
  for (const step of [{ handle: 0, dx: -10, dy: -10 }, { handle: 1, dx: 0, dy: -8 },
    { handle: 4, dx: 8, dy: 0 }, { handle: 5, dx: -10, dy: 10 }]) {
    const anchor = manualHandleAnchors(current)[step.handle];
    const from = { x: anchor.x + 2, y: anchor.y + 2 };
    const observed = await dragHandle(from, { x: from.x + step.dx, y: from.y + step.dy });
    const mask = [5, 4, 6, 1, 2, 9, 8, 10][step.handle];
    const left = (mask & 1) !== 0 ? current.left + step.dx : current.left;
    const right = (mask & 2) !== 0 ? current.left + current.width + step.dx : current.left + current.width;
    const top = (mask & 4) !== 0 ? current.top + step.dy : current.top;
    const bottom = (mask & 8) !== 0 ? current.top + current.height + step.dy : current.top + current.height;
    assert(observed.includes(manualLabel(right - left, bottom - top)),
      `Handle ${step.handle} drag (${step.dx},${step.dy}) jumped or confirmed: ${observed}`);
    assert(await pickerVisible(), 'A handle drag confirmed the selection.');
    record('handle-drag', { handle: step.handle, dx: step.dx, dy: step.dy, label: observed });
    current = { left, top, width: right - left, height: bottom - top };
  }

  // Back to the exact manual selection for the remaining edge and tiny checks.
  await native('mouse-move', point(rect.left + 20, rect.top + 10));
  await native('mouse-right', point(rect.left + 20, rect.top + 10));
  await delay(150);
  const redrawn = await dragHandle({ x: rect.left, y: rect.top },
    { x: rect.left + rect.width, y: rect.top + rect.height });
  assert(redrawn.includes(manualLabel(rect.width, rect.height)),
    `Manual selection was not restored: ${redrawn}`);

  // Screen edge: resizing far past the desktop origin clamps at the physical
  // origin and the magnifier samples the exact frozen corner pixel. That pixel
  // lies outside the final crop, so it is excluded from the BMP cross-check.
  const origin = manualHandleAnchors(rect)[0];
  const edgeCorner = { x: desktopX + 2, y: desktopY + 2 };
  const edge = await dragHandle({ x: origin.x + 2, y: origin.y + 2 }, edgeCorner);
  assert(edge.includes(manualLabel(origin.x + rect.width - desktopX,
    origin.y + rect.height - desktopY)),
    `Screen-edge resize did not clamp to the desktop origin: ${edge}`);
  record('screen-edge-drag', { label: edge });
  const edgeObservation = await observe(edgeCorner, 'sizeNWSE',
    { x: desktopX, y: desktopY }, 'screen-edge-corner');
  edgeObservation.crossCheck = false;
  const edgeRestored = await dragHandle(edgeCorner, { x: origin.x + 2, y: origin.y + 2 });
  assert(edgeRestored.includes(manualLabel(rect.width, rect.height)),
    `Screen-edge restore drifted: ${edgeRestored}`);
  record('screen-edge-restore', { label: edgeRestored });

  // Tiny 4x4 selection: all eight handles overlap; the nearest-anchor rule
  // still switches between the two inclusive corner pixels deterministically.
  await native('mouse-move', point(rect.left + 20, rect.top + 10));
  await native('mouse-right', point(rect.left + 20, rect.top + 10));
  await delay(150);
  const tx = rect.left + 20, ty = rect.top + 10;
  const tinySeed = await dragHandle({ x: tx, y: ty }, { x: tx + 20, y: ty + 20 });
  assert(tinySeed.includes(manualLabel(20, 20)), `Tiny seed selection failed: ${tinySeed}`);
  const tinyNarrow = await dragHandle({ x: tx + 22, y: ty + 12 }, { x: tx + 6, y: ty + 12 });
  assert(tinyNarrow.includes(manualLabel(4, 20)), `Tiny width shrink failed: ${tinyNarrow}`);
  const tinyShort = await dragHandle({ x: tx + 2, y: ty + 22 }, { x: tx + 2, y: ty + 6 });
  assert(tinyShort.includes(manualLabel(4, 4)), `Tiny height shrink failed: ${tinyShort}`);
  record('tiny-selection', { labels: [tinySeed, tinyNarrow, tinyShort] });
  await observe({ x: tx, y: ty }, 'sizeNWSE', { x: tx, y: ty }, 'tiny-top-left');
  await observe({ x: tx + 4, y: ty + 4 }, 'sizeNWSE', { x: tx + 3, y: ty + 3 }, 'tiny-bottom-right');

  // Restore the exact manual selection so the shipped pixel assertions hold.
  await native('mouse-move', point(rect.left + 20, rect.top + 10));
  await native('mouse-right', point(rect.left + 20, rect.top + 10));
  await delay(150);
  const restored = await dragHandle({ x: rect.left, y: rect.top },
    { x: rect.left + rect.width, y: rect.top + rect.height });
  assert(restored.includes(manualLabel(rect.width, rect.height)),
    `Manual selection was not restored after the tiny probe: ${restored}`);
  record('manual-restored', { label: restored });
  return observations;
}

async function captureThroughHotkey(app, fixture, evidenceRoot, evidence, manual = false) {
  let page = app.page;
  const toolbarHandles = (await windows(app.child.pid))
    .filter((item) => item.Visible && item.Handle !== app.main.Handle && area(item) > 1000)
    .map((item) => item.Handle);
  assert(toolbarHandles.length > 0, 'Enabled toolbar has no visible native window.');
  await watchScreenshotRevision(page);
  await native('hide', { AppPid: app.child.pid, Handle: app.main.Handle });
  assert(!(await windows(app.child.pid)).find((item) =>
    item.Handle === app.main.Handle && item.Visible), 'Main app window did not hide.');
  const focusX = fixture.editRect.left + Math.floor(fixture.editRect.width / 2);
  const focusY = fixture.editRect.top + Math.floor(fixture.editRect.height / 2);
  await native('focus-fixture', {
    FixturePid: fixture.pid, Handle: fixture.root, X: focusX, Y: focusY,
  });
  const fixtureFrame = path.join(evidenceRoot, manual ? 'manual-fixture-button.png' : 'fixture-button.png');
  const pixelEvidence = { mode: manual ? 'manual' : 'smart', fixtureFrame,
    buttonGeometry: JSON.parse(await native('geometry', { FixturePid: fixture.pid, Handle: fixture.button })),
    rootGeometry: JSON.parse(await native('geometry', { FixturePid: fixture.pid, Handle: fixture.root })),
    observedAt: new Date().toISOString() };
  (evidence.capturePixels ??= []).push(pixelEvidence);
  await native('frame', { FixturePid: fixture.pid, Handle: fixture.button,
    X: fixture.buttonRect.left, Y: fixture.buttonRect.top,
    Width: fixture.buttonRect.width, Height: fixture.buttonRect.height,
    EvidenceRoot: evidenceRoot, OutputPath: fixtureFrame });
  pixelEvidence.fixturePixels = await pngPixels(page, fs.readFileSync(fixtureFrame));
  const sentAt = Date.now();
  await native('hotkey', { FixturePid: fixture.pid });
  evidence.hotkeyDeliveredAt = Date.now() - sentAt;

  const button = fixture.buttonRect;
  const x = button.left + Math.floor(button.width / 2);
  const y = button.top + Math.floor(button.height / 2);
  evidence.smartQueryFixture = {
    rootRect: fixture.rootRect, buttonRect: fixture.buttonRect, editRect: fixture.editRect,
    activationPointer: { x: focusX, y: focusY }, requestedHoverPointer: { x, y },
    observedAt: new Date().toISOString(),
  };
  const overlays = await waitForWindows(app.child.pid,
    (item) => item.Visible && item.Handle !== app.main.Handle &&
      area(item) > fixture.rootRect.width * fixture.rootRect.height * 2 &&
      inside(item, x, y), 12000);
  const overlay = overlays.sort((a, b) => area(b) - area(a))[0];
  pixelEvidence.overlayGeometry = JSON.parse(await native('geometry', {
    FixturePid: app.child.pid, Handle: overlay.Handle,
  }));
  const client = pixelEvidence.overlayGeometry.client;
  assert.deepEqual(client, { X: fixture.backgroundRect.left, Y: fixture.backgroundRect.top,
    Width: fixture.backgroundRect.width, Height: fixture.backgroundRect.height },
    'The picker client must cover the frozen desktop without inset or scaling.');
  assert(overlay.Bounds.Left <= client.X && overlay.Bounds.Top <= client.Y &&
    overlay.Bounds.Right >= client.X + client.Width &&
    overlay.Bounds.Bottom >= client.Y + client.Height,
    'The picker frame must contain its desktop-aligned client.');
  const duringCapture = await windows(app.child.pid);
  assert(!duringCapture.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Edit hotkey exposed the main window during selection.');
  assert(!duringCapture.some((item) => toolbarHandles.includes(item.Handle) && item.Visible),
    'Toolbar or sensor remained visible during capture.');
  evidence.overlayProbe = JSON.parse(await native('probe', {
    AppPid: app.child.pid, FixturePid: fixture.pid, X: x, Y: y,
  }));
  let selectedRegion = button;
  const gesture = { mode: manual ? 'manual-drag-resize-click' : 'smart-click', events: [] };
  pixelEvidence.gesture = gesture;
  if (manual) {
    const start = { AppPid: app.child.pid, X: button.left + 4, Y: button.top + 4 };
    const end = { AppPid: app.child.pid, X: button.right - 4, Y: button.bottom - 4 };
    await native('mouse-move', start);
    gesture.events.push({ event: 'manual-start-cursor', cursor: JSON.parse(await native('cursor')) });
    await native('mouse-down', start);
    try { await native('mouse-move', end); }
    finally { await native('mouse-up', end); }
    const dragLabel = await native('selection', { AppPid: app.child.pid, Handle: overlay.Handle });
    assert(dragLabel.includes(`手动 · ${button.width - 8} × ${button.height - 8} px`),
      `First drag was confirmed or selected different pixels: ${dragLabel}`);
    gesture.events.push({ event: 'drag-released', label: dragLabel, observedAt: new Date().toISOString() });
    gesture.manualHandleEvidence = await verifyManualHandleEvidence(app, overlay, client, button, gesture);
    const handle = { ...end, X: end.X + 3, Y: end.Y + 3 };
    const resized = { ...end, X: end.X + 13, Y: end.Y + 13 };
    await native('mouse-move', handle);
    await native('mouse-down', handle);
    try { await native('mouse-move', resized); }
    finally { await native('mouse-up', resized); }
    const resizeLabel = await native('selection', { AppPid: app.child.pid, Handle: overlay.Handle });
    assert(resizeLabel.includes(`手动 · ${button.width + 2} × ${button.height + 2} px`),
      `Offset handle resize jumped or confirmed: ${resizeLabel}`);
    gesture.events.push({ event: 'resize-released', label: resizeLabel, observedAt: new Date().toISOString() });
    selectedRegion = { left: start.X, top: start.Y, width: button.width + 2, height: button.height + 2 };
  } else {
    await native('hover', { AppPid: app.child.pid, X: x, Y: y });
  }
  // The production UIA query has a 200 ms budget. One bounded settle period
  // lets the real overlay consume it; no retry or synthetic command injection.
  await delay(350);
  assert.equal(await taskbarState(), evidence.taskbarStateBefore,
    'Screenshot selection changed the Windows taskbar preference.');
  const selectionStates = [];
  if (!manual) {
    for (let index = 0; index < 8; index++) {
      const label = await native('selection', { AppPid: app.child.pid, Handle: overlay.Handle });
      selectionStates.push(label);
      if (index > 0 && label.includes(` · ${button.width} × ${button.height} px · Enter`)) break;
      await native('tab', { AppPid: app.child.pid });
    }
    evidence.smartSelectionStates = selectionStates;
    assert(selectionStates.at(-1)?.includes(` · ${button.width} × ${button.height} px · Enter`),
      `Smart picker did not expose the synthetic button: ${JSON.stringify(selectionStates)}`);
  }
  const click = { AppPid: app.child.pid,
    X: selectedRegion.left + Math.floor(selectedRegion.width / 2),
    Y: selectedRegion.top + Math.floor(selectedRegion.height / 2) };
  // Smart Tab selection keeps the current mouse point; moving it would select a new candidate.
  if (manual) await native('mouse-move', click);
  await native('mouse-down', click);
  try {
    assert((await windows(app.child.pid)).some((item) => item.Handle === overlay.Handle && item.Visible),
      'A press confirmed before release.');
    gesture.events.push({ event: 'click-pressed', observedAt: new Date().toISOString() });
  } finally { await native('mouse-up', click); }
  gesture.events.push({ event: 'click-released', observedAt: new Date().toISOString() });
  // 普通截图先固化选区，Enter 才进入编辑；单击不得再自动跳过动作工具栏。
  assert((await windows(app.child.pid)).some((item) => item.Handle === overlay.Handle && item.Visible),
    'Selection click skipped the action toolbar.');
  await native('enter', { AppPid: app.child.pid });

  // The screenshot editor has its own WebView; the workbench stays hidden.
  const sceneDeadline = Date.now() + 12000;
  while (Date.now() < sceneDeadline) {
    const candidate = app.browser.contexts().flatMap(context => context.pages())
      .find(item => item !== app.page && item.url().includes('#/imageEdit?scene=1'));
    if (candidate) { page = candidate; break; }
    await delay(100);
  }
  assert(page !== app.page, 'Screenshot scene WebView did not appear.');
  await page.locator('html[data-capture-scene="true"]')
    .waitFor({ state: 'attached', timeout: 12000 });
  await watchScreenshotRevision(page);
  // Bootstrap may have completed before CDP attached to the scene. Carry only
  // the actual native state already observed by the shared workbench host.
  await page.evaluate(state => { window.__nativeActionsEvidence = state; },
    await app.page.evaluate(() => window.__nativeActionsEvidence));
  const editor = page.getByLabel('图片检查画布');
  await editor.waitFor({ timeout: 12000 });
  await page.waitForFunction(() => {
    const canvas = document.querySelector('canvas[aria-label="图片检查画布"]');
    if (!canvas) return false;
    const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    let changed = 0;
    for (let i = 0; i < data.length; i += 40)
      if (data[i] !== 22 || data[i + 1] !== 22 || data[i + 2] !== 22) changed++;
    return changed > 100;
  }, null, { timeout: 12000 });
  const restoredWindows = await windows(app.child.pid);
  assert(!restoredWindows.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Completed edit capture forced the hidden workbench to appear.');
  assert(toolbarHandles.every((handle) => restoredWindows.some((item) =>
    item.Handle === handle && item.Visible)), 'Toolbar did not resume after capture.');
  await page.waitForFunction(() => typeof window.__nativeActionsEvidence?.inputUrl === 'string',
    null, { timeout: 5000 });
  const inputUrl = await page.evaluate(() => window.__nativeActionsEvidence.inputUrl);
  const resource = new URL(inputUrl);
  assert(resource.origin === 'https://app.vibeocr' && resource.pathname.startsWith('/__resource/'),
    'Screenshot input is not the product resource observed from native state.');
  const input = await page.evaluate(async (url) => {
    const response = await fetch(url);
    if (!response.ok) throw new Error(`Screenshot resource returned ${response.status}`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.length > 1024 * 1024) throw new Error('Synthetic screenshot resource exceeded 1 MiB');
    return { mediaType: response.headers.get('content-type'), bytes: Array.from(bytes) };
  }, inputUrl);
  assert.equal(input.mediaType, 'image/bmp', 'Native screenshot resource is not BMP.');
  const bmp = Buffer.from(input.bytes);
  pixelEvidence.originalBmp = path.join(evidenceRoot, manual ? 'manual-original.bmp' : 'original.bmp');
  fs.writeFileSync(pixelEvidence.originalBmp, bmp);
  assert(bmp.length >= 54 && bmp.toString('ascii', 0, 2) === 'BM' &&
    bmp.readInt16LE(28) === 32 && bmp.readInt32LE(30) === 0, 'Expected product 32-bit RGB BMP.');
  const width = bmp.readInt32LE(18), height = Math.abs(bmp.readInt32LE(22));
  const offset = bmp.readUInt32LE(10);
  assert.equal(width, selectedRegion.width);
  assert.equal(height, selectedRegion.height);
  assert.equal(bmp.length - offset, width * height * 4);
  let black = 0, dark = 0, light = 0;
  for (let i = offset; i < bmp.length; i += 4) {
    if (bmp[i] === 0 && bmp[i + 1] === 0 && bmp[i + 2] === 0) black++;
    if (bmp[i] < 100 && bmp[i + 1] < 100 && bmp[i + 2] < 100) dark++;
    if (bmp[i] > 170 && bmp[i + 1] > 170 && bmp[i + 2] > 170) light++;
  }
  pixelEvidence.rawBmpPixels = { width, height, black, dark, light };
  pixelEvidence.decodedBmpPixels = await pngPixels(page, bmp, 'image/bmp');
  if (manual && gesture.manualHandleEvidence) {
    // The magnifier reads the same frozen desktop array the crop came from, so
    // every in-crop observation must match the shipped BMP byte for byte.
    const cropLeft = selectedRegion.left, cropTop = selectedRegion.top;
    const checkable = gesture.manualHandleEvidence
      .filter((item) => item.crossCheck !== false);
    assert.deepEqual(gesture.manualHandleEvidence.filter((item) => item.crossCheck === false)
      .map((item) => item.context), ['off-selection', 'screen-edge-corner'],
    'Only the off-selection and screen-edge observations may be outside the crop.');
    for (const observed of checkable) {
      const px = observed.sample.x - cropLeft, py = observed.sample.y - cropTop;
      assert(px >= 0 && py >= 0 && px < width && py < height,
        `Magnifier sample escaped the final crop: ${JSON.stringify(observed)}`);
      const index = offset + (py * width + px) * 4;
      const cropped = `#${[bmp[index + 2], bmp[index + 1], bmp[index]]
        .map((channel) => channel.toString(16).toUpperCase().padStart(2, '0')).join('')}`;
      assert.equal(observed.hex, cropped,
        `Magnifier pixel differs from the cropped BMP at ${observed.sample.x},${observed.sample.y}.`);
    }
    pixelEvidence.magnifierCrossChecks = checkable.length;
  }
  const before = await canvasOrange(page);
  pixelEvidence.canvasBefore = before;
  assert(before.nonBackground > 1000, 'Real screenshot image never decoded in WebView2.');
  await page.getByRole('button', { name: '矩形', exact: true }).click();
  // The stroke color defaults to red #e02020; select the public orange swatch
  // explicitly (same as the demand-tools drawRectangle) for the orange checks.
  await page.getByRole('button', { name: '常用颜色 #f38b35', exact: true }).click();
  await page.getByRole('combobox', { name: '线条宽度' }).selectOption('8');
  const box = await editor.boundingBox();
  // A CSS size floor would reject the legitimate 140x42 crop at 175% DPI;
  // verify the exact physical→CSS mapping of the first frame instead.
  pixelEvidence.canvasLayout = assertCaptureCanvasLayout({
    box,
    selection: selectedRegion,
    desktopClient: client,
    viewport: await page.evaluate(() => ({ width: innerWidth, height: innerHeight })),
  });
  await page.mouse.move(box.x + box.width * 0.35, box.y + box.height * 0.45);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.65, box.y + box.height * 0.55);
  await page.mouse.up();
  // Host revision acknowledgement and the canvas frame complete independently.
  await page.waitForFunction((baseline) => {
    if (!(window.__nativeActionsEvidence?.revision >= 1)) return false;
    const element = document.querySelector('canvas[aria-label="图片检查画布"]');
    if (!element) return false;
    const { data } = element.getContext('2d').getImageData(0, 0, element.width, element.height);
    let orange = 0;
    for (let i = 0; i < data.length; i += 4)
      if (data[i] > 210 && data[i + 1] > 90 && data[i + 1] < 190 && data[i + 2] < 110) orange++;
    return orange > baseline + 50;
  }, before.orange, { timeout: 5000 });
  const after = await canvasOrange(page);
  assert(after.orange > before.orange + 50, 'Real WebView2 rectangle edit changed too few pixels.');

  // Scene-terminal copy: a successful 复制标注图 closes the session and
  // retires this WebView, so all page reads (revision, editor screenshot)
  // happen before the click and the copy proof is Node-held only. The
  // pre-click passive observer keeps every upload, the public copy command,
  // receipts and the close; the copy upload is selected by the command's
  // resourceUri, so the automatic text-layer upload never stands in for the
  // copied pixels.
  const revision = await page.evaluate(() => window.__nativeActionsEvidence);
  assert(revision?.sessionId && revision.revision >= 1,
    'Host screenshot session revision did not advance after editing.');
  await page.screenshot({ path: path.join(evidenceRoot, manual ? 'manual-screenshot-editor.png' : 'screenshot-editor.png') });
  const terminal = await observeSceneCopyTerminal(page);
  pixelEvidence.copyTimeline = terminal.timeline;
  const commandDeadline = raceDeadline(10000);
  const commandObserved = Promise.race([terminal.received.then(() => true), commandDeadline.promise]);
  let png;
  let uploadBytes;
  try {
    const clickError = await page.getByRole('button', { name: '复制标注图' })
      .click()
      .then(() => null, (error) => `${error.name}: ${error.message}`);
    if (clickError) terminal.timeline.clickError = clickError;
    assert(await commandObserved,
      `No public copyScreenshotImage command within 10 seconds: ${JSON.stringify(terminal.timeline)}`);
    const endDeadline = raceDeadline(10000);
    let ended;
    try {
      ended = await Promise.race([terminal.ended.then(() => true), endDeadline.promise]);
    } finally { endDeadline.clear(); }
    if (!ended) terminal.timeline.noTerminalEvent = true;
    settleSceneCopyTimeline(terminal.timeline);
    const command = terminal.timeline.copyCommands[0];
    const upload = terminal.timeline.uploads.find(
      (item) => item.resourceUri === command.resourceUri);
    // The immutable Blob evidence of exactly that upload is pushed to Node by
    // the pre-click binding at fetch time; the scene is never evaluated after
    // the click, so even an instant close keeps the pixels in Node.
    const blobDeadline = raceDeadline(5000);
    let observed;
    try {
      observed = await Promise.race([terminal.waitForUploadBlob(upload.seq), blobDeadline.promise]);
    } finally { blobDeadline.clear(); }
    if (!observed) observed = { mimeType: null, byteLength: null, bytes: null,
      problem: `copy upload Blob never reached Node: ${JSON.stringify(terminal.timeline)}` };
    assert.equal(observed.problem ?? null, null,
      `Product Blob observation failed: ${JSON.stringify(observed)}; timeline=${JSON.stringify(terminal.timeline)}.`);
    assert.equal(observed.mimeType, 'image/png', 'Product upload Blob was not image/png.');
    assert(Array.isArray(observed.bytes) && observed.bytes.length === observed.byteLength,
      'Product upload Blob size changed during observation.');
    png = Buffer.from(observed.bytes);
    uploadBytes = observed.byteLength;
    assert(png.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])),
      `Product upload Blob is not PNG; bytes=${uploadBytes}.`);
  } finally {
    commandDeadline.clear();
    await page.evaluate(() => window.__nativeActionsRestoreFetch?.()).catch(() => {});
  }
  fs.writeFileSync(path.join(evidenceRoot, manual ? 'manual-edited.png' : 'synthetic-edited.png'), png);
  // Decode on the still-alive main workbench page; the scene is closed now.
  const pixels = await pngPixels(app.page, png);
  pixelEvidence.editedPngPixels = pixels;
  assert.equal(pixels.width, selectedRegion.width, 'Final crop differs from the selected physical width.');
  assert.equal(pixels.height, selectedRegion.height, 'Final crop differs from the selected physical height.');
  assert(pixels.orange > 20 && pixels.dark > 10 && pixels.light > 100,
    `Synthetic PNG pixels are incomplete: ${JSON.stringify(pixels)}`);
  assert(!(await windows(app.child.pid)).some(item =>
    item.Handle === app.main.Handle && item.Visible),
  'Closing the screenshot scene forced the hidden workbench to appear.');
  return { hotkey, overlayHandle: overlay.Handle,
    copyClosedScene: true, copyTimeline: terminal.timeline,
    mainHiddenAfterSelection: true,
    mainHiddenDuringSelection: true, toolbarHiddenDuringSelection: true,
    background: fixture.backgroundRect, button, selectedRegion, gesture, canvasBefore: before,
    canvasAfter: after, png: pixels, productUploadBytes: uploadBytes,
    sessionId: revision.sessionId,
    revision: revision.revision };
}

// The explicit 结束会话 keeps its own coverage on a fresh, independent
// screenshot session: the annotated captures above end with the
// scene-terminal copy, so the public end button is proven here instead.
async function explicitEndSessionCoverage(app, fixture) {
  const appPid = app.child.pid;
  const toolbarHandles = (await windows(appPid))
    .filter((item) => item.Visible && item.Handle !== app.main.Handle && area(item) > 1000)
    .map((item) => item.Handle);
  assert(toolbarHandles.length > 0, 'Enabled toolbar has no visible native window.');
  await native('hide', { AppPid: appPid, Handle: app.main.Handle });
  await native('focus-fixture', {
    FixturePid: fixture.pid, Handle: fixture.root,
    X: fixture.editRect.left + Math.floor(fixture.editRect.width / 2),
    Y: fixture.editRect.top + Math.floor(fixture.editRect.height / 2),
  });
  await native('hotkey', { FixturePid: fixture.pid });
  const button = fixture.buttonRect;
  const x = button.left + Math.floor(button.width / 2);
  const y = button.top + Math.floor(button.height / 2);
  const overlays = await waitForWindows(appPid, (item) => item.Visible &&
    item.Handle !== app.main.Handle &&
    area(item) > fixture.rootRect.width * fixture.rootRect.height * 2 &&
    inside(item, x, y), 12000);
  const overlay = overlays.sort((a, b) => area(b) - area(a))[0];
  const start = { AppPid: appPid, X: button.left + 4, Y: button.top + 4 };
  const end = { AppPid: appPid, X: button.right - 4, Y: button.bottom - 4 };
  await native('mouse-move', start);
  await native('mouse-down', start);
  try { await native('mouse-move', end); }
  finally { await native('mouse-up', end); }
  const dragLabel = await native('selection', { AppPid: appPid, Handle: overlay.Handle });
  assert(dragLabel.includes(`手动 · ${button.width - 8} × ${button.height - 8} px`),
    `End-session seed selection was not the manual drag: ${dragLabel}`);
  const click = { AppPid: appPid,
    X: button.left + 4 + Math.floor((button.width - 8) / 2),
    Y: button.top + 4 + Math.floor((button.height - 8) / 2) };
  await native('mouse-move', click);
  await native('mouse-down', click);
  try {
    assert((await windows(appPid)).some((item) => item.Handle === overlay.Handle && item.Visible),
      'A press confirmed the end-session selection before release.');
  } finally { await native('mouse-up', click); }
  await native('enter', { AppPid: appPid });
  const deadline = Date.now() + 12000;
  let scene = null;
  while (Date.now() < deadline && !scene) {
    scene = app.browser.contexts().flatMap((context) => context.pages())
      .find((item) => item !== app.page && !item.isClosed() &&
        item.url().includes('#/imageEdit?scene=1'));
    if (!scene) await delay(100);
  }
  assert(scene, 'End-session scene WebView did not appear.');
  await scene.locator('html[data-capture-scene="true"]')
    .waitFor({ state: 'attached', timeout: 12000 });
  await scene.getByLabel('图片检查画布').waitFor({ timeout: 12000 });
  const duringScene = await windows(appPid);
  assert(!duringScene.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Completed edit capture forced the hidden workbench to appear.');
  const sceneClosed = scene.waitForEvent('close', { timeout: 10000 });
  await scene.getByRole('button', { name: '结束会话', exact: true }).click();
  await sceneClosed;
  const after = await windows(appPid);
  assert(!after.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Closing the screenshot scene forced the hidden workbench to appear.');
  assert(toolbarHandles.every((handle) => after.some((item) =>
    item.Handle === handle && item.Visible)), 'Toolbar did not resume after the explicit end.');
  return { sceneClosed: true, mainHiddenAfterEnd: true,
    toolbarRestoredAfterEnd: true, selectionLabel: dragLabel };
}

async function verifyRecognitionHotkeyGuard(app, fixture) {
  const appPid = app.child.pid;
  const mainHandle = app.main.Handle;
  const focusX = fixture.editRect.left + Math.floor(fixture.editRect.width / 2);
  const focusY = fixture.editRect.top + Math.floor(fixture.editRect.height / 2);
  const buttonX = fixture.buttonRect.left + Math.floor(fixture.buttonRect.width / 2);
  const buttonY = fixture.buttonRect.top + Math.floor(fixture.buttonRect.height / 2);
  const isOverlay = (item) => item.Visible && item.Handle !== mainHandle &&
    area(item) > fixture.rootRect.width * fixture.rootRect.height * 2 &&
    inside(item, buttonX, buttonY);

  await native('hide', { AppPid: appPid, Handle: mainHandle });
  assert(!(await windows(appPid)).find((item) => item.Handle === mainHandle && item.Visible),
    'Main window did not hide before recognition hotkey.');
  await native('focus-fixture', {
    FixturePid: fixture.pid, Handle: fixture.root, X: focusX, Y: focusY,
  });
  await native('recognize-hotkey', { ForegroundPid: fixture.pid });
  const overlays = await waitForWindows(appPid, isOverlay, 12000);
  assert.equal(overlays.length, 1, 'First recognition hotkey opened multiple overlays.');
  const overlay = overlays[0];
  assert(!(await windows(appPid)).find((item) => item.Handle === mainHandle && item.Visible),
    'Recognition hotkey exposed the main window before selection completed.');
  assert.equal(Number(await native('foreground', { AppPid: appPid })), overlay.Handle,
    'Recognition overlay did not retain foreground focus.');

  // The duplicate is a real WM_HOTKEY while the production picker owns focus.
  await native('recognize-hotkey', { ForegroundPid: appPid });
  await delay(350);
  const afterDuplicate = await windows(appPid);
  assert(!(afterDuplicate.find((item) => item.Handle === mainHandle && item.Visible)),
    'Rejected duplicate recognition hotkey reactivated the main window.');
  assert.deepEqual(afterDuplicate.filter(isOverlay).map((item) => item.Handle),
    [overlay.Handle], 'Rejected duplicate recognition hotkey opened or replaced the overlay.');
  assert.equal(Number(await native('foreground', { AppPid: appPid })), overlay.Handle,
    'Rejected duplicate recognition hotkey stole overlay foreground focus.');

  // The first Esc may leave an intelligent preview for manual selection;
  // the second must cancel that same picker. No OCR is ever submitted.
  await native('escape', { AppPid: appPid });
  let stillOpen = true;
  const firstEscapeDeadline = Date.now() + 1200;
  while (Date.now() < firstEscapeDeadline) {
    stillOpen = (await windows(appPid)).some((item) =>
      item.Handle === overlay.Handle && item.Visible);
    if (!stillOpen) break;
    await delay(100);
  }
  if (stillOpen)
    await native('escape', { AppPid: appPid });
  // 工具栏优先后：Esc 取消不再强制显示主工作台（与纯截图取消同一契约）；
  // 选区窗口关闭且主窗保持隐藏即通过，由冒烟自行恢复主窗可见性。
  const cancelDeadline = Date.now() + 12000;
  let overlayClosed = false;
  while (Date.now() < cancelDeadline) {
    const current = await windows(appPid);
    overlayClosed = !current.some((item) => item.Handle === overlay.Handle && item.Visible);
    if (overlayClosed) break;
    await delay(180);
  }
  if (!overlayClosed)
    throw new Error('Recognition overlay did not close after Esc.');
  assert(!(await windows(appPid)).some((item) => item.Handle === mainHandle && item.Visible),
    'Cancelled recognition hotkey force-showed the hidden workbench.');
  await native('restore', { AppPid: appPid, Handle: mainHandle });
  await waitForWindows(appPid, (item) => item.Handle === mainHandle && item.Visible, 12000);
  return { hotkey: recognizeHotkey, overlayHandle: overlay.Handle,
    duplicateOverlayCount: afterDuplicate.filter(isOverlay).length,
    mainStayedHidden: true, foregroundStayedOnOverlay: true,
    cancelledBeforeSelection: true, mainRestoredBySmoke: true };
}

async function main() {
  assert.equal(process.platform, 'win32', 'Native actions smoke requires Windows.');
  const source = fs.realpathSync(option('--product-root'));
  const work = fs.realpathSync(option('--work-root'));
  assert(!contained(source, work) && !contained(work, source),
    'ProductRoot and WorkRoot must not nest.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
    'runtime/backend/runtime-manifest.json', 'runtime/env/runtime-env/python.exe'])
    assert(fs.statSync(path.join(source, marker)).isFile(), `Candidate marker missing: ${marker}`);
  assert(!fs.existsSync(path.join(source, 'state')),
    'Source candidate already has state; refusing to copy it.');
  assert(fs.existsSync(nativeScript), 'Synthetic fixture script is missing.');
  const smokeRoot = path.join(work, `vibeocr-native-actions-${crypto.randomUUID()}`);
  const candidate = path.join(smokeRoot, 'candidate');
  const webviewData = path.join(smokeRoot, 'webview2');
  const instanceId = crypto.randomUUID().replaceAll('-', '');
  fs.mkdirSync(smokeRoot);
  fs.cpSync(source, candidate, { recursive: true, errorOnExist: true, force: false });
  let app, fixture;
  const evidence = { schema_version: 1, state: 'failed', smokeRoot, appPids: [] };
  evidence.taskbarStateBefore = await taskbarState();
  try {
    app = await launchApp(candidate, webviewData, instanceId);
    evidence.appPids.push(app.child.pid);
    await configure(app, evidence);
    const firstMain = app.main.Handle;
    await closeAppNormally(app.child, { AppPid: app.child.pid, Handle: firstMain });
    await app.browser.close().catch(() => {});
    app = await launchApp(candidate, webviewData, instanceId);
    evidence.appPids.push(app.child.pid);
    await verifyRestart(app.page);
    await app.page.screenshot({ path: path.join(smokeRoot, 'native-actions-settings.png') });
    fixture = await startFixture();
    evidence.capture = await captureThroughHotkey(app, fixture, smokeRoot, evidence);
    evidence.manualCapture = await captureThroughHotkey(app, fixture, smokeRoot, evidence, true);
    // Both captures above end with the scene-terminal copy; the explicit end
    // button keeps its own independent fresh-session coverage.
    evidence.explicitEndSession = await explicitEndSessionCoverage(app, fixture);
    evidence.recognitionHotkeyGuard = await verifyRecognitionHotkeyGuard(app, fixture);
    await openSettings(app.page);
    await setCheckbox(app.page, '启用悬浮工具栏', false);
    await toolbarStatus(app.page, '已关闭');
    evidence.fixturePid = fixture.pid;
    evidence.taskbarStateAfter = await taskbarState();
    assert.equal(evidence.taskbarStateAfter, evidence.taskbarStateBefore,
      'Screenshot completion or cancellation changed the Windows taskbar preference.');
    await closeAppNormally(app.child, { AppPid: app.child.pid, Handle: app.main.Handle });
    evidence.state = 'passed';
    console.log(`Native actions E2E passed; synthetic evidence: ${smokeRoot}`);
  } catch (error) {
    evidence.error = `${error.name}: ${error.message}`;
    if (app?.child.exitCode === null) {
      try {
        evidence.recordingDOM = await app.page.evaluate(() => ({
          events: window.__hotkeyRecordingEvents ?? [],
          active: document.activeElement?.outerHTML,
          rows: [...document.querySelectorAll('.hotkey-action-row')].map(row => ({
            text: row.innerText, value: row.querySelector('input')?.value,
          })),
        }));
        await app.page.screenshot({ path: path.join(smokeRoot, 'native-actions-failure.png') });
      } catch (diagnosticError) {
        evidence.diagnosticError = diagnosticError.message;
      }
    }
    throw error;
  } finally {
    const cleanupErrors = [];
    if (fixture) {
      try { await stopOwned(fixture.child, 'quit', {
        FixturePid: fixture.pid, Handle: fixture.root, ThreadId: fixture.threadId,
      }); } catch (error) { cleanupErrors.push(error); }
    }
    if (app) {
      try { await stopOwned(app.child, 'close', {
          AppPid: app.child.pid, Handle: app.main.Handle,
      }); } catch (error) { cleanupErrors.push(error); }
      try { await app.browser.close(); } catch { /* app may already have closed WebView2 */ }
    }
    if (cleanupErrors.length) {
      evidence.state = 'failed';
      evidence.cleanupError = cleanupErrors.map((error) => error.message).join('; ');
    }
    fs.writeFileSync(path.join(smokeRoot, 'native-actions-health.json'),
      JSON.stringify(evidence, null, 2));
    if (cleanupErrors.length) throw cleanupErrors[0];
  }
}

export { native, windows, waitForWindows, area, inside, delay, startFixture,
  launchApp, openSettings, toolbarStatus, setCheckbox, stopOwned, closeAppNormally, configure,
  captureThroughHotkey, taskbarState, pngPixels, focusRecorder,
  assertCaptureCanvasLayout, settleSceneCopyTimeline, observeSceneCopyTerminal,
  raceDeadline };

if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(`Native actions E2E failed: ${error.name}: ${error.message}`);
    process.exitCode = 1;
  });
}

