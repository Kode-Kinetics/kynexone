import client from './client';
import type { PagedResult } from './organization';

export interface SalaryStructure {
  id: string;
  code: string;
  name: string;
  currency: string;
  effectiveDate: string;
  isActive: boolean;
  createdAtUtc: string;
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
  year: number;
  month: number;
  status: string;
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
}

export interface PayrollValidationResult {
  id: string;
  employeeId: number | null;
  severity: string;
  code: string;
  message: string;
  isResolved: boolean;
}

export interface Payslip {
  id: string;
  payrollRunId: string;
  employeeId: number;
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

export interface FinalSettlementBreakdown {
  component: string;
  amount: number;
}

export interface FinalSettlementResult {
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
  leaveBalanceDays: number;
  leaveEncashment: number;
  noticePeriodDaysShort: number;
  noticePeriodDeduction: number;
  totalPayable: number;
  breakdown: FinalSettlementBreakdown[];
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

  approveRun: (id: string, notes?: string) =>
    client.post<PayrollRun>(`/api/payroll/runs/${id}/approve`, { notes }).then((r) => r.data),

  sendBackRun: (id: string, notes?: string) =>
    client.post<PayrollRun>(`/api/payroll/runs/${id}/send-back`, { notes }).then((r) => r.data),

  glJournal: (id: string) =>
    client.get<PayrollGLJournal>(`/api/payroll/runs/${id}/gl-journal`).then((r) => r.data),

  reconciliation: (runId: string) =>
    client.get<PayrollReconciliation>('/api/payroll/reports/reconciliation', { params: { runId } }).then((r) => r.data),

  finalSettlement: (employeeId: number, lastWorkingDay: string, noticePeriodDaysShort = 0) =>
    client.post<FinalSettlementResult>('/api/payroll/final-settlement', { employeeId, lastWorkingDay, noticePeriodDaysShort }).then((r) => r.data),

  slips: (runId: string, params: { page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<PayrollSlip>>(`/api/payroll/runs/${runId}/slips`, { params }).then((r) => r.data),

  validateRun: (id: string) =>
    client.post<PayrollValidationResult[]>(`/api/payroll/runs/${id}/validate`).then((r) => r.data),

  listValidationResults: (runId: string) =>
    client.get<PayrollValidationResult[]>(`/api/payroll/runs/${runId}/validate`).then((r) => r.data).catch(() => [] as PayrollValidationResult[]),

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

  listSalaryStructures: () =>
    client.get<SalaryStructure[]>('/api/payroll/salary-structures').then((r) => r.data),

  createSalaryStructure: (payload: unknown) =>
    client.post<SalaryStructure>('/api/payroll/salary-structures', payload).then((r) => r.data),

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
