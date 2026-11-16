import { expect, test } from '@playwright/test';
import { daysInMonth, joinDate, monthNames, splitDate, yearChoices } from '../src/lib/dateParts';

// The browser's own month and date controls print mm/dd/yyyy whatever the page language; the pickers draw the parts themselves.
test.describe('date parts', () => {
  test('splits and joins calendar dates and months, tolerating blanks', () => {
    expect(splitDate('2026-11-14')).toEqual({ year: 2026, month: 11, day: 14 });
    expect(splitDate('2026-11')).toEqual({ year: 2026, month: 11, day: null });
    expect(splitDate('')).toEqual({ year: null, month: null, day: null });
    expect(joinDate({ year: 2026, month: 3, day: 5 })).toBe('2026-03-05');
    expect(joinDate({ year: 2026, month: 3, day: null }, 'month')).toBe('2026-03');
    expect(joinDate({ year: null, month: 3, day: 5 })).toBe('');
  });

  test('month lengths follow the calendar, including leap years, and a day is clamped when the month shrinks', () => {
    expect(daysInMonth(2026, 2)).toBe(28);
    expect(daysInMonth(2028, 2)).toBe(29);
    expect(daysInMonth(2026, 11)).toBe(30);
    expect(joinDate({ year: 2026, month: 2, day: 31 })).toBe('2026-02-28');
  });

  test('month names come in the viewer\'s language with Latin digits for the year', () => {
    expect(monthNames('en')[10]).toBe('November');
    expect(monthNames('ar')[10]).toBe('نوفمبر');
    expect(yearChoices(2026, 2)).toEqual([2024, 2025, 2026, 2027, 2028]);
  });
});
