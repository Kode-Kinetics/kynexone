import { expect, test } from '@playwright/test';
import { addYears, ageOn, hijriLine, startDateRange, todayInZone } from '../src/lib/hire/dates';

test.describe('company-zone dates', () => {
  test('dates_riyadhTodayAt0130: 01:30 Riyadh is the Riyadh calendar day, not the UTC one', () => {
    // 2026-10-26 22:30 UTC is 2026-10-27 01:30 in Riyadh (UTC+3).
    const now = new Date('2026-10-26T22:30:00Z');
    expect(todayInZone('Asia/Riyadh', now)).toBe('2026-10-27');
    expect(now.toISOString().slice(0, 10)).toBe('2026-10-26');
  });
  test('an unknown or empty zone falls back to Riyadh', () => {
    const now = new Date('2026-10-26T22:30:00Z');
    expect(todayInZone('', now)).toBe('2026-10-27');
    expect(todayInZone('Not/AZone', now)).toBe('2026-10-27');
  });
  test('another zone gives its own day', () => {
    expect(todayInZone('America/Los_Angeles', new Date('2026-10-27T05:00:00Z'))).toBe('2026-10-26');
  });
  test('addYears clamps 29 Feb', () => {
    expect(addYears('2028-02-29', 1)).toBe('2029-02-28');
    expect(addYears('2026-10-27', -50)).toBe('1976-10-27');
  });
  test('hard start-date window is today-50y .. today+2y', () => {
    expect(startDateRange('2026-10-27')).toEqual({ min: '1976-10-27', max: '2028-10-27' });
  });
  test('age counts whole years', () => {
    expect(ageOn('2010-10-28', '2026-10-27')).toBe(15);
    expect(ageOn('2010-10-27', '2026-10-27')).toBe(16);
  });
  test('Hijri line uses Umm al-Qura and the right language', () => {
    expect(hijriLine('2026-10-14', 'en')).toMatch(/Jumada I? ?1448|1448/);
    const ar = hijriLine('2026-10-14', 'ar');
    expect(ar).toContain('1448');
    expect(ar).toMatch(/[؀-ۿ]/);
    expect(hijriLine('not-a-date', 'en')).toBe('');
  });
});
