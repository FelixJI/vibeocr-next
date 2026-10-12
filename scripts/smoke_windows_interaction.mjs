// Task #217 bounded Windows keyboard/IME/UIA interaction smoke. Run only on
// an explicitly authorized interactive Windows desktop by the main agent on a
// final real candidate. This script deliberately reuses the owned launch /
// CDP / fixture / cleanup seams exported by scripts/smoke_native_actions.mjs
// (and the existing clipboard fixture) instead of a new runner framework.
// Real-input rule: every keystroke for AC1-3 evidence is delivered through
// real SendInput (native fixtures with RequireForeground + focus-ownership
// guards) into the owned app's actual WebView focus; JS/CDP key dispatch
// (page.keyboard) is never used for evidence. Composition events are only
// observed passively. Steps that cannot obtain their precondition report
// EVIDENCE_MISSING with the observed reason - never a simulated success.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { native, windows, waitForWindows, delay, launchApp,
  stopOwned } from './smoke_native_actions.mjs';

const exec = promisify(execFile);
const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repo = path.dirname(scriptDir);
const interactionScript = path.join(scriptDir, 'native_interaction_fixture.ps1');
const clipboardScript = path.join(scriptDir, 'native_codes_clipboard_fixture.ps1');
const demandFixturesScript = path.join(scriptDir, 'runtime_demand_fixtures.py');
const PASS = 'pass';
const FAIL = 'fail';
const EVIDENCE_MISSING = 'evidence_missing';
const VK = { Z: 0x5A, Y: 0x59, S: 0x53, W: 0x57, A: 0x41, C: 0x43 };
const cjkPattern = /[\u3400-\u9fff\uf900-\ufaff]/;

function option(name) {
  const index = process.argv.indexOf(name);
  assert(index > 0 && index + 1 < process.argv.length, `Required option ${name} is missing.`);
  return process.argv[index + 1];
}

function contained(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

// Calls the #217 interaction fixture. Guard violations throw (fail-closed,
// exit non-zero from pwsh) so they can never be classified as evidence gaps.
// Boolean options bind as PowerShell switch parameters (presence only):
// `pwsh -File` cannot convert a "true" string to [bool].
async function interaction(action, options = {}) {
  const args = ['-NoProfile', '-NonInteractive', '-File', interactionScript, '-Action', action];
  for (const [key, value] of Object.entries(options)) {
    if (value === undefined || value === null || value === false) continue;
    if (value === true) args.push(`-${key}`);
    else args.push(`-${key}`, String(value));
  }
  const { stdout } = await exec('pwsh', args, { timeout: 20000, windowsHide: true });
  const text = stdout.trim();
  return text && text.startsWith('{') ? JSON.parse(text) : text;
}

// Pure classifier: decides pass / evidence_missing / fail from the passive
// in-page IME observations after real SendInput keystrokes.
function classifyImeObservation({ compositionStarted, compositionEnded, value, typed }) {
  assert(typeof typed === 'string' && typed.length > 0, 'typed pinyin is required.');
  if (compositionStarted && compositionEnded) return { state: PASS, reason: null };
  if (compositionStarted && !compositionEnded)
    return { state: FAIL, reason: `composition did not end after commit/cancel (value=${JSON.stringify(value)}).` };
  if ((value ?? '').includes(typed))
    return { state: EVIDENCE_MISSING,
      reason: `Active IME passed pinyin through as ASCII without composition (value=${JSON.stringify(value)}); the active TIP is likely in English mode. Composition evidence requires the Chinese mode.` };
  return { state: FAIL,
    reason: `Real keystrokes were neither composed nor reflected in the input (value=${JSON.stringify(value)}).` };
}

// Pure classifier for the COMMIT phase: unlike classifyImeObservation
// (which judges observation phases and intentionally passes started+ended),
// a commit must produce CJK text - an ASCII passthrough "composition" can
// never be reported as PASS here.
function classifyImeCommit({ started, ended, value, typed }) {
  assert(typeof typed === 'string' && typed.length > 0, 'typed pinyin is required.');
  if (!started)
    return (value ?? '').includes(typed)
      ? { state: EVIDENCE_MISSING,
          reason: `Active IME passed the commit pinyin through as ASCII without a new composition (value=${JSON.stringify(value)}); likely English mode after the earlier cancel.` }
      : { state: FAIL,
          reason: `Commit keystrokes were neither composed nor reflected in the input (value=${JSON.stringify(value)}).` };
  if (!ended)
    return { state: FAIL, reason: 'Space did not end the live commit composition (no new compositionend).' };
  if (!cjkPattern.test(value ?? ''))
    return { state: FAIL,
      reason: `Commit composition ended without CJK text (value=${JSON.stringify(value)}).` };
  return { state: PASS, reason: null };
}

// Pure aggregator for the step records: a single fail fails the whole smoke
// (exit 1); evidence gaps alone are NOT a pass - they surface as
// evidence_missing (main exits 2) so an AC gap can never hide behind a
// green overall result (AC5 keeps EVIDENCE_MISSING explicit, never faked).
function summarizeSteps(steps) {
  const states = Object.fromEntries(Object.entries(steps)
    .map(([name, step]) => [name, step?.state ?? FAIL]));
  const failures = Object.entries(states).filter(([, state]) => state === FAIL);
  const gaps = Object.entries(states).filter(([, state]) => state === EVIDENCE_MISSING);
  return {
    states,
    overall: failures.length ? 'failed' : (gaps.length ? 'evidence_missing' : 'passed'),
    failureReasons: failures.map(([name]) => `${name}: ${steps[name].reason}`),
    gapReasons: gaps.map(([name]) => `${name}: ${steps[name].reason}`),
  };
}

// Deterministic synthetic inputs (vibeocr-123.png / vibeocr-123.pdf) come
// from the existing scripts/runtime_demand_fixtures.py seam - this smoke
// ships no encoder of its own. Failure to generate is an explicit evidence
// gap for the steps that need the fixtures, never a fabricated input.
async function ensureSyntheticFixtures(root) {
  const inputs = path.join(root, 'inputs');
  try {
    await exec('uv', ['run', '--frozen', 'python', demandFixturesScript, '--out', inputs],
      { cwd: repo, timeout: 30000, windowsHide: true });
    return { ok: fs.existsSync(path.join(inputs, 'vibeocr-123.png')) };
  } catch (error) {
    return { ok: false, error: error.message };
  }
}

// Real Windows focus point for an element inside the owned WebView, using the
// same measured webview-bounds scale math as the reused focusRecorder seam.
async function webviewPoint(app, element) {
  const bounds = JSON.parse(await native('webview-bounds', {
    AppPid: app.child.pid, Handle: app.main.Handle,
  }));
  const box = await element.boundingBox();
  assert(box, 'Target element has no visible bounds inside the owned WebView.');
  const view = await app.page.evaluate(() => ({ width: innerWidth, height: innerHeight }));
  const sx = (bounds.Right - bounds.Left) / view.width;
  const sy = (bounds.Bottom - bounds.Top) / view.height;
  assert(sx > 0 && sy > 0 && Math.abs(sx - sy) < 0.03, 'Owned WebView must have a uniform measured scale.');
  return {
    X: Math.round(bounds.Left + (box.x + Math.min(8, box.width / 2)) * sx),
    Y: Math.round(bounds.Top + (box.y + Math.min(8, box.height / 2)) * sy),
  };
}

async function focusElement(app, element) {
  await native('restore', { AppPid: app.child.pid, Handle: app.main.Handle });
  await element.scrollIntoViewIfNeeded();
  const clickPoint = await webviewPoint(app, element);
  try {
    await native('focus-fixture', {
      FixturePid: app.child.pid, Handle: app.main.Handle, ...clickPoint,
    });
  } catch (error) {
    const probe = await native('probe', {
      AppPid: app.child.pid, FixturePid: app.child.pid, ...clickPoint,
    });
    throw new Error(`${error.message}; post-failure geometry=${probe}`);
  }
  assert(await element.evaluate((node) => document.activeElement === node),
    'Guarded native click did not focus the requested input.');
  // IME candidate UI may cover the caret. Key input still checks actual focus
  // ownership; use a separate App-owned header point for the occlusion guard.
  const bounds = JSON.parse(await native('webview-bounds', {
    AppPid: app.child.pid, Handle: app.main.Handle,
  }));
  const point = { X: bounds.Left + 8, Y: bounds.Top + 8 };
  await interaction('focus-state', { AppPid: app.child.pid, ...point });
  return point;
}

// Passive-only in-page observer for IME composition + trusted-key evidence.
async function installImeObserver(page, selector) {
  await page.evaluate((target) => {
    const element = document.querySelector(target);
    if (!element) throw new Error(`IME observer target is missing: ${target}`);
    window.__intEvents = [];
    const record = (type, event) => window.__intEvents.push({
      type, key: event.key ?? null, code: event.code ?? null,
      trusted: event.isTrusted, composing: event.isComposing ?? null,
      keyCode: event.keyCode ?? null, data: event.data ?? null, at: Date.now(),
    });
    for (const type of ['compositionstart', 'compositionupdate', 'compositionend'])
      element.addEventListener(type, (event) => record(type, event));
    element.addEventListener('keydown', (event) => record('keydown', event), true);
    element.addEventListener('keyup', (event) => record('keyup', event), true);
    window.__intTarget = target;
  }, selector);
}

// Per-phase event waits: every wait starts from a recorded index into the
// passive observer's array so stale events from an earlier phase can never
// satisfy a later phase's waitForFunction.
async function eventCount(page) {
  return page.evaluate(() => (window.__intEvents ?? []).length);
}

async function waitNewEvent(page, from, type, timeoutMs) {
  await page.waitForFunction(([start, kind]) => (window.__intEvents ?? [])
    .slice(start).some((event) => event.type === kind), [from, type],
  { timeout: timeoutMs });
}

async function sliceFrom(page, from) {
  return page.evaluate((start) => (window.__intEvents ?? []).slice(start), from);
}

// Direct pixel comparisons for canvas change evidence (no hashing: AGENTS
// forbids digests for ordinary correctness). Snapshots are kept in-page and
// compared byte-for-byte per pixel.
async function canvasSnapshot(page) {
  return page.getByLabel('图片检查画布').evaluate(() => {
    const canvas = document.querySelector('canvas[aria-label="图片检查画布"]');
    const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
    (window.__intCanvasSnapshots ??= []).push(Array.from(data));
    return { index: window.__intCanvasSnapshots.length - 1,
      width: canvas.width, height: canvas.height };
  });
}

async function canvasPixelDiff(page, first, second) {
  return page.evaluate(([a, b]) => {
    const left = window.__intCanvasSnapshots[a];
    const right = window.__intCanvasSnapshots[b];
    if (!left || !right || left.length !== right.length)
      return { comparable: false, changedPixels: -1 };
    let changed = 0;
    for (let index = 0; index < left.length; index += 4)
      if (left[index] !== right[index] || left[index + 1] !== right[index + 1] ||
        left[index + 2] !== right[index + 2]) changed++;
    return { comparable: true, changedPixels: changed };
  }, [first.index, second.index]);
}

// Shared real-IME sequence for any focused product text input:
// activate (or reuse) the Chinese input locale on the app thread, type pinyin
// through real SendInput, observe composition, cancel with real Esc, commit
// with a real Space, then undo with a real Ctrl+Z. Returns a classified step.
function applyImeRestore(record, restore) {
  record.restore = restore;
  if (!restore.restored) {
    record.state = FAIL;
    record.reason = [record.reason, 'Original input thread locale restoration was not confirmed.'].filter(Boolean).join('; ');
  }
  return record;
}

async function realImeSequence(app, { element, selector, point, urlGuard = true, undoCheck = true }) {
  const page = app.page;
  const record = { state: FAIL, reason: null, phases: {} };
  const urlBefore = page.url();
  const mainVisible = async () => (await windows(app.child.pid))
    .some((item) => item.Handle === app.main.Handle && item.Visible);
  try {
    record.valueBefore = await element.inputValue();
    await installImeObserver(page, selector);
    const activeBefore = await page.evaluate(() => document.activeElement?.matches?.(window.__intTarget));
    assert(activeBefore, 'Real native focus did not land on the IME target input.');
    const focus = await interaction('focus-state', { AppPid: app.child.pid, X: point.X, Y: point.Y });
    record.focus = focus;
    record.hklBefore = focus.activeHkl;
    const activation = await interaction('ime-activate', {
      AppPid: app.child.pid, X: point.X, Y: point.Y, LangId: 2052,
    });
    record.activation = activation;
    if (activation.status === EVIDENCE_MISSING) {
      Object.assign(record, { state: EVIDENCE_MISSING, reason: activation.reason });
      return record;
    }
    // 1) composition through real keystrokes (fresh observer array)
    const composeMark = 0;
    await interaction('type-keys', { AppPid: app.child.pid, X: point.X, Y: point.Y, Text: 'nihao' });
    let started = false;
    try {
      await waitNewEvent(page, composeMark, 'compositionstart', 4000);
      started = true;
    } catch { /* classified below from real observations */ }
    const valueAfterType = await element.inputValue();
    record.afterType = { started, value: valueAfterType };
    record.phases.compose = await sliceFrom(page, composeMark);
    const keyEvents = record.phases.compose.filter((event) => event.type === 'keydown');
    record.untrustedKeyEvents = keyEvents.filter((event) => event.trusted === false);
    if (record.untrustedKeyEvents.length)
      return Object.assign(record, { state: FAIL, reason: 'Non-trusted keydown events observed; real-input contract violated.' });
    if (!started) {
      const classified = classifyImeObservation({
        compositionStarted: false, compositionEnded: false,
        value: valueAfterType, typed: 'nihao',
      });
      return Object.assign(record, { state: classified.state, reason: classified.reason });
    }
    // 2) real Esc (this round's guarded app-hotkey: focus-root + point-root
    // ownership) must end the composition without closing/deleting anything.
    // The wait starts from the current index so only a NEW compositionend
    // counts; stale events from the compose phase can never satisfy it.
    const escapeMark = await eventCount(page);
    await interaction('app-hotkey', { AppPid: app.child.pid, X: point.X, Y: point.Y, Key: 0x1B });
    try {
      await waitNewEvent(page, escapeMark, 'compositionend', 4000);
    } catch {
      record.phases.escape = await sliceFrom(page, escapeMark);
      return Object.assign(record, { state: FAIL, reason: 'Esc did not end the live IME composition.' });
    }
    const valueAfterEsc = await element.inputValue();
    record.phases.escape = await sliceFrom(page, escapeMark);
    record.afterEsc = { value: valueAfterEsc,
      url: page.url(), mainVisible: await mainVisible() };
    if (valueAfterEsc !== record.valueBefore)
      return Object.assign(record, { state: FAIL,
        reason: `Esc changed the pre-composition text: ${JSON.stringify(record.valueBefore)} -> ${JSON.stringify(valueAfterEsc)}.` });
    if (urlGuard) {
      assert.equal(record.afterEsc.url, urlBefore, 'Esc navigated away from the surface.');
      assert(record.afterEsc.mainVisible, 'Esc closed or hid the owned main window.');
    }
    // 3) commit path: pinyin -> candidates -> real Space commits CJK. The
    // waits again start from the post-Esc index; only NEW composition
    // events count, and the verdict uses classifyImeCommit so an ASCII
    // passthrough can never be reported as PASS.
    const commitMark = await eventCount(page);
    await element.fill('');
    await interaction('type-keys', { AppPid: app.child.pid, X: point.X, Y: point.Y, Text: 'zhongwen' });
    let commitStarted = false;
    try {
      await waitNewEvent(page, commitMark, 'compositionstart', 4000);
      commitStarted = true;
    } catch { /* classified below from real observations */ }
    const valueBeforeCommit = commitStarted ? null : await element.inputValue();
    if (!commitStarted) {
      const classified = classifyImeCommit({
        started: false, ended: false, value: valueBeforeCommit, typed: 'zhongwen',
      });
      record.phases.commit = await sliceFrom(page, commitMark);
      return Object.assign(record, { state: classified.state, reason: classified.reason });
    }
    // Space commit: the keyboard focus must still be the target, and the
    // compositionend must be NEW after a separately recorded index taken
    // immediately before Space - an end from during the pinyin phase can
    // never satisfy the commit evidence.
    const stillFocusedBeforeSpace = await page.evaluate(() =>
      document.activeElement?.matches?.(window.__intTarget));
    assert(stillFocusedBeforeSpace, 'Real keyboard focus left the IME target before Space.');
    const spaceMark = await eventCount(page);
    await interaction('type-keys', { AppPid: app.child.pid, X: point.X, Y: point.Y, Text: ' ' });
    let ended = false;
    try {
      await waitNewEvent(page, spaceMark, 'compositionend', 4000);
      ended = true;
    } catch { /* classified below */ }
    const valueCommitted = await element.inputValue();
    record.phases.commit = await sliceFrom(page, commitMark);
    record.commit = { ended, value: valueCommitted };
    const commitVerdict = classifyImeCommit({
      started: true, ended, value: valueCommitted, typed: 'zhongwen',
    });
    if (commitVerdict.state !== PASS)
      return Object.assign(record, { state: commitVerdict.state, reason: commitVerdict.reason });
    // 4) real Ctrl+Z must be consumed by the text input first (undo commit)
    if (undoCheck) {
      await interaction('app-hotkey', {
        AppPid: app.child.pid, X: point.X, Y: point.Y, Key: VK.Z, Ctrl: true,
      });
      await delay(400);
      const valueAfterUndo = await element.inputValue();
      record.undo = { value: valueAfterUndo };
      if (cjkPattern.test(valueAfterUndo))
        return Object.assign(record, { state: FAIL,
          reason: `Ctrl+Z did not undo the committed CJK text in the focused input (value=${valueAfterUndo}).` });
    }
    record.state = PASS;
    return record;
  } catch (error) {
    record.reason = `${error.name}: ${error.message}`;
    return record;
  } finally {
    // Restore the captured thread even when activation was not confirmed.
    if (record.focus) {
      try {
        record.restore = await interaction('ime-restore', {
          AppPid: app.child.pid, X: point.X, Y: point.Y, Hkl: record.hklBefore,
          FocusHandle: record.focus.focusHandle, ThreadId: record.focus.focusThreadId,
        });
      } catch (error) {
        record.restore = { restored: false, error: error.message };
      }
      applyImeRestore(record, record.restore);
    } else {
      record.restore = { restored: false, attempted: false,
        reason: record.activation?.status ?? 'activation not reached' };
    }
  }
}

async function preflightStep(app) {
  const step = { state: FAIL, reason: null };
  try {
    step.system = await interaction('system-state');
    // Observation only: the page-side media queries are NOT a substitute for
    // real Windows high-contrast dynamic verification (left to the main
    // agent; a global theme/HC switch is out of this smoke's scope).
    step.pageColors = await app.page.evaluate(() => ({
      forcedColors: matchMedia('(forced-colors: active)').matches,
      prefersDark: matchMedia('(prefers-color-scheme: dark)').matches,
      prefersContrastMore: matchMedia('(prefers-contrast: more)').matches,
    }));
    step.note = 'Read-only system + page observation; no Windows theme/HC state was changed by this smoke.';
    step.state = PASS;
  } catch (error) {
    step.reason = `${error.name}: ${error.message}`;
  }
  return step;
}

// AC3 slice: real focus + Tab travel + UIA readable names on key controls.
async function focusAndNamesStep(app) {
  const page = app.page;
  const step = { state: FAIL, reason: null };
  try {
    await page.getByRole('link', { name: '二维码与条码', exact: true }).click();
    await page.getByRole('heading', { name: '二维码与条码', exact: true }).waitFor();
    await page.getByRole('tab', { name: '生成', exact: true }).click();
    const input = page.getByLabel('输入内容', { exact: true });
    const point = await focusElement(app, input);
    await interaction('focus-state', { AppPid: app.child.pid, X: point.X, Y: point.Y });
    const focused = await interaction('uia-focused', {
      AppPid: app.child.pid, X: point.X, Y: point.Y,
    });
    step.uiaFocusedOnInput = focused;
    assert(focused.name?.includes('输入内容'),
      `UIA name of the focused input was ${JSON.stringify(focused.name)}.`);
    // Real Tab through this round's guarded SendInput (focus-root +
    // point-root ownership), replacing the older foreground-only action.
    await interaction('app-hotkey', { AppPid: app.child.pid, X: point.X, Y: point.Y, Key: 0x09 });
    await delay(300);
    step.activeAfterTab = await page.evaluate(() => ({
      id: document.activeElement?.id ?? null,
      label: document.activeElement?.getAttribute?.('aria-label') ?? null,
      same: document.activeElement?.matches?.('#qr-content') ?? false,
    }));
    assert(step.activeAfterTab.same === false, 'Real Tab did not move focus off the input.');
    const focusedAfterTab = await interaction('uia-focused', {
      AppPid: app.child.pid, X: point.X, Y: point.Y,
    });
    step.uiaFocusedAfterTab = focusedAfterTab;
    assert(typeof focusedAfterTab.name === 'string' && focusedAfterTab.name.length > 0,
      'UIA name of the tabbed-to control is empty.');
    step.state = PASS;
  } catch (error) {
    step.reason = `${error.name}: ${error.message}`;
  }
  return step;
}

// AC2 slice: real IME on the QR text input (no Runtime dependency).
async function qrImeStep(app) {
  const page = app.page;
  const step = await (async () => {
    await page.getByRole('link', { name: '二维码与条码', exact: true }).click();
    await page.getByRole('heading', { name: '二维码与条码', exact: true }).waitFor();
    await page.getByRole('tab', { name: '生成', exact: true }).click();
    const input = page.getByLabel('输入内容', { exact: true });
    const point = await focusElement(app, input);
    return realImeSequence(app, { element: input, selector: '#qr-content', point });
  })();
  return step.state === FAIL && /outside this smoke run|focus/i.test(step.reason ?? '')
    ? { ...step, reason: `Guard failure on the QR surface: ${step.reason}` }
    : step;
}

// AC1/AC2 slice: synthetic image -> real paste -> text annotation input with
// real IME, Esc layering, Ctrl+Z/Y observable on canvas, command guards.
async function imageTextStep(app, pngPath) {
  const page = app.page;
  const step = { state: FAIL, reason: null };
  let imeRecord = null;
  try {
    if (!pngPath || !fs.existsSync(pngPath)) {
      step.state = EVIDENCE_MISSING;
      step.reason = 'Synthetic image fixture unavailable; the image text-annotation path needs runtime_demand_fixtures.py output.';
      return step;
    }
    await exec('pwsh', ['-NoProfile', '-NonInteractive', '-STA', '-File', clipboardScript,
      '-SmokeRoot', path.dirname(pngPath), '-ImagePath', pngPath], { timeout: 20000, windowsHide: true });
    await page.getByRole('link', { name: '图片编辑', exact: true }).click();
    await page.getByRole('heading', { name: '图片编辑', exact: true }).waitFor();
    await page.getByRole('button', { name: '粘贴图片', exact: true }).click();
    await page.getByLabel('图片检查画布').waitFor({ timeout: 15000 });
    // The runtime_demand_fixtures synthetic image is a white page with black
    // text glyphs; wait for exactly that content to actually decode in the
    // canvas (sampled dark and light pixels), not any fixed accent color.
    await page.waitForFunction(() => {
      const canvas = document.querySelector('canvas[aria-label="图片检查画布"]');
      if (!canvas) return false;
      const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
      let dark = 0, light = 0;
      for (let index = 0; index < data.length; index += 400) {
        if (data[index] < 100 && data[index + 1] < 100 && data[index + 2] < 100) dark++;
        if (data[index] > 180 && data[index + 1] > 180 && data[index + 2] > 180) light++;
      }
      return dark > 5 && light > 5;
    }, null, { timeout: 15000 });
    await page.getByRole('button', { name: '文字', exact: true }).click();
    const input = page.getByLabel('标注文字');
    await input.waitFor({ timeout: 5000 });
    const point = await focusElement(app, input);
    const baseSnapshot = await canvasSnapshot(page);
    imeRecord = await realImeSequence(app, {
      element: input, selector: '[aria-label="标注文字"]', point, undoCheck: false,
    });
    step.ime = imeRecord;
    if (imeRecord.state === EVIDENCE_MISSING) {
      step.state = EVIDENCE_MISSING;
      step.reason = imeRecord.reason;
      return step;
    }
    if (imeRecord.state === FAIL) {
      step.state = FAIL;
      step.reason = imeRecord.reason;
      return step;
    }
    // Esc layering on this surface: composition cancelled, nothing deleted
    // (direct per-pixel comparison against the base snapshot; no hashing).
    const escSnapshot = await canvasSnapshot(page);
    step.escCanvasDiff = await canvasPixelDiff(page, baseSnapshot, escSnapshot);
    assert(step.escCanvasDiff.comparable && step.escCanvasDiff.changedPixels === 0,
      `Esc during text annotation changed ${step.escCanvasDiff.changedPixels} canvas pixels.`);
    // Drag the committed text annotation with the real pointer (CDP mouse,
    // same precedent as the shipped native smokes) and observe undo/redo via
    // real guarded Ctrl+Z / Ctrl+Y SendInput.
    const box = await page.getByLabel('图片检查画布').boundingBox();
    assert(box, 'Editor canvas has no visual bounds.');
    // Text marks, unlike numbering, require a drag of at least 8 image pixels.
    await page.mouse.move(box.x + box.width * 0.3, box.y + box.height * 0.4);
    await page.mouse.down();
    try { await page.mouse.move(box.x + box.width * 0.7, box.y + box.height * 0.6, { steps: 8 }); }
    finally { await page.mouse.up(); }
    await page.waitForFunction(() => {
      const undo = document.querySelector('button[aria-label="撤销"]');
      return undo && !undo.disabled;
    }, null, { timeout: 5000 });
    const placedSnapshot = await canvasSnapshot(page);
    step.placedDiff = await canvasPixelDiff(page, baseSnapshot, placedSnapshot);
    step.annotationPlaced = step.placedDiff.comparable && step.placedDiff.changedPixels > 0;
    assert(step.annotationPlaced,
      `Committed text annotation never appeared on the canvas (changedPixels=${step.placedDiff.changedPixels}).`);
    await interaction('app-hotkey', {
      AppPid: app.child.pid, X: point.X, Y: point.Y, Key: VK.Z, Ctrl: true,
    });
    await delay(400);
    const undoSnapshot = await canvasSnapshot(page);
    step.undoDiff = await canvasPixelDiff(page, placedSnapshot, undoSnapshot);
    assert(step.undoDiff.comparable && step.undoDiff.changedPixels > 0,
      `Real Ctrl+Z did not change the annotated canvas (changedPixels=${step.undoDiff.changedPixels}).`);
    await interaction('app-hotkey', {
      AppPid: app.child.pid, X: point.X, Y: point.Y, Key: VK.Y, Ctrl: true,
    });
    await delay(400);
    const redoSnapshot = await canvasSnapshot(page);
    step.redoDiff = await canvasPixelDiff(page, placedSnapshot, redoSnapshot);
    assert(step.redoDiff.comparable && step.redoDiff.changedPixels === 0,
      `Real Ctrl+Y did not restore the annotation (changedPixels=${step.redoDiff.changedPixels}).`);
    // Only Ctrl+Shift+S is unsupported here; Ctrl+S/W now have product commands.
    // Their picker/close behavior requires separate real-candidate evidence.
    const urlBefore = page.url();
    const windowsBefore = new Set((await windows(app.child.pid)).map((item) => item.Handle));
    step.commandGuards = [];
    for (const combo of [
      { label: 'Ctrl+Shift+S', Key: VK.S, Ctrl: true, Shift: true },
    ]) {
      const { label, ...keys } = combo;
      await interaction('app-hotkey', { AppPid: app.child.pid, X: point.X, Y: point.Y, ...keys });
      await delay(300);
      const current = await windows(app.child.pid);
      const newWindows = current
        .filter((item) => !windowsBefore.has(item.Handle) && item.Visible)
        .map((item) => item.ClassName);
      const mainVisible = current.some((item) => item.Handle === app.main.Handle && item.Visible);
      assert(newWindows.length === 0,
        `Unsupported command ${label} opened new window(s): ${JSON.stringify(newWindows)}.`);
      assert(mainVisible, `Unsupported command ${label} closed or hid the owned main window.`);
      assert(page.url() === urlBefore, `Unsupported command ${label} navigated away.`);
      step.commandGuards.push({ label, newWindows: 0, mainVisible: true, urlUnchanged: true });
    }
    step.state = PASS;
  } catch (error) {
    step.reason = `${error.name}: ${error.message}`;
  }
  return step;
}

// AC1/AC2 slice: PDF correction text via the real public PDF path. The
// 校正文字 textarea only exists for OCR-source blocks; without a real Runtime
// OCR session every precondition gap is EVIDENCE_MISSING (never fabricated).
async function pdfCorrectionStep(app, root) {
  const page = app.page;
  const step = { state: FAIL, reason: null };
  try {
    const pdfPath = path.join(root, 'inputs', 'vibeocr-123.pdf');
    if (!fs.existsSync(pdfPath)) {
      step.state = EVIDENCE_MISSING;
      step.reason = 'Synthetic PDF fixture unavailable; no PDF path without runtime_demand_fixtures.py output.';
      return step;
    }
    await page.evaluate(() => {
      window.__intPdf = null;
      window.chrome.webview.addEventListener('message', ({ data }) => {
        if (data?.kind !== 'event' || data.type !== 'app.state' || data.payload?.scope !== 'pdf') return;
        const state = data.payload.state;
        window.__intPdf = {
          sessionId: state?.sessionId ?? null,
          statusCode: state?.status ?? null,
          isBusy: state?.isBusy ?? null,
          pageCount: state?.pageCount ?? null,
        };
      });
    });
    await page.getByRole('link', { name: 'PDF', exact: true }).click();
    await page.getByRole('button', { name: '打开 PDF', exact: true }).click();
    await native('codes-open-file', { Handle: app.main.Handle, AppPid: app.child.pid,
      FilePath: pdfPath });
    let pdf = null;
    try {
      await page.waitForFunction(() => {
        const state = window.__intPdf;
        return !!state && state.isBusy === false && (state.sessionId ||
          ['pdf.failed', 'pdf.cancelled'].includes(state.statusCode));
      }, null, { timeout: 20000 });
      pdf = await page.evaluate(() => window.__intPdf);
    } catch {
      pdf = await page.evaluate(() => window.__intPdf);
      step.state = EVIDENCE_MISSING;
      step.reason = `Runtime PDF session unavailable under this smoke's --shell-only launch (observed=${JSON.stringify(pdf)}); RuntimeDemandCoordinator rejects service demands in shell-only mode, so PDF AC evidence needs the main agent's normal Portable + real OCR isolated candidate flow, not a product failure here.`;
      return step;
    }
    step.pdf = pdf;
    if (!pdf.sessionId) {
      step.state = EVIDENCE_MISSING;
      step.reason = `PDF open ended in ${pdf.statusCode} under the --shell-only launch (service demands are rejected by design); PDF AC evidence needs the main agent's normal Portable + real OCR isolated candidate flow, not a product failure here.`;
      return step;
    }
    // OCR-source blocks are labeled with an "OCR" prefix; the correction
    // textarea only appears for them (PdfInspection.tsx selected.source).
    // The block layer needs 显示文字框 enabled; toggling it on is an
    // in-session product checkbox (same seam as the shipped smokes).
    const showBoxesCheckbox = page.getByRole('checkbox', { name: '显示文字框' });
    if (!(await showBoxesCheckbox.isChecked())) await showBoxesCheckbox.click();
    const blockButtons = page.locator('button[aria-label^="OCR"]');
    const blockCount = await blockButtons.count();
    if (blockCount === 0) {
      step.state = EVIDENCE_MISSING;
      step.reason = 'No OCR-source block exists in this PDF (correction textarea requires selected.source === "ocr"); a real Runtime OCR fixture is needed.';
      return step;
    }
    await blockButtons.first().click();
    const textarea = page.getByLabel('校正文字');
    try {
      await textarea.waitFor({ timeout: 5000 });
    } catch {
      step.state = EVIDENCE_MISSING;
      step.reason = 'OCR block did not expose the editable 校正文字 textarea (canEdit/textTruncated precondition missing).';
      return step;
    }
    const point = await focusElement(app, textarea);
    const ime = await realImeSequence(app, {
      element: textarea, selector: '[aria-label="校正文字"]', point, undoCheck: false,
    });
    step.ime = ime;
    step.state = ime.state;
    step.reason = ime.reason ?? null;
  } catch (error) {
    step.reason = `${error.name}: ${error.message}`;
  }
  return step;
}

async function main() {
  assert.equal(process.platform, 'win32', 'Windows interaction smoke requires Windows.');
  const source = fs.realpathSync(option('--product-root'));
  const work = fs.realpathSync(option('--work-root'));
  assert(!contained(source, work) && !contained(work, source),
    'ProductRoot and WorkRoot must not nest.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
    'runtime/backend/runtime-manifest.json', 'runtime/installer/vibeocr-runtime-installer.exe'])
    assert(fs.statSync(path.join(source, marker)).isFile(), `Candidate marker missing: ${marker}`);
  // Refuse to copy a source that already carries state: real or stale user
  // state must never be duplicated into the isolated candidate (same rule as
  // the shipped native-actions runner).
  assert(!fs.existsSync(path.join(source, 'state')),
    'Source candidate already has state; refusing to copy it.');
  assert(fs.existsSync(interactionScript), 'Interaction fixture script is missing.');
  const root = path.join(work, `vibeocr-windows-interaction-${crypto.randomUUID()}`);
  const candidate = path.join(root, 'candidate');
  const webviewData = path.join(root, 'webview2');
  const instanceId = crypto.randomUUID().replaceAll('-', '');
  fs.mkdirSync(root);
  fs.cpSync(source, candidate, { recursive: true, errorOnExist: true, force: false });
  const record = { schema_version: 1, smoke: 'windows-interaction', task: 217,
    state: 'failed', root, appPids: [], steps: {} };
  let app = null;
  const fixtures = await ensureSyntheticFixtures(root);
  record.fixtures = fixtures;
  const pngPath = fixtures.ok ? path.join(root, 'inputs', 'vibeocr-123.png') : null;
  try {
    app = await launchApp(candidate, webviewData, instanceId);
    record.appPids.push(app.child.pid);
    record.steps.preflight = await preflightStep(app);
    record.steps.focusNames = await focusAndNamesStep(app);
    record.steps.qrIme = await qrImeStep(app);
    await app.page.screenshot({ path: path.join(root, 'qr-ime.png') });
    record.steps.imageText = await imageTextStep(app, pngPath);
    await app.page.screenshot({ path: path.join(root, 'image-ime.png') });
    record.steps.pdfCorrection = await pdfCorrectionStep(app, root);
    const summary = summarizeSteps(record.steps);
    // Per-step finally blocks already restore the thread-local input locale
    // bound to each activation's original focus window/thread; the app exit
    // below terminates the thread regardless, so no extra restore is needed
    // here (and none is claimed).
    record.imeRestorePolicy = 'restored per-step in finally, bound to the original focus window/thread';
    record.summary = summary;
    record.state = summary.overall;
    // Exit semantics for the main agent: 0 = passed, 1 = failure,
    // 2 = evidence missing (an AC gap is never a green overall pass).
    if (summary.overall === 'failed') {
      process.exitCode = 1;
      console.error(`Windows interaction smoke failed; evidence root: ${root}`);
      for (const failure of summary.failureReasons) console.error(`FAIL: ${failure}`);
    } else if (summary.overall === 'evidence_missing') {
      process.exitCode = 2;
      console.error(`Windows interaction smoke incomplete (EVIDENCE_MISSING, exit 2); evidence root: ${root}`);
      for (const gap of summary.gapReasons) console.error(`EVIDENCE_MISSING: ${gap}`);
    } else {
      console.log(`Windows interaction smoke passed; evidence root: ${root}`);
    }
  } catch (error) {
    record.error = `${error.name}: ${error.message}`;
    console.error(`Windows interaction smoke failed to run: ${error.message}`);
    process.exitCode = 1;
  } finally {
    const cleanupErrors = [];
    if (app) {
      try {
        await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: app.main.Handle });
      } catch (error) { cleanupErrors.push(error.message); }
      try { await app.browser.close(); } catch { /* WebView2 may already be gone */ }
    }
    if (cleanupErrors.length) {
      record.cleanupError = cleanupErrors.join('; ');
      record.state = 'failed';
      process.exitCode = 1;
    }
    // The evidence root is always kept; nothing is deleted by this smoke.
    fs.writeFileSync(path.join(root, 'windows-interaction-health.json'),
      JSON.stringify(record, null, 2));
  }
}

export { interaction, realImeSequence, focusElement, applyImeRestore, classifyImeObservation, classifyImeCommit, summarizeSteps,
  ensureSyntheticFixtures, PASS, FAIL, EVIDENCE_MISSING, main };

if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(`Windows interaction smoke crashed: ${error.name}: ${error.message}`);
    process.exitCode = 1;
  });
}
