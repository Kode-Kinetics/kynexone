import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type Request, type Route, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { bootEmployeeWorkspaceFixture, employees as fixtureEmployees } from './employee-workspace.fixture';

/**
 * Cost centre on transfer (Wave A, D2/D4): Edit employee → Employment shows the RESOLVED cost centre in a dropdown with where it
 * comes from, asks for a reason for any change, shows the server's answer live when the department changes (preview endpoint),
 * and asks keep-or-follow when an override meets a department change. Every API request is intercepted (the frozen contract);
 * nothing touches a real tenant. Deterministic: waitForRequest / waitForResponse, no fixed sleeps.
 *   npx next dev -p 5197 &
 *   npx playwright test -c e2e/playwright.cost-centre-employee.config.ts e2e/employee-edit-cost-centre.spec.ts
 * CC_SHOTS_DIR, when set, also receives the review screenshots.
 */

const CO = 'fixture-company';
const costCentres = [
  { id: 'cc-admin', companyId: CO, code: 'ADMIN', name: 'Administration', nameAr: 'الإدارة', isActive: true },
  { id: 'cc-sales', companyId: CO, code: 'SALES', name: 'Sales', nameAr: 'المبيعات', isActive: true },
  { id: 'cc-hr', companyId: CO, code: 'CC-HR', name: 'Human Resources', nameAr: 'الموارد البشرية', isActive: true },
  { id: 'cc-old', companyId: CO, code: 'OLD-OPS', name: 'Old operations', isActive: false },
  { id: 'cc-elsewhere', companyId: 'other-company', code: 'AE-OPS', name: 'UAE operations', isActive: true },
];
const departments = [
  { id: 'qa-finance', companyId: CO, costCenterId: 'cc-admin', code: 'QA-FIN', nameEn: 'QA Finance', nameAr: 'المالية', isActive: true },
  { id: 'qa-hr', companyId: CO, costCenterId: 'cc-hr', code: 'QA-HR', nameEn: 'Human Resources', nameAr: 'الموارد البشرية', isActive: true },
  { id: 'qa-empty', companyId: CO, code: 'QA-EMPTY', nameEn: 'QA Empty', nameAr: '', isActive: true },
  { id: 'qa-foreign', companyId: 'other-company', costCenterId: 'cc-elsewhere', code: 'AE-DEPT', nameEn: 'UAE Department', nameAr: '', isActive: true },
];

type Dto = { costCentreId: string | null; code: string | null; nameEn: string | null; nameAr: string | null; source: string; sourceName: string | null; sourceNameAr: string | null; level: string | null; isOverride: boolean; invalidReason: string | null };
const inherited = (ccId: string, deptId: string): Dto => {
  const cc = costCentres.find((c) => c.id === ccId)!;
  const dept = departments.find((d) => d.id === deptId)!;
  return { costCentreId: cc.id, code: cc.code, nameEn: cc.name, nameAr: cc.nameAr ?? null, source: 'Department', sourceName: dept.nameEn, sourceNameAr: dept.nameAr || null, level: 'department', isOverride: false, invalidReason: null };
};
const NONE: Dto = { costCentreId: null, code: null, nameEn: null, nameAr: null, source: 'None', sourceName: null, sourceNameAr: null, level: null, isOverride: false, invalidReason: null };
const PREVIEW: Record<string, Dto> = { 'qa-finance': inherited('cc-admin', 'qa-finance'), 'qa-hr': inherited('cc-hr', 'qa-hr'), 'qa-empty': NONE };

interface Options {
  locale?: 'en' | 'ar';
  manage?: boolean;
  /** The person has an override (SALES) in QA Finance (which would give ADMIN). */
  override?: boolean;
  /** Nothing resolves anywhere: no department cost centre, no default. */
  nothing?: boolean;
  /** PUT answers this once. */
  refusal?: { status: number; body: unknown };
  /** Holds the preview for this department until released. */
  holdPreviewFor?: string;
}

async function boot(page: Page, options: Options = {}) {
  const manage = options.manage ?? true;
  await bootEmployeeWorkspaceFixture(page, {
    locale: options.locale,
    permissions: ['employees.read', 'employees.write', 'employees.sensitive', 'dashboard.read', 'organization.read', ...(manage ? ['organization.costcenter.manage'] : [])],
  });
  const state = {
    refusal: options.refusal ?? null as null | { status: number; body: unknown },
    writes: [] as Array<{ body: any }>,
    previews: [] as string[],
    held: null as null | { route: Route; release: () => Promise<void> },
  };
  const departmentId = options.nothing ? 'qa-empty' : 'qa-finance';
  const resolution = options.override
    ? { costCenterId: 'cc-sales', code: 'SALES', name: 'Sales', nameAr: 'المبيعات', source: 'Employee', level: 'employee', unresolvedReason: null }
    : options.nothing
      ? { costCenterId: null, code: null, name: null, nameAr: null, source: 'None', level: null, unresolvedReason: 'none' }
      : { costCenterId: 'cc-admin', code: 'ADMIN', name: 'Administration', nameAr: 'الإدارة', source: 'Department', level: 'department', unresolvedReason: null };
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const p = url.pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (p === '/api/organization/cost-centers' || p === '/api/cost-centers') return reply(paged(costCentres));
    if (p === '/api/departments') return reply(paged(departments));
    if (p === '/api/positions') return reply([]);
    if (p === '/api/employees/901/cost-centre/preview') {
      state.previews.push(url.search);
      const dept = url.searchParams.get('departmentId') ?? departmentId;
      const body = PREVIEW[dept];
      if (!body) return reply({ error: 'department_not_found', message: 'Department not found.' }, 404);
      if (options.holdPreviewFor === dept && !state.held) {
        await new Promise<void>((resolve) => { state.held = { route, release: async () => { await route.fulfill({ json: body }).catch(() => undefined); resolve(); } }; });
        return;
      }
      return reply(body);
    }
    if (p === '/api/employees/901') {
      if (request.method() === 'PUT') {
        state.writes.push({ body: request.postDataJSON() });
        if (state.refusal) { const r = state.refusal; state.refusal = null; return reply(r.body, r.status); }
        return reply({ updated: true });
      }
      return reply({
        ...fixtureEmployees[901], departmentId, department: options.nothing ? 'QA Empty' : 'QA Finance',
        costCenterId: options.override ? 'cc-sales' : options.nothing ? null : 'cc-admin',
        costCenter: 'STALE-CACHE', // D3: the cached string must never be shown
        costCenterIsOverride: !!options.override, costCentreResolution: resolution,
      });
    }
    return route.fallback();
  });
  return { state, errors };
}

const tid = (page: Page, id: string) => page.getByTestId(id);
const isPreview = (departmentId?: string) => (r: Request) => r.url().includes('/api/employees/901/cost-centre/preview') && (departmentId === undefined ? !r.url().includes('departmentId=') : r.url().includes(`departmentId=${departmentId}`));

async function open(page: Page) {
  const firstPreview = page.waitForResponse((r) => isPreview()(r.request()));
  await page.goto('/people/901/edit?section=employment');
  await expect(page.getByRole('heading', { name: /Review employment details|راجع بيانات التوظيف/ })).toBeVisible();
  await firstPreview;
}

async function changeDepartment(page: Page, label: string, departmentId: string) {
  const preview = page.waitForResponse((r) => isPreview(departmentId)(r.request()));
  await page.getByRole('combobox', { name: /^(Department|القسم)$/ }).selectOption({ label });
  return preview;
}

async function saveFromReview(page: Page) {
  await page.getByRole('button', { name: /^Step 6: Review/ }).click();
  const put = page.waitForRequest((r) => r.url().endsWith('/api/employees/901') && r.method() === 'PUT');
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  return (await put).postDataJSON();
}

async function shot(page: Page, info: TestInfo, name: string) {
  await tid(page, 'cc-edit-field').evaluate((el) => el.scrollIntoView({ block: 'center' }));
  const file = info.outputPath(`${name}.png`);
  await page.screenshot({ path: file, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path: file, contentType: 'image/png' });
  const dir = process.env.CC_SHOTS_DIR;
  if (dir) { fs.mkdirSync(dir, { recursive: true }); fs.copyFileSync(file, path.join(dir, `${name}.png`)); }
}

// ═════════════════════════════ The client's screenshot (D4) ═════════════════════════════

test('the cost centre is a dropdown pre-selected to the resolved value, with where it comes from (never a blank box)', async ({ page }, info) => {
  const { errors } = await boot(page);
  await open(page);
  const select = page.getByRole('combobox', { name: 'Cost center', exact: true });
  await expect(select).toBeEnabled();
  await expect(select.locator('option:checked')).toHaveText('Follow department (ADMIN — Administration)');
  await expect(tid(page, 'cc-edit-source')).toHaveText('Inherited from QA Finance');
  await expect(page.getByText('Managed on the Employment tab of the profile, with a reason.')).toHaveCount(0);
  await expect(page.getByText('STALE-CACHE')).toHaveCount(0);
  const options = await select.locator('option').allTextContents();
  expect(options).toEqual(['Follow department (ADMIN — Administration)', 'SALES — Sales', 'CC-HR — Human Resources']);
  // Only the employee's company's departments are offered.
  const departmentOptions = await page.getByRole('combobox', { name: 'Department', exact: true }).locator('option').allTextContents();
  expect(departmentOptions).not.toContain('UAE Department');
  await expect(tid(page, 'cc-edit-reason')).toHaveCount(0);
  await shot(page, info, 'cc-edit-en-inherited');
  expect(errors).toEqual([]);
});

test('nothing resolves: the line says so and links to Setup → Company structure', async ({ page }) => {
  await boot(page, { nothing: true });
  await open(page);
  const link = tid(page, 'cc-edit-source').getByRole('link', { name: 'No cost centre: add one in Setup → Company structure' });
  await expect(link).toHaveAttribute('href', '/setup?tab=costCenters');
  await expect(page.getByRole('combobox', { name: 'Cost center', exact: true }).locator('option:checked')).toHaveText('Follow department (no cost centre)');
});

test('an override shows as the selected cost centre with the override line', async ({ page }) => {
  await boot(page, { override: true });
  await open(page);
  const select = page.getByRole('combobox', { name: 'Cost center', exact: true });
  await expect(select).toHaveValue('cc-sales');
  await expect(tid(page, 'cc-edit-source')).toHaveText('Override: set for this employee');
  // The follow option names what removing the override returns to (the no-parameter preview).
  await expect(select.locator('option').first()).toHaveText('Follow department (ADMIN — Administration)');
});

// ═════════════════════════════ Choosing in the dropdown ═════════════════════════════

test('a different cost centre asks for a reason inline; the PUT carries the code and the reason', async ({ page }) => {
  const { state } = await boot(page);
  await open(page);
  await page.getByRole('combobox', { name: 'Cost center', exact: true }).selectOption('cc-sales');
  const reason = page.getByRole('textbox', { name: /^Reason/ });
  await expect(reason).toBeVisible();
  // Leaving the step without the reason is refused on the step that owns the field, with focus on the reason.
  await page.getByRole('button', { name: /^Next: / }).click();
  await expect(tid(page, 'cc-edit-error')).toHaveText('Enter a reason for this change.');
  await expect(reason).toBeFocused();
  await reason.fill('  Seconded to sales ');
  const body = await saveFromReview(page);
  expect(body.changes).toEqual({ costCenter: 'SALES' });
  expect(body.costCenterReason).toBe('Seconded to sales');
  expect(body).not.toHaveProperty('costCenterDecision');
  expect(state.writes).toHaveLength(1);
  await expect(page).toHaveURL(/\/people\/901\?section=employment/);
});

test('the review shows the cost centre change with its reason', async ({ page }) => {
  await boot(page);
  await open(page);
  await page.getByRole('combobox', { name: 'Cost center', exact: true }).selectOption('cc-hr');
  await page.getByRole('textbox', { name: /^Reason/ }).fill('Project');
  await page.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(page.locator('[data-employee-step="5"]')).toContainText('From ADMIN to CC-HR. Reason: Project');
});

test('following the department again while overridden removes the override: an empty code and the reason', async ({ page }) => {
  await boot(page, { override: true });
  await open(page);
  await page.getByRole('combobox', { name: 'Cost center', exact: true }).selectOption('');
  await page.getByRole('textbox', { name: /^Reason/ }).fill('Back to the department');
  const body = await saveFromReview(page);
  expect(body.changes).toEqual({ costCenter: '' });
  expect(body.costCenterReason).toBe('Back to the department');
});

// ═════════════════════════════ The department changes (D2) ═════════════════════════════

test('a department change asks the server and shows the new resolved cost centre live; the PUT carries only the department', async ({ page }, info) => {
  const { state } = await boot(page);
  await open(page);
  const response = await changeDepartment(page, 'Human Resources', 'qa-hr');
  expect(new URL(response.url()).searchParams.get('departmentId')).toBe('qa-hr');
  const line = tid(page, 'cc-edit-preview');
  await expect(line).toHaveAttribute('aria-live', 'polite');
  await expect(line).toHaveText('Will follow Human Resources → CC-HR');
  await expect(page.getByRole('combobox', { name: 'Cost center', exact: true }).locator('option:checked')).toHaveText('Follow department (CC-HR — Human Resources)');
  await expect(tid(page, 'cc-edit-decision')).toHaveCount(0);
  await shot(page, info, 'cc-edit-en-department-change');
  const body = await saveFromReview(page);
  expect(body.changes).toMatchObject({ department: 'Human Resources' });
  expect(Object.keys(body.changes)).not.toContain('costCenter');
  expect(body).not.toHaveProperty('costCenterReason');
  expect(body).not.toHaveProperty('costCenterDecision');
  expect(state.previews.every((q) => !q.includes('costCenterId'))).toBe(true);
});

test('a department with nothing to inherit says so live', async ({ page }) => {
  await boot(page);
  await open(page);
  await changeDepartment(page, 'QA Empty', 'qa-empty');
  await expect(tid(page, 'cc-edit-preview')).toHaveText('Will follow QA Empty: it has no cost centre yet.');
});

test('an older preview answer that arrives late never replaces the newer one', async ({ page }) => {
  const { state } = await boot(page, { holdPreviewFor: 'qa-hr' });
  await open(page);
  const held = page.waitForRequest(isPreview('qa-hr'));
  await page.getByRole('combobox', { name: 'Department', exact: true }).selectOption({ label: 'Human Resources' });
  await held;
  await changeDepartment(page, 'QA Empty', 'qa-empty');
  await expect(tid(page, 'cc-edit-preview')).toHaveText('Will follow QA Empty: it has no cost centre yet.');
  await state.held!.release();
  await expect(tid(page, 'cc-edit-preview')).toHaveText('Will follow QA Empty: it has no cost centre yet.');
});

test('override + department change: keep-or-follow is required with a reason; the PUT carries the decision, the reason and the override id', async ({ page }, info) => {
  const { state } = await boot(page, { override: true });
  await open(page);
  await changeDepartment(page, 'Human Resources', 'qa-hr');
  const decision = page.getByRole('radiogroup', { name: 'This employee has a cost centre override. What happens to it after the move?' });
  await expect(decision).toBeVisible();
  await expect(decision.getByRole('radio', { name: 'Keep SALES (override)' })).not.toBeChecked();
  await expect(decision.getByRole('radio', { name: 'Follow Human Resources → CC-HR' })).not.toBeChecked();
  // Leaving Employment without answering is refused before any request, with focus on the question.
  await page.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(page.getByRole('heading', { name: 'Review employment details' })).toBeVisible();
  await expect(tid(page, 'cc-edit-error')).toHaveText('Choose whether to keep the override or follow the new department.');
  await expect(tid(page, 'cc-edit-decision')).toBeFocused();
  expect(state.writes).toHaveLength(0);
  await decision.getByRole('radio', { name: 'Keep SALES (override)' }).check();
  await page.getByRole('textbox', { name: /^Reason/ }).fill('Still charged to sales');
  await shot(page, info, 'cc-edit-en-keep-or-follow');
  const body = await saveFromReview(page);
  expect(body.changes).toMatchObject({ department: 'Human Resources' });
  expect(Object.keys(body.changes)).not.toContain('costCenter');
  expect(body).toMatchObject({ costCenterDecision: 'keep', costCenterReason: 'Still charged to sales', expectedCostCenterOverrideId: 'cc-sales' });
});

test('override + department change: follow sends drop', async ({ page }) => {
  await boot(page, { override: true });
  await open(page);
  await changeDepartment(page, 'Human Resources', 'qa-hr');
  await page.getByRole('radio', { name: 'Follow Human Resources → CC-HR' }).check();
  await page.getByRole('textbox', { name: /^Reason/ }).fill('Moves with the team');
  await page.getByRole('button', { name: /^Step 6: Review/ }).click();
  await expect(page.locator('[data-employee-step="5"]')).toContainText('Follows Human Resources: CC-HR. Reason: Moves with the team');
  const put = page.waitForRequest((r) => r.url().endsWith('/api/employees/901') && r.method() === 'PUT');
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  expect((await put).postDataJSON()).toMatchObject({ costCenterDecision: 'drop', costCenterReason: 'Moves with the team', expectedCostCenterOverrideId: 'cc-sales' });
});

// ═════════════════════════════ Refusals ═════════════════════════════

test('422 cost_centre_decision_required jumps back to Employment and focuses the question', async ({ page }) => {
  await boot(page, { override: true, refusal: { status: 422, body: { error: 'cost_centre_decision_required', message: 'Choose keep or drop.', messageAr: 'اختر.' } } });
  await open(page);
  await changeDepartment(page, 'Human Resources', 'qa-hr');
  await page.getByRole('radio', { name: 'Keep SALES (override)' }).check();
  await page.getByRole('textbox', { name: /^Reason/ }).fill('r');
  await saveFromReview(page);
  await expect(page.getByRole('heading', { name: 'Review employment details' })).toBeVisible();
  await expect(tid(page, 'cc-edit-error')).toHaveText('Choose whether to keep the override or follow the new department.');
  await expect(tid(page, 'cc-edit-decision')).toBeFocused();
});

test('422 cost_centre_inactive is shown in words on Employment with focus on the dropdown', async ({ page }) => {
  await boot(page, { refusal: { status: 422, body: { error: 'cost_centre_inactive', message: 'x' } } });
  await open(page);
  await page.getByRole('combobox', { name: 'Cost center', exact: true }).selectOption('cc-sales');
  await page.getByRole('textbox', { name: /^Reason/ }).fill('r');
  await saveFromReview(page);
  await expect(tid(page, 'cc-edit-error')).toHaveText('This cost centre is inactive. Choose an active one.');
  await expect(page.getByRole('combobox', { name: 'Cost center', exact: true })).toBeFocused();
});

test('409 cost_centre_state_changed says someone else changed it', async ({ page }) => {
  await boot(page, { override: true, refusal: { status: 409, body: { error: 'cost_centre_state_changed', message: 'x' } } });
  await open(page);
  await changeDepartment(page, 'Human Resources', 'qa-hr');
  await page.getByRole('radio', { name: 'Keep SALES (override)' }).check();
  await page.getByRole('textbox', { name: /^Reason/ }).fill('r');
  await saveFromReview(page);
  await expect(tid(page, 'cc-edit-error')).toHaveText('Someone changed this employee’s cost centre since you opened the form. Reload the employee and try again.');
});

// ═════════════════════════════ Without organization.costcenter.manage ═════════════════════════════

test('without the permission: the resolved value read-only with who can change it, and the department locked with a note', async ({ page }, info) => {
  const { state } = await boot(page, { manage: false });
  await open(page);
  await expect(page.getByRole('combobox', { name: 'Cost center', exact: true })).toHaveCount(0);
  await expect(tid(page, 'cc-edit-value')).toHaveText('ADMIN — Administration');
  await expect(tid(page, 'cc-edit-source')).toHaveText('Inherited from QA Finance');
  await expect(tid(page, 'cc-edit-readonly-note')).toHaveText('Only HR managers and admins can change this.');
  await expect(page.getByRole('combobox', { name: 'Department', exact: true })).toBeDisabled();
  await expect(tid(page, 'cc-edit-department-note')).toHaveText('Changing the department moves the cost centre, so only HR managers and admins can change it.');
  // Only the plain (employees.read) preview is asked: no hypothetical parameters.
  expect(state.previews.every((q) => q === '')).toBe(true);
  await shot(page, info, 'cc-edit-en-readonly');
});

// ═════════════════════════════ Arabic ═════════════════════════════

test('Arabic: RTL labels with the Arabic names, and the arrow points in reading order', async ({ page }, info) => {
  await boot(page, { locale: 'ar', override: true });
  await open(page);
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const select = page.getByRole('combobox', { name: 'مركز التكلفة', exact: true });
  await expect(select.locator('option:checked')).toHaveText('SALES — المبيعات');
  await expect(tid(page, 'cc-edit-source')).toHaveText('استثناء: محدد لهذا الموظف');
  await changeDepartment(page, 'Human Resources', 'qa-hr');
  await expect(tid(page, 'cc-edit-preview')).toHaveText('سيتبع الموارد البشرية ← CC-HR');
  // Each value is its own isolated run, so the Latin code keeps its order inside the Arabic sentence.
  await expect(tid(page, 'cc-edit-preview').locator('bdi')).toHaveText(['الموارد البشرية', 'CC-HR']);
  await expect(page.getByRole('radio', { name: 'الإبقاء على SALES (استثناء)' })).toBeVisible();
  await expect(page.getByRole('radio', { name: 'اتباع الموارد البشرية ← CC-HR' })).toBeVisible();
  await shot(page, info, 'cc-edit-ar-keep-or-follow');
});

test('axe: the cost-centre block with the keep-or-follow question has no serious or critical violations', async ({ page }) => {
  await boot(page, { override: true });
  await open(page);
  await changeDepartment(page, 'Human Resources', 'qa-hr');
  await expect(tid(page, 'cc-edit-decision')).toBeVisible();
  const result = await new AxeBuilder({ page }).include('[data-testid="cc-edit-field"]').withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  expect(result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id)).toEqual([]);
});
