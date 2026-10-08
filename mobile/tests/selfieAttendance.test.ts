import test from 'node:test';
import assert from 'node:assert/strict';
import {
  KNOWN_REFUSAL_CODES,
  REVIEW_API_CODES,
  busyRetryDelayMs,
  REFUSAL_KEYS,
  VERIFICATION_CACHE_MS,
  SELFIE_IMAGE_TYPE,
  VERIFICATION_OFF,
  buildLocationFields,
  buildPunchBody,
  cacheIsFresh,
  choosePictureSize,
  clientPlatformHeaders,
  isSelfieBusy,
  mapPunchRefusal,
  mockDetectionFields,
  parseAttendanceVerification,
  parseWithdrawal,
  planPunch,
  refusalPrompt,
  refusalText,
  withdrawalNoticeKey,
} from '../src/features/attendance/selfieAttendance.ts';
import {
  CaptureGuard,
  SELFIE_FILE_PREFIX,
  previewActionFor,
  isCameraCaptureFile,
  isOwnSelfieFile,
  selfieFileName,
  selfieSweepTargets,
} from '../src/features/attendance/selfiePhotoPolicy.ts';
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
});

test('consent needed: no selfie step, a non-blocking consent prompt', () => {
  const plan = planPunch(discovery({ step: 'consent_needed', consent: null }));
  assert.equal(plan.selfie, 'skip');
  assert.equal(plan.offerConsent, true);
});

test('consented, optional: selfie offered', () => {
  const plan = planPunch(discovery({ step: 'optional', consent }));
  assert.equal(plan.selfie, 'optional');
  assert.equal(plan.offerConsent, false);
});

test('consented, required: selfie asked for, and the plan carries no withdraw lever', () => {
  const plan = planPunch(discovery({ step: 'required', requiredForConsented: true, consent }));
  assert.equal(plan.selfie, 'required');
  assert.equal('offerWithdraw' in plan, false);
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
    assert.deepEqual(plan, { selfie: 'skip', offerConsent: false, locationRequired: false, maxAccuracyMeters: null });
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
    assert.equal(r.nextKey, key === 'selfieRequired' ? null : `selfie.refusal.${key}.next`);
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

// ---- Review 2, item 8: never present withdrawal as the way past a required selfie ----

const echo = (key: string) => `[${key}]`;
const SELFIE_PLANS = ['skip', 'optional', 'required'] as const;
const STAGES = ['upload', 'punch'] as const;

test('selfie_required: no local next line is appended to the server message', () => {
  const r = mapPunchRefusal(refusal(400, { code: 'selfie_required', message: 'Take a selfie and try again.', messageAr: 'التقط صورة ذاتية.' }), 'en');
  assert.equal(r.nextKey, null);
  assert.equal(refusalText(r, echo), 'Take a selfie and try again.');
  const local = mapPunchRefusal(refusal(400, { code: 'selfie_required' }), 'ar');
  assert.equal(refusalText(local, echo), '[selfie.refusal.selfieRequired.message]');
  // Other refusals keep their next line.
  const geo = mapPunchRefusal(refusal(400, { code: 'outside_geofence', message: 'Too far.' }), 'en');
  assert.equal(refusalText(geo, echo), 'Too far.\n\n[selfie.refusal.outsideGeofence.next]');
});

test('selfie_required refusals offer a selfie, never withdrawal or a consent detour', () => {
  const r = mapPunchRefusal(refusal(400, { code: 'selfie_required', message: 'm' }), 'en');
  for (const plan of ['optional', 'required'] as const) {
    const prompt = refusalPrompt(r, plan, 'punch', echo);
    assert.deepEqual(prompt.buttons, ['cancel', 'take_selfie'], plan);
    assert.equal(prompt.body, 'm');
  }
  // After a refresh shows no consent (plan skip), the punch goes without a selfie.
  assert.deepEqual(refusalPrompt(r, 'skip', 'punch', echo).buttons, ['cancel', 'without_selfie']);
});

test('no refusal prompt ever shows a withdraw or consent button, nor withdraw wording in its body', () => {
  const allowed = new Set(['cancel', 'ok', 'try_again', 'open_settings', 'without_selfie', 'selfie_try_again', 'take_selfie', 'take_new_selfie']);
  const codes = [...KNOWN_REFUSAL_CODES, 'something_new'];
  for (const code of codes) {
    for (const lang of ['en', 'ar'] as const) {
      const strings = lang === 'en' ? selfieEn : selfieAr;
      const translate = (key: string) => key.split('.').slice(1).reduce((node: any, part) => node?.[part], strings) ?? key;
      const r = mapPunchRefusal(refusal(400, { code }), lang);
      for (const plan of SELFIE_PLANS) {
        for (const stage of STAGES) {
          const prompt = refusalPrompt(r, plan, stage, translate);
          for (const button of prompt.buttons) assert.ok(allowed.has(button), `${code}/${plan}/${stage}: ${button}`);
          assert.equal(/withdraw|سحب|اسحب/i.test(prompt.body), false, `${code}/${plan}/${stage}/${lang}: ${prompt.body}`);
        }
      }
    }
  }
});

test('review 3, item 7: "clock without a selfie" after a failed upload only when the server says punchWithoutSelfie: true', () => {
  const waived = mapPunchRefusal(refusal(429, { code: 'selfie_busy', punchWithoutSelfie: true }), 'en');
  const storage = mapPunchRefusal(refusal(503, { code: 'selfie_storage_unavailable', punchWithoutSelfie: true }), 'en');
  assert.equal(waived.punchWithoutSelfie, true);
  for (const plan of ['optional', 'required'] as const) {
    assert.deepEqual(refusalPrompt(waived, plan, 'upload', echo).buttons, ['cancel', 'without_selfie', 'selfie_try_again'], plan);
    assert.deepEqual(refusalPrompt(storage, plan, 'upload', echo).buttons, ['cancel', 'without_selfie', 'selfie_try_again'], plan);
  }
  // Never inferred from the code, the key or the status: the server will not waive these.
  const notWaived = [
    mapPunchRefusal(refusal(429, { code: 'selfie_busy' }), 'en'),                                   // the daily cap is used up
    mapPunchRefusal(refusal(429, { code: 'selfie_busy', punchWithoutSelfie: false }), 'en'),
    mapPunchRefusal(refusal(503, { code: 'selfie_storage_unavailable', punchWithoutSelfie: 'true' }), 'en'),
    mapPunchRefusal(refusal(500, {}), 'en'),
    mapPunchRefusal(refusal(429, {}), 'en'),
    mapPunchRefusal(new Error('Network Error'), 'en'),
    mapPunchRefusal(refusal(409, { code: 'selfie_upload_in_progress' }), 'en'),
  ];
  for (const r of notWaived) {
    assert.equal(r.punchWithoutSelfie, false, r.code);
    assert.deepEqual(refusalPrompt(r, 'required', 'upload', echo).buttons, ['cancel', 'selfie_try_again'], r.code);
    assert.deepEqual(refusalPrompt(r, 'optional', 'upload', echo).buttons, ['cancel', 'selfie_try_again'], r.code);
  }
  const limited = mapPunchRefusal(refusal(429, { code: 'selfie_rate_limited' }), 'en');
  const prompt = refusalPrompt(limited, 'required', 'upload', echo);
  assert.deepEqual(prompt.buttons, ['cancel', 'selfie_try_again']);
  assert.equal(prompt.body, '[selfie.refusal.selfieRateLimited.message]\n\n[selfie.punch.requiredHelp]');
  assert.deepEqual(refusalPrompt(limited, 'optional', 'upload', echo).buttons, ['cancel', 'without_selfie']);
});

test('review 3, item 7: selfie_upload_in_progress says a selfie is already being sent, and offers a retry', () => {
  assert.equal(REVIEW_API_CODES.selfie_upload_in_progress, 'selfieInProgress');
  const r = mapPunchRefusal(refusal(409, { code: 'selfie_upload_in_progress' }), 'en');
  assert.equal(r.key, 'selfieInProgress');
  assert.equal(r.action, 'retry');
  assert.equal(isSelfieBusy(refusal(409, { code: 'selfie_upload_in_progress' })), false); // not auto-retried as busy
  assert.equal(selfieEn.refusal.selfieInProgress.message, 'A selfie is already being sent, wait a moment.');
  assert.ok(selfieAr.refusal.selfieInProgress.message.length > 0);
  const translate = (key: string) => key.split('.').slice(1).reduce((node: any, part) => node?.[part], selfieEn) ?? key;
  const prompt = refusalPrompt(r, 'required', 'upload', translate);
  assert.deepEqual(prompt.buttons, ['cancel', 'selfie_try_again']);
  assert.match(prompt.body, /already being sent/);
});

test('the capture and refusal strings no longer carry the withdraw lever', () => {
  for (const strings of [selfieEn, selfieAr]) {
    assert.equal('withdrawInstead' in strings.capture, false);
    assert.equal('reviewConsent' in strings.punch, false);
    for (const text of [strings.capture.cameraNeededRequiredBody, strings.refusal.selfieRequired.message, strings.refusal.selfieRequired.next, strings.consent.requiredNote]) {
      assert.equal(/withdraw|سحب|اسحب/i.test(text), false, text);
    }
  }
});

// ---- Review 2, item 10: Arabic says تسجيل الحضور, never بصمة (fingerprint) ----

test('no Arabic selfie string uses بصمة or implies biometrics beyond a photo', () => {
  const ar = JSON.stringify(selfieAr);
  assert.equal(ar.includes('بصم'), false);
  for (const word of ['بيومتر', 'حيوي', 'قزحية', 'مطابقة الوجه']) assert.equal(ar.includes(word), false, word);
});

// ---- Review 2, item 2-mobile: the withdrawal notice ----

test('withdrawal notice: deleted, awaiting deletion (about 15 minutes), or nothing to delete', () => {
  assert.deepEqual(parseWithdrawal({ selfie: {}, withdrawal: { unusedSelfiesDeleted: 2, unusedSelfiesAwaitingDeletion: 1 } }), { unusedSelfiesDeleted: 2, unusedSelfiesAwaitingDeletion: 1 });
  assert.equal(parseWithdrawal({ selfie: {} }), null);
  assert.equal(parseWithdrawal(null), null);
  assert.deepEqual(parseWithdrawal({ withdrawal: { unusedSelfiesDeleted: '3', unusedSelfiesAwaitingDeletion: -2 } }), { unusedSelfiesDeleted: 3, unusedSelfiesAwaitingDeletion: 0 });

  assert.equal(withdrawalNoticeKey({ unusedSelfiesDeleted: 2, unusedSelfiesAwaitingDeletion: 1 }), 'selfie.consent.withdrawnPendingToast');
  assert.equal(withdrawalNoticeKey({ unusedSelfiesDeleted: 0, unusedSelfiesAwaitingDeletion: 3 }), 'selfie.consent.withdrawnPendingToast');
  assert.equal(withdrawalNoticeKey({ unusedSelfiesDeleted: 2, unusedSelfiesAwaitingDeletion: 0 }), 'selfie.consent.withdrawnDeletedToast');
  assert.equal(withdrawalNoticeKey({ unusedSelfiesDeleted: 0, unusedSelfiesAwaitingDeletion: 0 }), 'selfie.consent.withdrawnToast');
  assert.equal(withdrawalNoticeKey(null), 'selfie.consent.withdrawnToast');

  assert.match(selfieEn.consent.withdrawnPendingToast, /within about 15 minutes/);
  assert.equal(/deleted now/i.test(selfieEn.consent.withdrawnPendingToast), false);
  assert.match(selfieAr.consent.withdrawnPendingToast, /15 دقيقة/);
});

// ---- Review 2, item 11-mobile: platform header ----

test('X-Client-Platform is android or ios, and absent elsewhere', () => {
  assert.deepEqual(clientPlatformHeaders('android'), { 'X-Client-Platform': 'android' });
  assert.deepEqual(clientPlatformHeaders('ios'), { 'X-Client-Platform': 'ios' });
  for (const other of ['web', 'windows', 'macos', '', 'Android']) assert.deepEqual(clientPlatformHeaders(other), {}, other);
});

// ---- JPEG only ----

test('the capture asks for JPEG (the server accepts JPEG only)', () => {
  assert.equal(SELFIE_IMAGE_TYPE, 'jpg');
});

// ---- Review 2, item 9: the photo never stays on the phone ----

test('selfie file names carry the prefix and are recognised by the sweep', () => {
  const name = selfieFileName(1_760_000_000_000, 0.123456);
  assert.ok(name.startsWith(SELFIE_FILE_PREFIX), name);
  assert.ok(name.endsWith('.jpg'), name);
  assert.equal(isOwnSelfieFile(name), true);
  assert.notEqual(selfieFileName(1_760_000_000_000, 0.5), name);
  assert.equal(isOwnSelfieFile(selfieFileName(0, 0)), true);
  assert.equal(isOwnSelfieFile(selfieFileName(Date.now(), 0.999999)), true);
});

test('the sweep matcher deletes only files the app created', () => {
  const own = [
    'kx-selfie-mgcz1k0w-4fzyo8.jpg',
    'KX-SELFIE-abc-123.JPG',
    'kx-selfie-abc.jpeg',
    'kx-selfie-.jpg',
    'kx-selfie-abc.png',
    'kx-selfie-abc.jpg.bak',
    'selfie-abc.jpg',
    'profile-photo.jpg',
    '../kx-selfie-abc.jpg',
    'nested/kx-selfie-abc.jpg',
    '.kx-selfie-abc.jpg',
  ];
  assert.deepEqual(selfieSweepTargets('own', own), ['kx-selfie-mgcz1k0w-4fzyo8.jpg', 'KX-SELFIE-abc-123.JPG', 'kx-selfie-abc.jpeg']);

  const camera = [
    '3F2504E0-4F89-11D3-9A0C-0305E82C3301.jpg',
    'b1c2d3e4-0000-4a5b-8c9d-0123456789ab.jpg',
    'b1c2d3e4-0000-4a5b-8c9d-0123456789ab.mov',
    'b1c2d3e4-0000-4a5b-8c9d-0123456789ab.mp4',
    'b1c2d3e4-0000-4a5b-8c9d-0123456789ab.png',
    'document-scan.jpg',
    'b1c2d3e4.jpg',
  ];
  assert.deepEqual(selfieSweepTargets('camera', camera), ['3F2504E0-4F89-11D3-9A0C-0305E82C3301.jpg', 'b1c2d3e4-0000-4a5b-8c9d-0123456789ab.jpg']);
  assert.equal(isCameraCaptureFile('kx-selfie-abc.jpg'), false);
  assert.equal(isOwnSelfieFile('3F2504E0-4F89-11D3-9A0C-0305E82C3301.jpg'), false);
});

test('a photo that arrives after the modal closed is not kept', () => {
  const guard = new CaptureGuard();
  const ticket = guard.begin();
  assert.equal(guard.isCurrent(ticket), true);
  guard.close(); // modal closed while the camera was saving
  assert.equal(guard.isCurrent(ticket), false);
  guard.open(); // reopened: the old capture stays stale, a new one is wanted
  assert.equal(guard.isCurrent(ticket), false);
  assert.equal(guard.isCurrent(guard.begin()), true);
});

test('a photo that arrives after the app went to the background, or after a retake, is not kept', () => {
  const guard = new CaptureGuard();
  const first = guard.begin();
  guard.cancel(); // AppState background, or retake
  assert.equal(guard.isCurrent(first), false);
  const second = guard.begin();
  assert.equal(guard.isCurrent(second), true);
  guard.close();
  assert.equal(guard.isCurrent(guard.begin()), false, 'no capture is wanted while the modal is closed');
});

test('review-2 codes map to an existing plain-language refusal', () => {
  assert.equal(REVIEW_API_CODES.mock_detection_required, 'appUpdateRequired');
  assert.equal(REVIEW_API_CODES.selfie_storage_unavailable, 'selfieBusy');
  assert.equal(REVIEW_API_CODES.selfie_upload_interrupted, 'selfieUnusable');
});

// ---- Review 3, 12-note: the iOS app-switcher snapshot never shows the selfie ----

test('the preview is covered while the app is inactive, the photo deleted in the background, shown when active', () => {
  assert.equal(previewActionFor('inactive'), 'cover');   // iOS takes the app-switcher snapshot here
  assert.equal(previewActionFor('background'), 'delete');
  assert.equal(previewActionFor('active'), 'show');
  assert.equal(previewActionFor('unknown'), 'cover');    // anything else fails safe
  assert.equal(previewActionFor('extension'), 'cover');
});

test('hardening: selfie_upload_timeout and selfie_upload_incomplete are a retake/retry, never a waiver', () => {
  assert.equal(REVIEW_API_CODES.selfie_upload_timeout, 'network');
  assert.equal(REVIEW_API_CODES.selfie_upload_incomplete, 'network');
  const echo = (key: string) => `[${key}]`;
  for (const [status, code] of [[408, 'selfie_upload_timeout'], [400, 'selfie_upload_incomplete']] as const) {
    const r = mapPunchRefusal(refusal(status, { code, message: 'Sending took too long.', messageAr: 'استغرق الإرسال وقتًا طويلًا.' }), 'en');
    assert.equal(r.code, code);
    assert.equal(r.key, 'network');
    assert.equal(r.action, 'retry');
    assert.equal(r.punchWithoutSelfie, false, code);
    assert.equal(r.serverMessage, 'Sending took too long.');
    assert.equal(r.titleKey, 'selfie.refusal.network.title'); // an existing string, in both languages
    assert.ok(selfieEn.refusal.network.title.length > 0 && selfieAr.refusal.network.title.length > 0);
    assert.equal(isSelfieBusy(refusal(status, { code })), false); // not auto-retried as busy
    // At the upload: take the selfie again; never "Clock without a selfie", required or optional.
    assert.deepEqual(refusalPrompt(r, 'required', 'upload', echo).buttons, ['cancel', 'selfie_try_again']);
    assert.deepEqual(refusalPrompt(r, 'optional', 'upload', echo).buttons, ['cancel', 'selfie_try_again']);
    // Even if a server ever sent punchWithoutSelfie: false explicitly, nothing changes.
    const explicit = mapPunchRefusal(refusal(status, { code, punchWithoutSelfie: false }), 'ar');
    assert.equal(explicit.punchWithoutSelfie, false);
  }
});
