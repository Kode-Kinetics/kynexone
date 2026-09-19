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

interface AuthState {
  user: AuthUser | null;
  tenantId: string | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  isInitialized: boolean;
  error: string | null;
  sessionExpired: boolean;

  initialize: () => Promise<void>;
  login: (username: string, password: string, tenantId: string) => Promise<LoginOutcome>;
  completeMfa: (challengeToken: string, totpCode: string, tenantId: string) => Promise<void>;
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

  initialize: async () => {
    set({ isLoading: true });
    try {
      const [storedUser, tenantId] = await Promise.all([
        userStorage.getUser(),
        appStorage.get<string>('zayra_tenant_id'),
      ]);

      if (!storedUser || !tenantId) {
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

      initApiClient(tenantId);
      setSessionExpiredHandler(() => get().handleSessionExpired());

      try {
        const freshUser = await authApi.getMe();
        await userStorage.saveUser(freshUser);
        set({
          user: freshUser,
          tenantId,
          isAuthenticated: true,
          isInitialized: true,
          isLoading: false,
          sessionExpired: false,
        });
      } catch {
        // Keep a previously authenticated session usable offline. The first live
        // request still refreshes or expires it through the API interceptor.
        set({
          user: storedUser,
          tenantId,
          isAuthenticated: true,
          isInitialized: true,
          isLoading: false,
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
    });
  },

  login: async (username, password, tenantId) => {
    set({ isLoading: true, error: null });
    try {
      const outcome = await authApi.login(username, password, tenantId);
      if (outcome.kind === 'authenticated') {
        await get().finishLogin(outcome.user, outcome.tokens, tenantId);
      } else {
        set({ isLoading: false });
      }
      return outcome;
    } catch (error: unknown) {
      set({ isLoading: false, error: extractAuthError(error) });
      throw error;
    }
  },

  completeMfa: async (challengeToken, totpCode, tenantId) => {
    set({ isLoading: true, error: null });
    try {
      const session = await authApi.verifyMfaChallenge(challengeToken, totpCode, tenantId);
      await get().finishLogin(session.user, session.tokens, tenantId);
    } catch (error: unknown) {
      set({ isLoading: false, error: extractAuthError(error, 'Invalid or expired authentication code.') });
      throw error;
    }
  },

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
    if (axiosError.response?.status === 401) return 'Invalid username, password, or authentication code.';
    if (axiosError.response?.status === 423) return 'Your account is locked. Contact HR.';
    if (axiosError.response?.status === 403) return 'Access denied. Check with your administrator.';
    return axiosError.message ?? fallback;
  }
  if (error instanceof Error && error.message) return error.message;
  return 'Unable to connect. Check your internet connection.';
}
