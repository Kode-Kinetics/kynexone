/**
 * Mass entry: record the same in (and optional out) time for chosen employees over a date range.
 * The caps mirror the server (500 employees, 31 days, reason up to 500 characters); the server remains the
 * authority, these only stop a request that is certain to be refused.
 */

export const MASS_ENTRY_MAX_EMPLOYEES = 500;
export const MASS_ENTRY_MAX_DAYS = 31;
export const MASS_ENTRY_MAX_REASON = 500;

/** Message keys of the confirmation line and the submit button (ICU plurals; Arabic in i18n/attendanceBulk.ts). */
export const MASS_ENTRY_SUMMARY_KEY = '{employees, plural, one {# employee} other {# employees}} × {days, plural, one {# day} other {# days}}';
export const MASS_ENTRY_SUBMIT_KEY = 'Record attendance for {employees, plural, one {# employee} other {# employees}} × {days, plural, one {# day} other {# days}}';

const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;

/** Calendar date -> UTC day number, or null when it is not a real date. Never goes through the machine's zone. */
function dayNumber(iso: string): number | null {
  const m = DATE_ONLY.exec(iso);
  if (!m) return null;
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])];
  const ms = Date.UTC(y, mo - 1, d);
  const back = new Date(ms);
  if (back.getUTCFullYear() !== y || back.getUTCMonth() !== mo - 1 || back.getUTCDate() !== d) return null;
  return ms / 86_400_000;
}

/** Days from `from` to `to`, both included; null for an empty, malformed or backwards range. */
export function inclusiveDays(from: string, to: string): number | null {
  const a = dayNumber(from);
  const b = dayNumber(to);
  if (a === null || b === null || b < a) return null;
  return b - a + 1;
}

export type MassEntryProblem = 'no-employees' | 'bad-range' | 'too-many-employees' | 'too-many-days' | null;

export interface MassEntryPlan {
  employees: number;
  days: number;
  /** Punches that would be written if none is refused: employees x days x (1 or 2). */
  punches: number;
  problem: MassEntryProblem;
}

export function massEntryPlan(input: { employees: number; fromDate: string; toDate: string; hasOutTime: boolean }): MassEntryPlan {
  const days = inclusiveDays(input.fromDate, input.toDate);
  const base = { employees: input.employees, days: days ?? 0, punches: input.employees * (days ?? 0) * (input.hasOutTime ? 2 : 1) };
  if (input.employees <= 0) return { ...base, problem: 'no-employees' };
  if (days === null) return { ...base, problem: 'bad-range' };
  if (input.employees > MASS_ENTRY_MAX_EMPLOYEES) return { ...base, problem: 'too-many-employees' };
  if (days > MASS_ENTRY_MAX_DAYS) return { ...base, problem: 'too-many-days' };
  return { ...base, problem: null };
}

export interface MassEntryStateInput {
  employees: number;
  fromDate: string;
  toDate: string;
  inTime: string;
  outTime: string;
  reason: string;
  busy: boolean;
  canWrite: boolean;
}

export interface MassEntryState {
  plan: MassEntryPlan;
  canSubmit: boolean;
  /** The out time is not after the in time, so the out punch lands on the next day. */
  overnight: boolean;
}

const HH_MM = /^([01]\d|2[0-3]):[0-5]\d$/;

export function massEntryState(input: MassEntryStateInput): MassEntryState {
  const hasOut = HH_MM.test(input.outTime);
  const plan = massEntryPlan({ employees: input.employees, fromDate: input.fromDate, toDate: input.toDate, hasOutTime: hasOut });
  const reason = input.reason.trim();
  const canSubmit = input.canWrite && !input.busy && plan.problem === null && HH_MM.test(input.inTime)
    && (input.outTime === '' || hasOut) && reason.length > 0 && reason.length <= MASS_ENTRY_MAX_REASON;
  return { plan, canSubmit, overnight: hasOut && HH_MM.test(input.inTime) && input.outTime <= input.inTime };
}
