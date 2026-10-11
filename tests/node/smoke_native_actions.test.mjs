// Behavioral contract for the seams exported by scripts/smoke_native_actions.mjs:
// the owned-process cleanup (issues #167/#169) and the capture-scene canvas
// physical→CSS geometry mapping (issue #216). The children here are
// real short-lived node processes spawned from process.execPath; no desktop
// windows are touched (the native close path is only exercised through
// stopOwned's bounded fallback on a windowless child). Finally blocks
// terminate only the ChildProcess handles this test itself spawned.
import assert from 'node:assert/strict';
import { ChildProcess, spawn } from 'node:child_process';
import { once } from 'node:events';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const { stopOwned, assertCaptureCanvasLayout } = await import(
  pathToFileURL(path.join(scriptDir, '..', '..', 'scripts', 'smoke_native_actions.mjs')).href);

setTimeout(() => {
  console.error('smoke_native_actions contract test hung for 60s.');
  process.exit(1);
}, 60000).unref();

function spawnIdle() {
  return spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'],
    { stdio: 'ignore', windowsHide: true });
}

async function dispose(child) {
  if (child.exitCode === null && child.signalCode === null) child.kill();
  if (child.exitCode === null && child.signalCode === null) await once(child, 'exit');
}

// Cases 4-7 snapshot the exit/error listener counts before the cleanup call
// and assert they are restored afterwards, directly proving that waitExit's
// aborted once(signal) wait and ownedKill's temporary capture unsubscribe.
const listenerSnapshot = (child) =>
  ({ exit: child.listenerCount('exit'), error: child.listenerCount('error') });
function assertListenersRestored(child, before) {
  assert.equal(child.listenerCount('exit'), before.exit, 'waitExit leaked an exit listener.');
  assert.equal(child.listenerCount('error'), before.error, 'waitExit or ownedKill leaked an error listener.');
}

// 1. A normally exited child must make stopOwned a no-op that resolves.
{
  const child = spawn(process.execPath, ['-e', ''], { stdio: 'ignore', windowsHide: true });
  await once(child, 'exit');
  assert.equal(child.exitCode, 0);
  try {
    await stopOwned(child, 'close', { AppPid: child.pid });
  } finally {
    await dispose(child);
  }
  console.log('case1 normally-exited: ok');
}

// 2. An already SIGTERM-exited child (exit event already fired, exitCode null,
// signalCode SIGTERM) must still count as done: stopOwned resolves instead of
// waiting for an exit event that can never fire again.
{
  const child = spawnIdle();
  child.kill();
  await once(child, 'exit');
  assert.equal(child.exitCode, null);
  assert.equal(child.signalCode, 'SIGTERM');
  try {
    await stopOwned(child, 'close', { AppPid: child.pid });
  } finally {
    await dispose(child);
  }
  assert.equal(child.exitCode, null);
  assert.equal(child.signalCode, 'SIGTERM');
  console.log('case2 signal-exited: ok');
}

// 3. A still-alive owned windowless child must be terminated through the real
// handle: native close fails without an owned HWND and the bounded fallback
// settles the same handle by signal.
{
  const child = spawnIdle();
  try {
    await stopOwned(child, 'close', { AppPid: child.pid });
    assert.equal(child.exitCode, null);
    assert.equal(child.signalCode, 'SIGTERM');
  } finally {
    await dispose(child);
  }
  console.log('case3 alive-owned-cleanup: ok');
}

// 4. Deadline-positive (the observed pinned-smoke WinUI failure): the owned
// process really exited and the same handle answers dead, but the public
// exitCode/signalCode fields are still null because the JS exit notification
// never arrived. The old code waited for an exit event that cannot fire a
// second time and failed the whole run; the endpoint probe must confirm the
// real exit. Only the two public fields are masked; the real handle and pid
// of this owned child are kept.
{
  const child = spawn(process.execPath, ['-e', ''], { stdio: 'ignore', windowsHide: true });
  await once(child, 'exit');
  const { exitCode, signalCode } = child;
  child.exitCode = null;
  child.signalCode = null;
  const listeners = listenerSnapshot(child);
  try {
    await stopOwned(child, 'close', { AppPid: child.pid });
  } finally {
    child.exitCode = exitCode;
    child.signalCode = signalCode;
    try { assertListenersRestored(child, listeners); } finally { await dispose(child); }
  }
  console.log('case4 exit-notification-late: ok');
}

// 5. Fail-closed error contract: a synchronous kill error on the real owned
// ChildProcess.kill seam must fail cleanup instead of passing as a clean
// exit. The injection only controls the method's return and emission; the
// finally block restores the original method and still kills the live child
// through its original handle only.
{
  const child = spawnIdle();
  const originalKill = child.kill.bind(child);
  child.kill = () => {
    child.emit('error', new Error('injected owned-handle kill failure'));
    return false;
  };
  const listeners = listenerSnapshot(child);
  try {
    await assert.rejects(stopOwned(child, 'close', { AppPid: child.pid }),
      /injected owned-handle kill failure/);
  } finally {
    child.kill = originalKill;
    try { assertListenersRestored(child, listeners); } finally { await dispose(child); }
  }
  console.log('case5 kill-error-fail-closed: ok');
}

// 6. Fail-closed alive contract: after the original grace and force 5s
// deadlines, a still-alive answer from the same owned handle must fail
// cleanup instead of trusting the missing exit event. Only the actual
// termination kill is suppressed; the signal-0 query reaches the real
// still-alive handle as a genuine negative control.
{
  const child = spawnIdle();
  const originalKill = child.kill.bind(child);
  child.kill = (signal) => (signal === 0 ? originalKill(0) : true);
  const listeners = listenerSnapshot(child);
  try {
    await assert.rejects(stopOwned(child, 'close', { AppPid: child.pid }),
      /survived cleanup/);
  } finally {
    child.kill = originalKill;
    try { assertListenersRestored(child, listeners); } finally { await dispose(child); }
  }
  console.log('case6 endpoint-alive-fail-closed: ok');
}

// 7. Fail-closed endpoint-error contract: an accepted termination whose
// signal-0 endpoint query synchronously emits an error and answers false
// must still fail cleanup; a bare false is never enough without the
// no-error condition on the original handle.
{
  const child = spawnIdle();
  const originalKill = child.kill.bind(child);
  child.kill = (signal) => {
    if (signal !== 0) return true;
    child.emit('error', new Error('injected endpoint query failure'));
    return false;
  };
  const listeners = listenerSnapshot(child);
  try {
    await assert.rejects(stopOwned(child, 'close', { AppPid: child.pid }),
      /endpoint probe failed: injected endpoint query failure/);
  } finally {
    child.kill = originalKill;
    try { assertListenersRestored(child, listeners); } finally { await dispose(child); }
  }
  console.log('case7 endpoint-error-fail-closed: ok');
}

// 8. A real ChildProcess object that has never spawned has no owned process
// handle; reject it before attempting native close or a signal query.
await assert.rejects(stopOwned(new ChildProcess(), 'close', {}), /positive pid/);
console.log('case8 unspawned-child-fail-closed: ok');

// 9. A 140x42 crop maps exactly through the 175% viewport scale: no CSS size
// floor may reject a legitimate small selection.
{
  const layout = assertCaptureCanvasLayout({
    box: { x: 200, y: 160, width: 80, height: 24 },
    selection: { left: 350, top: 280, width: 140, height: 42 },
    desktopClient: { X: 0, Y: 0, Width: 1920, Height: 1080 },
    viewport: { width: 1097, height: 617 },
  });
  assert.equal(layout.expected.width, 140 * (1097 / 1920));
  assert.equal(layout.scale.y, 617 / 1080);
  assert.deepEqual(layout.measured, { x: 200, y: 160, width: 80, height: 24 });
  console.log('case9 small-crop-175dpi-mapping: ok');
}

// 10. A canvas 60 CSS px off the selection's on-screen spot must fail.
{
  assert.throws(() => assertCaptureCanvasLayout({
    box: { x: 260, y: 160, width: 80, height: 24 },
    selection: { left: 350, top: 280, width: 140, height: 42 },
    desktopClient: { X: 0, Y: 0, Width: 1920, Height: 1080 },
    viewport: { width: 1097, height: 617 },
  }), /canvas x is 260/);
  console.log('case10 wrong-position-fails: ok');
}

// 11. A distorted aspect ratio must fail despite positive, usable sizes.
{
  assert.throws(() => assertCaptureCanvasLayout({
    box: { x: 200, y: 160, width: 80, height: 40 },
    selection: { left: 350, top: 280, width: 140, height: 42 },
    desktopClient: { X: 0, Y: 0, Width: 1920, Height: 1080 },
    viewport: { width: 1097, height: 617 },
  }), /canvas height is 40/);
  console.log('case11 wrong-ratio-fails: ok');
}

// 12. A virtual desktop extending left of the primary has negative screen
// coordinates; the selection still maps through its relative position.
{
  const layout = assertCaptureCanvasLayout({
    box: { x: 1330, y: 750, width: 70, height: 21 },
    selection: { left: 100, top: 60, width: 140, height: 42 },
    desktopClient: { X: -2560, Y: -1440, Width: 5120, Height: 2880 },
    viewport: { width: 2560, height: 1440 },
  });
  assert.deepEqual(layout.expected, { x: 1330, y: 750, width: 70, height: 21 });
  console.log('case12 negative-desktop-origin: ok');
}

// 13. Illegal geometry must fail closed before any tolerance comparison.
{
  const valid = {
    box: { x: 1330, y: 750, width: 70, height: 21 },
    selection: { left: 100, top: 60, width: 140, height: 42 },
    desktopClient: { X: -2560, Y: -1440, Width: 5120, Height: 2880 },
    viewport: { width: 2560, height: 1440 },
  };
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, selection: { ...valid.selection, left: -3000 },
  }), /escaped the frozen desktop/);
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, selection: { ...valid.selection, height: 0 },
  }), /selection geometry is invalid/);
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, desktopClient: { ...valid.desktopClient, Width: 0 },
  }), /client geometry is invalid/);
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, viewport: { ...valid.viewport, width: Number.NaN },
  }), /viewport is invalid/);
  assert.throws(() => assertCaptureCanvasLayout({ ...valid, box: null }),
    /positive finite visual bounds/);
  assert.throws(() => assertCaptureCanvasLayout({
    ...valid, box: { x: 1330, y: 750, width: 70, height: 0 },
  }), /positive finite visual bounds/);
  console.log('case13 illegal-geometry-fails: ok');
}

console.log('smoke_native_actions contract: passed');
