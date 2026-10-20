import { expect, test } from '@playwright/test';
import type { ReadinessItem } from '../src/api/employees';
import { readinessFixHref, readinessFixTarget } from '../src/lib/readinessFixTarget';

// Categories exactly as EmployeeFieldRegistry / EmployeeReadinessEvaluator emit them.
const item = (category: string, fix?: ReadinessItem['fix'] | { kind: 'policy' }): Pick<ReadinessItem, 'category' | 'fix'> =>
  ({ category, fix: fix as ReadinessItem['fix'] });

test('a missing government ID opens the Identity step of Edit Employee', () => {
  const idNumber = item('identity', { kind: 'field', target: 'idNumber' });
  expect(readinessFixTarget(idNumber)).toEqual({ section: 'compliance-documents', edit: true });
  // stepForSection('compliance-documents') is the Identity step of the edit flow.
  expect(readinessFixHref(42, idNumber, '/people')).toBe('/people/42/edit?section=compliance-documents&returnTo=%2Fpeople');
});

test('payroll fields open the payroll step, and a missing salary opens the salary step', () => {
  expect(readinessFixTarget(item('payroll', { kind: 'field', target: 'payrollProfile.iban' }))).toEqual({ section: 'pay-benefits', edit: true });
  expect(readinessFixTarget(item('payroll', { kind: 'field', target: 'salary' }))).toEqual({ section: 'pay-benefits', edit: true, panel: 'salary' });
  expect(readinessFixHref(42, item('payroll', { kind: 'field', target: 'salary' }))).toBe('/people/42/edit?section=pay-benefits&panel=salary');
});

test('personal, org and contract fields open their own edit steps', () => {
  expect(readinessFixTarget(item('personal', { kind: 'field', target: 'dateOfBirth' }))).toEqual({ section: 'personal', edit: true });
  expect(readinessFixTarget(item('org', { kind: 'field', target: 'departmentId' }))).toEqual({ section: 'employment', edit: true });
  expect(readinessFixTarget(item('contract', { kind: 'field', target: 'joiningDate' }))).toEqual({ section: 'employment', edit: true });
});

test('a required document opens the upload panel, not the edit flow', () => {
  const contract = item('document', { kind: 'document', documentType: 'Contract' });
  expect(readinessFixTarget(contract)).toEqual({ section: 'compliance-documents', edit: false, panel: 'documents' });
  expect(readinessFixHref(42, contract)).toBe('/people/42?section=compliance-documents&panel=documents');
});

test('"pay" is a gate, not a category, and no-policy items are not field edits', () => {
  // The old mapping matched category === 'pay', which the server never sends.
  expect(readinessFixTarget(item('pay'))).toEqual({ section: 'employment', edit: false });
  expect(readinessFixTarget(item('compliance', { kind: 'policy' }))).toEqual({ section: 'compliance-documents', edit: false });
  expect(readinessFixTarget(item('IDENTITY', { kind: 'field', target: 'iqamaNumber' }))).toEqual({ section: 'compliance-documents', edit: true });
});
