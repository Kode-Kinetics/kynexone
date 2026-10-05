'use client';

import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import { authApi, isMfaChallenge, isMfaEnrollment } from '../api/auth';
import type { AuthUser } from '../api/auth';
import { clearSessionKeepingLocale } from '../api/clearSession';

// Returned when the backend requires a TOTP code before issuing full tokens.
export interface MfaPendingState {
  challengeToken: string;
  expiresInSeconds: number;
}

export interface MfaEnrollmentPendingState {
  enrollmentToken: string;
  expiresInSeconds: number;
}

export type LoginOutcome = 'authenticated' | 'mfa' | 'mfa-enroll';

interface AuthContextValue {
  user: AuthUser | null;
  isLoading: boolean;
  mfaPending: MfaPendingState | null;
  mfaEnrollmentPending: MfaEnrollmentPendingState | null;
  /** Normal credential login. Returns mfaPending state when TOTP is required. */
  login: (email: string, password: string, tenantSlug: string) => Promise<LoginOutcome>;
  /** Complete login after TOTP entry during challenge flow. */
  verifyMfaChallenge: (totpCode: string) => Promise<void>;
  logout: () => Promise<void>;
  hasPermission: (permission: string) => boolean;
  hasRole: (role: string) => boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [mfaPending, setMfaPending] = useState<MfaPendingState | null>(null);
  const [mfaEnrollmentPending, setMfaEnrollmentPending] = useState<MfaEnrollmentPendingState | null>(null);

  useEffect(() => {
    if (['/login', '/reset-password', '/accept-invitation'].includes(window.location.pathname)) {
      setIsLoading(false);
      return;
    }
    const token = localStorage.getItem('zayra_access_token');
    if (!token) {
      setIsLoading(false);
      return;
    }
    authApi
      .me()
      .then(setUser)
      .catch((err: { response?: { status?: number } }) => {
        // Only a definite "not signed in" ends the session. A network error or a 5xx (a deploy's
        // ~38 s of 502s, a cold start) must not sign the user out and wipe their workspace; the
        // API client already clears the session itself when a 401 cannot be refreshed.
        if (err?.response?.status === 401) clearSessionKeepingLocale();
      })
      .finally(() => setIsLoading(false));
  }, []);

  const login = useCallback(async (email: string, password: string, tenantSlug: string) => {
    const res = await authApi.login(email, password, tenantSlug);
    if (isMfaChallenge(res)) {
      // Credentials verified; TOTP step required before tokens are issued.
      setMfaPending({ challengeToken: res.challengeToken, expiresInSeconds: res.expiresInSeconds });
      setMfaEnrollmentPending(null);
      return 'mfa';
    }
    if (isMfaEnrollment(res)) {
      // Credentials verified, but tenant policy requires first-time MFA setup before any session is issued.
      setMfaEnrollmentPending({ enrollmentToken: res.enrollmentToken, expiresInSeconds: res.expiresInSeconds });
      setMfaPending(null);
      return 'mfa-enroll';
    }
    localStorage.setItem('zayra_access_token', res.accessToken);
    localStorage.setItem('zayra_refresh_token', res.refreshToken);
    setMfaPending(null);
    setMfaEnrollmentPending(null);
    setUser(res.user);
    return 'authenticated';
  }, []);

  const verifyMfaChallenge = useCallback(async (totpCode: string) => {
    if (!mfaPending) throw new Error('No MFA challenge in progress.');
    const res = await authApi.mfaVerifyChallenge(mfaPending.challengeToken, totpCode);
    localStorage.setItem('zayra_access_token', res.accessToken);
    localStorage.setItem('zayra_refresh_token', res.refreshToken);
    setMfaPending(null);
    setMfaEnrollmentPending(null);
    setUser(res.user);
  }, [mfaPending]);

  const logout = useCallback(async () => {
    const refreshToken = localStorage.getItem('zayra_refresh_token') ?? '';
    try {
      await authApi.logout(refreshToken);
    } catch {
      // ignore
    }
    // Same wipe as an expired session: a shared computer keeps nothing of this user's
    // (search history, company selection, import history), only the display language.
    clearSessionKeepingLocale();
    setUser(null);
    setMfaPending(null);
    setMfaEnrollmentPending(null);
  }, []);

  const hasPermission = useCallback(
    (permission: string) => user?.permissions.includes(permission) ?? false,
    [user],
  );

  const hasRole = useCallback(
    (role: string) => user?.roles.includes(role) ?? false,
    [user],
  );

  return (
    <AuthContext.Provider value={{ user, isLoading, mfaPending, mfaEnrollmentPending, login, verifyMfaChallenge, logout, hasPermission, hasRole }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
