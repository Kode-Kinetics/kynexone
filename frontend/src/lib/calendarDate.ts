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
