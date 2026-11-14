/** Pure parts of the month and date pickers (components/ui/DateParts.tsx). Values are "YYYY-MM-DD" or "YYYY-MM"; blank is ''. */
export interface DateParts { year: number | null; month: number | null; day: number | null }

export function splitDate(value: string): DateParts {
  const m = /^(\d{4})-(\d{2})(?:-(\d{2}))?$/.exec(value ?? '');
  if (!m) return { year: null, month: null, day: null };
  return { year: Number(m[1]), month: Number(m[2]), day: m[3] ? Number(m[3]) : null };
}

export function daysInMonth(year: number, month: number): number {
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

export function joinDate(parts: DateParts, kind: 'date' | 'month' = 'date'): string {
  if (parts.year == null || parts.month == null) return '';
  const ym = `${parts.year}-${String(parts.month).padStart(2, '0')}`;
  if (kind === 'month') return ym;
  if (parts.day == null) return '';
  const day = Math.min(parts.day, daysInMonth(parts.year, parts.month));
  return `${ym}-${String(day).padStart(2, '0')}`;
}

/** Twelve month names, Gregorian, in the language (the numbering stays Latin elsewhere). */
export function monthNames(locale: string): string[] {
  const tag = locale === 'ar' ? 'ar-SA-u-ca-gregory-nu-latn' : `${locale || 'en'}-u-ca-gregory`;
  const f = new Intl.DateTimeFormat(tag, { month: 'long', timeZone: 'UTC' });
  return Array.from({ length: 12 }, (_, i) => f.format(new Date(Date.UTC(2026, i, 1))));
}

export function yearChoices(center: number, span = 5): number[] {
  return Array.from({ length: span * 2 + 1 }, (_, i) => center - span + i);
}
