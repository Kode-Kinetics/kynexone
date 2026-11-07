import { expect, test } from '@playwright/test';
import type { EntitlementMatrix, MatrixCell, MatrixComponent } from '../src/api/entitlements';
import type { EmployeeDetail } from '../src/api/employees';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { releaseASlices } from '../src/i18n/releaseA';
import { buildSalaryBreakdownChange, ALLOWANCES_KEY } from '../src/lib/employeeEditDraft';
import { emptyHireForm } from '../src/lib/hire/hireForm';
import { formFromEmployee } from '../src/lib/hire/draftHydrate';
import { validateHire } from '../src/lib/hire/hireValidation';
import { buildCreateRequest } from '../src/lib/hire/payload';
import {
  allowanceChoices, allowanceProblems, allowanceRefusal, allowancesForRequest, allowancesNeedBasic, compareAllowances, currentRecordLines, decodeValue, describeLines, encodeValue,
  gradeAllowanceLines, lineMonthly, lineName, linesFromWire, nameOf, rateFromPercent, requestLines, sameLines, totalMonthly, valueAfterEdit,
  type AllowanceDraftLine, type SalaryAllowanceView,
} from '../src/lib/salaryAllowances';

// Monthly cash allowances (ALW_*) on a salary: what is sent, what is shown, and what is left alone.

const line = (code: string, basis: 'Amount' | 'PercentOfBasic', value: string): AllowanceDraftLine => ({ key: `k-${code}`, code, basis, value });
const meal = line('ALW_MEAL', 'Amount', '300');
const car = line('ALW_CAR', 'PercentOfBasic', '10');

test('an untouched section sends nothing at all, so the server carries the lines forward; only an edit sends a list', () => {
  expect(allowancesForRequest(null)).toBeUndefined();
  // The property is absent from the JSON, not [] (which would remove every allowance).
  expect(JSON.stringify({ basicSalary: 5000, allowances: allowancesForRequest(null) })).toBe('{"basicSalary":5000}');
  expect(allowancesForRequest([])).toEqual([]);
  expect(allowancesForRequest([meal, car])).toEqual([
    { code: 'ALW_MEAL', basis: 'Amount', amount: 300 },
    { code: 'ALW_CAR', basis: 'PercentOfBasic', rate: 0.1 },
  ]);
});

test('editing back to what the baseline already says is untouched again, and clearing is a deliberate empty list', () => {
  const baseline = [meal, car];
  expect(valueAfterEdit(baseline, [{ ...car, key: 'x' }, { ...meal, key: 'y', value: '300.00' }])).toBeNull();
  expect(valueAfterEdit(baseline, [meal])).toEqual([meal]);
  expect(valueAfterEdit(baseline, [])).toEqual([]);
  // With nothing on the record, an empty section is the baseline (nothing to remove), not an edit.
  expect(valueAfterEdit([], [])).toBeNull();
  expect(sameLines([meal, car], [car, meal])).toBe(true);
  expect(sameLines([meal], [{ ...meal, value: '301' }])).toBe(false);
});

test('a percentage shows the monthly amount it comes to on the basic, to the halala, half away from zero', () => {
  expect(rateFromPercent(10)).toBe(0.1);
  expect(rateFromPercent(12.5)).toBe(0.125);
  expect(lineMonthly(car, 5000)).toBe(500);
  expect(lineMonthly(line('ALW_X', 'PercentOfBasic', '12.5'), 3333)).toBe(416.63); // 416.625
  expect(lineMonthly(car, undefined)).toBeUndefined(); // no basic yet: not worked out
  expect(lineMonthly(meal, undefined)).toBe(300);
  expect(lineMonthly(line('ALW_X', 'Amount', '1,250.50'), undefined)).toBe(1250.5);
  expect(lineMonthly(line('ALW_X', 'Amount', '١٢٠٠'), undefined)).toBe(1200); // Arabic-Indic digits
  expect(totalMonthly([meal, car, line('ALW_BAD', 'Amount', '')], 5000)).toBe(800); // an unfinished line counts for nothing
});

test('what stops a line, by line, mirrors the server', () => {
  expect(allowanceProblems([meal, car], 5000)).toEqual({});
  const found = allowanceProblems([
    line('', 'Amount', '5'),
    line('ALW_A', 'Amount', '0'),
    line('ALW_B', 'Amount', '10.123'),
    line('ALW_C', 'PercentOfBasic', '101'),
    line('ALW_D', 'PercentOfBasic', 'abc'),
    line('ALW_MEAL', 'Amount', '5'),
    line('alw_meal', 'Amount', '6'),
    line('ALW_TINY', 'PercentOfBasic', '0.01'),
  ], 10);
  expect(Object.values(found)).toEqual([
    'Choose the allowance for this line.',
    'Enter a monthly amount above zero, with at most two decimal places.',
    'Enter a monthly amount above zero, with at most two decimal places.',
    'Enter a percentage above 0 and up to 100, with at most two decimal places.',
    'Enter a percentage above 0 and up to 100, with at most two decimal places.',
    'This allowance is on the salary twice. Keep one line for it.',
    'This percentage of the basic salary comes to nothing. Use a fixed amount instead.',
  ]);
});

test('the edit form keeps the section as text: empty is untouched, and it round-trips', () => {
  expect(encodeValue(null)).toBe('');
  expect(decodeValue('')).toBeNull();
  expect(decodeValue(undefined)).toBeNull();
  expect(decodeValue('not json')).toBeNull();
  expect(decodeValue(encodeValue([]))).toEqual([]);
  expect(decodeValue(encodeValue([meal, car]))).toEqual([meal, car]);
});

const salaryForm = (extra: Record<string, string> = {}) => ({
  'salaryBreakdown.basicSalary': '5000', 'salaryBreakdown.housingAllowance': '1000', 'salaryBreakdown.transportAllowance': '0',
  'salaryBreakdown.foodAllowance': '0', 'salaryBreakdown.mobileAllowance': '0', 'salaryBreakdown.otherAllowance': '0', 'salaryBreakdown.fixedDeduction': '0',
  'salaryBreakdown.effectiveDate': '2026-12-01', 'salaryBreakdown.currency': 'SAR', 'salaryBreakdown.salaryStructureCode': '', ...extra,
});

test('the edit form: a basic-only change sends no allowances, an edit sends the lines, clearing sends []', () => {
  expect(buildSalaryBreakdownChange(salaryForm({ [ALLOWANCES_KEY]: '' }))).not.toHaveProperty('allowances');
  expect(buildSalaryBreakdownChange(salaryForm())).not.toHaveProperty('allowances');
  expect(buildSalaryBreakdownChange(salaryForm({ [ALLOWANCES_KEY]: encodeValue([meal, car]) })).allowances).toEqual([
    { code: 'ALW_MEAL', basis: 'Amount', amount: 300 }, { code: 'ALW_CAR', basis: 'PercentOfBasic', rate: 0.1 },
  ]);
  expect(buildSalaryBreakdownChange(salaryForm({ [ALLOWANCES_KEY]: encodeValue([]) })).allowances).toEqual([]);
  expect(() => buildSalaryBreakdownChange(salaryForm({ [ALLOWANCES_KEY]: encodeValue([line('ALW_MEAL', 'Amount', '0')]) }))).toThrow(/allowance lines/);
  // The fixed deduction is checked against the whole gross, allowances included.
  expect(() => buildSalaryBreakdownChange(salaryForm({ 'salaryBreakdown.fixedDeduction': '6500' }))).toThrow(/cannot exceed/);
  expect(() => buildSalaryBreakdownChange(salaryForm({ 'salaryBreakdown.fixedDeduction': '6500', [ALLOWANCES_KEY]: encodeValue([meal, car]) }))).not.toThrow();
});

test('the hire wizard: untouched sends no allowances (the server fills from the grade); touched sends the lines', () => {
  const ctx = { companyCountry: 'SA', currency: 'SAR' };
  const base = { ...emptyHireForm(), englishName: 'Test Person', basic: '5000' };
  expect(buildCreateRequest(base, ctx).salaryBreakdown).toEqual({ basicSalary: 5000, currency: 'SAR' });
  expect(buildCreateRequest(base, ctx).salaryBreakdown).not.toHaveProperty('allowances');
  expect(buildCreateRequest({ ...base, allowances: encodeValue([meal]) }, ctx).salaryBreakdown)
    .toEqual({ basicSalary: 5000, currency: 'SAR', allowances: [{ code: 'ALW_MEAL', basis: 'Amount', amount: 300 }] });
  expect(buildCreateRequest({ ...base, allowances: encodeValue([]) }, ctx).salaryBreakdown).toHaveProperty('allowances', []);
  // No pay typed at all and no edit: still no salary breakdown.
  expect(buildCreateRequest({ ...emptyHireForm(), englishName: 'Test Person' }, ctx).salaryBreakdown).toBeUndefined();
  const issues = validateHire({ ...base, allowances: encodeValue([line('ALW_MEAL', 'Amount', '')]) }, { today: '2026-11-05', companyCountry: 'SA' }, 'create');
  expect(issues.find(i => i.field === 'allowances')?.key).toBe('Fix the allowance lines that show a message before saving.');
  expect(validateHire({ ...base, allowances: encodeValue([meal]) }, { today: '2026-11-05', companyCountry: 'SA' }, 'create').some(i => i.field === 'allowances')).toBe(false);
});

test('resuming a draft keeps the lines already staged with its package as the person\'s own', () => {
  const ctx = { companyCountry: 'SA' };
  const staged = { id: 7, status: 'Draft', englishName: 'Test Person', salaryBreakdown: { basicSalary: 5000, allowances: [{ code: 'ALW_MEAL', basis: 'Amount', amount: 300 }] } } as unknown as EmployeeDetail;
  expect(decodeValue(formFromEmployee(staged, ctx).allowances)?.map(l => [l.code, l.basis, l.value])).toEqual([['ALW_MEAL', 'Amount', '300']]);
  const plain = { id: 7, status: 'Draft', englishName: 'Test Person', salaryBreakdown: { basicSalary: 5000 } } as unknown as EmployeeDetail;
  expect(formFromEmployee(plain, ctx).allowances).toBe('');
});

const component = (code: string, extra: Partial<MatrixComponent> = {}): MatrixComponent => ({
  code, nameEn: code, nameAr: code, class: 'Allowance', floor: 'None', group: 'Wage', allowedValueTypes: ['Amount', 'PercentOfBasic'], allowedCoverageTiers: [],
  allowedDependantScopes: [], allowedLimitPeriods: [], defaultLimitPeriod: null, isFloor: false, canBeSkipped: true, isLoanFacility: false, floorReason: null, ...extra,
});
const cell = (gradeId: string, componentCode: string, extra: Partial<MatrixCell>): MatrixCell => ({
  gradeId, componentCode, cellId: `${gradeId}-${componentCode}`, isCompanyOverride: false, inherited: false, eligible: true, valueType: 'Amount', amount: null, rate: null,
  maxOutstandingAmount: null, coverageTier: null, quantity: null, dependantScope: 'None', maxDependants: null, limitPeriod: null, minServiceMonths: null, afterProbation: false,
  nationalityScope: 'Any', nationalityBasis: null, sourceRule: null, note: null, effectiveFrom: '2026-01-01', effectiveTo: null, nextChangeOn: null, ...extra,
});

test('the definitions to choose from are the customer-added ALW_ allowances, by name in the reader\'s language', () => {
  const components = [
    component('ALW_MEAL', { isCustom: true, nameEn: 'Meal', nameAr: 'وجبات' }),
    component('ALW_CAR', { isCustom: true, nameEn: 'Car', nameAr: 'سيارة' }),
    component('BEN_PARKING', { isCustom: true, group: 'Contract' }),
    component('HOUSING'),
    component('ALW_BUILTIN', { isCustom: false }),
  ];
  expect(allowanceChoices(components, 'en').map(c => c.code)).toEqual(['ALW_CAR', 'ALW_MEAL']);
  expect(nameOf(allowanceChoices(components, 'ar')[0], 'ar')).toBe('سيارة');
  expect(nameOf({ code: 'ALW_X', nameEn: '', nameAr: 'اسم' }, 'en')).toBe('اسم');
  expect(nameOf({ code: 'ALW_X' }, 'ar')).toBe('ALW_X');
});

test('the grade fills the lines the way the server does: only offered cash allowances, as an amount or a percentage', () => {
  const matrix: Pick<EntitlementMatrix, 'components' | 'cells'> = {
    components: [component('ALW_MEAL', { isCustom: true }), component('ALW_CAR', { isCustom: true }), component('ALW_NONE', { isCustom: true }), component('HOUSING')],
    cells: [
      cell('g1', 'ALW_MEAL', { valueType: 'Amount', amount: 300 }),
      cell('g1', 'ALW_CAR', { valueType: 'PercentOfBasic', rate: 0.1 }),
      cell('g1', 'ALW_NONE', { eligible: false, valueType: 'Amount', amount: 50 }),
      cell('g1', 'HOUSING', { valueType: 'PercentOfBasic', rate: 0.25 }),
      cell('g2', 'ALW_MEAL', { valueType: 'Amount', amount: 999 }),
    ],
  };
  expect(gradeAllowanceLines(matrix, 'g1').map(l => [l.code, l.basis, l.value])).toEqual([['ALW_CAR', 'PercentOfBasic', '10'], ['ALW_MEAL', 'Amount', '300']]);
  expect(gradeAllowanceLines(matrix, 'nope')).toEqual([]);
});

const view = (code: string, basis: 'Amount' | 'PercentOfBasic', amount: number, rate: number | null = null): SalaryAllowanceView => ({ code, nameEn: code, nameAr: code, basis, rate, amount });

test('the approval comparison names every difference, and an unchanged line is unchanged', () => {
  const rows = compareAllowances(
    [view('ALW_MEAL', 'Amount', 300), view('ALW_CAR', 'PercentOfBasic', 500, 0.1), view('ALW_OLD', 'Amount', 80)],
    [view('ALW_MEAL', 'Amount', 300), view('ALW_CAR', 'PercentOfBasic', 600, 0.1), view('ALW_NEW', 'Amount', 40)],
  );
  expect(rows.map(r => [r.code, r.change])).toEqual([['ALW_CAR', 'changed'], ['ALW_MEAL', 'unchanged'], ['ALW_NEW', 'added'], ['ALW_OLD', 'removed']]);
  expect(compareAllowances(undefined, undefined)).toEqual([]);
  expect(compareAllowances([view('ALW_MEAL', 'Amount', 300)], [view('ALW_MEAL', 'Amount', 300)]).every(r => r.change === 'unchanged')).toBe(true);
});

test('describing lines for a review reads in the reader\'s words', () => {
  const words = { none: 'None', name: (l: AllowanceDraftLine) => l.code, money: (n: number) => n.toFixed(2), amount: (a: string) => `${a}/m`, percent: (r: string, a: string) => `${r}% = ${a}/m` };
  expect(describeLines([], 5000, words)).toBe('None');
  expect(describeLines([meal, car], 5000, words)).toBe('ALW_MEAL: 300.00/m; ALW_CAR: 10% = 500.00/m');
  expect(describeLines([car], undefined, words)).toBe('ALW_CAR: 10% = —/m');
});

test('a line is named by the server first, then the catalogue (per language), then the code', () => {
  const choices = [{ code: 'ALW_MEAL', nameEn: 'Meal (catalogue)', nameAr: 'وجبة (الفهرس)' }];
  // A reader without access to the catalogue (no choices) still sees the server's name.
  expect(lineName({ code: 'ALW_MEAL', nameEn: 'Meal', nameAr: 'وجبة' }, [], 'en')).toBe('Meal');
  expect(lineName({ code: 'ALW_MEAL', nameEn: 'Meal', nameAr: 'وجبة' }, [], 'ar')).toBe('وجبة');
  expect(lineName({ code: 'ALW_MEAL', nameEn: 'Meal', nameAr: 'وجبة' }, choices, 'en')).toBe('Meal');
  // The name is per language: a missing Arabic comes from the catalogue, and with neither it falls back to the English, then the code.
  expect(lineName({ code: 'alw_meal', nameEn: 'Meal' }, choices, 'ar')).toBe('وجبة (الفهرس)');
  expect(lineName({ code: 'ALW_MEAL', nameEn: 'Meal' }, [], 'ar')).toBe('Meal');
  expect(lineName({ code: 'ALW_MEAL' }, choices, 'en')).toBe('Meal (catalogue)');
  // The definition is gone: the server omits the names and the catalogue has nothing, so the code stands in.
  expect(lineName({ code: 'ALW_GONE' }, choices, 'en')).toBe('ALW_GONE');
  expect(lineName({ code: 'ALW_GONE', nameEn: '  ', nameAr: null }, [], 'ar')).toBe('ALW_GONE');
});

test('the names the server sends survive into the rows and the edit form, and never go back out on a request', () => {
  const rows = linesFromWire([{ code: 'ALW_MEAL', basis: 'Amount', amount: 300, nameEn: 'Meal', nameAr: 'وجبة' }, { code: 'ALW_GONE', basis: 'Amount', amount: 50 }]);
  expect(rows[0]).toMatchObject({ nameEn: 'Meal', nameAr: 'وجبة' });
  expect(rows[1].nameEn).toBeUndefined();
  expect('nameEn' in rows[1]).toBe(false);
  expect(decodeValue(encodeValue(rows))?.[0]).toMatchObject({ code: 'ALW_MEAL', nameEn: 'Meal', nameAr: 'وجبة' });
  expect(requestLines(rows)).toEqual([{ code: 'ALW_MEAL', basis: 'Amount', amount: 300 }, { code: 'ALW_GONE', basis: 'Amount', amount: 50 }]);
  // Names do not make two otherwise identical sections different.
  expect(sameLines(rows, linesFromWire([{ code: 'ALW_MEAL', basis: 'Amount', amount: 300 }, { code: 'ALW_GONE', basis: 'Amount', amount: 50 }]))).toBe(true);
});

test('Assign Salary takes the current lines from the active record in force on the salary list', () => {
  const rec = (over: Partial<Parameters<typeof currentRecordLines>[0][number]>) => ({ isActive: true, effectiveDate: '2026-01-01', createdAtUtc: '2026-01-01T00:00:00Z', allowances: [], ...over });
  const named = [{ code: 'ALW_MEAL', basis: 'Amount' as const, amount: 300, nameEn: 'Meal', nameAr: 'وجبة' }];
  // The latest effective date among the active records wins, then the latest created; an inactive record is never "now".
  const rows = currentRecordLines([
    rec({ effectiveDate: '2025-01-01', allowances: [{ code: 'ALW_OLD', basis: 'Amount', amount: 1 }] }),
    rec({ effectiveDate: '2026-03-01', allowances: named }),
    rec({ effectiveDate: '2026-09-01', isActive: false, allowances: [{ code: 'ALW_INACTIVE', basis: 'Amount', amount: 9 }] }),
  ]);
  expect(rows?.map(r => [r.code, r.nameEn])).toEqual([['ALW_MEAL', 'Meal']]);
  expect(currentRecordLines([rec({ createdAtUtc: '2026-01-01T00:00:00Z', allowances: named }), rec({ createdAtUtc: '2026-01-02T00:00:00Z', allowances: [] })])).toEqual([]);
  // A record that carries none is known to carry none; no active record at all leaves nothing to carry forward.
  expect(currentRecordLines([rec({ allowances: [] })])).toEqual([]);
  expect(currentRecordLines([])).toEqual([]);
  expect(currentRecordLines([rec({ isActive: false })])).toEqual([]);
  // Masked (viewer may not see amounts) or an older server without the field: not known, so the section leaves the lines as they are.
  expect(currentRecordLines([rec({ allowances: null })])).toBeNull();
  expect(currentRecordLines([rec({ allowances: undefined })])).toBeNull();
});

test('lines the person entered need a basic salary first; lines the grade will fill in do not', () => {
  expect(allowancesNeedBasic(null, undefined)).toBe(false); // untouched: the server fills from the grade or carries forward
  expect(allowancesNeedBasic([], undefined)).toBe(false); // an empty list removes lines, nothing to work out
  expect(allowancesNeedBasic([meal], undefined)).toBe(true);
  expect(allowancesNeedBasic([car], 0)).toBe(true);
  expect(allowancesNeedBasic([meal], 5000)).toBe(false);
});

test('the hire wizard refuses entered allowance lines until the basic salary is in, on the basic salary field', () => {
  const ctx = { today: '2026-11-05', companyCountry: 'SA' };
  const form = { ...emptyHireForm(), englishName: 'Test Person', companyId: 'c1', allowances: encodeValue([meal]) };
  const blocked = validateHire({ ...form, basic: '' }, ctx).filter(i => i.field === 'basic');
  expect(blocked.map(i => i.key)).toEqual(['Enter the basic salary before adding monthly allowances.']);
  expect(blocked[0].step).toBe(3);
  expect(validateHire({ ...form, basic: '5000' }, ctx).filter(i => i.field === 'basic')).toEqual([]);
  // Untouched lines (the grade's) are not entered by the person: no block.
  expect(validateHire({ ...form, allowances: '', basic: '' }, ctx).filter(i => i.field === 'basic')).toEqual([]);
});

test('lines on a record become rows with stable keys and a percentage the way people write it', () => {
  const rows = linesFromWire([{ code: 'alw_car', basis: 'PercentOfBasic', rate: 0.125, amount: 625 }, { code: 'ALW_MEAL', basis: 'Amount', amount: 300 }]);
  expect(rows.map(r => [r.code, r.basis, r.value])).toEqual([['ALW_CAR', 'PercentOfBasic', '12.5'], ['ALW_MEAL', 'Amount', '300']]);
  expect(linesFromWire(null)).toEqual([]);
  expect(linesFromWire([{ code: 'ALW_MEAL', basis: 'Amount', amount: 300 }])[0].key).toBe(linesFromWire([{ code: 'ALW_MEAL', basis: 'Amount', amount: 300 }])[0].key);
  expect(requestLines(rows)).toEqual([{ code: 'ALW_CAR', basis: 'PercentOfBasic', rate: 0.125 }, { code: 'ALW_MEAL', basis: 'Amount', amount: 300 }]);
});

test('the server\'s refusals of an allowance are recognised, and nothing else is claimed', () => {
  const refusal = (status: number, error: string, message: string) => ({ response: { status, data: { error, message } } });
  expect(allowanceRefusal(refusal(400, 'salary_allowance_invalid', 'ALW_X is on this salary twice.'))).toEqual({ code: 'salary_allowance_invalid', message: 'ALW_X is on this salary twice.' });
  expect(allowanceRefusal(refusal(422, 'salary_allowance_unknown', 'ALW_X was removed.'))?.code).toBe('salary_allowance_unknown');
  expect(allowanceRefusal(refusal(409, 'salary_change_stale', 'Reject it.'))?.code).toBe('salary_change_stale');
  expect(allowanceRefusal(refusal(400, 'something_else', 'x'))).toBeNull();
  expect(allowanceRefusal(new Error('network'))).toBeNull();
});

test('every string the allowance screens show has an Arabic that is not the English, including the ones looked up at run time', () => {
  const dynamic = ['New', 'Removed', 'Changed', 'Unchanged', 'None', 'Allowance', 'Proposed', 'Difference', 'On the salary now', 'Monthly allowances',
    'Fixed amount', '% of basic salary', 'Remove', 'Try again', '{rate}% of basic = {amount} a month', '{amount} a month'];
  const keys = [...Object.keys(releaseASlices.salaryAllowances.en), ...dynamic];
  for (const key of keys) {
    const en = (LOCALE_DICTS.en as Record<string, string>)[key];
    const ar = (LOCALE_DICTS.ar as Record<string, string>)[key];
    expect(en, `en: ${key}`).toBeTruthy();
    expect(ar, `ar: ${key}`).toBeTruthy();
    expect(ar, `ar differs: ${key}`).not.toBe(en);
    expect(/^[A-Z{%]/.test(key), `key starts upper-case: ${key}`).toBe(true);
    expect(/[:,]$/.test(key), `key ends in : or ,: ${key}`).toBe(false);
  }
});
