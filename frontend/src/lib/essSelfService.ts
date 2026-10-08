/**
 * Employee self-service pages. The self-service buttons ("Apply Leave", "OT Request", "My Requests")
 * must point HERE, never at /leave, /overtime or /hr-requests: those are the HR team's screens, gated on
 * leave.*, overtime.* and approvals.* — permissions the Employee role does not hold — so an employee
 * who clicked was sent to "Access Denied". These pages are gated on ess.read and call only endpoints
 * that are scoped to the caller's own record.
 */
export const ESS_LEAVE_PATH = '/ess/leave';
export const ESS_OVERTIME_PATH = '/ess/overtime';
export const ESS_REQUESTS_PATH = '/ess/requests';

export type StatusTone = 'blue' | 'emerald' | 'amber' | 'rose' | 'slate';

/** Leave statuses an employee may still cancel themselves (nothing has been approved yet). */
const PENDING_LEAVE = new Set(['Draft', 'Submitted', 'PendingManagerApproval', 'PendingHRApproval']);

export const canCancelLeave = (status: string) => PENDING_LEAVE.has(status);

/** A plain-language label (an English t() key) and a tone for a leave request status. */
export function leaveStatus(status: string): { label: string; tone: StatusTone } {
  if (PENDING_LEAVE.has(status)) return { label: 'Waiting for approval', tone: 'amber' };
  switch (status) {
    case 'Approved': return { label: 'Approved', tone: 'emerald' };
    case 'Rejected': return { label: 'Rejected', tone: 'rose' };
    case 'Cancelled': return { label: 'Cancelled', tone: 'slate' };
    case 'Withdrawn': return { label: 'Withdrawn', tone: 'slate' };
    case 'CancellationRequested': return { label: 'Cancellation requested', tone: 'amber' };
    default: return { label: status, tone: 'slate' };
  }
}

/** A plain-language label (an English t() key) and a tone for an overtime request status. */
export function overtimeStatus(status: string): { label: string; tone: StatusTone } {
  if (status.startsWith('Pending')) return { label: 'Waiting for approval', tone: 'amber' };
  switch (status) {
    case 'Draft': return { label: 'Waiting for approval', tone: 'amber' };
    case 'Approved': return { label: 'Approved', tone: 'emerald' };
    case 'Paid': return { label: 'Paid', tone: 'emerald' };
    case 'ConvertedToCompOff': return { label: 'Converted to time off', tone: 'blue' };
    case 'Rejected': return { label: 'Rejected', tone: 'rose' };
    case 'Cancelled': return { label: 'Cancelled', tone: 'slate' };
    default: return { label: status, tone: 'slate' };
  }
}

/**
 * Overtime is entered as a local date and start/end clock times and sent as UTC instants, the same
 * conversion the mobile app makes. An end time at or before the start is an overnight block
 * (22:00 → 02:00), so it rolls to the next day rather than being refused as negative.
 */
export function overtimeWindow(date: string, start: string, end: string): { startTimeUtc: string; endTimeUtc: string } | null {
  const s = new Date(`${date}T${start}`);
  let e = new Date(`${date}T${end}`);
  if (Number.isNaN(s.getTime()) || Number.isNaN(e.getTime())) return null;
  if (e.getTime() <= s.getTime()) e = new Date(e.getTime() + 24 * 60 * 60 * 1000);
  return { startTimeUtc: s.toISOString(), endTimeUtc: e.toISOString() };
}

/**
 * Whole hours and the remaining minutes, to fill a translated duration such as
 * t('{hours} h {minutes} min'). The words around the numbers belong to the translation, not here.
 */
export function splitMinutes(minutes: number): { hours: number; minutes: number } {
  const m = Math.max(0, Math.round(minutes));
  return { hours: Math.floor(m / 60), minutes: m % 60 };
}

/** "14:05" in the viewer's own clock, from a UTC instant. Digits only, so it reads the same in Arabic. */
export function localClock(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

/**
 * A plain-language label (an English t() key) and a tone for an HR request, from the server's
 * responseStatus (Closed, Responded, Overdue — not responded, Awaiting HR response).
 */
export function hrRequestStatus(responseStatus: string): { label: string; tone: StatusTone } {
  if (responseStatus === 'Closed') return { label: 'Closed', tone: 'slate' };
  if (responseStatus === 'Responded') return { label: 'HR has replied', tone: 'emerald' };
  if (responseStatus.startsWith('Overdue')) return { label: 'Overdue: HR has not replied yet', tone: 'rose' };
  return { label: 'Waiting for HR to reply', tone: 'amber' };
}

/**
 * HR replies the employee has already opened, remembered in this browser per login. The server has no
 * "read" marker for an HR request, and its "Responded" status lasts until HR closes the request, so
 * without this a reply would sit in "Needs your attention" long after it was read. Storage can be
 * blocked or cleared; then a reply simply shows again, which is the safe direction.
 */
const seenKey = (userId: string) => `kx-ess-seen-replies:${userId}`;

export function readSeenReplies(userId: string | undefined): Set<string> {
  if (!userId) return new Set();
  try {
    const raw = localStorage.getItem(seenKey(userId));
    const list: unknown = raw ? JSON.parse(raw) : [];
    return new Set(Array.isArray(list) ? list.filter((x): x is string => typeof x === 'string') : []);
  } catch {
    return new Set();
  }
}

export function markReplySeen(userId: string | undefined, requestId: string): void {
  if (!userId) return;
  try {
    const seen = readSeenReplies(userId);
    seen.add(requestId);
    // Bounded: the newest 200 are plenty for one employee's open requests.
    localStorage.setItem(seenKey(userId), JSON.stringify([...seen].slice(-200)));
  } catch { /* storage unavailable: the reply shows again, never hidden */ }
}
