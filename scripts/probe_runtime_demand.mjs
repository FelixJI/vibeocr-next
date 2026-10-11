// Task #214: normal Portable, public OCR/PDF actions, fresh state per metric.
// No shell-only/selftest, fabricated bridge state, HTTP bypass or user inputs.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { promisify } from 'node:util';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { native, windows, area, stopOwned, delay } from './smoke_native_actions.mjs';

const exec = promisify(execFile);
const scripts = path.dirname(fileURLToPath(import.meta.url));
const repo = path.dirname(scripts);
const { chromium, expect } = createRequire(path.join(repo,
  'src/dotnet/VibeOCR.App/WebAssets/package.json'))('@playwright/test');
const timeout = 30 * 60 * 1000;
const jsonLines = (file) => fs.existsSync(file)
  ? fs.readFileSync(file, 'utf8').split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
function option(name, fallback) {
  const index = process.argv.indexOf(name);
  if (index < 0) { assert(fallback !== undefined, `Required option ${name}`); return fallback; }
  assert(process.argv[index + 1], `Missing value for ${name}`);
  return process.argv[index + 1];
}
async function port() {
  const server = net.createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const value = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  return value;
}
async function waitFile(file, child, milliseconds = 20000) {
  const until = Date.now() + milliseconds;
  while (!fs.existsSync(file)) {
    assert(child.exitCode === null && child.signalCode === null, `Process exited before ${path.basename(file)}`);
    assert(Date.now() < until, `Timeout waiting for ${path.basename(file)}`);
    await delay(100);
  }
}
async function connect(child, debugPort) {
  const until = Date.now() + 30000;
  let error;
  while (Date.now() < until) {
    assert(child.exitCode === null, 'App exited before CDP connected');
    let browser;
    try {
      browser = await chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`, { timeout: 1000 });
      const context = browser.contexts()[0];
      assert(context, 'CDP context missing');
      const page = context.pages().find((item) => item.url().startsWith('https://app.vibeocr/index.html'))
        ?? await context.waitForEvent('page', { timeout: 5000 });
      await page.waitForURL(/^https:\/\/app\.vibeocr\/index\.html/, { timeout: 5000 });
      await page.getByRole('link', { name: '单次识别', exact: true }).waitFor();
      return { browser, page };
    } catch (cause) {
      error = cause;
      if (browser) await browser.close();
      await delay(200);
    }
  }
  throw error;
}
async function observe(page) {
  await page.evaluate(() => {
    // Passive evidence only: this listener never sends commands or edits app state.
    window.__runtimeDemandEvidence = [];
    window.__runtimeDemandClick = null;
    document.addEventListener('click', (event) => {
      if (event.isTrusted && event.target.closest('button')) window.__runtimeDemandClick = Date.now();
    }, true);
    window.chrome.webview.addEventListener('message', (event) => {
      const envelope = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
      if (envelope.kind !== 'event' || envelope.type !== 'app.state') return;
      const { scope, state, revision } = envelope.payload;
      if (!['recognition', 'pdf'].includes(scope)) return;
      window.__runtimeDemandEvidence.push({ at_ms: Date.now(), scope, revision,
        isBusy: state.isBusy, statusCode: state.statusCode, result: state.result?.url,
        sessionId: state.sessionId, pageCount: state.pageCount });
    });
  });
}
async function measure(page, metric, app, main, fixture, iteration, t0) {
  const offset = await page.evaluate(() => window.__runtimeDemandEvidence.length);
  const previousResult = await page.evaluate(() => window.__runtimeDemandEvidence.findLast((row) => row.scope === 'recognition' && row.result)?.result);
  await page.evaluate(() => { window.__runtimeDemandClick = null; });
  let sessionId;
  let text;
  if (metric === 'ocr') {
    await page.getByRole('button', { name: '从剪贴板', exact: true }).click();
    await page.waitForFunction(({ start, previous }) => window.__runtimeDemandEvidence.slice(start)
      .some((row) => row.scope === 'recognition' && !row.isBusy &&
        ((row.result && row.result !== previous) || ['recognition.failed', 'recognition.cancelled'].includes(row.statusCode))),
    { start: offset, previous: previousResult }, { timeout });
    const failed = await page.evaluate((start) => window.__runtimeDemandEvidence.slice(start)
      .findLast((row) => ['recognition.failed', 'recognition.cancelled'].includes(row.statusCode)), offset);
    assert(!failed, `OCR terminal failure: ${failed?.statusCode}`);
    await expect(page.locator('.result-document')).toContainText(/VibeOCR\s*123/, { timeout });
    text = await page.locator('.result-document').textContent();
  } else {
    await page.getByRole('button', { name: '打开 PDF', exact: true }).click();
    await native('codes-open-file', { Handle: main.Handle, AppPid: app.pid, FilePath: fixture }, 20000);
    await page.waitForFunction((start) => window.__runtimeDemandEvidence.slice(start)
      .some((row) => row.scope === 'pdf' && !row.isBusy &&
        ((row.sessionId && row.pageCount === 1) || ['pdf.failed', 'pdf.cancelled'].includes(row.statusCode))), offset, { timeout });
    const failed = await page.evaluate((start) => window.__runtimeDemandEvidence.slice(start)
      .findLast((row) => ['pdf.failed', 'pdf.cancelled'].includes(row.statusCode)), offset);
    assert(!failed, `PDF terminal failure: ${failed?.statusCode}`);
    const image = page.getByRole('img', { name: '第 1 页缩略图', exact: true });
    await image.waitFor({ timeout });
    await expect(image).toHaveJSProperty('complete', true, { timeout });
    assert(await image.evaluate((element) => element.naturalWidth > 0 && element.naturalHeight > 0), 'Actual PDF thumbnail failed to render');
    sessionId = await page.evaluate((start) => window.__runtimeDemandEvidence.slice(start)
      .findLast((row) => row.scope === 'pdf' && row.sessionId)?.sessionId, offset);
  }
  const resultAt = Date.now();
  const clickedAt = await page.evaluate(() => window.__runtimeDemandClick);
  assert(Number.isSafeInteger(clickedAt), 'Trusted public demand click was not observed');
  return { iteration, t_demand_ms: clickedAt, t_result_ms: resultAt,
    t0_to_result_ms: resultAt - t0, demand_to_result_ms: resultAt - clickedAt,
    text, session_id: sessionId,
    states: await page.evaluate((start) => window.__runtimeDemandEvidence.slice(start), offset) };
}
async function cleanupLineage(root) {
  const records = jsonLines(path.join(root, 'process-starts.jsonl'))
    .filter((row) => row.creation_filetime !== null);
  for (const row of records) assert(Number.isSafeInteger(row.pid) && /^\d+$/.test(row.creation_filetime));
  // Match observed creation time before terminating each owned survivor; no PID-tree guess after exit.
  const entries = records.map((row) => `'${row.pid}|${row.creation_filetime}'=$true`).join(';');
  const command = `$ErrorActionPreference='Stop';$owned=@{${entries}};` +
    `$live=Get-CimInstance Win32_Process;foreach($p in $live){$key=[string]$p.ProcessId+'|'+[string]$p.CreationDate.ToUniversalTime().ToFileTimeUtc();if($owned.ContainsKey($key)){Stop-Process -Id $p.ProcessId -ErrorAction SilentlyContinue}};` +
    `Start-Sleep -Milliseconds 500;$live=Get-CimInstance Win32_Process;foreach($p in $live){$key=[string]$p.ProcessId+'|'+[string]$p.CreationDate.ToUniversalTime().ToFileTimeUtc();if($owned.ContainsKey($key)){throw 'Owned process survived cleanup'}}`;
  await exec('pwsh', ['-NoProfile', '-NonInteractive', '-Command', command], { windowsHide: true, timeout: 20000 });
}
async function monitor(root) {
  const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File',
    path.join(scripts, 'runtime_demand_process_probe.ps1'), '-OutputRoot', root], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', (chunk) => { stderr += chunk; });
  const finished = once(child, 'exit').then(([code]) => { assert.equal(code, 0, `Process monitor failed: ${stderr}`); });
  // Attach a handler now, while readiness is being awaited.
  finished.catch(() => {});
  try { await waitFile(path.join(root, 'monitor-ready'), child); }
  catch (error) { await finished; throw error; }
  return { child, finished };
}
async function run(candidate, series, metric, retryBaselineCancellation) {
  const root = path.join(series, metric);
  fs.mkdirSync(root);
  const record = { metric, valid: false, root, observations: [], failed_attempts: [] };
  let watch;
  let app;
  let browser;
  let page;
  try {
    await exec('uv', ['run', '--frozen', 'python', path.join(scripts, 'runtime_demand_fixtures.py'), '--out', path.join(root, 'inputs')], { cwd: repo, timeout: 30000, windowsHide: true });
    const copy = path.join(root, 'candidate');
    await exec('robocopy', [candidate, copy, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP'], { timeout, windowsHide: true })
      .catch((error) => { if (!Number.isInteger(error.code) || error.code < 1 || error.code > 7) throw error; });
    assert(!fs.existsSync(path.join(copy, 'state')), 'Copied candidate already has state');
    const png = path.join(root, 'inputs', 'vibeocr-123.png');
    if (metric === 'ocr') await exec('pwsh', ['-NoProfile', '-NonInteractive', '-STA', '-File',
      path.join(scripts, 'native_codes_clipboard_fixture.ps1'), '-SmokeRoot', root, '-ImagePath', png], { windowsHide: true, timeout: 20000 });
    const debugPort = await port();
    record.debug_port = debugPort;
    watch = await monitor(root);
    const env = { ...process.env, WEBVIEW2_USER_DATA_FOLDER: path.join(root, 'webview'),
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${debugPort} --remote-debugging-address=127.0.0.1`,
      VIBEOCR_SUPERVISOR_HEALTH_TRACE: path.join(root, 'health.jsonl') };
    for (const name of Object.keys(env)) if (name.startsWith('VIBEOCR_SELF_TEST') || name.startsWith('VIBEOCR_SOAK')) delete env[name];
    const executable = path.join(copy, 'app', 'VibeOCR.WinUI.exe');
    const t0 = Date.now();
    record.t0_ms = t0;
    app = spawn(executable, ['--profile', 'production', '--install-root', copy], { cwd: path.dirname(executable), env, stdio: 'ignore' });
    assert(Number.isSafeInteger(app.pid), 'App spawn failed');
    fs.writeFileSync(path.join(root, 'root-pid.txt'), String(app.pid));
    const connected = await connect(app, debugPort);
    browser = connected.browser;
    page = connected.page;
    await observe(page);
    await page.getByRole('link', { name: metric === 'ocr' ? '单次识别' : 'PDF', exact: true }).click();
    const main = (await windows(app.pid)).filter((item) => item.Visible && area(item) > 20000).sort((a, b) => area(b) - area(a))[0];
    assert(main, 'Owned main window missing');
    for (let iteration = 0; iteration < 3; iteration++) {
      if (metric === 'pdf' && iteration) {
        await page.getByRole('button', { name: '关闭文档', exact: true }).click();
        await page.getByRole('button', { name: '打开 PDF', exact: true }).waitFor();
      }
      let observation;
      try {
        observation = await measure(page, metric, app, main,
          path.join(root, 'inputs', 'vibeocr-123.pdf'), iteration, t0);
      } catch (error) {
        // The old baseline invalidates the first cold OCR catalog. Preserve that
        // failure; this explicit option permits one public retry after Ready only.
        if (!retryBaselineCancellation || metric !== 'ocr' || iteration !== 0 ||
          error.message !== 'OCR terminal failure: recognition.cancelled') throw error;
        record.failed_attempts.push({ error: error.message, failure_at_ms: Date.now(),
          t_demand_ms: await page.evaluate(() => window.__runtimeDemandClick),
          states: await page.evaluate(() => window.__runtimeDemandEvidence) });
        record.first_request_succeeded = false;
        fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(record, null, 2));
        const until = Date.now() + 90000;
        while (!jsonLines(path.join(root, 'health.jsonl')).some((row) => row.state === 'Ready')) {
          assert(app.exitCode === null, 'App exited before baseline retry');
          assert(Date.now() < until, 'Supervisor did not become Ready for baseline retry');
          await delay(100);
        }
        observation = await measure(page, metric, app, main,
          path.join(root, 'inputs', 'vibeocr-123.pdf'), iteration, t0);
        observation.baseline_retry = true;
        observation.initial_demand_to_result_ms = observation.t_result_ms -
          record.failed_attempts[0].t_demand_ms;
      }
      if (metric === 'pdf') assert(!record.observations.some((item) => item.session_id === observation.session_id), 'Warm PDF open reused an old document instead of opening it');
      record.observations.push(observation);
      fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(record, null, 2));
    }
    const coldEnd = record.observations[0].t_result_ms;
    const starts = jsonLines(path.join(root, 'process-starts.jsonl'));
    const warmStarts = starts.filter((entry) => entry.started_ms > coldEnd);
    assert(!warmStarts.some((entry) => entry.class === 'supervisor'), 'Warm task restarted Supervisor');
    assert(!warmStarts.some((entry) => entry.class === 'installer' &&
      (!entry.action || /install|repair|ensure|initialize_default/.test(entry.action))), 'Warm task installed/repaired or has unknown Installer evidence');
    const ready = jsonLines(path.join(root, 'health.jsonl')).filter((entry) => entry.state === 'Ready');
    assert.equal(new Set(ready.map((entry) => entry.instance_id)).size, 1, 'Missing health evidence or Supervisor session changed');
    record.functional_valid = true;
    record.first_request_succeeded ??= true;
    record.valid = true;
  } catch (error) {
    record.error = error.stderr || error.message;
    record.failure_at_ms = Date.now();
    if (page) record.failed_states = await page.evaluate(() => window.__runtimeDemandEvidence).catch(() => []);
  } finally {
    try {
      if (browser) await browser.close();
      if (app) await stopOwned(app, 'quit', {});
      if (watch) {
        fs.writeFileSync(path.join(root, 'stop-monitor'), 'stop');
        await Promise.race([watch.finished, delay(15000).then(() => { throw new Error('Process monitor did not finish'); })]);
        await cleanupLineage(root);
      }
    } catch (error) {
      record.valid = false;
      record.error = `${record.error ?? ''}; cleanup/evidence: ${error.stderr || error.message}`;
      record.cleanup_error = { code: error.code, killed: error.killed, signal: error.signal };
      if (watch) await stopOwned(watch.child, 'quit', {}).catch(() => {});
    }
    record.process_starts = jsonLines(path.join(root, 'process-starts.jsonl'));
    record.evidence_gaps = record.process_starts.filter((entry) => entry.evidence_gap).map((entry) => ({ pid: entry.pid, gap: entry.evidence_gap }));
    if (fs.existsSync(path.join(root, 'monitor-info.json'))) {
      record.monitor = JSON.parse(fs.readFileSync(path.join(root, 'monitor-info.json'), 'utf8'));
      record.evidence_gaps.push({ gap: record.monitor.evidence_gap });
    }
    record.process_samples = jsonLines(path.join(root, 'process-samples.jsonl'));
    record.health = jsonLines(path.join(root, 'health.jsonl'));
    record.install_actions = record.process_starts.filter((entry) => entry.class === 'installer').map((entry) => entry.action);
    record.log_directory = path.join(root, 'candidate', 'state', 'logs');
    fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(record, null, 2));
  }
  return record;
}
async function main() {
  assert.equal(process.platform, 'win32', 'Windows desktop required');
  if (process.argv.includes('--self-check')) {
    const root = path.join(repo, '.test-results', `runtime-demand-self-check-${randomUUID()}`);
    fs.mkdirSync(root, { recursive: true });
    await exec('uv', ['run', '--frozen', 'python', path.join(scripts, 'runtime_demand_fixtures.py'), '--out', path.join(root, 'inputs')], { cwd: repo, windowsHide: true });
    await exec('pwsh', ['-NoProfile', '-NonInteractive', '-File', path.join(scripts, 'runtime_demand_process_probe.ps1'), '-OutputRoot', root, '-SelfCheck'], { windowsHide: true });
    const watch = await monitor(root);
    const child = spawn(process.execPath, ['-e',
      'require("node:child_process").spawn(process.execPath,["-e","setInterval(()=>{},1000)"],{stdio:"ignore",windowsHide:true});setInterval(()=>{},1000)'],
    { windowsHide: true, stdio: 'ignore' });
    fs.writeFileSync(path.join(root, 'root-pid.txt'), String(child.pid));
    try {
      const until = Date.now() + 10000;
      while (jsonLines(path.join(root, 'process-starts.jsonl')).length < 2) {
        assert(watch.child.exitCode === null, 'Process monitor failed during self-check');
        assert(Date.now() < until, 'Owned parent/child starts were not observed');
        await delay(200);
      }
      const starts = jsonLines(path.join(root, 'process-starts.jsonl'));
      assert(starts.some((row) => row.pid === child.pid));
      assert(starts.some((row) => row.ppid === child.pid));
      await waitFile(path.join(root, 'process-samples.jsonl'), watch.child);
      assert(jsonLines(path.join(root, 'process-samples.jsonl')).some((sample) => sample.procs.length > 0));
    } finally {
      await stopOwned(child, 'quit', {});
      fs.writeFileSync(path.join(root, 'stop-monitor'), 'stop');
      try { await watch.finished; } finally { await cleanupLineage(root); }
    }
    console.log(`No-GUI fixtures, Windows argv, parent/child process capture and owned cleanup: passed (${root})`);
    return;
  }
  const candidate = path.resolve(option('--candidate'));
  assert(fs.existsSync(path.join(candidate, 'app', 'VibeOCR.WinUI.exe')), 'Candidate App missing');
  assert(!fs.existsSync(path.join(candidate, 'state')), 'Source candidate has state');
  const mode = option('--mode', 'both');
  assert(['ocr', 'pdf', 'both'].includes(mode), 'Mode must be ocr/pdf/both');
  const series = path.join(path.resolve(option('--work')), randomUUID());
  fs.mkdirSync(series, { recursive: true });
  const results = [];
  for (const metric of mode === 'both' ? ['ocr', 'pdf'] : [mode]) {
    const record = await run(candidate, series, metric,
      process.argv.includes('--baseline-retry-first-ocr-cancel'));
    results.push(record);
    console.log(JSON.stringify({ metric, valid: record.valid, error: record.error,
      first_request_succeeded: record.first_request_succeeded, failed_attempts: record.failed_attempts,
      timings: record.observations.map((item) => ({ t0_ms: item.t0_to_result_ms,
        demand_ms: item.demand_to_result_ms, baseline_retry: item.baseline_retry,
        initial_demand_to_result_ms: item.initial_demand_to_result_ms })),
      evidence_gaps: record.evidence_gaps, root: record.root }));
  }
  fs.writeFileSync(path.join(series, 'results.json'), JSON.stringify(results, null, 2));
  process.exitCode = results.every((record) => record.valid) ? 0 : 1;
}
if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  main().catch((error) => { console.error(error); process.exitCode = 1; });
}
