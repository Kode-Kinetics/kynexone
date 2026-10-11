import { msg } from '../i18n/translations';
import { describeApiError } from './apiError';
import type { MessageParams } from '../i18n/message';
import type {
  AssignmentStatus, CompletionRule, CompletionSource, ContentKind, CourseStatus, CredentialStatus, EnrollmentStatus, MoveOn, NextAction,
  PathStatus, QuestionKind, TargetKind,
} from '../api/learning';

/**
 * Pure helpers for the learning screens: the words for every server value (as i18n keys, never shown raw), chip
 * tones, the refusal messages for every error code the API returns, and small checks the forms run before sending.
 * Kept free of React so unit/learning.spec.ts can prove them.
 */

export const LEARNING_FLAG = 'learning';

type Tone = 'blue' | 'cyan' | 'emerald' | 'amber' | 'rose' | 'slate';

export const assignmentStatusLabel: Record<AssignmentStatus, string> = {
  Assigned: msg('Not started'),
  InProgress: msg('In progress'),
  Completed: msg('Completed'),
  Cancelled: msg('Cancelled'),
};

export const enrollmentStatusLabel: Record<EnrollmentStatus, string> = {
  NotStarted: msg('Not started'),
  InProgress: msg('In progress'),
  PendingVerification: msg('Waiting for HR to verify'),
  Completed: msg('Completed'),
  Locked: msg('Locked — waiting for HR'),
  Cancelled: msg('Cancelled'),
};

export const credentialStatusLabel: Record<CredentialStatus, string> = {
  Active: msg('Valid'),
  Expired: msg('Expired'),
  Revoked: msg('Revoked'),
  Superseded: msg('Replaced'),
};

export const courseStatusLabel: Record<CourseStatus, string> = {
  Draft: msg('Draft'),
  Published: msg('Published'),
  Retired: msg('Retired'),
};

export const pathStatusLabel: Record<PathStatus, string> = {
  Draft: msg('Draft'),
  Active: msg('Active'),
  Archived: msg('Archived'),
};

export const contentKindLabel: Record<ContentKind, string> = {
  Lesson: msg('Lesson'),
  Link: msg('Link to video or document'),
  Cmi5: msg('Learning unit (cmi5)'),
};

export const completionRuleLabel: Record<CompletionRule, string> = {
  Assessment: msg('Pass the assessment'),
  Cmi5MoveOn: msg('Meet the unit’s completion rule'),
  Acknowledgement: msg('Read and acknowledge'),
};

/**
 * How the transcript and certificate name the evidence behind a completion. Acknowledgement is labelled as such (§3.1);
 * cmi5 is what the course content itself reported — the learner's browser carries it, so it is never called proof.
 */
export const completionSourceLabel: Record<CompletionSource, string> = {
  Assessment: msg('Passed assessment'),
  Cmi5: msg('Completed — reported by course content'),
  Acknowledgement: msg('Acknowledged'),
};

export const moveOnLabel: Record<MoveOn, string> = {
  Completed: msg('Completed'),
  Passed: msg('Passed'),
  CompletedAndPassed: msg('Completed and passed'),
  CompletedOrPassed: msg('Completed or passed'),
  NotApplicable: msg('Opening it is enough'),
};

export const questionKindLabel: Record<QuestionKind, string> = {
  Single: msg('One correct answer'),
  Multi: msg('Several correct answers'),
  TrueFalse: msg('True or false'),
};

export const targetKindLabel: Record<TargetKind, string> = {
  Employee: msg('Employees'),
  Company: msg('Company'),
  Department: msg('Department'),
  Designation: msg('Designation'),
  Grade: msg('Grade'),
};

export const sourceLabel: Record<string, string> = {
  Employee: msg('Assigned by HR'),
  Department: msg('Assigned to the department'),
  Designation: msg('Assigned to the designation'),
  Grade: msg('Assigned to the grade'),
  Rule: msg('Assigned by a standing rule'),
  Renewal: msg('Renewal'),
};

/** Why an employee was not assigned (AssignmentOutcome.reasons). */
export const refusalReasonLabel: Record<string, string> = {
  already_assigned: msg('Already has this path and has not finished it'),
  employee_not_active: msg('Not an active employee'),
  employee_not_in_scope: msg('Not an employee you manage'),
  company_not_eligible: msg('Their company is not eligible for this path'),
  department_not_eligible: msg('Their department is not eligible for this path'),
  designation_not_eligible: msg('Their designation is not eligible for this path'),
  grade_not_eligible: msg('Their grade is not eligible for this path'),
};

export const nextActionLabel: Record<NextAction, string> = {
  open: msg('Start'),
  take_assessment: msg('Take the assessment'),
  acknowledge: msg('Acknowledge'),
  locked: msg('Ask HR to reset your attempts'),
  blocked: msg('Finish the earlier course first'),
  pending_verification: msg('Waiting for HR to verify'),
  done: msg('Completed'),
  closed: msg('No longer assigned'),
};

export function assignmentTone(status: AssignmentStatus, overdue: boolean): Tone {
  if (status === 'Completed') return 'emerald';
  if (status === 'Cancelled') return 'slate';
  if (overdue) return 'rose';
  return status === 'InProgress' ? 'blue' : 'amber';
}

/**
 * The one chip for an assignment: waiting for HR verification (the learner has done their part) wins over overdue, which
 * wins over the plain status. Returns an i18n key and a tone.
 */
export function assignmentChip(status: AssignmentStatus, overdue: boolean, awaitingVerification: boolean): { label: string; tone: Tone } {
  const live = status === 'Assigned' || status === 'InProgress';
  if (live && awaitingVerification) return { label: msg('Waiting for HR to verify'), tone: 'amber' };
  if (live && overdue) return { label: msg('Overdue'), tone: 'rose' };
  return { label: assignmentStatusLabel[status], tone: assignmentTone(status, false) };
}

export function enrollmentTone(status: EnrollmentStatus): Tone {
  return ({ Completed: 'emerald', PendingVerification: 'amber', InProgress: 'blue', NotStarted: 'slate', Locked: 'rose', Cancelled: 'slate' } as const)[status];
}

export function credentialTone(status: CredentialStatus): Tone {
  return ({ Active: 'emerald', Expired: 'amber', Revoked: 'rose', Superseded: 'slate' } as const)[status];
}

/** Every error code the learning API returns, as a sentence the person can act on. */
const errorMessages: Record<string, string> = {
  feature_not_enabled: msg('Learning is not switched on for your organisation yet.'),
  not_found: msg('This record no longer exists, or you do not have access to it.'),
  invalid_request: msg('Some details are missing or not valid. Check the form and try again.'),
  concurrent_change: msg('Someone else changed this at the same time. Reload and try again.'),
  course_code_taken: msg('A course with this code already exists. Open it and create a new version instead.'),
  course_immutable: msg('A published version cannot be changed. Create a new version, edit it and publish that.'),
  course_not_draft: msg('Only a draft can be published.'),
  course_incomplete: msg('This draft is not ready to publish: add its content and questions first.'),
  course_not_published: msg('Only the published version can be retired.'),
  draft_exists: msg('This course already has a draft. Edit that one.'),
  path_code_taken: msg('A learning path with this code already exists.'),
  path_items_locked: msg('The courses in an active path cannot change, because people are already assigned to it. Create a new path instead.'),
  path_archived: msg('An archived path cannot be changed.'),
  path_not_draft: msg('Only a draft path can be activated.'),
  path_incomplete: msg('Add at least one required course before activating the path.'),
  path_course_unpublished: msg('Every course in the path needs a published version first.'),
  path_not_active: msg('Activate the path before assigning it.'),
  path_not_effective: msg('This path is outside its effective dates.'),
  path_course_unavailable: msg('A course in this path has no published version, so it cannot be assigned.'),
  duplicate_assignment: msg('This employee already has this path and has not finished it.'),
  rule_exists: msg('An active rule already assigns this path to this group.'),
  group_scope_required: msg('Only a group-level HR user can create a standing rule. Assign the path directly instead.'),
  assignment_not_live: msg('This learning is no longer assigned.'),
  already_completed: msg('This course is already completed.'),
  enrollment_locked: msg('Every attempt has been used. Ask HR to reset your attempts.'),
  enrollment_not_locked: msg('Only a course locked after its last failed attempt can be reset.'),
  prerequisite_incomplete: msg('Finish the earlier course in this path first.'),
  completion_rule_mismatch: msg('This course is completed by its assessment or learning unit, not by acknowledging it.'),
  content_not_opened: msg('Open the course content before you acknowledge it.'),
  no_assessment: msg('This course has no assessment.'),
  attempts_exhausted: msg('Every attempt has been used. Ask HR to reset your attempts.'),
  credential_not_current: msg('Only a current or expired certificate can be changed.'),
  self_decision_forbidden: msg('You cannot decide on your own learning record. Ask another HR colleague.'),
  score_below_pass_mark: msg('A valid certificate cannot show a score below the pass mark. Revoke it instead.'),
  awaiting_verification: msg('This completion is waiting for HR to verify it.'),
  not_pending_verification: msg('This completion is no longer waiting for verification. Reload to see its current state.'),
  no_employee_record: msg('Your login is not linked to an employee record yet. Ask HR to link it.'),
  access_mode: msg('This kind of login cannot use self-service.'),
};

/** Why a draft cannot be published yet (the `details` of a course_incomplete refusal). */
export const publishProblemLabel: Record<string, string> = {
  title_missing: msg('Give the course a title.'),
  lesson_content_missing: msg('Write the lesson.'),
  link_missing: msg('Add the address of the video or document.'),
  cmi5_unit_incomplete: msg('Add the unit’s address, activity id and completion rule.'),
  assessment_empty: msg('Add at least one question.'),
  pass_mark_missing: msg('Set a pass mark above 0%.'),
  move_on_not_applicable_with_certificate: msg('A unit that counts as complete when opened cannot issue a certificate or expire.'),
  mastery_score_missing: msg('Set the pass mark the unit must report.'),
};

export function learningError(err: unknown, t: (key: string, params?: MessageParams) => string): string {
  const data = (err as { response?: { status?: number; data?: { error?: string; details?: unknown } } })?.response?.data;
  const code = typeof data?.error === 'string' ? data.error : undefined;
  if (code === 'course_incomplete' && Array.isArray(data?.details)) {
    const reasons = data.details.filter((d): d is string => typeof d === 'string' && d in publishProblemLabel).map((d) => t(publishProblemLabel[d]));
    if (reasons.length > 0) return [t(errorMessages.course_incomplete), ...reasons].join(' ');
  }
  if (code && errorMessages[code]) return t(errorMessages[code]);
  return describeApiError(err, t);
}

export function learningErrorCode(err: unknown): string | undefined {
  const data = (err as { response?: { data?: { error?: string } } })?.response?.data;
  return typeof data?.error === 'string' ? data.error : undefined;
}

/** Course and path codes: 2–40 letters, digits, dashes or underscores (the server's rule). */
export function validCode(code: string): boolean {
  return /^[A-Za-z0-9_-]{2,40}$/.test(code.trim());
}

/** cmi5 completion rules that need a "passed" — the unit must be given its pass mark (mastery score). */
export function moveOnNeedsPassMark(moveOn: MoveOn): boolean {
  return moveOn === 'Passed' || moveOn === 'CompletedAndPassed' || moveOn === 'CompletedOrPassed';
}

/** An absolute http(s) address (links and cmi5 units). */
export function validHttpUrl(value: string): boolean {
  try {
    const url = new URL(value.trim());
    return url.protocol === 'https:' || url.protocol === 'http:';
  } catch {
    return false;
  }
}

/** Client-side check of a question before saving; the server re-validates. Returns an i18n key or null. */
export function questionProblem(q: { kind: QuestionKind; prompt: string; options: string[]; correct: number[]; trueFalse: boolean | null }): string | null {
  if (!q.prompt.trim()) return msg('Write the question.');
  if (q.kind === 'TrueFalse') return q.trueFalse === null ? msg('Say whether the statement is true or false.') : null;
  const filled = q.options.filter((o) => o.trim()).length;
  if (filled < 2) return msg('Give at least two answers.');
  if (q.correct.length === 0) return msg('Mark the correct answer.');
  if (q.kind === 'Single' && q.correct.length !== 1) return msg('A one-answer question has exactly one correct answer.');
  return null;
}

/** "Due in N days" / "Overdue by N days" / null, relative to `today` (YYYY-MM-DD). */
export function dueIn(dueDate: string | null, today: string): number | null {
  if (!dueDate) return null;
  const ms = Date.parse(`${dueDate}T00:00:00Z`) - Date.parse(`${today}T00:00:00Z`);
  return Math.round(ms / 86_400_000);
}

export function todayIso(now = new Date()): string {
  return now.toISOString().slice(0, 10);
}

/** Saves a downloaded blob under `fileName`. */
export function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
