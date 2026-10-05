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
 * Whether to offer a restart at cold start, given that the layout does not match `lang`.
 *
 * At most ONE prompt per language: `promptedFor` remembers the language a restart was last offered
 * for (by this function or by the Settings switch). If a restart did not flip the direction (a dev
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
    if ((await storage.getItem(promptedKey)) === lang) return false;
    await storage.setItem(promptedKey, lang);
    return true;
  } catch {
    // If the marker cannot be stored, do not prompt: an unbounded prompt loop is worse than none.
    return false;
  }
}

/** Record that a restart was offered for `lang` (the Settings switch does its own prompt). */
export async function markRestartPrompted(lang: AppLanguage, storage: LanguageStorage, promptedKey: string): Promise<void> {
  try { await storage.setItem(promptedKey, lang); } catch { /* best effort */ }
}
