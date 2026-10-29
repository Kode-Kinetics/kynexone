// ============================================================
// KynexOne Mobile — Auth Store (Zustand)
// ============================================================

import { create } from 'zustand';
import * as Device from 'expo-device';
import Constants from 'expo-constants';
import { Platform } from 'react-native';
import { setErrorTrackingTenant } from '@/observability/reportError';
import { useCompatibilityStore } from '@/safety/compatibilityStore';
import { tokenStorage, userStorage, clearAllAuthData, appStorage } from '@/storage';
import { STORAGE_KEYS } from '@/config';
import { authApi, deviceApi, type LoginOutcome } from '@/api/services';
import { initApiClient, setSessionExpiredHandler } from '@/api/client';
import { isDefinitiveAuthRefusal } from '@/api/sessionErrors';
import { ResponseShapeError } from '@/api/schemas';
import type { AuthTokens, AuthUser } from '@/types';
import { generateDeviceId } from '@/utils/device';
import { registerPushToken } from '@/features/notifications/pushNotifications';
import { deriveMobileAccess, hasEffectivePermission } from './accessPolicy';
import { normalizeEmail, normalizeWorkspace, requireWorkspace } from './publicAuthInput';
import { isWelcomeCode } from './welcomeCode';
import type { MfaPrompt } from './mfaFlow';
import { currentSessionGeneration, invalidateSession, isCurrentSession, requireCurrentSession, writeSession } from './sessionGeneration';

interface AuthState {
  user: AuthUser | null;
  tenantId: string | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  isInitialized: boolean;
  error: string | null;
  sessionExpired: boolean;
  /** Tokens are kept but /auth/me could not be reached (offline, timeout, 5xx): show the retry screen, not sign-in. */
  connectionIssue: boolean;
  /** Mandatory two-step sign-in standing for the signed-in user (null = unknown / not asked yet). */
  mfaPrompt: MfaPrompt | null;
  mfaPromptDismissed: boolean;
  /** Set after a signed-in enrolment ended the session, so the sign-in screen can explain why. */
  mfaEnrolledNotice: { email: string; tenantId: string } | null;

  initialize: () => Promise<void>;
  /** The company ID is optional: without it the server finds the company from the email. */
  login: (username: string, password: string, tenantId?: string) => Promise<LoginOutcome>;
  completeMfa: (challengeToken: string, totpCode: string, tenantId: string) => Promise<void>;
  refreshMfaStatus: () => Promise<void>;
  dismissMfaPrompt: () => void;
  endSessionAfterMfaEnrollment: () => Promise<void>;
  consumeMfaEnrolledNotice: () => void;
  finishLogin: (user: AuthUser, tokens: AuthTokens, tenantId: string) => Promise<void>;
  logout: () => Promise<void>;
  refreshUser: () => Promise<void>;
  clearError: () => void;
  handleSessionExpired: () => void;
  hasPermission: (module: string, action: string) => boolean;
  canAccess: (module: string) => boolean;
}

export const useAuthStore = create<AuthState>((set, get) => ({
  user: null,
  tenantId: null,
  isAuthenticated: false,
  isLoading: false,
  isInitialized: false,
  error: null,
  sessionExpired: false,
  connectionIssue: false,
  mfaPrompt: null,
  mfaPromptDismissed: false,
  mfaEnrolledNotice: null,

  initialize: async () => {
    // Only cold start, an active session, or its recovery screen can restore credentials.
    // A queued retry after logout starts must not re-open storage before cleanup finishes.
    const state = get();
    if (state.isInitialized && !state.isAuthenticated && !state.connectionIssue) return;
    const generation = currentSessionGeneration();
    // Keep the recovery view mounted throughout session retry; only terminal outcomes clear it.
    set({ isLoading: true });
    try {
      const [storedUser, tenantId] = await Promise.all([
        userStorage.getUser(),
        appStorage.get<string>('zayra_tenant_id'),
      ]);

      if (!isCurrentSession(generation)) return;
      const normalizedTenant = normalizeWorkspace(tenantId);
      if (!storedUser || !normalizedTenant) {
        if (!await writeSession(generation, clearAllAuthData) || !isCurrentSession(generation)) return;
        set({ isInitialized: true, isLoading: false, connectionIssue: false });
        return;
      }

      const tokenExpired = await tokenStorage.isTokenExpired();
      if (!isCurrentSession(generation)) return;
      if (tokenExpired) {
        const refreshToken = await tokenStorage.getRefreshToken();
        if (!isCurrentSession(generation)) return;
        if (!refreshToken) {
          if (!await writeSession(generation, clearAllAuthData) || !isCurrentSession(generation)) return;
          set({ isInitialized: true, isLoading: false, connectionIssue: false });
          return;
        }
      }

      if (normalizedTenant !== tenantId &&
          !await writeSession(generation, () => appStorage.set('zayra_tenant_id', normalizedTenant))) return;
      if (!isCurrentSession(generation)) return;
      initApiClient(normalizedTenant);
      setSessionExpiredHandler(() => get().handleSessionExpired());

      try {
        const freshUser = await authApi.getMe();
        if (!await writeSession(generation, () => userStorage.saveUser(freshUser)) || !isCurrentSession(generation)) return;
        set({
          user: freshUser,
          tenantId: normalizedTenant,
          isAuthenticated: true,
          isInitialized: true,
          isLoading: false,
          sessionExpired: false,
          connectionIssue: false,
        });
        void get().refreshMfaStatus();
      } catch (error) {
        if (!isCurrentSession(generation)) return;
        // /auth/me is the authoritative role/access graph. Cached permissions
        // cannot reopen MainTabs when that graph is unavailable or rejected;
        // an offline mode would need a separate restricted design and TTL.
        if (error instanceof ResponseShapeError) {
          // The server answered, but in a shape this app version cannot read. Not a sign-out and not
          // a retry loop: the compatibility store (set by the client) shows the update screen.
          set({ user: null, isAuthenticated: false, isInitialized: true, isLoading: false, connectionIssue: false });
          return;
        }
        if (!isDefinitiveAuthRefusal(error)) {
          // Timeout, airplane mode, 5xx: the credentials were not judged. Keep the
          // tokens and show the retry screen instead of making the user sign in again.
          set({ user: null, isAuthenticated: false, isInitialized: true, isLoading: false, connectionIssue: true });
          return;
        }
        if (!await writeSession(generation, clearAllAuthData) || !isCurrentSession(generation)) return;
        set({
          user: null,
          tenantId: null,
          isAuthenticated: false,
          isInitialized: true,
          isLoading: false,
          sessionExpired: true,
          connectionIssue: false,
        });
      }
    } catch {
      if (!isCurrentSession(generation)) return;
      if (!await writeSession(generation, clearAllAuthData) || !isCurrentSession(generation)) return;
      set({ isInitialized: true, isLoading: false, isAuthenticated: false, connectionIssue: false });
    }
  },

  finishLogin: async (user, tokens, tenantId) => {
    const generation = invalidateSession();
    if (!await writeSession(generation, async () => {
      await tokenStorage.saveTokens(tokens);
      await userStorage.saveUser(user);
      await appStorage.set('zayra_tenant_id', tenantId);
    }) || !isCurrentSession(generation)) return;

    initApiClient(tenantId);
    setSessionExpiredHandler(() => get().handleSessionExpired());

    // Device and push registration are non-blocking session enhancements: a
    // permission denial or provider outage must never stop a valid sign-in.
    try {
      const deviceId = await generateDeviceId();
      if (!isCurrentSession(generation)) return;
      await deviceApi.register({
        deviceId,
        platform: Platform.OS,
        model: Device.modelName ?? 'Unknown',
        osVersion: Device.osVersion ?? 'Unknown',
        appVersion: Constants.expoConfig?.version ?? '1.0.0',
      });
      if (!await writeSession(generation, () => appStorage.set(STORAGE_KEYS.DEVICE_ID, deviceId)) || !isCurrentSession(generation)) return;
      void registerPushToken(deviceId).catch((error) =>
        console.warn('[Auth] Push registration failed:', error)
      );
    } catch (deviceError) {
      console.warn('[Auth] Device registration failed:', deviceError);
    }

    if (!isCurrentSession(generation)) return;
    useCompatibilityStore.getState().reset();
    set({
      user,
      tenantId,
      isAuthenticated: true,
      isInitialized: true,
      isLoading: false,
      error: null,
      connectionIssue: false,
      sessionExpired: false,
      mfaPrompt: null,
      mfaPromptDismissed: false,
      mfaEnrolledNotice: null,
    });
    // Grace-period standing is not in the login reply; ask once the session exists.
    void get().refreshMfaStatus();
  },

  login: async (username, password, tenantId) => {
    let generation = invalidateSession();
    set({ isLoading: true, error: null });
    try {
      const requested = normalizeWorkspace(tenantId);
      const email = normalizeEmail(username);
      if (!email) throw new Error('Work email is required.');
      const outcome = await authApi.login(email, password, requested || undefined);
      requireCurrentSession(generation);
      if (outcome.kind === 'authenticated') {
        // Signed in by email alone: the session names its company, and the API client needs it.
        const finishing = get().finishLogin(outcome.user, outcome.tokens, requireWorkspace(requested || outcome.user.tenantSlug));
        generation = currentSessionGeneration(); // finishLogin invalidates synchronously before its first await
        await finishing;
      } else {
        set({ isLoading: false });
      }
      return outcome;
    } catch (error: unknown) {
      // The sign-in screen answers these itself (ask for the company ID; or send a welcome code typed
      // as a password to the welcome screen), so no generic alert is raised for them.
      if (isCurrentSession(generation))
        set({ isLoading: false, error: handledBySignInScreen(error, password) ? null : extractAuthError(error) });
      throw error;
    }
  },

  // The code screen owns its own submitting/error state (see mfaFlow.codeEntryReducer).
  // Touching the shared isLoading/error here would raise the sign-in screen's alert
  // underneath the code screen for every wrong code.
  completeMfa: async (challengeToken, totpCode, tenantId) => {
    const generation = currentSessionGeneration();
    const requested = normalizeWorkspace(tenantId);
    const session = await authApi.verifyMfaChallenge(challengeToken, totpCode, requested);
    requireCurrentSession(generation);
    await get().finishLogin(session.user, session.tokens, requireWorkspace(requested || session.user.tenantSlug));
  },

  refreshMfaStatus: async () => {
    const generation = currentSessionGeneration();
    const requestedFor = get().user?.id;
    if (!requestedFor) return;
    try {
      const mfaPrompt = await authApi.getMfaStatus();
      // A reply that lands after sign-out or an account switch belongs to someone else.
      const { isAuthenticated, user } = get();
      if (isCurrentSession(generation) && isAuthenticated && user?.id === requestedFor) set({ mfaPrompt });
    } catch {
      // Advisory only: a missing status must never block or end a valid session.
      // The error is not logged because it carries request headers.
    }
  },

  dismissMfaPrompt: () => set({ mfaPromptDismissed: true }),

  endSessionAfterMfaEnrollment: async () => {
    // Turning the factor on rotates the server-side session stamp, so this
    // session's tokens are already dead; calling /auth/logout would only 401.
    const { user, tenantId } = get();
    const generation = invalidateSession();
    if (!await writeSession(generation, clearAllAuthData) || !isCurrentSession(generation)) return;
    useCompatibilityStore.getState().reset();
    set({
      user: null,
      tenantId: null,
      isAuthenticated: false,
      isLoading: false,
      connectionIssue: false,
      sessionExpired: false,
      mfaPrompt: null,
      mfaPromptDismissed: false,
      mfaEnrolledNotice: user && tenantId ? { email: user.email, tenantId } : null,
    });
  },

  consumeMfaEnrolledNotice: () => set({ mfaEnrolledNotice: null }),

  logout: async () => {
    // Invalidate before any await: an in-flight retry cannot revive this account.
    const generation = invalidateSession();
    useCompatibilityStore.getState().reset();
    set({ isLoading: true, isInitialized: true, user: null, tenantId: null, isAuthenticated: false, connectionIssue: false });
    try {
      const refreshToken = await tokenStorage.getRefreshToken();
      const deviceId = await appStorage.get<string>(STORAGE_KEYS.DEVICE_ID);
      if (!isCurrentSession(generation)) return;
      await Promise.allSettled([
        refreshToken ? authApi.logout(refreshToken) : Promise.resolve(),
        deviceId ? deviceApi.unregister(deviceId) : Promise.resolve(),
      ]);
    } finally {
      if (await writeSession(generation, clearAllAuthData) && isCurrentSession(generation)) {
        // After a response-shape error, sign-out must restore the normal auth navigator.
        useCompatibilityStore.getState().reset();
        set({
          user: null,
          tenantId: null,
          isAuthenticated: false,
          isLoading: false,
          connectionIssue: false,
          sessionExpired: false,
          mfaPrompt: null,
          mfaPromptDismissed: false,
        });
      }
    }
  },

  refreshUser: async () => {
    if (!get().isAuthenticated) return;
    const generation = currentSessionGeneration();
    try {
      const freshUser = await authApi.getMe();
      if (!await writeSession(generation, () => userStorage.saveUser(freshUser)) || !isCurrentSession(generation)) return;
      set({ user: freshUser });
    } catch (error) {
      if (isCurrentSession(generation)) console.warn('[Auth] refreshUser failed:', error);
    }
  },

  clearError: () => set({ error: null }),

  handleSessionExpired: () => {
    const generation = invalidateSession();
    void writeSession(generation, clearAllAuthData);
    useCompatibilityStore.getState().reset();
    set({
      connectionIssue: false,
      user: null,
      tenantId: null,
      isAuthenticated: false,
      sessionExpired: true,
      isInitialized: true,
      isLoading: false,
      mfaPrompt: null,
      mfaPromptDismissed: false,
    });
  },

  hasPermission: (module, action) => {
    return hasEffectivePermission(get().user, `${module}.${action}`);
  },

  canAccess: (module) => {
    const { user } = get();
    if (!user) return false;
    if (deriveMobileAccess(user).mode === 'NoLogin') return false;
    return hasEffectivePermission(user, `${module}.*`);
  },
}));

/** HTTP status and `{ code }` of a failed request, if it had a response. */
// Error tracking carries only the opaque tenant GUID (a no-op unless a DSN is configured; a slug is refused).
useAuthStore.subscribe((state) => setErrorTrackingTenant(state.user?.tenantId));

export function authFailure(error: unknown): { status?: number; code?: string } {
  if (typeof error !== 'object' || error === null || !('response' in error)) return {};
  const response = (error as { response?: { status?: number; data?: { code?: unknown } } }).response;
  return { status: response?.status, code: typeof response?.data?.code === 'string' ? response.data.code : undefined };
}

function handledBySignInScreen(error: unknown, password: string): boolean {
  const { status, code } = authFailure(error);
  if (status === 400 && code === 'workspace_required') return true;
  return (status === 401 || status === 400) && isWelcomeCode(password);
}

function extractAuthError(error: unknown, fallback = 'Login failed. Please try again.'): string {
  if (typeof error === 'object' && error !== null && 'response' in error) {
    const axiosError = error as {
      response?: { status?: number; data?: { message?: string; title?: string } };
      message?: string;
    };
    const serverMessage = axiosError.response?.data?.message ?? axiosError.response?.data?.title;
    if (serverMessage) return serverMessage;
    if (axiosError.response?.status === 429) return 'The sign-in service is busy — try again in a few seconds.';
    if (axiosError.response?.status === 401) return 'Invalid username, password, or authentication code.';
    if (axiosError.response?.status === 423) return 'Your account is locked. Contact HR.';
    if (axiosError.response?.status === 403) return 'Access denied. Check with your administrator.';
    return axiosError.message ?? fallback;
  }
  if (error instanceof Error && error.message) return error.message;
  return 'Unable to connect. Check your internet connection.';
}
