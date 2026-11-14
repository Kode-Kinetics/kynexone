import { expect, test } from '@playwright/test';
import { instantToZonedInput, zonedInputToInstant } from '../src/lib/localDateTime';

// The offer's response deadline showed 19 Nov in the list (tenant zone, Riyadh) and 11/18/2026 11:43 PM in the form (the browser's own zone).
// Both now use the tenant zone, whatever zone the browser is in.
const INSTANT = '2026-11-18T23:43:00.000Z'; // 02:43 on 19 Nov in Riyadh

test.describe('tenant-zone date-time input', () => {
  let saved: string | undefined;
  test.beforeEach(() => { saved = process.env.TZ; });
  test.afterEach(() => { if (saved === undefined) delete process.env.TZ; else process.env.TZ = saved; });

  for (const browser of ['UTC', 'America/Los_Angeles', 'Asia/Karachi', 'Asia/Riyadh']) {
    test(`an instant reads as Riyadh wall time in a browser set to ${browser}`, () => {
      process.env.TZ = browser;
      expect(instantToZonedInput(INSTANT, 'Asia/Riyadh')).toBe('2026-11-19T02:43');
      expect(zonedInputToInstant('2026-11-19T02:43', 'Asia/Riyadh')).toBe(INSTANT);
    });
  }

  test('a zone with daylight saving round-trips on both sides of the change', () => {
    for (const wall of ['2026-03-08T01:30', '2026-03-08T03:30', '2026-11-01T00:30', '2026-11-01T03:30']) {
      expect(instantToZonedInput(zonedInputToInstant(wall, 'America/New_York'), 'America/New_York')).toBe(wall);
    }
  });

  test('a blank, invalid or zone-less value falls back safely', () => {
    expect(instantToZonedInput('', 'Asia/Riyadh')).toBe('');
    expect(instantToZonedInput('not a date', 'Asia/Riyadh')).toBe('');
    expect(zonedInputToInstant('2026-11-19', 'Asia/Riyadh')).toBe('');
    process.env.TZ = 'Asia/Riyadh';
    expect(instantToZonedInput(INSTANT, '')).toBe('2026-11-19T02:43');
  });
});
