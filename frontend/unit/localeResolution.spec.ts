import { expect, test } from '@playwright/test';
import { DEVICE_LANGUAGE_PATHS, LEGACY_LOCALE_KEY, LOCALE_BOOT, LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from '../src/i18n/localeBoot';
import { resolveLocale, tenantDefaultLocale } from '../src/i18n/localeResolution';

/**
 * Which language the UI starts in (i18n/localeResolution.ts) and the pre-paint script that sets
 * `dir` before React hydrates (i18n/localeBoot.ts). Both must agree, or the first frame and the
 * hydrated page point in different directions.
 */

/** A fake localStorage; `entries` is mutated, so a test can see what was written. */
const store = (entries: Record<string, string>) => ({
  get: (k: string) => entries[k] ?? null,
  set: (k: string, v: string) => { entries[k] = v; },
});

/**
 * Runs LOCALE_BOOT against a fake localStorage (`entries`, mutated), <html>, navigator and location;
 * returns what it set. Node has a real global `navigator`, so one is always passed (empty by default)
 * to keep the host's language out of the result.
 */
function boot(entries: Record<string, string>, opts: { languages?: string[]; pathname?: string } = {}) {
  const html = { lang: 'en', dir: 'ltr' };
  const languages = opts.languages ?? [];
  new Function('localStorage', 'document', 'navigator', 'location', LOCALE_BOOT)(
    { getItem: (k: string) => entries[k] ?? null, setItem: (k: string, v: string) => { entries[k] = v; } },
    { documentElement: html },
    { languages, language: languages[0] ?? '' },
    { pathname: opts.pathname ?? '/dashboard' },
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

  test('a real legacy Arabic choice survives: legacy ar + tenant en → ar, copied to v2 once', () => {
    // Old builds wrote only 'en' by themselves, so a legacy 'ar' was the user's own pick.
    const entries: Record<string, string> = { [LEGACY_LOCALE_KEY]: 'ar' };
    expect(resolveLocale(store(entries), { loaded: true, defaultLanguage: 'en' })).toBe('ar');
    expect(entries[LOCALE_CHOICE_KEY]).toBe('ar');
    // Before paint, too.
    const early: Record<string, string> = { [LEGACY_LOCALE_KEY]: 'ar', [TENANT_LOCALE_KEY]: 'en' };
    expect(boot(early)).toEqual({ lang: 'ar', dir: 'rtl' });
    expect(early[LOCALE_CHOICE_KEY]).toBe('ar');
  });

  test('legacy en is still not a choice, and is not copied', () => {
    const entries: Record<string, string> = { [LEGACY_LOCALE_KEY]: 'en' };
    expect(resolveLocale(store(entries), { loaded: true, defaultLanguage: 'ar' })).toBe('ar');
    expect(entries[LOCALE_CHOICE_KEY]).toBeUndefined();
    const early: Record<string, string> = { [LEGACY_LOCALE_KEY]: 'en' };
    boot(early);
    expect(early[LOCALE_CHOICE_KEY]).toBeUndefined();
  });

  test('an existing v2 key wins over the legacy value, and is not overwritten', () => {
    const entries: Record<string, string> = { [LOCALE_CHOICE_KEY]: 'en', [LEGACY_LOCALE_KEY]: 'ar' };
    expect(resolveLocale(store(entries), { loaded: true, defaultLanguage: 'ar' })).toBe('en');
    expect(entries[LOCALE_CHOICE_KEY]).toBe('en');
    const early: Record<string, string> = { [LOCALE_CHOICE_KEY]: 'en', [LEGACY_LOCALE_KEY]: 'ar' };
    expect(boot(early)).toEqual({ lang: 'en', dir: 'ltr' });
    expect(early[LOCALE_CHOICE_KEY]).toBe('en');
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

  test('signed in, an Arabic device does not override an English tenant: /dashboard → en', () => {
    const arabicPhone = ['ar-SA', 'en-US'];
    // Signed-in pages (AppLayout's LocaleProvider) pass no device languages.
    expect(resolveLocale(store({ [TENANT_LOCALE_KEY]: 'en' }), { loaded: true, defaultLanguage: 'en' })).toBe('en');
    expect(resolveLocale(store({}), { loaded: false })).toBe('en');
    // Before paint on any signed-in path, the cached tenant default decides, not the device.
    for (const pathname of ['/dashboard', '/ess', '/employees/42', '/', '/login-help']) {
      expect(boot({ [TENANT_LOCALE_KEY]: 'en' }, { languages: arabicPhone, pathname }), pathname).toEqual({ lang: 'en', dir: 'ltr' });
      expect(boot({}, { languages: arabicPhone, pathname }), pathname).toEqual({ lang: 'en', dir: 'ltr' });
    }
    // And an Arabic tenant stays Arabic there.
    expect(boot({ [TENANT_LOCALE_KEY]: 'ar' }, { languages: ['en-US'], pathname: '/dashboard' })).toEqual({ lang: 'ar', dir: 'rtl' });
  });

  test('on /login and /welcome, an Arabic device starts in Arabic over a cached English tenant', () => {
    expect(DEVICE_LANGUAGE_PATHS).toEqual(['/login', '/welcome']);
    for (const pathname of ['/login', '/welcome', '/login/']) {
      expect(boot({ [TENANT_LOCALE_KEY]: 'en' }, { languages: ['ar-SA', 'en-US'], pathname }), pathname).toEqual({ lang: 'ar', dir: 'rtl' });
      expect(boot({}, { languages: ['ar'], pathname }), pathname).toEqual({ lang: 'ar', dir: 'rtl' });
      // An English device leaves the tenant default in charge.
      expect(boot({ [TENANT_LOCALE_KEY]: 'ar' }, { languages: ['en-US'], pathname }), pathname).toEqual({ lang: 'ar', dir: 'rtl' });
    }
    // LoginPage and WelcomePage pass the device languages to resolveLocale.
    expect(resolveLocale(store({ [TENANT_LOCALE_KEY]: 'en' }), { loaded: false }, ['ar-SA'])).toBe('ar');
  });

  test('an explicit choice still wins on /login', () => {
    expect(boot({ [LOCALE_CHOICE_KEY]: 'en' }, { languages: ['ar-SA'], pathname: '/login' })).toEqual({ lang: 'en', dir: 'ltr' });
    expect(boot({ [LOCALE_CHOICE_KEY]: 'ar' }, { languages: ['en-US'], pathname: '/login' })).toEqual({ lang: 'ar', dir: 'rtl' });
    expect(resolveLocale(store({ [LOCALE_CHOICE_KEY]: 'en' }), { loaded: false }, ['ar-SA'])).toBe('en');
  });

  test('the boot script ignores junk in storage', () => {
    expect(boot({ [LOCALE_CHOICE_KEY]: '"><script>' })).toEqual({ lang: 'en', dir: 'ltr' });
  });
});
