// Company wall-clock time ↔ UTC. An employee fixing a punch means "08:00 at work", i.e. on the
// company clock (Asia/Riyadh), whatever zone the phone is set to. Pure; tests/companyTime.test.ts.
// Owned by lane A1.

export const COMPANY_TIME_ZONE = 'Asia/Riyadh';

function wallClockAsUtcMs(instantMs: number, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: 'numeric',
    day: 'numeric',
    hour: 'numeric',
    minute: 'numeric',
    second: 'numeric',
  }).formatToParts(new Date(instantMs));
  const get = (type: Intl.DateTimeFormatPartTypes) => Number(parts.find((p) => p.type === type)?.value ?? 0);
  return Date.UTC(get('year'), get('month') - 1, get('day'), get('hour') % 24, get('minute'), get('second'));
}

/**
 * "2026-10-08" + "08:00" on the company clock → the UTC instant, as ISO.
 * Throws on malformed input rather than sending a wrong time.
 */
export function companyWallTimeToUtcIso(date: string, time: string, timeZone: string = COMPANY_TIME_ZONE): string {
  const dm = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  const tm = /^(\d{1,2}):(\d{2})$/.exec(time);
  if (!dm || !tm || Number(tm[1]) > 23 || Number(tm[2]) > 59) throw new Error(`Invalid date/time: ${date} ${time}`);
  const wall = Date.UTC(Number(dm[1]), Number(dm[2]) - 1, Number(dm[3]), Number(tm[1]), Number(tm[2]));
  // Two passes settle the offset even across a DST change (Riyadh has none, other GCC zones neither).
  let guess = wall - (wallClockAsUtcMs(wall, timeZone) - wall);
  guess = wall - (wallClockAsUtcMs(guess, timeZone) - guess);
  return new Date(guess).toISOString();
}

/** HH:mm of a picker Date's own wall-clock fields (the picker shows device-local fields). */
export function wallTimeOf(date: Date): string {
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
}

/** YYYY-MM-DD of a picker Date's own calendar fields. */
export function wallDateOf(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

/** A Date whose local fields show `day` (YYYY-MM-DD) at `time` (HH:mm) — for seeding a picker. */
export function pickerDate(day: string, time = '09:00'): Date {
  const [y, m, d] = day.split('-').map(Number);
  const [hh, mm] = time.split(':').map(Number);
  return new Date(y, (m || 1) - 1, d || 1, hh || 0, mm || 0, 0, 0);
}

export type CorrectionError = 'dateRequired' | 'timeRequired' | 'orderInvalid' | 'reasonShort';

/** What blocks a punch correction, per field. Same-day times only: clock-out must follow clock-in. */
export function validateCorrection(input: {
  date: string | null;
  clockIn: string | null;
  clockOut: string | null;
  reason: string;
}): Partial<Record<'date' | 'times' | 'reason', CorrectionError>> {
  const errors: Partial<Record<'date' | 'times' | 'reason', CorrectionError>> = {};
  if (!input.date) errors.date = 'dateRequired';
  if (!input.clockIn && !input.clockOut) errors.times = 'timeRequired';
  else if (input.clockIn && input.clockOut && input.clockOut <= input.clockIn) errors.times = 'orderInvalid';
  if (input.reason.trim().length < 5) errors.reason = 'reasonShort';
  return errors;
}
