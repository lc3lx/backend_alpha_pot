/**
 * Token recognition across brokers.
 *
 * Run with: node --test
 *
 * These are the assertions that would have caught the bug this file was extracted to fix.
 * The capture used to read only a `token` field, so a Quotex login — which sends
 * `session` — was reported as "no auth frame was ever seen" even though the browser had
 * signed in successfully and the frame was right there in the log.
 */

import assert from 'node:assert/strict';
import test from 'node:test';

import { BROKER_PRESETS, brokerPreset, extractWsAuthorizationToken } from './authHelpers.mjs';

const LONG = 'a'.repeat(40);

test('a Binolla authorization frame yields its token', () => {
  const frame = `42["authorization",{"isDemo":true,"token":"${LONG}"}]`;
  assert.equal(extractWsAuthorizationToken(frame), LONG);
});

test('a Quotex authorization frame yields its session', () => {
  const frame = `42["authorization",{"session":"${LONG}","isDemo":0,"tournamentId":0}]`;
  assert.equal(extractWsAuthorizationToken(frame), LONG);
});

test('a non-auth frame yields nothing', () => {
  // The trading socket carries hundreds of these a second. Treating one as a session
  // would store a price update where the credentials belong.
  assert.equal(extractWsAuthorizationToken(`42["instruments/list",{"session":"${LONG}"}]`), null);
});

test('every broker preset carries the fields the capture reads', () => {
  for (const [name, preset] of Object.entries(BROKER_PRESETS)) {
    for (const field of ['loginUrl', 'signupUrl', 'tradingUrl', 'label']) {
      assert.equal(typeof preset[field], 'string', `${name}.${field}`);
      assert.ok(preset[field].length > 0, `${name}.${field} is empty`);
    }
    assert.ok(Array.isArray(preset.cookieHosts) && preset.cookieHosts.length > 0, `${name}.cookieHosts`);
    // May legitimately be empty — Quotex has no JSON login — but must exist, because the
    // capture branches on its length rather than on the broker's name.
    assert.ok(Array.isArray(preset.loginApiPaths), `${name}.loginApiPaths`);
    assert.ok(Array.isArray(preset.signupApiPaths), `${name}.signupApiPaths`);
  }
});

test('an unknown broker falls back to Binolla rather than to nothing', () => {
  // The capture would otherwise dereference undefined and fail with a stack trace instead
  // of attempting the login every existing caller expects.
  assert.equal(brokerPreset('nope'), BROKER_PRESETS.binolla);
  assert.equal(brokerPreset(undefined), BROKER_PRESETS.binolla);
});

test('broker names are matched regardless of case and padding', () => {
  assert.equal(brokerPreset(' Quotex '), BROKER_PRESETS.quotex);
});

test('Quotex is not given a guessed JSON login path', () => {
  // It signs in through a CSRF-bearing form. Posting to a guessed endpoint burns seconds
  // and can trip the rate limiter before the real form is ever filled.
  assert.deepEqual(BROKER_PRESETS.quotex.loginApiPaths, []);
});
