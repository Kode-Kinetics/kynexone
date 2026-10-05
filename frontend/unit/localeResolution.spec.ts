import { expect, test } from '@playwright/test';
import { LEGACY_LOCALE_KEY, LOCALE_BOOT, LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from '../src/i18n/localeBoot';
import { resolveLocale, tenantDefaultLocale } from '../src/i18n/localeResolution';

/**
 * Which language the UI starts in (i18n/localeResolution.ts) and the pre-paint script that sets
 * `dir` before React hydrates (i18n/localeBoot.ts). Both must agree, or the first frame and the
 * hydrated page point in different directions.
 */

const store = (entries: Record<string, string>) => ({ get: (k: string) => entries[k] ?? null });

/** Runs LOCALE_BOOT against a fake localStorage and <html>; returns what it set. */
function boot(entries: Record<string, string>) {
  const html = { lang: 'en', dir: 'ltr' };
  new Function('localStorage', 'document', LOCALE_BOOT)(
    { getItem: (k: string) => entries[k] ?? null },
    { documentElement: html },
  );
  return html;
}

test.describe('UI language resolution', () => {
  test('the legacy key is not a choice: legacy en + tenant ar → ar', () => {
    // Every existing user has 'kynexone-locale' = 'en', written on mount by older builds.
    const s = store({ [LEGACY_LOCALE_KEY]: 'en' });
    expect(resolveLocale(s, { loaded: true, defaultLanguage: 'ar' })).toBe('ar');
    // Before paint, with the tenant default cached by an earlier visit.
    expect(boot({ [LEGACY_LOCALE_KEY]: 'en', [TENANT_LOCALE_KEY]: 'ar' })).toEqual({ lang: 'ar', dir: 'rtl' });
  });

  test('an explicit choice wins: v2 en + tenant ar → en', () => {
    const s = store({ [LOCALE_CHOICE_KEY]: 'en', [TENANT_LOCALE_KEY]: 'ar' });
    expect(resolveLocale(s, { loaded: true, defaultLanguage: 'ar' })).toBe('en');
    expect(boot({ [LOCALE_CHOICE_KEY]: 'en', [TENANT_LOCALE_KEY]: 'ar' })).toEqual({ lang: 'en', dir: 'ltr' });
    expect(resolveLocale(store({ [LOCALE_CHOICE_KEY]: 'ar' }), { loaded: true, defaultLanguage: 'en' })).toBe('ar');
  });

  test('before the tenant settings load, their placeholder is ignored', () => {
    // TenantSettingsContext's DEFAULTS say 'en' until the fetch lands. Acting on that flipped an
    // Arabic tenant RTL → LTR → RTL and overwrote the cache with 'en'.
    const cached = store({ [TENANT_LOCALE_KEY]: 'ar' });
    expect(tenantDefaultLocale({ loaded: false, defaultLanguage: 'en' })).toBeNull();
    expect(resolveLocale(cached, { loaded: false, defaultLanguage: 'en' })).toBe('ar');
    // Once loaded, the tenant's real answer applies.
    expect(resolveLocale(cached, { loaded: true, defaultLanguage: 'en' })).toBe('en');
  });

  test('a failed fetch (never loaded) keeps the cached language; nothing cached → English', () => {
    expect(resolveLocale(store({ [TENANT_LOCALE_KEY]: 'ar' }), { loaded: false })).toBe('ar');
    expect(resolveLocale(store({}), { loaded: false })).toBe('en');
    expect(boot({})).toEqual({ lang: 'en', dir: 'ltr' });
  });

  test('a tenant default that is not offered in the switcher is not applied', () => {
    expect(tenantDefaultLocale({ loaded: true, defaultLanguage: 'fr' })).toBeNull();
    expect(resolveLocale(store({}), { loaded: true, defaultLanguage: 'fr' })).toBe('en');
    expect(tenantDefaultLocale({ loaded: true, defaultLanguage: 'ar-SA' })).toBe('ar');
  });

  test('the boot script ignores junk in storage', () => {
    expect(boot({ [LOCALE_CHOICE_KEY]: '"><script>' })).toEqual({ lang: 'en', dir: 'ltr' });
  });
});
