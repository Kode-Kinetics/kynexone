import { expect, test, type Page } from '@playwright/test';

// Setup > Headcount & budget: pay-range reconciliation. Every API call is intercepted; nothing is written.
// Contract (camelCase, nullable): level rows carry `pay`, department rows carry `payPlan`; null = withheld.

const band = (min: number, mid: number, max: number) => ({ min, mid, max, currency: 'SAR' });
type PayStatus = 'priced' | 'noRange' | 'currencyMismatch' | null;
const level = (id: string, name: string, budgeted: number | null, current: number, pay: ReturnType<typeof band> | null,
  extra: { payStatus?: PayStatus; note?: string | null } = {}) => ({
  staffingLevelId: id, levelCode: id, levelNameEn: name, levelNameAr: '', budgeted, current,
  gap: budgeted === null ? null : budgeted - current, exitingIncumbents: 0, positionsAtLevel: 0, positionsExceedBudget: false, pay,
  payStatus: extra.payStatus === undefined ? (pay ? 'priced' : 'noRange') : extra.payStatus, note: extra.note ?? null,
});
const department = (id: string, name: string, levels: unknown[], payPlan: unknown, note: string | null = null) => ({
  departmentId: id, departmentName: name, costCenterId: null, approvedHeadcount: 5, currentHeadcount: 3, openRequisitionHeadcount: 0,
  gap: 2, monthlyBudgetAmount: 10000, currentMonthlySpend: null, allocated: 5, unallocated: 0, managerEmployeeId: null,
  levels, unclassifiedCount: 0, unresolvedDepartmentCount: 0, payPlan, note,
});

const CATALOG = [
  { id: 'lvl-mgr', code: 'MGR', nameEn: 'Manager', nameAr: '', rank: 1, isActive: true },
  { id: 'lvl-ana', code: 'ANA', nameEn: 'Analyst', nameAr: '', rank: 2, isActive: true },
  { id: 'lvl-dir', code: 'DIR', nameEn: 'Director', nameAr: '', rank: 0, isActive: true },
];
const ENG_PLAN = { avgPerHead: 2000, plannedMin: 8900, plannedMid: 9600, plannedMax: 10300, unallocatedBudget: 400, unpricedHeads: 0, uncontrolledLevels: 1, flags: [] as string[] };
const DEPARTMENTS = [
  // Manager 2 x 2,400 + Analyst 3 x 1,600 = 9,600 against 10,000; Director is uncontrolled and has no range.
  department('dept-eng', 'Engineering', [
    level('lvl-dir', 'Director', null, 0, null), level('lvl-mgr', 'Manager', 2, 1, band(2200, 2400, 2600)), level('lvl-ana', 'Analyst', 3, 2, band(1500, 1600, 1700), { note: 'Old note' }),
  ], ENG_PLAN, 'Hiring in Q3'),
  // Bands in a currency other than the company's arrive unpriced (pay null); the server flags it and does not compare.
  department('dept-ops', 'Operations', [
    level('lvl-dir', 'Director', null, 0, null), level('lvl-mgr', 'Manager', 2, 1, null, { payStatus: 'currencyMismatch' }), level('lvl-ana', 'Analyst', 3, 2, null, { payStatus: 'noRange' }),
  ], { avgPerHead: 2000, plannedMin: 0, plannedMid: 0, plannedMax: 0, unallocatedBudget: 10000, unpricedHeads: 5, uncontrolledLevels: 1, flags: ['notComparable', 'currencyMismatch'] }),
  // Same shape, but the caller may not see salaries.
  department('dept-fin', 'Finance', [
    level('lvl-dir', 'Director', null, 0, null, { payStatus: null }), level('lvl-mgr', 'Manager', 2, 1, null, { payStatus: null }), level('lvl-ana', 'Analyst', 3, 2, null, { payStatus: null }),
  ], null, 'Finance note is never withheld'),
];

const errorsByPage = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  errorsByPage.set(page, errors);
  page.on('pageerror', error => errors.push(error.message));
});
test.afterEach(async ({ page }) => {
  expect(errorsByPage.get(page), 'No application runtime errors').toEqual([]);
});

async function boot(page: Page, options: { readOnly?: boolean; saveFails?: string } = {}) {
  const writes: string[] = [];
  const bodies: Array<{ path: string; body: any }> = [];
  await page.addInitScript(() => {
    localStorage.setItem('zayra_access_token', 'fixture');
    localStorage.setItem('kynexone-locale-choice-v2', 'en');
    localStorage.setItem('kynexone-company:reviewer', 'fixture-company');
  });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (['PUT', 'POST', 'PATCH', 'DELETE'].includes(request.method())) {
      writes.push(`${request.method()} ${path}`);
      bodies.push({ path, body: request.postDataJSON() });
      if (options.saveFails && path.endsWith('/budgets')) return route.fulfill({ status: 400, json: { message: options.saveFails } });
    }
    const company = { id: 'fixture-company', legalNameEn: 'Workspace Fixture LLC', countryCode: 'SA', defaultCurrency: 'SAR', isActive: true, approvalStatus: 'Active' };
    if (path === '/api/auth/me') return reply({
      id: 'reviewer', tenantId: 'tenant', tenantSlug: 'fixture', fullName: 'Reviewer', roles: ['Admin'],
      permissions: options.readOnly
        ? ['organization.read', 'organization.write', 'organization.establishment.read']
        : ['organization.read', 'organization.write', 'organization.establishment.read', 'organization.establishment.write', 'dashboard.read'],
      accountType: 'SingleCompany', isGroupScope: false,
      companies: [{ id: company.id, name: company.legalNameEn, code: company.id, countryCode: 'SA', isActive: true }],
    });
    if (path === '/api/establishment/matrix') return reply({ enforcementMode: 'Enforced', unresolvedDepartmentCount: 0, departments: DEPARTMENTS });
    if (path === '/api/establishment/levels') return reply(CATALOG);
    if (path === '/api/tenant-hr-config') return reply({ establishmentEnforcementMode: 'Enforced' });
    if (path === '/api/tenant-admin/localization') return reply({ countryCode: 'SAU', currencyCode: 'SAR' });
    if (path === '/api/auth/mfa/status') return reply({ promptToEnroll: false });
    if (path === '/api/companies') return reply({ items: [company], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path.includes('/features/') || path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/setup?tab=establishment&companyId=fixture-company');
  await expect(page.getByRole('button', { name: 'Staffing levels — Engineering' })).toBeVisible({ timeout: 30_000 });
  return { writes, bodies };
}

const expand = (page: Page, dept: string) => page.getByRole('button', { name: `Staffing levels — ${dept}` }).click();

test('the level table names its last column Status, and a level with nothing to report says OK', async ({ page }) => {
  await boot(page);
  await expand(page, 'Engineering');
  await expect(page.getByRole('columnheader', { name: 'Status', exact: true })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Notes', exact: true })).toHaveCount(0);
  await expect(page.getByTestId('level-status-dept-eng-lvl-mgr')).toHaveText('OK');
  await expect(page.getByRole('columnheader', { name: 'Pay band', exact: true })).toBeVisible();
  await expect(page.getByTestId('level-pay-band-dept-eng-lvl-mgr')).toHaveText('2,200–2,600');
  await expect(page.getByTestId('level-planned-cost-dept-eng-lvl-mgr')).toHaveText('2 × 2,400 = 4,800');
  await expect(page.getByTestId('level-planned-cost-dept-eng-lvl-mgr')).toHaveAccessibleName('Planned cost 4,800 for Manager level');
  // An uncontrolled level with no range: no figures are invented.
  await expect(page.getByTestId('level-pay-band-dept-eng-lvl-dir')).toHaveText('No range set');
  await expect(page.getByTestId('level-planned-cost-dept-eng-lvl-dir')).toHaveText('—');
});

test('a budget of 10,000 over 5 approved heads is 2,000 per head, and the level plan is compared with the budget', async ({ page }) => {
  await boot(page);
  await expand(page, 'Engineering');
  const summary = page.getByRole('group', { name: 'Pay reconciliation — Engineering' });
  await expect(summary).toBeVisible();
  await expect(page.getByTestId('pay-avg-per-head-dept-eng')).toContainText('2,000');
  await expect(page.getByTestId('pay-planned-vs-budget-dept-eng')).toContainText('9,600 vs 10,000');
  await expect(page.getByTestId('chip-within-budget-dept-eng')).toBeVisible();
  await expect(page.getByTestId('chip-over-budget-dept-eng')).toHaveCount(0);
  // The Director level has no range but no budget either (uncontrolled): it adds no cost, so it raises no chip.
  await expect(page.getByTestId('chip-no-range-dept-eng')).toHaveCount(0);
  await expect(page.getByTestId('level-total-dept-eng')).toContainText('5');
});

test('typing a larger level budget recomputes the plan live and warns when it exceeds the budget; nothing is saved', async ({ page }) => {
  const { writes } = await boot(page);
  await expand(page, 'Engineering');
  await page.getByRole('spinbutton', { name: 'Budgeted Manager — Engineering' }).fill('3');
  // 3 x 2,400 + 3 x 1,600 = 12,000 against 10,000.
  await expect(page.getByTestId('pay-planned-vs-budget-dept-eng')).toContainText('12,000 vs 10,000');
  await expect(page.getByTestId('chip-over-budget-dept-eng')).toHaveText('Planned cost exceeds budget by 2,000');
  await expect(page.getByTestId('chip-within-budget-dept-eng')).toHaveCount(0);
  await expect(page.getByTestId('level-planned-cost-dept-eng-lvl-mgr')).toHaveText('3 × 2,400 = 7,200');
  // Warning only: Save stays available.
  await expect(page.getByRole('button', { name: 'Save', exact: true }).first()).toBeEnabled();
  expect(writes).toEqual([]);
  // Clearing the field returns the level to uncontrolled: no figure, never 0 or NaN.
  await page.getByRole('spinbutton', { name: 'Budgeted Manager — Engineering' }).fill('');
  await expect(page.getByTestId('level-planned-cost-dept-eng-lvl-mgr')).toHaveText('—');
  await expect(page.getByTestId('pay-summary-dept-eng')).not.toContainText('NaN');
});

test('without salary permission the pay figures say Restricted, never 0, while the budget average still shows', async ({ page }) => {
  await boot(page);
  await expand(page, 'Finance');
  await expect(page.getByTestId('pay-restricted-dept-fin')).toHaveText('Restricted');
  await expect(page.getByTestId('level-pay-band-dept-fin-lvl-mgr')).toHaveText('Restricted');
  await expect(page.getByTestId('level-planned-cost-dept-fin-lvl-mgr')).toHaveText('Restricted');
  await expect(page.getByTestId('pay-avg-per-head-dept-fin')).toContainText('2,000');
  await expect(page.getByTestId('chip-over-budget-dept-fin')).toHaveCount(0);
  await expect(page.getByTestId('chip-within-budget-dept-fin')).toHaveCount(0);
});

test('read-only viewers get the same figures without any input', async ({ page }) => {
  await boot(page, { readOnly: true });
  await expand(page, 'Engineering');
  await expect(page.getByTestId('pay-planned-vs-budget-dept-eng')).toContainText('9,600 vs 10,000');
  await expect(page.getByRole('spinbutton', { name: /Budgeted Manager/ })).toHaveCount(0);
});

test('an untouched department shows the server verdict, not a client recomputation', async ({ page }) => {
  await boot(page);
  await expand(page, 'Operations');
  await expect(page.getByTestId('pay-avg-per-head-dept-ops')).toContainText('2,000');
  await expect(page.getByTestId('pay-planned-vs-budget-dept-ops').locator('dd')).toHaveText('—');
  await expect(page.getByTestId('chip-currency-dept-ops')).toHaveText('Pay bands use mixed or different currencies - cost not compared');
  await expect(page.getByTestId('chip-no-range-dept-ops')).toHaveText('No pay range set for 1 level');
  // Budgeted levels in a currency-flagged department say why they are unpriced; the uncontrolled one just has no range.
  await expect(page.getByTestId('level-pay-band-dept-ops-lvl-mgr')).toHaveText('Currency differs');
  // The department flag no longer paints every unpriced level: an unpriced-for-range level keeps its own text.
  await expect(page.getByTestId('level-pay-band-dept-ops-lvl-ana')).toHaveText('No range set');
  await expect(page.getByTestId('level-pay-band-dept-ops-lvl-dir')).toHaveText('No range set');
  await expect(page.getByTestId('chip-within-budget-dept-ops')).toHaveCount(0);
  await expect(page.getByTestId('chip-over-budget-dept-ops')).toHaveCount(0);
  // Editing keeps the server's currency verdict: the level that was unpriced for currency stays unpriced.
  await page.getByRole('spinbutton', { name: 'Budgeted Manager — Operations' }).fill('4');
  await expect(page.getByTestId('chip-currency-dept-ops')).toBeVisible();
  await expect(page.getByTestId('pay-planned-vs-budget-dept-ops').locator('dd')).toHaveText('—');
  // A department with consistent currencies shows no currency chip.
  await expand(page, 'Engineering');
  await expect(page.getByTestId('chip-currency-dept-eng')).toHaveCount(0);
});

test('control: the write recorder does see a save, so "nothing is saved" above can fail', async ({ page }) => {
  const { writes } = await boot(page);
  await expand(page, 'Engineering');
  await page.getByRole('spinbutton', { name: 'Budgeted Manager — Engineering' }).fill('3');
  await page.getByRole('button', { name: 'Save', exact: true }).first().click();
  await page.getByPlaceholder('e.g. Approved by management for Q3 expansion').fill('Q3 plan');
  await page.getByRole('button', { name: 'Confirm save' }).click();
  await expect.poll(() => writes).toContain('PUT /api/establishment/departments/dept-eng/budgets');
});

const save = async (page: Page, reason = 'Q3 plan') => {
  await page.getByRole('button', { name: 'Save', exact: true }).first().click();
  await page.getByPlaceholder('e.g. Approved by management for Q3 expansion').fill(reason);
  await page.getByRole('button', { name: 'Confirm save' }).click();
};
const BUDGETS = '/api/establishment/departments/dept-eng/budgets';

test('a changed level note is sent alone; unchanged notes and the department note are omitted', async ({ page }) => {
  const { bodies } = await boot(page);
  await expand(page, 'Engineering');
  await expect(page.getByTestId('level-note-dept-eng-lvl-ana')).toHaveValue('Old note');
  await page.getByTestId('level-note-dept-eng-lvl-mgr').fill('  Raise at Q3  ');
  await save(page);
  await expect.poll(() => bodies.length).toBe(1);
  expect(bodies[0].path).toBe(BUDGETS);
  expect(bodies[0].body).toEqual({ rows: [{ staffingLevelId: 'lvl-mgr', budgetedHeadcount: 2, note: 'Raise at Q3' }], reason: 'Q3 plan' });
});

test('clearing a saved level note sends an empty string', async ({ page }) => {
  const { bodies } = await boot(page);
  await expand(page, 'Engineering');
  await page.getByTestId('level-note-dept-eng-lvl-ana').fill('   ');
  await save(page);
  await expect.poll(() => bodies.length).toBe(1);
  expect(bodies[0].body).toEqual({ rows: [{ staffingLevelId: 'lvl-ana', budgetedHeadcount: 3, note: '' }], reason: 'Q3 plan' });
});

test('a department note alone saves with rows: [] and the note', async ({ page }) => {
  const { bodies } = await boot(page);
  await expand(page, 'Engineering');
  await expect(page.getByTestId('dept-note-dept-eng')).toHaveValue('Hiring in Q3');
  await page.getByTestId('dept-note-dept-eng').fill('Hiring freeze');
  await save(page);
  await expect.poll(() => bodies.length).toBe(1);
  expect(bodies[0].body).toEqual({ rows: [], reason: 'Q3 plan', note: 'Hiring freeze' });
});

test('retyping the saved note is not a change', async ({ page }) => {
  await boot(page);
  await expand(page, 'Engineering');
  await page.getByTestId('dept-note-dept-eng').fill('Hiring in Q3  ');
  await expect(page.getByRole('button', { name: 'Save', exact: true }).first()).toBeDisabled();
});

test('the note field stops at 500 characters and counts as you type', async ({ page }) => {
  await boot(page);
  await expand(page, 'Engineering');
  const input = page.getByTestId('level-note-dept-eng-lvl-mgr');
  await expect(input).toHaveAttribute('maxlength', '500');
  await expect(page.getByTestId('level-note-count-dept-eng-lvl-mgr')).toHaveText('0/500');
  await input.fill('x'.repeat(600));
  await expect(input).toHaveValue('x'.repeat(500));
  await expect(page.getByTestId('level-note-count-dept-eng-lvl-mgr')).toHaveText('500/500');
  await expect(page.getByTestId('dept-note-count-dept-eng')).toHaveText('12/500');
});

test('a level with no budgeted headcount cannot take a note, and clearing the headcount disables it', async ({ page }) => {
  await boot(page);
  await expand(page, 'Engineering');
  const director = page.getByTestId('level-note-dept-eng-lvl-dir');
  await expect(director).toBeDisabled();
  await expect(director).toHaveAttribute('title', /Set a budgeted headcount/);
  const manager = page.getByTestId('level-note-dept-eng-lvl-mgr');
  await expect(manager).toBeEnabled();
  await page.getByRole('spinbutton', { name: 'Budgeted Manager — Engineering' }).fill('');
  await expect(manager).toBeDisabled();
  await page.getByRole('spinbutton', { name: 'Budgeted Manager — Engineering' }).fill('0');
  await expect(manager).toBeEnabled();
});

test('read-only users see the saved notes as text, with no inputs', async ({ page }) => {
  await boot(page, { readOnly: true });
  await expand(page, 'Engineering');
  await expect(page.getByTestId('level-note-dept-eng-lvl-ana')).toHaveText('Old note');
  await expect(page.getByTestId('dept-note-dept-eng')).toHaveText('Hiring in Q3');
  // The Setup page has its own settings-search box, so only note fields are counted.
  await expect(page.getByRole('textbox', { name: /note/i })).toHaveCount(0);
});

test('a note is shown to a viewer without salary permission', async ({ page }) => {
  await boot(page);
  await expand(page, 'Finance');
  await expect(page.getByTestId('dept-note-dept-fin')).toHaveValue('Finance note is never withheld');
});

test('the server refusal is shown in the existing error pattern', async ({ page }) => {
  await boot(page, { saveFails: 'A note can be at most 500 characters.' });
  await expand(page, 'Engineering');
  await page.getByTestId('dept-note-dept-eng').fill('too long on the server');
  await save(page);
  await expect(page.getByText('A note can be at most 500 characters.')).toBeVisible();
});
