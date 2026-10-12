import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// Learning management (LRN). HR: /api/learning (catalogue, paths, rules, assignments, records, reports).
// Employee: /api/ess/learning (own assignments, content, assessments, transcript, certificates).
// Both are closed (403 feature_not_enabled) unless the tenant has the `learning` opt-in flag on.
// Errors come back as { error, message, details } — see learningError() in lib/learning.ts.
// ─────────────────────────────────────────────────────────────────────────────

export type ContentKind = 'Lesson' | 'Link' | 'Cmi5';
export type CompletionRule = 'Assessment' | 'Cmi5MoveOn' | 'Acknowledgement';
export type MoveOn = 'Passed' | 'Completed' | 'CompletedAndPassed' | 'CompletedOrPassed' | 'NotApplicable';
export type CourseStatus = 'Draft' | 'Published' | 'Retired';
export type PathStatus = 'Draft' | 'Active' | 'Archived';
export type AssignmentStatus = 'Assigned' | 'InProgress' | 'Completed' | 'Cancelled';
export type EnrollmentStatus = 'NotStarted' | 'InProgress' | 'PendingVerification' | 'Completed' | 'Locked' | 'Cancelled';
export type CredentialStatus = 'Active' | 'Expired' | 'Revoked' | 'Superseded';
export type CompletionSource = 'Assessment' | 'Cmi5' | 'Acknowledgement';
export type QuestionKind = 'Single' | 'Multi' | 'TrueFalse';
export type TargetKind = 'Employee' | 'Company' | 'Department' | 'Designation' | 'Grade';
export type NextAction = 'open' | 'take_assessment' | 'acknowledge' | 'locked' | 'blocked' | 'pending_verification' | 'done' | 'closed';

export interface QuestionInput {
  kind: QuestionKind;
  prompt: string;
  promptAr?: string | null;
  options?: Array<{ text: string; textAr?: string | null }>;
  correctOptionIndexes?: number[];
  trueFalseAnswer?: boolean | null;
}

export interface CourseInput {
  code: string;
  title: string;
  titleAr?: string | null;
  description?: string | null;
  descriptionAr?: string | null;
  estimatedMinutes?: number | null;
  contentKind: ContentKind;
  lessonHtml?: string | null;
  linkUrl?: string | null;
  cmi5AuUrl?: string | null;
  cmi5ActivityId?: string | null;
  cmi5MoveOn?: MoveOn | null;
  completionRule: CompletionRule;
  questions?: QuestionInput[] | null;
  passMarkPercent?: number | null;
  maxAttempts?: number | null;
  shuffleQuestions?: boolean;
  validityMonths?: number | null;
  issuesCertificate?: boolean;
  /** cmi5 only: a content-reported completion waits for HR before the certificate is issued (default on with a certificate or validity). */
  requiresHrVerification?: boolean | null;
  version?: number;
}

export interface CourseSummary {
  id: string; code: string; versionNumber: number; status: CourseStatus; title: string; titleAr: string | null;
  contentKind: ContentKind; completionRule: CompletionRule; validityMonths: number | null; issuesCertificate: boolean;
  questionCount: number; publishedAtUtc: string | null; updatedAtUtc: string; version: number;
}

export interface AuthorQuestion {
  id: string; kind: QuestionKind; prompt: string; promptAr: string | null;
  options: Array<{ id: string; text: string; textAr: string | null }>; correctOptionIds: string[];
}

export interface CourseDetail extends CourseSummary {
  description: string | null; descriptionAr: string | null; estimatedMinutes: number | null; lessonHtml: string | null;
  linkUrl: string | null; cmi5AuUrl: string | null; cmi5ActivityId: string | null; cmi5MoveOn: MoveOn | null;
  passMarkPercent: number | null; maxAttempts: number | null; shuffleQuestions: boolean; requiresHrVerification: boolean;
  /** Only for learning.manage. */
  questions: AuthorQuestion[] | null;
  versions: CourseSummary[];
}

export interface Eligibility { companyIds?: string[]; departmentIds?: string[]; designationIds?: string[]; gradeIds?: string[] }

export interface PathItem {
  id: string; sequence: number; courseId: string; courseCode: string; courseTitle: string; courseTitleAr: string | null;
  courseStatus: CourseStatus; hasPublishedVersion: boolean; isRequired: boolean; prerequisiteSequence: number | null;
}

export interface LearningPath {
  id: string; code: string; name: string; nameAr: string | null; description: string | null; status: PathStatus; eligibility: Eligibility;
  effectiveFrom: string | null; effectiveTo: string | null; renewalWindowDays: number; items: PathItem[];
  liveAssignments: number; completedAssignments: number; updatedAtUtc: string; version: number;
}

export interface PathInput {
  code: string; name: string; nameAr?: string | null; description?: string | null; eligibility?: Eligibility;
  effectiveFrom?: string | null; effectiveTo?: string | null; renewalWindowDays?: number;
  items?: Array<{ courseId: string; isRequired: boolean; prerequisiteSequence?: number | null }>; version?: number;
}

export interface Rule {
  id: string; pathId: string; pathName: string; name: string; targetKind: TargetKind; targetId: string; targetName: string;
  dueInDays: number; isMandatory: boolean; isActive: boolean; lastAppliedAtUtc: string | null; createdAtUtc: string;
}

export interface AssignmentOutcome { employeeId: number; employeeName: string; outcome: 'created' | 'skipped' | 'refused'; reasons: string[]; assignmentId: string | null }
export interface BulkResult { created: number; skipped: number; refused: number; results: AssignmentOutcome[] }

export interface EnrollmentItem {
  id: string; sequence: number; courseCode: string; courseVersion: number; title: string; titleAr: string | null; contentKind: ContentKind;
  completionRule: CompletionRule; isRequired: boolean; status: EnrollmentStatus; isBlocked: boolean; blockedByTitle: string | null;
  blockedByTitleAr: string | null; progressPercent: number | null; scorePercent: number | null; attemptsUsed: number; attemptAllowance: number;
  estimatedMinutes: number | null; firstLaunchedAtUtc: string | null; completedAtUtc: string | null; completionSource: CompletionSource | null;
  nextAction: NextAction;
}

export interface AssignmentView {
  id: string; employeeId: number; employeeName: string; employeeCode: string; pathId: string; pathName: string; pathNameAr: string | null;
  status: AssignmentStatus; isMandatory: boolean; dueDate: string | null; isOverdue: boolean; cycle: number; source: string;
  requiredCount: number; requiredCompleted: number; progressPercent: number; assignedAtUtc: string; completedAtUtc: string | null;
  cancelReason: string | null; items: EnrollmentItem[];
  /** Every open required item is a reported completion waiting for HR: shown as such, never as overdue. */
  awaitingVerification: boolean;
}

export interface Credential {
  id: string; certificateNumber: string; verificationCode: string; status: CredentialStatus; employeeId: number; employeeName: string;
  employeeNameAr: string | null; employeeCode: string; courseCode: string; courseVersion: number; courseTitle: string; courseTitleAr: string | null;
  pathName: string; pathNameAr: string | null; completionSource: CompletionSource; scorePercent: number | null; completedOn: string;
  expiresOn: string | null; issuedAtUtc: string; supersedesCredentialId: string | null; supersedeReason: string | null;
  supersededAtUtc: string | null; revokedAtUtc: string | null; revokeReason: string | null; companyName: string | null;
}

export interface AttemptSummary { id: string; attemptNo: number; status: 'InProgress' | 'Passed' | 'Failed'; scorePercent: number | null; passMarkPercent: number; startedAtUtc: string; submittedAtUtc: string | null }

export interface EnrollmentDetail {
  item: EnrollmentItem; assignmentId: string; assignmentStatus: AssignmentStatus; pathName: string; pathNameAr: string | null;
  description: string | null; descriptionAr: string | null; passMarkPercent: number | null; questionCount: number;
  attempts: AttemptSummary[]; openAttemptId: string | null; credentialId: string | null; linkUrl: string | null;
}

export interface LaunchResult { kind: ContentKind; lessonHtml: string | null; linkUrl: string | null; launchUrl: string | null; sessionId: string | null; enrollment: EnrollmentDetail }

export interface ClientQuestion { id: string; kind: QuestionKind; prompt: string; promptAr: string | null; options: Array<{ id: string; text: string; textAr: string | null }> }
export interface Attempt { id: string; attemptNo: number; status: string; questions: ClientQuestion[]; passMarkPercent: number; attemptsUsed: number; attemptAllowance: number; startedAtUtc: string }
export interface AttemptResult {
  attemptId: string; attemptNo: number; status: 'Passed' | 'Failed'; scorePercent: number; passMarkPercent: number; correct: number; total: number;
  attemptsRemaining: number; enrollmentStatus: EnrollmentStatus; credentialId: string | null; certificateNumber: string | null;
}

export interface TranscriptEntry {
  enrollmentId: string; courseCode: string; courseVersion: number; title: string; titleAr: string | null; pathName: string; pathNameAr: string | null;
  cycle: number; completedAtUtc: string; completionSource: CompletionSource; scorePercent: number | null; credentialId: string | null;
  certificateNumber: string | null; credentialStatus: CredentialStatus | null; expiresOn: string | null;
}

export interface MyLearning { assignments: AssignmentView[]; credentials: Credential[]; transcript: TranscriptEntry[] }

export interface StatusRow {
  assignmentId: string; employeeId: number; employeeCode: string; employeeName: string; departmentId: string | null; department: string; pathId: string;
  path: string; cycle: number; status: AssignmentStatus; mandatory: boolean; dueDate: string | null; overdue: boolean; requiredItems: number;
  completedItems: number; progressPercent: number; assignedAtUtc: string; completedAtUtc: string | null;
  /** The person's employee status (a Suspended person stays counted as overdue, labelled). */
  employeeStatus: string; awaitingVerification: boolean;
}
export interface CompletionRow { groupBy: 'path' | 'department'; groupId: string | null; group: string; assigned: number; completed: number; inProgress: number; notStarted: number; overdue: number; completionRatePercent: number }
export interface ExpiringRow { credentialId: string; employeeId: number; employeeCode: string; employeeName: string; courseCode: string; course: string; certificateNumber: string; expiresOn: string; daysLeft: number; renewalAssignmentId: string | null }
export interface Kpi { key: 'assigned' | 'open' | 'completed' | 'overdue' | 'expiring' | 'locked' | 'pending'; value: number; definition: string; drillReport: string; drillFilter: string | null }
export interface Summary { kpis: Kpi[]; completionRatePercent: number; scope: string; asOf: string }

export type ReportName = 'status' | 'overdue' | 'locked' | 'pending' | 'completion' | 'expiring';

const HR = '/api/learning';
const ESS = '/api/ess/learning';

export const learningApi = {
  // Catalogue
  courses: (status?: CourseStatus) => client.get<CourseSummary[]>(`${HR}/courses`, { params: { status } }).then((r) => r.data),
  course: (id: string) => client.get<CourseDetail>(`${HR}/courses/${id}`).then((r) => r.data),
  createCourse: (body: CourseInput) => client.post<CourseDetail>(`${HR}/courses`, body).then((r) => r.data),
  updateCourse: (id: string, body: CourseInput) => client.put<CourseDetail>(`${HR}/courses/${id}`, body).then((r) => r.data),
  publishCourse: (id: string, version?: number) => client.post<CourseDetail>(`${HR}/courses/${id}/publish`, null, { params: { version } }).then((r) => r.data),
  newVersion: (id: string) => client.post<CourseDetail>(`${HR}/courses/${id}/new-version`).then((r) => r.data),
  retireCourse: (id: string) => client.post<CourseDetail>(`${HR}/courses/${id}/retire`).then((r) => r.data),
  paths: () => client.get<LearningPath[]>(`${HR}/paths`).then((r) => r.data),
  createPath: (body: PathInput) => client.post<LearningPath>(`${HR}/paths`, body).then((r) => r.data),
  updatePath: (id: string, body: PathInput) => client.put<LearningPath>(`${HR}/paths/${id}`, body).then((r) => r.data),
  activatePath: (id: string) => client.post<LearningPath>(`${HR}/paths/${id}/activate`).then((r) => r.data),
  archivePath: (id: string) => client.post<LearningPath>(`${HR}/paths/${id}/archive`).then((r) => r.data),
  rules: () => client.get<Rule[]>(`${HR}/rules`).then((r) => r.data),
  createRule: (body: { pathId: string; name?: string; targetKind: TargetKind; targetId: string; dueInDays: number; isMandatory: boolean }) =>
    client.post<{ rule: Rule; applied: BulkResult }>(`${HR}/rules`, body).then((r) => r.data),
  deactivateRule: (id: string) => client.post<Rule>(`${HR}/rules/${id}/deactivate`).then((r) => r.data),

  // Assignments and records
  assign: (body: { pathId: string; targetKind: TargetKind; employeeIds?: number[]; targetId?: string; dueDate?: string | null; dueInDays?: number | null; isMandatory: boolean }) =>
    client.post<BulkResult>(`${HR}/assignments`, body).then((r) => r.data),
  assignments: (params: { pathId?: string; employeeId?: number; status?: string }) =>
    client.get<AssignmentView[]>(`${HR}/assignments`, { params }).then((r) => r.data),
  assignment: (id: string) => client.get<AssignmentView>(`${HR}/assignments/${id}`).then((r) => r.data),
  cancelAssignment: (id: string, reason: string) => client.post<AssignmentView>(`${HR}/assignments/${id}/cancel`, { reason }).then((r) => r.data),
  attempts: (enrollmentId: string) => client.get<AttemptSummary[]>(`${HR}/enrollments/${enrollmentId}/attempts`).then((r) => r.data),
  resetAttempts: (enrollmentId: string, reason: string) =>
    client.post<AssignmentView>(`${HR}/enrollments/${enrollmentId}/reset-attempts`, { reason }).then((r) => r.data),
  credentials: (params: { employeeId?: number; status?: string }) => client.get<Credential[]>(`${HR}/credentials`, { params }).then((r) => r.data),
  credential: (id: string) => client.get<Credential>(`${HR}/credentials/${id}`).then((r) => r.data),
  verify: (number: string, code: string) => client.get<Credential>(`${HR}/credentials/verify`, { params: { number, code } }).then((r) => r.data),
  /** Revocation reopens the learning: `dueDate` (YYYY-MM-DD, today or later) is when it must be completed again. */
  revoke: (id: string, reason: string, dueDate: string) => client.post<Credential>(`${HR}/credentials/${id}/revoke`, { reason, dueDate }).then((r) => r.data),
  /** A content-reported (cmi5) completion waiting for HR: verify issues the certificate; reject sends it back to in progress. */
  verifyCompletion: (enrollmentId: string) => client.post<AssignmentView>(`${HR}/enrollments/${enrollmentId}/verify`).then((r) => r.data),
  rejectCompletion: (enrollmentId: string, reason: string) =>
    client.post<AssignmentView>(`${HR}/enrollments/${enrollmentId}/reject-verification`, { reason }).then((r) => r.data),
  correct: (id: string, body: { reason: string; completedOn?: string | null; scorePercent?: number | null }) =>
    client.post<Credential>(`${HR}/credentials/${id}/correct`, body).then((r) => r.data),

  // Reports
  summary: () => client.get<Summary>(`${HR}/reports/summary`).then((r) => r.data),
  statusReport: (params: { pathId?: string; departmentId?: string; status?: string }) =>
    client.get<StatusRow[]>(`${HR}/reports/status`, { params }).then((r) => r.data),
  overdueReport: (params: { pathId?: string; departmentId?: string } = {}) => client.get<StatusRow[]>(`${HR}/reports/overdue`, { params }).then((r) => r.data),
  lockedReport: () => client.get<StatusRow[]>(`${HR}/reports/locked`).then((r) => r.data),
  pendingReport: () => client.get<StatusRow[]>(`${HR}/reports/pending`).then((r) => r.data),
  completionReport: (groupBy: 'path' | 'department') => client.get<CompletionRow[]>(`${HR}/reports/completion`, { params: { groupBy } }).then((r) => r.data),
  expiringReport: (days: number) => client.get<ExpiringRow[]>(`${HR}/reports/expiring`, { params: { days } }).then((r) => r.data),
  /** The same report as CSV (formula-injection safe on the server). */
  exportCsv: (report: ReportName, params: Record<string, string | number | undefined> = {}) =>
    client.get<Blob>(`${HR}/reports/${report}`, { params: { ...params, format: 'csv' }, responseType: 'blob' }).then((r) => r.data),

  // Employee
  mine: () => client.get<MyLearning>(ESS).then((r) => r.data),
  enrollment: (id: string) => client.get<EnrollmentDetail>(`${ESS}/enrollments/${id}`).then((r) => r.data),
  launch: (id: string, returnUrl?: string) => client.post<LaunchResult>(`${ESS}/enrollments/${id}/launch`, { returnUrl }).then((r) => r.data),
  acknowledge: (id: string) => client.post<EnrollmentDetail>(`${ESS}/enrollments/${id}/acknowledge`).then((r) => r.data),
  startAttempt: (id: string) => client.post<Attempt>(`${ESS}/enrollments/${id}/attempts`).then((r) => r.data),
  submitAttempt: (attemptId: string, answers: Record<string, string[]>) =>
    client.post<AttemptResult>(`${ESS}/attempts/${attemptId}/submit`, { answers }).then((r) => r.data),
  myCredential: (id: string) => client.get<Credential>(`${ESS}/credentials/${id}`).then((r) => r.data),
};
