import client from './client';
import type { LoanRepaymentMethod } from './loans';

export interface LoanPolicyInput {
  companyId: string; loanTypeId: string; policyName: string;
  maxAmount: number; maxTotalOutstanding: number; maxMultiplierOfSalary: number;
  maxInstallmentPercentOfSalary: number; minServiceMonths: number; maxInstallments: number;
  requireProbationCompleted: boolean; blockDuringNotice: boolean; blockOnOverdue: boolean;
  allowedEmploymentStatuses: string[]; allowedContractTypes: string[]; allowedRepaymentMethods: string[];
  allowedRepaymentFrequencies: string[];
  maxConcurrentLoans: number; cooldownMonthsAfterRepayment: number;
  additionalApprovalThreshold: number; additionalApproverRole: string;
  allowExceptions: boolean; allowEarlySettlement: boolean; allowRescheduling: boolean; isActive: boolean;
  /** False = this company does not offer the loan type at all (its employees cannot apply). Defaults to true. */
  isOffered?: boolean;
}
export interface LoanPolicy extends LoanPolicyInput { id: string; version: number; createdAtUtc: string; }
export interface LoanEligibility {
  eligible: boolean; reasons: string[]; codes?: string[]; canRequestException?: boolean; maxAvailableAmount: number | null;
  policyId?: string | null; policyVersion?: number | null; monthlySalary: number | null; committedAmount: number;
  /** True when no amount was sent: only the limits are judged. */
  preview?: boolean;
  /** The employee's company currency; every amount in this response is in it. */
  currency?: string | null;
  /** Strictest of every evaluated limit, as principal. 0 when nothing can be borrowed; null when nothing caps the amount. */
  available?: number | null;
  gradeLimit?: LoanGradeLimitCheck | null; bindingLimit?: LoanBindingLimit | null; limitBreakdowns?: LoanLimitBreakdown[] | null;
}

/** Which of the effective limits is the one that actually caps this request (strictest wins). */
export type LoanBindingLimit = 'GradePerLoan' | 'GradeOutstanding' | 'PolicyMaxAmount' | 'PolicyTotalOutstanding'
  | 'PolicySalaryMultiple' | 'PolicyInstallmentPercent' | 'PolicyConcurrentLoans';

/** How one limit was computed, so the form can explain it: available = cap − outstandingNow, cap = multiple × salaryBasisAmount when salary-based. */
export interface LoanLimitBreakdown {
  /** Which limit this breakdown describes; the form explains the one matching bindingLimit. */
  limit: LoanBindingLimit;
  /** 'Count' only for PolicyConcurrentLoans (a number of loans, not money). */
  basis: 'Amount' | 'MultipleOfBasic' | 'MultipleOfGross' | 'MultipleOfHousing' | 'Count';
  multiple?: number | null;
  salaryBasisAmount?: number | null;
  /** In `unit`: principal, a monthly instalment amount, or a number of loans. */
  cap: number;
  /** Null for a per-loan limit (nothing is subtracted). In `unit`. */
  outstandingNow: number | null;
  /** Principal this limit still allows; null when it does not cap the amount. */
  available: number | null;
  /** Principal for money limits; MonthlyInstalment for PolicyInstallmentPercent (cap/outstandingNow are monthly); Loans for the count. */
  unit?: 'Principal' | 'MonthlyInstalment' | 'Loans';
  /** For MonthlyInstalment: the number of instalments `available` is worked out over. */
  installments?: number | null;
}

// ── Grade loan limits (slice L1) ─────────────────────────────────────────────

/** How a grade cell's per-loan figure is expressed. EligibilityOnly = no per-loan cap (or not eligible). */
export type GradeLimitValueType = 'Amount' | 'MultipleOfBasic' | 'MultipleOfGross' | 'MultipleOfHousing' | 'EligibilityOnly';

/** Grade-limit reason codes the eligibility service can return. Never exceptionable. */
export type GradeLimitReasonCode = 'GradeNotEligible' | 'GradeLimitPerLoan' | 'GradeLimitOutstanding' | 'GradeMissing' | 'GradeLimitNotConfigured' | 'GradeSalaryMissing' | 'GradeLimitCurrencyAmbiguous' | 'GradeHousingInKind';

/** One row of GET /api/finance/loans/grade-limits — one per active grade, ordered by level. */
export interface GradeLoanLimitRow {
  gradeId: string;
  gradeCode: string;
  gradeName: string;
  /** Optional Arabic grade name; fall back to gradeName. */
  gradeNameAr?: string | null;
  level: number;
  /** Null when this grade has no limit in force for the loan type (and company, when given). */
  cellId?: string | null;
  eligible: boolean;
  /** Null when there is no cell in force. */
  valueType: GradeLimitValueType | null;
  /** Fixed per-loan maximum; set only when valueType = Amount. */
  amount: number | null;
  /** Salary multiple (e.g. 3 = three months); set only when valueType = MultipleOfBasic / MultipleOfGross. */
  rate: number | null;
  /** Fixed total-outstanding maximum for this loan type; null = no grade cap on outstanding. */
  maxOutstandingAmount: number | null;
  effectiveFrom: string | null;
  effectiveTo?: string | null;
  /** True when the cell in force is the company's own override rather than the group-wide cell. */
  isCompanyOverride: boolean;
  note?: string | null;
}

/** Response of PUT /api/finance/loans/grade-limits: the grid as of the published effectiveFrom. */
export interface PublishGradeLoanLimitsResponse { changed: number; unchanged: number; rows: GradeLoanLimitRow[]; }

/** One row sent to PUT /api/finance/loans/grade-limits. Only the rows the user changed are sent. */
export interface GradeLoanLimitInput {
  gradeId: string;
  eligible: boolean;
  valueType: GradeLimitValueType;
  amount: number | null;
  rate: number | null;
  maxOutstandingAmount: number | null;
}

export interface PublishGradeLoanLimitsRequest {
  loanTypeId: string;
  /** Null = the group-wide grid (requires group scope); a company id publishes that company's overrides. */
  companyId: string | null;
  effectiveFrom: string;
  rows: GradeLoanLimitInput[];
}

/** A grade the server reports as lacking a limit when grade limiting is switched on. */
export interface GradeMissingLimit { gradeId?: string; gradeCode?: string; gradeName?: string; gradeNameAr?: string | null; level?: number; }

/** The gradeLimit block of GET /api/finance/loans/eligibility. */
export interface LoanGradeLimitCheck {
  applies: boolean;
  eligible: boolean;
  perLoanCap: number | null;
  outstandingCap: number | null;
  outstandingNow: number | null;
  available: number | null;
  reasonCode: GradeLimitReasonCode | null;
  reasonText: string | null;
  /** Optional display name of the grade in force (e.g. "Grade 2"). Shown in the limit card when present. */
  gradeName?: string | null;
  gradeNameAr?: string | null;
  basis?: GradeLimitValueType | null;
  multiple?: number | null;
  salaryBasisAmount?: number | null;
  codes?: string[];
  /** Optional ISO currency of the caps; the tenant currency is used when absent. */
  currency?: string | null;
}

/** GET /api/finance/loans/types/offered — the loan types one employee may apply for. */
export interface OfferedLoanType {
  loanTypeId: string; code: string; nameEn: string; nameAr: string; gradeLimited: boolean;
  offered: boolean; reasonCode: 'LoanTypeNotOffered' | 'InterestNotPermitted' | null; reasonText: string | null;
}

/** GET/PUT /api/finance/loans/offerings — a company's explicit offer / don't-offer decision per loan type. */
export interface LoanTypeOffering {
  loanTypeId: string; code: string; nameEn: string; nameAr: string; gradeLimited: boolean; companyId: string; offered: boolean;
  source: 'CompanyPolicy' | 'GroupPolicy' | 'CompanyNotOffered' | 'NoPolicy' | 'LoanTypeBaseline';
  policyId: string | null; policyVersion: number | null;
  /** The company runs on its own policy while a group policy exists: group changes no longer reach it. */
  detachedFromGroupPolicy?: boolean;
}

/** A company the server lists when enabling grade limits would stop it offering the loan type. */
export interface CompanyWithoutPolicy { id: string; name: string; }

export const loanOfferingsApi = {
  offeredTypes: (employeeIntId?: number) =>
    client.get<OfferedLoanType[]>('/api/finance/loans/types/offered', { params: employeeIntId ? { employeeIntId } : {} }).then(r => r.data),
  list: (companyId: string) => client.get<LoanTypeOffering[]>('/api/finance/loans/offerings', { params: { companyId } }).then(r => r.data),
  set: (body: { companyId: string; loanTypeId: string; offered: boolean }) =>
    client.put<LoanTypeOffering>('/api/finance/loans/offerings', body).then(r => r.data),
};

export const gradeLoanLimitsApi = {
  list: (params: { loanTypeId: string; companyId?: string; asOf?: string }, signal?: AbortSignal) =>
    client.get<GradeLoanLimitRow[]>('/api/finance/loans/grade-limits', { params, signal }).then(r => r.data),
  publish: (body: PublishGradeLoanLimitsRequest) =>
    client.put<PublishGradeLoanLimitsResponse>('/api/finance/loans/grade-limits', body).then(r => r.data),
  /** confirmStopOffering: HR has seen the companies without a policy and accepts that they stop offering the type. */
  setGradeLimited: (loanTypeId: string, gradeLimited: boolean, confirmStopOffering = false) =>
    client.patch(`/api/finance/loans/types/${loanTypeId}/grade-limited`, { gradeLimited, confirmStopOffering }).then(r => r.data),
};
export interface LoanChangeRequest {
  id: string; loanId: string; changeType: 'Reschedule' | 'PolicyException' | 'CollectionMethod'; status: string;
  reason: string; requestedInstallments?: number; requestedStartDate?: string; exceptionCodes?: string[];
  createdBy?: string; createdAtUtc: string; decisionReason?: string; decidedAtUtc?: string;
  requestedRepaymentMethod?: string; reference?: string;
}
export interface LoanCorrectionRequest {
  id: string; changeType: 'ReceiptReversal' | 'DisbursementReversal'; status: string;
  repaymentId?: string; effectiveDate: string; reference: string; reason: string; createdBy?: string;
  createdAtUtc: string; decisionReason?: string;
}
export const loanGovernanceApi = {
  policies: (params: { companyId?: string; loanTypeId?: string } = {}) => client.get<LoanPolicy[]>('/api/finance/loans/policies', { params }).then(r => r.data),
  createPolicy: (body: LoanPolicyInput) => client.post<LoanPolicy>('/api/finance/loans/policies', body).then(r => r.data),
  eligibility: (params: { employeeIntId?: number; loanTypeId: string; amount: number; installments: number; repaymentMethod: LoanRepaymentMethod }) => client.get<LoanEligibility>('/api/finance/loans/eligibility', { params }).then(r => r.data),
  /** Limits-only preview for a chosen loan type: no amount or instalments, so only the limits (gradeLimit, available, bindingLimit) are meaningful. */
  preview: (params: { employeeIntId?: number; loanTypeId: string }) => client.get<LoanEligibility>('/api/finance/loans/eligibility', { params }).then(r => r.data),
  refreshLifecycle: (id: string) => client.post(`/api/finance/loans/${id}/lifecycle/refresh`).then(r => r.data),
  reviewLifecycle: (id: string, body: { decision: 'Continue' | 'Hold' | 'Cancel'; reason: string }) => client.patch(`/api/finance/loans/${id}/lifecycle/review`, body).then(r => r.data),
  changes: (id: string) => client.get<LoanChangeRequest[]>(`/api/finance/loans/${id}/changes`).then(r => r.data),
  requestChange: (id: string, body: { changeType: 'Reschedule' | 'PolicyException' | 'CollectionMethod'; reason: string; installments?: number; startDate?: string; exceptionCodes?: string[]; repaymentMethod?: string; reconciliationReference?: string; confirmNoPayrollCollection?: boolean }) => client.post<LoanChangeRequest>(`/api/finance/loans/${id}/changes`, body).then(r => r.data),
  decideChange: (id: string, changeId: string, body: { decision: 'Approved' | 'Rejected'; reason: string }) => client.patch(`/api/finance/loans/${id}/changes/${changeId}/decide`, body).then(r => r.data),
  corrections: (id: string) => client.get<LoanCorrectionRequest[]>(`/api/finance/loans/${id}/corrections`).then(r => r.data),
  requestCorrection: (id: string, body: { changeType: 'ReceiptReversal' | 'DisbursementReversal'; repaymentId?: string; effectiveDate: string; reference: string; reason: string }) => client.post<LoanCorrectionRequest>(`/api/finance/loans/${id}/corrections`, body).then(r => r.data),
  decideCorrection: (id: string, changeId: string, body: { decision: 'Approved' | 'Rejected'; reason: string }) => client.patch(`/api/finance/loans/${id}/corrections/${changeId}/decide`, body).then(r => r.data),
};
