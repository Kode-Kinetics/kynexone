// ============================================================
// Manager team — pure model (lane B): today's buckets and who's out.
//
// One definition per KPI, used by BOTH Manager Home and the Team screen, so the numbers reconcile:
//   present      = clocked in today, INCLUDING late arrivals and half days
//   late         = the late arrivals inside `present` (a sub-count, not a separate bucket)
//   notIn        = no punch and no record yet today ("Not clocked in") — NEUTRAL, never "absent":
//                  the API does not tell a manager whether today is a scheduled working day for the
//                  report (/ess/team has no day classification and the tenant work week is not
//                  readable by a manager), so a missing punch is not evidence of absence
//   absent       = the processed daily record says Absent, on a day not known to be off
//   onLeave      = approved leave covering today
//   missingPunch = processed record with a missing punch
//   off          = public holiday / rest day (the record says so, or the day is a public holiday
//                  in the tenant's holiday calendar — then no-punch and absent both count as off)
//   other        = a status the app does not recognise (shown as the server wrote it)
// Every member falls in exactly one bucket, so the buckets add up to the team total.
//
// Import-free so tests/managerModel.test.ts runs it under node --test.
// ============================================================

/** A direct report with today's status — the API's own status words kept in `rawStatus`. */
export interface TeamMemberToday {
  employeeId: string;
  fullName: string;
  jobTitle: string;
  department?: string;
  profilePhotoUrl?: string;
  /** Legacy normalised status (kept for older callers); use rawStatus + teamBucket for counting. */
  todayStatus: 'PRESENT' | 'ABSENT' | 'LATE' | 'HALF_DAY' | 'HOLIDAY' | 'WEEKEND' | 'ON_LEAVE' | 'MISSING_PUNCH';
  clockIn?: string;
  clockOut?: string;
  hasPendingRequests?: boolean;
  rawStatus: string;
  currentlyActive: boolean;
}

export type TeamBucket = 'present' | 'notIn' | 'absent' | 'onLeave' | 'missingPunch' | 'off' | 'other';

export const TEAM_BUCKETS: readonly TeamBucket[] = ['present', 'notIn', 'onLeave', 'missingPunch', 'absent', 'off', 'other'];

function norm(status: string | null | undefined): string {
  return (status ?? '').trim().toUpperCase().replace(/[\s-]+/g, '_');
}

/** What is known about TODAY for the whole team (from real data only). */
export interface TeamDay {
  /** Today is a public holiday in the tenant's holiday calendar (GET /leave/holidays/range). */
  publicHoliday?: boolean;
  /** Today is a known rest day. No API gives a manager this today; kept for when one does. */
  restDay?: boolean;
}

export function teamBucket(rawStatus: string | null | undefined, day: TeamDay = {}): TeamBucket {
  const bucket = statusBucket(rawStatus);
  // Absence only counts on a scheduled working day.
  if ((day.publicHoliday || day.restDay) && (bucket === 'notIn' || bucket === 'absent')) return 'off';
  return bucket;
}

function statusBucket(rawStatus: string | null | undefined): TeamBucket {
  switch (norm(rawStatus)) {
    case 'PRESENT':
    case 'LATE':
    case 'HALF_DAY':
    case 'HALFDAY':
      return 'present';
    case '':
    case 'NOT_CLOCKED_IN':
    case 'NONE':
      return 'notIn';
    case 'ABSENT':
      return 'absent';
    case 'ON_LEAVE':
    case 'LEAVE':
    case 'ONLEAVE':
      return 'onLeave';
    case 'MISSING_PUNCH':
    case 'MISSINGPUNCH':
      return 'missingPunch';
    case 'HOLIDAY':
    case 'PUBLIC_HOLIDAY':
    case 'WEEKEND':
    case 'REST_DAY':
    case 'WEEK_OFF':
    case 'OFF':
      return 'off';
    default:
      return 'other';
  }
}

export function isLate(rawStatus: string | null | undefined): boolean {
  return norm(rawStatus) === 'LATE';
}

export type TeamCounts = Record<TeamBucket, number> & { total: number; late: number };

/** Status colour per bucket. Only a real exception is red; a missing punch with no schedule is neutral. */
export const BUCKET_TONE: Record<TeamBucket, 'success' | 'warning' | 'danger' | 'info' | 'neutral'> = {
  present: 'success',
  notIn: 'neutral',
  absent: 'danger',
  onLeave: 'info',
  missingPunch: 'warning',
  off: 'neutral',
  other: 'neutral',
};

export function teamTodayCounts(members: readonly { rawStatus?: string | null }[], day: TeamDay = {}): TeamCounts {
  const counts: TeamCounts = { total: members.length, late: 0, present: 0, notIn: 0, absent: 0, onLeave: 0, missingPunch: 0, off: 0, other: 0 };
  for (const m of members) {
    counts[teamBucket(m.rawStatus, day)] += 1;
    if (isLate(m.rawStatus)) counts.late += 1;
  }
  return counts;
}

// ------------------------------------------------------------------
// Who's out (GET /api/leave/calendar/team?fromDate&toDate)
// ------------------------------------------------------------------

export interface OutEntry {
  employeeId: number | null;
  employeeName: string;
  leaveTypeName: string | null;
  startDate: string;
  endDate: string;
  days: number | null;
  /** False for a request the calendar lists but that is not approved yet. */
  approved: boolean;
}

const ISO_DAY = /^\d{4}-\d{2}-\d{2}/;

/** The endpoint answers [{ departmentName, employees: [...] }]. */
export function flattenTeamCalendar(groups: unknown): OutEntry[] {
  if (!Array.isArray(groups)) return [];
  const out: OutEntry[] = [];
  for (const g of groups as { employees?: unknown }[]) {
    const employees = Array.isArray(g?.employees) ? (g.employees as Record<string, unknown>[]) : [];
    for (const e of employees) {
      const start = String(e?.startDate ?? '');
      const end = String(e?.endDate ?? '');
      if (!ISO_DAY.test(start) || !ISO_DAY.test(end)) continue;
      const days = Number(e?.days);
      out.push({
        employeeId: e?.employeeId === null || e?.employeeId === undefined ? null : Number(e.employeeId),
        employeeName: String(e?.employeeName ?? '').trim(),
        leaveTypeName: e?.leaveTypeName ? String(e.leaveTypeName) : null,
        startDate: start.slice(0, 10),
        endDate: end.slice(0, 10),
        days: Number.isFinite(days) ? days : null,
        approved: String(e?.status ?? '').toLowerCase() === 'approved',
      });
    }
  }
  return out.sort((a, b) => a.startDate.localeCompare(b.startDate) || a.employeeName.localeCompare(b.employeeName));
}

/** Calendar arithmetic on YYYY-MM-DD (UTC, no time-zone drift). */
export function addDays(isoDate: string, n: number): string {
  const [y, m, d] = isoDate.slice(0, 10).split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1, d + n));
  return t.toISOString().slice(0, 10);
}

export interface OutDay {
  date: string;
  out: OutEntry[];
}

/** One row per calendar day from `fromDate`, listing everyone whose leave covers that day. */
export function whosOutByDay(entries: readonly OutEntry[], fromDate: string, days: number): OutDay[] {
  const result: OutDay[] = [];
  for (let i = 0; i < days; i++) {
    const date = addDays(fromDate, i);
    result.push({ date, out: entries.filter((e) => e.startDate <= date && e.endDate >= date) });
  }
  return result;
}

/** Leave by others that overlaps [start, end] — the "who else is off" line on a leave decision. */
export function overlapping(entries: readonly OutEntry[], start: string, end: string, excludeEmployeeId: number | null): OutEntry[] {
  return entries.filter((e) => e.startDate <= end && e.endDate >= start
    && (excludeEmployeeId === null || e.employeeId !== excludeEmployeeId));
}

/** Distinct people out at any point in the window. */
export function peopleOut(entries: readonly OutEntry[]): number {
  return new Set(entries.map((e) => e.employeeId ?? e.employeeName)).size;
}

/** GET /leave/holidays/range rows → is `isoDate` a (non-optional) public holiday? Returns its names. */
export function holidayOn(rows: unknown, isoDate: string): { nameEn: string | null; nameAr: string | null } | null {
  if (!Array.isArray(rows)) return null;
  const hit = (rows as Record<string, unknown>[]).find((h) =>
    String(h?.date ?? '').slice(0, 10) === isoDate && h?.isOptional !== true);
  if (!hit) return null;
  const text = (v: unknown) => (typeof v === 'string' && v.trim() ? v.trim() : null);
  return { nameEn: text(hit.nameEn) ?? text(hit.name), nameAr: text(hit.nameAr) };
}

/** Whole calendar days from `fromIso` to `toIso` (YYYY-MM-DD, negative when `toIso` is earlier). */
export function daysBetween(fromIso: string, toIso: string): number | null {
  const parse = (s: string) => {
    const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s ?? '');
    return m ? Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : null;
  };
  const a = parse(fromIso);
  const b = parse(toIso);
  return a === null || b === null ? null : Math.round((b - a) / 86_400_000);
}
