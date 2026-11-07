// Behavioral contract for the seams exported by scripts/smoke_native_actions.mjs:
// the owned-process cleanup (issues #167/#169) and the capture-scene canvas
// physical→CSS geometry mapping (issue #216). The children here are
// real short-lived node processes spawned from process.execPath; no desktop
// windows are touched (the native close path is only exercised through
// stopOwned's bounded fallback on a windowless child). Finally blocks
// terminate only the ChildProcess handles this test itself spawned.
import assert from 'node:assert/strict';
import { ChildProcess, spawn } from 'node:child_process';
import { EventEmitter, once } from 'node:events';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const { stopOwned, closeAppNormally, assertCaptureCanvasLayout, settleSceneCopyTimeline,
  observeSceneCopyTerminal, createUploadHandoff, raceDeadline } = await import(
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
    await closeAppNormally(child, { AppPid: child.pid });
  } finally {
    await dispose(child);
  }
  console.log('case1 normally-exited: ok');
}

// A completed app with a nonzero exit must fail acceptance even though cleanup is done.
{
  const child = spawn(process.execPath, ['-e', 'process.exit(5)'], { stdio: 'ignore', windowsHide: true });
  await once(child, 'exit');
  await assert.rejects(closeAppNormally(child, { AppPid: child.pid }), /Normal app close exited 5/);
  console.log('normal-close-nonzero-fails: ok');
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
    await assert.rejects(closeAppNormally(child, { AppPid: child.pid }), /received signal SIGTERM/);
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

// 14. Scene-terminal copy lifecycle (goal316): the judge accepts only a
// complete success timeline - exactly one accepted 201 __annotation upload
// followed by the scene close - and keeps failing every incomplete or
// abnormal outcome: no upload, non-201/missing response, crash, no close,
// duplicate uploads and a close that precedes the accepted response.
{
  const base = { requestAt: 1000, requestCount: 1, responseStatus: 201,
    responseAt: 1010, closedAt: 1020, crashedAt: null,
    requestOrder: 1, responseOrder: 2, closedOrder: 3 };
  assert.equal(settleSceneCopyTimeline({ ...base }), true);
  assert.throws(() => settleSceneCopyTimeline({ ...base, requestAt: null, requestCount: 0 }),
    /no __annotation POST observed/);
  assert.throws(() => settleSceneCopyTimeline({ ...base, responseStatus: 500 }),
    /was not accepted/);
  assert.throws(() => settleSceneCopyTimeline({ ...base, responseStatus: null, responseAt: null }),
    /was not accepted/);
  assert.throws(() => settleSceneCopyTimeline({ ...base, crashedAt: 1015 }),
    /crashed instead of closing/);
  assert.throws(() => settleSceneCopyTimeline({ ...base, closedAt: null }),
    /did not close after the accepted copy/);
  assert.throws(() => settleSceneCopyTimeline({ ...base, requestCount: 2 }),
    /exactly one annotation upload/);
  assert.throws(() => settleSceneCopyTimeline({ ...base, closedAt: 1005, closedOrder: 1 }),
    /closed before the upload response/);
  assert.throws(() => settleSceneCopyTimeline({ ...base, noTerminalEvent: true }),
    /exceeded its deadline/);
  assert.throws(() => settleSceneCopyTimeline(null), /timeline evidence is missing/);
  console.log('case14 scene-copy-timeline-lifecycle: ok');
}

// 15. Real terminal-copy listeners driven by simulated page events: an
// instant success close (request + 201 + close in the same tick) still
// leaves the complete Node-held timeline, while a close without any upload
// fails. This is the no-GUI proof that an immediately closing scene cannot
// lose the copy evidence held by the pre-registered listeners.
{
  const instant = new EventEmitter();
  const observed = observeSceneCopyTerminal(instant);
  instant.emit('request', { method: () => 'POST',
    url: () => 'https://app.vibeocr/__annotation',
    postDataBuffer: () => Buffer.from([137, 80, 78, 71]) });
  instant.emit('response', { url: () => 'https://app.vibeocr/__annotation',
    request: () => ({ method: () => 'POST' }), status: () => 201 });
  instant.emit('close');
  await observed.ended;
  await observed.received;
  assert.equal(observed.timeline.requestCount, 1);
  assert.equal(observed.timeline.cdpBodyBytes, 4);
  assert.equal(settleSceneCopyTimeline(observed.timeline), true);
  assert.throws(() => settleSceneCopyTimeline({ ...observed.timeline,
    requestCount: 2 }), /exactly one/);

  const noUpload = new EventEmitter();
  const closing = observeSceneCopyTerminal(noUpload);
  noUpload.emit('close');
  await closing.ended;
  assert.throws(() => settleSceneCopyTimeline(closing.timeline),
    /no __annotation POST observed/);
  const originalNow = Date.now;
  try {
    Date.now = () => 1000;
    const reversed = new EventEmitter();
    const earlyClose = observeSceneCopyTerminal(reversed);
    reversed.emit('request', { method: () => 'POST',
      url: () => 'https://app.vibeocr/__annotation', postDataBuffer: () => null });
    reversed.emit('close');
    reversed.emit('response', { url: () => 'https://app.vibeocr/__annotation',
      request: () => ({ method: () => 'POST' }), status: () => 201 });
    await earlyClose.ended;
    assert.throws(() => settleSceneCopyTimeline(earlyClose.timeline),
      /closed before the upload response/);
  } finally { Date.now = originalNow; }
  console.log('case15 terminal-copy-event-simulation: ok');
}

// 16. The upload handoff sink keeps only the first delivered payload (the
// binding evidence is one-shot), and raceDeadline settles false on expiry
// while clear() prevents both firing and a dangling timer.
{
  const handoff = createUploadHandoff();
  handoff.deliver({ count: 1, mimeType: 'image/png', byteLength: 4,
    bytes: [1, 2, 3, 4], problem: null });
  handoff.deliver({ count: 2 });
  assert.deepEqual(await handoff.done, { count: 1, mimeType: 'image/png',
    byteLength: 4, bytes: [1, 2, 3, 4], problem: null });
  assert.equal(handoff.payload.count, 1);

  const expired = raceDeadline(1);
  assert.equal(await expired.promise, false);
  const cleared = raceDeadline(20000);
  cleared.clear();
  assert.equal(cleared.promise instanceof Promise, true);
  console.log('case16 upload-handoff-and-race-deadline: ok');
}

console.log('smoke_native_actions contract: passed');
