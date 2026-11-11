import { expect, test } from '@playwright/test';
import { SALARY_BREAKDOWN_EDIT_FIELDS } from '../src/api/employeeFieldCatalog';
import { releaseASlices } from '../src/i18n/releaseA';
import { retryOnGradeStructure, structureFollowsGrade } from '../src/lib/employeeEditDraft';
import { gradeForCode, gradePayVerdict, monthlyPayFromDraft, todaysPayAlsoChecked } from '../src/lib/gradePayRange';

// Edit employee, salary step: the salary structure follows the grade, and the pay is shown against the grade it will be judged by.

const salary = { basicSalary: 30000, housingAllowance: 7500, transportAllowance: 3000, foodAllowance: 0, mobileAllowance: 0, otherAllowance: 350, fixedDeduction: 0, currency: 'SAR', effectiveDate: '2026-10-31', salaryStructureCode: 'GRADE-G3' };

test('HR is not offered a salary structure on the edit form', () => {
  const field = SALARY_BREAKDOWN_EDIT_FIELDS.find((f) => f.key === 'salaryBreakdown.salaryStructureCode');
  expect(field?.hidden).toBe(true);
  expect(SALARY_BREAKDOWN_EDIT_FIELDS.filter((f) => !f.hidden).map((f) => f.label)).not.toContain('Salary structure');
});

test('a salary change keeps the current structure when the grade does not change', () => {
  const changes = structureFollowsGrade({ salaryBreakdown: salary }, { gradeChanged: false, currentStructureStale: false });
  expect(changes.salaryBreakdown).toEqual(salary);
});

test('a salary change with a grade change goes on the new grade’s own structure', () => {
  const changes = structureFollowsGrade({ grade: 'G5', salaryBreakdown: salary }, { gradeChanged: true, currentStructureStale: false });
  expect(changes).toEqual({ grade: 'G5', salaryBreakdown: { ...salary, salaryStructureCode: '' } });
});

test('a structure no longer valid is replaced by the grade’s own, never sent back', () => {
  const changes = structureFollowsGrade({ salaryBreakdown: { ...salary, salaryStructureCode: 'OPS-OLD' } }, { gradeChanged: false, currentStructureStale: true });
  expect((changes.salaryBreakdown as typeof salary).salaryStructureCode).toBe('');
});

test('an edit without a salary package is left alone, even with a grade change', () => {
  expect(structureFollowsGrade({ grade: 'G5' }, { gradeChanged: true, currentStructureStale: true })).toEqual({ grade: 'G5' });
});

test('the range test matches the server: min and max, a side at 0 is not checked', () => {
  expect(gradePayVerdict({ minSalary: 20000, maxSalary: 40000 }, 40850)).toBe('above');
  expect(gradePayVerdict({ minSalary: 20000, maxSalary: 40000 }, 19999)).toBe('below');
  expect(gradePayVerdict({ minSalary: 20000, maxSalary: 40000 }, 40000)).toBe('inside');
  expect(gradePayVerdict({ minSalary: 0, maxSalary: 40000 }, 10)).toBe('inside');
  expect(gradePayVerdict({ minSalary: 20000, maxSalary: 0 }, 900000)).toBe('inside');
  expect(gradePayVerdict({ minSalary: 0, maxSalary: 0 }, 5)).toBe('noRange');
});

test('monthly pay is basic plus allowances plus the other monthly allowance lines, without the fixed deduction', () => {
  const form = {
    'salaryBreakdown.basicSalary': '30000', 'salaryBreakdown.housingAllowance': '7500', 'salaryBreakdown.transportAllowance': '3000',
    'salaryBreakdown.foodAllowance': '0', 'salaryBreakdown.mobileAllowance': '', 'salaryBreakdown.otherAllowance': '350', 'salaryBreakdown.fixedDeduction': '900',
  };
  expect(monthlyPayFromDraft(form, [])).toBe(40850);
  expect(monthlyPayFromDraft(form, [
    { key: 'a', code: 'ALW_MEAL', basis: 'Amount', value: '150' },
    { key: 'b', code: 'ALW_EDU', basis: 'PercentOfBasic', value: '10' },
  ])).toBe(40850 + 150 + 3000);
});

test('the grade is the employee’s company’s own first, then a group-wide one, by code in any case', () => {
  const grades = [
    { code: 'G5', minSalary: 1, maxSalary: 2, companyId: 'other' },
    { code: 'g5', minSalary: 3, maxSalary: 4, companyId: null },
    { code: 'G5', minSalary: 5, maxSalary: 6, companyId: 'mine' },
  ];
  expect(gradeForCode(grades, 'G5', 'mine')?.minSalary).toBe(5);
  expect(gradeForCode(grades, ' g5 ', 'third')?.minSalary).toBe(3);
  expect(gradeForCode(grades.slice(0, 1), 'G5', 'mine')).toBeUndefined();
  expect(gradeForCode(grades, '', 'mine')).toBeUndefined();
});

test('a refused current structure is sent once more on the grade’s own, and nothing else is retried', () => {
  const refused = (code: unknown, status = 422) => ({ response: { status, data: { code: 'inactive_salary_structure', salaryStructureCode: code } } });
  const changes = { salaryBreakdown: { ...salary, salaryStructureCode: 'OPS-OLD' } };
  expect(retryOnGradeStructure(changes, refused('ops-old'))).toEqual({ salaryBreakdown: { ...salary, salaryStructureCode: '' } });
  expect(retryOnGradeStructure(changes, refused('OTHER'))).toBeNull();
  expect(retryOnGradeStructure(changes, refused('OPS-OLD', 400))).toBeNull();
  expect(retryOnGradeStructure(changes, { response: { status: 422, data: { message: 'Salary package is outside grade G5’s salary range.' } } })).toBeNull();
  expect(retryOnGradeStructure({ salaryBreakdown: { ...salary, salaryStructureCode: '' } }, refused(''))).toBeNull();
  expect(retryOnGradeStructure({ grade: 'G5' }, refused('OPS-OLD'))).toBeNull();
});

test('a grade change is judged against today’s pay too when the edited package is not the pay in force today', () => {
  const at = (packageDate: string, extra: Partial<Parameters<typeof todaysPayAlsoChecked>[0]> = {}) =>
    todaysPayAlsoChecked({ gradeChanged: true, payEdited: true, packageDate, recordDate: '2026-10-01', today: '2026-11-11', ...extra });
  expect(at('2026-12-01')).toBe(true); // starts later
  expect(at('2026-09-01')).toBe(true); // before the record in force
  expect(at('2026-11-11')).toBe(false);
  expect(at('2026-10-01')).toBe(false);
  expect(at('2026-12-01', { gradeChanged: false })).toBe(false);
  expect(at('2026-12-01', { payEdited: false })).toBe(false); // untouched pay is today's pay, already judged
  expect(at('2026-12-01', { recordDate: '' })).toBe(false); // no record
  expect(at('2026-12-01', { recordDate: '2026-11-20' })).toBe(false); // the record is not in force yet: the server finds none to judge
});

test('every new sentence has an Arabic translation', () => {
  const ar = releaseASlices.salaryAllowances.ar;
  for (const key of [
    'Add allowances in Benefits by grade', 'Ask an administrator to add them in Benefits by grade.', 'Grade changes from {from} to {to}.',
    'The pay below is the current pay. It stays as it is unless you change it.', '{grade} salary range: {min} to {max} a month.',
    '{grade} has no salary range set.', '{grade} is not an active grade of this company, so its salary range cannot be shown.',
    'The monthly pay of {amount} is inside this range.', 'The monthly pay of {amount} is below this range.', 'The monthly pay of {amount} is above this range.',
    'Change the pay before saving, or the grade change will be refused.', 'Change the pay before saving, or the salary change will be refused.',
    'The new pay does not start today, so the grade change is also checked against today’s pay of {amount}, which is outside this range. Date the new pay from {from} to today, or the grade change will be refused.',
  ]) {
    expect(ar[key], key).toBeTruthy();
    for (const slot of key.match(/\{\w+\}/g) ?? []) expect(ar[key], `${key} keeps ${slot}`).toContain(slot);
  }
});
