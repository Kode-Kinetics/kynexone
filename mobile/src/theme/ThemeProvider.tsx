import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from 'react';
import { AccessibilityInfo, useColorScheme } from 'react-native';
import { STORAGE_KEYS } from '@/config';
import i18n from '@/config/i18n';
import { appStorage } from '@/storage';
import {
  createTheme,
  type KynexTheme,
  type ResolvedThemeMode,
  type ThemeLanguage,
  type ThemePreference,
} from './tokens';

function toThemeLanguage(lang: string | undefined): ThemeLanguage {
  return lang?.startsWith('ar') ? 'ar' : 'en';
}

interface ThemeContextValue {
  theme: KynexTheme;
  preference: ThemePreference;
  setPreference: (preference: ThemePreference) => Promise<void>;
  reduceMotion: boolean;
  /** UI language the typography is tuned for (follows i18next). */
  language: ThemeLanguage;
}

const ThemeContext = createContext<ThemeContextValue | null>(null);

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  const systemScheme = useColorScheme();
  const [preference, setPreferenceState] = useState<ThemePreference>('system');
  const [reduceTransparency, setReduceTransparency] = useState(false);
  const [reduceMotion, setReduceMotion] = useState(false);
  const [language, setLanguage] = useState<ThemeLanguage>(() => toThemeLanguage(i18n.language));

  // Typography is locale-aware (Arabic: no tracking, taller lines, no uppercase).
  useEffect(() => {
    const onChange = (lng: string) => setLanguage(toThemeLanguage(lng));
    onChange(i18n.language);
    i18n.on('languageChanged', onChange);
    return () => {
      i18n.off('languageChanged', onChange);
    };
  }, []);
  useEffect(() => {
    let active = true;

    Promise.all([
      appStorage.get<ThemePreference>(STORAGE_KEYS.THEME),
      AccessibilityInfo.isReduceTransparencyEnabled(),
      AccessibilityInfo.isReduceMotionEnabled(),
    ]).then(([stored, transparency, motion]) => {
      if (!active) return;
      if (stored === 'system' || stored === 'light' || stored === 'dark') {
        setPreferenceState(stored);
      }
      setReduceTransparency(transparency);
      setReduceMotion(motion);
    });

    const transparencySubscription = AccessibilityInfo.addEventListener(
      'reduceTransparencyChanged',
      setReduceTransparency,
    );
    const motionSubscription = AccessibilityInfo.addEventListener(
      'reduceMotionChanged',
      setReduceMotion,
    );

    return () => {
      active = false;
      transparencySubscription.remove();
      motionSubscription.remove();
    };
  }, []);
  const resolvedMode: ResolvedThemeMode = useMemo(() => {
    if (preference === 'light' || preference === 'dark') return preference;
    return systemScheme === 'light' ? 'light' : 'dark';
  }, [preference, systemScheme]);

  const theme = useMemo(
    () => createTheme(resolvedMode, reduceTransparency, language),
    [language, reduceTransparency, resolvedMode],
  );

  const setPreference = useCallback(async (next: ThemePreference) => {
    setPreferenceState(next);
    await appStorage.set(STORAGE_KEYS.THEME, next);
  }, []);

  const value = useMemo(
    () => ({ theme, preference, setPreference, reduceMotion, language }),
    [language, preference, reduceMotion, setPreference, theme],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme() {
  const value = useContext(ThemeContext);
  if (!value) throw new Error('useTheme must be used inside ThemeProvider');
  return value;
}
