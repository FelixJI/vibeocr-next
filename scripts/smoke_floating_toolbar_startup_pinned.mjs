// Authorized minimal cold-start regression for the pinned floating toolbar;
// all state and evidence stay in a new TEMP tree. The full
// scripts/smoke_floating_toolbar.mjs gate (and its --startup-enabled mode)
// only arms the edge sensor with the default auto_hide=true; this run saves
// enabled=true + auto_hide=false + hidden_by_user=false before the first
// launch, so the very first ShowAt happens synchronously inside OnLaunched.
// Old code opened the popup before the window content Loaded (no host
// XamlRoot) and crashed with 0x8000FFFF before the WebView CDP port answered;
// this smoke must reach the Workbench, find the rendered 272x44 toolbar popup
// host, and keep the persisted settings untouched.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import {
  native, waitForWindows, delay, launchApp, openSettings, stopOwned, pngPixels,
} from './smoke_native_actions.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const exec = promisify(execFile);
const source = fs.realpathSync(process.argv[process.argv.indexOf('--product-root') + 1]);
assert(fs.existsSync(path.join(source, 'app/metadata/product-layout.json')));
assert(!fs.existsSync(path.join(source, 'state')), 'Candidate must have no user state.');
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'vibeocr-toolbar-pinned-'));
const candidate = path.join(work, 'candidate');
const instanceId = crypto.randomUUID().replaceAll('-', '');
fs.cpSync(source, candidate, { recursive: true, force: false, errorOnExist: true });
fs.mkdirSync(path.join(candidate, 'state/config'), { recursive: true });
const pinnedSettings = { enabled: true, auto_hide: false, hidden_by_user: false };
fs.writeFileSync(path.join(candidate, 'state/config/app_settings.json'),
  JSON.stringify({ floating_toolbar: pinnedSettings }));
const identity = JSON.parse(
  fs.readFileSync(path.join(source, 'app/metadata/component-identities.json'), 'utf8')).project;
const evidence = { schema_version: 1, state: 'failed', sourceSha: identity.source_sha, version: identity.version,
  candidateNote: process.argv.includes('--candidate-note') ? process.argv[process.argv.indexOf('--candidate-note') + 1] : 'Build from source identity above.',
  candidate, appPids: [], screenshots: [], gaps: [
    'This run covers only the pinned cold-start first-popup path; linger, drag, hotkey, theme transition and tray behavior stay with the full floating toolbar smoke.',
    'Window styles are asserted structurally only: taskbar semantics via WS_EX_TOOLWINDOW without WS_EX_APPWINDOW, and no-activate via WS_EX_NOACTIVATE present on the created window; physical foreground/focus behavior is not exercised here and stays with the full smoke.',
  ] };
const configFile = path.join(candidate, 'state/config/app_settings.json');
const settings = () => JSON.parse(fs.readFileSync(configFile, 'utf8')).floating_toolbar;
async function clientGeometry(handle) {
  return JSON.parse(await native('geometry', { FixturePid: app.child.pid, Handle: handle }));
}
// The popup host outer frame carries a DPI-scaled border; the XAML toolbar is
// the OwnedPopup client, so geometry checks use client coordinates.
async function pinnedToolbar() {
  const until = Date.now() + 10000;
  while (Date.now() < until) {
    const candidates = await waitForWindows(app.child.pid, (w) => w.Visible && w.Handle !== app.main.Handle &&
      w.ClassName === 'WinUIDesktopWin32WindowClass' && (w.ExtendedStyle & 0x80) !== 0);
    for (const window of candidates) {
      const { client, dpi } = await clientGeometry(window.Handle);
      const scale = dpi / 96;
      if (Math.abs(client.Width / scale - 272) < 3 && Math.abs(client.Height / scale - 44) < 3)
        return { ...window, client, dpi };
    }
    await delay(180);
  }
  throw new Error('Pinned toolbar popup host with a 272x44 logical client did not appear.');
}
let app;
try {
  app = await launchApp(candidate, path.join(work, 'webview2'), instanceId);
  evidence.appPids.push(app.child.pid);
  evidence.pageErrors = [];
  app.page.on('pageerror', (error) => evidence.pageErrors.push(error.message));
  const bar = await pinnedToolbar();
  evidence.pinnedToolbar = bar;
  assert(bar.ExtendedStyle & 0x80, 'Native toolbar must use WS_EX_TOOLWINDOW.');
  assert(!(bar.ExtendedStyle & 0x40000), 'Native toolbar must not use WS_EX_APPWINDOW.');
  assert(bar.ExtendedStyle & 0x08000000, 'Native toolbar must carry WS_EX_NOACTIVATE.');
  // The cold-start Workbench must stay reachable after the first popup open.
  await openSettings(app.page);
  await app.page.waitForURL('https://app.vibeocr/index.html#/settings');
  const file = path.join(work, 'toolbar-startup-pinned.png');
  const { client } = await clientGeometry(bar.Handle);
  await exec('pwsh', ['-NoProfile', '-NonInteractive', '-File', path.join(scriptDir, 'scroll_capture_fixture.ps1'),
    '-Action', 'frame', '-FixturePid', String(app.child.pid), '-Handle', String(bar.Handle),
    '-X', String(client.X), '-Y', String(client.Y), '-Width', String(client.Width),
    '-Height', String(client.Height), '-EvidenceRoot', work, '-OutputPath', file],
  { timeout: 15000, windowsHide: true });
  evidence.screenshots.push(file);
  evidence.pinnedPixels = await pngPixels(app.page, fs.readFileSync(file));
  assert(evidence.pinnedPixels.dark > 20 && evidence.pinnedPixels.light > 20,
    'Cold-start pinned toolbar did not render distinguishable commands.');
  assert.deepEqual(evidence.pageErrors, [], 'Workbench reported page errors during cold start.');
  const saved = settings();
  // Only this task's three-contract fields; Load fills defaults for the rest
  // and future default expansion must not fail this run.
  assert.equal(saved.enabled, true, 'Cold start must keep the toolbar enabled.');
  assert.equal(saved.auto_hide, false, 'Cold start must keep the pinned toolbar visible.');
  assert.equal(saved.hidden_by_user, false, 'Cold start must not mark the toolbar user-hidden.');
  evidence.savedSettings = saved;
  evidence.state = 'passed';
} catch (error) {
  evidence.error = `${error.name}: ${error.message}`;
  if (app?.child.exitCode === null) {
    evidence.windowsAtFailure = await waitForWindows(app.child.pid, () => true, 1000).catch(() => []);
    await app.page.screenshot({ path: path.join(work, 'startup-pinned-failure.png') }).catch(() => {});
    if (fs.existsSync(path.join(work, 'startup-pinned-failure.png')))
      evidence.screenshots.push(path.join(work, 'startup-pinned-failure.png'));
  }
  process.exitCode = 1;
} finally {
  try {
    if (app) {
      await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: app.main.Handle });
      await app.browser.close().catch(() => {});
    }
    evidence.ownedProcessesStopped = true;
  } catch (error) { evidence.cleanupError = error.message; process.exitCode = 1; evidence.state = 'failed'; }
  fs.writeFileSync(path.join(work, 'toolbar-startup-pinned-health.json'), JSON.stringify(evidence, null, 2));
  console.log(`Floating toolbar pinned cold-start smoke ${evidence.state}; evidence: ${work}`);
  if (evidence.error) console.error(evidence.error);
}
