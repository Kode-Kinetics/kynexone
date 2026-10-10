// ============================================================
// Employee Home — raw API rows → typed values. Pure (no react-native, no axios), so
// tests/homeMappers.test.ts drives it under node --test. Owned by lane A1.
//
// Every mapper keeps "unknown" as null/undefined. Nothing here invents a date, a count or a status.
// ============================================================

import type { AttendanceStatus } from '../types';

export interface Announcement {
  id: string;
  title: string;
  body: string;
  audience?: string;
  /** ISO instant (UTC). */
  publishedAt?: string;
}

export interface PublicHolidayItem {
  id: string;
  /** Calendar date YYYY-MM-DD. */
  date: string;
  nameEn: string;
  nameAr?: string;
  /** Server-stored Hijri date text, when HR entered one. */
  hijriDate?: string;
  isOptional: boolean;
  type?: string;
}

/** One processed attendance day (GET /attendance/daily). */
export interface DayRecord {
  date: string;
  status: AttendanceStatus;
  /** The raw server status, for evidence ("Rest day"). */
  rawStatus: string;
  missingPunch: boolean;
  correctionRequested: boolean;
  payrollLocked: boolean;
}

export type PendingSource = 'leave' | 'overtime' | 'hr';

/** Backend attendance statuses: Present, Late, Absent, Half day, On leave, Missing punch, Public holiday, Rest day, Weekend. */
export function mapAttendanceStatus(status?: string | null): AttendanceStatus | null {
  const value = (status ?? '').trim().toUpperCase().replace(/[\s-]+/g, '_');
  switch (value) {
    case 'PRESENT': return 'PRESENT';
    case 'LATE': return 'LATE';
    case 'ABSENT': return 'ABSENT';
    case 'LEAVE':
    case 'ON_LEAVE':
    case 'ONLEAVE': return 'ON_LEAVE';
    case 'HALF_DAY':
    case 'HALFDAY': return 'HALF_DAY';
    case 'HOLIDAY':
    case 'PUBLIC_HOLIDAY': return 'HOLIDAY';
    case 'WEEKEND':
    case 'REST_DAY':
    case 'WEEK_OFF': return 'WEEKEND';
    case 'MISSING_PUNCH':
    case 'MISSINGPUNCH': return 'MISSING_PUNCH';
    default: return null; // unknown is not "absent"
  }
}

function str(value: unknown): string {
  return typeof value === 'string' ? value : value == null ? '' : String(value);
}

export function mapAnnouncement(raw: any): Announcement | null {
  const id = str(raw?.id);
  const title = str(raw?.title).trim();
  if (!id || !title) return null;
  return {
    id,
    title,
    body: str(raw?.body).trim(),
    audience: raw?.audience ? str(raw.audience) : undefined,
    publishedAt: raw?.publishedAtUtc ? str(raw.publishedAtUtc) : undefined,
  };
}

export function mapAnnouncements(raw: unknown): Announcement[] {
  if (!Array.isArray(raw)) return [];
  return raw.map(mapAnnouncement).filter((a): a is Announcement => a !== null);
}

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;

export function mapHoliday(raw: any): PublicHolidayItem | null {
  const date = str(raw?.date).slice(0, 10);
  const nameEn = str(raw?.nameEn ?? raw?.name).trim();
  const nameAr = str(raw?.nameAr).trim();
  if (!DATE_ONLY.test(date) || (!nameEn && !nameAr)) return null;
  return {
    id: str(raw?.id) || date,
    date,
    nameEn: nameEn || nameAr,
    nameAr: nameAr || undefined,
    hijriDate: str(raw?.hijriDate).trim() || undefined,
    isOptional: Boolean(raw?.isOptional),
    type: raw?.holidayType ? str(raw.holidayType) : undefined,
  };
}

export function mapHolidays(raw: unknown): PublicHolidayItem[] {
  if (!Array.isArray(raw)) return [];
  return raw
    .map(mapHoliday)
    .filter((h): h is PublicHolidayItem => h !== null)
    .sort((a, b) => a.date.localeCompare(b.date));
}

export function mapDayRecord(raw: any): DayRecord | null {
  const date = str(raw?.workDate).slice(0, 10);
  if (!DATE_ONLY.test(date)) return null;
  const missingPunch = Boolean(raw?.missingPunch) && !!raw?.firstInUtc !== !!raw?.lastOutUtc;
  const status = missingPunch ? 'MISSING_PUNCH' : mapAttendanceStatus(raw?.status);
  if (!status) return null;
  const correction = str(raw?.manualCorrectionStatus);
  return {
    date,
    status,
    rawStatus: str(raw?.status),
    missingPunch,
    correctionRequested: !!correction && correction !== 'None',
    payrollLocked: Boolean(raw?.isPayrollLocked),
  };
}

/** Whether a request row is still waiting on someone (not decided, closed or withdrawn). */
export function isPendingStatus(source: PendingSource, status?: string | null): boolean {
  const v = str(status).replace(/[\s_-]/g, '').toLowerCase();
  if (!v) return false;
  if (source === 'hr') return v === 'open' || v === 'inprogress' || v === 'assigned' || v === 'pendingemployee' || v === 'new';
  if (/approved|reject|cancel|withdraw|draft|closed|declined/.test(v)) return false;
  return v.includes('pending') || v === 'submitted' || v === 'inreview';
}

/** Approved leave whose range covers `day` (YYYY-MM-DD), with its type name when the server gave one. */
export function approvedLeaveOn(rows: unknown, day: string): { leaveTypeName?: string } | null {
  if (!Array.isArray(rows)) return null;
  for (const row of rows as any[]) {
    if (!/^approved$/i.test(str(row?.status))) continue;
    const start = str(row?.startDate).slice(0, 10);
    const end = str(row?.endDate).slice(0, 10) || start;
    if (start && start <= day && end >= day) return { leaveTypeName: str(row?.leaveTypeName).trim() || undefined };
  }
  return null;
}

/** What the ESS dashboard carries beyond the shared EmployeeDashboard type. */
export interface EmployeeHomeExtras {
  announcements: Announcement[];
  /**
   * Status of today's attendance record — only when it was PROCESSED by HR's attendance run
   * (`attendanceTodaySource === 'processed'`). A record derived from raw punches is not a verdict.
   */
  todayProcessedStatus: AttendanceStatus | null;
  /** Minutes worked today, from the server's record (breaks deducted when processed). */
  todayWorkedMinutes: number | null;
}

export function mapDashboardExtras(data: any): EmployeeHomeExtras {
  const record = data?.attendanceToday ?? null;
  const source = str(data?.attendanceTodaySource).toLowerCase();
  const processed = record && source !== 'raw' ? mapAttendanceStatus(record.status) : null;
  const worked = Number(record?.totalWorkedMinutes);
  return {
    announcements: mapAnnouncements(data?.announcements),
    todayProcessedStatus: processed,
    todayWorkedMinutes: record && Number.isFinite(worked) && worked > 0 ? worked : null,
  };
}
