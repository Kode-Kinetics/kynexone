import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { calendarDaysBetween, daysUntilCalendarDate, formatCalendarDate, parseCalendarDate } from '../src/lib/calendarDate';

/**
 * R01 — date-only values (salary effective date, offboarding dates, performance dates) rendered one
 * day early west of UTC, because `new Date('YYYY-MM-DD')` parses as UTC midnight. Node honours a
 * runtime TZ change, so each case runs in the zone that exposed the bug and in the tenant's own zone.
 */

const ZONES = ['America/New_York', 'America/Los_Angeles', 'Pacific/Pago_Pago', 'Asia/Riyadh', 'Pacific/Kiritimati'];

function inZone<T>(timeZone: string, fn: () => T): T {
  const previous = process.env.TZ;
  process.env.TZ = timeZone;
  try { return fn(); } finally {
    if (previous === undefined) delete process.env.TZ; else process.env.TZ = previous;
  }
}

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

/** A page's `fmtDate` declaration: the one-liner, or the block up to its closing brace. */
function fmtDateSource(file: string): string {
  const source = read(file);
  const found = /^function fmtDate\(.*\}$/m.exec(source) ?? /^function fmtDate\([^\n]*\{\n[\s\S]*?\n\}/m.exec(source);
  expect(found, `${file} declares fmtDate`).not.toBeNull();
  return found![0];
}

test.describe('calendar dates render as the day they name', () => {
  for (const zone of ZONES) {
    test(`a YYYY-MM-DD value keeps its day in ${zone}`, () => {
      inZone(zone, () => {
        expect(formatCalendarDate('2026-09-26')).toBe('Sep 26, 2026');
        expect(formatCalendarDate('2026-01-01', 'en-GB')).toBe('1 Jan 2026');
        expect(formatCalendarDate('2026-12-31')).toBe('Dec 31, 2026');
        const d = parseCalendarDate('2026-03-01');
        expect([d.getFullYear(), d.getMonth(), d.getDate()]).toEqual([2026, 2, 1]);
      });
    });
  }

  test('the bug this replaces: a bare date parsed by Date shifts a day west of UTC', () => {
    inZone('America/New_York', () => {
      expect(new Date('2026-09-26').toLocaleDateString('en-US', { day: 'numeric', month: 'short', year: 'numeric' }))
        .toBe('Sep 25, 2026');
    });
  });

  test('timestamps keep their normal local-time behaviour', () => {
    // 02:00 UTC on the 26th is still the evening of the 25th in New York, and that is correct.
    inZone('America/New_York', () => expect(formatCalendarDate('2026-09-26T02:00:00Z')).toBe('Sep 25, 2026'));
    inZone('Asia/Riyadh', () => expect(formatCalendarDate('2026-09-26T02:00:00Z')).toBe('Sep 26, 2026'));
  });

  test('blank shows a dash, and a value that is not a real date is shown as sent', () => {
    expect(formatCalendarDate(null)).toBe('—');
    expect(formatCalendarDate(undefined)).toBe('—');
    expect(formatCalendarDate('')).toBe('—');
    expect(formatCalendarDate('2026-02-30')).toBe('2026-02-30');
    expect(formatCalendarDate('not a date')).toBe('not a date');
  });

  test('offboarding, performance, payroll and leave format dates through the shared helper', () => {
    for (const file of ['src/views/OffboardingPage.tsx', 'src/views/PerformancePage.tsx', 'src/views/PayrollPage.tsx', 'src/views/LeavePage.tsx']) {
      const body = fmtDateSource(file);
      expect(body, file).toContain('formatCalendarDate(');
      expect(body, file).not.toContain('new Date(');
      expect(read(file), file).toContain("from '../lib/calendarDate'");
    }
  });
});

/**
 * R01 remainder — the offboarding countdown ("3d left") subtracted `new Date(lastWorkingDay)`, UTC
 * midnight, from the current instant. West of UTC that midnight is the previous evening, so from
 * late afternoon on the countdown ran a day short; east of UTC it ran a day long after midnight.
 */
test.describe('days until a calendar date count whole local days', () => {
  /** A wall-clock moment in the zone under test (month is 1-based). */
  const at = (y: number, m: number, d: number, h: number, min = 0) => new Date(y, m - 1, d, h, min);

  for (const zone of ZONES) {
    test(`the countdown is stable across the whole day in ${zone}`, () => {
      inZone(zone, () => {
        for (const [h, min] of [[0, 0], [9, 0], [16, 30], [22, 0], [23, 59]]) {
          const now = at(2026, 9, 29, h, min);
          expect(daysUntilCalendarDate('2026-09-30', now), `${h}:${min}`).toBe(1);
          expect(daysUntilCalendarDate('2026-09-29', now), `${h}:${min}`).toBe(0);
          expect(daysUntilCalendarDate('2026-09-28', now), `${h}:${min}`).toBe(-1);
          expect(daysUntilCalendarDate('2026-10-06', now), `${h}:${min}`).toBe(7);
        }
      });
    });
  }

  test('the bug this replaces: the UTC-midnight countdown ran a day short in a New York evening', () => {
    inZone('America/New_York', () => {
      const now = at(2026, 9, 29, 22);
      const old = Math.ceil((new Date('2026-09-30').getTime() - now.getTime()) / 86400000);
      expect(old + 0).toBe(0); // Math.ceil gives -0 here; the card rendered it as "0d left" the day before.
      expect(daysUntilCalendarDate('2026-09-30', now)).toBe(1);
    });
  });

  test('a daylight-saving change inside the span does not lose or add a day', () => {
    // US clocks go back on 1 Nov 2026 and forward on 8 Mar 2026.
    inZone('America/New_York', () => {
      expect(calendarDaysBetween('2026-10-31', '2026-11-02')).toBe(2);
      expect(calendarDaysBetween('2026-03-07', '2026-03-09')).toBe(2);
    });
  });

  test('blank or impossible dates give no countdown rather than a wrong one', () => {
    expect(daysUntilCalendarDate(null)).toBeNull();
    expect(daysUntilCalendarDate('')).toBeNull();
    expect(daysUntilCalendarDate('2026-02-30')).toBeNull();
    expect(calendarDaysBetween('2026-09-01', 'not a date')).toBeNull();
  });

  test('offboarding counts days through the shared helper, not a UTC-midnight Date', () => {
    const source = read('src/views/OffboardingPage.tsx');
    expect(source).toMatch(/const daysLeft = [^\n]*daysUntilCalendarDate\(o\.lastWorkingDay\)/);
    expect(source).not.toContain('new Date(o.lastWorkingDay)');
    expect(source).not.toMatch(/T00:00:00Z`\)\.getTime|daysBetween\(new Date/);
  });

  test('leave counts the days a request spans through the shared helper', () => {
    const source = read('src/views/LeavePage.tsx');
    expect(source).toContain('calendarDaysBetween(start, end)');
    expect(source).not.toMatch(/Date\.UTC\(year, month - 1, day\)/);
  });
});
