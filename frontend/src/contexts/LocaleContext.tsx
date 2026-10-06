'use client';

import { createContext, useCallback, useContext, useEffect, useState } from 'react';
import { LOCALE_METADATA, translate } from '../i18n/translations';
import type { LocaleCode, MessageParams } from '../i18n/translations';
import { LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from '../i18n/localeBoot';
import { resolveLocale, tenantDefaultLocale } from '../i18n/localeResolution';
import { useTenantSettingsContext } from './TenantSettingsContext';

export { type LocaleCode };

/** Languages offered in the switcher. Hidden ones (fr, es) still work for anyone who chose them. */
export const LOCALES = Object.entries(LOCALE_METADATA)
  .filter(([, meta]) => meta.selectable)
  .map(([code, meta]) => ({ code: code as LocaleCode, ...meta }));

const storage = {
  get(key: string): string | null {
    if (typeof window === 'undefined') return null;
    try { return localStorage.getItem(key); } catch { return null; }
  },
  set(key: string, value: string) {
    try { localStorage.setItem(key, value); } catch { /* private mode: the choice lasts the session */ }
  },
};

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
  const { settings, loaded } = useTenantSettingsContext();
  const defaultLanguage = settings.defaultLanguage;
  const [locale, setLocaleState] = useState<LocaleCode>('en');

  // Resolved after mount (no SSR mismatch), and again when the tenant's settings arrive or change:
  // the user's own choice, else the tenant's default language once it has LOADED, else the tenant
  // default as last cached, else English (i18n/localeResolution.ts). Before the settings load,
  // `defaultLanguage` is a placeholder and is ignored, so an Arabic tenant never flashes LTR and a
  // failed fetch leaves the language and the cache as they were.
  useEffect(() => {
    const tenant = { loaded, defaultLanguage };
    const fromTenant = tenantDefaultLocale(tenant);
    if (fromTenant) storage.set(TENANT_LOCALE_KEY, fromTenant);
    const next = resolveLocale(storage, tenant);
    setLocaleState(next);
    applyDocument(next);
  }, [loaded, defaultLanguage]);

  /** The language switcher: the only writer of the user's explicit choice. */
  const setLocale = useCallback((code: LocaleCode) => {
    setLocaleState(code);
    applyDocument(code);
    storage.set(LOCALE_CHOICE_KEY, code);
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
