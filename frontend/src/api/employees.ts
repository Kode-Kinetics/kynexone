import client from './client';
import type { PagedResult } from './organization';

export interface OrgChartNodeDto {
  id: number;
  employeeCode: string;
  fullName: string;
  designation: string;
  department: string;
  profilePhotoUrl?: string;
  directReports: OrgChartNodeDto[];
}

export interface ReportingLineDto {
  id: string;
  employeeId: number;
  employeeName: string;
  managerEmployeeId: number;
  managerName: string;
  relationshipType: string;
  effectiveFrom: string;
  effectiveTo?: string;
  isPrimary: boolean;
  isActive: boolean;
}

export interface EmployeeListItem {
  id: number;
  employeeCode: string;
  fullName: string;
  arabicName: string;
  department: string;
  designation: string;
  branch: string;
  managerEmployeeId?: number;
  status: string;
  profileCompletenessScore: number;
  visaExpiryDate?: string;
  passportExpiryDate?: string;
  iqamaNumber: string;
  // Denormalized readiness snapshot (server-computed; display-only, the gate always
  // re-evaluates live). readinessState ∈ "Ready" | "NeedsAttention" | "Blocked".
  readinessState: string;
  activationBlockersCount: number;
}

/** Read-only Ex-Employees archive row (former staff retained for statutory audit). */
export interface ExEmployeeListItem {
  id: number;
  employeeCode: string;
  fullName: string;
  arabicName: string;
  department: string;
  designation: string;
  branch: string;
  lastStatus: string;
  isDeleted: boolean;
  exitDate?: string;
  retentionUntilUtc?: string;
  privacyStatus: string;
}

export interface EmployeePayrollProfileRequest {
  bankName?: string;
  iban?: string;
  accountNumber?: string;
  paymentMethod?: string;
  salaryCurrency?: string;
  payrollGroup?: string;
  salaryStructureReference?: string;
  wpsEligible: boolean;
  eosbEligible: boolean;
  socialInsuranceReference?: string;
  molId?: string;
  bankRoutingCode?: string;
}

export interface EmployeeSalaryBreakdownRequest {
  basicSalary?: number;
  housingAllowance?: number;
  transportAllowance?: number;
  foodAllowance?: number;
  mobileAllowance?: number;
  otherAllowance?: number;
  fixedDeduction?: number;
  salaryStructureCode?: string;
  effectiveDate?: string;
  currency?: string;
}

export interface EmployeeComplianceRecordRequest {
  countryCode: string;
  fieldKey: string;
  fieldLabel: string;
  fieldValue?: string;
  issueDate?: string;
  expiryDate?: string;
  isSensitive: boolean;
  isRequired: boolean;
}

export interface EmployeeCreateRequest {
  employeeCode?: string;
  manualEmployeeCode: boolean;
  englishName: string;
  arabicName?: string;
  preferredName?: string;
  gender: string;
  dateOfBirth?: string;
  nationality?: string;
  maritalStatus?: string;
  personalEmail?: string;
  workEmail?: string;
  mobileNumber?: string;
  profilePhotoUrl?: string;
  companyId?: string;
  branchId?: string;
  departmentId?: string;
  designationId?: string;
  gradeId?: string;
  costCenterId?: string;
  jobTitle?: string;
  reportingManagerEmployeeId?: number;
  secondLevelManagerEmployeeId?: number;
  employmentType?: string;
  contractType?: string;
  joiningDate?: string;
  confirmationDate?: string;
  probationStartDate?: string;
  probationEndDate?: string;
  noticePeriodDays?: number;
  workLocation?: string;
  payrollGroup?: string;
  shiftPolicyCode?: string;
  leavePolicyCode?: string;
  attendancePolicyCode?: string;
  payrollProfile?: EmployeePayrollProfileRequest;
  salaryBreakdown?: EmployeeSalaryBreakdownRequest;
  complianceRecords?: EmployeeComplianceRecordRequest[];
}

export interface EmployeeEntity {
  id: number;
  tenantId?: string;
  employeeCode: string;
  fullName: string;
  englishName: string;
  arabicName: string;
  preferredName: string;
  profilePhotoUrl: string;
  personalEmail: string;
  workEmail: string;
  phone: string;
  gender: string;
  dateOfBirth?: string;
  maritalStatus: string;
  nationality: string;
  countryCode: string;
  companyId?: string;
  branchId?: string;
  departmentId?: string;
  designationId?: string;
  gradeId?: string;
  costCenterId?: string;
  department: string;
  designation: string;
  branch: string;
  workLocation: string;
  managerEmployeeId?: number;
  secondLevelManagerEmployeeId?: number;
  status: string;
  joiningDate?: string;
  confirmationDate?: string;
  probationStartDate?: string;
  probationEndDate?: string;
  contractType: string;
  employmentType: string;
  jobTitle: string;
  grade: string;
  costCenter: string;
  noticePeriodDays?: number;
  payrollProfileCode: string;
  shiftPolicyCode: string;
  leavePolicyCode: string;
  attendancePolicyCode: string;
  salary?: number;
  bankName: string;
  bankIban: string;
  wpsBankDetails: string;
  sponsorName: string;
  passportIssueDate?: string;
  passportNumber: string;
  passportExpiryDate?: string;
  visaIssueDate?: string;
  visaNumber: string;
  visaExpiryDate?: string;
  visaFileNumber: string;
  iqamaNumber: string;
  iqamaExpiryDate?: string;
  muqeemNumber: string;
  gosiReference: string;
  qiwaContractNumber: string;
  emiratesId: string;
  emiratesIdExpiryDate?: string;
  laborCardNumber: string;
  qid: string;
  qidExpiryDate?: string;
  workPermitNumber: string;
  workPermitIssueDate?: string;
  civilId: string;
  civilIdExpiryDate?: string;
  residencyNumber: string;
  residencyIssueDate?: string;
  profileCompletenessScore: number;
}

export interface EmployeeDocument {
  id: string;
  employeeId?: number;
  documentType: string;
  fileName: string;
  contentType: string;
  storageUrl: string;
  isRequired: boolean;
  issueDate?: string;
  expiryDate?: string;
  renewalReminderDate?: string;
  approvalStatus: string;
  versionNumber: number;
  uploadedAtUtc: string;
}

export interface EmployeeHistory {
  id: string;
  eventType: string;
  fieldName: string;
  oldValue: string;
  newValue: string;
  effectiveDate: string;
  reason: string;
  createdAtUtc: string;
}

export interface EmployeeTransferRequest {
  id: string;
  currentBranch: string;
  currentDepartment: string;
  currentDesignation: string;
  newBranch: string;
  newDepartment: string;
  newDesignation: string;
  newManagerEmployeeId?: number;
  effectiveDate: string;
  reason: string;
  status: string;
  createdAtUtc: string;
}

export interface EmployeeDetail extends EmployeeEntity {
  payrollProfile?: EmployeePayrollProfileRequest;
  complianceRecords: EmployeeComplianceRecordRequest[];
  documents: EmployeeDocument[];
  history: EmployeeHistory[];
  transfers: EmployeeTransferRequest[];
}

export interface EmployeeStatusChangeRequest {
  status: string;
  effectiveDate: string;
  reason: string;
}

export interface EmployeeTransferCreateRequest {
  newBranch?: string;
  newDepartment?: string;
  newDesignation?: string;
  newManagerEmployeeId?: number;
  effectiveDate: string;
  reason: string;
}

export interface EmployeeGroupCount {
  name: string;
  count: number;
}

export interface EmployeeHeadcountReport {
  total: number;
  byCompany: EmployeeGroupCount[];
  byDepartment: EmployeeGroupCount[];
  byStatus: EmployeeGroupCount[];
}

export interface EmployeeMissingDocumentsReport {
  employeeId: number;
  employeeCode: string;
  fullName: string;
  missingDocumentTypes: string[];
}

export interface EmployeeExpiringDocument {
  employeeId: number;
  employeeCode: string;
  fullName: string;
  documentType: string;
  expiryDate?: string;
}

// ── Readiness / hard activation gate (server is the single source of truth) ──────
// A `fix` hint tells the UI how to close a gap: an inline field edit, or a document upload.
export type ReadinessFix =
  | { kind: 'field'; target: string }
  | { kind: 'document'; documentType: string };

/** One checklist item, identical shape across GET /readiness and the 422 body. */
export interface ReadinessItem {
  key: string;
  label: string;
  category: string;
  reason?: string;
  jurisdiction?: string;
  gate?: string;
  fix?: ReadinessFix;
}

export interface ReadinessProgress {
  present: number;
  requiredTotal: number;
}

export interface ReadinessPolicy {
  countryCode: string;
  tier: string;
  sources: string[];
}

/**
 * Shape shared by the live readiness endpoint and the activation-blocked 422 body, so the
 * ReadinessChecklist component renders both from one server-authoritative source.
 */
export interface ReadinessView {
  progress: ReadinessProgress;
  policy: ReadinessPolicy;
  blocking: ReadinessItem[];
  payBlocking: ReadinessItem[];
  recommended: ReadinessItem[];
  present?: ReadinessItem[];
  expiringSoon?: ReadinessItem[];
  disclaimer: string;
}

/** GET /api/employees/{id}/readiness */
export interface EmployeeReadiness extends ReadinessView {
  employeeId: number;
  state: string; // Ready | NeedsAttention | Blocked
  score: number;
  present: ReadinessItem[];
  expiringSoon: ReadinessItem[];
}

/** 422 body returned by Activate / ChangeStatus / ApproveDraft / bulk-activate when blocked. */
export interface EmployeeNotActivatable extends ReadinessView {
  error: 'employee_not_activatable';
  employeeId: number;
  message: string;
}

export interface EmployeeImportCreatedIncomplete {
  employeeId: number;
  employeeCode: string;
  name: string;
  blockingCount: number;
}

/** POST /api/employees/import response (leniency + persistent results). */
export interface EmployeeImportResult {
  received: number;
  created: number;
  skipped: number;
  hierarchyLinked?: number;
  payrollProfilesCreated?: number;
  importBatchId: string;
  errors: string[];
  warnings: string[];
  createdIncomplete: EmployeeImportCreatedIncomplete[];
}

export interface EmployeeImportPreviewRow {
  row: number;
  employeeCode: string;
  fullName: string;
  status: string; // "WillCreate" | "Error"
  projectedStatus: string; // "Active" | "Draft" | ""
  blocking: string[];
  recommended: string[];
  errors: string[];
  warnings: string[];
}

/** One aggregated missing/incorrect-field summary across all previewed rows. */
export interface EmployeeImportFieldGap {
  /** Field/checklist key (e.g. "iqamaNumber") when the server supplies it. */
  field?: string;
  /** Human label shown in the popup (e.g. "Iqama (residence permit)"). */
  label: string;
  /** "activate" | "pay" | "recommended" — which readiness tier the gap sits in. */
  gate?: string;
  /** How many previewed rows are missing/incorrect for this field. */
  rowCount: number;
}

/** POST /api/employees/import-preview response (dry-run, persists nothing). */
export interface EmployeeImportPreview {
  received: number;
  wouldCreate: number;
  wouldSkip: number;
  wouldCreateActive: number;
  wouldCreateDraft: number;
  rows: EmployeeImportPreviewRow[];
  /**
   * Optional per-field roll-up of the missing/incorrect details across all rows ("ideally which
   * fields", per the owner's spec). When the server omits it, the toolbar derives the same summary
   * from each row's blocking/recommended lists, so the popup always shows a "most common gaps" strip.
   */
  fieldGaps?: EmployeeImportFieldGap[];
}

export interface BulkActivateResult {
  activated: number[];
  blocked: Array<{ id: number; blocking?: ReadinessItem[]; error?: string }>;
}

/**
 * Returns the structured employee_not_activatable 422 payload when the error is the
 * readiness gate's 422, otherwise null. Use at every Activate / ChangeStatus catch site so
 * the block renders as the itemized checklist rather than a swallowed message string.
 */
export function notActivatableFromError(err: unknown): EmployeeNotActivatable | null {
  const e = err as { response?: { status?: number; data?: { error?: string } } };
  if (e?.response?.status === 422 && e.response.data?.error === 'employee_not_activatable') {
    return e.response.data as EmployeeNotActivatable;
  }
  return null;
}

export const employeesApi = {
  list: (params: { search?: string; status?: string; department?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<EmployeeListItem>>('/api/employees', {
      params: { page: 1, pageSize: 25, ...params },
    }).then((r) => r.data),

  /** Read-only Ex-Employees registry: terminated / archived / offboarded / soft-deleted staff. */
  listExEmployees: (params: { search?: string; status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<ExEmployeeListItem>>('/api/employees/ex-employees', {
      params: { page: 1, pageSize: 25, ...params },
    }).then((r) => r.data),

  get: (id: number) => client.get<EmployeeDetail>(`/api/employees/${id}`).then((r) => r.data),

  create: (data: EmployeeCreateRequest) => client.post<EmployeeDetail>('/api/employees', data).then((r) => r.data),

  /** Sends only the changed fields; sensitive fields (salary, passport, bank…) return 202 and go to an approval workflow. */
  update: (id: number, effectiveDate: string, changes: Record<string, unknown>) =>
    client.put(`/api/employees/${id}`, { effectiveDate, changes }),

  remove: (id: number) => client.delete(`/api/employees/${id}`),

  changeStatus: (id: number, data: EmployeeStatusChangeRequest) =>
    client.patch<EmployeeDetail>(`/api/employees/${id}/status`, data).then((r) => r.data),

  activate: (id: number, data: Omit<EmployeeStatusChangeRequest, 'status'>) =>
    client.post<EmployeeDetail>(`/api/employees/${id}/activate`, { ...data, status: 'Active' }).then((r) => r.data),

  /** Live activation checklist for one employee (server-computed; drives the badge, drawer, and inline 422). */
  readiness: (id: number) =>
    client.get<EmployeeReadiness>(`/api/employees/${id}/readiness`).then((r) => r.data),

  /** Dry-run: projects each row's Active-vs-Draft landing without persisting anything. */
  importPreview: (csvContent: string) =>
    client.post<EmployeeImportPreview>('/api/employees/import-preview', { csvContent }).then((r) => r.data),

  /** Lenient import: creates everything with a name, records gaps, never silently lands Active. */
  import: (csvContent: string) =>
    client.post<EmployeeImportResult>('/api/employees/import', { csvContent }).then((r) => r.data),

  /** Multi-select activation for the "Needs info" worklist; each id passes the same gate. */
  bulkActivate: (employeeIds: number[], reason?: string) =>
    client.post<BulkActivateResult>('/api/employees/bulk-activate', { employeeIds, reason }).then((r) => r.data),

  terminate: (id: number, data: Omit<EmployeeStatusChangeRequest, 'status'>) =>
    client.post<EmployeeDetail>(`/api/employees/${id}/terminate`, { ...data, status: 'Terminated' }).then((r) => r.data),

  uploadDocument: (id: number, metadata: { documentType: string; issueDate?: string; expiryDate?: string; renewalReminderDate?: string; isRequired: boolean; approvalStatus?: string }, file: File) => {
    const form = new FormData();
    form.append('file', file);
    Object.entries(metadata).forEach(([key, value]) => {
      if (value !== undefined && value !== null) form.append(key, String(value));
    });
    return client.post<EmployeeDocument>(`/api/employees/${id}/documents`, form).then((r) => r.data);
  },

  documents: (id: number) => client.get<EmployeeDocument[]>(`/api/employees/${id}/documents`).then((r) => r.data),

  history: (id: number) => client.get<EmployeeHistory[]>(`/api/employees/${id}/history`).then((r) => r.data),

  transfer: (id: number, data: EmployeeTransferCreateRequest) =>
    client.post<EmployeeTransferRequest>(`/api/employees/${id}/transfer`, data).then((r) => r.data),

  reports: {
    headcount: () => client.get<EmployeeHeadcountReport>('/api/employees/reports/headcount').then((r) => r.data),
    expiringDocuments: (days = 60) =>
      client.get<EmployeeExpiringDocument[]>('/api/employees/reports/expiring-documents', { params: { days } }).then((r) => r.data),
    missingDocuments: () =>
      client.get<EmployeeMissingDocumentsReport[]>('/api/employees/reports/missing-documents').then((r) => r.data),
    statusSummary: () =>
      client.get<{ statuses: EmployeeGroupCount[] }>('/api/employees/reports/status-summary').then((r) => r.data),
  },

  orgChart: (rootEmployeeId?: number, maxDepth = 5) =>
    client.get<OrgChartNodeDto[]>('/api/employees/org-chart', { params: { rootEmployeeId, maxDepth } }).then((r) => r.data),

  setManager: (id: number, managerEmployeeId: number | null) =>
    client.put(`/api/employees/${id}/manager`, { managerEmployeeId }).then((r) => r.data),

  reportingLines: (id: number) =>
    client.get<ReportingLineDto[]>(`/api/employees/${id}/reporting-lines`).then((r) => r.data),
};
