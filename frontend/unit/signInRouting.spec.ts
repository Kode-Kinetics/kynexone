import { expect, test } from '@playwright/test';
import { homePathFor, isEmployeeOnly } from '../src/lib/homePath';
import { deviceLocale, resolveLocale, type LocaleStore } from '../src/i18n/localeResolution';
import { LOCALE_BOOT, LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from '../src/i18n/localeBoot';

const EMPLOYEE = ['dashboard.read', 'profile.read', 'ess.read', 'ess.write', 'performance.read', 'loans.self'];

test('employee-only users land on Self-Service; anyone with more lands on the dashboard', () => {
  expect(homePathFor({ permissions: EMPLOYEE })).toBe('/ess');
  expect(homePathFor({ permissions: [...EMPLOYEE, 'ess.documents'] })).toBe('/ess');
  expect(homePathFor({ permissions: ['ess.read'], accessMode: 'ESSOnly' })).toBe('/ess');
  expect(homePathFor({ permissions: [...EMPLOYEE, 'manager.read'] })).toBe('/dashboard');
  expect(homePathFor({ permissions: [...EMPLOYEE, 'employees.read'] })).toBe('/dashboard');
  expect(homePathFor({ permissions: ['dashboard.read', 'users.manage'] })).toBe('/dashboard');
  // No ess.read: Self-Service would deny them, so never send them there.
  expect(isEmployeeOnly({ permissions: ['dashboard.read'], accessMode: 'ESSOnly' })).toBe(false);
  expect(homePathFor(null)).toBe('/dashboard');
});

function store(values: Record<string, string>): LocaleStore {
  return { get: (k) => values[k] ?? null, set: (k, v) => { values[k] = v; } };
}

test('an Arabic device language follows an explicit choice and precedes tenant defaults', () => {
  expect(deviceLocale(['ar-SA', 'en-US'])).toBe('ar');
  // English is never taken from the device: it is every office PC's default and says nothing.
  expect(deviceLocale(['en-GB', 'ar'])).toBeNull();
  expect(deviceLocale(['ur-PK'])).toBeNull();
  const notLoaded = { loaded: false };
  expect(resolveLocale(store({}), notLoaded, ['ar-SA'])).toBe('ar');
  expect(resolveLocale(store({ [LOCALE_CHOICE_KEY]: 'en' }), notLoaded, ['ar-SA'])).toBe('en');
  expect(resolveLocale(store({ [TENANT_LOCALE_KEY]: 'en' }), notLoaded, ['ar-SA'])).toBe('ar');
  // An English device leaves the tenant default in charge.
  expect(resolveLocale(store({ [TENANT_LOCALE_KEY]: 'ar' }), notLoaded, ['en-US'])).toBe('ar');
  expect(resolveLocale(store({}), { loaded: true, defaultLanguage: 'ar' }, ['en-US'])).toBe('ar');
  expect(resolveLocale(store({}), notLoaded)).toBe('en');
  expect(resolveLocale(store({ [TENANT_LOCALE_KEY]: 'ar' }), notLoaded)).toBe('ar');
});

test('before first paint on the sign-in page, the boot script applies the same order', () => {
  const boot = (entries: Record<string, string>, languages: string[], pathname = '/login') => {
    const html = { lang: 'en', dir: 'ltr' };
    new Function('localStorage', 'document', 'navigator', 'location', LOCALE_BOOT)(
      { getItem: (k: string) => entries[k] ?? null, setItem: (k: string, v: string) => { entries[k] = v; } },
      { documentElement: html },
      { languages, language: languages[0] },
      { pathname },
    );
    return html;
  };
  expect(boot({}, ['ar-SA'])).toEqual({ lang: 'ar', dir: 'rtl' });
  expect(boot({ [LOCALE_CHOICE_KEY]: 'en' }, ['ar-SA'])).toEqual({ lang: 'en', dir: 'ltr' });
  expect(boot({ [TENANT_LOCALE_KEY]: 'ar' }, ['en-US'])).toEqual({ lang: 'ar', dir: 'rtl' });
  expect(boot({}, ['en-US'])).toEqual({ lang: 'en', dir: 'ltr' });
  // Signed-in pages are not the sign-in page: the device language is not consulted there.
  expect(boot({ [TENANT_LOCALE_KEY]: 'en' }, ['ar-SA'], '/ess')).toEqual({ lang: 'en', dir: 'ltr' });
});
