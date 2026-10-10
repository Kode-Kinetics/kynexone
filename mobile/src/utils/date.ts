// ============================================================
// ZAYRA MOBILE — Date Utilities
// ============================================================

import { format, formatDistanceToNow, parseISO, isValid, type Locale } from 'date-fns';
import { ar as arLocale, enUS } from 'date-fns/locale';
import i18n from '@/config/i18n';
import {
  DEFAULT_TIME_ZONE,
  createFormatter,
  parseDateInput,
  toZonedWallClock,
} from './formatCore';

// Legacy helpers used by existing screens. They now print in the UI language and on the
// company clock (Asia/Riyadh) instead of the device's zone. New code: useFormat() (utils/format.ts).

export type DateFormat =
  | 'date'        // Dec 15, 2024
  | 'display'     // alias for date
  | 'short'       // 15/12/2024
  | 'time'        // 09:30 AM
  | 'datetime'    // Dec 15, 2024 09:30 AM
  | 'dateTime'    // alias for datetime
  | 'month-year'  // December 2024
  | 'monthYear'   // alias for month-year
  | 'month-short' // Dec 2024
  | 'relative'    // 2 hours ago
  | 'iso';        // 2024-12-15

function isArabic(): boolean {
  return Boolean(i18n.language?.startsWith('ar'));
}

function dateLocale(): Locale {
  return isArabic() ? arLocale : enUS;
}

/**
 * The value as a Date whose local fields show what the user should read:
 * instants on the company clock; calendar dates and wall times unchanged.
 */
function toDisplayDate(value: string | Date): Date | null {
  if (value instanceof Date) {
    if (!isValid(value)) return null;
    // A Date built at local midnight (date pickers, month grids) is a calendar date.
    const localMidnight = value.getHours() === 0 && value.getMinutes() === 0
      && value.getSeconds() === 0 && value.getMilliseconds() === 0;
    return localMidnight ? value : toZonedWallClock(value, DEFAULT_TIME_ZONE);
  }
  const parsed = parseDateInput(value);
  if (!parsed) return null;
  const d = parsed.date;
  if (parsed.kind === 'instant') return toZonedWallClock(d, DEFAULT_TIME_ZONE);
  // calendar / wall values are stored at UTC: rebuild them as local fields.
  return new Date(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate(), d.getUTCHours(), d.getUTCMinutes(), d.getUTCSeconds());
}

export function formatDate(
  dateStr: string | Date | undefined | null,
  fmt: DateFormat = 'date',
  useHijri = false
): string {
  if (!dateStr) return '—';

  if (fmt === 'relative') {
    const parsed = dateStr instanceof Date ? dateStr : parseDateInput(dateStr)?.date;
    if (!parsed || !isValid(parsed)) return '—';
    return formatDistanceToNow(parsed, { addSuffix: true, locale: dateLocale() });
  }

  if (useHijri && fmt !== 'time' && fmt !== 'iso') {
    const hijri = createFormatter({ language: isArabic() ? 'ar' : 'en' }).hijri(dateStr);
    if (hijri) return hijri;
    // Runtime without the Umm al-Qura calendar: fall through to Gregorian.
  }

  const date = toDisplayDate(dateStr);
  if (!date) return '—';
  const locale = dateLocale();

  switch (fmt) {
    case 'date':
    case 'display':
      return format(date, 'MMM d, yyyy', { locale });
    case 'short':
      return format(date, 'dd/MM/yyyy');
    case 'time':
      return format(date, 'hh:mm a', { locale });
    case 'datetime':
    case 'dateTime':
      return format(date, 'MMM d, yyyy hh:mm a', { locale });
    case 'month-year':
    case 'monthYear':
      return format(date, 'MMMM yyyy', { locale });
    case 'month-short':
      return format(date, 'MMM yyyy', { locale });
    case 'iso':
      return format(date, 'yyyy-MM-dd');
    default:
      return format(date, 'MMM d, yyyy', { locale });
  }
}

/** "09:30:00" (wall time, shown as written) or an ISO instant (shown on the company clock). */
export function formatTime(timeStr: string | undefined | null): string {
  if (!timeStr) return '—';
  const date = toDisplayDate(timeStr);
  if (!date) return timeStr;
  return format(date, 'hh:mm a', { locale: dateLocale() });
}

export function formatDuration(minutes: number): string {
  if (minutes < 60) return `${minutes}m`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m > 0 ? `${h}h ${m}m` : `${h}h`;
}

export function formatWorkHours(decimalHours: number): string {
  const h = Math.floor(decimalHours);
  const m = Math.round((decimalHours - h) * 60);
  return m > 0 ? `${h}h ${m}m` : `${h}h`;
}

export function getDayOfWeek(dateStr: string): string {
  const date = toDisplayDate(dateStr);
  return date ? format(date, 'EEE', { locale: dateLocale() }) : '—';
}

export function getMonthDays(year: number, month: number): Date[] {
  const days: Date[] = [];
  const d = new Date(year, month - 1, 1);
  while (d.getMonth() === month - 1) {
    days.push(new Date(d));
    d.setDate(d.getDate() + 1);
  }
  return days;
}

export function isWeekend(date: Date, weekendDays: number[] = [5, 6]): boolean {
  // Default: Friday (5) and Saturday (6) — GCC weekend
  return weekendDays.includes(date.getDay());
}

export function toISODate(date: Date): string {
  return format(date, 'yyyy-MM-dd');
}

export function toISODateTime(date: Date): string {
  return date.toISOString();
}

export function daysUntil(dateStr: string): number {
  const date = parseISO(dateStr);
  if (!isValid(date)) return 0;
  const now = new Date();
  const diff = date.getTime() - now.getTime();
  return Math.ceil(diff / (1000 * 60 * 60 * 24));
}

export { formatDistanceToNow } from 'date-fns';
