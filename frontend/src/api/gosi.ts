import client from './client';

// GOSI filing & variance (api/gosi). Read-only: the three reconciliation endpoints only.
// Readiness lives on /saudi-compliance; contribution-rule writes are statutory-override gated
// and deliberately not surfaced here.

export interface GosiComponentBreakdown {
  componentCode: string;
  componentName: string;
  isEmployerContribution: boolean;
  totalAmount: number;
  employeeCount: number;
}

interface GosiTieOut {
  hasStatutoryData: boolean;
  packResolved: boolean;
  packStatusNote: string | null;
  totalEmployeeContrib: number;
  totalEmployerContrib: number;
  totalGosi: number;
  expectedEmployeeContrib: number;
  expectedEmployerContrib: number;
  expectedVsActualEmployeeDelta: number;
  expectedVsActualEmployerDelta: number;
  glPosted: boolean;
  glEmployeeLiability: number | null;
  glEmployerLiability: number | null;
  glEmployeeDelta: number | null;
  glEmployerDelta: number | null;
  branchBreakdown: GosiComponentBreakdown[];
}

export interface GosiPeriodRunSummary {
  runId: string;
  runType: string;
  status: string;
  includesRecurringPay: boolean;
  employeeTotal: number;
  employerTotal: number;
}

export interface GosiPeriodEmployeeRow {
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  classification: string;
  coveredWageBase: number;
  expectedEmployee: number;
  expectedEmployer: number;
  actualEmployee: number;
  actualEmployer: number;
  runCount: number;
  employeeVariance: number;
  employerVariance: number;
  hasVariance: boolean;
}

export interface GosiPeriodSummary extends GosiTieOut {
  period: string;
  companyId: string | null;
  runCount: number;
  runIds: string[];
  runs: GosiPeriodRunSummary[];
  varianceCount: number;
  employees: GosiPeriodEmployeeRow[];
}

export interface GosiRunSummary extends GosiTieOut {
  runId: string;
  period: string;
  siblingRunCount: number;
  expectedIsPeriodPartial: boolean;
  periodScopeNote: string | null;
  slipEmployeeStatutoryTotal: number;
  slipEmployerStatutoryTotal: number;
  runEmployerStatutoryCost: number;
  glEmployeeAccount: string | null;
  glEmployerAccount: string | null;
}

export interface GosiVarianceLine {
  code: string;
  label: string;
  employeeAmount: number;
  employerAmount: number;
}

export interface GosiVarianceRow {
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  classification: string;
  coveredWageBase: number;
  expectedEmployeeContrib: number;
  actualEmployeeContrib: number;
  employeeVariance: number;
  expectedEmployerContrib: number;
  actualEmployerContrib: number;
  employerVariance: number;
  hasVariance: boolean;
  expectedLines: GosiVarianceLine[];
}

export interface GosiVarianceReport {
  runId: string;
  period: string;
  packResolved: boolean;
  packStatusNote: string | null;
  hasStatutoryData: boolean;
  totalEmployees: number;
  withVariance: number;
  siblingRunCount: number;
  expectedIsPeriodPartial: boolean;
  periodScopeNote: string | null;
  rows: GosiVarianceRow[];
}

export const gosiApi = {
  periodSummary: (year: number, month: number, companyId?: string | null) =>
    client.get<GosiPeriodSummary>(`/api/gosi/periods/${year}/${month}/contribution-summary`, {
      params: companyId ? { companyId } : undefined,
    }).then((r) => r.data),
  runSummary: (runId: string) =>
    client.get<GosiRunSummary>(`/api/gosi/payroll-runs/${runId}/contribution-summary`).then((r) => r.data),
  runVariance: (runId: string) =>
    client.get<GosiVarianceReport>(`/api/gosi/payroll-runs/${runId}/variance-report`).then((r) => r.data),
};
