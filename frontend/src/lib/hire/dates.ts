import { dateLocale } from '../format';

export const DEFAULT_ZONE = 'Asia/Riyadh';

function zone(tz?: string | null): string {
  const z = tz?.trim();
  if (!z) return DEFAULT_ZONE;
  try { new Intl.DateTimeFormat('en', { timeZone: z }); return z; } catch { return DEFAULT_ZONE; }
}

/** The calendar date (YYYY-MM-DD) it is right now in the company's zone. Never a UTC slice. */
export function todayInZone(tz?: string | null, now: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat(dateLocale('en'), { timeZone: zone(tz), year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(now);
  const get = (t: string) => parts.find(p => p.type === t)?.value ?? '';
  return `${get('year')}-${get('month')}-${get('day')}`;
}

export function isRealDate(iso: string): boolean {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso ?? '');
  if (!m) return false;
  const d = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return d.getUTCFullYear() === Number(m[1]) && d.getUTCMonth() === Number(m[2]) - 1 && d.getUTCDate() === Number(m[3]);
}

const pad = (n: number) => String(n).padStart(2, '0');

export function addYears(iso: string, years: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const target = new Date(Date.UTC(y + years, m - 1, 1));
  const last = new Date(Date.UTC(y + years, m, 0)).getUTCDate();
  return `${target.getUTCFullYear()}-${pad(m)}-${pad(Math.min(d, last))}`;
}

export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1, d + days));
  return `${t.getUTCFullYear()}-${pad(t.getUTCMonth() + 1)}-${pad(t.getUTCDate())}`;
}

/** Hard integrity window for the start date (CD12, CD14): today-50y .. today+2y. */
export function startDateRange(today: string): { min: string; max: string } {
  return { min: addYears(today, -50), max: addYears(today, 2) };
}

export function ageOn(dob: string, on: string): number {
  const [by, bm, bd] = dob.split('-').map(Number);
  const [y, m, d] = on.split('-').map(Number);
  return y - by - (m < bm || (m === bm && d < bd) ? 1 : 0);
}

/** Umm al-Qura date for a calendar date, e.g. "3 Jumada I 1448 AH". Date-only values are read as that day, in every zone. */
export function hijriLine(iso: string, lang: string): string {
  if (!isRealDate(iso)) return '';
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(Date.UTC(y, m - 1, d, 12));
  const text = new Intl.DateTimeFormat(dateLocale(lang, 'islamic-umalqura'), { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' }).format(date);
  return lang === 'ar' ? text.replace(/\s*هـ$/u, 'هـ') : text;
}
