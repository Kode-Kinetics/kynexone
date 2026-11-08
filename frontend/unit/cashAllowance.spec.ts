import { expect, test } from '@playwright/test';
import type { CashAllowanceInfo, CashGlDriver, MatrixComponent } from '../src/api/entitlements';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { releaseASlices } from '../src/i18n/releaseA';
import {
  addBenefitProblems, addBenefitRequest, allowanceWarnings, benefitServerProblem, blankAddBenefit, cashGlChoices, confirmProblems, confirmRequest,
  correctProblems, correctRequest, correctionMonths, financeServerProblem, isCashAllowance, isPendingFinance, settingsFromInfo, staleRunMonths,
  readinessIssueHref, readinessIssueName, waitingForFinance,
  type AddBenefitForm, type CorrectForm,
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

test('leaving a cash allowance out of end of service needs a category, and a note only for Other', () => {
  expect(addBenefitProblems(cash({ eosbIncluded: false }))).toEqual({ eosbCategory: 'Choose why this allowance is left out of end of service pay.' });
  // A category alone is enough for Reimbursement, Discretionary and Non-recurring: the note is optional.
  for (const eosbCategory of ['Reimbursement', 'Discretionary', 'NonRecurring'] as const) expect(addBenefitProblems(cash({ eosbIncluded: false, eosbCategory }))).toEqual({});
  expect(addBenefitProblems(cash({ eosbIncluded: false, eosbCategory: 'Other' }))).toEqual({ eosbReason: 'Say why this allowance is left out of end of service pay.' });
  expect(addBenefitProblems(cash({ eosbIncluded: false, eosbCategory: 'Other', eosbReason: '   ' }))).toHaveProperty('eosbReason');
  expect(addBenefitProblems(cash({ eosbIncluded: false, eosbCategory: 'Other', eosbReason: 'Paid only in summer' }))).toEqual({});
  expect(addBenefitProblems(cash({ eosbIncluded: false, eosbCategory: 'Reimbursement', eosbReason: 'x'.repeat(501) }))).toEqual({ eosbReason: 'Keep the reason under 500 characters.' });
  // A category or note chosen and then switched back to "include" is neither required nor sent.
  expect(addBenefitProblems(cash({ eosbIncluded: true, eosbCategory: '', eosbReason: '' }))).toEqual({});
});

test('a cash allowance sends kind Cash, its GL account and the end of service choice with its category', () => {
  expect(addBenefitRequest(cash({ nameAr: 'هاتف' }))).toEqual({
    nameEn: 'Phone', nameAr: 'هاتف', kind: 'Cash', glDriverKey: 'EARN:PHONE', eosbIncluded: true, eosbExclusionCategory: null, eosbExclusionReason: null,
  });
  expect(addBenefitRequest(cash({ eosbIncluded: false, eosbCategory: 'Reimbursement', eosbReason: ' Not wage ' })))
    .toMatchObject({ eosbIncluded: false, eosbExclusionCategory: 'Reimbursement', eosbExclusionReason: 'Not wage' });
  // The note is optional outside Other: blank goes as null.
  expect(addBenefitRequest(cash({ eosbIncluded: false, eosbCategory: 'NonRecurring' }))).toMatchObject({ eosbExclusionCategory: 'NonRecurring', eosbExclusionReason: null });
  expect(addBenefitRequest(cash({ eosbIncluded: true, eosbCategory: 'Other', eosbReason: 'left over text' }))).toMatchObject({ eosbExclusionCategory: null, eosbExclusionReason: null });
});

test('each server refusal is said next to the field it belongs to', () => {
  const refused = (error: string, message = 'server sentence') => ({ response: { status: 400, data: { error, message } } });
  expect(benefitServerProblem(refused('gl_driver_required'))).toEqual({ field: 'glDriver', key: 'Choose the GL account this allowance posts to.' });
  // An unmapped or unknown driver: the server names the driver and the company, so its own sentence is shown.
  expect(benefitServerProblem(refused('gl_driver_invalid', "GL driver 'EARN:X' resolves to no account in company 7 (Unmapped)."))).toEqual({
    field: 'glDriver', message: "GL driver 'EARN:X' resolves to no account in company 7 (Unmapped).",
  });
  expect(benefitServerProblem(refused('eosb_category_invalid'))).toMatchObject({ field: 'eosbCategory' });
  expect(benefitServerProblem(refused('eosb_reason_required'))).toMatchObject({ field: 'eosbReason' });
  expect(benefitServerProblem(refused('eosb_reason_too_long'))).toMatchObject({ field: 'eosbReason' });
  expect(benefitServerProblem(refused('benefit_exists'))).toMatchObject({ field: 'name' });
  expect(benefitServerProblem(refused('name_required'))).toMatchObject({ field: 'name' });
  expect(benefitServerProblem(refused('group_scope_required', 'Needs access to every company.'))).toEqual({ field: 'general', message: 'Needs access to every company.' });
  // No body at all (network down): the caller's fallback sentence.
  expect(benefitServerProblem(new Error('Network Error'), 'Unable to add the benefit.')).toEqual({ field: 'general', message: 'Unable to add the benefit.' });
});

const row = (over: Partial<CashGlDriver>): CashGlDriver => ({ key: 'EARN:BONUS', label: 'Bonus', labelAr: null, usable: true, reasonCode: null, reason: null, ...over });

test('the GL choices are the server\u2019s verdicts, in its order; a refused one keeps its reason', () => {
  const choices = cashGlChoices([
    row({ key: 'EARN:PHONE', label: 'Phone allowance' }),
    row({ key: 'EARN:BASIC', label: 'Basic salary', usable: false, reasonCode: 'reserved', reason: 'Reserved' }),
    row({ key: 'EARN:GOSI_ER', label: 'Employer GOSI', usable: false, reasonCode: 'employer_pair', reason: 'Employer' }),
    row({ key: 'EARN:X', label: 'X', usable: false, reasonCode: 'unmapped', reason: 'Unmapped' }),
    row({ key: 'EARN:Y', label: 'Y', usable: false, reasonCode: 'protected_account', reason: 'Protected' }),
  ], 'en');
  expect(choices.map(c => [c.key, c.usable])).toEqual([['EARN:PHONE', true], ['EARN:BASIC', false], ['EARN:GOSI_ER', false], ['EARN:X', false], ['EARN:Y', false]]);
  expect(choices.map(c => c.reasonKey)).toEqual([null,
    '{account} — kept for payroll’s own lines', '{account} — an employer cost account',
    '{account} — not mapped to an account yet', '{account} — posts to a protected account']);
});

test('an unknown refusal code falls back to the general sentence; Arabic labels are used in Arabic', () => {
  expect(cashGlChoices([row({ usable: false, reasonCode: null })], 'en')[0].reasonKey).toBe('{account} — no usable account');
  expect(cashGlChoices([row({ label: 'Phone', labelAr: 'بدل هاتف' })], 'ar')[0].label).toBe('بدل هاتف');
  expect(cashGlChoices([row({ label: 'Phone', labelAr: ' ' })], 'ar')[0].label).toBe('Phone');
  expect(cashGlChoices([row({ label: 'Phone', labelAr: 'بدل هاتف' })], 'en')[0].label).toBe('Phone');
});

test('a tenant with no earning account gets an empty list, which the form turns into a message and a link', () => {
  expect(cashGlChoices([], 'en')).toEqual([]);
  expect(cashGlChoices([row({ usable: false, reasonCode: 'unmapped' })], 'en').filter(c => c.usable)).toEqual([]);
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

// ── Finance confirms and corrects an allowance (wave D) ─────────────────────────────────────────────────────────────────────

const info = (over: Partial<CashAllowanceInfo> = {}): CashAllowanceInfo => ({
  financePending: false, financeConfirmedAtUtc: '2026-11-01T08:00:00Z', glDriverKey: 'EARN:PHONE', eosbIncluded: true, eosbExclusionCategory: null,
  eosbExclusionReason: null, effectiveFrom: null, ...over,
});
const refusedWith = (status: number, data: Record<string, unknown>) => ({ response: { status, data } });

test('only an allowance the server marks as waiting for Finance is pending', () => {
  expect(isPendingFinance({ cashAllowance: info({ financePending: true }) })).toBe(true);
  expect(isPendingFinance({ cashAllowance: info() })).toBe(false);
  expect(isPendingFinance({ cashAllowance: null })).toBe(false);
  expect(isPendingFinance({})).toBe(false);
});

test('the confirm and correct forms start from the setting in effect', () => {
  expect(settingsFromInfo(info())).toEqual({ glDriverKey: 'EARN:PHONE', eosbIncluded: true, eosbCategory: '', eosbReason: '' });
  expect(settingsFromInfo(info({ eosbIncluded: false, eosbExclusionCategory: 'Other', eosbExclusionReason: 'Summer only' })))
    .toEqual({ glDriverKey: 'EARN:PHONE', eosbIncluded: false, eosbCategory: 'Other', eosbReason: 'Summer only' });
  expect(settingsFromInfo(null)).toEqual({ glDriverKey: '', eosbIncluded: true, eosbCategory: '', eosbReason: '' });
});

test('confirming needs the GL account and a complete end of service choice, and sends them as shown', () => {
  const now = settingsFromInfo(info());
  expect(confirmProblems({ ...now, glDriverKey: '' })).toEqual({ glDriver: 'Choose the GL account this allowance posts to.' });
  expect(confirmProblems({ ...now, eosbIncluded: false })).toEqual({ eosbCategory: 'Choose why this allowance is left out of end of service pay.' });
  expect(confirmProblems({ ...now, eosbIncluded: false, eosbCategory: 'Other' })).toHaveProperty('eosbReason');
  expect(confirmProblems(now)).toEqual({});
  expect(confirmRequest(now)).toEqual({ glDriverKey: 'EARN:PHONE', eosbIncluded: true, eosbExclusionCategory: null, eosbExclusionReason: null });
  expect(confirmRequest({ ...now, glDriverKey: ' EARN:CAR ', eosbIncluded: false, eosbCategory: 'Discretionary', eosbReason: ' bonus ' }))
    .toEqual({ glDriverKey: 'EARN:CAR', eosbIncluded: false, eosbExclusionCategory: 'Discretionary', eosbExclusionReason: 'bonus' });
});

test('a correction needs a month that starts on the 1st, a reason and a real change', () => {
  const now = settingsFromInfo(info());
  const form = (over: Partial<CorrectForm> = {}): CorrectForm => ({ ...now, effectiveFrom: '2026-12-01', reason: 'Posted to the wrong account', ...over });
  expect(correctProblems(form(), now)).toEqual({ change: 'Change the GL account or the end of service setting, or cancel.' });
  expect(correctProblems(form({ glDriverKey: 'EARN:CAR' }), now)).toEqual({});
  expect(correctProblems(form({ glDriverKey: 'EARN:CAR', effectiveFrom: '' }), now)).toEqual({ effectiveFrom: 'Choose the month the change starts from.' });
  expect(correctProblems(form({ glDriverKey: 'EARN:CAR', effectiveFrom: '2026-12-15' }), now)).toHaveProperty('effectiveFrom');
  expect(correctProblems(form({ glDriverKey: 'EARN:CAR', reason: '  ' }), now)).toEqual({ reason: 'Say why you are changing it.' });
  // Switching end of service to "left out" needs its category; the GL account is not asked again when it did not change.
  expect(correctProblems(form({ eosbIncluded: false }), now)).toEqual({ eosbCategory: 'Choose why this allowance is left out of end of service pay.' });
  expect(correctProblems(form({ eosbIncluded: false, eosbCategory: 'Reimbursement' }), now)).toEqual({});
  expect(correctProblems(form({ glDriverKey: '' }), now)).toHaveProperty('glDriver');
});

test('a correction sends only the parts that changed', () => {
  const now = settingsFromInfo(info());
  const base: CorrectForm = { ...now, effectiveFrom: '2026-12-01', reason: ' Wrong account ' };
  expect(correctRequest({ ...base, glDriverKey: 'EARN:CAR' }, now)).toEqual({ effectiveFrom: '2026-12-01', reason: 'Wrong account', glDriverKey: 'EARN:CAR' });
  expect(correctRequest({ ...base, eosbIncluded: false, eosbCategory: 'Reimbursement', eosbReason: 'Phone bills' }, now)).toEqual({
    effectiveFrom: '2026-12-01', reason: 'Wrong account', eosbIncluded: false, eosbExclusionCategory: 'Reimbursement', eosbExclusionReason: 'Phone bills',
  });
  expect(correctRequest({ ...base, glDriverKey: 'EARN:CAR', eosbIncluded: false, eosbCategory: 'Other', eosbReason: 'x' }, now))
    .toMatchObject({ glDriverKey: 'EARN:CAR', eosbIncluded: false, eosbExclusionCategory: 'Other' });
  // Back to "included" clears the category and note on the wire.
  const left = settingsFromInfo(info({ eosbIncluded: false, eosbExclusionCategory: 'Other', eosbExclusionReason: 'x' }));
  expect(correctRequest({ ...left, effectiveFrom: '2026-12-01', reason: 'r', eosbIncluded: true }, left))
    .toEqual({ effectiveFrom: '2026-12-01', reason: 'r', eosbIncluded: true, eosbExclusionCategory: null, eosbExclusionReason: null });
});

test('each refusal of confirm and correct belongs to a field, and the maker is told once', () => {
  const field = (error: string, status = 400, extra: Record<string, unknown> = {}) => financeServerProblem(refusedWith(status, { error, message: 'server sentence', ...extra }));
  expect(field('gl_driver_required')).toMatchObject({ field: 'glDriver', key: 'Choose the GL account this allowance posts to.' });
  // The server names the driver and company for an invalid one: its own sentence is the useful part.
  expect(field('gl_driver_invalid')).toEqual({ field: 'glDriver', message: 'server sentence' });
  expect(field('eosb_category_invalid')).toMatchObject({ field: 'eosbCategory' });
  expect(field('eosb_reason_required')).toMatchObject({ field: 'eosbReason' });
  expect(field('eosb_reason_too_long')).toMatchObject({ field: 'eosbReason' });
  expect(field('effective_from_required')).toMatchObject({ field: 'effectiveFrom' });
  expect(field('effective_from_not_month_start')).toMatchObject({ field: 'effectiveFrom' });
  expect(field('reason_required')).toMatchObject({ field: 'reason' });
  expect(field('reason_too_long')).toMatchObject({ field: 'reason' });
  expect(field('nothing_to_change')).toMatchObject({ field: 'change' });
  expect(field('finance_required', 403)).toMatchObject({ field: 'general' });
  expect(field('group_scope_required', 403)).toMatchObject({ field: 'general' });
  // The person who added it cannot confirm it: the grid withdraws Confirm for them.
  expect(field('maker_cannot_confirm', 403)).toMatchObject({ field: 'general', makerBlocked: true, key: 'You added this allowance, so someone else in Finance must confirm it.' });
  // A screen that is out of date is told to reload.
  for (const code of ['already_confirmed', 'not_confirmed', 'not_a_cash_allowance']) expect(field(code, 409)).toMatchObject({ field: 'general', stale: true });
  expect(field('later_version_exists', 409)).toMatchObject({ field: 'effectiveFrom' });
  // A committed period carries the first month that can still change.
  expect(field('period_committed', 409, { earliestEffectiveFrom: '2027-01-01' })).toMatchObject({ field: 'effectiveFrom', earliestEffectiveFrom: '2027-01-01' });
  expect(field('period_committed', 409, { earliestEffectiveFrom: '2027-01-01T00:00:00' }).earliestEffectiveFrom).toBe('2027-01-01');
  expect(field('period_committed', 409)).not.toHaveProperty('earliestEffectiveFrom');
  // Unknown or no body: the server's words, then the caller's fallback.
  expect(field('something_else', 500)).toEqual({ field: 'general', message: 'server sentence' });
  expect(financeServerProblem(new Error('Network Error'), 'Unable to confirm the allowance.')).toEqual({ field: 'general', message: 'Unable to confirm the allowance.' });
});

test('the correction months run from a few months back to a year ahead, and always include the earliest the server named', () => {
  const months = correctionMonths('2026-11-05', 2, 3);
  expect(months).toEqual(['2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01', '2027-01-01', '2027-02-01']);
  expect(correctionMonths('2026-01-31', 1, 0)).toEqual(['2025-12-01', '2026-01-01']);
  expect(correctionMonths('2026-11-05', 0, 1, '2025-06-01')).toEqual(['2025-06-01', '2026-11-01', '2026-12-01']);
  // A name that is not a month start is ignored.
  expect(correctionMonths('2026-11-05', 0, 0, '2025-06-15')).toEqual(['2026-11-01']);
});

test('the draft runs a correction left stale are listed oldest first, and warnings are plain, de-duplicated sentences', () => {
  const runs = [{ id: 'b', year: 2026, month: 12, status: 'Draft' }, { id: 'a', year: 2026, month: 11, status: 'Draft' }];
  expect(staleRunMonths(runs, iso => iso.slice(0, 7))).toBe('2026-11, 2026-12');
  expect(staleRunMonths([], iso => iso)).toBe('');
  expect(allowanceWarnings([{ code: 'a', message: ' Looks like a reimbursement. ' }, { code: 'b', message: 'Looks like a reimbursement.' }, { code: 'c', message: '  ' }]))
    .toEqual(['Looks like a reimbursement.']);
  expect(allowanceWarnings(null)).toEqual([]);
  // A caution the server knows by code becomes an i18n key with Arabic, whatever English the server sent.
  const keys = allowanceWarnings([
    { code: 'eosb_included_reimbursive', message: 'x' }, { code: 'eosb_excluded_fixed_monthly', message: 'y' }, { code: 'commission_name', message: 'z' }]);
  expect(keys).toHaveLength(3);
  const ar = releaseASlices.cashAllowanceFinance.ar;
  for (const k of keys) { expect(ar[k], k).toBeTruthy(); expect(ar[k]).not.toBe(k); }
});

test('every sentence of the Finance confirm and correct panels, the end of service categories and the cautions has Arabic', () => {
  const reused = ['Reimbursement', 'Discretionary', 'Other', 'Note (optional)', 'Confirm', 'Cancel', 'Saving…', 'Open Benefits by grade', 'Try again',
    'Choose an account', 'Yes, include it', 'No, leave it out', 'Basic salary', 'Housing allowance', 'Transport allowance', 'Food allowance', 'Mobile allowance', 'Other allowance'];
  const own = Object.keys(releaseASlices.cashAllowanceFinance.en);
  for (const key of [...reused, ...own]) {
    expect(LOCALE_DICTS.en[key], key).toBeDefined();
    expect(/[؀-ۿ]/.test(LOCALE_DICTS.ar[key] ?? ''), key).toBe(true);
    expect(LOCALE_DICTS.ar[key], key).not.toBe(LOCALE_DICTS.en[key]);
  }
  // Every key a refusal can resolve to is in the dictionary.
  const refusals = ['gl_driver_required', 'eosb_category_invalid', 'eosb_reason_required', 'eosb_reason_too_long', 'effective_from_required', 'effective_from_not_month_start',
    'reason_required', 'reason_too_long', 'nothing_to_change', 'group_scope_required', 'finance_required', 'maker_cannot_confirm', 'not_a_cash_allowance',
    'already_confirmed', 'not_confirmed', 'later_version_exists', 'period_committed'];
  for (const code of refusals) {
    const key = financeServerProblem(refusedWith(400, { error: code })).key;
    expect(key, code).toBeTruthy();
    expect(/[؀-ۿ]/.test(LOCALE_DICTS.ar[key!] ?? ''), `${code} → ${key}`).toBe(true);
  }
});

// G-011: the "Waiting for Finance" list on Benefits by grade, and the payroll readiness line that names the allowance.

const pendingInfo = { financePending: true } as CashAllowanceInfo;
const doneInfo = { financePending: false } as CashAllowanceInfo;
const comp = (code: string, info: CashAllowanceInfo | null): Pick<MatrixComponent, 'code' | 'cashAllowance'> => ({ code, cashAllowance: info });

test('the Waiting for Finance list names only the pending allowances, only for someone who can confirm', () => {
  const all = [comp('BEN_GYM', null), comp('ALW_CAR', pendingInfo), comp('ALW_PHONE', doneInfo), comp('ALW_MEAL', pendingInfo)];
  expect(waitingForFinance(all, true, new Set())).toEqual([{ code: 'ALW_CAR', canConfirm: true }, { code: 'ALW_MEAL', canConfirm: true }]);
  // The person who added it is told someone else must confirm it; the action is not offered.
  expect(waitingForFinance(all, true, new Set(['ALW_MEAL']))).toEqual([{ code: 'ALW_CAR', canConfirm: true }, { code: 'ALW_MEAL', canConfirm: false }]);
  // Without the Finance permission the list is not shown (the grid already says "Finance must confirm it").
  expect(waitingForFinance(all, false, new Set())).toEqual([]);
  expect(waitingForFinance([], true, new Set())).toEqual([]);
});

test('a payroll readiness line is named by the allowance, in the reader’s language, and links where Finance confirms it', () => {
  const issue = { code: 'ALW_CAR', allowanceNameEn: 'Car allowance', allowanceNameAr: 'بدل السيارة', href: '/benefits/by-grade#waiting-for-finance' };
  expect(readinessIssueName(issue, 'en')).toBe('Car allowance');
  expect(readinessIssueName(issue, 'ar')).toBe('بدل السيارة');
  // An older API (or a removed definition) names nothing: the code is the honest fallback.
  expect(readinessIssueName({ code: 'ALW_OLD' }, 'en')).toBe('ALW_OLD');
  expect(readinessIssueName({ code: 'ALW_OLD', allowanceNameEn: ' ', allowanceNameAr: null }, 'ar')).toBe('ALW_OLD');
  expect(readinessIssueHref(issue)).toBe('/benefits/by-grade#waiting-for-finance');
  expect(readinessIssueHref({ code: 'ALW_OLD' })).toBe('/benefits/by-grade');
  // Only an in-app path is ever followed.
  expect(readinessIssueHref({ code: 'x', href: 'https://evil.example/' })).toBe('/benefits/by-grade');
  expect(readinessIssueHref({ code: 'x', href: '//evil.example/' })).toBe('/benefits/by-grade');
});
