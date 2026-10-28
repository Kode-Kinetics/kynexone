import { expect, test } from '@playwright/test';
import { emptyHireForm } from '../src/lib/hire/hireForm';
import { addDays, planTemplate, planUndo } from '../src/lib/hire/hireTemplate';

const defaults = { departmentId: 'dep-1', employmentType: 'Full-time', contractType: 'Permanent', probationDays: 90, workLocation: 'HQ', managerRule: 'department-head' as const };

test('a template fills empty fields and reports what it changed', () => {
  const form = { ...emptyHireForm(), startDate: '2026-11-08' };
  const plan = planTemplate(form, new Set(), defaults, 42);
  expect(plan.patch).toEqual({ departmentId: 'dep-1', employmentType: 'Full-time', contractType: 'Permanent', workLocation: 'HQ', probationEndDate: '2027-02-06', managerId: '42' });
  expect(plan.kept).toEqual([]);
  expect(plan.prior.departmentId).toBe('');
});

// AC69: explicit input is never overwritten.
test('a field the person typed in is never overwritten, even if it was later cleared', () => {
  const form = { ...emptyHireForm(), startDate: '2026-11-08', workLocation: 'Riyadh warehouse', employmentType: '' };
  const plan = planTemplate(form, new Set(['employmentType', 'workLocation']), defaults, null);
  expect(plan.patch.workLocation).toBeUndefined();
  expect(plan.patch.employmentType).toBeUndefined();
  expect(plan.kept).toEqual(expect.arrayContaining(['workLocation', 'employmentType']));
  expect(plan.patch.contractType).toBe('Permanent');
});

test('a value already in the form is kept although the person did not touch it this session (a resumed draft)', () => {
  const form = { ...emptyHireForm(), startDate: '2026-11-08', departmentId: 'dep-9' };
  const plan = planTemplate(form, new Set(), defaults, null);
  expect(plan.patch.departmentId).toBeUndefined();
  expect(plan.kept).toContain('departmentId');
});

test('probation end needs a start date; the department head only when the rule asks for it and one exists', () => {
  const noStart = planTemplate(emptyHireForm(), new Set(), defaults, null);
  expect(noStart.patch.probationEndDate).toBeUndefined();
  expect(noStart.patch.managerId).toBeUndefined();
  expect(planTemplate(emptyHireForm(), new Set(), { ...defaults, managerRule: 'none' }, 42).patch.managerId).toBeUndefined();
});

test('undo restores only what the template put there and is not fooled by later edits', () => {
  const form = { ...emptyHireForm(), startDate: '2026-11-08' };
  const plan = planTemplate(form, new Set(), defaults, null);
  const after = { ...form, ...plan.patch, workLocation: 'Jeddah' };   // the person edited the location after applying
  const restore = planUndo(after, plan.patch, plan.prior);
  expect(restore.workLocation).toBeUndefined();
  expect(restore.departmentId).toBe('');
  expect(restore.employmentType).toBe('');
});

test('addDays is calendar arithmetic across month and year ends', () => {
  expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
  expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
});
