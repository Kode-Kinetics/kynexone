import client from './client';

// ── Types ──────────────────────────────────────────────────────────────────────

export interface PerformanceCycle {
  id: string;
  name: string;
  cycleType: string;
  reviewPeriodStart: string;
  reviewPeriodEnd: string;
  status: string;
  enableCalibration: boolean;
  enable360Feedback: boolean;
  enableSelfAssessment: boolean;
  enableForcedDistribution: boolean;
  selfAssessmentDeadline: string | null;
  managerReviewDeadline: string | null;
  calibrationDeadline: string | null;
  defaultScorecardTemplateId: string | null;
  notes: string;
  createdAtUtc: string;
  launchedAtUtc: string | null;
  publishedAtUtc: string | null;
  closedAtUtc: string | null;
  enrolledCount?: number;
}

export interface ScorecardTemplate {
  id: string;
  name: string;
  departmentName: string;
  designationTitle: string;
  grade: string;
  kpiWeight: number;
  competencyWeight: number;
  attendanceWeight: number;
  productivityWeight: number;
  feedbackWeight: number;
  disciplineWeight: number;
  minPassingScore: number;
  requiresCalibration: boolean;
  requires360Feedback: boolean;
  isDefault: boolean;
  isActive: boolean;
  ratingLabels: string;
  createdAtUtc: string;
}

export interface EmployeeGoal {
  id: string;
  cycleId: string | null;
  employeeId: number;
  employeeName: string;
  title: string;
  description: string;
  category: string;
  kpiType: string;
  measurementUnit: string;
  baselineValue: number;
  targetValue: number;
  actualValue: number;
  weight: number;
  achievementPct: number;
  priority: string; // High/Medium/Low
  startDate: string | null;
  dueDate: string | null;
  status: string; // Draft/Active/Completed/OnHold/Cancelled
  managerApproved: boolean;
  createdAtUtc: string;
}

export interface AppraisalReview {
  id: string;
  cycleId: string;
  cycleName: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  designationTitle: string;
  scorecardTemplateId: string;
  kpiScore: number;
  competencyScore: number;
  attendanceScore: number;
  productivityScore: number;
  feedbackScore: number;
  disciplineScore: number;
  finalScore: number;
  finalRating: string;
  calibrationAdjustment: number;
  calibrationNotes: string;
  selfAssessmentNotes: string;
  managerNotes: string;
  hrNotes: string;
  status: string;
  selfAssessmentSubmittedAt: string | null;
  managerReviewedAt: string | null;
  publishedAt: string | null;
  acknowledgedAt: string | null;
  isAppealed: boolean;
  reviewerManagerId: number | null;
  reviewerManagerName: string;
  createdAtUtc: string;
}

export interface ScoreBreakdown {
  component: string;
  rawScore: number;
  weight: number;
  weightedScore: number;
  notes: string;
}

export interface CompetencyRating {
  competencyId: string;
  competencyName: string;
  competencyCategory: string;
  selfRating: number;
  managerRating: number;
  selfComments: string;
  managerComments: string;
  weight: number;
}

export interface Competency {
  id: string;
  name: string;
  category: string;
  description: string;
  behavioralIndicators: string;
  isActive: boolean;
}

export interface CalibrationRecord {
  id: string;
  reviewId: string;
  employeeName: string;
  departmentName: string;
  originalScore: number;
  adjustedScore: number;
  adjustmentReason: string;
  originalRating: string;
  adjustedRating: string;
  calibratedByName: string;
  calibratedAtUtc: string;
}

export interface IncrementRecommendation {
  id: string;
  reviewId: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  designationTitle: string;
  currentSalary: number;
  recommendedIncrementPct: number;
  recommendedIncrementAmount: number;
  newSalary: number;
  effectiveDate: string;
  reason: string;
  status: string;
  recommendedByName: string;
  createdAtUtc: string;
}

export interface PromotionRecommendation {
  id: string;
  reviewId: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  currentDesignation: string;
  proposedDesignation: string;
  effectiveDate: string;
  reason: string;
  status: string;
  recommendedByName: string;
  createdAtUtc: string;
}

export interface BonusRecommendation {
  id: string;
  reviewId: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  bonusAmount: number;
  bonusType: string;
  reason: string;
  status: string;
  recommendedByName: string;
  createdAtUtc: string;
}

export interface PerformanceImprovementPlan {
  id: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  performanceGaps: string;
  improvementGoals: string;
  supportPlan: string;
  startDate: string;
  endDate: string;
  status: string;
  hrNotes: string;
  managerNotes: string;
  employeeComments: string;
  initiatedByName: string;
  createdAtUtc: string;
  closedAtUtc: string | null;
  /** The monitoring record, read back. PIPCheckIn.Outcome previously had no read site at all. */
  latestCheckInOutcome?: string | null;
  checkInCount?: number;
}

export interface AppraisalAppeal {
  id: string;
  reviewId: string;
  employeeId: number;
  employeeName: string;
  appealReason: string;
  employeeJustification: string;
  status: string;
  hrResponse: string;
  reviewedByName: string;
  submittedAt: string;
  reviewedAt: string | null;
}

export interface PipTerminationQueueItem {
  id: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  performanceGaps: string;
  startDate: string;
  endDate: string;
  closedAtUtc: string | null;
  recommendationReason: string;
  action: string;
}

export interface PIPCheckIn {
  id: string;
  pipId: string;
  checkInDate: string;
  notes: string;
  outcome: string;
  checkedByName: string;
  createdAtUtc: string;
}

export interface ProbationReview {
  id: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  designationTitle: string;
  probationStartDate: string;
  probationEndDate: string;
  reviewDueDate: string | null;
  performanceSummary: string;
  overallRating: number;
  managerRecommendation: string;
  managerNotes: string;
  hrDecision: string;
  hrNotes: string;
  status: string;
  reviewedByManagerName: string;
  createdAtUtc: string;
}

export interface ContinuousFeedback {
  id: string;
  employeeId: number;
  employeeName: string;
  givenByName: string;
  feedbackType: string;
  content: string;
  isPrivate: boolean;
  linkedReviewId: string | null;
  createdAtUtc: string;
}

export interface PerformanceDashboardStats {
  activeCycles: number;
  inReviewCycles: number;
  totalReviews: number;
  pendingSelfAssessment: number;
  pendingManagerReview: number;
  published: number;
  activePips: number;
  pendingProbation: number;
}

export interface CycleAnalytics {
  summary: {
    totalEnrolled: number;
    completed: number;
    completionRate: number;
    overallAvgScore: number;
    highPerformers: number;
    atRisk: number;
    calibrationsMade: number;
    appealsSubmitted: number;
  };
  distribution: { rating: string; count: number; pct: number }[];
  deptAvg: { department: string; avgScore: number; count: number }[];
  topPerformers: { employeeId: number; employeeName: string; departmentName: string; finalScore: number; finalRating: string }[];
  lowPerformers: { employeeId: number; employeeName: string; departmentName: string; finalScore: number; finalRating: string }[];
  statusCounts: Record<string, number>;
  managerBias: { managerName: string; avgScore: number; count: number; possibleLeniency: boolean; possibleSeverity: boolean }[];
}

// ── API ────────────────────────────────────────────────────────────────────────

export const cyclesApi = {
  list: (params: { status?: string; type?: string } = {}) =>
    client.get<{ items: PerformanceCycle[]; total: number }>('/api/performance/cycles', { params }).then(r => r.data),

  stats: () =>
    client.get<PerformanceDashboardStats>('/api/performance/cycles/stats').then(r => r.data),

  get: (id: string) =>
    client.get<{ cycle: PerformanceCycle; employees: object[]; reviews: AppraisalReview[]; statusCounts: Record<string, number> }>(`/api/performance/cycles/${id}`).then(r => r.data),

  create: (body: {
    name: string; cycleType: string; reviewPeriodStart: string; reviewPeriodEnd: string;
    enableCalibration: boolean; enable360Feedback: boolean; enableSelfAssessment: boolean;
    enableForcedDistribution: boolean; selfAssessmentDeadline?: string;
    managerReviewDeadline?: string; calibrationDeadline?: string;
    defaultScorecardTemplateId?: string; notes?: string;
  }) => client.post<PerformanceCycle>('/api/performance/cycles', body).then(r => r.data),

  launch: (id: string) =>
    client.post<{ cycle: PerformanceCycle; enrolledCount: number }>(`/api/performance/cycles/${id}/launch`).then(r => r.data),

  advance: (id: string) =>
    client.post<PerformanceCycle>(`/api/performance/cycles/${id}/advance`).then(r => r.data),

  close: (id: string) =>
    client.post<PerformanceCycle>(`/api/performance/cycles/${id}/close`).then(r => r.data),
};

export const templatesApi = {
  list: () =>
    client.get<ScorecardTemplate[]>('/api/performance/templates').then(r => r.data),

  create: (body: {
    name: string; departmentName?: string; designationTitle?: string; grade?: string;
    kpiWeight: number; competencyWeight: number; attendanceWeight: number;
    productivityWeight: number; feedbackWeight: number; disciplineWeight: number;
    minPassingScore: number; requiresCalibration: boolean; requires360Feedback: boolean;
    isDefault: boolean; ratingLabels?: string;
  }) => client.post<ScorecardTemplate>('/api/performance/templates', body).then(r => r.data),

  update: (id: string, body: object) =>
    client.put<ScorecardTemplate>(`/api/performance/templates/${id}`, body).then(r => r.data),

  delete: (id: string) =>
    client.delete(`/api/performance/templates/${id}`).then(r => r.data),
};

export const goalsApi = {
  list: (params: { employeeId?: number; cycleId?: string; status?: string; category?: string } = {}) =>
    client.get<{ items: EmployeeGoal[]; total: number }>('/api/performance/goals', { params }).then(r => r.data),

  get: (id: string) =>
    client.get<{ goal: EmployeeGoal; updates: object[] }>(`/api/performance/goals/${id}`).then(r => r.data),

  create: (body: {
    employeeId: number; employeeName: string; cycleId?: string; title: string;
    description?: string; category: string; kpiType: string; measurementUnit?: string;
    baselineValue?: number; targetValue: number; actualValue: number; weight: number;
    priority?: string; startDate?: string; dueDate?: string;
  }) => client.post<EmployeeGoal>('/api/performance/goals', body).then(r => r.data),

  updateProgress: (id: string, updatedValue: number, notes?: string, updatedByName?: string) =>
    client.post<EmployeeGoal>(`/api/performance/goals/${id}/progress`, { updatedValue, notes, updatedByName }).then(r => r.data),

  approve: (id: string) =>
    client.post<EmployeeGoal>(`/api/performance/goals/${id}/approve`).then(r => r.data),

  delete: (id: string) =>
    client.delete<{ deleted: boolean }>(`/api/performance/goals/${id}`).then(r => r.data),
};

export const reviewsApi = {
  list: (params: { cycleId?: string; employeeId?: number; status?: string; department?: string } = {}) =>
    client.get<{ items: AppraisalReview[]; total: number }>('/api/performance/reviews', { params }).then(r => r.data),

  get: (id: string) =>
    client.get<{
      review: AppraisalReview; template: ScorecardTemplate | null;
      breakdown: ScoreBreakdown[]; competencies: CompetencyRating[];
      goals: EmployeeGoal[]; feedback360: object[]; auditLog: object[];
      calibration: CalibrationRecord | null; appeal: object | null;
    }>(`/api/performance/reviews/${id}`).then(r => r.data),

  submitSelfAssessment: (id: string, body: {
    notes: string; kpiScore: number; competencyScore: number; productivityScore: number;
    competencyRatings?: { competencyId: string; competencyName: string; competencyCategory: string; rating: number; comments?: string; weight: number }[];
  }) => client.post<AppraisalReview>(`/api/performance/reviews/${id}/self-assessment`, body).then(r => r.data),

  submitManagerReview: (id: string, body: {
    kpiScore: number; competencyScore: number; attendanceScore: number;
    productivityScore: number; feedbackScore: number; disciplineScore: number;
    managerNotes?: string; reviewerManagerId?: number; reviewerManagerName?: string;
    competencyRatings?: object[];
  }) => client.post<AppraisalReview>(`/api/performance/reviews/${id}/manager-review`, body).then(r => r.data),

  overrideScore: (id: string, body: {
    kpiScore?: number; competencyScore?: number; attendanceScore?: number;
    productivityScore?: number; feedbackScore?: number; disciplineScore?: number;
    reason: string;
  }) => client.post<AppraisalReview>(`/api/performance/reviews/${id}/override-score`, body).then(r => r.data),

  publish: (id: string) =>
    client.post<AppraisalReview>(`/api/performance/reviews/${id}/publish`).then(r => r.data),

  acknowledge: (id: string) =>
    client.post<AppraisalReview>(`/api/performance/reviews/${id}/acknowledge`).then(r => r.data),

  appeal: (id: string, appealReason: string, justification?: string) =>
    client.post(`/api/performance/reviews/${id}/appeal`, { appealReason, justification }).then(r => r.data),

  listAppeals: (status?: string) =>
    client.get<AppraisalAppeal[]>('/api/performance/reviews/appeals', { params: { status } }).then(r => r.data),

  respondToAppeal: (appealId: string, decision: string, response: string) =>
    client.post<{ appeal: AppraisalAppeal; reviewStatus: string; compensationPermitted: boolean; nextStep: string }>(
      `/api/performance/reviews/appeals/${appealId}/respond`, { decision, response }).then(r => r.data),

  computeAttendance: (id: string) =>
    client.post<{ attendanceScore: number }>(`/api/performance/reviews/${id}/compute-attendance`).then(r => r.data),
};

export const calibrationApi = {
  getBoard: (cycleId: string, department?: string) =>
    client.get<{ reviews: object[]; distribution: object; managerStats: object[]; totalReviews: number }>(
      `/api/performance/calibration/${cycleId}`, { params: { department } }).then(r => r.data),

  adjust: (cycleId: string, reviewId: string, adjustment: number, reason: string) =>
    client.post(`/api/performance/calibration/${cycleId}/adjust`, { reviewId, adjustment, reason }).then(r => r.data),
};

export const recommendationsApi = {
  listIncrements: (status?: string) =>
    client.get<IncrementRecommendation[]>('/api/performance/recommendations/increments', { params: { status } }).then(r => r.data),

  createIncrement: (body: {
    reviewId: string; employeeId: number; employeeName: string; departmentName: string;
    designationTitle: string; currentSalary: number; incrementPct: number;
    effectiveDate: string; reason: string;
  }) => client.post<IncrementRecommendation>('/api/performance/recommendations/increments', body).then(r => r.data),

  approveIncrement: (id: string, decision: string, notes?: string) =>
    client.post<IncrementRecommendation>(`/api/performance/recommendations/increments/${id}/approve`, { decision, notes }).then(r => r.data),

  listPromotions: (status?: string) =>
    client.get<PromotionRecommendation[]>('/api/performance/recommendations/promotions', { params: { status } }).then(r => r.data),

  createPromotion: (body: {
    reviewId: string; employeeId: number; employeeName: string; departmentName: string;
    currentDesignation: string; proposedDesignation: string; effectiveDate: string; reason: string;
  }) => client.post<PromotionRecommendation>('/api/performance/recommendations/promotions', body).then(r => r.data),

  approvePromotion: (id: string, decision: string, notes?: string) =>
    client.post<PromotionRecommendation>(`/api/performance/recommendations/promotions/${id}/approve`, { decision, notes }).then(r => r.data),

  listBonuses: (status?: string) =>
    client.get<BonusRecommendation[]>('/api/performance/recommendations/bonuses', { params: { status } }).then(r => r.data),

  createBonus: (body: {
    reviewId: string; employeeId: number; employeeName: string; departmentName: string;
    bonusAmount: number; bonusType: string; reason: string;
  }) => client.post<BonusRecommendation>('/api/performance/recommendations/bonuses', body).then(r => r.data),

  approveBonus: (id: string, decision: string) =>
    client.post<BonusRecommendation>(`/api/performance/recommendations/bonuses/${id}/approve`, { decision }).then(r => r.data),
};

export const pipApi = {
  list: (params: { employeeId?: number; status?: string } = {}) =>
    client.get<PerformanceImprovementPlan[]>('/api/performance/pip', { params }).then(r => r.data),

  get: (id: string) =>
    client.get<{ pip: PerformanceImprovementPlan; checkIns: PIPCheckIn[] }>(`/api/performance/pip/${id}`).then(r => r.data),

  create: (body: {
    employeeId: number; employeeName: string; departmentName: string; triggerReviewId?: string;
    performanceGaps: string; improvementGoals: string; supportPlan?: string;
    startDate: string; endDate: string; hrNotes?: string;
  }) => client.post<PerformanceImprovementPlan>('/api/performance/pip', body).then(r => r.data),

  updateStatus: (id: string, status: string, notes?: string) =>
    client.post<{ pip: PerformanceImprovementPlan; routedTo: string | null; nextStep: string | null }>(
      `/api/performance/pip/${id}/status`, { status, notes }).then(r => r.data),

  terminationQueue: () =>
    client.get<{ items: PipTerminationQueueItem[]; total: number }>('/api/performance/pip/termination-queue').then(r => r.data),

  addCheckIn: (id: string, body: { checkInDate: string; notes: string; outcome: string }) =>
    client.post(`/api/performance/pip/${id}/checkin`, body).then(r => r.data),
};

export const probationApi = {
  list: (params: { status?: string; employeeId?: number } = {}) =>
    client.get<ProbationReview[]>('/api/performance/probation', { params }).then(r => r.data),

  create: (body: {
    employeeId: number; employeeName: string; departmentName: string; designationTitle: string;
    probationStartDate: string; probationEndDate: string; reviewDueDate?: string;
  }) => client.post<ProbationReview>('/api/performance/probation', body).then(r => r.data),

  managerReview: (id: string, body: {
    performanceSummary: string; overallRating: number; recommendation: string; notes?: string;
  }) => client.post<ProbationReview>(`/api/performance/probation/${id}/manager-review`, body).then(r => r.data),

  hrDecision: (id: string, decision: string, notes?: string, extra?: { effectiveDate?: string; newProbationEndDate?: string }) =>
    client.post<{
      probationReview: ProbationReview;
      employeeStatus: string;
      confirmationDate: string | null;
      probationEndDate: string | null;
      separationRaised: boolean;
      separationType: string | null;
    }>(`/api/performance/probation/${id}/hr-decision`, { decision, notes, ...extra }).then(r => r.data),
};

export const feedbackApi = {
  listContinuous: (params: { employeeId?: number; type?: string } = {}) =>
    client.get<ContinuousFeedback[]>('/api/performance/feedback/continuous', { params }).then(r => r.data),

  createContinuous: (body: {
    employeeId: number; employeeName: string; feedbackType: string;
    content: string; isPrivate: boolean; linkedReviewId?: string;
  }) => client.post('/api/performance/feedback/continuous', body).then(r => r.data),

  list360: (reviewId: string) =>
    client.get(`/api/performance/feedback/360/${reviewId}`).then(r => r.data),
};

export const analyticsApi = {
  dashboard: () =>
    client.get<{
      activeCycles: object[];
      pendingActions: { selfAssessmentDue: number; managerReviewPending: number; calibrationPending: number; appealsPending: number };
      recommendations: { incrementPending: number; promotionPending: number; bonusPending: number };
      activePips: number;
      probationDue: number;
    }>('/api/performance/analytics/dashboard').then(r => r.data),

  cycleAnalytics: (cycleId: string) =>
    client.get<CycleAnalytics>(`/api/performance/analytics/cycle/${cycleId}`).then(r => r.data),

  goalsCompletion: (cycleId?: string) =>
    client.get<{ total: number; completed: number; onTrack: number; atRisk: number; avgAchievement: number }>(
      '/api/performance/analytics/goals-completion', { params: { cycleId } }).then(r => r.data),
};

export const competenciesApi = {
  list: (category?: string) =>
    client.get<Competency[]>('/api/performance/competencies', { params: { category } }).then(r => r.data),

  create: (body: { name: string; category: string; description?: string; behavioralIndicators?: string }) =>
    client.post<Competency>('/api/performance/competencies', body).then(r => r.data),

  update: (id: string, body: object) =>
    client.put<Competency>(`/api/performance/competencies/${id}`, body).then(r => r.data),

  delete: (id: string) =>
    client.delete(`/api/performance/competencies/${id}`).then(r => r.data),
};
