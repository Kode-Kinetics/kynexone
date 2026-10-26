import { expect, test } from '@playwright/test';
import { buildEmployeeEditChanges } from '../src/lib/employeeEditDraft';
import {
  NO_COST_CENTRE,
  costCentreErrorKind,
  createCostCentreFields,
  isOverride,
  overridePatch,
  removeOverridePatch,
  resolveCostCentre,
  type ResolveInput,
} from '../src/lib/employeeCostCentre';

const cc = (id: string, over: Partial<{ companyId: string; isActive: boolean }> = {}) => ({ id, code: id.toUpperCase(), name: `${id} name`, companyId: 'co', isActive: true, ...over });

const base = (): ResolveInput => ({
  companyId: 'co',
  employee: { costCenterId: null, departmentId: 'd-child', positionId: null, costCenterIsOverride: null },
  departments: [
    { id: 'd-child', nameEn: 'Child', parentDepartmentId: 'd-parent', costCenterId: null },
    { id: 'd-parent', nameEn: 'Parent', parentDepartmentId: null, costCenterId: 'admin' },
  ],
  positions: [{ id: 'p1', title: 'Analyst', costCenterId: null }],
  costCentres: [cc('admin'), cc('sales'), cc('ops'), cc('dflt'), cc('old', { isActive: false }), cc('other', { companyId: 'elsewhere' })],
  companyDefaultId: 'dflt',
});

test.describe('resolveCostCentre follows the server order: employee, position, department, parent department, company default', () => {
  test('a parent department supplies it when the department has none', () => {
    expect(resolveCostCentre(base())).toMatchObject({ kind: 'parentDepartment', costCentreId: 'admin', sourceName: 'Parent' });
  });

  test('the department wins over its parent', () => {
    const i = base();
    i.departments[0].costCenterId = 'ops';
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'department', costCentreId: 'ops', sourceName: 'Child' });
  });

  test('a position cost centre wins over the department', () => {
    const i = base();
    i.employee.positionId = 'p1';
    i.positions[0].costCenterId = 'sales';
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'position', costCentreId: 'sales', sourceName: 'Analyst' });
  });

  test('an override wins over everything', () => {
    const i = base();
    i.employee.costCenterId = 'sales';
    i.employee.costCenterIsOverride = true;
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'employee', costCentreId: 'sales' });
  });

  test('the company default is the last step; with none anywhere the answer is none', () => {
    const i = base();
    i.departments[1].costCenterId = null;
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'companyDefault', costCentreId: 'dflt' });
    i.companyDefaultId = null;
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'none', costCentreId: null });
  });

  test('an unknown default (the reader cannot see finance settings) is not reported as none', () => {
    const i = base();
    i.departments[1].costCenterId = null;
    i.companyDefaultId = undefined;
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'unknown' });
  });

  test('an inactive, deleted or other-company cost centre at a level is invalid and never falls through to the next level', () => {
    const i = base();
    i.departments[0].costCenterId = 'old';
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'invalid', level: 'department', code: 'OLD' });
    i.departments[0].costCenterId = 'other';
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'invalid', level: 'department' });
    i.departments[0].costCenterId = 'gone';
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'invalid', level: 'department' });
  });

  test('a department loop ends at the default instead of spinning', () => {
    const i = base();
    i.departments[1].parentDepartmentId = 'd-child';
    i.departments[1].costCenterId = null;
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'companyDefault' });
  });
});

test.describe('isOverride', () => {
  test('the server flag decides when it is present', () => {
    const i = base();
    i.employee.costCenterId = 'sales';
    i.employee.costCenterIsOverride = false;
    expect(isOverride(i)).toBe(false);
    i.employee.costCenterIsOverride = true;
    expect(isOverride(i)).toBe(true);
  });

  test('without the flag, a stored cost centre that differs from the department\'s own is an override (the server\'s write rule)', () => {
    const i = base();
    i.departments[0].costCenterId = 'ops';
    i.employee.costCenterId = 'ops';
    expect(isOverride(i)).toBe(false);
    i.employee.costCenterId = 'sales';
    expect(isOverride(i)).toBe(true);
    i.employee.costCenterId = null;
    expect(isOverride(i)).toBe(false);
  });
});

test.describe('the AM-30 request shapes', () => {
  test('setting an override sends the code and the reason; removing sends an empty code and the reason', () => {
    expect(overridePatch('SALES', '  Seconded to sales ')).toEqual({ changes: { costCenter: 'SALES' }, costCenterReason: 'Seconded to sales' });
    expect(removeOverridePatch(' Back to the department ')).toEqual({ changes: { costCenter: '' }, costCenterReason: 'Back to the department' });
  });

  test('the explicit None for the employee form is the empty Guid', () => {
    expect(NO_COST_CENTRE).toBe('00000000-0000-0000-0000-000000000000');
  });

  test('create: a user without the permission sends no cost centre at all, even though the department has one', () => {
    expect(createCostCentreFields({ canManage: false, chosenId: 'ops', departmentCostCentreId: 'ops', reason: '' })).toEqual({});
  });

  test('create: the department\'s own cost centre is a plain copy and needs no reason', () => {
    expect(createCostCentreFields({ canManage: true, chosenId: 'ops', departmentCostCentreId: 'ops', reason: '' })).toEqual({ costCenterId: 'ops' });
  });

  test('create: a different cost centre is an override and carries the trimmed reason', () => {
    expect(createCostCentreFields({ canManage: true, chosenId: 'sales', departmentCostCentreId: 'ops', reason: ' Project ' })).toEqual({ costCenterId: 'sales', costCenterReason: 'Project' });
  });

  test('create: nothing chosen leaves the field out; clearing a department\'s cost centre sends the explicit None', () => {
    expect(createCostCentreFields({ canManage: true, chosenId: '', departmentCostCentreId: '', reason: '' })).toEqual({});
    expect(createCostCentreFields({ canManage: true, chosenId: '', departmentCostCentreId: 'ops', reason: '' })).toEqual({ costCenterId: NO_COST_CENTRE });
  });
});

test.describe('costCentreErrorKind', () => {
  const err = (status: number, code: string) => ({ response: { status, data: { error: code } } });
  test('names the refusals the screen handles', () => {
    expect(costCentreErrorKind(err(422, 'cost_centre_reason_required'))).toBe('reason');
    expect(costCentreErrorKind(err(409, 'locked_into_approved_run'))).toBe('locked');
    expect(costCentreErrorKind(err(403, 'x'))).toBe('forbidden');
    expect(costCentreErrorKind(err(422, 'cost_centre_inactive'))).toBe('other');
    expect(costCentreErrorKind(new Error('network'))).toBe('other');
  });
});

test.describe('the shared edit-draft change set never carries the cost centre (AM-30)', () => {
  test('a changed costCenter key is dropped: a department change must not turn into an override', () => {
    const fields = [{ section: 'Employment', key: 'department', label: 'Department' }, { section: 'Employment', key: 'costCenter', label: 'Cost center' }];
    const changes = buildEmployeeEditChanges({ department: 'Ops', costCenter: 'OPS' }, fields as never, ['department', 'costCenter']);
    expect(changes).toEqual({ department: 'Ops' });
  });
});
