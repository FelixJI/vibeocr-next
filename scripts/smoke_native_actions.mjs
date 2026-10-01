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
  const args = ['-NoProfile', '-NonInteractive', '-File', nativeScript,
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
      // Stop our app PID, but do not claim its child process tree was cleaned.
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
  await native('hotkey', { FixturePid: app.child.pid });
  await expect(editInput).toHaveValue(hotkey);
  assert((await windows(app.child.pid)).some(item =>
    item.Handle === app.main.Handle && item.Visible),
  'Recording an already-registered key unexpectedly hid the main window.');
  evidence.stage = 'edit-rerecording-escape';
  await native('escape', { AppPid: app.child.pid });
  await expect(editInput).toHaveValue('');
  await row.getByText(`当前生效：${hotkey}`).waitFor();

  const recognizeRow = page.locator('.hotkey-action-row').filter({ hasText: '快捷截图识别' });
  evidence.stage = 'recognition-recording';
  const recognizeInput = recognizeRow.getByRole('textbox', { name: '快捷截图识别新快捷键' });
  await recognizeInput.click();
  await recognizeRow.getByRole('status').filter({ hasText: '请按下新的组合键' }).waitFor();
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
    window.__nativeActionsEvidence = { sessionId: null, revision: null };
    window.chrome.webview.addEventListener('message', ({ data }) => {
      if (data?.kind !== 'event' || data?.type !== 'app.state' ||
          data?.payload?.scope !== 'recognition') return;
      const session = data.payload.state?.screenshotSession;
      if (typeof session?.sessionId === 'string' && Number.isSafeInteger(session.revision))
        window.__nativeActionsEvidence = {
          sessionId: session.sessionId, revision: session.revision,
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

async function pngPixels(page, png) {
  return page.evaluate(async (encoded) => {
    const bytes = Uint8Array.from(atob(encoded), (letter) => letter.charCodeAt(0));
    const image = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
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
  }, png.toString('base64'));
}

async function observeNextAnnotationUpload(page) {
  await page.evaluate(() => {
    const originalFetch = window.fetch;
    const observed = { count: 0, mimeType: null, byteLength: null,
      bytes: null, problem: null };
    window.__nativeActionsUpload = observed;
    window.fetch = function(input, init) {
      if (input === '/__annotation' && init?.method === 'POST') {
        observed.count++;
        try {
          const body = init.body;
          if (!(body instanceof Blob)) observed.problem = 'upload body was not a Blob';
          else {
            observed.mimeType = body.type;
            observed.byteLength = body.size;
            if (body.size > 8 * 1024 * 1024) observed.problem = 'synthetic PNG exceeded 8 MiB';
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
    window.__nativeActionsRestoreFetch = () => {
      window.fetch = originalFetch;
      delete window.__nativeActionsRestoreFetch;
      delete window.__nativeActionsUpload;
    };
  });
}

async function captureThroughHotkey(app, fixture, evidenceRoot, evidence) {
  const { page } = app;
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
  const sentAt = Date.now();
  await native('hotkey', { FixturePid: fixture.pid });
  evidence.hotkeyDeliveredAt = Date.now() - sentAt;

  const button = fixture.buttonRect;
  const x = button.left + Math.floor(button.width / 2);
  const y = button.top + Math.floor(button.height / 2);
  const overlays = await waitForWindows(app.child.pid,
    (item) => item.Visible && item.Handle !== app.main.Handle &&
      area(item) > fixture.rootRect.width * fixture.rootRect.height * 2 &&
      inside(item, x, y), 12000);
  const overlay = overlays.sort((a, b) => area(b) - area(a))[0];
  assert.equal(overlay.Bounds.Left, fixture.backgroundRect.left);
  assert.equal(overlay.Bounds.Top, fixture.backgroundRect.top);
  assert.equal(overlay.Bounds.Right, fixture.backgroundRect.right);
  assert.equal(overlay.Bounds.Bottom, fixture.backgroundRect.bottom);
  const duringCapture = await windows(app.child.pid);
  assert(!duringCapture.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Edit hotkey exposed the main window during selection.');
  assert(!duringCapture.some((item) => toolbarHandles.includes(item.Handle) && item.Visible),
    'Toolbar or sensor remained visible during capture.');
  evidence.overlayProbe = JSON.parse(await native('probe', {
    AppPid: app.child.pid, FixturePid: fixture.pid, X: x, Y: y,
  }));
  await native('hover', { AppPid: app.child.pid, X: x, Y: y });
  // The production UIA query has a 200 ms budget. One bounded settle period
  // lets the real overlay consume it; no retry or synthetic command injection.
  await delay(350);
  assert.equal(await taskbarState(), evidence.taskbarStateBefore,
    'Screenshot selection changed the Windows taskbar preference.');
  const selectionStates = [];
  for (let index = 0; index < 8; index++) {
    const label = await native('selection', { AppPid: app.child.pid, Handle: overlay.Handle });
    selectionStates.push(label);
    if (index > 0 && label.includes(` · ${button.width} × ${button.height} px · Enter`)) break;
    await native('tab', { AppPid: app.child.pid });
  }
  evidence.smartSelectionStates = selectionStates;
  assert(selectionStates.at(-1)?.includes(` · ${button.width} × ${button.height} px · Enter`),
    `Smart picker did not expose the synthetic button: ${JSON.stringify(selectionStates)}`);
  await native('enter', { AppPid: app.child.pid });

  await page.getByRole('heading', { name: '单次识别' }).waitFor({ timeout: 12000 });
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
  assert(restoredWindows.some((item) => item.Handle === app.main.Handle && item.Visible),
    'Completed edit capture did not show its editor window.');
  assert(toolbarHandles.every((handle) => restoredWindows.some((item) =>
    item.Handle === handle && item.Visible)), 'Toolbar did not resume after capture.');
  const before = await canvasOrange(page);
  assert(before.nonBackground > 1000, 'Real screenshot image never decoded in WebView2.');
  await page.getByRole('button', { name: '矩形', exact: true }).click();
  await page.getByRole('combobox', { name: '线条宽度' }).selectOption('8');
  const box = await editor.boundingBox();
  assert(box && box.width > 100 && box.height > 100, 'Editor canvas has no usable visual bounds.');
  await page.mouse.move(box.x + box.width * 0.35, box.y + box.height * 0.45);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.65, box.y + box.height * 0.55);
  await page.mouse.up();
  await page.waitForFunction(() => window.__nativeActionsEvidence?.revision >= 1,
    null, { timeout: 5000 });
  const after = await canvasOrange(page);
  assert(after.orange > before.orange + 50, 'Real WebView2 rectangle edit changed too few pixels.');

  await observeNextAnnotationUpload(page);
  let png;
  let uploadBytes;
  try {
    const requestPromise = page.waitForRequest((request) =>
      request.method() === 'POST' && request.url() === 'https://app.vibeocr/__annotation',
    { timeout: 10000 });
    await page.getByRole('button', { name: '复制标注图' }).click();
    const request = await requestPromise;
    const response = await request.response();
    assert.equal(response?.status(), 201, 'Public annotation upload was not accepted.');
    // WebView2 CDP may omit a fetch Blob from request.postDataBuffer(). Observe
    // that same immutable Blob without changing the arguments sent to fetch.
    const cdpBytes = request.postDataBuffer()?.length ?? null;
    try {
      await page.waitForFunction(() => {
        const upload = window.__nativeActionsUpload;
        return upload && (Array.isArray(upload.bytes) || upload.problem !== null);
      }, null, { timeout: 5000 });
    } catch (error) {
      const observed = await page.evaluate(() => {
        const upload = window.__nativeActionsUpload;
        return { count: upload?.count ?? 0, mimeType: upload?.mimeType ?? null,
          byteLength: upload?.byteLength ?? null, problem: upload?.problem ?? null };
      }).catch(() => null);
      throw new Error(`Product Blob observation timed out: ${JSON.stringify(observed)}; ` +
        `CDP body bytes=${cdpBytes ?? 'unavailable'}.`, { cause: error });
    }
    const upload = await page.evaluate(() => window.__nativeActionsUpload);
    assert.equal(upload.count, 1,
      `Expected one product PNG upload, saw ${upload.count}; CDP body bytes=${cdpBytes ?? 'unavailable'}.`);
    assert.equal(upload.problem, null,
      `Product Blob observation failed: ${upload.problem}; CDP body bytes=${cdpBytes ?? 'unavailable'}.`);
    assert.equal(upload.mimeType, 'image/png', 'Product upload Blob was not image/png.');
    assert(Array.isArray(upload.bytes) && upload.bytes.length === upload.byteLength,
      'Product upload Blob size changed during observation.');
    png = Buffer.from(upload.bytes);
    uploadBytes = upload.byteLength;
    assert(png.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])),
      `Product upload Blob is not PNG; bytes=${uploadBytes}, CDP body bytes=${cdpBytes ?? 'unavailable'}.`);
    await page.getByText('已复制截图副本', { exact: false }).waitFor();
  } finally {
    await page.evaluate(() => window.__nativeActionsRestoreFetch?.()).catch(() => {});
  }
  fs.writeFileSync(path.join(evidenceRoot, 'synthetic-edited.png'), png);
  await page.screenshot({ path: path.join(evidenceRoot, 'screenshot-editor.png') });
  const pixels = await pngPixels(page, png);
  assert.equal(pixels.width, button.width, 'Smart region did not select the synthetic button width.');
  assert.equal(pixels.height, button.height, 'Smart region did not select the synthetic button height.');
  assert(pixels.orange > 20 && pixels.dark > 10 && pixels.light > 100,
    `Synthetic PNG pixels are incomplete: ${JSON.stringify(pixels)}`);
  const revision = await page.evaluate(() => window.__nativeActionsEvidence);
  assert(revision?.sessionId && revision.revision >= 1,
    'Host screenshot session revision did not advance after editing.');
  return { hotkey, overlayHandle: overlay.Handle,
    mainHiddenDuringSelection: true, toolbarHiddenDuringSelection: true,
    toolbarRestoredAfterSelection: true,
    background: fixture.backgroundRect, button, canvasBefore: before,
    canvasAfter: after, png: pixels, productUploadBytes: uploadBytes,
    sessionId: revision.sessionId,
    revision: revision.revision };
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
  const until = Date.now() + 12000;
  while (Date.now() < until) {
    const current = await windows(appPid);
    if (current.some((item) => item.Handle === mainHandle && item.Visible) &&
        !current.some((item) => item.Handle === overlay.Handle && item.Visible))
      return { hotkey: recognizeHotkey, overlayHandle: overlay.Handle,
        duplicateOverlayCount: afterDuplicate.filter(isOverlay).length,
        mainStayedHidden: true, foregroundStayedOnOverlay: true,
        cancelledBeforeSelection: true };
    await delay(180);
  }
  throw new Error('Recognition overlay did not close and restore the main window after Esc.');
}

async function main() {
  assert.equal(process.platform, 'win32', 'Native actions smoke requires Windows.');
  const source = fs.realpathSync(option('--product-root'));
  const work = fs.realpathSync(option('--work-root'));
  assert(!contained(source, work) && !contained(work, source),
    'ProductRoot and WorkRoot must not nest.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
    'runtime/backend/runtime-manifest.json', 'runtime/installer/vibeocr-runtime-installer.exe'])
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
    await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: firstMain });
    await app.browser.close().catch(() => {});
    app = await launchApp(candidate, webviewData, instanceId);
    evidence.appPids.push(app.child.pid);
    await verifyRestart(app.page);
    await app.page.screenshot({ path: path.join(smokeRoot, 'native-actions-settings.png') });
    fixture = await startFixture();
    evidence.capture = await captureThroughHotkey(app, fixture, smokeRoot, evidence);
    evidence.recognitionHotkeyGuard = await verifyRecognitionHotkeyGuard(app, fixture);
    await openSettings(app.page);
    await setCheckbox(app.page, '启用悬浮工具栏', false);
    await toolbarStatus(app.page, '已关闭');
    evidence.fixturePid = fixture.pid;
    evidence.taskbarStateAfter = await taskbarState();
    assert.equal(evidence.taskbarStateAfter, evidence.taskbarStateBefore,
      'Screenshot completion or cancellation changed the Windows taskbar preference.');
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

main().catch((error) => {
  console.error(`Native actions E2E failed: ${error.name}: ${error.message}`);
  process.exitCode = 1;
});
