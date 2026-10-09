import { expect, test, type Page } from '@playwright/test';

const base = '/api/compensation/benefits';
const plan = { id: 'medical', code: 'MED', name: 'Medical Gold', companyId: 'company-benefits', planType: 'Medical', classification: 'Discretionary', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null, requiresEnrollment: true, isActive: true };
const initialEnrollment = { id: 'enrollment-medical', benefitPlanId: plan.id, employeeId: 42, employeeName: 'Alex Morgan', companyId: plan.companyId, coverageTier: 'Employee', eligibilityRuleId: 'grade-rule', entitlementTier: 'Gold', maximumBenefitAmount: 25000, requestedBenefitAmount: null, limitPeriod: 'Annual', effectiveFrom: '2026-10-15', effectiveTo: null, status: 'Active', assignmentSource: 'GradeDefault', hasException: false, exceptionReason: null, updatedAtUtc: '2026-10-08T12:00:00Z' };

async function boot(page: Page, authorized: boolean, mandatory = false) {
  let enrollment = { ...initialEnrollment };
  const exceptions: unknown[] = [];
  const submitted: Record<string, unknown>[] = [];
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'benefit-browser-fixture'));
  await page.route('**/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (path === '/api/auth/me') return reply({ id: 'benefit-reviewer', tenantId: 'benefit-tenant', tenantSlug: 'benefits-fixture', fullName: authorized ? 'HR Manager' : 'HR Officer', roles: [authorized ? 'HR Manager' : 'HR Officer'], permissions: ['employees.read', 'employees.write', ...(authorized ? ['employees.approve'] : [])], isGroupScope: false, accountType: 'SingleCompany', companies: [{ id: plan.companyId, name: 'Benefits Test Company', code: 'TEST', countryCode: 'SA', isActive: true }] });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === `${base}/plans`) return reply([{ ...plan, classification: mandatory ? 'Mandatory' : plan.classification }]);
    if (path === `${base}/enrollments`) return reply([enrollment]);
    if (path === `${base}/enrollments/${enrollment.id}`) return reply({ enrollment, exceptions, contributions: [], links: [], deductions: [] });
    if (path.endsWith('/deduction-candidates')) return reply([]);
    if (path.endsWith('/exception') && request.method() === 'PATCH') {
      const body = request.postDataJSON();
      submitted.push(body);
      exceptions.push({ id: 'exception-1', reason: body.reason, previousValuesJson: JSON.stringify(enrollment), newValuesJson: JSON.stringify(body), createdAtUtc: '2026-10-08T12:30:00Z', createdBy: 'HR Manager' });
      enrollment = { ...enrollment, ...body, hasException: true, exceptionReason: body.reason, updatedAtUtc: '2026-10-08T12:30:00Z' };
      return reply(enrollment);
    }
    if (path === '/api/grades') return reply({ items: [], total: 0, page: 1, pageSize: 100 });
    if (path.includes('/features/') || path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  await page.goto('/benefits');
  await expect(page).toHaveURL(/\/benefits$/);
  await expect(page).not.toHaveTitle('');
  await expect(page.getByRole('heading', { name: 'Benefits Administration' })).toBeVisible();
  await page.getByRole('tab', { name: 'Enrolments (1)' }).click();
  await expect(page.getByTestId('enrollments-table')).toContainText('Grade default');
  await page.getByRole('cell', { name: 'Alex Morgan', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: 'Enrolment detail' });
  await expect(drawer.getByTestId('enrollment-entitlement')).toContainText('25,000.00 SAR');
  return { drawer, submitted, errors };
}

test('authorized HR records a reasoned exception without losing grade assignment provenance', async ({ page }, info) => {
  const { drawer, submitted, errors } = await boot(page, true);
  await expect(drawer.getByRole('form', { name: 'Record contribution' }).getByLabel('Effective from', { exact: true })).toHaveValue(initialEnrollment.effectiveFrom > new Date().toISOString().slice(0, 10) ? initialEnrollment.effectiveFrom : new Date().toISOString().slice(0, 10));
  await drawer.getByRole('button', { name: 'Apply exception', exact: true }).click();
  const form = drawer.getByRole('form', { name: 'Apply benefit exception' });
  await expect(form.getByRole('button', { name: 'Save exception' })).toBeDisabled();
  await form.getByLabel('Individual benefit limit (SAR)', { exact: true }).fill('30000');
  await form.getByLabel('Coverage tier', { exact: true }).selectOption('Family');
  await form.getByLabel('Exception reason', { exact: false }).fill('Approved family coverage for this employee.');
  await form.getByRole('button', { name: 'Save exception' }).click();
  await expect(drawer.getByTestId('enrollment-entitlement')).toContainText('30,000.00 SAR');
  await expect(drawer.getByTestId('enrollment-entitlement')).toContainText('Grade default');
  await expect(drawer.getByTestId('enrollment-entitlement')).toContainText('Individual exception');
  await expect(drawer.getByTestId('benefit-exception-history')).toContainText('Approved family coverage for this employee.');
  await expect(drawer.getByTestId('benefit-exception-history')).toContainText(/Benefit limit:\s*SAR\s*25,000\.00 → SAR\s*30,000\.00/);
  expect(submitted).toHaveLength(1);
  expect(submitted[0]).toMatchObject({ expectedUpdatedAtUtc: initialEnrollment.updatedAtUtc, maximumBenefitAmount: 30000, coverageTier: 'Family', reason: 'Approved family coverage for this employee.' });
  expect(errors).toEqual([]);
  await page.screenshot({ path: `/tmp/kynex-benefits-${info.project.name}-exception.png`, fullPage: false });
});

test('HR without approval authority can read the grade benefit but cannot change an exception', async ({ page }) => {
  const { drawer, submitted, errors } = await boot(page, false);
  await expect(drawer.getByTestId('enrollment-entitlement')).toContainText('Grade default');
  await expect(drawer.getByRole('button', { name: 'Apply exception', exact: true })).toHaveCount(0);
  await expect(drawer.getByRole('form', { name: 'Apply benefit exception' })).toHaveCount(0);
  expect(submitted).toEqual([]);
  expect(errors).toEqual([]);
});

test('mandatory benefit exceptions keep the configured limit period', async ({ page }) => {
  const { drawer, submitted, errors } = await boot(page, true, true);
  await drawer.getByRole('button', { name: 'Apply exception', exact: true }).click();
  const form = drawer.getByRole('form', { name: 'Apply benefit exception' });
  const period = form.getByRole('combobox', { name: 'Limit period', exact: true });
  await expect(period).toHaveValue('Annual');
  await expect(period).toBeDisabled();
  await expect(period).toHaveAccessibleDescription('Mandatory benefits keep their configured limit period.');
  await expect(form.getByText('Mandatory benefits keep their configured limit period.', { exact: true })).toBeVisible();
  expect(submitted).toEqual([]);
  expect(errors).toEqual([]);
});

async function openIneligibleEnrollment(page: Page, companyAllowed = true) {
  await page.getByRole('dialog', { name: 'Enrolment detail' }).getByRole('button', { name: 'Close', exact: true }).click();
  await page.route('**/api/employees?**', route => route.fulfill({ json: { items: [{ id: 42, employeeCode: 'TEST-042', fullName: 'Alex Morgan', department: 'Operations', status: 'Active' }], total: 1, page: 1, pageSize: 8 } }));
  await page.route('**/api/compensation/benefits/eligibility-check?**', route => route.fulfill({ json: {
    benefitPlanId: plan.id, currency: 'SAR', employeeId: 42, employeeName: 'Alex Morgan', companyId: plan.companyId, companyName: 'Benefits Test Company', gradeId: 'other-grade', gradeName: 'Other grade', effectiveFrom: '2026-10-15', eligible: false, blockingReason: companyAllowed ? 'This grade is not eligible.' : 'This company is outside the plan scope.', alreadyEnrolled: false, matchedRuleId: null, tierName: null, maximumBenefitAmount: null, limitPeriod: null, customCriteriaNote: null,
    checks: [{ key: 'plan_active', label: 'Plan is active', passed: true, detail: 'Active plan' }, { key: 'company_scope', label: 'Company scope', passed: companyAllowed, detail: companyAllowed ? 'Company is in scope' : 'Company is not in scope' }, { key: 'plan_window', label: 'Plan dates', passed: true, detail: 'Within plan dates' }, { key: 'eligibility_rules', label: 'Grade eligibility', passed: false, detail: 'This grade is not eligible.' }],
  } }));
  await page.getByRole('button', { name: 'Enrol employee', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Enrol employee', exact: true });
  await dialog.getByRole('textbox', { name: 'Search employee' }).fill('Alex');
  await dialog.getByRole('option', { name: /Alex Morgan/ }).click();
  await expect(dialog.getByTestId('eligibility-result')).toHaveAttribute('data-eligible', 'false');
  return dialog;
}

test('authorized HR can grant an out-of-grade benefit only with an exception reason', async ({ page }) => {
  const { errors } = await boot(page, true);
  const grants: Record<string, unknown>[] = [];
  await page.route('**/api/compensation/benefits/enrollments', async route => {
    if (route.request().method() !== 'POST') return route.fallback();
    grants.push(route.request().postDataJSON());
    return route.fulfill({ json: { ...initialEnrollment, id: 'individual-grant', assignmentSource: 'IndividualException', hasException: true } });
  });
  const dialog = await openIneligibleEnrollment(page);
  await expect(dialog.getByRole('button', { name: 'Enrol', exact: true })).toBeDisabled();
  await dialog.getByRole('checkbox', { name: 'Apply an individual exception' }).check();
  await expect(dialog.getByRole('button', { name: 'Enrol', exact: true })).toBeDisabled();
  await dialog.getByLabel('Exception reason', { exact: false }).fill('Approved additional medical benefit for this employee.');
  await dialog.getByRole('button', { name: 'Enrol', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(grants).toHaveLength(1);
  expect(grants[0]).toMatchObject({ employeeId: 42, benefitPlanId: plan.id, exceptionReason: 'Approved additional medical benefit for this employee.' });
  expect(errors).toEqual([]);
});

test('an individual grant cannot bypass plan company scope', async ({ page }) => {
  await boot(page, true);
  const dialog = await openIneligibleEnrollment(page, false);
  await expect(dialog.getByRole('checkbox', { name: 'Apply an individual exception' })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Enrol', exact: true })).toBeDisabled();
});


test('plan enrolment count includes scheduled assignments and excludes ended or waived benefits', async ({ page }) => {
  const { errors } = await boot(page, false);
  const rows = [
    { ...initialEnrollment, effectiveFrom: '2099-01-01', effectiveTo: '2099-01-31' },
    { ...initialEnrollment, id: 'scheduled-successor', effectiveFrom: '2099-02-01' },
    { ...initialEnrollment, id: 'ended-enrollment', effectiveFrom: '2026-01-01', effectiveTo: '2026-01-31' },
    { ...initialEnrollment, id: 'waived-enrollment', status: 'Waived' },
  ];
  await page.route('**/api/compensation/benefits/enrollments', route => route.fulfill({ json: rows }));
  await page.reload();
  await expect(page.getByTestId('benefit-plan-list')).toContainText('1 enrolled');
  await page.getByRole('tab', { name: 'Enrolments (4)' }).click();
  const table = page.getByTestId('enrollments-table');
  await expect(table.getByText('Scheduled', { exact: true })).toHaveCount(2);
  await expect(table.getByText('Ended', { exact: true })).toBeVisible();
  await expect(table.getByText('Waived', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});
