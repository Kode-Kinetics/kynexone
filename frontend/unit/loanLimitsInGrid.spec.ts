import { expect, test } from '@playwright/test';
import type { GradeLoanLimitRow } from '../src/api/loanGovernance';
import type { LoanType } from '../src/api/loans';
import { draftFromRow } from '../src/lib/gradeLoanLimits';
import { canEditLoanLimits, loanLimitsLink, loanTypeForComponent, planLoanLimitPublish, rowsChangedSince } from '../src/lib/loanLimitsInGrid';

const row = (over: Partial<GradeLoanLimitRow> = {}): GradeLoanLimitRow => ({
  gradeId: 'g1', gradeCode: 'G1', gradeName: 'Grade 1', level: 1, cellId: 'c1', eligible: true, valueType: 'Amount',
  amount: 10000, rate: null, maxOutstandingAmount: 20000, effectiveFrom: '2026-01-01', isCompanyOverride: false, ...over,
});
const type = (over: Partial<LoanType>): LoanType => ({
  id: 't1', code: 'CAR', nameEn: 'Car Loan', nameAr: 'قرض سيارة', maxAmount: 0, maxInstallments: 12, repaymentFrequency: 'Monthly',
  isInterestFree: true, interestRate: 0, minServiceMonths: 0, requiresApproval: true, isActive: true, ...over,
});
const who = (roles: string[], permissions: string[]) => ({ hasRole: (r: string) => roles.includes(r), hasPermission: (p: string) => permissions.includes(p) });

// The server's own gate: PublishGradeLimits needs an HR role (Admin, HR Manager, HR Director) AND loans.policy_manage or
// employees.approve, and the group-wide grid needs group scope. The grid asks the same question, never a wider one.
test('who may edit a loan limit in the grid is who may publish it in Loan policies', () => {
  const edit = (roles: string[], permissions: string[], isGroupScope: boolean, companyId: string) =>
    canEditLoanLimits({ ...who(roles, permissions), isGroupScope, companyId });
  expect(edit(['HR Director'], ['employees.approve'], true, '')).toBe(true);
  expect(edit(['HR Manager'], ['loans.policy_manage'], true, '')).toBe(true);
  expect(edit(['Admin'], ['loans.policy_manage'], false, 'co-1')).toBe(true);
  // Finance holds loans.write, which does not publish limits.
  expect(edit(['Finance'], ['loans.write', 'loans.policy_manage'], true, '')).toBe(false);
  // An HR role without either key, and a key without an HR role.
  expect(edit(['HR Manager'], ['entitlements.manage', 'loans.write'], true, '')).toBe(false);
  expect(edit(['Employee'], ['employees.approve'], true, '')).toBe(false);
  // The group-wide grid only for someone with every company; a company grid for that company's admin.
  expect(edit(['HR Manager'], ['loans.policy_manage'], false, '')).toBe(false);
  expect(edit(['HR Manager'], ['loans.policy_manage'], false, 'co-1')).toBe(true);
});

test('a loan row is matched to its loan type by the facility component code, never by display name', () => {
  const types = [type({ id: 'a', code: 'CAR', entitlementComponentCode: 'LOAN_CAR' }), type({ id: 'b', code: 'HOUSING', entitlementComponentCode: 'LOAN_HOUSING_ADVANCE' }), type({ id: 'c', code: 'NEW' })];
  expect(loanTypeForComponent(types, 'LOAN_CAR')?.id).toBe('a');
  expect(loanTypeForComponent(types, 'loan_housing_advance')?.id).toBe('b');
  // A type that has no facility yet owns no row.
  expect(loanTypeForComponent(types, 'LOAN_NEW')).toBeUndefined();
  expect(loanTypeForComponent(null, 'LOAN_CAR')).toBeUndefined();
});

test('the Loan policies link lands on the loan type and company, not on the Loans list', () => {
  expect(loanLimitsLink('t1', 'co-1')).toBe('/setup?tab=loanPolicies&loanType=t1&companyId=co-1');
  expect(loanLimitsLink('t1', '')).toBe('/setup?tab=loanPolicies&loanType=t1');
  expect(loanLimitsLink(null, 'co-1')).toBe('/setup?tab=loanPolicies&companyId=co-1');
  expect(loanLimitsLink(undefined, '')).toBe('/setup?tab=loanPolicies');
  for (const href of [loanLimitsLink('t1', 'co-1'), loanLimitsLink(null, '')]) expect(href).not.toBe('/loans');
});

test('a grade someone else changed since the editor opened is named; untouched grades are not', () => {
  const opened = [row({ gradeId: 'g1' }), row({ gradeId: 'g2', cellId: 'c2' }), row({ gradeId: 'g3', cellId: null, eligible: false, valueType: null, amount: null, maxOutstandingAmount: null })];
  const same = opened.map(r => ({ ...r }));
  expect(rowsChangedSince(opened, same, ['g1', 'g2', 'g3'])).toEqual([]);
  // A new version replaces the cell (a new id), even with the same figures.
  expect(rowsChangedSince(opened, same.map(r => (r.gradeId === 'g1' ? { ...r, cellId: 'c9' } : r)), ['g1', 'g2'])).toEqual(['g1']);
  expect(rowsChangedSince(opened, same.map(r => (r.gradeId === 'g2' ? { ...r, amount: 12000 } : r)), ['g2'])).toEqual(['g2']);
  expect(rowsChangedSince(opened, same.map(r => (r.gradeId === 'g3' ? { ...r, cellId: 'c3', eligible: true, valueType: 'Amount', amount: 5 } : r)), ['g3'])).toEqual(['g3']);
  // Only the grades being saved are compared: a change to another grade does not block this save.
  expect(rowsChangedSince(opened, same.map(r => (r.gradeId === 'g2' ? { ...r, amount: 12000 } : r)), ['g1'])).toEqual([]);
  // A grade that vanished from the answer counts as changed, never as unchanged.
  expect(rowsChangedSince(opened, same.filter(r => r.gradeId !== 'g1'), ['g1'])).toEqual(['g1']);
});

test('the publish request is exactly what Limits by grade sends: changed rows only, validated the same way', () => {
  const g1 = draftFromRow(row({ gradeId: 'g1' }));
  const g2 = draftFromRow(row({ gradeId: 'g2', gradeCode: 'G2', gradeName: 'Grade 2', level: 2, cellId: 'c2' }));
  const edited = { ...g1, perLoan: '15000', dirty: true };
  const plan = planLoanLimitPublish({ drafts: [edited, g2], loanTypeId: 't1', companyId: 'co-1', effectiveFrom: '2026-11-20' });
  expect(plan).toEqual({ ok: true, changed: [edited], request: {
    loanTypeId: 't1', companyId: 'co-1', effectiveFrom: '2026-11-20',
    rows: [{ gradeId: 'g1', eligible: true, valueType: 'Amount', amount: 15000, rate: null, maxOutstandingAmount: 20000 }],
  } });
  // The group grid is a null company, as the API wants it.
  expect(planLoanLimitPublish({ drafts: [edited], loanTypeId: 't1', companyId: '', effectiveFrom: '2026-11-20' })).toMatchObject({ ok: true, request: { companyId: null } });

  expect(planLoanLimitPublish({ drafts: [g1, g2], loanTypeId: 't1', companyId: '', effectiveFrom: '2026-11-20' }))
    .toEqual({ ok: false, key: 'Change at least one grade before publishing.' });
  expect(planLoanLimitPublish({ drafts: [edited], loanTypeId: 't1', companyId: '', effectiveFrom: '' }))
    .toEqual({ ok: false, key: 'Choose the date the new limits start.' });
  // Nothing is loosened: a zero per-loan limit, a third decimal, an unset eligibility are all refused before any request.
  expect(planLoanLimitPublish({ drafts: [{ ...edited, perLoan: '0' }], loanTypeId: 't1', companyId: '', effectiveFrom: '2026-11-20' }))
    .toMatchObject({ ok: false, key: 'Per-loan maximum must be more than zero, or left empty for no per-loan limit.' });
  expect(planLoanLimitPublish({ drafts: [{ ...edited, maxOutstanding: '1.005' }], loanTypeId: 't1', companyId: '', effectiveFrom: '2026-11-20' }))
    .toMatchObject({ ok: false, key: 'Amounts can have at most two decimal places.' });
  const unset = draftFromRow(row({ gradeId: 'g3', cellId: null, eligible: false, valueType: null, amount: null, maxOutstandingAmount: null }));
  expect(planLoanLimitPublish({ drafts: [{ ...unset, dirty: true }], loanTypeId: 't1', companyId: '', effectiveFrom: '2026-11-20' }))
    .toMatchObject({ ok: false, key: 'Choose whether this grade is eligible.' });
});
