import assert from 'node:assert/strict';
import test from 'node:test';
import { validateReplacementHealth } from '../../scripts/smoke_capture_replacement.mjs';

function health() {
  const baseline = { Subscriptions: 1, Resources: 0, AnnotationEntries: 0,
    AnnotationReservations: 0, AnnotationOwnedFiles: 0, ResourceFiles: 0, EditorWebViews: 0, TotalWebViews: 1 };
  const held = { ...baseline, EditorWebViews: 1, TotalWebViews: 2 };
  return { smoke: 'capture-replacement-e2e', state: 'passed', baseline,
    submit_attempts: 0, startup_ensure_attempts: 0, installer_attempts: 0, inference_attached: false,
    renderer_failure: 'UNVERIFIED: only Reload was induced', rows: [
      { phase: 'main-reload', counts: baseline },
      { phase: 'two-replacements-held', counts: held, intermediate: 'b', latest: 'c', restorations: 0 },
      { phase: 'latest-editor-and-reload', counts: { ...held, Subscriptions: 2, Resources: 1 }, latest: 'c' },
      { phase: 'pending-cancelled', counts: baseline, restorations: 1 },
      { phase: 'shutdown-held', counts: held, restorations: 1 },
      { phase: 'shutdown-drained', counts: Object.fromEntries(Object.keys(baseline).map(key => [key, 0])), restorations: 1 },
    ] };
}

test('lifecycle evidence requires real bounded replacement and completed cleanup', () => {
  validateReplacementHealth(health());
  const overlap = health();
  overlap.rows[1].counts.EditorWebViews = 2;
  overlap.rows[1].counts.TotalWebViews = 3;
  assert.throws(() => validateReplacementHealth(overlap), /WebView bound/);
  const incomplete = health();
  incomplete.rows.pop();
  assert.throws(() => validateReplacementHealth(incomplete));
  const leak = health();
  leak.rows[5].counts.Resources = 1;
  assert.throws(() => validateReplacementHealth(leak), /Shutdown: Resources/);
});

test('failure or simulated renderer coverage cannot pass lifecycle evidence', () => {
  const failed = health();
  failed.state = 'failed';
  failed.error = 'native disposal fault';
  assert.throws(() => validateReplacementHealth(failed), /native disposal fault/);
  const simulated = health();
  simulated.renderer_failure = 'passed';
  assert.throws(() => validateReplacementHealth(simulated), /ProcessFailed/);
});
