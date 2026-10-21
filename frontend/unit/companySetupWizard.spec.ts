import { expect, test } from '@playwright/test';
import type { CompanySetupContext, CompanySetupDraftBody, CompanySetupPlan } from '../src/api/companySetup';
import {
  buildTree, canCommit, defaultFirstPayrollMonth, eligibleCompanySuperAdmins, emptyBody, firstBlockingStep, groupPlan, molEstablishmentError,
  normalizeFieldPath, numberingPreview, planUsage, stepState, stepWarnings, validateStep,
  completeCompanyHref, draftHref, firstPayrollMonthLocked, isPlanChange, planCounts, planIsNoChange, readinessProgress, rowChanged, stepCopy, wizardMode,
  legacyWarnings, reasonFields, setupCodeCopy,
} from '../src/lib/companySetupWizard';

const context: CompanySetupContext = {
  canCreate: true, creationMode: 'GroupSelfServiceWithinLimit', accountType: 'Group', companiesUsed: 2, companiesAllowed: 5,
  isGroupScope: true, sisterCompanies: [{ id: 'c1', legalNameEn: 'Sister KSA', countryCode: 'SA' }],
  sharedCatalog: { departments: [{ code: 'HR', nameEn: 'Human Resources', nameAr: 'الموارد البشرية' }], designations: [], grades: [{ code: 'G1', nameEn: 'Grade 1', currency: 'SAR' }] },
  gradesCurrency: 'SAR',
};

function saudiBody(): CompanySetupDraftBody {
  const b = emptyBody('SA');
  b.company = { ...b.company, legalNameEn: 'Riyadh Trading', legalNameAr: 'شركة الرياض', registrationNumber: '1010123456', molEstablishmentNumber: '7-1234567', currency: 'SAR', firstPayrollMonth: '2026-11' };
  b.branches = [{ code: 'HQ', nameEn: 'Head Office', nameAr: '', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: true, locations: [] }];
  return b;
}
const fields = (errors: { field: string }[]) => errors.map(e => e.field);

test('first payroll month defaults to next month, rolling the year in December', () => {
  expect(defaultFirstPayrollMonth(new Date(2026, 9, 20))).toBe('2026-11');
  expect(defaultFirstPayrollMonth(new Date(2026, 11, 3))).toBe('2027-01');
});

test('a complete Saudi company passes step 2', () => {
  expect(validateStep(2, { body: saudiBody(), context })).toEqual([]);
});

test('Saudi step 2 requires the Arabic name, a 10-digit CR, a valid MOL number and SAR', () => {
  const b = saudiBody();
  b.company = { ...b.company, legalNameAr: '', registrationNumber: '12345', molEstablishmentNumber: '', currency: 'AED' };
  expect(fields(validateStep(2, { body: b, context }))).toEqual([
    'company.legalNameAr', 'company.currency', 'company.registrationNumber', 'company.molEstablishmentNumber',
  ]);
});

test('outside Saudi Arabia the Arabic name and MOL number are optional and any CR shape is accepted', () => {
  const b = saudiBody();
  b.company = { ...b.company, countryCode: 'AE', currency: 'AED', legalNameAr: '', registrationNumber: 'DXB-77', molEstablishmentNumber: '' };
  expect(validateStep(2, { body: b, context })).toEqual([]);
});

test('MOL establishment number mirrors KsaWageFileRules.ValidateEstablishmentId', () => {
  expect(molEstablishmentError('7-1234567')).toBeNull();
  expect(molEstablishmentError('71234567')).toBeNull();
  expect(molEstablishmentError('')).toMatch(/Enter the MOL/);
  expect(molEstablishmentError('7-12-34')).toMatch(/labour-office number/);
  expect(molEstablishmentError('A-123')).toMatch(/labour-office number/);
  expect(molEstablishmentError('0-0000')).toMatch(/all zeros/);
});

test('optional Unified National Number and email domain are validated only when entered', () => {
  const b = saudiBody();
  b.company = { ...b.company, unifiedNationalNumber: '123', emailDomain: '@acme.sa' };
  expect(fields(validateStep(2, { body: b, context }))).toEqual(['company.unifiedNationalNumber', 'company.emailDomain']);
  b.company = { ...b.company, unifiedNationalNumber: '7001234567', emailDomain: 'acme.sa' };
  expect(validateStep(2, { body: b, context })).toEqual([]);
});

test('branches need exactly one head office, unique codes, and code/name/city on every row', () => {
  const b = saudiBody();
  b.branches = [];
  expect(fields(validateStep(3, { body: b, context }))).toEqual(['branches']);
  b.branches = [
    { code: 'HQ', nameEn: 'HQ', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: true, locations: [] },
    { code: 'hq', nameEn: '', city: '', timeZoneId: 'Asia/Riyadh', isHeadOffice: true, locations: [{ code: '', nameEn: 'Gate' }] },
  ];
  expect(fields(validateStep(3, { body: b, context }))).toEqual([
    'branches', 'branches.1.nameEn', 'branches.1.city', 'branches.1.locations.0.code', 'branches.1.code',
  ]);
});

test('no locations is a warning, not a blocker', () => {
  const b = saudiBody();
  expect(validateStep(3, { body: b, context })).toEqual([]);
  expect(stepWarnings(3, { body: b, context }).map(w => w.field)).toEqual(['locations']);
  b.branches[0].locations = [{ code: 'GATE', nameEn: 'Main gate' }];
  expect(stepWarnings(3, { body: b, context })).toEqual([]);
});

test('organisation rows: reused catalog codes need no names, new rows do, grades must rise', () => {
  const b = saudiBody();
  b.departments = [{ code: 'HR', nameEn: '' }, { code: 'OPS', nameEn: '' }, { code: 'FIN', nameEn: 'Finance', costCentreCode: 'NOPE' }];
  b.grades = [{ code: 'G1', nameEn: '', level: 0, minSalary: 0, midSalary: 0, maxSalary: 0 }, { code: 'G9', nameEn: 'Senior', level: 1, minSalary: 9000, midSalary: 8000, maxSalary: 12000 }];
  expect(fields(validateStep(4, { body: b, context }))).toEqual([
    'departments.1.nameEn', 'departments.2.costCentreCode', 'grades.1.maxSalary',
  ]);
});

test('a grade currency different from the company currency warns on step 4', () => {
  const b = saudiBody();
  b.company.currency = 'AED';
  b.grades = [{ code: 'G2', nameEn: 'Grade 2', level: 2, minSalary: 1, midSalary: 2, maxSalary: 3 }];
  expect(stepWarnings(4, { body: b, context }).map(w => w.params)).toEqual([{ gradeCurrency: 'SAR', currency: 'AED' }]);
});

test('company super admin: invite needs a name and an on-domain email; existing needs a user; hidden step never blocks', () => {
  const b = saudiBody();
  b.company.emailDomain = 'acme.sa';
  b.companyAdmin = { mode: 'invite', fullNameEn: 'Sara', email: 'sara@gmail.com' };
  expect(validateStep(6, { body: b, context }).map(e => e.params)).toEqual([{ domain: 'acme.sa' }]);
  b.companyAdmin.email = 'sara@acme.sa';
  expect(validateStep(6, { body: b, context })).toEqual([]);
  b.companyAdmin = { mode: 'existing' };
  expect(fields(validateStep(6, { body: b, context }))).toEqual(['companyAdmin.existingUserId']);
  expect(validateStep(6, { body: b, context, adminStepVisible: false })).toEqual([]);
  b.companyAdmin = { mode: 'groupOnly' };
  expect(validateStep(6, { body: b, context })).toEqual([]);
});

test('step state and the first blocking step drive the rail', () => {
  const b = saudiBody();
  const visited = new Set([1, 2, 3] as const);
  expect(stepState(2, { body: b, context, visited })).toBe('done');
  expect(stepState(3, { body: b, context, visited })).toBe('warning'); // no locations
  expect(stepState(4, { body: b, context, visited })).toBe('notStarted');
  expect(stepState(2, { body: b, context, visited, serverProblems: [{ step: 2, field: 'company.registrationNumber', code: 'cr_taken', message: 'Taken' }] })).toBe('warning');
  b.company.registrationNumber = '';
  expect(firstBlockingStep([2, 3, 4, 5, 6, 7], { body: b, context })).toBe(2);
});

test('server field paths normalise to the client keys', () => {
  expect(normalizeFieldPath('Branches[0].Code')).toBe('branches.0.code');
  expect(normalizeFieldPath('company.RegistrationNumber')).toBe('company.registrationNumber');
});

test('the plan groups by step, and any conflict or problem blocks Create', () => {
  const plan: CompanySetupPlan = {
    ok: true, problems: [], warnings: [],
    items: [
      { area: 'grade', code: 'G1', label: 'Grade 1', action: 'reuse' },
      { area: 'company', code: '1010123456', label: 'Riyadh Trading', action: 'create' },
      { area: 'branch', code: 'HQ', label: 'Head Office', action: 'create' },
      { area: 'costCentre', code: 'CC1', label: 'Admin', action: 'conflict', reason: 'Code CC1 is used by Sister KSA' },
      { area: 'gccSetting', code: 'SA', label: 'EOSB', action: 'create' },
    ],
  };
  expect(groupPlan(plan).map(g => [g.step, g.items.length, g.conflicts])).toEqual([[2, 1, 0], [3, 1, 0], [4, 2, 1], [5, 1, 0]]);
  expect(canCommit(plan)).toBe(false);
  expect(canCommit({ ...plan, items: plan.items.filter(i => i.action !== 'conflict') })).toBe(true);
  expect(canCommit({ ...plan, items: [], problems: [{ step: 2, field: 'x', code: 'y', message: 'z' }] })).toBe(false);
  expect(canCommit(null)).toBe(false);
});

test('the structure tree tags shared rows reused and shows the catalog name', () => {
  const b = saudiBody();
  b.branches[0].locations = [{ code: 'GATE', nameEn: 'Main gate' }];
  b.costCentres = [{ code: 'CC1', nameEn: 'Admin' }];
  b.departments = [{ code: 'hr', nameEn: 'typed name' }, { code: 'OPS', nameEn: 'Operations' }];
  const tree = buildTree(b, context);
  const [company, catalog] = tree.children;
  expect(company.label).toBe('Riyadh Trading');
  expect(company.children[0].children[0].children.map(n => n.label)).toEqual(['Main gate']);
  expect(company.children[1].children.map(n => n.code)).toEqual(['CC1']);
  const departments = catalog.children[0].children;
  expect(departments.map(n => [n.label, n.tag])).toEqual([['Human Resources', 'reused'], ['Operations', 'new']]);
});

test('plan usage and numbering preview', () => {
  expect(planUsage(context)).toEqual({ used: 2, allowed: 5, full: false });
  expect(planUsage({ ...context, companiesUsed: 5 })?.full).toBe(true);
  expect(planUsage({ ...context, companiesAllowed: null })?.allowed).toBeNull();
  const b = saudiBody();
  b.rules.employeeNumbering = { prefix: 'RT', useCountryPrefix: true, useBranchPrefix: true, useDepartmentPrefix: false, useYear: true, paddingLength: 4, nextSequence: 7 };
  expect(numberingPreview(b, 2026)).toBe('RT-SA-HQ-2026-0007');
});

test('step 6 offers only active users who already hold Admin as an existing company super admin', () => {
  const user = (id: string, roles: string[], over: Partial<{ isActive: boolean; status: string }> = {}) =>
    ({ id, roles, isActive: true, status: 'Active', ...over });
  const users = [
    user('company-admin', ['Admin']),
    user('group-admin', ['admin', 'HR Director']),
    user('hr-manager', ['HR Manager']),
    user('no-roles', []),
    user('locked-admin', ['Admin'], { status: 'Locked' }),
    user('inactive-admin', ['Admin'], { isActive: false }),
    { id: 'roles-missing', roles: null, isActive: true, status: 'Active' },
  ];
  expect(eligibleCompanySuperAdmins(users).map(u => u.id)).toEqual(['company-admin', 'group-admin']);
  expect(eligibleCompanySuperAdmins([])).toEqual([]);
});

// ── Addendum A: existing mode ────────────────────────────────────────────────

function existingBody(): CompanySetupDraftBody {
  const b = saudiBody();
  b.existingCompanyId = 'co-evostel';
  b.branches = [{ ...b.branches[0], existingId: 'br-1' }, { code: 'JED', nameEn: 'Jeddah', nameAr: '', city: 'Jeddah', timeZoneId: 'Asia/Riyadh', isHeadOffice: false, locations: [] }];
  return b;
}

test('the mode follows existingCompanyId, and only step 1 and the review read differently', () => {
  expect(wizardMode(saudiBody())).toBe('create');
  expect(wizardMode(existingBody())).toBe('existing');
  expect(stepCopy(1, 'existing').title).toBe('Summary');
  expect(stepCopy(7, 'existing').title).toBe('Review & update');
  expect(stepCopy(3, 'existing')).toEqual(stepCopy(3, 'create'));
  expect(stepCopy(1, 'create').title).toBe('Start');
});

test('existing mode never asks for a copy source on step 1', () => {
  const b = existingBody();
  b.startFrom = 'copy';
  expect(validateStep(1, { body: b, context })).toEqual([]);
});

test('drafts resume at their own address, with ids encoded', () => {
  expect(draftHref({ id: 'd1' })).toBe('/setup/new-company?draft=d1');
  expect(draftHref({ id: 'd2', existingCompanyId: 'co-1' })).toBe('/setup/company/co-1/complete?draft=d2');
  expect(completeCompanyHref('a/b')).toBe('/setup/company/a%2Fb/complete');
});

test('the first payroll month locks only once the cutover has left Planned; unknown leaves it to the server', () => {
  expect(firstPayrollMonthLocked({ cutoverStatus: 'Planned' })).toBe(false);
  expect(firstPayrollMonthLocked({ cutoverStatus: 'planned' })).toBe(false);
  expect(firstPayrollMonthLocked({ cutoverStatus: 'Live' })).toBe(true);
  expect(firstPayrollMonthLocked({ cutoverStatus: null })).toBe(false);
  expect(firstPayrollMonthLocked({})).toBe(false);
});

test('readiness progress is clamped and complete only when every item is done', () => {
  expect(readinessProgress({ readinessDone: 5, readinessTotal: 9 })).toEqual({ done: 5, total: 9, percent: 56, complete: false });
  expect(readinessProgress({ readinessDone: 9, readinessTotal: 9 }).complete).toBe(true);
  expect(readinessProgress({ readinessDone: 12, readinessTotal: 9 }).done).toBe(9);
  expect(readinessProgress({ readinessDone: 0, readinessTotal: 0 })).toEqual({ done: 0, total: 0, percent: 0, complete: false });
});

test('an existing row is changed only against its own prefilled copy; new rows are never "changed"', () => {
  const baseline = existingBody().branches;
  const edited = { ...baseline[0], nameEn: 'Head Office Riyadh' };
  expect(rowChanged(edited, baseline, ['nameEn', 'city'])).toBe(true);
  expect(rowChanged({ ...baseline[0], nameEn: ' Head Office ' }, baseline, ['nameEn'])).toBe(false);
  expect(rowChanged(baseline[1], baseline, ['nameEn'])).toBe(false);
  expect(rowChanged({ ...baseline[0], existingId: 'unknown' }, baseline, ['nameEn'])).toBe(false);
});

test('the review opens only what changes in existing mode; unchanged and reused rows are folded away', () => {
  expect(['create', 'update', 'conflict'].every(a => isPlanChange(a as never, 'existing'))).toBe(true);
  expect(isPlanChange('unchanged', 'existing')).toBe(false);
  expect(isPlanChange('reuse', 'existing')).toBe(false);
  // Create mode keeps showing reused catalog rows as part of what Create does.
  expect(isPlanChange('reuse', 'create')).toBe(true);
  const plan: CompanySetupPlan = { ok: true, problems: [], warnings: [], items: [
    { area: 'company', code: '1010123456', label: 'Evostel', action: 'unchanged' },
    { area: 'branch', code: 'HQ', label: 'Head Office', action: 'update', changedFields: ['nameEn'] },
    { area: 'branch', code: 'JED', label: 'Jeddah', action: 'create' },
    { area: 'department', code: 'HR', label: 'Human Resources', action: 'reuse' },
  ] };
  expect(planCounts(plan)).toEqual({ create: 1, update: 1, unchanged: 1, reuse: 1, conflict: 0 });
  expect(planIsNoChange(plan)).toBe(false);
  expect(canCommit(plan)).toBe(true);
  const untouched = { ...plan, items: plan.items.map(i => ({ ...i, action: i.action === 'reuse' ? 'reuse' as const : 'unchanged' as const })) };
  expect(planIsNoChange(untouched)).toBe(true);
  expect(canCommit(untouched)).toBe(true);
  expect(planIsNoChange(null)).toBe(false);
});

test('the structure tree marks existing rows apart from new ones and the company as existing', () => {
  const tree = buildTree(existingBody(), context);
  const company = tree.children[0];
  expect(company.tag).toBe('existing');
  const branches = company.children.find(n => n.id === 'branches')!.children;
  expect(branches.map(b => [b.code, b.tag])).toEqual([['HQ', 'existing'], ['JED', 'new']]);
  expect(buildTree(saudiBody(), context).children[0].tag).toBe('new');
});

test('existing mode: registration values saved before today are advice, a changed one is checked as on create', () => {
  const baseline = existingBody();
  baseline.company = { ...baseline.company, molEstablishmentNumber: '', registrationNumber: '12345' };
  const body = structuredClone(baseline);
  expect(validateStep(2, { body, context, baseline })).toEqual([]);
  expect(legacyWarnings({ body, context, baseline }).map(w => w.field)).toEqual(['company.registrationNumber', 'company.molEstablishmentNumber']);
  expect(stepWarnings(2, { body, context, baseline }).map(w => w.field)).toContain('company.molEstablishmentNumber');
  body.company.registrationNumber = '99';
  expect(fields(validateStep(2, { body, context, baseline }))).toEqual(['company.registrationNumber']);
  // Create mode is unchanged: the same values block.
  const created = structuredClone(baseline); created.existingCompanyId = undefined;
  expect(fields(validateStep(2, { body: created, context }))).toEqual(['company.registrationNumber', 'company.molEstablishmentNumber']);
});

test('existing mode: the employee-number counter never goes below what the company has issued', () => {
  const baseline = existingBody();
  baseline.rules.employeeNumbering.nextSequence = 42;
  const body = structuredClone(baseline);
  body.rules.employeeNumbering.nextSequence = 7;
  expect(validateStep(5, { body, context, baseline })).toEqual([expect.objectContaining({ field: 'rules.employeeNumbering.nextSequence', params: { number: 42 } })]);
  body.rules.employeeNumbering.nextSequence = 42;
  expect(validateStep(5, { body, context, baseline })).toEqual([]);
});

test('the planner\'s English change words map to dictionary labels; anything unknown keeps the server text', () => {
  expect(reasonFields('Changes: name (English), city.')).toEqual(['Name (English)', 'City']);
  expect(reasonFields('Changes: CR number, email domain.')).toEqual(['Commercial registration (CR) number', 'Work email domain (optional)']);
  expect(reasonFields('Changes: from 2026-11-01.')).toBeNull();
  expect(reasonFields('Already exists')).toBeNull();
  expect(reasonFields(null)).toBeNull();
});

test('every existing-mode server code has what-to-do-next copy', () => {
  for (const code of ['country_locked', 'currency_locked', 'draft_target_locked', 'existing_code_locked', 'first_payroll_month_locked', 'company_not_manageable',
    'company_admin_required', 'company_not_active', 'existing_company_draft_open', 'existing_row_unknown', 'branch_code_taken', 'sequence_cannot_go_back',
    'work_week_override_kept', 'workspace_defaults_need_group_scope', 'shared_row_not_edited']) expect(setupCodeCopy(code), code).toBeTruthy();
  expect(setupCodeCopy('mol_required')).toBeUndefined();
});
