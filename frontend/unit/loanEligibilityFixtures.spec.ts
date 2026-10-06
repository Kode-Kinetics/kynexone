import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import type { LoanBindingLimit, LoanEligibility, OfferedLoanType } from '../src/api/loanGovernance';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import {
  bindingBreakdown, breakdownExplanation, companiesWithoutPolicyFromError, isGradeBlocked, isLoanTypeNotOffered, limitCardText,
  moneyFormatter, reasonKeyFor,
} from '../src/lib/gradeLoanLimits';
import { loanErrorMessage } from '../src/lib/loanWorkflow';

/**
 * Specs built from the backend's REAL eligibility JSON. The fixture is written and verified by the backend
 * test GradeLoanLimitTests.EligibilityResponses_MatchTheFixtureTheFrontendSpecsUse, so if the API changes
 * shape, that test fails until the fixture (and therefore these specs) are refreshed together.
 */
const raw = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'loanEligibilityResponses.json'), 'utf8')) as Record<string, unknown>;
/** GET /eligibility responses only (the fixture also pins the create refusal and /types/offered). */
const fixtures = Object.fromEntries(Object.entries(raw).filter(([name]) => name !== 'TypesOffered' && !name.startsWith('Create'))) as Record<string, LoanEligibility>;
const createRefused = raw.CreateRefusedGradeLimitPerLoan as LoanEligibility & { error: string };
const typesOffered = raw.TypesOffered as OfferedLoanType[];

/** A money formatter that fails loudly on anything but a number — what toLocaleString on null used to do. */
const strictMoney = (n: number) => {
  if (typeof n !== 'number' || Number.isNaN(n)) throw new Error(`money() called with ${String(n)}`);
  return `SAR ${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
};
const en = (key: string) => translate('en', key);
const ar = (key: string) => translate('ar', key);

const kinds: LoanBindingLimit[] = ['GradePerLoan', 'GradeOutstanding', 'PolicyMaxAmount', 'PolicyTotalOutstanding',
  'PolicySalaryMultiple', 'PolicyInstallmentPercent', 'PolicyConcurrentLoans'];

test('the fixture covers every bindingLimit kind, and the server really sends null for per-loan outstanding', () => {
  for (const kind of kinds) expect(fixtures[kind]?.bindingLimit, kind).toBe(kind);
  const perLoan = fixtures.GradePerLoan.limitBreakdowns!.find(b => b.limit === 'GradePerLoan')!;
  expect(perLoan.outstandingNow).toBeNull();
  const policyMax = fixtures.PolicyMaxAmount.limitBreakdowns!.find(b => b.limit === 'PolicyMaxAmount')!;
  expect(policyMax.outstandingNow).toBeNull();
  expect(fixtures.PolicyMaxAmount.gradeLimit?.applies).toBe(false);
});

test('no real response crashes the limit card, in English or Arabic, for the employee or for HR', () => {
  for (const [name, eligibility] of Object.entries(fixtures)) {
    for (const self of [true, false]) {
      expect(() => limitCardText(eligibility, self, 'en', en, strictMoney), name).not.toThrow();
      expect(() => limitCardText(eligibility, self, 'ar', ar, strictMoney), name).not.toThrow();
    }
    for (const breakdown of eligibility.limitBreakdowns ?? [])
      expect(() => breakdownExplanation(breakdown, strictMoney), `${name}/${breakdown.limit}`).not.toThrow();
  }
});

test('each binding limit is explained with its own arithmetic', () => {
  const explain = (kind: LoanBindingLimit) => limitCardText(fixtures[kind], true, 'en', en, strictMoney)?.explanation;
  expect(explain('GradePerLoan')).toBe('Eligible up to SAR 10,000.00 (limit SAR 10,000.00)');
  expect(explain('GradeOutstanding')).toBe('Eligible up to SAR 9,000.00 = limit SAR 12,000.00 − outstanding SAR 3,000.00');
  expect(explain('PolicyMaxAmount')).toBe('Eligible up to SAR 6,000.00 (limit SAR 6,000.00)');
  expect(explain('PolicyTotalOutstanding')).toBe('Eligible up to SAR 5,000.00 = limit SAR 8,000.00 − outstanding SAR 3,000.00');
  expect(explain('PolicySalaryMultiple')).toBe('Eligible up to SAR 18,000.00 = 2 × gross salary SAR 12,000.00 − outstanding SAR 6,000.00');
  // Monthly figures, explained as monthly — never "limit SAR 1,000 − outstanding" against a principal.
  expect(explain('PolicyInstallmentPercent')).toBe('Eligible up to SAR 4,000.00 over 4 instalments: instalments can be up to SAR 1,000.00 a month (10% of salary SAR 10,000.00)');
  expect(explain('PolicyConcurrentLoans')).toBeNull();
  const multiple = limitCardText(fixtures.GradeMultipleOfBasic, true, 'en', en, strictMoney)!;
  expect(multiple.explanation).toBe('Eligible up to SAR 12,000.00 = 2 × basic salary SAR 6,000.00');
});

test('the card speaks to the employee, and about the employee when HR applies for them', () => {
  const own = limitCardText(fixtures.GradePerLoan, true, 'en', en, strictMoney)!;
  expect(own.heading).toBe('Your limit (Grade 2)');
  expect(own.parts).toEqual(['up to SAR 10,000.00 per loan', 'Outstanding SAR 0.00', 'Available SAR 10,000.00']);
  expect(own.binding).toBe("The limit that applies: your grade's per-loan maximum.");
  const hr = limitCardText(fixtures.GradePerLoan, false, 'en', en, strictMoney)!;
  expect(hr.heading).toBe("This employee's limit (Grade 2)");
  expect(hr.binding).toBe("The limit that applies: this employee's grade's per-loan maximum.");
  const blocked = limitCardText(fixtures.GradeNotEligible, false, 'en', en, strictMoney)!;
  expect(blocked.reason).toBe("This employee's grade isn't eligible for this loan type.");
  expect(isGradeBlocked(fixtures.GradeNotEligible)).toBe(true);
  // Arabic: translated, never the English key or a code.
  const arabic = limitCardText(fixtures.GradeOutstanding, true, 'ar', ar, strictMoney)!;
  expect(arabic.heading).not.toContain('Your limit');
  expect(arabic.binding).not.toContain('GradeOutstanding');
  expect(arabic.explanation).toContain('SAR 9,000.00');
});

test('salary missing, no grade cell and not offered are blocked with a plain reason in both languages', () => {
  expect(fixtures.GradeSalaryMissing.gradeLimit?.reasonCode).toBe('GradeSalaryMissing');
  for (const self of [true, false]) {
    const text = limitCardText(fixtures.GradeSalaryMissing, self, 'en', en, strictMoney)!;
    expect(text.reason).toContain('salary');
    expect(LOCALE_DICTS.ar[reasonKeyFor('GradeSalaryMissing', self)]).toBeTruthy();
  }
  expect(fixtures.GradeLimitNotConfigured.codes).toContain('GradeLimitNotConfigured');
  expect(isLoanTypeNotOffered(fixtures.LoanTypeNotOffered)).toBe(true);
});

test('a preview carries the limits without judging an amount', () => {
  const preview = fixtures.PreviewNoAmount;
  expect(preview.preview).toBe(true);
  expect(preview.eligible).toBe(true);
  expect(preview.available).toBe(10000);
  expect(bindingBreakdown(preview)?.limit).toBe(preview.bindingLimit);
  expect(limitCardText(preview, true, 'en', en, strictMoney)?.parts[0]).toBe('up to SAR 10,000.00 per loan');
});

test('every eligibility code in the fixtures maps to EN and AR text, never shown raw', () => {
  const codes = new Set(Object.values(fixtures).flatMap(f => f.codes ?? []));
  codes.add('MinService'); codes.add('Probation'); codes.add('SalaryAffordability'); codes.add('Cooldown');
  codes.add('GradeLimitCurrencyAmbiguous'); codes.add('GradeSalaryMissing');
  for (const code of codes)
    for (const self of [true, false]) {
      const key = reasonKeyFor(code, self);
      expect(LOCALE_DICTS.en[key], `${code} en`).toBe(key);
      expect(LOCALE_DICTS.ar[key], `${code} ar`).toBeTruthy();
      expect(LOCALE_DICTS.ar[key], `${code} ar`).not.toBe(key);
      expect(key).not.toBe(code);
    }
});

test('every response states the company currency, and the card formats in it — never a guessed default', () => {
  for (const [name, eligibility] of Object.entries(fixtures)) expect(eligibility.currency, name).toBe('SAR');
  expect(moneyFormatter('SAR')(1000)).toContain('SAR');
  expect(moneyFormatter(null)(1000)).toBe('1,000.00');
  expect(moneyFormatter(undefined)(1000)).not.toContain('USD');
});

test('a refused submission (create 400) carries the same explainable fields the form shows', () => {
  expect(createRefused.error).toBe('loan_ineligible');
  expect(createRefused.codes).toEqual(['GradeLimitPerLoan']);
  expect(createRefused.bindingLimit).toBe('GradePerLoan');
  expect(createRefused.currency).toBe('SAR');
  const text = limitCardText(createRefused, true, 'en', en, strictMoney)!;
  expect(text.reason).toBe('This amount is above the per-loan maximum for your grade (SAR 10,000.00).');
  expect(loanErrorMessage({ response: { data: createRefused } }, 'fallback')).toBeTruthy();
});

test('/types/offered lists every type with a plain reason for the ones not offered', () => {
  const byCode = Object.fromEntries(typesOffered.map(t => [t.code, t]));
  expect(byCode.PERSONAL.offered).toBe(true);
  expect(byCode.PERSONAL.reasonCode).toBeNull();
  expect(byCode.HOME).toMatchObject({ offered: false, reasonCode: 'LoanTypeNotOffered', nameAr: 'قرض سكن' });
  expect(byCode.LEGACY).toMatchObject({ offered: false, reasonCode: 'InterestNotPermitted' });
  for (const t of typesOffered.filter(x => !x.offered)) expect(LOCALE_DICTS.ar[reasonKeyFor(t.reasonCode, true)]).toBeTruthy();
});

test('enabling grade limits that would stop companies offering a type is read as a confirmation request', () => {
  const body = { error: 'companies_without_policy', message: 'm', companies: [{ id: 'c1', name: 'Other Co' }, { id: 'c2', name: '' }] };
  expect(companiesWithoutPolicyFromError({ response: { data: body } })).toEqual([{ id: 'c1', name: 'Other Co' }, { id: 'c2', name: 'c2' }]);
  expect(companiesWithoutPolicyFromError({ response: { data: { error: 'grade_limits_missing', companies: body.companies } } })).toEqual([]);
  expect(companiesWithoutPolicyFromError(new Error('x'))).toEqual([]);
});

test('terminology: an employer loan is قرض and a salary advance is سلفة — never the other way round', () => {
  for (const [key, value] of Object.entries(LOCALE_DICTS.ar)) {
    const k = key.toLowerCase();
    if (k.includes('loan') && !k.includes('advance')) expect(value, key).not.toContain('سلف');
    if (k.includes('advance') && !k.includes('loan')) expect(value, key).not.toContain('قرض');
  }
});
