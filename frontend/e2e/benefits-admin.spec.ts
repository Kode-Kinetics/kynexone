import { test, expect, type APIRequestContext } from '@playwright/test';
import {
  apiLogin, crashIndicators, mainText, tenantLogin,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_SLUG,
} from './helpers';

// W2-F — Benefits administration + "My benefits".
// Every tenant starts with zero plans (no seeder, by design), so each test creates what it needs with
// a unique code. Assertions are on named content, never on body length.

const BASE = '/api/compensation/benefits';
const uid = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 5)}`.toUpperCase();

async function adminToken(request: APIRequestContext) {
  return apiLogin(request, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
}

async function employeeByName(request: APIRequestContext, token: string, name: string): Promise<number> {
  const resp = await request.get('/api/employees', { headers: { Authorization: `Bearer ${token}` }, params: { search: name, pageSize: 5 } });
  expect(resp.status()).toBe(200);
  const hit = (await resp.json()).items.find((e: { fullName: string }) => e.fullName === name);
  expect(hit, `seeded employee ${name}`).toBeTruthy();
  return hit.id as number;
}

test.describe('Benefits administration — UI', () => {
  test('HR creates a plan, restricts it by grade, and enrols an eligible employee with the check shown first', async ({ page }) => {
    const code = `E2E-${uid()}`;
    const name = `Medical ${code}`;
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/benefits');
    await expect(page.getByRole('heading', { name: 'Benefits Administration' })).toBeVisible({ timeout: 15_000 });

    // Either the guided first-plan state or the populated list: both must offer plan creation.
    const firstPlan = page.getByRole('button', { name: 'Create your first plan' });
    const newPlan = page.getByRole('button', { name: 'New plan' });
    await expect(firstPlan.or(newPlan)).toBeVisible();
    await firstPlan.or(newPlan).click();

    const dialog = page.getByRole('dialog', { name: 'New benefit plan' });
    await dialog.getByLabel('Plan code').fill(code);
    await dialog.getByLabel('Plan name').fill(name);
    await dialog.getByLabel('Effective from').fill('2026-01-01');
    await dialog.getByRole('button', { name: 'Create plan' }).click();
    await expect(dialog).toBeHidden();

    const detail = page.getByTestId('benefit-plan-detail');
    await expect(detail.getByRole('heading', { name })).toBeVisible();
    await expect(detail.getByTestId('rules-empty')).toContainText('can be enrolled');

    // Eligibility rule: IntelliFlow's employees are on grade "IFL Standard".
    await detail.getByRole('button', { name: 'Add rule' }).click();
    const ruleForm = detail.getByRole('form', { name: 'Add eligibility rule' });
    await ruleForm.getByLabel('Rule grade').selectOption({ label: 'IFL Standard (IFL-STD)' });
    await ruleForm.getByRole('button', { name: 'Save rule' }).click();
    await expect(detail.getByTestId('rules-list')).toContainText('IFL Standard');

    // Enrol: the eligibility result must be visible BEFORE the submit is possible.
    await detail.getByRole('button', { name: 'Enrol in this plan' }).click();
    const enrol = page.getByRole('dialog', { name: 'Enrol employee' });
    await enrol.getByLabel('Search employee').fill('Liu Wei');
    await enrol.getByRole('option', { name: /Liu Wei/ }).click();
    await enrol.getByLabel('Start date').fill('2026-09-01');
    const result = enrol.getByTestId('eligibility-result');
    await expect(result).toHaveAttribute('data-eligible', 'true');
    await expect(result).toContainText('Eligible for this plan');
    await expect(result).toContainText('Matches a company/grade eligibility rule');
    await enrol.getByRole('button', { name: 'Enrol', exact: true }).click();
    await expect(enrol).toBeHidden();

    await expect(page.getByRole('tab', { name: /Enrolments/ })).toHaveAttribute('aria-selected', 'true');
    const table = page.getByTestId('enrollments-table');
    await expect(table.getByRole('row', { name: new RegExp(`Liu Wei.*${name}`) })).toBeVisible();

    // Record a contribution from the enrolment drawer.
    await table.getByRole('row', { name: new RegExp(`Liu Wei.*${name}`) }).click();
    const drawer = page.getByRole('dialog', { name: 'Enrolment detail' });
    const contribForm = drawer.getByRole('form', { name: 'Record contribution' });
    await contribForm.getByLabel('Employee share').fill('250');
    await contribForm.getByLabel('Employer share').fill('750');
    await contribForm.getByRole('button', { name: 'Record contribution' }).click();
    await expect(drawer.getByTestId('contributions-table')).toContainText('750.00 SAR');

    expect(crashIndicators(await mainText(page))).toEqual([]);
  });

  test('an ineligible employee is shown as not eligible and cannot be submitted; the API agrees', async ({ page, request }) => {
    const token = await adminToken(request);
    const h = { Authorization: `Bearer ${token}` };
    const code = `E2E-${uid()}`;
    // A grade no seeded employee holds, so a rule on it excludes everyone on "IFL Standard".
    const grade = await request.post('/api/grades', { headers: h, data: { code: `G${code}`.slice(0, 20), name: `Exec ${code}`, level: 9, isActive: true } });
    expect(grade.status(), await grade.text()).toBeLessThan(300);
    const gradeId = (await grade.json()).id;
    const plan = await request.post(`${BASE}/plans`, { headers: h, data: { companyId: null, code, name: `Executive ${code}`, planType: 'Medical', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null } });
    expect(plan.status()).toBe(201);
    const planId = (await plan.json()).id;
    expect((await request.post(`${BASE}/plans/${planId}/eligibility`, { headers: h, data: { companyId: null, gradeId, effectiveFrom: '2026-01-01', effectiveTo: null } })).status()).toBe(200);

    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/benefits');
    await page.getByTestId('benefit-plan-list').getByRole('button', { name: new RegExp(`Executive ${code}`) }).click();
    await page.getByTestId('benefit-plan-detail').getByRole('button', { name: 'Enrol in this plan' }).click();
    const enrol = page.getByRole('dialog', { name: 'Enrol employee' });
    await enrol.getByLabel('Search employee').fill('Carlos');
    await enrol.getByRole('option', { name: /Carlos Mendez/ }).click();
    await enrol.getByLabel('Start date').fill('2026-09-01');
    const result = enrol.getByTestId('eligibility-result');
    await expect(result).toHaveAttribute('data-eligible', 'false');
    await expect(result).toContainText('Not eligible for this plan');
    await expect(result).toContainText('None of the 1 rule(s) in effect');
    await expect(enrol.getByRole('button', { name: 'Enrol', exact: true })).toBeDisabled();

    // The gate the preview mirrors returns the same reason as a 400.
    const carlos = await employeeByName(request, token, 'Carlos Mendez');
    const blocked = await request.post(`${BASE}/enrollments`, { headers: h, data: { benefitPlanId: planId, employeeId: carlos, effectiveFrom: '2026-09-01' } });
    expect(blocked.status()).toBe(400);
    expect(await blocked.text()).toContain('not eligible for this benefit plan based on company/grade');
  });
});

test.describe('Benefits — API contract', () => {
  test('eligibility-check reports each gate check and never writes', async ({ request }) => {
    const token = await adminToken(request);
    const h = { Authorization: `Bearer ${token}` };
    const code = `E2E-${uid()}`;
    const planId = (await (await request.post(`${BASE}/plans`, { headers: h, data: { companyId: null, code, name: `Dental ${code}`, planType: 'Dental', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null } })).json()).id;
    const liu = await employeeByName(request, token, 'Liu Wei');

    const early = await request.get(`${BASE}/eligibility-check`, { headers: h, params: { planId, employeeId: liu, effectiveFrom: '2025-06-01' } });
    expect(early.status()).toBe(200);
    const body = await early.json();
    expect(body.eligible).toBe(false);
    expect(body.checks.map((c: { key: string }) => c.key)).toEqual(['plan_active', 'company_scope', 'plan_window', 'eligibility_rules']);
    expect(body.checks.find((c: { key: string }) => c.key === 'plan_window').passed).toBe(false);

    const list = await request.get(`${BASE}/enrollments`, { headers: h, params: { planId } });
    expect(await list.json()).toEqual([]);
  });

  test('employees cannot reach the HR benefits API', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    const resp = await request.get(`${BASE}/plans`, { headers: { Authorization: `Bearer ${token}` } });
    expect(resp.status()).toBe(403);
  });
});

test.describe('My benefits — employee self-service', () => {
  test('an enrolled employee sees their own plan and cost share', async ({ page, request }) => {
    const token = await adminToken(request);
    const h = { Authorization: `Bearer ${token}` };
    const code = `E2E-${uid()}`;
    const planName = `Life ${code}`;
    const planId = (await (await request.post(`${BASE}/plans`, { headers: h, data: { companyId: null, code, name: planName, planType: 'Life', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null } })).json()).id;
    const liu = await employeeByName(request, token, 'Liu Wei');
    const enr = await request.post(`${BASE}/enrollments`, { headers: h, data: { benefitPlanId: planId, employeeId: liu, coverageTier: 'Employee', effectiveFrom: '2026-01-01' } });
    expect(enr.status()).toBe(200);
    const contribution = await request.post(`${BASE}/enrollments/${(await enr.json()).id}/contributions`, { headers: h, data: { employeeAmount: 40, employerAmount: 160, frequency: 'Monthly', effectiveFrom: '2026-01-01' } });
    expect(contribution.status()).toBe(200);

    await tenantLogin(page, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    await page.goto('/ess/benefits');
    await expect(page.getByRole('heading', { name: 'My Benefits' })).toBeVisible({ timeout: 15_000 });
    const card = page.getByTestId('my-benefits-list').getByRole('article').filter({ hasText: planName });
    await expect(card).toContainText('40.00 SAR');
    await expect(card).toContainText('160.00 SAR');
    expect(crashIndicators(await mainText(page))).toEqual([]);
  });
});
