import { expect, test } from '@playwright/test';
import type { GradePackageComponentOption, GradePayScaleComponentRequest } from '../src/api/organization';
import { linesToSave, packageTotals, rangeStatus, unknownCodes } from '../src/lib/gradePackage';

const line = (componentCode: string, componentType: string, amount: number, percentage = 0, frequency = 'Monthly'): GradePayScaleComponentRequest => ({
  componentCode, componentName: '', componentType, calculationType: percentage > 0 ? 'PercentOfBasic' : 'Fixed',
  amount, percentage, isTaxable: false, frequency, sortOrder: 0, isActive: true,
});
const option = (code: string, nameEn: string, componentType: GradePackageComponentOption['componentType'], isTaxable = false): GradePackageComponentOption => ({
  id: null, companyId: null, code, nameEn, nameAr: nameEn, componentType, isTaxable, isSystem: true,
});
const catalogue = [option('BASIC', 'Basic salary', 'Earning', true), option('HOUSING', 'Housing allowance', 'Earning'), option('FIXED_DEDUCTION', 'Fixed deduction', 'Deduction')];

test.describe('grade package totals (mirror of backend GradePackageMath)', () => {
  test('gross is the monthly cash earnings; a % line is a share of basic; benefits and annual lines are left out', () => {
    const totals = packageTotals([
      line('BASIC', 'Earning', 10_000), line('HOUSING', 'Earning', 0, 25), line('TRANSPORT', 'Earning', 1_000),
      line('TICKET', 'Benefit', 3_000), line('BONUS', 'Earning', 5_000, 0, 'Annual'), line('FIXED_DEDUCTION', 'Deduction', 200),
      line('', 'Earning', 999),
    ]);
    expect(totals).toEqual({ basic: 10_000, gross: 13_500, deductions: 200 });
  });

  test('the range warning names the side, and an unpriced package or an open bound never warns', () => {
    expect(rangeStatus(13_500, 10_000, 20_000)).toBeNull();
    expect(rangeStatus(9_000, 10_000, 20_000)).toBe('below');
    expect(rangeStatus(25_000, 10_000, 20_000)).toBe('above');
    expect(rangeStatus(25_000, 10_000, 0)).toBeNull();
    expect(rangeStatus(0, 10_000, 20_000)).toBeNull();
  });
});

test.describe('saving grade package lines', () => {
  test('blank rows are stripped and name, type and taxable come from the catalogue', () => {
    const saved = linesToSave([line('basic', 'Deduction', 12_000), line('  ', 'Earning', 50), line('HOUSING', 'Earning', 700, 25)], catalogue);
    expect(saved.map(l => [l.componentCode, l.componentName, l.componentType, l.isTaxable, l.amount, l.percentage, l.sortOrder])).toEqual([
      ['BASIC', 'Basic salary', 'Earning', true, 12_000, 0, 1],
      ['HOUSING', 'Housing allowance', 'Earning', false, 0, 25, 2],
    ]);
  });

  test('a legacy free-text code is reported so the editor can block the save', () => {
    expect(unknownCodes([line('BASIC', 'Earning', 1), line('ticket', 'Benefit', 1), line('', 'Earning', 0)], catalogue)).toEqual(['TICKET']);
  });
});
