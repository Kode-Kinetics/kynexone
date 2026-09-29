/**
 * The rules the New hires screen acts on, kept out of the component so they can be checked on
 * their own. The server stays authoritative: every action is re-checked there (maker-checker,
 * data scope, closed drafts, activation readiness), and these helpers only decide what to offer.
 */

export type DraftTone = 'amber' | 'emerald' | 'rose' | 'slate';

const STATUS: Record<string, { label: string; tone: DraftTone }> = {
  Draft: { label: 'Being prepared', tone: 'slate' },
  Submitted: { label: 'Waiting for HR approval', tone: 'amber' },
  PendingHrApproval: { label: 'Waiting for HR approval', tone: 'amber' },
  Activated: { label: 'Activated', tone: 'emerald' },
  Rejected: { label: 'Rejected', tone: 'rose' },
  Cancelled: { label: 'Withdrawn', tone: 'slate' },
};

export function draftStatusChip(status: string): { label: string; tone: DraftTone } {
  return STATUS[status] ?? { label: status, tone: 'slate' };
}

/** Open drafts can still be approved, rejected or withdrawn; the others are final. */
export function isOpenDraft(status: string): boolean {
  return status === 'Draft' || status === 'Submitted' || status === 'PendingHrApproval';
}

/** A checker decides a draft once it has been sent for approval, never while it is still being prepared. */
export function canDecideDraft(status: string, canApprove: boolean): boolean {
  return canApprove && (status === 'Submitted' || status === 'PendingHrApproval');
}

export interface DraftRowFacts {
  status: string;
  isMine: boolean;
  canApprove: boolean;
  approveBlockedReason: string | null;
  activatedEmployeeId: number | null;
}

/** The one next action a row offers. */
export type DraftNextAction =
  | { kind: 'review'; label: string }
  | { kind: 'submit'; label: string }
  | { kind: 'waiting'; label: string; reason: string }
  | { kind: 'openEmployee'; label: string; employeeId: number }
  | { kind: 'closed'; label: string };

export function nextDraftAction(row: DraftRowFacts): DraftNextAction {
  if (row.status === 'Activated') {
    return row.activatedEmployeeId != null
      ? { kind: 'openEmployee', label: 'Open employee', employeeId: row.activatedEmployeeId }
      : { kind: 'closed', label: 'Activated' };
  }
  if (!isOpenDraft(row.status)) return { kind: 'closed', label: draftStatusChip(row.status).label };
  // A draft that has not been sent yet is not ready to decide: its maker sends it first.
  if (row.status === 'Draft') {
    return row.isMine
      ? { kind: 'submit', label: 'Send for HR approval' }
      : { kind: 'waiting', label: 'View', reason: 'Still being prepared: not yet sent for approval.' };
  }
  if (canDecideDraft(row.status, row.canApprove)) return { kind: 'review', label: 'Review and decide' };
  return {
    kind: 'waiting',
    label: 'View',
    reason: row.approveBlockedReason ?? 'Waiting for an HR approver.',
  };
}

export const REJECTION_REASON_MIN = 5;
export const REJECTION_REASON_MAX = 1000;

/** Mirrors the server rule so the reviewer learns it before the round trip, not after. */
export function rejectionReasonError(reason: string): string | null {
  const trimmed = reason.trim();
  if (trimmed.length < REJECTION_REASON_MIN)
    return `Say why this hire is rejected (at least ${REJECTION_REASON_MIN} characters). The reason is kept on the record.`;
  if (trimmed.length > REJECTION_REASON_MAX)
    return `Keep the reason under ${REJECTION_REASON_MAX.toLocaleString('en-GB')} characters.`;
  return null;
}

/**
 * Approve is offered only when the server says this user may decide the draft AND the activation
 * check found nothing that would refuse it. Otherwise the reviewer is told what to fix first.
 */
export function approveDisabledReason(
  canApprove: boolean,
  approveBlockedReason: string | null,
  problemCount: number | null,
): string | null {
  if (!canApprove) return approveBlockedReason ?? 'You cannot approve this draft.';
  if (problemCount === null) return 'Checking whether this hire can be activated…';
  if (problemCount > 0)
    return `Fix the ${problemCount === 1 ? 'problem' : `${problemCount} problems`} above before approving. Activation would be refused.`;
  return null;
}

type AxiosLike = {
  isAxiosError?: boolean;
  response?: { status?: number; data?: { message?: string; error?: string } };
} | null;

/**
 * Why a request failed, in words an operator can act on. Local equivalent of
 * requestFailureReason (src/lib/requestFailure.ts, PR #121, not on this branch's base). One
 * difference: a 403 carries the server's own reason when it gives one, because the draft endpoints
 * use 403 for maker-checker ("a second user with employees.approve must activate this hire"), which is actionable.
 */
export function draftRequestFailureReason(err: unknown): string {
  const e = err as AxiosLike;
  const status = e?.response?.status;
  const message = e?.response?.data?.message;
  if (status === 403) return message ?? 'You do not have permission for this.';
  if (e?.isAxiosError && !e.response) return 'The server could not be reached. Check your connection, then retry.';
  if (e?.response) return message ?? `The server returned an error (HTTP ${status}).`;
  return 'Something went wrong. Please retry.';
}

/** True when the server refused because the draft had already been decided (409 draft_closed). */
export function isClosedDraftError(err: unknown): boolean {
  const e = err as AxiosLike;
  return e?.response?.status === 409 && e.response.data?.error === 'draft_closed';
}

export type DraftFilterKey = 'awaiting' | 'Draft' | 'Activated' | 'Rejected' | 'Cancelled' | 'all';

export const DRAFT_FILTERS: ReadonlyArray<{ key: DraftFilterKey; label: string; countKey?: 'awaitingApproval' | 'draft' | 'activated' | 'rejected' | 'cancelled' }> = [
  { key: 'awaiting', label: 'Waiting for approval', countKey: 'awaitingApproval' },
  { key: 'Draft', label: 'Being prepared', countKey: 'draft' },
  { key: 'Activated', label: 'Activated', countKey: 'activated' },
  { key: 'Rejected', label: 'Rejected', countKey: 'rejected' },
  { key: 'Cancelled', label: 'Withdrawn', countKey: 'cancelled' },
  { key: 'all', label: 'All' },
];
