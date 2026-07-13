import client from './client';

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
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: string;
  user: AuthUser;
}

export interface ForgotPasswordResponse {
  message: string;
  resetToken?: string;
  resetTokenExpiresAtUtc?: string;
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
  login: (email: string, password: string, tenantSlug = '') =>
    client.post<LoginResponse>('/api/auth/login', { email, password, tenantSlug }).then((r) => r.data),

  mfaVerifyChallenge: (challengeToken: string, totpCode: string) =>
    client.post<AuthResponse>('/api/auth/mfa/challenge/verify', { challengeToken, totpCode }).then((r) => r.data),

  mfaSetup: () =>
    client.post<{ provisioningUri: string }>('/api/auth/mfa/setup').then((r) => r.data),

  mfaVerifySetup: (tempSecret: string, totpCode: string) =>
    client.post('/api/auth/mfa/verify-setup', { tempSecret, totpCode }),

  mfaEnrollmentSetup: (enrollmentToken: string) =>
    client.post<{ provisioningUri: string }>('/api/auth/mfa/enrollment/setup', { enrollmentToken }).then((r) => r.data),

  mfaEnrollmentVerifySetup: (enrollmentToken: string, tempSecret: string, totpCode: string) =>
    client.post('/api/auth/mfa/enrollment/verify-setup', { enrollmentToken, tempSecret, totpCode }),

  mfaDisable: (totpCode: string) =>
    client.post('/api/auth/mfa/disable', { totpCode }),

  logout: (refreshToken: string) =>
    client.post('/api/auth/logout', { refreshToken }),

  me: () => client.get<AuthUser>('/api/auth/me').then((r) => r.data),

  forgotPassword: (email: string, tenantSlug?: string) =>
    client.post<ForgotPasswordResponse>('/api/auth/forgot-password', { email, tenantSlug }).then((r) => r.data),

  resetPassword: (email: string, resetToken: string, newPassword: string, tenantSlug?: string) =>
    client.post('/api/auth/reset-password', { email, resetToken, newPassword, tenantSlug }),
};
