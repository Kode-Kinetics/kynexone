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

/**
 * Wiring guard: the helper is only worth something if every way out of a session uses it.
 * A bare `localStorage.clear()` loses the language; a tokens-only removal leaves the previous
 * user's search history and company selection on a shared computer.
 */
test('every tenant session exit goes through clearSessionKeepingLocale', async () => {
  const fs = await import('node:fs');
  const path = await import('node:path');
  const root = path.resolve(__dirname, '..');
  const files: string[] = [];
  const walk = (dir: string) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (/\.(ts|tsx)$/.test(entry.name)) files.push(full);
    }
  };
  walk(path.join(root, 'src'));
  walk(path.join(root, 'app'));

  const bareClears = files
    .filter((f) => !f.endsWith(path.join('api', 'clearSession.ts')))
    .filter((f) => /localStorage\.clear\s*\(/.test(fs.readFileSync(f, 'utf8')))
    .map((f) => path.relative(root, f));
  expect(bareClears, 'use clearSessionKeepingLocale() instead of localStorage.clear()').toEqual([]);

  const auth = fs.readFileSync(path.join(root, 'src', 'contexts', 'AuthContext.tsx'), 'utf8');
  expect(auth, 'tenant logout must not remove only the tokens').not.toMatch(/removeItem\(\s*'zayra_access_token'/);
  expect(auth.match(/clearSessionKeepingLocale\(\)/g)?.length ?? 0).toBeGreaterThanOrEqual(2);
});
