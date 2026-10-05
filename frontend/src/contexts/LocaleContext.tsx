'use client';

import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import { LOCALE_DICTS, LOCALE_METADATA, translate } from '../i18n/translations';
import type { LocaleCode, MessageParams } from '../i18n/translations';
import { useTenantSettings } from './TenantSettingsContext';

export { type LocaleCode };

/** Languages offered in the switcher. Hidden ones (fr, es) still work for anyone who chose them. */
export const LOCALES = Object.entries(LOCALE_METADATA)
  .filter(([, meta]) => meta.selectable)
  .map(([code, meta]) => ({ code: code as LocaleCode, ...meta }));

/** The user's own explicit choice. Only `setLocale` writes it. */
const STORAGE_KEY = 'kynexone-locale';
/**
 * The tenant's default language, cached so app/layout.tsx's pre-paint script can set `dir`
 * before React hydrates. Never treated as the user's choice: a user who never picked a
 * language follows the tenant if the tenant's default later changes.
 */
const TENANT_STORAGE_KEY = 'kynexone-tenant-locale';

function asLocale(v: string | null | undefined): LocaleCode | null {
  if (!v) return null;
  const two = v.slice(0, 2).toLowerCase();
  return two in LOCALE_DICTS ? (two as LocaleCode) : null;
}

function readStorage(key: string): string | null {
  if (typeof window === 'undefined') return null;
  try { return localStorage.getItem(key); } catch { return null; }
}

function writeStorage(key: string, value: string) {
  try { localStorage.setItem(key, value); } catch { /* private mode: the choice lasts the session */ }
}

function applyDocument(code: LocaleCode) {
  document.documentElement.dir = LOCALE_METADATA[code].dir;
  document.documentElement.lang = code;
}

interface LocaleCtx {
  locale: LocaleCode;
  dir: 'ltr' | 'rtl';
  setLocale: (code: LocaleCode) => void;
  /** Translate `key`; with `params`, fill `{placeholders}` and plurals (see i18n/message.ts). */
  t: (key: string, params?: MessageParams) => string;
}

const Ctx = createContext<LocaleCtx>({
  locale: 'en',
  dir: 'ltr',
  setLocale: () => {},
  t: (k, p) => translate('en', k, p),
});

export function LocaleProvider({ children }: { children: React.ReactNode }) {
  const { defaultLanguage } = useTenantSettings();
  const [locale, setLocaleState] = useState<LocaleCode>('en');
  const [userChose, setUserChose] = useState(false);

  // Resolve once on mount (avoids an SSR mismatch): the user's saved choice wins; otherwise the
  // tenant's default as last seen; otherwise English.
  useEffect(() => {
    const own = asLocale(readStorage(STORAGE_KEY));
    const initial = own ?? asLocale(readStorage(TENANT_STORAGE_KEY)) ?? 'en';
    setUserChose(own != null);
    setLocaleState(initial);
    applyDocument(initial);
  }, []);

  // Honour the tenant's default language whenever the user has not picked one themselves.
  useEffect(() => {
    const tenant = asLocale(defaultLanguage);
    if (!tenant || userChose || !LOCALE_METADATA[tenant].selectable) return;
    if (readStorage(STORAGE_KEY)) return; // mount effect has not committed yet; the user chose
    writeStorage(TENANT_STORAGE_KEY, tenant);
    setLocaleState(tenant);
    applyDocument(tenant);
  }, [defaultLanguage, userChose]);

  const setLocale = useCallback((code: LocaleCode) => {
    setUserChose(true);
    setLocaleState(code);
    applyDocument(code);
    writeStorage(STORAGE_KEY, code);
  }, []);

  const t = useCallback((key: string, params?: MessageParams) => translate(locale, key, params), [locale]);

  return (
    <Ctx.Provider value={{ locale, dir: LOCALE_METADATA[locale].dir, setLocale, t }}>
      {children}
    </Ctx.Provider>
  );
}

export function useLocale() {
  return useContext(Ctx);
}
