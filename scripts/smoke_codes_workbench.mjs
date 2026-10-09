// Explicit interactive Windows smoke; all inputs and clipboard images are synthetic.
//
// Stage 1 (candidate 1, --shell-only): local QR/Code128/EAN-13 generation,
// native clipboard copy/paste pixel roundtrip, source-Runtime pyzbar
// cross-check and local decode of the current preview without a Supervisor.
//
// Stage 2 (fresh candidate 2, no state): the real product first materializes
// its own portable-layout state during a shell-only boot (no manager list),
// then the real frozen Runtime Installer receives the authoritative binding
// request (protocol 2, cpu, install_component_ids=[]) for an offline base
// Ensure into the fresh candidate state. Only then the full App starts; the
// first manager list discovers the ensured base as the active legacy
// environment and launches a real Supervisor. Every recognition result below
// comes from the actual UI backed by the local C# decoder (Windows
// BitmapDecoder + ZXing.Net); the Supervisor lifecycle itself is still
// exercised end to end.
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
const clipboardScript = path.join(scriptDir, 'native_codes_clipboard_fixture.ps1');
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
// Bounded budgets: 30 min mirrors the product's own candidate ensure budget;
// Supervisor startup mirrors the 90 s engine budget plus headroom.
const ensureTimeoutMs = Number(process.env.VIBEOCR_CODES_SMOKE_ENSURE_TIMEOUT_MS || 30 * 60 * 1000);
const supervisorReadyTimeoutMs = 4 * 60 * 1000;
const decodeTimeoutMs = 90 * 1000;
const qrTests = [
  { format: 'qrcode', input: '中文🙂 hello QR', expected: '中文🙂 hello QR', caption: 'custom' },
  { format: 'code128', input: 'VIBE-128', expected: 'VIBE-128', caption: 'off' },
  { format: 'ean13', input: '590123412345', expected: '5901234123457', caption: 'payload' },
];
const statusCopied = '已复制当前预览图片';
const statusDecoded = '识别完成';
const statusNoCodes = '当前预览中未识别到支持的二维码或条码';

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

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
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

async function exitWithin(child, timeoutMs) {
  let timer;
  try {
    return await Promise.race([
      once(child, 'close').then(([code]) => ({ code })),
      new Promise((resolve) => { timer = setTimeout(() => resolve(null), timeoutMs); }),
    ]);
  } finally { clearTimeout(timer); }
}

async function waitExit(child, timeoutMs) {
  if (!child || child.exitCode !== null) return true;
  return (await exitWithin(child, timeoutMs)) !== null;
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

async function launchApp(candidate, webviewData, instanceId, extra = {}) {
  const port = await freeLocalPort();
  const executable = path.join(candidate, 'app', 'VibeOCR.WinUI.exe');
  const args = ['--profile', 'production', '--install-root', candidate];
  if (extra.shellOnly !== false) args.unshift('--shell-only');
  const child = spawn(executable, args, {
    cwd: path.dirname(executable), stdio: 'ignore',
    env: {
      ...process.env,
      VIBEOCR_SELF_TEST_SMOKE: 'native-actions-e2e',
      VIBEOCR_SELF_TEST_INSTANCE: instanceId,
      WEBVIEW2_USER_DATA_FOLDER: webviewData,
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:
        `--remote-debugging-port=${port} --remote-debugging-address=127.0.0.1`,
      ...(extra.env || {}),
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

// Exact product layout paths come from the candidate's own product-layout
// descriptor (scripts/product_layout.py schema), never from assumptions.
function productLayoutPaths(candidate) {
  const descriptor = readJson(path.join(candidate, 'app', 'metadata', 'product-layout.json'));
  const app = descriptor.app ?? {};
  const runtime = descriptor.runtime ?? {};
  const metadata = descriptor.metadata ?? {};
  const resolveUnder = (value, field) => {
    assert(typeof value === 'string' && value.length > 0 && !path.isAbsolute(value),
      `product-layout.json field ${field} must be a relative path.`);
    const full = path.resolve(candidate, value);
    assert(contained(candidate, full), `product-layout.json field ${field} escapes the candidate.`);
    return full;
  };
  return {
    appEntry: resolveUnder(app.entry, 'app.entry'),
    webAssetsRoot: resolveUnder(app.web_assets, 'app.web_assets'),
    runtimeManifest: resolveUnder(runtime.manifest, 'runtime.manifest'),
    runtimeInstaller: resolveUnder(runtime.installer, 'runtime.installer'),
    componentLock: resolveUnder(metadata.component_lock, 'metadata.component_lock'),
  };
}

async function stage1(source, smokeRoot, evidence) {
  const candidate = path.join(smokeRoot, 'candidate');
  fs.cpSync(source, candidate, { recursive: true, errorOnExist: true, force: false });
  let app;
  try {
    app = await launchApp(candidate, path.join(smokeRoot, 'webview2'), crypto.randomUUID().replaceAll('-', ''));
    evidence.appPid = app.child.pid;
    await foreground(app);
    const page = app.page;
    await page.getByRole('link', { name: '二维码与条码', exact: true }).click();
    await page.getByRole('heading', { name: '二维码与条码', exact: true }).waitFor();
    for (const test of qrTests) {
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
      await page.getByText(statusCopied, { exact: true }).waitFor();
      // Never inspect an existing user clipboard: this paste occurs only after our own successful copy.
      await page.getByRole('tab', { name: '识别', exact: true }).click();
      // #213: decode is local, so the generated preview decodes even without
      // a Supervisor; the pasted image then replaces it and decodes again.
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
    console.log(`Codes workbench stage 1 (shell-only) passed: ${smokeRoot}`);
  } finally {
    if (app) {
      let cleanupError = null;
      try {
        await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: app.main.Handle });
      } catch (error) { cleanupError = error; }
      await app.browser.close().catch(() => {});
      if (cleanupError) {
        evidence.cleanupError = cleanupError.message;
        throw cleanupError;
      } else {
        assert(app.child.exitCode !== null, 'Stage-1 owned app PID did not exit.');
      }
    }
  }
}

// The fresh candidate's portable-layout manifest and state directories are
// materialized by the real product itself: a shell-only boot never runs the
// first manager list, so legacy discovery stays pending for the Ensure below.
async function materializePortableLayout(candidate, webviewData, smokeRoot) {
  let boot;
  try {
    boot = await launchApp(candidate, webviewData, crypto.randomUUID().replaceAll('-', ''));
    const layoutManifest = path.join(candidate, 'portable-layout.json');
    const until = Date.now() + 20000;
    while (!fs.existsSync(layoutManifest) && Date.now() < until) await delay(200);
    assert(fs.existsSync(layoutManifest), 'Real product did not materialize portable-layout.json.');
    assert(fs.existsSync(path.join(candidate, 'state')), 'Real product did not materialize its state root.');
    return layoutManifest;
  } finally {
    if (boot) {
      await stopOwned(boot.child, 'close', { AppPid: boot.child.pid, Handle: boot.main.Handle });
      await boot.browser.close().catch(() => {});
      assert(boot.child.exitCode !== null, 'Layout-materialization owned app PID did not exit.');
    }
  }
}

// Offline base-only Ensure through the real frozen installer. The binding
// request mirrors RuntimeInstallerClient (protocol 2, cpu accelerator, empty
// install list = base-only scope); paths and product id come from the
// candidate's own layout config. Nothing here writes registries or state
// directly - only the real installer process does.
async function ensureBaseOffline(candidate, layout, smokeRoot) {
  const layoutManifest = path.join(candidate, 'portable-layout.json');
  const portable = readJson(layoutManifest);
  assert.equal(portable.schema_version, 1, 'Unexpected portable-layout schema.');
  const products = Object.keys(portable.products ?? {});
  assert(products.length === 1, `Expected one registered product, got: ${products.join(',')}`);
  const productId = products[0];
  const manifest = readJson(layout.runtimeManifest);
  const capabilities = manifest.capabilities ?? [];
  for (const capability of ['runtime.maintenance.v2', 'runtime.component-selection.v1'])
    assert(capabilities.includes(capability), `Frozen runtime manifest lacks ${capability}.`);
  const request = {
    protocol_version: 2,
    product_root: candidate,
    component_lock: layout.componentLock,
    runtime_manifest: layout.runtimeManifest,
    accelerator: 'cpu',
    layout_manifest: layoutManifest,
    product_id: productId,
    operation: 'ensure',
    accepted_event_streams: ['ndjson.v2'],
    operation_id: `codes-smoke-${crypto.randomUUID()}`,
    install_component_ids: [],
  };
  fs.writeFileSync(path.join(smokeRoot, 'installer-ensure-request.json'), JSON.stringify(request, null, 2));
  const installerTemp = path.join(smokeRoot, 'installer-temp');
  fs.mkdirSync(installerTemp, { recursive: true });
  const child = spawn(layout.runtimeInstaller, ['--request-json', JSON.stringify(request)], {
    cwd: candidate, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
    env: { ...process.env, TEMP: installerTemp, TMP: installerTemp },
  });
  let stdout = '';
  let stderr = '';
  child.stdout.setEncoding('utf8').on('data', (chunk) => { stdout += chunk; });
  child.stderr.setEncoding('utf8').on('data', (chunk) => { stderr += chunk; });
  const completed = await exitWithin(child, ensureTimeoutMs);
  const exit = completed?.code ?? null;
  if (exit === null) {
    await forceStop(child);
    throw new Error(`Offline base Ensure timed out after ${ensureTimeoutMs} ms.`);
  }
  fs.writeFileSync(path.join(smokeRoot, 'installer-ensure-output.jsonl'), stdout);
  const lines = stdout.trim().split(/\r?\n/).filter(Boolean);
  let envelope = null;
  if (lines.length) {
    try { envelope = JSON.parse(lines[lines.length - 1]); } catch { envelope = null; }
  }
  if (exit !== 0 || !envelope?.ok || envelope.operation !== 'ensure' || !envelope.launch) {
    throw new Error(`Frozen installer ensure failed (${exit}): ${envelope?.error?.message ?? stderr.trim()}`);
  }
  const stateRoot = path.join(candidate, 'state');
  const launch = envelope.launch;
  // Python and model storage must be anchored inside the fresh candidate's
  // portable state; working_directory is the product root itself.
  for (const [field, value] of Object.entries({
    python_executable: launch.python_executable,
    model_root: launch.model_root,
  })) {
    assert(typeof value === 'string' && contained(stateRoot, value),
      `Ensure launch ${field} is not anchored in the fresh candidate state: ${value}`);
  }
  assert(fs.statSync(launch.python_executable).isFile(), 'Ensure launch python executable is missing.');
  assert.equal(path.normalize(launch.working_directory), path.normalize(candidate),
    'Ensure launch working_directory must be the product root itself.');
  // Real legacy discovery inputs produced by the installer, before any manager
  // list: the .installed.json marker plus python must exist and no managed
  // registry may exist yet.
  assert(fs.existsSync(path.join(stateRoot, 'runtime', '.installed.json')),
    'Ensure did not produce the legacy .installed.json marker.');
  assert(!fs.existsSync(path.join(stateRoot, 'environments.json')),
    'Managed registry appeared before the first manager list; legacy discovery would be skipped.');
  return { productId, request: { ...request }, launch, state: envelope.state };
}

// Synthetic composites use the repository's locked Pillow; output stays in
// the smoke root.
async function composePillow(out, sources) {
  const code = `import sys
from pathlib import Path

from PIL import Image

out = Path(sys.argv[1])
canvas = Image.new("RGB", (1280, 520), "white")
x = 60
for raw in sys.argv[2:]:
    part = Image.open(raw).convert("RGB")
    part.thumbnail((560, 460))
    canvas.paste(part, (x, (520 - part.height) // 2))
    x += part.width + 80
out.parent.mkdir(parents=True, exist_ok=True)
canvas.save(out, format="PNG")
`;
  await execFileAsync('uv', ['run', '--frozen', '--no-sync', 'python', '-c', code, out, ...sources],
    { cwd: repoRoot, windowsHide: true, timeout: 120000, encoding: 'utf8' });
  assert(fs.statSync(out).isFile(), `Pillow composite was not produced: ${out}`);
}

async function setClipboardImage(smokeRoot, image) {
  const { stdout } = await execFileAsync('pwsh', ['-NoProfile', '-NonInteractive', '-STA',
    '-File', clipboardScript, '-SmokeRoot', smokeRoot, '-ImagePath', image],
  { timeout: 20000, windowsHide: true, maxBuffer: 1024 * 1024 });
  assert.equal(stdout.trim(), 'clipboard-image-set', 'Clipboard fixture did not confirm its synthetic image.');
}

function readTrace(file) {
  if (!fs.existsSync(file)) return [];
  return fs.readFileSync(file, 'utf8').split(/\r?\n/).filter(Boolean)
    .map((line) => {
      try { return JSON.parse(line); } catch { return null; }
    })
    .filter(Boolean);
}

async function waitForTrace(file, predicate, timeoutMs, label) {
  const until = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < until) {
    const records = readTrace(file);
    const matches = records.filter(predicate);
    if (matches.length) return matches;
    last = records[records.length - 1];
    await delay(250);
  }
  throw new Error(`Timed out waiting for supervisor health ${label}; last record: ${JSON.stringify(last)}`);
}

async function assertDescendant(pid, rootPid) {
  assert(Number.isInteger(pid) && pid > 0 && Number.isInteger(rootPid) && rootPid > 0,
    'Supervisor descendant check requires positive integer PIDs.');
  const script = `$ErrorActionPreference = 'Stop'
$current = ${pid}
$root = ${rootPid}
$seen = @{}
while ($current -gt 0 -and -not $seen.ContainsKey($current)) {
    if ($current -eq $root) { Write-Output 'descendant'; exit 0 }
    $seen[$current] = $true
    $process = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $current) -ErrorAction SilentlyContinue
    if ($null -eq $process) { break }
    $current = [int]$process.ParentProcessId
}
Write-Output 'not-descendant'
exit 1`;
  const { stdout } = await execFileAsync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', script],
    { timeout: 15000, windowsHide: true });
  assert.equal(stdout.trim(), 'descendant', `Supervisor PID ${pid} is not a descendant of owned app PID ${rootPid}.`);
}

async function waitForPidExit(pid, timeoutMs) {
  assert(Number.isInteger(pid) && pid > 0, 'waitForPidExit requires a positive integer PID.');
  const script = `if (Get-Process -Id ${pid} -ErrorAction SilentlyContinue) { 'alive' } else { 'gone' }`;
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    const { stdout } = await execFileAsync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', script],
      { timeout: 15000, windowsHide: true });
    if (stdout.trim() === 'gone') return true;
    await delay(300);
  }
  return false;
}

async function qrStatus(page) {
  return (await page.locator('output.status-line').innerText({ timeout: 10000 })).trim();
}

// Observe real bridge traffic: React may coalesce the brief running UI state.
// Requests still flow unchanged through the production postMessage method.
async function watchStatus(page) {
  await page.evaluate(() => {
    window.__codesSmokeStatusLog = [];
    window.__codesSmokeRequest = null;
    if (window.__codesSmokeBridgeObserver) return;
    window.__codesSmokeBridgeObserver = true;
    const channel = window.chrome.webview;
    const post = channel.postMessage.bind(channel);
    channel.postMessage = (data) => {
      if (data?.payload?.command?.scope === 'qrcode' &&
          data.payload.command.action === 'decodeCurrent')
        window.__codesSmokeRequest = { id: data.id, action: data.payload.command.action };
      return post(data);
    };
    channel.addEventListener('message', ({ data }) => {
      if (data?.type !== 'app.state' && data?.type !== 'app.command') return;
      window.__codesSmokeStatusLog.push({ kind: data.kind, type: data.type, id: data.id,
        scope: data.payload?.scope, ok: data.payload?.ok,
        problem: data.payload?.problem ?? null,
        statusCode: data.payload?.state?.statusCode,
        previewRevision: data.payload?.state?.previewRevision });
      window.__codesSmokeStatusLog = window.__codesSmokeStatusLog.slice(-64);
    });
  });
}

async function readStatusLog(page) {
  return page.evaluate(() => ({ request: window.__codesSmokeRequest,
    messages: window.__codesSmokeStatusLog ?? [] }));
}

async function waitManualDecode(page) {
  await page.waitForFunction(() => {
    const request = window.__codesSmokeRequest;
    const messages = window.__codesSmokeStatusLog ?? [];
    const receipt = request && messages.find((m) =>
      m.kind === 'response' && m.type === 'app.command' && m.id === request.id);
    return receipt && (receipt.ok === false || messages.some((m) => m.scope === 'qrcode' && m.statusCode === 'qrcode.decoded'));
  }, null, { timeout: decodeTimeoutMs });
  const log = await readStatusLog(page);
  const receipt = log.messages.find((m) => m.kind === 'response' && m.id === log.request?.id);
  assert.equal(receipt?.ok, true, `Manual decode receipt failed: ${JSON.stringify(log)}`);
  assert(log.messages.some((m) => m.scope === 'qrcode' && m.statusCode === 'qrcode.running'),
    `No native running state for manual decode: ${JSON.stringify(log)}`);
  await page.getByText(statusDecoded, { exact: true }).waitFor({ timeout: decodeTimeoutMs });
  return log;
}
async function qrResults(page) {
  return page.locator('ul.decoded-results > li').evaluateAll((items) => items.map((item) => {
    const spans = item.querySelectorAll('span');
    return {
      format: spans[0]?.textContent?.trim() ?? '',
      data: spans[1]?.textContent ?? '',
    };
  }));
}

function resultTexts(results) {
  return results.map((item) => `${item.format}:${item.data}`).sort();
}

async function waitDecodedItem(page, expected, timeoutMs) {
  await expect(page.getByRole('listitem').filter({ hasText: expected }))
    .toBeVisible({ timeout: timeoutMs });
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
  const stage2Root = path.join(smokeRoot, 'stage2');
  const evidence = { schema_version: 2, state: 'failed', smokeRoot, cases: [],
    stage1: { state: 'failed' }, stage2: { state: 'not-run' },
    desktopRuntime: 'stage1 shell-only local decode; stage2 real Supervisor from offline base Ensure via frozen installer (decode stays local)',
    decoder: 'stage1+stage2 local C# decoder (BitmapDecoder+ZXing.Net); source Runtime pyzbar used as independent cross-check',
    commonWindowsAppPaste: 'not covered by this script' };
  try {
    await stage1(source, smokeRoot, evidence);
    evidence.stage1.state = 'passed';

    // Stage 2: second fresh synthetic candidate without state from the source.
    assert(!fs.existsSync(path.join(source, 'state')),
      'Source candidate gained state during stage 1; refusing a dirty copy.');
    fs.mkdirSync(stage2Root);
    const candidate2 = path.join(stage2Root, 'candidate');
    fs.cpSync(source, candidate2, { recursive: true, errorOnExist: true, force: false });
    const layout = productLayoutPaths(candidate2);
    for (const [field, value] of Object.entries(layout)) {
      if (field === 'webAssetsRoot')
        assert(fs.statSync(value).isDirectory(), `Candidate layout directory missing for ${field}: ${value}`);
      else assert(fs.statSync(value).isFile(), `Candidate layout file missing for ${field}: ${value}`);
    }
    evidence.stage2 = { state: 'failed', cases: [] };

    // 1) Real product materializes portable layout state (no manager list).
    const webview2 = path.join(stage2Root, 'webview2');
    await materializePortableLayout(candidate2, webview2, stage2Root);
    // 2) Offline base-only Ensure through the real frozen installer, before
    //    the first manager list, so real legacy discovery finds it active.
    const ensured = await ensureBaseOffline(candidate2, layout, stage2Root);
    evidence.stage2.ensure = {
      productId: ensured.productId,
      operationId: ensured.request.operation_id,
      accelerator: ensured.request.accelerator,
      installComponentIds: ensured.request.install_component_ids,
      launch: {
        python_executable: ensured.launch.python_executable,
        working_directory: ensured.launch.working_directory,
        model_root: ensured.launch.model_root,
      },
      state: ensured.state,
    };

    // 3) Full App: real Supervisor via legacy discovery + attach.
    const trace = path.join(stage2Root, 'supervisor-health.jsonl');
    let app;
    try {
      app = await launchApp(candidate2, webview2, crypto.randomUUID().replaceAll('-', ''), {
        shellOnly: false,
        env: { VIBEOCR_SUPERVISOR_HEALTH_TRACE: trace },
      });
      evidence.stage2.appPid = app.child.pid;
      const ready = await waitForTrace(trace,
        (record) => record.state === 'Ready' && Number(record.process_id) > 0 && !!record.instance_id,
        supervisorReadyTimeoutMs, 'initial Ready');
      const initialReady = ready[ready.length - 1];
      evidence.stage2.supervisor = { readyInstanceId: initialReady.instance_id,
        readyProcessId: Number(initialReady.process_id) };
      await foreground(app);
      const page = app.page;
      await page.getByRole('link', { name: '二维码与条码', exact: true }).click();
      await page.getByRole('heading', { name: '二维码与条码', exact: true }).waitFor();

      for (const test of qrTests) {
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
        fs.writeFileSync(path.join(stage2Root, `${test.format}-generated.png`), Buffer.from(generated.bytes));
        // Switching to the decode tab auto-decodes once through the local decoder.
        await page.getByRole('tab', { name: '识别', exact: true }).click();
        await waitDecodedItem(page, test.expected, decodeTimeoutMs);
        const autoResults = await qrResults(page);
        assert(autoResults.some((item) => item.data === test.expected),
          `Auto decode missed ${test.format} payload: ${JSON.stringify(autoResults)}`);
        assert(test.format !== 'qrcode' || autoResults.some((item) => item.format.toUpperCase().includes('QR')),
          `QR decode format unexpected: ${JSON.stringify(autoResults)}`);
        // Copy our own image, paste it back: pixels and decoded payload must match.
        await native('foreground', { AppPid: app.child.pid });
        await page.getByRole('button', { name: '复制图片', exact: true }).click();
        await page.getByText(statusCopied, { exact: true }).waitFor();
        await native('foreground', { AppPid: app.child.pid });
        await page.getByRole('button', { name: '粘贴图片', exact: true }).click();
        await page.waitForFunction((old) => {
          const img = document.querySelector('img[alt="当前二维码与条码预览"]');
          return img && img.src !== old && img.complete && img.naturalWidth > 0;
        }, generated.url, { timeout: 20000 });
        await waitDecodedItem(page, test.expected, decodeTimeoutMs);
        const pasted = await preview(page);
        assert(Buffer.from(pasted.pixels).equals(Buffer.from(generated.pixels)),
          'Supervisor-backed paste changed final pixels.');
        const pastedResults = await qrResults(page);
        assert(pastedResults.some((item) => item.data === test.expected),
          `Paste decode missed ${test.format} payload: ${JSON.stringify(pastedResults)}`);
        // Manual re-recognition visibly re-runs the local decode on the same
        // preview revision.
        await native('foreground', { AppPid: app.child.pid });
        await watchStatus(page);
        await page.getByRole('button', { name: '识别当前预览 / 重新识别', exact: true }).click();
        const manualLog = await waitManualDecode(page);
        const manualResults = await qrResults(page);
        assert(manualResults.some((item) => item.data === test.expected),
          `Manual re-recognition missed ${test.format} payload: ${JSON.stringify(manualResults)}`);
        assert.equal(await page.getByAltText('当前二维码与条码预览').getAttribute('src'), pasted.url,
          'Manual re-recognition changed the preview revision.');
        // Tab switches must not repeat the auto decode at this revision.
        await watchStatus(page);
        await page.getByRole('tab', { name: '生成', exact: true }).click();
        await page.getByRole('tab', { name: '识别', exact: true }).click();
        await delay(900);
        const switchLog = await readStatusLog(page);
        assert(!switchLog.request && !switchLog.messages.some((m) => m.scope === 'qrcode' && m.statusCode === 'qrcode.running'),
          `Tab switching re-triggered an automatic decode: ${JSON.stringify(switchLog)}`);
        assert.equal(await qrStatus(page), statusDecoded,
          'Tab switching re-triggered or disturbed the decode status.');
        assert.equal(await page.getByAltText('当前二维码与条码预览').getAttribute('src'), pasted.url,
          'Tab switching changed the preview revision.');
        assert.deepEqual(resultTexts(await qrResults(page)), resultTexts(manualResults),
          'Tab switching changed the decoded results.');
        await page.screenshot({ path: path.join(stage2Root, `${test.format}-decoded.png`) });
        evidence.stage2.cases.push({ ...test, autoResults, pastedResults, manualResults,
          manualBridge: manualLog, tabSwitchBridge: switchLog,
          nativeClipboardPixelsEqual: true });
      }

      // Blank synthetic image through the write-only clipboard fixture.
      const blank = path.join(stage2Root, 'blank-composite.png');
      await composePillow(blank, []);
      await setClipboardImage(stage2Root, blank);
      await native('foreground', { AppPid: app.child.pid });
      const beforeBlank = await page.getByAltText('当前二维码与条码预览').getAttribute('src');
      await page.getByRole('button', { name: '粘贴图片', exact: true }).click();
      await page.waitForFunction((old) => {
        const img = document.querySelector('img[alt="当前二维码与条码预览"]');
        return img && img.src !== old && img.complete && img.naturalWidth > 0;
      }, beforeBlank, { timeout: 20000 });
      await page.getByText(statusNoCodes, { exact: true }).waitFor({ timeout: decodeTimeoutMs });
      assert.deepEqual(await qrResults(page), [], 'Blank image must decode to zero results.');
      evidence.stage2.blankNoCodes = true;

      // Multi-code composite reuses the actually generated QR + Code128 PNGs.
      const multi = path.join(stage2Root, 'multi-composite.png');
      await composePillow(multi, [path.join(stage2Root, 'qrcode-generated.png'),
        path.join(stage2Root, 'code128-generated.png')]);
      await setClipboardImage(stage2Root, multi);
      await native('foreground', { AppPid: app.child.pid });
      const beforeMulti = await page.getByAltText('当前二维码与条码预览').getAttribute('src');
      await page.getByRole('button', { name: '粘贴图片', exact: true }).click();
      await page.waitForFunction((old) => {
        const img = document.querySelector('img[alt="当前二维码与条码预览"]');
        return img && img.src !== old && img.complete && img.naturalWidth > 0;
      }, beforeMulti, { timeout: 20000 });
      await waitDecodedItem(page, '中文🙂 hello QR', decodeTimeoutMs);
      await waitDecodedItem(page, 'VIBE-128', decodeTimeoutMs);
      const multiResults = await qrResults(page);
      assert(multiResults.some((item) => item.data === '中文🙂 hello QR') &&
        multiResults.some((item) => item.data === 'VIBE-128'),
      `Multi-code decode missed payloads: ${JSON.stringify(multiResults)}`);
      const multiUrl = await page.getByAltText('当前二维码与条码预览').getAttribute('src');
      await page.screenshot({ path: path.join(stage2Root, 'multi-decoded.png') });
      evidence.stage2.multiCodeResults = multiResults;

      // Recovery: evidence-gated, single attempt, verified descendant only.
      const readyRecords = readTrace(trace).filter((record) =>
        record.state === 'Ready' && Number(record.process_id) > 0 && !!record.instance_id);
      if (!readyRecords.length) {
        evidence.stage2.recovery = { gap: 'No Ready supervisor record in the health trace; recovery not attempted.' };
      } else {
        const owned = readyRecords[readyRecords.length - 1];
        const supervisorPid = Number(owned.process_id);
        await assertDescendant(supervisorPid, app.child.pid);
        await execFileAsync('taskkill', ['/PID', String(supervisorPid), '/F'],
          { timeout: 10000, windowsHide: true });
        await waitForTrace(trace, (record) => record.state === 'Faulted', 30000, 'Faulted after owned supervisor exit');
        const recovered = await waitForTrace(trace, (record) =>
          record.state === 'Ready' && Number(record.process_id) > 0 &&
          !!record.instance_id && record.instance_id !== owned.instance_id,
        supervisorReadyTimeoutMs, 'recovered Ready');
        const recoveredReady = recovered[recovered.length - 1];
        evidence.stage2.recovery = { killedSupervisorPid: supervisorPid,
          beforeInstanceId: owned.instance_id, afterInstanceId: recoveredReady.instance_id,
          afterProcessId: Number(recoveredReady.process_id) };
        // The same current preview survives; decode is local and unaffected by
        // the Supervisor kill, while manual re-recognition still re-runs and
        // the recovered Supervisor evidence stays valid for the lifecycle.
        await native('foreground', { AppPid: app.child.pid });
        await watchStatus(page);
        await page.getByRole('button', { name: '识别当前预览 / 重新识别', exact: true }).click();
        const recoveryLog = await waitManualDecode(page);
        assert.equal(await page.getByAltText('当前二维码与条码预览').getAttribute('src'), multiUrl,
          'Recovery changed the current preview revision.');
        const recoveredResults = await qrResults(page);
        assert(recoveredResults.some((item) => item.data === '中文🙂 hello QR') &&
          recoveredResults.some((item) => item.data === 'VIBE-128'),
        `Post-recovery decode missed payloads: ${JSON.stringify(recoveredResults)}`);
        await page.screenshot({ path: path.join(stage2Root, 'recovery-decoded.png') });
        evidence.stage2.recovery.bridge = recoveryLog;
        evidence.stage2.recovery.resultsAfterRecovery = recoveredResults;
      }
      evidence.stage2.state = 'passed';
    } catch (error) {
      if (app) {
        evidence.stage2.bridgeAtFailure = await readStatusLog(app.page).catch(() => null);
        evidence.stage2.statusAtFailure = await qrStatus(app.page).catch(() => null);
        await app.page.screenshot({ path: path.join(stage2Root, 'failure.png') }).catch(() => {});
      }
      throw error;
    } finally {
      if (app) {
        let cleanupError = null;
        try {
          await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: app.main.Handle });
        } catch (error) { cleanupError = error; }
        await app.browser.close().catch(() => {});
        if (cleanupError) {
          evidence.stage2.cleanupError = cleanupError.message;
          throw cleanupError;
        } else {
          assert(app.child.exitCode !== null, 'Stage-2 owned app PID did not exit.');
        }
        const lastReady = readTrace(trace).filter((record) =>
          record.state === 'Ready' && Number(record.process_id) > 0).pop();
        if (lastReady) {
          const supervisorPid = Number(lastReady.process_id);
          if (!await waitForPidExit(supervisorPid, 15000)) {
            evidence.stage2.supervisorStillAlivePid = supervisorPid;
            throw new Error(`Owned Supervisor ${supervisorPid} survived App teardown.`);
          }
        }
      }
    }
    evidence.state = 'passed';
    console.log(`Codes workbench product HTTP smoke passed: ${smokeRoot}`);
  } catch (error) {
    evidence.error = `${error.name}: ${error.message}`;
    throw error;
  } finally {
    fs.writeFileSync(path.join(smokeRoot, 'codes-health.json'), JSON.stringify(evidence, null, 2));
  }
}
main().catch((error) => { console.error(`Codes workbench smoke failed: ${error.message}`); process.exitCode = 1; });
