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

/** Whole numbers on the assessment's own scale; anything else is refused before it is sent. */
export function parseAssessmentScore(raw: string | undefined, totalMarks: number | null | undefined): number | null {
  if (raw == null || raw.trim() === '') return null;
  const score = Number(raw);
  if (!Number.isInteger(score) || score < 0 || score > assessmentScoreMax(totalMarks)) return null;
  return score;
}
