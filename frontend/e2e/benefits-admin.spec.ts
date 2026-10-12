import { test, expect, type APIRequestContext } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import {
  apiLogin, crashIndicators, mainText, tenantLogin,
  INTELLIFLOW_ADMIN, INTELLIFLOW_EMP1, INTELLIFLOW_SLUG,
} from './helpers';

// W2-F — Benefits administration + "My benefits".
// Every tenant starts with zero plans (no seeder, by design), so each test creates what it needs with
// a unique code. Assertions are on named content, never on body length.

const BASE = '/api/compensation/benefits';
const uid = () => randomUUID().replaceAll('-', '').slice(0, 16).toUpperCase();

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

async function createGradedEmployee(request: APIRequestContext, token: string) {
  const headers = { Authorization: `Bearer ${token}` };
  const code = uid();
  const companies = await request.get('/api/companies', { headers, params: { pageSize: 100 } });
  expect(companies.status(), await companies.text()).toBe(200);
  const company = (await companies.json()).items.find((item: { isActive: boolean; countryCode: string }) => item.isActive && item.countryCode);
  expect(company, 'an active employing company with a configured country').toBeTruthy();
  const gradeResponse = await request.post('/api/grades', { headers, data: { code: `GB${code}`, name: `Benefits grade ${code}`, level: 4, isActive: true } });
  expect(gradeResponse.status(), await gradeResponse.text()).toBe(201);
  const grade = await gradeResponse.json();
  // Create before this test's plan/rule so hire-time defaults cannot pre-enrol the test subject.
  // Shared seeded employees are never regraded or otherwise changed by these scenarios.
  const employeeResponse = await request.post('/api/employees', { headers, data: {
    employeeCode: `BE${code}`, manualEmployeeCode: true, englishName: `Benefits Employee ${code}`,
    companyId: company.id, gradeId: grade.id, joiningDate: '2026-01-01T00:00:00Z',
  } });
  expect(employeeResponse.status(), await employeeResponse.text()).toBe(201);
  const employee = await employeeResponse.json();
  expect(employee.gradeId).toBe(grade.id);
  return { grade, employee };
}

test.describe('Benefits administration — UI', () => {
  test('HR creates a plan, restricts it by grade, and enrols an eligible employee with the check shown first', async ({ page, request }) => {
    const { grade, employee } = await createGradedEmployee(request, await adminToken(request));
    const code = `E2E-${uid()}`;
    const name = `Medical ${code}`;
    await tenantLogin(page, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    await page.goto('/benefits');
    await expect(page.getByRole('heading', { name: 'Benefits Administration' })).toBeVisible({ timeout: 15_000 });

    // Either the guided first-plan state or the populated list: both must offer plan creation.
    const firstPlan = page.getByRole('button', { name: 'Create your first plan' });
    const newPlan = page.getByRole('button', { name: 'Add benefit plan' });
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
    await expect(detail.getByTestId('rules-empty')).toContainText('Add at least one grade before employees can be enrolled');

    // Use this scenario's real grade id; display labels also include the grade level.
    await detail.getByRole('button', { name: 'Add rule' }).click();
    const ruleForm = detail.getByRole('form', { name: 'Add eligibility rule' });
    await ruleForm.getByLabel('Rule grade').selectOption(grade.id);
    await ruleForm.getByLabel('Tier name', { exact: true }).fill('Medical Gold');
    await ruleForm.getByRole('button', { name: 'Save rule' }).click();
    await expect(detail.getByTestId('rules-list')).toContainText(grade.name);

    // Enrol: the eligibility result must be visible BEFORE the submit is possible.
    await detail.getByRole('button', { name: 'Enrol in this plan' }).click();
    const enrol = page.getByRole('dialog', { name: 'Enrol employee' });
    await enrol.getByLabel('Search employee').fill(employee.employeeCode);
    await enrol.getByRole('option', { name: new RegExp(employee.fullName) }).click();
    await enrol.getByLabel('Start date').fill('2026-09-01');
    const result = enrol.getByTestId('eligibility-result');
    await expect(result).toHaveAttribute('data-eligible', 'true');
    await expect(result).toContainText('Eligible for this plan');
    await expect(result).toContainText('Matches an effective grade eligibility rule');
    await expect(result).toContainText(grade.name);
    await expect(result).toContainText('Resolved entitlement: Medical Gold');
    await enrol.getByRole('button', { name: 'Enrol', exact: true }).click();
    await expect(enrol).toBeHidden();

    await expect(page.getByRole('tab', { name: /Enrolments/ })).toHaveAttribute('aria-selected', 'true');
    const table = page.getByTestId('enrollments-table');
    await expect(table.getByRole('row', { name: new RegExp(`${employee.fullName}.*${name}`) })).toBeVisible();

    // Record a contribution from the enrolment drawer.
    await table.getByRole('row', { name: new RegExp(`${employee.fullName}.*${name}`) }).click();
    const drawer = page.getByRole('dialog', { name: 'Enrolment detail' });
    const contribForm = drawer.getByRole('form', { name: 'Record contribution' });
    await contribForm.getByLabel('Employee share').fill('250');
    await contribForm.getByLabel('Employer share').fill('750');
    await contribForm.getByRole('button', { name: 'Record contribution' }).click();
    await expect(drawer.getByTestId('contributions-table')).toContainText('250.00 SAR');
    await expect(drawer.getByTestId('contributions-table')).toContainText('750.00 SAR');

    expect(crashIndicators(await mainText(page))).toEqual([]);
  });

  test('an ineligible employee is shown as not eligible and cannot be submitted; the API agrees', async ({ page, request }) => {
    const token = await adminToken(request);
    const h = { Authorization: `Bearer ${token}` };
    const { employee } = await createGradedEmployee(request, token);
    const code = `E2E-${uid()}`;
    // The dedicated employee has a grade, but not the different grade allowed by this plan.
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
    await enrol.getByLabel('Search employee').fill(employee.employeeCode);
    await enrol.getByRole('option', { name: new RegExp(employee.fullName) }).click();
    await enrol.getByLabel('Start date').fill('2026-09-01');
    const result = enrol.getByTestId('eligibility-result');
    await expect(result).toHaveAttribute('data-eligible', 'false');
    await expect(result).toContainText('Not eligible for this plan');
    await expect(result).toContainText('Employee has a grade for benefit eligibility.');
    await expect(result).toContainText("None of the 1 grade rule(s) in effect match the employee's company and grade.");
    await expect(enrol.getByRole('button', { name: 'Enrol', exact: true })).toBeDisabled();

    // The gate the preview mirrors returns the same reason as a 400.
    const blocked = await request.post(`${BASE}/enrollments`, { headers: h, data: { benefitPlanId: planId, employeeId: employee.id, effectiveFrom: '2026-09-01' } });
    expect(blocked.status()).toBe(400);
    expect(await blocked.text()).toContain('not eligible for this benefit plan based on grade eligibility rules');
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
    expect(body.checks.map((c: { key: string }) => c.key)).toEqual(['plan_active', 'company_scope', 'plan_window', 'employee_grade', 'grade_rules_configured', 'eligibility_rules']);
    expect(body.checks.find((c: { key: string }) => c.key === 'plan_window').passed).toBe(false);
    expect(body.checks.find((c: { key: string }) => c.key === 'grade_rules_configured').passed).toBe(false);
    expect(body.checks.find((c: { key: string }) => c.key === 'eligibility_rules').passed).toBe(false);

    // Moving inside the plan dates still fails closed until an explicit grade rule exists.
    const withoutRule = await request.get(`${BASE}/eligibility-check`, { headers: h, params: { planId, employeeId: liu, effectiveFrom: '2026-09-01' } });
    expect(withoutRule.status()).toBe(200);
    const noRule = await withoutRule.json();
    expect(noRule.eligible).toBe(false);
    expect(noRule.checks.find((c: { key: string }) => c.key === 'plan_window').passed).toBe(true);
    expect(noRule.checks.find((c: { key: string }) => c.key === 'grade_rules_configured').passed).toBe(false);

    const list = await request.get(`${BASE}/enrollments`, { headers: h, params: { planId } });
    expect(list.status()).toBe(200);
    expect(await list.json()).toEqual([]);
  });

  test('employees cannot reach the HR benefits API', async ({ request }) => {
    const token = await apiLogin(request, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    const resp = await request.get(`${BASE}/plans`, { headers: { Authorization: `Bearer ${token}` } });
    expect(resp.status()).toBe(403);
  });
});

test.describe('My benefits — employee self-service', () => {
  test('an employee sees their own mandatory coverage and employer-paid cost without an employee charge', async ({ page, request }) => {
    const token = await adminToken(request);
    const h = { Authorization: `Bearer ${token}` };
    const code = `E2E-${uid()}`;
    const planName = `Life ${code}`;
    // A mandatory minimum does not depend on grade configuration. This ESS scenario leaves
    // the shared employee's placement untouched; discretionary grade coverage is tested above.
    const plan = await request.post(`${BASE}/plans`, { headers: h, data: { companyId: null, code, name: planName, planType: 'Life', classification: 'Mandatory', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null } });
    expect(plan.status(), await plan.text()).toBe(201);
    const planId = (await plan.json()).id;
    const liu = await employeeByName(request, token, 'Liu Wei');
    const eligibility = await request.get(`${BASE}/eligibility-check`, { headers: h, params: { planId, employeeId: liu, effectiveFrom: '2026-01-01' } });
    expect(eligibility.status()).toBe(200);
    const check = await eligibility.json();
    expect(check.eligible).toBe(true);
    expect(check.checks.every((item: { passed: boolean }) => item.passed)).toBe(true);
    expect(check.checks.find((item: { key: string }) => item.key === 'eligibility_rules').detail).toContain('Mandatory minimum coverage applies');
    const enr = await request.post(`${BASE}/enrollments`, { headers: h, data: { benefitPlanId: planId, employeeId: liu, coverageTier: 'Employee', effectiveFrom: '2026-01-01' } });
    expect(enr.status(), await enr.text()).toBe(200);
    const enrollmentId = (await enr.json()).id;
    const charged = await request.post(`${BASE}/enrollments/${enrollmentId}/contributions`, { headers: h, data: { employeeAmount: 40, employerAmount: 160, frequency: 'Monthly', effectiveFrom: '2026-01-01' } });
    expect(charged.status()).toBe(400);
    expect(await charged.text()).toContain('Employees cannot be charged for the mandatory benefit floor');
    const contribution = await request.post(`${BASE}/enrollments/${enrollmentId}/contributions`, { headers: h, data: { employeeAmount: 0, employerAmount: 160, frequency: 'Monthly', effectiveFrom: '2026-01-01' } });
    expect(contribution.status(), await contribution.text()).toBe(200);

    await tenantLogin(page, INTELLIFLOW_EMP1.email, INTELLIFLOW_EMP1.password, INTELLIFLOW_SLUG);
    await page.goto('/ess/benefits');
    await expect(page.getByRole('heading', { name: 'My Benefits' })).toBeVisible({ timeout: 15_000 });
    const card = page.getByTestId('my-benefits-list').getByRole('article').filter({ hasText: planName });
    await expect(card.getByText('You pay (Monthly)', { exact: true }).locator('..')).toContainText('0.00 SAR');
    await expect(card.getByText('Employer pays', { exact: true }).locator('..')).toContainText('160.00 SAR');
    await expect(card).not.toContainText('40.00 SAR');
    expect(crashIndicators(await mainText(page))).toEqual([]);
  });
});
