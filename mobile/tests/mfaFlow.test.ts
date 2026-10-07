import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MFA_MAX_ATTEMPTS,
  attemptsLeft,
  authenticatorUri,
  classifyLoginResponse,
  classifyMfaFailure,
  classifyMfaStatus,
  codeEntryReducer,
  formatCountdown,
  groupSecret,
  initialCodeEntry,
  normalizeCode,
  parseRecoveryCodes,
  secondsLeft,
  secretFromProvisioningUri,
  spellSecretForScreenReader,
  type CodeEntryState,
  type MfaFailureClass,
} from '../src/auth/mfaFlow.ts';
import { mfaAr, mfaEn } from '../src/config/mfaStrings.ts';
import { readFileSync } from 'node:fs';

const NOW = Date.parse('2026-10-05T12:00:00Z');

// Exact shapes AuthController.Login / MfaController return on sec/hardening-wave1.
const ENROLLED_LOGIN = { mfaRequired: true, challengeToken: 'chal.opaque', expiresInSeconds: 300 };
const ENFORCED_LOGIN = {
  mfaEnrollmentRequired: true,
  enrollmentToken: 'enrol.opaque',
  expiresInSeconds: 300,
  message: 'Your organization requires multi-factor authentication. Please set up MFA to continue.',
};
const TOKENS_LOGIN = { accessToken: 'a', refreshToken: 'r', expiresAtUtc: '2026-10-05T13:00:00Z', user: { id: 'u' } };

function axiosError(status?: number): unknown {
  return status === undefined
    ? { isAxiosError: true, request: {}, response: undefined, message: 'Network Error' }
    : { isAxiosError: true, response: { status, data: { message: 'Invalid or expired MFA challenge.' } } };
}

function submitAndFail(state: CodeEntryState, failure: MfaFailureClass, nowMs = NOW + 1_000): CodeEntryState {
  const submitted = codeEntryReducer(state, { type: 'submit', nowMs });
  assert.equal(submitted.phase, 'submitting');
  return codeEntryReducer(submitted, { type: 'failed', failure, nowMs });
}

// ---- (a) enrolled ------------------------------------------------------------

test('enrolled privileged user: login yields a code challenge, never a session', () => {
  const step = classifyLoginResponse(ENROLLED_LOGIN);
  assert.deepEqual(step, { kind: 'mfaChallenge', challengeToken: 'chal.opaque', expiresInSeconds: 300 });
});

test('enrolled: a correct code completes the challenge', () => {
  const start = initialCodeEntry(300, NOW);
  const submitted = codeEntryReducer(start, { type: 'submit', nowMs: NOW + 5_000 });
  const done = codeEntryReducer(submitted, { type: 'succeeded' });
  assert.equal(done.phase, 'succeeded');
  assert.equal(done.error, null);
});

test('malformed MFA replies fail loudly instead of signing in', () => {
  assert.throws(() => classifyLoginResponse({ mfaRequired: true }));
  assert.throws(() => classifyLoginResponse({ mfaEnrollmentRequired: true, enrollmentToken: '' }));
});

// ---- (b) grace -----------------------------------------------------------------

test('grace: login returns ordinary tokens; status prompts with the enforcement date', () => {
  assert.equal(classifyLoginResponse(TOKENS_LOGIN).kind, 'authenticated');
  const prompt = classifyMfaStatus(
    {
      enabled: false,
      required: true,
      requiredBecause: 'privileged_role',
      enforceFromUtc: '2026-10-19T20:06:57.123Z',
      enforced: false,
      promptToEnroll: true,
    },
    NOW
  );
  assert.deepEqual(prompt, { kind: 'grace', enforceFromUtc: '2026-10-19T20:06:57.123Z' });
});

test('grace: offset-less dates are read as UTC; no date or a break-glass past date omits it', () => {
  assert.deepEqual(
    classifyMfaStatus({ enabled: false, enforced: false, promptToEnroll: true, enforceFromUtc: '2026-10-19T00:00:00' }, NOW),
    { kind: 'grace', enforceFromUtc: '2026-10-19T00:00:00.000Z' }
  );
  assert.deepEqual(
    classifyMfaStatus({ enabled: false, enforced: false, promptToEnroll: true, enforceFromUtc: null }, NOW),
    { kind: 'grace', enforceFromUtc: null }
  );
  // Break-glass: date passed, server still says not enforced.
  assert.deepEqual(
    classifyMfaStatus({ enabled: false, enforced: false, promptToEnroll: true, enforceFromUtc: '2026-10-01T00:00:00Z' }, NOW),
    { kind: 'grace', enforceFromUtc: null }
  );
});

test('no banner for enrolled, non-privileged, or unreadable status', () => {
  assert.deepEqual(classifyMfaStatus({ enabled: true, required: true, promptToEnroll: false, enforced: false }, NOW), { kind: 'none' });
  assert.deepEqual(classifyMfaStatus({ enabled: false, required: false, promptToEnroll: false, enforced: false }, NOW), { kind: 'none' });
  assert.deepEqual(classifyMfaStatus(null, NOW), { kind: 'none' });
});

// ---- (c) enforced --------------------------------------------------------------

test('enforced: login yields a setup-only enrolment token, never a session', () => {
  assert.deepEqual(classifyLoginResponse(ENFORCED_LOGIN), {
    kind: 'mfaEnrollment',
    enrollmentToken: 'enrol.opaque',
    expiresInSeconds: 300,
  });
});

test('enforced while still signed in: status says enforced', () => {
  assert.deepEqual(
    classifyMfaStatus({ enabled: false, enforced: true, promptToEnroll: false, enforceFromUtc: '2026-10-01T00:00:00Z' }, NOW),
    { kind: 'enforced' }
  );
});

// ---- wrong code / attempt limit / expiry -----------------------------------------

test('wrong code: back to ready, one attempt used, plain-language error', () => {
  const after = submitAndFail(initialCodeEntry(300, NOW), 'rejected');
  assert.equal(after.phase, 'ready');
  assert.equal(after.error, 'wrongCode');
  assert.equal(attemptsLeft(after), MFA_MAX_ATTEMPTS - 1);
  assert.equal(codeEntryReducer(after, { type: 'edited' }).error, null, 'typing clears the error');
});

test('attempt limit: the fifth wrong code locks the request; nothing more is sent', () => {
  let state = initialCodeEntry(300, NOW);
  for (let i = 1; i < MFA_MAX_ATTEMPTS; i++) {
    state = submitAndFail(state, 'rejected');
    assert.equal(state.phase, 'ready');
    assert.equal(attemptsLeft(state), MFA_MAX_ATTEMPTS - i);
  }
  state = submitAndFail(state, 'rejected');
  assert.equal(state.phase, 'locked');
  assert.equal(state.error, 'attemptLimit');
  assert.equal(codeEntryReducer(state, { type: 'submit', nowMs: NOW + 2_000 }), state, 'locked is terminal');
});

test('expired challenge: the clock expires it, and a late rejection reads as expired, not wrong code', () => {
  const start = initialCodeEntry(300, NOW);
  assert.equal(secondsLeft(start, NOW), 300);
  const ticked = codeEntryReducer(start, { type: 'tick', nowMs: NOW + 300_000 });
  assert.equal(ticked.phase, 'expired');
  assert.equal(ticked.error, 'expired');

  const submitted = codeEntryReducer(start, { type: 'submit', nowMs: NOW + 299_000 });
  const late = codeEntryReducer(submitted, { type: 'failed', failure: 'rejected', nowMs: NOW + 301_000 });
  assert.equal(late.phase, 'expired');
  assert.equal(late.failedAttempts, 0);

  assert.equal(codeEntryReducer(start, { type: 'submit', nowMs: NOW + 300_000 }).phase, 'expired');
});

// ---- transport failures --------------------------------------------------------

test('network error: no attempt is used and the user can retry', () => {
  assert.equal(classifyMfaFailure(axiosError()), 'network');
  const after = submitAndFail(initialCodeEntry(300, NOW), 'network');
  assert.equal(after.phase, 'ready');
  assert.equal(after.error, 'network');
  assert.equal(after.failedAttempts, 0);
});

test('throttling and server faults are never counted as wrong codes', () => {
  assert.equal(classifyMfaFailure(axiosError(429)), 'rateLimited');
  assert.equal(classifyMfaFailure(axiosError(500)), 'server');
  assert.equal(classifyMfaFailure(axiosError(401)), 'rejected');
  assert.equal(classifyMfaFailure(axiosError(400)), 'server', 'a 400 is a malformed request, not a wrong code');
  assert.equal(classifyMfaFailure(axiosError(409)), 'conflict');
  assert.equal(classifyMfaFailure(new Error('parse')), 'server');
  const limited = submitAndFail(initialCodeEntry(300, NOW), 'rateLimited');
  assert.equal(limited.error, 'rateLimited');
  assert.equal(limited.failedAttempts, 0);
});

test('after a network or server failure the next 401 is worded neutrally, then wording returns to wrong code', () => {
  for (const transient of ['network', 'server'] as const) {
    let state = submitAndFail(initialCodeEntry(300, NOW), transient);
    assert.equal(state.afterTransientFailure, true);
    state = submitAndFail(state, 'rejected');
    assert.equal(state.error, 'notAccepted');
    assert.equal(state.failedAttempts, 1, 'it still counts against the server limit');
    state = submitAndFail(state, 'rejected');
    assert.equal(state.error, 'wrongCode');
  }
  const afterThrottle = submitAndFail(submitAndFail(initialCodeEntry(300, NOW), 'rateLimited'), 'rejected');
  assert.equal(afterThrottle.error, 'wrongCode', 'a 429 never reached the code check');
});

test('first sign-in after enrolling: the first rejection says to wait for the next code, later ones say wrong code', () => {
  let state = initialCodeEntry(300, NOW, { justEnrolled: true });
  state = submitAndFail(state, 'rejected');
  assert.equal(state.error, 'waitForNextCode');
  assert.equal(state.failedAttempts, 1, 'the backend still counts it');
  state = submitAndFail(state, 'rejected');
  assert.equal(state.error, 'wrongCode');
  assert.equal(submitAndFail(initialCodeEntry(300, NOW), 'rejected').error, 'wrongCode', 'ordinary sign-in unchanged');
  assert.match(mfaEn.doneSignInAgain, /Wait for the next code/);
});

test('a second submit while one is in flight is ignored', () => {
  const submitted = codeEntryReducer(initialCodeEntry(300, NOW), { type: 'submit', nowMs: NOW });
  assert.equal(codeEntryReducer(submitted, { type: 'submit', nowMs: NOW }), submitted);
});

// ---- enrolment helpers -----------------------------------------------------------

test('setup key comes out of the backend otpauth URI and is shown grouped', () => {
  const uri = 'otpauth://totp/admin%40evostel.sa?secret=JBSWY3DPEHPK3PXP&issuer=Zayra%20HRM&algorithm=SHA1&digits=6&period=30';
  assert.equal(secretFromProvisioningUri(uri), 'JBSWY3DPEHPK3PXP');
  assert.equal(groupSecret('JBSWY3DPEHPK3PXP'), 'JBSW Y3DP EHPK 3PXP');
  assert.equal(spellSecretForScreenReader('JBSWY3DP'), 'J B S W, Y 3 D P', 'screen readers spell it out');
  assert.equal(authenticatorUri(uri, 'JBSWY3DPEHPK3PXP', 'admin@evostel.sa'), uri, "backend's own URI is preferred");
  assert.equal(secretFromProvisioningUri('otpauth://totp/x?issuer=y'), '');
  assert.equal(secretFromProvisioningUri('otpauth://totp/x?secret=not*base32'), '');
});

test('without a usable backend URI the deep link is built from the secret', () => {
  const built = authenticatorUri('', 'JBSWY3DPEHPK3PXP', 'admin@evostel.sa');
  assert.equal(
    built,
    'otpauth://totp/KynexOne:admin%40evostel.sa?secret=JBSWY3DPEHPK3PXP&issuer=KynexOne&algorithm=SHA1&digits=6&period=30'
  );
});

test('recovery codes are shown only when the server returns them (tenant enrolment returns 204)', () => {
  assert.equal(parseRecoveryCodes(undefined), null);
  assert.equal(parseRecoveryCodes(''), null);
  assert.equal(parseRecoveryCodes({ recoveryCodes: [] }), null);
  assert.deepEqual(parseRecoveryCodes({ recoveryCodes: ['abcd-efgh', '', 7] }), ['abcd-efgh']);
});

test('code input keeps digits only, at most six; countdown formats m:ss', () => {
  assert.equal(normalizeCode('12 3-45a678'), '123456');
  assert.equal(formatCountdown(299), '4:59');
  assert.equal(formatCountdown(-3), '0:00');
});

// ---- strings -------------------------------------------------------------------------

function flatten(obj: Record<string, unknown>, prefix = ''): Map<string, string> {
  const out = new Map<string, string>();
  for (const [key, value] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value && typeof value === 'object') {
      for (const [k, v] of flatten(value as Record<string, unknown>, path)) out.set(k, v);
    } else {
      out.set(path, String(value));
    }
  }
  return out;
}

test('every MFA string exists in English and Arabic with the same placeholders', () => {
  const en = flatten(mfaEn);
  const ar = flatten(mfaAr);
  assert.deepEqual([...ar.keys()].sort(), [...en.keys()].sort());
  const placeholders = (text: string) => (text.match(/\{\{\w+\}\}/g) ?? []).sort();
  for (const [key, text] of en) {
    assert.ok(ar.get(key)!.trim().length > 0, `${key} is empty in Arabic`);
    assert.deepEqual(placeholders(ar.get(key)!), placeholders(text), `${key} placeholders differ`);
  }
  for (const kind of ['wrongCode', 'notAccepted', 'waitForNextCode', 'attemptLimit', 'expired', 'rateLimited', 'network', 'server']) {
    assert.ok(en.has(`errors.${kind}`), `missing error string ${kind}`);
  }
  assert.equal(mfaEn.bannerWithDate, 'Your role requires two-step sign-in from {{date}}. Set it up now.');
  assert.equal(mfaEn.errors.notAccepted, 'Not accepted. If this keeps happening, sign in again.');
});

// Dependencies of the last native baseline (origin/main before the first store
// build). Anything else is new native surface and must be named here on purpose.
const BASELINE_DEPENDENCIES = [
  '@expo/vector-icons', '@hookform/resolvers', '@react-native-async-storage/async-storage',
  '@react-native-community/datetimepicker', '@react-navigation/bottom-tabs', '@react-navigation/native',
  '@react-navigation/native-stack', 'axios', 'date-fns', 'expo', 'expo-application', 'expo-blur', 'expo-camera',
  'expo-constants', 'expo-device', 'expo-document-picker', 'expo-file-system', 'expo-font', 'expo-glass-effect',
  'expo-haptics', 'expo-image-picker', 'expo-linear-gradient', 'expo-local-authentication', 'expo-location',
  'expo-notifications', 'expo-secure-store', 'expo-sharing', 'expo-splash-screen', 'expo-status-bar',
  'expo-system-ui', 'i18next', 'react', 'react-hook-form', 'react-i18next', 'react-native',
  'react-native-reanimated', 'react-native-safe-area-context', 'react-native-screens', 'react-native-svg',
  'react-native-worklets', 'zod', 'zustand',
  // Build 6 (#58): config plugin for iOS scene support (app.json plugins), not part of MFA.
  'expo-build-properties',
];
// Both must ship in the FIRST store binary: the OTA channel, and screen-capture
// protection for the setup key.
const ALLOWED_NEW_NATIVE = ['expo-screen-capture', 'expo-updates'];

test('new native modules are exactly expo-updates and expo-screen-capture; no clipboard module', () => {
  const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  assert.equal(pkg.dependencies['expo-clipboard'], undefined);
  const added = Object.keys(pkg.dependencies).filter((name) => !BASELINE_DEPENDENCIES.includes(name)).sort();
  assert.deepEqual(added, ALLOWED_NEW_NATIVE);
  const mfaSources = [
    '../src/features/auth/MfaEnrollmentView.tsx',
    '../src/features/auth/MfaSetupBanner.tsx',
    '../src/features/auth/MfaChallengeScreen.tsx',
    '../src/features/auth/mfaCodeEntry.tsx',
  ].map((path) => readFileSync(new URL(path, import.meta.url), 'utf8'));
  for (const source of mfaSources) {
    for (const [, specifier] of source.matchAll(/from '([^'.@][^']*|@[^/']+\/[^']+)'/g)) {
      const bare = specifier.startsWith('@') ? specifier.split('/').slice(0, 2).join('/') : specifier.split('/')[0];
      assert.ok(bare in pkg.dependencies, `${specifier} is not an existing dependency`);
    }
  }
  const view = mfaSources[0];
  assert.match(view, /Share\.share\(\{ message: /);
  assert.match(view, /usePreventScreenCapture\('mfa-enrollment'\)/);
  assert.match(view, /disableAppSwitcherProtectionAsync\(\)/, 'protection is removed on unmount');
  assert.match(view, /<Text\s+selectable\s+style=\{styles\.secret\}/);
  assert.equal('copyKey' in mfaEn, false);
  assert.ok(mfaEn.shareKey && mfaAr.shareKey && mfaEn.longPressToCopy && mfaAr.longPressToCopy);
});
