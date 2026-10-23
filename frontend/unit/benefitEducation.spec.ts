import { expect, test } from '@playwright/test';
import type { BenefitPaymentPolicy } from '../src/api/benefits';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  CLAIM_TERM_LABELS, COST_TYPE_LABELS, GCC_COUNTRIES, claimRules, educationClaimFields, educationRulesProblem, hasEducationRules,
  inferCoveredTypes, isGccSet, parseCountryList, payslipLineCode, sanitizePayslipCodeInput, splitLines,
} from '../src/lib/benefitEducation';

const legacy: BenefitPaymentPolicy = { delivery: 'Reimbursement', amount: 2000, frequency: 'Monthly', paymentMonth: null, prorate: false, salaryComponentId: 'earning', receiptRequired: true, receiptLabel: 'Receipt', claimWindowDays: null, instructions: '' };
const education: BenefitPaymentPolicy = { ...legacy, claimFor: 'Child', limitPerChild: true, maxChildren: 3, extraChildByException: true, coveredCosts: ['Tuition'], allowedCountries: [...GCC_COUNTRIES], claimTerms: ['Semester', 'HalfYear', 'FullYear'] };

test('payslip line code follows the server rule: capital letters, digits, underscores, starting with a letter, at most 40', () => {
  expect(payslipLineCode('EDU-ALLOW')).toBe('EDU_ALLOW');
  expect(payslipLineCode('  child education 2026 ')).toBe('CHILD_EDUCATION_2026');
  expect(payslipLineCode('2026 tuition')).toBe('TUITION');
  expect(payslipLineCode('---')).toBe('');
  expect(payslipLineCode('A'.repeat(39) + '-B')).toMatch(/^[A-Z][A-Z0-9_]{0,39}$/);
  expect(payslipLineCode('A'.repeat(39) + '-B').endsWith('_')).toBe(false);
  expect(sanitizePayslipCodeInput('edu-allow x')).toBe('EDU_ALLOW_X');
});

test('covered and not-covered money is summed in whole cents', () => {
  const split = splitLines([{ type: 'Tuition', amount: 0.1 }, { type: 'Tuition', amount: 0.2 }, { type: 'Transport', amount: 150 }, { type: 'Vat', amount: 22.5 }, { type: 'Books', amount: Number.NaN }], ['Tuition']);
  expect(split).toEqual({ covered: 0.3, notCovered: 172.5, total: 172.8, notCoveredTypes: ['Transport', 'Vat'] });
  // No covered-cost list means the whole amount counts (legacy).
  expect(splitLines([{ type: 'Transport', amount: 10 }], null).covered).toBe(10);
});

test('the GCC preset is recognised in any order and only when complete', () => {
  expect(isGccSet(['QA', 'OM', 'BH', 'KW', 'AE', 'SA'])).toBe(true);
  expect(isGccSet(['SA', 'AE'])).toBe(false);
  expect(isGccSet(null)).toBe(false);
  expect(parseCountryList('sa, AE ;ae  kw')).toEqual(['SA', 'AE', 'KW']);
});

test('a legacy plan has no education rules and submits no extra fields', () => {
  const rules = claimRules(legacy, null);
  expect(hasEducationRules(rules)).toBe(false);
  expect(educationClaimFields(rules, { dependantId: 'child', schoolName: 'School', schoolCountry: 'SA', claimTerm: 'Semester', lines: [{ type: 'Tuition', amount: 5 }] })).toEqual({});
  expect(educationRulesProblem(legacy)).toBeNull();
});

test('education rules send exactly the fields the policy asks for, with server rules preferred over the plan copy', () => {
  const rules = claimRules(education, null);
  expect(hasEducationRules(rules)).toBe(true);
  expect(educationClaimFields(rules, { dependantId: 'child-1', schoolName: ' Riyadh School ', schoolCountry: 'SA', claimTerm: 'Semester', lines: [{ type: 'Tuition', amount: 1000.005 }, { type: 'Transport', amount: 0 }] }))
    .toEqual({ dependantId: 'child-1', schoolName: 'Riyadh School', schoolCountry: 'SA', claimTerm: 'Semester', lines: [{ type: 'Tuition', amount: 1000.01 }] });
  const serverRules = { ...rules, maxChildren: 2, limitPeriod: 'ContractYear' };
  expect(claimRules(education, { canClaim: true, maximumAmount: 1, reservedAmount: 0, approvedAmount: 0, remainingAmount: 1, periodFrom: '2026-01-01', periodTo: '2026-12-31', rules: serverRules })).toBe(serverRules);
});

test('policy validation explains the first broken rule', () => {
  expect(educationRulesProblem(education)).toBeNull();
  expect(educationRulesProblem({ ...education, maxChildren: 21 })?.key).toContain('1 to 20');
  expect(educationRulesProblem({ ...education, coveredCosts: [] })?.key).toContain('covered cost');
  expect(educationRulesProblem({ ...education, allowedCountries: ['SA', 'KSA'] })).toEqual({ key: 'Use two-letter country codes such as SA or AE. Not recognised: {codes}.', params: { codes: 'KSA' } });
  expect(educationRulesProblem({ ...education, claimTerms: [] })?.key).toContain('claim term');
});

test('covered line types are inferred only when exactly one combination matches the claimed amount', () => {
  expect(inferCoveredTypes([{ type: 'Tuition', amount: 1000 }, { type: 'Transport', amount: 200 }, { type: 'Vat', amount: 150 }], 1000)).toEqual(['Tuition']);
  expect(inferCoveredTypes([{ type: 'Tuition', amount: 1000 }, { type: 'Books', amount: 1000 }], 1000)).toBeNull();
  expect(inferCoveredTypes([], 0)).toBeNull();
});

test('every vocabulary label has an Arabic translation', () => {
  for (const label of [...Object.values(COST_TYPE_LABELS), ...Object.values(CLAIM_TERM_LABELS), 'Contract year']) {
    expect(LOCALE_DICTS.en[label], label).toBeTruthy();
    expect(LOCALE_DICTS.ar[label], label).toBeTruthy();
    expect(LOCALE_DICTS.ar[label]).not.toBe(LOCALE_DICTS.en[label]);
  }
});
