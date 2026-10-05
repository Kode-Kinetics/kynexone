import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import type { LoanBindingLimit, LoanEligibility } from '../src/api/loanGovernance';
import { LOCALE_DICTS, translate } from '../src/i18n/translations';
import {
  bindingBreakdown, breakdownExplanation, isGradeBlocked, isLoanTypeNotOffered, limitCardText, reasonKeyFor,
} from '../src/lib/gradeLoanLimits';

/**
 * Specs built from the backend's REAL eligibility JSON. The fixture is written and verified by the backend
 * test GradeLoanLimitTests.EligibilityResponses_MatchTheFixtureTheFrontendSpecsUse, so if the API changes
 * shape, that test fails until the fixture (and therefore these specs) are refreshed together.
 */
const fixtures = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'loanEligibilityResponses.json'), 'utf8')) as Record<string, LoanEligibility>;

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
  for (const code of codes)
    for (const self of [true, false]) {
      const key = reasonKeyFor(code, self);
      expect(LOCALE_DICTS.en[key], `${code} en`).toBe(key);
      expect(LOCALE_DICTS.ar[key], `${code} ar`).toBeTruthy();
      expect(LOCALE_DICTS.ar[key], `${code} ar`).not.toBe(key);
      expect(key).not.toBe(code);
    }
});
