// App lock decisions as pure functions (import-free for node --test).
//
// The lock is a privacy gate on a phone that is already unlocked or shared for a moment: after the
// app has been in the background for the chosen time, the next open asks for Face ID / fingerprint /
// the device passcode (the passcode fallback keeps it usable for people who cannot use biometrics).

export const LOCK_TIMEOUT_MINUTES = [0, 1, 5, 15] as const;
export type LockTimeoutMinutes = (typeof LOCK_TIMEOUT_MINUTES)[number];

export interface AppLockPrefs {
  enabled: boolean;
  timeoutMinutes: LockTimeoutMinutes;
}

export interface ColdStartDecisionState {
  hydrated: boolean;
  initialized: boolean;
  loading: boolean;
  /** Stored credentials exist, but the authoritative session restore is still waiting on connectivity. */
  connectionIssue: boolean;
}

/** The one-time cold-start choice must not be consumed while an offline session restore is unresolved. */
export function coldStartDecisionReady(state: ColdStartDecisionState): boolean {
  return state.hydrated && state.initialized && !state.loading && !state.connectionIssue;
}

/** On by default: payslips, loans and the profile show salary, IBAN and ID numbers. */
export const DEFAULT_APP_LOCK: AppLockPrefs = { enabled: true, timeoutMinutes: 1 };

export function normalizeLockPrefs(raw: unknown): AppLockPrefs {
  if (!raw || typeof raw !== 'object') return { ...DEFAULT_APP_LOCK };
  const { enabled, timeoutMinutes } = raw as { enabled?: unknown; timeoutMinutes?: unknown };
  const minutes = LOCK_TIMEOUT_MINUTES.find((m) => m === timeoutMinutes);
  return {
    enabled: typeof enabled === 'boolean' ? enabled : DEFAULT_APP_LOCK.enabled,
    timeoutMinutes: minutes ?? DEFAULT_APP_LOCK.timeoutMinutes,
  };
}

export interface ResumeInput {
  prefs: AppLockPrefs;
  /** Biometrics or a device passcode is set up. Without one there is nothing to verify against. */
  canAuthenticate: boolean;
  signedIn: boolean;
  /** Epoch ms when the app left the foreground; null if it did not (or we never saw it). */
  backgroundedAt: number | null;
  now: number;
}

/** True when returning to the foreground must show the lock. */
export function shouldLockOnResume(input: ResumeInput): boolean {
  const { prefs, canAuthenticate, signedIn, backgroundedAt, now } = input;
  if (!prefs.enabled || !canAuthenticate || !signedIn) return false;
  if (backgroundedAt === null) return false;
  const away = now - backgroundedAt;
  if (away < 0) return true; // the clock moved back: do not trust it
  return away >= prefs.timeoutMinutes * 60_000;
}

/** True for a fresh launch that restored an existing session (not for a sign-in the user just made). */
export function shouldLockOnColdStart(prefs: AppLockPrefs, canAuthenticate: boolean, restoredSession: boolean): boolean {
  return prefs.enabled && canAuthenticate && restoredSession;
}
