// ============================================================
// ZAYRA MOBILE — Secure Storage
// ============================================================

import * as SecureStore from 'expo-secure-store';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { STORAGE_KEYS } from '@/config';
import type { AuthTokens, AuthUser } from '@/types';

export const CREDENTIAL_ENVELOPE_KEY = 'zayra_credential_envelope';
export const USER_CACHE_TTL_MS = 24 * 60 * 60 * 1000;

export interface CredentialEnvelope extends AuthTokens {
  version: 1;
  tenantId: string;
  persistedAt: number;
}

export interface CachedAuthUser {
  id: string;
  employeeId: string;
  email: string;
  fullName: string;
  profilePhotoUrl?: string;
}

interface CachedAuthUserEnvelope {
  version: 1;
  cachedAt: number;
  user: CachedAuthUser;
}

const LEGACY_TOKEN_KEYS = [
  STORAGE_KEYS.ACCESS_TOKEN,
  STORAGE_KEYS.REFRESH_TOKEN,
  STORAGE_KEYS.TOKEN_EXPIRY,
] as const;

// SecureStore for sensitive data (tokens)
export const secureStorage = {
  async set(key: string, value: string): Promise<void> {
    await SecureStore.setItemAsync(key, value, {
      keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY,
    });
  },

  async get(key: string): Promise<string | null> {
    return SecureStore.getItemAsync(key);
  },

  async delete(key: string): Promise<void> {
    await SecureStore.deleteItemAsync(key);
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

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function isCredentialEnvelope(value: unknown): value is CredentialEnvelope {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<CredentialEnvelope>;
  return candidate.version === 1
    && isNonEmptyString(candidate.accessToken)
    && isNonEmptyString(candidate.refreshToken)
    && isNonEmptyString(candidate.tenantId)
    && typeof candidate.expiresAt === 'number'
    && Number.isFinite(candidate.expiresAt)
    && candidate.expiresAt > 0
    && typeof candidate.persistedAt === 'number'
    && Number.isFinite(candidate.persistedAt)
    && candidate.persistedAt > 0;
}

async function clearLegacyTokenKeys(): Promise<void> {
  await Promise.all(LEGACY_TOKEN_KEYS.map((key) => secureStorage.delete(key)));
}

export async function saveCredentialEnvelope(envelope: CredentialEnvelope): Promise<void> {
  if (!isCredentialEnvelope(envelope)) {
    throw new Error('Invalid credential envelope.');
  }
  await clearLegacyTokenKeys();
  await secureStorage.set(CREDENTIAL_ENVELOPE_KEY, JSON.stringify(envelope));
}

export async function loadCredentialEnvelope(): Promise<CredentialEnvelope | null> {
  const raw = await secureStorage.get(CREDENTIAL_ENVELOPE_KEY);
  if (raw === null) {
    await clearLegacyTokenKeys();
    return null;
  }

  try {
    const envelope: unknown = JSON.parse(raw);
    if (isCredentialEnvelope(envelope)) return envelope;
  } catch {
    // Invalid secure data is cleared below and must never create a session.
  }

  await clearCredentialEnvelope();
  return null;
}

export async function clearCredentialEnvelope(): Promise<void> {
  await Promise.all([
    secureStorage.delete(CREDENTIAL_ENVELOPE_KEY),
    clearLegacyTokenKeys(),
  ]);
}

// Compatibility helpers for call sites that consume individual token fields.
// Every read and refresh update is backed by the one transactional envelope.
export const tokenStorage = {
  async saveTokens(tokens: AuthTokens): Promise<void> {
    const current = await loadCredentialEnvelope();
    if (!current) throw new Error('No credential envelope to update.');
    await saveCredentialEnvelope({
      ...current,
      ...tokens,
      persistedAt: Date.now(),
    });
  },

  async getAccessToken(): Promise<string | null> {
    return (await loadCredentialEnvelope())?.accessToken ?? null;
  },

  async getRefreshToken(): Promise<string | null> {
    return (await loadCredentialEnvelope())?.refreshToken ?? null;
  },

  async getTokenExpiry(): Promise<number | null> {
    return (await loadCredentialEnvelope())?.expiresAt ?? null;
  },

  async clearTokens(): Promise<void> {
    await clearCredentialEnvelope();
  },

  async isTokenExpired(): Promise<boolean> {
    const expiry = await this.getTokenExpiry();
    if (!expiry) return true;
    return Date.now() >= expiry - 60_000; // 1 min buffer
  },
};

function isCachedAuthUserEnvelope(value: unknown): value is CachedAuthUserEnvelope {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<CachedAuthUserEnvelope>;
  const user = candidate.user as Partial<CachedAuthUser> | undefined;
  return candidate.version === 1
    && typeof candidate.cachedAt === 'number'
    && Number.isFinite(candidate.cachedAt)
    && candidate.cachedAt > 0
    && !!user
    && isNonEmptyString(user.id)
    && isNonEmptyString(user.employeeId)
    && isNonEmptyString(user.email)
    && isNonEmptyString(user.fullName)
    && (user.profilePhotoUrl === undefined || typeof user.profilePhotoUrl === 'string');
}

// Display-only user cache. Authorization fields are intentionally omitted;
// authenticated startup always restores the server-owned user graph.
export const userStorage = {
  async saveUser(user: AuthUser): Promise<void> {
    const cachedUser: CachedAuthUser = {
      id: user.id,
      employeeId: user.employeeId,
      email: user.email,
      fullName: user.fullName,
      ...(user.profilePhotoUrl ? { profilePhotoUrl: user.profilePhotoUrl } : {}),
    };
    const envelope: CachedAuthUserEnvelope = {
      version: 1,
      cachedAt: Date.now(),
      user: cachedUser,
    };
    await appStorage.set(STORAGE_KEYS.USER, envelope);
  },

  async getUser(): Promise<CachedAuthUser | null> {
    const envelope = await appStorage.get<unknown>(STORAGE_KEYS.USER);
    const now = Date.now();
    if (
      !isCachedAuthUserEnvelope(envelope)
      || envelope.cachedAt > now
      || now - envelope.cachedAt > USER_CACHE_TTL_MS
    ) {
      if (envelope !== null) await appStorage.delete(STORAGE_KEYS.USER);
      return null;
    }
    return {
      id: envelope.user.id,
      employeeId: envelope.user.employeeId,
      email: envelope.user.email,
      fullName: envelope.user.fullName,
      ...(envelope.user.profilePhotoUrl
        ? { profilePhotoUrl: envelope.user.profilePhotoUrl }
        : {}),
    };
  },

  async clearUser(): Promise<void> {
    await appStorage.delete(STORAGE_KEYS.USER);
  },
};

// Clear all auth data
export async function clearAllAuthData(): Promise<void> {
  await Promise.all([clearCredentialEnvelope(), userStorage.clearUser()]);
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
