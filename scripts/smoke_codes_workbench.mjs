// Explicit interactive Windows smoke; all inputs and clipboard images are synthetic.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { promisify } from 'node:util';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import crypto from 'node:crypto';
const execFileAsync = promisify(execFile);
const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..');
const requireWebAssets = createRequire(path.join(repoRoot, 'src/dotnet/VibeOCR.App/WebAssets/package.json'));
const { chromium, expect } = requireWebAssets('@playwright/test');
const nativeScript = path.join(scriptDir, 'native_actions_fixture.ps1');
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

async function foreground(app) {
  const r = app.main.Bounds;
  await native('focus-fixture', { FixturePid: app.child.pid, Handle: app.main.Handle,
    X: r.Left + Math.floor((r.Right - r.Left) / 2), Y: r.Top + 20 });
  assert.equal(Number(await native('foreground', { AppPid: app.child.pid })), app.main.Handle);
}

async function preview(page) {
  const image = page.getByAltText('当前二维码与条码预览');
  await expect(image).toBeVisible();
  return image.evaluate(async (element) => {
    const response = await fetch(element.src);
    if (!response.ok) throw new Error(`Preview resource failed: ${response.status}`);
    const bytes = await response.arrayBuffer();
    if (bytes.byteLength > 8 * 1024 * 1024) throw new Error('Synthetic image exceeds 8 MiB.');
    const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
    const canvas = document.createElement('canvas');
    canvas.width = bitmap.width; canvas.height = bitmap.height;
    const context = canvas.getContext('2d');
    context.drawImage(bitmap, 0, 0);
    const pixels = Array.from(context.getImageData(0, 0, bitmap.width, bitmap.height).data);
    bitmap.close();
    return { url: element.src, width: canvas.width, height: canvas.height,
      bytes: Array.from(new Uint8Array(bytes)), pixels };
  });
}

async function runtimeDecode(file) {
  const python = 'import json,sys; from vibeocr.runtime.codes.qrcode_decode_service import QrcodeDecodeService; print(json.dumps([{ "data": x.data, "format": x.type } for x in QrcodeDecodeService().decode_file(sys.argv[1])]))';
  const { stdout } = await execFileAsync('uv', ['run', '--frozen', '--no-sync', 'python', '-c', python, file],
    { cwd: repoRoot, windowsHide: true, timeout: 30000, encoding: 'utf8' });
  return JSON.parse(stdout.trim());
}

async function main() {
  assert.equal(process.platform, 'win32', 'Code workbench smoke requires Windows.');
  const source = fs.realpathSync(option('--product-root'));
  const work = fs.realpathSync(option('--work-root'));
  assert(!contained(source, work) && !contained(work, source), 'ProductRoot and WorkRoot must not nest.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
    'runtime/backend/runtime-manifest.json', 'runtime/installer/vibeocr-runtime-installer.exe'])
    assert(fs.statSync(path.join(source, marker)).isFile(), `Candidate marker missing: ${marker}`);
  assert(!fs.existsSync(path.join(source, 'state')), 'Refusing source candidate with user or prior smoke state.');
  const smokeRoot = path.join(work, `vibeocr-codes-${crypto.randomUUID()}`);
  fs.mkdirSync(smokeRoot);
  const candidate = path.join(smokeRoot, 'candidate');
  fs.cpSync(source, candidate, { recursive: true, errorOnExist: true, force: false });
  const evidence = { schema_version: 1, state: 'failed', smokeRoot, cases: [],
    desktopRuntime: 'shell-only; unavailable path checked',
    decoder: 'real source Runtime QrcodeDecodeService/pyzbar; separate from desktop HTTP',
    commonWindowsAppPaste: 'not covered by this script' };
  let app;
  try {
    app = await launchApp(candidate, path.join(smokeRoot, 'webview2'), crypto.randomUUID().replaceAll('-', ''));
    evidence.appPid = app.child.pid;
    await foreground(app);
    const page = app.page;
    await page.getByRole('link', { name: '二维码与条码', exact: true }).click();
    await page.getByRole('heading', { name: '二维码与条码', exact: true }).waitFor();
    for (const test of [
      { format: 'qrcode', input: '中文🙂 hello QR', expected: '中文🙂 hello QR', caption: 'custom' },
      { format: 'code128', input: 'VIBE-128', expected: 'VIBE-128', caption: 'off' },
      { format: 'ean13', input: '590123412345', expected: '5901234123457', caption: 'payload' },
    ]) {
      await native('foreground', { AppPid: app.child.pid });
      await page.getByRole('tab', { name: '生成', exact: true }).click();
      await page.getByLabel('生成格式').selectOption(test.format);
      await page.getByLabel('输入内容', { exact: true }).fill(test.input);
      await page.getByLabel('底部文字', { exact: true }).selectOption(test.caption);
      if (test.caption === 'custom') await page.getByLabel('独立说明', { exact: true }).fill('合成图片说明\n中文与换行不会改变 payload。');
      const oldImage = page.getByAltText('当前二维码与条码预览');
      const oldUrl = await oldImage.count() ? await oldImage.getAttribute('src') : null;
      await page.getByRole('button', { name: '生成图片', exact: true }).click();
      await page.waitForFunction((old) => {
        const img = document.querySelector('img[alt="当前二维码与条码预览"]');
        return img && img.src !== old && img.complete && img.naturalWidth > 0;
      }, oldUrl, { timeout: 15000 });
      const generated = await preview(page);
      const file = path.join(smokeRoot, `${test.format}-generated.png`);
      fs.writeFileSync(file, Buffer.from(generated.bytes));
      const decoded = await runtimeDecode(file);
      assert(decoded.some((item) => item.data === test.expected), `Real Runtime failed ${test.format}: ${JSON.stringify(decoded)}`);
      await native('foreground', { AppPid: app.child.pid });
      await page.getByRole('button', { name: '复制图片', exact: true }).click();
      await page.getByText('已复制当前预览图片', { exact: true }).waitFor();
      // Never inspect an existing user clipboard: this paste occurs only after our own successful copy.
      await page.getByRole('tab', { name: '识别', exact: true }).click();
      await page.getByText('图片识别需要识别运行环境，请启动或恢复后重试', { exact: true }).waitFor();
      await page.getByRole('button', { name: '粘贴图片', exact: true }).click();
      await page.waitForFunction((old) => {
        const img = document.querySelector('img[alt="当前二维码与条码预览"]');
        return img && img.src !== old && img.complete && img.naturalWidth > 0;
      }, generated.url, { timeout: 15000 });
      const pasted = await preview(page);
      assert.equal(pasted.width, generated.width); assert.equal(pasted.height, generated.height);
      assert(Buffer.from(pasted.pixels).equals(Buffer.from(generated.pixels)), 'Native clipboard roundtrip changed final pixels.');
      const pastedFile = path.join(smokeRoot, `${test.format}-clipboard.png`);
      fs.writeFileSync(pastedFile, Buffer.from(pasted.bytes));
      const pastedDecoded = await runtimeDecode(pastedFile);
      assert(pastedDecoded.some((item) => item.data === test.expected), 'Clipboard image no longer decodes to actual payload.');
      await page.screenshot({ path: path.join(smokeRoot, `${test.format}-workbench.png`) });
      evidence.cases.push({ ...test, width: generated.width, height: generated.height,
        nativeClipboardPixelsEqual: true, decoded, pastedDecoded });
    }
    // Invalid generation must show a local message and keep the same preview.
    await page.getByRole('tab', { name: '生成', exact: true }).click();
    const retained = await preview(page);
    await page.getByLabel('输入内容', { exact: true }).fill('5901234123450');
    await page.getByRole('button', { name: '生成图片', exact: true }).click();
    await page.getByRole('alert').filter({ hasText: '校验位不正确' }).waitFor();
    assert.equal((await preview(page)).url, retained.url);
    evidence.invalidEanRetainsPreview = true;
    evidence.state = 'passed';
    console.log(`Codes workbench synthetic smoke passed: ${smokeRoot}`);
  } catch (error) {
    evidence.error = `${error.name}: ${error.message}`;
    throw error;
  } finally {
    if (app) {
      try {
        await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: app.main.Handle });
      } catch (error) { evidence.state = 'failed'; evidence.cleanupError = error.message; process.exitCode = 1; }
      await app.browser.close().catch(() => {});
    }
    fs.writeFileSync(path.join(smokeRoot, 'codes-health.json'), JSON.stringify(evidence, null, 2));
  }
}
main().catch((error) => { console.error(`Codes workbench smoke failed: ${error.message}`); process.exitCode = 1; });