import { expect, test } from '@playwright/test';
import { createFormatter, stripBidiMarks } from '../src/lib/format';

// "14 نوفمبر ,2026 00:50": the date and the time were joined with an English ", ", which the bidi algorithm
// moved in front of the year in an Arabic line. Arabic joins them with its own comma, and English is unchanged.
const INSTANT = '2026-11-14T00:50:00Z'; // 03:50 in Riyadh

test('an Arabic date-time joins date and time with the Arabic comma', () => {
  const ar = createFormatter({ locale: 'ar', timeZone: 'Asia/Riyadh' });
  expect(stripBidiMarks(ar.dateTime(INSTANT))).toBe('14 نوفمبر 2026، 03:50');
  expect(ar.dateTime(INSTANT)).not.toContain(', ');
});

test('an English date-time is unchanged', () => {
  const en = createFormatter({ locale: 'en', timeZone: 'Asia/Riyadh' });
  expect(en.dateTime(INSTANT)).toBe('14 Nov 2026, 03:50');
});

test('a calendar date has no time and no comma in either language', () => {
  for (const locale of ['en', 'ar'] as const) {
    expect(createFormatter({ locale, timeZone: 'Asia/Riyadh' }).dateTime('2026-11-14')).not.toMatch(/[,،]/);
  }
});
