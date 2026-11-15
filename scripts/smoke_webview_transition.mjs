// Requires exclusive desktop authorization; only synthetic Portable installations.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { launchApp, closeAppNormally, stopOwned, delay, raceDeadline } from './smoke_native_actions.mjs';

const execFileAsync = promisify(execFile);

export function browserUserData(commandLine) {
  const matches = [...commandLine.matchAll(/(?:^|\s)--user-data-dir=(?:"([^"]+)"|([^\s]+))/g)];
  assert.equal(matches.length, 1, 'Browser must have one observable user-data directory.');
  return matches[0][1] ?? matches[0][2];
}

export function verifyProfile(phase, actual, nonce) {
  assert(['before', 'after', 'rollback'].includes(phase));
  if (phase === 'before' || phase === 'after') {
    assert.equal(actual.nonce, null, 'Fresh browser profile unexpectedly contains an old nonce.');
  } else {
    assert.equal(actual.nonce, nonce, 'Rollback did not recover the original browser profile.');
    assert.equal(actual.collapsed, 'true', 'Rollback lost the original browser preference.');
  }
}

export function verifyEvergreenImage(image, version) {
  const normalized = image.replaceAll('\\', '/');
  assert(/\/Microsoft\/EdgeWebView\/Application\/\d+\.\d+\.\d+\.\d+\/msedgewebview2\.exe$/i.test(normalized),
    'Historical browser must come from an installed Evergreen runtime.');
  assert.equal(normalized.split('/').at(-2), version);
}

export async function beforeDeadline(operation, deadline) {
  const result = await Promise.race([
    operation.then(value => ({ value })), deadline.promise,
  ]);
  assert(result, 'WebView transition observation deadline exceeded.');
  return result.value;
}

async function processInfo(processId) {
  assert(Number.isSafeInteger(processId) && processId > 0);
  const { stdout } = await execFileAsync('pwsh', ['-NoProfile', '-NonInteractive', '-Command',
    `Get-CimInstance Win32_Process -Filter 'ProcessId = ${processId}' | ` +
    'Select-Object ProcessId,ExecutablePath,CommandLine,CreationDate | ConvertTo-Json -Compress'],
  { timeout: 15000, windowsHide: true });
  return stdout.trim() ? JSON.parse(stdout) : null;
}

function option(name) {
  const index = process.argv.indexOf(name);
  assert(index > 0 && process.argv[index + 1], `Missing ${name}.`);
  return process.argv[index + 1];
}

async function main() {
  const root = fs.realpathSync(option('--install-root'));
  const evidenceRoot = fs.realpathSync(option('--evidence-dir'));
  const phase = option('--phase');
  const nonce = option('--nonce');
  const version = option('--runtime-version');
  assert.match(nonce, /^[a-f0-9]{32}$/);
  assert.match(version, /^\d+\.\d+\.\d+\.\d+$/);
  assert(['before', 'after', 'rollback'].includes(phase));
  // The harness owns this newly extracted tree; never accept an arbitrary installed app.
  assert.equal(path.basename(root), 'installed-old');
  assert(fs.existsSync(path.join(root, '.portable')));
  const current = path.join(root, 'current');
  const identity = JSON.parse(fs.readFileSync(
    path.join(current, 'app', 'metadata', 'component-identities.json'), 'utf8'));
  const source = identity.project.source_sha;
  assert.match(source, /^[a-f0-9]{40}$/);
  const before = phase === 'before' ? null : JSON.parse(
    fs.readFileSync(path.join(evidenceRoot, 'before.json'), 'utf8'));
  if (phase === 'after') {
    const expectedSource = option('--expected-source');
    assert.match(expectedSource, /^[a-f0-9]{40}$/);
    assert.equal(source, expectedSource, 'Update must use the expected candidate source.');
    assert.notEqual(source, before.source_sha, 'Update must use different real code.');
  }
  if (phase === 'rollback') assert.equal(source, before.source_sha, 'Rollback must restore original code.');
  const legacy = path.join(root, 'state', 'webview2');
  const expectedUserData = phase === 'after'
    ? path.join(root, 'state', 'webview2-fixed', version) : legacy;
  const evidence = { phase, source_sha: source, state: 'failed' };
  let app;
  let browser;
  let observationDeadline;
  try {
    app = await launchApp(current, legacy, nonce, root);
    // Startup owns its bounded cleanup; after it returns, every CDP wait must
    // yield to our finally before the Python parent's last-resort timeout.
    observationDeadline = raceDeadline(60000);
    const observe = operation => beforeDeadline(operation, observationDeadline);
    const session = await observe(app.browser.newBrowserCDPSession());
    const { processInfo: processes } = await observe(session.send('SystemInfo.getProcessInfo'));
    const browsers = processes.filter(item => item.type === 'browser');
    assert.equal(browsers.length, 1, 'Expected one browser process in this CDP session.');
    browser = await processInfo(browsers[0].id);
    assert(browser?.ExecutablePath, 'Actual browser image path is unavailable.');
    const browserVersion = await observe(session.send('Browser.getVersion'));
    const userData = browserUserData(browser.CommandLine);
    // WebView2 appends EBWebView to the host's UDF before launching Chromium.
    // https://learn.microsoft.com/microsoft-edge/web-platform/devtools-mcp-server
    assert.equal(path.resolve(userData).toLowerCase(),
      path.join(expectedUserData, 'EBWebView').toLowerCase());
    const expectedImage = path.join(current, 'app', 'WebView2Runtime', 'msedgewebview2.exe');
    if (phase === 'after') {
      assert.equal(path.resolve(browser.ExecutablePath).toLowerCase(), expectedImage.toLowerCase());
      assert.equal(browserVersion.product.split('/').at(-1), version);
    } else {
      verifyEvergreenImage(browser.ExecutablePath, browserVersion.product.split('/').at(-1));
      assert(!fs.existsSync(path.join(current, 'app', 'WebView2Runtime')),
        'Historical baseline must be the original framework-dependent product.');
    }
    const profile = await observe(app.page.evaluate(() => ({
      nonce: localStorage.getItem('vibeocr.transition.nonce'),
      collapsed: localStorage.getItem('recognition.previewCollapsed'),
    })));
    verifyProfile(phase, profile, nonce);
    if (phase !== 'rollback') {
      await observe(app.page.evaluate(({ value, collapsed }) => {
        localStorage.setItem('vibeocr.transition.nonce', value);
        localStorage.setItem('recognition.previewCollapsed', collapsed);
      }, { value: phase === 'before' ? nonce : `fixed-${nonce}`,
        collapsed: phase === 'before' ? 'true' : 'false' }));
    }
    Object.assign(evidence, { browser, browser_version: browserVersion.product,
      expected_user_data_folder: expectedUserData, browser_user_data_dir: userData, profile });
    await closeAppNormally(app.child, { AppPid: app.child.pid, Handle: app.main.Handle });
    const deadline = Date.now() + 15000;
    let remaining;
    do {
      remaining = await processInfo(browser.ProcessId);
      if (!remaining || remaining.CreationDate !== browser.CreationDate) break;
      await delay(200);
    } while (Date.now() < deadline);
    assert(!remaining || remaining.CreationDate !== browser.CreationDate,
      'Owned browser did not exit before the update/rollback boundary.');
    evidence.state = 'passed';
  } catch (error) {
    evidence.error = `${error.name}: ${error.message}`;
    throw error;
  } finally {
    observationDeadline?.clear();
    try {
      if (app) {
        await stopOwned(app.child, 'close', { AppPid: app.child.pid, Handle: app.main.Handle });
        const disconnectDeadline = raceDeadline(5000);
        try { await beforeDeadline(app.browser.close(), disconnectDeadline); }
        finally { disconnectDeadline.clear(); }
      }
    } catch (error) {
      evidence.state = 'failed';
      evidence.cleanup_error = error.message;
      throw error;
    } finally {
      fs.writeFileSync(path.join(evidenceRoot, `${phase}.json`), JSON.stringify(evidence, null, 2));
    }
  }
}

if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  main().catch(error => { console.error(error); process.exitCode = 1; });
}
