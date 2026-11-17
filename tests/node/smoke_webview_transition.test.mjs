import assert from 'node:assert/strict';
import { browserUserData, verifyProfile, verifyEvergreenImage, beforeDeadline,
  parseProductEnvironment } from '../../scripts/smoke_webview_transition.mjs';
import { launchApp } from '../../scripts/smoke_native_actions.mjs';

await assert.rejects(launchApp('unused', 'unused', `transition-${'a'.repeat(32)}`), /32-character GUID/);

assert.equal(browserUserData('browser.exe --user-data-dir="C:\\owned root\\state\\webview2" --flag'),
  'C:\\owned root\\state\\webview2');
assert.equal(browserUserData('browser.exe --user-data-dir=C:\\owned\\state --flag'), 'C:\\owned\\state');
assert.throws(() => browserUserData('browser.exe'), /one observable/);
assert.throws(() => browserUserData('--user-data-dir=A --user-data-dir=B'), /one observable/);
verifyProfile('before', { nonce: null, collapsed: null }, 'old');
verifyProfile('after', { nonce: null, collapsed: null }, 'old');
verifyProfile('rollback', { nonce: 'old', collapsed: 'true' }, 'old');
assert.throws(() => verifyProfile('after', { nonce: 'old' }, 'old'), /old nonce/);
assert.throws(() => verifyProfile('rollback', { nonce: 'fixed-old', collapsed: 'false' }, 'old'),
  /original browser profile/);
assert.throws(() => verifyProfile('rollback', { nonce: 'old', collapsed: 'false' }, 'old'),
  /original browser preference/);
verifyEvergreenImage('C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\157.0.1.2\\msedgewebview2.exe', '157.0.1.2');
assert.throws(() => verifyEvergreenImage('C:\\other\\msedgewebview2.exe', '157.0.1.2'), /installed Evergreen/);
assert.throws(() => verifyEvergreenImage('C:\\Microsoft\\EdgeWebView\\Application\\156.0.1.2\\msedgewebview2.exe', '157.0.1.2'));
let cleanup = false;
await assert.rejects(async () => {
  try { await beforeDeadline(new Promise(() => {}), { promise: Promise.resolve(false) }); }
  finally { cleanup = true; }
}, /deadline exceeded/);
assert.equal(cleanup, true, 'Timed-out observations must unwind through owned cleanup.');
assert.equal(await beforeDeadline(Promise.resolve(false), { promise: new Promise(() => {}) }), false);
const validProductEnvironment = {
  LOCALAPPDATA: 'C:\\owned\\profile\\AppData\\Local',
  APPDATA: 'C:\\owned\\profile\\AppData\\Roaming',
  USERPROFILE: 'C:\\owned\\profile',
  HOME: 'C:\\owned\\profile',
  TEMP: 'C:\\owned\\temp',
  TMP: 'C:\\owned\\temp',
};
assert.deepEqual(parseProductEnvironment(JSON.stringify(validProductEnvironment)),
  validProductEnvironment);
assert.throws(() => parseProductEnvironment(JSON.stringify(
  { ...validProductEnvironment, SECRET_SENTINEL: 'token' })), /unknown keys/);
const missingHome = { ...validProductEnvironment };
delete missingHome.HOME;
assert.throws(() => parseProductEnvironment(JSON.stringify(missingHome)),
  /explicit product HOME/);
assert.throws(() => parseProductEnvironment(
  JSON.stringify({ ...validProductEnvironment, TEMP: '' })), /non-empty string/);
assert.throws(() => parseProductEnvironment('[]'), /JSON object/);
console.log('WebView transition evidence contracts passed.');
