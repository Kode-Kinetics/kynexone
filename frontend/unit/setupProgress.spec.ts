import { expect, test } from '@playwright/test';
import type { ManageableCompany, ReadinessItem } from '../src/api/companySetup';
import { readinessProgress } from '../src/lib/companySetupWizard';
import { activeCount, areaProgressItems, areaStatus, firstIncomplete, missingReadinessItems, progressCompany } from '../src/components/setup/setupProgress';

// Setup progress is computed from persisted data only: Guided setup's checklist (server-computed) and record checks
// (at least one active record). Anything that cannot be read is left out, never guessed.

const readiness = (key: string, status: ReadinessItem['status'], scope: ReadinessItem['scope'] = 'company'): ReadinessItem => ({ key, label: key, status, scope, href: `/setup?tab=${key}` });
const ORGANIZATION = ['companies', 'branches', 'departments', 'designations', 'grades', 'costCenters', 'establishment', 'masterData', 'people', 'contractRenewals'];

test('an area counts its record checks and Guided setup items, and nothing else', () => {
  const items = areaProgressItems('organization', ORGANIZATION, [readiness('establishment', 'recommended')], {
    companies: 1, branches: 2, departments: 0, designations: 0, grades: 0, costCenters: 1, masterData: 3,
  });
  // People and contract renewals are workspaces with no check: they are not counted.
  expect(items.map(item => [item.destinationId, item.done, item.source])).toEqual([
    ['companies', true, 'records'], ['branches', true, 'records'], ['departments', false, 'records'], ['designations', false, 'records'],
    ['grades', false, 'records'], ['costCenters', true, 'records'], ['establishment', false, 'guided'], ['masterData', true, 'records'],
  ]);
  expect(areaStatus(items)).toEqual({ done: 4, total: 8, state: 'partial' });
  expect(firstIncomplete(items)?.destinationId).toBe('departments');
});

test('Guided setup\'s verdict wins over a record check, so an area never contradicts it', () => {
  // The workspace has a drop-down list type, but Guided setup still lists the item (it counts inactive ones too, or the
  // server knows better): its verdict is used.
  const items = areaProgressItems('organization', ['masterData'], [readiness('masterData', 'todo', 'organisation')], { masterData: 2 });
  expect(items).toEqual([{ destinationId: 'masterData', done: false, source: 'guided' }]);
  // Once configured the server stops listing a workspace item; the record check answers.
  expect(areaProgressItems('organization', ['masterData'], [], { masterData: 2 })).toEqual([{ destinationId: 'masterData', done: true, source: 'records' }]);
});

test('an unreadable source leaves its item out: an area with nothing measurable has no status', () => {
  expect(areaProgressItems('organization', ['grades', 'departments'], null, { grades: null })).toEqual([]);
  expect(areaStatus([])).toBeNull();
  // Leave and absence has no check at all.
  expect(areaStatus(areaProgressItems('leave', ['leaveTypes', 'leavePolicies', 'holidayCalendars', 'leave'], null, {}))).toBeNull();
  // A readiness-only item (Payroll accounting) is not measurable without a company's checklist.
  expect(areaProgressItems('payroll', ['glMapping', 'fiscalYears', 'payroll'], null, { fiscalYears: 1 })).toEqual([{ destinationId: 'fiscalYears', done: true, source: 'records' }]);
  // An item the user cannot open is not in destinationIds, so it is never counted.
  expect(areaProgressItems('payroll', ['fiscalYears'], [readiness('glMapping', 'todo')], { fiscalYears: 0 })).toEqual([{ destinationId: 'fiscalYears', done: false, source: 'records' }]);
});

test('statuses: Not started, x of y, Done', () => {
  expect(areaStatus([{ destinationId: 'a', done: false, source: 'records' }])).toEqual({ done: 0, total: 1, state: 'notStarted' });
  expect(areaStatus([{ destinationId: 'a', done: true, source: 'records' }, { destinationId: 'b', done: false, source: 'guided' }])).toEqual({ done: 1, total: 2, state: 'partial' });
  expect(areaStatus([{ destinationId: 'a', done: true, source: 'records' }])).toEqual({ done: 1, total: 1, state: 'done' });
  expect(firstIncomplete([{ destinationId: 'a', done: true, source: 'records' }])).toBeNull();
});

test('payroll and compliance map their Guided setup items to their destinations', () => {
  const items = readinessItems();
  expect(areaProgressItems('payroll', ['glMapping', 'fiscalYears', 'payroll', 'payslipTemplates'], items, {})).toEqual([
    { destinationId: 'glMapping', done: true, source: 'guided' },
    { destinationId: 'payroll', done: false, source: 'guided' },
  ]);
  expect(areaProgressItems('compliance', ['gccSettings', 'statutoryRules', 'saudiCompliance', 'gosiFiling'], items, { gccSettings: 0 })).toEqual([
    { destinationId: 'gccSettings', done: false, source: 'guided' },
    { destinationId: 'statutoryRules', done: true, source: 'guided' },
    { destinationId: 'saudiCompliance', done: false, source: 'guided' },
  ]);
});

function readinessItems(): ReadinessItem[] {
  return [
    readiness('glMapping', 'done'), readiness('establishment', 'recommended'), readiness('locations', 'done'), readiness('gccSettings', 'todo'),
    readiness('statutoryRules', 'done'), readiness('bankWps', 'todo'), readiness('nitaqat', 'recommended'), readiness('companyAdmin', 'done'),
    readiness('emailConfig', 'todo', 'organisation'),
  ];
}

test('the progress line drills down to exactly the items behind Guided setup\'s number', () => {
  const items = readinessItems();
  const company: ManageableCompany = { id: 'c1', legalNameEn: 'Fixture', countryCode: 'SA', readinessDone: items.filter(i => i.status === 'done').length, readinessTotal: items.length };
  const progress = readinessProgress(company);
  expect(progress).toMatchObject({ done: 4, total: 9, complete: false });
  const missing = missingReadinessItems(items);
  expect(missing.map(item => item.key)).toEqual(['establishment', 'gccSettings', 'bankWps', 'nitaqat', 'emailConfig']);
  expect(missing.length).toBe(progress.total - progress.done);
  expect(missingReadinessItems(null)).toEqual([]);
});

test('the progress company is the selected one, or the only one; never a made-up group total', () => {
  const a: ManageableCompany = { id: 'a', legalNameEn: 'A', countryCode: 'SA', readinessDone: 1, readinessTotal: 9 };
  const b: ManageableCompany = { ...a, id: 'b', legalNameEn: 'B' };
  expect(progressCompany([a, b], 'b')).toBe(b);
  expect(progressCompany([a, b], null)).toBeNull();
  expect(progressCompany([a], null)).toBe(a);
  // A selected company the caller cannot set up shows no number.
  expect(progressCompany([a], 'z')).toBeNull();
  expect(progressCompany(null, 'a')).toBeNull();
});

test('active records: inactive rows do not count; a reply that is not a list is not measurable', () => {
  expect(activeCount([{ isActive: true }, { isActive: false }, { name: 'no flag' }])).toBe(2);
  expect(activeCount([])).toBe(0);
  expect(activeCount({ items: [] })).toBeNull();
  expect(activeCount(undefined)).toBeNull();
});
