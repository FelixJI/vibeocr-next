import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { statistics, attachWorkingSets, summarize, checkAfter } from '../../scripts/smoke_capture_first_frame.mjs';

const baselineCounts = { Subscriptions: 1, Resources: 0, AnnotationEntries: 0,
  AnnotationReservations: 0, AnnotationOwnedFiles: 0, ResourceFiles: 0, EditorWebViews: 0, TotalWebViews: 1 };
function batch(cycles) {
  return { health: { schema_version: 1, state: 'passed', callbacks_after_stop: 0,
    submit_attempts: 0, startup_ensure_attempts: 0, installer_attempts: 0, inference_attached: false,
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
    })) }, zero_evidence: { violations: [] } };
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
  const growth = structuredClone(batches);
  for (const row of growth[0].health.rows.slice(-5)) row.working_set.app_closed_median_bytes += 33 * 1024 * 1024;
  assert.throws(() => summarize(growth), /closed working set/);
  const samples = [{ at_ms: 2300, procs: [{ pid: 99, name: 'VibeOCR.WinUI.exe', ws_bytes: 10 },
    { pid: 101, name: 'msedgewebview2.exe', ws_bytes: 20, webview_role: 'browser' },
    { pid: 102, name: 'msedgewebview2.exe', ws_bytes: 30, webview_role: 'renderer' }] }];
  const attached = attachWorkingSets(batches[1].health, samples, 99);
  assert.equal(attached[0].working_set.app_closed_median_bytes, 10);
  assert.equal(attached[0].working_set.webview_closed_median_bytes, 50);
  assert.deepEqual(attached[0].working_set.webview_role_observed_peaks, { browser: 1, renderer: 1 });
  assert.throws(() => attachWorkingSets(batches[1].health, [], 99), /missing/);
  assert.throws(() => attachWorkingSets(batches[1].health, [{ ...samples[0], at_ms: 5000 }], 99), /No closed/);
  assert.throws(() => attachWorkingSets(batches[1].health, samples, 999), /working set is missing/);
}
if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  runSelfCheck();
  console.log('Capture first-frame statistics, resource rejection, working-set and frozen-budget contracts: passed');
}
