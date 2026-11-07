import fs from 'node:fs';
import os from 'node:os';
import { execFileSync, spawn } from 'node:child_process';
import { once } from 'node:events';
import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { cleanupLineage, zeroEvidence, allowsLocalToolRequest } from '../../scripts/smoke_runtime_demand_tools.mjs';
import { statistics, attachWorkingSets, summarize, validateBaseline, checkAfter } from '../../scripts/smoke_capture_first_frame.mjs';

const baselineCounts = { Subscriptions: 1, Resources: 0, AnnotationEntries: 0,
  AnnotationReservations: 0, AnnotationOwnedFiles: 0, ResourceFiles: 0, EditorWebViews: 0, TotalWebViews: 1 };
function batch(cycles) {
  return { state: 'passed', health: { schema_version: 1, state: 'passed', callbacks_after_stop: 0,
    submit_attempts: 0, startup_ensure_attempts: 0, inference_attached: false,
    baseline: baselineCounts, rows: Array.from({ length: cycles }, (_, index) => ({
      cycle: index + 1, temperature: index === 0 ? 'cold' : 'warm',
      capture_to_presented_ms: index === 0 ? 100 : 50,
      capture_complete_to_presented_ms: 45, editor_to_presented_ms: 40, command_to_presented_ms: 110,
      dispose_ms: 3, before: baselineCounts, closed: baselineCounts,
      ready: { ...baselineCounts, Subscriptions: 2, Resources: 1, EditorWebViews: 1, TotalWebViews: 2 },
      capture: { Width: 900, Height: 700, WhitePixels: 20000, DarkPixels: 200 },
      command_at_ms: 1000, closed_at_ms: 2000, settled_at_ms: 3000,
      dispatch_lag_ms: [1, 2, 3],
      working_set: { app_closed_median_bytes: 100 * 1024 * 1024,
        webview_closed_median_bytes: 200 * 1024 * 1024,
        app_observed_peak_bytes: 150 * 1024 * 1024, webview_observed_peak_bytes: 250 * 1024 * 1024 },
    })) }, zero_evidence: { violations: [], root_pid_seen: true, applog_non_empty: true, applog_forbidden_hits: [] } };
}
export function runSelfCheck() {
  assert.deepEqual(statistics([9, 1, 3, 7]), { count: 4, median: 5, p95: 9, max: 9 });
  assert.throws(() => statistics([]), /missing/);
  assert.throws(() => statistics([NaN]), /Invalid/);
  const batches = [batch(20), batch(1), batch(1)];
  const summary = summarize(batches);
  assert.equal(summary.row_count, 22);
  assert.deepEqual(summary.cold, { count: 3, median_ms: 100, max_ms: 100 });
  assert.deepEqual(summary.warm, { count: 19, median_ms: 50, p95_ms: 50, max_ms: 50 });
  assert.equal(summary.frozen_after_budgets.ui_dispatch_p95_ms, 32);
  assert.equal(checkAfter(summary, summary), summary.frozen_after_budgets);
  const slower = structuredClone(summary);
  slower.warm.p95_ms = summary.frozen_after_budgets.warm_p95_ms + 1;
  assert.throws(() => checkAfter(slower, summary), /Warm p95/);
  const leaked = structuredClone(batches);
  leaked[0].health.rows[19].closed = { ...leaked[0].health.rows[19].closed, Resources: 1 };
  assert.throws(() => summarize(leaked));
  const missing = structuredClone(batches);
  missing[0].health.rows[0].dispatch_lag_ms = [];
  assert.throws(() => summarize(missing), /dispatch samples/);
  const forbidden = structuredClone(batches);
  forbidden[1].zero_evidence.violations.push('installer observed');
  assert.throws(() => summarize(forbidden));
  for (const field of ['root_pid_seen', 'applog_non_empty']) {
    const absent = structuredClone(batches);
    absent[1].zero_evidence[field] = false;
    assert.throws(() => summarize(absent));
    delete absent[1].zero_evidence[field];
    assert.throws(() => summarize(absent));
  }
  const installerLog = structuredClone(batches);
  installerLog[0].zero_evidence.applog_forbidden_hits.push('Runtime Installer command starting action=list');
  assert.throws(() => summarize(installerLog));
  const growth = structuredClone(batches);
  for (const row of growth[0].health.rows.slice(-5)) row.working_set.app_closed_median_bytes += 33 * 1024 * 1024;
  const growthSummary = summarize(growth);
  assert.equal(growthSummary.row_count, 22);
  assert.equal(growthSummary.closed_working_set_growth.app.passed, false);
  assert.match(growthSummary.gate_violations[0], /closed working set growth 34603008 bytes exceeds 33554432 bytes/);
  assert.throws(() => checkAfter(growthSummary, summary), /closed working set growth/);
  const failedBefore = { schema_version: 1, state: 'failed', original_state: 'failed',
    measurement_complete: true, batches: growth, summary: summarize(growth) };
  assert.equal(validateBaseline(failedBefore), failedBefore.summary);
  assert.doesNotThrow(() => checkAfter(summarize(batches), validateBaseline(failedBefore)));
  const invalidBatch = structuredClone(failedBefore);
  invalidBatch.batches[0].state = 'failed';
  assert.throws(() => validateBaseline(invalidBatch), /Invalid batch evidence/);
  assert.throws(() => validateBaseline({ ...failedBefore, measurement_complete: false }), /incomplete/);
  const invalidBefore = structuredClone(failedBefore);
  invalidBefore.batches[0].health.rows[0].dispatch_lag_ms = [];
  assert.throws(() => validateBaseline(invalidBefore), /dispatch samples/);
  const frozen = { ...summary, frozen_after_budgets: {
    cold_median_ms: 825.55237, warm_p95_ms: 722.57103, ui_dispatch_p95_ms: 32 } };
  const budgetFailure = summarize(batches);
  budgetFailure.warm.p95_ms = 723;
  assert.throws(() => checkAfter(budgetFailure, frozen), /Warm p95 723ms exceeds frozen before budget 722.57103ms/);
  assert.equal(budgetFailure.frozen_after_budgets, frozen.frozen_after_budgets);
  assert.equal(budgetFailure.performance_comparison.warm_p95_ms.observed_ms, 723);
  assert.equal(budgetFailure.performance_comparison.warm_p95_ms.passed, false);
  assert.equal(budgetFailure.row_count, 22);
  assert.equal(budgetFailure.gate_violations.length, 1);
  const samples = [{ started_at_ms: 2299, at_ms: 2300, procs: [{ pid: 99, name: 'VibeOCR.WinUI.exe', ws_bytes: 10 },
    { pid: 101, name: 'msedgewebview2.exe', ws_bytes: 20, webview_role: 'browser' },
    { pid: 102, name: 'msedgewebview2.exe', ws_bytes: 30, webview_role: 'renderer' }] }];
  const attached = attachWorkingSets(batches[1].health, samples, 99);
  assert.equal(attached[0].working_set.app_closed_median_bytes, 10);
  assert.equal(attached[0].working_set.webview_closed_median_bytes, 50);
  assert.deepEqual(attached[0].working_set.webview_role_observed_peaks, { browser: 1, renderer: 1 });
  assert.throws(() => attachWorkingSets(batches[1].health, [], 99), /missing/);
  assert.throws(() => attachWorkingSets(batches[1].health, [{ ...samples[0], at_ms: 5000 }], 99), /No closed/);
  assert.throws(() => attachWorkingSets(batches[1].health, samples, 999), /working set is missing/);
  const incompleteFirstClosed = { started_at_ms: 2200, at_ms: 2250, procs: [samples[0].procs[0]] };
  assert.throws(() => attachWorkingSets(batches[1].health, [incompleteFirstClosed, ...samples], 99),
    /App\/shared WebView working set is missing/);
  assert.throws(() => attachWorkingSets(batches[1].health, [{ ...samples[0], started_at_ms: 1999 }], 99), /No closed/);
  assert.throws(() => attachWorkingSets(batches[1].health, [{ ...samples[0], started_at_ms: 2301 }], 99), /sample interval/);
}
if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  runSelfCheck();
  console.log('Capture first-frame statistics, resource rejection, working-set and frozen-budget contracts: passed');
}

test('real owned working-set sampling continues while discovery is blocked and stops after disposal',
  { skip: process.platform !== 'win32' }, () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'capture-sampler-selfcheck-'));
    const output = execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-File',
      path.resolve('scripts/runtime_demand_process_probe.ps1'), '-OutputRoot', root, '-SelfCheck'],
    { encoding: 'utf8', windowsHide: true, timeout: 25000 });
    assert.match(output, /independent owned working-set sampler: passed/);
    // 真实握手回归：延迟 root 登记、错误身份不回执、仅含 root 完整样本落盘后
    // 原子回执、退出后不写。
    assert.match(output, /Root first-sample receipt handshake: passed/);
    assert.match(output, /Owned per-sample census: passed/);
  });

test('zero-Runtime evidence rejects forbidden owned processes and missing real log evidence', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'capture-zero-evidence-'));
  const copy = path.join(root, 'candidate');
  const logs = path.join(copy, 'state', 'logs');
  fs.mkdirSync(logs, { recursive: true });
  const log = path.join(logs, 'winui-dev-20380119.log');
  fs.writeFileSync(log, '00:00:00.000 [INFO ] OnLaunched: profile=production shellOnly=False');
  const samples = path.join(root, 'process-samples.jsonl');
  fs.writeFileSync(samples, '');
  const starts = path.join(root, 'process-starts.jsonl');
  const writeStarts = (rows) => fs.writeFileSync(starts, rows.map(row => JSON.stringify(row)).join('\n'));
  const owner = { pid: 123, class: 'other' };
  writeStarts([owner]);
  assert.deepEqual(zeroEvidence(copy, root, 123).violations, []);
  for (const role of ['installer', 'installer-child', 'supervisor', 'python-child']) {
    writeStarts([owner, { pid: 124, class: role }]);
    assert(zeroEvidence(copy, root, 123).violations.some(message => message.includes('owned lineage')));
  }
  writeStarts([owner]);
  const writeSamples = (procs) => fs.writeFileSync(samples, JSON.stringify({ procs }) + '\n');
  for (const name of ['PYTHON.EXE', 'VibeOCR-runtime-installer.exe']) {
    writeSamples([{ pid: 124, name, creation_filetime: '134385582680742311' }]);
    assert.match(zeroEvidence(copy, root, 123).violations.join('\n'), /working-set samples recorded Installer\/Python/);
  }
  writeSamples([{ pid: 124, name: 'msedgewebview2.exe', creation_filetime: '134385582680742311' }]);
  assert.deepEqual(zeroEvidence(copy, root, 123).violations, []);
  writeStarts([]);
  assert.equal(zeroEvidence(copy, root, 123).root_pid_seen, false);
  writeStarts([owner]);
  fs.writeFileSync(log, 'Runtime Installer command starting action=list');
  assert(zeroEvidence(copy, root, 123).violations.some(message => message.includes('AppLog shows')));
  fs.writeFileSync(log, '');
  assert.equal(zeroEvidence(copy, root, 123).applog_non_empty, false);
});

test('cleanup rejects a still-live exact sampled identity without terminating sample-only PIDs',
  { skip: process.platform !== 'win32', timeout: 60000 }, async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'capture-sampled-cleanup-'));
    const samples = path.join(root, 'process-samples.jsonl');
    const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 60'],
      { windowsHide: true, stdio: 'ignore' });
    const exited = once(child, 'exit');
    exited.catch(() => {});
    const writeSample = (creation_filetime) => fs.writeFileSync(samples, JSON.stringify({
      procs: [{ pid: child.pid, name: 'pwsh.exe', creation_filetime }],
    }) + '\n');
    try {
      assert(Number.isSafeInteger(child.pid));
      const exact = execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command',
        `(Get-Process -Id ${child.pid}).StartTime.ToUniversalTime().ToFileTimeUtc().ToString()`],
      { encoding: 'utf8', windowsHide: true, timeout: 5000 }).trim();
      writeSample((BigInt(exact) + 1n).toString());
      await cleanupLineage(root);
      writeSample(exact);
      await assert.rejects(cleanupLineage(root), /Owned process from working-set samples survived cleanup/);
      assert.equal(child.exitCode, null);
      assert.equal(child.signalCode, null);
    } finally {
      if (child.exitCode === null && child.signalCode === null) child.kill();
      await exited;
    }
    await cleanupLineage(root);
  });

test('isolated tool network policy permits only local resources and loopback', () => {
  for (const url of ['https://app.vibeocr/__resource/image', 'http://127.0.0.1:1234/',
    'http://localhost:1234/', 'http://[::1]:1234/', 'blob:https://app.vibeocr/pixels', 'data:image/png;base64,AA=='])
    assert.equal(allowsLocalToolRequest(url), true, url);
  for (const url of ['https://example.com/', 'https://app.vibeocr.evil/',
    'http://app.vibeocr/', 'https://127.0.0.1.evil/', 'wss://example.com/'])
    assert.equal(allowsLocalToolRequest(url), false, url);
});
