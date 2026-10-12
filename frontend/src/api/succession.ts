import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// Succession and career development (SUC). HR / managers: /api/succession (critical positions, pools, nominations,
// talent review and the 9-box, slates, reports, development plans). Employee: /api/ess/development-plan.
// Closed (403 feature_not_enabled) unless the tenant has the `succession` opt-in flag on. Every by-id read outside the
// caller's reach answers 404, exactly like an id that does not exist.
// Errors come back as { error, message, details } — see successionError() in lib/succession.ts.
// ─────────────────────────────────────────────────────────────────────────────

export type RiskLevel = 'Low' | 'Medium' | 'High';
export type ImpactLevel = 'Low' | 'Medium' | 'High' | 'Critical';
export type Readiness = 'ReadyNow' | 'OneToTwoYears' | 'ThreePlusYears';
export type NominationStatus = 'Proposed' | 'InReview' | 'Calibrated' | 'OnSlate' | 'Declined' | 'Withdrawn';
export type PerformanceBand = 'Low' | 'Moderate' | 'High' | 'Unrated';
export type AssessmentStatus = 'Draft' | 'Submitted' | 'Calibrated' | 'Superseded';
export type SlateStatus = 'Draft' | 'Submitted' | 'ReturnedForRevision' | 'Approved' | 'Superseded' | 'Cancelled';
export type PlanStatus = 'Draft' | 'Active' | 'OnHold' | 'Completed' | 'Cancelled';
export type ActionType = 'Learning' | 'Goal' | 'Experience' | 'Mentoring' | 'Other';
export type ActionStatus = 'NotStarted' | 'InProgress' | 'Completed' | 'Cancelled';
export type CompletionMethod = 'Manual' | 'LinkedLearningCompleted' | 'LinkedGoalCompleted';
export type GapCode = 'NoApprovedSlate' | 'NoReadyNow' | 'StaleSlate' | 'VacantNoReadyNow' | 'HighRisk';
/** The masked stage a nominator sees for their own nominations (never slate membership). */
export type MaskedStage = 'Proposed' | 'InReview' | 'Closed';

export interface PersonRef { employeeId: number; employeeCode: string; nameEn: string; nameAr: string; status: string; hasLeft: boolean }

export interface SlateRef { id: string; versionNo: number; status: SlateStatus; submittedAtUtc: string | null; approvedAtUtc: string | null; effectiveFrom: string | null }

export interface CriticalPosition {
  id: string; positionId: string; positionCode: string; positionTitle: string; companyId: string; companyName: string | null;
  departmentName: string | null; riskOfLoss: RiskLevel; impactOfVacancy: ImpactLevel; rationale: string; accountable: PersonRef | null;
  incumbent: PersonRef | null; vacant: boolean; status: 'Active' | 'Retired'; effectiveFrom: string; retiredAtUtc: string | null;
  retiredReason: string | null; /** null when the caller may not see candidate-level counts (no succession.slates.read). */
  liveNominations: number | null; readyNow: number | null; oneToTwoYears: number | null; threePlusYears: number | null;
  approvedSlate: SlateRef | null; openSlate: SlateRef | null; gaps: GapCode[]; needsReviewCount: number | null; version: number;
}

export interface PositionDetail { position: CriticalPosition; viaRelationship: boolean }

export interface AvailablePosition { id: string; code: string; title: string; companyId: string; companyName: string | null; incumbent: PersonRef | null }

export interface DesignateInput {
  positionId: string; riskOfLoss: RiskLevel; impactOfVacancy: ImpactLevel; rationale: string; accountableEmployeeId?: number | null; effectiveFrom?: string | null;
}

export interface UpdatePositionInput { riskOfLoss?: RiskLevel; impactOfVacancy?: ImpactLevel; rationale?: string; accountableEmployeeId?: number | null; version: number }

export interface TalentPool {
  id: string; companyId: string; companyName: string | null; code: string; nameEn: string; nameAr: string | null; purpose: string;
  owner: PersonRef | null; status: 'Active' | 'Archived'; liveMembers: number | null; version: number;
}

export interface PoolInput { companyId: string; code: string; nameEn: string; nameAr?: string | null; purpose: string; ownerEmployeeId?: number | null }

export interface NominationTarget { kind: 'position' | 'pool' | string; id: string; code: string; title: string; titleAr: string | null; companyId: string }

export interface TargetRef { kind: string; id: string; title: string; titleAr: string | null }

export interface AssessmentBrief {
  id: string; reviewPeriod: string; status: AssessmentStatus; proposedPotential: number; calibratedPotential: number | null;
  performanceBand: PerformanceBand; performanceScore: number | null; nineBoxCell: string | null;
}

export interface Nomination {
  id: string; target: TargetRef; candidate: PersonRef; status: NominationStatus; readiness: Readiness | null; evidence: string;
  competencyEvidence: string[]; nominatedBy: PersonRef | null; nominatedAtUtc: string; readinessPrevious: Readiness | null;
  readinessChangedAtUtc: string | null; readinessReason: string | null; needsReview: boolean; reviewReason: string | null;
  exitReason: string | null; exitNote: string | null; exitedAtUtc: string | null; currentAssessment: AssessmentBrief | null;
  readyNowOnApprovedSlates: number; overCommitted: boolean; version: number;
}

export interface MyNomination {
  id: string; targetKind: string; targetTitle: string; candidate: PersonRef; stage: MaskedStage; nominatedAtUtc: string; closedNote: string | null; canWithdraw: boolean;
}

export interface HistoryEntry { action: string; atUtc: string; userId: string | null; metadata: string | null }

export interface NominateInput {
  employeeId: number; criticalPositionId?: string | null; talentPoolId?: string | null; readiness?: Readiness | null; evidence: string; competencyRatingIds?: string[];
}

export interface TalentAssessment {
  id: string; employee: PersonRef; reviewPeriod: string; status: AssessmentStatus; assessor: PersonRef | null; proposedPotential: number;
  potentialEvidence: string; appraisalReviewId: string | null; appraisalCycleName: string | null; performanceScore: number | null;
  performanceBand: PerformanceBand; bandRuleVersion: string; calibratedPotential: number | null; calibrationReason: string | null;
  calibratedBy: PersonRef | null; calibratedAtUtc: string | null; nineBoxCell: string | null; cellLabelEn: string | null; cellLabelAr: string | null;
  supersedesId: string | null; createdAtUtc: string; anonymised: boolean; version: number;
}

export interface AssessInput { employeeId: number; reviewPeriod: string; proposedPotential: number; potentialEvidence: string; draft?: boolean; supersede?: boolean }

export interface NineBoxPlacement {
  assessmentId: string; employee: PersonRef; status: AssessmentStatus; provisional: boolean; nineBoxCell: string; potential: number;
  proposedPotential: number; calibratedPotential: number | null; calibrationReason: string | null; performanceBand: PerformanceBand;
  performanceScore: number | null; appraisalReviewId: string | null; bandRuleVersion: string; assessor: PersonRef | null; reviewPeriod: string;
}

export interface NineBoxCell { code: string; potential: number; band: PerformanceBand; labelEn: string; labelAr: string; people: NineBoxPlacement[] }

export interface BandRule { highFrom: number; moderateFrom: number; version: string; source: string }

export interface NineBox { reviewPeriod: string | null; ruleVersion: string; bandRule: BandRule; cells: NineBoxCell[]; unrated: NineBoxPlacement[]; notAssessed: number }

export interface SnapshotIncumbent { employeeId: number; employeeCode: string; nameEn: string; nameAr: string; status: string; hasLeft: boolean }

export interface SnapshotCandidate {
  order: number; rationale: string; nominationId: string; employeeId: number; employeeCode: string; nameEn: string; nameAr: string;
  readiness: Readiness; assessmentId: string | null; reviewPeriod: string | null; nineBoxCell: string | null; performanceBand: PerformanceBand;
  performanceScore: number | null; appraisalReviewId: string | null; bandRuleVersion: string | null; proposedPotential: number | null;
  calibratedPotential: number | null; calibrationReason: string | null; assessorEmployeeId: number | null; calibratedAtUtc: string | null;
}

export interface SlateSnapshot {
  schema: number; criticalPositionId: string; positionId: string; positionCode: string; positionTitle: string; companyId: string;
  riskOfLoss: RiskLevel; impactOfVacancy: ImpactLevel; incumbent: SnapshotIncumbent | null; candidates: SnapshotCandidate[];
  rules: { nineBox: string; readinessScale: string; ordering: string }; builtAtUtc: string;
}

export interface SlateVersion {
  id: string; criticalPositionId: string; versionNo: number; status: SlateStatus; snapshot: SlateSnapshot | null; snapshotSha256: string;
  hashVerified: boolean; approvalRequestId: string | null; approvalStatus: string | null; submittedAtUtc: string | null; approvedAtUtc: string | null;
  effectiveFrom: string | null; supersededAtUtc: string | null; returnedAtUtc: string | null; returnReason: string | null;
  cancelledAtUtc: string | null; cancelReason: string | null; revisedFromId: string | null; closedSinceApproval: string[];
  anonymised: boolean; readOnly: boolean; version: number;
  /** Verified: re-hashes to the submitted hash. Anonymised: retention rewrote names/free text and it re-hashes to the post-anonymisation hash. Tampered: neither. */
  integrity?: 'Verified' | 'Anonymised' | 'Tampered';
}

export interface SlateCandidateInput { nominationId: string; order: number; rationale: string }

export interface PlanProgress { total: number; completed: number; cancelled: number; overdue: number; percentComplete: number | null; onTrack: boolean; reviewOverdue: boolean }

export interface PlanAction {
  id: string; actionType: ActionType; title: string; description: string | null; status: ActionStatus; dueOn: string | null; isOverdue: boolean;
  owner: PersonRef | null; mentor: PersonRef | null; learningAssignmentId: string | null; learningPathName: string | null;
  learningPathNameAr: string | null; learningStatus: string | null; employeeGoalId: string | null; goalTitle: string | null;
  goalStatus: string | null; progressNote: string | null; completedAtUtc: string | null; completionMethod: CompletionMethod | null;
  /** The employee proposed it is done; the plan owner or HR confirms (only confirmed completions count). */
  completionProposedAtUtc?: string | null;
  sortOrder: number; version: number;
}

export interface DevelopmentPlan {
  id: string; employee: PersonRef; owner: PersonRef | null; purpose: string; status: PlanStatus; nextReviewOn: string;
  lastReviewedAtUtc: string | null; lastReviewNote: string | null; activatedAtUtc: string | null; completedAtUtc: string | null;
  cancelledAtUtc: string | null; cancelReason: string | null;
  /** Only for callers holding succession.slates.read; null for everyone else. */
  nominationId: string | null; progress: PlanProgress; actions: PlanAction[]; createdAtUtc: string; version: number;
}

export interface PlanInput { employeeId: number; purpose: string; ownerEmployeeId?: number | null; nextReviewOn?: string | null; nominationId?: string | null }

export interface ActionInput {
  actionType: ActionType; title: string; description?: string | null; dueOn?: string | null; ownerEmployeeId?: number | null;
  learningPathId?: string | null; learningAssignmentId?: string | null; employeeGoalId?: string | null; mentorEmployeeId?: number | null;
}

export interface ActionUpdateInput { status?: ActionStatus | null; progressNote?: string | null; dueOn?: string | null; version: number; proposeCompletion?: boolean }

export interface MyMembership { kind: 'pool' | 'position' | string; title: string; titleAr: string | null; stage: string; readiness: Readiness | null }

export interface MyDevelopment { current: DevelopmentPlan | null; history: DevelopmentPlan[]; candidateVisibility: boolean; memberships: MyMembership[] }

export interface LearningPathOption { id: string; code: string; name: string; nameAr: string | null }
export interface GoalOption { id: string; title: string; status: string; dueDate: string | null }

export interface Metric { key: string; value: number; numerator: number | null; denominator: number | null; definition: string; drill: string }
export interface ReportSummary { asOf: string; activePositions: number; metrics: Metric[] }

export interface BenchRow {
  criticalPositionId: string; positionTitle: string; slateVersionId: string; slateVersionNo: number; nominationId: string; candidate: PersonRef;
  readiness: Readiness; order: number;
}
export interface PlanRow {
  planId: string; employee: PersonRef; status: PlanStatus; nextReviewOn: string; total: number; completed: number; cancelled: number;
  overdue: number; percentComplete: number | null; onTrack: boolean;
}
export interface DrillResult { metric: string; kind: 'positions' | 'candidates' | 'plans'; positions: CriticalPosition[]; candidates: BenchRow[]; plans: PlanRow[] }

export interface TrendPoint { monthEnd: string; activePositions: number; withReadyNow: number; withReadyNowOrOneToTwo: number; coverage: number | null; extendedCoverage: number | null }

const base = '/api/succession';
const plans = `${base}/development-plans`;

export const successionApi = {
  // Critical positions
  positions: (status?: string) => client.get<CriticalPosition[]>(`${base}/positions`, { params: { status } }).then((r) => r.data),
  available: () => client.get<AvailablePosition[]>(`${base}/positions/available`).then((r) => r.data),
  myPositions: () => client.get<CriticalPosition[]>(`${base}/positions/mine`).then((r) => r.data),
  position: (id: string) => client.get<PositionDetail>(`${base}/positions/${id}`).then((r) => r.data),
  designate: (input: DesignateInput) => client.post<CriticalPosition>(`${base}/positions`, input).then((r) => r.data),
  updatePosition: (id: string, input: UpdatePositionInput) => client.put<CriticalPosition>(`${base}/positions/${id}`, input).then((r) => r.data),
  retirePosition: (id: string, reason: string) => client.post<CriticalPosition>(`${base}/positions/${id}/retire`, { reason }).then((r) => r.data),
  slates: (positionId: string) => client.get<SlateVersion[]>(`${base}/positions/${positionId}/slates`).then((r) => r.data),
  createSlate: (positionId: string, candidates: SlateCandidateInput[]) =>
    client.post<SlateVersion>(`${base}/positions/${positionId}/slates`, { candidates }).then((r) => r.data),

  // Pools
  pools: (includeArchived = false) => client.get<TalentPool[]>(`${base}/pools`, { params: { includeArchived } }).then((r) => r.data),
  pool: (id: string) => client.get<TalentPool>(`${base}/pools/${id}`).then((r) => r.data),
  createPool: (input: PoolInput) => client.post<TalentPool>(`${base}/pools`, input).then((r) => r.data),
  archivePool: (id: string, reason: string) => client.post<TalentPool>(`${base}/pools/${id}/archive`, { reason }).then((r) => r.data),

  // Nominations
  targets: () => client.get<NominationTarget[]>(`${base}/nomination-targets`).then((r) => r.data),
  nominations: (params: { positionId?: string; poolId?: string; employeeId?: number; status?: string } = {}) =>
    client.get<Nomination[]>(`${base}/nominations`, { params }).then((r) => r.data),
  myNominations: () => client.get<MyNomination[]>(`${base}/nominations/mine`).then((r) => r.data),
  nomination: (id: string) => client.get<Nomination>(`${base}/nominations/${id}`).then((r) => r.data),
  history: (id: string) => client.get<HistoryEntry[]>(`${base}/nominations/${id}/history`).then((r) => r.data),
  // A caller without succession.slates.read (a line manager) gets the masked MyNomination view back, never the full record.
  nominate: (input: NominateInput) => client.post<Nomination | MyNomination>(`${base}/nominations`, input).then((r) => r.data),
  startReview: (id: string) => client.post<Nomination>(`${base}/nominations/${id}/review`).then((r) => r.data),
  calibrateNomination: (id: string, readiness: Readiness, reason: string | null, version: number) =>
    client.post<Nomination>(`${base}/nominations/${id}/calibrate`, { readiness, reason, version }).then((r) => r.data),
  changeReadiness: (id: string, readiness: Readiness, reason: string, version: number) =>
    client.post<Nomination>(`${base}/nominations/${id}/readiness`, { readiness, reason, version }).then((r) => r.data),
  decline: (id: string, reason: string, version?: number) => client.post<Nomination>(`${base}/nominations/${id}/decline`, { reason, version }).then((r) => r.data),
  withdraw: (id: string, reason: string) => client.post<MyNomination>(`${base}/nominations/${id}/withdraw`, { reason }).then((r) => r.data),
  clearReview: (id: string, reason: string) => client.post<Nomination>(`${base}/nominations/${id}/clear-review`, { reason }).then((r) => r.data),

  // Talent review and the 9-box
  assessments: (params: { period?: string; employeeId?: number; status?: string } = {}) =>
    client.get<TalentAssessment[]>(`${base}/assessments`, { params }).then((r) => r.data),
  periods: () => client.get<string[]>(`${base}/assessments/periods`).then((r) => r.data),
  assessment: (id: string) => client.get<TalentAssessment>(`${base}/assessments/${id}`).then((r) => r.data),
  assess: (input: AssessInput) => client.post<TalentAssessment>(`${base}/assessments`, input).then((r) => r.data),
  submitAssessment: (id: string) => client.post<TalentAssessment>(`${base}/assessments/${id}/submit`).then((r) => r.data),
  calibrateAssessment: (id: string, calibratedPotential: number, reason: string | null, version: number) =>
    client.post<TalentAssessment>(`${base}/assessments/${id}/calibrate`, { calibratedPotential, reason, version }).then((r) => r.data),
  nineBox: (params: { period?: string; positionId?: string; poolId?: string } = {}) =>
    client.get<NineBox>(`${base}/nine-box`, { params }).then((r) => r.data),

  // Slates
  slate: (id: string) => client.get<SlateVersion>(`${base}/slates/${id}`).then((r) => r.data),
  updateSlate: (id: string, candidates: SlateCandidateInput[], version: number) =>
    client.put<SlateVersion>(`${base}/slates/${id}`, { candidates, version }).then((r) => r.data),
  submitSlate: (id: string, version: number) => client.post<SlateVersion>(`${base}/slates/${id}/submit`, { version }).then((r) => r.data),
  reviseSlate: (id: string) => client.post<SlateVersion>(`${base}/slates/${id}/revise`).then((r) => r.data),
  cancelSlate: (id: string, reason: string) => client.post<SlateVersion>(`${base}/slates/${id}/cancel`, { reason }).then((r) => r.data),

  // Reports
  summary: () => client.get<ReportSummary>(`${base}/reports/summary`).then((r) => r.data),
  drill: (metric: string) => client.get<DrillResult>(`${base}/reports/drill`, { params: { metric } }).then((r) => r.data),
  trend: (months = 12) => client.get<TrendPoint[]>(`${base}/reports/trend`, { params: { months } }).then((r) => r.data),

  // Pickers
  people: (search: string) => client.get<PersonRef[]>(`${base}/people`, { params: { search } }).then((r) => r.data),

  // Development plans
  plans: (params: { employeeId?: number; status?: string } = {}) => client.get<DevelopmentPlan[]>(plans, { params }).then((r) => r.data),
  plan: (id: string) => client.get<DevelopmentPlan>(`${plans}/${id}`).then((r) => r.data),
  createPlan: (input: PlanInput) => client.post<DevelopmentPlan>(plans, input).then((r) => r.data),
  activatePlan: (id: string) => client.post<DevelopmentPlan>(`${plans}/${id}/activate`).then((r) => r.data),
  holdPlan: (id: string, reason: string) => client.post<DevelopmentPlan>(`${plans}/${id}/hold`, { reason }).then((r) => r.data),
  completePlan: (id: string) => client.post<DevelopmentPlan>(`${plans}/${id}/complete`).then((r) => r.data),
  cancelPlan: (id: string, reason: string) => client.post<DevelopmentPlan>(`${plans}/${id}/cancel`, { reason }).then((r) => r.data),
  reviewPlan: (id: string, note: string, nextReviewOn: string, version: number) =>
    client.post<DevelopmentPlan>(`${plans}/${id}/review`, { note, nextReviewOn, version }).then((r) => r.data),
  addAction: (planId: string, input: ActionInput) => client.post<DevelopmentPlan>(`${plans}/${planId}/actions`, input).then((r) => r.data),
  updateAction: (actionId: string, input: ActionUpdateInput) => client.put<DevelopmentPlan>(`${plans}/actions/${actionId}`, input).then((r) => r.data),
  confirmCompletion: (actionId: string, version: number) =>
    client.post<DevelopmentPlan>(`${plans}/actions/${actionId}/confirm-completion`, { version }).then((r) => r.data),
  learningPaths: () => client.get<LearningPathOption[]>(`${plans}/learning-paths`).then((r) => r.data),
  goals: (employeeId: number) => client.get<GoalOption[]>(`${plans}/goals`, { params: { employeeId } }).then((r) => r.data),

  // Employee self-service
  myPlan: () => client.get<MyDevelopment>('/api/ess/development-plan').then((r) => r.data),
  updateMyAction: (actionId: string, input: ActionUpdateInput) =>
    client.put<MyDevelopment>(`/api/ess/development-plan/actions/${actionId}`, input).then((r) => r.data),

  // The slate decision goes through the shared approvals API. A Reject is "Return for revision" and needs comments.
  decideSlate: (approvalRequestId: string, decision: 'Approve' | 'Reject', comments: string) =>
    client.post(`/api/approval-requests/${approvalRequestId}/decisions`, { decision, comments }).then((r) => r.data),
};
