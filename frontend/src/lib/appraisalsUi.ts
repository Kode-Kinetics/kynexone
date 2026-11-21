import type { ScorecardTemplate } from '../api/performance';

/**
 * Rules the appraisal screens share, kept apart from the components so they can be tested without a browser.
 * The server stays the authority on every one of them; these decide only what the screen offers and shows.
 */

/** From these statuses on, HR has published the result and the employee may see it. Before that the score, the rating
 *  and the breakdown are not shown to them, whatever the API sends. */
const RESULT_VISIBLE = new Set(['Published', 'Acknowledged', 'Appealed', 'Closed']);
export const employeeMaySeeResult = (status: string) => RESULT_VISIBLE.has(status);

/** The employee confirms they have seen a published result. */
export const canAcknowledge = (status: string) => status === 'Published';

/** The API takes an appeal on a Published or Acknowledged review, and refuses a second while one is open. */
export const canAppeal = (status: string) => status === 'Published' || status === 'Acknowledged';

/** Where a review is, as an index into the five phases the employee is shown. */
export const REVIEW_PHASES = ['self', 'manager', 'calibration', 'published', 'acknowledged'] as const;
export function reviewProgress(status: string): number {
  switch (status) {
    case 'SelfAssessmentSubmitted': case 'ManagerReview': return 1;
    case 'ManagerReviewComplete': case 'ManagerReviewed': case 'Calibration': case 'FinalApproval': return 2;
    case 'Published': return 3;
    case 'Acknowledged': case 'Appealed': case 'Closed': return 4;
    default: return 0;
  }
}

/** A cycle can only be launched with an active scorecard template. */
export const selectableTemplates = (templates: ScorecardTemplate[]) => templates.filter(t => t.isActive);

/** The template to pre-select: the company default, or the only one there is. Otherwise the user chooses. */
export function defaultTemplateId(templates: ScorecardTemplate[]): string {
  const active = selectableTemplates(templates);
  return active.find(t => t.isDefault)?.id ?? (active.length === 1 ? active[0].id : '');
}

/** The API refuses to close a PIP as Failed or TerminationRecommended while it has no recorded check-in. */
export const adverseCloseNeedsCheckIn = (newStatus: string, checkInCount: number) =>
  (newStatus === 'Failed' || newStatus === 'TerminationRecommended') && checkInCount === 0;

export const PIP_OUTCOMES = ['OnTrack', 'AtRisk', 'Improved', 'Deteriorated'] as const;
export const PROBATION_RECOMMENDATIONS = ['Confirm', 'Extend', 'Terminate'] as const;

type Tr = (key: string, params?: Record<string, string | number | null | undefined>) => string;

/** The viewer-language text for a review status. Words that already exist in the dictionary are reused. */
export function reviewStatusLabel(t: Tr, status: string): string {
  switch (status) {
    case 'Pending': return t('Pending');
    case 'SelfAssessmentDue': return t('Self-Assessment Due');
    case 'SelfAssessmentSubmitted': return t('Self-Assessment Submitted');
    case 'ManagerReview': return t('Manager Review');
    case 'ManagerReviewComplete': case 'ManagerReviewed': return t('Manager Reviewed');
    case 'Calibration': return t('Calibration');
    case 'FinalApproval': return t('Final Approval');
    case 'Published': return t('Published');
    case 'Acknowledged': return t('Acknowledged');
    case 'Appealed': return t('Appealed');
    case 'Closed': return t('Closed');
    default: return status;
  }
}

/** The five default rating names the server writes; a company's own labels are shown as they were typed. */
const RATINGS = ['Outstanding', 'Exceeds Expectations', 'Meets Expectations', 'Developing', 'Unsatisfactory'];
export const ratingLabel = (t: Tr, rating: string) => (RATINGS.includes(rating) ? t(`apr.rating.${rating}`) : rating);

const COMPONENTS = ['KPI', 'Competency', 'Attendance', 'Productivity', 'Feedback', 'Discipline'];
export const scoreComponentLabel = (t: Tr, component: string) => (COMPONENTS.includes(component) ? t(`apr.component.${component}`) : component);
