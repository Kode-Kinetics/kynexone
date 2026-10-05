import type {
  GradeLimitReasonCode, GradeLoanLimitInput, GradeLoanLimitRow, GradeMissingLimit,
  LoanBindingLimit, LoanEligibility, LoanLimitBreakdown,
} from '../api/loanGovernance';

/** Limit basis the admin picks per grade. "No per-loan cap" is an empty per-loan figure, not a basis. */
export type GradeLimitBasis = 'Amount' | 'MultipleOfBasic' | 'MultipleOfGross';

/** Eligibility as edited: 'unset' only exists for a grade with no limit in force yet. */
export type GradeEligibilityChoice = 'unset' | 'yes' | 'no';

export interface GradeLimitDraft {
  gradeId: string;
  gradeCode: string;
  gradeName: string;
  level: number;
  hasCell: boolean;
  isCompanyOverride: boolean;
  effectiveFrom: string | null;
  eligible: GradeEligibilityChoice;
  basis: GradeLimitBasis;
  /** Per-loan maximum as typed: an amount for Amount, a multiple for the salary bases. Empty = no per-loan grade cap. */
  perLoan: string;
  /** Total-outstanding maximum as typed (always an amount). Empty = no outstanding grade cap. */
  maxOutstanding: string;
  dirty: boolean;
}

/** Message keys (English source text, translated by t()) for each grade-limit refusal. */
export const gradeReasonKeys: Record<GradeLimitReasonCode, string> = {
  GradeNotEligible: "Your grade isn't eligible for this loan type.",
  GradeLimitPerLoan: 'This amount is above the per-loan maximum for your grade ({perLoanCap}).',
  GradeLimitOutstanding: 'This would take your outstanding balance for this loan type above the maximum for your grade ({outstandingCap}).',
  GradeMissing: "There's no grade on your employee record yet. HR needs to set it before you can apply for this loan type.",
  GradeLimitNotConfigured: "Your loan limit hasn't been set up yet — HR has been notified.",
};
export const gradeReasonFallbackKey = "This request is outside the loan limit for your grade.";

/** Plain-language names for whichever limit caps the request. */
export const bindingLimitKeys: Record<LoanBindingLimit, string> = {
  GradePerLoan: "your grade's per-loan maximum",
  GradeOutstanding: "your grade's total outstanding maximum",
  PolicyMaxAmount: "the company policy's maximum loan amount",
  PolicyTotalOutstanding: "the company policy's total outstanding maximum",
  PolicySalaryMultiple: "the company policy's salary multiple",
  PolicyInstallmentPercent: "the company policy's limit on instalments as a share of salary",
  PolicyConcurrentLoans: "the company policy's maximum number of open loans",
};

/** The code the eligibility service returns when the employee's company has no active policy for the type. */
export const NOT_OFFERED_CODE = 'LoanTypeNotOffered';
export const isLoanTypeNotOffered = (eligibility: Pick<LoanEligibility, 'codes'> | null | undefined) =>
  !!eligibility?.codes?.includes(NOT_OFFERED_CODE);

/** True when grade limits block this request. Grade refusals are never exceptionable. */
export const isGradeBlocked = (eligibility: Pick<LoanEligibility, 'gradeLimit'> | null | undefined) =>
  !!eligibility?.gradeLimit?.applies && !eligibility.gradeLimit.eligible;

/** Replaces {name} placeholders. Unknown placeholders are left as-is so a missing value is visible. */
export function fillTemplate(template: string, values: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (match, key: string) => (key in values ? String(values[key]) : match));
}

export function draftFromRow(row: GradeLoanLimitRow): GradeLimitDraft {
  const hasCell = !!row.cellId;
  const basis: GradeLimitBasis = row.valueType === 'MultipleOfBasic' || row.valueType === 'MultipleOfGross' ? row.valueType : 'Amount';
  const perLoanValue = row.valueType === 'Amount' ? row.amount : basis !== 'Amount' ? row.rate : null;
  return {
    gradeId: row.gradeId, gradeCode: row.gradeCode, gradeName: row.gradeName, level: row.level,
    hasCell, isCompanyOverride: row.isCompanyOverride, effectiveFrom: row.effectiveFrom,
    eligible: !hasCell ? 'unset' : row.eligible ? 'yes' : 'no',
    basis,
    perLoan: hasCell && row.eligible && perLoanValue != null ? String(perLoanValue) : '',
    maxOutstanding: hasCell && row.eligible && row.maxOutstandingAmount != null ? String(row.maxOutstandingAmount) : '',
    dirty: false,
  };
}

/** Builds the PUT row honouring the cell shape rules: not eligible => no figures; empty per-loan => EligibilityOnly. */
export function inputFromDraft(draft: GradeLimitDraft): GradeLoanLimitInput {
  if (draft.eligible !== 'yes') return { gradeId: draft.gradeId, eligible: false, valueType: 'EligibilityOnly', amount: null, rate: null, maxOutstandingAmount: null };
  const perLoan = draft.perLoan.trim() === '' ? null : Number(draft.perLoan);
  const maxOutstanding = draft.maxOutstanding.trim() === '' ? null : Number(draft.maxOutstanding);
  if (perLoan == null) return { gradeId: draft.gradeId, eligible: true, valueType: 'EligibilityOnly', amount: null, rate: null, maxOutstandingAmount: maxOutstanding };
  return draft.basis === 'Amount'
    ? { gradeId: draft.gradeId, eligible: true, valueType: 'Amount', amount: perLoan, rate: null, maxOutstandingAmount: maxOutstanding }
    : { gradeId: draft.gradeId, eligible: true, valueType: draft.basis, amount: null, rate: perLoan, maxOutstandingAmount: maxOutstanding };
}

const decimals = (text: string) => (text.includes('.') ? text.split('.')[1].length : 0);

/** Returns a message key (English source text) for the first problem in a changed row, or null when it can be published. */
export function draftProblem(draft: GradeLimitDraft): string | null {
  if (!draft.dirty) return null;
  if (draft.eligible === 'unset') return 'Choose whether this grade is eligible.';
  if (draft.eligible === 'no') return null;
  const perLoan = draft.perLoan.trim();
  if (perLoan !== '') {
    const value = Number(perLoan);
    if (!Number.isFinite(value) || value <= 0) return 'Per-loan maximum must be more than zero, or left empty for no per-loan limit.';
    if (draft.basis === 'Amount' && decimals(perLoan) > 2) return 'Amounts can have at most two decimal places.';
    if (draft.basis !== 'Amount' && decimals(perLoan) > 4) return 'A salary multiple can have at most four decimal places.';
  }
  const outstanding = draft.maxOutstanding.trim();
  if (outstanding !== '') {
    const value = Number(outstanding);
    if (!Number.isFinite(value) || value <= 0) return 'Total outstanding maximum must be more than zero, or left empty for no limit.';
    if (decimals(outstanding) > 2) return 'Amounts can have at most two decimal places.';
  }
  return null;
}

type DraftValues = Pick<GradeLimitDraft, 'eligible' | 'basis' | 'perLoan' | 'maxOutstanding'>;
const valuesOf = (draft: GradeLimitDraft): DraftValues => ({ eligible: draft.eligible, basis: draft.basis, perLoan: draft.perLoan, maxOutstanding: draft.maxOutstanding });

/** "Same for all grades": copy the source grade's values to every grade. */
export function applySameForAll(drafts: GradeLimitDraft[], sourceGradeId: string): GradeLimitDraft[] {
  const source = drafts.find(d => d.gradeId === sourceGradeId);
  if (!source) return drafts;
  const values = valuesOf(source);
  return drafts.map(d => (d.gradeId === sourceGradeId ? d : { ...d, ...values, dirty: true }));
}

/** "From grade X upward": copy grade X's values to every grade at the same or a higher level. */
export function applyFromGradeUpward(drafts: GradeLimitDraft[], sourceGradeId: string): GradeLimitDraft[] {
  const source = drafts.find(d => d.gradeId === sourceGradeId);
  if (!source) return drafts;
  const values = valuesOf(source);
  return drafts.map(d => (d.gradeId === sourceGradeId || d.level < source.level ? d : { ...d, ...values, dirty: true }));
}

/** Names of grades with no limit in force — they block employees once grade limiting is switched on. */
export const unsetGradeNames = (drafts: GradeLimitDraft[]) => drafts.filter(d => !d.hasCell).map(d => d.gradeName || d.gradeCode);

/** Reads the grades the server listed when it refused to switch grade limiting on. */
export function missingGradesFromError(error: unknown): string[] {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (!data || typeof data !== 'object') return [];
  const list = (data as { missingGrades?: unknown }).missingGrades;
  if (!Array.isArray(list)) return [];
  return list.map(item => (typeof item === 'string' ? item : (item as GradeMissingLimit)?.gradeName || (item as GradeMissingLimit)?.gradeCode || ''))
    .filter((name): name is string => !!name);
}

/** The breakdown of the limit that actually caps the request, when the server sent one. */
export function bindingBreakdown(eligibility: Pick<LoanEligibility, 'bindingLimit' | 'limitBreakdowns'> | null | undefined): LoanLimitBreakdown | null {
  if (!eligibility?.bindingLimit) return null;
  return eligibility.limitBreakdowns?.find(item => item.limit === eligibility.bindingLimit) ?? null;
}

/**
 * Message key + values that explain how the available amount was worked out, e.g.
 * "Eligible up to SAR 18,000 = 2 × basic salary SAR 12,000 − outstanding SAR 6,000".
 * Returns null for limits that are not money (number of open loans).
 */
export function breakdownExplanation(breakdown: LoanLimitBreakdown, money: (n: number) => string): { key: string; values: Record<string, string> } | null {
  if (breakdown.limit === 'PolicyConcurrentLoans') return null;
  const values: Record<string, string> = { available: money(breakdown.available), cap: money(breakdown.cap), outstanding: money(breakdown.outstandingNow) };
  const hasOutstanding = breakdown.outstandingNow > 0;
  if (breakdown.basis !== 'Amount' && breakdown.multiple != null && breakdown.salaryBasisAmount != null) {
    values.multiple = String(breakdown.multiple);
    values.salary = money(breakdown.salaryBasisAmount);
    const salaryWord = breakdown.basis === 'MultipleOfBasic' ? 'basic' : 'gross';
    return { key: hasOutstanding ? `Eligible up to {available} = {multiple} × ${salaryWord} salary {salary} − outstanding {outstanding}` : `Eligible up to {available} = {multiple} × ${salaryWord} salary {salary}`, values };
  }
  return { key: hasOutstanding ? 'Eligible up to {available} = limit {cap} − outstanding {outstanding}' : 'Eligible up to {available} (limit {cap})', values };
}
