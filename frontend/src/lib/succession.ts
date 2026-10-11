import { msg } from '../i18n/translations';
import { describeApiError } from './apiError';
import type { MessageParams } from '../i18n/message';
import type {
  ActionStatus, ActionType, AssessmentStatus, CompletionMethod, CriticalPosition, GapCode, ImpactLevel, MaskedStage, Metric,
  NominationStatus, PerformanceBand, PlanProgress, PlanStatus, Readiness, RiskLevel, SlateStatus,
} from '../api/succession';

/**
 * Pure helpers for the succession screens: the words for every server value (i18n keys under `suc.`, never shown raw),
 * chip tones, the 9-box layout and its right-to-left mirroring, refusal messages for every error code the API returns,
 * and the checks forms run before sending. Free of React so unit/succession.spec.ts can prove them.
 */

export const SUCCESSION_FLAG = 'succession';
export const CANDIDATE_VISIBILITY_FLAG = 'succession_candidate_visibility';

/** The 12 permission keys (SUC_DESIGN §4). */
export const SUC = {
  read: 'succession.read',
  positionsManage: 'succession.positions.manage',
  poolsManage: 'succession.pools.manage',
  nominate: 'succession.nominate',
  assess: 'succession.assess',
  calibrate: 'succession.calibrate',
  slatesRead: 'succession.slates.read',
  slatesManage: 'succession.slates.manage',
  devplansRead: 'succession.devplans.read',
  devplansManage: 'succession.devplans.manage',
  reportsRead: 'succession.reports.read',
  self: 'succession.self',
} as const;

/** Every key that opens the HR / manager workspace (the nav entry and the page gate use this exact list). */
export const SUCCESSION_WORKSPACE_PERMISSIONS = [
  SUC.read, SUC.positionsManage, SUC.poolsManage, SUC.nominate, SUC.assess, SUC.calibrate, SUC.slatesRead, SUC.slatesManage,
  SUC.devplansRead, SUC.devplansManage, SUC.reportsRead,
];

export type Tone = 'blue' | 'cyan' | 'emerald' | 'amber' | 'rose' | 'slate';

export const READINESS: Readiness[] = ['ReadyNow', 'OneToTwoYears', 'ThreePlusYears'];
export const RISKS: RiskLevel[] = ['Low', 'Medium', 'High'];
export const IMPACTS: ImpactLevel[] = ['Low', 'Medium', 'High', 'Critical'];
export const ACTION_TYPES: ActionType[] = ['Learning', 'Goal', 'Mentoring', 'Experience', 'Other'];
export const POTENTIALS = [1, 2, 3] as const;
export const BANDS: Exclude<PerformanceBand, 'Unrated'>[] = ['Low', 'Moderate', 'High'];

export const readinessLabel: Record<Readiness, string> = {
  ReadyNow: msg('suc.readiness.ReadyNow'),
  OneToTwoYears: msg('suc.readiness.OneToTwoYears'),
  ThreePlusYears: msg('suc.readiness.ThreePlusYears'),
};

export const riskLabel: Record<RiskLevel, string> = {
  Low: msg('suc.level.Low'),
  Medium: msg('suc.level.Medium'),
  High: msg('suc.level.High'),
};

export const impactLabel: Record<ImpactLevel, string> = { ...riskLabel, Critical: msg('suc.level.Critical') };

export const nominationStatusLabel: Record<NominationStatus, string> = {
  Proposed: msg('suc.stage.Proposed'),
  InReview: msg('suc.stage.InReview'),
  Calibrated: msg('suc.stage.Calibrated'),
  OnSlate: msg('suc.stage.OnSlate'),
  Declined: msg('suc.stage.Declined'),
  Withdrawn: msg('suc.stage.Withdrawn'),
};

export const maskedStageLabel: Record<MaskedStage, string> = {
  Proposed: msg('suc.masked.Proposed'),
  InReview: msg('suc.masked.InReview'),
  Closed: msg('suc.masked.Closed'),
};

export const assessmentStatusLabel: Record<AssessmentStatus, string> = {
  Draft: msg('suc.assessment.Draft'),
  Submitted: msg('suc.assessment.Submitted'),
  Calibrated: msg('suc.assessment.Calibrated'),
  Superseded: msg('suc.assessment.Superseded'),
};

export const slateStatusLabel: Record<SlateStatus, string> = {
  Draft: msg('suc.slate.status.Draft'),
  Submitted: msg('suc.slate.status.Submitted'),
  ReturnedForRevision: msg('suc.slate.status.ReturnedForRevision'),
  Approved: msg('suc.slate.status.Approved'),
  Superseded: msg('suc.slate.status.Superseded'),
  Cancelled: msg('suc.slate.status.Cancelled'),
};

export const planStatusLabel: Record<PlanStatus, string> = {
  Draft: msg('suc.plan.status.Draft'),
  Active: msg('suc.plan.status.Active'),
  OnHold: msg('suc.plan.status.OnHold'),
  Completed: msg('suc.plan.status.Completed'),
  Cancelled: msg('suc.plan.status.Cancelled'),
};

export const actionStatusLabel: Record<ActionStatus, string> = {
  NotStarted: msg('suc.action.status.NotStarted'),
  InProgress: msg('suc.action.status.InProgress'),
  Completed: msg('suc.action.status.Completed'),
  Cancelled: msg('suc.action.status.Cancelled'),
};

export const actionTypeLabel: Record<ActionType, string> = {
  Learning: msg('suc.action.type.Learning'),
  Goal: msg('suc.action.type.Goal'),
  Experience: msg('suc.action.type.Experience'),
  Mentoring: msg('suc.action.type.Mentoring'),
  Other: msg('suc.action.type.Other'),
};

/** How an action was completed. Linked completions say plainly that nobody ticked a box. */
export const completionMethodLabel: Record<CompletionMethod, string> = {
  Manual: msg('suc.method.Manual'),
  LinkedLearningCompleted: msg('suc.method.LinkedLearningCompleted'),
  LinkedGoalCompleted: msg('suc.method.LinkedGoalCompleted'),
};

export const bandLabel: Record<PerformanceBand, string> = {
  Low: msg('suc.band.Low'),
  Moderate: msg('suc.band.Moderate'),
  High: msg('suc.band.High'),
  Unrated: msg('suc.band.Unrated'),
};

export const potentialLabel: Record<number, string> = {
  1: msg('suc.potential.1'),
  2: msg('suc.potential.2'),
  3: msg('suc.potential.3'),
};

/** Gap codes as a short chip and a sentence that says what is missing. */
export const gapLabel: Record<GapCode, string> = {
  NoApprovedSlate: msg('suc.gap.NoApprovedSlate'),
  NoReadyNow: msg('suc.gap.NoReadyNow'),
  StaleSlate: msg('suc.gap.StaleSlate'),
  VacantNoReadyNow: msg('suc.gap.VacantNoReadyNow'),
  HighRisk: msg('suc.gap.HighRisk'),
};

export const gapExplanation: Record<GapCode, string> = {
  NoApprovedSlate: msg('suc.gapWhy.NoApprovedSlate'),
  NoReadyNow: msg('suc.gapWhy.NoReadyNow'),
  StaleSlate: msg('suc.gapWhy.StaleSlate'),
  VacantNoReadyNow: msg('suc.gapWhy.VacantNoReadyNow'),
  HighRisk: msg('suc.gapWhy.HighRisk'),
};

/** Worst first: what puts the business most at risk leads the list. */
const GAP_WEIGHT: Record<GapCode, number> = { VacantNoReadyNow: 16, HighRisk: 8, NoApprovedSlate: 4, StaleSlate: 2, NoReadyNow: 1 };

export function gapTone(code: GapCode): Tone {
  return code === 'VacantNoReadyNow' || code === 'HighRisk' ? 'rose' : code === 'StaleSlate' ? 'amber' : 'amber';
}

/** Positions with gaps first (heaviest gaps first), then by title — the workspace's order. */
export function sortGapsFirst<T extends Pick<CriticalPosition, 'gaps' | 'positionTitle'>>(rows: T[]): T[] {
  const weight = (p: T) => p.gaps.reduce((s, g) => s + (GAP_WEIGHT[g] ?? 0), 0);
  return [...rows].sort((a, b) => weight(b) - weight(a) || a.positionTitle.localeCompare(b.positionTitle));
}

/** Metric tiles: the label key and the group they sit in. Unknown keys are not rendered. */
export const metricLabel: Record<string, string> = {
  positions: msg('suc.metric.positions'),
  coverage: msg('suc.metric.coverage'),
  extendedCoverage: msg('suc.metric.extendedCoverage'),
  benchReadyNow: msg('suc.metric.benchReadyNow'),
  benchOneToTwoYears: msg('suc.metric.benchOneToTwoYears'),
  benchThreePlusYears: msg('suc.metric.benchThreePlusYears'),
  gapNoApprovedSlate: msg('suc.metric.gapNoApprovedSlate'),
  gapNoReadyNow: msg('suc.metric.gapNoReadyNow'),
  gapStaleSlate: msg('suc.metric.gapStaleSlate'),
  gapVacantNoReadyNow: msg('suc.metric.gapVacantNoReadyNow'),
  gapHighRisk: msg('suc.metric.gapHighRisk'),
  plansActive: msg('suc.metric.plansActive'),
  plansOnTrack: msg('suc.metric.plansOnTrack'),
  plansOffTrack: msg('suc.metric.plansOffTrack'),
  actionsOverdue: msg('suc.metric.actionsOverdue'),
  actionsCompletedRate: msg('suc.metric.actionsCompletedRate'),
};

/** The definition in the viewer's language. The server's English definition is the contract; these say the same. */
export const metricDefinition: Record<string, string> = {
  positions: msg('suc.metricDef.positions'),
  coverage: msg('suc.metricDef.coverage'),
  extendedCoverage: msg('suc.metricDef.extendedCoverage'),
  benchReadyNow: msg('suc.metricDef.benchReadyNow'),
  benchOneToTwoYears: msg('suc.metricDef.benchOneToTwoYears'),
  benchThreePlusYears: msg('suc.metricDef.benchThreePlusYears'),
  gapNoApprovedSlate: msg('suc.metricDef.gapNoApprovedSlate'),
  gapNoReadyNow: msg('suc.metricDef.gapNoReadyNow'),
  gapStaleSlate: msg('suc.metricDef.gapStaleSlate'),
  gapVacantNoReadyNow: msg('suc.metricDef.gapVacantNoReadyNow'),
  gapHighRisk: msg('suc.metricDef.gapHighRisk'),
  plansActive: msg('suc.metricDef.plansActive'),
  plansOnTrack: msg('suc.metricDef.plansOnTrack'),
  plansOffTrack: msg('suc.metricDef.plansOffTrack'),
  actionsOverdue: msg('suc.metricDef.actionsOverdue'),
  actionsCompletedRate: msg('suc.metricDef.actionsCompletedRate'),
};

export type MetricGroup = 'gaps' | 'coverage' | 'bench' | 'plans';

/** Gaps first (SUC_DESIGN §7): the groups in the order the workspace shows them. */
export const METRIC_GROUPS: Array<{ id: MetricGroup; label: string; keys: string[] }> = [
  { id: 'gaps', label: msg('suc.metricGroup.gaps'), keys: ['gapVacantNoReadyNow', 'gapHighRisk', 'gapNoApprovedSlate', 'gapNoReadyNow', 'gapStaleSlate'] },
  { id: 'coverage', label: msg('suc.metricGroup.coverage'), keys: ['positions', 'coverage', 'extendedCoverage'] },
  { id: 'bench', label: msg('suc.metricGroup.bench'), keys: ['benchReadyNow', 'benchOneToTwoYears', 'benchThreePlusYears'] },
  { id: 'plans', label: msg('suc.metricGroup.plans'), keys: ['plansActive', 'plansOnTrack', 'plansOffTrack', 'actionsOverdue', 'actionsCompletedRate'] },
];

/** A rate metric carries numerator/denominator; its value is a 0–100 percentage. */
export function isRate(m: Pick<Metric, 'numerator' | 'denominator'>): boolean {
  return m.denominator !== null && m.denominator !== undefined;
}

/**
 * How many rows the metric's drill returns. A rate's drill lists the numerator's rows (e.g. coverage → the positions
 * WITH a Ready-now successor); a count's drill lists exactly `value` rows; actionsOverdue sums actions, so its drill
 * lists the plans that hold them and the count is not a row count.
 */
export function expectedDrillRows(m: Pick<Metric, 'key' | 'value' | 'numerator' | 'denominator'>): number | null {
  if (m.key === 'actionsOverdue' || m.key === 'actionsCompletedRate') return null;
  if (m.key === 'plansOnTrack') return m.numerator ?? m.value;
  return isRate(m) ? m.numerator ?? 0 : m.value;
}

/** Bench drills list candidate names, so they need slate access. */
export function metricNeedsSlateAccess(drill: string): boolean {
  return drill.startsWith('bench:');
}

// ── The 9-box ───────────────────────────────────────────────────────────────

export interface NineBoxCellDef { code: string; potential: number; band: Exclude<PerformanceBand, 'Unrated'>; label: string }

/** SUC_DESIGN §2. Cell codes are 'P{potential}-{band}'. */
export const nineBoxCellLabel: Record<string, string> = {
  'P3-Low': msg('suc.cell.P3-Low'),
  'P3-Moderate': msg('suc.cell.P3-Moderate'),
  'P3-High': msg('suc.cell.P3-High'),
  'P2-Low': msg('suc.cell.P2-Low'),
  'P2-Moderate': msg('suc.cell.P2-Moderate'),
  'P2-High': msg('suc.cell.P2-High'),
  'P1-Low': msg('suc.cell.P1-Low'),
  'P1-Moderate': msg('suc.cell.P1-Moderate'),
  'P1-High': msg('suc.cell.P1-High'),
};

export function cellCode(potential: number, band: PerformanceBand): string | null {
  return band === 'Unrated' ? null : `P${Math.min(3, Math.max(1, potential))}-${band}`;
}

/**
 * The grid in DOM order: rows from High potential (top) to Limited (bottom); within a row, performance Low → High in
 * reading order. The grid element carries the document's `dir`, so CSS grid itself mirrors it: in Arabic, Low
 * performance sits on the right and High on the left. Nothing is re-sorted by hand.
 */
export function nineBoxRows(): NineBoxCellDef[][] {
  return [3, 2, 1].map((potential) => BANDS.map((band) => ({ code: `P${potential}-${band}`, potential, band, label: nineBoxCellLabel[`P${potential}-${band}`] })));
}

/** The performance bands as the eye sees them from left to right on screen, for a given writing direction. */
export function visualBandOrder(dir: 'ltr' | 'rtl'): Array<Exclude<PerformanceBand, 'Unrated'>> {
  return dir === 'rtl' ? [...BANDS].reverse() : [...BANDS];
}

// ── Plans ───────────────────────────────────────────────────────────────────

/** The plan's progress sentence: "{done} of {countable} actions completed" (cancelled actions do not count). */
export function progressParts(p: PlanProgress): { key: string; params: MessageParams } {
  const countable = Math.max(0, p.total - p.cancelled);
  if (countable === 0) return { key: msg('suc.progress.none'), params: {} };
  return { key: msg('suc.progress.count'), params: { done: p.completed, total: countable } };
}

/** Why a plan is (not) on track — never a bare green/red. */
export function onTrackReason(p: PlanProgress): string {
  if (p.overdue > 0 && p.reviewOverdue) return msg('suc.track.bothLate');
  if (p.overdue > 0) return msg('suc.track.overdueActions');
  if (p.reviewOverdue) return msg('suc.track.reviewLate');
  return msg('suc.track.ok');
}

export function planTone(status: PlanStatus, onTrack: boolean): Tone {
  if (status === 'Completed') return 'emerald';
  if (status === 'Cancelled') return 'slate';
  if (status === 'Draft') return 'slate';
  if (status === 'OnHold') return 'amber';
  return onTrack ? 'blue' : 'rose';
}

export function actionTone(status: ActionStatus, overdue: boolean): Tone {
  if (status === 'Completed') return 'emerald';
  if (status === 'Cancelled') return 'slate';
  if (overdue) return 'rose';
  return status === 'InProgress' ? 'blue' : 'slate';
}

/** Learning and Goal actions follow their link; only the others can be moved by hand (the server enforces the same). */
export function isLinkedAction(type: ActionType): boolean {
  return type === 'Learning' || type === 'Goal';
}

export function nominationTone(status: NominationStatus): Tone {
  return ({ Proposed: 'slate', InReview: 'amber', Calibrated: 'blue', OnSlate: 'emerald', Declined: 'rose', Withdrawn: 'slate' } as const)[status];
}

export function slateTone(status: SlateStatus): Tone {
  return ({ Draft: 'slate', Submitted: 'amber', ReturnedForRevision: 'rose', Approved: 'emerald', Superseded: 'slate', Cancelled: 'slate' } as const)[status];
}

export function readinessTone(r: Readiness | null): Tone {
  return r === 'ReadyNow' ? 'emerald' : r === 'OneToTwoYears' ? 'blue' : r === 'ThreePlusYears' ? 'slate' : 'slate';
}

// ── Form checks (the server re-validates; these only save a round trip) ─────

export function longEnough(value: string | null | undefined, min: number): boolean {
  return (value ?? '').trim().length >= min;
}

/** A calibration that moves the potential needs a reason of at least 20 characters; one that confirms it does not. */
export function calibrationReasonProblem(proposed: number, calibrated: number, reason: string): string | null {
  if (proposed === calibrated) return null;
  return longEnough(reason, 20) ? null : msg('suc.check.calibrationReason');
}

/** A review period like 2026-H2, 2026-Q3 or 2026-FY (the server's pattern). */
export function validPeriod(value: string): boolean {
  return /^\d{4}-(H[12]|Q[1-4]|FY)$/.test(value.trim().toUpperCase());
}

/** The current half-year, e.g. "2026-H2" — the assess form's default. */
export function currentPeriod(now = new Date()): string {
  return `${now.getUTCFullYear()}-H${now.getUTCMonth() < 6 ? 1 : 2}`;
}

/** A slate draft: candidates ordered 1..n with no gaps or repeats, each with a rationale of at least 10 characters. */
export function slateDraftProblem(entries: Array<{ order: number; rationale: string }>): string | null {
  if (entries.length === 0) return msg('suc.check.slateEmpty');
  const orders = entries.map((e) => e.order).sort((a, b) => a - b);
  if (orders.some((o, i) => o !== i + 1)) return msg('suc.check.slateOrder');
  if (entries.some((e) => !longEnough(e.rationale, 10))) return msg('suc.check.slateRationale');
  return null;
}

export function todayIso(now = new Date()): string {
  return now.toISOString().slice(0, 10);
}

export function addMonthsIso(months: number, now = new Date()): string {
  const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + months, now.getUTCDate()));
  return d.toISOString().slice(0, 10);
}

/** The first 12 characters of a SHA-256 — enough to compare by eye, the full hash is in the title. */
export function shaPrefix(sha: string | null | undefined): string {
  return (sha ?? '').slice(0, 12);
}

/** A person's name in the viewer's language, falling back to the other script. */
export function personName(p: { nameEn: string; nameAr: string } | null | undefined, locale: string): string {
  if (!p) return '';
  return locale === 'ar' ? p.nameAr || p.nameEn : p.nameEn || p.nameAr;
}

/** The audit actions on a nomination's history, in words. */
export const historyActionLabel: Record<string, string> = {
  'succession.nomination.created': msg('suc.history.created'),
  'succession.nomination.in_review': msg('suc.history.in_review'),
  'succession.nomination.calibrated': msg('suc.history.calibrated'),
  'succession.nomination.readiness_changed': msg('suc.history.readiness_changed'),
  'succession.nomination.declined': msg('suc.history.declined'),
  'succession.nomination.withdrawn': msg('suc.history.withdrawn'),
  'succession.nomination.auto_withdrawn': msg('suc.history.auto_withdrawn'),
  'succession.nomination.flagged': msg('suc.history.flagged'),
  'succession.nomination.review_cleared': msg('suc.history.review_cleared'),
};

// ── Errors ──────────────────────────────────────────────────────────────────

/** Every error code the succession API returns, as a sentence the person can act on. */
const errorMessages: Record<string, string> = {
  feature_not_enabled: msg('suc.error.feature_not_enabled'),
  not_found: msg('suc.error.not_found'),
  forbidden: msg('suc.error.forbidden'),
  concurrent_change: msg('suc.error.concurrent_change'),
  reviewer_conflict: msg('suc.error.reviewer_conflict'),
  position_without_company: msg('suc.error.position_without_company'),
  position_closed: msg('suc.error.position_closed'),
  already_critical: msg('suc.error.already_critical'),
  position_retired: msg('suc.error.position_retired'),
  duplicate_code: msg('suc.error.duplicate_code'),
  pool_archived: msg('suc.error.pool_archived'),
  candidate_ineligible: msg('suc.error.candidate_ineligible'),
  duplicate_nomination: msg('suc.error.duplicate_nomination'),
  invalid_transition: msg('suc.error.invalid_transition'),
  assessment_not_calibrated: msg('suc.error.assessment_not_calibrated'),
  assessment_exists: msg('suc.error.assessment_exists'),
  assessment_calibrated: msg('suc.error.assessment_calibrated'),
  no_change: msg('suc.error.no_change'),
  slate_open: msg('suc.error.slate_open'),
  slate_not_draft: msg('suc.error.slate_not_draft'),
  slate_empty: msg('suc.error.slate_empty'),
  candidate_not_calibrated: msg('suc.error.candidate_not_calibrated'),
  plan_closed: msg('suc.error.plan_closed'),
  plan_empty: msg('suc.error.plan_empty'),
  plan_exists: msg('suc.error.plan_exists'),
  open_actions: msg('suc.error.open_actions'),
  linked_action: msg('suc.error.linked_action'),
  learning_not_enabled: msg('suc.error.learning_not_enabled'),
  learning_refused: msg('suc.error.learning_refused'),
  employee_not_active: msg('suc.error.employee_not_active'),
  no_employee_record: msg('suc.error.no_employee_record'),
  access_mode: msg('suc.error.access_mode'),
};

/**
 * Codes whose server message carries the specific rule that was broken (which reviewer conflict, which field):
 * the server's sentence is shown after ours, so the person learns exactly what to fix.
 */
const WITH_SERVER_DETAIL = new Set(['reviewer_conflict', 'invalid_transition', 'open_actions']);

export function successionErrorCode(err: unknown): string | undefined {
  const data = (err as { response?: { data?: { error?: string } } })?.response?.data;
  return typeof data?.error === 'string' ? data.error : undefined;
}

export function successionStatus(err: unknown): number | undefined {
  return (err as { response?: { status?: number } })?.response?.status;
}

export function successionError(err: unknown, t: (key: string, params?: MessageParams) => string): string {
  const data = (err as { response?: { data?: { error?: string; message?: string } } })?.response?.data;
  const code = typeof data?.error === 'string' ? data.error : undefined;
  if (code && errorMessages[code]) {
    const ours = t(errorMessages[code]);
    const theirs = typeof data?.message === 'string' ? data.message.trim() : '';
    return WITH_SERVER_DETAIL.has(code) && theirs ? `${ours} ${theirs}` : ours;
  }
  // invalid_request: the server's sentence names the field and the rule ("Give … of at least 10 characters").
  return describeApiError(err, t);
}
