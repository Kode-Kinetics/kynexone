// ============================================================
// Selfie attendance v2 + server-side geofence — pure client logic
// ============================================================
//
// Import-free (type imports only) so tests/selfieAttendance.test.ts runs it under node --test.
//
// The server decides everything that matters: whether a punch is inside the site, whether the
// location is accurate or mocked, whether the selfie evidence is valid, and which verification
// method is stored. This module only decides what to ASK the employee for, builds the request
// body, and turns a refusal into plain words with a next step. It never sends a verification
// claim (no verificationMethod / confidenceScore / clientBiometricVerified).
//
// Contract: GET /api/ess/attendance-verification, POST /api/attendance/evidence/selfie,
// POST /api/attendance/punch/{mobile|kiosk}, POST /api/ess/biometric-consent[/withdraw].

export type SelfieStep = 'off' | 'consent_needed' | 'optional' | 'required';

export interface BiometricConsentView {
  policyVersion: string;
  givenAtUtc: string;
  channel: string;
}

export interface GeofenceSiteView {
  name: string;
  latitude: number;
  longitude: number;
  radiusMeters: number;
}

export interface AttendanceVerification {
  selfie: {
    enabled: boolean;
    step: SelfieStep;
    requiredForConsented: boolean;
    currentPolicyVersion: string;
    consent: BiometricConsentView | null;
    evidenceLifetimeSeconds: number;
    maxUploadsPerHour: number;
  };
  geofence: {
    enforced: boolean;
    maxAccuracyMeters: number | null;
    allowMockedLocation: boolean | null;
    sites: GeofenceSiteView[];
  };
}

/** Everything off: what the app assumes when discovery has not answered. */
export const VERIFICATION_OFF: AttendanceVerification = {
  selfie: {
    enabled: false,
    step: 'off',
    requiredForConsented: false,
    currentPolicyVersion: '',
    consent: null,
    evidenceLifetimeSeconds: 600,
    maxUploadsPerHour: 10,
  },
  geofence: { enforced: false, maxAccuracyMeters: null, allowMockedLocation: null, sites: [] },
};

const STEPS: readonly SelfieStep[] = ['off', 'consent_needed', 'optional', 'required'];

function num(value: unknown): number | null {
  const n = typeof value === 'string' && value.trim() !== '' ? Number(value) : value;
  return typeof n === 'number' && Number.isFinite(n) ? n : null;
}

function str(value: unknown): string {
  return typeof value === 'string' ? value : value == null ? '' : String(value);
}

/**
 * Normalises the discovery response defensively. Anything missing or malformed reads as OFF, and
 * the step is made consistent with consent: the app never asks for a selfie from an employee with
 * no consent on record, whatever the step says (rule 6 — the selfie is never mandatory without
 * consent; the server enforces the same).
 */
export function parseAttendanceVerification(raw: unknown): AttendanceVerification {
  if (!raw || typeof raw !== 'object') return VERIFICATION_OFF;
  const r = raw as Record<string, any>;
  const s = (r.selfie && typeof r.selfie === 'object' ? r.selfie : {}) as Record<string, any>;
  const g = (r.geofence && typeof r.geofence === 'object' ? r.geofence : {}) as Record<string, any>;

  const enabled = s.enabled === true;
  const consentRaw = s.consent && typeof s.consent === 'object' ? (s.consent as Record<string, any>) : null;
  const consent: BiometricConsentView | null = consentRaw && str(consentRaw.policyVersion)
    ? { policyVersion: str(consentRaw.policyVersion), givenAtUtc: str(consentRaw.givenAtUtc), channel: str(consentRaw.channel) }
    : null;

  let step: SelfieStep = STEPS.includes(s.step) ? s.step : 'off';
  if (!enabled) step = 'off';
  else if (!consent && (step === 'optional' || step === 'required')) step = 'consent_needed';
  else if (consent && step === 'consent_needed') step = 'optional';

  const sites: GeofenceSiteView[] = Array.isArray(g.sites)
    ? g.sites.flatMap((site: any) => {
      const latitude = num(site?.latitude);
      const longitude = num(site?.longitude);
      const radiusMeters = num(site?.radiusMeters);
      return latitude == null || longitude == null || radiusMeters == null
        ? []
        : [{ name: str(site?.name), latitude, longitude, radiusMeters }];
    })
    : [];
  const enforced = g.enforced === true;

  return {
    selfie: {
      enabled,
      step,
      requiredForConsented: s.requiredForConsented === true,
      currentPolicyVersion: str(s.currentPolicyVersion),
      consent,
      evidenceLifetimeSeconds: num(s.evidenceLifetimeSeconds) ?? 600,
      maxUploadsPerHour: num(s.maxUploadsPerHour) ?? 10,
    },
    geofence: {
      enforced,
      maxAccuracyMeters: enforced ? num(g.maxAccuracyMeters) : null,
      allowMockedLocation: enforced && typeof g.allowMockedLocation === 'boolean' ? g.allowMockedLocation : null,
      sites,
    },
  };
}

// ---- Punch-flow decision ----

export interface PunchPlan {
  /** 'skip': no selfie step. 'optional': offer it with a "clock without a selfie" choice. 'required': ask for it. */
  selfie: 'skip' | 'optional' | 'required';
  /** Show the non-blocking "read and decide" prompt for the consent screen. Never a gate. */
  offerConsent: boolean;
  /** Offer "withdraw consent" next to the selfie step so the employee can switch to punching without one. */
  offerWithdraw: boolean;
  /** The server refuses a punch without a location: block early with a clear message if location is denied. */
  locationRequired: boolean;
  /** Ask the OS for its best fix: the server compares accuracy against this many metres. */
  maxAccuracyMeters: number | null;
}

/**
 * What the punch asks for. `null` (discovery failed or not loaded yet) is treated as everything
 * off: no selfie step, location still sent. If the tenant does enforce something, the server says
 * so with a refusal and the flow re-reads discovery.
 */
export function planPunch(verification: AttendanceVerification | null | undefined): PunchPlan {
  const v = verification ?? VERIFICATION_OFF;
  const step = v.selfie.enabled ? v.selfie.step : 'off';
  const hasConsent = v.selfie.consent != null;
  const selfie: PunchPlan['selfie'] = !hasConsent
    ? 'skip'
    : step === 'required'
      ? 'required'
      : step === 'optional'
        ? 'optional'
        : 'skip';
  return {
    selfie,
    offerConsent: step === 'consent_needed',
    offerWithdraw: selfie !== 'skip',
    locationRequired: v.geofence.enforced,
    maxAccuracyMeters: v.geofence.enforced ? v.geofence.maxAccuracyMeters : null,
  };
}

// ---- Location payload ----

/** The parts of an expo-location LocationObject this module reads. */
export interface DevicePosition {
  coords: { latitude: number; longitude: number; accuracy?: number | null };
  /** Android: isFromMockProvider. Absent on iOS. */
  mocked?: boolean;
}

/** How the server should read the mocked flag (backend review, pending API.md): Android reports it, iOS cannot. */
export type MockDetection = 'Supported' | 'Unsupported';

export interface PunchLocationFields {
  latitude?: number;
  longitude?: number;
  accuracyMeters?: number;
  mockDetection?: MockDetection;
  locationMocked?: boolean;
}

/**
 * The mock-location fields. Kept apart from the coordinates so it is easy to align with the final API:
 * Android sends mockDetection "Supported" with locationMocked true/false (expo-location's isFromMockProvider);
 * iOS (and anything else) sends "Unsupported" with no locationMocked, because the platform exposes no flag.
 */
export function mockDetectionFields(platform: string, mocked: boolean | undefined): Pick<PunchLocationFields, 'mockDetection' | 'locationMocked'> {
  if (platform === 'android') return { mockDetection: 'Supported', locationMocked: mocked === true };
  return { mockDetection: 'Unsupported' };
}

/**
 * Latitude, longitude, the device's reported horizontal accuracy in metres, and the mock-detection
 * fields. Values are passed through unchanged (no rounding that could make a fix look more accurate
 * than it is). An unusable accuracy is omitted, never invented: the server then refuses a
 * geofence-enforced punch with location_inaccurate and the employee is told to turn on precise location.
 */
export function buildLocationFields(position: DevicePosition | null | undefined, platform: string): PunchLocationFields {
  if (!position?.coords) return {};
  const { latitude, longitude, accuracy } = position.coords;
  if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) return {};
  const fields: PunchLocationFields = { latitude, longitude };
  if (typeof accuracy === 'number' && Number.isFinite(accuracy) && accuracy >= 0) fields.accuracyMeters = accuracy;
  return { ...fields, ...mockDetectionFields(platform, position.mocked) };
}

export type PunchDirection = 'In' | 'Out';

export interface PunchBodyInput {
  direction: PunchDirection;
  locationName: string;
  location: PunchLocationFields;
  evidenceId?: string | null;
}

/**
 * The punch request body. employeeId is always 0: the server resolves the caller's own linked
 * employee, so the app can only ever punch for the signed-in user (punches are self-only).
 */
export function buildPunchBody(input: PunchBodyInput): Record<string, unknown> {
  const body: Record<string, unknown> = {
    employeeId: 0,
    punchDirection: input.direction,
    locationName: input.locationName,
    ...input.location,
  };
  if (input.evidenceId) body.evidenceId = input.evidenceId;
  return body;
}

// ---- Refusals ----

export type RefusalKey =
  | 'outsideGeofence'
  | 'locationInaccurate'
  | 'locationMocked'
  | 'locationRequired'
  | 'locationInvalid'
  | 'siteMissing'
  | 'evidenceExpired'
  | 'evidenceUsed'
  | 'evidenceNotFound'
  | 'consentRequired'
  | 'selfieNotEnabled'
  | 'selfieRequired'
  | 'selfieRateLimited'
  | 'selfieUnusable'
  | 'accessMode'
  | 'notLinked'
  | 'appUpdateRequired'
  | 'mobileAppRequired'
  | 'selfieBusy'
  | 'network'
  | 'unknown';

/**
 * What the employee can do next. The screen turns it into buttons:
 * - retry: try the same punch again;
 * - retake_selfie: discard the evidence and take a new selfie;
 * - take_selfie: re-read discovery and run the selfie step;
 * - without_selfie: punch again with no selfie (always allowed: the non-biometric alternative);
 * - open_settings: open the phone's settings (precise location / permissions), then retry;
 * - none: nothing the employee can fix from the app (e.g. HR must set up the site).
 */
export type RefusalAction = 'retry' | 'retake_selfie' | 'take_selfie' | 'without_selfie' | 'open_settings' | 'none';

/**
 * Refusals added by the backend review on a parallel branch, before API.md names their codes. The
 * code strings here are the expected ones; until confirmed, the server's own message is still shown
 * for any code the app does not know, and a 429 that is not the hourly limit reads as selfieBusy.
 * Align these three lines with API.md when it is updated.
 */
export const PENDING_API_CODES: Readonly<Record<string, RefusalKey>> = {
  app_update_required: 'appUpdateRequired',
  mobile_app_required: 'mobileAppRequired',
  selfie_busy: 'selfieBusy',
};

const CODE_TO_KEY: Record<string, RefusalKey> = {
  outside_geofence: 'outsideGeofence',
  location_inaccurate: 'locationInaccurate',
  location_mocked: 'locationMocked',
  location_required: 'locationRequired',
  location_invalid: 'locationInvalid',
  geofence_site_missing: 'siteMissing',
  evidence_expired: 'evidenceExpired',
  evidence_used: 'evidenceUsed',
  evidence_not_found: 'evidenceNotFound',
  consent_required: 'consentRequired',
  selfie_not_enabled: 'selfieNotEnabled',
  feature_not_enabled: 'selfieNotEnabled',
  selfie_required: 'selfieRequired',
  selfie_rate_limited: 'selfieRateLimited',
  selfie_missing: 'selfieUnusable',
  selfie_too_large: 'selfieUnusable',
  selfie_invalid: 'selfieUnusable',
  access_mode_not_allowed: 'accessMode',
  employee_not_linked: 'notLinked',
  ...PENDING_API_CODES,
};

const KEY_TO_ACTION: Record<RefusalKey, RefusalAction> = {
  outsideGeofence: 'retry',
  locationInaccurate: 'open_settings',
  locationMocked: 'retry',
  locationRequired: 'open_settings',
  locationInvalid: 'retry',
  siteMissing: 'none',
  evidenceExpired: 'retake_selfie',
  evidenceUsed: 'retake_selfie',
  evidenceNotFound: 'retake_selfie',
  consentRequired: 'without_selfie',
  selfieNotEnabled: 'without_selfie',
  selfieRequired: 'take_selfie',
  selfieRateLimited: 'without_selfie',
  selfieUnusable: 'retake_selfie',
  accessMode: 'without_selfie',
  notLinked: 'none',
  appUpdateRequired: 'none',
  mobileAppRequired: 'retry',
  selfieBusy: 'retry',
  network: 'retry',
  unknown: 'retry',
};

export interface PunchRefusal {
  /** The server's code, or 'network' / 'unknown'. */
  code: string;
  key: RefusalKey;
  /** i18n keys under the `selfie` namespace. */
  titleKey: string;
  nextKey: string;
  /** The server's plain-language message in the app language, or null to use the local fallback (`messageKey`). */
  serverMessage: string | null;
  messageKey: string;
  action: RefusalAction;
  /** Discovery may be stale (consent or tenant settings changed): re-read it before acting. */
  refreshVerification: boolean;
}

const STALE_DISCOVERY: ReadonlySet<RefusalKey> = new Set<RefusalKey>([
  'consentRequired', 'selfieNotEnabled', 'selfieRequired', 'locationRequired', 'locationInaccurate', 'locationMocked',
  'siteMissing', 'outsideGeofence',
]);

/**
 * Maps any thrown error (axios-shaped or not) to plain words. The server's `message`/`messageAr`
 * are shown as they are; the matching language is preferred and an English-only message is not
 * shown to an Arabic reader (the local Arabic fallback is used instead). Codes the app does not
 * know fall back to `unknown`, keeping the server's message if it has one.
 */
export function mapPunchRefusal(error: unknown, language: string): PunchRefusal {
  const e = error as { response?: { status?: number; data?: unknown }; request?: unknown; code?: string } | null;
  const response = e && typeof e === 'object' ? e.response : undefined;
  const data = (response?.data && typeof response.data === 'object' ? response.data : {}) as Record<string, unknown>;
  const rawCode = typeof data.code === 'string' ? data.code : typeof data.error === 'string' ? data.error : '';

  // A local pre-check can throw an Error carrying a server code (e.g. employee_not_linked) and no response.
  const localCode = !response && e && typeof e.code === 'string' && CODE_TO_KEY[e.code] ? e.code : null;

  let key: RefusalKey;
  if (localCode) key = CODE_TO_KEY[localCode];
  else if (!response) key = 'network';
  else if (CODE_TO_KEY[rawCode]) key = CODE_TO_KEY[rawCode];
  // A fast 429 that is not the hourly upload limit: the server's image processing is busy.
  else if (response.status === 429) key = 'selfieBusy';
  else key = 'unknown';

  const arabic = language.toLowerCase().startsWith('ar');
  const en = typeof data.message === 'string' && data.message.trim() ? data.message.trim() : null;
  const ar = typeof data.messageAr === 'string' && data.messageAr.trim() ? data.messageAr.trim() : null;
  const serverMessage = response ? (arabic ? ar : en) : null;

  return {
    code: localCode ?? (!response ? 'network' : rawCode || 'unknown'),
    key,
    titleKey: `selfie.refusal.${key}.title`,
    nextKey: `selfie.refusal.${key}.next`,
    serverMessage,
    messageKey: `selfie.refusal.${key}.message`,
    action: KEY_TO_ACTION[key],
    refreshVerification: STALE_DISCOVERY.has(key),
  };
}

/** The refusal keys the app knows; tests check each has EN and AR strings. */
/** A busy refusal is retried once, after this delay, before it is shown. */
export const SELFIE_BUSY_RETRY_MS = 2_000;

export function isSelfieBusy(error: unknown): boolean {
  return mapPunchRefusal(error, 'en').key === 'selfieBusy';
}

// ---- Selfie size ----

/** Target long edge for the selfie (the upload is capped at 8 MB; the server re-encodes to ≤ 512 px). */
export const SELFIE_TARGET_LONG_EDGE = 1080;
export const SELFIE_JPEG_QUALITY = 0.8;

/**
 * Picks the camera picture size whose long edge is closest to the target, preferring the larger on a
 * tie. Sizes are the strings expo-camera's getAvailablePictureSizesAsync returns ("1280x720" on Android,
 * presets such as "Photo" or "1920x1080" on iOS); presets without dimensions are ignored. Returns
 * undefined when nothing has dimensions, which leaves the camera default.
 *
 * This sizes the capture itself, so no image-processing native module is needed (adding one would need
 * a new store build).
 */
export function choosePictureSize(sizes: readonly string[] | null | undefined, target = SELFIE_TARGET_LONG_EDGE): string | undefined {
  let best: { size: string; diff: number; edge: number } | undefined;
  for (const size of sizes ?? []) {
    const match = /(\d+)\s*x\s*(\d+)/i.exec(size);
    if (!match) continue;
    const edge = Math.max(Number(match[1]), Number(match[2]));
    if (!Number.isFinite(edge) || edge <= 0) continue;
    const diff = Math.abs(edge - target);
    if (!best || diff < best.diff || (diff === best.diff && edge > best.edge)) best = { size, diff, edge };
  }
  return best?.size;
}

export const REFUSAL_KEYS: readonly RefusalKey[] = Object.keys(KEY_TO_ACTION) as RefusalKey[];
export const KNOWN_REFUSAL_CODES: readonly string[] = Object.keys(CODE_TO_KEY);

// ---- Discovery cache ----

/**
 * A short-lived, in-memory cache of the discovery JSON, keyed by tenant and employee so a
 * different sign-in never reads another person's answer. Never holds images.
 */
export const VERIFICATION_CACHE_MS = 60_000;

export interface VerificationCacheEntry {
  key: string;
  at: number;
  value: AttendanceVerification;
}

export function cacheIsFresh(entry: VerificationCacheEntry | null, key: string, now: number): boolean {
  return entry != null && entry.key === key && now - entry.at >= 0 && now - entry.at < VERIFICATION_CACHE_MS;
}
