import { expect, test } from '@playwright/test';
import { designationChoices } from '../src/lib/employeeDesignationChoices';

const row = (code: string, over: Partial<{ departmentId: string; companyId: string | null }> = {}) => ({ code, ...over });

test.describe('designations offered on the employee edit form', () => {
  const rows = [
    row('ACCOUNTANT', { departmentId: 'fin', companyId: 'co' }),
    row('FIN-DIR', { companyId: 'co' }),
    row('HR-MGR', { departmentId: 'hr', companyId: 'co' }),
    row('CEO', { companyId: 'co' }),
    row('LEGACY'),
    row('OTHER-CO', { companyId: 'elsewhere' }),
  ];

  test('a department keeps its own designations and the company-wide ones, not another department\'s', () => {
    const codes = designationChoices(rows, { companyId: 'co', departmentId: 'fin' }).map((item) => item.code);
    expect(codes).toEqual(['ACCOUNTANT', 'FIN-DIR', 'CEO', 'LEGACY']);
  });

  test('with no department chosen every designation of the company is offered', () => {
    const codes = designationChoices(rows, { companyId: 'co' }).map((item) => item.code);
    expect(codes).toEqual(['ACCOUNTANT', 'FIN-DIR', 'HR-MGR', 'CEO', 'LEGACY']);
  });

  test('another company\'s designation is never offered', () => {
    expect(designationChoices(rows, { companyId: 'co', departmentId: 'fin' }).some((item) => item.code === 'OTHER-CO')).toBe(false);
  });
});
