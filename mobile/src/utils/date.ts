// ============================================================
// ZAYRA MOBILE — Date Utilities
// ============================================================

import { format, formatDistanceToNow, parseISO, isValid } from 'date-fns';

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

export function formatDate(
  dateStr: string | Date | undefined | null,
  fmt: DateFormat = 'date',
  useHijri = false
): string {
  if (!dateStr) return '—';

  const date = typeof dateStr === 'string' ? parseISO(dateStr) : dateStr;
  if (!isValid(date)) return '—';

  // TODO: Integrate Hijri calendar library (e.g., @khawarizmus/hijri-moment)
  // For now, Gregorian display with a placeholder note
  if (useHijri) {
    // PLACEHOLDER: Hijri date formatting
    // return formatHijri(date, fmt);
    console.log('[TODO] Hijri date formatting not yet implemented');
  }

  switch (fmt) {
    case 'date':
      return format(date, 'MMM d, yyyy');
    case 'short':
      return format(date, 'dd/MM/yyyy');
    case 'time':
      return format(date, 'hh:mm a');
    case 'datetime':
      return format(date, 'MMM d, yyyy hh:mm a');
    case 'month-year':
      return format(date, 'MMMM yyyy');
    case 'month-short':
      return format(date, 'MMM yyyy');
    case 'relative':
      return formatDistanceToNow(date, { addSuffix: true });
    case 'iso':
      return format(date, 'yyyy-MM-dd');
    default:
      return format(date, 'MMM d, yyyy');
  }
}

export function formatTime(timeStr: string | undefined | null): string {
  if (!timeStr) return '—';
  // Handles "09:30:00" or ISO datetime
  const date = timeStr.includes('T') ? parseISO(timeStr) : parseISO(`1970-01-01T${timeStr}`);
  if (!isValid(date)) return timeStr;
  return format(date, 'hh:mm a');
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
  const date = parseISO(dateStr);
  return isValid(date) ? format(date, 'EEE') : '—';
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
