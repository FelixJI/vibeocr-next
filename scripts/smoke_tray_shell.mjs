// Explicitly authorized Windows smoke: only the isolated App and fixture are controlled.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import readline from 'node:readline';
import crypto from 'node:crypto';

const exec = promisify(execFile);
const directory = path.dirname(fileURLToPath(import.meta.url));
const nativeScript = path.join(directory, 'native_actions_fixture.ps1');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function option(name) {
  const index = process.argv.indexOf(name);
  assert(index > 0 && process.argv[index + 1], `Required option ${name} is missing.`);
  return fs.realpathSync(process.argv[index + 1]);
}
function contained(parent, child) {
  const relative = path.relative(parent, child);
  return !relative || (!relative.startsWith('..') && !path.isAbsolute(relative));
}
async function native(action, options = {}) {
  const arguments_ = ['-NoProfile', '-NonInteractive', '-File', nativeScript, '-Action', action];
  for (const [name, value] of Object.entries(options)) arguments_.push(`-${name}`, String(value));
  const { stdout } = await exec('pwsh', arguments_, { timeout: 15000, windowsHide: true });
  return stdout.trim();
}
async function waitFor(check, message, timeout = 10000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const result = await check();
    if (result) return result;
    await delay(100);
  }
  throw new Error(message);
}
async function exitOwned(child, action, options) {
  if (!child || (child.exitCode !== null || child.signalCode !== null)) return;
  try { await native(action, options); } catch { /* Terminate only this run's held process handle below. */ }
  if (!await waitFor(() => (child.exitCode !== null || child.signalCode !== null), 'owned close timeout', 4000).catch(() => false)) {
    child.kill();
    await waitFor(() => (child.exitCode !== null || child.signalCode !== null), `Owned process ${child.pid} survived cleanup`, 5000);
  }
}

async function main() {
  assert.equal(process.platform, 'win32');
  const source = option('--product-root'), work = option('--work-root');
  assert(!contained(source, work) && !contained(work, source), 'Product and work roots must not nest.');
  assert(!fs.existsSync(path.join(source, 'state')), 'Source candidate must have no state.');
  assert(fs.existsSync(path.join(source, 'app/VibeOCR.WinUI.exe')), 'Current-tree candidate is missing.');
  const smokeRoot = path.join(work, `vibeocr-tray-shell-${crypto.randomUUID()}`);
  const candidate = path.join(smokeRoot, 'candidate');
  fs.mkdirSync(smokeRoot);
  fs.cpSync(source, candidate, { recursive: true, errorOnExist: true, force: false });
  const evidence = { schema_version: 1, state: 'failed', smokeRoot, nodeVersion: process.version, sessions: [],
    sourceTree: 'current checkout plus uncommitted task implementation',
    unsupported: ['100/125/150/200% DPI', 'mixed-monitor DPI', 'actual Explorer restart', 'menu screenshot: existing owned-point capture guard refused the menu corners'] };
  let app, fixture, mainHandle, helper;
  const instanceId = crypto.randomUUID().replaceAll('-', '');
  evidence.isolatedTrayGuid = instanceId;
  try {
    const executable = path.join(candidate, 'app/VibeOCR.WinUI.exe');
    app = spawn(executable, ['--shell-only', '--profile', 'production', '--install-root', candidate], {
      cwd: path.dirname(executable), stdio: 'ignore',
      env: { ...process.env, VIBEOCR_SELF_TEST_SMOKE: 'native-actions-e2e',
        VIBEOCR_SELF_TEST_INSTANCE: instanceId, VIBEOCR_TRAY_SELF_TEST: '1',
        WEBVIEW2_USER_DATA_FOLDER: path.join(smokeRoot, 'webview2') },
    });
    evidence.appPid = app.pid;
    mainHandle = await waitFor(async () => {
      assert.equal(app.exitCode, null, 'Isolated App exited before first window.');
      const output = await native('windows', { AppPid: app.pid });
      const windows = output ? [JSON.parse(output)].flat() : [];
      evidence.appWindowsAtLaunch = windows;
      const visible = windows.filter(w => w.Visible && w.Bounds.Right - w.Bounds.Left > 400);
      return visible.sort((a, b) => (b.Bounds.Right - b.Bounds.Left) * (b.Bounds.Bottom - b.Bounds.Top) -
        (a.Bounds.Right - a.Bounds.Left) * (a.Bounds.Bottom - a.Bounds.Top))[0]?.Handle;
    }, 'Isolated WinUI main window did not appear.', 30000);
    evidence.mainHandle = mainHandle;
    fixture = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File', nativeScript, '-Action', 'tray-fixture'],
      { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    const lines = readline.createInterface({ input: fixture.stdout });
    const fields = (await Promise.race([
      once(lines, 'line').then(([line]) => line),
      once(fixture, 'exit').then(([code]) => { throw new Error(`Owned fixture exited ${code}`); }),
      delay(15000).then(() => { throw new Error('Owned fixture startup timed out'); }),
    ])).split('|');
    lines.close();
    assert.equal(Number(fields[0]), fixture.pid);
    helper = { pid: fixture.pid, thread: Number(fields[1]), handle: Number(fields[2]),
      bounds: fields[7].split(',').map(Number), editBounds: fields[10].split(',').map(Number) };
    evidence.fixturePid = helper.pid;
    const parameters = { AppPid: app.pid, FixturePid: helper.pid, Handle: mainHandle };
    const state = async () => JSON.parse(await native('tray-state', parameters));
    const focusHelper = async () => {
      await native('restore', { AppPid: helper.pid, Handle: helper.handle });
      await native('focus-fixture', { FixturePid: helper.pid, Handle: helper.handle,
        X: Math.round((helper.editBounds[0] + helper.editBounds[2]) / 2),
        Y: Math.round((helper.editBounds[1] + helper.editBounds[3]) / 2) });
    };
    const initial = await state();
    if (!process.argv.includes('--exit-only')) {
    evidence.openGestures = [];
    const icon = { AppPid: app.pid, FixturePid: helper.pid, Handle: initial.Owner.Handle, IconGuid: instanceId };
    for (const action of ['tray-left-click', 'tray-double-click']) {
      await native('hide', { AppPid: app.pid, Handle: mainHandle });
      await focusHelper();
      await native('tray-expose', icon);
      const input = JSON.parse(await native(action, icon));
      const gesture = { action, input };
      evidence.openGestures.push(gesture);
      const after = await waitFor(async () => {
        const snapshot = await state();
        gesture.last = snapshot;
        return snapshot.Main.Visible && !snapshot.Main.Iconic && snapshot.ForegroundHandle === mainHandle && snapshot;
      }, `${action} did not open and activate the main window`);
      gesture.after = after;
    }
    for (const mode of ['hidden', 'minimized', 'background']) {
      await native('restore', { AppPid: app.pid, Handle: mainHandle });
      if (mode === 'hidden') await native('hide', { AppPid: app.pid, Handle: mainHandle });
      if (mode === 'minimized') await native('minimize', { AppPid: app.pid, Handle: mainHandle });
      await focusHelper();
      const before = await state();
      assert.equal(before.Main.Visible, mode !== 'hidden');
      assert.equal(before.Main.Iconic, mode === 'minimized');
      assert.equal(before.ForegroundHandle, helper.handle);
      const record = { mode, before, cancellations: [] };
      evidence.sessions.push(record);
      const icon = { AppPid: app.pid, FixturePid: helper.pid, Handle: before.Owner.Handle, IconGuid: instanceId };
      const openMenu = async trigger => {
        await focusHelper();
        const exposed = JSON.parse(await native('tray-expose', icon));
        const input = JSON.parse(await native(trigger === 'keyboard' ? 'tray-keyboard' : 'tray-click', icon));
        const during = await waitFor(async () => {
          const snapshot = await state();
          return snapshot.Menus.length === 1 && snapshot;
        }, `${mode}: ${trigger} did not open an owned menu`);
        assert.equal(during.Main.Visible, before.Main.Visible);
        assert.equal(during.Main.Iconic, before.Main.Iconic);
        assert.notEqual(during.ForegroundHandle, mainHandle, 'Tray menu activated the WinUI main window.');
        assert.equal(during.ForegroundPid, app.pid, 'Menu foreground is outside the isolated App.');
        assert([during.Owner.Handle, ...during.Menus.map(menu => menu.Handle)].includes(during.ForegroundHandle),
          'Foreground must be the isolated tray owner or its menu.');
        return { trigger, exposed, input, during };
      };
      for (const [trigger, cancel] of [['mouse', 'escape'], ['keyboard', 'escape'], ['mouse', 'outside']]) {
        const attempt = await openMenu(trigger);
        attempt.cancel = cancel;
        record.cancellations.push(attempt);
        if (cancel === 'escape') await native('escape', { AppPid: app.pid });
        else await focusHelper();
        const after = await waitFor(async () => {
          const snapshot = await state();
          return snapshot.Menus.length === 0 && snapshot;
        }, `${mode}: ${cancel} did not close the owned menu`);
        attempt.after = after;
        assert.equal(after.Main.Visible, before.Main.Visible);
        assert.equal(after.Main.Iconic, before.Main.Iconic);
        assert.notEqual(after.ForegroundHandle, mainHandle, 'Menu cancellation brought back the main window.');
      }
      record.explicitOpen = await openMenu('mouse');
      record.explicitOpen.click = JSON.parse(await native('tray-menu-open', { AppPid: app.pid, Handle: record.explicitOpen.during.Menus[0].Handle }));
      record.open = await waitFor(async () => {
        const snapshot = await state();
        record.lastOpenState = snapshot;
        return snapshot.Main.Visible && !snapshot.Main.Iconic && snapshot.ForegroundHandle === mainHandle && snapshot;
      }, `${mode}: explicit Open Workbench did not restore and activate the main window`);
    }
    }
    const final = await state();
    const icon = { AppPid: app.pid, FixturePid: helper.pid, Handle: final.Owner.Handle, IconGuid: instanceId };
    await focusHelper();
    await native('tray-expose', icon);
    await native('tray-click', icon);
    evidence.exitMenu = await waitFor(async () => { const snapshot = await state(); return snapshot.Menus.length === 1 && snapshot; }, 'Exit menu did not appear');
    evidence.exitInput = JSON.parse(await native('tray-menu-quit', { AppPid: app.pid, Handle: evidence.exitMenu.Menus[0].Handle }));
    await waitFor(async () => {
      if (app.exitCode !== null) return true;
      evidence.exitLast = await state();
      return false;
    }, 'Tray Exit did not stop the isolated App');
    evidence.exit = JSON.parse(await native('tray-gone', { Handle: final.Owner.Handle, IconGuid: instanceId }));
    assert(evidence.exit.OwnerGone && evidence.exit.IconGone, 'Tray Exit leaked the owned owner or icon');
    evidence.dpi = final.Main.Dpi;
    evidence.state = 'passed';
    console.log(`Real WinUI tray shell smoke passed: ${smokeRoot}`);
  } catch (error) {
    evidence.error = `${error.name}: ${error.message}`;
    throw error;
  } finally {
    const failures = [];
    try { await exitOwned(fixture, 'quit', { FixturePid: fixture?.pid, Handle: helper?.handle ?? 0, ThreadId: helper?.thread ?? 0 }); } catch (error) { failures.push(error.message); }
    try { await exitOwned(app, 'close', { AppPid: app?.pid, Handle: mainHandle ?? 0 }); }
    catch (error) { failures.push(error.message); }
    if (failures.length) { evidence.state = 'failed'; evidence.cleanupError = failures; }
    evidence.ownedPidsExited = [app, fixture].filter(Boolean).every(child => (child.exitCode !== null || child.signalCode !== null));
    fs.writeFileSync(path.join(smokeRoot, 'tray-shell-health.json'), JSON.stringify(evidence, null, 2));
    if (failures.length) throw new Error(failures.join('; '));
  }
}
main().catch(error => { console.error(`${error.name}: ${error.message}`); process.exitCode = 1; });
