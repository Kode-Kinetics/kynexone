// ============================================================
// Leave request logic — pure, unit-tested (tests/leavePay.test.ts). No react / react-native imports.
//
// Dates here are calendar dates as 'YYYY-MM-DD' strings. They are never shifted through a time
// zone: a leave day is a day on the company calendar, not an instant.
// ============================================================

export type LeaveDayMode = 'full' | 'half';

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

function isoToUtcMs(iso: string): number | null {
  const m = ISO_DATE.exec(iso);
  if (!m) return null;
  const ms = Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
  return Number.isNaN(ms) ? null : ms;
}

export function isIsoDate(value: string | null | undefined): value is string {
  return typeof value === 'string' && isoToUtcMs(value) !== null;
}

/** 'YYYY-MM-DD' plus n calendar days. */
export function addIsoDays(iso: string, days: number): string {
  const ms = isoToUtcMs(iso);
  if (ms === null) return iso;
  return new Date(ms + days * 86_400_000).toISOString().slice(0, 10);
}

/**
 * The range a request will actually send. A half day is ALWAYS one date: the backend refuses a
 * half day over several dates, and the old screen sent exactly that when Half day was switched on
 * after picking a range (the end date was never reset). An end before the start snaps to the start.
 */
export function normalizeLeaveRange(input: { start: string; end: string; mode: LeaveDayMode }): {
  start: string;
  end: string;
} {
  const { start, mode } = input;
  if (mode === 'half') return { start, end: start };
  const s = isoToUtcMs(start);
  const e = isoToUtcMs(input.end);
  if (s === null || e === null || e < s) return { start, end: start };
  return { start, end: input.end };
}

/** Calendar days from start to end, both included. 0 for an invalid range. */
export function calendarDaysInclusive(start: string, end: string): number {
  const s = isoToUtcMs(start);
  const e = isoToUtcMs(end);
  if (s === null || e === null || e < s) return 0;
  return Math.round((e - s) / 86_400_000) + 1;
}

/**
 * What the app can honestly say about the length of a request before it is sent.
 * There is no server-side preview: the server counts WORKING days (rest days and public holidays
 * excluded) at submit time. So a full-day range is described as calendar days, never as working days.
 */
export function describeRequestedLength(input: { start: string; end: string; mode: LeaveDayMode }):
  | { basis: 'halfDay'; days: 0.5 }
  | { basis: 'calendar'; days: number } {
  const range = normalizeLeaveRange(input);
  if (input.mode === 'half') return { basis: 'halfDay', days: 0.5 };
  return { basis: 'calendar', days: calendarDaysInclusive(range.start, range.end) };
}

/**
 * Balance hint from calendar days. Working days are never MORE than calendar days, so a range that
 * fits in calendar days certainly fits. A range that does not might still fit once rest days and
 * holidays are excluded — that is 'mayExceed' (an informational flag), never a hard "over balance".
 */
export function balanceHint(requestedDays: number, available: number | null | undefined): 'within' | 'mayExceed' | 'unknown' {
  if (available === null || available === undefined || !Number.isFinite(available)) return 'unknown';
  if (requestedDays <= 0) return 'unknown';
  return requestedDays <= available ? 'within' : 'mayExceed';
}

// ---- Status -------------------------------------------------------------------------------

export type LeaveStatusKind =
  | 'pending'
  | 'approved'
  | 'rejected'
  | 'cancelled'
  | 'withdrawn'
  | 'cancellationPending'
  | 'draft'
  | 'unknown';

/**
 * Backend LeaveRequest.Status values: Draft, Submitted, PendingManagerApproval, PendingHRApproval,
 * Approved, Rejected, Cancelled, Withdrawn, CancellationRequested.
 */
export function leaveStatusKind(raw: string | null | undefined): LeaveStatusKind {
  const v = (raw ?? '').replace(/[\s_-]/g, '').toLowerCase();
  if (!v) return 'unknown';
  if (v === 'cancellationrequested') return 'cancellationPending';
  if (v === 'approved') return 'approved';
  if (v === 'rejected') return 'rejected';
  if (v === 'cancelled' || v === 'canceled') return 'cancelled';
  if (v === 'withdrawn') return 'withdrawn';
  if (v === 'draft') return 'draft';
  if (v === 'submitted' || v.startsWith('pending')) return 'pending';
  return 'unknown';
}

export type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

export function leaveStatusTone(kind: LeaveStatusKind): StatusTone {
  switch (kind) {
    case 'approved':
      return 'success';
    case 'pending':
    case 'cancellationPending':
      return 'warning';
    case 'rejected':
      return 'danger';
    default:
      return 'neutral';
  }
}

export type LeaveAction = 'withdraw' | 'requestCancel';

/**
 * The one action an employee can take on their own request.
 * - Not yet approved → withdraw (POST /leave/requests/{id}/withdraw): stops it and releases the days held.
 * - Approved and not started yet → ask to cancel (POST /leave/requests/{id}/cancel): the approver confirms.
 * Leave that has started or ended is changed through HR, not from the phone.
 */
export function leaveActionFor(kind: LeaveStatusKind, startDate: string, todayIso: string): LeaveAction | null {
  if (kind === 'pending' || kind === 'draft') return 'withdraw';
  if (kind === 'approved') {
    const s = isoToUtcMs(startDate);
    const t = isoToUtcMs(todayIso);
    if (s !== null && t !== null && s > t) return 'requestCancel';
  }
  return null;
}

/** Groups for My requests: what is still moving first, then what is coming up, then history. */
export function leaveGroupFor(kind: LeaveStatusKind, endDate: string, todayIso: string): 'inProgress' | 'upcoming' | 'past' {
  if (kind === 'pending' || kind === 'cancellationPending' || kind === 'draft') return 'inProgress';
  if (kind === 'approved') {
    const e = isoToUtcMs(endDate);
    const t = isoToUtcMs(todayIso);
    if (e !== null && t !== null && e >= t) return 'upcoming';
  }
  return 'past';
}

/** Who a pending request is waiting for, from the request's approval steps. */
export type ApproverKind = 'manager' | 'hr' | 'named' | 'unknown';

export function nextApproverFrom(
  steps: { stepNumber?: number; approverRole?: string | null; approverName?: string | null; decision?: string | null }[],
): { kind: ApproverKind; name?: string } | null {
  const pending = [...steps]
    .filter((s) => (s.decision ?? '').toLowerCase() === 'pending')
    .sort((a, b) => (a.stepNumber ?? 0) - (b.stepNumber ?? 0))[0];
  if (!pending) return null;
  const name = (pending.approverName ?? '').trim();
  if (name) return { kind: 'named', name };
  const role = (pending.approverRole ?? '').replace(/^role:/i, '').replace(/[\s_-]/g, '').toLowerCase();
  if (role.includes('manager') && !role.startsWith('hr')) return { kind: 'manager' };
  if (role.startsWith('hr') || role.includes('humanresources')) return { kind: 'hr' };
  return { kind: 'unknown' };
}
