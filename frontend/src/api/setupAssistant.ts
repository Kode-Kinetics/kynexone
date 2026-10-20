import client from './client';

export interface SetupSections {
  org: boolean; leave: boolean; shifts: boolean; payroll: boolean; entity: boolean; governance: boolean;
  leavePolicies: boolean; holidays: boolean; attendance: boolean; localization: boolean; benefits?: boolean;
}

export interface CompanyProfile {
  /** Immutable target for a reviewed setup draft. Names are display data, not an authorization boundary. */
  companyId?: string;
  countryCode: string;
  industry: string;
  companySize: string;
  currencyCode: string;
  notes?: string;
  legalEntityName?: string;
  branchCity?: string;
  operatingModel?: string;
  payrollModel?: string;
  approvalModel?: string;
  strictEntityScope: boolean;
  requireCostCenterForPayroll: boolean;
  requireGradeForApprovalPolicy: boolean;
  sections: SetupSections;

  // Operating choices. Each one lands in a column the product already reads; see
  // SetupAssistantContracts.cs for what each drives.
  workPattern?: string;          // SingleDayShift | TwoShifts | ContinuousThreeShifts | FieldRoster
  weekendPattern?: string;       // CountryDefault | Fri-Sat | Sat-Sun | Fri | Sun  (REST days)
  leaveYearBasis?: string;       // Calendar | JoiningDate | Fiscal
  probationMonths?: number;      // 0 = not stated
  noticePeriodDays?: number;     // 0 = not stated
  workforceMix?: string;         // MostlyNational | Mixed | MostlyExpat
  overtimeHandling?: string;     // PaidOvertime | CompensatoryOff | NotApplicable
  attendanceCapture?: string;    // BiometricDevice | MobileGeofence | WebCheckIn | Manual
  payCycle?: string;             // Monthly | SemiMonthly | Biweekly | Weekly
  timeZone?: string;             // IANA zone; empty = derive from country
  defaultLanguage?: string;      // en | ar | bilingual
  configuration?: SetupConfiguration;
}

export interface DraftDepartment { code: string; nameEn: string; }
export interface DraftBranch { code: string; nameEn: string; city: string; isHeadOffice: boolean; }
export interface DraftCostCenter { code: string; name: string; departmentCode: string; }
export interface DraftDesignation { code: string; titleEn: string; departmentCode: string; gradeCode: string; jobLevel: string; isManagerRole: boolean; levelRank: number; }
export interface DraftGrade { code: string; name: string; band: string; level: number; minSalary: number; midSalary: number; maxSalary: number; currency: string; }
export interface DraftGradePayComponent { gradeCode: string; componentCode: string; componentName: string; componentType: string; calculationType: string; amount: number; percentage: number; isTaxable: boolean; frequency: string; }
export interface DraftLeaveType { code: string; nameEn: string; category: string; isPaid: boolean; maxConsecutiveDays: number; requiresAttachment: boolean; colorCode: string; }
export interface DraftShift { code: string; name: string; start: string; end: string; breakMinutes: number; color: string; }
export interface DraftWorkingWeek { workWeek: string; weekStartDay: string; }
export interface DraftPayComponent { code: string; name: string; componentType: string; calculationType: string; amount: number; percentage: number; isTaxable: boolean; }
export interface DraftStatutoryRule { ruleKey: string; ruleValue: string; dataType: string; description: string; }
export interface DraftEmployeeIdRule { companyPrefix: string; useCountryPrefix: boolean; useBranchPrefix: boolean; useDepartmentPrefix: boolean; useYear: boolean; paddingLength: number; nextSequence: number; allowManualOverride: boolean; }
export interface DraftLeavePolicy { name: string; leaveTypeCode: string; annualEntitlementDays: number; accrualMethod: string; encashmentAllowed: boolean; encashmentMaxDays: number; minimumDaysPerRequest: number; maximumDaysPerRequest: number; noticeRequiredDays: number; weekendsIncluded: boolean; publicHolidaysIncluded: boolean; appliesOnProbation: boolean; payrollImpact: string; proratePartialMonths?: boolean; gradeCode?: string; departmentCode?: string; employmentType?: string; }
export interface DraftHoliday { nameEn: string; nameAr: string; date: string; isRecurring: boolean; isOptional: boolean; holidayType: string; notes: string; }
export interface DraftHolidayCalendar { name: string; calendarYear: number; holidays: DraftHoliday[]; }
export interface DraftAttendancePolicy { code: string; name: string; graceMinutes: number; lateThresholdMinutes: number; earlyExitThresholdMinutes: number; halfDayThresholdMinutes: number; absentThresholdMinutes: number; standardWorkMinutes: number; breakMinutes: number; roundingRule: string; requiresOvertimeApproval: boolean; allowAbsenceToLeaveConversion: boolean; }
export interface DraftOvertimeMultiplier { dayCategory: string; multiplier: number; }
export interface DraftOvertimePolicy { code: string; name: string; hourlyRateBasis: string; standardMonthlyHours: number; minimumMinutes: number; maximumMinutesPerDay: number; monthlyCapMinutes: number; roundingRule: string; requiresApproval: boolean; allowCompOffConversion: boolean; multipliers: DraftOvertimeMultiplier[]; }
export interface DraftLocalization { defaultLanguage: string; rtlEnabled: boolean; calendarSystem: string; defaultTimezone: string; dateFormat: string; hijriDatesEnabled: boolean; }
export interface DraftHrConfig { useDeptHeadApproval: boolean; useHrFinalApproval: boolean; useSupervisorBeforeManager: boolean; allowDottedLineApproval: boolean; autoCreateDeptOnImport: boolean; autoCreateDesignationOnImport: boolean; requireImportPreviewBeforeCommit: boolean; allowCrossDeptManager: boolean; allowCrossLocationManager: boolean; requireCostCenterForPayroll: boolean; requireGradeForApprovalPolicy: boolean; }

export interface DraftBenefitPlan {
  code: string; name: string; planType: string; currency: string;
  effectiveFrom: string; effectiveTo?: string | null; requiresEnrollment: boolean; gradeCodes: string[];
}
export interface SetupConfiguration {
  attendanceMethods?: string[];
  attendancePolicy?: DraftAttendancePolicy;
  overtimeModes?: string[];
  overtimePolicy?: DraftOvertimePolicy;
  grades?: DraftGrade[] | null;
  leavePolicies?: DraftLeavePolicy[] | null;
  hrConfig?: DraftHrConfig;
  benefitPlans?: DraftBenefitPlan[] | null;
  policySourceText?: string;
  policyDocumentId?: string;
  policySourceHash?: string;
  /** Draft binder sources; documents remain private until explicitly published. */
  policySources?: PolicyDraftSource[];
  /** Client draft checkpoint; not an applied company setting. */
  policyExtraction?: PolicyExtractionCheckpoint;
  localOnboardingPreparation?: { sourceSignature: string; results: PolicyExtractionResult[] };
  policyIntakeText?: string;
  policyReview?: { drafts: Record<string, PolicyReviewDecision>; history: PolicyReviewRecord[] };
  policyFieldSources?: { documentId: string; contentSha256: string; target: 'configuration' | 'profile'; field: string; sourceStart: number; sourceLength: number; userCompleted?: boolean }[];
  usePolicySourceForAi?: boolean;
}

export interface PolicyDraftSource { documentId: string; contentSha256: string; name: string; }

export interface SetupDraft {
  policySource?: { documentId: string; contentSha256: string } | null;
  policySources?: PolicyDraftSource[] | null;
  policyFieldSources?: SetupConfiguration['policyFieldSources'] | null;
  benefitPlans?: DraftBenefitPlan[];
  branches: DraftBranch[];
  departments: DraftDepartment[];
  costCenters: DraftCostCenter[];
  designations: DraftDesignation[];
  grades: DraftGrade[];
  gradePayComponents: DraftGradePayComponent[];
  leaveTypes: DraftLeaveType[];
  shifts: DraftShift[];
  workingWeek: DraftWorkingWeek | null;
  payComponents: DraftPayComponent[];
  statutoryRules: DraftStatutoryRule[];
  employeeIdRule: DraftEmployeeIdRule | null;
  hrConfig: DraftHrConfig | null;
  leavePolicies: DraftLeavePolicy[];
  holidayCalendar: DraftHolidayCalendar | null;
  attendancePolicy: DraftAttendancePolicy | null;
  overtimePolicy: DraftOvertimePolicy | null;
  localization: DraftLocalization | null;
}

export interface SetupPreviewResult { configurationVersion?: number; draft: SetupDraft; notes: string[]; engine: string; }

const asArray = <T,>(v: unknown): T[] => (Array.isArray(v) ? (v as T[]) : []);

/**
 * Coerce a raw preview response into a render-safe shape. LLM-generated JSON
 * (or backend serialization) can omit/null any list; the UI maps over all of
 * them during render, so a missing array would throw mid-render and blank the
 * page. Every list defaults to []; workingWeek defaults to null.
 */
export function normalizeDraft(raw: unknown): SetupDraft {
  const d = (raw ?? {}) as Partial<SetupDraft>;
  return {
    policySource: d.policySource,
    policySources: d.policySources,
    policyFieldSources: d.policyFieldSources,
    benefitPlans: asArray<DraftBenefitPlan>(d.benefitPlans).map(p => ({ ...p, gradeCodes: asArray<string>(p.gradeCodes) })),
    branches: asArray<DraftBranch>(d.branches),
    departments: asArray<DraftDepartment>(d.departments),
    costCenters: asArray<DraftCostCenter>(d.costCenters),
    designations: asArray<DraftDesignation>(d.designations),
    grades: asArray<DraftGrade>(d.grades),
    gradePayComponents: asArray<DraftGradePayComponent>(d.gradePayComponents),
    leaveTypes: asArray<DraftLeaveType>(d.leaveTypes),
    shifts: asArray<DraftShift>(d.shifts),
    workingWeek: d.workingWeek ?? null,
    payComponents: asArray<DraftPayComponent>(d.payComponents),
    statutoryRules: asArray<DraftStatutoryRule>(d.statutoryRules),
    employeeIdRule: d.employeeIdRule ?? null,
    hrConfig: d.hrConfig ?? null,
    leavePolicies: asArray<DraftLeavePolicy>(d.leavePolicies),
    // The calendar's own holiday list is mapped over during render, so it gets the same
    // treatment as every top-level list: a calendar with a null list must not blank the page.
    holidayCalendar: d.holidayCalendar
      ? { ...d.holidayCalendar, holidays: asArray<DraftHoliday>(d.holidayCalendar.holidays) }
      : null,
    attendancePolicy: d.attendancePolicy ?? null,
    overtimePolicy: d.overtimePolicy
      ? { ...d.overtimePolicy, multipliers: asArray<DraftOvertimeMultiplier>(d.overtimePolicy.multipliers) }
      : null,
    localization: d.localization ?? null,
  };
}

export interface PolicyExtractionProposal { target: 'configuration' | 'profile'; field: string; value: unknown; sourceQuote: string; sourceStart: number; sourceLength: number; userCompleted?: boolean; reviewKey?: string; }
export interface PolicyPartialProposal extends PolicyExtractionProposal { missingFields: string[]; }
export interface PolicyOnboardingTaskProposal {
  id: string; taskTitle: string; taskDescription: string; category: string;
  assignedToName: string | null; dueOffsetDays: number | null; isMandatory: boolean | null; condition: string | null;
  sourceQuote: string; sourceStart: number; sourceLength: number;
}
export interface PolicyExtractionCheckpoint {
  reviewVersion?: number;
  preparedSetupCount?: number;
  sourceSignature: string; setupSignature: string; documentIndex: number; chunkIndex: number; complete: boolean;
  proposals: (PolicyExtractionProposal & { documentId: string; contentSha256: string; sourceName: string })[];
  partials: (PolicyPartialProposal & { documentId: string; sourceHash: string; sourceName?: string })[];
  issues: PolicyExtractionIssue[];
  reviewItems?: PolicyReviewItem[];
  onboardingTaskDrafts?: (PolicyOnboardingTaskProposal & { documentId: string; contentSha256: string; sourceName: string })[];
}
export interface PolicyExtractionIssue { id?: string | null; section: string; kind: 'missing' | 'unsupported' | 'review' | 'info'; message: string; code?: string | null; target?: string | null; field?: string | null; sourceQuote?: string | null; sourceStart?: number | null; sourceLength?: number | null; }
export interface PolicyRequirement { id: string; section: string; summary: string; capability: string; status: string; sourceClassification: string; sourceQuote: string; sourceStart: number; sourceLength: number; target?: string | null; field?: string | null; missingFields?: string[] | null; }
export interface PolicyReviewSource { documentId: string; contentSha256: string; sourceName: string; sourceQuote: string; sourceStart: number; sourceLength: number; }
export interface PolicyReviewItem { id: string; itemType: 'proposal' | 'issue' | 'requirement'; kind: 'supported' | 'incomplete' | 'conflict' | 'unsupported' | 'outOfScope'; section: string; summary: string; sources: PolicyReviewSource[]; target?: string; field?: string; sourceClassification?: string; capability?: string; }
export type PolicyReviewDisposition = 'Reviewed' | 'NeedsClarification' | 'Deferred' | 'ManualProcess' | 'ProductGap' | 'NotApplicable' | 'Answered';
export interface PolicyReviewDecision { disposition: PolicyReviewDisposition | ''; comment: string; owner?: string; approvalReference?: string; }
export interface PolicyReviewRequest extends Omit<PolicyReviewDecision, 'disposition'> { companyId: string; documentId: string; sourceHash: string; itemType: PolicyReviewItem['itemType']; itemKey: string; summary: string; target?: string; field?: string; sourceQuote?: string; sourceStart?: number; sourceLength?: number; disposition: PolicyReviewDisposition; }
export interface PolicyReviewRecord extends PolicyReviewRequest { id: string; reviewedByUserId: string; reviewedAtUtc: string; }
export interface PolicyExtractionResult {
  documentId: string; sourceHash: string; provider: string;
  proposals: PolicyExtractionProposal[];
  partialProposals?: PolicyPartialProposal[];
  issues: PolicyExtractionIssue[];
  requirements?: PolicyRequirement[];
  onboardingTaskDrafts?: PolicyOnboardingTaskProposal[];
  coverage: { section: string; status: string }[];
  message: string;
  progress?: { chunkIndex: number; chunkCount: number; sourceStart: number; sourceLength: number; totalCharacters: number; nextChunkIndex: number | null; isComplete: boolean };
}

export const setupAssistantApi = {
  prepareOnboarding: (documentId: string, profile: CompanyProfile, signal?: AbortSignal) =>
    client.post<PolicyExtractionResult>('/api/setup-assistant/policy/extract', { documentId, useAi: false, profile: serverProfile(profile) }, { signal }).then(r => r.data),
  savePolicyReview: (body: PolicyReviewRequest, signal?: AbortSignal) => client.post<PolicyReviewRecord>('/api/setup-assistant/policy/reviews', body, { signal }).then(r => r.data),
  policyReviewHistory: (companyId: string, documentId: string, signal?: AbortSignal) => client.get<PolicyReviewRecord[]>('/api/setup-assistant/policy/reviews', { params: { companyId, documentId }, signal }).then(r => r.data),
  extractPolicy: (documentId: string, profile: CompanyProfile, signal?: AbortSignal, chunkIndex = 0) =>
    client.post<PolicyExtractionResult>('/api/setup-assistant/policy/extract', { documentId, useAi: true, profile: serverProfile(profile), chunkIndex }, { signal }).then(r => r.data),
  preview: (profile: CompanyProfile) =>
    client.post<SetupPreviewResult>('/api/setup-assistant/preview', serverProfile(profile)).then(r => ({
      draft: normalizeDraft(r.data?.draft),
      notes: asArray<string>(r.data?.notes),
      engine: r.data?.engine ?? '',
      configurationVersion: r.data?.configurationVersion,
    })),

  apply: (draft: SetupDraft, countryCode: string, currencyCode: string, legalEntityName?: string, companyId?: string) =>
    client.post<{ applied: Record<string, number>; total: number; skipped?: Record<string, { count: number; reasonCode: string; reason: string }> }>(
      '/api/setup-assistant/apply', { draft, countryCode, currencyCode, legalEntityName, companyId },
    ).then(r => r.data),
};

/** Browser checkpoints are local review state, not AI context or server configuration. */
function serverProfile(profile: CompanyProfile): CompanyProfile {
  if (!profile.configuration) return profile;
  return { ...profile, configuration: { ...profile.configuration, policyExtraction: undefined, policyIntakeText: undefined, policyReview: undefined } };
}

export interface OrgStructureImportRequest {
  companiesCsv?: string;
  branchesCsv?: string;
  costCentersCsv?: string;
  departmentsCsv?: string;
  gradesCsv?: string;
  gradePayComponentsCsv?: string;
  designationsCsv?: string;
  positionsCsv?: string;
}

export interface OrgStructureImportResult {
  received: number;
  errors: number;
  warnings: number;
  hasBlockingErrors: boolean;
  committed: boolean;
  applied: Record<string, number>;
  rows: { rowNumber: number; entityCode?: string; entityName?: string; status: string; errors: string[]; warnings: string[] }[];
}

/**
 * Governed batch import DTOs — mirror the C# records in
 * OrganizationStructureImportController (MigrationReconciliationDetails,
 * MigrationImportBatchDto). The batch flow (dry-run -> reconciliation -> commit)
 * persists a checksum-keyed batch server-side; the UI drives it instead of the
 * stateless preview()/commit() pair. `result` carries the full grouped findings
 * inline so the client can render blocking/warning detail from one round-trip
 * (backend DTO enrichment §9a). It is optional: until that field ships the panel
 * degrades to error COUNTS only (blockingCount from errorRows).
 */
export interface MigrationReconciliationDetails {
  sectionCounts: Record<string, number>;
  identityCounts: Record<string, number>;
  operationalCounts: Record<string, number>;
  validationErrors: number;
  validationWarnings: number;
}

export interface MigrationImportBatchDto {
  id: string;
  externalBatchId?: string;
  packageType: string;
  status: string;
  packageChecksum: string;
  dryRun: boolean;
  currentSection: string;
  receivedRows: number;
  createdRows: number;
  updatedRows: number;
  skippedRows: number;
  errorRows: number;
  reconciliation: MigrationReconciliationDetails;
  errors: string[];
  result?: OrgStructureImportResult;
  createdAtUtc: string;
  updatedAtUtc?: string;
  completedAtUtc?: string;
}

const BASE = '/api/setup/organization-structure-import';

export const orgStructureImportApi = {
  template: () => client.get<string>(`${BASE}/template`, { responseType: 'text' }).then(r => r.data),
  // Stateless preview/commit remain for existing API clients + backend scope tests;
  // the governed cockpit UI now uses the batch endpoints below.
  preview: (body: OrgStructureImportRequest) =>
    client.post<OrgStructureImportResult>(`${BASE}/preview`, body).then(r => r.data),
  commit: (body: OrgStructureImportRequest) =>
    client.post<OrgStructureImportResult>(`${BASE}/commit`, body).then(r => r.data),
  // Governed batch flow: persists a checksum-keyed batch, returns reconciliation + findings.
  dryRun: (body: OrgStructureImportRequest) =>
    client.post<MigrationImportBatchDto>(`${BASE}/batches/dry-run`, body).then(r => r.data),
  getBatch: (id: string) =>
    client.get<MigrationImportBatchDto>(`${BASE}/batches/${id}/reconciliation`).then(r => r.data),
  commitBatch: (id: string) =>
    client.post<MigrationImportBatchDto>(`${BASE}/batches/${id}/commit`, {}).then(r => r.data),
};
