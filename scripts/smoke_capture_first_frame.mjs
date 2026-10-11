// #216 CP1: identical observation seam before/after; 3 fresh App/UDF batches,
// 20+1+1 real native editor cycles. No OCR, browser pool or renderer prewarming.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const read = (file) => JSON.parse(fs.readFileSync(file, 'utf8'));
const lines = (file) => fs.readFileSync(file, 'utf8').split(/\r?\n/).filter(Boolean).map(JSON.parse);
const mib = 1024 * 1024;

export function statistics(values) {
  assert(values.length > 0, 'Measurement samples are missing.');
  assert(values.every((value) => Number.isFinite(value) && value >= 0), 'Invalid measurement value.');
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return { count: sorted.length,
    median: sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2,
    p95: sorted[Math.ceil(sorted.length * 0.95) - 1], max: sorted.at(-1) };
}

export function attachWorkingSets(health, samples, appPid) {
  assert(samples.length, 'Owned process samples are missing.');
  return health.rows.map((row) => {
    const closed = samples.filter((sample) => sample.at_ms >= row.closed_at_ms &&
      sample.at_ms <= row.settled_at_ms);
    assert(closed.length, `No closed working-set sample for cycle ${row.cycle}.`);
    const app = [], webview = [];
    for (const sample of closed) {
      const ownApp = sample.procs.find((proc) => proc.pid === appPid);
      const web = sample.procs.filter((proc) => proc.name.toLowerCase() === 'msedgewebview2.exe');
      assert(ownApp && web.length, 'App/shared WebView working set is missing.');
      app.push(ownApp.ws_bytes);
      webview.push(web.reduce((sum, proc) => sum + proc.ws_bytes, 0));
    }
    const active = samples.filter((sample) => sample.at_ms >= row.command_at_ms &&
      sample.at_ms <= row.settled_at_ms);
    const roles = {};
    const appPeak = [], webPeak = [];
    for (const sample of active) {
      const counts = {};
      const ownApp = sample.procs.find((proc) => proc.pid === appPid);
      assert(ownApp, 'App peak working-set evidence missing.');
      appPeak.push(ownApp.ws_bytes);
      webPeak.push(sample.procs.filter((proc) => proc.name.toLowerCase() === 'msedgewebview2.exe')
        .reduce((sum, proc) => sum + proc.ws_bytes, 0));
      for (const proc of sample.procs.filter((proc) => proc.name.toLowerCase() === 'msedgewebview2.exe')) {
        const role = proc.webview_role ?? 'unknown';
        counts[role] = (counts[role] ?? 0) + 1;
      }
      for (const [role, count] of Object.entries(counts)) roles[role] = Math.max(roles[role] ?? 0, count);
    }
    return { ...row, working_set: { closed_sample_count: closed.length,
      app_closed_median_bytes: statistics(app).median,
      webview_closed_median_bytes: statistics(webview).median,
      app_observed_peak_bytes: statistics(appPeak).max, webview_observed_peak_bytes: statistics(webPeak).max,
      webview_role_observed_peaks: roles } };
  });
}

export function summarize(batches) {
  assert.equal(batches.length, 3, 'Expected exactly 3 cold batches.');
  const expectedCycles = [20, 1, 1];
  for (const [index, batch] of batches.entries()) {
    assert.equal(batch.health.schema_version, 1);
    assert.equal(batch.state, 'passed', batch.error ?? batch.cleanup_error ?? batch.evidence_error ?? 'Invalid batch evidence.');
    assert.equal(batch.health.state, 'passed', batch.health.error ?? 'Native measurement failed.');
    assert.equal(batch.health.rows.length, expectedCycles[index]);
    assert.equal(batch.health.callbacks_after_stop, 0);
    assert.equal(batch.health.submit_attempts, 0);
    assert.equal(batch.health.startup_ensure_attempts, 0);
    assert.equal(batch.health.installer_attempts, 0);
    assert.equal(batch.health.inference_attached, false);
    assert.equal(batch.zero_evidence.violations.length, 0, JSON.stringify(batch.zero_evidence.violations));
    for (const [cycle, row] of batch.health.rows.entries()) {
      assert.equal(row.cycle, cycle + 1);
      assert.equal(row.temperature, cycle === 0 ? 'cold' : 'warm');
      assert.deepEqual(row.before, batch.health.baseline);
      assert.deepEqual(row.closed, batch.health.baseline);
      assert.equal(row.ready.Subscriptions, row.before.Subscriptions + 1);
      assert.equal(row.ready.EditorWebViews, 1);
      assert.equal(row.ready.TotalWebViews, 2);
      assert.equal(row.ready.Resources, row.before.Resources + 1);
      assert(row.dispose_ms <= 5000, 'Dispose exceeded 5s.');
      assert(row.capture.Width > 0 && row.capture.Height > 0 &&
        row.capture.WhitePixels >= 10000 && row.capture.DarkPixels >= 100);
      assert(row.dispatch_lag_ms.length, 'UI dispatch samples are missing.');
      for (const metric of ['capture_to_presented_ms', 'capture_complete_to_presented_ms',
        'editor_to_presented_ms', 'command_to_presented_ms', 'dispose_ms']) statistics([row[metric]]);
      assert(Number.isFinite(row.working_set?.app_closed_median_bytes) &&
        Number.isFinite(row.working_set?.webview_closed_median_bytes), 'Working set evidence missing.');
    }
  }
  const rows = batches.flatMap((batch) => batch.health.rows);
  const firstCapture = rows[0].capture;
  assert(rows.every((row) => row.capture.Width === firstCapture.Width && row.capture.Height === firstCapture.Height),
    'Capture dimensions changed between inputs.');
  const cold = statistics(rows.filter((row) => row.temperature === 'cold').map((row) => row.capture_to_presented_ms));
  const warm = statistics(rows.filter((row) => row.temperature === 'warm').map((row) => row.capture_to_presented_ms));
  const dispatch = statistics([...rows.flatMap((row) => row.dispatch_lag_ms),
    ...batches.flatMap((batch) => batch.health.dispatch_tail_ms ?? [])]);
  const twenty = batches[0].health.rows;
  const closedGrowth = {};
  const gateViolations = [];
  for (const [name, field] of Object.entries({ app: 'app_closed_median_bytes', webview: 'webview_closed_median_bytes' })) {
    const early = statistics(twenty.slice(1, 5).map((row) => row.working_set[field])).median;
    const late = statistics(twenty.slice(-5).map((row) => row.working_set[field])).median;
    const allowance = Math.max(32 * mib, early * 0.1);
    const passed = late - early <= allowance;
    closedGrowth[name] = { early_median_bytes: early, late_median_bytes: late,
      growth_bytes: late - early, allowance_bytes: allowance, passed };
    if (!passed) gateViolations.push(`${name} closed working set growth ${late - early} bytes exceeds ${allowance} bytes.`);
  }
  return { row_count: rows.length,
    cold: { count: cold.count, median_ms: cold.median, max_ms: cold.max },
    warm: { count: warm.count, median_ms: warm.median, p95_ms: warm.p95, max_ms: warm.max },
    ui_dispatch_lag: { count: dispatch.count, median_ms: dispatch.median, p95_ms: dispatch.p95, max_ms: dispatch.max },
    closed_working_set_growth: closedGrowth, gate_violations: gateViolations,
    working_set_observed_peaks: { app_bytes: Math.max(...rows.map((row) => row.working_set.app_observed_peak_bytes)),
      webview_bytes: Math.max(...rows.map((row) => row.working_set.webview_observed_peak_bytes)) },
    frozen_after_budgets: { cold_median_ms: cold.median * 1.1 + 16,
      warm_p95_ms: warm.p95 * 1.1 + 16, ui_dispatch_p95_ms: Math.max(dispatch.p95 * 1.1 + 16, 32) },
  };
}

function frozenBudgets(summary) {
  const budgets = summary?.frozen_after_budgets;
  assert(budgets && Object.values(budgets).every((value) => Number.isFinite(value) && value >= 0),
    'Frozen before budgets missing.');
  assert.deepEqual(Object.keys(budgets).sort(), ['cold_median_ms', 'ui_dispatch_p95_ms', 'warm_p95_ms']);
  return budgets;
}

export function validateBaseline(before) {
  assert.equal(before.schema_version, 1, 'Invalid before schema.');
  assert.equal(before.measurement_complete, true, 'Before observations are incomplete.');
  const observed = summarize(before.batches);
  for (const field of ['row_count', 'cold', 'warm', 'ui_dispatch_lag', 'closed_working_set_growth',
    'working_set_observed_peaks', 'gate_violations'])
    assert.deepEqual(before.summary?.[field], observed[field], `Before ${field} differs from raw observations.`);
  frozenBudgets(before.summary);
  return before.summary;
}

export function checkAfter(summary, baseline) {
  const budgets = frozenBudgets(baseline);
  summary.frozen_after_budgets = budgets;
  summary.performance_comparison = {};
  for (const [label, metric, observed] of [
    ['Cold median', 'cold_median_ms', summary.cold.median_ms],
    ['Warm p95', 'warm_p95_ms', summary.warm.p95_ms],
    ['UI dispatch p95', 'ui_dispatch_p95_ms', summary.ui_dispatch_lag.p95_ms],
  ]) {
    const passed = observed <= budgets[metric];
    summary.performance_comparison[metric] = { observed_ms: observed, budget_ms: budgets[metric], passed };
    if (!passed) summary.gate_violations.push(`${label} ${observed}ms exceeds frozen before budget ${budgets[metric]}ms.`);
  }
  assert.equal(summary.gate_violations.length, 0, summary.gate_violations.join('; '));
  return budgets;
}

function contained(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}
function option(name, required = true) {
  const index = process.argv.indexOf(name);
  if (index < 0 && !required) return null;
  assert(index > 0 && index + 1 < process.argv.length, `Missing ${name}.`);
  return fs.realpathSync(process.argv[index + 1]);
}

async function batchRun(candidate, root, cycleCount, helpers, replacement = false) {
  fs.mkdirSync(root);
  const copy = path.join(root, 'candidate');
  const record = { root, app_pid: null, health: null, state: 'failed' };
  let child, watch, exit, deadline;
  try {
    await exec('robocopy', [candidate, copy, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP'],
      { timeout: 30 * 60 * 1000, windowsHide: true }).catch((error) => {
      if (!Number.isInteger(error.code) || error.code < 1 || error.code > 7) throw error;
    });
    assert(!fs.existsSync(path.join(copy, 'state')), 'Copied candidate has prior state.');
    watch = await helpers.monitor(root);
    const env = { ...process.env };
    for (const key of Object.keys(env))
      if (key.startsWith('VIBEOCR_SELF_TEST') || key.startsWith('VIBEOCR_SOAK') ||
          key.startsWith('VIBEOCR_CAPTURE_FIRST_FRAME') || key.startsWith('VIBEOCR_CAPTURE_REPLACEMENT') || key === 'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS') delete env[key];
    Object.assign(env, { VIBEOCR_SELF_TEST_SMOKE: replacement ? 'capture-replacement-e2e' : 'capture-first-frame-e2e',
      VIBEOCR_SELF_TEST_INSTANCE: randomUUID().replaceAll('-', ''),
      VIBEOCR_CAPTURE_FIRST_FRAME_CYCLES: String(cycleCount),
      VIBEOCR_CAPTURE_FIRST_FRAME_HEALTH: path.join(root, 'capture-first-frame-health.json'),
      WEBVIEW2_USER_DATA_FOLDER: path.join(root, 'webview') });
    if (replacement) {
      delete env.VIBEOCR_CAPTURE_FIRST_FRAME_CYCLES;
      delete env.VIBEOCR_CAPTURE_FIRST_FRAME_HEALTH;
      env.VIBEOCR_CAPTURE_REPLACEMENT_HEALTH = path.join(root, 'capture-replacement-health.json');
    }
    const executable = path.join(copy, 'app', 'VibeOCR.WinUI.exe');
    child = spawn(executable, ['--profile', 'production', '--install-root', copy],
      { cwd: path.dirname(executable), env, windowsHide: true, stdio: 'ignore' });
    assert(Number.isSafeInteger(child.pid), 'App did not spawn.');
    record.app_pid = child.pid;
    exit = once(child, 'exit');
    exit.catch(() => {});
    fs.writeFileSync(path.join(root, 'root-pid.txt'), String(child.pid));
    const timeout = new Promise((_, reject) => {
      deadline = setTimeout(() => reject(new Error('Capture batch exceeded 180s.')), 180000);
      deadline.unref();
    });
    await Promise.race([exit, timeout]);
    assert.equal(child.exitCode, 0, 'App exit failed.');
    record.health = read(path.join(root, replacement ? 'capture-replacement-health.json' : 'capture-first-frame-health.json'));
    assert.equal(record.health.state, 'passed', record.health.error ?? 'Native capture failed.');
    if (!replacement) assert(fs.statSync(path.join(root, 'capture-first-frame-preview.png')).size > 100, 'Real WebView preview missing.');
  } catch (error) { record.error = error.stack ?? String(error); }
  finally {
    clearTimeout(deadline);
    if (child && child.exitCode === null && child.signalCode === null) {
      try {
        const { stopOwned } = await import('./smoke_native_actions.mjs');
        await stopOwned(child, 'quit', {});
      } catch (error) { record.cleanup_error = error.stack ?? String(error); }
    }
    if (watch) {
      try {
        fs.writeFileSync(path.join(root, 'stop-monitor'), 'stop');
        let monitorDeadline;
        try {
          await Promise.race([watch.finished, new Promise((_, reject) => {
            monitorDeadline = setTimeout(() => reject(new Error('Process monitor did not stop within 15s.')), 15000);
            monitorDeadline.unref();
          })]);
        } finally { clearTimeout(monitorDeadline); }
        await helpers.cleanupLineage(root);
      } catch (error) { record.cleanup_error = error.stack ?? String(error); }
    }
    try {
      record.zero_evidence = helpers.zeroEvidence(copy, root, record.app_pid);
      assert.equal(record.zero_evidence.violations.length, 0, JSON.stringify(record.zero_evidence.violations));
      record.monitor = read(path.join(root, 'monitor-info.json'));
      if (record.health && !replacement) record.health.rows = attachWorkingSets(record.health,
        lines(path.join(root, 'process-samples.jsonl')), record.app_pid);
      if (!record.error && !record.cleanup_error && record.health?.state === 'passed') record.state = 'passed';
    } catch (error) { record.evidence_error = error.stack ?? String(error); }
    fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(record, null, 2));
  }
  return record;
}

// The independent lifecycle entry reuses owned launch/monitor/cleanup, without
// joining the frozen 22-row timing or working-set performance comparison.
export async function runCaptureReplacementBatch(candidate, root, helpers) {
  return batchRun(candidate, root, 1, helpers, true);
}

async function main() {
  if (process.argv.includes('--self-check')) {
    const { runSelfCheck } = await import('../tests/node/smoke_capture_first_frame.test.mjs');
    runSelfCheck();
    console.log('Capture first-frame measurement self-check: passed');
    return;
  }
  assert.equal(process.platform, 'win32', 'Real Windows desktop required.');
  const candidate = option('--candidate'), work = option('--work');
  const baselinePath = option('--baseline', false);
  assert(!contained(candidate, work) && !contained(work, candidate), 'Candidate/Work must not nest.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
    'runtime/backend/runtime-manifest.json', 'runtime/installer/vibeocr-runtime-installer.exe'])
    assert(fs.statSync(path.join(candidate, marker)).isFile(), `Candidate marker missing: ${marker}`);
  assert(!fs.existsSync(path.join(candidate, 'state')), 'Source candidate must have no state.');
  const helpers = await import('./smoke_runtime_demand_tools.mjs');
  const root = path.join(work, `capture216-${randomUUID().replaceAll('-', '')}`);
  fs.mkdirSync(root);
  const record = { schema_version: 1, issue: 216, state: 'failed', measurement_complete: false, root,
    source_identity: read(path.join(candidate, 'app/metadata/component-identities.json')).project,
    batches: [], coverage_gaps: ['Only this physical monitor/DPI; mixed DPI and negative-coordinate secondary displays UNVERIFIED.'],
    limitations: ['CIM intervals are nominal 250ms plus query time; short-lived processes can be missed.',
      'WebView working set sums include shared pages counted by multiple processes.',
      'Cold n=3; max/median only. Warm n=19; nearest-rank p95 and complete raw rows.',
      'UI max dispatch lag is reported for investigation; it is not attributed to a function.'] };
  try {
    for (const [index, cycles] of [20, 1, 1].entries()) {
      const batch = await batchRun(candidate, path.join(root, `batch-${index + 1}`), cycles, helpers);
      record.batches.push(batch);
      assert.equal(batch.state, 'passed', batch.error ?? batch.cleanup_error ?? batch.evidence_error);
    }
    record.summary = summarize(record.batches);
    record.measurement_complete = true;
    if (baselinePath) {
      const before = validateBaseline(read(baselinePath));
      record.baseline = baselinePath;
      checkAfter(record.summary, before);
    }
    assert.equal(record.summary.gate_violations.length, 0, record.summary.gate_violations.join('; '));
    record.state = 'passed';
  } catch (error) { record.error = error.stack ?? String(error); }
  fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(record, null, 2));
  console.log(JSON.stringify({ state: record.state, root, summary: record.summary, error: record.error }));
  process.exitCode = record.state === 'passed' ? 0 : 1;
}
if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url))
  main().catch((error) => { console.error(error); process.exitCode = 1; });
