import { expect, test } from '@playwright/test';
import type { MatrixComponent } from '../src/api/entitlements';
import type { GlDriver, GlResolveRow } from '../src/api/financeGl';
import { LOCALE_DICTS } from '../src/i18n/translations';
import {
  addBenefitProblems, addBenefitRequest, benefitServerProblem, blankAddBenefit, earningDriverChoices, isCashAllowance, type AddBenefitForm,
} from '../src/lib/cashAllowance';
import { blankDraft, draftProblem, inputFromDraft, summarise } from '../src/lib/entitlementMatrix';

// Add a benefit: a contract benefit or a monthly cash allowance paid in the salary (step 2).

const cash = (over: Partial<AddBenefitForm> = {}): AddBenefitForm => ({ ...blankAddBenefit, nameEn: 'Phone', kind: 'Cash', glDriverKey: 'EARN:PHONE', ...over });

test('a contract benefit stays the default and sends no pay fields', () => {
  expect(blankAddBenefit.kind).toBe('Contract');
  const form = { ...blankAddBenefit, nameEn: ' Parking ', nameAr: '' };
  expect(addBenefitProblems(form)).toEqual({});
  expect(addBenefitRequest(form)).toEqual({ nameEn: 'Parking', nameAr: null, kind: 'Contract' });
  // A GL account picked and then abandoned for a contract benefit is never sent.
  expect(addBenefitRequest({ ...form, glDriverKey: 'EARN:PHONE' })).not.toHaveProperty('glDriverKey');
});

test('a name is needed in either language, for both kinds', () => {
  expect(addBenefitProblems({ ...blankAddBenefit })).toEqual({ name: 'Give the benefit a name.' });
  expect(addBenefitProblems({ ...blankAddBenefit, nameAr: 'هاتف' })).toEqual({});
  expect(addBenefitProblems(cash({ nameEn: '  ' }))).toEqual({ name: 'Give the benefit a name.' });
});

test('a cash allowance needs its GL account', () => {
  expect(addBenefitProblems(cash({ glDriverKey: '' }))).toEqual({ glDriver: 'Choose the GL account this allowance posts to.' });
  expect(addBenefitProblems(cash())).toEqual({});
});

test('leaving a cash allowance out of end of service needs a reason, and a reason that fits', () => {
  expect(addBenefitProblems(cash({ eosbIncluded: false }))).toEqual({ eosbReason: 'Say why this allowance is left out of end of service pay.' });
  expect(addBenefitProblems(cash({ eosbIncluded: false, eosbReason: '   ' }))).toHaveProperty('eosbReason');
  expect(addBenefitProblems(cash({ eosbIncluded: false, eosbReason: 'x'.repeat(501) }))).toEqual({ eosbReason: 'Keep the reason under 500 characters.' });
  expect(addBenefitProblems(cash({ eosbIncluded: false, eosbReason: 'Reimbursement, not wage' }))).toEqual({});
  // A reason typed and then switched back to "include" is neither required nor sent.
  expect(addBenefitProblems(cash({ eosbIncluded: true, eosbReason: '' }))).toEqual({});
});

test('a cash allowance sends kind Cash, its GL account and the end of service choice', () => {
  expect(addBenefitRequest(cash({ nameAr: 'هاتف' }))).toEqual({
    nameEn: 'Phone', nameAr: 'هاتف', kind: 'Cash', glDriverKey: 'EARN:PHONE', eosbIncluded: true, eosbExclusionReason: null,
  });
  expect(addBenefitRequest(cash({ eosbIncluded: false, eosbReason: ' Not wage ' }))).toMatchObject({ eosbIncluded: false, eosbExclusionReason: 'Not wage' });
  expect(addBenefitRequest(cash({ eosbIncluded: true, eosbReason: 'left over text' }))).toMatchObject({ eosbExclusionReason: null });
});

test('each server refusal is said next to the field it belongs to', () => {
  const refused = (error: string, message = 'server sentence') => ({ response: { status: 400, data: { error, message } } });
  expect(benefitServerProblem(refused('gl_driver_required'))).toEqual({ field: 'glDriver', key: 'Choose the GL account this allowance posts to.' });
  // An unmapped or unknown driver: the server names the driver and the company, so its own sentence is shown.
  expect(benefitServerProblem(refused('gl_driver_invalid', "GL driver 'EARN:X' resolves to no account in company 7 (Unmapped)."))).toEqual({
    field: 'glDriver', message: "GL driver 'EARN:X' resolves to no account in company 7 (Unmapped).",
  });
  expect(benefitServerProblem(refused('eosb_reason_required'))).toMatchObject({ field: 'eosbReason' });
  expect(benefitServerProblem(refused('eosb_reason_too_long'))).toMatchObject({ field: 'eosbReason' });
  expect(benefitServerProblem(refused('benefit_exists'))).toMatchObject({ field: 'name' });
  expect(benefitServerProblem(refused('name_required'))).toMatchObject({ field: 'name' });
  expect(benefitServerProblem(refused('group_scope_required', 'Needs access to every company.'))).toEqual({ field: 'general', message: 'Needs access to every company.' });
  // No body at all (network down): the caller's fallback sentence.
  expect(benefitServerProblem(new Error('Network Error'), 'Unable to add the benefit.')).toEqual({ field: 'general', message: 'Unable to add the benefit.' });
});

const driver = (over: Partial<GlDriver>): GlDriver => ({
  id: over.key ?? 'd', companyId: null, key: 'EARN:BONUS', label: 'Bonus', category: 'Earning', postingSide: 'DR', accountType: 'Expense',
  defaultCode: '5200', defaultName: 'Bonus expense', matchSource: null, matchMode: 'Exact', matchComponentCode: null,
  emitsEmployerExpensePair: false, pairedExpenseDriverKey: null, isSystem: true, isActive: true, sortOrder: 1, inherited: false, ...over,
});
const where = (key: string, code: string, problem: string | null = null): GlResolveRow => ({
  driverKey: key, label: key, category: 'Earning', side: 'DR', account: { id: null, code, name: 'Account' }, source: 'GroupMapping', problem,
});

test('only active Earning drivers that are not an employer expense pair are offered, in the server order', () => {
  const choices = earningDriverChoices([
    driver({ key: 'EARN:OTHER', label: 'Other earnings' }),
    driver({ key: 'DED:LOAN', label: 'Loan deduction', category: 'Deduction' }),
    driver({ key: 'EARN:OLD', label: 'Old', isActive: false }),
    driver({ key: 'EARN:GOSI_ER', label: 'Employer GOSI', emitsEmployerExpensePair: true }),
    driver({ key: 'EARN:BONUS', label: 'Bonus' }),
  ], null);
  expect(choices.map(c => c.key)).toEqual(['EARN:OTHER', 'EARN:BONUS']);
  // Where it posts could not be read: everything is offered and the server decides.
  expect(choices.every(c => c.usable && c.account === null)).toBe(true);
});

test('a driver that posts nowhere usable is shown but cannot be chosen; a mapped one names its account', () => {
  const choices = earningDriverChoices(
    [driver({ key: 'EARN:A', label: 'A' }), driver({ key: 'EARN:B', label: 'B' }), driver({ key: 'EARN:C', label: 'C' }), driver({ key: 'EARN:D', label: 'D' })],
    [where('EARN:A', '4101'), where('EARN:B', '9999'), where('EARN:C', '4300', 'NotInChart'), where('EARN:D', '4400', 'Unmapped')]);
  expect(choices.map(c => [c.key, c.usable])).toEqual([['EARN:A', true], ['EARN:B', false], ['EARN:C', false], ['EARN:D', true]]);
  expect(choices[0].account).toBe('4101 Account');
});

test('a tenant with no earning driver mapped gets an empty list, which the form turns into a message and a link', () => {
  expect(earningDriverChoices([], null)).toEqual([]);
  expect(earningDriverChoices([driver({ key: 'EARN:A' })], [where('EARN:A', '9999')]).filter(c => c.usable)).toEqual([]);
});

const ALW: MatrixComponent = {
  code: 'ALW_PHONE', nameEn: 'Phone', nameAr: 'هاتف', class: 'QiwaWage', floor: 'None', group: 'Wage', allowedValueTypes: ['Amount', 'PercentOfBasic'],
  allowedCoverageTiers: [], allowedDependantScopes: ['None'], allowedLimitPeriods: ['Monthly'], defaultLimitPeriod: 'Monthly',
  isFloor: false, canBeSkipped: false, isLoanFacility: false, floorReason: null, isCustom: true,
};

test('only an ALW_ row the customer added is a cash allowance', () => {
  expect(isCashAllowance(ALW)).toBe(true);
  expect(isCashAllowance({ code: 'BEN_PARKING', isCustom: true })).toBe(false);
  expect(isCashAllowance({ code: 'ALW_PHONE', isCustom: false })).toBe(false);
  expect(isCashAllowance({ code: 'HOUSING' })).toBe(false);
});

test('a cash allowance cell takes an amount or a percentage of basic, monthly, and reads without a period suffix', () => {
  const blank = blankDraft(ALW, 'g1');
  expect(blank).toMatchObject({ valueType: 'Amount', limitPeriod: 'Monthly', dependantScope: 'None' });
  const amount = { ...blank, state: 'offered' as const, amount: '350' };
  expect(draftProblem(amount, ALW)).toBeNull();
  expect(inputFromDraft(amount)).toMatchObject({ componentCode: 'ALW_PHONE', valueType: 'Amount', amount: 350, limitPeriod: 'Monthly' });
  expect(summarise(amount, n => `SAR ${n}`)).toEqual([{ raw: 'SAR 350' }]);
  const percent = { ...blank, state: 'offered' as const, valueType: 'PercentOfBasic' as const, percent: '5' };
  expect(inputFromDraft(percent)).toMatchObject({ valueType: 'PercentOfBasic', rate: 0.05, limitPeriod: 'Monthly' });
  // The server's rule decides: an in-kind or count value is not on offer for this row.
  expect(draftProblem({ ...blank, state: 'offered' as const, valueType: 'InKind' as const }, ALW)).toBe('Choose how this benefit is given.');
});

test('every new sentence of the add form has Arabic', () => {
  const keys = [
    'Is it paid in the monthly salary?', 'Yes, a monthly cash allowance paid in the salary', 'GL account for this allowance (required)', 'Paid in salary',
    'No — GOSI is calculated on basic + housing only', 'Yes — paid in the salary transfer', 'Does it count toward end of service pay?',
    'Why is it left out? (required)', 'Each employee’s amount is set on their salary and approved by Finance before it’s paid.',
    'Choose the GL account this allowance posts to.', 'Say why this allowance is left out of end of service pay.', 'Keep the reason under 500 characters.',
    'A benefit with this name already exists. Choose a different name.',
  ];
  for (const key of keys) {
    expect(LOCALE_DICTS.en[key], key).toBe(key);
    expect(/[؀-ۿ]/.test(LOCALE_DICTS.ar[key] ?? ''), key).toBe(true);
  }
});
