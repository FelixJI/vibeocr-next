// Task #214 AC1 bounded smoke: normal Portable boot, pure tools only, zero
// Installer/Supervisor expectation. Public-UI steps: local QR generate/decode
// + copy/paste roundtrip, generic settings (hotkey rows read-only + floating
// toolbar show/hide toggle), image edit (clipboard-fixture paste -> rectangle
// annotation -> copy -> repaste verify) and a pure screenshot capture through
// the public 截图 button into the scene editor (manual drag over the synthetic
// fixture, session BMP verification, annotate, pin + on-site measurement
// before the scene-terminal copy, explicit end on an independent session,
// then the #318 real Windows Clipboard readback of that scene copy through
// the still-alive workbench's public paste input).
// Combined zero evidence: owned CIM lineage (runtime_demand_process_probe.ps1)
// records no installer/supervisor/python descendants + candidate AppLog has no
// "Runtime demand: connect begin" / "Runtime demand: initialize_default begin"
// + fresh candidate state keeps no environment registry/default/plans/legacy
// marker (semantic presence only, no hashes) and the AppLog both stays
// non-empty and contains no "Runtime Installer command starting action=" line
// (any action, including transient list / switch reservation) and no
// "Runtime demand: connect begin" / "Runtime demand: initialize_default begin".
// soak env (inherited values are stripped), no private bridge commands, no
// direct Runtime HTTP, no user images: the only clipboard images are the ones
// direct Runtime HTTP, no user images: the only clipboard images are the ones
// this smoke copies or sets from its own synthetic QR PNG via
// native_codes_clipboard_fixture.ps1 (write-only, <=8 MiB, inside the root).
// Save/pin coverage: the real FileSavePicker behind the public 保存标注图 is
// driven through the owned save-file seam (scroll_capture_fixture.ps1: the
// #32770 dialog must be owned by our main window; filename set, read back and
// confirmed through verified controls), and the scene 贴图 pin is proven by a
// passive owned-window diff (stable new visible top-level window, transient
// popups/tooltips excluded) and removed again via owned close.
// a0628: imageEdit copy/save upload evidence comes from the shared pre-click
// observer (CDP network events proved lossy on this page); the save picker
// is only driven after the accepted 201 + public command, fail closed.
// Not covered (left for the main agent): settings hotkey re-recording. The
// evidence root is never deleted; owned cleanup
// follows recorded PID + creation_filetime lineage only.
// Task #215 (additive, listener-only, same Issue214 baseline above): passive
// per-scope/scene WebView message counting with UTF-8 serialized-payload
// bytes plus the actual JS resource request graph (buffered in-page resource
// timing + passive CDP request events) via scripts/workbench_delivery_probe.mjs
// into record.measurement_215 / root file workbench-delivery-215.json. Contexts
// existing at attach are instrumented; when the script holds the scene page its
// context is confirmed explicitly (scene.context()) and the scene is harvested
// before its window closes. The late-connected main page and any scene whose
// document start cannot be confirmed are marked partial (first_screen_missed);
// first-screen resources are corroborated by buffered resource timing only,
// with explicit gaps - never fabricated into a pass.
// No private bridge commands, no public input step changes, no new validity
// rule: existing failures are never swallowed by missing new measurement.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { promisify } from 'node:util';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { native, windows, waitForWindows, area, inside, delay,
  startFixture, openSettings, setCheckbox, toolbarStatus, stopOwned,
  settleSceneCopyTimeline, observeSceneCopyTerminal, assertClipboardReadback,
  pasteImageEditClipboard, installImageEditClipboardWatch,
  collectSceneCopyUploadBlob, decodeImageRgba, raceDeadline } from './smoke_native_actions.mjs';
import { createWorkbenchCollector, runMeasurementSelfCheck } from './workbench_delivery_probe.mjs';

const exec = promisify(execFile);
const scripts = path.dirname(fileURLToPath(import.meta.url));
const repo = path.dirname(scripts);
const { chromium, expect } = createRequire(path.join(repo,
  'src/dotnet/VibeOCR.App/WebAssets/package.json'))('@playwright/test');
const clipboardScript = path.join(scripts, 'native_codes_clipboard_fixture.ps1');
const scrollCaptureScript = path.join(scripts, 'scroll_capture_fixture.ps1');
const probeScript = path.join(scripts, 'runtime_demand_process_probe.ps1');
const decodeTimeoutMs = 90 * 1000;
// Forbidden owned-lineage descendant classes; class 'other' (for example the
// app's own WebView2 browser children) stays expected and unconstrained.
const forbiddenClasses = new Set(['installer', 'installer-child', 'supervisor', 'python-child']);
// Owner-landed AppLog Installer invocation line: the prefix rejects ANY
// action, including read-only transient "list" and "switch reservation".
const appLogForbiddenPrefixes = [
  'Runtime demand: starting product Supervisor',
  'Runtime Installer command starting action=',
  'Runtime demand: connect begin',
  'Runtime demand: initialize_default begin',
];
// Verified registration surfaces, relative to the candidate copy root, from
// the actual App client construction chain: PortableLayout.Resolve(production)
// writes <copy>/portable-layout.json (shared_root="state") and
// RuntimeInstallerConfiguration.ForNext passes ProductRoot=layout.InstallRoot
// (<copy>) plus that manifest, so the installer store anchors on <copy>/state:
// store environments root <copy>/state/environments, store state root
// <copy>/state/state (registry/default-environment.json/environment-plans/
// installer-cache) and legacy runtime marker <copy>/state/runtime/.installed.json
// (real baseline: candidate/state/environments/... and
// candidate/state/state/installer-cache). <copy>/state/locks is layout.LocksRoot
// and exists after every normal boot - never a forbidden surface.
const environmentSurfaces = [
  path.join('state', 'environments'),
  path.join('state', 'state', 'environments.json'),
  path.join('state', 'state', 'default-environment.json'),
  path.join('state', 'state', 'environment-plans'),
  path.join('state', 'state', 'installer-cache'),
  path.join('state', 'runtime', '.installed.json'),
];

function option(name) {
  const index = process.argv.indexOf(name);
  assert(index > 0 && index + 1 < process.argv.length, `Required option ${name} is missing.`);
  return process.argv[index + 1];
}

function contained(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

const jsonLines = (file) => fs.existsSync(file)
  ? fs.readFileSync(file, 'utf8').split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];

// Minimal named exports for scripts/smoke_workbench_delivery.mjs reuse only;
// the CLI behavior, zero-runtime rules and steps of this smoke are unchanged.
export function allowsLocalToolRequest(value) {
  const url = new URL(value);
  return url.protocol === 'data:' || url.origin === 'https://app.vibeocr' ||
    (['http:', 'https:'].includes(url.protocol) && ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname));
}

export async function freePort() {
  const server = net.createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const port = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  return port;
}

async function waitFile(file, child, milliseconds = 20000) {
  const until = Date.now() + milliseconds;
  while (!fs.existsSync(file)) {
    assert(child.exitCode === null && child.signalCode === null,
      `Process exited before ${path.basename(file)}`);
    assert(Date.now() < until, `Timeout waiting for ${path.basename(file)}`);
    await delay(100);
  }
}

export async function connect(child, debugPort) {
  const until = Date.now() + 30000;
  let error;
  while (Date.now() < until) {
    assert(child.exitCode === null, 'App exited before CDP connected');
    let browser;
    try {
      browser = await chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`, { timeout: 1000 });
      const context = browser.contexts()[0];
      assert(context, 'CDP context missing');
      const page = context.pages().find((item) => item.url().startsWith('https://app.vibeocr/index.html'))
        ?? await context.waitForEvent('page', { timeout: 5000 });
      await page.waitForURL(/^https:\/\/app\.vibeocr\/index\.html/, { timeout: 5000 });
      await page.getByRole('link', { name: '单次识别', exact: true }).waitFor();
      return { browser, page };
    } catch (cause) {
      error = cause;
      if (browser) await browser.close();
      await delay(200);
    }
  }
  throw error;
}

// Owner-written read-only monitor contract: wait for monitor-ready, the
// spawned App PID goes to root-pid.txt, stop-monitor ends both discovery and
// independent owned working-set reads. CIM ownership snapshots remain a lower
// bound (complete_start_trace=false), never a complete process-start trace.
export async function monitor(root) {
  const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File', probeScript,
    '-OutputRoot', root], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', (chunk) => { stderr += chunk; });
  const finished = once(child, 'exit').then(([code]) => {
    assert.equal(code, 0, `Process monitor failed: ${stderr}`);
  });
  finished.catch(() => {});
  try { await waitFile(path.join(root, 'monitor-ready'), child); }
  catch (error) { await finished; throw error; }
  return { child, finished };
}

// Only CIM-attested identities are termination candidates. The sampler can
// observe a child before CIM does, so also reject any remaining sampled identity.
export async function cleanupLineage(root) {
  const starts = jsonLines(path.join(root, 'process-starts.jsonl'))
    .filter((row) => row.creation_filetime !== null && row.creation_filetime !== undefined);
  const sampled = jsonLines(path.join(root, 'process-samples.jsonl')).flatMap((sample) => sample.procs);
  const identityEntries = (rows) => {
    const keys = new Set();
    for (const { pid, creation_filetime } of rows) {
      assert(Number.isSafeInteger(pid) && pid > 0 && pid <= 0x7fffffff &&
        typeof creation_filetime === 'string' && /^[1-9]\d{0,18}$/.test(creation_filetime),
      'Owned lineage record lacks PID/creation_filetime ownership.');
      keys.add(`'${pid}|${creation_filetime}'=$true`);
    }
    return [...keys].join(';');
  };
  const entries = identityEntries(starts);
  const sampledEntries = identityEntries(sampled);
  const sampledPids = [...new Set(sampled.map(({ pid }) => pid))].join(',');
  const checkSampled = sampledPids ? `
$sampled = @{${sampledEntries}}
function Get-Creation([int]$processId, [bool]$requireLive) {
  try { $p = [Diagnostics.Process]::GetProcessById($processId) }
  catch [ArgumentException] { return }
  try {
    try { $created = $p.StartTime.ToUniversalTime().ToFileTimeUtc().ToString() }
    catch { if ($p.HasExited) { return }; throw }
    if (-not $requireLive -or -not $p.HasExited) { $created }
  } finally { $p.Dispose() }
}
foreach ($processId in @(${sampledPids})) {
  $created = Get-Creation $processId $true
  $key = [string]$processId + '|' + $created
  # Reread creation from a new Process after the liveness check, so a reused PID
  # cannot make the old process look alive between the two .NET property reads.
  if ($sampled.ContainsKey($key) -and (Get-Creation $processId $false) -ceq $created) {
    throw "Owned process from working-set samples survived cleanup: PID $processId FILETIME $created"
  }
}
` : '';
  const command = `$ErrorActionPreference='Stop';$owned=@{${entries}};` +
    `$live=Get-CimInstance Win32_Process;foreach($p in $live){$key=[string]$p.ProcessId+'|'+[string]$p.CreationDate.ToUniversalTime().ToFileTimeUtc();if($owned.ContainsKey($key)){Stop-Process -Id $p.ProcessId -ErrorAction SilentlyContinue}};` +
    `Start-Sleep -Milliseconds 500;$live=Get-CimInstance Win32_Process;foreach($p in $live){$key=[string]$p.ProcessId+'|'+[string]$p.CreationDate.ToUniversalTime().ToFileTimeUtc();if($owned.ContainsKey($key)){throw 'Owned process survived cleanup'}};` +
    checkSampled + 'exit 0';
  await exec('pwsh', ['-NoProfile', '-NonInteractive', '-Command', command],
    { windowsHide: true, timeout: 20000 });
}

async function setClipboardImage(root, image) {
  const { stdout } = await exec('pwsh', ['-NoProfile', '-NonInteractive', '-STA', '-File',
    clipboardScript, '-SmokeRoot', root, '-ImagePath', image],
  { timeout: 20000, windowsHide: true, maxBuffer: 1024 * 1024 });
  assert.equal(stdout.trim(), 'clipboard-image-set',
    'Clipboard fixture did not confirm its synthetic image.');
}

// Passive resource observation of the product's own preview element.
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

async function qrStatus(page) {
  return (await page.locator('output.status-line').innerText({ timeout: decodeTimeoutMs })).trim();
}

async function qrResults(page) {
  return page.locator('ul.decoded-results > li').evaluateAll((items) => items.map((item) => {
    const spans = item.querySelectorAll('span');
    return { format: spans[0]?.textContent?.trim() ?? '', data: spans[1]?.textContent ?? '' };
  }));
}

async function waitDecoded(page, payload) {
  await expect(page.getByRole('listitem').filter({ hasText: payload }))
    .toBeVisible({ timeout: decodeTimeoutMs });
  assert.equal(await qrStatus(page), '识别完成');
  const results = await qrResults(page);
  assert(results.some((item) => item.data === payload),
    `In-app decode missed payload: ${JSON.stringify(results)}`);
  return results;
}

function canvasOrange(page) {
  return page.getByLabel('图片检查画布').evaluate((canvas) => {
    const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
    let orange = 0, dark = 0;
    for (let i = 0; i < data.length; i += 4) {
      if (data[i] > 210 && data[i + 1] > 90 && data[i + 1] < 190 && data[i + 2] < 110) orange++;
      if (data[i] < 100 && data[i + 1] < 100 && data[i + 2] < 100) dark++;
    }
    return { orange, dark };
  });
}

// Decode a saved PNG inside the product's own WebView2 (Blob/createImageBitmap
// via CDP evaluate; passive resource observation, no Runtime, no new deps).
function decodePng(page, buffer) {
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
      if (data[i] > 210 && data[i + 1] > 90 && data[i + 1] < 190 && data[i + 2] < 110) orange++;
      if (data[i] < 100 && data[i + 1] < 100 && data[i + 2] < 100) dark++;
      if (data[i] > 170 && data[i + 1] > 170 && data[i + 2] > 170) light++;
    }
    return { width: image.width, height: image.height, orange, dark, light };
  }, buffer.toString('base64'));
}

// Passive owned-window diff for the 贴图 pin: a new visible top-level window
// ("VibeOCR 贴图", always-on-top, image-sized) that stays present across spaced
// samples; transient popups/tooltips drop out before the stability window.
async function waitForStableNewWindow(pid, beforeHandles, mainHandle, timeoutMs = 12000) {
  const firstSeen = new Map();
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    const current = new Map((await windows(pid)).filter((item) => item.Visible &&
      !beforeHandles.has(item.Handle) && item.Handle !== mainHandle &&
      area(item) > 2000 && (Number(item.ExtendedStyle) & 0x8) !== 0)
      .map((item) => [item.Handle, item]));
    const now = Date.now();
    for (const [handle] of current)
      if (!firstSeen.has(handle)) firstSeen.set(handle, now);
    for (const [handle, item] of current)
      if (now - firstSeen.get(handle) >= 600) return item;
    await delay(200);
  }
  throw new Error('No new stable visible owned window appeared for the pin.');
}

async function drawRectangle(page) {
  await page.getByRole('button', { name: '矩形', exact: true }).click();
  await page.getByRole('button', { name: '常用颜色 #f38b35', exact: true }).click();
  await page.getByRole('combobox', { name: '线条宽度' }).selectOption('8');
  const editor = page.getByLabel('图片检查画布');
  const box = await editor.boundingBox();
  assert(box && box.width > 100 && box.height > 100, 'Editor canvas has no usable visual bounds.');
  await page.mouse.move(box.x + box.width * 0.35, box.y + box.height * 0.45);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.65, box.y + box.height * 0.55);
  await page.mouse.up();
}

// Non-terminal public copy (a0628): shared-observer timeline + fail-closed
// judge (non-terminal mode), awaited host receipt, product success text;
// no CDP waitForRequest.
async function copyAnnotated(handoff, page, successText) {
  const clickError = await page.getByRole('button', { name: '复制标注图', exact: true })
    .click()
    .then(() => null, (error) => `${error.name}: ${error.message}`);
  if (clickError) handoff.timeline.clickError = clickError;
  const command = await handoff.waitForCommand('copyScreenshotImage');
  assert(command, `No public imageEdit.copyScreenshotImage command within 10 seconds: ${
    JSON.stringify(handoff.timeline)}`);
  settleSceneCopyTimeline(handoff.timeline, 'ImageEdit copy',
    { action: 'copyScreenshotImage', requireScope: 'imageEdit', requireClose: false });
  const upload = handoff.timeline.uploads.find(
    (item) => item.resourceUri === command.resourceUri);
  assert(upload?.blobMeta?.problem == null && Number.isSafeInteger(upload?.blobMeta?.byteLength),
    `ImageEdit copy: unsound upload Blob evidence: ${JSON.stringify(handoff.timeline)}`);
  const receipt = await handoff.waitForReceipt(command.envelopeId);
  assert(receipt && receipt.ok === true,
    `Public copy command receipt missing or failed: ${JSON.stringify(handoff.timeline)}`);
  await page.getByText(successText, { exact: false }).waitFor({ timeout: 15000 });
  return { status: upload.responseStatus, byteLength: upload.blobMeta.byteLength,
    resourceUri: command.resourceUri, command: 'imageEdit.copyScreenshotImage',
    receiptOk: receipt.ok, requestAt: upload.requestAt, responseAt: upload.responseAt };
}

// Scene-terminal copy (goal316/318): the scene closes on copy success, so
// the proof is the Node-held timeline from the pre-click passive
// observeSceneCopyTerminal, settled by the shared fail-closed judge; no
// public copy command, non-201, failure receipt, crash or missing close is
// never a success, and the automatic text-layer upload never counts as the
// copy (association is by the command's resourceUri). The optional holder
// receives the Node-held upload Blob bytes via collectSceneCopyUploadBlob
// (the Blob arrives on an independent CDP path that may land later than the
// 201/command/close, so the bounded Blob wait runs BEFORE any meta check)
// so the #318 clipboard readback can compare against this revision's export;
// the bytes never enter the serialized evidence record.
async function copyAnnotatedTerminal(scene, holder) {
  const terminal = await observeSceneCopyTerminal(scene);
  const clickError = await scene.getByRole('button', { name: '复制标注图', exact: true })
    .click()
    .then(() => null, (error) => `${error.name}: ${error.message}`);
  if (clickError) terminal.timeline.clickError = clickError;
  const deadline = raceDeadline(10000);
  let ended;
  try {
    ended = await Promise.race([terminal.ended.then(() => true), deadline.promise]);
  } finally { deadline.clear(); }
  if (!ended) terminal.timeline.noTerminalEvent = true;
  settleSceneCopyTimeline(terminal.timeline);
  const command = terminal.timeline.copyCommands[0];
  const upload = terminal.timeline.uploads.find(
    (item) => item.resourceUri === command.resourceUri);
  if (holder) holder.uploadBlob = await collectSceneCopyUploadBlob(terminal, upload);
  return { status: upload.responseStatus,
    byteLength: upload.blobMeta?.byteLength ?? null,
    requestAt: upload.requestAt, responseAt: upload.responseAt,
    closedAt: terminal.timeline.closedAt,
    clickError: terminal.timeline.clickError ?? null };
}

// Task #318 clipboard readback: the scene-terminal copy timeline proves the
// public command, its accepted upload and the close, but not that the
// Windows Clipboard actually received this revision's composite image. The
// still-alive workbench reads the real Clipboard back through its public
// 粘贴图片 input (imageEdit.readClipboard -> host Clipboard.GetContent); the
// republished input resource and this revision's export PNG are decoded in
// the SAME WebView and compared byte-for-byte (shared assertClipboardReadback
// against THIS revision's region). The persistent imageEdit-state watch was
// installed before imageEditStep first entered 图片编辑, so the real
// pre-paste session is the baseline and only the paste's terminal non-busy
// record naming a NEW session OR resource is accepted - StartImageEdit's
// busy old-URL republish (the stale QR image this smoke copied earlier) can
// never satisfy
// the wait. The clipboard image is written to disk only AFTER the comparison
// passed. The closed scene is never evaluated again, its suppressed receipt
// stays unrequired, and every wait is bounded with no retry.
async function readbackSceneClipboard(ctx, uploadPng, region) {
  const { page, root } = ctx;
  await focusMain(ctx);
  await page.getByRole('link', { name: '图片编辑', exact: true }).click();
  await page.getByRole('heading', { name: '图片编辑', exact: true }).waitFor();
  const snapshot = await pasteImageEditClipboard(page);
  const resource = new URL(snapshot.url);
  assert(resource.origin === 'https://app.vibeocr' && resource.pathname.startsWith('/__resource/'),
    'Clipboard readback input is not the product resource observed from imageEdit state.');
  const pasted = await page.evaluate(async (url) => {
    const response = await fetch(url);
    if (!response.ok) throw new Error(`Clipboard readback resource returned ${response.status}`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.length > 8 * 1024 * 1024) throw new Error('Clipboard readback image exceeded 8 MiB');
    return { mediaType: response.headers.get('content-type'), bytes: Array.from(bytes) };
  }, snapshot.url);
  // Verify BEFORE persisting: a failed comparison must not write unknown
  // clipboard pixels into the evidence root.
  const expectedRgba = await decodeImageRgba(page, uploadPng, 'image/png');
  const clipboardBytes = Buffer.from(pasted.bytes);
  const rgba = await decodeImageRgba(page, clipboardBytes, pasted.mediaType);
  assertClipboardReadback({ mediaType: pasted.mediaType, rgba }, { width: region.width,
    height: region.height, expectedRgba }, 'Scene clipboard readback');
  fs.writeFileSync(path.join(root, 'capture-clipboard-readback.png'), clipboardBytes);
  return { mediaType: pasted.mediaType, bytes: clipboardBytes.length,
    pixels: { width: rgba.width, height: rgba.height },
    inputUrl: snapshot.url, sessionId: snapshot.sessionId, stateSeq: snapshot.seq,
    matchesRevision: true };
}

// Pure AppLog zero-evidence scanner (self-checkable without any GUI):
// rejects any forbidden-prefix hit and requires at least one non-empty log
// file. A merely existing directory is never sufficient evidence.
function scanAppLog(logsDir) {
  const files = fs.existsSync(logsDir)
    ? fs.readdirSync(logsDir).filter((name) => name.endsWith('.log')).sort() : [];
  const hits = [];
  let nonEmpty = false;
  for (const name of files) {
    const text = fs.readFileSync(path.join(logsDir, name), 'utf8');
    if (text.trim().length > 0) nonEmpty = true;
    for (const needle of appLogForbiddenPrefixes)
      if (text.includes(needle)) hits.push(`${name}: ${needle}`);
  }
  return { files, nonEmpty, hits };
}

// Passive app.state watch for the screenshot session input resource; this
// listener never sends commands or edits app state (same contract as the
// shipped native-actions smoke/probe observation).
async function watchSessionInput(page) {
  await page.evaluate(() => {
    window.__rt214Session = null;
    window.chrome.webview.addEventListener('message', ({ data }) => {
      if (data?.kind !== 'event' || data.type !== 'app.state' ||
          data.payload?.scope !== 'recognition') return;
      const session = data.payload.state?.screenshotSession;
      if (typeof session?.sessionId === 'string' && Number.isSafeInteger(session.revision))
        window.__rt214Session = { sessionId: session.sessionId, revision: session.revision,
          inputUrl: data.payload.state?.input?.url ?? null };
    });
  });
}

// Passive imageEdit-scope input watch: the repaste verification must wait for
// the new input resource to actually arrive before reading canvas pixels, so a
// stale pre-paste canvas can never satisfy the assertion. Listener only.
async function watchImageEditInput(page) {
  await page.evaluate(() => {
    window.__rt214ImageEditInput = null;
    window.chrome.webview.addEventListener('message', ({ data }) => {
      if (data?.kind !== 'event' || data.type !== 'app.state' ||
          data.payload?.scope !== 'imageEdit') return;
      const url = data.payload.state?.input?.url;
      if (typeof url !== 'string') return;
      const session = data.payload.state?.screenshotSession;
      window.__rt214ImageEditInput = { url,
        sessionId: typeof session?.sessionId === 'string' ? session.sessionId : null };
    });
  });
}

export async function focusMain(ctx) {
  await ctx.page.bringToFront();
  const main = (await windows(ctx.child.pid)).find((item) => item.Handle === ctx.main.Handle);
  assert(main?.Visible && !main.Iconic, 'Owned main window is not available for input.');
  const scale = main.Dpi / 96;
  const x = Math.round((main.Bounds.Left + main.Bounds.Right) / 2);
  const y = main.Bounds.Top + Math.round(14 * scale);
  try {
    await exec('pwsh', ['-NoProfile', '-NonInteractive', '-File', scrollCaptureScript,
      '-Action', 'focus', '-Handle', String(main.Handle), '-FixturePid', String(ctx.child.pid),
      '-X', String(x), '-Y', String(y)], { windowsHide: true, timeout: 15000 });
  } catch (error) {
    const observed = await native('probe', { AppPid: ctx.child.pid, X: x, Y: y });
    throw new Error(`Owned focus failed: ${error.stderr || error.message}; geometry=${JSON.stringify(main)}; probe=${observed}`);
  }
  await native('foreground', { AppPid: ctx.child.pid });
}

async function qrToolStep(ctx) {
  const { page, root, child } = ctx;
  const step = { state: 'failed' };
  await page.getByRole('link', { name: '二维码与条码', exact: true }).click();
  await page.getByRole('heading', { name: '二维码与条码', exact: true }).waitFor();
  const payload = `RT214-QR-${randomUUID().slice(0, 8)}`;
  await page.getByRole('tab', { name: '生成', exact: true }).click();
  await page.getByLabel('生成格式').selectOption('qrcode');
  await page.getByLabel('输入内容', { exact: true }).fill(payload);
  await page.locator('summary').filter({ hasText: '底部文字' }).click();
  await page.getByLabel('底部文字', { exact: true }).selectOption('off');
  // The preview element may not exist before the first generation; a null
  // baseline is valid, so never block on a missing attribute.
  const oldImage = page.getByAltText('当前二维码与条码预览');
  const oldUrl = (await oldImage.count()) ? await oldImage.getAttribute('src') : null;
  await page.getByRole('button', { name: '生成图片', exact: true }).click();
  await page.waitForFunction((old) => {
    const img = document.querySelector('img[alt="当前二维码与条码预览"]');
    return img && img.src !== old && img.complete && img.naturalWidth > 0;
  }, oldUrl, { timeout: 15000 });
  const generated = await preview(page);
  const png = path.join(root, 'qr-generated.png');
  fs.writeFileSync(png, Buffer.from(generated.bytes));
  step.generated = { payload, width: generated.width, height: generated.height, file: png };
  ctx.qrDims = { width: generated.width, height: generated.height };
  await focusMain(ctx);
  await page.getByRole('button', { name: '复制图片', exact: true }).click();
  await page.getByText('已复制当前预览图片', { exact: true }).waitFor();
  // Switching to the decode tab auto-decodes the current preview locally.
  await page.getByRole('tab', { name: '识别', exact: true }).click();
  step.generatedResults = await waitDecoded(page, payload);
  // Paste only ever reads back the image this smoke just copied itself.
  await focusMain(ctx);
  await page.getByRole('button', { name: '粘贴图片', exact: true }).click();
  await page.waitForFunction((old) => {
    const img = document.querySelector('img[alt="当前二维码与条码预览"]');
    return img && img.src !== old && img.complete && img.naturalWidth > 0;
  }, generated.url, { timeout: 20000 });
  const pasted = await preview(page);
  assert.equal(pasted.width, generated.width);
  assert.equal(pasted.height, generated.height);
  assert(Buffer.from(pasted.pixels).equals(Buffer.from(generated.pixels)),
    'Native clipboard roundtrip changed final pixels.');
  step.pastedResults = await waitDecoded(page, payload);
  step.clipboardRoundtripPixelsEqual = true;
  await page.screenshot({ path: path.join(root, 'qr-decoded.png') });
  step.state = 'passed';
  return step;
}

async function settingsStep(ctx) {
  const { page, root } = ctx;
  const step = { state: 'failed' };
  await openSettings(page);
  const rows = await page.locator('.hotkey-action-row').count();
  assert(rows > 0, 'Settings page exposes no hotkey action rows.');
  await page.locator('.hotkey-action-row').filter({ hasText: '截图编辑' })
    .getByText('未绑定', { exact: true }).waitFor();
  await page.locator('.hotkey-action-row').filter({ hasText: '快捷截图识别' })
    .getByText('当前生效：Ctrl+Alt+Q', { exact: true }).waitFor();
  step.hotkeyRows = rows;
  // Generic local settings toggle: floating toolbar show/hide with status.
  await setCheckbox(page, '启用悬浮工具栏', true);
  await page.getByRole('button', { name: '显示', exact: true }).click();
  await toolbarStatus(page, '显示中');
  await page.getByRole('button', { name: '隐藏', exact: true }).click();
  await toolbarStatus(page, '已主动隐藏（鼠标路过不恢复）');
  await setCheckbox(page, '启用悬浮工具栏', false);
  await toolbarStatus(page, '已关闭');
  step.floatingToolbarToggle = true;
  await page.screenshot({ path: path.join(root, 'settings.png') });
  step.state = 'passed';
  return step;
}

async function imageEditStep(ctx) {
  const { page, root, child, main } = ctx;
  const step = { state: 'failed' };
  // #318: the persistent imageEdit-state watch must exist BEFORE this step
  // first enters 图片编辑, so the captureStep clipboard readback later sees
  // the real previous session (the busy old-URL republish of the paste can
  // never satisfy its non-busy NEW-session acceptance).
  await installImageEditClipboardWatch(page);
  await page.getByRole('link', { name: '图片编辑', exact: true }).click();
  await page.getByRole('heading', { name: '图片编辑', exact: true }).waitFor();
  await watchImageEditInput(page);
  // Write-only synthetic clipboard: our own generated QR PNG from the smoke root.
  await setClipboardImage(root, path.join(root, 'qr-generated.png'));
  await focusMain(ctx);
  await page.getByRole('button', { name: '粘贴图片', exact: true }).click();
  await page.getByLabel('图片检查画布').waitFor({ timeout: 15000 });
  await page.waitForFunction(() => {
    const element = document.querySelector('canvas[aria-label="图片检查画布"]');
    if (!element) return false;
    const { data } = element.getContext('2d').getImageData(0, 0, element.width, element.height);
    let dark = 0;
    for (let i = 0; i < data.length; i += 4)
      if (data[i] < 100 && data[i + 1] < 100 && data[i + 2] < 100) dark++;
    return dark > 500;
  }, null, { timeout: 15000 });
  // Pin the first paste to its observed public input URL and its session:
  // the session is the precondition for the copy/save session variants
  // (imageEdit.copyScreenshotImage / saveScreenshotImage), so it must exist.
  await page.waitForFunction(() => {
    const record = window.__rt214ImageEditInput;
    return !!record && typeof record.url === 'string' &&
      typeof record.sessionId === 'string' && record.sessionId.length > 0;
  }, null, { timeout: 15000 });
  const firstInput = await page.evaluate(() => window.__rt214ImageEditInput);
  assert(typeof firstInput?.sessionId === 'string' && firstInput.sessionId.length > 0,
    `Pasted imageEdit session missing; copy/save would use the session-less variants: ${
      JSON.stringify(firstInput)}`);
  step.firstInputUrl = firstInput.url;
  step.firstSessionId = firstInput.sessionId;
  const before = await canvasOrange(page);
  assert(before.dark > 500, 'Pasted QR image never decoded in the editor canvas.');
  await drawRectangle(page);
  await page.waitForFunction((baseline) => {
    const element = document.querySelector('canvas[aria-label="图片检查画布"]');
    if (!element) return false;
    const { data } = element.getContext('2d').getImageData(0, 0, element.width, element.height);
    let orange = 0;
    for (let i = 0; i < data.length; i += 4)
      if (data[i] > 210 && data[i + 1] > 90 && data[i + 1] < 190 && data[i + 2] < 110) orange++;
    return orange > baseline + 50;
  }, before.orange, { timeout: 10000 });
  const after = await canvasOrange(page);
  assert(after.orange > before.orange + 50, 'Rectangle edit changed too few canvas pixels.');
  step.annotationPixels = { before: before.orange, after: after.orange };
  // a0628: one pre-click observer for both handoffs; the pasted session
  // makes the public commands the copyScreenshotImage/saveScreenshotImage
  // session variants (never the session-less copyAnnotatedImage).
  const handoff = await observeSceneCopyTerminal(page,
    { actions: ['copyScreenshotImage', 'saveScreenshotImage'] });
  step.annotationUpload = await copyAnnotated(handoff, page, '已复制截图副本');
  // Real FileSavePicker through the owned save-file seam: the export upload
  // is observed passively first (a0628: accepted 201 + the public
  // imageEdit.saveScreenshotImage command BEFORE the picker is driven), then
  // the picker dialog (owned by our main window) is driven through the
  // fixture's verified UIA/Win32 controls. The saved PNG is then decoded in
  // the product's own WebView2 and checked for exact input dimensions, the
  // QR content and the baked annotation pixels.
  const savedFile = path.join(root, 'imageedit-saved.png');
  const saveClickError = await page.getByRole('button', { name: '保存标注图', exact: true })
    .click()
    .then(() => null, (error) => `${error.name}: ${error.message}`);
  if (saveClickError) handoff.timeline.clickError = saveClickError;
  const saveCommand = await handoff.waitForCommand('saveScreenshotImage');
  assert(saveCommand, `No public imageEdit.saveScreenshotImage command within 10 seconds: ${
    JSON.stringify(handoff.timeline)}`);
  settleSceneCopyTimeline(handoff.timeline, 'ImageEdit save',
    { action: 'saveScreenshotImage', requireScope: 'imageEdit', requireClose: false });
  const saveUpload = handoff.timeline.uploads.find(
    (item) => item.resourceUri === saveCommand.resourceUri);
  step.saveUpload = { status: saveUpload.responseStatus,
    byteLength: saveUpload.blobMeta?.byteLength ?? null,
    resourceUri: saveCommand.resourceUri, command: 'imageEdit.saveScreenshotImage' };
  await exec('pwsh', ['-NoProfile', '-NonInteractive', '-File', scrollCaptureScript,
    '-Action', 'save-file', '-Handle', String(main.Handle), '-AppPid', String(child.pid),
    '-OutputPath', savedFile, '-EvidenceRoot', root],
  { timeout: 45000, windowsHide: true });
  const saveReceipt = await handoff.waitForReceipt(saveCommand.envelopeId, 20000);
  assert(saveReceipt && saveReceipt.ok === true,
    `Public save command receipt missing or failed: ${JSON.stringify(handoff.timeline)}`);
  step.saveUpload.receiptOk = saveReceipt.ok;
  await page.getByText('已保存截图副本', { exact: false }).waitFor({ timeout: 15000 });
  const savedStat = fs.statSync(savedFile);
  assert(savedStat.isFile() && savedStat.size > 8, 'Saved annotation PNG is missing or empty.');
  const savedPixels = await decodePng(page, fs.readFileSync(savedFile));
  assert(ctx.qrDims, 'QR dimensions were not recorded for the save verification.');
  assert.equal(savedPixels.width, ctx.qrDims.width, 'Saved PNG width differs from the input image.');
  assert.equal(savedPixels.height, ctx.qrDims.height, 'Saved PNG height differs from the input image.');
  assert(savedPixels.orange > 50, `Saved PNG lost the annotation: ${JSON.stringify(savedPixels)}`);
  assert(savedPixels.dark > 500, `Saved PNG lost the QR content: ${JSON.stringify(savedPixels)}`);
  step.savedFile = { file: savedFile, bytes: savedStat.size, pixels: savedPixels };
  // Repaste proves the copied clipboard image really carries the annotation;
  // wait for the NEW public input resource first so pixel reads can never
  // pass on the stale pre-paste canvas.
  await focusMain(ctx);
  await page.getByRole('button', { name: '粘贴图片', exact: true }).click();
  await page.waitForFunction((previous) => {
    const record = window.__rt214ImageEditInput;
    return !!record && typeof record.url === 'string' && record.url !== previous;
  }, firstInput.url, { timeout: 20000 });
  step.repastedInputUrl = await page.evaluate(() => window.__rt214ImageEditInput.url);
  await page.waitForFunction(() => {
    const element = document.querySelector('canvas[aria-label="图片检查画布"]');
    if (!element) return false;
    const { data } = element.getContext('2d').getImageData(0, 0, element.width, element.height);
    let orange = 0;
    for (let i = 0; i < data.length; i += 4)
      if (data[i] > 210 && data[i + 1] > 90 && data[i + 1] < 190 && data[i + 2] < 110) orange++;
    return orange > 50;
  }, null, { timeout: 20000 });
  await page.screenshot({ path: path.join(root, 'imageedit-annotated.png') });
  step.repastedAnnotatedPixels = true;
  // Shared restore hook: leave the page surfaces as the product had them.
  await page.evaluate(() => window.__nativeActionsRestoreFetch?.())
    .catch(() => { /* page already gone: nothing to restore */ });
  step.state = 'passed';
  return step;
}

// Open one fresh screenshot scene session through the public 截图 button
// (manual drag, label check, Enter, scene appears and paints); reused for the
// annotated session and the independent explicit-end session (goal316).
async function startSceneSession(ctx, fixture) {
  const { child, browser, page } = ctx;
  await focusMain(ctx);
  await page.getByRole('button', { name: '截图', exact: true }).click();
  const button = fixture.buttonRect;
  const x = button.left + Math.floor(button.width / 2);
  const y = button.top + Math.floor(button.height / 2);
  const overlays = await waitForWindows(child.pid, (item) => item.Visible &&
    item.Handle !== ctx.main.Handle &&
    area(item) > fixture.rootRect.width * fixture.rootRect.height * 2 &&
    inside(item, x, y), 12000);
  const overlay = overlays.sort((a, b) => area(b) - area(a))[0];
  const start = { AppPid: child.pid, X: fixture.rootRect.left + 20, Y: fixture.rootRect.top + 40 };
  const end = { AppPid: child.pid, X: fixture.rootRect.right - 20, Y: fixture.rootRect.bottom - 20 };
  await native('mouse-move', start);
  await native('mouse-down', start);
  try { await native('mouse-move', end); }
  finally { await native('mouse-up', end); }
  const region = { left: start.X, top: start.Y,
    width: end.X - start.X, height: end.Y - start.Y };
  const label = await native('selection', { AppPid: child.pid, Handle: overlay.Handle });
  assert(label.includes(`手动 · ${region.width} × ${region.height} px`),
    `Manual selection label mismatch: ${label}`);
  const click = { AppPid: child.pid,
    X: region.left + Math.floor(region.width / 2),
    Y: region.top + Math.floor(region.height / 2) };
  await native('mouse-move', click);
  await native('mouse-down', click);
  try {
    assert((await windows(child.pid)).some((item) => item.Handle === overlay.Handle && item.Visible),
      'Selection click confirmed before release.');
  } finally { await native('mouse-up', click); }
  await native('enter', { AppPid: child.pid });
  const deadline = Date.now() + 12000;
  let scene = null;
  while (Date.now() < deadline && !scene) {
    scene = browser.contexts().flatMap((context) => context.pages())
      .find((item) => item !== page && !item.isClosed() &&
        item.url().includes('#/imageEdit?scene=1'));
    if (!scene) await delay(100);
  }
  assert(scene, 'Screenshot scene WebView did not appear.');
  // Task #215: install the probe on the scene as soon as it is located,
  // BEFORE any public operation, and await it (its context is attached
  // inside; an init-script probe, if any, stays untouched/idempotent).
  if (ctx.collector) await ctx.collector.installOnPage(scene)
    .catch((error) => ctx.collector.noteError(`scene install: ${error.message}`));
  await scene.locator('html[data-capture-scene="true"]')
    .waitFor({ state: 'attached', timeout: 12000 });
  await scene.getByLabel('图片检查画布').waitFor({ timeout: 12000 });
  await scene.waitForFunction(() => {
    const canvas = document.querySelector('canvas[aria-label="图片检查画布"]');
    if (!canvas) return false;
    const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
    let changed = 0;
    for (let i = 0; i < data.length; i += 40)
      if (data[i] !== 22 || data[i + 1] !== 22 || data[i + 2] !== 22) changed++;
    return changed > 100;
  }, null, { timeout: 12000 });
  return { scene, region, overlayHandle: overlay.Handle, selectionLabel: label };
}

// Pure screenshot capture through the public 截图 button: the synthetic
// fixture (topmost full-desktop background) makes the frozen desktop and the
// manual selection deterministic. Enter opens the scene editor; the capture
// is proven by fetching the session input resource (32-bpp BMP of the exact
// selected region) before annotating. Pinning and measurement precede the
// scene-terminal copy; the explicit 结束会话 runs on an independent session.
async function captureStep(ctx) {
  const { child, page, root } = ctx;
  const step = { state: 'failed' };
  const fixture = await startFixture();
  ctx.fixture = fixture;
  step.fixturePid = fixture.pid;
  try {
    await watchSessionInput(page);
    await page.getByRole('link', { name: '单次识别', exact: true }).click();

    await native('focus-fixture', {
      FixturePid: fixture.pid, Handle: fixture.root,
      X: fixture.editRect.left + Math.floor(fixture.editRect.width / 2),
      Y: fixture.editRect.top + Math.floor(fixture.editRect.height / 2),
    });
    await native('frame', { FixturePid: fixture.pid, Handle: fixture.root,
      X: fixture.rootRect.left, Y: fixture.rootRect.top,
      Width: fixture.rootRect.width, Height: fixture.rootRect.height,
      EvidenceRoot: root, OutputPath: path.join(root, 'capture-fixture.png') });
    const first = await startSceneSession(ctx, fixture);
    const scene = first.scene;
    step.overlayHandle = first.overlayHandle;
    step.selectionLabel = first.selectionLabel;
    const region = first.region;
    // The main workbench page carries the passive session state projection.
    await page.waitForFunction(() => {
      const session = window.__rt214Session;
      return !!(session && typeof session.inputUrl === 'string');
    }, null, { timeout: 10000 });
    const session = await page.evaluate(() => window.__rt214Session);
    step.sessionId = session.sessionId;
    const input = await scene.evaluate(async (url) => {
      const response = await fetch(url);
      if (!response.ok) throw new Error(`Screenshot resource returned ${response.status}`);
      const bytes = new Uint8Array(await response.arrayBuffer());
      if (bytes.length > 1024 * 1024) throw new Error('Synthetic screenshot resource exceeded 1 MiB');
      return { mediaType: response.headers.get('content-type'), bytes: Array.from(bytes) };
    }, session.inputUrl);
    assert.equal(input.mediaType, 'image/bmp', 'Native screenshot resource is not BMP.');
    const bmp = Buffer.from(input.bytes);
    const bmpFile = path.join(root, 'capture-original.bmp');
    fs.writeFileSync(bmpFile, bmp);
    assert(bmp.length >= 54 && bmp.toString('ascii', 0, 2) === 'BM' &&
      bmp.readInt16LE(28) === 32 && bmp.readInt32LE(30) === 0,
    'Expected product 32-bit RGB BMP.');
    const width = bmp.readInt32LE(18);
    const height = Math.abs(bmp.readInt32LE(22));
    const offset = bmp.readUInt32LE(10);
    assert.equal(width, region.width, 'Capture width differs from the manual selection.');
    assert.equal(height, region.height, 'Capture height differs from the manual selection.');
    assert.equal(bmp.length - offset, width * height * 4);
    let dark = 0, light = 0;
    for (let i = offset; i < bmp.length; i += 4) {
      if (bmp[i] < 100 && bmp[i + 1] < 100 && bmp[i + 2] < 100) dark++;
      if (bmp[i] > 170 && bmp[i + 1] > 170 && bmp[i + 2] > 170) light++;
    }
    assert(dark > 10 && light > 100, `Captured fixture pixels incomplete: dark=${dark}, light=${light}`);
    step.capture = { bmp: bmpFile, width, height, dark, light };
    // The scene canvas already carries orange crop handles (~192 px baseline),
    // so the stroke wait is baseline-relative with the +50 threshold unchanged.
    const annotationBefore = await canvasOrange(scene);
    await drawRectangle(scene);
    await scene.waitForFunction((baseline) => {
      const canvas = document.querySelector('canvas[aria-label="图片检查画布"]');
      if (!canvas) return false;
      const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
      let orange = 0;
      for (let i = 0; i < data.length; i += 4)
        if (data[i] > 210 && data[i + 1] > 90 && data[i + 1] < 190 && data[i + 2] < 110) orange++;
      return orange > baseline + 50;
    }, annotationBefore.orange, { timeout: 10000 });
    step.annotationPixels = { before: annotationBefore.orange,
      after: (await canvasOrange(scene)).orange };
    // Public 贴图 pin: passive owned-window diff (stable new visible top-level
    // always-on-top window; transient popups/tooltips drop out) followed by
    // an owned close that must actually remove the window again.
    // Runs before the copy: pinning keeps the session alive; the copy is terminal.
    const pinBefore = new Set((await windows(child.pid)).filter((item) => item.Visible)
      .map((item) => item.Handle));
    await scene.getByRole('button', { name: '贴图', exact: true }).click();
    await scene.getByText('已创建桌面贴图。', { exact: false }).waitFor({ timeout: 15000 });
    const pin = await waitForStableNewWindow(child.pid, pinBefore, ctx.main.Handle);
    step.pin = { handle: pin.Handle, className: pin.ClassName, bounds: pin.Bounds,
      extendedStyle: pin.ExtendedStyle };
    await native('close', { AppPid: child.pid, Handle: pin.Handle });
    const pinGone = Date.now() + 10000;
    while ((await windows(child.pid)).some((item) => item.Handle === pin.Handle && item.Visible)) {
      assert(Date.now() < pinGone, 'Owned pin window did not close.');
      await delay(200);
    }
    step.pinClosed = true;
    // Task #215: recover the scene page's bounded measurement while the
    // window is still open (listener-only data; no bridge commands).
    if (ctx.collector) await ctx.collector.harvest('scene-capture', scene);
    await scene.screenshot({ path: path.join(root, 'capture-scene.png') });
    // Scene-terminal copy (goal316): proof is the Node-held request/response/
    // close timeline; nothing is read from the scene afterwards.
    const copyUpload = {};
    step.annotationUpload = await copyAnnotatedTerminal(scene, copyUpload);
    // Task #318: the timeline proves command/upload/close only; read the
    // actual Windows Clipboard back through the still-alive workbench's
    // public paste input and compare with THIS revision's composite image
    // (the QR image this smoke itself copied earlier is the only possible
    // stale content, and its dimensions/pixels cannot pass the judge).
    step.clipboardReadback = await readbackSceneClipboard(ctx, copyUpload.uploadBlob, region);
    // The explicit 结束会话 stays covered on its own fresh, independent
    // session (a different session id), never on the terminal one.
    const second = await startSceneSession(ctx, fixture);
    await page.waitForFunction((previous) => {
      const session = window.__rt214Session;
      return !!(session && session.sessionId !== previous &&
        typeof session.inputUrl === 'string');
    }, session.sessionId, { timeout: 10000 });
    step.explicitEndSession = {
      sessionId: (await page.evaluate(() => window.__rt214Session)).sessionId,
      selectionLabel: second.selectionLabel,
    };
    const endClosed = second.scene.waitForEvent('close', { timeout: 10000 });
    await second.scene.getByRole('button', { name: '结束会话', exact: true }).click();
    await endClosed;
    step.explicitEndSession.closed = true;
    step.state = 'passed';
  } finally {
    const owned = ctx.fixture;
    ctx.fixture = null;
    if (owned) {
      try {
        await stopOwned(owned.child, 'quit', {
          FixturePid: owned.pid, Handle: owned.root, ThreadId: owned.threadId,
        });
      } catch (error) {
        // Recorded, never swallowed: the result keeps state failed via cleanup.
        ctx.fixtureCleanupErrors.push(`fixture ${owned.pid}: ${error.message}`);
      }
    }
  }
  return step;
}

// Post-exit combined zero evidence: owned CIM classes and sampled process names,
// candidate AppLog demand markers and fresh-state environment surfaces.
export function zeroEvidence(copy, root, appPid) {
  const violations = [];
  const starts = jsonLines(path.join(root, 'process-starts.jsonl'));
  const forbidden = starts.filter((entry) => forbiddenClasses.has(entry.class));
  if (forbidden.length)
    violations.push(`owned lineage recorded Installer/Supervisor work: ${JSON.stringify(forbidden)}`);
  const samples = jsonLines(path.join(root, 'process-samples.jsonl'));
  const sampledRuntime = new Set();
  for (const sample of samples)
    for (const { pid, name } of sample.procs)
      if (/runtime-installer|python/i.test(name)) sampledRuntime.add(`${pid}:${name}`);
  if (sampledRuntime.size)
    violations.push(`owned working-set samples recorded Installer/Python processes: ${[...sampledRuntime].join(', ')}`);
  const rootSeen = starts.some((entry) => entry.pid === appPid);
  if (!rootSeen)
    violations.push('monitor never observed the spawned App PID (CIM snapshot lower bound)');
  const logsDir = path.join(copy, 'state', 'logs');
  const scan = scanAppLog(logsDir);
  if (!fs.existsSync(logsDir))
    violations.push(`AppLog directory missing under the current candidate path ${logsDir}`);
  else if (!scan.nonEmpty)
    violations.push(`AppLog exists but holds no non-empty log file: ${logsDir}`);
  if (scan.hits.length)
    violations.push(`AppLog shows Installer/runtime demand activity: ${scan.hits.join('; ')}`);
  const present = environmentSurfaces.filter((relative) => fs.existsSync(path.join(copy, relative)));
  if (present.length)
    violations.push(`environment registration state appeared: ${present.join(', ')}`);
  return {
    violations,
    process_starts: starts,
    process_samples_count: samples.length,
    root_pid_seen: rootSeen,
    applog_directory: logsDir,
    applog_files: scan.files,
    applog_non_empty: scan.nonEmpty,
    applog_forbidden_hits: scan.hits,
    environment_state_absent: environmentSurfaces.filter((relative) =>
      !fs.existsSync(path.join(copy, relative))),
    combined: 'CIM owned lineage (query-time lower bound, complete_start_trace=false), independently sampled owned process names + ' +
      'non-empty AppLog without Installer command starts (any action, incl. list/' +
      'switch reservation) or runtime demand connect/initialize_default + fresh ' +
      'state kept no environment registry/store/plans/cache/marker on the ' +
      'state-anchored shared store (<copy>/state/store surfaces)',
  };
}

// Bounded self-check of the pure zero-evidence rules (no GUI, no candidate):
// a benign non-empty log is allowed, any Installer command-start prefix
// (including read-only list / switch reservation) is rejected, and a log set
// without non-empty content is rejected. The temp directory is kept as
// evidence; nothing is deleted.
async function selfCheck() {
  // Task #215 (no GUI): real in-page probe source on a fake DOM - UTF-8
  // Chinese payload bytes, scope aggregation, bounded maps/lists.
  const measurement = await runMeasurementSelfCheck();
  const root = path.join(fs.realpathSync(os.tmpdir()),
    `rt214-zero-selfcheck-${randomUUID().slice(0, 8)}`);
  const benign = path.join(root, 'benign');
  const installer = path.join(root, 'installer');
  const empty = path.join(root, 'empty');
  fs.mkdirSync(benign, { recursive: true });
  fs.mkdirSync(installer, { recursive: true });
  fs.mkdirSync(empty, { recursive: true });
  fs.writeFileSync(path.join(benign, 'winui-dev-20380119.log'),
    '00:00:00.000 [INFO ] OnLaunched: profile=production shellOnly=False\r\n' +
    '00:00:01.000 [INFO ] Runtime demand: ready\r\n');
  fs.writeFileSync(path.join(installer, 'winui-dev-20380119.log'),
    '00:00:00.000 [INFO ] Runtime Installer command starting action=list\r\n' +
    '00:00:02.000 [INFO ] Runtime Installer command starting action=switch reservation\r\n');
  fs.writeFileSync(path.join(empty, 'winui-dev-20380119.log'), '   \r\n');
  const allowed = scanAppLog(benign);
  assert.equal(allowed.nonEmpty, true, 'benign case must count as non-empty');
  assert.deepEqual(allowed.hits, [], 'benign log must not be rejected');
  const installerScan = scanAppLog(installer);
  assert.equal(installerScan.nonEmpty, true, 'installer case must count as non-empty');
  assert(installerScan.hits.length >= 1 &&
    installerScan.hits.every((hit) => hit.includes('Runtime Installer command starting action=')),
  `read-only Installer command starts must be rejected: ${JSON.stringify(installerScan.hits)}`);
  const emptyScan = scanAppLog(empty);
  assert.equal(emptyScan.nonEmpty, false, 'blank-only log must not count as non-empty');
  assert.deepEqual(emptyScan.hits, []);
  const noFiles = scanAppLog(path.join(root, 'missing'));
  assert.equal(noFiles.nonEmpty, false, 'missing directory must not count as non-empty');
  console.log(JSON.stringify({ self_check: 'passed', root, cases: {
    benign_allowed: allowed.hits.length === 0,
    installer_list_and_switch_reservation_rejected: installerScan.hits.length > 0,
    empty_rejected: !emptyScan.nonEmpty && !noFiles.nonEmpty,
  }, measurement_215: measurement }));
}

async function run(candidate, work) {
  const root = path.join(work, `rt214-tools-${randomUUID().slice(0, 8)}`);
  fs.mkdirSync(root, { recursive: true });
  const copy = path.join(root, 'candidate');
  const record = { schema_version: 1, smoke: 'runtime-demand-tools', issue: '214 AC1',
    state: 'failed', root, app_pid: null,
    launch: { shell_only: false, args: ['--profile', 'production', '--install-root', '<copy>'],
      env_cleaned: ['VIBEOCR_SELF_TEST*', 'VIBEOCR_SOAK*'],
      note: 'normal Portable boot; no self-test/soak injection; no supervisor health trace env' },
    steps: {}, coverage_gaps: [
      'scene-editor 保存 not separately driven (imageEdit 保存 covered via the real FileSavePicker; scene 复制/贴图 covered)',
      'settings hotkey re-recording not covered (rows verified read-only; floating toolbar toggled)',
    ], evidence_gaps: [
      'CIM 250ms snapshots are a lower bound (complete_start_trace=false); short-lived processes can be missed',
      'AppLog read from the current candidate state/logs path; final location per Owner landing',
      'environment surfaces checked semantically by presence only; no hashes by design',
      'zero AppLog evidence requires a non-empty log plus no Installer command start (any action) and no runtime demand connect/initialize_default',
    ] };
  const ctx = { child: null, browser: null, page: null, main: null,
    root, copy, fixture: null, fixtureCleanupErrors: [], collector: null };
  let watch;
  try {
    for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
      'runtime/backend/runtime-manifest.json', 'runtime/env/runtime-env/python.exe'])
      assert(fs.statSync(path.join(candidate, marker)).isFile(), `Candidate marker missing: ${marker}`);
    assert(!fs.existsSync(path.join(candidate, 'state')), 'Source candidate must have no state.');
    await exec('robocopy', [candidate, copy, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP'],
      { timeout: 30 * 60 * 1000, windowsHide: true })
      .catch((error) => {
        if (!Number.isInteger(error.code) || error.code < 1 || error.code > 7) throw error;
      });
    assert(!fs.existsSync(path.join(copy, 'state')), 'Copied candidate already has state.');
    const debugPort = await freePort();
    const proxyPort = await freePort();
    assert.notEqual(proxyPort, debugPort, 'Unavailable HTTP proxy must not use the debug listener.');
    const blockedProxy = `http://127.0.0.1:${proxyPort}`;
    record.network_policy = { app_http_proxy: blockedProxy, external_http: 'refused or blocked by client',
      allowed_web_origin: 'https://app.vibeocr', loopback_allowed: true, os_network_changed: false,
      blocked_external_requests: 0 };
    record.debug_port = debugPort;
    watch = await monitor(root);
    const env = { ...process.env,
      WEBVIEW2_USER_DATA_FOLDER: path.join(root, 'webview'),
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:
        `--remote-debugging-port=${debugPort} --remote-debugging-address=127.0.0.1` };
    for (const name of Object.keys(env))
      if (name.startsWith('VIBEOCR_SELF_TEST') || name.startsWith('VIBEOCR_SOAK') ||
          ['http_proxy', 'https_proxy', 'all_proxy', 'no_proxy'].includes(name.toLowerCase())) delete env[name];
    Object.assign(env, { HTTP_PROXY: blockedProxy, HTTPS_PROXY: blockedProxy, ALL_PROXY: blockedProxy,
      NO_PROXY: 'localhost,127.0.0.1,::1' });
    const executable = path.join(copy, 'app', 'VibeOCR.WinUI.exe');
    ctx.child = spawn(executable, ['--profile', 'production', '--install-root', copy],
      { cwd: path.dirname(executable), env, stdio: 'ignore' });
    assert(Number.isSafeInteger(ctx.child.pid), 'App spawn failed');
    record.app_pid = ctx.child.pid;
    fs.writeFileSync(path.join(root, 'root-pid.txt'), String(ctx.child.pid));
    const connected = await connect(ctx.child, debugPort);
    ctx.browser = connected.browser;
    ctx.page = connected.page;
    const guardedContexts = ctx.browser.contexts();
    for (const context of guardedContexts) await context.route('**/*', (route) => {
      if (allowsLocalToolRequest(route.request().url())) return route.continue();
      record.network_policy.blocked_external_requests++;
      return route.abort('blockedbyclient');
    });
    record.network_policy.guarded_contexts = guardedContexts.length;
    // Task #215: passive measurement (listener-only, additive). Contexts
    // existing at attach are instrumented; the scene page's context is
    // confirmed explicitly via page.context() at harvest time. The already-
    // loaded main page gets the probe directly (late install recorded as
    // such, first_screen_missed=true in measurement_215).
    ctx.collector = createWorkbenchCollector();
    await ctx.collector.attachBrowser(ctx.browser)
      .catch((error) => ctx.collector.noteError(`browser attach: ${error.message}`));
    await ctx.collector.installOnPage(ctx.page)
      .catch((error) => ctx.collector.noteError(`main page install: ${error.message}`));
    ctx.main = (await windows(ctx.child.pid))
      .filter((item) => item.Visible && area(item) > 20000)
      .sort((a, b) => area(b) - area(a))[0];
    assert(ctx.main, 'Owned main window missing');
    await focusMain(ctx);
    record.steps.qrcode = await qrToolStep(ctx);
    record.steps.settings = await settingsStep(ctx);
    record.steps.image_edit = await imageEditStep(ctx);
    record.steps.screenshot_capture = await captureStep(ctx);
    assert(ctx.browser.contexts().every(context => guardedContexts.includes(context)),
      'An unguarded WebView context appeared during the tool actions.');
    record.functional_valid = true;
  } catch (error) {
    record.error = error.stderr || `${error.name}: ${error.message}`;
    record.failure_at_ms = Date.now();
    if (ctx.page) await ctx.page.screenshot({ path: path.join(root, 'failure.png') }).catch(() => {});
  } finally {
    // Always finish: owned app/fixture/monitor cleanup, batch lineage cleanup,
    // then collect the combined zero evidence before writing result.json.
    const cleanupErrors = [...ctx.fixtureCleanupErrors];
    if (ctx.fixture) {
      try {
        await stopOwned(ctx.fixture.child, 'quit', {
          FixturePid: ctx.fixture.pid, Handle: ctx.fixture.root, ThreadId: ctx.fixture.threadId,
        });
      } catch (error) { cleanupErrors.push(`fixture ${ctx.fixture.pid}: ${error.message}`); }
      ctx.fixture = null;
    }
    // Task #215: bounded measurement recovery before the pages go away;
    // gaps land in measurement_215.errors/limitations and never mask the
    // functional/zero-evidence outcome below.
    if (ctx.collector && ctx.page) await ctx.collector.harvest('main', ctx.page);
    if (ctx.browser) await ctx.browser.close().catch(() => {});
    if (ctx.child) {
      try {
        const main = (await windows(ctx.child.pid).catch(() => []))
          .filter((item) => item.Visible && area(item) > 20000)
          .sort((a, b) => area(b) - area(a))[0];
        if (main) await stopOwned(ctx.child, 'close', { AppPid: ctx.child.pid, Handle: main.Handle });
        else await stopOwned(ctx.child, 'quit', {});
      } catch (error) { cleanupErrors.push(`app ${ctx.child.pid}: ${error.message}`); }
    }
    if (watch) {
      try {
        fs.writeFileSync(path.join(root, 'stop-monitor'), 'stop');
        await Promise.race([watch.finished, delay(15000)
          .then(() => { throw new Error('Process monitor did not finish'); })]);
      } catch (error) { cleanupErrors.push(`monitor: ${error.message}`); }
      try { await cleanupLineage(root); }
      catch (error) { cleanupErrors.push(`lineage: ${error.message}`); }
    }
    if (cleanupErrors.length) record.cleanup_error = cleanupErrors.join('; ');
    if (ctx.collector) {
      record.measurement_215 = ctx.collector.finalise();
      try {
        fs.writeFileSync(path.join(root, 'workbench-delivery-215.json'),
          JSON.stringify(record.measurement_215, null, 2));
      } catch (error) {
        record.measurement_215.write_error = error.message;
      }
    }
    try {
      record.zero_evidence = zeroEvidence(copy, root, record.app_pid);
      if (fs.existsSync(path.join(root, 'monitor-info.json')))
        record.monitor = JSON.parse(fs.readFileSync(path.join(root, 'monitor-info.json'), 'utf8'));
      else record.evidence_gaps.push('monitor-info.json missing');
      const violations = [...(record.zero_evidence.violations ?? [])];
      if (record.cleanup_error) violations.push(`cleanup: ${record.cleanup_error}`);
      if (record.error) violations.push(`functional: ${record.error}`);
      if (violations.length) record.zero_evidence_violations = violations;
      else if (record.functional_valid) record.state = 'passed';
    } catch (error) {
      record.zero_evidence_error = `${error.name}: ${error.message}`;
    }
    fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(record, null, 2));
  }
  return record;
}

async function main() {
  assert.equal(process.platform, 'win32', 'Windows desktop required');
  if (process.argv.includes('--self-check')) {
    await selfCheck();
    return;
  }
  const candidate = fs.realpathSync(option('--candidate'));
  const work = fs.realpathSync(option('--work'));
  assert(!contained(candidate, work) && !contained(work, candidate),
    'Candidate and Work roots must not nest.');
  const record = await run(candidate, work);
  console.log(JSON.stringify({ state: record.state, root: record.root,
    error: record.error ?? record.zero_evidence_violations?.[0] ?? null,
    steps: Object.fromEntries(Object.entries(record.steps)
      .map(([key, value]) => [key, value.state ?? 'not-run'])),
    coverage_gaps: record.coverage_gaps, evidence_gaps: record.evidence_gaps }));
  process.exitCode = record.state === 'passed' ? 0 : 1;
}

if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error); process.exitCode = 1; });
}
