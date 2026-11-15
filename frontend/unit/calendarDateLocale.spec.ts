import { test, expect } from '@playwright/test';
import { formatCalendarDateForLocale } from '../src/lib/calendarDate';

// HR leave cards showed "Aug 24, 2026" on the Arabic screen: the page formatted every date as en-US.

test('a calendar date reads in Arabic on the Arabic screen, with Western digits', () => {
  const text = formatCalendarDateForLocale('2026-08-24', 'ar');
  expect(text).toContain('2026');
  expect(text).toContain('24');
  expect(text).toMatch(/[؀-ۿ]/);
  expect(text).not.toMatch(/Aug/);
  expect(text).not.toMatch(/[٠-٩]/);
});

test('English keeps the existing "Aug 24, 2026" form, and a calendar date never shifts a day', () => {
  expect(formatCalendarDateForLocale('2026-08-24', 'en')).toBe('Aug 24, 2026');
  expect(formatCalendarDateForLocale('2026-01-01', 'ar')).toContain('1');
});

test('blank shows a dash and a value that is not a date is shown as sent, in either language', () => {
  expect(formatCalendarDateForLocale(null, 'ar')).toBe('—');
  expect(formatCalendarDateForLocale('', 'en')).toBe('—');
  expect(formatCalendarDateForLocale('not-a-date', 'ar')).toBe('not-a-date');
});
