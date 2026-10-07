import client, { publicAuthClient } from './client';
import { normalizeEmail, normalizeWorkspace, requireWorkspace } from '../lib/publicAuth';
import { DEFAULT_MIN_PASSWORD_LENGTH, normalizeWelcomeCode } from '../lib/welcomeCode';

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
}

// Returned by /api/auth/login when the user has TOTP enabled.
export interface MfaChallengeResponse {
  mfaRequired: true;
  challengeToken: string;
  expiresInSeconds: number;
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

  mfaVerifyChallenge: (challengeToken: string, totpCode: string) =>
    publicAuthClient.post<AuthResponse>('/api/auth/mfa/challenge/verify', { challengeToken, totpCode }).then((r) => r.data),

  mfaSetup: () =>
    client.post<{ provisioningUri: string }>('/api/auth/mfa/setup').then((r) => r.data),

  mfaVerifySetup: (tempSecret: string, totpCode: string) =>
    client.post('/api/auth/mfa/verify-setup', { tempSecret, totpCode }),

  mfaEnrollmentSetup: (enrollmentToken: string) =>
    publicAuthClient.post<{ provisioningUri: string }>('/api/auth/mfa/enrollment/setup', { enrollmentToken }).then((r) => r.data),

  mfaEnrollmentVerifySetup: (enrollmentToken: string, tempSecret: string, totpCode: string) =>
    publicAuthClient.post('/api/auth/mfa/enrollment/verify-setup', { enrollmentToken, tempSecret, totpCode }),

  /** Mandatory-MFA standing of the signed-in user (drives the enrolment prompt). */
  mfaStatus: () =>
    client.get<{
      enabled: boolean; required: boolean; requiredBecause: string | null;
      enforceFromUtc: string | null; enforced: boolean; promptToEnroll: boolean;
    }>('/api/auth/mfa/status').then((r) => r.data),

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
