// Attendance history — month grid, summary and exception ordering. Pure; tested in
// tests/attendanceHistoryModel.test.ts. Owned by lane A1.

import type { AttendanceDay, AttendanceStatus } from '../../types';

export type Tone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

/** Status → tone. Colour is never the only signal: every use also shows the status in words. */
export const STATUS_TONE: Record<AttendanceStatus, Tone> = {
  PRESENT: 'success',
  LATE: 'warning',
  HALF_DAY: 'warning',
  ABSENT: 'danger',
  MISSING_PUNCH: 'danger',
  ON_LEAVE: 'info',
  HOLIDAY: 'info',
  WEEKEND: 'neutral',
  UNKNOWN: 'neutral',
};

/**
 * Weeks of a month as rows of 7 calendar days (YYYY-MM-DD) or null padding. The Saudi working
 * week starts on Sunday, so the grid does too (row direction mirrors in Arabic by itself).
 */
export function monthGrid(year: number, month: number, weekStartsOn = 0): (string | null)[][] {
  const first = new Date(Date.UTC(year, month - 1, 1));
  const days = new Date(Date.UTC(year, month, 0)).getUTCDate();
  const lead = (first.getUTCDay() - weekStartsOn + 7) % 7;
  const cells: (string | null)[] = Array.from({ length: lead }, () => null);
  for (let d = 1; d <= days; d++) {
    cells.push(`${year}-${String(month).padStart(2, '0')}-${String(d).padStart(2, '0')}`);
  }
  while (cells.length % 7 !== 0) cells.push(null);
  const weeks: (string | null)[][] = [];
  for (let i = 0; i < cells.length; i += 7) weeks.push(cells.slice(i, i + 7));
  return weeks;
}

export interface MonthSummary {
  present: number;
  late: number;
  absentOrMissing: number;
  workedMinutes: number;
}

/** Attended = present, late or half day (a late day is attendance, as the server counts it). */
export function summarizeMonth(records: readonly AttendanceDay[]): MonthSummary {
  return records.reduce<MonthSummary>(
    (acc, r) => {
      if (r.status === 'PRESENT' || r.status === 'LATE' || r.status === 'HALF_DAY') acc.present += 1;
      if (r.status === 'LATE' || (r.lateMinutes ?? 0) > 0) acc.late += 1;
      if (r.status === 'ABSENT' || r.status === 'MISSING_PUNCH') acc.absentOrMissing += 1;
      acc.workedMinutes += Math.round((r.workHours ?? 0) * 60);
      return acc;
    },
    { present: 0, late: 0, absentOrMissing: 0, workedMinutes: 0 },
  );
}

function exceptionRank(r: AttendanceDay): number {
  if (r.status === 'MISSING_PUNCH') return 0;
  if (r.status === 'ABSENT') return 1;
  if (r.status === 'LATE' || (r.lateMinutes ?? 0) > 0) return 2;
  if ((r.earlyExitMinutes ?? 0) > 0) return 3;
  return -1;
}

/** Days that need a look, most serious first, newest first within each kind. */
export function exceptionsFirst(records: readonly AttendanceDay[]): AttendanceDay[] {
  return records
    .filter((r) => exceptionRank(r) >= 0)
    .sort((a, b) => exceptionRank(a) - exceptionRank(b) || b.date.localeCompare(a.date));
}

/** Can the employee ask to fix this day? Past or today, and no correction already pending. */
export function canFixDay(day: string, today: string, record?: AttendanceDay | null): boolean {
  if (day > today) return false;
  return !record?.hasCorrection;
}
