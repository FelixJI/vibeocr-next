import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { runCaptureReplacementBatch } from './smoke_capture_first_frame.mjs';

export function validateReplacementHealth(health) {
  assert.equal(health.smoke, 'capture-replacement-e2e');
  assert.equal(health.state, 'passed', health.error ?? 'Native lifecycle failed.');
  for (const key of ['submit_attempts', 'startup_ensure_attempts', 'installer_attempts']) assert.equal(health[key], 0);
  assert.equal(health.inference_attached, false);
  assert(health.renderer_failure.startsWith('UNVERIFIED:'), 'Reload must not masquerade as ProcessFailed evidence.');
  const phases = ['main-reload', 'two-replacements-held', 'latest-editor-and-reload',
    'pending-cancelled', 'shutdown-held', 'shutdown-drained'];
  assert.deepEqual(health.rows.map(row => row.phase), phases);
  const baseline = health.baseline;
  assert.equal(baseline.EditorWebViews, 0);
  assert.equal(baseline.TotalWebViews, 1);
  assert.equal(baseline.Subscriptions, 1);
  for (const row of health.rows) {
    assert(row.counts.EditorWebViews <= 1 && row.counts.TotalWebViews <= 2, `WebView bound: ${row.phase}`);
  }
  assert.deepEqual(health.rows[0].counts, baseline);
  assert.equal(health.rows[1].counts.EditorWebViews, 1);
  assert.equal(health.rows[1].counts.Subscriptions, baseline.Subscriptions);
  assert.equal(health.rows[1].restorations, 0);
  assert.notEqual(health.rows[1].intermediate, health.rows[1].latest);
  assert.equal(health.rows[2].latest, health.rows[1].latest);
  assert.equal(health.rows[2].counts.Subscriptions, baseline.Subscriptions + 1);
  assert.equal(health.rows[2].counts.Resources, baseline.Resources + 1);
  assert.deepEqual(health.rows[3].counts, baseline);
  assert.equal(health.rows[3].restorations, 1);
  assert.equal(health.rows[4].counts.EditorWebViews, 1);
  assert.equal(health.rows[4].restorations, 1);
  assert.equal(health.rows[5].restorations, 1);
  for (const [key, value] of Object.entries(health.rows[5].counts))
    assert.equal(value, key === 'ResourceFiles' ? baseline.ResourceFiles : 0, `Shutdown: ${key}`);
}

async function main() {
  assert.equal(process.platform, 'win32', 'Real Windows desktop required.');
  function option(name) {
    const index = process.argv.indexOf(name);
    assert(index > 0 && index + 1 < process.argv.length, `Missing ${name}.`);
    return fs.realpathSync(process.argv[index + 1]);
  }
  const candidate = option('--candidate'), work = option('--work');
  function contained(root, child) {
    const relative = path.relative(root, child);
    return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
  }
  assert(!contained(candidate, work) && !contained(work, candidate), 'Candidate/Work must not nest.');
  assert(!fs.existsSync(path.join(candidate, 'state')), 'Source candidate must have no state.');
  for (const marker of ['app/VibeOCR.WinUI.exe', 'app/metadata/product-layout.json',
    'runtime/backend/runtime-manifest.json', 'runtime/installer/vibeocr-runtime-installer.exe'])
    assert(fs.statSync(path.join(candidate, marker)).isFile(), `Candidate marker missing: ${marker}`);
  const root = path.join(work, `capture216-lifecycle-${randomUUID().replaceAll('-', '')}`);
  const helpers = await import('./smoke_runtime_demand_tools.mjs');
  const record = await runCaptureReplacementBatch(candidate, root, helpers);
  try {
    assert.equal(record.state, 'passed', record.error ?? record.cleanup_error ?? record.evidence_error);
    validateReplacementHealth(record.health);
  } catch (error) { record.state = 'failed'; record.validation_error = error.stack ?? String(error); }
  fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(record, null, 2));
  console.log(JSON.stringify({ state: record.state, root }));
  if (record.state !== 'passed') process.exitCode = 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  main().catch(error => { console.error(error.stack ?? error); process.exitCode = 1; });
