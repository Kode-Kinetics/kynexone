import fs from 'node:fs';
import path from 'node:path';
import { test, expect } from '@playwright/test';
import type { DeductionLine } from '../src/api/deductions';
import { deductions } from '../src/i18n/releaseA/deductions';
import { translate } from '../src/i18n/translations';
import { capSummary, consentSentences, exceedsConsentThreshold, lineSentence, splitByCap, sumAmounts } from '../src/lib/deductions';

// Release A R3: the deductions statement's sentences. Every number is a placeholder in a whole sentence, the legal-basis
// keys the API sends are all translated, and the employee sentence reads like the plan's example.

const fmt = {
  money: (n: number) => `SAR ${n.toLocaleString('en', { minimumFractionDigits: 0, maximumFractionDigits: 2 })}`,
  percent: (n: number) => `${n}%`,
  tr: (k: string) => k,
};

const loan = (over: Partial<DeductionLine> = {}): DeductionLine => ({
  componentCode: 'LOAN_EMI', label: 'Personal loan', category: 'EmployerLoan', amount: 400, countsTowardCap: true,
  legalBasisKey: 'Repayment of a loan from the employer (Article 92). Each instalment may be at most 10% of the wage unless the employee agreed in writing.',
  loanId: 'l1', loanNumber: null, loanType: 'Loan', loanTypeAr: null, balanceAfter: 2400, instalmentsRemaining: 6, instalmentsTotal: 12,
  percentOfWage: 8.3, aboveConsentThreshold: null, consentOnFile: false, ...over,
});

test("the employee's loan line reads as the plan's sentence", () => {
  const s = lineSentence(loan(), fmt);
  expect(translate('en', s.key, s.params)).toBe('Loan instalment of SAR 400: 6 of 12 instalments left, SAR 2,400 still owed.');
  const c = consentSentences(loan(), 'employee', fmt).map((x) => translate('en', x.key, x.params));
  expect(c).toEqual(['Limit without your written consent: 10% of your wage (this instalment is 8.3%).']);
  const ar = translate('ar', s.key, s.params);
  expect(ar).toContain('SAR 2,400');
  expect(ar).toMatch(/[؀-ۿ]/);
});

test('a loan above 10% without consent says so; with consent it does not', () => {
  const over = loan({ percentOfWage: 15, consentOnFile: false });
  expect(exceedsConsentThreshold(over)).toBe(true);
  expect(consentSentences(over, 'hr', fmt).map((x) => x.key)).toContain('Above 10% and no written consent is on file.');
  expect(consentSentences({ ...over, consentOnFile: true }, 'hr', fmt).map((x) => x.key)).toContain("The employee's written consent is on file.");
  expect(exceedsConsentThreshold(loan({ percentOfWage: 10 }))).toBe(false);
  expect(exceedsConsentThreshold(loan({ percentOfWage: null }))).toBe(true); // unknown wage fails closed
});

test('an unknown balance is never guessed, and the last instalment is named', () => {
  expect(lineSentence(loan({ balanceAfter: null, instalmentsRemaining: null }), fmt).key)
    .toBe('{type} instalment of {amount}. The balance after this payslip could not be confirmed.');
  expect(lineSentence(loan({ balanceAfter: 0, instalmentsRemaining: 0 }), fmt).key).toBe('{type} instalment of {amount}: this was the last instalment.');
  const noTotal = lineSentence(loan({ instalmentsTotal: null, instalmentsRemaining: 1 }), fmt);
  expect(translate('en', noTotal.key, noTotal.params)).toBe('Loan instalment of SAR 400: 1 instalment left, SAR 2,400 still owed.');
  expect(translate('ar', noTotal.key, noTotal.params)).toContain('قسط واحد');
});

test('the cap summary drills to the lines: counted lines sum to the counted total', () => {
  const lines = [loan(), loan({ loanId: 'l2', amount: 600 }), loan({ category: 'Statutory', countsTowardCap: false, loanId: null, amount: 975 })];
  const { counted, notCounted } = splitByCap(lines);
  expect(sumAmounts(counted)).toBe(1000);
  expect(sumAmounts(notCounted)).toBe(975);
  const within = capSummary({ debtTotal: 1000, capLimit: 5000, headroom: 4000, debtPercentOfWage: 10, capStatus: 'Within' }, fmt);
  expect(translate('en', within.key, within.params)).toBe('Counted deductions of SAR 1,000 use 10% of the wage. The limit is SAR 5,000, so SAR 4,000 is left.');
  const over = capSummary({ debtTotal: 5001, capLimit: 5000, headroom: -1, debtPercentOfWage: 50.01, capStatus: 'Over' }, fmt);
  expect(translate('en', over.key, over.params)).toBe('Counted deductions of SAR 5,001 are over the limit of SAR 5,000 by SAR 1.');
});

test('every legal-basis key the API sends is translated', () => {
  const source = fs.readFileSync(path.join(__dirname, '../../backend-dotnet/Zayra.Api/Infrastructure/Payroll/DeductionStatementService.cs'), 'utf8');
  const block = source.slice(source.indexOf('public static class DeductionLegalBasis'));
  const keys = Array.from(block.matchAll(/public const string \w+ = "((?:[^"\\]|\\.)*)";/g)).map((m) => m[1]);
  expect(keys.length).toBe(11);
  for (const key of keys) {
    expect(deductions.en[key], key).toBe(key);
    expect(deductions.ar[key], key).toMatch(/[؀-ۿ]/);
  }
});

test('the exception rule matches the server: flagged or not plainly within, never voided', async () => {
  const { needsAttention } = await import('../src/lib/deductions');
  expect(needsAttention('Within', [])).toBe(false);
  expect(needsAttention('Within', ['DEDUCTION_SPLIT_UNRECONCILED'])).toBe(true);
  expect(needsAttention('NeedsReview', ['DEDUCTION_LINES_MISSING'])).toBe(true);
  expect(needsAttention('Voided', [])).toBe(false);
});

test('the unrounded consent answer wins over the floored percentage shown', () => {
  expect(exceedsConsentThreshold(loan({ percentOfWage: 10, aboveConsentThreshold: true }))).toBe(true);
  expect(exceedsConsentThreshold(loan({ percentOfWage: 9.99, aboveConsentThreshold: false }))).toBe(false);
});

test('a loan type is named in Arabic when the tenant gave one', () => {
  const s = lineSentence(loan({ loanType: 'Personal', loanTypeAr: 'قرض شخصي' }), { ...fmt, locale: 'ar' });
  expect(String(s.params?.type)).toBe('قرض شخصي');
});
