import { expect, test } from '@playwright/test';
import { homePathFor, isEmployeeOnly } from '../src/lib/homePath';
import { deviceLocale, resolveLocale, type LocaleStore } from '../src/i18n/localeResolution';
import { LOCALE_CHOICE_KEY, TENANT_LOCALE_KEY } from '../src/i18n/localeBoot';

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

test('on the sign-in surfaces the device language follows an explicit choice and precedes tenant defaults', () => {
  expect(deviceLocale(['ar-SA', 'en-US'])).toBe('ar');
  expect(deviceLocale(['en-GB', 'ar'])).toBe('en');
  expect(deviceLocale(['ur-PK'])).toBeNull();
  const notLoaded = { loaded: false };
  expect(resolveLocale(store({}), notLoaded, ['ar-SA'])).toBe('ar');
  expect(resolveLocale(store({ [LOCALE_CHOICE_KEY]: 'en' }), notLoaded, ['ar-SA'])).toBe('en');
  expect(resolveLocale(store({ [TENANT_LOCALE_KEY]: 'en' }), notLoaded, ['ar-SA'])).toBe('ar');
  // Inside the app (no device list passed) nothing changes.
  expect(resolveLocale(store({}), notLoaded)).toBe('en');
  expect(resolveLocale(store({ [TENANT_LOCALE_KEY]: 'ar' }), notLoaded)).toBe('ar');
});
