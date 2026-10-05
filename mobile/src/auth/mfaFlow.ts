// ============================================================
// KynexOne Mobile — two-step sign-in (TOTP) response handling
// ============================================================
//
// Pure, import-free state machine for the backend's MFA contract
// (backend-dotnet/Zayra.Api: AuthController.Login, MfaController,
// PrivilegedMfaPolicy). Kept free of React Native so it runs under node --test.
//
// Contract notes that shape this module:
// - POST /auth/login answers 200 with ONE of: session tokens; { mfaRequired,
//   challengeToken, expiresInSeconds } (factor enrolled); or
//   { mfaEnrollmentRequired, enrollmentToken, expiresInSeconds, message }
//   (enrolment is mandatory now). The grace period is NOT visible here: it
//   returns ordinary tokens and the client must ask GET /auth/mfa/status.
// - Challenge and enrolment verification answer every rejection (wrong code,
//   5th wrong code, expired, already used) with the same 401 message. The
//   client therefore counts attempts and tracks expiry itself; the server's
//   limit is MfaChallengeToken.MaxAttempts = 5 and both tokens live 300 s.
//
// Nothing in this module logs, stores or forwards a secret, code or token.

export const MFA_MAX_ATTEMPTS = 5;
export const MFA_DEFAULT_TTL_SECONDS = 300;
const MFA_MAX_TTL_SECONDS = 3600;
export const MFA_CODE_LENGTH = 6;

// ---- Login response ------------------------------------------------------

export type LoginStep =
  | { kind: 'authenticated'; payload: unknown }
  | { kind: 'mfaChallenge'; challengeToken: string; expiresInSeconds: number }
  | { kind: 'mfaEnrollment'; enrollmentToken: string; expiresInSeconds: number };

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : null;
}

export function normalizeTtl(value: unknown): number {
  const seconds = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(seconds) || seconds <= 0) return MFA_DEFAULT_TTL_SECONDS;
  return Math.min(Math.floor(seconds), MFA_MAX_TTL_SECONDS);
}

/** Decides which step follows the password. Throws on a malformed MFA reply. */
export function classifyLoginResponse(data: unknown): LoginStep {
  const body = asRecord(data);
  if (body?.mfaRequired === true) {
    const challengeToken = typeof body.challengeToken === 'string' ? body.challengeToken : '';
    if (!challengeToken) throw new Error('The server returned an invalid sign-in code request.');
    return { kind: 'mfaChallenge', challengeToken, expiresInSeconds: normalizeTtl(body.expiresInSeconds) };
  }
  if (body?.mfaEnrollmentRequired === true) {
    const enrollmentToken = typeof body.enrollmentToken === 'string' ? body.enrollmentToken : '';
    if (!enrollmentToken) throw new Error('The server returned an invalid two-step setup request.');
    return { kind: 'mfaEnrollment', enrollmentToken, expiresInSeconds: normalizeTtl(body.expiresInSeconds) };
  }
  return { kind: 'authenticated', payload: data };
}

// ---- Signed-in standing (GET /auth/mfa/status) -----------------------------

export type MfaPrompt =
  | { kind: 'none' }
  /** Not enrolled, sign-in still allowed. enforceFromUtc is null when no future date applies. */
  | { kind: 'grace'; enforceFromUtc: string | null }
  /** Not enrolled and the rule already applies; the session will not be renewed. */
  | { kind: 'enforced' };

export function parseUtc(value: unknown): number | null {
  if (typeof value !== 'string' || !value.trim()) return null;
  const text = value.trim();
  // ASP.NET serialises a UTC DateTime with "Z"; an offset-less value is UTC by contract.
  const explicit = /([zZ]|[+-]\d{2}:?\d{2})$/.test(text) ? text : `${text}Z`;
  const ms = Date.parse(explicit);
  return Number.isFinite(ms) ? ms : null;
}

export function classifyMfaStatus(data: unknown, nowMs: number): MfaPrompt {
  const body = asRecord(data);
  if (!body || body.enabled === true) return { kind: 'none' };
  if (body.enforced === true) return { kind: 'enforced' };
  if (body.promptToEnroll !== true) return { kind: 'none' };
  const enforceAt = parseUtc(body.enforceFromUtc);
  // A date already in the past while the server still says "not enforced" means
  // break-glass is suspending enforcement: prompt, but do not quote a past date.
  return {
    kind: 'grace',
    enforceFromUtc: enforceAt !== null && enforceAt > nowMs ? new Date(enforceAt).toISOString() : null,
  };
}

// ---- Errors --------------------------------------------------------------

/**
 * notAccepted: a 401 right after a network or server failure. The earlier
 * request may have reached the server (and used up the code or the request),
 * so "wrong code" could be untrue; the wording stays neutral.
 */
export type MfaErrorKind = 'wrongCode' | 'notAccepted' | 'waitForNextCode' | 'attemptLimit' | 'expired' | 'rateLimited' | 'network' | 'server';

export type MfaFailureClass = 'rejected' | 'rateLimited' | 'network' | 'server' | 'conflict';

/** Classifies an axios-shaped failure without reading (or echoing) its body. */
export function classifyMfaFailure(error: unknown): MfaFailureClass {
  const record = asRecord(error);
  const response = asRecord(record?.response);
  const status = typeof response?.status === 'number' ? response.status : undefined;
  if (status === undefined) return record?.isAxiosError === true || record?.request ? 'network' : 'server';
  // 401 is the backend's only "code/request not accepted" answer. A 400 is a
  // malformed request (validation), which no code the user types can fix.
  if (status === 401) return 'rejected';
  if (status === 409) return 'conflict';
  if (status === 429) return 'rateLimited';
  return 'server';
}

// ---- Code entry state machine ----------------------------------------------

export type CodeEntryPhase = 'ready' | 'submitting' | 'succeeded' | 'locked' | 'expired';

export interface CodeEntryState {
  phase: CodeEntryPhase;
  failedAttempts: number;
  expiresAtMs: number;
  error: MfaErrorKind | null;
  /** The previous attempt ended in a network or server failure. */
  afterTransientFailure: boolean;
  /**
   * First sign-in right after enrolling. The backend refuses a TOTP code whose
   * time-step was already accepted, and enrolment accepted one, so the likeliest
   * reason the first code fails is that it is the enrolment code again.
   */
  justEnrolled: boolean;
}

export type CodeEntryEvent =
  | { type: 'edited' }
  | { type: 'submit'; nowMs: number }
  | { type: 'succeeded' }
  | { type: 'failed'; failure: MfaFailureClass; nowMs: number }
  | { type: 'tick'; nowMs: number };

export function initialCodeEntry(
  expiresInSeconds: number,
  nowMs: number,
  options: { justEnrolled?: boolean } = {}
): CodeEntryState {
  return {
    phase: 'ready',
    failedAttempts: 0,
    expiresAtMs: nowMs + normalizeTtl(expiresInSeconds) * 1000,
    error: null,
    afterTransientFailure: false,
    justEnrolled: options.justEnrolled === true,
  };
}

export function codeEntryReducer(state: CodeEntryState, event: CodeEntryEvent): CodeEntryState {
  if (state.phase === 'succeeded' || state.phase === 'locked' || state.phase === 'expired') return state;
  switch (event.type) {
    case 'edited':
      return state.error && state.phase === 'ready' ? { ...state, error: null } : state;
    case 'submit':
      if (state.phase !== 'ready') return state;
      if (event.nowMs >= state.expiresAtMs) return { ...state, phase: 'expired', error: 'expired' };
      return { ...state, phase: 'submitting', error: null };
    case 'succeeded':
      return state.phase === 'submitting' ? { ...state, phase: 'succeeded', error: null } : state;
    case 'failed': {
      if (state.phase !== 'submitting') return state;
      if (event.failure === 'rejected') {
        if (event.nowMs >= state.expiresAtMs) return { ...state, phase: 'expired', error: 'expired' };
        const failedAttempts = state.failedAttempts + 1;
        if (failedAttempts >= MFA_MAX_ATTEMPTS) {
          return { ...state, phase: 'locked', failedAttempts, error: 'attemptLimit', afterTransientFailure: false };
        }
        return {
          ...state,
          phase: 'ready',
          failedAttempts,
          error: state.afterTransientFailure ? 'notAccepted'
            : state.justEnrolled && failedAttempts === 1 ? 'waitForNextCode'
            : 'wrongCode',
          afterTransientFailure: false,
        };
      }
      // Transport, throttling and server faults never count as a wrong code.
      if (event.failure === 'rateLimited') return { ...state, phase: 'ready', error: 'rateLimited' };
      return {
        ...state,
        phase: 'ready',
        error: event.failure === 'network' ? 'network' : 'server',
        afterTransientFailure: true,
      };
    }
    case 'tick':
      return state.phase === 'ready' && event.nowMs >= state.expiresAtMs
        ? { ...state, phase: 'expired', error: 'expired' }
        : state;
    default:
      return state;
  }
}

export function attemptsLeft(state: CodeEntryState): number {
  return Math.max(0, MFA_MAX_ATTEMPTS - state.failedAttempts);
}

export function secondsLeft(state: CodeEntryState, nowMs: number): number {
  return Math.max(0, Math.ceil((state.expiresAtMs - nowMs) / 1000));
}

/**
 * What a screen reader announces for the setup key: one character at a time,
 * with a pause between groups, so "JBSW Y3DP" is not read as words.
 */
export function spellSecretForScreenReader(secret: string, size = 4): string {
  return groupSecret(secret, size)
    .split(' ')
    .filter(Boolean)
    .map((group) => group.split('').join(' '))
    .join(', ');
}

export function normalizeCode(text: string): string {
  return text.replace(/\D/g, '').slice(0, MFA_CODE_LENGTH);
}

export function formatCountdown(seconds: number): string {
  const safe = Math.max(0, Math.floor(seconds));
  return `${Math.floor(safe / 60)}:${String(safe % 60).padStart(2, '0')}`;
}

// ---- Enrolment helpers -------------------------------------------------------

/** The backend returns only the otpauth URI; verify-setup needs the base32 secret from it. */
export function secretFromProvisioningUri(provisioningUri: string): string {
  const match = /[?&]secret=([^&#]+)/i.exec(provisioningUri ?? '');
  if (!match) return '';
  let secret: string;
  try {
    secret = decodeURIComponent(match[1]);
  } catch {
    return '';
  }
  secret = secret.replace(/[\s-]/g, '').toUpperCase();
  return /^[A-Z2-7]+=*$/.test(secret) ? secret.replace(/=+$/, '') : '';
}

/** "JBSWY3DPEHPK3PXP" -> "JBSW Y3DP EHPK 3PXP" for reading and typing by hand. */
export function groupSecret(secret: string, size = 4): string {
  const clean = secret.replace(/\s/g, '');
  return clean.match(new RegExp(`.{1,${size}}`, 'g'))?.join(' ') ?? '';
}

/**
 * The link the "Open authenticator app" button follows: the backend's own
 * otpauth URI when it carries the same secret, otherwise one built from it.
 */
export function authenticatorUri(provisioningUri: string, secret: string, account: string, issuer = 'KynexOne'): string {
  if (/^otpauth:\/\/totp\//i.test(provisioningUri ?? '') && secretFromProvisioningUri(provisioningUri) === secret) {
    return provisioningUri;
  }
  const label = `${encodeURIComponent(issuer)}:${encodeURIComponent(account)}`;
  return `otpauth://totp/${label}?secret=${secret}&issuer=${encodeURIComponent(issuer)}&algorithm=SHA1&digits=6&period=30`;
}

/** One-time recovery codes, only if the server actually returned some. Tenant enrolment returns 204 today. */
export function parseRecoveryCodes(data: unknown): string[] | null {
  const codes = asRecord(data)?.recoveryCodes;
  if (!Array.isArray(codes)) return null;
  const valid = codes.filter((code): code is string => typeof code === 'string' && code.trim().length > 0);
  return valid.length > 0 ? valid : null;
}

/** Calendar date for the banner, in the reader's language; ISO date if Intl is unavailable. */
export function formatEnforceDate(isoUtc: string, language: string): string {
  const ms = parseUtc(isoUtc);
  if (ms === null) return '';
  const date = new Date(ms);
  try {
    return date.toLocaleDateString(language === 'ar' ? 'ar' : 'en-GB', { day: 'numeric', month: 'long', year: 'numeric' });
  } catch {
    return date.toISOString().slice(0, 10);
  }
}
