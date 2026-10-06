import { expect, test } from '@playwright/test';
import type { GradeLoanLimitRow, LoanEligibility } from '../src/api/loanGovernance';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  applyFromGradeUpward, applySameForAll, bindingBreakdown, bindingLimitKeys, breakdownExplanation, draftFromRow,
  draftProblem, fillTemplate, gradeReasonKeys, inputFromDraft, isGradeBlocked, isLoanTypeNotOffered,
  missingGradesFromError, unsetGradeNames,
} from '../src/lib/gradeLoanLimits';

const row = (over: Partial<GradeLoanLimitRow>): GradeLoanLimitRow => ({
  gradeId: 'g1', gradeCode: 'G1', gradeName: 'Grade 1', level: 1, cellId: 'c1', eligible: true, valueType: 'Amount',
  amount: 10000, rate: null, maxOutstandingAmount: 20000, effectiveFrom: '2026-01-01', isCompanyOverride: false, ...over,
});
const sar = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'SAR' });

test('a grade with no cell is shown as not set, never guessed as eligible or not', () => {
  const draft = draftFromRow(row({ cellId: null, eligible: false, valueType: 'EligibilityOnly', amount: null, maxOutstandingAmount: null }));
  expect(draft.eligible).toBe('unset');
  expect(draft.perLoan).toBe('');
  expect(unsetGradeNames([draft])).toEqual(['Grade 1']);
  expect(draftProblem({ ...draft, dirty: true })).toBe('Choose whether this grade is eligible.');
});

test('rows round-trip into the PUT shape the cell CHECKs require', () => {
  const fixed = draftFromRow(row({}));
  expect(inputFromDraft(fixed)).toEqual({ gradeId: 'g1', eligible: true, valueType: 'Amount', amount: 10000, rate: null, maxOutstandingAmount: 20000 });
  const multiple = draftFromRow(row({ valueType: 'MultipleOfGross', amount: null, rate: 2.5 }));
  expect(multiple.perLoan).toBe('2.5');
  expect(inputFromDraft(multiple)).toEqual({ gradeId: 'g1', eligible: true, valueType: 'MultipleOfGross', amount: null, rate: 2.5, maxOutstandingAmount: 20000 });
  // Empty per-loan figure = no per-loan grade cap, which is EligibilityOnly with the outstanding cap kept.
  expect(inputFromDraft({ ...fixed, perLoan: '' })).toEqual({ gradeId: 'g1', eligible: true, valueType: 'EligibilityOnly', amount: null, rate: null, maxOutstandingAmount: 20000 });
  // Not eligible clears every figure.
  expect(inputFromDraft({ ...fixed, eligible: 'no' })).toEqual({ gradeId: 'g1', eligible: false, valueType: 'EligibilityOnly', amount: null, rate: null, maxOutstandingAmount: null });
});

test('changed rows are validated in plain language', () => {
  const base = { ...draftFromRow(row({})), dirty: true };
  expect(draftProblem(base)).toBeNull();
  expect(draftProblem({ ...base, perLoan: '0' })).toContain('more than zero');
  expect(draftProblem({ ...base, perLoan: '10.555' })).toContain('two decimal places');
  expect(draftProblem({ ...base, basis: 'MultipleOfBasic', perLoan: '1.12345' })).toContain('four decimal places');
  expect(draftProblem({ ...base, maxOutstanding: '-1' })).toContain('Total outstanding maximum');
  expect(draftProblem({ ...base, eligible: 'no', perLoan: '-5' })).toBeNull();
  expect(draftProblem({ ...base, perLoan: '-5', dirty: false })).toBeNull();
});

test('helpers copy one grade to all grades, or to that grade and every higher level', () => {
  const drafts = [1, 2, 3].map(level => draftFromRow(row({ gradeId: `g${level}`, gradeName: `Grade ${level}`, level, amount: level * 1000 })));
  const all = applySameForAll(drafts, 'g2');
  expect(all.map(d => d.perLoan)).toEqual(['2000', '2000', '2000']);
  expect(all.map(d => d.dirty)).toEqual([true, false, true]);
  const upward = applyFromGradeUpward(drafts, 'g2');
  expect(upward.map(d => d.perLoan)).toEqual(['1000', '2000', '2000']);
  expect(upward.map(d => d.dirty)).toEqual([false, false, true]);
});

test('apply-form gating: grade refusals and "not offered" block submission', () => {
  const gradeLimit = { applies: true, eligible: false, perLoanCap: 10000, outstandingCap: null, outstandingNow: 0, available: 10000, reasonCode: 'GradeLimitPerLoan' as const, reasonText: null };
  expect(isGradeBlocked({ gradeLimit })).toBe(true);
  expect(isGradeBlocked({ gradeLimit: { ...gradeLimit, applies: false } })).toBe(false);
  expect(isGradeBlocked({ gradeLimit: null })).toBe(false);
  expect(isLoanTypeNotOffered({ codes: ['LoanTypeNotOffered'] })).toBe(true);
  expect(isLoanTypeNotOffered({ codes: ['AmountLimit'] })).toBe(false);
});

test('the binding limit is explained with its arithmetic', () => {
  const eligibility: Pick<LoanEligibility, 'bindingLimit' | 'limitBreakdowns'> = {
    bindingLimit: 'GradeOutstanding',
    limitBreakdowns: [
      { limit: 'PolicyMaxAmount', basis: 'Amount', cap: 50000, outstandingNow: 0, available: 50000 },
      { limit: 'GradeOutstanding', basis: 'MultipleOfBasic', multiple: 2, salaryBasisAmount: 12000, cap: 24000, outstandingNow: 6000, available: 18000 },
    ],
  };
  const breakdown = bindingBreakdown(eligibility)!;
  expect(breakdown.limit).toBe('GradeOutstanding');
  const explained = breakdownExplanation(breakdown, sar)!;
  expect(fillTemplate(explained.key, explained.values)).toBe(`Eligible up to ${sar(18000)} = 2 × basic salary ${sar(12000)} − outstanding ${sar(6000)}`);
  const fixed = breakdownExplanation({ limit: 'PolicyMaxAmount', basis: 'Amount', cap: 50000, outstandingNow: 0, available: 50000 }, sar)!;
  expect(fillTemplate(fixed.key, fixed.values)).toBe(`Eligible up to ${sar(50000)} (limit ${sar(50000)})`);
  expect(breakdownExplanation({ limit: 'PolicyConcurrentLoans', basis: 'Amount', cap: 1, outstandingNow: 1, available: 0 }, sar)).toBeNull();
  expect(bindingBreakdown({ bindingLimit: 'GradePerLoan', limitBreakdowns: [] })).toBeNull();
});

test('every reason, binding-limit and explanation text has English and Arabic wording', () => {
  const explanationKeys = (['MultipleOfBasic', 'MultipleOfGross', 'Amount'] as const).flatMap(basis => [0, 1].map(outstandingNow =>
    breakdownExplanation({ limit: 'GradePerLoan', basis, multiple: 2, salaryBasisAmount: 1000, cap: 2000, outstandingNow, available: 2000 - outstandingNow }, sar)!.key));
  for (const key of [...Object.values(gradeReasonKeys), ...Object.values(bindingLimitKeys), ...explanationKeys]) {
    expect(LOCALE_DICTS.en[key], key).toBe(key);
    expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
    expect(LOCALE_DICTS.ar[key], key).not.toBe(key);
  }
});

test('missing grades are read from the refusal body whether names or objects', () => {
  expect(missingGradesFromError({ response: { data: { missingGrades: [{ gradeName: 'Grade 3' }, { gradeCode: 'G4' }, 'Grade 5'] } } })).toEqual(['Grade 3', 'G4', 'Grade 5']);
  expect(missingGradesFromError({ response: { data: 'nope' } })).toEqual([]);
  expect(missingGradesFromError(new Error('network'))).toEqual([]);
});
