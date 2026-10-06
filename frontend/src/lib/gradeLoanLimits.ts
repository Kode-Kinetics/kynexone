import type {
  CompanyWithoutPolicy, GradeLimitReasonCode, GradeLoanLimitInput, GradeLoanLimitRow, GradeMissingLimit,
  LoanBindingLimit, LoanEligibility, LoanLimitBreakdown,
} from '../api/loanGovernance';

/** Limit basis the admin picks per grade. "No per-loan cap" is an empty per-loan figure, not a basis. */
export type GradeLimitBasis = 'Amount' | 'MultipleOfBasic' | 'MultipleOfGross' | 'MultipleOfHousing';

/** The Facility code a loan type's grade limits are keyed by — the server's GradeLoanLimitResolver.FacilityCodeFor. */
export const facilityCodeFor = (loanTypeCode: string | null | undefined) => {
  const cleaned = (loanTypeCode ?? '').trim().toUpperCase().replace(/[^A-Z0-9]/g, '_').replace(/^_+|_+$/g, '');
  return ('LOAN_' + (cleaned || 'TYPE')).slice(0, 64);
};

/** Release A (R2): × housing allowance is allowed only for the housing-advance facility (EntitlementComponentRules), keyed on
 * the loan type's entitlement component code like the server, never on its display code. */
export const allowsHousingMultiple = (loanType: { code?: string | null; entitlementComponentCode?: string | null } | null | undefined) =>
  !!loanType && (loanType.entitlementComponentCode || facilityCodeFor(loanType.code)).toUpperCase() === 'LOAN_HOUSING_ADVANCE';

/** Eligibility as edited: 'unset' only exists for a grade with no limit in force yet. */
export type GradeEligibilityChoice = 'unset' | 'yes' | 'no';

export interface GradeLimitDraft {
  gradeId: string;
  gradeCode: string;
  gradeName: string;
  gradeNameAr: string | null;
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

/**
 * Message keys (English source text, translated by t()) for each grade-limit refusal, in the applicant's own
 * voice. HR applying on an employee's behalf reads {@link gradeReasonKeysForEmployee} instead.
 */
export const gradeReasonKeys: Record<GradeLimitReasonCode, string> = {
  GradeNotEligible: "Your grade isn't eligible for this loan type.",
  GradeLimitPerLoan: 'This amount is above the per-loan maximum for your grade ({perLoanCap}).',
  GradeLimitOutstanding: 'This would take your outstanding balance for this loan type above the maximum for your grade ({outstandingCap}).',
  GradeMissing: "Your employee record has no grade in use (none is set, or the grade is no longer active). HR needs to update it before you can apply for this loan type.",
  GradeLimitNotConfigured: "Your loan limit hasn't been set up yet — HR has been notified.",
  GradeSalaryMissing: "Your limit is a multiple of your salary, and no current salary is on file. HR needs to complete it before you can apply.",
  GradeLimitCurrencyAmbiguous: "Your grade's loan limit is a fixed amount set for all companies, but the companies pay in different currencies. HR needs to set your company's own limit before you can apply.",
  GradeHousingInKind: "Your housing is provided in kind (accommodation), so there is no housing allowance to advance against.",
};

/** The same refusals, worded for HR applying on an employee's behalf. */
export const gradeReasonKeysForEmployee: Record<GradeLimitReasonCode, string> = {
  GradeNotEligible: "This employee's grade isn't eligible for this loan type.",
  GradeLimitPerLoan: "This amount is above the per-loan maximum for this employee's grade ({perLoanCap}).",
  GradeLimitOutstanding: "This would take the employee's outstanding balance for this loan type above the maximum for their grade ({outstandingCap}).",
  GradeMissing: "This employee's record has no grade in use (none is set, or the grade is no longer active). Update it before applying for this loan type.",
  GradeLimitNotConfigured: "No loan limit is set for this employee's grade yet. Set it in Loan Policies → Limits by grade.",
  GradeSalaryMissing: "This employee's limit is a multiple of salary, and no current salary is on file. Complete it before applying.",
  GradeLimitCurrencyAmbiguous: "This grade's loan limit is a fixed amount set for all companies, but the companies pay in different currencies. Set this employee's company's own limit in Loan Policies \u2192 Limits by grade.",
  GradeHousingInKind: "This employee's housing is provided in kind, so there is no housing allowance to advance against.",
};
export const gradeReasonFallbackKey = "This request is outside the loan limit for your grade.";
export const gradeReasonFallbackKeyForEmployee = "This request is outside the loan limit for this employee's grade.";

export const gradeReasonKeyFor = (code: GradeLimitReasonCode | null | undefined, self: boolean): string =>
  (code ? (self ? gradeReasonKeys : gradeReasonKeysForEmployee)[code] : undefined)
  ?? (self ? gradeReasonFallbackKey : gradeReasonFallbackKeyForEmployee);

/** Plain-language names for whichever limit caps the request (applicant's voice). */
export const bindingLimitKeys: Record<LoanBindingLimit, string> = {
  GradePerLoan: "your grade's per-loan maximum",
  GradeOutstanding: "your grade's total outstanding maximum",
  PolicyMaxAmount: "the company policy's maximum loan amount",
  PolicyTotalOutstanding: "the company policy's total outstanding maximum",
  PolicySalaryMultiple: "the company policy's salary multiple",
  PolicyInstallmentPercent: "the company policy's limit on instalments as a share of salary",
  PolicyConcurrentLoans: "the company policy's maximum number of open loans",
};

/** The same names for HR applying on an employee's behalf. */
export const bindingLimitKeysForEmployee: Record<LoanBindingLimit, string> = {
  ...bindingLimitKeys,
  GradePerLoan: "this employee's grade's per-loan maximum",
  GradeOutstanding: "this employee's grade's total outstanding maximum",
};

export const bindingLimitKeyFor = (limit: LoanBindingLimit, self: boolean): string =>
  (self ? bindingLimitKeys : bindingLimitKeysForEmployee)[limit] ?? 'the company loan policy';

/**
 * Plain-language text for every other eligibility code the server returns. Neutral wording, so the same text
 * serves the employee and HR applying for them. The server's English reason is never shown raw: a code
 * without an entry here falls back to {@link policyReasonFallbackKey}.
 */
export const policyReasonKeys: Record<string, string> = {
  LoanTypeNotOffered: "Loans of this type aren't offered by the employee's company.",
  InterestNotPermitted: 'Employee loans must be interest-free under Saudi law.',
  EmploymentStatus: "The employee's employment status isn't eligible under this policy.",
  Notice: "Employees serving notice can't receive a new loan.",
  EmploymentDate: 'A valid joining date is needed before a loan can be assessed.',
  MinService: "The minimum service period for this loan type hasn't been completed.",
  Probation: 'Probation must be completed before applying.',
  ContractType: "The employee's contract type isn't eligible for this loan type.",
  RepaymentMethod: "This repayment method isn't allowed by the company policy.",
  RepaymentFrequency: "This repayment frequency isn't allowed by the company policy.",
  Installments: 'The number of instalments is outside what the policy allows.',
  InvalidAmount: 'Enter a positive amount with at most two decimals that covers every instalment.',
  AmountLimit: 'This amount is above the most that can be borrowed now.',
  ConcurrentLoans: 'The maximum number of open loans would be exceeded, counting requests still waiting for approval.',
  Overdue: 'An overdue loan must be settled before another loan can be granted.',
  Cooldown: 'A waiting period applies after the previous loan was settled.',
  SalaryCurrency: "A current salary in the company's currency is needed for the salary-based limits.",
  SalaryAffordability: 'Monthly instalments would be above the share of salary the policy allows.',
  CommitmentCurrency: 'An existing loan is in another currency. Finance must reconcile it first.',
  PolicyInvalid: 'The loan policy needs correcting by HR before this can be assessed.',
};
export const policyReasonFallbackKey = "This request doesn't meet the company loan policy.";

/** Message key for any eligibility code: grade codes by voice, everything else neutral. */
export const reasonKeyFor = (code: string | null | undefined, self: boolean): string =>
  code && code.startsWith('Grade') ? gradeReasonKeyFor(code as GradeLimitReasonCode, self) : (code && policyReasonKeys[code]) || policyReasonFallbackKey;

/** Arabic name when the UI is Arabic and one exists; otherwise the English name. */
export const localName = (locale: string, en: string | null | undefined, ar: string | null | undefined): string =>
  (locale === 'ar' && ar && ar.trim() ? ar : en) ?? '';

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
  // Arabic grade names are display-only: callers pick gradeNameAr via localName().
  const hasCell = !!row.cellId;
  const basis: GradeLimitBasis = row.valueType === 'MultipleOfBasic' || row.valueType === 'MultipleOfGross' || row.valueType === 'MultipleOfHousing' ? row.valueType : 'Amount';
  const perLoanValue = row.valueType === 'Amount' ? row.amount : basis !== 'Amount' ? row.rate : null;
  return {
    gradeId: row.gradeId, gradeCode: row.gradeCode, gradeName: row.gradeName, gradeNameAr: row.gradeNameAr ?? null, level: row.level,
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
export const unsetGradeNames = (drafts: GradeLimitDraft[], locale = 'en') =>
  drafts.filter(d => !d.hasCell).map(d => localName(locale, d.gradeName, d.gradeNameAr) || d.gradeCode);

/** Reads the grades the server listed when it refused to switch grade limiting on. */
export function missingGradesFromError(error: unknown, locale = 'en'): string[] {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (!data || typeof data !== 'object') return [];
  const list = (data as { missingGrades?: unknown }).missingGrades;
  if (!Array.isArray(list)) return [];
  return list.map(item => (typeof item === 'string' ? item
    : localName(locale, (item as GradeMissingLimit)?.gradeName, (item as GradeMissingLimit)?.gradeNameAr) || (item as GradeMissingLimit)?.gradeCode || ''))
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
 * Returns null for limits that are not money (number of open loans) or when a figure is missing.
 * The instalment-share limit is explained in monthly terms, because its cap and existing instalments are monthly.
 */
export function breakdownExplanation(breakdown: LoanLimitBreakdown, money: (n: number) => string): { key: string; values: Record<string, string> } | null {
  if (breakdown.limit === 'PolicyConcurrentLoans' || breakdown.basis === 'Count' || breakdown.unit === 'Loans') return null;
  if (breakdown.available == null || breakdown.cap == null) return null;
  const outstanding = breakdown.outstandingNow ?? 0;
  const values: Record<string, string> = { available: money(breakdown.available), cap: money(breakdown.cap), outstanding: money(outstanding) };
  const hasOutstanding = outstanding > 0;
  if (breakdown.unit === 'MonthlyInstalment' || breakdown.limit === 'PolicyInstallmentPercent') {
    if (!breakdown.installments || breakdown.installments < 1) return null;
    values.installments = String(breakdown.installments);
    values.percent = breakdown.multiple != null ? String(Math.round(breakdown.multiple * 10000) / 100) : '';
    values.salary = breakdown.salaryBasisAmount != null ? money(breakdown.salaryBasisAmount) : '';
    return {
      key: hasOutstanding
        ? 'Eligible up to {available} over {installments} instalments: instalments can be up to {cap} a month ({percent}% of salary {salary}), less {outstanding} a month already committed'
        : 'Eligible up to {available} over {installments} instalments: instalments can be up to {cap} a month ({percent}% of salary {salary})',
      values,
    };
  }
  if (breakdown.basis === 'MultipleOfHousing' && breakdown.multiple != null && breakdown.salaryBasisAmount != null) {
    values.multiple = String(breakdown.multiple);
    values.salary = money(breakdown.salaryBasisAmount);
    return { key: hasOutstanding
      ? 'Eligible up to {available} = {multiple} × housing allowance {salary} − outstanding {outstanding}'
      : 'Eligible up to {available} = {multiple} × housing allowance {salary}', values };
  }
  if ((breakdown.basis === 'MultipleOfBasic' || breakdown.basis === 'MultipleOfGross') && breakdown.multiple != null && breakdown.salaryBasisAmount != null) {
    values.multiple = String(breakdown.multiple);
    values.salary = money(breakdown.salaryBasisAmount);
    const salaryWord = breakdown.basis === 'MultipleOfBasic' ? 'basic' : 'gross';
    return { key: hasOutstanding ? `Eligible up to {available} = {multiple} × ${salaryWord} salary {salary} − outstanding {outstanding}` : `Eligible up to {available} = {multiple} × ${salaryWord} salary {salary}`, values };
  }
  return { key: hasOutstanding ? 'Eligible up to {available} = limit {cap} − outstanding {outstanding}' : 'Eligible up to {available} (limit {cap})', values };
}

/** Everything the limit card prints, worked out without React so it can be tested against real responses. */
export interface LimitCardText { heading: string | null; parts: string[]; reason: string; binding: string | null; explanation: string | null; }

/**
 * Builds the limit card's text from an eligibility response. `t` translates a message key, `money` formats an
 * amount. Every figure the server may send as null is skipped, never formatted — money(null) was the crash.
 * Returns null when there is nothing to explain (no grade limit and no binding limit).
 */
export function limitCardText(eligibility: LoanEligibility, self: boolean, locale: string,
  t: (key: string) => string, money: (n: number) => string): LimitCardText | null {
  const grade = eligibility.gradeLimit;
  if (!grade?.applies && !eligibility.bindingLimit) return null;
  const breakdown = bindingBreakdown(eligibility);
  const explained = breakdown ? breakdownExplanation(breakdown, money) : null;
  const gradeName = localName(locale, grade?.gradeName, grade?.gradeNameAr);
  const parts: string[] = [];
  let heading: string | null = null;
  if (grade?.applies) {
    heading = gradeName
      ? fillTemplate(t(self ? 'Your limit ({grade})' : "This employee's limit ({grade})"), { grade: gradeName })
      : t(self ? 'Your limit' : "This employee's limit");
    parts.push(grade.perLoanCap == null ? t('no per-loan grade limit') : fillTemplate(t('up to {amount} per loan'), { amount: money(grade.perLoanCap) }));
    if (grade.outstandingNow != null) parts.push(fillTemplate(t('Outstanding {amount}'), { amount: money(grade.outstandingNow) }));
    if (grade.available != null) parts.push(fillTemplate(t('Available {amount}'), { amount: money(grade.available) }));
  }
  const reason = grade?.applies && !grade.eligible
    ? fillTemplate(t(gradeReasonKeyFor(grade.reasonCode, self)), {
      perLoanCap: grade.perLoanCap == null ? '' : money(grade.perLoanCap),
      outstandingCap: grade.outstandingCap == null ? '' : money(grade.outstandingCap),
    })
    : '';
  return {
    heading, parts, reason,
    binding: eligibility.bindingLimit ? fillTemplate(t('The limit that applies: {limit}.'), { limit: t(bindingLimitKeyFor(eligibility.bindingLimit, self)) }) : null,
    explanation: explained ? fillTemplate(t(explained.key), explained.values) : null,
  };
}

/** Reads the companies the server listed when enabling grade limits would stop them offering the type. */
export function companiesWithoutPolicyFromError(error: unknown): CompanyWithoutPolicy[] {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (!data || typeof data !== 'object' || (data as { error?: unknown }).error !== 'companies_without_policy') return [];
  const list = (data as { companies?: unknown }).companies;
  return Array.isArray(list)
    ? list.filter((c): c is CompanyWithoutPolicy => !!c && typeof (c as CompanyWithoutPolicy).id === 'string')
      .map(c => ({ id: c.id, name: c.name || c.id }))
    : [];
}

/** Formats an amount in the response's own currency. With no currency, a plain number — never a guessed one. */
export function moneyFormatter(currency: string | null | undefined): (n: number) => string {
  return currency
    ? (n: number) => n.toLocaleString('en-US', { style: 'currency', currency, maximumFractionDigits: 2 })
    : (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
