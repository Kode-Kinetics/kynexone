/**
 * Calendar dates vs instants.
 *
 * The API sends a calendar date (a C# `DateOnly`: salary effective date, last working day, goal due
 * date, review period) as a bare `YYYY-MM-DD`. ECMAScript parses that form as UTC MIDNIGHT, so
 * `new Date('2026-09-26').toLocaleDateString()` shows 25 Sep in every browser west of UTC — the date
 * a user entered comes back one day early. A calendar date is a fact about a day, not an instant, so
 * it is built from its parts in local time and never shifts.
 *
 * Timestamps (`2026-09-26T14:05:00Z`, `...T00:00:00`) are real instants and keep their normal
 * local-time behaviour: a record created late on the 25th in New York still reads the 25th there.
 */

const CALENDAR_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

/** A `YYYY-MM-DD` value as that local calendar day; anything else parsed as the instant it names. */
export function parseCalendarDate(value: string): Date {
  const parts = CALENDAR_DATE.exec(value);
  if (!parts) return new Date(value);
  const [year, month, day] = [Number(parts[1]), Number(parts[2]), Number(parts[3])];
  const date = new Date(year, month - 1, day);
  // `new Date(2026, 1, 30)` quietly rolls over to 2 March; a day that does not exist is invalid.
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day
    ? date
    : new Date(Number.NaN);
}

const MS_PER_DAY = 86_400_000;

/** The local calendar day a Date falls on, as a whole-day count: subtracting two gives days apart. */
function localDayNumber(date: Date): number {
  return Math.round(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / MS_PER_DAY);
}

/**
 * Whole calendar days from `from` to `to` (each a `YYYY-MM-DD` or a Date): 0 for the same day,
 * negative when `to` is earlier. Counted day to day, never from the clock time, so it cannot
 * shift with the viewer's timezone or the hour it is read. Null when either side is not a date.
 */
export function calendarDaysBetween(from: string | Date, to: string | Date): number | null {
  const start = typeof from === 'string' ? parseCalendarDate(from) : from;
  const end = typeof to === 'string' ? parseCalendarDate(to) : to;
  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) return null;
  return localDayNumber(end) - localDayNumber(start);
}

/**
 * Days left until a calendar date, counted from the viewer's today: 1 the day before, 0 on the day,
 * negative once it has passed. `new Date('YYYY-MM-DD')` is UTC midnight, which in the Americas is
 * the previous evening, so a countdown built on it ran a day short there from late afternoon on.
 */
export function daysUntilCalendarDate(value: string | null | undefined, now: Date = new Date()): number | null {
  if (!value) return null;
  return calendarDaysBetween(now, value);
}

const DEFAULT_OPTIONS: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'short', year: 'numeric' };

/**
 * Formats a calendar date or a timestamp for display. Blank shows a dash; a value that is not a date
 * is shown as sent rather than as "Invalid Date", so a bad record is visible instead of disguised.
 */
export function formatCalendarDate(
  value: string | null | undefined,
  locale = 'en-US',
  options: Intl.DateTimeFormatOptions = DEFAULT_OPTIONS,
): string {
  if (!value) return '—';
  const date = parseCalendarDate(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString(locale, options);
}
