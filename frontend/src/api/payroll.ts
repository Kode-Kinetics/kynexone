import client from './client';
import type { PagedResult } from './organization';

export interface SalaryStructure {
  id: string;
  companyId: string | null;
  companyName: string | null;
  code: string;
  name: string;
  currency: string;
  effectiveDate: string;
  minGrossSalary: number;
  maxGrossSalary: number;
  minBasicSalary: number;
  maxBasicSalary: number;
  eligibleGradeIds: string[];
  eligibleDesignationIds: string[];
  versionNumber: number;
  previousVersionId: string | null;
  isActive: boolean;
  createdAtUtc: string;
  assignedEmployeeCount: number;
  components: SalaryComponent[];
}

export interface SalaryComponent {
  id: string;
  salaryStructureId: string | null;
  code: string;
  name: string;
  componentType: string;
  calculationType: string;
  amount: number;
  percentage: number;
  isTaxable: boolean;
  isActive: boolean;
}

export interface EmployeeSalaryStructure {
  id: string;
  employeeId: number;
  salaryStructureId: string;
  basicSalary: number;
  housingAllowance: number;
  transportAllowance: number;
  foodAllowance: number;
  mobileAllowance: number;
  otherAllowance: number;
  fixedDeduction: number;
  effectiveDate: string;
  currency: string;
  isActive: boolean;
  createdAtUtc: string;
}

export interface PayrollRun {
  id: string;
  companyId: string | null;
  year: number;
  month: number;
  status: string;
  runType: string;
  totalGrossSalary: number;
  totalDeductions: number;
  totalNetSalary: number;
  totalEmployerStatutoryCost: number;
  employeeCount: number;
  createdAtUtc: string;
  processedAtUtc: string | null;
  lockedAtUtc: string | null;
}

export interface PayrollSlip {
  id: string;
  runId: string;
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  department: string;
  basicSalary: number;
  housingAllowance: number;
  transportAllowance: number;
  otherAllowances: number;
  grossSalary: number;
  deductions: number;
  netSalary: number;
  status: string;
  // Statutory deduction totals (GOSI/GPSSA/GRSIA split)
  employeeStatutoryTotal: number;
  employerStatutoryTotal: number;
  // Deduction breakdown lines (code, name, amount, source)
  deductionLines?: { code: string; name: string; amount: number; source: string }[];
  // Compliance fields (populated during payroll processing)
  loanDeductions: number;
  ytdGross: number;
  ytdDeductions: number;
  ytdNet: number;
  // POD-C3 — why a wage is what it is. All null on a slip processed before proration shipped, and on
  // every employee who was employed for the whole period (factor 1). The register grid renders
  // paidDays/prorationBasis so a halved wage is never an unexplained halving.
  paidFromDate?: string | null;
  paidToDate?: string | null;
  paidDays?: number | null;
  prorationDenominatorDays?: number | null;
  prorationBasis?: string | null;
  prorationFactor?: number | null;
  /** Retro/backdated arrears settled on this slip. Already inside grossSalary/otherAllowances. */
  arrearsAmount?: number | null;
  /** This slip pays the employee's LAST wage month (the POD-C1 settlement handoff). */
  isFinalWageMonth?: boolean;
}

export interface PayrollValidationResult {
  id: string;
  employeeId: number | null;
  severity: string;
  code: string;
  message: string;
  isResolved: boolean;
}

/** One member of a run's resolved population, on whichever of the three lists they landed. */
export interface PayrollPopulationMember {
  employeeId: number;
  code: string;
  name: string;
  /** Present on `excluded` and `notEligible` only. */
  reason?: string;
}

export interface PayrollRunPopulation {
  runId: string;
  runType: string;
  includesRecurringPay: boolean;
  populationMode: string;
  eligibleCount: number;
  includedCount: number;
  excludedCount: number;
  notEligibleCount: number;
  included: PayrollPopulationMember[];
  /** Deliberate hold-outs. The approver must state this count to approve the run. */
  excluded: PayrollPopulationMember[];
  notEligible: PayrollPopulationMember[];
}

/** A blocking compliance error a human consciously cleared, with the reason they gave. */
export interface PayrollValidationOverride {
  id: string;
  code: string;
  employeeId: number | null;
  reason: string;
  overriddenByUserId: string | null;
  overriddenByName: string;
  createdAtUtc: string;
}

export interface PayrollValidationOverrideReport {
  runId: string;
  overrides: PayrollValidationOverride[];
  /** Codes a human is allowed to override at all. */
  overridableCodes: string[];
  /** Codes that can never be overridden — these must be fixed at source. */
  nonOverridableCodes: string[];
}

export interface AuditIntegrityFailure {
  auditLogId: string;
  createdAtUtc: string;
  action: string;
  entityName: string;
  /** unsealed_row · entry_hash_mismatch · sequence_out_of_order · previous_hash_mismatch */
  reason: string;
}

export interface AuditIntegrityReport {
  isValid: boolean;
  checkedEntries: number;
  firstEntryUtc: string | null;
  lastEntryUtc: string | null;
  failures: AuditIntegrityFailure[];
}

/**
 * The void endpoint's elections, all query-string by design — they are things the SYSTEM cannot
 * know and the operator must state. Only the ones a UI can honestly collect are surfaced.
 */
export interface VoidRunOptions {
  /** Book the reversal AND the replacement into the current open month, so a closed month stays closed. */
  priorPeriodAdjustment?: boolean;
  /** yyyy-MM. Only meaningful with priorPeriodAdjustment. */
  adjustmentPeriod?: string;
  /** The operator states that the ERP posting has been handled outside this product. */
  acknowledgeErpPosted?: boolean;
}

function voidParams(o: VoidRunOptions): Record<string, string | boolean> {
  const params: Record<string, string | boolean> = {};
  if (o.priorPeriodAdjustment) params.priorPeriodAdjustment = true;
  if (o.priorPeriodAdjustment && o.adjustmentPeriod) params.adjustmentPeriod = o.adjustmentPeriod;
  if (o.acknowledgeErpPosted) params.acknowledgeErpPosted = true;
  return params;
}

export interface Payslip {
  id: string;
  payrollRunId: string;
  employeeId: number;
  /** Denormalised at run time — the same value printed on the payslip PDF. */
  employeeCode: string;
  /** Denormalised at run time — the same value printed on the payslip PDF. Never blank. */
  employeeName: string;
  payslipNumber: string;
  language: string;
  isPublishedToEss: boolean;
  createdAtUtc: string;
  publishedAtUtc: string | null;
}

export interface PayrollPaymentBatch {
  id: string;
  payrollRunId: string;
  batchNumber: string;
  paymentMethod: string;
  totalAmount: number;
  currency: string;
  status: string;
  wpsStatus: string;
  wpsStatusChangedAtUtc: string | null;
  wpsSubmissionReference: string | null;
  wpsRejectionReason: string | null;
  createdAtUtc: string;
}

export interface PayrollPaymentRecord {
  id: string;
  paymentBatchId: string;
  employeeId: number;
  amount: number;
  iban: string;
  status: string;
  wpsReference: string;
}

export interface PayrollApproval {
  id: string;
  payrollRunId: string;
  approvalLevel: string;
  decision: string;
  notes: string;
  decidedByUserId: string | null;
  decidedAtUtc: string | null;
}

export interface PayrollGLEntry {
  componentCode: string;
  componentName: string;
  glAccount: string;
  glAccountName: string;
  entryType: 'DR' | 'CR';
  amount: number;
}

export interface PayrollGLJournal {
  runId: string;
  period: string;
  currency: string;
  entries: PayrollGLEntry[];
  totalDebits: number;
  totalCredits: number;
  isBalanced: boolean;
}

export interface PayrollVarianceRow {
  employeeId: number;
  employeeName: string;
  employeeCode: string;
  priorGross: number;
  currentGross: number;
  grossDelta: number;
  grossVariancePct: number;
  priorNet: number;
  currentNet: number;
  netDelta: number;
  isVarianceFlag: boolean;
}

export interface PayrollReconciliation {
  runId: string;
  period: string;
  priorPeriod: string | null;
  currentHeadcount: number;
  priorHeadcount: number;
  joinerCount: number;
  leaverCount: number;
  currentTotalGross: number;
  priorTotalGross: number;
  currentTotalNet: number;
  priorTotalNet: number;
  flaggedVariances: number;
  variances: PayrollVarianceRow[];
}

export interface CostCenterAllocationRow {
  costCenterId: string | null;
  costCenterCode: string;
  costCenterName: string;
  employeeCount: number;
  grossSalary: number;
  netSalary: number;
  employerCost: number;
  totalCost: number;
  percentOfTotal: number;
}

export interface CostCenterAllocationReport {
  runId: string;
  period: string;
  currency: string;
  totalEmployees: number;
  totalGross: number;
  totalNet: number;
  totalEmployerCost: number;
  totalCost: number;
  allocations: CostCenterAllocationRow[];
}

export interface FinalSettlementBreakdown {
  component: string;
  amount: number;
}

export interface FinalSettlementResult {
  settlementId: string;
  status: string;
  employeeId: number;
  employeeName: string;
  lastWorkingDay: string;
  currency: string;
  basicSalary: number;
  grossSalary: number;
  proRataSalary: number;
  daysWorkedInMonth: number;
  daysInMonth: number;
  eosbAmount: number;
  totalYears: number;
  terminationReason: string;
  leaveBalanceDays: number;
  leaveEncashment: number;
  noticePeriodDaysShort: number;
  noticePeriodDeduction: number;
  totalPayable: number;
  netPayable: number;
  settlementDueDate: string;
  wageBaseDelta: number;
  wagesPaidByRunId: string | null;
  warnings: string[];
  breakdown: FinalSettlementBreakdown[];
}

export interface FinalSettlementListRow {
  id: string;
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  companyId: string | null;
  offboardingId: string;
  lastWorkingDay: string;
  settlementDueDate: string;
  terminationReason: string;
  status: string;
  currency: string;
  netPayable: number;
  payrollRunId: string | null;
  paymentBatchId: string | null;
  paidAtUtc: string | null;
  isOverdue: boolean;
  overdueDays: number;
}

export interface FinalSettlementListResponse {
  outstandingTotal: number;
  note: string;
  rows: FinalSettlementListRow[];
}

export interface PayrollGroup {
  id: string;
  code: string;
  name: string;
  currency: string;
  isActive: boolean;
}

export interface WPSFileBatch {
  id: string;
  paymentBatchId: string;
  sifFileName: string;
  status: string;
  createdAtUtc: string;
}

export interface PayrollSummary {
  totalRuns: number;
  lockedRuns: number;
  totalEmployeesPaid: number;
  totalGrossYtd: number;
  totalNetYtd: number;
}

// ── Command Center types ──────────────────────────────────────────────────────

export interface PayrollCompany {
  id: string;
  name: string;
  tradeName: string;
  defaultCurrency: string;
  wpsEmployerId: string;
  gosiEmployerId: string;
}

export interface PayrollCompanySummary {
  companyId: string;
  companyName: string;
  tradeName: string;
  currency: string;
  activeEmployees: number;
  employeesWithSalary: number;
  employeesMissingSalary: number;
  salaryCoveragePercent: number;
  payrollRunStatus: string | null;
  grossPayroll: number;
  totalDeductions: number;
  netPayroll: number;
  validationErrors: number;
  validationWarnings: number;
  pendingApprovals: number;
  wpsEmployerId: string;
  gosiEmployerId: string;
  hasPayrollRun: boolean;
}

export interface PayrollOverview {
  year: number;
  month: number;
  totalCompanies: number;
  totalActiveEmployees: number;
  totalGrossPayroll: number;
  totalNetPayroll: number;
  totalValidationErrors: number;
  totalPendingApprovals: number;
  companies: PayrollCompanySummary[];
}

export interface ReadinessStep {
  step: number;
  label: string;
  complete: boolean;
  detail: string;
}

export interface PayrollReadiness {
  year: number;
  month: number;
  companyId: string | null;
  completionPercent: number;
  isReadyForProcessing: boolean;
  totalActiveEmployees: number;
  employeesWithSalary: number;
  salaryCoveragePercent: number;
  validationErrors: number;
  payrollRunStatus: string | null;
  steps: ReadinessStep[];
}

export interface EmployeeSalaryRow {
  id: string;
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  department: string;
  companyId: string | null;
  salaryStructureId: string;
  basicSalary: number;
  housingAllowance: number;
  transportAllowance: number;
  foodAllowance: number;
  mobileAllowance: number;
  otherAllowance: number;
  fixedDeduction: number;
  currency: string;
  effectiveDate: string;
  isActive: boolean;
  createdAtUtc: string;
}

export interface AIInsight {
  id: string;
  tenantId: string;
  module: string;
  insightType: string;
  severity: 'Info' | 'Warning' | 'Critical';
  employeeId: number | null;
  employeeName: string;
  title: string;
  summary: string;
  dataJson: string;
  generatedBy: string;
  isAcknowledged: boolean;
  createdAtUtc: string;
}

export const payrollApi = {
  listRuns: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<PayrollRun>>('/api/payroll/runs', { params }).then((r) => r.data),

  createRun: (year: number, month: number, companyId?: string) =>
    client.post<PayrollRun>('/api/payroll/runs', { year, month, companyId }).then((r) => r.data),

  processRun: (id: string) =>
    client.post<PayrollRun>(`/api/payroll/runs/${id}/process`).then((r) => r.data),

  lockRun: (id: string) =>
    client.post<PayrollRun>(`/api/payroll/runs/${id}/lock`).then((r) => r.data),

  // Hard-delete a Draft (unprocessed) run. Processed/Locked runs must be voided instead.
  deleteRun: (id: string) =>
    client.delete(`/api/payroll/runs/${id}`).then((r) => r.data),

  // The two acknowledgement counts are NOT optional decoration. The backend refuses with 409
  // `excluded_employees_not_acknowledged` / `overridden_errors_not_acknowledged` whenever the run
  // carries a deliberate exclusion or a consciously-overridden compliance error and the approver has
  // not stated the number. Until this signature existed the client sent neither, so the very first
  // run with one exclusion could not be approved from the UI at all. The caller must read the real
  // counts from `runPopulation` / `runValidationOverrides` and have a human affirm them — this is a
  // cash count, so a client that silently echoes the server's number back would be no control.
  approveRun: (
    id: string,
    body: { notes?: string; expectedExcludedCount?: number | null; expectedOverriddenCount?: number | null } = {},
  ) =>
    client
      .post<PayrollRun>(`/api/payroll/runs/${id}/approve`, {
        notes: body.notes,
        expectedExcludedCount: body.expectedExcludedCount ?? null,
        expectedOverriddenCount: body.expectedOverriddenCount ?? null,
      })
      .then((r) => r.data),

  // Who this run pays, who it deliberately does NOT pay, and why. The `excluded` list is what the
  // approver must acknowledge; `EMPLOYEE_EXCLUDED_FROM_RUN` is only a Warning by design and a normal
  // run carries dozens of warnings, so a warning alone is invisible in practice.
  runPopulation: (id: string) =>
    client.get<PayrollRunPopulation>(`/api/payroll/runs/${id}/population`).then((r) => r.data),

  runValidationOverrides: (id: string) =>
    client.get<PayrollValidationOverrideReport>(`/api/payroll/runs/${id}/validation-overrides`).then((r) => r.data),

  /** Clears ONE blocking validation error, durably, with a mandatory reason on the audit chain. */
  resolveValidationResult: (runId: string, resultId: string, reason: string) =>
    client
      .post<{ message?: string }>(`/api/payroll/runs/${runId}/validation/${resultId}/resolve`, { reason })
      .then((r) => r.data),

  /**
   * Reopen a Draft/Processed/PendingFinanceReview/Approved run back to Draft, releasing the loan
   * installments, attendance/leave/overtime impacts and bonuses it had consumed. Refused outright
   * once any Payroll GL row exists — which is exactly the post-Lock case.
   */
  reopenRun: (id: string, reason: string) =>
    client.post<unknown>(`/api/payroll/runs/${id}/reopen`, { reason }).then((r) => r.data),

  /**
   * Soft-delete a run. Never a hard delete: the status goes to Voided, payslips are voided and any
   * posted GL is contra-entered. The reason is mandatory — this is an irreversible financial action.
   */
  voidRun: (id: string, reason: string, options: VoidRunOptions = {}) =>
    client
      .post<unknown>(`/api/payroll/runs/${id}/void`, { notes: reason }, { params: voidParams(options) })
      .then((r) => r.data),

  /**
   * POD-A3 — the tamper-evident PAYROLL audit chain, verified end to end. Admin and the read-only
   * Auditor role only: the roles that create payroll-audit events cannot self-attest their integrity.
   */
  auditIntegrity: () =>
    client.get<AuditIntegrityReport>('/api/payroll/audit/integrity').then((r) => r.data),

  sendBackRun: (id: string, notes?: string) =>
    client.post<PayrollRun>(`/api/payroll/runs/${id}/send-back`, { notes }).then((r) => r.data),

  glJournal: (id: string) =>
    client.get<PayrollGLJournal>(`/api/payroll/runs/${id}/gl-journal`).then((r) => r.data),

  reconciliation: (runId: string) =>
    client.get<PayrollReconciliation>('/api/payroll/reports/reconciliation', { params: { runId } }).then((r) => r.data),

  costCenterAllocation: (runId: string) =>
    client.get<CostCenterAllocationReport>(`/api/payroll/runs/${runId}/cost-center-allocation`).then((r) => r.data),

  // terminationReason is optional: when omitted the backend sources the reason from the
  // employee's EmployeeOffboarding.SeparationType record (Resignation → Art.85 discount,
  // Article80 → forfeiture), defaulting to full-award Termination when nothing is recorded.
  finalSettlement: (employeeId: number, lastWorkingDay: string, noticePeriodDaysShort = 0, terminationReason?: string) =>
    client.post<FinalSettlementResult>('/api/payroll/final-settlement',
      { employeeId, lastWorkingDay, noticePeriodDaysShort, terminationReason: terminationReason || undefined }).then((r) => r.data),

  listFinalSettlements: (params: { employeeId?: number; status?: string } = {}) =>
    client.get<FinalSettlementListResponse>('/api/payroll/final-settlements', { params }).then((r) => r.data),

  getFinalSettlement: (id: string) =>
    client.get<FinalSettlementResult>(`/api/payroll/final-settlements/${id}`).then((r) => r.data),

  submitFinalSettlement: (id: string, reason?: string) =>
    client.post<{ settlementId: string; status: string }>(`/api/payroll/final-settlements/${id}/submit`, { reason }).then((r) => r.data),

  approveFinalSettlement: (id: string, body: {
    confirmTerminationReason: string;
    acknowledgeWageBaseFloor?: boolean;
    acknowledgeWagesUnpaid?: boolean;
    wagesUnpaidReason?: string;
    acknowledgeSelfApproval?: boolean;
    accrualDate?: string;
    reason?: string;
  }) => client.post<{ settlementId: string; status: string }>(`/api/payroll/final-settlements/${id}/approve`, body).then((r) => r.data),

  cancelFinalSettlement: (id: string, reason: string) =>
    client.post<{ settlementId: string; status: string }>(`/api/payroll/final-settlements/${id}/cancel`, { reason }).then((r) => r.data),

  slips: (runId: string, params: { page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<PayrollSlip>>(`/api/payroll/runs/${runId}/slips`, { params }).then((r) => r.data),

  validateRun: (id: string) =>
    client.post<PayrollValidationResult[]>(`/api/payroll/runs/${id}/validate`).then((r) => r.data),

  listValidationResults: (runId: string) =>
    client.get<PayrollValidationResult[]>(`/api/payroll/runs/${runId}/validate`).then((r) => r.data),

  runApprovals: (runId: string) =>
    client.get<PayrollApproval[]>(`/api/payroll/runs/${runId}/approvals`).then((r) => r.data),

  generatePayslips: (id: string) =>
    client.post<Payslip[]>(`/api/payroll/runs/${id}/payslips/generate`).then((r) => r.data),

  listPayslips: (runId: string, params: { page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<Payslip>>(`/api/payroll/runs/${runId}/payslips`, { params }).then((r) => r.data),

  downloadSlipPdf: (payslipId: string, filename?: string) =>
    client.get(`/api/payroll/slips/${payslipId}/pdf`, { responseType: 'blob' }).then((r) => {
      const url = URL.createObjectURL(new Blob([r.data], { type: 'application/pdf' }));
      const a = document.createElement('a');
      a.href = url;
      a.download = filename ?? `payslip-${payslipId}.pdf`;
      a.click();
      URL.revokeObjectURL(url);
    }),

  downloadRunPdfBundle: (runId: string, period: string) =>
    client.get(`/api/payroll/runs/${runId}/pdf-bundle`, { responseType: 'blob' }).then((r) => {
      const url = URL.createObjectURL(new Blob([r.data], { type: 'application/zip' }));
      const a = document.createElement('a');
      a.href = url;
      a.download = `payslips-${period}.zip`;
      a.click();
      URL.revokeObjectURL(url);
    }),

  // currency omitted by default so the backend resolves it from the tenant's company currency.
  // Pass an explicit currency only to override.
  createPaymentBatch: (runId: string, paymentMethod = 'WPS', currency?: string) =>
    client.post<PayrollPaymentBatch>(`/api/payroll/runs/${runId}/payment-batches`, { paymentMethod, ...(currency ? { currency } : {}) }).then((r) => r.data),

  listPaymentBatches: (runId?: string) =>
    client.get<PayrollPaymentBatch[]>('/api/payroll/payment-batches', { params: runId ? { runId } : undefined }).then((r) => r.data),

  paymentRecords: (batchId: string) =>
    client.get<PayrollPaymentRecord[]>(`/api/payroll/payment-batches/${batchId}/records`).then((r) => r.data),

  generateWpsFile: (batchId: string) =>
    client.post<WPSFileBatch>(`/api/payroll/payment-batches/${batchId}/wps-file`).then((r) => r.data),

  updateWpsStatus: (batchId: string, body: { status: string; reference?: string; notes?: string }) =>
    client.post<{ batchId: string; wpsStatus: string }>(`/api/payroll/payment-batches/${batchId}/wps-status`, body).then((r) => r.data),

  settlePaymentBatch: (batchId: string, body: { reference?: string; paidDate?: string }) =>
    client.post<{ batchId: string; runId: string; wpsStatus: string; settled: number }>(`/api/payroll/payment-batches/${batchId}/settle`, body).then((r) => r.data),

  reversePaymentSettlement: (batchId: string, reason: string) =>
    client.post<{ batchId: string; wpsStatus: string; reversedEntries: number }>(`/api/payroll/payment-batches/${batchId}/settle/reverse`, { reason }).then((r) => r.data),

  remitRun: (runId: string, body: { group: 'GOSI' | 'TAX' | 'LOAN' | 'All'; reference?: string; remitDate?: string }) =>
    client.post(`/api/payroll/runs/${runId}/remit`, body).then((r) => r.data),

  reverseRemittance: (runId: string, body: { group: 'GOSI' | 'TAX' | 'LOAN'; reason: string }) =>
    client.post(`/api/payroll/runs/${runId}/remit/reverse`, body).then((r) => r.data),

  listSalaryStructures: () =>
    client.get<SalaryStructure[]>('/api/payroll/salary-structures').then((r) => r.data),

  getSalaryStructure: (id: string) =>
    client.get<SalaryStructure>(`/api/payroll/salary-structures/${id}`).then((r) => r.data),

  createSalaryStructure: (payload: unknown) =>
    client.post<SalaryStructure>('/api/payroll/salary-structures', payload).then((r) => r.data),

  updateSalaryStructure: (id: string, payload: unknown) =>
    client.put<SalaryStructure>(`/api/payroll/salary-structures/${id}`, payload).then((r) => r.data),

  deleteSalaryStructure: (id: string) =>
    client.delete(`/api/payroll/salary-structures/${id}`).then((r) => r.data),

  listEmployeeSalaryStructures: (employeeId?: number) =>
    client.get<EmployeeSalaryStructure[]>('/api/payroll/employee-salary-structures', { params: employeeId ? { employeeId } : undefined }).then((r) => r.data),

  assignEmployeeSalary: (payload: unknown) =>
    client.post<EmployeeSalaryStructure>('/api/payroll/employee-salary-structures', payload).then((r) => r.data),

  listGroups: () =>
    client.get<PayrollGroup[]>('/api/payroll/groups').then((r) => r.data),

  createGroup: (code: string, name: string, currency = 'AED') =>
    client.post<PayrollGroup>('/api/payroll/groups', { code, name, currency }).then((r) => r.data),

  reportRegister: (runId: string) =>
    client.get<PayrollSlip[]>('/api/payroll/reports/register', { params: { runId } }).then((r) => r.data),

  reportSummary: () =>
    client.get<PayrollSummary>('/api/payroll/reports/summary').then((r) => r.data),

  aiValidation: (runId: string) =>
    client.get('/api/payroll/ai-validation', { params: { runId } }).then((r) => r.data),

  calculateEosb: (employeeId: number, asOfDate?: string) =>
    client.post('/api/payroll/eosb/calculate', { employeeId, asOfDate }).then((r) => r.data),

  listEosb: (employeeId?: number) =>
    client.get('/api/payroll/eosb/list', { params: employeeId ? { employeeId } : undefined }).then((r) => r.data),

  exportRegister: (runId: string) =>
    `/api/payroll/reports/register/export?runId=${runId}`,

  downloadWpsFile: (batchId: string) =>
    `/api/payroll/payment-batches/${batchId}/wps-file/download`,

  // ── Payroll Command Center ────────────────────────────────────────────────
  listCompanies: () =>
    client.get<PayrollCompany[]>('/api/payroll/companies').then((r) => r.data),

  getOverview: (params: { companyId?: string; year?: number; month?: number } = {}) =>
    client.get<PayrollOverview>('/api/payroll/overview', { params }).then((r) => r.data),

  getReadiness: (params: { companyId?: string; year?: number; month?: number } = {}) =>
    client.get<PayrollReadiness>('/api/payroll/readiness', { params }).then((r) => r.data),

  // ── Employee Salary Import / Export ───────────────────────────────────────
  listEmployeeSalaries: (params: { companyId?: string; departmentId?: string; activeOnly?: boolean } = {}) =>
    client.get<EmployeeSalaryRow[]>('/api/payroll/employee-salaries', { params }).then((r) => r.data),

  exportEmployeeSalaries: async (companyId?: string) => {
    const res = await client.get<string>('/api/payroll/employee-salaries/export', {
      responseType: 'text',
      params: companyId ? { companyId } : undefined,
    });
    return res.data;
  },

  employeeSalariesTemplate: async () => {
    const res = await client.get<string>('/api/payroll/employee-salaries/import-template', { responseType: 'text' });
    return res.data;
  },

  importEmployeeSalaries: (csvContent: string) =>
    client.post<{ received: number; created: number; updated: number; skipped: number; errors: string[] }>(
      '/api/payroll/employee-salaries/import', { csvContent }
    ).then((r) => r.data),
};
