// Run only on an explicitly authorized interactive Windows desktop. This script
// drives the shipped WinUI/WebView2 application with real owned input, a real
// WM_MOUSEWHEEL scroll on its own synthetic window, and UIA limited to the app
// PID. It never performs a desktop-wide UIA text scan and never touches real
// user data: everything lives inside an isolated WorkRoot candidate copy.
// The optional --prepared-candidate <root> reuses one retained managed-
// environment candidate (real Runtime, no --shell-only) and additionally
// verifies the real in-place text layer and explicit recognition through
// public buttons and public app.state broadcasts only.
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
import { native as pickerNative, stopOwned, observeSceneCopyTerminal,
  settleSceneCopyTimeline, collectSceneCopyUploadBlob, raceDeadline, setClipboardImage,
  readbackSceneCopyClipboard, decodeImageRgba } from './smoke_native_actions.mjs';

const execFileAsync = promisify(execFile);
const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const requireWebAssets = createRequire(path.resolve(scriptDir,
  '../src/dotnet/VibeOCR.App/WebAssets/package.json'));
const { chromium, expect } = requireWebAssets('@playwright/test');
const fixtureScript = path.join(scriptDir, 'scroll_capture_fixture.ps1');
const selectionInset = 16;
const previewPauseMs = 5500; // Preview wait must not consume the dynamic-content window.
const stableWaitMs = 1350; // >= 1.3 s settle between start/wheel/finish.
const wheelSteps = 3;
// Prepared-candidate mode keeps the retained Runtime state, so the app starts
// without --shell-only and real OCR runs. Both waits stay bounded; they only
// need to cover the Supervisor cold start plus one inference each.
const textLayerTimeoutMs = 300000; // Real text-layer preparation over the final PNG.
const recognitionTimeoutMs = 900000; // Real explicit OCR result over the same PNG.
// Public-state preflight before 取字: the engine catalog is only published
// after the Supervisor attached; no retry loop and no extra OCR request.
const supervisorReadyTimeoutMs = 180000;
const runtimePollMs = 1000;
// Controller negatives: must exceed the product DynamicContentTimeoutMs (5 s)
// so the animated scenario really trips its own timeout, not our poll budget.
const controllerErrorTimeoutMs = 20000;
// Proof against recognizing only a last ~305 px single frame: the stitched
// document encodes rows 0000-0018, so row 0003 only exists near the top and
// 0016 only near the bottom of the real long image.
const requiredTopRow = 3;
const requiredBottomRow = 16;
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function option(name) {
  const index = process.argv.indexOf(name);
  assert(index > 0 && index + 1 < process.argv.length,
    `Required option ${name} is missing.`);
  return process.argv[index + 1];
}

function optionalOption(name) {
  const index = process.argv.indexOf(name);
  if (index === -1) return undefined;
  assert(index + 1 < process.argv.length, `Option ${name} is missing its value.`);
  return process.argv[index + 1];
}

function contained(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

// Mirrors the PreparedCandidateRoot contract of scripts/smoke_screenshot_session.ps1:
// the argument must be the retained isolated product candidate strictly
// inside the same WorkRoot (ve-12hex/candidate), already carrying its own
// state, with layout metadata and product binding identical to the ProductRoot
// being verified. The candidate is never copied, moved or overwritten here.
function validatePreparedCandidate(source, preparedRoot, work) {
  const candidate = fs.realpathSync(preparedRoot);
  const preparedSmokeRoot = path.dirname(candidate);
  const relative = path.relative(work, preparedSmokeRoot);
  assert.equal(path.basename(candidate), 'candidate',
    'PreparedCandidateRoot must be the retained candidate directory itself.');
  assert(/^ve-[0-9a-f]{12}$/.test(path.basename(preparedSmokeRoot)),
    'PreparedCandidateRoot must live in a ve-12hex product smoke root.');
  assert(relative && !relative.startsWith('..') && !path.isAbsolute(relative),
    'PreparedCandidateRoot must be strictly inside WorkRoot.');
  assert(fs.statSync(path.join(candidate, 'state')).isDirectory(),
    'Prepared candidate has no retained state directory.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json'])
    assert(fs.statSync(path.join(candidate, marker)).isFile(),
      `Prepared candidate marker missing: ${marker}`);
  assert(fs.readFileSync(path.join(source, 'app/metadata/product-layout.json'))
    .equals(fs.readFileSync(path.join(candidate, 'app/metadata/product-layout.json'))),
    'Prepared candidate does not match the ProductRoot layout.');
  const sourceIdentity = JSON.parse(fs.readFileSync(
    path.join(source, 'app/metadata/component-identities.json'), 'utf8'));
  const candidateIdentity = JSON.parse(fs.readFileSync(
    path.join(candidate, 'app/metadata/component-identities.json'), 'utf8'));
  for (const field of ['component', 'repository', 'version', 'source_sha'])
    assert(typeof sourceIdentity.project?.[field] === 'string' &&
      sourceIdentity.project[field].length > 0 &&
      sourceIdentity.project[field] === candidateIdentity.project?.[field],
      `Prepared candidate product binding differs: ${field}`);
  return candidate;
}

async function freeLocalPort() {
  const server = net.createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const port = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  return port;
}

async function native(action, options = {}, timeoutMs = 20000) {
  const args = ['-NoProfile', '-NonInteractive', '-File', fixtureScript,
    '-Action', action];
  for (const [key, value] of Object.entries(options))
    args.push(`-${key}`, String(value));
  const { stdout } = await execFileAsync('pwsh', args, {
    timeout: timeoutMs, windowsHide: true, maxBuffer: 1024 * 1024,
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
    right > left && bottom > top, 'Synthetic scroll fixture geometry is invalid.');
  return { left, top, right, bottom, width: right - left, height: bottom - top };
}

async function firstLine(child, timeoutMs) {
  const lines = readline.createInterface({ input: child.stdout });
  let stderr = '';
  child.stderr.on('data', (chunk) => { stderr = (stderr + chunk).slice(-1000); });
  try {
    return await Promise.race([
      once(lines, 'line').then(([line]) => line),
      once(child, 'exit').then(([code]) => {
        throw new Error(`Scroll fixture exited ${code}: ${stderr}`);
      }),
      delay(timeoutMs).then(() => { throw new Error('Scroll fixture startup timed out.'); }),
    ]);
  } finally {
    lines.close();
  }
}

async function startFixture() {
  const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File', fixtureScript,
    '-Action', 'fixture'], {
    stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
  });
  try {
    const fields = (await firstLine(child, 15000)).split('|');
    assert.equal(fields.length, 10, 'Scroll fixture must report handles, geometry and row encoding.');
    const fixture = {
      child,
      pid: Number(fields[0]),
      threadId: Number(fields[1]),
      root: Number(fields[2]),
      rootRect: parseRect(fields[3]),
      clientRect: parseRect(fields[4]),
      rows: Number(fields[5]),
      rowHeight: Number(fields[6]),
      stripeWidth: Number(fields[7]),
      textLeft: Number(fields[8]),
      wheelStepPixels: Number(fields[9]),
    };
    assert.equal(fixture.pid, child.pid, 'Scroll fixture PID does not match its process.');
    assert(fixture.rows >= 64 && fixture.rowHeight >= 16 &&
      fixture.wheelStepPixels >= 8, 'Scroll fixture row encoding is unusable.');
    return fixture;
  } catch (error) {
    await stopOwned(child, 'close', {});
    throw error;
  }
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

async function launchApp(candidate, webviewData, instanceId, { shellOnly = true } = {}) {
  const port = await freeLocalPort();
  const executable = path.join(candidate, 'app', 'VibeOCR.WinUI.exe');
  // Without --shell-only the app starts the real Supervisor/Runtime; the
  // native-actions-e2e smoke mode never replaces the production region picker.
  const appArguments = ['--profile', 'production', '--install-root', candidate];
  if (shellOnly) appArguments.unshift('--shell-only');
  const child = spawn(executable, appArguments, {
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
    try { await stopOwned(child, 'close', {}); }
    catch (cleanupError) {
      throw new AggregateError([error, cleanupError],
        `Isolated app startup and owned PID cleanup both failed: ${error.message}; ${cleanupError.message}`);
    }
    throw error;
  }
}

// Collect every screenshot-session state broadcast so the harness can prove the
// cancel paths never open a new session or reuse the previous output as one.
// The same public app.state listener also mirrors the full recognition
// projection (statusCode/result/textLayer) for the prepared-candidate step.
async function watchScreenshotSession(page) {
  await page.evaluate(() => {
    window.__scrollCaptureSessions = [];
    window.__scrollCaptureRecognition = null;
    window.chrome.webview.addEventListener('message', ({ data }) => {
      if (data?.kind !== 'event' || data?.type !== 'app.state' ||
          data.payload?.scope !== 'recognition') return;
      const state = data.payload.state;
      if (state) window.__scrollCaptureRecognition = { ...state };
      const session = state?.screenshotSession;
      window.__scrollCaptureCurrentSession = session ?? null;
      if (typeof session?.sessionId === 'string' &&
          !window.__scrollCaptureSessions.some((item) =>
            item.sessionId === session.sessionId && item.revision === session.revision))
        window.__scrollCaptureSessions.push({
          sessionId: session.sessionId, revision: session.revision });
    });
  });
}

async function sessionEvidence(page) {
  return page.evaluate(() => window.__scrollCaptureSessions ?? []);
}

async function recognitionSnapshot(page) {
  return page.evaluate(() => window.__scrollCaptureRecognition ?? null);
}

// Public state excludes transient status/busy fields: a cancelled capture must
// preserve the exact prior session, revision and original/final resource refs.
async function screenshotOutput(page) {
  const state = await recognitionSnapshot(page);
  return { session: state?.screenshotSession ?? null, input: state?.input ?? null,
    result: state?.result ?? null, resultImage: state?.resultImage ?? null };
}

async function assertStoppedOutput(app, before, statusCode = 'recognition.cancelled') {
  await app.page.waitForFunction((statusCode) =>
    window.__scrollCaptureRecognition?.statusCode === statusCode &&
    window.__scrollCaptureRecognition?.isBusy === false, statusCode, { timeout: 15000 });
  assert.deepEqual(await screenshotOutput(app.page), before,
    'Stopped capture changed the previous session, revision or image resources.');
}

async function connectEditor(app) {
  const until = Date.now() + 15000;
  let page;
  while (Date.now() < until) {
    page = app.browser.contexts().flatMap((context) => context.pages())
      .find((item) => !item.isClosed() && item !== app.page && item !== app.editorPage &&
        item.url().includes('#/imageEdit?scene=1'));
    if (page) break;
    await delay(100);
  }
  assert(page, 'Screenshot editor WebView did not appear.');
  await page.getByRole('heading', { name: '截图现场编辑', exact: true }).waitFor({ timeout: 15000 });
  await page.getByLabel('图片检查画布').waitFor({ timeout: 15000 });
  const session = (await screenshotOutput(app.page)).session;
  assert(session?.sessionId, 'Main window published no screenshot session for the editor.');
  await expect(page.locator('.canvas-editor')).toHaveAttribute(
    'data-screenshot-session', session.sessionId, { timeout: 15000 });
  app.editorPage = page;
  const editors = (await windows(app.child.pid)).filter((item) => item.Visible &&
    item.Handle !== app.main.Handle && area(item) > 20000);
  assert.equal(editors.length, 1, 'Expected exactly one owned screenshot editor window.');
  app.editorWindow = editors[0];
  app.evidence.editor = { url: page.url(), handle: app.editorWindow.Handle, sessionId: session.sessionId };
  return page;
}

// Prepared-candidate mode observes every public /__annotation upload (the
// text-layer preparation and the explicit recognition each export one final
// PNG) without altering the request. Records stay readable after restore.
async function watchAnnotationUploads(page) {
  const uploads = [];
  await page.exposeFunction('__scrollCaptureRecordUpload', (entry) => uploads.push(entry));
  await page.evaluate(() => {
    const originalFetch = window.fetch;
    const observed = [];
    window.__scrollCaptureUploads = observed;
    window.fetch = function(input, init) {
      if (input === '/__annotation' && init?.method === 'POST') {
        const entry = { mimeType: null, byteLength: null, bytes: null, problem: null };
        observed.push(entry);
        try {
          const body = init.body;
          if (!(body instanceof Blob)) entry.problem = 'upload body was not a Blob';
          else {
            entry.mimeType = body.type;
            entry.byteLength = body.size;
            body.arrayBuffer().then(
              (buffer) => {
                entry.bytes = Array.from(new Uint8Array(buffer));
                return window.__scrollCaptureRecordUpload(entry);
              },
              () => {
                entry.problem = 'Blob observation failed';
                return window.__scrollCaptureRecordUpload(entry);
              },
            );
          }
        } catch {
          entry.problem = 'Blob observation failed';
        }
        if (entry.problem) void window.__scrollCaptureRecordUpload(entry);
      }
      return originalFetch.apply(this, arguments);
    };
    window.__scrollCaptureRestoreUploads = () => {
      window.fetch = originalFetch;
      delete window.__scrollCaptureRestoreUploads;
    };
  });
  // Scene recognition closes this WebView; retain the passive upload evidence
  // in the harness before that handoff instead of reading a closed page.
  return uploads;
}

async function controlBar(appPid, automationId) {
  return JSON.parse(await native('uia-find',
    { AppPid: appPid, AutomationId: automationId }, 30000));
}

async function waitForControlBar(appPid, automationId, timeoutMs) {
  const until = Date.now() + timeoutMs;
  let last;
  while (Date.now() < until) {
    last = await controlBar(appPid, automationId);
    if (last.found) return last;
    await delay(200);
  }
  throw new Error(`Control bar button never appeared: ${automationId}`);
}

async function waitForControlBarGone(appPid, automationId, timeoutMs) {
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    if (!(await controlBar(appPid, automationId)).found) return true;
    await delay(200);
  }
  throw new Error(`Control bar never closed: ${automationId}`);
}

async function barText(appPid, automationId) {
  return JSON.parse(await native('uia-bar-text',
    { AppPid: appPid, AutomationId: automationId }, 30000));
}

function rectsOverlap(a, b) {
  return a.left < b.right - 1 && b.left < a.right - 1 &&
    a.top < b.bottom - 1 && b.top < a.bottom - 1;
}

function isOverlay(app, fixture) {
  // Scroll masks share the app PID and can cover this point outside the inset.
  return (item) => item.Visible && item.Handle !== app.main.Handle &&
    item.ClassName === 'WinUIDesktopWin32WindowClass' &&
    area(item) > fixture.rootRect.width * fixture.rootRect.height * 2 &&
    inside(item, fixture.clientRect.left + Math.floor(fixture.clientRect.width / 2),
      fixture.clientRect.top + 10);
}

async function dragSelection(app, fixture, selection, overlayHandle) {
  const appPid = app.child.pid;
  const x0 = selection.left;
  const y0 = selection.top;
  const x1 = selection.right;
  const y1 = selection.bottom;
  await native('mouse-move', { AppPid: appPid, X: x0, Y: y0 });
  await delay(250);
  await native('mouse-down', { AppPid: appPid, X: x0, Y: y0 });
  await native('mouse-move', { AppPid: appPid, X: x1, Y: y1 });
  await delay(350);
  await native('mouse-up', { AppPid: appPid, X: x1, Y: y1 });
  // Keep the manual selection geometry proof before choosing its native action.
  await delay(1200);
  app.evidence.selectionText = JSON.parse(await native('window-text', { AppPid: appPid, Handle: overlayHandle }));
  const size = app.evidence.selectionText.map((text) => text.match(/^手动 · (\d+) × (\d+) px/)).find(Boolean);
  // The toolbar can fill window-text's bounded prefix before the magnifier.
  const magnifier = await pickerNative('magnifier', { AppPid: appPid, Handle: overlayHandle });
  app.evidence.selectionMagnifier = magnifier;
  const point = magnifier.match(/^(-?\d+), (-?\d+)\r?\n#/);
  assert(size && point, 'Picker did not expose a manual selection and bottom-right sample pixel.');
  // The bottom-right magnifier samples the last included pixel, not the exclusive edge.
  const actual = { width: Number(size[1]), height: Number(size[2]), right: Number(point[1]) + 1, bottom: Number(point[2]) + 1 };
  actual.left = actual.right - actual.width;
  actual.top = actual.bottom - actual.height;
  for (const edge of ['left', 'top', 'right', 'bottom'])
    assert(Math.abs(actual[edge] - selection[edge]) <= 5, `Picker coordinate mapping differs by more than five pixels at ${edge}.`);
  Object.assign(selection, actual);
  app.evidence.selectionAction = JSON.parse(await native('selection-scroll',
    { AppPid: appPid, Handle: overlayHandle }));
  assert.equal(app.evidence.selectionAction.windowHandle, overlayHandle,
    'Long screenshot action did not belong to the selected picker.');
  const until = Date.now() + 10000;
  while (Date.now() < until) {
    if (!(await windows(appPid)).some(isOverlay(app, fixture))) return;
    await delay(180);
  }
  throw new Error('Region picker overlay did not close after owned selection.');
}

// Opens 截图, drags the fixed fixture-internal selection (inset from the
// client edges so borders and any chrome are excluded) and waits for the
// native scroll control bar.
async function startScrollSession(app, fixture) {
  const current = (await windows(app.child.pid)).find((item) => item.Handle === app.main.Handle);
  assert(current?.Visible, 'Owned main window is not visible before capture.');
  app.main = current;
  app.evidence.mainWindowBeforeCapture = current;
  app.evidence.focusProbe = JSON.parse(await native('probe', { AppPid: app.child.pid, FixturePid: fixture.pid,
    X: Math.floor((current.Bounds.Left + current.Bounds.Right) / 2), Y: current.Bounds.Top + 40 }));
  await native('focus', { FixturePid: app.child.pid, Handle: app.main.Handle,
    X: Math.floor((app.main.Bounds.Left + app.main.Bounds.Right) / 2), Y: app.main.Bounds.Top + 40 });
  await native('raise', { FixturePid: fixture.pid, Handle: fixture.root });
  const sourceProbe = JSON.parse(await native('probe', { AppPid: app.child.pid, FixturePid: fixture.pid,
    X: fixture.clientRect.left + 100, Y: fixture.clientRect.top + 100 }));
  app.evidence.sourceBeforeSelection = sourceProbe;
  assert.equal(sourceProbe.HitPid, fixture.pid, 'Synthetic source is obscured before selection.');
  await app.page.getByRole('button', { name: '截图', exact: true }).click();
  const overlays = await waitForWindows(app.child.pid, isOverlay(app, fixture), 15000);
  const overlay = overlays.sort((a, b) => area(b) - area(a))[0];
  assert.equal(Number(await native('taskbar-state')), app.evidence.taskbarStateBefore,
    'Opening the screenshot selector changed the Windows taskbar preference.');
  const selection = {
    left: fixture.clientRect.left + selectionInset,
    top: fixture.clientRect.top + selectionInset,
    right: fixture.clientRect.right - selectionInset,
    bottom: fixture.clientRect.bottom - selectionInset,
  };
  selection.width = selection.right - selection.left;
  selection.height = selection.bottom - selection.top;
  await dragSelection(app, fixture, selection, overlay.Handle);
  const direction = await waitForControlBar(app.child.pid, 'scroll-capture-direction-down', 15000);
  assert(direction.enabled, 'The direction segment is disabled before a direction is chosen.');
  // A topmost source can hide the controller's compositor shadow and mask #127.
  // Keep the real source in the normal window band for capture verification.
  await native('lower', { FixturePid: fixture.pid, Handle: fixture.root });
  assert.equal(Number(await native('taskbar-state')), app.evidence.taskbarStateBefore,
    'Opening the scroll controller changed the Windows taskbar preference.');
  const barBounds = { left: direction.left, top: direction.top, right: direction.right, bottom: direction.bottom };
  assert(!rectsOverlap(barBounds, selection),
    'Scroll control bar overlaps the fixed selection region.');
  for (const id of ['scroll-capture-finish', 'scroll-capture-cancel', 'scroll-capture-settings'])
    assert((await controlBar(app.child.pid, id)).found, `Control bar is missing ${id}.`);
  await disableAutoScroll(app.child.pid);
  return { overlayHandle: overlay.Handle, selection, direction };
}

async function waitForCapturePreview(appPid, expectedText) {
  const until = Date.now() + 15000;
  while (Date.now() < until) {
    const confirm = await controlBar(appPid, 'scroll-capture-confirm');
    if (confirm.found && confirm.enabled && confirm.name === expectedText) return confirm;
    await delay(200);
  }
  throw new Error(`Capture preview confirmation was not ready: ${expectedText}`);
}

// Selecting one direction segment starts capture immediately: the controller
// disables every segment (single select) and returns foreground to the source
// window. Uses the owned UIA toggle pattern, no desktop-wide scanning.
async function pickDirection(appPid) {
  const segment = await waitForControlBar(appPid, 'scroll-capture-direction-down', 15000);
  assert(segment.enabled, 'Direction segment is disabled before a direction is chosen.');
  await native('uia-toggle', { AppPid: appPid, AutomationId: 'scroll-capture-direction-down' });
  const until = Date.now() + 10000;
  while (Date.now() < until) {
    const state = await controlBar(appPid, 'scroll-capture-direction-down');
    if (state.found && !state.enabled) return segment;
    await delay(180);
  }
  throw new Error('Picking a direction did not lock the segmented control.');
}

// The product scrolls automatically by default; this harness needs
// deterministic manual wheel steps, so every round opts out through the public
// settings flyout before picking a direction (manual scrolling path).
async function disableAutoScroll(appPid) {
  await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-settings' });
  const box = await waitForControlBar(appPid, 'scroll-capture-automatic', 5000);
  assert(box.enabled && box.name.includes('自动滚动'), 'The auto-scroll setting must stay adjustable.');
  const toggled = JSON.parse(
    await native('uia-toggle', { AppPid: appPid, AutomationId: 'scroll-capture-automatic' }));
  assert(toggled.toggled === 'Off', `Auto-scroll opt-out failed: ${toggled.toggled}.`);
  await native('key', { AppPid: appPid, Key: 'escape' }); // light-dismiss the settings flyout
}

async function runScrollCapture(app, fixture, { cancel }) {
  const appPid = app.child.pid;
  const outputBefore = await screenshotOutput(app.page);
  const opened = await startScrollSession(app, fixture);
  const barTexts = [];
  app.evidence.activeCapture = { selection: opened.selection, barTexts };
  const recordBar = async (phase) => {
    const snapshot = await barText(appPid, 'scroll-capture-finish').catch(() => null);
    if (snapshot) barTexts.push({ phase, text: snapshot.text });
  };
  await recordBar('selected');

  if (cancel) {
    await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-cancel' });
    await waitForControlBarGone(appPid, 'scroll-capture-finish', 10000);
    const restored = await windows(appPid);
    assert(restored.some((item) => item.Handle === app.main.Handle && item.Visible),
      'Cancelled scroll capture did not restore the main window.');
    await assertStoppedOutput(app, outputBefore);
    return { cancelled: true, preservedOutput: outputBefore, barTexts, ...opened };
  }

  const controlWindow = (await windows(appPid)).find((item) => item.Handle === opened.direction.windowHandle);
  assert(controlWindow, 'Owned scroll control window is missing.');
  // Focus through a click on the non-interactive status area only: the toolbar
  // now starts with the direction segments at the left window edge.
  await native('focus', { FixturePid: appPid, Handle: controlWindow.Handle,
    X: Math.floor((controlWindow.Bounds.Left + controlWindow.Bounds.Right) / 2),
    Y: Math.floor(controlWindow.Bounds.Top + (controlWindow.Bounds.Bottom - controlWindow.Bounds.Top) * 0.75) });
  await pickDirection(appPid);
  await recordBar('direction-picked');
  // The source window must be activated by the controller before scrolling.
  const until = Date.now() + 10000;
  for (;;) {
    let foreground;
    try { foreground = Number(await native('foreground', { FixturePid: fixture.pid })); }
    catch { foreground = 0; }
    if (foreground === fixture.root) break;
    if (Date.now() > until)
      throw new Error(`Controller did not activate the scroll source window: ${JSON.stringify(await native('probe', { AppPid: appPid, FixturePid: fixture.pid, X: opened.selection.left + 100, Y: opened.selection.top + 100 }))}`);
    await delay(180);
  }
  const during = await windows(appPid);
  assert(!during.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Main window stayed visible while the scroll controller owned the screen.');
  await recordBar('started');
  await waitForFirstFrameAccepted(appPid);
  await delay(stableWaitMs);
  await native('frame', { FixturePid: fixture.pid, Handle: fixture.root, X: opened.selection.left, Y: opened.selection.top, Width: opened.selection.width, Height: opened.selection.height, EvidenceRoot: app.evidence.smokeRoot, OutputPath: path.join(app.evidence.smokeRoot, 'source-before.png') });

  const steps = [];
  const wheelX = fixture.clientRect.left + Math.floor(fixture.clientRect.width / 2);
  const wheelY = fixture.clientRect.top + Math.floor(fixture.clientRect.height / 2);
  for (let step = 1; step <= wheelSteps; step++) {
    await native('wheel', {
      FixturePid: fixture.pid, Handle: fixture.root, X: wheelX, Y: wheelY, Delta: -120,
    });
    await delay(stableWaitMs);
    await recordBar(`wheel-${step}`);
    await native('frame', { FixturePid: fixture.pid, Handle: fixture.root,
      X: opened.selection.left, Y: opened.selection.top, Width: opened.selection.width, Height: opened.selection.height,
      EvidenceRoot: app.evidence.smokeRoot, OutputPath: path.join(app.evidence.smokeRoot, `source-wheel-${step}.png`) });
    steps.push(step);
  }
  // Focus the already-validated owned control window directly: extra UIA
  // enumerations after the last wheel can push focus past the product's 30 s
  // no-progress boundary. Same-HWND check follows; every finish assertion and
  // limit is unchanged.
  const finishFocusStarted = performance.now();
  await native('focus', { FixturePid: appPid, Handle: controlWindow.Handle,
    X: Math.floor((controlWindow.Bounds.Left + controlWindow.Bounds.Right) / 2),
    Y: Math.floor(controlWindow.Bounds.Top +
      (controlWindow.Bounds.Bottom - controlWindow.Bounds.Top) * 0.75) });
  const finish = await controlBar(appPid, 'scroll-capture-finish');
  assert(finish.found && finish.enabled, 'scroll-capture-finish is missing or disabled.');
  assert.equal(finish.windowHandle, controlWindow.Handle,
    'Scroll control bar moved to a different window between start and finish.');
  app.evidence.finishFocusTiming = {
    helperSequence: 'focus(known owned control window) -> one controlBar sample (same HWND)',
    helperMs: Math.round(performance.now() - finishFocusStarted),
    note: 'Timing scope only; product limits and every finish assertion are unchanged.',
  };
  await delay(5500); // Pausing on the controls must not contaminate frames or trip dynamic-content timeout.
  await recordBar('control-focused-before-finish');
  assert((await controlBar(appPid, 'scroll-capture-finish')).enabled, 'Focusing capture controls invalidated a stable capture.');
  try {
    // Finishing delivers the segment directly: the editor is the result
    // preview and the controller closes with the completed session.
    await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-finish' });
    await waitForControlBarGone(appPid, 'scroll-capture-finish', 15000);
  } catch (error) {
    await recordBar('finish-failed');
    app.evidence.finishProbe = JSON.parse(await native('probe', { AppPid: appPid, FixturePid: fixture.pid,
      X: opened.selection.left + 100, Y: opened.selection.top + 100 }));
    throw error;
  }
  const restored = await windows(appPid);
  assert(restored.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Finished scroll capture did not restore the main window.');

  const page = await connectEditor(app);
  const editor = page.getByLabel('图片检查画布');
  await editor.waitFor({ timeout: 15000 });
  await page.waitForFunction(() => {
    const canvas = document.querySelector('canvas[aria-label="图片检查画布"]');
    if (!canvas) return false;
    const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    let changed = 0;
    for (let i = 0; i < data.length; i += 40)
      if (data[i] !== 22 || data[i + 1] !== 22 || data[i + 2] !== 22) changed++;
    return changed > 100;
  }, null, { timeout: 15000 });
  return { cancelled: false, barTexts, steps, ...opened };
}

// Reads the session's ORIGINAL long image from the product's own public
// recognition snapshot: the input resource is bound to the just-finished
// capture (the same pixels the editor renders) and served from the product
// __resource origin. This is a raw product resource, not a user output: the
// editor is still open, so no terminal export has happened yet.
async function fetchScrollOriginal(app) {
  const output = await screenshotOutput(app.page);
  assert(output.session?.sessionId,
    'Scroll capture published no screenshot session for the original input.');
  const resource = new URL(output.input?.url);
  assert(resource.origin === 'https://app.vibeocr' && resource.pathname.startsWith('/__resource/'),
    'Original input must be a product resource before it is fetched.');
  const probe = await app.page.evaluate(async (url) => {
    const response = await fetch(url, { cache: 'no-store' });
    if (!response.ok) throw new Error(`Original input resource returned ${response.status}.`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.length > 64 * 1024 * 1024) throw new Error('Original input resource exceeded 64 MiB.');
    return { mediaType: response.headers.get('content-type'), bytes: Array.from(bytes) };
  }, output.input.url);
  const bytes = Buffer.from(probe.bytes);
  const mediaType = assertScrollSessionOriginal(output.input, bytes);
  assert.equal(probe.mediaType ?? mediaType, mediaType,
    'Original resource content type disagrees with the published media type.');
  return { bytes, mediaType, sessionId: output.session.sessionId,
    revision: output.session.revision, url: output.input.url };
}

// Terminal copy (#318/#320 semantics: success closes the scene). The
// clipboard sentinel is this run's own fixture frame; after the scene closes
// only Node-held evidence is used, and the real Clipboard is proven through
// the still-alive workbench's public paste input.
async function terminalCopyAndReadback(app, { analysis, annotationRect, originalRgba }) {
  const page = app.editorPage;
  assert(page && !page.isClosed(), 'Screenshot editor is not available for the terminal copy.');
  const session = (await screenshotOutput(app.page)).session;
  assert(session?.sessionId, 'Terminal copy found no live screenshot session.');
  const sentinel = path.join(app.evidence.smokeRoot, 'source-before.png');
  assert(fs.existsSync(sentinel),
    'Scroll fixture frame evidence is missing for the clipboard sentinel.');
  await setClipboardImage(app.evidence.smokeRoot, sentinel);
  const terminal = await observeSceneCopyTerminal(page);
  let png;
  try {
    const clickError = await page.getByRole('button', { name: '复制标注图' }).click()
      .then(() => null, (error) => `${error.name}: ${error.message}`);
    if (clickError) terminal.timeline.clickError = clickError;
    const command = await terminal.waitForCommand('copyScreenshotImage', 15000);
    assert(command,
      `No public copyScreenshotImage command before the scene closed: ${JSON.stringify(terminal.timeline)}`);
    const endDeadline = raceDeadline(15000);
    let ended;
    try { ended = await Promise.race([terminal.ended.then(() => true), endDeadline.promise]); }
    finally { endDeadline.clear(); }
    if (!ended) terminal.timeline.noTerminalEvent = true;
    const upload = terminal.timeline.uploads.find(
      (item) => item.resourceUri === command.resourceUri);
    png = await collectSceneCopyUploadBlob(terminal, upload, 'Scroll capture copy');
  } finally {
    await page.evaluate(() => window.__nativeActionsRestoreFetch?.()).catch(() => {});
  }
  fs.writeFileSync(path.join(app.evidence.smokeRoot, 'scroll-capture-copied.png'), png);
  assertSceneTerminalExport('copyScreenshotImage', terminal.timeline, png, png,
    'Scroll capture copy');
  // The exported PNG must carry THIS revision's annotation in the expected
  // area - an unannotated original export fails closed on pixels.
  const exportRgba = await decodeImageRgba(app.page, png, 'image/png');
  const annotated = assertAnnotatedExportRectangle(exportRgba, originalRgba,
    annotationRect, { context: 'Terminal copy annotation' });
  // The scene is closed now; the readback runs on the still-alive main window.
  const readback = await readbackSceneCopyClipboard(app, app.evidence.smokeRoot, false,
    { width: analysis.width, height: analysis.height }, png);
  await app.page.getByRole('link', { name: '单次识别', exact: true }).click();
  return { session: { sessionId: session.sessionId, revision: session.revision },
    timeline: terminal.timeline, readback, annotated };
}

// Real native Save on the recovered scene: the public save command and its
// accepted upload are proven BEFORE the owned picker is driven; the saved
// file must be exactly the upload bytes bound to that command's resourceUri.
async function saveRecoveredSession(app, outputName, expectedSessionId) {
  const page = app.editorPage;
  assert(page && !page.isClosed(), 'Screenshot editor is not available for the native save.');
  const session = (await screenshotOutput(app.page)).session;
  assert.equal(session?.sessionId, expectedSessionId,
    'Native save must address the recovered screenshot session.');
  const savedPath = path.join(app.evidence.smokeRoot, outputName);
  const terminal = await observeSceneCopyTerminal(page, { actions: ['saveScreenshotImage'] });
  try {
    const clickError = await page.getByRole('button', { name: '保存标注图', exact: true }).click()
      .then(() => null, (error) => `${error.name}: ${error.message}`);
    if (clickError) terminal.timeline.clickError = clickError;
    // Command and its accepted upload are proven FIRST; the owned native
    // picker is driven only afterwards, so the dialog never precedes the
    // command evidence.
    const command = await terminal.waitForCommand('saveScreenshotImage', 15000);
    assert(command,
      `No public saveScreenshotImage command before the scene closed: ${JSON.stringify(terminal.timeline)}`);
    const uploadDeadline = Date.now() + 15000;
    let upload;
    while (Date.now() < uploadDeadline) {
      upload = terminal.timeline.uploads.find((item) =>
        item.resourceUri === command.resourceUri && item.responseStatus === 201);
      if (upload) break;
      await delay(150);
    }
    assert(upload,
      `No accepted 201 upload was bound to the save command: ${JSON.stringify(terminal.timeline)}`);
    await native('save-file', { AppPid: app.child.pid, Handle: app.editorWindow.Handle,
      OutputPath: savedPath, EvidenceRoot: app.evidence.smokeRoot }, 30000);
    const endDeadline = raceDeadline(15000);
    let ended;
    try { ended = await Promise.race([terminal.ended.then(() => true), endDeadline.promise]); }
    finally { endDeadline.clear(); }
    if (!ended) terminal.timeline.noTerminalEvent = true;
    const uploadPng = await collectSceneCopyUploadBlob(terminal, upload, 'Scroll capture save');
    const saveFileDeadline = Date.now() + 15000;
    while (!fs.existsSync(savedPath) && Date.now() < saveFileDeadline) await delay(150);
    assert(fs.existsSync(savedPath), 'Native file picker did not save the recovered long screenshot.');
    const savedPng = fs.readFileSync(savedPath);
    assertSceneTerminalExport('saveScreenshotImage', terminal.timeline, savedPng, uploadPng,
      'Scroll capture save');
    return { savedPng, uploadPng, savedPath, timeline: terminal.timeline,
      session: { sessionId: session.sessionId, revision: session.revision } };
  } finally {
    await page.evaluate(() => window.__nativeActionsRestoreFetch?.()).catch(() => {});
  }
}

// Decode the product-exported stitched image inside the real WebView2 context and
// verify every encoded row: the stripe color at stripeX encodes the document row
// index, so order, range, gaps and duplicated rows are all detected pixel-exactly.
// The scroll capture original is a top-down BMP (InputService.EncodeTopDownBmp);
// terminal exports are PNG: the media type is always the real artifact type.
async function analyzeStitched(page, image, fixture, selection, startOffsetPixels = 0,
  mediaType = 'image/png') {
  assert(Number.isSafeInteger(startOffsetPixels) && startOffsetPixels >= 0,
    'Synthetic source offset must be a non-negative integer.');
  return page.evaluate(async ({ encoded, rowHeight, stripeX, textX, startY, mediaType }) => {
    const bytes = Uint8Array.from(atob(encoded), (letter) => letter.charCodeAt(0));
    const image = await createImageBitmap(new Blob([bytes], { type: mediaType }));
    const canvas = document.createElement('canvas');
    canvas.width = image.width;
    canvas.height = image.height;
    const context = canvas.getContext('2d');
    context.drawImage(image, 0, 0);
    const { data } = context.getImageData(0, 0, image.width, image.height);
    const width = image.width;
    const height = image.height;
    const bad = [];
    const counts = new Map();
    for (let y = 0; y < height; y++) {
      const i = (y * width + stripeX) * 4;
      const r = data[i], g = data[i + 1], b = data[i + 2], a = data[i + 3];
      const decoded = r + (g << 8);
      const expected = Math.floor((y + startY) / rowHeight);
      if (!(a > 200 && b > 24 && b < 104 && decoded === expected))
        bad.push({ y, expected, decoded, rgba: [r, g, b, a] });
      else counts.set(decoded, (counts.get(decoded) || 0) + 1);
    }
    const blocks = [];
    for (let row = Math.max(1, Math.ceil(startY / rowHeight));
      (row + 1) * rowHeight - startY <= height; row++) {
      let dark = 0;
      const yEnd = (row + 1) * rowHeight - startY;
      for (let y = row * rowHeight - startY; y < yEnd; y++)
        for (let x = textX; x < width; x += 3) {
          const i = (y * width + x) * 4;
          if (data[i] < 120 && data[i + 1] < 120 && data[i + 2] < 120) dark++;
        }
      blocks.push({ row, dark });
    }
    return {
      width, height, badCount: bad.length, bad: bad.slice(0, 20),
      rows: [...counts.entries()].map(([row, pixels]) => ({ row, pixels })),
      blocks, minDark: Math.min(...blocks.map((item) => item.dark)),
    };
  }, {
    encoded: image.toString('base64'),
    rowHeight: fixture.rowHeight,
    startY: selection.top - fixture.clientRect.top + startOffsetPixels,
    stripeX: Math.max(0, Math.floor(fixture.stripeWidth / 2) - selectionInset),
    textX: fixture.textLeft - selectionInset + 2,
    mediaType,
  });
}

// Pure judges exported only for the Node regression (the script itself keeps
// its main-guard below): every check runs on Node-held buffers/timelines so a
// closed scene page never has to be evaluated again. No hashing.
export function detectImageMediaType(bytes) {
  assert(Buffer.isBuffer(bytes) && bytes.length >= 8, 'Image evidence buffer is missing.');
  if (bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47 &&
      bytes[4] === 0x0d && bytes[5] === 0x0a && bytes[6] === 0x1a && bytes[7] === 0x0a)
    return 'image/png';
  assert(bytes.length > 54 && bytes[0] === 0x42 && bytes[1] === 0x4d,
    'Image evidence is neither a production PNG export nor the scroll BMP original.');
  return 'image/bmp';
}

// The original long image must come from the product's own session-bound
// __resource URL (raw product pixels), never from a screen re-capture; the
// published media type/byte length, when present, must match the bytes.
export function assertScrollSessionOriginal(input, bytes, context = 'Scroll original') {
  assert(input && typeof input.url === 'string' && input.url.length > 0,
    `${context}: no original input resource was published.`);
  const resource = new URL(input.url);
  assert(resource.origin === 'https://app.vibeocr' &&
      resource.pathname.startsWith('/__resource/'),
    `${context}: original input is not a product __resource URL: ${input.url}`);
  const mediaType = detectImageMediaType(bytes);
  if (typeof input.mediaType === 'string')
    assert.equal(input.mediaType, mediaType,
      `${context}: published media type ${input.mediaType} differs from the actual ${mediaType} bytes.`);
  if (Number.isSafeInteger(input.byteLength))
    assert.equal(input.byteLength, bytes.length,
      `${context}: published byte length ${input.byteLength} differs from the fetched ${bytes.length}.`);
  return mediaType;
}

// Terminal Copy/Save both close the screenshot scene (#318/#320 product
// semantics). After the page is gone this judge alone decides from the
// Node-held timeline: exactly one accepted upload bound to this very command,
// a normal close after it, no failed receipt, and the artifact saved on disk
// must be those very upload bytes in the production PNG form.
export function assertSceneTerminalExport(action, timeline, artifactBytes, uploadBytes,
  context = 'Scene terminal export') {
  assert(Buffer.isBuffer(artifactBytes) && artifactBytes.length > 0,
    `${context}: artifact bytes are missing.`);
  settleSceneCopyTimeline(timeline, context, { action });
  assert(Buffer.isBuffer(uploadBytes) && uploadBytes.length > 0,
    `${context}: Node-held upload bytes are missing.`);
  assert.equal(detectImageMediaType(artifactBytes), 'image/png',
    `${context}: artifact on disk is not the production PNG export.`);
  assert.equal(detectImageMediaType(uploadBytes), 'image/png',
    `${context}: upload bound to the ${action} command is not PNG.`);
  assert(artifactBytes.equals(uploadBytes),
    `${context}: artifact bytes differ from the upload bound to this command's resourceUri.`);
  return true;
}

// Pure judge for the annotated terminal export (#320 review fix): the export
// must carry this revision's annotation inside the expected NATURAL-image
// rectangle; the unannotated original or a misplaced annotation fails closed.
export function assertAnnotatedExportRectangle(exportRgba, originalRgba, rect,
  { color = { r: 243, g: 139, b: 53 }, tolerance = 40, margin = 12,
    minEdgePixels = 12, context = 'Annotated export' } = {}) {
  for (const [name, image] of [['export', exportRgba], ['original', originalRgba]]) {
    assert(image && Number.isSafeInteger(image.width) && image.width > 0 &&
        Number.isSafeInteger(image.height) && image.height > 0 &&
        typeof image.rgbaBase64 === 'string',
      `${context}: ${name} RGBA evidence is invalid.`);
    assert(Buffer.from(image.rgbaBase64, 'base64').length ===
        image.width * image.height * 4,
      `${context}: ${name} RGBA payload is not width*height*4 bytes.`);
  }
  assert.equal(exportRgba.width, originalRgba.width,
    `${context}: export width differs from the original.`);
  assert.equal(exportRgba.height, originalRgba.height,
    `${context}: export height differs from the original.`);
  for (const field of ['left', 'top', 'right', 'bottom'])
    assert(Number.isSafeInteger(rect?.[field]), `${context}: rect.${field} is not an integer.`);
  assert(rect.left >= 0 && rect.top >= 0 && rect.right > rect.left &&
      rect.bottom > rect.top && rect.right < exportRgba.width &&
      rect.bottom < exportRgba.height,
    `${context}: expected rectangle ${JSON.stringify(rect)} is outside the image.`);
  const near = (pixels, index) =>
    Math.abs(pixels[index] - color.r) <= tolerance &&
    Math.abs(pixels[index + 1] - color.g) <= tolerance &&
    Math.abs(pixels[index + 2] - color.b) <= tolerance;
  const countBand = (pixels, x0, y0, x1, y1) => {
    let count = 0;
    for (let y = Math.max(0, y0); y <= Math.min(exportRgba.height - 1, y1); y++)
      for (let x = Math.max(0, x0); x <= Math.min(exportRgba.width - 1, x1); x++)
        if (near(pixels, (y * exportRgba.width + x) * 4)) count++;
    return count;
  };
  const exportPixels = Buffer.from(exportRgba.rgbaBase64, 'base64');
  const originalPixels = Buffer.from(originalRgba.rgbaBase64, 'base64');
  const bands = {
    top: [rect.left + margin, rect.top - margin, rect.right - margin, rect.top + margin],
    bottom: [rect.left + margin, rect.bottom - margin, rect.right - margin, rect.bottom + margin],
    left: [rect.left - margin, rect.top + margin, rect.left + margin, rect.bottom - margin],
    right: [rect.right - margin, rect.top + margin, rect.right + margin, rect.bottom - margin],
  };
  const edgeCounts = {};
  for (const [edge, band] of Object.entries(bands)) {
    edgeCounts[edge] = countBand(exportPixels, ...band);
    assert(edgeCounts[edge] >= minEdgePixels,
      `${context}: expected ${edge} edge band carries only ${edgeCounts[edge]} ` +
      `stroke pixels (need ${minEdgePixels}); the export is not annotated there.`);
    assert.equal(countBand(originalPixels, ...band), 0,
      `${context}: the ORIGINAL already carries the stroke color in the ` +
      `${edge} edge band; the fixture baseline is unusable.`);
  }
  const ring = {
    x0: rect.left - margin * 2, y0: rect.top - margin * 2,
    x1: rect.right + margin * 2, y1: rect.bottom + margin * 2,
  };
  let stray = 0;
  for (let y = 0; y < exportRgba.height; y++)
    for (let x = 0; x < exportRgba.width; x++)
      if ((x < ring.x0 || x > ring.x1 || y < ring.y0 || y > ring.y1) &&
          near(exportPixels, (y * exportRgba.width + x) * 4)) stray++;
  assert.equal(stray, 0,
    `${context}: ${stray} stroke-colored pixels appear outside the expected rectangle.`);
  return { edgeCounts, strayPixels: stray };
}

// Pure mapping mirroring annotationGeometry.imageTransform at rotation 0: the
// scroll editor canvas exposes a fixed logical coordinate space
// (data-coordinate-width/height), the image is fitted with
// scale=min(logicalW/imgW, logicalH/imgH) and centered; pointer injection goes
// through the CSS bounding box. The pixel judge works in NATURAL pixels, so
// the same plan maps expected natural fractions to the injection points.
export function planAnnotationInjection({ fractions, natural, logical, css }) {
  assert(natural.width > 0 && natural.height > 0 && logical.width > 0 &&
      logical.height > 0 && css.width > 0 && css.height > 0,
    'Annotation mapping geometry is invalid.');
  for (const [name, value] of Object.entries(fractions))
    assert(Number.isFinite(value) && value > 0.05 && value < 0.95,
      `Annotation fraction ${name} is out of the safely drawable range.`);
  assert(fractions.x1 > fractions.x0 + 0.05 && fractions.y1 > fractions.y0 + 0.05,
    'Annotation fractions do not describe a usable rectangle.');
  const scale = Math.min(logical.width / natural.width, logical.height / natural.height);
  const offsetX = (logical.width - natural.width * scale) / 2;
  const offsetY = (logical.height - natural.height * scale) / 2;
  const naturalRect = {
    left: Math.round(natural.width * fractions.x0),
    top: Math.round(natural.height * fractions.y0),
    right: Math.round(natural.width * fractions.x1),
    bottom: Math.round(natural.height * fractions.y1),
  };
  const toCss = (naturalX, naturalY) => ({
    x: css.x + (offsetX + naturalX * scale) * (css.width / logical.width),
    y: css.y + (offsetY + naturalY * scale) * (css.height / logical.height),
  });
  const start = toCss(naturalRect.left, naturalRect.top);
  const end = toCss(naturalRect.right, naturalRect.bottom);
  for (const [name, point] of [['start', start], ['end', end]]) {
    assert(point.x >= css.x && point.x < css.x + css.width &&
        point.y >= css.y && point.y < css.y + css.height,
      `Annotation ${name} point maps outside the canvas bounding box.`);
  }
  return { naturalRect, start, end,
    layout: { scale, offsetX, offsetY } };
}
function normalizeOcrText(text) {
  return String(text ?? '')
    .normalize('NFKC')
    .toLowerCase()
    .replace(/[\s\p{P}\p{S}]+/gu, '');
}

function detectedRowNumbers(normalized, maxRow) {
  const found = new Set();
  for (let row = 0; row <= maxRow; row++) {
    const id = String(row).padStart(4, '0');
    if (normalized.includes(`行${id}`) || normalized.includes(`line${id}`))
      found.add(row);
  }
  return found;
}

function assertRequiredRows(label, normalized, maxRow) {
  const found = detectedRowNumbers(normalized, maxRow);
  for (const required of [requiredTopRow, requiredBottomRow]) {
    const id = String(required).padStart(4, '0');
    assert(found.has(required), `${label} did not cover synthetic row ${id}; ` +
      `detected rows: ${[...found].join(', ') || 'none'}.`);
  }
  return [...found];
}

// Bounded preflight on public state only: the recognition engine catalog is
// published (non-empty) after the Supervisor attached and the runtime catalog
// was negotiated. Waiting here keeps 取字 from submitting while the service
// is still starting; failures are diagnosed from the real public status.
async function waitForRecognitionService(app, timeoutMs) {
  const until = Date.now() + timeoutMs;
  while (true) {
    const snapshot = await recognitionSnapshot(app.page);
    if (Array.isArray(snapshot?.engines) && snapshot.engines.length > 0)
      return snapshot;
    if (Date.now() > until)
      throw new Error('Recognition service did not publish an engine catalog ' +
        `within ${timeoutMs} ms; last public status: ` +
        `${snapshot?.statusCode ?? 'none'}.`);
    await delay(runtimePollMs);
  }
}

// Bounded wait for the public textLayer projection. Only preparing/ready are
// live states; any terminal unavailable/failed/cancelled/empty status fails
// the smoke immediately with the real status — no automatic retry and no
// swallowed errors.
async function waitForScreenshotTextLayer(app, sessionId, revision) {
  const until = Date.now() + textLayerTimeoutMs;
  while (true) {
    const snapshot = await recognitionSnapshot(app.page);
    const layer = snapshot?.textLayer;
    if (layer?.status === 'textlayer.ready') {
      assert.equal(layer.binding?.sessionId, sessionId,
        'Text layer binds a different screenshot session.');
      assert.equal(layer.binding?.revision, revision,
        'Text layer binds a different content revision.');
      return layer;
    }
    if (layer && layer.status !== 'textlayer.preparing')
      throw new Error(`Screenshot text layer ended as ${layer.status}: ` +
        `${layer.reason ?? 'no reason'}; the real Runtime mode requires a ` +
        `catalog-ready local lightweight text engine.`);
    if (Date.now() > until)
      throw new Error('Screenshot text layer did not become ready within ' +
        `${textLayerTimeoutMs} ms; last status: ${layer?.status ?? 'none'} ` +
        `(${layer?.reason ?? 'no reason'}).`);
    await delay(runtimePollMs);
  }
}

// Bounded wait for the explicit recognition result. The screenshot session
// reference must stay identical (same sessionId and unchanged revision).
async function waitForScreenshotRecognition(app, sessionId, revision) {
  const until = Date.now() + recognitionTimeoutMs;
  while (true) {
    const snapshot = await recognitionSnapshot(app.page);
    const session = snapshot?.screenshotSession;
    if (session) {
      assert.equal(session.sessionId, sessionId,
        'Explicit recognition switched the screenshot session.');
      assert.equal(session.revision, revision,
        'Explicit recognition changed the content revision.');
    }
    if (snapshot?.statusCode === 'recognition.cancelled')
      throw new Error('Explicit screenshot recognition was cancelled.');
    if (snapshot?.statusCode === 'recognition.failed' ||
        snapshot?.statusCode === 'recognition.modeUnavailable' ||
        snapshot?.statusCode === 'recognition.expired')
      throw new Error(`Explicit screenshot recognition ended as ${snapshot.statusCode}.`);
    if (snapshot?.statusCode === 'recognition.completed') {
      // A completed result WITHOUT this session's binding is a stale result
      // from an earlier recognition; it must fail instead of passing.
      assert(session?.sessionId === sessionId && session?.revision === revision,
        'Explicit recognition completed without the expected screenshot session binding.');
      assert(typeof snapshot.result?.url === 'string' && snapshot.result.url.length > 0,
        'Explicit recognition completed without a result resource.');
      const resource = new URL(snapshot.result.url);
      assert(resource.origin === 'https://app.vibeocr' &&
          resource.pathname.startsWith('/__resource/'),
        'Explicit recognition result is not a product __resource URL.');
      return snapshot;
    }
    if (Date.now() > until)
      throw new Error('Explicit screenshot recognition did not complete within ' +
        `${recognitionTimeoutMs} ms; last status: ${snapshot?.statusCode ?? 'none'}.`);
    await delay(runtimePollMs);
  }
}

// Prepared-candidate step, inserted after the same-session edit and before the
// cancel phase: switch to 取字 through the public toolbar button, let the real
// Runtime prepare the in-place text layer for the current stitched final PNG,
// verify the binding/image/rows, then click 识别当前图 and verify the real OCR
// result still references the same screenshot session and covers rows from
// both the top and the bottom of the long image.
async function verifyPreparedCandidateRecognition(app, {
  evidenceRoot, fixture, selection, analysis, sessionId, revision, originalRgba,
  annotationRect,
}) {
  const page = app.editorPage;
  const startY = selection.top - fixture.clientRect.top;
  const maxRow = Math.floor((analysis.height - 1 + startY) / fixture.rowHeight);
  assert(requiredTopRow <= maxRow && requiredBottomRow <= maxRow,
    'Stitched document does not reach the required coverage rows for this fixture.');
  const recognitionUploads = await watchAnnotationUploads(page);
  try {
    // Observe public state before submitting anything: the catalog proves the
    // real Runtime service is up, so 取字 never races Supervisor startup.
    await waitForRecognitionService(app, supervisorReadyTimeoutMs);
    await page.getByRole('button', { name: '取字', exact: true }).click();
    const layer = await waitForScreenshotTextLayer(app, sessionId, revision);
    await page.waitForFunction(() => {
      const uploads = window.__scrollCaptureUploads ?? [];
      return uploads.length >= 1 &&
        (Array.isArray(uploads[0].bytes) || uploads[0].problem !== null);
    }, null, { timeout: 30000 });
    const uploaded = (await page.evaluate(() => window.__scrollCaptureUploads ?? []))
      .find((item) => Array.isArray(item.bytes));
    assert(uploaded, 'Text layer preparation produced no observable PNG upload.');
    assert.equal(uploaded.problem, null,
      `Text layer upload observation failed: ${uploaded.problem}.`);
    assert.equal(uploaded.mimeType, 'image/png', 'Text layer upload was not image/png.');
    const uploadPng = Buffer.from(uploaded.bytes);
    assert(uploadPng.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])),
      'Text layer upload Blob is not PNG.');
    // Same annotated-pixel judge as the terminal copy: this revision's export
    // must carry the annotation, never a re-upload of the original image.
    const uploadRgba = await decodeImageRgba(page, uploadPng, 'image/png');
    const annotated = assertAnnotatedExportRectangle(uploadRgba, originalRgba,
      annotationRect, { context: 'Text layer annotation' });
    const image = layer.image;
    assert(image?.url, 'Ready text layer published no image resource.');
    assert.equal(image.mediaType, 'image/png', 'Text layer image is not image/png.');
    assert.equal(image.byteLength, uploaded.byteLength,
      'Text layer image bytes differ from the exported final PNG upload.');
    const imageProbe = await page.evaluate(async (url) => {
      const response = await fetch(url, { cache: 'no-store' });
      const buffer = await response.arrayBuffer();
      const bitmap = await createImageBitmap(new Blob([buffer], { type: 'image/png' }));
      const size = { width: bitmap.width, height: bitmap.height };
      bitmap.close();
      return { size, bytes: Array.from(new Uint8Array(buffer)) };
    }, image.url);
    const dimensions = imageProbe.size;
    // Content equality, not just length: the layer resource must be the very
    // bytes the editor uploaded for this revision.
    assert(Buffer.from(imageProbe.bytes).equals(uploadPng),
      'Text layer image content differs from the exported final PNG upload.');
    assert.equal(dimensions.width, analysis.width,
      `Text layer image width ${dimensions.width} does not match the stitched ` +
      `long image width ${analysis.width}.`);
    assert.equal(dimensions.height, analysis.height,
      `Text layer image height ${dimensions.height} does not match the stitched ` +
      `long image height ${analysis.height}.`);
    fs.writeFileSync(path.join(evidenceRoot, 'scroll-text-layer-input.png'), uploadPng);

    await page.waitForSelector('.image-text-layer', { timeout: 15000 });
    const domLines = await page.$$eval('.image-text-layer .image-text-line',
      (nodes) => nodes.map((node) => node.textContent ?? ''));
    const layerLines = layer.lines ?? [];
    assert(layerLines.length > 0, 'Ready text layer carries no OCR lines.');
    assert(domLines.length >= Math.min(8, layerLines.length),
      `Text layer rendered ${domLines.length} selectable lines ` +
      `for ${layerLines.length} OCR lines.`);
    const layerRows = assertRequiredRows('Text layer',
      normalizeOcrText([...layerLines.map((line) => line.text), ...domLines].join('\n')),
      maxRow);

    const editorClosed = page.waitForEvent('close', { timeout: 30000 });
    await page.getByRole('button', { name: '识别当前图' }).click();
    await editorClosed;
    const completed = await waitForScreenshotRecognition(app, sessionId, revision);
    await app.page.waitForFunction(() =>
      (document.querySelector('.result-document')?.textContent ?? '').trim().length > 0,
      null, { timeout: 30000 });
    const resultText = await app.page.evaluate(async (url) =>
      (await (await fetch(url, { cache: 'no-store' })).text()), completed.result.url);
    const resultRows = assertRequiredRows('Explicit recognition',
      normalizeOcrText(resultText), maxRow);
    fs.writeFileSync(path.join(evidenceRoot, 'scroll-recognition-result.txt'), resultText);
    // Both exports must have resolved their async Blob observation before any
    // byte comparison; length equality alone never counts as content equality.
    const uploadDeadline = Date.now() + 30000;
    while (recognitionUploads.length < 2 && Date.now() < uploadDeadline) await delay(100);
    assert(recognitionUploads.length >= 2,
      'Text-layer and recognition uploads were not both observed before editor handoff.');
    const recognized = recognitionUploads[recognitionUploads.length - 1];
    assert.equal(recognized?.problem, null,
      `Explicit recognition upload observation failed: ${recognized?.problem}.`);
    const recognizePng = Buffer.from(recognized?.bytes ?? []);
    assert(recognizePng.equals(uploadPng),
      'Explicit recognition exported different final PNG content after the text layer.');
    if (Array.isArray(recognized?.bytes))
      fs.writeFileSync(path.join(evidenceRoot, 'scroll-recognition-input.png'),
        Buffer.from(recognized.bytes));
    await app.page.screenshot({ path: path.join(evidenceRoot, 'scroll-recognition.png') });
    return {
      textLayer: {
        status: layer.status,
        modeId: layer.modeId,
        binding: layer.binding,
        annotated,
        image: { width: dimensions.width, height: dimensions.height,
          byteLength: image.byteLength,
          equalsUploadBytes: Buffer.from(imageProbe.bytes).equals(uploadPng) },
        lineCount: layerLines.length,
        domLineCount: domLines.length,
        coveredRows: layerRows,
      },
      recognition: {
        statusCode: completed.statusCode,
        session: completed.screenshotSession,
        resultChars: resultText.length,
        coveredRows: resultRows,
        uploadCount: recognitionUploads.length,
        recognizeUploadBytes: recognized?.byteLength ?? null,
        recognizeUploadEqualsLayerUpload: recognizePng.equals(uploadPng),
      },
    };
  } finally {
    await page.evaluate(() => window.__scrollCaptureRestoreUploads?.()).catch(() => {});
  }
}

// Waits (bounded) until the real control bar reports the expected production
// error text and both action buttons are disabled after the failure.
async function waitForControllerError(appPid, scenario, expectedParts, timeoutMs) {
  const until = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < until) {
    last = await barText(appPid, 'scroll-capture-finish').catch(() => null);
    if (last && expectedParts.every((part) => last.text.includes(part))) {
      const finish = await controlBar(appPid, 'scroll-capture-finish');
      assert.equal(finish.enabled, false,
        `${scenario} failure left 完成 enabled.`);
      const segment = await controlBar(appPid, 'scroll-capture-direction-down');
      assert.equal(segment.enabled, false,
        `${scenario} failure left a direction segment enabled.`);
      const confirm = await controlBar(appPid, 'scroll-capture-confirm');
      assert(!confirm.found || !confirm.enabled,
        `${scenario} terminal failure exposed a confirmation/correction action.`);
      return last;
    }
    await delay(300);
  }
  throw new Error(`${scenario} controller failure did not report ` +
    `${JSON.stringify(expectedParts)} within ${timeoutMs} ms; last bar text: ` +
    `${last?.text ?? 'unavailable'}.`);
}

// Bounded wait for the first accepted stable frame: 完成 becomes enabled only
// after the first frame enters the stitcher, so jump/move are triggered on an
// existing first frame (the initial preview confirmation step is gone).
async function waitForFirstFrameAccepted(appPid, timeoutMs = 20000) {
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    const finish = await controlBar(appPid, 'scroll-capture-finish');
    if (finish.found && finish.enabled) return finish;
    await delay(250);
  }
  throw new Error(`First stable frame was not accepted within ${timeoutMs} ms.`);
}

// Real negative evidence for the scroll controller, using the owned fixture's
// scenario switch (scripts/scroll_capture_fixture.ps1 -Action scenario). Every
// round reuses the full real manual selection flow, asserts the production
// error text read from the control bar, proves 完成/开始 are disabled and that
// cancelling never opens a new screenshot session. low-texture/dynamic are set
// before start (window geometry unchanged); jump/move fire after the first
// accepted frame; every round except the final move restores normal.
async function verifyControllerFailureModes(app, fixture) {
  const appPid = app.child.pid;
  const setScenario = (scenario) => native('scenario',
    { FixturePid: fixture.pid, Handle: fixture.root, Scenario: scenario });
  const results = [];
  const runNegative = async ({ scenario, before, expectedParts, trigger, restore = true,
    terminalStatus = 'recognition.failed' }) => {
    const sessionsBefore = await sessionEvidence(app.page);
    const outputBefore = await screenshotOutput(app.page);
    if (before) await setScenario(before);
    await startScrollSession(app, fixture);
    await pickDirection(appPid);
    if (trigger) await trigger();
    const bar = await waitForControllerError(appPid, scenario, expectedParts,
      controllerErrorTimeoutMs);
    await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-cancel' });
    await waitForControlBarGone(appPid, 'scroll-capture-finish', 10000);
    await assertStoppedOutput(app, outputBefore, terminalStatus);
    const sessionsAfter = await sessionEvidence(app.page);
    assert.deepEqual(sessionsAfter, sessionsBefore,
      `${scenario} round created or changed a screenshot session.`);
    if (restore) await setScenario('normal');
    results.push({ scenario, barText: bar.text, terminalStatus,
      preservedOutput: outputBefore });
  };

  // All-white content set before start: the first stable frame has no vertical
  // variation, so the stitcher constructor itself fails closed (real text from
  // ScrollCaptureSession/VerticalScrollStitcher, verified against source).
  await runNegative({
    scenario: 'low-texture',
    before: 'low-texture',
    expectedParts: ['首帧沿滚动方向没有可辨识的内容变化', '已停止采集'],
  });

  // Continuously repainting content set before start: no two captures ever
  // match, so the controller trips its own 5 s dynamic-content timeout.
  await runNegative({
    scenario: 'dynamic',
    before: 'dynamic',
    expectedParts: ['选区内容持续变化', '已停止采集'],
  });

  // A single +1000 px jump after the first accepted frame: no overlap can be
  // verified at any offset, so the append is rejected.
  // A recoverable gap (finish disabled with explicit guidance) is not a
  // terminal failure: cancelling preserves the prior session; the gap
  // boundary confirmation itself is covered by the App self-check.
  await runNegative({
    scenario: 'jump',
    expectedParts: ['滚动过快或内容变化', '向回滚动'],
    terminalStatus: 'recognition.cancelled',
    trigger: async () => {
      await waitForFirstFrameAccepted(appPid);
      await setScenario('jump');
    },
  });

  // Moving the source window itself (+8 px) after the first accepted frame:
  // the geometry guard stops the capture. Last round: no restore needed.
  await runNegative({
    scenario: 'move',
    expectedParts: ['源窗口位置或大小发生变化', '已停止采集'],
    trigger: async () => {
      await waitForFirstFrameAccepted(appPid);
      await setScenario('move');
    },
    restore: false,
  });
  return results;
}

// A reverse frame is a recoverable review, not a terminal failure. Exercise
// the real discard action, return to the accepted boundary, then append one
// new strip. Export only after explicit completion and verify every
// encoded pixel row so retaining only the last viewport cannot pass.
async function verifyReverseRecovery(app, fixture) {
  const appPid = app.child.pid;
  const sessionsBefore = await sessionEvidence(app.page);
  const priorIds = [...new Set(sessionsBefore.map((item) => item.sessionId))];
  const opened = await startScrollSession(app, fixture);
  const wheel = (delta) => native('wheel', { FixturePid: fixture.pid, Handle: fixture.root,
    X: fixture.clientRect.left + 100, Y: fixture.clientRect.top + 100, Delta: delta });
  await pickDirection(appPid);
  await waitForFirstFrameAccepted(appPid);
  await wheel(120);
  const discard = await waitForCapturePreview(appPid, '丢弃反向帧并继续');
  const rejected = await barText(appPid, 'scroll-capture-finish');
  assert(rejected.text.includes('反向滚动') && rejected.text.includes('该帧未加入已收选段'),
    `Reverse review did not identify the rejected frame: ${rejected.text}`);
  assert((await controlBar(appPid, 'scroll-capture-finish')).enabled,
    'Reverse review must allow finishing the already accepted segment.');
  const offset = await controlBar(appPid, 'scroll-capture-offset');
  assert(!offset.found || offset.right <= offset.left || offset.bottom <= offset.top,
    'Reverse review exposed an impossible positive-offset correction.');
  assert.deepEqual(await sessionEvidence(app.page), sessionsBefore,
    'Reverse review published a screenshot before explicit completion.');
  await delay(previewPauseMs);
  await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-confirm' });
  await wheel(-120); // Back to the first accepted viewport: no new strip.
  await delay(stableWaitMs);
  const afterDiscard = await controlBar(appPid, 'scroll-capture-confirm');
  assert(!afterDiscard.found || !afterDiscard.enabled,
    'Discarding the reverse frame did not resume capture.');
  await wheel(-120); // Beyond that viewport: exactly one new strip.
  await delay(stableWaitMs);
  const resumed = await barText(appPid, 'scroll-capture-finish');
  const expectedHeight = opened.selection.height + fixture.wheelStepPixels;
  assert(resumed.text.includes('已收 2 帧') &&
    resumed.text.includes(`${opened.selection.width} × ${expectedHeight}`),
    `Forward capture did not resume from the retained first frame: ${resumed.text}`);
  // Finishing delivers the retained segment directly: the editor is the
  // result preview, so the controller closes with the completed session.
  assert.deepEqual(await sessionEvidence(app.page), sessionsBefore,
    'Capture published output before the finish action.');
  await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-finish' });
  await waitForControlBarGone(appPid, 'scroll-capture-finish', 15000);
  await app.page.waitForFunction((ids) => {
    const session = window.__scrollCaptureCurrentSession;
    return typeof session?.sessionId === 'string' && !ids.includes(session.sessionId);
  }, priorIds, { timeout: 15000 });
  const sessionsAfter = await sessionEvidence(app.page);
  assert.deepEqual(sessionsAfter.slice(0, sessionsBefore.length), sessionsBefore,
    'Finishing a recovered capture rewrote the prior session history.');
  const added = sessionsAfter.slice(sessionsBefore.length);
  const recoveredId = added[0]?.sessionId;
  assert(recoveredId && !priorIds.includes(recoveredId) &&
    added.every((item) => item.sessionId === recoveredId),
    'Explicit completion must create exactly one new screenshot session.');
  await connectEditor(app);
  const outputName = 'scroll-capture-reverse-recovered.png';
  // The save is a terminal export of THIS reverse round's own session (its
  // actual session/revision is recorded below); it is deliberately not
  // compared against the earlier edited-copy session's binary.
  const saved = await saveRecoveredSession(app, outputName, recoveredId);
  const png = saved.savedPng;
  // The earlier full-capture round left the fixture at wheelSteps * step.
  // The reverse/return pair cancels out; only the final forward step grows it.
  const startOffsetPixels = wheelSteps * fixture.wheelStepPixels;
  const analysis = await analyzeStitched(app.page, png, fixture, opened.selection, startOffsetPixels);
  assert.equal(analysis.width, opened.selection.width, 'Recovered output width changed.');
  assert.equal(analysis.height, expectedHeight, 'Recovered output lost its first viewport or new strip.');
  assert.equal(analysis.badCount, 0,
    `Recovered first-frame/new-strip pixels differ: ${JSON.stringify(analysis.bad)}`);
  assert(analysis.minDark >= 8, 'Recovered complete rows lost rendered text.');

  // Preserve the original reverse cancellation check as a separate real round.
  // Its history now includes the completed recovered output above.
  const beforeCancel = await sessionEvidence(app.page);
  const outputBeforeCancel = await screenshotOutput(app.page);
  await startScrollSession(app, fixture);
  await pickDirection(appPid);
  await waitForFirstFrameAccepted(appPid);
  await wheel(120);
  await waitForCapturePreview(appPid, '丢弃反向帧并继续');
  await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-cancel' });
  await waitForControlBarGone(appPid, 'scroll-capture-finish', 10000);
  await assertStoppedOutput(app, outputBeforeCancel);
  assert.deepEqual(await sessionEvidence(app.page), beforeCancel,
    'Cancelling reverse review created or changed a screenshot session.');
  return {
    rejected, discardAction: discard.name, reviewPauseMs: previewPauseMs, resumed: resumed.text,
    sessionId: recoveredId, startOffsetPixels, stitched: analysis,
    output: saved.savedPath,
    nativeSave: { path: saved.savedPath, bytes: saved.savedPng.length,
      equalsSaveUpload: true, uploadBytes: saved.uploadPng.length,
      session: saved.session, saveTimeline: saved.timeline },
    noOutputBeforeFinish: true, cancelPreservedHistory: true,
  };
}

async function main() {
  assert.equal(process.platform, 'win32', 'Scroll capture smoke requires Windows.');
  const source = fs.realpathSync(option('--product-root'));
  const work = fs.realpathSync(option('--work-root'));
  assert(!contained(source, work) && !contained(work, source),
    'ProductRoot and WorkRoot must not nest.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
    'runtime/backend/runtime-manifest.json', 'runtime/env/runtime-env/python.exe'])
    assert(fs.statSync(path.join(source, marker)).isFile(), `Candidate marker missing: ${marker}`);
  assert(!fs.existsSync(path.join(source, 'state')),
    'Source candidate already has state; refusing to copy it.');
  assert(fs.existsSync(fixtureScript), 'Scroll capture fixture script is missing.');
  const preparedCandidateRoot = optionalOption('--prepared-candidate');
  // Prepared mode reuses one retained isolated product candidate; the
  // evidence root and its WebView2 profile are unique per run so neither the
  // retained candidate state nor any previous evidence is overwritten.
  const smokeRoot = preparedCandidateRoot
    ? path.join(work, `vibeocr-scroll-evidence-${crypto.randomUUID()}`)
    : path.join(work, `vibeocr-scroll-capture-${crypto.randomUUID()}`);
  const candidate = preparedCandidateRoot
    ? validatePreparedCandidate(source, preparedCandidateRoot, work)
    : path.join(smokeRoot, 'candidate');
  const webviewData = path.join(smokeRoot, preparedCandidateRoot ? 'scroll-webview2' : 'webview2');
  const instanceId = crypto.randomUUID().replaceAll('-', '');
  fs.mkdirSync(smokeRoot);
  if (!preparedCandidateRoot)
    fs.cpSync(source, candidate, { recursive: true, errorOnExist: true, force: false });
  let app, fixture;
  const gaps = [];
  const evidence = { schema_version: 1, state: 'failed', smokeRoot, appPids: [] };
  if (preparedCandidateRoot)
    evidence.preparedCandidate = {
      root: candidate, shellOnly: false, smokeMode: 'native-actions-e2e',
      note: 'Real Runtime started from the retained isolated product candidate state.',
    };
  try {
    app = await launchApp(candidate, webviewData, instanceId,
      { shellOnly: !preparedCandidateRoot });
    app.evidence = evidence;
    evidence.taskbarStateBefore = Number(await native('taskbar-state'));
    evidence.appPids.push(app.child.pid);
    await watchScreenshotSession(app.page);
    await app.page.getByRole('button', { name: '截图', exact: true }).waitFor({ timeout: 20000 });
    fixture = await startFixture();
    evidence.fixture = {
      pid: fixture.pid, rootHandle: fixture.root, rootRect: fixture.rootRect,
      clientRect: fixture.clientRect, rows: fixture.rows, rowHeight: fixture.rowHeight,
      stripeWidth: fixture.stripeWidth, textLeft: fixture.textLeft,
      wheelStepPixels: fixture.wheelStepPixels,
      rowEncoding: 'stripe RGB encodes row index: R=row&0xFF, G=(row>>8)&0xFF, B=0x40; ' +
        'synthetic table: first column "行 NNNN / Row", second column "中文 · line NNNN · VibeOCR"',
    };
    evidence.stableWaitMs = stableWaitMs;
    evidence.previewPauseMs = previewPauseMs;
    evidence.wheelSteps = wheelSteps;

    // 0) Prepared warmup: retained state alone proves no live Supervisor; one
    // real public 单次识别→从剪贴板 OCR over this run's own fixture image
    // proves the connection before 取字. Its adopted legacy session stays as
    // the REAL baseline: cancels must preserve it verbatim, and scroll-session
    // assertions only count sessions beyond that baseline.
    if (preparedCandidateRoot) {
      const warmupImage = path.join(smokeRoot, 'source-warmup.png');
      await native('frame', { FixturePid: fixture.pid, Handle: fixture.root,
        X: fixture.clientRect.left + 16, Y: fixture.clientRect.top + 16,
        Width: Math.min(400, fixture.clientRect.width - 32),
        Height: Math.min(240, fixture.clientRect.height - 32),
        EvidenceRoot: smokeRoot, OutputPath: warmupImage });
      await setClipboardImage(smokeRoot, warmupImage);
      await app.page.getByRole('button', { name: '从剪贴板', exact: true }).click();
      const warmupHandle = await app.page.waitForFunction(() => {
        const state = window.__scrollCaptureRecognition;
        return state && state.isBusy === false &&
          ['recognition.completed', 'recognition.failed',
            'recognition.modeUnavailable', 'recognition.expired',
            'recognition.cancelled'].includes(state.statusCode);
      }, null, { timeout: recognitionTimeoutMs });
      const warmupState = await warmupHandle.jsonValue();
      assert.equal(warmupState.statusCode, 'recognition.completed',
        `Clipboard warmup OCR did not complete: ${warmupState.statusCode}.`);
      assert(typeof warmupState.result?.url === 'string' && warmupState.result.url.length > 0,
        'Clipboard warmup OCR completed without a result resource.');
      const warmupResource = new URL(warmupState.result.url);
      assert(warmupResource.origin === 'https://app.vibeocr' &&
          warmupResource.pathname.startsWith('/__resource/'),
        'Clipboard warmup result is not a product __resource URL.');
      const warmupText = await app.page.evaluate(async (url) =>
        (await (await fetch(url, { cache: 'no-store' })).text()), warmupState.result.url);
      const warmupRows = [...detectedRowNumbers(normalizeOcrText(warmupText), fixture.rows)];
      assert(warmupRows.length >= 1,
        `Clipboard warmup OCR covered no fixture rows: ${warmupText.slice(0, 120)}`);
      fs.writeFileSync(path.join(smokeRoot, 'scroll-warmup-result.txt'), warmupText);
      evidence.runtimeWarmup = {
        flow: 'public 单次识别→从剪贴板 over this run\'s own fixture frame',
        statusCode: warmupState.statusCode, resultChars: warmupText.length,
        coveredRows: warmupRows.slice(0, 12),
      };
    }
    // Real session baseline (includes the prepared warmup's adopted legacy
    // session): never cleared - cancel rounds below must preserve it verbatim.
    const baselineSessions = await sessionEvidence(app.page);
    const baselineSessionIds = [...new Set(baselineSessions.map((item) => item.sessionId))];

    // 1) Cancel before any scroll output: the preserved baseline must survive
    // verbatim and no scroll session may open beyond it.
    const cancelledFirst = await runScrollCapture(app, fixture, { cancel: true });
    assert.deepEqual(await sessionEvidence(app.page), baselineSessions,
      'Cancelled scroll capture changed the preserved session baseline.');
    evidence.cancelBeforeOutput = {
      cancelled: true, preservedOutput: cancelledFirst.preservedOutput, overlayHandle: cancelledFirst.overlayHandle,
      barTexts: cancelledFirst.barTexts,
    };

    // 2) Full capture: real wheel steps, exact stitched document.
    evidence.memoryBeforeCapture = JSON.parse(await native('metrics', { AppPid: app.child.pid }));
    const captureStarted = performance.now();
    const captured = await runScrollCapture(app, fixture, { cancel: false });
    evidence.captureWallMs = performance.now() - captureStarted;
    evidence.memoryAfterCapture = JSON.parse(await native('metrics', { AppPid: app.child.pid }));
    evidence.captureTimingScope = 'Public selection/start/manual wheel/finish, includes UIA helper and deliberate settling waits';
    evidence.selection = captured.selection;
    evidence.controlBarTexts = captured.barTexts;
    evidence.overlayHandle = captured.overlayHandle;
    const distinctBarTexts = new Set(captured.barTexts.map((item) => item.text)
      .filter((text) => text.length > 0));
    assert(distinctBarTexts.size >= 2,
      'Scroll control bar did not show changing capture state.');

    // The editor stays open: the original stitched image is read from the
    // product's own session-bound input resource (raw product pixels, not a
    // screen re-capture). Terminal Copy/Save close the scene (#318/#320
    // product semantics), so each real export runs later on its own scene.
    const original = await fetchScrollOriginal(app);
    const originalRgba = await decodeImageRgba(app.page, original.bytes, original.mediaType);
    evidence.scrollOriginal = { sessionId: original.sessionId, revision: original.revision,
      url: original.url, mediaType: original.mediaType, bytes: original.bytes.length };
    fs.writeFileSync(path.join(smokeRoot,
      `scroll-capture-stitched-original.${original.mediaType === 'image/png' ? 'png' : 'bmp'}`), original.bytes);
    const analysis = await analyzeStitched(app.page, original.bytes, fixture,
      captured.selection, 0, original.mediaType);
    evidence.stitched = analysis;
    const expectedHeight = captured.selection.height + wheelSteps * fixture.wheelStepPixels;
    assert.equal(analysis.width, captured.selection.width,
      `Stitched width ${analysis.width} does not match the selection width.`);
    assert.equal(analysis.height, expectedHeight,
      `Stitched height ${analysis.height} does not match selection ${captured.selection.height} ` +
      `plus ${wheelSteps}x${fixture.wheelStepPixels} scrolled rows.`);
    assert(analysis.height > captured.selection.height,
      'Stitched image is not taller than the selection.');
    assert.equal(analysis.badCount, 0, `Stitched rows decode mismatch: ${JSON.stringify(analysis.bad)}`);
    const startY = captured.selection.top - fixture.clientRect.top;
    const maxRow = Math.floor((analysis.height - 1 + startY) / fixture.rowHeight);
    assert.equal(analysis.rows.length, maxRow + 1,
      'Stitched document misses encoded rows (missing or extra blocks).');
    for (const row of analysis.rows) {
      const expectedPixels = Math.min(analysis.height, (row.row + 1) * fixture.rowHeight - startY) -
        Math.max(0, row.row * fixture.rowHeight - startY);
      assert.equal(row.pixels, expectedPixels,
        `Row ${row.row} contributes ${row.pixels} pixels; expected ${expectedPixels}.`);
    }
    assert(analysis.minDark >= 8,
      'Some stitched row blocks carry no rendered text pixels (content lost or duplicated).');

    // 3) Same session editing: an annotation bumps the revision without a new session.
    const sessionsBeforeEdit = await sessionEvidence(app.page);
    assert(sessionsBeforeEdit.length >= 1 && sessionsBeforeEdit.at(-1).sessionId,
      'Scroll capture did not open a screenshot session.');
    const scrollSessionIds = new Set(sessionsBeforeEdit
      .filter((item) => !baselineSessionIds.includes(item.sessionId))
      .map((item) => item.sessionId));
    assert.equal(scrollSessionIds.size, 1,
      'More than one scroll screenshot session was broadcast beyond the preserved baseline.');
    const scrollSessionId = [...scrollSessionIds][0];
    const revisionBefore = sessionsBeforeEdit.at(-1).revision ?? 0;
    const editorPage = app.editorPage;
    await editorPage.getByRole('button', { name: '矩形', exact: true }).click();
    // Explicitly select the existing public orange swatch so the exported
    // stroke color is known (#f38b35) and the annotated-pixel judge below can
    // fail closed on an unannotated or misplaced export.
    await editorPage.getByRole('button', { name: '常用颜色 #f38b35' }).click();
    await editorPage.getByRole('combobox', { name: '线条宽度' }).selectOption('8');
    const box = await editorPage.getByLabel('图片检查画布').boundingBox();
    assert(box && box.width > 100 && box.height > 100, 'Editor canvas has no usable bounds.');
    // Map expected natural-image fractions through the editor's real fit
    // (logical coordinate space + centering, then the CSS bounding box);
    // the canvas backing store is deliberately NOT assumed to equal the image.
    const canvasGeometry = await editorPage.getByLabel('图片检查画布').evaluate(
      (canvas) => ({ logical: {
        width: Number(canvas.dataset.coordinateWidth) || canvas.width,
        height: Number(canvas.dataset.coordinateHeight) || canvas.height,
      } }));
    const injection = planAnnotationInjection({
      fractions: { x0: 0.35, y0: 0.45, x1: 0.65, y1: 0.55 },
      natural: { width: analysis.width, height: analysis.height },
      logical: canvasGeometry.logical,
      css: { x: box.x, y: box.y, width: box.width, height: box.height },
    });
    await editorPage.mouse.move(injection.start.x, injection.start.y);
    await editorPage.mouse.down();
    await editorPage.mouse.move(injection.end.x, injection.end.y);
    await editorPage.mouse.up();
    await app.page.waitForFunction((before) => {
      const sessions = window.__scrollCaptureSessions ?? [];
      const last = sessions.at(-1);
      return last && Number.isSafeInteger(last.revision) && last.revision > before;
    }, revisionBefore, { timeout: 8000 });
    const sessionsAfterEdit = await sessionEvidence(app.page);
    assert(sessionsAfterEdit.every((item) => item.sessionId === scrollSessionId ||
        baselineSessionIds.includes(item.sessionId)),
      'Editing opened or switched the screenshot session.');
    const annotationRect = injection.naturalRect;
    evidence.annotation = { color: '#f38b35', strokeWidth: 8, rect: annotationRect,
      geometry: { logical: canvasGeometry.logical,
        css: { x: box.x, y: box.y, width: box.width, height: box.height },
        image: { width: analysis.width, height: analysis.height },
        ...injection.layout } };
    evidence.session = {
      sessionId: scrollSessionId,
      revisionBefore, revisionAfter: sessionsAfterEdit.at(-1).revision,
      zeroAutoOcr: 'UNVERIFIED in this harness; covered by ScreenshotSessionWorkbenchTests',
    };
    await editorPage.screenshot({ path: path.join(smokeRoot, 'scroll-capture-editor.png') });

    // 3b) Prepared-candidate mode only: real Runtime text layer plus explicit
    // OCR over the same stitched final image, before the cancel phase runs.
    if (preparedCandidateRoot) {
      const verified = await verifyPreparedCandidateRecognition(app, {
        evidenceRoot: smokeRoot,
        fixture,
        selection: captured.selection,
        analysis,
        sessionId: scrollSessionId,
        revision: sessionsAfterEdit.at(-1).revision,
        originalRgba,
        annotationRect,
      });
      evidence.textLayer = verified.textLayer;
      evidence.explicitRecognition = verified.recognition;
    }

    // 4) Cancel after a finished output: the previous output must not become a new session.
    const cancelledAgain = await runScrollCapture(app, fixture, { cancel: true });
    const sessionsAfterCancel = await sessionEvidence(app.page);
    assert(sessionsAfterCancel.every((item) => item.sessionId === scrollSessionId ||
        baselineSessionIds.includes(item.sessionId)),
      'Cancelled capture incorrectly published the previous image as a new session.');
    evidence.cancelAfterOutput = { cancelled: true, preservedOutput: cancelledAgain.preservedOutput,
      barTexts: cancelledAgain.barTexts };

    // 4b) Shell-only terminal copy: after the cancelled round proved the
    // session/annotation survived, the real Copy closes the scene and the
    // Clipboard itself is proven through the public paste readback.
    if (!preparedCandidateRoot) {
      assert(app.editorPage && !app.editorPage.isClosed(),
        'Cancelled capture round lost the open screenshot editor.');
      const editedSession = (await screenshotOutput(app.page)).session;
      assert.equal(editedSession?.sessionId, scrollSessionId,
        'Terminal copy must still address the edited screenshot session.');
      assert(Number.isSafeInteger(editedSession.revision) &&
          editedSession.revision > revisionBefore,
        'Terminal copy must run on the annotated revision.');
      evidence.terminalCopy = await terminalCopyAndReadback(app, {
        analysis, annotationRect, originalRgba });
    }

    evidence.reverseRecovery = await verifyReverseRecovery(app, fixture);
    evidence.reverseRejected = evidence.reverseRecovery.rejected;
    evidence.nativeSave = evidence.reverseRecovery.nativeSave;
    evidence.memoryAfterCancel = JSON.parse(await native('metrics', { AppPid: app.child.pid }));

    // 5) Controller negatives through the owned fixture scenario switch: real
    // error text, disabled 完成, and cancels that open no new session.
    evidence.controllerFailures = await verifyControllerFailureModes(app, fixture);

    evidence.taskbarStateAfter = Number(await native('taskbar-state'));
    assert.equal(evidence.taskbarStateAfter, evidence.taskbarStateBefore,
      'Capture completion/cancellation changed the Windows taskbar preference.');
    evidence.state = 'passed';
    console.log(`Scroll capture E2E passed; evidence: ${smokeRoot}`);
  } catch (error) {
    evidence.error = `${error.name}: ${error.message}`;
    throw error;
  } finally {
    const cleanupErrors = [];
    if (fixture) {
      try { await stopOwned(fixture.child, 'close', {
        AppPid: fixture.pid, Handle: fixture.root,
      }); } catch (error) { cleanupErrors.push(error); }
    }
    if (app) {
      try { await stopOwned(app.child, 'close', {
          AppPid: app.child.pid, Handle: app.main.Handle,
      }); } catch (error) { cleanupErrors.push(error); }
      try { await app.browser.close(); } catch { /* app may already have closed WebView2 */ }
    }
    if (gaps.length) evidence.gaps = gaps;
    if (cleanupErrors.length) {
      evidence.state = 'failed';
      evidence.cleanupError = cleanupErrors.map((error) => error.message).join('; ');
    }
    fs.writeFileSync(path.join(smokeRoot, 'scroll-capture-health.json'),
      JSON.stringify(evidence, null, 2));
    if (cleanupErrors.length) throw cleanupErrors[0];
  }
}

if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(`Scroll capture E2E failed: ${error.name}: ${error.message}`);
    process.exitCode = 1;
  });
}
