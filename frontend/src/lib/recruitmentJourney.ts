/**
 * The hire journey's action rules, in one place so the application drawer, the Offers tab and the
 * Assessments tab cannot disagree with each other or with the API.
 *
 * The API is the authority (see OfferRules and ApplicationsController.Advance): these predicates
 * only decide which next action to offer, so a user is not shown a button that will be refused.
 */

export const PIPELINE_STAGES = ['Applied', 'Screening', 'Assessment', 'Interview', 'Offer', 'Hired'] as const;

/**
 * The stage "Move to …" may take an application to. Hired is never a stage move: accepting the
 * offer hires the candidate and creates their employee record in one step, and the API refuses a
 * move into Hired.
 */
export function nextPipelineStage(stage: string): string | null {
  const idx = PIPELINE_STAGES.indexOf(stage as (typeof PIPELINE_STAGES)[number]);
  if (idx < 0 || idx >= PIPELINE_STAGES.length - 1) return null;
  const next = PIPELINE_STAGES[idx + 1];
  return next === 'Hired' ? null : next;
}

/** Shown where "Move to Hired" used to be. */
export const HIRE_THROUGH_OFFER_HINT = 'To hire, open the Offer tab and accept the offer. That also creates the employee record.';

/**
 * Draft offers without approval steps and fully Approved offers can be sent. A Draft that an
 * approver rejected is refused by the API with a message saying so; it is still offered here
 * because the list does not carry approval history.
 */
export function canSendOffer(status: string): boolean {
  return status === 'Draft' || status === 'Approved';
}

/**
 * The one next action an offer offers the signed-in user. Offer approval is on unless the tenant
 * switched it off: an offer needs a named HR Manager or Admin, who did not write it, to approve it
 * before it is sent, and whoever approved it cannot also send it. `context` comes from
 * GET /api/recruitment/offers/{id}, which evaluates those rules for the caller.
 */
export type OfferNextAction = 'decide' | 'send' | 'request-approval' | 'awaiting-approval' | 'rejected' | null;

export function offerNextAction(
  status: string,
  context: { canSend: boolean; required: boolean; myPendingStepId: string | null } | null,
  approvals: { status: string }[],
): OfferNextAction {
  if (!context) return null;
  if (context.myPendingStepId && status === 'PendingApproval') return 'decide';
  if (approvals.some(a => a.status === 'Rejected')) return 'rejected';
  if (!canSendOffer(status) && status !== 'PendingApproval') return null;
  if (context.canSend) return 'send';
  if (status === 'PendingApproval' || approvals.some(a => a.status === 'Pending')) return 'awaiting-approval';
  if (context.required && approvals.length === 0) return 'request-approval';
  return null;
}

/** An approver can be added while the offer is being prepared or is already in approval, unless a
 * step was rejected: then a revised offer is the way forward. */
export function canRequestApproval(status: string, approvals: { status: string }[]): boolean {
  return (status === 'Draft' || status === 'PendingApproval') && !approvals.some(a => a.status === 'Rejected');
}

export const OFFER_NEXT_ACTION_TEXT: Record<Exclude<OfferNextAction, null>, string> = {
  decide: 'You are an approver on this offer. Approve it, or reject it with a reason.',
  send: 'Approved and ready to send to the candidate.',
  'request-approval': 'This offer needs approval before it can be sent. Choose an HR Manager or Admin who did not write it.',
  'awaiting-approval': 'Waiting for the named approver to decide.',
  rejected: 'An approver rejected this offer. Generate a revised offer and request approval on that.',
};

/** An assessment's result can be recorded once it has gone to the candidate and has no score yet. */
export function canRecordAssessmentResult(a: { status: string; scorePercentage: number | null }): boolean {
  return a.scorePercentage == null && ['Sent', 'InProgress', 'Completed'].includes(a.status);
}

/**
 * The scale HR enters a result on. A template with a question bank is scored in raw marks out of
 * its total; one without (total 0 or unknown) is administered elsewhere and entered as a percentage.
 */
export function assessmentScoreMax(totalMarks: number | null | undefined): number {
  return totalMarks != null && totalMarks > 0 ? totalMarks : 100;
}

export function assessmentScoreLabel(totalMarks: number | null | undefined): string {
  return totalMarks != null && totalMarks > 0 ? `Marks (0–${totalMarks})` : 'Score % (0–100)';
}

/**
 * Why an offer could not be created, in the API's own words. The API refuses an offer whose
 * department or designation is not one of the organisation's records (422
 * `offer_placement_unresolved`), because the accepted offer's employee record could never be
 * activated. That reason must reach the recruiter, not a generic "Failed".
 */
export function offerCreationFailure(err: unknown, fallback = 'The offer could not be created. Please try again.'): string {
  const e = err as { response?: { data?: { message?: string } } } | null;
  return e?.response?.data?.message ?? fallback;
}

/** Whole numbers on the assessment's own scale; anything else is refused before it is sent. */
export function parseAssessmentScore(raw: string | undefined, totalMarks: number | null | undefined): number | null {
  if (raw == null || raw.trim() === '') return null;
  const score = Number(raw);
  if (!Number.isInteger(score) || score < 0 || score > assessmentScoreMax(totalMarks)) return null;
  return score;
}
