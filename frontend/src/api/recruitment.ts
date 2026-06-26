import client from './client';

// ── Types ─────────────────────────────────────────────────────────────────────

export interface ManpowerRequisition {
  id: string;
  tenantId: string;
  requisitionNumber: string;
  departmentName: string;
  designationTitle: string;
  headCount: number;
  employmentType: string;
  priority: string;
  justification: string;
  requiredSkills: string;
  minExperienceYears: number | null;
  maxExperienceYears: number | null;
  budgetFrom: number | null;
  budgetTo: number | null;
  targetJoiningDate: string | null;
  status: string;
  requestedByName: string;
  rejectionReason: string;
  createdAtUtc: string;
  submittedAtUtc: string | null;
  approvedAtUtc: string | null;
}

export interface JobOpening {
  id: string;
  jobCode: string;
  requisitionId: string | null;
  title: string;
  departmentName: string;
  designationTitle: string;
  employmentType: string;
  headCount: number;
  filledCount: number;
  description: string;
  requirements: string;
  responsibilities: string;
  salaryFrom: number | null;
  salaryTo: number | null;
  location: string;
  status: string;
  assignedHrName: string;
  createdAtUtc: string;
  activeApplications?: number;
  remaining?: number;
}

export interface Candidate {
  id: string;
  firstName: string;
  lastName: string;
  fullName: string;
  email: string;
  phone: string;
  currentJobTitle: string;
  currentCompany: string;
  totalExperienceYears: number;
  educationLevel: string;
  nationality: string;
  linkedInUrl: string;
  resumeUrl: string;
  source: string;
  status: string;
  tags: string;
  createdAtUtc: string;
  applicationCount?: number;
}

export interface JobApplication {
  id: string;
  jobOpeningId: string;
  jobTitle: string;
  candidateId: string;
  candidateName: string;
  candidateEmail: string;
  stage: string;
  stageOrder: number;
  status: string;
  rejectionReason: string;
  offeredSalary: number | null;
  appliedAtUtc: string;
  stageChangedAtUtc: string | null;
  hiredAtUtc: string | null;
  onboardingDraftId: string | null;
}

export interface ApplicationEvent {
  id: string;
  eventType: string;
  stage: string;
  notes: string;
  performedByName: string;
  createdAtUtc: string;
}

export interface InterviewSchedule {
  id: string;
  applicationId: string;
  interviewType: string;
  interviewerNames: string;
  scheduledAt: string;
  durationMinutes: number;
  mode: string;
  meetingLink: string;
  location: string;
  status: string;
  overallRating: number | null;
  recommendation: string;
  feedbackNotes: string;
  completedAt: string | null;
  createdAtUtc: string;
}

export interface OfferLetter {
  id: string;
  applicationId: string;
  candidateName: string;
  offeredJobTitle: string;
  offeredDepartment: string;
  startDate: string;
  basicSalary: number;
  housingAllowance: number;
  transportAllowance: number;
  otherAllowances: number;
  grossSalary: number;
  probationMonths: number;
  status: string;
  generatedAtUtc: string;
  sentAtUtc: string | null;
  responseDeadline: string | null;
  acceptedAtUtc: string | null;
}

export interface KanbanStage {
  stage: string;
  order: number;
  applications: JobApplication[];
}

export interface ApplicationDetail {
  application: JobApplication;
  candidate: Candidate | null;
  events: ApplicationEvent[];
  interviews: InterviewSchedule[];
  offer: OfferLetter | null;
}

export interface RecruitmentStats {
  openPositions: number;
  totalOpenings: number;
  activeApplications: number;
  hiredThisMonth: number;
  offersPending: number;
}

export interface RequisitionStats {
  total: number;
  draft: number;
  pending: number;
  approved: number;
  converted: number;
}

// ── API ───────────────────────────────────────────────────────────────────────

export const requisitionsApi = {
  list: (params: { status?: string; priority?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ items: ManpowerRequisition[]; total: number }>('/api/recruitment/requisitions', { params }).then(r => r.data),

  get: (id: string) =>
    client.get<ManpowerRequisition>(`/api/recruitment/requisitions/${id}`).then(r => r.data),

  create: (body: {
    departmentId?: string; departmentName: string; designationId?: string; designationTitle: string;
    headCount: number; employmentType: string; priority: string; justification: string;
    requiredSkills: string; minExperienceYears?: number; maxExperienceYears?: number;
    budgetFrom?: number; budgetTo?: number; targetJoiningDate?: string; requestedByName: string;
  }) => client.post<ManpowerRequisition>('/api/recruitment/requisitions', body).then(r => r.data),

  submit: (id: string) =>
    client.post<ManpowerRequisition>(`/api/recruitment/requisitions/${id}/submit`).then(r => r.data),

  approve: (id: string) =>
    client.post<ManpowerRequisition>(`/api/recruitment/requisitions/${id}/approve`, {}).then(r => r.data),

  reject: (id: string, reason: string) =>
    client.post<ManpowerRequisition>(`/api/recruitment/requisitions/${id}/reject`, { reason }).then(r => r.data),

  stats: () =>
    client.get<RequisitionStats>('/api/recruitment/requisitions/stats').then(r => r.data),
};

export const openingsApi = {
  list: (params: { status?: string; page?: number } = {}) =>
    client.get<{ items: JobOpening[]; total: number }>('/api/recruitment/openings', { params }).then(r => r.data),

  get: (id: string) =>
    client.get<{ opening: JobOpening; stageCounts: { stage: string; count: number }[] }>(`/api/recruitment/openings/${id}`).then(r => r.data),

  create: (body: {
    requisitionId?: string; title: string; departmentName: string; designationTitle: string;
    employmentType: string; headCount: number; description: string; requirements: string;
    responsibilities: string; salaryFrom?: number; salaryTo?: number; location: string; assignedHrName: string;
  }) => client.post<JobOpening>('/api/recruitment/openings', body).then(r => r.data),

  close: (id: string) =>
    client.post<JobOpening>(`/api/recruitment/openings/${id}/close`).then(r => r.data),

  stats: () =>
    client.get<RecruitmentStats>('/api/recruitment/openings/stats').then(r => r.data),
};

export const candidatesApi = {
  list: (params: { search?: string; status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ items: Candidate[]; total: number }>('/api/recruitment/candidates', { params }).then(r => r.data),

  create: (body: {
    firstName: string; lastName: string; email: string; phone: string;
    currentJobTitle: string; currentCompany: string; totalExperienceYears: number;
    educationLevel: string; nationality: string; linkedInUrl: string; source: string; tags: string; resumeUrl?: string;
  }) => client.post<Candidate>('/api/recruitment/candidates', body).then(r => r.data),
};

// ── Extended Types ────────────────────────────────────────────────────────────

export interface WorkforcePlan {
  id: string;
  planCode: string;
  planYear: number;
  planName: string;
  departmentName: string;
  currentHeadcount: number;
  plannedHeadcount: number;
  gapCount: number;
  budgetAllocated: number;
  budgetUtilized: number;
  currencyCode: string;
  status: string;
  notes: string;
  createdByName: string;
  createdAtUtc: string;
  approvedAtUtc: string | null;
}

export interface InterviewFeedback {
  id: string;
  interviewScheduleId: string;
  applicationId: string;
  interviewerName: string;
  interviewerRole: string;
  communicationScore: number;
  technicalScore: number;
  cultureFitScore: number;
  problemSolvingScore: number;
  leadershipScore: number;
  overallScore: number;
  strengths: string;
  concerns: string;
  notes: string;
  recommendation: string;
  submittedAtUtc: string;
}

export interface AssessmentTemplate {
  id: string;
  code: string;
  title: string;
  description: string;
  assessmentType: string;
  durationMinutes: number;
  passingScore: number;
  totalMarks: number;
  isRandomized: boolean;
  audience: string;
  isActive: boolean;
  createdAtUtc: string;
}

export interface CandidateAssessment {
  id: string;
  applicationId: string;
  candidateId: string;
  templateId: string;
  templateName: string;
  status: string;
  sentAtUtc: string | null;
  completedAtUtc: string | null;
  expiresAtUtc: string | null;
  scoreObtained: number | null;
  totalMarks: number | null;
  scorePercentage: number | null;
  passed: boolean | null;
  createdAtUtc: string;
}

export interface OfferApproval {
  id: string;
  offerLetterId: string;
  stepOrder: number;
  approverName: string;
  approverRole: string;
  status: string;
  comments: string;
  decidedAtUtc: string | null;
}

export interface OnboardingChecklist {
  id: string;
  code: string;
  name: string;
  description: string;
  applicableTo: string;
  departmentName: string;
  isActive: boolean;
}

export interface OnboardingTask {
  id: string;
  checklistId: string | null;
  employeeId: string | null;
  applicationId: string | null;
  taskTitle: string;
  taskDescription: string;
  category: string;
  assignedToName: string;
  status: string;
  dueDate: string | null;
  completedDate: string | null;
  notes: string;
  orderIndex: number;
  isMandatory: boolean;
  createdAtUtc: string;
}

// ── Extended API Clients ───────────────────────────────────────────────────────

export const workforcePlanningApi = {
  list: (year?: number, status?: string) =>
    client.get<{ total: number; items: WorkforcePlan[] }>('/api/recruitment/workforce-planning', { params: { year, status } }).then(r => r.data),

  get: (id: string) =>
    client.get<WorkforcePlan>(`/api/recruitment/workforce-planning/${id}`).then(r => r.data),

  create: (body: { planName: string; planYear: number; departmentId?: string; departmentName?: string; currentHeadcount: number; plannedHeadcount: number; budgetAllocated: number; currencyCode?: string; notes?: string }) =>
    client.post<WorkforcePlan>('/api/recruitment/workforce-planning', body).then(r => r.data),

  updateStatus: (id: string, status: string) =>
    client.patch<WorkforcePlan>(`/api/recruitment/workforce-planning/${id}/status`, { status }).then(r => r.data),

  summary: (year?: number) =>
    client.get<{ totalPlans: number; draft: number; approved: number; inProgress: number; totalGap: number; totalBudget: number }>('/api/recruitment/workforce-planning/summary', { params: { year } }).then(r => r.data),
};

export const interviewsApi = {
  list: (applicationId?: string, status?: string, page = 1) =>
    client.get<{ total: number; items: InterviewSchedule[] }>('/api/recruitment/interviews', { params: { applicationId, status, page } }).then(r => r.data),

  get: (id: string) =>
    client.get<{ interview: InterviewSchedule; feedbacks: InterviewFeedback[] }>(`/api/recruitment/interviews/${id}`).then(r => r.data),

  schedule: (body: { applicationId: string; interviewType: string; interviewerNames: string; scheduledAt: string; durationMinutes: number; mode: string; meetingLink?: string; location?: string }) =>
    client.post<InterviewSchedule>('/api/recruitment/interviews', body).then(r => r.data),

  complete: (id: string, body: { overallRating: number; recommendation: string; feedbackNotes?: string }) =>
    client.patch<InterviewSchedule>(`/api/recruitment/interviews/${id}/complete`, body).then(r => r.data),

  cancel: (id: string) =>
    client.patch<InterviewSchedule>(`/api/recruitment/interviews/${id}/cancel`, {}).then(r => r.data),

  submitFeedback: (id: string, body: { interviewerName?: string; interviewerRole?: string; communicationScore: number; technicalScore: number; cultureFitScore: number; problemSolvingScore: number; leadershipScore: number; overallScore: number; strengths?: string; concerns?: string; notes?: string; recommendation: string }) =>
    client.post<InterviewFeedback>(`/api/recruitment/interviews/${id}/feedback`, body).then(r => r.data),

  getFeedback: (id: string) =>
    client.get<InterviewFeedback[]>(`/api/recruitment/interviews/${id}/feedback`).then(r => r.data),
};

export const assessmentsApi = {
  listTemplates: () =>
    client.get<AssessmentTemplate[]>('/api/recruitment/assessments/templates').then(r => r.data),

  getTemplate: (id: string) =>
    client.get<{ template: AssessmentTemplate; questions: unknown[] }>(`/api/recruitment/assessments/templates/${id}`).then(r => r.data),

  createTemplate: (body: { code: string; title: string; description?: string; assessmentType: string; durationMinutes: number; passingScore: number; isRandomized: boolean; audience?: string }) =>
    client.post<AssessmentTemplate>('/api/recruitment/assessments/templates', body).then(r => r.data),

  list: (applicationId?: string, status?: string) =>
    client.get<{ total: number; items: CandidateAssessment[] }>('/api/recruitment/assessments', { params: { applicationId, status } }).then(r => r.data),

  send: (body: { applicationId: string; templateId: string; expiryDays?: number }) =>
    client.post<CandidateAssessment>('/api/recruitment/assessments/send', body).then(r => r.data),

  recordResult: (id: string, scoreObtained: number) =>
    client.patch<CandidateAssessment>(`/api/recruitment/assessments/${id}/result`, { scoreObtained }).then(r => r.data),
};

export const offersApi = {
  list: (applicationId?: string, status?: string) =>
    client.get<{ total: number; items: OfferLetter[] }>('/api/recruitment/offers', { params: { applicationId, status } }).then(r => r.data),

  get: (id: string) =>
    client.get<{ offer: OfferLetter; approvals: OfferApproval[] }>(`/api/recruitment/offers/${id}`).then(r => r.data),

  create: (body: { applicationId: string; offeredJobTitle: string; offeredDepartment?: string; startDate: string; basicSalary: number; housingAllowance: number; transportAllowance: number; otherAllowances: number; probationMonths: number; contentHtml?: string; responseDeadline?: string }) =>
    client.post<OfferLetter>('/api/recruitment/offers', body).then(r => r.data),

  send: (id: string) =>
    client.patch<OfferLetter>(`/api/recruitment/offers/${id}/send`, {}).then(r => r.data),

  accept: (id: string) =>
    client.patch<OfferLetter>(`/api/recruitment/offers/${id}/accept`, {}).then(r => r.data),

  decline: (id: string, reason: string) =>
    client.patch<OfferLetter>(`/api/recruitment/offers/${id}/decline`, { reason }).then(r => r.data),
};

export const onboardingApi = {
  listChecklists: () =>
    client.get<OnboardingChecklist[]>('/api/recruitment/onboarding/checklists').then(r => r.data),

  listTasks: (params: { employeeId?: string; applicationId?: string; status?: string; page?: number } = {}) =>
    client.get<{ total: number; items: OnboardingTask[] }>('/api/recruitment/onboarding/tasks', { params }).then(r => r.data),

  createTask: (body: { taskTitle: string; taskDescription?: string; category?: string; checklistId?: string; employeeId?: string; applicationId?: string; assignedToName?: string; dueDate?: string; isMandatory: boolean }) =>
    client.post<OnboardingTask>('/api/recruitment/onboarding/tasks', body).then(r => r.data),

  updateStatus: (id: string, status: string, notes?: string) =>
    client.patch<OnboardingTask>(`/api/recruitment/onboarding/tasks/${id}/status`, { status, notes }).then(r => r.data),

  summary: (employeeId?: string, applicationId?: string) =>
    client.get<{ total: number; pending: number; inProgress: number; completed: number; blocked: number; mandatory: number; mandatoryCompleted: number; completionPct: number }>('/api/recruitment/onboarding/summary', { params: { employeeId, applicationId } }).then(r => r.data),
};

export const recruitmentReportsApi = {
  pipelineSummary: () =>
    client.get('/api/recruitment/reports/pipeline-summary').then(r => r.data),

  timeToHire: (year?: number, month?: number) =>
    client.get('/api/recruitment/reports/time-to-hire', { params: { year, month } }).then(r => r.data),

  sourceEffectiveness: () =>
    client.get('/api/recruitment/reports/source-effectiveness').then(r => r.data),

  openPositions: () =>
    client.get('/api/recruitment/reports/open-positions').then(r => r.data),

  aiInsights: () =>
    client.get('/api/recruitment/reports/ai-insights').then(r => r.data),
};

export const applicationsApi = {
  list: (params: { jobOpeningId?: string; stage?: string; status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ items: JobApplication[]; total: number }>('/api/recruitment/applications', { params }).then(r => r.data),

  kanban: (jobOpeningId: string) =>
    client.get<{ stages: KanbanStage[]; rejected: JobApplication[] }>(`/api/recruitment/applications/kanban/${jobOpeningId}`).then(r => r.data),

  get: (id: string) =>
    client.get<ApplicationDetail>(`/api/recruitment/applications/${id}`).then(r => r.data),

  apply: (jobOpeningId: string, candidateId: string, notes?: string) =>
    client.post<JobApplication>('/api/recruitment/applications', { jobOpeningId, candidateId, notes }).then(r => r.data),

  advance: (id: string, notes?: string) =>
    client.post<JobApplication>(`/api/recruitment/applications/${id}/advance`, { notes }).then(r => r.data),

  reject: (id: string, reason: string) =>
    client.post<JobApplication>(`/api/recruitment/applications/${id}/reject`, { reason }).then(r => r.data),

  addNote: (id: string, notes: string) =>
    client.post(`/api/recruitment/applications/${id}/notes`, { notes }).then(r => r.data),

  scheduleInterview: (id: string, body: {
    interviewType: string; interviewerNames: string; scheduledAt: string;
    durationMinutes: number; mode: string; meetingLink: string; location: string;
  }) => client.post<InterviewSchedule>(`/api/recruitment/applications/${id}/interviews`, body).then(r => r.data),

  recordFeedback: (interviewId: string, body: { overallRating: number; recommendation: string; feedbackNotes: string }) =>
    client.post<InterviewSchedule>(`/api/recruitment/applications/interviews/${interviewId}/feedback`, body).then(r => r.data),

  generateOffer: (id: string, body: {
    department: string; startDate: string; basicSalary: number;
    housingAllowance: number; transportAllowance: number; otherAllowances: number; probationMonths: number;
  }) => client.post<OfferLetter>(`/api/recruitment/applications/${id}/offer`, body).then(r => r.data),

  sendOffer: (offerId: string) =>
    client.post<OfferLetter>(`/api/recruitment/applications/offers/${offerId}/send`).then(r => r.data),

  acceptOffer: (offerId: string) =>
    client.post(`/api/recruitment/applications/offers/${offerId}/accept`).then(r => r.data),

  declineOffer: (offerId: string, reason: string) =>
    client.post(`/api/recruitment/applications/offers/${offerId}/decline`, { reason }).then(r => r.data),

  getOfferHtml: (offerId: string) =>
    client.get<string>(`/api/recruitment/applications/offers/${offerId}/html`, { responseType: 'text' }).then(r => r.data),
};

// ── AI recruitment helpers (opt-in, advisory) ─────────────────────────────────

export interface JobDescriptionResult { summary: string; responsibilities: string[]; requirements: string[]; engine: string; }
export interface CandidateScore { candidateId: string; name: string; score: number; recommendation: string; rationale: string; }
export interface ScreeningResult { ranked: CandidateScore[]; engine: string; notes: string[]; }
export interface QuestionCategory { category: string; questions: string[]; }
export interface InterviewQuestionsResult { categories: QuestionCategory[]; engine: string; }

export const recruitmentAiApi = {
  jobDescription: (body: {
    title: string; departmentName?: string; designationTitle?: string;
    employmentType: string; seniorityLevel?: string; countryCode?: string; notes?: string;
  }) => client.post<JobDescriptionResult>('/api/recruitment/ai/job-description', body).then(r => r.data),

  screen: (openingId: string) =>
    client.post<ScreeningResult>('/api/recruitment/ai/screen', { openingId }).then(r => r.data),

  interviewQuestions: (body: { openingId?: string; title?: string; seniorityLevel?: string; notes?: string }) =>
    client.post<InterviewQuestionsResult>('/api/recruitment/ai/interview-questions', body).then(r => r.data),
};
