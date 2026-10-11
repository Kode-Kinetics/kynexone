// ============================================================
// Employee Home — the decisions behind what the screen shows. Pure (no react-native), so
// tests/employeeHomeModel.test.ts drives it under node --test. Owned by lane A1.
//
// Rules this file enforces:
// - "Absent" only when a processed attendance record says so. No punches is "not recorded yet".
// - A rest day is a rest day only on evidence (processed "Rest day" records for this weekday).
// - Every alert comes from a real record; nothing is padded to fill the list.
// ============================================================

import type { AttendanceStatus, LeaveBalance } from '../../types';
import type { DayRecord, PublicHolidayItem } from '../../api/homeMappers';

// ---- Names and greetings -----------------------------------------------------------------

// First-name particles that are not a name on their own: "Abdul Rahman", "عبد الله", "Abu Bakr".
const LEADING_COMPOUND = new Set(['abd', 'abdul', 'abdel', 'abdal', 'abdil', 'abu', 'abou', 'abo', 'عبد', 'أبو', 'ابو']);
const ARTICLE = new Set(['al', 'el', 'ul', 'ad', 'ud', 'ed', 'ar', 'as', 'an', 'at', 'ال']);
// Second parts that complete a given name: "Saif al-Din", "Nasr Allah", "Nour El Din".
const TRAILING_COMPOUND = /^(?:(?:al|el|ul|ad|ud|ed)-?)?(?:din|deen|dien|allah|ullah)$|^(?:الدين|الله)$/i;

/**
 * The name to greet someone by. A preferred name wins; otherwise the first given name, keeping
 * compound names whole ("Abdul Rahman", "عبد الله", "Saif al-Din") — never just "Abdul" or "عبد".
 */
export function displayFirstName(fullName?: string | null, preferredName?: string | null): string {
  const preferred = (preferredName ?? '').trim();
  if (preferred) return preferred;
  const tokens = (fullName ?? '').trim().split(/\s+/).filter(Boolean);
  if (tokens.length === 0) return '';
  const lower = (s: string | undefined) => (s ?? '').toLowerCase();
  const first = tokens[0];

  // "Abd al-Aziz" / "Abd Al Aziz" / "عبد العزيز" / "Abdul Rahman"
  if (LEADING_COMPOUND.has(lower(first).replace(/-$/, '')) && tokens.length > 1) {
    if (ARTICLE.has(lower(tokens[1])) && tokens.length > 2) return tokens.slice(0, 3).join(' ');
    return tokens.slice(0, 2).join(' ');
  }
  // "Abdul-Rahman" already joined.
  if (tokens.length > 1 && TRAILING_COMPOUND.test(tokens[1])) return tokens.slice(0, 2).join(' ');
  if (tokens.length > 2 && ARTICLE.has(lower(tokens[1])) && TRAILING_COMPOUND.test(tokens[2])) {
    return tokens.slice(0, 3).join(' ');
  }
  return first;
}

export type DayPeriod = 'morning' | 'afternoon' | 'evening';

/** Hour of day (0–23) of an instant on the company clock. */
export function hourInZone(now: Date, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-GB', { hour: 'numeric', hourCycle: 'h23', timeZone }).formatToParts(now);
  const hour = Number(parts.find((p) => p.type === 'hour')?.value ?? NaN);
  return Number.isFinite(hour) ? hour % 24 : now.getUTCHours();
}

/** Greeting period on the company clock (Riyadh by default), not the phone's. */
export function greetingPeriod(now: Date, timeZone: string): DayPeriod {
  const hour = hourInZone(now, timeZone);
  if (hour >= 4 && hour < 12) return 'morning';
  if (hour >= 12 && hour < 17) return 'afternoon';
  return 'evening';
}

// ---- Durations ---------------------------------------------------------------------------

export function splitMinutes(total: number): { h: number; m: number } {
  const safe = Math.max(0, Math.round(total));
  return { h: Math.floor(safe / 60), m: safe % 60 };
}

/** Whole minutes between two instants; null when either is missing or the order is wrong. */
export function minutesBetween(from?: string | null, to?: string | Date | null): number | null {
  if (!from || !to) return null;
  const a = instantMs(from);
  const b = to instanceof Date ? to.getTime() : instantMs(to);
  if (a === null || b === null || !Number.isFinite(b) || b < a) return null;
  return Math.floor((b - a) / 60_000);
}

/** ISO instant → epoch ms. Offset-less API timestamps are UTC (the *Utc fields). */
function instantMs(value: string): number | null {
  const s = value.trim();
  const iso = s.includes('T') && !/(Z|[+-]\d{2}:?\d{2})$/i.test(s) ? `${s}Z` : s;
  const ms = new Date(iso).getTime();
  return Number.isFinite(ms) ? ms : null;
}

// ---- Calendar helpers (YYYY-MM-DD strings, no time zone shifts) --------------------------

export function addDays(day: string, delta: number): string {
  const [y, m, d] = day.split('-').map(Number);
  const date = new Date(Date.UTC(y, m - 1, d + delta));
  return date.toISOString().slice(0, 10);
}

export function weekdayOf(day: string): number {
  const [y, m, d] = day.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d)).getUTCDay();
}

export function daysBetween(fromDay: string, toDay: string): number {
  const [y1, m1, d1] = fromDay.split('-').map(Number);
  const [y2, m2, d2] = toDay.split('-').map(Number);
  return Math.round((Date.UTC(y2, m2 - 1, d2) - Date.UTC(y1, m1 - 1, d1)) / 86_400_000);
}

// ---- Today's state -----------------------------------------------------------------------

export type DayKind =
  | 'onClock'      // clocked in, not out
  | 'done'         // clocked in and out
  | 'notStarted'   // a day with no punch yet and nothing that says it is off
  | 'restDay'
  | 'holiday'
  | 'onLeave'
  | 'absent'       // ONLY from a processed record
  | 'missingPunch';

export type DayTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

export interface DayState {
  kind: DayKind;
  tone: DayTone;
  /** Is a punch expected today? Off days still allow one, but quietly. */
  punchExpected: boolean;
  holidayName?: string;
  leaveTypeName?: string;
  /** The processed record's status, when there is one (e.g. LATE on a completed day). */
  recordStatus?: AttendanceStatus;
}

export interface DayInputs {
  clockIn?: string | null;
  clockOut?: string | null;
  currentlyActive: boolean;
  /** Status of today's PROCESSED daily record. Raw-punch-derived statuses do not count. */
  processedStatus?: AttendanceStatus | null;
  approvedLeaveToday?: { leaveTypeName?: string } | null;
  holidayToday?: { nameEn: string; nameAr?: string } | null;
  /** Evidence from recent processed days (see restDayFromHistory). */
  restDayByHistory?: boolean;
  language?: 'en' | 'ar';
}

/**
 * What kind of day today is, from evidence only. Priority:
 * punches → processed record → approved leave → public holiday → rest-day history → not started.
 */
export function classifyDay(input: DayInputs): DayState {
  const record = input.processedStatus ?? undefined;
  if (input.currentlyActive) return { kind: 'onClock', tone: 'success', punchExpected: true, recordStatus: record };
  if (input.clockIn && input.clockOut) {
    const tone: DayTone = record === 'LATE' || record === 'HALF_DAY' ? 'warning' : 'success';
    return { kind: 'done', tone, punchExpected: true, recordStatus: record };
  }
  const holidayName = input.holidayToday
    ? (input.language === 'ar' ? input.holidayToday.nameAr || input.holidayToday.nameEn : input.holidayToday.nameEn)
    : undefined;
  switch (record) {
    case 'ON_LEAVE':
      return { kind: 'onLeave', tone: 'info', punchExpected: false, leaveTypeName: input.approvedLeaveToday?.leaveTypeName, recordStatus: record };
    case 'HOLIDAY':
      return { kind: 'holiday', tone: 'info', punchExpected: false, holidayName, recordStatus: record };
    case 'WEEKEND':
      return { kind: 'restDay', tone: 'neutral', punchExpected: false, recordStatus: record };
    case 'ABSENT':
      return { kind: 'absent', tone: 'danger', punchExpected: true, recordStatus: record };
    case 'MISSING_PUNCH':
      return { kind: 'missingPunch', tone: 'danger', punchExpected: true, recordStatus: record };
    default:
      break;
  }
  if (input.approvedLeaveToday) {
    return { kind: 'onLeave', tone: 'info', punchExpected: false, leaveTypeName: input.approvedLeaveToday.leaveTypeName };
  }
  if (holidayName) return { kind: 'holiday', tone: 'info', punchExpected: false, holidayName };
  if (input.restDayByHistory) return { kind: 'restDay', tone: 'neutral', punchExpected: false };
  return { kind: 'notStarted', tone: 'neutral', punchExpected: true };
}

/**
 * True when this weekday was a processed "Rest day" in the recent records (at least once) and was
 * never worked, absent or anything else there. The employee's work week is not exposed to ESS, so this reads
 * it from their own processed days rather than assuming Friday/Saturday.
 */
export function restDayFromHistory(today: string, records: readonly DayRecord[], lookbackDays = 28): boolean {
  const weekday = weekdayOf(today);
  const from = addDays(today, -lookbackDays);
  const same = records.filter((r) => r.date < today && r.date >= from && weekdayOf(r.date) === weekday);
  if (same.length === 0) return false;
  return same.every((r) => r.status === 'WEEKEND');
}

// ---- KPIs --------------------------------------------------------------------------------

/** The annual leave balance, if the employee has one. Never a guessed "first" type. */
export function pickAnnualBalance(balances: readonly LeaveBalance[] | null | undefined): LeaveBalance | null {
  if (!balances?.length) return null;
  return balances.find((b) => /annual|سنوي/i.test(`${b.leaveTypeName} ${b.leaveTypeNameAr ?? ''}`)) ?? null;
}

export interface PendingPart {
  source: 'leave' | 'overtime' | 'hr';
  /** null when that list could not be loaded. */
  count: number | null;
}

export interface PendingSummary {
  /** null when nothing loaded at all. */
  total: number | null;
  partial: boolean;
  parts: PendingPart[];
}

export function summarizePending(parts: readonly PendingPart[]): PendingSummary {
  const loaded = parts.filter((p) => p.count !== null);
  if (loaded.length === 0) return { total: null, partial: parts.length > 0, parts: [...parts] };
  return {
    total: loaded.reduce((sum, p) => sum + (p.count ?? 0), 0),
    partial: loaded.length < parts.length,
    parts: [...parts],
  };
}

// ---- "Needs you" -------------------------------------------------------------------------

export type DocumentKind =
  | 'iqama' | 'passport' | 'nationalId' | 'visa' | 'workPermit' | 'contract' | 'drivingLicense' | 'medical' | 'document';

/** Known document types get a translated name; anything else keeps the server's own label. */
export function documentKind(type?: string | null): DocumentKind {
  const v = (type ?? '').toLowerCase();
  if (/iqama|residen|إقامة|اقامة/.test(v)) return 'iqama';
  if (/passport|جواز/.test(v)) return 'passport';
  if (/national\s*id|هوية/.test(v)) return 'nationalId';
  if (/work\s*permit|رخصة عمل/.test(v)) return 'workPermit';
  if (/visa|تأشيرة/.test(v)) return 'visa';
  if (/contract|عقد/.test(v)) return 'contract';
  if (/driv|قيادة/.test(v)) return 'drivingLicense';
  if (/medical|insurance|تأمين/.test(v)) return 'medical';
  return 'document';
}

export type NeedsItem =
  | { kind: 'documentExpired'; key: string; documentType: string; docKind: DocumentKind; expiryDate: string; daysLeft: number }
  | { kind: 'documentExpiring'; key: string; documentType: string; docKind: DocumentKind; expiryDate: string; daysLeft: number }
  | { kind: 'missingPunch'; key: string; date: string }
  | { kind: 'payslip'; key: string; payslipId: string; year: number; month: number; periodLabel: string };

export interface NeedsInputs {
  today: string;
  documents?: readonly { id: string; documentType: string; expiryDate?: string | null }[];
  dayRecords?: readonly DayRecord[];
  latestPayslip?: { id: string; year: number; month: number; periodLabel: string } | null;
  seenPayslipId?: string | null;
  /** How far ahead an expiry is worth raising (the dashboard already limits alerts to 60 days). */
  documentWindowDays?: number;
  limit?: number;
}

/**
 * Up to `limit` (3) things the employee should act on, most urgent first:
 * expired documents → missing punches (newest first) → documents expiring soon → a new payslip.
 */
export function buildNeedsYou(input: NeedsInputs): NeedsItem[] {
  const limit = input.limit ?? 3;
  const window = input.documentWindowDays ?? 60;
  const expired: NeedsItem[] = [];
  const expiring: NeedsItem[] = [];
  for (const doc of input.documents ?? []) {
    const expiry = (doc.expiryDate ?? '').slice(0, 10);
    if (!/^\d{4}-\d{2}-\d{2}$/.test(expiry)) continue;
    const daysLeft = daysBetween(input.today, expiry);
    const base = { key: `doc:${doc.id}`, documentType: doc.documentType, docKind: documentKind(doc.documentType), expiryDate: expiry, daysLeft };
    if (daysLeft < 0) expired.push({ kind: 'documentExpired', ...base });
    else if (daysLeft <= window) expiring.push({ kind: 'documentExpiring', ...base });
  }
  expired.sort((a, b) => ('daysLeft' in a && 'daysLeft' in b ? a.daysLeft - b.daysLeft : 0));
  expiring.sort((a, b) => ('daysLeft' in a && 'daysLeft' in b ? a.daysLeft - b.daysLeft : 0));

  const missing: NeedsItem[] = (input.dayRecords ?? [])
    .filter((r) => r.status === 'MISSING_PUNCH' && r.date < input.today && !r.correctionRequested && !r.payrollLocked)
    .sort((a, b) => b.date.localeCompare(a.date))
    .map((r) => ({ kind: 'missingPunch' as const, key: `punch:${r.date}`, date: r.date }));

  const payslip: NeedsItem[] = [];
  const p = input.latestPayslip;
  if (p && p.id && p.id !== input.seenPayslipId && isRecentPeriod(input.today, p.year, p.month)) {
    payslip.push({ kind: 'payslip', key: `payslip:${p.id}`, payslipId: p.id, year: p.year, month: p.month, periodLabel: p.periodLabel });
  }

  return [...expired, ...missing, ...expiring, ...payslip].slice(0, limit);
}

/** A payslip is "new" only for this month or last month — an old slip is not news. */
export function isRecentPeriod(today: string, year: number, month: number): boolean {
  const [ty, tm] = today.split('-').map(Number);
  const diff = (ty * 12 + tm) - (year * 12 + month);
  return diff >= 0 && diff <= 1;
}

// ---- Holidays ----------------------------------------------------------------------------

/** The next non-optional public holiday on or after today. */
export function nextHoliday(holidays: readonly PublicHolidayItem[], today: string): PublicHolidayItem | null {
  return [...holidays]
    .filter((h) => !h.isOptional && h.date >= today)
    .sort((a, b) => a.date.localeCompare(b.date))[0] ?? null;
}

export function holidayOn(holidays: readonly PublicHolidayItem[], day: string): PublicHolidayItem | null {
  return holidays.find((h) => !h.isOptional && h.date === day) ?? null;
}
