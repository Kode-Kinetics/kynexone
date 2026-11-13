import { test, expect } from '@playwright/test';
import { defaultDispositions } from '../src/lib/offerHandoffDefaults';

const c = (code: string, over: Record<string, unknown> = {}) => ({ code, kind: 'Cash', frequency: 'Monthly', included: true, structureField: null as string | null, ...over });

test.describe('compensation handoff: the standard components map themselves', () => {
  test('basic, housing, transport and other allowances go to their payroll fields', () => {
    const rows = defaultDispositions([
      c('BASIC', { structureField: 'BasicSalary' }), c('HOUSING', { structureField: 'HousingAllowance' }),
      c('TRANSPORT', { structureField: 'TransportAllowance' }), c('OTHER_ALLOWANCES', { structureField: 'OtherAllowance' }),
    ], []);
    expect(rows).toEqual([
      { code: 'BASIC', action: 'Payroll', payrollField: 'BasicSalary' },
      { code: 'HOUSING', action: 'Payroll', payrollField: 'HousingAllowance' },
      { code: 'TRANSPORT', action: 'Payroll', payrollField: 'TransportAllowance' },
      { code: 'OTHER_ALLOWANCES', action: 'Payroll', payrollField: 'OtherAllowance' },
    ]);
  });

  test('an excluded component stays excluded', () => {
    expect(defaultDispositions([c('CAR', { included: false })], [])).toEqual([{ code: 'CAR', action: 'Excluded' }]);
  });

  test('benefits, annual and one-time items and unmapped components are left for the person to decide', () => {
    const rows = defaultDispositions([
      c('MEDICAL', { kind: 'Benefit' }), c('TICKET', { frequency: 'Annual', structureField: 'OtherAllowance' }), c('BONUS', { frequency: 'OneTime' }), c('PHONE'),
    ], []);
    expect(rows.map(r => r.action)).toEqual(['', '', '', '']);
  });

  test('two components for one payroll field map only the first; the second is for the person to decide', () => {
    const rows = defaultDispositions([c('HOUSE_A', { structureField: 'HousingAllowance' }), c('HOUSE_B', { structureField: 'HousingAllowance' })], []);
    expect(rows.map(r => r.action)).toEqual(['Payroll', '']);
  });

  test('what was already saved is never overwritten', () => {
    const saved = [{ code: 'HOUSING', action: 'NamedProcess', processName: 'Company housing', reason: 'Provided in kind' }];
    const rows = defaultDispositions([c('HOUSING', { structureField: 'HousingAllowance' })], saved);
    expect(rows).toEqual(saved);
  });
});
