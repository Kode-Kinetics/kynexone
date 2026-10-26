import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { bootEmployeeWorkspaceFixture, employees as fixtureEmployees } from './employee-workspace.fixture';

/**
 * WP-F phase 2, screen 6 and AM-30: the employee's cost-centre field on Employment, the Override and Remove-override
 * dialogs (reason required), read-only without organization.costcenter.manage, the 409/422 refusals in both languages,
 * and the existing employee forms that must stop sending a cost centre they do not mean to change.
 *   npx next dev -p 5197 &
 *   npx playwright test -c e2e/playwright.cost-centre-employee.config.ts
 * CC_SHOTS_DIR, when set, also receives the review screenshots.
 */

const M7_EN = 'A payroll run for this company is awaiting finance review or lock; reopen it first.';
const M7_AR = 'هناك مسيرة رواتب لهذه الشركة بانتظار المراجعة المالية أو القفل؛ أعد فتحها أولًا.';
const REASON_EN = 'Enter a reason for this change.';
const REASON_AR = 'أدخل سببًا لهذا التغيير.';

const CO = 'fixture-company';
const costCentres = [
  { id: 'cc-admin', companyId: CO, code: 'ADMIN', name: 'Administration', nameAr: 'الإدارة', isActive: true },
  { id: 'cc-sales', companyId: CO, code: 'SALES', name: 'Sales', nameAr: 'المبيعات', isActive: true },
  { id: 'cc-dflt', companyId: CO, code: 'POOL', name: 'Company pool', isActive: true },
  { id: 'cc-old', companyId: CO, code: 'OLD-OPS', name: 'Old operations', isActive: false },
  { id: 'cc-elsewhere', companyId: 'other-company', code: 'AE-OPS', name: 'UAE operations', isActive: true },
  { id: 'cc-group', code: 'GROUP-POOL', name: 'Group pool', isActive: true },
];

interface Options {
  locale?: 'en' | 'ar';
  /** organization.costcenter.manage (Admin, HR Manager). */
  manage?: boolean;
  /** The person already has an override (SALES). */
  override?: boolean;
  /** The department has no cost centre of its own; its parent has. */
  parentSupplies?: boolean;
  /** The department's cost centre is inactive. */
  invalidInherited?: boolean;
  /** finance.gl.read: readiness (the company default and the costing switch) is readable. */
  finance?: boolean;
  costingOn?: boolean;
  /** PUT /api/employees/901 answers this once. */
  refusal?: { status: number; body: unknown };
}

async function boot(page: Page, options: Options = {}) {
  const manage = options.manage ?? true;
  const finance = options.finance ?? true;
  const fx = await bootEmployeeWorkspaceFixture(page, {
    locale: options.locale,
    permissions: ['employees.read', 'employees.write', 'employees.sensitive', 'dashboard.read', 'organization.read',
      ...(manage ? ['organization.costcenter.manage'] : []), ...(finance ? ['finance.gl.read'] : [])],
  });
  const state = {
    costCenterId: options.override ? 'cc-sales' : options.invalidInherited ? 'cc-old' : 'cc-admin',
    costCenter: options.override ? 'SALES' : options.invalidInherited ? 'OLD-OPS' : 'ADMIN',
    refusal: options.refusal ?? null as null | { status: number; body: unknown },
    writes: [] as Array<{ method: string; path: string; body: any }>,
    departments: [
      { id: 'qa-finance', branchId: 'jeddah-office', parentDepartmentId: options.parentSupplies ? 'qa-group' : undefined, costCenterId: options.parentSupplies ? undefined : options.invalidInherited ? 'cc-old' : 'cc-admin', code: 'QA-FIN', nameEn: 'QA Finance', nameAr: 'المالية', isActive: true, companyId: CO },
      { id: 'qa-group', branchId: 'jeddah-office', costCenterId: 'cc-admin', code: 'QA-GRP', nameEn: 'QA Group Services', nameAr: '', isActive: true, companyId: CO },
      { id: 'qa-operations', branchId: 'riyadh-office', costCenterId: 'cc-sales', code: 'QA-OPS', nameEn: 'QA Operations', nameAr: '', managerEmployeeId: 902, isActive: true, companyId: CO },
    ],
  };
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const p = new URL(request.url()).pathname;
    const method = request.method();
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (p === '/api/organization/cost-centers' || p === '/api/cost-centers') return reply(paged(costCentres));
    if (p === '/api/departments') return reply(paged(state.departments));
    if (p === '/api/positions') return reply([]);
    if (p === '/api/finance/gl/cost-accounting/readiness') {
      if (!finance) return reply({ message: 'Forbidden' }, 403);
      return reply({ companyId: CO, settings: { enabled: !!options.costingOn, defaultCostCenterId: 'cc-dflt', smallCellMin: 0, awaitingRun: false }, checks: [], smallCellExposure: { count: 0, threshold: 3 } });
    }
    if (p === '/api/employees/901') {
      if (method === 'PUT') {
        const body = request.postDataJSON();
        state.writes.push({ method, path: p, body });
        if (state.refusal) { const r = state.refusal; state.refusal = null; return reply(r.body, r.status); }
        const code = body?.changes?.costCenter;
        if (typeof code === 'string') {
          const hit = costCentres.find((c) => c.code === code);
          if (code === '') { state.costCenterId = 'cc-admin'; state.costCenter = 'ADMIN'; } else if (hit) { state.costCenterId = hit.id; state.costCenter = hit.code; }
        }
        return reply({ updated: true });
      }
      if (method === 'GET') {
        return reply({ ...fixtureEmployees[901], costCenterId: state.costCenterId, costCenter: state.costCenter });
      }
    }
    return route.fallback();
  });
  return { fx, state, errors };
}

async function shot(page: Page, info: TestInfo, name: string) {
  const file = info.outputPath(`${name}.png`);
  await page.screenshot({ path: file, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path: file, contentType: 'image/png' });
  const dir = process.env.CC_SHOTS_DIR;
  if (dir) { fs.mkdirSync(dir, { recursive: true }); fs.copyFileSync(file, path.join(dir, `${name}.png`)); }
}

const tid = (page: Page, id: string) => page.getByTestId(id);
const open = async (page: Page) => {
  await page.goto('/people/901?section=employment');
  await expect(page.getByRole('heading', { name: /Employment details|تفاصيل التوظيف/ })).toBeVisible();
};
const axeSeriousOrWorse = async (page: Page, scope: string) => {
  const result = await new AxeBuilder({ page }).include(scope).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  return result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.nodes.map((n) => n.html.slice(0, 100)).join(' | ')}`);
};

// ═════════════════════════════ Screen 6: the field and its source ═════════════════════════════

test('an inherited cost centre shows its code and name, a text chip naming the department, and an Override button', async ({ page }, info) => {
  const { errors } = await boot(page);
  await open(page);
  const field = tid(page, 'cc-emp-field');
  await expect(field.getByText('Cost centre', { exact: true })).toBeVisible();
  await expect(tid(page, 'cc-emp-value')).toHaveText('ADMIN — Administration');
  await expect(tid(page, 'cc-emp-source')).toHaveText('From department: QA Finance');
  await expect(tid(page, 'cc-emp-override-open')).toHaveText(/Override…/);
  await expect(tid(page, 'cc-emp-override-remove')).toHaveCount(0);
  await expect(tid(page, 'cc-emp-readonly-note')).toHaveCount(0);
  await shot(page, info, 'cc-en-employee-inherited');
  expect(errors).toEqual([]);
});

test('the chip names the parent department when that is where the cost centre comes from', async ({ page }) => {
  await boot(page, { parentSupplies: true });
  await open(page);
  await expect(tid(page, 'cc-emp-source')).toHaveText('From parent department: QA Group Services');
  await expect(tid(page, 'cc-emp-value')).toHaveText('ADMIN — Administration');
});

test('"Why this cost centre?" opens the order with the winning step marked in text', async ({ page }) => {
  await boot(page);
  await open(page);
  const why = tid(page, 'cc-emp-why');
  await expect(why).toHaveAttribute('aria-expanded', 'false');
  await why.click();
  await expect(why).toHaveAttribute('aria-expanded', 'true');
  const order = tid(page, 'cc-emp-why-order');
  await expect(order).toContainText('Order: employee override, position, department, parent department, company default.');
  await expect(order.getByRole('listitem')).toHaveCount(5);
  await expect(order.getByRole('listitem').nth(2)).toContainText('(applies)');
  await expect(order.getByText('(applies)')).toHaveCount(1);
});

test('an override is a different chip, offers Change and Remove, and says where removing it returns to', async ({ page }) => {
  await boot(page, { override: true });
  await open(page);
  await expect(tid(page, 'cc-emp-value')).toHaveText('SALES — Sales');
  await expect(tid(page, 'cc-emp-source')).toHaveText('Set for this employee (override)');
  await expect(tid(page, 'cc-emp-override-open')).toHaveText(/Change…/);
  await expect(tid(page, 'cc-emp-override-remove')).toHaveText(/Remove override/);
  await tid(page, 'cc-emp-override-remove').click();
  const dialog = page.getByRole('dialog', { name: 'Remove the override?' });
  await expect(dialog).toContainText('Removing it returns to: ADMIN, from department QA Finance.');
});

test('an inactive inherited cost centre is a rose chip naming the level and code, never a silent blank', async ({ page }) => {
  await boot(page, { invalidInherited: true });
  await open(page);
  await expect(tid(page, 'cc-emp-invalid-chip')).toContainText('The department cost centre OLD-OPS is inactive, deleted or in another company.');
});

test('with nothing anywhere: the company default when there is one', async ({ page }) => {
  await boot(page, { parentSupplies: true });
  await page.route('**/api/departments', (route) => route.fulfill({ json: { items: [
    { id: 'qa-finance', code: 'QA-FIN', nameEn: 'QA Finance', nameAr: '', isActive: true, companyId: CO },
  ], total: 1, page: 1, pageSize: 100 } }));
  await open(page);
  await expect(tid(page, 'cc-emp-source')).toHaveText('Company default');
  await expect(tid(page, 'cc-emp-value')).toHaveText('POOL — Company pool');
});

test('costing off for the company: the source is still shown, with the note that payroll is not affected yet', async ({ page }) => {
  await boot(page, { costingOn: false });
  await open(page);
  await expect(tid(page, 'cc-emp-costing-off-note')).toHaveText('Cost accounting is off, so this does not affect payroll yet.');
  await expect(tid(page, 'cc-emp-source')).toBeVisible();
});

test('costing on: no "off" note', async ({ page }) => {
  await boot(page, { costingOn: true });
  await open(page);
  await expect(tid(page, 'cc-emp-source')).toBeVisible();
  await expect(tid(page, 'cc-emp-costing-off-note')).toHaveCount(0);
});

// ═════════════════════════════ Override and Remove ═════════════════════════════

test('Override: only this company\'s active cost centres are offered, a reason is required, and the request carries the code and the reason', async ({ page }, info) => {
  const { state, errors } = await boot(page);
  await open(page);
  await tid(page, 'cc-emp-override-open').click();
  const dialog = page.getByRole('dialog', { name: 'Override cost centre' });
  await expect(dialog).toContainText('Replaces the department\'s cost centre: ADMIN. Applies from the next Lock; locked runs do not change.');
  const select = tid(page, 'cc-emp-override-select');
  const options = await select.locator('option').allTextContents();
  expect(options.join('|')).toContain('SALES — Sales');
  expect(options.join('|')).not.toContain('OLD-OPS');
  expect(options.join('|')).not.toContain('AE-OPS');
  expect(options.join('|')).not.toContain('GROUP-POOL');
  await shot(page, info, 'cc-en-employee-override-dialog');

  await tid(page, 'cc-emp-override-save').click();
  await expect(dialog).toContainText('Choose a cost centre.');
  await select.selectOption('cc-sales');
  await tid(page, 'cc-emp-override-save').click();
  await expect(dialog).toContainText(REASON_EN);
  expect(state.writes).toHaveLength(0);

  await tid(page, 'cc-emp-override-reason').fill('  Seconded to the sales team ');
  await tid(page, 'cc-emp-override-save').click();
  await expect(dialog).toHaveCount(0);
  expect(state.writes).toHaveLength(1);
  expect(state.writes[0].body).toMatchObject({ changes: { costCenter: 'SALES' }, costCenterReason: 'Seconded to the sales team' });
  expect(Object.keys(state.writes[0].body.changes)).toEqual(['costCenter']);
  await expect(page.getByRole('status').filter({ hasText: 'Override saved.' })).toBeVisible();
  await expect(tid(page, 'cc-emp-source')).toHaveText('Set for this employee (override)');
  await expect(tid(page, 'cc-emp-value')).toHaveText('SALES — Sales');
  await expect(tid(page, 'cc-emp-override-open')).toBeFocused();
  expect(errors).toEqual([]);
});

test('Remove override: a reason is required, and the request sends an empty code with the reason', async ({ page }) => {
  const { state } = await boot(page, { override: true });
  await open(page);
  await tid(page, 'cc-emp-override-remove').click();
  const dialog = page.getByRole('dialog', { name: 'Remove the override?' });
  await dialog.getByRole('button', { name: 'Remove override', exact: true }).click();
  await expect(dialog).toContainText(REASON_EN);
  expect(state.writes).toHaveLength(0);
  await tid(page, 'cc-emp-remove-reason').fill('Back to the department');
  await dialog.getByRole('button', { name: 'Remove override', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(state.writes[0].body).toMatchObject({ changes: { costCenter: '' }, costCenterReason: 'Back to the department' });
  await expect(page.getByRole('status').filter({ hasText: 'Override removed.' })).toBeVisible();
  await expect(tid(page, 'cc-emp-source')).toHaveText('From department: QA Finance');
});

test('422 cost_centre_reason_required shows the server sentence in the dialog, and the dialog stays open', async ({ page }) => {
  const { state } = await boot(page, { refusal: { status: 422, body: { error: 'cost_centre_reason_required', message: 'Enter a reason for this change.', messageAr: REASON_AR } } });
  await open(page);
  await tid(page, 'cc-emp-override-open').click();
  await tid(page, 'cc-emp-override-select').selectOption('cc-sales');
  await tid(page, 'cc-emp-override-reason').fill('x');
  await tid(page, 'cc-emp-override-save').click();
  await expect(page.getByRole('dialog', { name: 'Override cost centre' })).toContainText(REASON_EN);
  expect(state.writes).toHaveLength(1);
});

test('409 locked_into_approved_run shows the server sentence, then the controls are off with that sentence beside them', async ({ page }) => {
  await boot(page, { refusal: { status: 409, body: { error: 'locked_into_approved_run', message: M7_EN, messageAr: M7_AR } } });
  await open(page);
  await tid(page, 'cc-emp-override-open').click();
  await tid(page, 'cc-emp-override-select').selectOption('cc-sales');
  await tid(page, 'cc-emp-override-reason').fill('x');
  await tid(page, 'cc-emp-override-save').click();
  await expect(page.getByRole('dialog', { name: 'Override cost centre' })).toContainText(M7_EN);
  await page.getByRole('dialog', { name: 'Override cost centre' }).getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(tid(page, 'cc-emp-locked-note')).toHaveText(M7_EN);
  await expect(tid(page, 'cc-emp-override-open')).toBeDisabled();
});

// ═════════════════════════════ No permission ═════════════════════════════

test('without organization.costcenter.manage the field is read-only with a visible sentence, and there is no Override or Remove', async ({ page }, info) => {
  await boot(page, { manage: false, override: true });
  await open(page);
  await expect(tid(page, 'cc-emp-value')).toHaveText('SALES — Sales');
  await expect(tid(page, 'cc-emp-readonly-note')).toHaveText('Only HR managers and admins can change this.');
  await expect(tid(page, 'cc-emp-override-open')).toHaveCount(0);
  await expect(tid(page, 'cc-emp-override-remove')).toHaveCount(0);
  await shot(page, info, 'cc-en-employee-readonly');
});

test('a reader without finance.gl.read still sees the field; the company default is simply not claimed', async ({ page }) => {
  const { errors } = await boot(page, { finance: false });
  await open(page);
  await expect(tid(page, 'cc-emp-value')).toHaveText('ADMIN — Administration');
  await expect(tid(page, 'cc-emp-costing-off-note')).toHaveCount(0);
  expect(errors).toEqual([]);
});

// ═════════════════════════════ Arabic ═════════════════════════════

test('Arabic: RTL, translated field, chip, dialog and the server\'s Arabic refusal; the code stays left to right', async ({ page }, info) => {
  await boot(page, { locale: 'ar', refusal: { status: 409, body: { error: 'locked_into_approved_run', message: M7_EN, messageAr: M7_AR } } });
  await open(page);
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(tid(page, 'cc-emp-field').getByText('مركز التكلفة', { exact: true })).toBeVisible();
  await expect(tid(page, 'cc-emp-value')).toHaveText('ADMIN — الإدارة');
  await expect(tid(page, 'cc-emp-value').locator('bdi').first()).toHaveText('ADMIN');
  expect(await tid(page, 'cc-emp-value').locator('bdi').first().evaluate((el) => el.getAttribute('dir'))).toBe('ltr');
  await expect(tid(page, 'cc-emp-source')).toHaveText('من القسم: المالية');
  await shot(page, info, 'cc-ar-employee-inherited');
  await tid(page, 'cc-emp-why').click();
  await expect(tid(page, 'cc-emp-why-order')).toContainText('الترتيب: استثناء الموظف، المنصب، القسم، القسم الأعلى، الافتراضي للشركة.');
  await tid(page, 'cc-emp-override-open').click();
  const dialog = page.getByRole('dialog', { name: 'استثناء مركز التكلفة' });
  await expect(dialog).toContainText('يحل محل');
  await tid(page, 'cc-emp-override-select').selectOption('cc-sales');
  await tid(page, 'cc-emp-override-save').click();
  await expect(dialog).toContainText(REASON_AR);
  await shot(page, info, 'cc-ar-employee-override-dialog');
  await tid(page, 'cc-emp-override-reason').fill('سبب');
  await tid(page, 'cc-emp-override-save').click();
  await expect(dialog).toContainText(M7_AR);
});

test('Arabic: read-only sentence and costing-off note are translated', async ({ page }) => {
  await boot(page, { locale: 'ar', manage: false });
  await open(page);
  await expect(tid(page, 'cc-emp-readonly-note')).toHaveText('يمكن لمديري الموارد البشرية والمسؤولين فقط تغيير هذا.');
  await expect(tid(page, 'cc-emp-costing-off-note')).toHaveText('محاسبة التكلفة متوقفة، لذا لا يؤثر هذا في الرواتب بعد.');
});

test('axe: the field, with the Why panel and the Override dialog open, has no serious or critical violations', async ({ page }) => {
  await boot(page, { override: true });
  await open(page);
  await tid(page, 'cc-emp-why').click();
  expect(await axeSeriousOrWorse(page, '[data-testid="cc-emp-field"]')).toEqual([]);
  await tid(page, 'cc-emp-override-open').click();
  expect(await axeSeriousOrWorse(page, '[role="dialog"]')).toEqual([]);
});

// ═════════════════════════════ AM-30: the existing employee forms ═════════════════════════════

test('the full edit form no longer turns a department change into a cost-centre override: the PUT names the department and not the cost centre', async ({ page }) => {
  const { state } = await boot(page);
  await page.goto('/people/901/edit?section=employment');
  await expect(page.getByRole('heading', { name: 'Review employment details' })).toBeVisible();
  await page.getByRole('combobox', { name: 'Department' }).selectOption({ label: 'QA Operations' });
  // The read-only field still shows where the person will land.
  await expect(page.getByLabel('Cost center')).toHaveValue('SALES');
  await page.getByRole('button', { name: /^Step 6: Review/ }).click();
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(page).toHaveURL(/\/people\/901\?section=employment/);
  const put = state.writes.find((m) => m.method === 'PUT');
  const body = (put?.body ?? {}) as { changes?: Record<string, unknown>; costCenterReason?: unknown };
  expect(body.changes).toMatchObject({ department: 'QA Operations' });
  expect(Object.keys(body.changes ?? {})).not.toContain('costCenter');
  expect(body.costCenterReason).toBeUndefined();
});
