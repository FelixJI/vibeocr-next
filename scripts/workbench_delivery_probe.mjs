// Task #215 shared helper for scripts/probe_runtime_demand.mjs and
// scripts/smoke_runtime_demand_tools.mjs. Additive passive measurement only:
// per-scope/per-scene WebView message counting with UTF-8 payload bytes
// (TextEncoder in page / Buffer.byteLength outside, on the JSON serialization
// - never string length, never claimed as network frame bytes) plus the
// actual JS resource request graph (in-page buffered resource timing + CDP
// request events). Listener-only: this module never sends bridge commands,
// never edits app state and never changes public input steps. Aggregation is
// bounded (fixed scope maps, capped entry lists, capped page snapshots) so no
// unbounded per-message arrays can grow on either side of the boundary.
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// All 10 host scopes, from WorkbenchStateMailbox.cs KnownScopes (shell,
// recognition, imageEdit, batch, pdf, qrcode, settings, update, about,
// diagnostics); unknown scopes still land in bounded buckets below.
export const KNOWN_SCOPES = ['shell', 'recognition', 'imageEdit', 'batch',
  'qrcode', 'pdf', 'settings', 'update', 'about', 'diagnostics'];

// UTF-8 bytes of the JSON serialization (Node side). Mirrors the in-page
// TextEncoder measurement; returns null when the value is not serializable.
export function utf8JsonBytes(value) {
  try {
    const text = JSON.stringify(value ?? null);
    if (typeof text !== 'string') return null;
    return Buffer.byteLength(text, 'utf8');
  } catch {
    return null;
  }
}

// In-page passive probe. Self-contained (globals only) so the exact same
// source is installed via context.addInitScript for pages created after a
// context was actually observed (coverage confirmed per context, never
// assumed) and via page.evaluate on pages attached after their first screen
// (install_stage records that fact; such pages are marked partial outside).
export function workbenchProbeInstall(source) {
  const w = window;
  if (w.__rt215) return; // idempotent: never resets an existing probe
  try {
    const encoder = new TextEncoder();
    const MAX_KEYS = 48;
    const MAX_JS_ENTRIES = 64;
    const installSource = source === 'init-script' || source === 'late-evaluate'
      ? source : 'unknown';
    const stats = {
      probe_installed_at_ms: Date.now(),
      // The ACTUAL install entry decides coverage - never readyState alone:
      // init-script runs at document start; late-evaluate runs after the
      // script obtained the page. 'unknown' defaults to a declared gap.
      install_source: installSource,
      first_screen_missed: installSource !== 'init-script',
      first_batch_message_gap: installSource !== 'init-script',
      install_stage: w.document?.readyState ?? 'unknown',
      page_url_at_install: w.location?.href ?? null,
      messages: {
        total: 0, utf8_bytes_total: 0, first_at_ms: null, last_at_ms: null,
        by_key: {}, overflow: { key: '__overflow__', count: 0, utf8_bytes: 0 },
        max_keys: MAX_KEYS, raw_string_messages: 0, object_messages: 0,
        listener_attached: false, listener_attached_at_ms: null,
        attach_deferred: null,
        known_scopes: ['shell', 'recognition', 'imageEdit', 'batch',
          'qrcode', 'pdf', 'settings', 'update', 'about', 'diagnostics'],
        utf8_bytes_definition: 'UTF-8 bytes of the JSON serialization of each ' +
          'webview message envelope (TextEncoder); not string length, not ' +
          'network frame bytes',
      },
      resources: {
        observer_installed: false, observer_entries: 0, by_initiator: {},
        js_count: 0, js_transfer_size_total: 0, js_entries: [], truncated: false,
        max_js_entries: MAX_JS_ENTRIES,
        js_attribution: 'URL pathname evidence only (.js/.mjs/.cjs, query ' +
          'and hash stripped): initiatorType script/link WITHOUT such an ' +
          'extension (e.g. CSS links, extension-less dynamic imports) is ' +
          'counted in script_or_link_without_js_extension, never as js',
        script_or_link_without_js_extension: { count: 0, sample: [] },
        first_screen_source: 'buffered performance resource timing; coverage ' +
          'is reported per page via install_stage - never assumed from the ' +
          'init script alone (a WebView2 surface may open in another browser ' +
          'context that was observed later, or not at all)',
      },
    };
    const recordMessage = (rawData) => {
      const now = Date.now();
      let envelope = rawData;
      let serialized = null;
      try {
        if (typeof rawData === 'string') {
          serialized = rawData;
          envelope = JSON.parse(rawData);
          stats.messages.raw_string_messages += 1;
        } else {
          serialized = JSON.stringify(rawData);
          stats.messages.object_messages += 1;
        }
      } catch {
        serialized = typeof rawData === 'string' ? rawData : null;
        envelope = null;
      }
      const bytes = serialized === null ? 0 : encoder.encode(serialized).length;
      const m = stats.messages;
      m.total += 1;
      m.utf8_bytes_total += bytes;
      m.first_at_ms ??= now;
      m.last_at_ms = now;
      let key;
      if (envelope && typeof envelope === 'object' && envelope.kind === 'event'
        && envelope.type === 'app.state' && envelope.payload
        && typeof envelope.payload.scope === 'string') {
        key = `scope:${envelope.payload.scope}`;
      } else {
        key = `msg:${envelope?.kind ?? 'unknown'}:${envelope?.type ?? 'unknown'}`;
      }
      let bucket = m.by_key[key];
      if (!bucket && Object.keys(m.by_key).length < MAX_KEYS) {
        bucket = m.by_key[key] = { count: 0, utf8_bytes: 0, first_at_ms: now };
      }
      if (bucket) {
        bucket.count += 1;
        bucket.utf8_bytes += bytes;
        bucket.last_at_ms = now;
      } else {
        m.overflow.count += 1;
        m.overflow.utf8_bytes += bytes;
      }
    };
    const classifyJsByPath = (name) => {
      // URL pathname evidence only: initiatorType 'link' also covers CSS and
      // 'script' can be extension-less, so js attribution requires a .js/.mjs/
      // .cjs pathname (query/hash stripped). Anything else stays in the
      // uncertain bucket - never silently relabelled as js.
      try {
        return /\.(?:js|mjs|cjs)$/i.test(new URL(name, w.location?.href).pathname);
      } catch {
        return false;
      }
    };
    const recordResource = (entry) => {
      const r = stats.resources;
      r.observer_entries += 1;
      const type = String(entry?.initiatorType ?? 'other');
      r.by_initiator[type] = (r.by_initiator[type] ?? 0) + 1;
      if (type === 'script' || type === 'link') {
        if (!classifyJsByPath(entry.name)) {
          r.script_or_link_without_js_extension.count += 1;
          if (r.script_or_link_without_js_extension.sample.length < 16)
            r.script_or_link_without_js_extension.sample.push({ url: entry.name, initiator: type });
          return;
        }
        r.js_count += 1;
        r.js_transfer_size_total += entry.transferSize ?? 0;
        if (r.js_entries.length < MAX_JS_ENTRIES) {
          r.js_entries.push({ url: entry.name, initiator: type,
            transfer_size: entry.transferSize ?? null,
            encoded_body_size: entry.encodedBodySize ?? null,
            decoded_body_size: entry.decodedBodySize ?? null,
            duration_ms: Math.round(entry.duration ?? 0) });
        } else {
          r.truncated = true;
        }
      }
    };
    let attachAttempts = 0;
    const attachMessages = () => {
      // Idempotent: the DOMContentLoaded and load retries can both fire after
      // chrome.webview appears; a second registration would double-count.
      if (stats.messages.listener_attached) return true;
      attachAttempts += 1;
      const target = w.chrome?.webview;
      if (!target || typeof target.addEventListener !== 'function') return false;
      target.addEventListener('message', (event) => recordMessage(event.data));
      stats.messages.listener_attached = true;
      stats.messages.listener_attached_at_ms = Date.now();
      // A deferred attach cannot confirm when the listener really went live:
      // messages before the actual attach stay a declared first-batch gap.
      stats.messages.attach_deferred = attachAttempts > 1;
      if (stats.messages.attach_deferred) stats.first_batch_message_gap = true;
      return true;
    };
    if (!attachMessages()) {
      // chrome.webview can be missing at document start; retry on ready events.
      for (const later of ['DOMContentLoaded', 'load'])
        w.document.addEventListener(later, () => attachMessages(), { once: true });
    }
    try {
      new w.PerformanceObserver((list) => {
        for (const entry of list.getEntries()) recordResource(entry);
      }).observe({ type: 'resource', buffered: true });
      stats.resources.observer_installed = true;
    } catch {
      stats.resources.observer_installed = false;
    }
    w.__rt215 = {
      stats,
      snapshot: () => {
        const copy = JSON.parse(JSON.stringify(stats));
        copy.collected_at_ms = Date.now();
        copy.resources.performance_entries_now =
          w.performance?.getEntriesByType?.('resource')?.length ?? null;
        return copy;
      },
    };
  } catch (error) {
    w.__rt215 = { stats: { probe_error: String(error) } };
  }
}

// Node-side controlled aggregation. Each page keeps only its latest cumulative
// snapshot (bounded by the in-page caps), pages are capped, CDP requests are
// counters plus a capped JS URL list. Never unbounded arrays.
export function createWorkbenchCollector() {
  const MAX_PAGES = 8;
  const state = {
    schema: 'workbench-delivery-probe/1',
    issue: '215',
    measurement: 'passive webview message counts + UTF-8 JSON payload bytes ' +
      'per scope/scene + actual JS resource request graph',
    known_scopes: KNOWN_SCOPES,
    collection_started_at_ms: Date.now(),
    collection_ended_at_ms: null,
    contexts: {},
    contexts_dropped: 0,
    cdp_requests: null,
    pages: {},
    pages_dropped: 0,
    errors: [],
    limitations: [
      'coverage is only ever claimed for actually observed contexts (see contexts, with discovery source): contexts existing at attach, the page context confirmed explicitly when the script holds a page, and - only as an opportunistic extra - the declared Browser on("context") signal, which is never used as proof of complete collection',
      'the main workbench page attaches after its first screen, and any scene page whose document start could not be confirmed (context first observed when the script obtained the page) is marked partial via first_screen_missed/install_stage - never upgraded to covered',
      'first-screen resource completion relies on the in-page buffered resource timing entries; pages without resource timing are reported as observed, never fabricated into a complete graph or a pass',
      'utf8 byte figures are serialized-envelope payload bytes, not network frame bytes (headers/framing are not measured)',
    ],
  };
  const noteError = (message) => {
    if (state.errors.length < MAX_PAGES) state.errors.push(message);
  };
  const attachedContexts = new WeakSet();
  let contextSeq = 0;
  const attachOneContext = (context, discovery) => {
    if (attachedContexts.has(context)) return null;
    attachedContexts.add(context);
    contextSeq += 1;
    const origin = `context-${contextSeq}`;
    if (Object.keys(state.contexts).length >= MAX_PAGES) {
      state.contexts_dropped += 1;
      return null;
    }
    const entry = state.contexts[origin] = { init_script_installed: false,
      observed_at_ms: Date.now(), discovery,
      discovered_after_attach: discovery !== 'initial' };
    entry.init_script_installed = false;
    // Passive CDP-side actual request observation (counts only + capped JS
    // URL sample). Marked partial for the pre-connect first screen.
    const cdp = state.cdp_requests ??= { attached_at_ms: Date.now(),
      partial_before_connect: true, count_total: 0, by_type: {},
      js_url_count: 0, js_urls: [], truncated: false };
    context.on('request', (request) => {
      cdp.count_total += 1;
      const type = request.resourceType() ?? 'other';
      cdp.by_type[type] = (cdp.by_type[type] ?? 0) + 1;
      if (type === 'script') {
        cdp.js_url_count += 1;
        if (cdp.js_urls.length < 128) cdp.js_urls.push(request.url());
        else cdp.truncated = true;
      }
    });
    context.on('requestfailed', (request) => {
      cdp.failed_total = (cdp.failed_total ?? 0) + 1;
      const type = request.resourceType() ?? 'other';
      (cdp.failed_by_type ??= {})[type] = (cdp.failed_by_type[type] ?? 0) + 1;
    });
    // Never rejects: failures are noted and reported as gaps.
    return context.addInitScript(workbenchProbeInstall, 'init-script')
      .then(() => { entry.init_script_installed = true; })
      .catch((error) => noteError(`${origin} init script: ${error.message}`));
  };
  return {
    noteError,
    // Simple provable scheme: attach the contexts that exist right now. No
    // completeness claim is derived from any event.
    attachBrowser(browser) {
      const pending = [];
      try {
        for (const context of browser.contexts())
          pending.push(attachOneContext(context, 'initial'));
      } catch (error) {
        noteError(`initial contexts: ${error.message}`);
      }
      // The Browser on('context') declaration is used ONLY as an
      // opportunistic extra discovery signal (it fires for API-created
      // contexts and some remoting paths; proving it for CDP-discovered
      // contexts is out of scope). It is never relied upon: any surface not
      // covered by it stays an honest partial/gap.
      try {
        if (typeof browser.on === 'function')
          browser.on('context', (context) => attachOneContext(context, 'browser-context-event'));
      } catch (error) {
        noteError(`context event hook: ${error.message}`);
      }
      return Promise.all(pending.filter(Boolean));
    },
    // Awaited install for pages the script holds (main page after connect,
    // scene page right after it is located): the page's context is attached
    // FIRST (awaited), then the probe runs via evaluate with the honest
    // 'late-evaluate' source. An existing init-script probe is untouched
    // (in-page idempotency, no reset), so this never masquerades as
    // full-round coverage.
    async installOnPage(page) {
      try {
        const context = page?.context?.();
        if (context) {
          const attached = attachOneContext(context, 'page-context');
          if (attached) await attached;
        }
      } catch (error) {
        noteError(`page context attach: ${error.message}`);
      }
      return page.evaluate(workbenchProbeInstall, 'late-evaluate');
    },
    // Collection ONLY: harvest never installs probes (installing at the end
    // would fake full-round coverage); snapshots carry the authoritative
    // install_source/first_screen_missed/first_batch_message_gap from the
    // actual install entry, never inferred from readyState.
    async harvest(pageKey, page) {
      try {
        const snapshot = await page.evaluate(() =>
          window.__rt215?.snapshot?.() ?? null);
        if (!snapshot) {
          noteError(`${pageKey}: probe snapshot unavailable`);
          return null;
        }
        const known = Object.prototype.hasOwnProperty.call(state.pages, pageKey);
        if (!known && Object.keys(state.pages).length >= MAX_PAGES) {
          state.pages_dropped += 1;
          return null;
        }
        snapshot.first_screen_listener_installed_at_document_start =
          snapshot.install_source === 'init-script';
        state.pages[pageKey] = snapshot;
        return snapshot;
      } catch (error) {
        noteError(`${pageKey} harvest failed: ${error.message}`);
        return null;
      }
    },
    finalise() {
      state.collection_ended_at_ms = Date.now();
      return JSON.parse(JSON.stringify(state));
    },
  };
}

// Discriminating no-GUI self-check: UTF-8 Chinese payload bytes must match
// Buffer.byteLength and exceed the JS string length; the real in-page probe
// source (run against a minimal fake DOM) must aggregate the recognition
// scope exactly, respect the bounded by_key map (overflow bucket) and keep a
// bounded JS entry list; the collector must cap stored pages.
export async function runMeasurementSelfCheck() {
  const chinese = '中文识别载荷';
  const envelope = { version: 2, kind: 'event', id: 'rt215-self-check',
    type: 'app.state',
    payload: { scope: 'recognition', revision: 1, state: { note: chinese } } };
  const serialized = JSON.stringify(envelope);
  const viaBuffer = Buffer.byteLength(serialized, 'utf8');
  assert.equal(utf8JsonBytes(envelope), viaBuffer, 'helper must match Buffer.byteLength');
  assert.equal(new TextEncoder().encode(serialized).length, viaBuffer,
    'TextEncoder must agree with Buffer.byteLength');
  assert(viaBuffer > serialized.length,
    'UTF-8 byte count must exceed the JS string length for Chinese payloads');
  assert(serialized.includes(chinese) && viaBuffer > Buffer.byteLength(chinese, 'utf8'),
    'Chinese payload must be present and its UTF-8 bytes counted');

  // Known scopes: all 10 host scopes incl. shell/update, in both places.
  assert.equal(KNOWN_SCOPES.length, 10);
  assert(KNOWN_SCOPES.includes('shell') && KNOWN_SCOPES.includes('update'),
    'KNOWN_SCOPES must match WorkbenchStateMailbox.cs (shell/update included)');

  // Exercise the exact in-page source with a minimal fake DOM (no GUI).
  const listeners = {};
  const observers = [];
  const resourceEntries = [];
  const fakeWindow = {
    location: { href: 'https://app.vibeocr/index.html#/' },
    document: { readyState: 'complete', addEventListener: (type, fn) => { (listeners[type] ??= []).push(fn); } },
    chrome: { webview: { addEventListener: (type, fn) => { (listeners[type] ??= []).push(fn); } } },
    performance: { getEntriesByType: () => resourceEntries },
    PerformanceObserver: class {
      constructor(callback) { this.callback = callback; }
      observe() { observers.push(this); }
    },
  };
  fakeWindow.window = fakeWindow;
  const savedWindow = globalThis.window;
  globalThis.window = fakeWindow;
  const overflowScopes = 80;
  let overflowBytes = 0;
  try {
    workbenchProbeInstall();
    const probe = fakeWindow.__rt215;
    assert(probe && !probe.stats.probe_error,
      `in-page probe failed to install: ${probe?.stats?.probe_error}`);
    assert.equal(probe.stats.install_stage, 'complete');
    assert.equal(probe.stats.install_source, 'unknown');
    assert.equal(probe.stats.first_screen_missed, true,
      'an unknown install entry must default to a declared gap');
    assert.deepEqual(probe.stats.messages.known_scopes, KNOWN_SCOPES,
      'in-page known_scopes must stay in sync with KNOWN_SCOPES');
    resourceEntries.push(
      { name: 'https://app.vibeocr/assets/app.js', initiatorType: 'script',
        transferSize: 1234, encodedBodySize: 1200, decodedBodySize: 2400, duration: 12.3 },
      { name: 'https://app.vibeocr/assets/chunk.css', initiatorType: 'link',
        transferSize: 50, encodedBodySize: 40, decodedBodySize: 40, duration: 2.1 },
      { name: 'https://app.vibeocr/assets/bundle.mjs', initiatorType: 'script',
        transferSize: 700, encodedBodySize: 690, decodedBodySize: 1400, duration: 5.5 },
      { name: 'https://app.vibeocr/assets/dynamic?q=1', initiatorType: 'script',
        transferSize: 99, encodedBodySize: 90, decodedBodySize: 90, duration: 1.5 },
      { name: 'https://app.vibeocr/index.html', initiatorType: 'other',
        transferSize: 10, encodedBodySize: 10, decodedBodySize: 10, duration: 1 },
    );
    for (const observer of observers)
      observer.callback({ getEntries: () => resourceEntries });
    const message = listeners.message?.[0];
    assert(message, 'webview message listener was not attached');
    message({ data: serialized });
    message({ data: envelope }); // object-form envelope: re-serialized, same bytes
    for (let index = 0; index < overflowScopes; index += 1) {
      const synthetic = { version: 2, kind: 'event', id: `rt215-${index}`,
        type: 'app.state', payload: { scope: `synthetic-${index}`, revision: 1 } };
      overflowBytes += Buffer.byteLength(JSON.stringify(synthetic), 'utf8');
      message({ data: synthetic });
    }
    const stats = probe.snapshot();
    assert.equal(stats.messages.total, overflowScopes + 2);
    assert.equal(stats.messages.raw_string_messages, 1);
    assert.equal(stats.messages.object_messages, overflowScopes + 1);
    assert.equal(stats.messages.by_key['scope:recognition'].count, 2);
    assert.equal(stats.messages.by_key['scope:recognition'].utf8_bytes, viaBuffer * 2);
    assert.equal(stats.messages.utf8_bytes_total, viaBuffer * 2 + overflowBytes);
    assert.equal(Object.keys(stats.messages.by_key).length, stats.messages.max_keys,
      'by_key must be capped at max_keys');
    assert(stats.messages.overflow.count > 0,
      'distinct-scope overflow must land in the bounded overflow bucket');
    assert(stats.messages.by_key['scope:recognition'].utf8_bytes > 0
      && serialized.length < viaBuffer, 'UTF-8 aggregation must not use string length');
    assert.equal(stats.resources.js_count, 2,
      'only .js/.mjs pathnames may count as js (CSS link and extension-less excluded)');
    assert.equal(stats.resources.js_transfer_size_total, 1934);
    assert.deepEqual(stats.resources.js_entries.map((entry) => entry.url),
      ['https://app.vibeocr/assets/app.js', 'https://app.vibeocr/assets/bundle.mjs']);
    assert.equal(stats.resources.by_initiator.other, 1);
    assert.equal(stats.resources.script_or_link_without_js_extension.count, 2,
      'CSS link + extension-less script must land in the uncertain bucket');
    assert.equal(stats.resources.script_or_link_without_js_extension.sample.length, 2);
    assert.equal(stats.resources.performance_entries_now, 5);
    assert.equal(stats.messages.by_key['scope:synthetic-0'] !== undefined, true);
    assert.equal(stats.messages.by_key[`scope:synthetic-${overflowScopes - 1}`] !== undefined, false,
      'the last synthetic scope must have overflowed the capped map');
  } finally {
    globalThis.window = savedWindow;
  }

  // Late chrome.webview: both the DOMContentLoaded and load retries fire, but
  // only one listener may register (no double counting).
  const retryListeners = {};
  const retryWindow = {
    location: { href: 'https://app.vibeocr/index.html#/' },
    document: { readyState: 'loading',
      addEventListener: (type, fn) => { (retryListeners[type] ??= []).push(fn); } },
    performance: { getEntriesByType: () => [] },
    PerformanceObserver: class { constructor() {} observe() {} },
  };
  retryWindow.window = retryWindow;
  const savedRetryWindow = globalThis.window;
  globalThis.window = retryWindow;
  try {
    workbenchProbeInstall();
    const retryProbe = retryWindow.__rt215;
    assert.equal(retryProbe.stats.messages.listener_attached, false);
    const retryMessages = [];
    retryWindow.chrome = { webview: {
      addEventListener: (type, fn) => { if (type === 'message') retryMessages.push(fn); },
    } };
    for (const type of ['DOMContentLoaded', 'load'])
      for (const fn of retryListeners[type] ?? []) fn();
    assert.equal(retryMessages.length, 1,
      'message listener must register exactly once across both ready retries');
    assert.equal(retryProbe.stats.messages.listener_attached, true);
    retryMessages[0]({ data: serialized });
    assert.equal(retryProbe.snapshot().messages.total, 1,
      'a single delivered message must be counted exactly once');
    assert.equal(retryProbe.snapshot().messages.by_key['scope:recognition'].count, 1);
    assert.equal(retryProbe.snapshot().messages.attach_deferred, true);
    assert.equal(retryProbe.snapshot().first_batch_message_gap, true,
      'a deferred chrome.webview attach must keep the first-batch gap declared');
  } finally {
    globalThis.window = savedRetryWindow;
  }

  // New scene installed at its actual init entry: per-scope counting works
  // and coverage follows the install source, not the readyState value.
  const sceneListeners = {};
  const sceneWindow = {
    location: { href: 'https://app.vibeocr/index.html#/imageEdit?scene=1' },
    document: { readyState: 'loading',
      addEventListener: (type, fn) => { (sceneListeners[type] ??= []).push(fn); } },
    chrome: { webview: {
      addEventListener: (type, fn) => { (sceneListeners[type] ??= []).push(fn); } } },
    performance: { getEntriesByType: () => [] },
    PerformanceObserver: class { constructor() {} observe() {} },
  };
  sceneWindow.window = sceneWindow;
  const savedSceneWindow = globalThis.window;
  globalThis.window = sceneWindow;
  try {
    workbenchProbeInstall('init-script');
    const sceneProbe = sceneWindow.__rt215;
    assert.equal(sceneProbe.stats.install_source, 'init-script');
    assert.equal(sceneProbe.stats.first_screen_missed, false);
    assert.equal(sceneProbe.stats.first_batch_message_gap, false);
    const deliver = (scope) => sceneListeners.message[0]({ data: JSON.stringify({
      version: 2, kind: 'event', id: `rt215-scene-${scope}`, type: 'app.state',
      payload: { scope, revision: 1 } }) });
    deliver('recognition');
    deliver('imageEdit');
    deliver('imageEdit');
    const sceneStats = sceneProbe.snapshot();
    assert.equal(sceneStats.messages.by_key['scope:recognition'].count, 1);
    assert.equal(sceneStats.messages.by_key['scope:imageEdit'].count, 2,
      'scene scope aggregation must count each scope separately');
    assert.equal(sceneStats.messages.total, 3);
    assert.equal(sceneStats.first_batch_message_gap, false);
    workbenchProbeInstall('late-evaluate'); // idempotent, no reset
    assert.equal(sceneProbe.snapshot().messages.total, 3,
      'a second install must never reset or double the existing probe');
  } finally {
    globalThis.window = savedSceneWindow;
  }

  // Late install while readyState is still 'loading' must stay partial:
  // readyState may never be used to claim document-start coverage.
  const lateLoadingWindow = {
    location: { href: 'https://app.vibeocr/index.html#/' },
    document: { readyState: 'loading', addEventListener: () => {} },
    chrome: { webview: { addEventListener: () => {} } },
    performance: { getEntriesByType: () => [] },
    PerformanceObserver: class { constructor() {} observe() {} },
  };
  lateLoadingWindow.window = lateLoadingWindow;
  const savedLateLoading = globalThis.window;
  globalThis.window = lateLoadingWindow;
  try {
    workbenchProbeInstall('late-evaluate');
    const lateLoading = lateLoadingWindow.__rt215.stats;
    assert.equal(lateLoading.install_stage, 'loading');
    assert.equal(lateLoading.install_source, 'late-evaluate');
    assert.equal(lateLoading.first_screen_missed, true,
      'late-but-loading must still be marked partial (install entry, not readyState)');
    assert.equal(lateLoading.first_batch_message_gap, true);
  } finally {
    globalThis.window = savedLateLoading;
  }

  // Collector-side bounded page storage, per-page first-screen flags and
  // per-context init-script confirmation (initial + explicit page-context +
  // the optional, non-load-bearing browser-context-event signal).
  const collector = createWorkbenchCollector();
  const makeContext = () => ({ on () {},
    addInitScript: async () => {} });
  const initialContext = makeContext();
  const contextEvents = [];
  const fakeBrowser = { contexts: () => [initialContext],
    on: (type, fn) => { if (type === 'context') contextEvents.push(fn); } };
  await collector.attachBrowser(fakeBrowser);
  // A browser without on() must not break the simple scheme.
  const noEventCollector = createWorkbenchCollector();
  await noEventCollector.attachBrowser({ contexts: () => [makeContext()] });
  assert.equal(noEventCollector.finalise().contexts['context-1'].init_script_installed, true);
  const latePage = await collector.harvest('late-page', {
    evaluate: async () => ({ install_source: 'late-evaluate',
      install_stage: 'complete', first_screen_missed: true,
      first_batch_message_gap: true, messages: { total: 1 } }),
  });
  assert.equal(latePage.first_screen_missed, true);
  assert.equal(latePage.first_screen_listener_installed_at_document_start, false);
  // Scene page: its context is attached explicitly via page.context() when
  // the script holds the page (the reliable path; no event reliance).
  // Scene page, mirroring captureStep: installOnPage is awaited as soon as
  // the script holds the page (context attached inside); harvest afterwards
  // is collection-only and never installs anything.
  const sceneContext = makeContext();
  await collector.installOnPage({
    context: () => sceneContext,
    evaluate: async () => null,
  });
  const scenePage = await collector.harvest('scene-page', {
    context: () => sceneContext,
    evaluate: async () => ({ install_source: 'late-evaluate',
      install_stage: 'interactive', first_screen_missed: true,
      first_batch_message_gap: true, messages: { total: 1 } }),
  });
  assert.equal(scenePage.first_screen_missed, true,
    'a late-attached scene page must be marked partial, never covered');
  for (let index = 0; index < 12; index += 1)
    await collector.harvest(`page-${index}`, {
      evaluate: async () => ({ collected_at_ms: index, messages: { total: 1 } }),
    });
  // The declared on('context') is only an opportunistic extra signal.
  contextEvents[0]?.(makeContext());
  await new Promise((resolve) => setImmediate(resolve));
  const finalised = collector.finalise();
  assert.equal(Object.keys(finalised.pages).length, 8, 'collector must cap stored pages');
  assert.equal(finalised.pages_dropped, 6);
  assert.equal(finalised.contexts['context-1'].init_script_installed, true);
  assert.equal(finalised.contexts['context-1'].discovery, 'initial');
  assert.equal(finalised.contexts['context-2'].init_script_installed, true,
    'the scene context must be confirmed installed via page.context()');
  assert.equal(finalised.contexts['context-2'].discovery, 'page-context');
  assert.equal(finalised.contexts['context-2'].discovered_after_attach, true);
  assert.equal(finalised.contexts['context-3'].init_script_installed, true);
  assert.equal(finalised.contexts['context-3'].discovery, 'browser-context-event');
  assert(Number.isSafeInteger(finalised.collection_ended_at_ms));
  assert(finalised.limitations.some((line) => line.includes('never used as proof')),
    'the context event must never be load-bearing for completeness claims');

  return { self_check: 'passed', utf8_bytes_chinese_sample: viaBuffer,
    string_length_same_sample: serialized.length,
    scoped_bucket_bytes: viaBuffer * 2, capped_map_size: 48,
    overflow_messages: overflowScopes + 1 - 48 + 1,
    late_webview_single_registration: true,
    scene_init_source_scope_counts: 3, late_loading_partial: true,
    js_by_pathname: 2, script_or_link_without_js_extension: 2,
    known_scopes: KNOWN_SCOPES.length,
    collector_pages_kept: 8, collector_pages_dropped: 6,
    contexts_confirmed: 3 };
}

const invokedDirectly = process.argv[1]
  && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (invokedDirectly) {
  if (process.argv.includes('--self-check')) {
    runMeasurementSelfCheck()
      .then((summary) => console.log(JSON.stringify(summary)))
      .catch((error) => { console.error(error); process.exitCode = 1; });
  } else {
    console.error('Usage: node scripts/workbench_delivery_probe.mjs --self-check');
    process.exitCode = 2;
  }
}
