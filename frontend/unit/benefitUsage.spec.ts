import { test, expect } from '@playwright/test';
import type { UtilisationRow } from '../src/api/benefitUsage';
import {
  INFO_KEYS, REFUSAL_KEYS, STATUS_KEYS, emptyUsageForm, leftFor, noFamilyNoteKey, measureText, newUsageId, refusalKey, statusKey, usageKind, usageProblems,
} from '../src/lib/benefitUsage';
import { releaseASlices } from '../src/i18n/releaseA';
import { translate } from '../src/i18n/translations';

const ARABIC = /[؀-ۿ]/;

const row = (over: Partial<UtilisationRow> = {}): UtilisationRow => ({
  employeeId: 7, employeeName: 'Mohammed', employeeCode: 'E-1', companyId: 'c1', benefitCode: 'AIR_TICKET', labelEn: 'Annual air ticket',
  labelAr: 'تذكرة السفر السنوية', verificationState: 'Verified', measure: 'Units', currency: null, entitlement: 3, pending: 0, approved: 1, remaining: 2,
  periodStart: '2026-02-01', periodEnd: '2027-01-31', dependantScope: 'Family', maxDependants: 2, perBeneficiary: 1,
  beneficiaries: [
    { dependantId: null, name: 'Mohammed', cap: 1, used: 1, remaining: 0, named: true },
    { dependantId: 'd1', name: 'Wife', cap: 1, used: 0, remaining: 1, named: false },
  ],
  canRequest: true, ...over,
});

test('every refusal, status and help text has an English key that is translated into Arabic', () => {
  for (const key of [...Object.values(REFUSAL_KEYS), ...Object.values(STATUS_KEYS), ...Object.values(INFO_KEYS)]) {
    expect(ARABIC.test(translate('ar', key)), key).toBe(true);
  }
  expect(Object.keys(releaseASlices)).toContain('benefitUsage');
});

test('the statuses are the loan request’s own: Pending, Approved, Rejected, Cancelled', () => {
  expect(Object.keys(STATUS_KEYS).sort()).toEqual(['Approved', 'Cancelled', 'Pending', 'Rejected']);
  expect(statusKey('Pending')).toBe('Waiting for Finance');
  expect(statusKey('Cancelled')).toBe('Withdrawn');
});

test('a refusal from the server becomes its own sentence; anything else a generic one', () => {
  expect(refusalKey({ response: { data: { error: 'usage_over_entitlement' } } })).toBe('That is more than is left for this period.');
  expect(refusalKey({ response: { data: { error: 'subject_cannot_decide' } } })).toBe('This is your own benefit, so another person must act on it.');
  expect(refusalKey(new Error('network'))).toBe('The change could not be saved. Try again.');
});

test('what is left is per traveller or child when the benefit covers dependants', () => {
  expect(leftFor(row(), '')).toBe(0);
  expect(leftFor(row(), 'd1')).toBe(1);
  expect(leftFor(row(), 'someone-else')).toBe(0);
  expect(leftFor(row({ beneficiaries: [], remaining: 2 }), '')).toBe(2);
});

test('the dialog checks the date, the count, the child and the evidence before sending', () => {
  const ticket = row();
  const form = { ...emptyUsageForm('2026-08-10'), benefitCode: 'AIR_TICKET', quantity: '1', dependantId: 'd1', evidenceReference: 'BKG-1' };
  expect(usageProblems(form, ticket)).toEqual({});
  expect(usageProblems({ ...form, dependantId: '' }, ticket).quantity).toBe('That is more than is left for this period.');
  expect(usageProblems({ ...form, quantity: '1.5' }, ticket).quantity).toBe('Enter a whole number.');
  expect(usageProblems({ ...form, usageDate: '2027-03-01' }, ticket).usageDate).toBe('Choose a date inside this benefit period.');
  expect(usageProblems({ ...form, evidenceReference: ' ' }, ticket).evidenceReference).toBe('Give the invoice, receipt or booking reference you checked.');
  expect(usageProblems(form, undefined).benefitCode).toBe('Choose a benefit.');

  const education = row({ benefitCode: 'EDUCATION', measure: 'Amount', currency: 'SAR', dependantScope: 'Children',
    beneficiaries: [{ dependantId: 'c1', name: 'Son', cap: 20000, used: 15000, remaining: 5000, named: true }] });
  const claim = { ...form, benefitCode: 'EDUCATION', quantity: '5000', dependantId: 'c1', academicYear: '2026/2027' };
  expect(usageProblems(claim, education)).toEqual({});
  expect(usageProblems({ ...claim, quantity: '5000.001' }, education).quantity).toBe('Use at most two decimals.');
  expect(usageProblems({ ...claim, dependantId: '' }, education).dependantId).toBe('Choose the child this claim is for.');
  expect(usageProblems({ ...claim, academicYear: '' }, education).academicYear).toBe('Give the academic year the fees are for.');
  for (const message of ['Choose a benefit.', 'Choose a date.', 'Choose a date inside this benefit period.', 'Enter how many, more than zero.',
    'Enter an amount more than zero.', 'Enter a whole number.', 'Use at most two decimals.']) {
    expect(ARABIC.test(translate('ar', message)), message).toBe(true);
  }
});

test('kinds, ids and figures', () => {
  expect([usageKind('AIR_TICKET'), usageKind('education'), usageKind('BEN_GYM')]).toEqual(['ticket', 'education', 'other']);
  expect(newUsageId()).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  expect(measureText(2, 'Units', null, 'en')).toBe('2');
  expect(measureText(12500, 'Amount', 'SAR', 'en')).toContain('12,500');
});

test('the screen says plainly that approving a use pays nothing, and where a payment is made', () => {
  const note = INFO_KEYS.paysNothing;
  expect(note).toMatch(/pays nothing|no money/i);
  expect(note).toMatch(/payroll/i);
  expect(ARABIC.test(translate('ar', note))).toBe(true);
});

test('a benefit for family members with none on file says so instead of showing an unexplained zero', () => {
  const education = row({ benefitCode: 'EDUCATION', dependantScope: 'Children', measure: 'Amount', currency: 'SAR', entitlement: 0, remaining: 0, perBeneficiary: 5000, beneficiaries: [], canRequest: false });
  const key = noFamilyNoteKey(education);
  expect(key).toBeTruthy();
  expect(ARABIC.test(translate('ar', key!))).toBe(true);
  expect(noFamilyNoteKey(row())).toBeNull();
  expect(noFamilyNoteKey(row({ dependantScope: 'None', maxDependants: null, perBeneficiary: null, beneficiaries: [] }))).toBeNull();
});
