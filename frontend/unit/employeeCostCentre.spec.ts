import { expect, test } from '@playwright/test';
import type { EmployeeDetail } from '../src/api/employees';
import { buildEmployeeEditChanges, buildEmployeeEditSnapshot } from '../src/lib/employeeEditDraft';
import { LOCAL_FIELD_CATALOG } from '../src/api/employeeFieldCatalog';
import { emptyHireForm, rebaseHireCompany } from '../src/lib/hire/hireForm';
import {
  NO_COST_CENTRE,
  costCentreErrorKind,
  costCentreOptions,
  costCentreRefusalKey,
  createCostCentreFields,
  editCostCentreNeedsReason,
  editCostCentrePatch,
  needsDecision,
  previewResolution,
  type EditCostCentreInput,
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

  test('create: "Follow department" (nothing chosen) sends nothing, so the person inherits; it never invents an explicit None', () => {
    expect(createCostCentreFields({ canManage: true, chosenId: '', departmentCostCentreId: '', reason: '' })).toEqual({});
    expect(createCostCentreFields({ canManage: true, chosenId: '', departmentCostCentreId: 'ops', reason: '' })).toEqual({});
  });

  test('create (a saved hire draft): following the department again where an override exists sends the explicit None', () => {
    expect(createCostCentreFields({ canManage: true, chosenId: '', departmentCostCentreId: 'ops', reason: '', hadOverride: true })).toEqual({ costCenterId: NO_COST_CENTRE });
    expect(createCostCentreFields({ canManage: true, chosenId: '', departmentCostCentreId: '', reason: '', hadOverride: true })).toEqual({ costCenterId: NO_COST_CENTRE });
    expect(createCostCentreFields({ canManage: false, chosenId: '', departmentCostCentreId: '', reason: '', hadOverride: true })).toEqual({});
  });
});

test.describe('costCentreErrorKind', () => {
  const err = (status: number, code: string) => ({ response: { status, data: { error: code } } });
  test('names the refusals the screen handles', () => {
    expect(costCentreErrorKind(err(422, 'cost_centre_reason_required'))).toBe('reason');
    expect(costCentreErrorKind(err(409, 'locked_into_approved_run'))).toBe('locked');
    expect(costCentreErrorKind(err(403, 'x'))).toBe('forbidden');
    expect(costCentreErrorKind(new Error('network'))).toBe('other');
  });

  test('a missing keep-or-follow decision is its own kind, so the screen can jump to the prompt', () => {
    expect(costCentreErrorKind(err(422, 'cost_centre_decision_required'))).toBe('decision');
    expect(costCentreErrorKind(err(422, 'cost_centre_decision_invalid'))).toBe('decision');
  });

  test('a cost centre that cannot be used (inactive, other company, not found) is the "ref" kind', () => {
    for (const code of ['cost_centre_inactive', 'cost_centre_other_company', 'cost_centre_not_found']) expect(costCentreErrorKind(err(422, code))).toBe('ref');
  });

  test('a reason with control or direction characters is a reason problem; a stale screen is its own kind', () => {
    expect(costCentreErrorKind(err(422, 'reason_invalid_characters'))).toBe('reason');
    expect(costCentreErrorKind(err(409, 'cost_centre_state_changed'))).toBe('stale');
    expect(costCentreErrorKind(err(422, 'something_else'))).toBe('other');
  });
});

// ═══════════════════════ The edit flow: what the PUT carries (Wave A, D2/D4) ═══════════════════════

const codes: Record<string, string> = { admin: 'ADMIN', sales: 'SALES', ops: 'OPS', hr: 'CC-HR' };
const edit = (over: Partial<EditCostCentreInput> = {}): EditCostCentreInput => ({
  canManage: true, isOverride: false, overrideId: null, inheritedId: 'admin', choice: undefined,
  departmentChanged: false, decision: undefined, reason: '', codeOf: (id) => codes[id] ?? '', ...over,
});

test.describe('editCostCentrePatch', () => {
  test('without organization.costcenter.manage nothing about the cost centre is ever sent', () => {
    expect(editCostCentrePatch(edit({ canManage: false, choice: 'sales', reason: 'x' }))).toEqual({});
    expect(editCostCentrePatch(edit({ canManage: false, isOverride: true, overrideId: 'sales', departmentChanged: true, decision: 'keep', reason: 'x' }))).toEqual({});
  });

  test('untouched, no department change: nothing is sent', () => {
    expect(editCostCentrePatch(edit())).toEqual({});
    expect(editCostCentrePatch(edit({ isOverride: true, overrideId: 'sales' }))).toEqual({});
  });

  test('untouched, department changed, no override: nothing is sent, the server re-derives', () => {
    expect(editCostCentrePatch(edit({ departmentChanged: true, inheritedId: 'hr' }))).toEqual({});
  });

  test('a different cost centre is an override: its code and the trimmed reason', () => {
    expect(editCostCentrePatch(edit({ choice: 'sales', reason: '  Seconded ' }))).toEqual({ changes: { costCenter: 'SALES' }, costCenterReason: 'Seconded' });
  });

  test('re-pointing an existing override to another cost centre is the same shape', () => {
    expect(editCostCentrePatch(edit({ isOverride: true, overrideId: 'sales', choice: 'ops', reason: 'Moved' }))).toEqual({ changes: { costCenter: 'OPS' }, costCenterReason: 'Moved' });
  });

  test('choosing the inherited cost centre is never sent as a code (that would make it an override)', () => {
    expect(editCostCentrePatch(edit({ choice: 'admin', reason: 'x' }))).toEqual({});
    expect(editCostCentrePatch(edit({ choice: '', reason: 'x' }))).toEqual({});
  });

  test('"Follow" (or the inherited cost centre) while overridden removes the override: an empty code and the reason', () => {
    expect(editCostCentrePatch(edit({ isOverride: true, overrideId: 'sales', choice: '', reason: ' Back ' }))).toEqual({ changes: { costCenter: '' }, costCenterReason: 'Back' });
    expect(editCostCentrePatch(edit({ isOverride: true, overrideId: 'sales', choice: 'admin', reason: 'Back' }))).toEqual({ changes: { costCenter: '' }, costCenterReason: 'Back' });
  });

  test('picking the current override again is no change', () => {
    expect(editCostCentrePatch(edit({ isOverride: true, overrideId: 'sales', choice: 'sales', reason: 'x' }))).toEqual({});
  });

  test('override + department change + no explicit pick: the decision, the reason and the override id it was made against', () => {
    const base = { isOverride: true, overrideId: 'sales', departmentChanged: true, inheritedId: 'hr', reason: ' Transfer ' } as const;
    expect(editCostCentrePatch(edit({ ...base, decision: 'keep' }))).toEqual({ costCenterDecision: 'keep', costCenterReason: 'Transfer', expectedCostCenterOverrideId: 'sales' });
    expect(editCostCentrePatch(edit({ ...base, decision: 'drop' }))).toEqual({ costCenterDecision: 'drop', costCenterReason: 'Transfer', expectedCostCenterOverrideId: 'sales' });
    // Picking the override itself again is the same as not touching the dropdown: the decision still applies.
    expect(editCostCentrePatch(edit({ ...base, choice: 'sales', decision: 'keep' }))).toMatchObject({ costCenterDecision: 'keep' });
  });

  test('override + department change + an explicit pick: the pick wins and no decision is sent (the server refuses both)', () => {
    const base = { isOverride: true, overrideId: 'sales', departmentChanged: true, inheritedId: 'hr', reason: 'r', decision: 'keep' as const };
    expect(editCostCentrePatch(edit({ ...base, choice: 'ops' }))).toEqual({ changes: { costCenter: 'OPS' }, costCenterReason: 'r' });
    expect(editCostCentrePatch(edit({ ...base, choice: '' }))).toEqual({ changes: { costCenter: '' }, costCenterReason: 'r' });
  });

  test('override + department change with no decision yet: no cost-centre fields (the screen must ask first)', () => {
    expect(editCostCentrePatch(edit({ isOverride: true, overrideId: 'sales', departmentChanged: true }))).toEqual({});
  });

  test('a decision with no department change is never sent (the server answers not_applicable)', () => {
    expect(editCostCentrePatch(edit({ isOverride: true, overrideId: 'sales', decision: 'drop', reason: 'x' }))).toEqual({});
  });

  test('a choice whose code is unknown is not sent as an empty code (that would read as "remove")', () => {
    expect(editCostCentrePatch(edit({ choice: 'ghost', reason: 'x' }))).toEqual({});
  });
});

test.describe('needsDecision and editCostCentreNeedsReason', () => {
  test('the keep-or-follow question is asked only for an override, a department change, a manager, and no explicit pick', () => {
    expect(needsDecision(edit({ isOverride: true, overrideId: 'sales', departmentChanged: true }))).toBe(true);
    expect(needsDecision(edit({ isOverride: true, overrideId: 'sales', departmentChanged: true, choice: 'sales' }))).toBe(true);
    expect(needsDecision(edit({ isOverride: true, overrideId: 'sales', departmentChanged: true, choice: 'ops' }))).toBe(false);
    expect(needsDecision(edit({ isOverride: true, overrideId: 'sales', departmentChanged: true, choice: '' }))).toBe(false);
    expect(needsDecision(edit({ isOverride: false, departmentChanged: true }))).toBe(false);
    expect(needsDecision(edit({ isOverride: true, overrideId: 'sales', departmentChanged: false }))).toBe(false);
    expect(needsDecision(edit({ canManage: false, isOverride: true, overrideId: 'sales', departmentChanged: true }))).toBe(false);
  });

  test('a reason is due whenever the PUT would change the override or carry a decision', () => {
    expect(editCostCentreNeedsReason(edit())).toBe(false);
    expect(editCostCentreNeedsReason(edit({ choice: 'sales' }))).toBe(true);
    expect(editCostCentreNeedsReason(edit({ isOverride: true, overrideId: 'sales', choice: '' }))).toBe(true);
    expect(editCostCentreNeedsReason(edit({ isOverride: true, overrideId: 'sales', departmentChanged: true }))).toBe(true);
    expect(editCostCentreNeedsReason(edit({ departmentChanged: true }))).toBe(false);
    expect(editCostCentreNeedsReason(edit({ canManage: false, choice: 'sales' }))).toBe(false);
  });
});

test.describe('costCentreOptions', () => {
  const list = [
    { id: 'admin', code: 'ADMIN', name: 'Administration', nameAr: 'الإدارة', companyId: 'co', isActive: true },
    { id: 'sales', code: 'SALES', name: 'Sales', nameAr: null, companyId: 'co', isActive: true },
    { id: 'old', code: 'OLD', name: 'Old', companyId: 'co', isActive: false },
    { id: 'elsewhere', code: 'AE', name: 'UAE', companyId: 'other', isActive: true },
    { id: 'group', code: 'GRP', name: 'Group', companyId: null, isActive: true },
  ];

  test('only this company\'s active cost centres, labelled CODE — name', () => {
    expect(costCentreOptions(list, 'co', 'en', null)).toEqual([
      { value: 'admin', code: 'ADMIN', name: 'Administration', label: 'ADMIN — Administration' },
      { value: 'sales', code: 'SALES', name: 'Sales', label: 'SALES — Sales' },
    ]);
  });

  test('Arabic uses the Arabic name and falls back to the English one', () => {
    expect(costCentreOptions(list, 'co', 'ar', null).map((o) => o.label)).toEqual(['ADMIN — الإدارة', 'SALES — Sales']);
  });

  test('the inherited cost centre is the first option ("follow"), with an empty value, and is not listed twice', () => {
    const options = costCentreOptions(list, 'co', 'en', { id: 'admin', code: 'ADMIN', name: 'Administration' });
    expect(options[0]).toEqual({ value: '', code: 'ADMIN', name: 'Administration', label: 'ADMIN — Administration', inherit: true });
    expect(options.filter((o) => o.code === 'ADMIN')).toHaveLength(1);
    expect(options.map((o) => o.value)).toEqual(['', 'sales']);
  });

  test('nothing to inherit still offers the follow option, without a code', () => {
    const options = costCentreOptions(list, 'co', 'en', { id: null, code: '', name: '' });
    expect(options[0]).toEqual({ value: '', code: '', name: '', label: '', inherit: true });
  });

  test('no company: nothing is offered rather than every company\'s list', () => {
    expect(costCentreOptions(list, '', 'en', null)).toEqual([]);
  });
});

test.describe('the edit draft takes the cost centre from the server resolution, never the cached string (D3)', () => {
  const fields = [{ section: 'Employment', key: 'costCenter', label: 'Cost center' }] as const;
  test('the snapshot shows the resolved code even when the cached copy disagrees', () => {
    const employee = { id: 1, costCenter: 'STALE', costCentreResolution: { costCenterId: 'hr', code: 'CC-HR', source: 'Department' } } as unknown as EmployeeDetail;
    expect(buildEmployeeEditSnapshot(employee, fields as never, LOCAL_FIELD_CATALOG)).toEqual({ costCenter: 'CC-HR' });
  });
  test('no resolution: blank, not the cached copy', () => {
    const employee = { id: 1, costCenter: 'STALE' } as unknown as EmployeeDetail;
    expect(buildEmployeeEditSnapshot(employee, fields as never, LOCAL_FIELD_CATALOG)).toEqual({ costCenter: '' });
  });
});

test.describe('the hire form: changing the employing company clears the cost centre and its reason', () => {
  test('rebaseHireCompany clears costCenterId and costCenterReason when the company changes', () => {
    const form = { ...emptyHireForm(), companyId: 'co', costCenterId: 'sales', costCenterReason: 'Project' };
    const next = rebaseHireCompany(form, 'other', { branches: [], departments: [], designations: [], grades: [], managers: [] });
    expect(next).toMatchObject({ companyId: 'other', costCenterId: '', costCenterReason: '' });
  });
  test('the same company keeps them', () => {
    const form = { ...emptyHireForm(), companyId: 'co', costCenterId: 'sales', costCenterReason: 'Project' };
    expect(rebaseHireCompany(form, 'co', { branches: [], departments: [], designations: [], grades: [], managers: [] })).toMatchObject({ costCenterId: 'sales', costCenterReason: 'Project' });
  });
});

test.describe('the shared edit-draft change set never carries the cost centre (AM-30)', () => {
  test('a changed costCenter key is dropped: a department change must not turn into an override', () => {
    const fields = [{ section: 'Employment', key: 'department', label: 'Department' }, { section: 'Employment', key: 'costCenter', label: 'Cost center' }];
    const changes = buildEmployeeEditChanges({ department: 'Ops', costCenter: 'OPS' }, fields as never, ['department', 'costCenter']);
    expect(changes).toEqual({ department: 'Ops' });
  });
});

test.describe('the server-resolved attribution wins over the inference when the API sends it', () => {
  const server = (over: Record<string, unknown>) => ({ costCenterId: 'sales', code: 'SALES', name: 'sales name', nameAr: null, source: 'Department', level: 'department', unresolvedReason: null, ...over });

  test('the server source and cost centre are used even where the lists would infer something else', () => {
    const i = base();
    i.employee.costCentreResolution = server({ costCenterId: 'sales', source: 'Position', level: 'position' });
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'position', costCentreId: 'sales' });
  });

  test('each server source maps to the screen\'s kinds', () => {
    const i = base();
    for (const [source, level, kind] of [['Employee', 'employee', 'employee'], ['Department', 'department', 'department'], ['ParentDepartment', 'parentDepartment', 'parentDepartment'], ['CompanyDefault', 'company', 'companyDefault']] as const) {
      i.employee.costCentreResolution = server({ source, level });
      expect(resolveCostCentre(i)).toMatchObject({ kind, costCentreId: 'sales' });
    }
  });

  test('the source name still comes from the lists when the kind agrees', () => {
    const i = base();
    i.employee.costCentreResolution = server({ costCenterId: 'admin', source: 'ParentDepartment', level: 'parentDepartment' });
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'parentDepartment', costCentreId: 'admin', sourceName: 'Parent' });
  });

  test('None with reason "none" is none; an "..._invalid" reason is invalid at its level', () => {
    const i = base();
    i.employee.costCentreResolution = server({ costCenterId: null, code: null, name: null, source: 'None', level: null, unresolvedReason: 'none' });
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'none', costCentreId: null });
    i.employee.costCentreResolution = server({ costCenterId: null, code: null, name: null, source: 'None', level: 'position', unresolvedReason: 'position_cost_centre_invalid' });
    expect(resolveCostCentre(i)).toMatchObject({ kind: 'invalid', level: 'position', costCentreId: null });
  });

  test('ignoreOverride (what removing the override would return to) still infers from the lists', () => {
    const i = base();
    i.employee.costCenterId = 'sales';
    i.employee.costCenterIsOverride = true;
    i.employee.costCentreResolution = server({ costCenterId: 'sales', source: 'Employee', level: 'employee' });
    expect(resolveCostCentre(i, { ignoreOverride: true })).toMatchObject({ kind: 'parentDepartment', costCentreId: 'admin' });
  });

  test('the override flag comes from the server resolution when costCenterIsOverride is absent', () => {
    const i = base();
    i.employee.costCenterId = 'sales';
    i.employee.costCentreResolution = server({ source: 'Employee', level: 'employee' });
    expect(isOverride(i)).toBe(true);
    i.employee.costCentreResolution = server({ source: 'Department', level: 'department' });
    expect(isOverride(i)).toBe(false);
  });

  test('without it (an older response) the inference is unchanged', () => {
    expect(resolveCostCentre(base())).toMatchObject({ kind: 'parentDepartment', costCentreId: 'admin' });
  });
});

test.describe('previewResolution: the preview endpoint\'s answer in the screen\'s terms', () => {
  const dto = (over: Record<string, unknown>) => ({ costCentreId: 'hr', code: 'CC-HR', nameEn: 'Human Resources', nameAr: 'الموارد البشرية', source: 'Department', sourceName: 'Human Resources', sourceNameAr: 'الموارد البشرية', level: 'department', isOverride: false, invalidReason: null, ...over });

  test('each source maps to its kind, with the source name in the reader\'s language', () => {
    expect(previewResolution(dto({}), 'en')).toEqual({ kind: 'department', costCentreId: 'hr', sourceName: 'Human Resources' });
    expect(previewResolution(dto({}), 'ar')).toEqual({ kind: 'department', costCentreId: 'hr', sourceName: 'الموارد البشرية' });
    expect(previewResolution(dto({ sourceNameAr: null }), 'ar')).toMatchObject({ sourceName: 'Human Resources' });
    expect(previewResolution(dto({ source: 'ParentDepartment', level: 'parentDepartment' }), 'en')).toMatchObject({ kind: 'parentDepartment' });
    expect(previewResolution(dto({ source: 'Position', level: 'position', sourceName: 'Analyst' }), 'en')).toMatchObject({ kind: 'position', sourceName: 'Analyst' });
    expect(previewResolution(dto({ source: 'CompanyDefault', level: 'company', sourceName: null }), 'en')).toEqual({ kind: 'companyDefault', costCentreId: 'hr' });
    expect(previewResolution(dto({ source: 'Employee', level: 'employee', isOverride: true }), 'en')).toEqual({ kind: 'employee', costCentreId: 'hr' });
  });

  test('None is none; an invalid reason is invalid at its level and never carries a code the server did not send', () => {
    expect(previewResolution(dto({ costCentreId: null, code: null, source: 'None', level: null }), 'en')).toEqual({ kind: 'none', costCentreId: null });
    expect(previewResolution(dto({ costCentreId: null, code: null, nameEn: null, source: 'None', level: null, invalidReason: 'employee_cost_centre_invalid' }), 'en'))
      .toEqual({ kind: 'invalid', costCentreId: null, level: 'employee', code: '' });
    expect(previewResolution(dto({ costCentreId: null, code: 'OLD', source: 'Department', level: 'department', invalidReason: 'department_cost_centre_invalid' }), 'en'))
      .toEqual({ kind: 'invalid', costCentreId: null, level: 'department', code: 'OLD' });
    expect(previewResolution(dto({ costCentreId: null, code: null, source: 'None', level: null, invalidReason: 'parent_department_cost_centre_invalid' }), 'en')).toMatchObject({ level: 'parentDepartment' });
    expect(previewResolution(dto({ costCentreId: null, code: null, source: 'None', level: null, invalidReason: 'company_cost_centre_invalid' }), 'en')).toMatchObject({ level: 'company' });
  });
});

test.describe('costCentreRefusalKey: every cost-centre refusal in words, by code', () => {
  const err = (status: number, code: string) => ({ response: { status, data: { error: code, message: 'server words' } } });
  test('each code has its own sentence', () => {
    expect(costCentreRefusalKey(err(422, 'cost_centre_inactive'))).toBe('This cost centre is inactive. Choose an active one.');
    expect(costCentreRefusalKey(err(422, 'cost_centre_other_company'))).toBe('This cost centre belongs to another company. Choose one of this company’s cost centres.');
    expect(costCentreRefusalKey(err(422, 'cost_centre_not_found'))).toBe('This cost centre no longer exists. Choose another.');
    expect(costCentreRefusalKey(err(422, 'cost_centre_reason_required'))).toBe('Enter a reason for this change.');
    expect(costCentreRefusalKey(err(422, 'cost_centre_decision_required'))).toBe('Choose whether to keep the override or follow the new department.');
    expect(costCentreRefusalKey(err(409, 'cost_centre_state_changed'))).toMatch(/^Someone changed/);
    expect(costCentreRefusalKey(err(422, 'reason_invalid_characters'))).toMatch(/not allowed/);
    expect(costCentreRefusalKey(err(403, 'x'))).toBe('Only HR managers and admins can change this.');
  });
  test('anything else is left to the server sentence', () => {
    expect(costCentreRefusalKey(err(409, 'locked_into_approved_run'))).toBeNull();
    expect(costCentreRefusalKey(err(422, 'validation_failed'))).toBeNull();
    expect(costCentreRefusalKey(new Error('network'))).toBeNull();
  });
});
