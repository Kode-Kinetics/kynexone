import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { onboardingP3 } from '../src/i18n/onboardingP3';
import { formatMessage } from '../src/i18n/message';

/** Counts in the onboarding screens use real plural forms: English one/other, Arabic zero/one/two/few/many/other. */
const fmt = (key: string, locale: 'en' | 'ar', params: Record<string, number | string>) =>
  formatMessage(LOCALE_DICTS[locale][key] ?? key, params, locale, n => String(n));

const TASKS = '{count, plural, one {# task} other {# tasks}}';
const AR_TASKS: Array<[number, string]> = [[0, 'لا مهام'], [1, 'مهمة واحدة'], [2, 'مهمتان'], [3, '3 مهام'], [11, '11 مهمة'], [100, '100 مهمة']];

test('task counts: Arabic has all six forms', () => {
  expect(LOCALE_DICTS.ar[TASKS], 'Arabic entry exists').toBeTruthy();
  for (const [n, text] of AR_TASKS) expect(fmt(TASKS, 'ar', { count: n }), `n=${n}`).toBe(text);
});

test('task counts: English one and other', () => {
  expect(fmt(TASKS, 'en', { count: 1 })).toBe('1 task');
  for (const n of [0, 2, 11]) expect(fmt(TASKS, 'en', { count: n })).toBe(`${n} tasks`);
});

test('no onboarding sentence still joins a bare number to a plural noun', () => {
  const offenders = Object.keys(onboardingP3.ar).filter(k => /\{(count|tasks|done|refused|total|filled|kept)\} (tasks|items|settings|fields)/.test(k));
  expect(offenders).toEqual([]);
});

test('every plural sentence renders in every count in both languages without leaving a placeholder', () => {
  const keys = Object.keys(onboardingP3.ar).filter(k => k.includes(', plural,'));
  expect(keys.length).toBeGreaterThan(5);
  for (const k of keys) for (const n of [0, 1, 2, 3, 11, 100]) for (const l of ['en', 'ar'] as const) {
    const out = fmt(k, l, { count: n, tasks: n, blocking: n, done: n, refused: n, total: n, shown: n, filled: n, kept: n, reason: 'x' });
    expect(out, `${l} ${n} ${k}`).not.toMatch(/[{}]/);
  }
});
