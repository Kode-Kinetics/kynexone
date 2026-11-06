import { expect, test } from '@playwright/test';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { ALLOWANCES_KEY, REDUCTION_REFERENCE_KEY, buildEmployeeEditChanges, buildSalaryBreakdownChange, buildEmployeeEditSnapshot } from '../src/lib/employeeEditDraft';
import type { EmployeeDetail } from '../src/api/employees';
import type { AllowanceDraftLine, SalaryAllowanceView } from '../src/lib/salaryAllowances';
import {
  CONSENT_REFERENCE_MAX, NO_REDUCTION, SLOT_LABEL_KEYS, carriedLines, compareReduction, consentReferenceForRequest, consentReferenceProblem, proposalReduction,
  recordInForce, reductionBlock, reductionRefusal, type PayPackage, type RecordInForce, type SlotNumbers,
} from '../src/lib/salaryReduction';

// Lowering pay needs the employee's written agreement. These mirror the server rule (SalaryReductions.Compare) so the form can say so first.

const slots = (over: Partial<SlotNumbers> = {}): SlotNumbers => ({ basicSalary: 5000, housingAllowance: 1250, transportAllowance: 500, foodAllowance: 0, mobileAllowance: 0, otherAllowance: 0, ...over });
const pkg = (over: Partial<SlotNumbers> = {}, lines: { code: string; amount: number }[] = []): PayPackage => {
  const s = slots(over);
  return { slots: { BASIC: s.basicSalary, HOUSING: s.housingAllowance, TRANSPORT: s.transportAllowance, FOOD: s.foodAllowance, MOBILE: s.mobileAllowance, OTHER: s.otherAllowance }, lines };
};
const meal: SalaryAllowanceView = { code: 'ALW_MEAL', basis: 'Amount', amount: 300, rate: null };
const car: SalaryAllowanceView = { code: 'ALW_CAR', basis: 'PercentOfBasic', rate: 0.1, amount: 500 };
const record = (over: Partial<RecordInForce> = {}): RecordInForce => ({ ...slots(), allowances: [meal, car], ...over });
const line = (code: string, basis: 'Amount' | 'PercentOfBasic', value: string): AllowanceDraftLine => ({ key: `k-${code}`, code, basis, value });

test('a first package, or any raise, is never a reduction', () => {
  expect(compareReduction(null, pkg())).toBe(NO_REDUCTION);
  const raise = compareReduction(pkg(), pkg({ basicSalary: 6000, housingAllowance: 1500 }));
  expect(raise).toMatchObject({ isReduction: false, items: [], changesContractWage: true, currentTotal: 6750, proposedTotal: 8000 });
  expect(compareReduction(pkg(), pkg())).toMatchObject({ isReduction: false, changesContractWage: false });
});

test('the whole monthly pay going down is a reduction, and the item that went down is named', () => {
  const r = compareReduction(pkg(), pkg({ basicSalary: 4500 }));
  expect(r).toMatchObject({ isReduction: true, currentTotal: 6750, proposedTotal: 6250, changesContractWage: true });
  expect(r.items).toEqual([{ item: 'BASIC', from: 5000, to: 4500 }]);
});

test('any single item going down is a reduction even when the total goes up', () => {
  const r = compareReduction(pkg(), pkg({ basicSalary: 7000, transportAllowance: 0 }));
  expect(r.isReduction).toBe(true);
  expect(r.items).toEqual([{ item: 'TRANSPORT', from: 500, to: 0 }]);
  expect(r.proposedTotal).toBeGreaterThan(r.currentTotal);
});

test('an allowance line lowered or removed counts, a new one does not, and codes compare regardless of case', () => {
  const now = pkg({}, [{ code: 'ALW_MEAL', amount: 300 }, { code: 'ALW_CAR', amount: 500 }]);
  expect(compareReduction(now, pkg({}, [{ code: 'alw_meal', amount: 250 }, { code: 'ALW_CAR', amount: 500 }])).items).toEqual([{ item: 'ALW_MEAL', from: 300, to: 250 }]);
  const removed = compareReduction(now, pkg({}, [{ code: 'ALW_CAR', amount: 500 }]));
  expect(removed.items).toEqual([{ item: 'ALW_MEAL', from: 300, to: 0 }]);
  const added = compareReduction(pkg(), pkg({}, [{ code: 'ALW_PHONE', amount: 40 }]));
  expect(added).toMatchObject({ isReduction: false, changesContractWage: true });
});

test('money noise below a halala is not a reduction', () => {
  expect(compareReduction(pkg({ basicSalary: 1000.1 }), pkg({ basicSalary: 1000.1000001 })).isReduction).toBe(false);
});

test('the fixed deduction is not pay: it is not even part of the comparison', () => {
  // There is no deduction slot to lower; raising a deduction never makes a package a reduction.
  expect(Object.keys(pkg().slots)).toEqual(['BASIC', 'HOUSING', 'TRANSPORT', 'FOOD', 'MOBILE', 'OTHER']);
});

test('an untouched allowance section carries the lines, a percentage worked out again on the new basic', () => {
  expect(carriedLines([meal, car], 4000)).toEqual([{ code: 'ALW_MEAL', amount: 300 }, { code: 'ALW_CAR', amount: 400 }]);
  // The slots are unchanged: nothing to say.
  expect(proposalReduction(record(), slots(), null)).toMatchObject({ isReduction: false, changesContractWage: false });
  // A lower basic lowers the carried percentage line as well, so it is named with the basic.
  const r = proposalReduction(record(), slots({ basicSalary: 4000 }), null);
  expect(r.isReduction).toBe(true);
  expect(r.items.map(i => i.item)).toEqual(['BASIC', 'ALW_CAR']);
  expect(r.items[1]).toEqual({ item: 'ALW_CAR', from: 500, to: 400 });
});

test('an edited section is exactly the lines shown: dropping a line lowers the pay, a line being typed changes nothing yet', () => {
  expect(proposalReduction(record(), slots(), [line('ALW_MEAL', 'Amount', '300')]).items).toEqual([{ item: 'ALW_CAR', from: 500, to: 0 }]);
  expect(proposalReduction(record(), slots(), [line('ALW_MEAL', 'Amount', '350'), line('ALW_CAR', 'PercentOfBasic', '10')])).toMatchObject({ isReduction: false, changesContractWage: true });
  // A blank or half-typed amount keeps the current amount until it is a number: no false alarm while typing.
  expect(proposalReduction(record(), slots(), [line('ALW_MEAL', 'Amount', ''), line('ALW_CAR', 'PercentOfBasic', '10')]).isReduction).toBe(false);
  // A line with no allowance chosen yet is ignored.
  expect(proposalReduction(record(), slots(), [line('', 'Amount', '5'), line('ALW_MEAL', 'Amount', '300'), line('ALW_CAR', 'PercentOfBasic', '10')]).isReduction).toBe(false);
});

test('with the record’s lines unknown (masked amounts) only the six slots are compared; the server judges the rest', () => {
  const masked = record({ allowances: null });
  expect(proposalReduction(masked, slots({ housingAllowance: 1000 }), null).items).toEqual([{ item: 'HOUSING', from: 1250, to: 1000 }]);
  expect(proposalReduction(masked, slots(), [line('ALW_MEAL', 'Amount', '1')]).isReduction).toBe(false);
  expect(proposalReduction(record({ allowances: undefined }), slots(), null).isReduction).toBe(false);
});

test('nothing in force means nothing to reduce', () => {
  expect(proposalReduction(null, slots({ basicSalary: 1 }), null)).toBe(NO_REDUCTION);
  expect(proposalReduction(undefined, slots({ basicSalary: 1 }), [])).toBe(NO_REDUCTION);
});

test('the record in force is the active one starting on or before the effective date, latest first', () => {
  const rec = (id: string, effectiveDate: string, isActive = true, createdAtUtc = '2026-01-01T00:00:00Z') => ({ id, effectiveDate, isActive, createdAtUtc });
  const list = [rec('old', '2025-01-01'), rec('new', '2026-06-01'), rec('future', '2027-01-01'), rec('off', '2026-09-01', false), rec('same-day-later', '2026-06-01', true, '2026-07-01T00:00:00Z')];
  expect(recordInForce(list, '2026-11-05')?.id).toBe('same-day-later');
  expect(recordInForce(list, '2026-03-01')?.id).toBe('old');
  expect(recordInForce(list, '2024-01-01')).toBeNull();
  expect(recordInForce([], '2026-11-05')).toBeNull();
  expect(recordInForce(list, '')?.id).toBe('future');
});

test('the reference: blank is refused only for a reduction, it is trimmed, kept to 200 characters and sent only when needed', () => {
  expect(reductionBlock({ isReduction: false }, '')).toBeNull();
  expect(reductionBlock({ isReduction: true }, '   ')).toBe('Enter the employee’s written agreement reference before saving.');
  expect(reductionBlock({ isReduction: true }, 'QIWA-123')).toBeNull();
  expect(reductionBlock({ isReduction: true }, 'x'.repeat(CONSENT_REFERENCE_MAX + 1))).toBe('Keep the agreement reference under 200 characters.');
  expect(consentReferenceProblem('x'.repeat(CONSENT_REFERENCE_MAX))).toBeNull();
  expect(consentReferenceForRequest('  QIWA-123 ', true)).toBe('QIWA-123');
  // Typed, then the proposal no longer lowers the pay: not sent.
  expect(consentReferenceForRequest('QIWA-123', false)).toBeUndefined();
  expect(consentReferenceForRequest('   ', true)).toBeUndefined();
});

test('the reference must be a real one: at least 3 characters with a letter or a number (Arabic counts), checked before the server is asked', () => {
  const problem = 'Enter a real reference: at least 3 characters, including a letter or a number.';
  for (const bad of ['x', 'ab', '---', ' . , ', '  x ']) {
    expect(consentReferenceProblem(bad), bad).toBe(problem);
    expect(reductionBlock({ isReduction: true }, bad), bad).toBe(problem);
  }
  for (const good of ['HR-2026-014', 'N/A', 'اتفاقية-٢٠٢٦', 'أ١٢', '123']) expect(consentReferenceProblem(good), good).toBeNull();
  // Blank is "no reference", not "a bad one": the reduction message for that stays as it was.
  expect(consentReferenceProblem('   ')).toBeNull();
  expect(reductionBlock({ isReduction: true }, '   ')).toBe('Enter the employee’s written agreement reference before saving.');
  expect(reductionBlock({ isReduction: false }, 'x')).toBeNull();
  expect(reductionRefusal({ response: { status: 400, data: { error: 'reduction_consent_reference_too_short', message: 'Too short' } } })).toEqual({ kind: 'tooShort', message: 'Too short', items: [] });
});

test('a higher fixed deduction comes back from the server as a reduction item and is read like any other', () => {
  const reducedItems = [{ item: 'FIXED_DEDUCTION', from: 0, to: 300 }];
  expect(reductionRefusal({ response: { status: 400, data: { code: 'reduction_consent_required', message: 'Fixed deduction raised', reducedItems } } }))
    .toMatchObject({ kind: 'required', items: reducedItems });
});

test('the server refusal of a reduction without its reference, on a proposal (400) and on approval (409), carries what goes down', () => {
  const reducedItems = [{ item: 'HOUSING', from: 1250, to: 1000 }, { item: 'ALW_MEAL', from: 300, to: 0 }];
  const proposed = reductionRefusal({ response: { status: 400, data: { error: 'reduction_consent_required', code: 'reduction_consent_required', message: 'Lowering needs agreement (HOUSING 1,250.00 to 1,000.00)', reducedItems } } });
  expect(proposed).toEqual({ kind: 'required', message: 'Lowering needs agreement (HOUSING 1,250.00 to 1,000.00)', items: reducedItems });
  const approval = reductionRefusal({ response: { status: 409, data: { error: 'salary_change_stale', code: 'reduction_consent_required', message: 'x Reject this change.', reducedItems } } });
  expect(approval).toMatchObject({ kind: 'required', items: reducedItems });
  expect(reductionRefusal({ response: { status: 400, data: { error: 'reduction_consent_reference_too_long', message: 'Keep it short' } } })).toEqual({ kind: 'tooLong', message: 'Keep it short', items: [] });
  // Other refusals, including the older stale ones, are not this one.
  expect(reductionRefusal({ response: { status: 409, data: { error: 'salary_change_stale', message: 'out of date' } } })).toBeNull();
  expect(reductionRefusal(new Error('Network Error'))).toBeNull();
  expect(reductionRefusal({ response: { status: 400, data: { error: 'reduction_consent_required', reducedItems: 'nonsense' } } })).toMatchObject({ items: [] });
});

test('every sentence of the reduction notice has Arabic, with the same placeholders', () => {
  const keys = [...Object.values(SLOT_LABEL_KEYS),
    'This lowers the employee’s pay', 'The employee’s written agreement is needed before their pay goes down.', '{item} goes from {from} to {to} a month.',
    '{item} is removed. It was {from} a month.', 'The monthly pay goes from {from} to {to}.', 'Employee’s written agreement reference',
    'For example the signed amendment’s number or the Qiwa request number.', 'Employee’s written agreement reference: {reference}', 'No written agreement reference was given.',
    'Keep the agreement reference under 200 characters.', 'Enter the employee’s written agreement reference before saving.',
    'Lowering an employee’s pay needs their written agreement; enter its reference.', 'This changes the contract wage. Amend the contract on Qiwa and keep the employee’s acceptance.',
    'Monthly pay: {current} now, {proposed} proposed.', 'Enter a real reference: at least 3 characters, including a letter or a number.',
    'At least 3 characters, with a letter or a number.', 'Fixed deduction', '{item} goes up from {from} to {to} a month.'];
  const holes = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort().join();
  for (const key of keys) {
    expect(LOCALE_DICTS.en[key], key).toBeDefined();
    expect(/[؀-ۿ]/.test(LOCALE_DICTS.ar[key] ?? ''), key).toBe(true);
    expect(holes(LOCALE_DICTS.ar[key]), key).toBe(holes(key));
  }
});

// ── The employee edit flow: the reference rides in the salary package ────────────────────────────────────────────────────────

const base: Record<string, string> = {
  'salaryBreakdown.basicSalary': '4500', 'salaryBreakdown.housingAllowance': '1250', 'salaryBreakdown.transportAllowance': '500', 'salaryBreakdown.foodAllowance': '0',
  'salaryBreakdown.mobileAllowance': '0', 'salaryBreakdown.otherAllowance': '0', 'salaryBreakdown.fixedDeduction': '0', 'salaryBreakdown.currency': 'SAR',
  'salaryBreakdown.effectiveDate': '2026-12-01', 'salaryBreakdown.salaryStructureCode': '', [ALLOWANCES_KEY]: '',
};

test('the salary package carries the trimmed agreement reference only when there is one', () => {
  expect(buildSalaryBreakdownChange({ ...base })).not.toHaveProperty('reductionConsentReference');
  expect(buildSalaryBreakdownChange({ ...base, [REDUCTION_REFERENCE_KEY]: '   ' })).not.toHaveProperty('reductionConsentReference');
  expect(buildSalaryBreakdownChange({ ...base, [REDUCTION_REFERENCE_KEY]: ' QIWA-123 ' })).toMatchObject({ basicSalary: 4500, reductionConsentReference: 'QIWA-123' });
  expect(() => buildSalaryBreakdownChange({ ...base, [REDUCTION_REFERENCE_KEY]: 'x'.repeat(201) })).toThrow('Keep the agreement reference under 200 characters.');
});

test('the reference field starts empty in the draft and is never sent as a flat key', () => {
  const employee = { id: 1, countryCode: 'SA', salaryBreakdown: { basicSalary: 5000, currency: 'SAR' }, payrollProfile: {}, complianceRecords: [] } as unknown as EmployeeDetail;
  const fields = [{ key: REDUCTION_REFERENCE_KEY, label: 'Employee’s written agreement reference', section: 'Salary', sensitive: true, hidden: true }, { key: 'salaryBreakdown.basicSalary', label: 'Basic salary', type: 'number' as const, section: 'Salary', sensitive: true }];
  const snapshot = buildEmployeeEditSnapshot(employee, fields, { fields: [] } as never, 'SA');
  expect(snapshot[REDUCTION_REFERENCE_KEY]).toBe('');
  const form = { ...base, [REDUCTION_REFERENCE_KEY]: 'QIWA-123' };
  const changes = buildEmployeeEditChanges(form, fields, ['salaryBreakdown.basicSalary', REDUCTION_REFERENCE_KEY]);
  expect(Object.keys(changes)).toEqual(['salaryBreakdown']);
  expect(changes.salaryBreakdown).toMatchObject({ reductionConsentReference: 'QIWA-123' });
});
