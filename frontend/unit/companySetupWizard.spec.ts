import { expect, test } from '@playwright/test';
import type { CompanySetupContext, CompanySetupDraftBody, CompanySetupPlan } from '../src/api/companySetup';
import {
  buildTree, canCommit, defaultFirstPayrollMonth, eligibleCompanySuperAdmins, emptyBody, firstBlockingStep, groupPlan, molEstablishmentError,
  normalizeFieldPath, numberingPreview, planUsage, stepState, stepWarnings, validateStep,
  completeCompanyHref, draftHref, firstPayrollMonthLocked, isPlanChange, planCounts, planIsNoChange, readinessProgress, rowChanged, stepCopy, wizardMode,
  legacyWarnings, reasonFields, setupCodeCopy, setupLandingCompanyLink,
  gradeWarnings, sortedGradeIndexes, suggestedMidpoint,
  CHANGED_ELSEWHERE, chooseWeekend, countryDisplayName, currentValueLabel, editRow, resolveChangedElsewhere, withOneHeadOffice,
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

test('organisation rows: every row is this company’s own and needs a code and a name; references must be in the lists; grades must rise', () => {
  const b = saudiBody();
  b.departments = [{ code: 'HR', nameEn: '' }, { code: 'OPS', nameEn: 'Operations', parentCode: 'OPS' }, { code: 'FIN', nameEn: 'Finance', costCentreCode: 'NOPE' }];
  b.grades = [{ code: 'G1', nameEn: 'Grade 1', level: 0, minSalary: 0, midSalary: 0, maxSalary: 0 }, { code: 'G9', nameEn: 'Senior', level: 1, minSalary: 9000, midSalary: 8000, maxSalary: 12000 }];
  b.designations = [{ code: 'ACC', titleEn: 'Accountant', gradeCode: 'G7' }];
  expect(fields(validateStep(4, { body: b, context }))).toEqual([
    'departments.0.nameEn', 'departments.1.parentCode', 'departments.2.costCentreCode', 'grades.0.level', 'grades.1.maxSalary', 'designations.0.gradeCode',
  ]);
});

test('a wholly blank new row asks to be filled in or removed; an existing row is never "blank"', () => {
  const b = saudiBody();
  b.costCentres = [{ code: '', nameEn: '', nameAr: '' }, { existingId: 'cc-1', code: 'ADM', nameEn: '' }];
  const errors = validateStep(4, { body: b, context });
  expect(errors.map(e => [e.field, e.message])).toEqual([
    ['costCentres.0.code', 'Fill in this row or remove it.'],
    ['costCentres.1.nameEn', 'Enter the name in English.'],
  ]);
});

test('grade bands: advice in amber for a shared level, an inverted minimum and an empty band; "use the middle" only when the midpoint is empty', () => {
  const grades = [
    { code: 'G1', nameEn: 'Junior', level: 1, minSalary: 5000, midSalary: 6000, maxSalary: 7000 },
    { code: 'G2', nameEn: 'Senior', level: 2, minSalary: 4000, midSalary: 0, maxSalary: 9000 },
    { code: 'G3', nameEn: 'Lead', level: 2, minSalary: 0, midSalary: 0, maxSalary: 0 },
  ];
  expect(gradeWarnings(grades).map(w => w.field).sort()).toEqual(['grades.1.minSalary', 'grades.2.level', 'grades.2.minSalary']);
  expect(suggestedMidpoint(grades[1])).toBe(6500);
  expect(suggestedMidpoint(grades[0])).toBeNull();
  expect(sortedGradeIndexes([{ code: 'B', level: 3 }, { code: 'A', level: 1 }, { code: 'C', level: 1 }])).toEqual([1, 2, 0]);
});

test('company super admin: invite needs a name and an on-domain email; existing needs a user; hidden step never blocks', () => {
  const b = saudiBody();
  b.company.emailDomain = 'acme.sa';
  b.companyAdmin = { mode: 'invite', fullNameEn: 'Sara', email: 'sara@other.sa' };
  expect(validateStep(6, { body: b, context }).map(e => e.params)).toEqual([{ domain: 'acme.sa' }]);
  b.company.emailDomain = '';
  b.companyAdmin.email = 'sara@gmail.com';
  expect(validateStep(6, { body: b, context }).map(e => e.params)).toEqual([{ domain: 'gmail.com' }]);
  b.company.emailDomain = 'acme.sa';
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
  expect(stepState(3, { body: b, context, visited })).toBe('review'); // no locations: advice, not a blocker
  expect(stepState(4, { body: b, context, visited })).toBe('notStarted');
  expect(stepState(2, { body: b, context, visited, serverProblems: [{ step: 2, field: 'company.registrationNumber', code: 'cr_taken', message: 'Taken' }] })).toBe('blocked');
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

test('the structure tree puts every Organisation section under the company, with no shared catalog', () => {
  const b = saudiBody();
  b.branches[0].locations = [{ code: 'GATE', nameEn: 'Main gate' }];
  b.costCentres = [{ code: 'CC1', nameEn: 'Admin' }];
  b.departments = [{ code: 'HR', nameEn: 'Human Resources' }, { code: 'OPS', nameEn: 'Operations' }];
  b.grades = [{ code: 'G2', nameEn: 'Two', level: 2, minSalary: 0, midSalary: 0, maxSalary: 0 }, { code: 'G1', nameEn: 'One', level: 1, minSalary: 0, midSalary: 0, maxSalary: 0 }];
  const tree = buildTree(b, context);
  expect(tree.children).toHaveLength(1);
  const [company] = tree.children;
  expect(company.label).toBe('Riyadh Trading');
  expect(company.children.map(n => [n.id, n.part ?? null])).toEqual([['branches', null], ['costCentres', 'costCentres'], ['departments', 'departments'], ['grades', 'grades'], ['designations', 'designations']]);
  expect(company.children[0].children[0].children.map(n => n.label)).toEqual(['Main gate']);
  expect(company.children[2].children.map(n => [n.label, n.tag])).toEqual([['Human Resources', 'new'], ['Operations', 'new']]);
  expect(company.children[3].children.map(n => n.code)).toEqual(['G1', 'G2']); // by level
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

test('the first payroll month locks up front once the cutover the server reports has left Planned', () => {
  expect(firstPayrollMonthLocked({ cutoverStatus: 'Planned' })).toBe(false);
  expect(firstPayrollMonthLocked({ cutoverStatus: 'Active' })).toBe(true);
  expect(firstPayrollMonthLocked({ cutoverStatus: 'Closed' })).toBe(true);
  // No cutover yet, or an older server that sends nothing: the month stays editable and the planner decides.
  expect(firstPayrollMonthLocked({ cutoverStatus: null })).toBe(false);
  expect(firstPayrollMonthLocked({})).toBe(false);
  // As the prefill sends it (JSON): the status survives a draft round trip and still locks.
  const company = JSON.parse(JSON.stringify({ ...emptyBody('SA').company, cutoverStatus: 'Active' })) as CompanySetupDraftBody['company'];
  expect(firstPayrollMonthLocked(company)).toBe(true);
});

/** An existing-mode prefill as the server sends it (A9): every existing row and block carries its baseline. */
function prefilled(): CompanySetupDraftBody {
  const b = saudiBody();
  b.existingCompanyId = 'co-1';
  b.company = { ...b.company, tradeName: 'Riyadh', baseline: { legalNameEn: 'Riyadh Trading', tradeName: 'Riyadh', firstPayrollMonth: '2026-11' } };
  b.branches = [
    { existingId: 'br-hq', code: 'HQ', nameEn: 'Head Office', nameAr: '', city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: true,
      baseline: { nameEn: 'Head Office', nameAr: null, city: 'Riyadh', timeZoneId: 'Asia/Riyadh', isHeadOffice: 'true' },
      locations: [{ existingId: 'loc-1', code: 'GATE', nameEn: 'Gate', latitude: 24.7, longitude: 46.7, radiusMeters: 150,
        baseline: { nameEn: 'Gate', nameAr: null, latitude: '24.7', longitude: '46.7', radiusMeters: '150' } }] },
    { existingId: 'br-jed', code: 'JED', nameEn: 'Jeddah', nameAr: '', city: 'Jeddah', timeZoneId: 'Asia/Riyadh', isHeadOffice: false,
      baseline: { nameEn: 'Jeddah', nameAr: null, city: 'Jeddah', timeZoneId: 'Asia/Riyadh', isHeadOffice: 'false' }, locations: [] },
  ];
  b.costCentres = [{ existingId: 'cc-1', code: 'ADM', nameEn: 'Admin', baseline: { nameEn: 'Admin' } }];
  b.rules = {
    ...b.rules,
    workWeek: { weekendDays: 'Fri,Sat', override: false, baseline: { weekendDays: 'Fri,Sat', override: 'false' } },
    employeeNumbering: { ...b.rules.employeeNumbering, nextSequence: 42, baseline: { prefix: 'EMP', nextSequence: '42' } },
  };
  return b;
}
const baselines = (b: CompanySetupDraftBody) => ({
  company: b.company.baseline,
  branches: b.branches.map(r => [r.baseline, r.locations.map(l => l.baseline)]),
  costCentres: b.costCentres.map(r => r.baseline),
  workWeek: b.rules.workWeek.baseline,
  numbering: b.rules.employeeNumbering.baseline,
});

test('A9: every edit, autosave and resume carries each baseline back unchanged', () => {
  const start = prefilled();
  const expected = structuredClone(baselines(start));
  let b = start;
  // The form's own edits, as the steps make them.
  b = { ...b, company: { ...b.company, tradeName: 'Riyadh Trading Co', firstPayrollMonth: '2026-12' } };
  b = { ...b, branches: withOneHeadOffice(b.branches, editRow(b.branches, 1, 'isHeadOffice', true)) };
  b = { ...b, branches: editRow(b.branches, 0, 'nameEn', 'Main office') };
  b = { ...b, branches: b.branches.map((r, i) => (i === 0 ? { ...r, locations: editRow(r.locations, 0, 'radiusMeters', 300) } : r)) };
  b = { ...b, costCentres: editRow(b.costCentres, 0, 'nameEn', 'Administration') };
  b = { ...b, rules: { ...b.rules, workWeek: chooseWeekend(b.rules.workWeek, 'Sat', 'Fri,Sat'), employeeNumbering: { ...b.rules.employeeNumbering, nextSequence: 50 } } };
  // A new row has no baseline, and needs none.
  b = { ...b, branches: [...b.branches, { code: 'DMM', nameEn: 'Dammam', city: 'Dammam', timeZoneId: 'Asia/Riyadh', isHeadOffice: false, locations: [] }] };
  // Autosave PUT and resume GET: JSON both ways.
  const resumed = JSON.parse(JSON.stringify(b)) as CompanySetupDraftBody;
  expect(resumed.branches[1].isHeadOffice).toBe(true);
  expect(resumed.branches[0].isHeadOffice).toBe(false);
  expect(resumed.rules.workWeek).toMatchObject({ weekendDays: 'Sat', override: true });
  expect(resumed.branches[2].baseline).toBeUndefined();
  const kept = baselines(resumed);
  kept.branches = kept.branches.slice(0, 2);
  expect(kept).toEqual(expected);
});

test('A9: take the current value or keep mine, on exactly the field the problem names', () => {
  const body = { ...prefilled(), branches: editRow(prefilled().branches, 1, 'nameEn', 'Jeddah (mine)') };
  const problem = { step: 3, field: 'branches.1.nameEn', code: CHANGED_ELSEWHERE, message: '', current: 'Jeddah (Setup)' };
  const theirs = resolveChangedElsewhere(body, problem, 'theirs');
  expect(theirs.branches[1].nameEn).toBe('Jeddah (Setup)');
  expect(theirs.branches[1].baseline).toEqual({ ...body.branches[1].baseline, nameEn: 'Jeddah (Setup)' });
  const mine = resolveChangedElsewhere(body, problem, 'mine');
  expect(mine.branches[1].nameEn).toBe('Jeddah (mine)');
  expect(mine.branches[1].baseline).toEqual({ ...body.branches[1].baseline, nameEn: 'Jeddah (Setup)' });
  expect(body.branches[1].baseline?.nameEn).toBe('Jeddah'); // the input body is not mutated
  expect(mine.branches[0]).toEqual(body.branches[0]);
  // Typed fields come back typed; the server's dotted and bracketed paths both resolve.
  const radius = resolveChangedElsewhere(body, { field: 'branches[0].locations[0].radiusMeters', current: '200' }, 'theirs');
  expect(radius.branches[0].locations[0].radiusMeters).toBe(200);
  const head = resolveChangedElsewhere(body, { field: 'branches.1.isHeadOffice', current: 'true' }, 'theirs');
  expect(head.branches[1].isHeadOffice).toBe(true);
  const week = resolveChangedElsewhere(body, { field: 'rules.workWeek.override', current: 'true' }, 'theirs');
  expect(week.rules.workWeek.override).toBe(true);
  const trade = resolveChangedElsewhere(body, { field: 'company.tradeName', current: null }, 'theirs');
  expect(trade.company.tradeName).toBe('');
  expect(currentValueLabel({ field: 'company.tradeName', current: null })).toBe('(empty)');
  expect(currentValueLabel({ field: 'branches.1.isHeadOffice', current: 'false' })).toBe('No');
  expect(currentValueLabel({ field: 'branches.1.nameEn', current: 'Jeddah (Setup)' })).toBe('Jeddah (Setup)');
  // A path that names nothing leaves the body as it is.
  expect(resolveChangedElsewhere(body, { field: 'branches.9.nameEn', current: 'x' }, 'theirs')).toBe(body);
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
  // Addendum B: there is no shared catalog, so reuse is never a change in either mode.
  expect(isPlanChange('reuse', 'create')).toBe(false);
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
    'work_week_override_kept', 'workspace_defaults_need_group_scope', 'shared_row_not_edited', 'draft_needs_refresh']) expect(setupCodeCopy(code), code).toBeTruthy();
  expect(setupCodeCopy('mol_required')).toBeUndefined();
});

test('a stored country that is not a region code is shown as written, never thrown', () => {
  const names = new Intl.DisplayNames(['en'], { type: 'region' });
  expect(countryDisplayName(names, 'sa')).toBe('Saudi Arabia');
  // Intl.DisplayNames.of throws a RangeError for each of these; Setup → Guided setup rendered them and crashed.
  expect(countryDisplayName(names, 'SAU')).toBe('SAU');
  expect(countryDisplayName(names, 'KSA')).toBe('KSA');
  expect(countryDisplayName(names, 'AE ')).toBe('United Arab Emirates');
  expect(countryDisplayName(names, ' ')).toBe('');
  expect(countryDisplayName(names, null)).toBe('');
  expect(countryDisplayName(null, 'SA')).toBe('SA');
});

test('a Guided setup deep link is a Setup arrival that names a company on the guided path, never a reload', () => {
  const link = (query: string, navigation?: string) => setupLandingCompanyLink(new URLSearchParams(query), navigation);
  // Links from elsewhere in the app, notifications and old studio bookmarks.
  expect(link('tab=aiSetup&path=guided&companyId=co-1')).toBe('co-1');
  expect(link('tab=aiSetup&mode=starter&companyId=co-1')).toBe('co-1');
  expect(link('tab=aiSetup&companyId=co-1')).toBe('co-1');
  expect(link('companyId=co-1')).toBe('co-1');
  // A forged company is still handed over: the chooser refuses it and says so.
  expect(link('tab=aiSetup&path=guided&companyId=forged', 'navigate')).toBe('forged');
  // No company, or another path or section: nothing to open.
  expect(link('tab=aiSetup&path=guided')).toBeNull();
  expect(link('tab=aiSetup&path=configuration&companyId=co-1')).toBeNull();
  expect(link('tab=aiSetup&area=payroll&companyId=co-1')).toBeNull();
  expect(link('tab=aiSetup&path=policyStarter&companyId=co-1')).toBeNull();
  expect(link('tab=grades&companyId=co-1')).toBeNull();
  // A reload or history restore brings back the scope Setup wrote into the address, not a followed link.
  expect(link('tab=aiSetup&path=guided&companyId=co-1', 'reload')).toBeNull();
  expect(link('tab=aiSetup&path=guided&companyId=co-1', 'back_forward')).toBeNull();
});

// ── Addendum B4: parts, and the deterministic helpers that drive the reader ──
import {
  appendCopiedRows, firstBlockingPosition, latinDigits, namesLookAlike, partOfField, positionOfField, resumePart, sectionChanges,
  stepStateFromParts, suggestCode, suggestCostCentre, withPart, wizardPositions,
} from '../src/lib/companySetupWizard';

test('steps 4 and 5 expand into their parts, in dependency order; field paths map to their part', () => {
  const keys = wizardPositions([1, 2, 3, 4, 5, 6, 7]).map(p => (p.part ? `${p.step}:${p.part}` : String(p.step)));
  expect(keys).toEqual(['1', '2', '3', '4:costCentres', '4:departments', '4:grades', '4:designations', '5:workRules', '5:leave', '5:endOfService', '5:moreSettings', '6', '7']);
  expect(partOfField('Departments[2].CostCentreCode')).toBe('departments');
  expect(positionOfField('grades.0.maxSalary')).toEqual({ step: 4, part: 'grades' });
  expect(positionOfField('rules.employeeNumbering.nextSequence')).toEqual({ step: 5, part: 'workRules' });
  expect(positionOfField('branches.0.code')).toEqual({ step: 3 });
});

test('the first blocked part stops the rail; resume lands on it, else on the part the address names', () => {
  const b = saudiBody();
  b.departments = [{ code: 'OPS', nameEn: '' }];
  const positions = wizardPositions([2, 3, 4, 5, 6, 7]);
  expect(firstBlockingPosition(positions, { body: b, context })).toEqual({ step: 4, part: 'departments' });
  expect(resumePart(4, 'designations', { body: b, context })).toBe('departments');
  b.departments[0].nameEn = 'Operations';
  expect(resumePart(4, 'designations', { body: b, context })).toBe('designations');
  expect(resumePart(4, 'nonsense', { body: b, context })).toBe('costCentres');
  expect(resumePart(2, 'grades', { body: b, context })).toBeUndefined();
});

test('the address keeps the draft and follows the part', () => {
  expect(withPart('/setup/new-company?draft=d1', 'grades')).toBe('/setup/new-company?draft=d1&part=grades');
  expect(withPart('/setup/new-company?draft=d1&part=grades', undefined)).toBe('/setup/new-company?draft=d1');
});

test('a step with parts is blocked when any part is, in progress until every part is seen', () => {
  expect(stepStateFromParts(['notStarted', 'notStarted'])).toBe('notStarted');
  expect(stepStateFromParts(['done', 'notStarted'])).toBe('inProgress');
  expect(stepStateFromParts(['done', 'blocked'])).toBe('blocked');
  expect(stepStateFromParts(['skipped', 'review', 'done'])).toBe('review');
  expect(stepStateFromParts(['skipped', 'done'])).toBe('done');
});

test('suggested codes come from the English name, never clash, and say nothing when the name gives nothing', () => {
  expect(suggestCode('Human Resources', [])).toBe('HR');
  expect(suggestCode('Finance', [])).toBe('FIN');
  expect(suggestCode('Grade 3', [])).toBe('G3');
  expect(suggestCode('Human Resources', ['HR'])).toBe('HR2');
  expect(suggestCode('Finance Department', [], 'CC_')).toBe('CC_FIN');
  expect(suggestCode('', [])).toBe('');
  expect(suggestCode('الموارد البشرية', [])).toBe('');
});

test('look-alike names: HR ↔ Human Resources, filler words ignored, different teams are different', () => {
  expect(namesLookAlike('HR', 'Human Resources')).toBe(true);
  expect(namesLookAlike('Finance Department', 'finance')).toBe(true);
  expect(namesLookAlike('Finance', 'Facilities')).toBe(false);
  expect(namesLookAlike('', 'Finance')).toBe(false);
});

test('a department is offered the cost centre whose name starts with its own, for the reader to confirm', () => {
  const centres = [{ code: 'CC_HR', nameEn: 'Human Resources Cost Center' }, { code: 'CC_FIN', nameEn: 'Finance Cost Centre' }];
  expect(suggestCostCentre('Finance', centres)).toBe('CC_FIN');
  expect(suggestCostCentre('Human Resources', centres)).toBe('CC_HR');
  expect(suggestCostCentre('Sales', centres)).toBeUndefined();
});

test('copy from a sister company appends by value, skipping codes already here', () => {
  const current: { code: string; nameEn: string; existingId?: string | null; baseline?: Record<string, string | null> | null }[] = [{ code: 'HR', nameEn: 'Human Resources', existingId: 'd1' }];
  const copied: typeof current = [{ code: 'hr', nameEn: 'HR (sister)' }, { code: 'FIN', nameEn: 'Finance', existingId: 'sister-row', baseline: { nameEn: 'Finance' } }];
  const { rows, added, skipped } = appendCopiedRows(current, copied);
  expect([added, skipped]).toEqual([1, 1]);
  expect(rows).toEqual([current[0], { code: 'FIN', nameEn: 'Finance' }]);
});

test('a section has changes when it adds a row or edits one the company has', () => {
  const baseline = saudiBody();
  baseline.costCentres = [{ existingId: 'c1', code: 'ADM', nameEn: 'Administration' }];
  const body = structuredClone(baseline);
  expect(sectionChanges(body, baseline, 'costCentres')).toEqual({ added: 0, changed: 0 });
  body.costCentres = [{ ...body.costCentres[0], nameEn: 'Admin' }, { code: 'OPS', nameEn: 'Operations' }];
  expect(sectionChanges(body, baseline, 'costCentres')).toEqual({ added: 1, changed: 1 });
});

test('Arabic-Indic and Persian digits are checked as the digits they are; a Saudi VAT number is 15 digits starting and ending with 3', () => {
  expect(latinDigits('١٠١٠١٢٣٤٥٦')).toBe('1010123456');
  expect(latinDigits('۷-۱۲۳')).toBe('7-123');
  const b = saudiBody();
  b.company.registrationNumber = '١٠١٠١٢٣٤٥٦';
  b.company.taxNumber = '300000000000003';
  expect(validateStep(2, { body: b, context })).toEqual([]);
  b.company.taxNumber = '123';
  expect(fields(validateStep(2, { body: b, context }))).toEqual(['company.taxNumber']);
});
