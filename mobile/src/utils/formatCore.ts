// ============================================================
// Locale + time-zone aware formatting — pure logic.
//
// No react / react-native imports: utils/format.ts wraps this in the useFormat() hook, and
// tests/format.test.ts drives it directly under `node --test`.
//
// Rules
// - Instants (ISO strings with a time, Date objects, epoch ms) are shown in the company's time
//   zone, never the device's: a demo phone on US time must still show Riyadh clock times.
// - Date-only strings ('2026-10-10') are calendar dates and are never shifted.
// - Wall-clock strings ('09:30' / '09:30:00', e.g. shift start) are shown as written.
// - ISO date-times without an offset are read as UTC: the API's *Utc fields are UTC.
// - Missing / unparseable input renders as an em dash — never a guessed value.
// ============================================================

export type FormatLanguage = 'en' | 'ar';
export type DateStyle = 'short' | 'medium' | 'long' | 'full' | 'monthYear' | 'monthShort' | 'dayMonth' | 'weekday';
export type DateInput = Date | string | number | null | undefined;

export const DEFAULT_TIME_ZONE = 'Asia/Riyadh';
export const EMPTY_VALUE = '—';

export interface FormatterOptions {
  language: FormatLanguage;
  /** IANA zone of the company; falls back to Asia/Riyadh. */
  timeZone?: string | null;
  /** Arabic-Indic digits in Arabic UI. Default false (Latin digits everywhere, like the web app). */
  arabicDigits?: boolean;
}

const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;
const WALL_TIME = /^(\d{1,2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?$/;
const HAS_OFFSET = /(Z|[+-]\d{2}:?\d{2})$/i;

type Parsed =
  | { kind: 'instant'; date: Date }
  | { kind: 'calendar'; date: Date } // UTC midnight of the calendar day
  | { kind: 'wall'; date: Date }; // 1970-01-01 at that wall time, UTC

function isValidDate(d: Date): boolean {
  return !Number.isNaN(d.getTime());
}

/** Classifies an input. Returns null when there is nothing trustworthy to show. */
export function parseDateInput(value: DateInput): Parsed | null {
  if (value === null || value === undefined || value === '') return null;
  if (value instanceof Date) return isValidDate(value) ? { kind: 'instant', date: value } : null;
  if (typeof value === 'number') {
    const d = new Date(value);
    return isValidDate(d) ? { kind: 'instant', date: d } : null;
  }
  const s = value.trim();
  const dateOnly = DATE_ONLY.exec(s);
  if (dateOnly) {
    const d = new Date(Date.UTC(Number(dateOnly[1]), Number(dateOnly[2]) - 1, Number(dateOnly[3])));
    return isValidDate(d) ? { kind: 'calendar', date: d } : null;
  }
  const wall = WALL_TIME.exec(s);
  if (wall) {
    const h = Number(wall[1]);
    const m = Number(wall[2]);
    if (h > 23 || m > 59) return null;
    return { kind: 'wall', date: new Date(Date.UTC(1970, 0, 1, h, m, Number(wall[3] ?? 0))) };
  }
  const iso = s.includes('T') && !HAS_OFFSET.test(s) ? `${s}Z` : s;
  const d = new Date(iso);
  return isValidDate(d) ? { kind: 'instant', date: d } : null;
}

export function resolveTimeZone(candidate?: string | null): string {
  if (!candidate) return DEFAULT_TIME_ZONE;
  try {
    new Intl.DateTimeFormat('en', { timeZone: candidate });
    return candidate;
  } catch {
    return DEFAULT_TIME_ZONE;
  }
}

function localeTag(language: FormatLanguage, calendar: 'gregory' | 'islamic-umalqura', arabicDigits: boolean): string {
  const base = language === 'ar' ? 'ar-SA' : 'en-SA';
  const nu = language === 'ar' && arabicDigits ? 'arab' : 'latn';
  return `${base}-u-ca-${calendar}-nu-${nu}`;
}

const DATE_OPTIONS: Record<DateStyle, Intl.DateTimeFormatOptions> = {
  short: { day: '2-digit', month: '2-digit', year: 'numeric' },
  medium: { day: 'numeric', month: 'short', year: 'numeric' },
  long: { day: 'numeric', month: 'long', year: 'numeric' },
  full: { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' },
  monthYear: { month: 'long', year: 'numeric' },
  monthShort: { month: 'short', year: 'numeric' },
  dayMonth: { day: 'numeric', month: 'short' },
  weekday: { weekday: 'short' },
};

export function createFormatter(options: FormatterOptions) {
  const language = options.language;
  const timeZone = resolveTimeZone(options.timeZone);
  const arabicDigits = Boolean(options.arabicDigits);
  const locale = localeTag(language, 'gregory', arabicDigits);
  const hijriLocale = localeTag(language, 'islamic-umalqura', arabicDigits);
  const numberLocale = `${language === 'ar' ? 'ar-SA' : 'en-SA'}-u-nu-${language === 'ar' && arabicDigits ? 'arab' : 'latn'}`;

  // Calendar dates and wall times are stored at UTC; instants are shown in the company zone.
  const zoneFor = (p: Parsed) => (p.kind === 'instant' ? timeZone : 'UTC');

  function date(value: DateInput, style: DateStyle = 'medium'): string {
    const p = parseDateInput(value);
    if (!p || p.kind === 'wall') return EMPTY_VALUE;
    return new Intl.DateTimeFormat(locale, { ...DATE_OPTIONS[style], timeZone: zoneFor(p) }).format(p.date);
  }

  function time(value: DateInput): string {
    const p = parseDateInput(value);
    if (!p || p.kind === 'calendar') return EMPTY_VALUE;
    return new Intl.DateTimeFormat(locale, { hour: 'numeric', minute: '2-digit', timeZone: zoneFor(p) }).format(p.date);
  }

  function dateTime(value: DateInput, style: DateStyle = 'medium'): string {
    const p = parseDateInput(value);
    if (!p) return EMPTY_VALUE;
    if (p.kind !== 'instant') return p.kind === 'calendar' ? date(value, style) : time(value);
    return `${date(value, style)} · ${time(value)}`;
  }

  /** Hijri (Umm al-Qura) date, for a secondary line. Empty string when the runtime cannot render it. */
  function hijri(value: DateInput, style: Exclude<DateStyle, 'weekday'> = 'long'): string {
    const p = parseDateInput(value);
    if (!p || p.kind === 'wall') return '';
    try {
      const fmt = new Intl.DateTimeFormat(hijriLocale, { ...DATE_OPTIONS[style], timeZone: zoneFor(p) });
      if (!fmt.resolvedOptions().calendar.startsWith('islamic')) return '';
      return fmt.format(p.date);
    } catch {
      return '';
    }
  }

  function money(amount: number | null | undefined, currency = 'SAR', opts: { decimals?: number } = {}): string {
    if (amount === null || amount === undefined || !Number.isFinite(amount)) return EMPTY_VALUE;
    const digits = opts.decimals ?? 2;
    return new Intl.NumberFormat(numberLocale, {
      style: 'currency',
      currency,
      minimumFractionDigits: digits,
      maximumFractionDigits: digits,
    }).format(amount);
  }

  function number(value: number | null | undefined, opts: Intl.NumberFormatOptions = {}): string {
    if (value === null || value === undefined || !Number.isFinite(value)) return EMPTY_VALUE;
    return new Intl.NumberFormat(numberLocale, { maximumFractionDigits: 2, ...opts }).format(value);
  }

  /** Calendar date (YYYY-MM-DD) of an instant in the company zone. */
  function businessDate(value: DateInput = new Date()): string | null {
    const p = parseDateInput(value);
    if (!p || p.kind === 'wall') return null;
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone: zoneFor(p),
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    }).formatToParts(p.date);
    const get = (t: Intl.DateTimeFormatPartTypes) => parts.find((x) => x.type === t)?.value;
    const y = get('year');
    const m = get('month');
    const d = get('day');
    return y && m && d ? `${y}-${m}-${d}` : null;
  }

  return { language, locale, timeZone, date, time, dateTime, hijri, money, number, businessDate };
}

export type Formatter = ReturnType<typeof createFormatter>;

/**
 * A Date whose LOCAL fields equal the wall clock of `date` in `timeZone`. For handing an instant to
 * date-fns (which formats in device time) so it prints the company's clock instead.
 */
export function toZonedWallClock(date: Date, timeZone: string = DEFAULT_TIME_ZONE): Date {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: resolveTimeZone(timeZone),
    hourCycle: 'h23',
    year: 'numeric',
    month: 'numeric',
    day: 'numeric',
    hour: 'numeric',
    minute: 'numeric',
    second: 'numeric',
  }).formatToParts(date);
  const get = (t: Intl.DateTimeFormatPartTypes) => Number(parts.find((x) => x.type === t)?.value ?? 0);
  return new Date(get('year'), get('month') - 1, get('day'), get('hour') % 24, get('minute'), get('second'), date.getMilliseconds());
}
