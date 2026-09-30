import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { formatCalendarDate, parseCalendarDate } from '../src/lib/calendarDate';

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

  test('offboarding, performance and payroll format dates through the shared helper', () => {
    for (const file of ['src/views/OffboardingPage.tsx', 'src/views/PerformancePage.tsx', 'src/views/PayrollPage.tsx']) {
      const body = fmtDateSource(file);
      expect(body, file).toContain('formatCalendarDate(');
      expect(body, file).not.toContain('new Date(');
      expect(read(file), file).toContain("from '../lib/calendarDate'");
    }
  });
});
