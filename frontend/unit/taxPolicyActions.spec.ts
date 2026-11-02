import { test, expect } from '@playwright/test';
import { taxPolicyActions } from '../src/lib/taxPolicyActions';

const TODAY = '2026-10-25';

test('a policy nothing used can be edited, ended and removed', () => {
  const a = taxPolicyActions({ effectiveTo: null, status: 'Active', usedByPayroll: false }, TODAY);
  expect(a).toMatchObject({ canEditFigures: true, canRetire: true, canRemove: true, removeBlockedReason: null });
});

test('a policy payroll has used keeps its figures, cannot be removed, and says why', () => {
  const a = taxPolicyActions({ effectiveTo: null, status: 'Active', usedByPayroll: true }, TODAY);
  expect(a.canEditFigures).toBe(false);
  expect(a.canRemove).toBe(false);
  expect(a.canRetire).toBe(true);
  expect(a.removeBlockedReason).toContain('End it from a date instead');
});

test('a policy already ended in the past cannot be ended again', () => {
  expect(taxPolicyActions({ effectiveTo: '2026-01-31', status: 'Active' }, TODAY).canRetire).toBe(false);
  expect(taxPolicyActions({ effectiveTo: '2026-12-31', status: 'Active' }, TODAY).canRetire).toBe(true);
  expect(taxPolicyActions({ effectiveTo: null, status: 'Archived' }, TODAY).canRetire).toBe(false);
});
