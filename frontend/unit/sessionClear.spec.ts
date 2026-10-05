import { expect, test } from '@playwright/test';
import { clearSessionKeepingLocale } from '../src/api/clearSession';
import { LEGACY_LOCALE_KEY, LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from '../src/i18n/localeBoot';

/**
 * A failed token refresh wipes the browser session (api/client.ts). It must take every
 * signed-in user's leftovers with it, but keep the display language, or an Arabic user is
 * bounced to an English, left-to-right login page.
 */
test('a dead session clears auth and cached data but keeps the language', () => {
  const entries = new Map<string, string>([
    ['zayra_access_token', 'a'],
    ['zayra_refresh_token', 'r'],
    ['some-cached-user-data', '{"name":"x"}'],
    [LOCALE_CHOICE_KEY, 'ar'],
    [TENANT_LOCALE_KEY, 'ar'],
  ]);
  clearSessionKeepingLocale({
    getItem: (k: string) => entries.get(k) ?? null,
    setItem: (k: string, v: string) => { entries.set(k, v); },
    clear: () => entries.clear(),
  });

  expect(Object.fromEntries(entries)).toEqual({ [LOCALE_CHOICE_KEY]: 'ar', [TENANT_LOCALE_KEY]: 'ar' });
  expect(entries.has(LEGACY_LOCALE_KEY)).toBe(false); // absent keys are not invented
});
