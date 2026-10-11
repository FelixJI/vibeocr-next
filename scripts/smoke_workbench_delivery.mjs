// Task #215 scene/background coexistence trace smoke: proves whether the
// screenshot scene receives irrelevant scope broadcasts while real public
// background actions run on the main window. Pure public-UI steps only:
// normal Portable boot -> public screenshot -> scene ready -> owned-window
// restore of the main window -> QR generate (qrcode scope) + open synthetic
// PDF via owned codes-open-file (pdf scope) + theme switch via the public
// 切换主题 menu (light settings scope). The scene's own session/input must
// stay intact throughout. Modes: 'baseline' measures and records scene scope
// messages/UTF-8 bytes without any zero requirement; 'after' additionally
// asserts scene scope:pdf/batch/qrcode == 0 and settings messages without
// env-ish state fields. Both modes require a main-window receipt (pdf/qrcode/
// settings scope messages observed on the main page) so a scene-zero can
// never pass vacuously. No private bridge commands, no direct Runtime HTTP,
// no fabricated state, no Issue214 zero-runtime rules here. Evidence root is
// never deleted; cleanup follows owned PID + creation_filetime lineage only.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { native, windows, waitForWindows, area, inside, delay,
  startFixture, stopOwned } from './smoke_native_actions.mjs';
import { connect, monitor, cleanupLineage, freePort, focusMain } from './smoke_runtime_demand_tools.mjs';
import { createWorkbenchCollector } from './workbench_delivery_probe.mjs';

const exec = promisify(execFile);
const scripts = path.dirname(fileURLToPath(import.meta.url));
const repo = path.dirname(scripts);
const waitMs = 90 * 1000;

function option(name, fallback) {
  const index = process.argv.indexOf(name);
  if (index < 0) { assert(fallback !== undefined, `Required option ${name}`); return fallback; }
  assert(process.argv[index + 1], `Missing value for ${name}`);
  return process.argv[index + 1];
}

function contained(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

const jsonLines = (file) => fs.existsSync(file)
  ? fs.readFileSync(file, 'utf8').split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];

// Pure trace evaluation (unit-testable without GUI). Receipt is required in
// BOTH modes; the heavy-scope zero assertions only apply to mode 'after'.
// settingsEnvHits counts only NON-EMPTY environment data/log values seen in
// scene settings messages (empty defaults like canPreviewInstall:false or
// sources:null are legal projections and never counted).
export function evaluateTrace({ mode, scene, main, settingsEnvHits }) {
  assert(['baseline', 'after'].includes(mode), `Unknown mode ${mode}`);
  const violations = [];
  const sceneCount = (scope) => scene?.[`scope:${scope}`]?.count ?? 0;
  const mainCount = (scope) => main?.[`scope:${scope}`]?.count ?? 0;
  for (const scope of ['pdf', 'qrcode', 'settings'])
    if (mainCount(scope) < 1)
      violations.push(`main window never received scope:${scope} messages (no background receipt)`);
  if (mode === 'after') {
    for (const scope of ['pdf', 'batch', 'qrcode'])
      if (sceneCount(scope) !== 0)
        violations.push(`scene received ${sceneCount(scope)} scope:${scope} messages after filtering`);
    if ((settingsEnvHits ?? 0) > 0)
      violations.push(`scene settings messages carried non-empty env-ish state values (${settingsEnvHits} hits)`);
  }
  return violations;
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

// Owned restore + focus of the main window. focusMain only asserts an
// already-visible window; after the public screenshot the app hides the main
// window, so restore it through the verified owned native 'restore' action
// (native_actions_fixture.ps1: Show with SW_RESTORE=9, Handle+AppPid bound)
// and only then focus. Bounded condition polling, no arbitrary sleeps.
async function showMain(ctx) {
  await native('restore', { AppPid: ctx.child.pid, Handle: ctx.main.Handle });
  const until = Date.now() + 10000;
  let visible = false;
  while (Date.now() < until) {
    visible = (await windows(ctx.child.pid))
      .some((item) => item.Handle === ctx.main.Handle && item.Visible && !item.Iconic);
    if (visible) break;
    await delay(180);
  }
  assert(visible, 'Owned main window was not restored (SW_RESTORE).');
  await focusMain(ctx);
}

// Passive main-window listeners (listener-only, same contract as the shipped
// smokes): recognition screenshotSession identity + pdf terminal state + the
// latest settings theme payload value (for the theme increment check).
async function watchMainState(page) {
  await page.evaluate(() => {
    window.__rt215Session = null;
    window.__rt215Pdf = null;
    window.__rt215Theme = null;
    window.chrome.webview.addEventListener('message', ({ data }) => {
      let envelope = data;
      try { envelope = typeof data === 'string' ? JSON.parse(data) : data; } catch { return; }
      if (envelope?.kind !== 'event' || envelope.type !== 'app.state') return;
      const { scope, state } = envelope.payload ?? {};
      if (scope === 'recognition') {
        const session = state?.screenshotSession;
        if (typeof session?.sessionId === 'string' && Number.isSafeInteger(session.revision))
          window.__rt215Session = { sessionId: session.sessionId, revision: session.revision,
            inputUrl: state?.input?.url ?? null };
      } else if (scope === 'pdf' && state) {
        window.__rt215Pdf = { at_ms: Date.now(), isBusy: state.isBusy,
          sessionId: state.sessionId ?? null, pageCount: state.pageCount ?? null,
          statusCode: state.statusCode ?? null };
      } else if (scope === 'settings' && state) {
        window.__rt215Theme = { value: typeof state.theme === 'string' ? state.theme : null,
          at_ms: Date.now() };
      }
    });
  });
}

// Passive scene-side watchers, installed as one self-contained function so
// the exact shipped source is also exercised by the no-GUI self-check.
// FROZEN CP1 FACT (features/ImageEditPage.tsx: scene ? "recognition" :
// "imageEdit"): the scene route is carried by the RECOGNITION scope, with
// identity at state.screenshotSession.sessionId/revision (+ state.input.url)
// - the same real schema as the main window. Bootstrap coverage comes from
// the app.bootstrap RESPONSE envelope (payload.features.recognition).
// (a) settings scope: bounded key set; env-ish keys only count when their
//     value is NON-EMPTY data (null/false/0/''/[]/{} are legal projected
//     defaults) with a capped kind/length sample, never full payloads;
// (b) recognition scope: latest scene-side screenshotSession identity.
//     imageEdit-scope messages are intentionally ignored for the scene.
export function installSceneWatchers(source) {
  const w = window;
  if (w.__rt215SceneWatch) return; // idempotent, never resets
  if (!w.chrome?.webview) return; // Late installation remains an explicit coverage gap.
  const isEmptyDefault = (value) => {
    if (value === null || value === undefined || value === false || value === 0) return true;
    if (typeof value === 'string') return value.trim() === '';
    if (Array.isArray(value)) return value.length === 0;
    if (typeof value === 'object') return Object.keys(value).length === 0;
    return false;
  };
  w.__rt215SceneWatch = {
    installed_via: source === 'init-script' || source === 'late-evaluate'
      ? source : 'unknown',
    settings: { messages: 0, keys: {}, env_field_hits: 0, env_non_empty_sample: [] },
    recognition: null,
  };
  const captureRecognition = (state, origin) => {
    const session = state?.screenshotSession;
    if (typeof session?.sessionId !== 'string' || !Number.isSafeInteger(session.revision)) return;
    w.__rt215SceneWatch.recognition = { sessionId: session.sessionId,
      revision: session.revision, inputUrl: state?.input?.url ?? null,
      at_ms: Date.now(), source: origin };
  };
  w.chrome.webview.addEventListener('message', ({ data }) => {
    let envelope = data;
    try { envelope = typeof data === 'string' ? JSON.parse(data) : data; } catch { return; }
    if (envelope?.kind === 'event' && envelope.type === 'app.state') {
      const { scope, state } = envelope.payload ?? {};
      if (scope === 'recognition') captureRecognition(state, 'event');
      else if (scope === 'settings' && state && typeof state === 'object') {
        const record = w.__rt215SceneWatch.settings;
        record.messages += 1;
        for (const [key, value] of Object.entries(state)) {
          if (Object.hasOwn(record.keys, key) || Object.keys(record.keys).length < 32)
            record.keys[key] = (record.keys[key] ?? 0) + 1;
          if (!/env|environment|install|runtime|source|maintenance|log/i.test(key)) continue;
          if (isEmptyDefault(value)) continue;
          record.env_field_hits += 1;
          if (record.env_non_empty_sample.length < 8)
            record.env_non_empty_sample.push({ key,
              kind: Array.isArray(value) ? 'array' : typeof value,
              length: Array.isArray(value) ? value.length
                : (value && typeof value === 'object' ? Object.keys(value).length
                  : String(value).length) });
        }
      }
    } else if (envelope?.kind === 'response' && envelope.type === 'app.bootstrap') {
      const features = envelope.payload?.features;
      if (features && typeof features === 'object')
        captureRecognition(features.recognition, 'bootstrap');
    }
  });
}

// Public canvas state only: existence, dimensions and a sampled non-blank
// count. Explicitly NOT a checksum/digest - byte identity is not this task's
// contract, and a theme switch may legitimately restyle the canvas
// background. Identity stability is proven by the real session id / input
// resource URL / revision above; pixels only prove the image is still shown.
function sceneCanvasState(scene) {
  return scene.evaluate(() => {
    const canvas = document.querySelector('canvas[aria-label="图片检查画布"]');
    if (!canvas) return null;
    const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
    let changed = 0;
    for (let i = 0; i < data.length; i += 40)
      if (data[i] !== 22 || data[i + 1] !== 22 || data[i + 2] !== 22) changed += 1;
    return { width: canvas.width, height: canvas.height, changedCount: changed };
  });
}

// Public 截图 flow (same core as the shipped #214 smoke, minus its annotation
// steps): synthetic fixture desktop -> 截图 button -> owned overlay -> manual
// drag -> Enter -> scene WebView ready.
async function openScene(ctx) {
  const { child, browser, page, root } = ctx;
  const fixture = await startFixture();
  ctx.fixture = fixture;
  fs.writeFileSync(path.join(root, 'fixture.json'), JSON.stringify({ pid: fixture.pid, root: fixture.root, background: fixture.background }));
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
  await showMain(ctx);
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
  const region = { width: end.X - start.X, height: end.Y - start.Y };
  const label = await native('selection', { AppPid: child.pid, Handle: overlay.Handle });
  assert(label.includes(`手动 · ${region.width} × ${region.height} px`),
    `Manual selection label mismatch: ${label}`);
  const click = { AppPid: child.pid,
    X: start.X + Math.floor(region.width / 2), Y: start.Y + Math.floor(region.height / 2) };
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
      .find((item) => item !== page && item.url().includes('#/imageEdit?scene=1'));
    if (!scene) await delay(100);
  }
  assert(scene, 'Screenshot scene WebView did not appear.');
  await scene.locator('html[data-capture-scene="true"]').waitFor({ state: 'attached', timeout: 12000 });
  // Install the probe BEFORE any further public operation (awaited; context
  // attach inside; idempotent against an init-script probe, never resets).
  await ctx.collector.installOnPage(scene)
    .catch((error) => ctx.collector.noteError(`scene install: ${error.message}`));
  // Late install only if the init script did not reach this page's context
  // (idempotent, never resets an init-script watcher; installed_via keeps
  // the coverage claim honest).
  await scene.evaluate(installSceneWatchers, 'late-evaluate')
    .catch((error) => ctx.collector.noteError(`scene watcher install: ${error.message}`));
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
  await page.waitForFunction(() => {
    const session = window.__rt215Session;
    return !!(session && typeof session.inputUrl === 'string');
  }, null, { timeout: 10000 });
  return scene;
}

async function backgroundActions(ctx, root) {
  const { page } = ctx;
  const steps = {};
  // QR generate (qrcode scope): public QR tool page.
  await showMain(ctx);
  await page.getByRole('link', { name: '二维码与条码', exact: true }).click();
  await page.getByRole('heading', { name: '二维码与条码', exact: true }).waitFor();
  await page.getByRole('tab', { name: '生成', exact: true }).click();
  const payload = `RT215-BG-${randomUUID().slice(0, 8)}`;
  await page.getByLabel('生成格式').selectOption('qrcode');
  await page.getByLabel('输入内容', { exact: true }).fill(payload);
  await page.getByRole('button', { name: '生成图片', exact: true }).click();
  await page.waitForFunction(() => {
    const img = document.querySelector('img[alt="当前二维码与条码预览"]');
    return img && img.src && img.complete && img.naturalWidth > 0;
  }, null, { timeout: 15000 });
  steps.qrcode_generated = payload;
  // Open synthetic PDF (pdf scope): public PDF page + owned codes-open-file.
  await showMain(ctx);
  await page.getByRole('link', { name: 'PDF', exact: true }).click();
  await page.getByRole('button', { name: '打开 PDF', exact: true }).click();
  await native('codes-open-file', { Handle: ctx.main.Handle, AppPid: ctx.child.pid,
    FilePath: path.join(root, 'inputs', 'vibeocr-123.pdf') }, 20000);
  await page.waitForFunction(() => {
    const pdf = window.__rt215Pdf;
    return !!pdf && pdf.isBusy === false &&
      ((pdf.sessionId && pdf.pageCount === 1) ||
        ['pdf.failed', 'pdf.cancelled'].includes(pdf.statusCode));
  }, null, { timeout: waitMs });
  const pdf = await page.evaluate(() => window.__rt215Pdf);
  assert(!['pdf.failed', 'pdf.cancelled'].includes(pdf.statusCode),
    `PDF terminal failure: ${pdf.statusCode}`);
  const thumb = page.getByRole('img', { name: '第 1 页缩略图', exact: true });
  await thumb.waitFor({ timeout: waitMs });
  assert(await thumb.evaluate((element) => element.complete && element.naturalWidth > 0),
    'PDF thumbnail did not render');
  steps.pdf_opened = { sessionId: pdf.sessionId, pageCount: pdf.pageCount };
  // Theme switch (light settings scope): public toolbar menu. Wait for an
  // INCREMENT over the pre-action settings count (earlier settings traffic
  // from the PDF/QR steps must not satisfy this) AND for the actual passive
  // theme payload value observed on the main window.
  await showMain(ctx);
  const settingsCountBefore = await page.evaluate(() =>
    window.__rt215?.stats?.messages?.by_key?.['scope:settings']?.count ?? 0);
  const themeBefore = await page.evaluate(() => window.__rt215Theme?.value ?? null);
  await page.getByRole('button', { name: '切换主题', exact: true }).click();
  await page.getByRole('menuitemradio', { name: '深色', exact: true }).click();
  await page.waitForFunction((before) => {
    const bucket = window.__rt215?.stats?.messages?.by_key?.['scope:settings'];
    return !!bucket && bucket.count > before && window.__rt215Theme?.value === 'dark';
  }, settingsCountBefore, { timeout: 15000 });
  steps.theme_switched = { from: themeBefore, to: 'dark',
    settings_count_before: settingsCountBefore };
  return steps;
}

async function run(candidate, work, mode) {
  const root = path.join(work, `rt215-delivery-${randomUUID().slice(0, 8)}`);
  fs.mkdirSync(root, { recursive: true });
  const copy = path.join(root, 'candidate');
  const record = { schema_version: 1, smoke: 'workbench-delivery', issue: '215 scene/background trace',
    mode, state: 'failed', root, app_pid: null, steps: {}, violations: [] };
  const ctx = { child: null, browser: null, page: null, main: null, collector: null,
    root, copy, fixture: null };
  let scene = null;
  let watch;
  const cleanupErrors = [];
  try {
    assert(fs.statSync(path.join(candidate, 'app', 'VibeOCR.WinUI.exe')).isFile(),
      'Candidate App missing');
    await exec('uv', ['run', '--frozen', 'python', path.join(scripts, 'runtime_demand_fixtures.py'),
      '--out', path.join(root, 'inputs')], { cwd: repo, timeout: 30000, windowsHide: true });
    await exec('robocopy', [candidate, copy, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP'],
      { timeout: 30 * 60 * 1000, windowsHide: true })
      .catch((error) => {
        if (!Number.isInteger(error.code) || error.code < 1 || error.code > 7) throw error;
      });
    assert(!fs.existsSync(path.join(copy, 'state')), 'Copied candidate already has state');
    const debugPort = await freePort();
    record.debug_port = debugPort;
    watch = await monitor(root);
    const env = { ...process.env,
      WEBVIEW2_USER_DATA_FOLDER: path.join(root, 'webview'),
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:
        `--remote-debugging-port=${debugPort} --remote-debugging-address=127.0.0.1` };
    for (const name of Object.keys(env))
      if (name.startsWith('VIBEOCR_SELF_TEST') || name.startsWith('VIBEOCR_SOAK')) delete env[name];
    const executable = path.join(copy, 'app', 'VibeOCR.WinUI.exe');
    ctx.child = spawn(executable, ['--profile', 'production', '--install-root', copy],
      { cwd: path.dirname(executable), env, stdio: 'ignore' });
    assert(Number.isSafeInteger(ctx.child.pid), 'App spawn failed');
    record.app_pid = ctx.child.pid;
    fs.writeFileSync(path.join(root, 'root-pid.txt'), String(ctx.child.pid));
    const connected = await connect(ctx.child, debugPort);
    ctx.browser = connected.browser;
    ctx.page = connected.page;
    ctx.collector = createWorkbenchCollector();
    await ctx.collector.attachBrowser(ctx.browser)
      .catch((error) => ctx.collector.noteError(`browser attach: ${error.message}`));
    // Scene watcher at document start: await addInitScript on every context
    // observed NOW (before the screenshot creates the scene) so the scene's
    // app.bootstrap response and first recognition events are covered. A
    // context discovered later only gets the late evaluate install inside
    // openScene - recorded via installed_via and, when identity is still
    // unobserved, as an explicit gap (never a fake coverage claim).
    for (const context of ctx.browser.contexts())
      await context.addInitScript(installSceneWatchers, 'init-script')
        .catch((error) => ctx.collector.noteError(`scene watcher init: ${error.message}`));
    await ctx.collector.installOnPage(ctx.page)
      .catch((error) => ctx.collector.noteError(`main install: ${error.message}`));
    await watchMainState(ctx.page);
    ctx.main = (await windows(ctx.child.pid))
      .filter((item) => item.Visible && area(item) > 20000)
      .sort((a, b) => area(b) - area(a))[0];
    assert(ctx.main, 'Owned main window missing');
    scene = await openScene(ctx);
    record.identity_before = { main: {
        sessionId: (await ctx.page.evaluate(() => window.__rt215Session)).sessionId,
        inputUrl: (await ctx.page.evaluate(() => window.__rt215Session)).inputUrl },
      sceneUrl: scene.url(), canvas: await sceneCanvasState(scene),
      sceneSide: await scene.evaluate(() => window.__rt215SceneWatch?.recognition ?? null),
      sceneWatcherAtDocumentStart: await scene.evaluate(() =>
        window.__rt215SceneWatch?.installed_via === 'init-script') };
    record.steps.background = await backgroundActions(ctx, root);
    // Scene integrity after the background actions.
    assert(scene.url().includes('#/imageEdit?scene=1'), 'Scene URL changed');
    await scene.waitForFunction(() => {
      const canvas = document.querySelector('canvas[aria-label="图片检查画布"]');
      if (!canvas) return false;
      const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
      let changed = 0;
      for (let i = 0; i < data.length; i += 40)
        if (data[i] !== 22 || data[i + 1] !== 22 || data[i + 2] !== 22) changed++;
      return changed > 100;
    }, null, { timeout: 10000 });
    const sessionAfter = await ctx.page.evaluate(() => window.__rt215Session);
    assert.equal(sessionAfter.sessionId, record.identity_before.main.sessionId,
      'Scene session identity changed during background actions');
    assert.equal(sessionAfter.inputUrl, record.identity_before.main.inputUrl,
      'Scene session input resource changed during background actions');
    const canvasAfter = await sceneCanvasState(scene);
    assert(canvasAfter, 'Scene canvas disappeared during background actions');
    assert.equal(canvasAfter.width, record.identity_before.canvas.width,
      'Scene canvas width changed');
    assert.equal(canvasAfter.height, record.identity_before.canvas.height,
      'Scene canvas height changed');
    assert(canvasAfter.changedCount > 100, 'Scene canvas no longer shows the captured image');
    const sceneSideAfter = await scene.evaluate(() =>
      window.__rt215SceneWatch?.recognition ?? null);
    const sceneIdentityObserved = Boolean(record.identity_before.sceneSide && sceneSideAfter);
    if (sceneIdentityObserved) {
      assert.equal(sceneSideAfter.sessionId, record.identity_before.sceneSide.sessionId,
        'Scene-side recognition session identity changed');
      assert.equal(sceneSideAfter.inputUrl, record.identity_before.sceneSide.inputUrl,
        'Scene-side recognition input resource changed');
      assert.equal(sceneSideAfter.revision, record.identity_before.sceneSide.revision,
        'Scene-side recognition revision changed');
    }
    record.identity_after = { main: sessionAfter, canvas: canvasAfter,
      sceneSide: sceneSideAfter, sceneSideIdentityObserved: sceneIdentityObserved };
    // Harvest the bounded scene/main snapshots and evaluate the trace.
    const sceneSnapshot = await ctx.collector.harvest('scene', scene);
    const mainSnapshot = await ctx.collector.harvest('main', ctx.page);
    const sceneSettings = await scene.evaluate(() =>
      window.__rt215SceneWatch?.settings
        ?? { messages: 0, keys: {}, env_field_hits: 0, env_non_empty_sample: [] });
    record.scene_trace = { messages: sceneSnapshot?.messages ?? null,
      resources_js: sceneSnapshot ? { js_count: sceneSnapshot.resources.js_count,
        script_or_link_without_js_extension:
          sceneSnapshot.resources.script_or_link_without_js_extension.count } : null,
      install_source: sceneSnapshot?.install_source ?? null,
      first_screen_missed: sceneSnapshot?.first_screen_missed ?? null,
      settings_messages: sceneSettings.messages, settings_keys: sceneSettings.keys,
      settings_env_field_hits: sceneSettings.env_field_hits,
      settings_env_non_empty_sample: sceneSettings.env_non_empty_sample };
    record.main_receipt = mainSnapshot?.messages ?? null;
    const violations = evaluateTrace({ mode,
      scene: sceneSnapshot?.messages?.by_key ?? {},
      main: mainSnapshot?.messages?.by_key ?? {},
      settingsEnvHits: sceneSettings.env_field_hits });
    if (!sceneIdentityObserved) {
      // After mode: an unobserved scene identity is INSUFFICIENT EVIDENCE -
      // a non-blank canvas alone must never substitute for the real session
      // identity. Baseline: record the gap honestly and keep measuring.
      if (mode === 'after') violations.push('scene recognition identity never observed ' +
        '(app.bootstrap/event missed): insufficient evidence for the after assertion');
      else record.scene_identity_gap = true;
    }
    record.violations = violations;
    record.functional_valid = true;
  } catch (error) {
    record.error = error.stderr || `${error.name}: ${error.message}`;
    record.failure_at_ms = Date.now();
    if (ctx.page) await ctx.page.screenshot({ path: path.join(root, 'failure.png') }).catch(() => {});
  } finally {
    // Recover the bounded scene snapshot while the page is still open: a
    // harvest failure on a live page is a real collection failure (surfaced
    // via measurement errors -> violations below), never a silent pass.
    if (ctx.collector && scene && !scene.isClosed())
      await ctx.collector.harvest('scene', scene);
    // Scene is closed through its public 结束会话 button while it still exists.
    if (scene) {
      try {
        const closed = scene.waitForEvent('close', { timeout: 10000 });
        await scene.getByRole('button', { name: '结束会话', exact: true }).click().catch(() => {});
        await closed;
      } catch (error) { cleanupErrors.push(`scene close: ${error.message}`); }
    }
    if (ctx.collector && ctx.page) await ctx.collector.harvest('main', ctx.page);
    if (ctx.collector) {
      record.measurement_215 = ctx.collector.finalise();
      // Distinguish declared first-screen gaps (pages.*.first_screen_missed,
      // expected and recorded) from actual collection failures (errors here):
      // the latter must fail the run, not pass silently.
      const collectErrors = record.measurement_215.errors ?? [];
      if (collectErrors.length)
        record.violations.push(`measurement collection failure (distinct from declared ` +
          `first-screen gaps): ${collectErrors.join('; ')}`);
      try {
        fs.writeFileSync(path.join(root, 'workbench-delivery-215.json'),
          JSON.stringify(record.measurement_215, null, 2));
      } catch (error) { record.measurement_215.write_error = error.message; }
    }
    if (ctx.fixture) {
      try {
        await stopOwned(ctx.fixture.child, 'quit', { FixturePid: ctx.fixture.pid,
          Handle: ctx.fixture.root, ThreadId: ctx.fixture.threadId });
      } catch (error) { cleanupErrors.push(`fixture ${ctx.fixture.pid}: ${error.message}`); }
      ctx.fixture = null;
    }
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
    if (cleanupErrors.length) {
      record.cleanup_error = cleanupErrors.join('; ');
      record.violations.push(`cleanup: ${record.cleanup_error}`);
    }
    record.process_starts = jsonLines(path.join(root, 'process-starts.jsonl'));
    if (record.error) record.violations.push(`functional: ${record.error}`);
    if (record.violations.length === 0 && record.functional_valid) record.state = 'passed';
    fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(record, null, 2));
  }
  return record;
}

async function selfCheck() {
  // Pure evaluation checks (no GUI, no candidate): receipt is mandatory in
  // both modes; heavy-scope zeros and env-field absence only in 'after'.
  const clean = evaluateTrace({ mode: 'after',
    scene: { 'scope:pdf': { count: 0 }, 'scope:batch': { count: 0 }, 'scope:qrcode': { count: 0 } },
    main: { 'scope:pdf': { count: 3 }, 'scope:qrcode': { count: 2 }, 'scope:settings': { count: 1 } },
    settingsEnvHits: 0 });
  assert.deepEqual(clean, [], 'clean after case must pass');
  const heavyScene = evaluateTrace({ mode: 'after',
    scene: { 'scope:pdf': { count: 2 }, 'scope:batch': { count: 0 }, 'scope:qrcode': { count: 1 } },
    main: { 'scope:pdf': { count: 3 }, 'scope:qrcode': { count: 2 }, 'scope:settings': { count: 1 } },
    settingsEnvHits: 0 });
  assert(heavyScene.some((line) => line.includes('scope:pdf')),
    'after mode must reject pdf scope messages in the scene');
  assert(heavyScene.some((line) => line.includes('scope:qrcode')),
    'after mode must reject qrcode scope messages in the scene');
  const envSettings = evaluateTrace({ mode: 'after',
    scene: { 'scope:pdf': { count: 0 }, 'scope:batch': { count: 0 }, 'scope:qrcode': { count: 0 } },
    main: { 'scope:pdf': { count: 1 }, 'scope:qrcode': { count: 1 }, 'scope:settings': { count: 2 } },
    settingsEnvHits: 4 });
  assert(envSettings.some((line) => line.includes('env-ish')),
    'settings messages with env-ish fields must be rejected in after mode');
  const noReceipt = evaluateTrace({ mode: 'baseline', scene: {}, main: {}, settingsEnvHits: 9 });
  assert(noReceipt.every((line) => line.includes('no background receipt')),
    'a missing main-window receipt must fail in every mode');
  const baselineHeavy = evaluateTrace({ mode: 'baseline',
    scene: { 'scope:pdf': { count: 5 }, 'scope:batch': { count: 0 }, 'scope:qrcode': { count: 7 } },
    main: { 'scope:pdf': { count: 3 }, 'scope:qrcode': { count: 2 }, 'scope:settings': { count: 1 } },
    settingsEnvHits: 6 });
  assert.deepEqual(baselineHeavy, [],
    'baseline mode measures and must not require any zero');
  assert.throws(() => evaluateTrace({ mode: 'other', scene: {}, main: {}, settingsEnvHits: 0 }),
    'unknown modes must be rejected');

  // The exact shipped scene watcher (no GUI, fake DOM) with the REAL frozen
  // CP1 schema: scene = recognition scope, identity at
  // state.screenshotSession.sessionId/revision + state.input.url, bootstrap
  // via the app.bootstrap RESPONSE payload.features.recognition. Legal empty
  // settings defaults never count as env data; non-empty data/logs do.
  const listeners = {};
  const fakeScene = {
    location: { href: 'https://app.vibeocr/index.html#/imageEdit?scene=1' },
    chrome: { webview: {
      addEventListener: (type, fn) => { (listeners[type] ??= []).push(fn); } } },
  };
  fakeScene.window = fakeScene;
  const savedSceneWindow = globalThis.window;
  globalThis.window = fakeScene;
  try {
    installSceneWatchers('init-script');
    const watch = fakeScene.__rt215SceneWatch;
    assert.equal(watch.installed_via, 'init-script');
    const send = (envelope) => listeners.message[0]({ data: JSON.stringify(envelope) });
    const recognitionEvent = (state) => ({ version: 2, kind: 'event', id: 'e-1',
      type: 'app.state',
      payload: { sessionId: 'ws-1', revision: 5, scope: 'recognition',
        change: 'replace', state } });
    const recognitionState = (sessionId, revision, url) => ({
      isBusy: false, statusCode: 'recognition.ready',
      screenshotSession: { sessionId, revision, startedAtMs: 1 },
      input: { url }, text: null, result: null });
    send({ version: 2, kind: 'response', id: 'r-1', type: 'app.bootstrap',
      payload: { sessionId: 'ws-1', revision: 5, route: 'imageEdit', theme: 'dark',
        capabilities: ['recognition.annotation'],
        features: { recognition: recognitionState('sess-boot', 12, 'blob:scene-input-boot') } } });
    assert.equal(watch.recognition.sessionId, 'sess-boot',
      'bootstrap response features.recognition must seed the scene identity');
    assert.equal(watch.recognition.inputUrl, 'blob:scene-input-boot');
    assert.equal(watch.recognition.source, 'bootstrap');
    send(recognitionEvent(recognitionState('sess-live', 13, 'blob:scene-input-live')));
    assert.equal(watch.recognition.sessionId, 'sess-live');
    assert.equal(watch.recognition.source, 'event');
    send({ version: 2, kind: 'event', id: 'e-2', type: 'app.state',
      payload: { sessionId: 'ws-1', revision: 6, scope: 'imageEdit',
        change: 'replace',
        state: { sessionId: 'wrong-schema', revision: 99,
          input: { url: 'blob:wrong' } } } });
    assert.equal(watch.recognition.sessionId, 'sess-live',
      'imageEdit-scope messages must never feed the scene identity');
    const projected = { theme: 'dark', isBusy: false, statusCode: 'settings.ready',
      backend: 'cpu', startupEnabled: false, sources: null, pendingBackend: 'cpu',
      canSwitchBackend: false, features: null, maintenance: null, statusMessage: '',
      serviceStatus: '', maintenanceStatus: '', maintenancePhase: '',
      progressActive: false, progressText: '', progressDetail: '', progressPercent: null,
      canPreviewInstall: false };
    send(recognitionEvent(recognitionState('sess-live', 14, 'blob:scene-input-live')));
    send({ version: 2, kind: 'event', id: 'e-3', type: 'app.state',
      payload: { sessionId: 'ws-1', revision: 7, scope: 'settings',
        change: 'replace', state: projected } });
    assert.equal(watch.settings.messages, 1);
    assert.equal(watch.settings.env_field_hits, 0,
      'legal projected defaults (canPreviewInstall:false, sources:null) must not count as env data');
    assert.deepEqual(Object.keys(watch.settings.keys).sort(), Object.keys(projected).sort(),
      'settings keys are still recorded (no payload hiding)');
    assert.equal(watch.recognition.revision, 14);
    installSceneWatchers('late-evaluate'); // idempotent, no reset
    const heavy = { ...projected, sources: [{ id: 'src-1' }],
      maintenance: { phase: 'installing' } };
    send({ version: 2, kind: 'event', id: 'e-4', type: 'app.state',
      payload: { sessionId: 'ws-1', revision: 8, scope: 'settings',
        change: 'replace', state: heavy } });
    assert.equal(watch.settings.messages, 2, 'second install must not reset the watcher');
    assert.equal(watch.settings.env_field_hits, 2,
      'non-empty sources/maintenance data must count as env payload hits');
    assert.deepEqual(watch.settings.env_non_empty_sample.map((item) => item.key).sort(),
      ['maintenance', 'sources']);
    const boundedKeys = Object.fromEntries(Array.from({ length: 40 }, (_, index) => [`field${index}`, null]));
    send({ version: 2, kind: 'event', id: 'keys-full', type: 'app.state',
      payload: { scope: 'settings', state: boundedKeys } });
    const themeCount = watch.settings.keys.theme;
    send({ version: 2, kind: 'event', id: 'keys-existing', type: 'app.state',
      payload: { scope: 'settings', state: { theme: 'light' } } });
    assert.equal(Object.keys(watch.settings.keys).length, 32);
    assert.equal(watch.settings.keys.theme, themeCount + 1,
      'existing key counts must still advance when the key budget is full');
    assert.equal(watch.recognition.sessionId, 'sess-live',
      'settings traffic must not disturb the recognition identity');
  } finally {
    globalThis.window = savedSceneWindow;
  }
  console.log(JSON.stringify({ self_check: 'passed', cases: {
    after_clean: clean.length === 0,
    after_rejects_pdf_qrcode_in_scene: heavyScene.length === 2,
    after_rejects_settings_env_fields: envSettings.length === 1,
    receipt_required_in_all_modes: noReceipt.length === 3,
    baseline_measures_without_zero_requirement: baselineHeavy.length === 0,
    scene_settings_empty_defaults_allowed: true,
    scene_settings_non_empty_env_data_rejected: true,
    scene_recognition_identity_event_and_bootstrap: true,
    scene_ignores_imageEdit_scope: true,
  } }));
}

async function main() {
  assert.equal(process.platform, 'win32', 'Windows desktop required');
  if (process.argv.includes('--self-check')) {
    await selfCheck();
    return;
  }
  const candidate = fs.realpathSync(option('--candidate'));
  const work = fs.realpathSync(option('--work'));
  const mode = option('--mode', 'after');
  assert(['baseline', 'after'].includes(mode), 'Mode must be baseline/after');
  assert(!contained(candidate, work) && !contained(work, candidate),
    'Candidate and Work roots must not nest.');
  assert(!fs.existsSync(path.join(candidate, 'state')), 'Source candidate must have no state');
  const record = await run(candidate, work, mode);
  const sceneCounts = Object.fromEntries(Object.entries(record.scene_trace?.messages?.by_key ?? {})
    .map(([key, value]) => [key, value.count]));
  console.log(JSON.stringify({ state: record.state, mode: record.mode, root: record.root,
    error: record.error ?? record.violations?.[0] ?? null,
    scene_scope_counts: sceneCounts,
    scene_settings_env_field_hits: record.scene_trace?.settings_env_field_hits ?? null,
    main_receipt_counts: Object.fromEntries(Object.entries(record.main_receipt?.by_key ?? {})
      .filter(([key]) => key.startsWith('scope:'))
      .map(([key, value]) => [key, value.count])),
    violations: record.violations }));
  process.exitCode = record.state === 'passed' ? 0 : 1;
}

if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error); process.exitCode = 1; });
}
