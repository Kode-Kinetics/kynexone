import { expect, test } from '@playwright/test';
import type { CompanySetupContext, CompanySetupDraftBody, CompanySetupPlan } from '../src/api/companySetup';
import {
  buildTree, canCommit, defaultFirstPayrollMonth, eligibleCompanySuperAdmins, emptyBody, firstBlockingStep, groupPlan, molEstablishmentError,
  normalizeFieldPath, numberingPreview, planUsage, stepState, stepWarnings, validateStep,
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
