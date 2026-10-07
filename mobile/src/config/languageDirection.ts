// ============================================================
// Language persistence and layout direction — pure logic.
//
// No react-native imports: config/i18n.ts passes in AsyncStorage and I18nManager, and
// tests/languageDirection.test.ts drives these with fakes under `node --test`.
// ============================================================

export type AppLanguage = 'en' | 'ar';

export function isLanguage(v: unknown): v is AppLanguage {
  return v === 'en' || v === 'ar';
}

export interface LanguageStorage {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

/** The parts of React Native's I18nManager this module uses. */
export interface DirectionManager {
  readonly isRTL: boolean;
  allowRTL(allow: boolean): void;
  forceRTL(force: boolean): void;
}

/** The user's saved language, or null when none (or it is unreadable). */
export async function readStoredLanguage(storage: Pick<LanguageStorage, 'getItem'>, key: string): Promise<AppLanguage | null> {
  try {
    const raw = await storage.getItem(key);
    if (raw == null) return null;
    // appStorage writes JSON ('"ar"'); accept a bare value too.
    const parsed: unknown = raw.startsWith('"') ? JSON.parse(raw) : raw;
    return isLanguage(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * Sets the native direction for `lang`. Returns true when the running layout does not match
 * yet: RTL is fixed per process, so it flips only after a restart.
 */
export function applyDirection(lang: AppLanguage, manager: DirectionManager): boolean {
  const rtl = lang === 'ar';
  manager.allowRTL(rtl);
  manager.forceRTL(rtl);
  return manager.isRTL !== rtl;
}

/**
 * Whether to offer a restart at cold start, given that the layout does not match `lang`. Reads
 * only; the marker is written by `offerRestartOnce` after the prompt has been shown.
 *
 * At most ONE prompt per language: `promptedKey` remembers the language a restart was last offered
 * for (at startup or by the Settings switch). If a restart did not flip the direction (a dev
 * client, an OS that ignores forceRTL), the user is not asked again on every launch.
 */
export async function shouldPromptRestart(
  lang: AppLanguage,
  mismatch: boolean,
  storage: LanguageStorage,
  promptedKey: string,
): Promise<boolean> {
  try {
    if (!mismatch) {
      // Direction matches: forget the marker so a later switch can prompt again.
      if (await storage.getItem(promptedKey)) await storage.removeItem(promptedKey);
      return false;
    }
    return (await storage.getItem(promptedKey)) !== lang;
  } catch {
    // Unreadable storage: do not prompt rather than risk asking on every launch.
    return false;
  }
}

/**
 * Show the restart prompt if it is due, and only THEN record it. Recording first lost the prompt
 * for good when the alert never appeared (Android drops an Alert raised before the first screen
 * mounts). If `show` throws, nothing is recorded and the next launch asks again.
 */
export async function offerRestartOnce(
  lang: AppLanguage,
  mismatch: boolean,
  storage: LanguageStorage,
  promptedKey: string,
  show: () => void,
): Promise<boolean> {
  if (!(await shouldPromptRestart(lang, mismatch, storage, promptedKey))) return false;
  show();
  await markRestartPrompted(lang, storage, promptedKey);
  return true;
}

/** Record that a restart was offered for `lang` (the Settings switch does its own prompt). */
export async function markRestartPrompted(lang: AppLanguage, storage: LanguageStorage, promptedKey: string): Promise<void> {
  try { await storage.setItem(promptedKey, lang); } catch { /* best effort */ }
}
