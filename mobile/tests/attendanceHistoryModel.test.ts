import test from 'node:test';
import assert from 'node:assert/strict';
import { canFixDay, exceptionsFirst, monthGrid, summarizeMonth } from '../src/features/attendance/attendanceHistoryModel.ts';
import type { AttendanceDay } from '../src/types/index.ts';

test('month grid starts on Sunday and pads to whole weeks', () => {
  const weeks = monthGrid(2026, 10); // 1 Oct 2026 is a Thursday
  assert.deepEqual(weeks[0], [null, null, null, null, '2026-10-01', '2026-10-02', '2026-10-03']);
  assert.equal(weeks.flat().filter(Boolean).length, 31);
  assert.ok(weeks.every((w) => w.length === 7));
  assert.equal(monthGrid(2027, 2).flat().filter(Boolean).length, 28);
});

const day = (date: string, status: AttendanceDay['status'], extra: Partial<AttendanceDay> = {}): AttendanceDay => ({ date, status, ...extra });

test('summary: late days are attendance; rest days and leave are not absences', () => {
  const s = summarizeMonth([
    day('2026-10-01', 'PRESENT', { workHours: 8 }),
    day('2026-10-04', 'LATE', { workHours: 7.5, lateMinutes: 20 }),
    day('2026-10-02', 'WEEKEND'),
    day('2026-10-05', 'ON_LEAVE'),
    day('2026-10-06', 'MISSING_PUNCH'),
    day('2026-10-07', 'ABSENT'),
  ]);
  assert.deepEqual(s, { present: 2, late: 1, absentOrMissing: 2, workedMinutes: 930 });
});

test('exceptions: missing punches first, then absences, late, early exits', () => {
  const list = exceptionsFirst([
    day('2026-10-08', 'PRESENT', { earlyExitMinutes: 45 }),
    day('2026-10-04', 'LATE', { lateMinutes: 12 }),
    day('2026-10-07', 'ABSENT'),
    day('2026-10-06', 'MISSING_PUNCH'),
    day('2026-10-01', 'PRESENT'),
    day('2026-10-02', 'WEEKEND'),
  ]);
  assert.deepEqual(list.map((d) => d.date), ['2026-10-06', '2026-10-07', '2026-10-04', '2026-10-08']);
});

test('a day can be fixed once, and never in the future', () => {
  assert.equal(canFixDay('2026-10-08', '2026-10-10', day('2026-10-08', 'MISSING_PUNCH')), true);
  assert.equal(canFixDay('2026-10-08', '2026-10-10', day('2026-10-08', 'MISSING_PUNCH', { hasCorrection: true })), false);
  assert.equal(canFixDay('2026-10-11', '2026-10-10', null), false);
});
