import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { endOfLocalDayInstant, instantToLocalInput, localInputToInstant } from '../src/lib/localDateTime';

/**
 * A datetime-local input holds a WALL time ("2026-11-20T10:00") with no zone. Posted as it stands, the server reads it as UTC, so a
 * 10:00 interview in Riyadh (UTC+3) was stored as 10:00Z and shown back as 1:00 PM in Riyadh and 5:00 AM in New York. The wall time
 * must become a real instant using the browser's own offset on the way out, and be written back as the browser's wall time on the way in.
 */
test.describe('datetime-local wall time to a real instant and back', () => {
  let saved: string | undefined;
  test.beforeEach(() => { saved = process.env.TZ; });
  test.afterEach(() => { if (saved === undefined) delete process.env.TZ; else process.env.TZ = saved; });

  const CASES: Array<[zone: string, wall: string, utc: string]> = [
    ['Asia/Riyadh', '2026-11-20T10:00', '2026-11-20T07:00:00.000Z'],
    ['America/New_York', '2026-11-20T10:00', '2026-11-20T15:00:00.000Z'],
    ['Asia/Karachi', '2026-11-20T10:00', '2026-11-20T05:00:00.000Z'],
    ['UTC', '2026-11-20T10:00', '2026-11-20T10:00:00.000Z'],
    // A late evening wall time lands on the previous UTC day for a viewer east of Greenwich.
    ['Asia/Tokyo', '2026-11-20T01:30', '2026-11-19T16:30:00.000Z'],
  ];
  for (const [zone, wall, utc] of CASES) {
    test(`${zone}: ${wall} is ${utc}, and reads back as ${wall}`, () => {
      process.env.TZ = zone;
      expect(Intl.DateTimeFormat().resolvedOptions().timeZone).toBe(zone);
      expect(localInputToInstant(wall)).toBe(utc);
      expect(instantToLocalInput(utc)).toBe(wall);
    });
  }

  test('what one person schedules is the same moment for a viewer in another zone', () => {
    process.env.TZ = 'Asia/Riyadh';
    const instant = localInputToInstant('2026-11-20T10:00');
    process.env.TZ = 'America/New_York';
    expect(instantToLocalInput(instant)).toBe('2026-11-20T02:00');
  });

  test('a blank, a half-typed or an invalid value is blank, never a throw or the string "Invalid Date"', () => {
    expect(localInputToInstant('')).toBe('');
    expect(localInputToInstant('2026-11-20')).toBe('');
    expect(localInputToInstant('not a date')).toBe('');
    expect(instantToLocalInput(null)).toBe('');
    expect(instantToLocalInput(undefined)).toBe('');
    expect(instantToLocalInput('garbage')).toBe('');
  });

  test('an instant that already carries an offset is read at that offset', () => {
    process.env.TZ = 'Asia/Riyadh';
    expect(instantToLocalInput('2026-11-20T10:00:00+03:00')).toBe('2026-11-20T10:00');
    expect(instantToLocalInput('2026-11-20T07:00:00Z')).toBe('2026-11-20T10:00');
  });
});

test.describe('recruitment never posts a bare datetime-local value', () => {
  const read = (p: string) => fs.readFileSync(path.resolve(__dirname, '..', p), 'utf8');

  test('both interview scheduling calls convert the wall time to an instant', () => {
    const src = read('src/views/RecruitmentPage.tsx');
    expect(src).toContain('scheduledAt: localInputToInstant(interviewForm.scheduledAt)');
    expect(src).toContain('scheduledAt: localInputToInstant(schedForm.scheduledAt)');
    expect(src).not.toMatch(/scheduledAt:\s*(interviewForm|schedForm)\.scheduledAt/);
  });

  test('the offer response deadline is shown in the browser\'s wall time, not the UTC text of the instant', () => {
    const src = read('src/components/recruitment/OfferWorkspace.tsx');
    expect(src).toContain('instantToLocalInput(draft.responseDeadline)');
    expect(src).not.toContain('draft.responseDeadline?.slice(0, 16)');
    expect(src).toContain('localInputToInstant(e.target.value)');
  });

  test('every datetime-local in the recruitment components converts through the helpers before sending', () => {
    const dir = path.resolve(__dirname, '../src/components/recruitment');
    for (const f of fs.readdirSync(dir).filter(n => /\.tsx$/.test(n))) {
      const src = fs.readFileSync(path.join(dir, f), 'utf8');
      if (!src.includes('datetime-local')) continue;
      expect(src, `${f} posts a datetime-local value`).toMatch(/toISOString\(\)|localInputToInstant\(/);
    }
  });
});

// Demo R1 F7: a date-only offer deadline is the END of that day where the person sits, not 00:00Z.
test.describe('a date-only deadline is the end of that day', () => {
  let saved: string | undefined;
  test.beforeEach(() => { saved = process.env.TZ; });
  test.afterEach(() => { if (saved === undefined) delete process.env.TZ; else process.env.TZ = saved; });

  const CASES: Array<[zone: string, date: string, utc: string]> = [
    ['Asia/Riyadh', '2026-12-15', '2026-12-15T20:59:59.000Z'],
    ['America/New_York', '2026-12-15', '2026-12-16T04:59:59.000Z'],
    ['UTC', '2026-12-15', '2026-12-15T23:59:59.000Z'],
    ['Asia/Tokyo', '2026-12-15', '2026-12-15T14:59:59.000Z'],
  ];
  for (const [zone, date, utc] of CASES) {
    test(`${zone}: ${date} ends at ${utc}, still the same day on the wall clock`, () => {
      process.env.TZ = zone;
      expect(endOfLocalDayInstant(date)).toBe(utc);
      expect(instantToLocalInput(utc)).toBe(`${date}T23:59`);
    });
  }

  test('blank, malformed or impossible dates send nothing', () => {
    process.env.TZ = 'Asia/Riyadh';
    for (const bad of ['', '  ', '15/12/2026', '2026-12-15T10:00', '2026-02-31', null, undefined]) expect(endOfLocalDayInstant(bad)).toBe('');
  });
});

test('F7: the Recruitment offer form sends its date-only deadline as the end of that day, and the register and Send dialog show it as a date', () => {
  const read = (...p: string[]) => fs.readFileSync(path.resolve(process.cwd(), ...p), 'utf8');
  expect(read('src', 'views', 'RecruitmentPage.tsx')).toContain('endOfLocalDayInstant(offerForm.responseDeadline)');
  expect(read('src', 'components', 'recruitment', 'OfferSendDialog.tsx')).toContain('f.date(offer.responseDeadline)');
  expect(read('src', 'components', 'recruitment', 'OfferHistoryRegister.tsx')).toContain('f.date(row.responseDeadline)');
});
