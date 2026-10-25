import { expect, test } from '@playwright/test';
import { applyGradeSalaryDefaults, grossPackage, mergeGradeSalary, monthlySalaryField, roundMoney } from '../src/lib/gradeSalaryDefaults';

/**
 * The create wizard's grade → salary fill. Mirrors the backend rule and its tests
 * (GradePayScaleSalaryMergeTests): blank lines come from the grade, typed ones (0 included) stay, percent lines follow
 * the basic, and only monthly Earning / Deduction lines are monthly salary.
 */
const line = (componentCode: string, opts: Partial<{ amount: number; percentage: number; componentType: string; calculationType: string; frequency: string; isActive: boolean }> = {}) => ({
  componentCode, componentType: 'Earning', calculationType: 'Fixed', amount: 0, percentage: 0, frequency: 'Monthly', isActive: true, ...opts,
});

const demoGrade = [
  line('BASIC', { amount: 4000 }),
  line('HOUSING', { percentage: 25, calculationType: 'PercentOfBasic' }),
  line('TRANSPORT', { amount: 500 }),
  line('MEDICAL', { amount: 300, componentType: 'Benefit' }),
  line('TICKET', { amount: 2400, frequency: 'Annual' }),
];

test('choosing the grade fills basic and every monthly line; benefit and annual lines are not cash', () => {
  const { values, filled } = applyGradeSalaryDefaults({}, {}, demoGrade);
  expect(values).toMatchObject({ basicSalary: 4000, housingAllowance: 1000, transportAllowance: 500 });
  expect(values.otherAllowance).toBeUndefined();
  expect(filled).toEqual({ basicSalary: 4000, housingAllowance: 1000, transportAllowance: 500 });
  expect(grossPackage(values)).toBe(5500);
  expect(monthlySalaryField(demoGrade[3])).toBeNull();
  expect(monthlySalaryField(demoGrade[4])).toBeNull();
});

test('a new basic re-computes the percent lines the user has not overridden', () => {
  const first = applyGradeSalaryDefaults({}, {}, demoGrade);
  const typedBasic = { ...first.values, basicSalary: 4800 };
  const { values } = applyGradeSalaryDefaults(typedBasic, { ...first.filled, basicSalary: undefined }, demoGrade);
  expect(values).toMatchObject({ basicSalary: 4800, housingAllowance: 1200, transportAllowance: 500 });
  expect(grossPackage(values)).toBe(6500);
});

test('a line the user changed is kept, even 0, when the basic or the grade changes', () => {
  const first = applyGradeSalaryDefaults({}, {}, demoGrade);
  const edited = { ...first.values, housingAllowance: 0, transportAllowance: 650 };
  const fill = { ...first.filled, housingAllowance: undefined, transportAllowance: undefined };
  const otherGrade = [line('BASIC', { amount: 6000 }), line('HOUSING', { percentage: 30, calculationType: 'PercentOfBasic' }), line('TRANSPORT', { amount: 800 }), line('FOOD', { amount: 200 })];
  const { values } = applyGradeSalaryDefaults(edited, fill, otherGrade);
  expect(values).toEqual({ basicSalary: 6000, housingAllowance: 0, transportAllowance: 650, foodAllowance: 200, mobileAllowance: undefined, otherAllowance: undefined, fixedDeduction: undefined });
});

test('changing to a grade without a line clears what the previous grade filled', () => {
  const first = applyGradeSalaryDefaults({}, {}, demoGrade);
  const { values, filled } = applyGradeSalaryDefaults(first.values, first.filled, [line('BASIC', { amount: 3000 })]);
  expect(values).toMatchObject({ basicSalary: 3000, housingAllowance: undefined, transportAllowance: undefined });
  expect(filled).toEqual({ basicSalary: 3000 });
});

test('blank lines on the form are what the server fills, so the gross shows the paid package', () => {
  const merged = mergeGradeSalary({ basicSalary: 4800 }, demoGrade);
  expect(merged).toMatchObject({ basicSalary: 4800, housingAllowance: 1200, transportAllowance: 500 });
  expect(mergeGradeSalary({ basicSalary: 4800, housingAllowance: 0 }, demoGrade).housingAllowance).toBe(0);
});

test('deductions are the fixed deduction (percent of basic too), never an allowance', () => {
  const merged = mergeGradeSalary({ basicSalary: 9000 }, [
    line('HOUSING_RECOVERY', { percentage: 5, componentType: 'Deduction', calculationType: 'PercentOfBasic' }),
    line('PARKING', { amount: 100, componentType: 'Deduction' }),
    line('FOOD', { amount: 300, isActive: false }),
  ]);
  expect(merged.fixedDeduction).toBe(550);
  expect(merged.housingAllowance).toBeUndefined();
  expect(merged.foodAllowance).toBeUndefined();
  expect(grossPackage({ basicSalary: 9000, fixedDeduction: 550 })).toBe(9000);
});

test('money rounds to 2 dp, half away from zero, like the server', () => {
  expect(roundMoney(1000.2 * 12.5 / 100)).toBe(125.03);
  expect(mergeGradeSalary({ basicSalary: 3333.33 }, [line('HOUSING', { percentage: 25, calculationType: 'PercentOfBasic' })]).housingAllowance).toBe(833.33);
});
