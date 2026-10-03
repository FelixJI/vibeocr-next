// Behavioral contract for the owned-process cleanup seam exported by
// scripts/smoke_native_actions.mjs (issue #167). The children here are real
// short-lived node processes spawned from process.execPath; no desktop
// windows are touched (the native close path is only exercised through
// stopOwned's bounded fallback on a windowless child). Finally blocks
// terminate only the ChildProcess handles this test itself spawned.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const { stopOwned } = await import(
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

console.log('smoke_native_actions contract: passed');
