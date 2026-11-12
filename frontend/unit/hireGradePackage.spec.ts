import { expect, test } from '@playwright/test';
import { emptyHireForm } from '../src/lib/hire/hireForm';
import { buildCreateRequest } from '../src/lib/hire/payload';
import { bandStatus, gradeFillFromMatrix, hirePackage } from '../src/lib/hire/gradePackage';
import type { EntitlementMatrix, MatrixCell } from '../src/api/entitlements';

/**
 * A hire's saved salary must be what the person saw and confirmed. For a Benefits-by-grade tenant the server fills a cash allowance
 * left BLANK from the grade (housing, transport, other). The wizard now reads the same cells, shows the amount, counts it in the
 * total it confirms and sends it, so nothing is added behind the screen. (Rehearsal: basic 4,500 + housing 1,125 + transport 450
 * was saved as 6,275 because the grade's "other allowance" of 200 was added silently.)
 */
const cell = (componentCode: string, p: Partial<MatrixCell>): MatrixCell => ({
  gradeId: 'g1', componentCode, cellId: `${componentCode}-1`, isCompanyOverride: false, inherited: false, eligible: true, valueType: 'Amount',
  amount: null, rate: null, maxOutstandingAmount: null, coverageTier: null, quantity: null, dependantScope: 'None', maxDependants: null, limitPeriod: null,
  minServiceMonths: null, afterProbation: false, nationalityScope: 'Any', nationalityBasis: null, sourceRule: null, note: null,
  effectiveFrom: '2026-01-01', effectiveTo: null, nextChangeOn: null, ...p,
});
const matrix = (cells: MatrixCell[]): Pick<EntitlementMatrix, 'components' | 'cells'> => ({ components: [], cells });
const base = { ...emptyHireForm(), englishName: 'Test Person', gradeId: 'g1', basic: '4500', housing: '1125', transport: '450' };
const ctx = { companyCountry: 'SA', currency: 'SAR' };

test.describe('what the grade adds to a blank allowance', () => {
  test('an Amount cell on OTHER_ALLOWANCES is read as the other allowance', () => {
    const fill = gradeFillFromMatrix(matrix([cell('OTHER_ALLOWANCES', { amount: 200 })]), 'g1', 4500);
    expect(fill).toEqual({ other: 200 });
  });
  test('a percentage of basic is worked out on the basic, to the halala, as the server does', () => {
    const fill = gradeFillFromMatrix(matrix([cell('HOUSING', { valueType: 'PercentOfBasic', rate: 0.25 })]), 'g1', 4500.5);
    expect(fill).toEqual({ housing: 1125.13 });
  });
  test('a percentage with no basic, in kind, not offered, another grade and a missing cell add nothing', () => {
    const cells = [
      cell('HOUSING', { valueType: 'PercentOfBasic', rate: 0.25 }),
      cell('TRANSPORT', { valueType: 'InKind' }),
      cell('OTHER_ALLOWANCES', { eligible: false, amount: 300 }),
      cell('HOUSING', { gradeId: 'g2', amount: 999 }),
    ];
    expect(gradeFillFromMatrix(matrix(cells), 'g1', undefined)).toEqual({});
  });
});

test.describe('the package the person confirms', () => {
  test('typed figures only: the total is what was typed and nothing is from the grade', () => {
    const pkg = hirePackage(base, {});
    expect(pkg.total).toBe(6075);
    expect(pkg.gradeFilled).toBe(false);
    expect(pkg.rows.map(r => [r.field, r.amount, r.fromGrade])).toEqual([['basic', 4500, false], ['housing', 1125, false], ['transport', 450, false]]);
  });
  test('a blank allowance the grade gives is shown, flagged and counted in the total', () => {
    const pkg = hirePackage(base, { other: 200 });
    expect(pkg.total).toBe(6275);
    expect(pkg.gradeFilled).toBe(true);
    expect(pkg.rows.find(r => r.field === 'other')).toEqual({ field: 'other', amount: 200, fromGrade: true });
  });
  test('an allowance typed as 0 is the person\'s own answer and the grade does not replace it', () => {
    const pkg = hirePackage({ ...base, other: '0' }, { other: 200 });
    expect(pkg.total).toBe(6075);
    expect(pkg.gradeFilled).toBe(false);
  });
  test('a typed allowance wins over the grade\'s', () => {
    expect(hirePackage({ ...base, housing: '2000' }, { housing: 1500 }).total).toBe(6950);
  });
  test('with no figures typed there is no package and nothing is filled (the server fills only a salary that has figures)', () => {
    const pkg = hirePackage({ ...base, basic: '', housing: '', transport: '' }, { other: 200 });
    expect(pkg.total).toBe(0);
    expect(pkg.rows).toEqual([]);
  });
});

test.describe('the grade band is checked on the total pay, as the server does', () => {
  const grade = { minSalary: 5000, maxSalary: 6200 };
  test('a basic under the band is fine when the total pay is inside it', () => {
    expect(bandStatus(6075, grade)).toBeNull();
  });
  test('the total over the maximum is above, under the minimum is below', () => {
    expect(bandStatus(6275, grade)).toBe('above');
    expect(bandStatus(4999, grade)).toBe('below');
  });
  test('no band set, or no pay, is never out of band', () => {
    expect(bandStatus(9999, { minSalary: 0, maxSalary: 0 })).toBeNull();
    expect(bandStatus(0, grade)).toBeNull();
  });
});

test.describe('the request carries what was confirmed', () => {
  test('a grade-given allowance left blank is sent as its amount, so the saved pay equals the confirmed total', () => {
    const body = buildCreateRequest(base, { ...ctx, gradeFill: { other: 200 } });
    expect(body.salaryBreakdown).toMatchObject({ basicSalary: 4500, housingAllowance: 1125, transportAllowance: 450, otherAllowance: 200 });
  });
  test('an allowance typed as 0 is sent as 0 (the server keeps a stated 0 and fills only a blank)', () => {
    const body = buildCreateRequest({ ...base, other: '0' }, { ...ctx, gradeFill: { other: 200 } });
    expect(body.salaryBreakdown?.otherAllowance).toBe(0);
  });
  test('a blank allowance with nothing from the grade is left out of the request', () => {
    expect(buildCreateRequest(base, ctx).salaryBreakdown?.otherAllowance).toBeUndefined();
  });
});
