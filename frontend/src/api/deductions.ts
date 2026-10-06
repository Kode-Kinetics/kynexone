import client from './client';

/**
 * Deductions statements (Release A slice R3). Read-only views over the deduction lines payroll already persisted:
 * category, legal basis, the Art. 93 half-wage limit, and for loans and advances the balance left. Every endpoint is
 * closed (403 feature_not_enabled) unless the tenant has release_a on.
 */

export type DeductionCategory = 'Statutory' | 'EmployerLoan' | 'SalaryAdvance' | 'Absence' | 'PenaltyOrAdjustment' | 'CourtOrder' | 'Other';
/** NeedsReview: the lines do not add up to the slip, so 'within' cannot be shown. Voided: the run no longer applies. */
export type CapStatus = 'Within' | 'Near' | 'Over' | 'NeedsReview' | 'Voided';

/** One reason a statement is flagged, as sentences in both languages. The UI never shows the code. */
export interface DeductionBlockReason {
  code: string;
  titleEn: string; titleAr: string;
  whyEn: string; whyAr: string;
  fixEn: string; fixAr: string;
  ownerRole: string;
}

export interface DeductionLine {
  componentCode: string;
  label: string;
  category: DeductionCategory;
  amount: number;
  countsTowardCap: boolean;
  /** An i18n key (src/i18n/releaseA/deductions.ts). */
  legalBasisKey: string;
  loanId: string | null;
  loanNumber: string | null;
  loanType: string | null;
  /** The loan type's Arabic name, when the tenant gave one. */
  loanTypeAr: string | null;
  balanceAfter: number | null;
  instalmentsRemaining: number | null;
  instalmentsTotal: number | null;
  /** This instalment ÷ the Art. 92 basis (the loan's wage witness, else the salary structure), 0–100, floored for display. */
  percentOfWage: number | null;
  /** Employer loans: the unrounded instalment is above 10% of that basis (or the basis is unknown). */
  aboveConsentThreshold: boolean | null;
  /** Employer loans: written consent to an instalment above 10% is on file. */
  consentOnFile: boolean | null;
}

export interface DeductionStatement {
  slipId: string;
  runId: string;
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  year: number;
  month: number;
  runStatus: string | null;
  runType: string | null;
  slipStatus: string;
  currency: string;
  grossPay: number;
  /** Absence / loss of pay and unpaid leave on this slip: pay not earned, taken off the wage due. */
  payNotEarned: number;
  otherRunsWageDue: number;
  otherRunsDebt: number;
  /** Other non-voided payroll runs this month: the limit covers the whole month (an employee sees only locked ones). */
  otherRuns: number;
  /** HR only: how many of those runs are still in progress. */
  otherRunsNotFinal: number;
  /** Art. 93 wage due: gross minus pay not earned, over the month's runs (pending legal confirmation). */
  wageDue: number;
  /** Deductions that count toward the Art. 93 limit. */
  debtTotal: number;
  /** 50% of the wage due. */
  capLimit: number;
  /** capLimit − debtTotal; negative when over. */
  headroom: number;
  /** debtTotal ÷ wageDue, 0–100. */
  debtPercentOfWage: number | null;
  capStatus: CapStatus;
  notCountedTotal: number;
  /** The payslip's own deductions total; `reconciles` says the lines add up to it. */
  slipDeductionTotal: number;
  reconciles: boolean;
  lines: DeductionLine[];
  flags: string[];
  reasons: DeductionBlockReason[];
}

export interface RunDeductionRow {
  slipId: string;
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  currency: string;
  runStatus: string | null;
  wageDue: number;
  debtTotal: number;
  capLimit: number;
  headroom: number;
  debtPercentOfWage: number | null;
  capStatus: CapStatus;
  /** The slip's lines add up to its deductions total. */
  reconciles: boolean;
  flags: string[];
}

export interface DebtBalance {
  id: string;
  loanNo: string;
  type: string;
  typeAr: string | null;
  currency: string | null;
  outstanding: number;
  instalment: number;
  remaining: number | null;
  instalmentsTotal: number | null;
  nextDueOn: string | null;
  consentOnFile: boolean | null;
  deductedFromPay: boolean;
}

export interface EmployeeDeductions {
  statements: DeductionStatement[];
  balances: { loans: DebtBalance[]; advances: DebtBalance[] };
}

export const deductionsApi = {
  /** HR: one payslip's statement (payroll.read, company scope). */
  slip: (slipId: string) =>
    client.get<DeductionStatement>(`/api/payroll/slips/${slipId}/deduction-statement`).then((r) => r.data),
  /** HR: a run's employees against the limit; nearCap = only those near or over it. */
  run: (runId: string, nearCap?: boolean) =>
    client.get<RunDeductionRow[]>(`/api/payroll/runs/${runId}/deduction-statements`, { params: nearCap ? { nearCap: true } : {} }).then((r) => r.data),
  /** HR: one employee's recent statements and open loans/advances (profile panel). */
  employee: (employeeId: number, months = 6) =>
    client.get<EmployeeDeductions>(`/api/payroll/employees/${employeeId}/deduction-statements`, { params: { months } }).then((r) => r.data),
  /** Employee: their own statements and balances. */
  mine: (months = 6) => client.get<EmployeeDeductions>('/api/ess/deductions', { params: { months } }).then((r) => r.data),
  /** Employee: the deductions on one of their own final payslips. */
  myPayslip: (payslipId: string) =>
    client.get<DeductionStatement>(`/api/ess/payslips/${payslipId}/deductions`).then((r) => r.data),
};
