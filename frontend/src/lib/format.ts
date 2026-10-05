/**
 * One formatter for every number, amount and date the tenant UI shows.
 *
 * WHY. Formatting was done at ~400 call sites, each choosing its own locale: 'en-US' here,
 * 'en-GB' there, `toLocaleString()` with no argument (the VIEWER's browser locale) elsewhere.
 * An Arabic user saw English month names; a tenant that chose DD/MM/YYYY saw MM/DD/YYYY; a
 * Hijri tenant saw only Gregorian dates outside the dashboard header.
 *
 * Decisions this module encodes (CTO, 2026-10-05):
 *   - Latin digits everywhere, including Arabic (`-u-nu-latn`): amounts, IDs and dates are
 *     copied into bank files, Qiwa and Mudad, which all use Latin digits.
 *   - Gregorian is explicit (`-u-ca-gregory`): some browsers default `ar-SA` to the Umm al-Qura
 *     calendar, which would silently turn every date Hijri.
 *   - Hijri, when the tenant asks for it, is islamic-umalqura (the Saudi civil calendar).
 *   - An amount is labelled with a currency only when one is supplied. There is no default
 *     currency: a guessed label is worse than a bare number. The label is the currency's symbol
 *     in the viewer's language: "$1,250.00" and "SAR 1,250.00" in English (en-US has no symbol
 *     for SAR, so it prints the code), "1,250.00 ر.س." in Arabic.
 *   - A bare "YYYY-MM-DD" is a calendar date, not an instant. It is read as that day in UTC and
 *     formatted in UTC, so it shows the same day for every viewer and every tenant zone. Only
 *     real instants are converted to the tenant's zone.
 *   - Percent uses '%' in every language (Arabic would print '٪'), matching the Latin digits.
 *   - Copyable values: Intl wraps Arabic amounts and numeric dates in invisible direction marks
 *     (U+200E, U+200F, U+061C). They keep a negative amount readable on screen, but they ride
 *     along when someone copies an amount, ID or IBAN into a bank file or a spreadsheet.
 *     `f.plain.*` returns the same text without them; use it for table cells, IDs, IBANs, export
 *     previews and anything else people copy. Display text (tiles, sentences) keeps the marks.
 *
 * Pure: no React. `useFormat()` (hooks/useFormat.ts) binds it to the viewer's language and the
 * tenant's localization settings. The i18n ratchet (e2e/i18n-coverage.spec.ts) counts the
 * remaining direct `toLocale*` / 'en-US' / 'en-GB' call sites per file; they may only go down.
 */

export type AppLocale = 'en' | 'ar' | 'fr' | 'es';

const BASE: Record<AppLocale, string> = { en: 'en-GB', ar: 'ar-SA', fr: 'fr-FR', es: 'es-ES' };

function asAppLocale(locale: string | null | undefined): AppLocale {
  const two = (locale ?? 'en').slice(0, 2).toLowerCase();
  return (two in BASE ? two : 'en') as AppLocale;
}

/** BCP-47 tag for numbers: Latin digits in every language. English uses en-US so compact
 *  notation reads "1.2M" (en-GB prints "1.2m"). */
export function numberLocale(locale: string): string {
  const l = asAppLocale(locale);
  return l === 'en' ? 'en-US' : `${BASE[l]}-u-nu-latn`;
}

/** BCP-47 tag for dates: explicit calendar, Latin digits. */
export function dateLocale(locale: string, calendar: 'gregory' | 'islamic-umalqura' = 'gregory'): string {
  return `${BASE[asAppLocale(locale)]}-u-ca-${calendar}-nu-latn`;
}

export interface FormatSettings {
  /** The UI language ('en' | 'ar' | ...). */
  locale: string;
  /** IANA zone; empty/undefined renders in the viewer's own zone (see TenantSettingsContext). */
  timeZone?: string | null;
  /** Tenant numeric date pattern: 'DD/MM/YYYY' | 'MM/DD/YYYY' | 'YYYY-MM-DD'. */
  dateFormat?: string | null;
  /** 'Gregorian' | 'Hijri' — which calendar leads. */
  calendarSystem?: string | null;
  /** Also show the other calendar in brackets where `dateBoth` is used. */
  hijriDatesEnabled?: boolean | null;
  /** Currency used by `money()` when the call site does not pass one. Leave unset to show bare amounts. */
  currency?: string | null;
}

export type DateInput = Date | string | number | null | undefined;
export type DateStyle = 'short' | 'medium' | 'long' | 'full' | 'dayMonth' | 'monthYear' | 'weekdayShort' | 'weekdayDate';

/** Invisible direction marks Intl puts into Arabic output: LRM, RLM and the Arabic letter mark. */
const BIDI_MARKS = /[\u200E\u200F\u061C]/g;

/** `s` without direction marks — for anything a person copies or exports. */
export function stripBidiMarks(s: string): string {
  return s.replace(BIDI_MARKS, '');
}

interface Parsed { date: Date; dateOnly: boolean }

const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;

function toDate(d: DateInput): Parsed | null {
  if (d == null || d === '') return null;
  // A bare "YYYY-MM-DD" is a calendar date, not an instant. Read it as that day at UTC midnight and
  // format it in UTC (see `dtf`), so the viewer's own zone and the tenant's zone cannot move it.
  // Parsing it as local midnight instead put it on the previous day for any viewer east of the
  // tenant's zone (Karachi or Tokyo viewing a Riyadh tenant saw 4 Oct for 2026-10-05).
  if (typeof d === 'string') {
    const m = DATE_ONLY.exec(d);
    if (m) {
      const date = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
      return Number.isNaN(date.getTime()) ? null : { date, dateOnly: true };
    }
  }
  const date = d instanceof Date ? d : new Date(d);
  return Number.isNaN(date.getTime()) ? null : { date, dateOnly: false };
}

/** Zones already reported as invalid, so a bad tenant setting warns once, not on every render. */
const warnedZones = new Set<string>();

function validZone(tz: string | undefined): string | undefined {
  if (!tz) return undefined;
  try {
    new Intl.DateTimeFormat('en-US', { timeZone: tz });
    return tz;
  } catch {
    if (!warnedZones.has(tz)) {
      warnedZones.add(tz);
      console.warn(`[format] The tenant time zone "${tz}" is not a valid IANA zone; showing times in the viewer's own zone instead. Fix it in Tenant Admin → Localization.`);
    }
    return undefined;
  }
}

const MONTHS = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];

export function createFormatter(settings: FormatSettings) {
  const locale = asAppLocale(settings.locale);
  const nLocale = numberLocale(locale);
  const tz = validZone(settings.timeZone?.trim() || undefined);
  const hijriFirst = (settings.calendarSystem ?? '').toLowerCase() === 'hijri';
  const primaryCal = hijriFirst ? 'islamic-umalqura' : 'gregory';
  const defaultCurrency = settings.currency?.trim() || null;

  const nf = (opts: Intl.NumberFormatOptions) => {
    try { return new Intl.NumberFormat(nLocale, opts); } catch { return new Intl.NumberFormat('en-US', opts); }
  };
  /** `dateOnly`: a calendar date (see `toDate`), always formatted in UTC; otherwise the tenant's zone. */
  const dtf = (opts: Intl.DateTimeFormatOptions, cal: 'gregory' | 'islamic-umalqura' = primaryCal, dateOnly = false) =>
    new Intl.DateTimeFormat(dateLocale(locale, cal), { ...opts, timeZone: dateOnly ? 'UTC' : tz });

  function number(n: number | null | undefined, opts: Intl.NumberFormatOptions = {}): string {
    if (n == null || Number.isNaN(n)) return '—';
    return nf({ maximumFractionDigits: 2, ...opts }).format(n);
  }

  function integer(n: number | null | undefined): string {
    return number(n == null ? n : Math.round(n), { maximumFractionDigits: 0 });
  }

  /** `value` is a 0–100 percentage (the API's convention), not a ratio. */
  function percent(value: number | null | undefined, digits = 0): string {
    if (value == null || Number.isNaN(value)) return '—';
    // '%' everywhere: Arabic would print '٪' (U+066A), which no bank file or spreadsheet reads.
    return nf({ style: 'percent', minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value / 100).replace(/\u066A/g, '%');
  }

  /**
   * An amount, labelled with `currency` (or the formatter's default) when one is known, by the
   * currency's symbol in the viewer's language: "$1,250.00" / "SAR 1,250.00" / "1,250.00 ر.س.".
   */
  function money(n: number | null | undefined, currency?: string | null, opts: { decimals?: number; compact?: boolean } = {}): string {
    if (n == null || Number.isNaN(n)) return '—';
    const code = currency === undefined ? defaultCurrency : currency?.trim() || null;
    const fixed = (d: number) => ({ minimumFractionDigits: d, maximumFractionDigits: d });
    // Compact keeps the dashboard's rounding: 1.25M, 12.3K, and whole units under a thousand.
    const digits = opts.compact
      ? fixed(Math.abs(n) >= 1_000_000 ? 2 : Math.abs(n) >= 1_000 ? 1 : 0)
      : fixed(opts.decimals ?? 2);
    const compact: Intl.NumberFormatOptions = opts.compact && Math.abs(n) >= 1_000 ? { notation: 'compact' } : {};
    if (!code) return nf({ ...digits, ...compact }).format(n);
    try {
      return nf({ style: 'currency', currency: code, currencyDisplay: 'symbol', ...digits, ...compact }).format(n);
    } catch {
      // Not an ISO 4217 code: label it by hand rather than drop it.
      return `${code} ${nf({ ...digits, ...compact }).format(n)}`;
    }
  }

  /** Compact amount for tiles and charts: "SAR 1.25M", "SAR 12.3K". */
  function moneyCompact(n: number | null | undefined, currency?: string | null): string {
    return money(n, currency, { compact: true });
  }

  function numericDate(d: Date, pattern: string, cal: 'gregory' | 'islamic-umalqura', dateOnly: boolean): string {
    const parts = dtf({ day: '2-digit', month: '2-digit', year: 'numeric' }, cal, dateOnly).formatToParts(d);
    const get = (type: string) => parts.find((p) => p.type === type)?.value ?? '';
    return pattern.replace(/YYYY/g, get('year')).replace(/DD/g, get('day')).replace(/MM/g, get('month'));
  }

  function dateIn(d: DateInput, style: DateStyle, cal: 'gregory' | 'islamic-umalqura'): string {
    const parsed = toDate(d);
    if (!parsed) return '—';
    const { date, dateOnly } = parsed;
    const fmt = (opts: Intl.DateTimeFormatOptions) => dtf(opts, cal, dateOnly).format(date);
    const pattern = settings.dateFormat?.trim();
    switch (style) {
      case 'short':
        if (pattern && /DD|MM|YYYY/.test(pattern) && cal === 'gregory') return numericDate(date, pattern, cal, dateOnly);
        return fmt({ day: '2-digit', month: '2-digit', year: 'numeric' });
      case 'dayMonth': return fmt({ day: 'numeric', month: 'short' });
      case 'monthYear': return fmt({ month: 'long', year: 'numeric' });
      case 'weekdayShort': return fmt({ weekday: 'short' });
      case 'weekdayDate': return fmt({ weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
      case 'long': return fmt({ day: 'numeric', month: 'long', year: 'numeric' });
      case 'full': return fmt({ weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
      case 'medium':
      default: return fmt({ day: 'numeric', month: 'short', year: 'numeric' });
    }
  }

  /** A date in the tenant's leading calendar. `short` honours the tenant's numeric pattern. */
  function date(d: DateInput, style: DateStyle = 'medium'): string {
    return dateIn(d, style, primaryCal);
  }

  /** A date in the Umm al-Qura (Saudi civil) calendar, whatever the tenant leads with. */
  function hijri(d: DateInput, style: DateStyle = 'long'): string {
    return dateIn(d, style, 'islamic-umalqura');
  }

  /** A date in the Gregorian calendar, whatever the tenant leads with. */
  function gregorian(d: DateInput, style: DateStyle = 'medium'): string {
    return dateIn(d, style, 'gregory');
  }

  /** Leading calendar, plus the other one in brackets when the tenant enabled dual dates. */
  function dateBoth(d: DateInput, style: DateStyle = 'medium'): string {
    const first = date(d, style);
    if (!settings.hijriDatesEnabled || first === '—') return first;
    const second = hijriFirst ? gregorian(d, style) : hijri(d, style);
    return `${first} (${second})`;
  }

  /** Clock time, 24-hour, in the tenant's zone. */
  function time(d: DateInput): string {
    const parsed = toDate(d);
    if (!parsed) return '—';
    return dtf({ hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }, 'gregory').format(parsed.date);
  }

  function dateTime(d: DateInput, style: DateStyle = 'medium'): string {
    const parsed = toDate(d);
    if (!parsed) return '—';
    // A calendar date has no time of day to show.
    if (parsed.dateOnly) return dateIn(d, style, primaryCal);
    return `${dateIn(parsed.date, style, primaryCal)}, ${time(parsed.date)}`;
  }

  /**
   * The zone `time()` and `dateTime()` render in, for labelling a timestamp: the tenant's IANA
   * zone, or the viewer's own when the tenant has none (or an invalid one).
   */
  function zone(): string {
    return tz ?? (Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC');
  }

  /** "3 days ago" / "قبل 3 أيام" — for feeds; falls back to a date after a week. */
  function relative(d: DateInput, now: number = Date.now()): string {
    const parsed = toDate(d);
    if (!parsed) return '—';
    const date = parsed.date;
    const mins = Math.round((date.getTime() - now) / 60_000);
    const rtf = (() => { try { return new Intl.RelativeTimeFormat(nLocale, { numeric: 'auto' }); } catch { return new Intl.RelativeTimeFormat('en', { numeric: 'auto' }); } })();
    const abs = Math.abs(mins);
    if (abs < 1) return rtf.format(0, 'minute');
    if (abs < 60) return rtf.format(mins, 'minute');
    if (abs < 24 * 60) return rtf.format(Math.round(mins / 60), 'hour');
    if (abs < 7 * 24 * 60) return rtf.format(Math.round(mins / (24 * 60)), 'day');
    return date.getFullYear() === new Date(now).getFullYear() ? dateIn(d, 'dayMonth', primaryCal) : dateIn(d, 'medium', primaryCal);
  }

  /**
   * The API's invariant period labels ("Sep 2026", or just "Sep") in the viewer's language:
   * "September 2026" / "سبتمبر 2026". Unrecognised labels are returned unchanged.
   */
  function period(label: string | null | undefined, style: 'long' | 'short' = 'long'): string {
    if (!label) return '';
    const m = /^([A-Za-z]{3})[a-z]*\.?(?:\s+(\d{4}))?$/.exec(label.trim());
    const idx = m ? MONTHS.indexOf(m[1].toLowerCase()) : -1;
    if (!m || idx < 0) return label;
    const year = m[2] ? Number(m[2]) : 2000;
    const d = new Date(Date.UTC(year, idx, 15));
    const opts: Intl.DateTimeFormatOptions = { month: style, timeZone: 'UTC', ...(m[2] ? { year: 'numeric' } : {}) };
    // English month names come from en-US: en-GB abbreviates September as "Sept", which would
    // change every chart axis the API already labels "Sep".
    const tag = locale === 'en' ? 'en-US-u-ca-gregory-nu-latn' : dateLocale(locale, 'gregory');
    try { return new Intl.DateTimeFormat(tag, opts).format(d); } catch { return label; }
  }

  const display = { number, integer, percent, money, moneyCompact, date, hijri, gregorian, dateBoth, time, dateTime };
  type Display = typeof display;
  /** The same formatters without direction marks: for table cells, IDs, IBANs, exports — anything copied. */
  const plain = Object.fromEntries(
    Object.entries(display).map(([name, fn]) => [name, (...args: unknown[]) => stripBidiMarks((fn as (...a: unknown[]) => string)(...args))]),
  ) as Display;

  return {
    locale,
    numberLocale: nLocale,
    currency: defaultCurrency,
    ...display,
    relative, period, zone,
    plain,
  };
}

export type Formatter = ReturnType<typeof createFormatter>;
