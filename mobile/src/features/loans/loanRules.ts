// My loans — pure rules. Import-free so node --test can load it.
//
// Backend: Controllers/Finance/LoansController*.cs (route api/finance/loans).
//   GET  /finance/loans                    → { total, items: EmployeeLoanDto[] } (loans.self = own loans only)
//   GET  /finance/loans/{id}/installments  → LoanInstallment[]
//   GET  /finance/loans/types              → LoanType[] (maxInstallments per type)
//   GET  /finance/loans/types/offered      → [{ loanTypeId, nameEn, nameAr, offered, reasonCode, reasonText }]
//   GET  /finance/loans/policies           → policy versions (allowedRepaymentMethods, maxInstallments)
//   GET  /finance/loans/eligibility        → { eligible, reasons, codes, available, currency, policyId, ... }
//   POST /finance/loans                    ← CreateLoanRequest; 400 { error: 'loan_ineligible', reasons, codes }
// The server decides eligibility. This file only turns its answers into what the screen shows:
// an ineligible type shows the server's reason and never an Apply button.

export interface OfferedLoanType {
  loanTypeId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  gradeLimited: boolean;
  offered: boolean;
  reasonCode: string | null;
  reasonText: string | null;
}

export interface LoanEligibility {
  eligible: boolean;
  reasons: string[];
  codes: string[];
  maxAvailableAmount: number | null;
  available: number | null;
  currency: string | null;
  policyId: string | null;
  preview: boolean;
}

export interface LoanPolicySummary {
  id: string;
  loanTypeId: string;
  maxInstallments: number;
  allowedRepaymentMethods: string[];
}

export interface Loan {
  id: string;
  loanTypeId: string;
  loanTypeName: string;
  loanNumber: string;
  requestedAmount: number;
  approvedAmount: number;
  requestedInstallments: number;
  approvedInstallments: number;
  installmentAmount: number;
  totalRepaid: number;
  outstandingBalance: number;
  status: string;
  rejectionReason: string | null;
  repaymentMethod: string;
  currency: string | null;
  createdAtUtc: string;
}

export interface LoanInstallment {
  id: string;
  installmentNumber: number;
  dueDate: string;
  amountDue: number;
  amountPaid: number;
  status: string;
  paidDate: string | null;
}

export type Tone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

// ---------------------------------------------------------------------------------------------
// Eligibility → what the "Apply" card shows
// ---------------------------------------------------------------------------------------------

export interface Reason {
  code: string | null;
  /** The server's sentence (English). Shown as-is in English; in Arabic when the code is not mapped. */
  text: string;
}

export type ApplyState =
  | { kind: 'checking' }
  | { kind: 'notOffered'; reason: Reason }
  | { kind: 'ineligible'; reasons: Reason[] }
  | { kind: 'eligible'; available: number | null; currency: string | null; maxInstallments: number | null }
  | { kind: 'unknown' };

/**
 * One loan type's card state. `eligibility` is undefined while loading and null when the check
 * failed — either way there is no Apply button until the server has said yes.
 */
export function applyState(
  type: OfferedLoanType,
  eligibility: LoanEligibility | null | undefined,
  maxInstallments: number | null,
): ApplyState {
  if (!type.offered) {
    return {
      kind: 'notOffered',
      reason: { code: type.reasonCode, text: type.reasonText ?? '' },
    };
  }
  if (eligibility === undefined) return { kind: 'checking' };
  if (eligibility === null) return { kind: 'unknown' };
  if (!eligibility.eligible) {
    const reasons = eligibility.reasons.map((text, i) => ({ code: eligibility.codes[i] ?? null, text }));
    // Ineligible with no stated reason is still ineligible: say so rather than offer the button.
    return { kind: 'ineligible', reasons: reasons.length ? reasons : [{ code: null, text: '' }] };
  }
  const available = eligibility.available ?? eligibility.maxAvailableAmount;
  if (available !== null && available !== undefined && available <= 0) {
    return { kind: 'ineligible', reasons: [{ code: 'NothingAvailable', text: '' }] };
  }
  return { kind: 'eligible', available: available ?? null, currency: eligibility.currency, maxInstallments };
}

/** Reason codes with an Arabic sentence in account.loans.reasons.* (the server's text is English only). */
export const TRANSLATED_REASON_CODES: readonly string[] = [
  'InterestNotPermitted', 'LoanTypeNotOffered', 'PolicyInvalid', 'EmploymentStatus', 'Notice',
  'EmploymentDate', 'MinService', 'Probation', 'ContractType', 'RepaymentMethod', 'RepaymentFrequency',
  'Installments', 'InvalidAmount', 'CommitmentCurrency', 'ConcurrentLoans', 'Overdue', 'Cooldown',
  'SalaryCurrency', 'SalaryAffordability', 'AmountLimit', 'GradeNotEligible', 'GradeLimitPerLoan',
  'GradeLimitOutstanding', 'GradeMissing', 'GradeLimitNotConfigured', 'GradeSalaryMissing',
  'GradeLimitCurrencyAmbiguous', 'GradeHousingInKind', 'NothingAvailable',
];

/**
 * How to show one reason. English: the server's own sentence (it carries the figures). Arabic: the
 * translated sentence for a known code, else the server's sentence rather than nothing.
 */
export function reasonDisplay(reason: Reason, language: 'en' | 'ar'): { kind: 'server'; text: string } | { kind: 'code'; code: string } | { kind: 'generic' } {
  const known = reason.code !== null && TRANSLATED_REASON_CODES.includes(reason.code);
  if (language === 'en' && reason.text.trim()) return { kind: 'server', text: reason.text };
  if (known) return { kind: 'code', code: reason.code as string };
  if (reason.text.trim()) return { kind: 'server', text: reason.text };
  return { kind: 'generic' };
}

/**
 * The repayment method to assess and apply with: the policy decides, not the employee. Payroll
 * deduction when the policy allows it, else its first allowed method, else the API default.
 */
export function chooseRepaymentMethod(allowed: readonly string[] | null | undefined): string {
  if (!allowed || allowed.length === 0) return 'BankTransfer';
  return allowed.includes('PayrollDeduction') ? 'PayrollDeduction' : allowed[0];
}

/**
 * The method for a type's eligibility check. Once the server has named the policy it applied
 * (policyId), that policy decides; before that, the type's only policy if there is exactly one.
 */
export function repaymentMethodFor(
  policies: readonly LoanPolicySummary[],
  loanTypeId: string,
  policyId?: string | null,
): { method: string; policy: LoanPolicySummary | null } {
  const named = policyId ? policies.find((p) => p.id === policyId) ?? null : null;
  if (named) return { method: chooseRepaymentMethod(named.allowedRepaymentMethods), policy: named };
  const candidates = policies.filter((p) => p.loanTypeId === loanTypeId);
  if (candidates.length === 1) return { method: chooseRepaymentMethod(candidates[0].allowedRepaymentMethods), policy: candidates[0] };
  return { method: chooseRepaymentMethod(null), policy: null };
}

/** The tighter of the loan type's and the policy's instalment ceilings (the server applies the same min). */
export function installmentCeiling(typeMax: number | null | undefined, policyMax: number | null | undefined): number | null {
  const values = [typeMax, policyMax].filter((v): v is number => typeof v === 'number' && v >= 1 && v <= 600);
  return values.length ? Math.min(...values) : null;
}

// ---------------------------------------------------------------------------------------------
// Application form
// ---------------------------------------------------------------------------------------------

const ARABIC_INDIC = '٠١٢٣٤٥٦٧٨٩';
const EASTERN_ARABIC = '۰۱۲۳۴۵۶۷۸۹';

/** Accepts Arabic-Indic digits, the Arabic decimal separator and thousands commas. Returns null when not a number. */
export function parseAmountInput(raw: string): number | null {
  let s = '';
  for (const ch of raw.trim()) {
    const a = ARABIC_INDIC.indexOf(ch);
    const e = EASTERN_ARABIC.indexOf(ch);
    if (a >= 0) s += String(a);
    else if (e >= 0) s += String(e);
    else if (ch === '٫') s += '.';
    else if (ch === ',' || ch === '٬' || ch === ' ') continue;
    else s += ch;
  }
  if (!/^\d+(\.\d{1,2})?$/.test(s)) return null;
  const n = Number(s);
  return Number.isFinite(n) ? n : null;
}

export type ApplyFieldError = 'required' | 'invalid' | 'aboveLimit' | 'tooSmall' | 'outOfRange';

export interface ApplyDraft {
  amountText: string;
  installments: number;
}

export function validateApplication(
  draft: ApplyDraft,
  limits: { available: number | null; maxInstallments: number | null },
): { ok: boolean; amount: number | null; errors: Partial<Record<'amount' | 'installments', ApplyFieldError>> } {
  const errors: Partial<Record<'amount' | 'installments', ApplyFieldError>> = {};
  const amount = draft.amountText.trim() ? parseAmountInput(draft.amountText) : null;
  if (!draft.amountText.trim()) errors.amount = 'required';
  else if (amount === null || amount <= 0) errors.amount = 'invalid';
  else if (limits.available !== null && amount > limits.available) errors.amount = 'aboveLimit';
  const max = limits.maxInstallments ?? 600;
  if (!Number.isInteger(draft.installments) || draft.installments < 1 || draft.installments > max) {
    errors.installments = 'outOfRange';
  } else if (amount !== null && amount > 0 && amount < draft.installments * 0.01) {
    errors.amount = 'tooSmall';
  }
  return { ok: Object.keys(errors).length === 0, amount, errors };
}

/** amount ÷ instalments, rounded to the halala. An estimate of the request, labelled as such. */
export function estimatedInstallment(amount: number | null, installments: number): number | null {
  if (amount === null || amount <= 0 || !Number.isInteger(installments) || installments < 1) return null;
  return Math.round((amount / installments) * 100) / 100;
}

// ---------------------------------------------------------------------------------------------
// My loans
// ---------------------------------------------------------------------------------------------

const OPEN_STATUSES = ['Pending', 'Approved', 'Active'];

export function isOpenLoan(loan: Pick<Loan, 'status'>): boolean {
  return OPEN_STATUSES.includes(loan.status);
}

export type LoanStatusKey = 'pending' | 'approved' | 'active' | 'settled' | 'closed' | 'rejected' | 'cancelled' | 'unknown';

export function loanStatus(status: string): { key: LoanStatusKey; tone: Tone } {
  switch (status) {
    case 'Pending': return { key: 'pending', tone: 'warning' };
    case 'Approved': return { key: 'approved', tone: 'info' };
    case 'Active': return { key: 'active', tone: 'info' };
    case 'Settled': return { key: 'settled', tone: 'success' };
    case 'Closed': return { key: 'closed', tone: 'success' };
    case 'Rejected': return { key: 'rejected', tone: 'danger' };
    case 'Cancelled': return { key: 'cancelled', tone: 'neutral' };
    default: return { key: 'unknown', tone: 'neutral' };
  }
}

export function installmentStatus(installment: Pick<LoanInstallment, 'status' | 'amountDue' | 'amountPaid'>): { key: 'paid' | 'due' | 'partial' | 'cancelled'; tone: Tone } {
  if (installment.status === 'Cancelled') return { key: 'cancelled', tone: 'neutral' };
  if (installment.status === 'Paid' || (installment.amountDue > 0 && installment.amountPaid >= installment.amountDue)) {
    return { key: 'paid', tone: 'success' };
  }
  if (installment.amountPaid > 0) return { key: 'partial', tone: 'warning' };
  return { key: 'due', tone: 'neutral' };
}

export interface LoanSummary {
  /** The server's outstanding balance — never recomputed here. */
  remaining: number;
  /** The principal the schedule repays: approved amount once decided, else what was asked for. */
  principal: number;
  repaid: number;
  /** 0..1 for the progress bar; null when there is no principal to measure against. */
  progress: number | null;
  next: LoanInstallment | null;
  paidCount: number;
  scheduledCount: number;
}

/** Remaining balance, progress and the next instalment still to pay (earliest due, not paid, not cancelled). */
export function summarizeLoan(loan: Loan, installments: readonly LoanInstallment[]): LoanSummary {
  const principal = loan.approvedAmount > 0 ? loan.approvedAmount : loan.requestedAmount;
  const live = installments.filter((i) => i.status !== 'Cancelled');
  const unpaid = live
    .filter((i) => installmentStatus(i).key !== 'paid')
    .sort((a, b) => a.dueDate.localeCompare(b.dueDate) || a.installmentNumber - b.installmentNumber);
  return {
    remaining: loan.outstandingBalance,
    principal,
    repaid: loan.totalRepaid,
    progress: principal > 0 ? Math.min(1, Math.max(0, loan.totalRepaid / principal)) : null,
    next: unpaid[0] ?? null,
    paidCount: live.length - unpaid.length,
    scheduledCount: live.length,
  };
}

export type LoansAccess = 'ok' | 'moduleOff' | 'forbidden' | 'notLinked' | 'error';

/**
 * A refusal from the loans API → the honest state. 403 feature_not_enabled = the company has the
 * Loans module switched off; 403 otherwise = this login may not see loans.
 */
export function loansAccessFromError(status: number | undefined, code: string | undefined): LoansAccess {
  if (status === 403) return code === 'feature_not_enabled' ? 'moduleOff' : 'forbidden';
  if (status === 400 && code === 'no_linked_employee') return 'notLinked';
  return 'error';
}
