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
  /**
   * Explicit "create anyway" override for the duplicate-person backstop. When the server detects a
   * STRONG identity match it refuses the create with 409 possible_duplicate; re-sending with this
   * flag set records an audited override and proceeds. Never set silently — only in response to the
   * operator dismissing the duplicate warning (never-silent-dup).
   */
  acknowledgeDuplicate?: boolean;
}

// ── Duplicate-person detection (create pre-check + flagged-row resolution) ─────────────────────
/** One national-identity value the probe checks against stored records (fieldKey → typed column). */
export interface DuplicateIdentityValue {
  fieldKey: string;
  value: string;
}

/**
 * Body for the advisory pre-create duplicate check. POST (not GET) because identity values are
 * sensitive and must not land in a URL. Detection is tenant-wide across companies; the response is
 * scope-masked per match.
 */
export interface DuplicateCheckRequest {
  englishName: string;
  arabicName?: string;
  dateOfBirth?: string;
  nationality?: string;
  companyId?: string;
  identityValues: DuplicateIdentityValue[];
  /** Exclude a known record (e.g. the employee being resolved) from its own match set. */
  excludeEmployeeId?: number;
}

/**
 * One candidate the server believes may be the same human. `canView` is false when the match lives
 * in a company outside the caller's scope — code/name/branch are masked server-side in that case and
 * the UI must show the no-PII "another company" message instead of any details.
 */
export interface DuplicateMatch {
  employeeId: number;
  employeeCode: string;
  fullName: string;
  branch?: string | null;
  companyId?: string | null;
  status: string;
  /** "strong" (exact national-identity match) | "probable" (name + DOB, or passport-only). */
  matchType: string;
  /** Human-readable reasons, e.g. ["Iqama number match", "Same date of birth"]. */
  signals: string[];
  canView: boolean;
}

/** POST /api/employees/duplicate-check response — advisory, always 200, never blocks. */
export interface DuplicateCheckResponse {
  hasStrong: boolean;
  hasProbable: boolean;
  matches: DuplicateMatch[];
}

/** 409 body the authoritative create backstop returns for an unacknowledged STRONG match. */
export interface PossibleDuplicateError {
  error: 'possible_duplicate';
  matches: DuplicateMatch[];
}

/** Body for POST /api/employees/{id}/resolve-duplicate (lightweight resolution). */
export interface ResolveDuplicateRequest {
  /** "distinct" clears the flag (both records survive); "merge" folds this record into a survivor. */
  resolution: 'distinct' | 'merge';
  /** Required for a merge — the surviving record this duplicate is folded into. */
  intoEmployeeId?: number;
  /** Why the operator confirmed distinct / chose to merge (audited). */
  reason?: string;
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

/**
 * One typed org-skeleton / linkage gap persisted against an imported (Draft) row.
 * `type` is the deep-link key (e.g. "link:manager", "org:department", "pay:salaryHeld");
 * `category` ∈ org | link | pay | readiness. Each gap is a People-list filter + deep-link.
 */
export interface EmployeeImportGap {
  type: string;
  category: string;
  detail: string;
}

export interface EmployeeImportCreatedIncomplete {
  employeeId: number;
  employeeCode: string;
  name: string;
  blockingCount: number;
  /** Typed gaps that landed this row as Draft (accept-never-block). Optional for back-compat. */
  gaps?: EmployeeImportGap[];
}

/**
 * POST /api/employees/import response (leniency + persistent results).
 * The `imported`/`receivedTotal`/split-skip/gap counters are the accept-never-block countable
 * summary; all are optional so the toolbar degrades gracefully to the legacy created/skipped shape.
 */
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
  // ── Countable org-skeleton summary (accept-never-block) — all optional ──
  imported?: number;
  receivedTotal?: number;
  /** Total rows dropped (only the lawful drops: no-name + duplicate EmployeeCode). */
  skippedNoName?: number;
  skippedDupCode?: number;
  /** Rows imported as Draft (incomplete) — mirrors createdIncomplete.length. */
  incompleteDraft?: number;
  managersUnresolved?: number;
  supervisorsUnresolved?: number;
  /** Distinct unresolved department / branch names the operator now needs to create. */
  newDepartments?: number;
  newBranches?: number;
  salariesHeld?: number;
  salariesForReview?: number;
  /**
   * Rows imported but flagged as a possible existing person (dup:strong / dup:possible gap).
   * Accept-never-block: they are created as-is, never dropped, never auto-merged — the operator
   * resolves each from the People list (confirm distinct / merge).
   */
  possibleDuplicates?: number;
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

// ── Unified bulk multi-select action (People list) ─────────────────────────────────────────────
/** Which action the bulk endpoint runs. Payroll bulk ops are a SEPARATE later phase — not here. */
export type BulkAction = 'activate' | 'deactivate' | 'delete' | 'export';

/**
 * Server-side "select all matching the current filter" predicate. Mirrors the ACTIVE People-list
 * filters exactly (search + status + readiness + import-gap deep-link) so the resolved set the
 * server acts on is the same set the operator saw across all pages — never just the visible page,
 * never an unfiltered tenant dump. There is deliberately no `department` field (the list UI has no
 * department filter — a predicate the UI never populates would only invite drift).
 */
export interface BulkSelectAllFilter {
  search?: string;
  status?: string;
  readiness?: string;
  importBatchId?: string;
  gapType?: string;
}

/**
 * POST /api/employees/bulk body. `selectionMode` is an EXPLICIT discriminator (never inferred):
 *  - "ids"         → act on `employeeIds` (page-level picks). Ids outside the caller's scope are
 *                    reported skipped:forbidden, never acted on.
 *  - "allMatching" → act on the whole server-resolved filtered set. For destructive actions
 *                    (delete/deactivate) `expectedCount` (the total the user saw) is REQUIRED and the
 *                    server returns 409 selection_changed if the resolved count differs.
 */
export interface BulkActionRequest {
  action: BulkAction;
  selectionMode: 'ids' | 'allMatching';
  employeeIds?: number[];
  filter?: BulkSelectAllFilter;
  reason?: string;
  /** Deactivate only — allow-list {Suspended, Inactive}; defaults to Suspended server-side. */
  targetStatus?: string;
  /** Required for a destructive select-all-matching action; the server reconciles it vs the live count. */
  expectedCount?: number;
}

/** One per-employee outcome from a bulk action (never a whole-batch pass/fail). */
export interface BulkItemOutcome {
  employeeId: number;
  employeeCode: string;
  fullName: string;
  outcome: 'succeeded' | 'skipped' | 'failed';
  /** Machine code: already_active, incomplete, forbidden, establishment_budget_exceeded, not_found, … */
  reason?: string | null;
  /** Readiness checklist items for a floor-skip (reason === "incomplete") — drives the inline checklist. */
  blocking?: ReadinessItem[] | null;
}

export interface BulkActionResult {
  action: BulkAction;
  requested: number;
  succeeded: number;
  skipped: number;
  failed: number;
  items: BulkItemOutcome[];
  /** The human summary, e.g. "12 activated, 3 skipped (incomplete: Iqama, GOSI reference), 1 failed". */
  summary: string;
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

/**
 * Returns the structured possible_duplicate 409 payload when the create backstop refuses an
 * unacknowledged STRONG identity match, otherwise null. Use at the create catch site so the
 * server-authoritative match set re-surfaces the same warning the advisory pre-check shows.
 */
export function possibleDuplicateFromError(err: unknown): PossibleDuplicateError | null {
  const e = err as { response?: { status?: number; data?: { error?: string } } };
  if (e?.response?.status === 409 && e.response.data?.error === 'possible_duplicate') {
    return e.response.data as PossibleDuplicateError;
  }
  return null;
}

export const employeesApi = {
  list: (
    params: {
      search?: string;
      status?: string;
      department?: string;
      // Server-side readiness worklist filter (the "Needs info" deep-link). `readiness` ∈
      // Blocked | NeedsAttention | NotReady | Ready. `gapType`/`importBatchId` narrow to a
      // specific typed gap from a specific import batch (e.g. link:manager from batch X).
      readiness?: string;
      gapType?: string;
      importBatchId?: string;
      page?: number;
      pageSize?: number;
    } = {},
  ) =>
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

  /**
   * Advisory pre-create duplicate check the create modal calls before submit. Always returns 200
   * (never blocks); the create commit is the authoritative backstop (409 possible_duplicate).
   */
  duplicateCheck: (body: DuplicateCheckRequest) =>
    client.post<DuplicateCheckResponse>('/api/employees/duplicate-check', body).then((r) => r.data),

  /**
   * Resolve a flagged possible-duplicate: "distinct" clears the flag (both records survive, reason
   * audited); "merge" folds this record into `intoEmployeeId` (soft-removed, audit link preserved).
   */
  resolveDuplicate: (id: number, body: ResolveDuplicateRequest) =>
    client.post(`/api/employees/${id}/resolve-duplicate`, body).then((r) => r.data),

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

  /**
   * Unified People-list bulk action (activate / deactivate / delete). Server is authoritative: it
   * re-resolves the target set within the caller's tenant + data + company scope and returns a
   * per-item summary — floor-blocked activations are SKIPPED (never force-activated), one row's
   * failure never rolls back the others.
   */
  bulk: (body: BulkActionRequest) =>
    client.post<BulkActionResult>('/api/employees/bulk', body).then((r) => r.data),

  /** Export-selected variant of the same endpoint — returns CSV text (respects filters/selection). */
  bulkExport: (body: BulkActionRequest) =>
    client.post<string>('/api/employees/bulk', body, { responseType: 'text' }).then((r) => r.data),

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
