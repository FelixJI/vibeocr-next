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

const execFileAsync = promisify(execFile);
const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const requireWebAssets = createRequire(path.resolve(scriptDir,
  '../src/dotnet/VibeOCR.App/WebAssets/package.json'));
const { chromium, expect } = requireWebAssets('@playwright/test');
const fixtureScript = path.join(scriptDir, 'scroll_capture_fixture.ps1');
const selectionInset = 16;
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
// the argument must be the retained managed-environment candidate strictly
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
    'PreparedCandidateRoot must live in a ve-12hex managed-environment smoke root.');
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
    await forceStop(child);
    throw error;
  }
}

async function waitExit(child, timeoutMs) {
  if (!child || child.exitCode !== null) return true;
  return Promise.race([
    once(child, 'exit').then(() => true),
    delay(timeoutMs).then(() => false),
  ]);
}

async function forceStop(child) {
  if (!child?.pid || child.exitCode !== null) return;
  try {
    await execFileAsync('taskkill', ['/PID', String(child.pid), '/T', '/F'], {
      timeout: 10000, windowsHide: true,
    });
  } catch (error) {
    if (child.exitCode === null) {
      child.kill();
      await waitExit(child, 5000);
      throw new Error(`Owned process tree cleanup was not confirmed for ${child.pid}: ${error.message}`);
    }
  }
  assert(await waitExit(child, 5000), `Owned process ${child.pid} survived cleanup.`);
}

async function stopOwned(child, closeAction, options) {
  if (!child || child.exitCode !== null) return;
  try { await native(closeAction, options); } catch { /* bounded PID cleanup below */ }
  if (await waitExit(child, 5000)) return;
  await forceStop(child);
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
    try { await forceStop(child); }
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

async function observeNextAnnotationUpload(page) {
  await page.evaluate(() => {
    const originalFetch = window.fetch;
    const observed = { count: 0, mimeType: null, byteLength: null,
      bytes: null, problem: null };
    window.__scrollCaptureUpload = observed;
    window.fetch = function(input, init) {
      if (input === '/__annotation' && init?.method === 'POST') {
        observed.count++;
        try {
          const body = init.body;
          if (!(body instanceof Blob)) observed.problem = 'upload body was not a Blob';
          else {
            observed.mimeType = body.type;
            observed.byteLength = body.size;
            if (body.size > 16 * 1024 * 1024) observed.problem = 'stitched PNG exceeded 16 MiB';
            else body.arrayBuffer().then(
              (buffer) => { observed.bytes = Array.from(new Uint8Array(buffer)); },
              () => { observed.problem = 'Blob observation failed'; },
            );
          }
        } catch {
          observed.problem = 'Blob observation failed';
        }
      }
      return originalFetch.apply(this, arguments);
    };
    window.__scrollCaptureRestoreFetch = () => {
      window.fetch = originalFetch;
      delete window.__scrollCaptureRestoreFetch;
      delete window.__scrollCaptureUpload;
    };
  });
}

// Prepared-candidate mode observes every public /__annotation upload (the
// text-layer preparation and the explicit recognition each export one final
// PNG) without altering the request. Records stay readable after restore.
async function watchAnnotationUploads(page) {
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
              (buffer) => { entry.bytes = Array.from(new Uint8Array(buffer)); },
              () => { entry.problem = 'Blob observation failed'; },
            );
          }
        } catch {
          entry.problem = 'Blob observation failed';
        }
      }
      return originalFetch.apply(this, arguments);
    };
    window.__scrollCaptureRestoreUploads = () => {
      window.fetch = originalFetch;
      delete window.__scrollCaptureRestoreUploads;
    };
  });
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
  return (item) => item.Visible && item.Handle !== app.main.Handle &&
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
  // The production picker may keep a preview after drag-release; one real
  // Enter confirms it, mirroring the owned keyboard confirm mode.
  await delay(1200);
  app.evidence.selectionText = JSON.parse(await native('window-text', { AppPid: appPid, Handle: overlayHandle }));
  const size = app.evidence.selectionText.map((text) => text.match(/^手动 · (\d+) × (\d+) px/)).find(Boolean);
  const point = app.evidence.selectionText.map((text) => text.match(/^(-?\d+), (-?\d+)\n#/)).find(Boolean);
  assert(size && point, 'Picker did not expose a manual selection and physical pointer location.');
  const actual = { width: Number(size[1]), height: Number(size[2]), right: Number(point[1]), bottom: Number(point[2]) };
  actual.left = actual.right - actual.width;
  actual.top = actual.bottom - actual.height;
  for (const edge of ['left', 'top', 'right', 'bottom'])
    assert(Math.abs(actual[edge] - selection[edge]) <= 5, `Picker coordinate mapping differs by more than five pixels at ${edge}.`);
  Object.assign(selection, actual);
  if ((await windows(appPid)).some(isOverlay(app, fixture))) {
    await native('key', { AppPid: appPid, Key: 'enter' });
  }
  const until = Date.now() + 10000;
  while (Date.now() < until) {
    if (!(await windows(appPid)).some(isOverlay(app, fixture))) return;
    await delay(180);
  }
  throw new Error('Region picker overlay did not close after owned selection.');
}

// Opens 长截图, drags the fixed fixture-internal selection (inset from the
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
  await app.page.getByRole('button', { name: '长截图' }).click();
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
  const start = await waitForControlBar(app.child.pid, 'scroll-capture-start', 15000);
  assert(start.enabled, 'scroll-capture-start is disabled.');
  // A topmost source can hide the controller's compositor shadow and mask #127.
  // Keep the real source in the normal window band for capture verification.
  await native('lower', { FixturePid: fixture.pid, Handle: fixture.root });
  assert.equal(Number(await native('taskbar-state')), app.evidence.taskbarStateBefore,
    'Opening the scroll controller changed the Windows taskbar preference.');
  const barBounds = { left: start.left, top: start.top, right: start.right, bottom: start.bottom };
  assert(!rectsOverlap(barBounds, selection),
    'Scroll control bar overlaps the fixed selection region.');
  for (const id of ['scroll-capture-finish', 'scroll-capture-cancel'])
    assert((await controlBar(app.child.pid, id)).found, `Control bar is missing ${id}.`);
  return { overlayHandle: overlay.Handle, selection, start };
}

async function runScrollCapture(app, fixture, { cancel }) {
  const appPid = app.child.pid;
  const opened = await startScrollSession(app, fixture);
  const barTexts = [];
  app.evidence.activeCapture = { selection: opened.selection, barTexts };
  const recordBar = async (phase) => {
    const snapshot = await barText(appPid, 'scroll-capture-start').catch(() => null);
    if (snapshot) barTexts.push({ phase, text: snapshot.text });
  };
  await recordBar('selected');

  if (cancel) {
    await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-cancel' });
    await waitForControlBarGone(appPid, 'scroll-capture-start', 10000);
    const restored = await windows(appPid);
    assert(restored.some((item) => item.Handle === app.main.Handle && item.Visible),
      'Cancelled scroll capture did not restore the main window.');
    return { cancelled: true, barTexts, ...opened };
  }

  const controlWindow = (await windows(appPid)).find((item) => item.Handle === opened.start.windowHandle);
  assert(controlWindow, 'Owned scroll control window is missing.');
  await native('focus', { FixturePid: appPid, Handle: controlWindow.Handle,
    X: controlWindow.Bounds.Left + 80, Y: controlWindow.Bounds.Top + 20 });
  await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-start' });
  await recordBar('start-invoked');
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
  const finish = await controlBar(appPid, 'scroll-capture-finish');
  assert(finish.found && finish.enabled, 'scroll-capture-finish is missing or disabled.');
  const finishingWindow = (await windows(appPid)).find((item) => item.Handle === finish.windowHandle);
  assert(finishingWindow, 'Owned finish window is missing.');
  await native('focus', { FixturePid: appPid, Handle: finish.windowHandle,
    X: finishingWindow.Bounds.Left + 80, Y: finishingWindow.Bounds.Top + 20 });
  await delay(5500); // Pausing on the controls must not contaminate frames or trip dynamic-content timeout.
  await recordBar('control-focused-before-finish');
  assert((await controlBar(appPid, 'scroll-capture-finish')).enabled, 'Focusing capture controls invalidated a stable capture.');
  await native('focus', { FixturePid: appPid, Handle: finish.windowHandle,
    X: Math.floor((finish.left + finish.right) / 2), Y: Math.floor((finish.top + finish.bottom) / 2) });
  try {
    await waitForControlBarGone(appPid, 'scroll-capture-start', 15000);
  } catch (error) {
    await recordBar('finish-failed');
    app.evidence.finishProbe = JSON.parse(await native('probe', { AppPid: appPid, FixturePid: fixture.pid,
      X: opened.selection.left + 100, Y: opened.selection.top + 100 }));
    throw error;
  }
  const restored = await windows(appPid);
  assert(restored.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Finished scroll capture did not restore the main window.');

  await app.page.getByRole('heading', { name: '单次识别' }).waitFor({ timeout: 15000 });
  const editor = app.page.getByLabel('图片检查画布');
  await editor.waitFor({ timeout: 15000 });
  await app.page.waitForFunction(() => {
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

async function copySessionPng(app, evidenceRoot) {
  await observeNextAnnotationUpload(app.page);
  try {
    const requestPromise = app.page.waitForRequest((request) =>
      request.method() === 'POST' && request.url() === 'https://app.vibeocr/__annotation',
    { timeout: 15000 });
    await app.page.getByRole('button', { name: '复制标注图' }).click();
    const request = await requestPromise;
    const response = await request.response();
    assert.equal(response?.status(), 201, 'Public annotation upload was not accepted.');
    await app.page.waitForFunction(() => {
      const upload = window.__scrollCaptureUpload;
      return upload && (Array.isArray(upload.bytes) || upload.problem !== null);
    }, null, { timeout: 8000 });
    const upload = await app.page.evaluate(() => window.__scrollCaptureUpload);
    assert.equal(upload.count, 1, `Expected one product PNG upload, saw ${upload.count}.`);
    assert.equal(upload.problem, null, `Product Blob observation failed: ${upload.problem}.`);
    assert.equal(upload.mimeType, 'image/png', 'Product upload Blob was not image/png.');
    const png = Buffer.from(upload.bytes);
    assert(png.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])),
      'Product upload Blob is not PNG.');
    await app.page.getByText('已复制截图副本', { exact: false }).waitFor({ timeout: 8000 });
    fs.writeFileSync(path.join(evidenceRoot, 'scroll-capture-stitched.png'), png);
    return png;
  } finally {
    await app.page.evaluate(() => window.__scrollCaptureRestoreFetch?.()).catch(() => {});
  }
}

// Decode the stitched PNG inside the real WebView2 context and verify every
// encoded row: the stripe color at stripeX encodes the document row index, so
// order, range, gaps and duplicated rows are all detected pixel-exactly.
async function analyzeStitched(page, png, fixture, selection) {
  return page.evaluate(async ({ encoded, rowHeight, stripeX, textX, startY }) => {
    const bytes = Uint8Array.from(atob(encoded), (letter) => letter.charCodeAt(0));
    const image = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
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
    for (let row = 1; (row + 1) * rowHeight - startY <= height; row++) {
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
    encoded: png.toString('base64'),
    rowHeight: fixture.rowHeight,
    startY: selection.top - fixture.clientRect.top,
    stripeX: Math.max(0, Math.floor(fixture.stripeWidth / 2) - selectionInset),
    textX: fixture.textLeft - selectionInset + 2,
  });
}

// OCR text comparison ignores whitespace/punctuation differences (the fixture
// rows read "行 NNNN / Row" and "中文 · line NNNN · VibeOCR"); row coverage is
// the hard contract, so a missing lower half of the long image cannot pass.
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
    if (snapshot?.statusCode === 'recognition.failed' ||
        snapshot?.statusCode === 'recognition.modeUnavailable' ||
        snapshot?.statusCode === 'recognition.expired')
      throw new Error(`Explicit screenshot recognition ended as ${snapshot.statusCode}.`);
    if (snapshot?.statusCode === 'recognition.completed' && snapshot.result?.url)
      return snapshot;
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
  evidenceRoot, fixture, selection, analysis, sessionId, revision,
}) {
  const { page } = app;
  const startY = selection.top - fixture.clientRect.top;
  const maxRow = Math.floor((analysis.height - 1 + startY) / fixture.rowHeight);
  assert(requiredTopRow <= maxRow && requiredBottomRow <= maxRow,
    'Stitched document does not reach the required coverage rows for this fixture.');
  await watchAnnotationUploads(page);
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
    const uploads = () => page.evaluate(() => window.__scrollCaptureUploads ?? []);
    const uploaded = (await uploads()).find((item) => Array.isArray(item.bytes));
    assert(uploaded, 'Text layer preparation produced no observable PNG upload.');
    assert.equal(uploaded.problem, null,
      `Text layer upload observation failed: ${uploaded.problem}.`);
    assert.equal(uploaded.mimeType, 'image/png', 'Text layer upload was not image/png.');
    const uploadPng = Buffer.from(uploaded.bytes);
    assert(uploadPng.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])),
      'Text layer upload Blob is not PNG.');
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

    await page.getByRole('button', { name: '识别当前图' }).click();
    const completed = await waitForScreenshotRecognition(app, sessionId, revision);
    await page.waitForFunction(() =>
      (document.querySelector('.result-document')?.textContent ?? '').trim().length > 0,
      null, { timeout: 30000 });
    const resultText = await page.evaluate(async (url) =>
      (await (await fetch(url, { cache: 'no-store' })).text()), completed.result.url);
    const resultRows = assertRequiredRows('Explicit recognition',
      normalizeOcrText(resultText), maxRow);
    fs.writeFileSync(path.join(evidenceRoot, 'scroll-recognition-result.txt'), resultText);
    // Both exports must have resolved their async Blob observation before any
    // byte comparison; length equality alone never counts as content equality.
    await page.waitForFunction(() => {
      const uploads = window.__scrollCaptureUploads ?? [];
      return uploads.length >= 2 && uploads.every((item) =>
        Array.isArray(item.bytes) || item.problem !== null);
    }, null, { timeout: 30000 });
    const recognitionUploads = await uploads();
    const recognized = recognitionUploads[recognitionUploads.length - 1];
    assert.equal(recognized?.problem, null,
      `Explicit recognition upload observation failed: ${recognized?.problem}.`);
    const recognizePng = Buffer.from(recognized?.bytes ?? []);
    assert(recognizePng.equals(uploadPng),
      'Explicit recognition exported different final PNG content after the text layer.');
    if (Array.isArray(recognized?.bytes))
      fs.writeFileSync(path.join(evidenceRoot, 'scroll-recognition-input.png'),
        Buffer.from(recognized.bytes));
    await page.screenshot({ path: path.join(evidenceRoot, 'scroll-recognition.png') });
    return {
      textLayer: {
        status: layer.status,
        modeId: layer.modeId,
        binding: layer.binding,
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
    last = await barText(appPid, 'scroll-capture-start').catch(() => null);
    if (last && expectedParts.every((part) => last.text.includes(part))) {
      const finish = await controlBar(appPid, 'scroll-capture-finish');
      assert.equal(finish.enabled, false,
        `${scenario} failure left 完成 enabled.`);
      const start = await controlBar(appPid, 'scroll-capture-start');
      assert.equal(start.enabled, false,
        `${scenario} failure left 开始 enabled.`);
      return last;
    }
    await delay(300);
  }
  throw new Error(`${scenario} controller failure did not report ` +
    `${JSON.stringify(expectedParts)} within ${timeoutMs} ms; last bar text: ` +
    `${last?.text ?? 'unavailable'}.`);
}

// Bounded wait for the first accepted stable frame: 完成 becomes enabled only
// after ReportAccepted, so jump/move are triggered on an existing first frame.
async function waitForFirstFrameAccepted(appPid, timeoutMs = 20000) {
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    if ((await controlBar(appPid, 'scroll-capture-finish')).enabled) return;
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
async function verifyControllerFailureModes(app, fixture, baselineSessionId) {
  const appPid = app.child.pid;
  const setScenario = (scenario) => native('scenario',
    { FixturePid: fixture.pid, Handle: fixture.root, Scenario: scenario });
  const results = [];
  const runNegative = async ({ scenario, before, expectedParts, trigger, restore = true }) => {
    const sessionsBefore = await sessionEvidence(app.page);
    if (before) await setScenario(before);
    await startScrollSession(app, fixture);
    await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-start' });
    if (trigger) await trigger();
    const bar = await waitForControllerError(appPid, scenario, expectedParts,
      controllerErrorTimeoutMs);
    await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-cancel' });
    await waitForControlBarGone(appPid, 'scroll-capture-start', 10000);
    await app.page.waitForFunction(() => window.__scrollCaptureCurrentSession === null);
    const sessionsAfter = await sessionEvidence(app.page);
    assert.equal(sessionsAfter.length, sessionsBefore.length,
      `${scenario} round opened a new screenshot session.`);
    assert(sessionsAfter.every((item) => item.sessionId === baselineSessionId),
      `${scenario} round switched the screenshot session.`);
    if (restore) await setScenario('normal');
    results.push({ scenario, barText: bar.text });
  };

  // All-white content set before start: the first stable frame has no vertical
  // variation, so the stitcher constructor itself fails closed (real text from
  // ScrollCaptureSession/VerticalScrollStitcher, verified against source).
  await runNegative({
    scenario: 'low-texture',
    before: 'low-texture',
    expectedParts: ['滚动截图采集失败', 'no vertical variation'],
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
  await runNegative({
    scenario: 'jump',
    expectedParts: ['新内容与已拼接区域无重叠', '已停止采集'],
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

async function main() {
  assert.equal(process.platform, 'win32', 'Scroll capture smoke requires Windows.');
  const source = fs.realpathSync(option('--product-root'));
  const work = fs.realpathSync(option('--work-root'));
  assert(!contained(source, work) && !contained(work, source),
    'ProductRoot and WorkRoot must not nest.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
    'runtime/backend/runtime-manifest.json', 'runtime/installer/vibeocr-runtime-installer.exe'])
    assert(fs.statSync(path.join(source, marker)).isFile(), `Candidate marker missing: ${marker}`);
  assert(!fs.existsSync(path.join(source, 'state')),
    'Source candidate already has state; refusing to copy it.');
  assert(fs.existsSync(fixtureScript), 'Scroll capture fixture script is missing.');
  const preparedCandidateRoot = optionalOption('--prepared-candidate');
  // Prepared mode reuses one retained managed-environment candidate; the
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
      note: 'Real Runtime started from the retained managed-environment candidate state.',
    };
  try {
    app = await launchApp(candidate, webviewData, instanceId,
      { shellOnly: !preparedCandidateRoot });
    app.evidence = evidence;
    evidence.taskbarStateBefore = Number(await native('taskbar-state'));
    evidence.appPids.push(app.child.pid);
    await watchScreenshotSession(app.page);
    await app.page.getByRole('button', { name: '长截图' }).waitFor({ timeout: 20000 });
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
    evidence.wheelSteps = wheelSteps;

    // 1) Cancel before any output exists: no session may open.
    const cancelledFirst = await runScrollCapture(app, fixture, { cancel: true });
    assert.equal((await sessionEvidence(app.page)).length, 0,
      'Cancelled scroll capture broadcast a screenshot session.');
    evidence.cancelBeforeOutput = {
      cancelled: true, overlayHandle: cancelledFirst.overlayHandle,
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

    const png = await copySessionPng(app, smokeRoot);
    const savedPath = path.join(smokeRoot, 'scroll-capture-native-save.png');
    await app.page.getByRole('button', { name: '保存标注图', exact: true }).click();
    await native('save-file', { AppPid: app.child.pid, Handle: app.main.Handle,
      OutputPath: savedPath, EvidenceRoot: smokeRoot }, 30000);
    const saveDeadline = Date.now() + 15000;
    while (!fs.existsSync(savedPath) && Date.now() < saveDeadline) await delay(150);
    assert(fs.existsSync(savedPath), 'Native file picker did not save the long screenshot.');
    await app.page.getByText('已保存截图副本', { exact: false }).waitFor({ timeout: 15000 });
    const savedPng = fs.readFileSync(savedPath);
    assert(savedPng.equals(png), 'Native saved PNG differs from the same unedited screenshot export.');
    evidence.nativeSave = { path: savedPath, bytes: savedPng.length, equalsCopiedImage: true };
    const analysis = await analyzeStitched(app.page, png, fixture, captured.selection);
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
    assert(sessionsBeforeEdit.length >= 1 && sessionsBeforeEdit[0].sessionId,
      'Scroll capture did not open a screenshot session.');
    const sessionIds = new Set(sessionsBeforeEdit.map((item) => item.sessionId));
    assert.equal(sessionIds.size, 1, 'More than one screenshot session was broadcast.');
    const revisionBefore = sessionsBeforeEdit.at(-1).revision ?? 0;
    await app.page.getByRole('button', { name: '矩形', exact: true }).click();
    await app.page.getByRole('combobox', { name: '线条宽度' }).selectOption('8');
    const box = await app.page.getByLabel('图片检查画布').boundingBox();
    assert(box && box.width > 100 && box.height > 100, 'Editor canvas has no usable bounds.');
    await app.page.mouse.move(box.x + box.width * 0.35, box.y + box.height * 0.45);
    await app.page.mouse.down();
    await app.page.mouse.move(box.x + box.width * 0.65, box.y + box.height * 0.55);
    await app.page.mouse.up();
    await app.page.waitForFunction((before) => {
      const sessions = window.__scrollCaptureSessions ?? [];
      const last = sessions.at(-1);
      return last && Number.isSafeInteger(last.revision) && last.revision > before;
    }, revisionBefore, { timeout: 8000 });
    const sessionsAfterEdit = await sessionEvidence(app.page);
    assert(sessionsAfterEdit.every((item) => item.sessionId === sessionsBeforeEdit[0].sessionId),
      'Editing opened or switched the screenshot session.');
    evidence.session = {
      sessionId: sessionsBeforeEdit[0].sessionId,
      revisionBefore, revisionAfter: sessionsAfterEdit.at(-1).revision,
      zeroAutoOcr: 'UNVERIFIED in this harness; covered by ScreenshotSessionWorkbenchTests',
    };
    await app.page.screenshot({ path: path.join(smokeRoot, 'scroll-capture-editor.png') });

    // 3b) Prepared-candidate mode only: real Runtime text layer plus explicit
    // OCR over the same stitched final image, before the cancel phase runs.
    if (preparedCandidateRoot) {
      const verified = await verifyPreparedCandidateRecognition(app, {
        evidenceRoot: smokeRoot,
        fixture,
        selection: captured.selection,
        analysis,
        sessionId: sessionsBeforeEdit[0].sessionId,
        revision: sessionsAfterEdit.at(-1).revision,
      });
      evidence.textLayer = verified.textLayer;
      evidence.explicitRecognition = verified.recognition;
    }

    // 4) Cancel after a finished output: the previous output must not become a new session.
    const cancelledAgain = await runScrollCapture(app, fixture, { cancel: true });
    await app.page.waitForFunction(() => window.__scrollCaptureCurrentSession === null);
    const sessionsAfterCancel = await sessionEvidence(app.page);
    assert(sessionsAfterCancel.every((item) => item.sessionId === sessionsBeforeEdit[0].sessionId),
      'Cancelled capture incorrectly published the previous image as a new session.');
    evidence.cancelAfterOutput = { cancelled: true, barTexts: cancelledAgain.barTexts };

    // A real reverse wheel step must fail closed, without publishing a partial image.
    await startScrollSession(app, fixture);
    await native('uia-invoke', { AppPid: app.child.pid, AutomationId: 'scroll-capture-start' });
    await delay(stableWaitMs);
    await native('wheel', { FixturePid: fixture.pid, Handle: fixture.root,
      X: fixture.clientRect.left + 100, Y: fixture.clientRect.top + 100, Delta: 120 });
    await delay(stableWaitMs);
    const reversed = await barText(app.child.pid, 'scroll-capture-start');
    assert(reversed.text.includes('反向'), `Reverse scroll did not report its reason: ${reversed.text}`);
    assert.equal((await controlBar(app.child.pid, 'scroll-capture-finish')).enabled, false);
    await native('uia-invoke', { AppPid: app.child.pid, AutomationId: 'scroll-capture-cancel' });
    await waitForControlBarGone(app.child.pid, 'scroll-capture-start', 10000);
    await app.page.waitForFunction(() => window.__scrollCaptureCurrentSession === null);
    assert((await sessionEvidence(app.page)).every((item) => item.sessionId === sessionsBeforeEdit[0].sessionId));
    evidence.reverseRejected = reversed;
    evidence.memoryAfterCancel = JSON.parse(await native('metrics', { AppPid: app.child.pid }));

    // 5) Controller negatives through the owned fixture scenario switch: real
    // error text, disabled 完成, and cancels that open no new session.
    evidence.controllerFailures = await verifyControllerFailureModes(app, fixture,
      sessionsBeforeEdit[0].sessionId);

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
        FixturePid: fixture.pid, Handle: fixture.root,
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

main().catch((error) => {
  console.error(`Scroll capture E2E failed: ${error.name}: ${error.message}`);
  process.exitCode = 1;
});
