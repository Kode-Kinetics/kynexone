/**
 * The salary credit date (value date) of a bank file: which days can be chosen and which one is suggested. Pure; dates are ISO
 * calendar days ("2026-09-27"), never times, so no time zone can move them.
 *
 * Rules (frontend only, until the backend gives cut-off times and a holiday calendar):
 *  - no day before today, where "today" is the date in Riyadh (the file is for a Saudi bank);
 *  - no Friday or Saturday (the Saudi weekend);
 *  - the suggestion is the next working day after today.
 */

const ISO = /^(\d{4})-(\d{2})-(\d{2})$/;

/** A calendar day as UTC midnight, so weekday and arithmetic never depend on the viewer's zone. */
export function parseIsoDay(iso: string): Date | null {
  const m = ISO.exec(iso ?? '');
  if (!m) return null;
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return d.getUTCFullYear() === Number(m[1]) && d.getUTCMonth() === Number(m[2]) - 1 && d.getUTCDate() === Number(m[3]) ? d : null;
}

export function toIsoDay(d: Date): string {
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
}

export function addDays(iso: string, days: number): string {
  const d = parseIsoDay(iso);
  if (!d) return iso;
  d.setUTCDate(d.getUTCDate() + days);
  return toIsoDay(d);
}

/** Friday (5) or Saturday (6). */
export function isWeekend(iso: string): boolean {
  const d = parseIsoDay(iso);
  if (!d) return false;
  const day = d.getUTCDay();
  return day === 5 || day === 6;
}

/** Today's date in Riyadh (UTC+3, no daylight saving). */
export function todayInRiyadh(now: Date = new Date()): string {
  return toIsoDay(new Date(now.getTime() + 3 * 60 * 60 * 1000));
}

/** A day the bank can credit salaries on: from today on, not a Friday or Saturday. */
export function isSelectableCreditDate(iso: string, today: string): boolean {
  return !!parseIsoDay(iso) && iso >= today && !isWeekend(iso);
}

/** The suggestion: the first working day AFTER today. */
export function nextWorkingDay(today: string): string {
  let d = addDays(today, 1);
  for (let i = 0; i < 7 && isWeekend(d); i++) d = addDays(d, 1);
  return d;
}

/** The weeks of a month for the calendar grid, Sunday first; days outside the month are null. */
export function monthGrid(year: number, month0: number): (string | null)[][] {
  const first = new Date(Date.UTC(year, month0, 1));
  const days = new Date(Date.UTC(year, month0 + 1, 0)).getUTCDate();
  const cells: (string | null)[] = Array.from({ length: first.getUTCDay() }, () => null);
  for (let day = 1; day <= days; day++) cells.push(toIsoDay(new Date(Date.UTC(year, month0, day))));
  while (cells.length % 7 !== 0) cells.push(null);
  const weeks: (string | null)[][] = [];
  for (let i = 0; i < cells.length; i += 7) weeks.push(cells.slice(i, i + 7));
  return weeks;
}
