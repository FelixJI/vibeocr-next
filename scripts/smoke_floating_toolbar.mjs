// Authorized interactive Windows smoke; all state and evidence stay in a new TEMP tree.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  native, windows, waitForWindows, area, delay, startFixture, launchApp,
  openSettings, toolbarStatus, setCheckbox, stopOwned, configure,
  captureThroughHotkey, taskbarState, pngPixels,
} from './smoke_native_actions.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const exec = promisify(execFile);
const source = fs.realpathSync(process.argv[process.argv.indexOf('--product-root') + 1]);
assert(fs.existsSync(path.join(source, 'app/metadata/product-layout.json')));
assert(!fs.existsSync(path.join(source, 'state')), 'Candidate must have no user state.');
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'vibeocr-toolbar-'));
const candidate = path.join(work, 'candidate');
const instanceId = crypto.randomUUID().replaceAll('-', '');
fs.cpSync(source, candidate, { recursive: true, force: false, errorOnExist: true });
const startupEnabled = process.argv.includes('--startup-enabled');
if (startupEnabled) {
  fs.mkdirSync(path.join(candidate, 'state/config'), { recursive: true });
  fs.writeFileSync(path.join(candidate, 'state/config/app_settings.json'), JSON.stringify({ floating_toolbar: { enabled: true } }));
}
const identity = JSON.parse(fs.readFileSync(path.join(source, 'app/metadata/component-identities.json'), 'utf8')).project;
const evidence = { schema_version: 1, state: 'failed', sourceSha: identity.source_sha, version: identity.version,
  candidateNote: process.argv.includes('--candidate-note') ? process.argv[process.argv.indexOf('--candidate-note') + 1] : 'Build from source identity above.',
  candidate, appPids: [], screenshots: [], gaps: [
    'System light/dark transition, high contrast and multiple DPI require authorized system changes; not exercised.',
    'Native toolbar commands have no disabled/busy state in production; no disabled visual state was manufactured.',
    'Tray menu physical invocation and actual Alt+Tab switcher inspection are not exercised.',
    'Timer/subscription disposal is covered by repository tests; external GUI can observe window/process cleanup only.',
  ] };
let app, fixture;
const configFile = path.join(candidate, 'state/config/app_settings.json');
const settings = () => JSON.parse(fs.readFileSync(configFile, 'utf8')).floating_toolbar;
const point = (window, dx = 0.5, dy = 0.5) => ({ X: Math.round(window.Bounds.Left + (window.Bounds.Right - window.Bounds.Left) * dx),
  Y: Math.round(window.Bounds.Top + (window.Bounds.Bottom - window.Bounds.Top) * dy) });
async function toolbar() {
  return (await waitForWindows(app.child.pid, (w) => w.Visible && w.Handle !== app.main.Handle &&
    Math.abs((w.Bounds.Right - w.Bounds.Left) / (w.Dpi / 96) - 272) < 3 &&
    Math.abs((w.Bounds.Bottom - w.Bounds.Top) / (w.Dpi / 96) - 44) < 3))[0];
}
async function savePreference(action) {
  const previousId = await app.page.evaluate(() => window.__toolbarSmokeRequests.filter((request) =>
    request.scope === 'settings' && request.action === 'setFloatingToolbarPreferences').at(-1)?.id ?? null);
  const until = Date.now() + 5000;
  await action();
  const result = await app.page.waitForFunction((priorId) => {
    const request = window.__toolbarSmokeRequests.filter((item) => item.scope === 'settings' &&
      item.action === 'setFloatingToolbarPreferences').at(-1);
    if (!request || request.id === priorId) return false;
    const receipt = window.__toolbarSmokeMessages.find((item) => item.id === request.id &&
      item.kind === 'response' && item.type === 'app.command');
    return receipt ? { request, receipt } : false;
  }, previousId, { timeout: Math.max(1, until - Date.now()) });
  const saved = await result.jsonValue();
  await result.dispose();
  (evidence.preferenceSaves ??= []).push(saved);
  return saved;
}
async function setDelay(value) {
  await app.page.getByLabel('收起时间（毫秒）').fill(String(value));
  evidence.delaySave = await savePreference(() =>
    app.page.getByRole('button', { name: '保存时间', exact: true }).click({ timeout: 5000 }));
  assert.equal(evidence.delaySave.receipt.ok, true, 'Floating toolbar delay save failed; see matched receipt problem.');
  assert.equal(settings().linger_ms, value);
}
async function show() {
  await app.page.getByRole('button', { name: '显示', exact: true }).click();
  await toolbarStatus(app.page, '显示中');
  return toolbar();
}
async function frame(window, name) {
  const file = path.join(work, name);
  const r = window.Bounds;
  await exec('pwsh', ['-NoProfile', '-NonInteractive', '-File', path.join(scriptDir, 'scroll_capture_fixture.ps1'),
    '-Action', 'frame', '-FixturePid', String(app.child.pid), '-Handle', String(window.Handle),
    '-X', String(r.Left), '-Y', String(r.Top), '-Width', String(r.Right - r.Left),
    '-Height', String(r.Bottom - r.Top), '-EvidenceRoot', work, '-OutputPath', file],
  { timeout: 15000, windowsHide: true });
  evidence.screenshots.push(file);
  return pngPixels(app.page, fs.readFileSync(file));
}
try {
  app = await launchApp(candidate, path.join(work, 'webview2'), instanceId);
  evidence.appPids.push(app.child.pid);
  evidence.pageErrors = [];
  evidence.navigations = [];
  app.page.on('pageerror', (error) => evidence.pageErrors.push(error.message));
  app.page.on('framenavigated', (frame) => evidence.navigations.push(frame.url()));
  await app.page.evaluate(() => {
    window.__toolbarSmokeMessages = [];
    window.__toolbarSmokeRequests = [];
    const channel = window.chrome.webview;
    const postMessage = channel.postMessage.bind(channel);
    channel.postMessage = (data) => {
      const request = { kind: data?.kind, type: data?.type, id: data?.id,
        scope: data?.payload?.command?.scope, action: data?.payload?.command?.action };
      window.__toolbarSmokeRequests.push(request);
      window.__toolbarSmokeRequests = window.__toolbarSmokeRequests.slice(-32);
      if (request.scope === 'settings' && request.action === 'refreshRuntime' && !window.__toolbarSmokeRefreshRequest)
        window.__toolbarSmokeRefreshRequest = request;
      if (request.scope === 'settings' && request.action === 'setFloatingToolbarEnabled')
        window.__toolbarSmokeEnableRequest = request;
      return postMessage(data);
    };
    window.chrome.webview.addEventListener('message', ({ data }) => {
      const message = { kind: data?.kind, type: data?.type, id: data?.id,
        scope: data?.payload?.scope, ok: data?.payload?.ok,
        problem: data?.payload?.problem ?? data?.payload?.error ?? null };
      window.__toolbarSmokeMessages.push(message);
      window.__toolbarSmokeMessages = window.__toolbarSmokeMessages.slice(-32);
      if (message.kind === 'response' && message.type === 'app.command' &&
        message.id === window.__toolbarSmokeRefreshRequest?.id) window.__toolbarSmokeRefreshReceipt = message;
    });
  });
  await openSettings(app.page);
  await app.page.waitForURL('https://app.vibeocr/index.html#/settings');
  await app.page.waitForFunction(() => window.__toolbarSmokeRefreshReceipt, null, { timeout: 60000 });
  evidence.initialRefresh = await app.page.evaluate(() => ({ request: window.__toolbarSmokeRefreshRequest,
    receipt: window.__toolbarSmokeRefreshReceipt }));
  assert.equal(evidence.initialRefresh.receipt.ok, true, 'Initial settings runtime refresh failed.');
  assert.equal(await app.page.getByLabel('收起时间（毫秒）').inputValue(), '300');
  assert.equal(await app.page.getByLabel('工具栏主题').inputValue(), 'system');
  await setCheckbox(app.page, '启用悬浮工具栏', true);
  if (startupEnabled) evidence.enableSkipped = 'Enabled before startup in fresh owned state; this does not verify settings enable.';
  else await app.page.waitForFunction(() => window.__toolbarSmokeMessages.some((message) =>
    message.id === window.__toolbarSmokeEnableRequest?.id && message.kind === 'response' &&
    message.type === 'app.command' && message.ok === true), null, { timeout: 5000 });
  evidence.enabledCheckbox = await app.page.getByRole('checkbox', { name: '启用悬浮工具栏' }).isChecked();
  evidence.enableMessages = await app.page.evaluate(() => window.__toolbarSmokeMessages);
  evidence.enabledWindows = await windows(app.child.pid);
  assert(evidence.enabledWindows.some((window) => window.Handle !== app.main.Handle &&
    window.ClassName === 'WinUIDesktopWin32WindowClass' && window.ExtendedStyle & 0x80));
  assert(evidence.enabledWindows.some((window) => window.Visible && window.ClassName.startsWith('VibeOCR.EdgeSensor.')));
  await setDelay(1000);
  await app.page.getByLabel('收起时间（毫秒）').fill('99');
  assert(await app.page.getByRole('button', { name: '保存时间', exact: true }).isDisabled());
  assert.equal(settings().linger_ms, 1000);
  await app.page.getByLabel('收起时间（毫秒）').fill('5001');
  assert(await app.page.getByRole('button', { name: '保存时间', exact: true }).isDisabled());
  await app.page.getByLabel('收起时间（毫秒）').fill('1000');
  let bar = await show();
  const outside = point(app.main, 0.6, 0.6);
  evidence.timer = JSON.parse(await native('toolbar-sequence', { AppPid: app.child.pid, Handle: bar.Handle,
    ...point(bar), OutsideX: outside.X, OutsideY: outside.Y, LingerMs: 1000 }));
  assert(evidence.timer.BeforeReentry && evidence.timer.ReentryCancelled && evidence.timer.HiddenAfterSecondExit);
  assert(evidence.timer.SecondExitElapsedMs >= 900 && evidence.timer.SecondExitElapsedMs <= 1500);
  evidence.defaultDelay = 300;
  evidence.savedDelay = 1000;
  await setCheckbox(app.page, '鼠标离开后自动收起', false);
  assert(await app.page.getByLabel('收起时间（毫秒）').isDisabled());
  bar = await toolbar();
  evidence.nativeWindow = bar;
  assert(bar.ExtendedStyle & 0x80, 'Native toolbar must use WS_EX_TOOLWINDOW.');
  assert(!(bar.ExtendedStyle & 0x40000), 'Native toolbar must not use WS_EX_APPWINDOW.');
  evidence.themes = {};
  for (const theme of ['light', 'dark', 'system']) {
    const savedTheme = await savePreference(() =>
      app.page.getByLabel('工具栏主题').selectOption(theme, { timeout: 5000 }));
    assert.equal(savedTheme.receipt.ok, true, 'Floating toolbar theme save failed; see matched receipt problem.');
    assert.equal(settings().theme, theme);
    const beforeFocus = await native('foreground', { AppPid: app.child.pid });
    const idle = await frame(bar, `toolbar-${theme}.png`);
    await native('hover', { AppPid: app.child.pid, ...point(bar, 0.19) });
    await delay(120);
    const hover = await frame(bar, `toolbar-${theme}-hover.png`);
    assert.equal(await native('foreground', { AppPid: app.child.pid }), beforeFocus);
    assert(idle.dark > 20 && idle.light > 20, `Native ${theme} icons/background are not distinguishable.`);
    evidence.themes[theme] = { idle, hover, retainedForeground: true };
    await native('hover', { AppPid: app.child.pid, ...outside });
  }
  const grip = point(bar, 0.075);
  const dragForeground = await native('foreground', { AppPid: app.child.pid });
  await native('toolbar-drag', { AppPid: app.child.pid, ...grip, OutsideX: outside.X, OutsideY: outside.Y });
  assert.equal(await native('foreground', { AppPid: app.child.pid }), dragForeground, 'Toolbar grip stole main foreground.');
  const dragged = (await windows(app.child.pid)).find((w) => w.Handle === bar.Handle);
  assert(dragged.Visible && dragged.Bounds.Top !== bar.Bounds.Top, 'Real grip drag did not relocate toolbar.');
  await delay(1200);
  assert((await windows(app.child.pid)).find((w) => w.Handle === bar.Handle).Visible, 'Pinned toolbar hid after drag.');
  evidence.drag = dragged;
  await app.page.getByRole('button', { name: '隐藏', exact: true }).click();
  await toolbarStatus(app.page, '已主动隐藏（鼠标路过不恢复）');
  assert.equal(settings().hidden_by_user, true);
  assert(!(await windows(app.child.pid)).some((w) => w.Visible && w.Handle !== app.main.Handle && area(w) > 100));
  const hotkeyRow = app.page.locator('.hotkey-action-row').filter({ hasText: '悬浮栏显示/隐藏' });
  await hotkeyRow.getByRole('textbox').fill('Ctrl+Alt+Shift+F12');
  await hotkeyRow.getByRole('button', { name: '应用 悬浮栏显示/隐藏' }).click();
  await hotkeyRow.getByText('当前生效：Ctrl+Alt+Shift+F12').waitFor();
  await native('toolbar-hotkey', { ForegroundPid: app.child.pid });
  await toolbarStatus(app.page, '显示中');
  assert.equal(settings().hidden_by_user, false);
  await native('toolbar-hotkey', { ForegroundPid: app.child.pid });
  await toolbarStatus(app.page, '已主动隐藏（鼠标路过不恢复）');
  evidence.hotkey = 'Ctrl+Alt+Shift+F12';
  await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: app.main.Handle });
  await app.browser.close().catch(() => {});
  app = await launchApp(candidate, path.join(work, 'webview2'), instanceId);
  evidence.appPids.push(app.child.pid);
  await openSettings(app.page);
  assert.equal(await app.page.getByLabel('收起时间（毫秒）').inputValue(), '1000');
  await toolbarStatus(app.page, '已主动隐藏（鼠标路过不恢复）');
  evidence.restart = { lingerMs: settings().linger_ms, theme: settings().theme, hiddenByUser: settings().hidden_by_user };
  await app.page.screenshot({ path: path.join(work, 'toolbar-settings.png') });
  evidence.screenshots.push(path.join(work, 'toolbar-settings.png'));
  await configure(app.page);
  await show();
  fixture = await startFixture();
  evidence.taskbarStateBefore = await taskbarState();
  evidence.capture = await captureThroughHotkey(app, fixture, work, evidence);
  await openSettings(app.page);
  await setCheckbox(app.page, '启用悬浮工具栏', false);
  await toolbarStatus(app.page, '已关闭');
  assert(!(await windows(app.child.pid)).some((w) => w.Visible && w.Handle !== app.main.Handle && area(w) > 100));
  evidence.state = 'passed';
} catch (error) {
  evidence.error = `${error.name}: ${error.message}`;
  if (app?.child.exitCode === null) {
    evidence.windowsAtFailure = await windows(app.child.pid).catch(() => []);
    evidence.uiPump = JSON.parse(await native('pump', { AppPid: app.child.pid, Handle: app.main.Handle }).catch(() => 'null'));
    evidence.messages = await app.page.evaluate(() => window.__toolbarSmokeMessages).catch(() => null);
    evidence.requests = await app.page.evaluate(() => window.__toolbarSmokeRequests).catch(() => null);
    evidence.pageText = await app.page.locator('body').innerText().catch(() => null);
    await app.page.screenshot({ path: path.join(work, 'toolbar-failure.png') }).catch(() => {});
    if (fs.existsSync(path.join(work, 'toolbar-failure.png'))) evidence.screenshots.push(path.join(work, 'toolbar-failure.png'));
  }
  process.exitCode = 1;
} finally {
  try {
    if (fixture) await stopOwned(fixture.child, 'quit', { FixturePid: fixture.pid, Handle: fixture.root, ThreadId: fixture.threadId });
    if (app) {
      await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: app.main.Handle });
      await app.browser.close().catch(() => {});
    }
    evidence.ownedProcessesStopped = true;
  } catch (error) { evidence.cleanupError = error.message; process.exitCode = 1; evidence.state = 'failed'; }
  fs.writeFileSync(path.join(work, 'toolbar-health.json'), JSON.stringify(evidence, null, 2));
  console.log(`Floating toolbar smoke ${evidence.state}; evidence: ${work}`);
  if (evidence.error) console.error(evidence.error);
}
