import test from 'node:test';
import assert from 'node:assert/strict';
import {
  DEFAULT_APP_LOCK,
  normalizeLockPrefs,
  shouldLockOnColdStart,
  shouldLockOnResume,
} from '../src/safety/appLockPolicy.ts';

const base = { prefs: DEFAULT_APP_LOCK, canAuthenticate: true, signedIn: true, now: 1_000_000 };

test('default is on with a one minute timeout', () => {
  assert.deepEqual(DEFAULT_APP_LOCK, { enabled: true, timeoutMinutes: 1 });
  assert.deepEqual(normalizeLockPrefs(undefined), DEFAULT_APP_LOCK);
  assert.deepEqual(normalizeLockPrefs({ enabled: false, timeoutMinutes: 5 }), { enabled: false, timeoutMinutes: 5 });
  assert.deepEqual(normalizeLockPrefs({ enabled: 'yes', timeoutMinutes: 7 }), DEFAULT_APP_LOCK);
});

test('locks only after the timeout has passed', () => {
  assert.equal(shouldLockOnResume({ ...base, backgroundedAt: base.now - 30_000 }), false);
  assert.equal(shouldLockOnResume({ ...base, backgroundedAt: base.now - 60_000 }), true);
  assert.equal(shouldLockOnResume({ ...base, prefs: { enabled: true, timeoutMinutes: 0 }, backgroundedAt: base.now }), true);
  assert.equal(shouldLockOnResume({ ...base, prefs: { enabled: true, timeoutMinutes: 15 }, backgroundedAt: base.now - 14 * 60_000 }), false);
});

test('never locks when off, signed out, nothing to verify against, or never backgrounded', () => {
  assert.equal(shouldLockOnResume({ ...base, prefs: { enabled: false, timeoutMinutes: 0 }, backgroundedAt: 0 }), false);
  assert.equal(shouldLockOnResume({ ...base, signedIn: false, backgroundedAt: 0 }), false);
  assert.equal(shouldLockOnResume({ ...base, canAuthenticate: false, backgroundedAt: 0 }), false);
  assert.equal(shouldLockOnResume({ ...base, backgroundedAt: null }), false);
});

test('a clock set backwards locks rather than trusting the gap', () => {
  assert.equal(shouldLockOnResume({ ...base, backgroundedAt: base.now + 5_000 }), true);
});

test('cold start locks a restored session but not a fresh sign-in', () => {
  assert.equal(shouldLockOnColdStart(DEFAULT_APP_LOCK, true, true), true);
  assert.equal(shouldLockOnColdStart(DEFAULT_APP_LOCK, true, false), false);
  assert.equal(shouldLockOnColdStart({ enabled: false, timeoutMinutes: 1 }, true, true), false);
  assert.equal(shouldLockOnColdStart(DEFAULT_APP_LOCK, false, true), false);
});
