import client, { publicAuthClient } from './client';
import { normalizeEmail, normalizeWorkspace, requireWorkspace } from '../lib/publicAuth';
import { DEFAULT_MIN_PASSWORD_LENGTH, normalizeWelcomeCode, toAsciiDigits } from '../lib/welcomeCode';

export interface CompanyAccess {
  id: string;
  name: string;
  code: string;
  countryCode: string;
  isActive: boolean;
}

export interface AuthUser {
  id: string;
  tenantId: string;
  tenantSlug: string;
  email: string;
  fullName: string;
  roles: string[];
  permissions: string[];
  employeeId?: number;
  accessMode?: string;
  requiresPasswordSetup?: boolean;
  /** SingleCompany | Group — drives group UI visibility. */
  accountType?: 'SingleCompany' | 'Group';
  /** True when the user's scope decision is group-wide (sees every company). */
  isGroupScope?: boolean;
  /** The user's ACCESSIBLE active companies — the company switcher's option list. */
  companies?: CompanyAccess[];
  /** Set while HR has issued a reset code for this login that is still unused (date issued). */
  pendingResetNotice?: PendingResetNotice | null;
}

export interface PendingResetNotice {
  /** When HR issued the code (ISO-8601 UTC). */
  date: string;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: string;
  user: AuthUser;
  /** May arrive on the login reply itself as well as on the user (contract Amendment 3). */
  pendingResetNotice?: PendingResetNotice | null;
}

export interface ForgotPasswordResponse {
  message: string;
  /** false when this company cannot send email: only HR's welcome code can help then. */
  emailDeliveryConfigured?: boolean;
}

// Returned by /api/auth/login when the user has TOTP enabled.
export interface MfaChallengeResponse {
  mfaRequired: true;
  challengeToken: string;
  expiresInSeconds: number;
  /**
   * How long this browser may be trusted if the person ticks "Trust this browser" (tenant setting,
   * 0..90). 0, or absent from an older API, means the choice is not offered.
   */
  rememberDeviceDays?: number;
}

/** GET /api/auth/mfa/status — the signed-in user's own two-step standing. */
export interface MfaStatus {
  enabled: boolean;
  /** The user's role can see or change payroll, banking or access, so two-step is mandatory. */
  required: boolean;
  requiredBecause: string | null;
  enforceFromUtc: string | null;
  enforced: boolean;
  promptToEnroll: boolean;
  /** enabled && !required. Absent from an older API: derive it the same way. */
  canDisable?: boolean;
  /** The tenant's trust lifetime in days; 0 = trusted browsers are switched off. */
  rememberDeviceDays?: number;
  trustedDeviceCount?: number;
  /** Unused backup codes left (0 when two-step is off). Absent from an older API: nothing is shown. */
  backupCodesRemaining?: number;
}

/** GET /api/auth/mfa/trusted-devices — one browser that skips the code until it expires. */
export interface TrustedDevice {
  id: string;
  createdAtUtc: string;
  lastUsedAtUtc: string | null;
  expiresAtUtc: string;
  /** A short summary from the user agent, e.g. "Firefox on macOS". */
  browser: string | null;
  ipAddress: string | null;
  /** This is the browser making the request. */
  current: boolean;
}

/**
 * The `code` of a refused two-step change (contract Amendment 1). verify-setup: 400 codeInvalid,
 * 400 setupInvalid (bad or expired setup secret), 409 alreadyEnabled. disable: 400 codeInvalid
 * (wrong or reused code), 409 notEnabled, 409 requiredForRole, 409 requiredByWorkspace.
 * Wave 2: backup-codes 400 codeInvalid / 409 notEnabled; challenge/verify 400 codeInvalid (wrong,
 * used or malformed app or backup code) / 400 codeRequired (neither or both sent); the admin reset
 * (POST /api/access/users/{id}/mfa/reset) 400 resetReason, 403 resetSelf, 409 notEnabled.
 */
export const MFA_ERRORS = {
  codeInvalid: 'ERR_MFA_CODE_INVALID',
  codeRequired: 'ERR_MFA_CODE_REQUIRED',
  setupInvalid: 'ERR_MFA_SETUP_INVALID',
  alreadyEnabled: 'ERR_MFA_ALREADY_ENABLED',
  notEnabled: 'ERR_MFA_NOT_ENABLED',
  requiredForRole: 'ERR_MFA_REQUIRED_FOR_ROLE',
  requiredByWorkspace: 'ERR_MFA_REQUIRED_BY_WORKSPACE',
  resetReason: 'ERR_MFA_RESET_REASON',
  resetSelf: 'ERR_MFA_RESET_SELF',
} as const;

/** The code step sends exactly one of these: the app's 6 digits, or one backup code. */
export type MfaChallengeCode = { totpCode: string } | { backupCode: string };

/**
 * A backup code as typed: Arabic-Indic digits to ASCII, spaces and hyphens dropped, upper case.
 * The server ignores case, spaces and hyphens too; this only makes "complete" checkable here.
 */
export function normalizeBackupCode(raw: string): string {
  return toAsciiDigits(raw).replace(/[\s\-\u2010-\u2015]/g, '').toUpperCase();
}

/**
 * The one-time list from verify-setup / regenerate. A 204 (an API from before backup codes) or a
 * body without the list both mean "no codes to show", never an error.
 */
function backupCodesFrom(data: unknown): string[] | null {
  const list = (data as { backupCodes?: unknown } | null)?.backupCodes;
  if (!Array.isArray(list)) return null;
  const codes = list.filter((c): c is string => typeof c === 'string' && c.trim() !== '');
  return codes.length ? codes : null;
}

export interface MfaEnrollmentResponse {
  mfaEnrollmentRequired: true;
  enrollmentToken: string;
  expiresInSeconds: number;
  message: string;
}

export type LoginResponse = AuthResponse | MfaChallengeResponse | MfaEnrollmentResponse;

export function isMfaChallenge(r: LoginResponse): r is MfaChallengeResponse {
  return (r as MfaChallengeResponse).mfaRequired === true;
}

export function isMfaEnrollment(r: LoginResponse): r is MfaEnrollmentResponse {
  return (r as MfaEnrollmentResponse).mfaEnrollmentRequired === true;
}

export const authApi = {
  /**
   * The workspace is OPTIONAL: without one, the server finds the company from the email's domain.
   * When that is ambiguous it answers 400 `{ code: 'workspace_required' }` and the page asks for it.
   */
  login: (email: string, password: string, tenantSlug?: string) =>
    publicAuthClient.post<LoginResponse>('/api/auth/login', {
      email: normalizeEmail(email),
      password,
      ...optionalWorkspace(tenantSlug),
    }).then((r) => r.data),

  /**
   * Exactly one code: a string is the app's 6-digit code (`totpCode`); `{ backupCode }` sends a
   * backup code instead. `rememberDevice` is sent only when the choice was offered (true asks the
   * server to trust this browser), and NEVER with a backup code: using one never trusts a browser.
   */
  mfaVerifyChallenge: (challengeToken: string, code: string | MfaChallengeCode, rememberDevice?: boolean) => {
    const sent: MfaChallengeCode = typeof code === 'string' ? { totpCode: code } : code;
    const backup = 'backupCode' in sent;
    return publicAuthClient.post<AuthResponse>('/api/auth/mfa/challenge/verify', {
      challengeToken,
      ...(backup ? { backupCode: normalizeBackupCode(sent.backupCode) } : { totpCode: sent.totpCode }),
      ...(backup || rememberDevice === undefined ? {} : { rememberDevice }),
    }).then((r) => r.data);
  },

  mfaSetup: () =>
    client.post<{ provisioningUri: string }>('/api/auth/mfa/setup').then((r) => r.data),

  /** 200 `{ backupCodes }` (shown once), or the older 204: then `null`, and no codes step. */
  mfaVerifySetup: (tempSecret: string, totpCode: string) =>
    client.post('/api/auth/mfa/verify-setup', { tempSecret, totpCode }).then((r) => backupCodesFrom(r.data)),

  mfaEnrollmentSetup: (enrollmentToken: string) =>
    publicAuthClient.post<{ provisioningUri: string }>('/api/auth/mfa/enrollment/setup', { enrollmentToken }).then((r) => r.data),

  /** As mfaVerifySetup, for the setup forced at sign-in. */
  mfaEnrollmentVerifySetup: (enrollmentToken: string, tempSecret: string, totpCode: string) =>
    publicAuthClient.post('/api/auth/mfa/enrollment/verify-setup', { enrollmentToken, tempSecret, totpCode })
      .then((r) => backupCodesFrom(r.data)),

  /**
   * A new set of backup codes; the old set stops working. Needs a current app code. 200
   * `{ backupCodes }`; 400 ERR_MFA_CODE_INVALID; 409 ERR_MFA_NOT_ENABLED.
   */
  mfaRegenerateBackupCodes: (totpCode: string) =>
    client.post<{ backupCodes: string[] }>('/api/auth/mfa/backup-codes', { totpCode }).then((r) => backupCodesFrom(r.data) ?? []),

  /** Two-step standing of the signed-in user (the enrolment prompt and the Sign-in security page). */
  mfaStatus: () =>
    client.get<MfaStatus>('/api/auth/mfa/status').then((r) => r.data),

  /** The browsers that skip the code for this user. */
  mfaTrustedDevices: () =>
    client.get<TrustedDevice[]>('/api/auth/mfa/trusted-devices').then((r) => r.data),

  /** 204; 404 when it is not this user's row or is already gone. */
  mfaForgetTrustedDevice: (id: string) =>
    client.delete(`/api/auth/mfa/trusted-devices/${encodeURIComponent(id)}`).then(() => undefined),

  /** 204: every trusted browser of this user is forgotten, this one included. */
  mfaForgetAllTrustedDevices: () =>
    client.delete('/api/auth/mfa/trusted-devices').then(() => undefined),

  /** Issues a setup-only enrolment token for the signed-in user; the sign-in page completes it. */
  mfaEnrollmentStart: () =>
    client.post<{ enrollmentToken: string; expiresInSeconds: number }>('/api/auth/mfa/enrollment/start').then((r) => r.data),

  mfaDisable: (totpCode: string) =>
    client.post('/api/auth/mfa/disable', { totpCode }),

  logout: (refreshToken: string) =>
    client.post('/api/auth/logout', { refreshToken }),

  me: () => client.get<AuthUser>('/api/auth/me').then((r) => r.data),

  forgotPassword: (email: string, tenantSlug?: string) =>
    publicAuthClient.post<ForgotPasswordResponse>('/api/auth/forgot-password', {
      email: normalizeEmail(email),
      ...optionalWorkspace(tenantSlug),
    }).then((r) => r.data),

  resetPassword: (resetToken: string, newPassword: string, tenantSlug: string) =>
    publicAuthClient.post('/api/auth/reset-password', {
      resetToken,
      newPassword,
      tenantSlug: requireWorkspace(tenantSlug),
    }, { timeout: 15_000 }),

  acceptInvitation: (invitationToken: string, newPassword: string, tenantSlug: string) =>
    publicAuthClient.post('/api/auth/accept-invitation', {
      invitationToken,
      newPassword,
      tenantSlug: requireWorkspace(tenantSlug),
    }, { timeout: 15_000 }).then(() => undefined),

  /**
   * First sign-in: exchange the welcome code HR gave the employee for a password they choose.
   * 200 `{ tenantSlug }` on success, and NO session: the caller signs in with the new password next,
   * using that company ID. Refusals are 400 `{ code }` — code_invalid | code_expired | code_used |
   * password_policy | workspace_required | sign_out_first | try_later | seat_limit — and 429. The code is normalised (Arabic-Indic / Persian digits, spaces, dashes) here
   * as well as on the server, so what is sent is exactly the eight digits on the slip.
   */
  welcomeRedeem: (email: string, code: string, newPassword: string, tenantSlug?: string) =>
    publicAuthClient.post<{ tenantSlug?: string | null }>('/api/auth/welcome/redeem', {
      email: normalizeEmail(email),
      code: normalizeWelcomeCode(code),
      newPassword,
      ...optionalWorkspace(tenantSlug),
    }, { timeout: 15_000 }).then((r) => ({ tenantSlug: normalizeWorkspace(r.data?.tenantSlug) || undefined })),

  /**
   * The company's minimum password length, for the live tick on /welcome. Anonymous; with no company
   * ID the server answers for the platform default. Any failure falls back to the shared floor.
   */
  passwordPolicy: (tenantSlug?: string) =>
    publicAuthClient.get<{ minLength?: number }>('/api/auth/password-policy', {
      params: optionalWorkspace(tenantSlug),
      timeout: 8_000,
    }).then((r) => {
      const n = Number(r.data?.minLength);
      return Number.isInteger(n) && n >= 1 && n <= 128 ? n : DEFAULT_MIN_PASSWORD_LENGTH;
    }).catch(() => DEFAULT_MIN_PASSWORD_LENGTH),
};

/** `{ tenantSlug }` only when one was given: the anonymous endpoints treat a missing slug as "find it from the email". */
function optionalWorkspace(tenantSlug?: string | null): { tenantSlug?: string } {
  const slug = normalizeWorkspace(tenantSlug);
  return slug ? { tenantSlug: slug } : {};
}
