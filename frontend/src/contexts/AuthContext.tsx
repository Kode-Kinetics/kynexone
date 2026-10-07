'use client';

import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import { authApi, isMfaChallenge, isMfaEnrollment } from '../api/auth';
import type { AuthResponse, AuthUser } from '../api/auth';
import { afterMeFailure, afterMeSuccess, type AuthLoadError } from '../lib/authLoadState';
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
  /**
   * Set when the session could not be confirmed for a reason OTHER than being signed out: the
   * server could not be reached ('network') or answered with an error ('server'). The tokens are
   * kept; screens show "can't reach the server" with a Retry instead of redirecting to /login.
   */
  authError: AuthLoadError | null;
  /** Re-run the session check (`/me`) after an authError. */
  retryAuth: () => Promise<void>;
  mfaPending: MfaPendingState | null;
  mfaEnrollmentPending: MfaEnrollmentPendingState | null;
  /** Normal credential login. Returns mfaPending state when TOTP is required. */
  login: (email: string, password: string, tenantSlug?: string) => Promise<LoginOutcome>;
  /** Complete login after TOTP entry during challenge flow. */
  verifyMfaChallenge: (totpCode: string) => Promise<void>;
  /** From a signed-in session: obtain an enrolment token, end this session, and hand the token to
   *  the sign-in page's existing "Set up two-factor authentication" step. */
  beginMfaEnrollment: () => Promise<void>;
  logout: () => Promise<void>;
  hasPermission: (permission: string) => boolean;
  hasRole: (role: string) => boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

/** HR's live reset code may be reported on the reply or on the user; keep it on the user either way. */
function withResetNotice(res: AuthResponse): AuthUser {
  return { ...res.user, pendingResetNotice: res.user.pendingResetNotice ?? res.pendingResetNotice ?? null };
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [mfaPending, setMfaPending] = useState<MfaPendingState | null>(null);
  const [mfaEnrollmentPending, setMfaEnrollmentPending] = useState<MfaEnrollmentPendingState | null>(null);

  const [authError, setAuthError] = useState<AuthLoadError | null>(null);

  // Ask the server who the user is. Only a 401 ends the session (lib/authLoadState.ts): a network
  // error or a 5xx keeps the tokens and sets authError, so an outage never signs anyone out.
  const loadUser = useCallback(async () => {
    let next;
    try {
      next = afterMeSuccess(await authApi.me());
    } catch (err) {
      next = afterMeFailure<AuthUser>(err);
    }
    if (next.clearSession) clearSessionKeepingLocale();
    setUser(next.user);
    setAuthError(next.authError);
  }, []);

  useEffect(() => {
    if (['/login', '/reset-password', '/accept-invitation', '/welcome'].includes(window.location.pathname)) {
      setIsLoading(false);
      return;
    }
    const token = localStorage.getItem('zayra_access_token');
    if (!token) {
      setIsLoading(false);
      return;
    }
    loadUser().finally(() => setIsLoading(false));
  }, [loadUser]);

  // The offline screen shows its own "Retrying…" state, so a retry does not flip isLoading (which
  // would swap that screen for a bare spinner and back).
  const retryAuth = loadUser;

  const login = useCallback(async (email: string, password: string, tenantSlug?: string) => {
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
    setAuthError(null);
    setUser(withResetNotice(res));
    return 'authenticated';
  }, []);

  const verifyMfaChallenge = useCallback(async (totpCode: string) => {
    if (!mfaPending) throw new Error('No MFA challenge in progress.');
    const res = await authApi.mfaVerifyChallenge(mfaPending.challengeToken, totpCode);
    localStorage.setItem('zayra_access_token', res.accessToken);
    localStorage.setItem('zayra_refresh_token', res.refreshToken);
    setMfaPending(null);
    setMfaEnrollmentPending(null);
    setAuthError(null);
    setUser(withResetNotice(res));
  }, [mfaPending]);

  const beginMfaEnrollment = useCallback(async () => {
    const { enrollmentToken, expiresInSeconds } = await authApi.mfaEnrollmentStart();
    // NOT authApi.logout(): logout rotates the session stamp, and the enrolment token is bound to
    // the current stamp, so it would be dead on arrival. Completing enrolment rotates the stamp
    // itself, which ends this session server-side; here we only drop the local copy, the same way
    // every other session exit does (the display language survives, nothing else of this user does).
    clearSessionKeepingLocale();
    setUser(null);
    setMfaPending(null);
    setMfaEnrollmentPending({ enrollmentToken, expiresInSeconds });
  }, []);

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
    setAuthError(null);
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
    <AuthContext.Provider value={{ user, isLoading, authError, retryAuth, mfaPending, mfaEnrollmentPending, login, verifyMfaChallenge, beginMfaEnrollment, logout, hasPermission, hasRole }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
