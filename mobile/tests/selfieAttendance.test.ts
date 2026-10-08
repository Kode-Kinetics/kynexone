import test from 'node:test';
import assert from 'node:assert/strict';
import {
  KNOWN_REFUSAL_CODES,
  REVIEW_API_CODES,
  busyRetryDelayMs,
  REFUSAL_KEYS,
  VERIFICATION_CACHE_MS,
  VERIFICATION_OFF,
  buildLocationFields,
  buildPunchBody,
  cacheIsFresh,
  choosePictureSize,
  isSelfieBusy,
  mapPunchRefusal,
  mockDetectionFields,
  parseAttendanceVerification,
  planPunch,
} from '../src/features/attendance/selfieAttendance.ts';
import { selfieAr, selfieEn } from '../src/config/selfieStrings.ts';

const consent = { policyVersion: '1', givenAtUtc: '2026-10-08T07:00:00Z', channel: 'Mobile' };

function discovery(selfie: Record<string, unknown>, geofence: Record<string, unknown> = { enforced: false }) {
  return parseAttendanceVerification({
    selfie: { enabled: true, currentPolicyVersion: '1', evidenceLifetimeSeconds: 600, maxUploadsPerHour: 10, ...selfie },
    geofence,
  });
}

// ---- Punch-flow decision ----

test('selfie off: no selfie step, no consent prompt', () => {
  const plan = planPunch(discovery({ enabled: false, step: 'optional', consent }));
  assert.equal(plan.selfie, 'skip');
  assert.equal(plan.offerConsent, false);
  assert.equal(plan.offerWithdraw, false);
});

test('consent needed: no selfie step, a non-blocking consent prompt', () => {
  const plan = planPunch(discovery({ step: 'consent_needed', consent: null }));
  assert.equal(plan.selfie, 'skip');
  assert.equal(plan.offerConsent, true);
});

test('consented, optional: selfie offered with withdraw', () => {
  const plan = planPunch(discovery({ step: 'optional', consent }));
  assert.equal(plan.selfie, 'optional');
  assert.equal(plan.offerConsent, false);
  assert.equal(plan.offerWithdraw, true);
});

test('consented, required: selfie asked for, withdraw still offered', () => {
  const plan = planPunch(discovery({ step: 'required', requiredForConsented: true, consent }));
  assert.equal(plan.selfie, 'required');
  assert.equal(plan.offerWithdraw, true);
});

test('a step that asks for a selfie without consent never makes the selfie mandatory', () => {
  for (const step of ['optional', 'required']) {
    const v = discovery({ step, consent: null });
    assert.equal(v.selfie.step, 'consent_needed');
    const plan = planPunch(v);
    assert.equal(plan.selfie, 'skip');
    assert.equal(plan.offerConsent, true);
  }
});

test('discovery unavailable: punch with location and no selfie step', () => {
  for (const v of [null, undefined, VERIFICATION_OFF]) {
    const plan = planPunch(v);
    assert.deepEqual(plan, { selfie: 'skip', offerConsent: false, offerWithdraw: false, locationRequired: false, maxAccuracyMeters: null });
  }
});

test('geofence enforced: location required with the tenant accuracy limit', () => {
  const plan = planPunch(discovery({ enabled: false }, { enforced: true, maxAccuracyMeters: 100, allowMockedLocation: false, sites: [] }));
  assert.equal(plan.locationRequired, true);
  assert.equal(plan.maxAccuracyMeters, 100);
  assert.equal(plan.selfie, 'skip');
});

test('discovery parsing is defensive: malformed reads as off', () => {
  assert.deepEqual(parseAttendanceVerification(null), VERIFICATION_OFF);
  assert.deepEqual(parseAttendanceVerification('nope'), VERIFICATION_OFF);
  const v = parseAttendanceVerification({ selfie: { enabled: 'yes', step: 'required' }, geofence: { enforced: 1 } });
  assert.equal(v.selfie.enabled, false);
  assert.equal(v.selfie.step, 'off');
  assert.equal(v.geofence.enforced, false);
  assert.equal(v.geofence.maxAccuracyMeters, null);
});

test('discovery parsing keeps only sites with coordinates and a radius', () => {
  const v = discovery({ step: 'off' }, {
    enforced: true,
    maxAccuracyMeters: 50,
    allowMockedLocation: false,
    sites: [
      { name: 'HQ office', latitude: 24.7136, longitude: 46.6753, radiusMeters: 150 },
      { name: 'Broken', latitude: null, longitude: 46.6, radiusMeters: 100 },
    ],
  });
  assert.deepEqual(v.geofence.sites, [{ name: 'HQ office', latitude: 24.7136, longitude: 46.6753, radiusMeters: 150 }]);
  assert.equal(v.geofence.allowMockedLocation, false);
});

// ---- Location payload ----

test('android: accuracy in metres, mockDetection Supported with locationMocked', () => {
  assert.deepEqual(
    buildLocationFields({ coords: { latitude: 24.7137, longitude: 46.6753, accuracy: 12.5 }, mocked: false }, 'android'),
    { latitude: 24.7137, longitude: 46.6753, accuracyMeters: 12.5, mockDetection: 'Supported', locationMocked: false },
  );
  assert.equal(buildLocationFields({ coords: { latitude: 1, longitude: 2, accuracy: 5 }, mocked: true }, 'android').locationMocked, true);
  // Android without a reported flag still answers false, never omits it.
  assert.equal(buildLocationFields({ coords: { latitude: 1, longitude: 2, accuracy: 5 } }, 'android').locationMocked, false);
});

test('ios: mockDetection Unsupported and no locationMocked', () => {
  const fields = buildLocationFields({ coords: { latitude: 24.7, longitude: 46.6, accuracy: 8 }, mocked: true }, 'ios');
  assert.deepEqual(fields, { latitude: 24.7, longitude: 46.6, accuracyMeters: 8, mockDetection: 'Unsupported' });
  assert.equal('locationMocked' in fields, false);
  assert.deepEqual(mockDetectionFields('web', undefined), { mockDetection: 'Unsupported' });
});

test('accuracy is passed through unchanged, and an unusable one is omitted rather than invented', () => {
  assert.equal(buildLocationFields({ coords: { latitude: 1, longitude: 2, accuracy: 99.99 } }, 'ios').accuracyMeters, 99.99);
  for (const accuracy of [null, undefined, Number.NaN, -1, Number.POSITIVE_INFINITY]) {
    const fields = buildLocationFields({ coords: { latitude: 1, longitude: 2, accuracy } }, 'android');
    assert.equal('accuracyMeters' in fields, false, String(accuracy));
  }
});

test('no position or bad coordinates: no location fields at all', () => {
  assert.deepEqual(buildLocationFields(null, 'android'), {});
  assert.deepEqual(buildLocationFields({ coords: { latitude: Number.NaN, longitude: 2, accuracy: 5 } }, 'android'), {});
});

test('punch body is self-only, carries the evidence id, and never a verification claim', () => {
  const location = buildLocationFields({ coords: { latitude: 24.7, longitude: 46.6, accuracy: 10 }, mocked: false }, 'android');
  const body = buildPunchBody({ direction: 'In', locationName: 'Mobile GPS', location, evidenceId: 'ev-1' });
  assert.deepEqual(body, {
    employeeId: 0,
    punchDirection: 'In',
    locationName: 'Mobile GPS',
    latitude: 24.7,
    longitude: 46.6,
    accuracyMeters: 10,
    mockDetection: 'Supported',
    locationMocked: false,
    evidenceId: 'ev-1',
  });
  for (const claim of ['verificationMethod', 'confidenceScore', 'clientBiometricVerified', 'ClientBiometricVerified']) {
    assert.equal(claim in body, false, claim);
  }
  assert.equal('evidenceId' in buildPunchBody({ direction: 'Out', locationName: 'Mobile', location: {} }), false);
  assert.equal('evidenceId' in buildPunchBody({ direction: 'Out', locationName: 'Mobile', location: {}, evidenceId: null }), false);
});

// ---- Refusals ----

const refusal = (status: number, data: Record<string, unknown>) => ({ response: { status, data } });

test('every documented refusal code maps to its plain-language key and next step', () => {
  const expected: Record<string, [string, string]> = {
    outside_geofence: ['outsideGeofence', 'retry'],
    location_inaccurate: ['locationInaccurate', 'open_settings'],
    location_mocked: ['locationMocked', 'retry'],
    location_required: ['locationRequired', 'open_settings'],
    location_invalid: ['locationInvalid', 'retry'],
    geofence_site_missing: ['siteMissing', 'none'],
    evidence_expired: ['evidenceExpired', 'retake_selfie'],
    evidence_used: ['evidenceUsed', 'retake_selfie'],
    evidence_not_found: ['evidenceNotFound', 'retake_selfie'],
    consent_required: ['consentRequired', 'without_selfie'],
    selfie_not_enabled: ['selfieNotEnabled', 'without_selfie'],
    selfie_required: ['selfieRequired', 'take_selfie'],
    selfie_rate_limited: ['selfieRateLimited', 'without_selfie'],
    selfie_missing: ['selfieUnusable', 'retake_selfie'],
    selfie_too_large: ['selfieUnusable', 'retake_selfie'],
    selfie_invalid: ['selfieUnusable', 'retake_selfie'],
    access_mode_not_allowed: ['accessMode', 'without_selfie'],
    employee_not_linked: ['notLinked', 'none'],
  };
  for (const [code, [key, action]] of Object.entries(expected)) {
    const r = mapPunchRefusal(refusal(400, { code, message: 'm', messageAr: 'ر' }), 'en');
    assert.equal(r.key, key, code);
    assert.equal(r.action, action, code);
    assert.equal(r.code, code);
    assert.equal(r.titleKey, `selfie.refusal.${key}.title`);
    assert.equal(r.nextKey, `selfie.refusal.${key}.next`);
  }
});

test("the server's message is shown in the app language", () => {
  const data = { code: 'outside_geofence', message: 'You are 420 m from HQ office (radius 150 m).', messageAr: 'أنت على بعد 420 م من المقر.' };
  assert.equal(mapPunchRefusal(refusal(400, data), 'en').serverMessage, data.message);
  assert.equal(mapPunchRefusal(refusal(400, data), 'ar').serverMessage, data.messageAr);
});

test('an English-only server message is not shown to an Arabic reader: the local Arabic text is used', () => {
  const r = mapPunchRefusal(refusal(403, { error: 'feature_not_enabled', feature: 'selfie_attendance', message: 'Feature off' }), 'ar');
  assert.equal(r.key, 'selfieNotEnabled');
  assert.equal(r.serverMessage, null);
  assert.equal(r.messageKey, 'selfie.refusal.selfieNotEnabled.message');
});

test('no response is a network problem; an unknown code keeps the server message', () => {
  const network = mapPunchRefusal(new Error('Network Error'), 'en');
  assert.equal(network.key, 'network');
  assert.equal(network.action, 'retry');
  assert.equal(network.serverMessage, null);
  const notLinked = mapPunchRefusal(Object.assign(new Error('not linked'), { code: 'employee_not_linked' }), 'ar');
  assert.equal(notLinked.key, 'notLinked');
  assert.equal(notLinked.serverMessage, null);
  assert.equal(mapPunchRefusal(Object.assign(new Error('x'), { code: 'ERR_NETWORK' }), 'en').key, 'network');
  const unknown = mapPunchRefusal(refusal(400, { code: 'something_new', message: 'Plain reason.' }), 'en');
  assert.equal(unknown.key, 'unknown');
  assert.equal(unknown.serverMessage, 'Plain reason.');
  const legacy = mapPunchRefusal(refusal(400, { message: 'Your login is not linked.' }), 'en');
  assert.equal(legacy.key, 'unknown');
  assert.equal(legacy.serverMessage, 'Your login is not linked.');
});

test('a fast 429 is "selfie processing is busy"; the hourly limit is not', () => {
  assert.equal(mapPunchRefusal(refusal(429, { message: 'busy' }), 'en').key, 'selfieBusy');
  assert.equal(isSelfieBusy(refusal(429, {})), true);
  assert.equal(isSelfieBusy(refusal(429, { code: 'selfie_rate_limited' })), false);
  assert.equal(isSelfieBusy(new Error('offline')), false);
  assert.equal(mapPunchRefusal(refusal(429, { code: 'selfie_rate_limited' }), 'en').key, 'selfieRateLimited');
});

test('refusals added by the backend review are mapped', () => {
  assert.equal(REVIEW_API_CODES.app_update_required, 'appUpdateRequired');
  assert.equal(mapPunchRefusal(refusal(400, { code: 'app_update_required' }), 'en').action, 'none');
  assert.equal(mapPunchRefusal(refusal(429, { code: 'selfie_busy' }), 'en').key, 'selfieBusy');
  assert.equal(mapPunchRefusal(refusal(400, { code: 'mobile_app_required' }), 'en').key, 'mobileAppRequired');
});

test('refusals that can follow a settings change ask for fresh discovery', () => {
  assert.equal(mapPunchRefusal(refusal(400, { code: 'consent_required' }), 'en').refreshVerification, true);
  assert.equal(mapPunchRefusal(refusal(400, { code: 'selfie_required' }), 'en').refreshVerification, true);
  assert.equal(mapPunchRefusal(refusal(400, { code: 'evidence_used' }), 'en').refreshVerification, false);
});

test('every refusal key has a title, message and next step in English and Arabic', () => {
  for (const key of REFUSAL_KEYS) {
    for (const [lang, strings] of [['en', selfieEn], ['ar', selfieAr]] as const) {
      const entry = (strings.refusal as Record<string, { title: string; message: string; next: string }>)[key];
      assert.ok(entry, `${lang}.${key}`);
      for (const part of ['title', 'message', 'next'] as const) {
        assert.ok(entry[part]?.trim(), `${lang}.${key}.${part}`);
      }
    }
  }
  assert.ok(KNOWN_REFUSAL_CODES.includes('outside_geofence'));
});

test('Arabic has exactly the English keys, every value translated', () => {
  const walk = (en: unknown, ar: unknown, path: string) => {
    if (typeof en === 'string') {
      assert.equal(typeof ar, 'string', path);
      assert.ok((ar as string).trim(), path);
      assert.notEqual(ar, en, `${path} is not translated`);
      return;
    }
    const enKeys = Object.keys(en as object).sort();
    assert.deepEqual(Object.keys(ar as object).sort(), enKeys, path);
    for (const k of enKeys) walk((en as any)[k], (ar as any)[k], `${path}.${k}`);
  };
  walk(selfieEn, selfieAr, 'selfie');
});

test('no string claims face verification', () => {
  const all = JSON.stringify(selfieEn).toLowerCase();
  assert.equal(all.includes('face verified'), false);
  assert.equal(all.includes('face recognised') || all.includes('face recognized'), false);
});

// ---- Selfie size ----

test('picture size closest to a 1080 px long edge, presets without dimensions ignored', () => {
  assert.equal(choosePictureSize(['640x480', '1280x720', '1920x1080', '4032x3024']), '1280x720');
  assert.equal(choosePictureSize(['Photo', 'High', 'Medium', 'Low', '3840x2160', '1920x1080', '1280x720', '640x480']), '1280x720');
  assert.equal(choosePictureSize(['1080x1080', '1280x720']), '1080x1080');
  assert.equal(choosePictureSize(['960x540', '1200x900']), '1200x900'); // tie on distance → the larger
  assert.equal(choosePictureSize(['Photo', 'High']), undefined);
  assert.equal(choosePictureSize([]), undefined);
  assert.equal(choosePictureSize(null), undefined);
});

// ---- Discovery cache ----

test('discovery cache is short-lived and keyed to the sign-in', () => {
  const entry = { key: 't1:5:u1', at: 1_000, value: VERIFICATION_OFF };
  assert.equal(cacheIsFresh(entry, 't1:5:u1', 1_000 + VERIFICATION_CACHE_MS - 1), true);
  assert.equal(cacheIsFresh(entry, 't1:5:u1', 1_000 + VERIFICATION_CACHE_MS), false);
  assert.equal(cacheIsFresh(entry, 't1:6:u2', 1_500), false);
  assert.equal(cacheIsFresh(null, 't1:5:u1', 1_500), false);
  assert.equal(cacheIsFresh(entry, 't1:5:u1', 500), false); // clock went backwards
});

test('a busy 429 retries after Retry-After, kept between 1 s and 5 s, or 2 s without it', () => {
  assert.equal(busyRetryDelayMs({ response: { headers: { 'retry-after': '5' } } }), 5000);
  assert.equal(busyRetryDelayMs({ response: { headers: { 'retry-after': '60' } } }), 5000);
  assert.equal(busyRetryDelayMs({ response: { headers: { 'retry-after': '0.2' } } }), 1000);
  assert.equal(busyRetryDelayMs({ response: { headers: {} } }), 2000);
  assert.equal(busyRetryDelayMs(new Error('no response')), 2000);
});
