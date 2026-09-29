// ============================================================
// ZAYRA MOBILE — Secure Storage
// ============================================================

import * as SecureStore from 'expo-secure-store';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { STORAGE_KEYS } from '@/config';
import type { AuthTokens, AuthUser } from '@/types';

// SecureStore for sensitive data (tokens)
export const secureStorage = {
  async set(key: string, value: string): Promise<void> {
    try {
      await SecureStore.setItemAsync(key, value, {
        keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY,
      });
    } catch (error) {
      console.error('[SecureStorage] set error:', key, error);
      throw error;
    }
  },

  async get(key: string): Promise<string | null> {
    try {
      return await SecureStore.getItemAsync(key);
    } catch (error) {
      console.error('[SecureStorage] get error:', key, error);
      return null;
    }
  },

  async delete(key: string): Promise<void> {
    try {
      await SecureStore.deleteItemAsync(key);
    } catch (error) {
      console.error('[SecureStorage] delete error:', key, error);
    }
  },
};

// AsyncStorage for non-sensitive preferences
export const appStorage = {
  async set(key: string, value: unknown): Promise<void> {
    try {
      await AsyncStorage.setItem(key, JSON.stringify(value));
    } catch (error) {
      console.error('[AppStorage] set error:', key, error);
    }
  },

  async get<T>(key: string): Promise<T | null> {
    try {
      const raw = await AsyncStorage.getItem(key);
      if (raw === null) return null;
      return JSON.parse(raw) as T;
    } catch (error) {
      console.error('[AppStorage] get error:', key, error);
      return null;
    }
  },

  async delete(key: string): Promise<void> {
    try {
      await AsyncStorage.removeItem(key);
    } catch (error) {
      console.error('[AppStorage] delete error:', key, error);
    }
  },
};

// Token helpers
export const tokenStorage = {
  async saveTokens(tokens: AuthTokens): Promise<void> {
    await Promise.all([
      secureStorage.set(STORAGE_KEYS.ACCESS_TOKEN, tokens.accessToken),
      secureStorage.set(STORAGE_KEYS.REFRESH_TOKEN, tokens.refreshToken),
      secureStorage.set(STORAGE_KEYS.TOKEN_EXPIRY, String(tokens.expiresAt)),
    ]);
  },

  async getAccessToken(): Promise<string | null> {
    return secureStorage.get(STORAGE_KEYS.ACCESS_TOKEN);
  },

  async getRefreshToken(): Promise<string | null> {
    return secureStorage.get(STORAGE_KEYS.REFRESH_TOKEN);
  },

  async getTokenExpiry(): Promise<number | null> {
    const val = await secureStorage.get(STORAGE_KEYS.TOKEN_EXPIRY);
    return val ? parseInt(val, 10) : null;
  },

  async clearTokens(): Promise<void> {
    await Promise.all([
      secureStorage.delete(STORAGE_KEYS.ACCESS_TOKEN),
      secureStorage.delete(STORAGE_KEYS.REFRESH_TOKEN),
      secureStorage.delete(STORAGE_KEYS.TOKEN_EXPIRY),
    ]);
  },

  async isTokenExpired(): Promise<boolean> {
    const expiry = await this.getTokenExpiry();
    if (!expiry) return true;
    return Date.now() >= expiry - 60_000; // 1 min buffer
  },
};

// User session
export const userStorage = {
  async saveUser(user: AuthUser): Promise<void> {
    await appStorage.set(STORAGE_KEYS.USER, user);
  },

  async getUser(): Promise<AuthUser | null> {
    return appStorage.get<AuthUser>(STORAGE_KEYS.USER);
  },

  async clearUser(): Promise<void> {
    await appStorage.delete(STORAGE_KEYS.USER);
  },
};

// Clear all auth data
export async function clearAllAuthData(): Promise<void> {
  await Promise.all([tokenStorage.clearTokens(), userStorage.clearUser()]);
}

// Offline punch queue
export interface OfflinePunch {
  id: string;
  payload: object;
  timestamp: number;
  synced: boolean;
}

export const offlinePunchStorage = {
  async getQueue(): Promise<OfflinePunch[]> {
    return (await appStorage.get<OfflinePunch[]>(STORAGE_KEYS.OFFLINE_PUNCHES)) ?? [];
  },

  async addToQueue(punch: OfflinePunch): Promise<void> {
    const queue = await this.getQueue();
    queue.push(punch);
    await appStorage.set(STORAGE_KEYS.OFFLINE_PUNCHES, queue);
  },

  async markSynced(id: string): Promise<void> {
    const queue = await this.getQueue();
    const updated = queue.map((p) => (p.id === id ? { ...p, synced: true } : p));
    await appStorage.set(STORAGE_KEYS.OFFLINE_PUNCHES, updated);
  },

  async clearSynced(): Promise<void> {
    const queue = await this.getQueue();
    const pending = queue.filter((p) => !p.synced);
    await appStorage.set(STORAGE_KEYS.OFFLINE_PUNCHES, pending);
  },
};
