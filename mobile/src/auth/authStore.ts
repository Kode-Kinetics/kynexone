// ============================================================
// KynexOne Mobile — Auth Store (Zustand)
// ============================================================

import { create } from 'zustand';
import * as Device from 'expo-device';
import Constants from 'expo-constants';
import { Platform } from 'react-native';
import { tokenStorage, userStorage, clearAllAuthData, appStorage } from '@/storage';
import { STORAGE_KEYS } from '@/config';
import { authApi, deviceApi, type LoginOutcome } from '@/api/services';
import { initApiClient, setSessionExpiredHandler } from '@/api/client';
import type { AuthTokens, AuthUser } from '@/types';
import { generateDeviceId } from '@/utils/device';
import { registerPushToken } from '@/features/notifications/pushNotifications';
import { deriveMobileAccess, hasEffectivePermission } from './accessPolicy';
import { normalizeEmail, normalizeWorkspace, requireWorkspace } from './publicAuthInput';
import type { MfaPrompt } from './mfaFlow';

interface AuthState {
  user: AuthUser | null;
  tenantId: string | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  isInitialized: boolean;
  error: string | null;
  sessionExpired: boolean;
  /** Mandatory two-step sign-in standing for the signed-in user (null = unknown / not asked yet). */
  mfaPrompt: MfaPrompt | null;
  mfaPromptDismissed: boolean;
  /** Set after a signed-in enrolment ended the session, so the sign-in screen can explain why. */
  mfaEnrolledNotice: { email: string; tenantId: string } | null;

  initialize: () => Promise<void>;
  login: (username: string, password: string, tenantId: string) => Promise<LoginOutcome>;
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
  mfaPrompt: null,
  mfaPromptDismissed: false,
  mfaEnrolledNotice: null,

  initialize: async () => {
    set({ isLoading: true });
    try {
      const [storedUser, tenantId] = await Promise.all([
        userStorage.getUser(),
        appStorage.get<string>('zayra_tenant_id'),
      ]);

      const normalizedTenant = normalizeWorkspace(tenantId);
      if (!storedUser || !normalizedTenant) {
        await clearAllAuthData();
        set({ isInitialized: true, isLoading: false });
        return;
      }

      if (await tokenStorage.isTokenExpired()) {
        const refreshToken = await tokenStorage.getRefreshToken();
        if (!refreshToken) {
          await clearAllAuthData();
          set({ isInitialized: true, isLoading: false });
          return;
        }
      }

      if (normalizedTenant !== tenantId) await appStorage.set('zayra_tenant_id', normalizedTenant);
      initApiClient(normalizedTenant);
      setSessionExpiredHandler(() => get().handleSessionExpired());

      try {
        const freshUser = await authApi.getMe();
        await userStorage.saveUser(freshUser);
        set({
          user: freshUser,
          tenantId: normalizedTenant,
          isAuthenticated: true,
          isInitialized: true,
          isLoading: false,
          sessionExpired: false,
        });
        void get().refreshMfaStatus();
      } catch {
        // /auth/me is the authoritative role/access graph. Cached permissions
        // cannot reopen MainTabs when that graph is unavailable or rejected;
        // an offline mode would need a separate restricted design and TTL.
        await clearAllAuthData();
        set({
          user: null,
          tenantId: null,
          isAuthenticated: false,
          isInitialized: true,
          isLoading: false,
          sessionExpired: true,
        });
      }
    } catch {
      await clearAllAuthData();
      set({ isInitialized: true, isLoading: false, isAuthenticated: false });
    }
  },

  finishLogin: async (user, tokens, tenantId) => {
    await Promise.all([
      tokenStorage.saveTokens(tokens),
      userStorage.saveUser(user),
      appStorage.set('zayra_tenant_id', tenantId),
    ]);

    initApiClient(tenantId);
    setSessionExpiredHandler(() => get().handleSessionExpired());

    // Device and push registration are non-blocking session enhancements: a
    // permission denial or provider outage must never stop a valid sign-in.
    try {
      const deviceId = await generateDeviceId();
      await deviceApi.register({
        deviceId,
        platform: Platform.OS,
        model: Device.modelName ?? 'Unknown',
        osVersion: Device.osVersion ?? 'Unknown',
        appVersion: Constants.expoConfig?.version ?? '1.0.0',
      });
      await appStorage.set(STORAGE_KEYS.DEVICE_ID, deviceId);
      void registerPushToken(deviceId).catch((error) =>
        console.warn('[Auth] Push registration failed:', error)
      );
    } catch (deviceError) {
      console.warn('[Auth] Device registration failed:', deviceError);
    }

    set({
      user,
      tenantId,
      isAuthenticated: true,
      isInitialized: true,
      isLoading: false,
      error: null,
      sessionExpired: false,
      mfaPrompt: null,
      mfaPromptDismissed: false,
      mfaEnrolledNotice: null,
    });
    // Grace-period standing is not in the login reply; ask once the session exists.
    void get().refreshMfaStatus();
  },

  login: async (username, password, tenantId) => {
    set({ isLoading: true, error: null });
    try {
      const workspace = requireWorkspace(tenantId);
      const email = normalizeEmail(username);
      if (!email) throw new Error('Work email is required.');
      const outcome = await authApi.login(email, password, workspace);
      if (outcome.kind === 'authenticated') {
        await get().finishLogin(outcome.user, outcome.tokens, workspace);
      } else {
        set({ isLoading: false });
      }
      return outcome;
    } catch (error: unknown) {
      set({ isLoading: false, error: extractAuthError(error) });
      throw error;
    }
  },

  // The code screen owns its own submitting/error state (see mfaFlow.codeEntryReducer).
  // Touching the shared isLoading/error here would raise the sign-in screen's alert
  // underneath the code screen for every wrong code.
  completeMfa: async (challengeToken, totpCode, tenantId) => {
    const workspace = requireWorkspace(tenantId);
    const session = await authApi.verifyMfaChallenge(challengeToken, totpCode, workspace);
    await get().finishLogin(session.user, session.tokens, workspace);
  },

  refreshMfaStatus: async () => {
    try {
      const mfaPrompt = await authApi.getMfaStatus();
      if (get().isAuthenticated) set({ mfaPrompt });
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
    await clearAllAuthData();
    set({
      user: null,
      tenantId: null,
      isAuthenticated: false,
      isLoading: false,
      sessionExpired: false,
      mfaPrompt: null,
      mfaPromptDismissed: false,
      mfaEnrolledNotice: user && tenantId ? { email: user.email, tenantId } : null,
    });
  },

  consumeMfaEnrolledNotice: () => set({ mfaEnrolledNotice: null }),

  logout: async () => {
    set({ isLoading: true });
    try {
      const refreshToken = await tokenStorage.getRefreshToken();
      const deviceId = await appStorage.get<string>(STORAGE_KEYS.DEVICE_ID);
      await Promise.allSettled([
        refreshToken ? authApi.logout(refreshToken) : Promise.resolve(),
        deviceId ? deviceApi.unregister(deviceId) : Promise.resolve(),
      ]);
    } finally {
      await clearAllAuthData();
      set({
        user: null,
        tenantId: null,
        isAuthenticated: false,
        isLoading: false,
        sessionExpired: false,
        mfaPrompt: null,
        mfaPromptDismissed: false,
      });
    }
  },

  refreshUser: async () => {
    try {
      const freshUser = await authApi.getMe();
      await userStorage.saveUser(freshUser);
      set({ user: freshUser });
    } catch (error) {
      console.warn('[Auth] refreshUser failed:', error);
    }
  },

  clearError: () => set({ error: null }),

  handleSessionExpired: () => {
    void clearAllAuthData();
    set({
      user: null,
      tenantId: null,
      isAuthenticated: false,
      sessionExpired: true,
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
