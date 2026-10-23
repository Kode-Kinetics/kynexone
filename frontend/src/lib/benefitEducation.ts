import type { BenefitClaimBalance, BenefitClaimLine, BenefitClaimRules, BenefitPaymentPolicy } from '../api/benefits';

/**
 * Child, invoice-line, country and term rules on a reimbursement policy (contract sections A, C, D).
 * Every field is optional and absent on a legacy plan, so a legacy plan renders and submits exactly as before.
 */

export const COST_TYPES = ['Tuition', 'Transport', 'Books', 'Uniform', 'Vat', 'Other'] as const;
export const CLAIM_TERMS = ['Semester', 'HalfYear', 'FullYear'] as const;
export const GCC_COUNTRIES = ['SA', 'AE', 'KW', 'BH', 'OM', 'QA'] as const;
export type CostType = typeof COST_TYPES[number];
export type ClaimTerm = typeof CLAIM_TERMS[number];

/** English source keys; render through t(). */
export const COST_TYPE_LABELS: Record<CostType, string> = { Tuition: 'Tuition', Transport: 'Transport', Books: 'Books', Uniform: 'Uniform', Vat: 'VAT', Other: 'Other costs' };
export const CLAIM_TERM_LABELS: Record<ClaimTerm, string> = { Semester: 'Semester', HalfYear: 'Half year', FullYear: 'Full year' };

/** True when the list is exactly the six GCC countries, in any order. */
export function isGccSet(countries: readonly string[] | null | undefined): boolean {
  if (!countries || countries.length !== GCC_COUNTRIES.length) return false;
  const set = new Set(countries.map(code => code.toUpperCase()));
  return GCC_COUNTRIES.every(code => set.has(code));
}

/** "SA, AE , kw" → ['SA', 'AE', 'KW']; distinct, in typed order. Invalid entries are kept so validation can name them. */
export function parseCountryList(text: string): string[] {
  const seen = new Set<string>(); const out: string[] = [];
  for (const raw of text.split(/[\s,;]+/)) {
    const code = raw.trim().toUpperCase();
    if (code && !seen.has(code)) { seen.add(code); out.push(code); }
  }
  return out;
}

/**
 * A payroll component code derived from a plan code or name: uppercase letters, digits and underscores, starting
 * with a letter, at most 40 characters (the server's rule for a benefit component code).
 */
export function payslipLineCode(source: string | null | undefined): string {
  const cleaned = (source ?? '').toUpperCase().replace(/[^A-Z0-9]+/g, '_').replace(/^[^A-Z]+/, '').replace(/_+$/, '');
  return cleaned.slice(0, 40).replace(/_+$/, '');
}

/** What a user types into the code box: uppercased, unsafe characters turned into underscores. */
export function sanitizePayslipCodeInput(value: string): string {
  return value.toUpperCase().replace(/[^A-Z0-9_]/g, '_').slice(0, 40);
}

const cents = (value: number) => Math.round(value * 100);

export interface LineSplit { covered: number; notCovered: number; total: number; notCoveredTypes: CostType[] }

/** Sums invoice lines into covered and not-covered money, in whole cents so 0.1 + 0.2 stays 0.30. */
export function splitLines(lines: readonly { type: string; amount: number }[], coveredCosts: readonly string[] | null | undefined): LineSplit {
  let covered = 0, notCovered = 0; const notCoveredTypes: CostType[] = [];
  for (const line of lines) {
    const amount = Number.isFinite(line.amount) && line.amount > 0 ? cents(line.amount) : 0;
    if (!coveredCosts || coveredCosts.includes(line.type)) covered += amount;
    else { notCovered += amount; if (amount > 0 && !notCoveredTypes.includes(line.type as CostType)) notCoveredTypes.push(line.type as CostType); }
  }
  return { covered: covered / 100, notCovered: notCovered / 100, total: (covered + notCovered) / 100, notCoveredTypes };
}

/**
 * Which cost types on a saved claim were covered, worked out from its lines and claimed amount. The claim does not carry
 * the plan's covered-cost list, so this answers only when exactly one set of the claim's line types adds up to the claimed
 * amount; otherwise it returns null and the caller shows totals without marking lines.
 */
export function inferCoveredTypes(lines: readonly { type: string; amount: number }[], claimed: number): string[] | null {
  const byType = new Map<string, number>();
  for (const line of lines) byType.set(line.type, (byType.get(line.type) ?? 0) + cents(line.amount));
  const types = [...byType.keys()];
  if (!types.length || types.length > 10) return null;
  const target = cents(claimed); const matches: string[][] = [];
  for (let mask = 1; mask < 1 << types.length; mask++) {
    const picked = types.filter((_, index) => mask & (1 << index));
    if (picked.reduce((sum, type) => sum + byType.get(type)!, 0) === target) matches.push(picked);
    if (matches.length > 1) return null;
  }
  return matches[0] ?? null;
}

/** The rules that drive the claim form: the server's rules on the balance when present, otherwise the plan policy. */
export function claimRules(policy: BenefitPaymentPolicy | null | undefined, balance: BenefitClaimBalance | null | undefined): BenefitClaimRules {
  if (balance?.rules) return balance.rules;
  return {
    claimFor: policy?.claimFor ?? 'Employee', limitPerChild: policy?.limitPerChild ?? false, maxChildren: policy?.maxChildren ?? null,
    extraChildByException: policy?.extraChildByException ?? false, coveredCosts: policy?.coveredCosts ?? null,
    allowedCountries: policy?.allowedCountries ?? null, claimTerms: policy?.claimTerms ?? null, limitPeriod: '',
  };
}

/** True when any of the new rules is switched on; a legacy plan returns false and keeps the original form. */
export const hasEducationRules = (rules: BenefitClaimRules) =>
  rules.claimFor === 'Child' || !!rules.coveredCosts || !!rules.allowedCountries || !!rules.claimTerms;

/** The first problem with the policy's education rules, as an English source key plus params; null when valid. */
export function educationRulesProblem(policy: BenefitPaymentPolicy): { key: string; params?: Record<string, string | number> } | null {
  const child = policy.claimFor === 'Child';
  if (policy.delivery !== 'Reimbursement') return null;
  if (child && policy.maxChildren != null && (!Number.isInteger(policy.maxChildren) || policy.maxChildren < 1 || policy.maxChildren > 20))
    return { key: 'Maximum children must be a whole number from 1 to 20.' };
  if (policy.coveredCosts && policy.coveredCosts.length === 0) return { key: 'Choose at least one covered cost, or switch off “Only some invoice costs are covered”.' };
  if (policy.allowedCountries) {
    if (policy.allowedCountries.length === 0) return { key: 'Enter at least one school country, or allow any country.' };
    const bad = policy.allowedCountries.filter(code => !/^[A-Z]{2}$/.test(code));
    if (bad.length) return { key: 'Use two-letter country codes such as SA or AE. Not recognised: {codes}.', params: { codes: bad.join(', ') } };
  }
  if (policy.claimTerms && policy.claimTerms.length === 0) return { key: 'Choose at least one claim term, or stop asking for the term.' };
  return null;
}

/** The submit payload's optional part: only the fields the rules ask for, so a legacy claim payload is unchanged. */
export function educationClaimFields(rules: BenefitClaimRules, input: { dependantId: string; schoolName: string; schoolCountry: string; claimTerm: string; lines: BenefitClaimLine[] }) {
  const out: { dependantId?: string; schoolName?: string; schoolCountry?: string; claimTerm?: string; lines?: BenefitClaimLine[] } = {};
  if (rules.claimFor === 'Child') { out.dependantId = input.dependantId; out.schoolName = input.schoolName.trim(); }
  if (rules.allowedCountries) out.schoolCountry = input.schoolCountry;
  if (rules.claimTerms) out.claimTerm = input.claimTerm;
  if (rules.coveredCosts) out.lines = input.lines.filter(line => line.amount > 0).map(line => ({ type: line.type, amount: cents(line.amount) / 100 }));
  return out;
}
