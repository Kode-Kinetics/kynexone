// ============================================================
// Manager approvals — pure model (lane B).
//
// One inbox for everything a manager decides on mobile:
//   - workflow approvals  GET /api/approval-requests?status=Pending (only rows where the caller is the
//                         current approver — the server scopes it)
//   - overtime            GET /api/overtime/requests?status=PendingManager (OvertimeController has its own
//                         approval path, so these never appear in /approval-requests)
//
// Rules this file enforces:
//   - Nothing is fabricated. No default urgency, no "requested just now" when the server sent no date,
//     no requester name invented from nothing. Unknown stays null and the screen hides the line.
//   - A multi-step approval reports its TRUE state after a decision: approving step 1 of 2 is
//     "approved at your step, now with HR", never "Approved".
//
// Import-free so tests/managerModel.test.ts runs it under node --test.
// ============================================================

export type ApprovalKind =
  | 'LEAVE'
  | 'OVERTIME'
  | 'ATTENDANCE_CORRECTION'
  | 'LOAN'
  | 'ADVANCE'
  | 'PAYROLL'
  | 'RECRUITMENT'
  | 'APPRAISAL'
  | 'EMPLOYEE_CHANGE'
  | 'OTHER';

export type InboxSource = 'workflow' | 'overtime';

export interface PriorDecision {
  stepOrder: number | null;
  decision: DecisionVerb;
  comments: string | null;
  decidedAt: string | null;
  decidedByUserId: string | null;
}

export interface OvertimeFacts {
  workDate: string | null;
  startUtc: string | null;
  endUtc: string | null;
  minutes: number | null;
  reason: string | null;
}

export interface InboxItem {
  /** `${source}:${id}` — unique across both sources. */
  key: string;
  source: InboxSource;
  /** Approval-request id (workflow) or overtime-request id. */
  id: string;
  /** The record being approved (leave request id, …). */
  entityId: string | null;
  kind: ApprovalKind;
  /** Server title, e.g. "Annual Leave — Rania Saleh". Null for overtime (built from facts instead). */
  title: string | null;
  /** What is being approved ("Annual Leave"), from the title. */
  subject: string | null;
  requesterName: string | null;
  requesterEmployeeId: number | null;
  submittedAt: string | null;
  dueAt: string | null;
  isOverdue: boolean;
  /** Workflow step the request is at; null when the source has no steps. */
  stepOrder: number | null;
  canDecide: boolean;
  /** Server's plain-words reason when canDecide is false (English only). */
  blockedReason: string | null;
  /** What is actually changing, e.g. "IBAN, passport". */
  changeSummary: string | null;
  priorDecisions: PriorDecision[];
  overtime: OvertimeFacts | null;
}

export type DecisionVerb = 'approved' | 'rejected' | 'returned' | 'other';

type Raw = Record<string, unknown> | null | undefined;

function str(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  const s = String(value).trim();
  return s ? s : null;
}

function num(value: unknown): number | null {
  if (value === null || value === undefined || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

export function approvalKind(entityName?: string | null): ApprovalKind {
  const v = (entityName ?? '').toUpperCase();
  if (v.includes('LEAVE')) return 'LEAVE';
  if (v.includes('OVERTIME')) return 'OVERTIME';
  if (v.includes('ATTENDANCE') || v.includes('REGULARIZATION') || v.includes('CORRECTION')) return 'ATTENDANCE_CORRECTION';
  if (v.includes('LOAN')) return 'LOAN';
  if (v.includes('ADVANCE')) return 'ADVANCE';
  if (v.includes('PAYROLL')) return 'PAYROLL';
  if (v.includes('REQUISITION') || v.includes('RECRUIT')) return 'RECRUITMENT';
  if (v.includes('APPRAISAL')) return 'APPRAISAL';
  if (v.includes('EMPLOYEE')) return 'EMPLOYEE_CHANGE';
  return 'OTHER';
}

export function decisionVerb(value: unknown): DecisionVerb {
  const v = String(value ?? '').toLowerCase();
  if (v.startsWith('approv')) return 'approved';
  if (v.startsWith('reject')) return 'rejected';
  if (v.includes('return') || v.includes('sentback') || v.includes('send back') || v.includes('sendback')) return 'returned';
  return 'other';
}

/** "Annual Leave — Rania Saleh" → { subject: "Annual Leave", requester: "Rania Saleh" }. */
export function splitTitle(title: string | null): { subject: string | null; requester: string | null } {
  if (!title) return { subject: null, requester: null };
  const parts = title.split(' — ');
  if (parts.length < 2) return { subject: str(title), requester: null };
  return { subject: str(parts[0]), requester: str(parts.slice(1).join(' — ')) };
}

function mapPriorDecisions(raw: unknown): PriorDecision[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((d: Raw) => ({
    stepOrder: num(d?.stepOrder),
    decision: decisionVerb(d?.decision),
    comments: str(d?.comments),
    decidedAt: str(d?.decidedAtUtc),
    decidedByUserId: str(d?.decidedByUserId),
  }));
}

/** One row of GET /api/approval-requests. */
export function mapWorkflowApproval(raw: Raw): InboxItem {
  const id = str(raw?.id) ?? '';
  const title = str(raw?.title);
  const { subject, requester } = splitTitle(title);
  const pending = String(raw?.status ?? '').toLowerCase() === 'pending';
  return {
    key: `workflow:${id}`,
    source: 'workflow',
    id,
    entityId: str(raw?.entityId),
    kind: approvalKind(str(raw?.entityName)),
    title,
    subject,
    requesterName: str(raw?.requestedByName) ?? requester,
    requesterEmployeeId: num(raw?.requestedForEmployeeId),
    submittedAt: str(raw?.createdAtUtc),
    dueAt: str(raw?.dueAtUtc),
    isOverdue: raw?.isOverdue === true,
    stepOrder: num(raw?.currentStepOrder),
    canDecide: pending && raw?.canDecide === true,
    blockedReason: str(raw?.decisionBlockedReason),
    changeSummary: str(raw?.changeSummary),
    priorDecisions: mapPriorDecisions(raw?.decisions),
    overtime: null,
  };
}

/** Overtime statuses a line manager decides. PendingHR belongs to HR and is not in the manager's inbox. */
export const MANAGER_OVERTIME_STATUS = 'PendingManager';

/**
 * One row of GET /api/overtime/requests. Returns null when the row is not the manager's to decide:
 * not at the manager stage, or the manager's own overtime (the server bars deciding your own).
 */
export function mapOvertimeApproval(raw: Raw, selfEmployeeId: number | null): InboxItem | null {
  if (str(raw?.status) !== MANAGER_OVERTIME_STATUS) return null;
  const employeeId = num(raw?.employeeId);
  if (selfEmployeeId !== null && employeeId === selfEmployeeId) return null;
  const id = str(raw?.id) ?? '';
  return {
    key: `overtime:${id}`,
    source: 'overtime',
    id,
    entityId: id,
    kind: 'OVERTIME',
    title: null,
    subject: null,
    requesterName: str(raw?.employeeName),
    requesterEmployeeId: employeeId,
    submittedAt: str(raw?.createdAtUtc),
    // Overtime carries no SLA: no due date, never "overdue".
    dueAt: null,
    isOverdue: false,
    stepOrder: null,
    canDecide: true,
    blockedReason: null,
    changeSummary: null,
    priorDecisions: [],
    overtime: {
      workDate: str(raw?.workDate),
      startUtc: str(raw?.startTimeUtc),
      endUtc: str(raw?.endTimeUtc),
      minutes: num(raw?.requestedMinutes),
      reason: str(raw?.reason),
    },
  };
}

function time(value: string | null): number | null {
  if (!value) return null;
  const s = /T/.test(value) && !/(Z|[+-]\d{2}:?\d{2})$/i.test(value) ? `${value}Z` : value;
  const t = new Date(s).getTime();
  return Number.isFinite(t) ? t : null;
}

function ascNullsLast(a: number | null, b: number | null): number {
  if (a === null && b === null) return 0;
  if (a === null) return 1;
  if (b === null) return -1;
  return a - b;
}

/** Exception-first order: overdue, then soonest due, then longest waiting. */
export function compareInbox(a: InboxItem, b: InboxItem): number {
  if (a.isOverdue !== b.isOverdue) return a.isOverdue ? -1 : 1;
  return ascNullsLast(time(a.dueAt), time(b.dueAt))
    || ascNullsLast(time(a.submittedAt), time(b.submittedAt))
    || a.key.localeCompare(b.key);
}

export function mergeInbox(...lists: InboxItem[][]): InboxItem[] {
  const seen = new Map<string, InboxItem>();
  for (const list of lists) for (const item of list) if (item.id && !seen.has(item.key)) seen.set(item.key, item);
  return [...seen.values()].sort(compareInbox);
}

export interface InboxSummary {
  /** Every request waiting for this manager (the one definition used by Home and Approvals). */
  total: number;
  overdue: number;
  byKind: Partial<Record<ApprovalKind, number>>;
  /** The item to lead with: the first overdue one, else the one waiting longest. */
  lead: InboxItem | null;
}

export function summarizeInbox(items: InboxItem[]): InboxSummary {
  const byKind: Partial<Record<ApprovalKind, number>> = {};
  for (const item of items) byKind[item.kind] = (byKind[item.kind] ?? 0) + 1;
  const overdue = items.filter((i) => i.isOverdue);
  const oldest = [...items]
    .filter((i) => time(i.submittedAt) !== null)
    .sort((a, b) => (time(a.submittedAt) ?? 0) - (time(b.submittedAt) ?? 0))[0];
  return {
    total: items.length,
    overdue: overdue.length,
    byKind,
    lead: overdue[0] ?? oldest ?? items[0] ?? null,
  };
}

// ------------------------------------------------------------------
// Who has it now
// ------------------------------------------------------------------

export interface QueueTarget {
  type: 'role' | 'person';
  name: string;
}

/** "Role:HR Manager" → role "HR Manager"; "Manager:Majed Al-Ghamdi" → person "Majed Al-Ghamdi". */
export function queueTarget(queue: string | null | undefined): QueueTarget | null {
  const q = str(queue);
  if (!q) return null;
  const at = q.indexOf(':');
  if (at < 0) return { type: 'role', name: q };
  const prefix = q.slice(0, at).trim().toLowerCase();
  const name = q.slice(at + 1).trim();
  if (!name) return null;
  return { type: prefix === 'role' || prefix === 'group' || prefix === 'position' ? 'role' : 'person', name };
}

/** Role names the app can say in Arabic. Anything else is shown as the server wrote it. */
export const KNOWN_ROLES: Record<string, string> = {
  'hr manager': 'hrManager',
  hr: 'hrManager',
  manager: 'manager',
  'line manager': 'manager',
  supervisor: 'supervisor',
  'finance approver': 'financeApprover',
  'payroll officer': 'payrollOfficer',
  'payroll manager': 'payrollManager',
  admin: 'admin',
};

export function roleKey(name: string | null | undefined): string | null {
  return KNOWN_ROLES[(name ?? '').trim().toLowerCase()] ?? null;
}

// ------------------------------------------------------------------
// Outcome of a decision — the true state, not the button pressed
// ------------------------------------------------------------------

export type DecisionOutcome =
  /** The whole request is approved. */
  | { kind: 'approved' }
  | { kind: 'rejected' }
  | { kind: 'returned' }
  /** Approved at this manager's step; the request continues to the next approver. */
  | { kind: 'movedOn'; next: QueueTarget | null }
  /** The server confirmed the call but did not say the resulting state. */
  | { kind: 'recorded' };

/** POST /api/approval-requests/{id}/decisions → ApprovalRequestDto. */
export function workflowOutcome(response: Raw): DecisionOutcome {
  const status = String(response?.status ?? '').replace(/[\s_-]/g, '').toLowerCase();
  if (status === 'approved') return { kind: 'approved' };
  if (status === 'rejected') return { kind: 'rejected' };
  if (status === 'returnedtorequester' || status === 'sentback' || status === 'returned') return { kind: 'returned' };
  if (status === 'pending') return { kind: 'movedOn', next: queueTarget(str(response?.currentQueue)) };
  return { kind: 'recorded' };
}

/**
 * POST /api/overtime/requests/{id}/approve|reject. A manager's approval answers the request with
 * status PendingHR (HR gives the final approval); an admin/HR final approval answers the payroll
 * calculation, which has no status but an amount.
 */
export function overtimeOutcome(response: Raw): DecisionOutcome {
  const status = String(response?.status ?? '').toLowerCase();
  if (status === 'pendinghr') return { kind: 'movedOn', next: { type: 'role', name: 'HR Manager' } };
  if (status === 'approved') return { kind: 'approved' };
  if (status === 'rejected') return { kind: 'rejected' };
  if (!status && response && (response.amount !== undefined || response.approvedMinutes !== undefined)) return { kind: 'approved' };
  return { kind: 'recorded' };
}

// ------------------------------------------------------------------
// Decided by you (history)
// ------------------------------------------------------------------

export type RequestState = 'approved' | 'rejected' | 'returned' | 'pending' | 'other';

export interface MyDecision {
  key: string;
  kind: ApprovalKind;
  subject: string | null;
  requesterName: string | null;
  myDecision: DecisionVerb;
  decidedAt: string | null;
  comments: string | null;
  /** Where the request stands now. */
  state: RequestState;
  /** Who has it now, while it is still pending. */
  nowWith: QueueTarget | null;
}

function requestState(status: unknown): RequestState {
  const v = String(status ?? '').replace(/[\s_-]/g, '').toLowerCase();
  if (v === 'approved') return 'approved';
  if (v === 'rejected') return 'rejected';
  if (v === 'pending') return 'pending';
  if (v === 'returnedtorequester' || v === 'returned' || v === 'sentback') return 'returned';
  return 'other';
}

/**
 * Rows of GET /api/approval-requests?queue=team&status=… reduced to the ones this user decided,
 * newest decision first. A request decided twice by the same user (two steps) shows the latest.
 */
export function myDecisionsFrom(rows: Raw[], myUserId: string | null | undefined): MyDecision[] {
  const me = str(myUserId)?.toLowerCase();
  if (!me) return [];
  const byKey = new Map<string, MyDecision>();
  for (const row of rows) {
    const id = str(row?.id);
    if (!id) continue;
    const mine = mapPriorDecisions(row?.decisions)
      .filter((d) => d.decidedByUserId?.toLowerCase() === me)
      .sort((a, b) => (time(b.decidedAt) ?? 0) - (time(a.decidedAt) ?? 0))[0];
    if (!mine) continue;
    const { subject, requester } = splitTitle(str(row?.title));
    const state = requestState(row?.status);
    byKey.set(id, {
      key: id,
      kind: approvalKind(str(row?.entityName)),
      subject,
      requesterName: requester,
      myDecision: mine.decision,
      decidedAt: mine.decidedAt,
      comments: mine.comments,
      state,
      nowWith: state === 'pending' ? queueTarget(str(row?.currentQueue)) : null,
    });
  }
  return [...byKey.values()].sort((a, b) => (time(b.decidedAt) ?? 0) - (time(a.decidedAt) ?? 0));
}

// ------------------------------------------------------------------
// Leave decision context
// ------------------------------------------------------------------

export interface LeaveFacts {
  leaveTypeId: string | null;
  leaveTypeName: string | null;
  startDate: string | null;
  endDate: string | null;
  totalDays: number | null;
  dayType: string | null;
  reason: string | null;
  isEmergency: boolean;
  status: string | null;
  employeeId: number | null;
}

/** GET /api/leave/requests/{id} → { request, approvals }. */
export function mapLeaveFacts(raw: Raw): LeaveFacts | null {
  const r = (raw?.request ?? raw) as Raw;
  if (!r || !str(r.id)) return null;
  return {
    leaveTypeId: str(r.leaveTypeId),
    leaveTypeName: str(r.leaveTypeName),
    startDate: str(r.startDate),
    endDate: str(r.endDate),
    totalDays: num(r.totalDays),
    dayType: str(r.dayType),
    reason: str(r.reason),
    isEmergency: r.isEmergency === true,
    status: str(r.status),
    employeeId: num(r.employeeId),
  };
}

export interface BalanceFacts {
  leaveTypeName: string | null;
  /** Days available now. The server already reserves pending requests out of this. */
  available: number;
  /** Days left if this request is approved — only when we can show it is already reserved; else null. */
  afterApproval: number | null;
}

/**
 * The requester's balance for the request's leave type and year (GET /api/leave/balances?employeeId=).
 * The server reserves a pending request's days at submission (balance.pending, LeaveRequestsController),
 * so `available` is already net of it — approving changes nothing, and "left if approved" = available.
 * We only say so when the balance shows enough pending days to include this request; otherwise we
 * show "available now" alone rather than guess.
 */
export function balanceFacts(balances: Raw[], leave: LeaveFacts): BalanceFacts | null {
  if (!leave.leaveTypeId) return null;
  const year = leave.startDate ? Number(leave.startDate.slice(0, 4)) : null;
  const match = balances.find((b) =>
    str(b?.leaveTypeId) === leave.leaveTypeId && (year === null || num(b?.year) === null || num(b?.year) === year));
  if (!match) return null;
  const available = num(match.available);
  if (available === null) return null;
  const pending = num(match.pending) ?? 0;
  const isPending = /^pending|^submitted/i.test(leave.status ?? '');
  const reserved = isPending && leave.totalDays !== null && pending >= leave.totalDays;
  return {
    leaveTypeName: str(match.leaveTypeName) ?? leave.leaveTypeName,
    available,
    afterApproval: reserved ? available : null,
  };
}

// ------------------------------------------------------------------
// Overtime arithmetic
// ------------------------------------------------------------------

export function hoursAndMinutes(minutes: number | null): { hours: number; minutes: number } | null {
  if (minutes === null || !Number.isFinite(minutes) || minutes < 0) return null;
  return { hours: Math.floor(minutes / 60), minutes: Math.round(minutes % 60) };
}

// ------------------------------------------------------------------
// Names
// ------------------------------------------------------------------

const COMPOUND_FIRST = new Set(['abd', 'abdul', 'abdel', 'abdal', 'abu', 'عبد', 'أبو', 'ابو']);

/** The name to greet someone by: the first given name, keeping "Abdul Rahman" / "عبد الله" whole. */
export function givenName(fullName: string | null | undefined): string | null {
  const parts = (fullName ?? '').trim().split(/\s+/).filter(Boolean);
  if (!parts.length) return null;
  if (parts.length > 1 && COMPOUND_FIRST.has(parts[0].toLowerCase())) return `${parts[0]} ${parts[1]}`;
  return parts[0];
}
