// Task #217 contract tests for the bounded Windows keyboard/IME/UIA seams.
// Pure Node + PowerShell only: no GUI, no SendInput, no settings access, no
// candidate app, no fixture execution beyond the non-GUI ABI self-check
// (Add-Type + Marshal.SizeOf). Only observable behavior is asserted here;
// source-string scanning is intentionally not used.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';

const exec = promisify(execFile);
const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repo = path.dirname(path.dirname(scriptDir));
const fixture = path.join(repo, 'scripts', 'native_interaction_fixture.ps1');
const smoke = path.join(repo, 'scripts', 'smoke_windows_interaction.mjs');

setTimeout(() => {
  console.error('smoke_windows_interaction contract test hung for 60s.');
  process.exit(1);
}, 60000).unref();

// 1. The smoke module loads and its result vocabulary is the exact contract
// the main agent parses from the exit code and health JSON.
const imported = await import(pathToFileURL(smoke).href);
assert.equal(imported.PASS, 'pass');
assert.equal(imported.FAIL, 'fail');
assert.equal(imported.EVIDENCE_MISSING, 'evidence_missing');
assert.equal(typeof imported.classifyImeObservation, 'function');
assert.equal(typeof imported.summarizeSteps, 'function');
assert.equal(typeof imported.main, 'function');
console.log('case1 smoke-module-behavior: ok');

// 2. IME observation classifier: honest pass / gap / fail semantics.
assert.deepEqual(imported.classifyImeObservation({
  compositionStarted: true, compositionEnded: true, value: '中文', typed: 'zhongwen',
}), { state: imported.PASS, reason: null });
assert.equal(imported.classifyImeObservation({
  compositionStarted: false, compositionEnded: false, value: 'nihao', typed: 'nihao',
}).state, imported.EVIDENCE_MISSING);
assert.match(imported.classifyImeObservation({
  compositionStarted: false, compositionEnded: false, value: 'nihao', typed: 'nihao',
}).reason, /English mode/);
assert.equal(imported.classifyImeObservation({
  compositionStarted: false, compositionEnded: false, value: '', typed: 'nihao',
}).state, imported.FAIL);
assert.equal(imported.classifyImeObservation({
  compositionStarted: true, compositionEnded: false, value: '中文', typed: 'zhongwen',
}).state, imported.FAIL);
assert.throws(() => imported.classifyImeObservation({
  compositionStarted: true, compositionEnded: true, value: 'x', typed: '',
}), /typed pinyin/);
// Commit verdicts use the dedicated classifier: the generic one intentionally
// passes started+ended (observation phases), so an ASCII passthrough commit
// must be rejected by classifyImeCommit - never by reusing the generic PASS.
assert.equal(imported.classifyImeObservation({
  compositionStarted: true, compositionEnded: true, value: 'zhongwen', typed: 'zhongwen',
}).state, imported.PASS);
assert.equal(imported.classifyImeCommit({
  started: true, ended: true, value: 'zhongwen', typed: 'zhongwen',
}).state, imported.FAIL, 'ASCII commit must not PASS');
assert.match(imported.classifyImeCommit({
  started: true, ended: true, value: 'zhongwen', typed: 'zhongwen',
}).reason, /without CJK text/);
assert.equal(imported.classifyImeCommit({
  started: true, ended: true, value: '中文', typed: 'zhongwen',
}).state, imported.PASS);
assert.equal(imported.classifyImeCommit({
  started: true, ended: false, value: '', typed: 'zhongwen',
}).state, imported.FAIL);
assert.equal(imported.classifyImeCommit({
  started: false, ended: false, value: 'zhongwen', typed: 'zhongwen',
}).state, imported.EVIDENCE_MISSING);
assert.equal(imported.classifyImeCommit({
  started: false, ended: false, value: '', typed: 'zhongwen',
}).state, imported.FAIL);
console.log('case2 ime-classifier: ok');

// 3. Step aggregation: fail dominates with exit semantics 1; evidence gaps
// alone are NOT a pass - they surface as evidence_missing (main exits 2) so
// an AC5 gap can never hide behind a green overall result.
assert.equal(imported.summarizeSteps({
  preflight: { state: 'pass' },
  qrIme: { state: 'pass' },
}).overall, 'passed');
assert.equal(imported.summarizeSteps({
  preflight: { state: 'pass' },
  qrIme: { state: 'evidence_missing', reason: 'no IME HKL' },
  imageText: { state: 'pass' },
}).overall, 'evidence_missing');
assert.equal(imported.summarizeSteps({
  preflight: { state: 'pass' },
  imageText: { state: 'fail', reason: 'Esc closed the window' },
}).overall, 'failed');
assert.deepEqual(imported.summarizeSteps({
  preflight: { state: 'pass' },
  qrIme: { state: 'evidence_missing', reason: 'no IME HKL' },
}).gapReasons, ['qrIme: no IME HKL']);
console.log('case3 summarize-steps: ok');

if (process.platform === 'win32') {
  // 4. Real non-GUI ABI self-check: Add-Type + Marshal.SizeOf only. Catches
  // GUITHREADINFO/INPUT layout drift that no static check could.
  const runFixture = (extra) => exec('pwsh', ['-NoProfile', '-NonInteractive', '-File', fixture,
    '-Action', 'abi-selfcheck', ...extra], { timeout: 60000, windowsHide: true });
  const { stdout } = await runFixture([]);
  const abi = JSON.parse(stdout.trim());
  assert.deepEqual(abi, { pointerBytes: 8, guiThreadInfoSize: 72, inputSize: 40 },
    `ABI self-check mismatch: ${stdout.trim()}`);
  console.log('case4 fixture-abi-selfcheck: ok');

  // 4b. Real parameter-binding regression (non-GUI): the modifier switches
  // must bind as PowerShell switches under `pwsh -File` exactly the way
  // interaction() sends them for every app-hotkey call; a string "true"
  // previously failed with Cannot convert System.String to System.Boolean.
  const bound = await runFixture(['-Ctrl', '-Shift', '-Alt']);
  assert.deepEqual(JSON.parse(bound.stdout.trim()),
    { pointerBytes: 8, guiThreadInfoSize: 72, inputSize: 40 },
    `Switch binding under -File failed: ${bound.stdout.trim()}`);
  console.log('case4b fixture-switch-binding: ok');

  // 5. PowerShell AST parse-only validation of the fixture (no execution of
  // any input/system action).
  const parse = await exec('pwsh', ['-NoProfile', '-NonInteractive', '-Command',
    `$tokens=$null;$errors=$null;` +
    `[System.Management.Automation.Language.Parser]::ParseFile('${fixture.replace(/'/g, "''")}',[ref]$tokens,[ref]$errors)|Out-Null;` +
    `if($errors.Count -gt 0){$errors|ForEach-Object{Write-Output $_.Message};exit 1};Write-Output parse-ok`],
  { timeout: 30000, windowsHide: true });
  assert.match(parse.stdout.trim(), /^parse-ok$/);
  console.log('case5 fixture-ast-parse: ok');
} else {
  console.log('case4/case5 windows-only: skipped (non-Windows)');
}

console.log('smoke_windows_interaction contract: passed');

const restoreRecord = { state: imported.PASS, reason: null };
assert.equal(imported.applyImeRestore(restoreRecord, { restored: false }), restoreRecord);
assert.equal(restoreRecord.state, imported.FAIL);
assert.equal(restoreRecord.restore.restored, false);
assert.equal(imported.applyImeRestore({ state: imported.PASS }, { restored: true }).state, imported.PASS);
