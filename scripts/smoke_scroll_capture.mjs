// Run only on an explicitly authorized interactive Windows desktop. This script
// drives the shipped WinUI/WebView2 application with real owned input, a real
// WM_MOUSEWHEEL scroll on its own synthetic window, and UIA limited to the app
// PID. It never performs a desktop-wide UIA text scan and never touches real
// user data: everything lives inside an isolated WorkRoot candidate copy.
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
const selectionInset = 4;
const stableWaitMs = 1350; // >= 1.3 s settle between start/wheel/finish.
const wheelSteps = 3;
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

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

// Collect every screenshot-session state broadcast so the harness can prove the
// cancel paths never open a new session or reuse the previous output as one.
async function watchScreenshotSession(page) {
  await page.evaluate(() => {
    window.__scrollCaptureSessions = [];
    window.chrome.webview.addEventListener('message', ({ data }) => {
      if (data?.kind !== 'event' || data?.type !== 'app.state' ||
          data.payload?.scope !== 'recognition') return;
      const session = data.payload.state?.screenshotSession;
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

async function dragSelection(app, fixture, selection) {
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
  await app.page.getByRole('button', { name: '长截图' }).click();
  const overlays = await waitForWindows(app.child.pid, isOverlay(app, fixture), 15000);
  const overlay = overlays.sort((a, b) => area(b) - area(a))[0];
  const selection = {
    left: fixture.clientRect.left + selectionInset,
    top: fixture.clientRect.top + selectionInset,
    right: fixture.clientRect.right - selectionInset,
    bottom: fixture.clientRect.bottom - selectionInset,
  };
  selection.width = selection.right - selection.left;
  selection.height = selection.bottom - selection.top;
  await dragSelection(app, fixture, selection);
  const start = await waitForControlBar(app.child.pid, 'scroll-capture-start', 15000);
  assert(start.enabled, 'scroll-capture-start is disabled.');
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

  await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-start' });
  // The source window must be activated by the controller before scrolling.
  const until = Date.now() + 10000;
  for (;;) {
    let foreground;
    try { foreground = Number(await native('foreground', { FixturePid: fixture.pid })); }
    catch { foreground = 0; }
    if (foreground === fixture.root) break;
    if (Date.now() > until)
      throw new Error('Controller did not activate the scroll source window.');
    await delay(180);
  }
  const during = await windows(appPid);
  assert(!during.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Main window stayed visible while the scroll controller owned the screen.');
  await recordBar('started');
  await delay(stableWaitMs);

  const steps = [];
  const wheelX = fixture.clientRect.left + Math.floor(fixture.clientRect.width / 2);
  const wheelY = fixture.clientRect.top + Math.floor(fixture.clientRect.height / 2);
  for (let step = 1; step <= wheelSteps; step++) {
    await native('wheel', {
      FixturePid: fixture.pid, Handle: fixture.root, X: wheelX, Y: wheelY, Delta: -120,
    });
    await delay(stableWaitMs);
    await recordBar(`wheel-${step}`);
    steps.push(step);
  }
  const finish = await controlBar(appPid, 'scroll-capture-finish');
  assert(finish.found && finish.enabled, 'scroll-capture-finish is missing or disabled.');
  await native('uia-invoke', { AppPid: appPid, AutomationId: 'scroll-capture-finish' });
  await waitForControlBarGone(appPid, 'scroll-capture-start', 15000);
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
  const smokeRoot = path.join(work, `vibeocr-scroll-capture-${crypto.randomUUID()}`);
  const candidate = path.join(smokeRoot, 'candidate');
  const webviewData = path.join(smokeRoot, 'webview2');
  const instanceId = crypto.randomUUID().replaceAll('-', '');
  fs.mkdirSync(smokeRoot);
  fs.cpSync(source, candidate, { recursive: true, errorOnExist: true, force: false });
  let app, fixture;
  const gaps = [];
  const evidence = { schema_version: 1, state: 'failed', smokeRoot, appPids: [] };
  try {
    app = await launchApp(candidate, webviewData, instanceId);
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
        'row text "行 NNNN · line NNNN · VibeOCR 滚动拼接校验 ScrollStitch abc-0123456789"',
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
    const captured = await runScrollCapture(app, fixture, { cancel: false });
    evidence.selection = captured.selection;
    evidence.controlBarTexts = captured.barTexts;
    evidence.overlayHandle = captured.overlayHandle;
    const distinctBarTexts = new Set(captured.barTexts.map((item) => item.text)
      .filter((text) => text.length > 0));
    assert(distinctBarTexts.size >= 2,
      'Scroll control bar did not show changing capture state.');

    const png = await copySessionPng(app, smokeRoot);
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

    // 4) Cancel after a finished output: the previous output must not become a new session.
    const cancelledAgain = await runScrollCapture(app, fixture, { cancel: true });
    await app.page.waitForFunction(() => window.__scrollCaptureCurrentSession === null);
    const sessionsAfterCancel = await sessionEvidence(app.page);
    assert(sessionsAfterCancel.every((item) => item.sessionId === sessionsBeforeEdit[0].sessionId),
      'Cancelled capture incorrectly published the previous image as a new session.');
    evidence.cancelAfterOutput = { cancelled: true, barTexts: cancelledAgain.barTexts };

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
