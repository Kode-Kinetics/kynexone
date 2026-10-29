import { create } from 'zustand';
import * as LocalAuthentication from 'expo-local-authentication';
import { STORAGE_KEYS } from '@/config';
import { appStorage } from '@/storage';
import {
  DEFAULT_APP_LOCK,
  normalizeLockPrefs,
  type AppLockPrefs,
  type LockTimeoutMinutes,
} from './appLockPolicy';

interface AppLockState {
  prefs: AppLockPrefs;
  hydrated: boolean;
  /** Authenticated screens are hidden until stored lock preferences and the first lock decision settle. */
  initialLockEvaluated: boolean;
  /** Face ID / fingerprint or a device passcode is set up on this phone. */
  canAuthenticate: boolean;
  locked: boolean;
  failed: boolean;
  hydrate: () => Promise<void>;
  markInitialLockEvaluated: () => void;
  setEnabled: (enabled: boolean) => Promise<void>;
  setTimeoutMinutes: (minutes: LockTimeoutMinutes) => Promise<void>;
  lock: () => void;
  unlock: (promptMessage: string, cancelLabel: string) => Promise<boolean>;
}

async function deviceCanAuthenticate(): Promise<boolean> {
  try {
    // getEnrolledLevelAsync covers a passcode-only phone, which isEnrolledAsync (biometrics) does not.
    const level = await LocalAuthentication.getEnrolledLevelAsync();
    return level !== LocalAuthentication.SecurityLevel.NONE;
  } catch {
    return false;
  }
}

export const useAppLockStore = create<AppLockState>((set, get) => ({
  prefs: { ...DEFAULT_APP_LOCK },
  hydrated: false,
  initialLockEvaluated: false,
  canAuthenticate: false,
  locked: false,
  failed: false,

  hydrate: async () => {
    const [stored, canAuthenticate] = await Promise.all([
      appStorage.get<unknown>(STORAGE_KEYS.APP_LOCK),
      deviceCanAuthenticate(),
    ]);
    set({ prefs: normalizeLockPrefs(stored), canAuthenticate, hydrated: true });
  },

  markInitialLockEvaluated: () => set({ initialLockEvaluated: true }),

  setEnabled: async (enabled) => {
    const prefs = { ...get().prefs, enabled };
    set({ prefs, locked: enabled ? get().locked : false });
    await appStorage.set(STORAGE_KEYS.APP_LOCK, prefs);
  },

  setTimeoutMinutes: async (timeoutMinutes) => {
    const prefs = { ...get().prefs, timeoutMinutes };
    set({ prefs });
    await appStorage.set(STORAGE_KEYS.APP_LOCK, prefs);
  },

  lock: () => set({ locked: true, failed: false }),

  unlock: async (promptMessage, cancelLabel) => {
    try {
      const result = await LocalAuthentication.authenticateAsync({
        promptMessage,
        cancelLabel,
        disableDeviceFallback: false, // device passcode is the fallback for failed or unavailable biometrics
      });
      if (result.success) {
        set({ locked: false, failed: false });
        return true;
      }
    } catch {
      // treated as a failed attempt below
    }
    set({ failed: true });
    return false;
  },
}));
