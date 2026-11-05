import { expect, test } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import {
  MASS_ENTRY_MAX_DAYS,
  MASS_ENTRY_SUBMIT_KEY,
  MASS_ENTRY_SUMMARY_KEY,
  MASS_ENTRY_MAX_EMPLOYEES,
  MASS_ENTRY_MAX_PUNCHES,
  MASS_ENTRY_PUNCH_MATHS_KEY,
  massEntryAllowed,
  inclusiveDays,
  massEntryPlan,
  massEntryState,
} from '../src/lib/attendanceBulkEntry';

test('inclusiveDays counts both ends and ignores the machine time zone', () => {
  expect(inclusiveDays('2026-06-01', '2026-06-01')).toBe(1);
  expect(inclusiveDays('2026-06-01', '2026-06-05')).toBe(5);
  expect(inclusiveDays('2026-02-27', '2026-03-02')).toBe(4); // across a month end
  expect(inclusiveDays('2026-03-28', '2026-03-30')).toBe(3); // around a DST change elsewhere
});

test('inclusiveDays is null for an empty, malformed or backwards range', () => {
  expect(inclusiveDays('', '2026-06-01')).toBeNull();
  expect(inclusiveDays('2026-06-05', '2026-06-01')).toBeNull();
  expect(inclusiveDays('2026-02-31', '2026-03-02')).toBeNull();
  expect(inclusiveDays('junk', 'junk')).toBeNull();
});

test('plan multiplies employees by days and counts punches', () => {
  const plan = massEntryPlan({ employees: 12, fromDate: '2026-06-01', toDate: '2026-06-05', hasOutTime: true });
  expect(plan).toMatchObject({ employees: 12, days: 5, punches: 120, problem: null });
  expect(massEntryPlan({ employees: 12, fromDate: '2026-06-01', toDate: '2026-06-05', hasOutTime: false }).punches).toBe(60);
});

test('plan names the cap that is exceeded (500 employees, 31 days)', () => {
  expect(MASS_ENTRY_MAX_EMPLOYEES).toBe(500);
  expect(MASS_ENTRY_MAX_DAYS).toBe(31);
  expect(massEntryPlan({ employees: 501, fromDate: '2026-06-01', toDate: '2026-06-02', hasOutTime: false }).problem).toBe('too-many-employees');
  expect(massEntryPlan({ employees: 500, fromDate: '2026-06-01', toDate: '2026-06-02', hasOutTime: false }).problem).toBeNull();
  expect(massEntryPlan({ employees: 2, fromDate: '2026-06-01', toDate: '2026-07-02', hasOutTime: false }).problem).toBe('too-many-days');
  expect(massEntryPlan({ employees: 2, fromDate: '2026-06-01', toDate: '2026-07-01', hasOutTime: false }).problem).toBeNull();
});

test('plan flags no selection and a bad range', () => {
  expect(massEntryPlan({ employees: 0, fromDate: '2026-06-01', toDate: '2026-06-01', hasOutTime: false }).problem).toBe('no-employees');
  expect(massEntryPlan({ employees: 3, fromDate: '2026-06-05', toDate: '2026-06-01', hasOutTime: false }).problem).toBe('bad-range');
});

test('summary sentence reads "N employees x D days" with the right singular', () => {
  expect(translate('en', MASS_ENTRY_SUMMARY_KEY, { employees: 12, days: 5 })).toBe('12 employees × 5 days');
  expect(translate('en', MASS_ENTRY_SUMMARY_KEY, { employees: 1, days: 1 })).toBe('1 employee × 1 day');
});

test('button label names what will be created', () => {
  expect(translate('en', MASS_ENTRY_SUBMIT_KEY, { employees: 12, days: 5 })).toBe('Record attendance for 12 employees × 5 days');
  expect(translate('en', MASS_ENTRY_SUBMIT_KEY, { employees: 1, days: 1 })).toBe('Record attendance for 1 employee × 1 day');
});

const ok = { employees: 2, fromDate: '2026-06-01', toDate: '2026-06-02', inTime: '09:00', outTime: '', reason: 'Biometric outage', busy: false, canWrite: true };

test('submit state: needs permission, in time, a reason and a valid plan; out time optional', () => {
  expect(massEntryState(ok).canSubmit).toBe(true);
  expect(massEntryState({ ...ok, canWrite: false }).canSubmit).toBe(false);
  expect(massEntryState({ ...ok, inTime: '' }).canSubmit).toBe(false);
  expect(massEntryState({ ...ok, reason: '   ' }).canSubmit).toBe(false);
  expect(massEntryState({ ...ok, employees: 0 }).canSubmit).toBe(false);
  expect(massEntryState({ ...ok, busy: true }).canSubmit).toBe(false);
  expect(massEntryState({ ...ok, outTime: '17:00' }).canSubmit).toBe(true);
});

test('submit state: reason is capped at 500 characters like the server', () => {
  expect(massEntryState({ ...ok, reason: 'x'.repeat(500) }).canSubmit).toBe(true);
  expect(massEntryState({ ...ok, reason: 'x'.repeat(501) }).canSubmit).toBe(false);
});

test('an out time not after the in time is an overnight shift and is announced, not rejected', () => {
  expect(massEntryState({ ...ok, outTime: '06:00' }).overnight).toBe(true);
  expect(massEntryState({ ...ok, outTime: '17:00' }).overnight).toBe(false);
  expect(massEntryState({ ...ok, outTime: '' }).overnight).toBe(false);
  expect(massEntryState({ ...ok, outTime: '06:00' }).canSubmit).toBe(true);
});

test('the server caps one mass entry at 5,000 punches in total', () => {
  expect(MASS_ENTRY_MAX_PUNCHES).toBe(5000);
  // 100 x 31 x 2 = 6,200
  const over = massEntryPlan({ employees: 100, fromDate: '2026-05-01', toDate: '2026-05-31', hasOutTime: true });
  expect(over).toMatchObject({ punches: 6200, problem: 'too-many-punches' });
  // the same people and days with no out time: 3,100
  expect(massEntryPlan({ employees: 100, fromDate: '2026-05-01', toDate: '2026-05-31', hasOutTime: false })).toMatchObject({ punches: 3100, problem: null });
  // exactly at the limit is allowed: 250 x 10 x 2 = 5,000
  expect(massEntryPlan({ employees: 250, fromDate: '2026-05-01', toDate: '2026-05-10', hasOutTime: true })).toMatchObject({ punches: 5000, problem: null });
  expect(massEntryPlan({ employees: 251, fromDate: '2026-05-01', toDate: '2026-05-10', hasOutTime: true }).problem).toBe('too-many-punches');
});

test('the per-employee and per-day caps are reported before the punch total', () => {
  expect(massEntryPlan({ employees: 501, fromDate: '2026-05-01', toDate: '2026-05-31', hasOutTime: true }).problem).toBe('too-many-employees');
  expect(massEntryPlan({ employees: 400, fromDate: '2026-05-01', toDate: '2026-07-01', hasOutTime: true }).problem).toBe('too-many-days');
});

test('an over-limit submit is blocked, and the maths sentence shows the numbers', () => {
  const state = massEntryState({ employees: 100, fromDate: '2026-05-01', toDate: '2026-05-31', inTime: '09:00', outTime: '17:00', reason: 'x', busy: false, canWrite: true });
  expect(state.canSubmit).toBe(false);
  expect(translate('en', MASS_ENTRY_PUNCH_MATHS_KEY, { employees: 100, days: 31, perDay: 2, punches: 6200, max: 5000 }))
    .toBe('100 employees × 31 days × 2 punches = 6,200; the limit is 5,000.');
  expect(translate('en', MASS_ENTRY_PUNCH_MATHS_KEY, { employees: 100, days: 31, perDay: 1, punches: 3100, max: 5000 }))
    .toBe('100 employees × 31 days × 1 punch = 3,100; the limit is 5,000.');
});

test('mass entry needs BOTH attendance.write and attendance.bulk_import', () => {
  const has = (...keys: string[]) => (p: string) => keys.includes(p);
  expect(massEntryAllowed(has('attendance.write', 'attendance.bulk_import'))).toBe(true);
  expect(massEntryAllowed(has('attendance.write'))).toBe(false);
  expect(massEntryAllowed(has('attendance.bulk_import'))).toBe(false);
  expect(massEntryAllowed(has())).toBe(false);
});
