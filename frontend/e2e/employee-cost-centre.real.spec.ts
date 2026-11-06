import fs from 'node:fs';
import path from 'node:path';
import { randomBytes } from 'node:crypto';
import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { resolveTarget } from './identity/env';
import { stampActor } from './identity/actor';
import { assertDisposableHost } from './disposable-host.guard';
import { PLATFORM_EMAIL, PLATFORM_PASSWORD } from './world';

/**
 * Cost centre on transfer (Wave A) against the REAL API: no request is intercepted. The mocked siblings
 * (employee-edit-cost-centre.spec.ts, cost-centre-employee.spec.ts, the create and hire wizard specs) prove the UI
 * against the frozen contract; this one proves that contract is what the running API and its database answer:
 *   • Edit employee → Employment pre-selects the RESOLVED cost centre with its source line;
 *   • a department change shows "Will follow …" from a real /cost-centre/preview response;
 *   • an override + a department change asks keep-or-follow, and "keep" + reason saves (200), keeps the override and
 *     writes the reason to the audit log;
 *   • without organization.costcenter.manage the field is read-only, the department locked, and a forced PUT is 403;
 *   • Add employee with "Follow department" sends no costCenterId and the person inherits;
 *   • Arabic renders the same screen;
 *   • the preview endpoint's refusals: foreign department 404 department_not_found, unknown employee 404, rate limit 429.
 *
 * ── Its own tenant ────────────────────────────────────────────────────────────────────────────
 * Every run creates a fresh SingleCompany tenant (`cc-e2e-<hex>`) through the platform-admin API with its own cost
 * centres, departments and employees, so the shared fixture tenants (whose payroll and GL specs read an uncosted world)
 * are never touched. The host guard refuses to run anywhere but a disposable stack.
 *
 * Collected by the Browser Pilot `chromium` project. Locally (never :5173, a prebuilt image):
 *   npx next dev -p 5298   with the API on E2E_API_BASE_URL and the world bootstrapped, then
 *   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5298 npx playwright test --project=chromium e2e/employee-cost-centre.real.spec.ts
 * CC_REAL_SHOTS_DIR=<dir> saves a screenshot of each state.
 */

const target = resolveTarget();
const API = target.apiBaseUrl;
const RUN = randomBytes(3).toString('hex');
const SLUG = `cc-e2e-${RUN}`;
const PASSWORD = `Cc${randomBytes(8).toString('hex')}!aA9`;
const DOMAIN = `cc-${RUN}.test`;
const OWNER = { email: `owner@${DOMAIN}`, fullName: 'Cost Centre Owner' };
/** HR Director: employees.write and organization.* but NOT organization.costcenter.manage (AuthSeeder). */
const HR_DIRECTOR = { email: `hrdirector@${DOMAIN}`, fullName: 'Cost Centre HR Director' };
/** A second payroll.approve holder, so a hire with a salary is not refused no_eligible_approver (as in the fixture world). */
const FINANCE = { email: `finance@${DOMAIN}`, fullName: 'Cost Centre Finance Approver' };

type Res<T = any> = { status: number; body: T; text: string; headers: Headers };

async function call<T = any>(method: string, p: string, opts: { token?: string; body?: unknown; companyId?: string } = {}): Promise<Res<T>> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (opts.token) headers.Authorization = `Bearer ${opts.token}`;
  if (opts.companyId) headers['X-Company-Id'] = opts.companyId;
  const res = await fetch(`${API}${p}`, { method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body) });
  const text = await res.text();
  let body: any = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* keep text */ }
  return { status: res.status, body, text, headers: res.headers };
}

function ok<T>(res: Res<T>, what: string, accept = [200, 201, 204]): T {
  if (!accept.includes(res.status)) throw new Error(`[cc-real] ${what}: HTTP ${res.status} ${res.text.slice(0, 600)}`);
  return res.body;
}

const items = (body: any): any[] => Array.isArray(body) ? body : body?.items ?? body?.Items ?? [];

async function tenantToken(email: string): Promise<string> {
  const body = ok(await call('POST', '/api/auth/login', { body: { email, password: PASSWORD, tenantSlug: SLUG } }), `login ${email}`) as any;
  return body.accessToken ?? body.token;
}

interface World {
  companyId: string;
  cc: Record<'ADM' | 'HRC' | 'SAL', { id: string; code: string }>;
  dept: Record<'finance' | 'people', { id: string; nameEn: string; nameAr: string }>;
  /** Inherits ADM from Finance. */
  inheritedId: number;
  /** Finance, with an override to SAL. */
  overrideId: number;
  /** Finance, inherits; the read-only and Arabic cases look at this one and never change it. */
  readOnlyId: number;
}
let world: World;
let ownerToken: string;
let hrToken: string;

/** A checksummed Saudi IBAN (24 characters: 'SA' + 2 check digits + a 20-digit BBAN), as bootstrap/provision.ts makes them. */
const IBAN = (seed: number) => {
  const bban = `80${String(seed).padStart(18, '0')}`.slice(-20);
  let remainder = 0;
  for (const ch of `${bban}281000`) remainder = (remainder * 10 + Number(ch)) % 97;
  return `SA${String(98 - remainder).padStart(2, '0')}${bban}`;
};

const TODAY = new Date().toISOString().slice(0, 10);

async function createEmployee(n: number, name: string, departmentId: string, extra: Record<string, unknown> = {}): Promise<number> {
  const code = `CC${RUN.toUpperCase()}${n}`;
  const body = ok(await call('POST', '/api/employees', {
    token: ownerToken,
    companyId: world.companyId,
    body: {
      employeeCode: code, manualEmployeeCode: true, englishName: name, gender: 'Male', dateOfBirth: '1990-01-15', nationality: 'Saudi',
      personalEmail: `${code.toLowerCase()}@${DOMAIN}`, companyId: world.companyId, departmentId, jobTitle: 'Specialist',
      employmentType: 'FullTime', contractType: 'Unlimited', joiningDate: `${TODAY}T00:00:00Z`,
      payrollProfile: { bankName: 'Al Rajhi Bank', iban: IBAN(6080101675 + n), accountNumber: String(6080101675 + n), paymentMethod: 'BankTransfer', salaryCurrency: 'SAR', wpsEligible: true, eosbEligible: true },
      ...extra,
    },
  }), `create employee ${name}`, [200, 201]) as any;
  return Number(body.id ?? body.employeeId ?? body.employee?.id);
}

const employee = async (id: number, token = ownerToken) => ok(await call('GET', `/api/employees/${id}`, { token, companyId: world.companyId }), `GET employee ${id}`) as any;

test.describe.configure({ mode: 'serial', timeout: 150_000 });
test.use({ storageState: { cookies: [], origins: [] }, viewport: { width: 1440, height: 1000 } });

test.beforeAll(async () => {
  test.setTimeout(240_000);
  assertDisposableHost(target.baseUrl, 'create a per-run cost-centre tenant');
  const platform = ok(await call('POST', '/api/platform/auth/login', { body: { email: PLATFORM_EMAIL, password: PLATFORM_PASSWORD } }), 'platform login') as any;
  const pt = platform.token ?? platform.accessToken;
  const created = ok(await call('POST', '/api/platform/tenants', {
    token: pt,
    body: {
      name: `Cost Centre E2E ${RUN}`, slug: SLUG, adminEmail: OWNER.email, adminFullName: OWNER.fullName, adminPassword: PASSWORD,
      accountType: 'SingleCompany', plan: 'Enterprise', maxUsers: 20, maxEmployees: 50, maxCompanies: 1,
      billingEmail: `billing@${SLUG}.test`, billingCycle: 'Monthly', monthlyAmount: 0, currencyCode: 'SAR', homeCountryCode: 'SA',
    },
  }), 'create cost-centre tenant', [201]) as any;
  ok(await call('POST', `/api/platform/tenants/${created.tenantId}/users`, {
    token: pt,
    body: { email: HR_DIRECTOR.email, password: PASSWORD, fullName: HR_DIRECTOR.fullName, roleName: 'HR Director', entityScope: 'group' },
  }), 'create HR Director');
  ok(await call('POST', `/api/platform/tenants/${created.tenantId}/users`, {
    token: pt,
    body: { email: FINANCE.email, password: PASSWORD, fullName: FINANCE.fullName, roleName: 'Finance Approver', entityScope: 'group' },
  }), 'create Finance Approver');
  ownerToken = await tenantToken(OWNER.email);
  hrToken = await tenantToken(HR_DIRECTOR.email);
  const [company] = items(ok(await call('GET', '/api/companies?page=1&pageSize=100', { token: ownerToken }), 'company'));
  const companyId = String(company.id);
  world = { companyId } as World;

  const cc = {} as World['cc'];
  for (const [code, name, nameAr] of [['ADM', 'Administration', 'الإدارة'], ['HRC', 'People costs', 'تكاليف الموظفين'], ['SAL', 'Sales', 'المبيعات']] as const) {
    const row = ok(await call('POST', '/api/organization/cost-centers', { token: ownerToken, companyId, body: { companyId, code, name, nameAr, isActive: true } }), `cost centre ${code}`, [200, 201]) as any;
    cc[code] = { id: String(row.id), code };
  }
  world.cc = cc;
  const dept = {} as World['dept'];
  for (const [key, code, nameEn, nameAr, ccId] of [['finance', 'FIN', 'Finance', 'المالية', cc.ADM.id], ['people', 'PPL', 'People', 'الموارد البشرية', cc.HRC.id]] as const) {
    const row = ok(await call('POST', '/api/departments', { token: ownerToken, companyId, body: { companyId, code, nameEn, nameAr, costCenterId: ccId, isActive: true } }), `department ${code}`, [200, 201]) as any;
    dept[key] = { id: String(row.id), nameEn, nameAr };
  }
  world.dept = dept;
  world.inheritedId = await createEmployee(1, 'Inherits Finance', dept.finance.id);
  world.overrideId = await createEmployee(2, 'Overridden Sales', dept.finance.id, { costCenterId: cc.SAL.id, costCenterReason: 'Seconded to sales (setup)' });
  world.readOnlyId = await createEmployee(3, 'Read Only Person', dept.finance.id);
  const overridden = await employee(world.overrideId);
  expect(overridden.costCenterIsOverride, 'setup: the override employee must be overridden').toBe(true);
});

// ── browser helpers ───────────────────────────────────────────────────────────────────────────

async function signIn(page: Page, email: string, locale: 'en' | 'ar' = 'en') {
  stampActor({ email, tenantSlug: SLUG, via: 'cost-centre real lane (per-run tenant)' });
  await page.addInitScript((l) => localStorage.setItem('kynexone-locale-choice-v2', l), locale);
  await page.goto(`/login?workspace=${encodeURIComponent(SLUG)}`);
  // A slow first paint can hydrate the form after a fill and reset it, so the values are re-checked before submitting.
  await expect(async () => {
    await page.locator('#li-em').fill(email);
    await page.locator('#li-pw').fill(PASSWORD);
    const workspace = page.locator('#li-ws');
    if (await workspace.count()) await workspace.fill(SLUG);
    await expect(page.locator('#li-em')).toHaveValue(email, { timeout: 1_000 });
    await expect(page.locator('#li-pw')).toHaveValue(PASSWORD, { timeout: 1_000 });
  }).toPass({ timeout: 30_000 });
  const login = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/auth/login' && r.request().method() === 'POST', { timeout: 30_000 });
  await page.locator('#li-pw').press('Enter');
  expect((await login).status(), `sign-in as ${email}`).toBe(200);
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function shot(page: Page, info: TestInfo, name: string) {
  const file = info.outputPath(`${name}.png`);
  const field = page.getByTestId('cc-edit-field');
  if (await field.count()) await field.first().evaluate((el) => el.scrollIntoView({ block: 'center' }));
  await page.screenshot({ path: file, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach(name, { path: file, contentType: 'image/png' });
  const dir = process.env.CC_REAL_SHOTS_DIR;
  if (dir) { fs.mkdirSync(dir, { recursive: true }); fs.copyFileSync(file, path.join(dir, `${name}.png`)); }
}

const previewOf = (id: number, departmentId?: string) => (url: string) =>
  url.includes(`/api/employees/${id}/cost-centre/preview`) && (departmentId === undefined ? !url.includes('departmentId=') : url.includes(`departmentId=${departmentId}`));

async function openEdit(page: Page, id: number, heading: RegExp = /Review employment details/) {
  const firstPreview = page.waitForResponse((r) => previewOf(id)(r.url()));
  await page.goto(`/people/${id}/edit?section=employment`);
  await expect(page.getByRole('heading', { name: heading })).toBeVisible({ timeout: 30_000 });
  const res = await firstPreview;
  expect(res.status(), 'the no-parameter preview the form opens with').toBe(200);
  return res;
}

async function saveFromReview(page: Page, id: number) {
  await page.getByRole('button', { name: /^Step 6: Review/ }).click();
  const put = page.waitForResponse((r) => r.url().endsWith(`/api/employees/${id}`) && r.request().method() === 'PUT');
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  return put;
}

// ═════════════════════════════ With organization.costcenter.manage (Admin) ═════════════════════════════

test('edit: the resolved cost centre is pre-selected with its source, from the real resolution', async ({ page }, info) => {
  await signIn(page, OWNER.email);
  const first = await openEdit(page, world.inheritedId);
  expect(await first.json()).toMatchObject({ costCentreId: world.cc.ADM.id, code: 'ADM', source: 'Department', isOverride: false });
  const select = page.getByRole('combobox', { name: 'Cost center', exact: true });
  await expect(select.locator('option:checked')).toHaveText('Follow department · ADM — Administration');
  await expect(page.getByTestId('cc-edit-source')).toHaveText('Inherited from Finance');
  await shot(page, info, 'real-en-inherited');
});

test('edit: a department change shows "Will follow …" from a real preview response, and the save follows it', async ({ page }, info) => {
  await signIn(page, OWNER.email);
  await openEdit(page, world.inheritedId);
  const preview = page.waitForResponse((r) => previewOf(world.inheritedId, world.dept.people.id)(r.url()));
  await page.getByRole('combobox', { name: 'Department', exact: true }).selectOption({ label: 'People' });
  const res = await preview;
  expect(res.status()).toBe(200);
  expect(await res.json()).toMatchObject({ costCentreId: world.cc.HRC.id, code: 'HRC', source: 'Department', sourceName: 'People' });
  await expect(page.getByTestId('cc-edit-preview')).toHaveText('Will follow People → HRC');
  await expect(page.getByTestId('cc-edit-decision')).toHaveCount(0);
  await shot(page, info, 'real-en-department-change');
  const put = await saveFromReview(page, world.inheritedId);
  expect(put.status(), await put.text()).toBe(200);
  const after = await employee(world.inheritedId);
  expect(after.departmentId).toBe(world.dept.people.id);
  expect(after.costCenterIsOverride).toBeFalsy();
  expect(after.costCentreResolution).toMatchObject({ costCenterId: world.cc.HRC.id, code: 'HRC', source: 'Department' });
});

test('edit: override + department change asks keep-or-follow; keep + reason saves 200, keeps the override and audits the reason', async ({ page }, info) => {
  await signIn(page, OWNER.email);
  const first = await openEdit(page, world.overrideId);
  expect(await first.json()).toMatchObject({ code: 'ADM', source: 'Department' }); // what removing the override returns to
  await expect(page.getByRole('combobox', { name: 'Cost center', exact: true })).toHaveValue(world.cc.SAL.id);
  await expect(page.getByTestId('cc-edit-source')).toHaveText('Override: set for this employee');
  const preview = page.waitForResponse((r) => previewOf(world.overrideId, world.dept.people.id)(r.url()));
  await page.getByRole('combobox', { name: 'Department', exact: true }).selectOption({ label: 'People' });
  expect((await preview).status()).toBe(200);
  const decision = page.getByRole('radiogroup', { name: 'This employee has a cost centre override. What happens to it after the move?' });
  await expect(decision).toBeVisible();
  await expect(decision.getByRole('radio', { name: 'Follow People → HRC' })).toBeVisible();
  await decision.getByRole('radio', { name: 'Keep SAL (override)' }).check();
  const reason = `Still charged to sales ${RUN}`;
  await page.getByRole('textbox', { name: /^Reason/ }).fill(reason);
  await shot(page, info, 'real-en-keep-or-follow');
  const put = await saveFromReview(page, world.overrideId);
  const sent = put.request().postDataJSON();
  expect(sent).toMatchObject({ costCenterDecision: 'keep', costCenterReason: reason, expectedCostCenterOverrideId: world.cc.SAL.id });
  expect(put.status(), await put.text()).toBe(200);

  const after = await employee(world.overrideId);
  expect(after.departmentId).toBe(world.dept.people.id);
  expect(after.costCenterIsOverride).toBe(true);
  expect(after.costCenterId).toBe(world.cc.SAL.id);
  expect(after.costCentreResolution).toMatchObject({ code: 'SAL', source: 'Employee' });

  // Saving lands on the profile's Employment tab, which shows the override retained.
  await expect(page).toHaveURL(new RegExp(`/people/${world.overrideId}\\?section=employment`), { timeout: 30_000 });
  await expect(page.getByTestId('cc-emp-value')).toContainText('SAL', { timeout: 30_000 });
  await expect(page.getByTestId('cc-emp-override-remove')).toBeVisible();
  await page.getByTestId('cc-emp-field').evaluate((el) => el.scrollIntoView({ block: 'center' }));
  await shot(page, info, 'real-en-profile-override-kept');

  // …and the reason is in the audit log, on the attribution row of this employee.
  const audit = items(ok(await call('GET', '/api/audit-logs?limit=200', { token: ownerToken }), 'audit log'));
  const rows = audit.filter((a: any) => String(a.entityId ?? a.EntityId) === String(world.overrideId) && JSON.stringify(a).includes(reason));
  expect(rows.length, `an audit row for employee ${world.overrideId} carrying the reason`).toBeGreaterThan(0);
});

// ═════════════════════════════ Without organization.costcenter.manage (HR Director) ═════════════════════════════

test('edit without the permission: read-only value and source, department locked; a forced PUT is 403 and nothing changes', async ({ page }, info) => {
  const before = await employee(world.readOnlyId);
  await signIn(page, HR_DIRECTOR.email);
  await openEdit(page, world.readOnlyId);
  await expect(page.getByRole('combobox', { name: 'Cost center', exact: true })).toHaveCount(0);
  await expect(page.getByTestId('cc-edit-value')).toHaveText('ADM — Administration');
  await expect(page.getByTestId('cc-edit-source')).toHaveText('Inherited from Finance');
  await expect(page.getByTestId('cc-edit-readonly-note')).toHaveText('Only HR managers and admins can change this.');
  await expect(page.getByRole('combobox', { name: 'Department', exact: true })).toBeDisabled();
  await expect(page.getByTestId('cc-edit-department-note')).toBeVisible();
  await shot(page, info, 'real-en-read-only');

  const date = new Date().toISOString().slice(0, 10);
  const forcedCc = await call('PUT', `/api/employees/${world.readOnlyId}`, { token: hrToken, companyId: world.companyId, body: { effectiveDate: date, changes: { costCenter: 'SAL' }, costCenterReason: 'forced' } });
  expect(forcedCc.status, forcedCc.text).toBe(403);
  const forcedDept = await call('PUT', `/api/employees/${world.readOnlyId}`, { token: hrToken, companyId: world.companyId, body: { effectiveDate: date, changes: { department: 'People' } } });
  expect(forcedDept.status, forcedDept.text).toBe(403);
  const hypothetical = await call('GET', `/api/employees/${world.readOnlyId}/cost-centre/preview?departmentId=${world.dept.people.id}`, { token: hrToken, companyId: world.companyId });
  expect(hypothetical.status, 'HR Director holds employees.write, so a hypothetical preview is allowed').toBe(200);
  const after = await employee(world.readOnlyId);
  expect({ d: after.departmentId, c: after.costCenterId, o: !!after.costCenterIsOverride }).toEqual({ d: before.departmentId, c: before.costCenterId, o: !!before.costCenterIsOverride });
});

// ═════════════════════════════ Add employee ═════════════════════════════

test('create wizard: "Follow department" sends no costCenterId and the new person inherits the department\'s cost centre', async ({ page }, info) => {
  await signIn(page, OWNER.email);
  await page.goto('/people/new');
  const dialog = page.getByRole('dialog', { name: 'Add Employee', exact: true });
  await expect(dialog).toBeVisible({ timeout: 30_000 });
  const name = `Follow Dept ${RUN}`;
  await dialog.getByLabel('English full name', { exact: false }).fill(name);
  await dialog.getByRole('button', { name: 'Next: Employment', exact: true }).click();
  await dialog.getByRole('combobox', { name: 'Department', exact: true }).selectOption({ label: 'Finance' });
  const select = dialog.getByTestId('cc-create-select');
  await expect(select.locator('option:checked')).toHaveText('Follow department · ADM — Administration');
  await shot(page, info, 'real-en-create-follow');
  const create = page.waitForRequest((r) => new URL(r.url()).pathname === '/api/employees' && r.method() === 'POST');
  // The quick path: Review, then Create (the wizard asks for whatever else this tenant requires on the way).
  await dialog.getByRole('button', { name: 'Next: Review', exact: true }).click();
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  const request = await create;
  const body = request.postDataJSON();
  expect(body).not.toHaveProperty('costCenterId');
  expect(body).not.toHaveProperty('costCenterReason');
  const response = await request.response();
  expect(response?.status(), await response?.text()).toBeLessThan(300);
  const createdId = Number((await response!.json()).id);
  const createdRow = await employee(createdId);
  expect(createdRow.costCenterIsOverride).toBeFalsy();
  expect(createdRow.costCentreResolution).toMatchObject({ code: 'ADM', source: 'Department' });
});

// ═════════════════════════════ Arabic ═════════════════════════════

test('Arabic: the edit screen renders RTL with the Arabic source line and names', async ({ page }, info) => {
  await signIn(page, OWNER.email, 'ar');
  await openEdit(page, world.readOnlyId, /راجع بيانات التوظيف/);
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  const select = page.getByRole('combobox', { name: 'مركز التكلفة', exact: true });
  await expect(select).toBeVisible();
  await expect(select.locator('option:checked')).toContainText('ADM');
  await expect(select.locator('option:checked')).toContainText('الإدارة');
  await expect(page.getByTestId('cc-edit-source')).toContainText('المالية');
  await shot(page, info, 'real-ar-inherited');
});

// ═════════════════════════════ The preview endpoint directly ═════════════════════════════

test('preview endpoint: plain 200, foreign department 404 department_not_found, unknown employee 404, then 429 under load', async () => {
  const plain = await call('GET', `/api/employees/${world.readOnlyId}/cost-centre/preview`, { token: ownerToken });
  expect(plain.status, plain.text).toBe(200);
  expect(plain.body).toMatchObject({ code: 'ADM', source: 'Department' });
  const foreign = await call('GET', `/api/employees/${world.readOnlyId}/cost-centre/preview?departmentId=${crypto.randomUUID()}`, { token: ownerToken });
  expect(foreign.status, foreign.text).toBe(404);
  expect(foreign.body).toMatchObject({ error: 'department_not_found' });
  const missing = await call('GET', '/api/employees/987654321/cost-centre/preview', { token: ownerToken });
  expect(missing.status).toBe(404);
  // 60 per user per minute: the 61st inside the window is refused.
  const statuses: number[] = [];
  for (let i = 0; i < 61; i++) statuses.push((await call('GET', `/api/employees/${world.readOnlyId}/cost-centre/preview`, { token: ownerToken })).status);
  expect(statuses).toContain(429);
});
