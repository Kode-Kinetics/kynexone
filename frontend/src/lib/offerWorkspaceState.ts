/**
 * What the offer workspace header says about the offer, from the data the workspace API already returns
 * (status, whether the original is frozen, the approval steps and the capabilities map) and what the viewer may do.
 * No state is invented here: every branch reads one of those. Returns English dictionary keys, to be passed through t().
 *
 * The header's "next action" must name only a button the viewer is actually offered (offeredButtons, which the
 * workspace renders from); when the viewer cannot take the step, the text says in plain words what is needed.
 */
export interface OfferStateInput {
  status: string;
  /** The original is frozen (offer.frozenAtUtc or an artifact exists). */
  frozen: boolean;
  approvals: { status: string }[];
  capabilities: Record<string, { allowed: boolean; reason?: string | null }>;
  /** The form has unsaved edits. */
  dirty: boolean;
  /** The viewer may ask someone to approve (an HR Manager or Admin: the approver picker is theirs). */
  viewerCanRequestApproval: boolean;
  /** The viewer is the approver of a step still waiting for a decision. */
  viewerIsPendingApprover: boolean;
}

export type OfferButton = 'save' | 'freeze' | 'issue' | 'respond' | 'request-approval' | 'decide' | 'revise' | 'withdraw';
export type OfferNext = { kind: 'button'; key: string; button: OfferButton } | { kind: 'info'; key: string } | { kind: 'none'; key: null };

const can = (s: OfferStateInput, name: string) => s.capabilities[name]?.allowed === true;
const rejected = (s: OfferStateInput) => s.approvals.some(a => a.status === 'Rejected');
const pending = (s: OfferStateInput) => s.status === 'PendingApproval' || s.approvals.some(a => a.status === 'Pending');
const needsApprovalRequest = (s: OfferStateInput) => s.status === 'Draft' && s.frozen && s.approvals.length === 0 && !can(s, 'issue') && /approval/i.test(s.capabilities.issue?.reason ?? '');

/** A more exact name for the status than the stored one, or null to show the stored status as it is. A rejected or frozen offer is still stored as Draft. */
export function offerStatusLabelKey(s: Pick<OfferStateInput, 'status' | 'frozen' | 'approvals'>): string | null {
  if (s.status !== 'Draft') return null;
  if (s.approvals.some(a => a.status === 'Rejected')) return 'Rejected by an approver';
  if (s.frozen) return 'Draft, original frozen';
  return null;
}

/** The header buttons the viewer is offered, in order. */
export function offeredButtons(s: OfferStateInput): OfferButton[] {
  const out: OfferButton[] = [];
  if (can(s, 'editDraft')) out.push('save');
  if (can(s, 'freeze')) out.push('freeze');
  if (can(s, 'issue')) out.push('issue');
  if (can(s, 'recordResponse')) out.push('respond');
  if (s.viewerIsPendingApprover && s.status === 'PendingApproval') out.push('decide');
  if (s.viewerCanRequestApproval && needsApprovalRequest(s)) out.push('request-approval');
  if (can(s, 'revise')) out.push('revise');
  if (can(s, 'withdraw')) out.push('withdraw');
  return out;
}

const BUTTON_KEY: Record<OfferButton, string> = { save: 'Save draft', freeze: 'Freeze original', issue: 'Send review', respond: 'Record acceptance', 'request-approval': 'Request approval', decide: 'Review and decide', revise: 'Create revision', withdraw: 'Withdraw' };
const act = (s: OfferStateInput, button: OfferButton): OfferNext => offeredButtons(s).includes(button) ? { kind: 'button', key: BUTTON_KEY[button], button } : { kind: 'none', key: null };

/** The one next step for the signed-in user: a button they are offered, or plain words about what is needed and from whom. */
export function offerNextAction(s: OfferStateInput): OfferNext {
  if (s.dirty && can(s, 'editDraft')) return act(s, 'save');
  for (const b of ['respond', 'issue', 'freeze'] as const) { const n = act(s, b); if (n.kind === 'button') return n; }
  if (pending(s) && !rejected(s)) return s.viewerIsPendingApprover ? act(s, 'decide') : { kind: 'info', key: 'Waiting for the approver to decide' };
  if (rejected(s)) { const n = act(s, 'revise'); return n.kind === 'button' ? n : { kind: 'info', key: 'Someone who can create a revision has to take this forward' }; }
  const reason = s.capabilities.issue?.reason ?? '';
  if (s.status === 'Approved' && /approved this offer/i.test(reason)) return { kind: 'info', key: 'Someone else has to send this offer' };
  if (needsApprovalRequest(s)) return s.viewerCanRequestApproval ? act(s, 'request-approval') : { kind: 'info', key: 'An HR Manager or Admin has to request approval' };
  return act(s, 'revise');
}

/** Why nobody else can be asked to approve right now, or null when the picker may be used. */
export function approverPickerBlock(status: string, approvals: { status: string }[]): string | null {
  const waiting = status === 'PendingApproval' || approvals.some(a => a.status === 'Pending');
  return waiting ? 'An approval is already waiting for a decision, so nobody else can be asked now. Wait for the decision, or create a revision to start again.' : null;
}
