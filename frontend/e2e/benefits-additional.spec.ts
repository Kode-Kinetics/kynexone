import { expect, test, type Page } from '@playwright/test';

const base = '/api/compensation/benefits';
const companyId = 'company-extra';
const medical = { id: 'medical', code: 'MED', name: 'Medical Gold', companyId, planType: 'Medical', classification: 'Discretionary', currency: 'SAR', effectiveFrom: '2026-01-01', effectiveTo: null, requiresEnrollment: true, isActive: true };
const transport = { ...medical, id: 'transport', code: 'TRN', name: 'Transport Allowance', planType: 'Transport' };
const education = { ...medical, id: 'education', code: 'EDU', name: 'Education Allowance', planType: 'Education' };
const baseline = { id: 'default-medical', benefitPlanId: medical.id, employeeId: 42, employeeName: 'Alex Morgan', companyId, coverageTier: 'Employee', eligibilityRuleId: 'grade-rule', entitlementTier: 'Gold', maximumBenefitAmount: 25000, requestedBenefitAmount: null, limitPeriod: 'Annual', effectiveFrom: '2026-01-01', effectiveTo: null, status: 'Active', assignmentSource: 'GradeDefault', hasException: false, exceptionReason: null, updatedAtUtc: '2026-10-09T12:00:00Z' };
const additional = { ...baseline, id: 'extra-education', benefitPlanId: education.id, assignmentSource: 'IndividualAdditional', entitlementTier: 'Education award', maximumBenefitAmount: 15000, effectiveStatus: 'Current', planName: education.name, planCode: education.code, currency: 'SAR', classification: 'Discretionary', reviewDate: '2027-01-01', reviewRequired: true, reviewReasons: ['Review date reached'], approvalRequestId: 'approved-request', grantReason: 'Professional development support.', treatment: 'Reimbursement', plannedEmployerCost: 15000, plannedEmployeeCost: 0, costFrequency: 'Annual' };

async function boot(page: Page, options: { fixedTerm?: boolean; missingRoute?: boolean; extra?: boolean; readonly?: boolean; manage?: boolean; partialFailure?: boolean; policy?: boolean } = {}) {
  const errors: string[] = [], submitted: Record<string, unknown>[] = [];
  let directWrites = 0;
  const endRequests: Record<string, unknown>[] = [];
  let request: Record<string, unknown> | null = null;
  const frozenPolicy = { delivery: 'SalaryAllowance', amount: 750, frequency: 'Monthly', salaryComponentId: 'mapping', prorate: false, paymentMonth: null, receiptRequired: false, receiptLabel: '', claimWindowDays: null, instructions: '' };
  const plans = [medical, { ...education, ...(options.policy ? { paymentPolicy: { ...frozenPolicy, amount: 950 }, policyVersion: 2 } : {}), effectiveTo: options.fixedTerm ? '2027-12-31' : null }, transport];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error' && !((options.missingRoute && message.text().includes('422')) || (options.partialFailure && message.text().includes('409')))) errors.push(message.text());
  });
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'additional-fixture'));
  await page.route('**/api/**', async route => {
    const req = route.request(), url = new URL(req.url()), path = url.pathname;
    const reply = (json: unknown, status = 200) => route.fulfill({ json, status });
    if (path === '/api/auth/me') return reply({ id: 'hr-officer', tenantId: 'fixture-tenant', tenantSlug: 'benefits', fullName: 'HR Officer', roles: [options.manage ? 'HR Manager' : 'HR Officer'], permissions: ['employees.read', 'ess.read', ...(options.readonly ? [] : ['employees.write']), ...(options.manage ? ['approvals.manage', 'employees.approve'] : [])], isGroupScope: false, companies: [{ id: companyId, name: 'Benefits Company', countryCode: 'SA', isActive: true }] });
    if (path === '/api/features/disabled-keys') return reply(['release_a']);
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === `${base}/plans`) return reply(plans);
    if (path === `${base}/enrollments` && req.method() === 'POST' || path.endsWith('/exception') && req.method() === 'PATCH') { directWrites++; return reply({}, 400); }
    if (path === `${base}/enrollments`) return reply(options.extra ? [baseline, additional] : [baseline]);
    if (path === `${base}/enrollments/${additional.id}`) return reply({ enrollment: additional, exceptions: [], contributions: [], links: [], deductions: [] });
    if (path === `${base}/employees/42/package`) return reply({ employeeId: 42, employeeName: 'Alex Morgan', companyId, gradeId: 'G5', asOf: '2026-10-09', enrollments: [{ ...baseline, planName: medical.name, planCode: medical.code, currency: 'SAR', effectiveStatus: 'Current', reviewRequired: false }, ...(options.extra ? [additional] : [])], additionalRequests: request ? [request] : [] });
    if (path.endsWith('/deduction-candidates')) return reply([]);
    if (path === '/api/employees') return reply({ items: [{ id: 42, employeeCode: 'TEST-042', fullName: 'Alex Morgan', status: 'Draft', companyId }], total: 1, page: 1, pageSize: 8 });
    if (path === `${base}/eligibility-check`) {
      const existing = url.searchParams.get('planId') === medical.id;
      return reply({ benefitPlanId: url.searchParams.get('planId'), currency: 'SAR', employeeId: 42, employeeName: 'Alex Morgan', companyId, gradeId: 'G5', effectiveFrom: url.searchParams.get('effectiveFrom'), eligible: false, alreadyEnrolled: existing || !!options.extra, blockingReason: 'Not a grade benefit.', maximumBenefitAmount: null, checks: ['plan_active', 'company_scope', 'plan_window'].map(key => ({ key, label: key, passed: true, detail: '' })).concat([{ key: 'eligibility_rules', label: 'Grade', passed: false, detail: 'Not a grade benefit.' }]) });
    }
    if (path === `${base}/enrollments/${additional.id}/end-request` && req.method() === 'POST') {
      endRequests.push(req.postDataJSON());
      return reply({ id: 'end-request', approvalRequestId: 'end-request', status: 'Pending', operation: 'End', endDate: req.postDataJSON().endDate, employeeId: 42, employeeName: 'Alex Morgan', benefitPlanId: education.id, planName: education.name, terms: { ...req.postDataJSON(), enrollmentId: additional.id } }, 201);
    }
    if (path === `${base}/additional-grants` && req.method() === 'POST') {
      submitted.push(req.postDataJSON());
      if (options.partialFailure && req.postDataJSON().benefitPlanId === transport.id && submitted.filter(item => item.benefitPlanId === transport.id).length === 1) return reply({ message: 'Limit needs correction.' }, 409);
      if (options.missingRoute) return reply({ code: 'approval_route_not_configured', message: 'No approval route is configured.', setupUrl: '/benefits#additional-benefit-approval' }, 422);
      request = { ...(options.policy ? { paymentPolicy: frozenPolicy, paymentPolicyVersion: 1 } : {}), id: 'request-1', approvalRequestId: 'request-1', status: 'Pending', employeeId: 42, employeeName: 'Alex Morgan', benefitPlanId: education.id, planName: education.name, currency: 'SAR', terms: req.postDataJSON(), baseline: options.extra ? additional : null, createdAtUtc: '2026-10-09T12:00:00Z', requestedByName: 'HR Officer', approval: { status: 'Pending', currentApproverName: 'Benefits Approver', currentApproverRole: 'HR Manager', canDecide: false, decisionBlockedReason: 'The requester cannot approve this request.', decisions: [] } };
      return reply(request, 201);
    }
    if (path === `${base}/additional-grants/request-1`) return reply(request);
    if (path === '/api/ess/benefits') return reply({ employeeId: 42, enrollments: [{ ...baseline, planName: medical.name, planType: 'Medical', currency: 'SAR', currentEmployeeAmount: null, currentEmployerAmount: null, deductions: [] }, { ...additional, planType: 'Education', currentEmployeeAmount: null, currentEmployerAmount: null, deductions: [], internalJustification: 'CONFIDENTIAL HR DECISION', exceptionReason: 'CONFIDENTIAL OLD EXCEPTION' }] });
    if (path.includes('/features/') || path === '/api/notifications') return reply([]);
    return reply({ items: [], total: 0, page: 1, pageSize: 100 });
  });
  if (!options.readonly) {
    await page.goto('/benefits');
    await expect(page.getByRole('heading', { name: 'Benefits Administration' })).toBeVisible();
    await expect(page).toHaveURL(/\/benefits$/);
  }
  return { submitted, endRequests, errors, directWrites: () => directWrites };
}

async function fillAdditional(page: Page) {
  await page.getByRole('button', { name: 'Manage benefits', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Manage benefits', exact: true });
  await dialog.getByLabel('Search employee', { exact: true }).fill('Alex');
  await dialog.getByRole('option', { name: /Alex Morgan/ }).click();
  await dialog.getByRole('checkbox', { name: education.name, exact: true }).check();
  await dialog.getByLabel('Limit (SAR)', { exact: false }).fill('15000');
  await dialog.getByLabel('Reason for changes', { exact: false }).fill('Approved retention package for a specialist.');
  return dialog;
}

test('HR manages benefits with inline terms, one reason and an independent approval', async ({ page }, info) => {
  const { submitted, errors, directWrites } = await boot(page);
  const dialog = await fillAdditional(page);
  await expect(dialog.getByRole('heading', { name: 'Available benefits', exact: true })).toBeVisible();
  await expect(dialog.getByRole('checkbox', { name: 'Keep Medical Gold', exact: true })).toBeChecked();
  await expect(dialog.getByRole('checkbox', { name: 'Keep Medical Gold', exact: true })).toBeDisabled();
  await expect(dialog.getByLabel('Entitlement tier', { exact: true })).not.toBeVisible();
  await expect(dialog.getByLabel('Reason shown to employee', { exact: true })).not.toBeVisible();
  await dialog.getByLabel('Review due', { exact: false }).fill('2027-01-01');
  expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  await page.screenshot({ path: `/tmp/manage-benefits-${info.project.name}.png` });
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog.getByRole('status')).toContainText('1 benefit requests sent');
  await expect(dialog.getByRole('checkbox', { name: education.name, exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'View request', exact: true }).click();
  const detail = page.getByRole('dialog', { name: 'Additional benefit request', exact: true });
  await expect(detail.getByTestId('additional-benefit-request-detail')).toContainText('Pending approval');
  await expect(detail).toContainText('Benefits Approver');
  await expect(detail).toContainText('requester cannot approve');
  expect(submitted).toHaveLength(1);
  expect(submitted[0]).toMatchObject({ employeeId: 42, benefitPlanId: education.id, entitlementTier: education.name, maximumBenefitAmount: 15000, reviewDate: '2027-01-01', effectiveTo: null, treatment: 'Reimbursement', reason: 'Individual benefit allocation.', internalJustification: 'Approved retention package for a specialist.' });
  expect(directWrites()).toBe(0);
  expect(errors).toEqual([]);
});

test('a fixed-term checklist row fills its plan end date', async ({ page }) => {
  await boot(page, { fixedTerm: true });
  const dialog = await fillAdditional(page);
  await expect(dialog.getByLabel('Ends', { exact: true })).toHaveValue('2027-12-31');
  await expect(dialog.getByLabel('Ends', { exact: true })).toHaveAttribute('max', '2027-12-31');
  await expect(dialog.getByLabel('Review due', { exact: false })).toHaveCount(0);
});

test('a missing approval route directs an HR officer to their administrator', async ({ page }) => {
  const { submitted, directWrites } = await boot(page, { missingRoute: true });
  const dialog = await fillAdditional(page);
  await dialog.getByLabel('Review due', { exact: false }).fill('2027-01-01');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toContainText('Contact an administrator');
  await expect(dialog.getByRole('link', { name: 'Configure approval workflow' })).toHaveCount(0);
  expect(submitted).toHaveLength(1);
  expect(directWrites()).toBe(0);
});

test('nested approval setup traps focus and Escape preserves the underlying proposal', async ({ page }) => {
  const { errors, directWrites } = await boot(page, { missingRoute: true, manage: true });
  const proposal = await fillAdditional(page);
  await proposal.getByLabel('Review due', { exact: false }).fill('2027-01-01');
  await proposal.getByRole('button', { name: 'Save changes', exact: true }).click();
  const configure = proposal.getByRole('link', { name: 'Configure approval workflow', exact: true });
  await configure.click();
  const setup = page.getByRole('dialog', { name: 'Additional benefit approvals', exact: true });
  const save = setup.getByRole('button', { name: 'Save approval route', exact: true });
  await expect(save).toBeEnabled();
  await expect(setup.getByRole('button', { name: 'Close', exact: true }).first()).toBeFocused();
  await save.focus();
  await page.keyboard.press('Tab');
  await expect(setup.getByRole('button', { name: 'Close', exact: true }).first()).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await expect(save).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(setup).toHaveCount(0);
  await expect(proposal).toBeVisible();
  await expect(proposal.getByLabel('Reason for changes', { exact: false })).toHaveValue('Approved retention package for a specialist.');
  await expect(proposal.getByRole('button', { name: 'Close', exact: true }).first()).toBeFocused();
  await expect(page).toHaveURL(/\/benefits$/);
  await configure.click();
  await expect(save).toBeEnabled();
  await page.keyboard.press('Escape');
  await expect(setup).toHaveCount(0);
  await expect(proposal).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(proposal).toHaveCount(0);
  expect(directWrites()).toBe(0);
  expect(errors).toEqual([]);
});

test('an existing additional benefit can only be amended through approval', async ({ page }) => {
  const { submitted, directWrites } = await boot(page, { extra: true });
  await page.getByRole('tab', { name: 'Enrolments (2)' }).click();
  await page.getByRole('row').filter({ hasText: 'Education Allowance' }).click();
  const drawer = page.getByRole('dialog', { name: 'Enrolment detail', exact: true });
  await expect(drawer.getByRole('button', { name: 'Adjust existing benefit' })).toHaveCount(0);
  await drawer.getByRole('button', { name: 'Amend additional benefit', exact: true }).click();
  const form = page.getByRole('dialog', { name: 'Amend additional benefit', exact: true });
  await expect(form.getByLabel('Benefit plan', { exact: true })).toBeDisabled();
  await form.getByLabel('Individual benefit limit (SAR)', { exact: true }).fill('18000');
  await form.getByLabel('Internal justification', { exact: false }).fill('Renewed professional development support.');
  await form.getByRole('button', { name: 'Review request', exact: true }).click();
  await expect(form).toContainText('Current additional benefit');
  await form.getByRole('button', { name: 'Submit for approval', exact: true }).click();
  await expect(form).toHaveCount(0);
  expect(submitted[0]).toMatchObject({ enrollmentId: additional.id, expectedUpdatedAtUtc: additional.updatedAtUtc, maximumBenefitAmount: 18000 });
  expect(directWrites()).toBe(0);
});

test('ESS shows additional coverage and public terms while hiding internal reasons', async ({ page }) => {
  await boot(page);
  await page.goto('/ess/benefits');
  await expect(page.getByTestId('my-benefits-list')).toContainText('Medical Gold');
  await expect(page.getByTestId('my-benefits-list')).toContainText('Additional benefit');
  await expect(page.getByTestId('my-benefits-list')).toContainText('Professional development support.');
  await expect(page.getByTestId('my-benefits-list')).toContainText('Pending HR review');
  await expect(page.getByTestId('my-benefits-list')).not.toContainText('CONFIDENTIAL');
});

async function mockEmployeeProfile(page: Page) {
  const employee = {
    id: 42, employeeCode: 'TEST-042', fullName: 'Alex Morgan', englishName: 'Alex Morgan', arabicName: '',
    companyId, gradeId: 'G5', status: 'Draft', profileCompletenessScore: 25, readinessState: 'NeedsAttention',
    activationBlockersCount: 1, accessState: 'waiting_for_work_email', workEmail: null,
    documents: [], history: [], complianceRecords: [], flags: [],
  };
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    const reply = (json: unknown) => route.fulfill({ json });
    if (path === '/api/employees') return reply({ items: [employee], total: 1, page: 1, pageSize: 100 });
    if (path === '/api/employees/42') return reply(employee);
    if (path.endsWith('/42/readiness')) return reply({ employeeId: 42, state: 'NeedsAttention', score: 25, progress: { present: 1, requiredTotal: 4 }, policy: { countryCode: 'SA', tier: 'Standard', sources: [] }, blocking: [], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: 'Synthetic browser fixture.' });
    if (path === '/api/employee-completion/42') return reply({ requested: false, status: 'NotRequested', selfServiceAvailable: false });
    if (path === '/api/employee-access/summary') return reply({ waiting_for_work_email: 1 });
    if (path === '/api/employee-access/42') return reply({ employeeId: 42, employeeName: employee.fullName, employeeCode: employee.employeeCode, workEmail: null, state: 'waiting_for_work_email', canIssue: false, codeExpiresAtUtc: null, codeIssuedByName: null, lastCodeExpiredAtUtc: null, lastSignInAtUtc: null, stoppedReason: null, blockedCode: null, blockedReason: null, reasonCode: null, emailDelivery: false });
    return route.fallback();
  });
}

test('superseded draft defaults remain read-only history in HR and ESS', async ({ page }) => {
  const { errors, directWrites } = await boot(page, { manage: true });
  await mockEmployeeProfile(page);
  const current = { ...baseline, planName: medical.name, planCode: medical.code, planType: medical.planType, currency: 'SAR', effectiveStatus: 'Current', reviewRequired: false, currentEmployeeAmount: null, currentEmployerAmount: null, deductions: [] };
  const replaced = { ...current, id: 'replaced-default', planName: 'Previous grade medical', entitlementTier: 'Silver', maximumBenefitAmount: 5000, status: 'Superseded', effectiveStatus: 'Superseded' };
  await page.route(`**${base}/employees/42/package`, route => route.fulfill({ json: { employeeId: 42, employeeName: 'Alex Morgan', companyId, gradeId: 'G5', asOf: '2026-10-09', enrollments: [current, replaced], additionalRequests: [] } }));
  await page.route(`**${base}/enrollments/${replaced.id}`, route => route.fulfill({ json: { enrollment: replaced, exceptions: [], contributions: [], links: [], deductions: [] } }));
  await page.route('**/api/ess/benefits', route => route.fulfill({ json: { employeeId: 42, enrollments: [current, replaced] } }));

  await page.goto('/people?employeeId=42&tab=benefits');
  const panel = page.getByTestId('employee-benefits-panel');
  await expect(panel.getByText(medical.name, { exact: true })).toBeVisible();
  await expect(panel.getByText(replaced.planName, { exact: true })).toBeHidden();
  await panel.getByText('Past benefits (1)', { exact: true }).click();
  const history = panel.locator('li').filter({ has: page.getByText(replaced.planName, { exact: true }) });
  await expect(history.getByText('Replaced', { exact: true })).toBeVisible();
  await expect(history.getByRole('button', { name: 'Adjust existing benefit', exact: true })).toHaveCount(0);
  await history.getByRole('button', { name: 'View benefit', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: 'Enrolment detail', exact: true });
  await expect(drawer.getByText('Replaced before activation after the employee’s draft details changed.', { exact: true })).toBeVisible();
  await expect(drawer.getByRole('button', { name: 'Adjust existing benefit', exact: true })).toHaveCount(0);
  await expect(drawer.getByRole('form', { name: 'Apply benefit exception' })).toHaveCount(0);
  await expect(drawer.getByRole('form', { name: 'Record contribution' })).toHaveCount(0);
  await expect(drawer.getByRole('form', { name: 'Link payroll deduction' })).toHaveCount(0);

  await page.goto('/ess/benefits');
  const benefits = page.getByTestId('my-benefits-list');
  await expect(benefits.getByText(medical.name, { exact: true })).toBeVisible();
  await expect(benefits.getByText(replaced.planName, { exact: true })).toHaveCount(0);
  await benefits.getByRole('button', { name: 'Past benefits (1)', exact: true }).click();
  const oldBenefit = benefits.getByRole('article').filter({ has: page.getByRole('heading', { name: replaced.planName, exact: true }) });
  await expect(oldBenefit.getByText('Replaced', { exact: true })).toBeVisible();
  await expect(oldBenefit).not.toContainText('Covered from');
  await expect(oldBenefit).toContainText('Replaced before activation');
  expect(directWrites()).toBe(0);
  expect(errors).toEqual([]);
});

test('an employee Benefits URL opens the populated panel before activation cards and the mobile list', async ({ page }, info) => {
  const { errors } = await boot(page, { extra: true });
  await mockEmployeeProfile(page);
  await page.goto('/people?employeeId=42&tab=benefits');
  await expect(page).toHaveURL(/\/people\?employeeId=42&tab=benefits$/);
  const panel = page.getByTestId('employee-benefits-panel');
  const profile = page.locator('aside').filter({ has: panel });
  const table = page.getByRole('table').filter({ has: page.getByRole('button', { name: 'Open profile for Alex Morgan', exact: true }) });
  await expect(panel.getByText('Medical Gold', { exact: true })).toBeVisible();
  await expect(panel.getByText('Education Allowance', { exact: true })).toBeVisible();
  await expect(profile.getByRole('button', { name: 'Benefits', exact: true })).toBeInViewport({ ratio: 1 });
  await expect(panel.getByRole('heading', { name: 'Benefits', exact: true })).toBeInViewport({ ratio: 1 });
  await expect(panel.getByText('Medical Gold', { exact: true })).toBeInViewport();
  await expect(profile.getByText('Activation checklist', { exact: true })).toHaveCount(0);
  await expect(profile.getByTestId('employee-access-card')).toHaveCount(0);
  expect(await panel.evaluate(node => node.scrollWidth <= node.clientWidth + 1)).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  if (info.project.name === 'phone') {
    const panelBounds = await panel.boundingBox(), tableBounds = await table.boundingBox();
    expect(panelBounds).not.toBeNull(); expect(tableBounds).not.toBeNull();
    expect(panelBounds!.y + panelBounds!.height).toBeLessThan(tableBounds!.y);
  }
  const screenshot = info.outputPath(`employee-benefits-url-${info.project.name}.png`);
  await page.screenshot({ path: screenshot, animations: 'disabled', style: 'nextjs-portal { display: none !important; }' });
  await info.attach('employee-benefits-url', { path: screenshot, contentType: 'image/png' });
  await profile.getByRole('button', { name: 'Personal Information', exact: true }).click();
  await expect(panel).toHaveCount(0);
  await expect(page.getByText('Activation checklist', { exact: true })).toBeVisible();
  await expect(page.getByTestId('employee-access-card')).toBeVisible();
  expect(errors).toEqual([]);
});

test('an unavailable employee Benefits URL keeps Personal and never loads the benefit package', async ({ page }) => {
  const { errors } = await boot(page, { readonly: true });
  await mockEmployeeProfile(page);
  let packageReads = 0;
  await page.route(`**${base}/employees/42/package`, async route => {
    packageReads++;
    return route.fulfill({ status: 403, json: { message: 'Forbidden' } });
  });
  await page.goto('/people?employeeId=42&tab=benefits');
  await expect(page.getByText('Activation checklist', { exact: true })).toBeVisible();
  await expect(page.getByTestId('employee-access-card')).toBeVisible();
  const profile = page.locator('aside').filter({ has: page.getByRole('button', { name: 'Close employee profile', exact: true }) });
  await expect(profile.getByRole('button', { name: 'Personal Information', exact: true })).toHaveClass(/bg-sapphire/);
  await expect(profile.getByRole('button', { name: 'Benefits', exact: true })).toHaveCount(0);
  await expect(page.getByTestId('employee-benefits-panel')).toHaveCount(0);
  expect(packageReads).toBe(0);
  expect(errors).toEqual([]);
});

test('multiple benefit changes retain partial success and retry only failed rows', async ({ page }) => {
  const { submitted, errors } = await boot(page, { partialFailure: true });
  const dialog = await fillAdditional(page);
  await dialog.getByRole('region', { name: education.name, exact: true }).getByLabel('Ends', { exact: true }).fill('2027-01-01');
  await dialog.getByRole('checkbox', { name: transport.name, exact: true }).check();
  const transportRow = dialog.getByRole('region', { name: transport.name, exact: true });
  await transportRow.getByLabel('Ends', { exact: true }).fill('2027-01-01');
  await transportRow.getByLabel('Limit (SAR)', { exact: false }).fill('3000');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog.getByRole('status')).toContainText('1 benefit requests sent');
  await expect(transportRow).toContainText('Limit needs correction.');
  await expect(dialog.getByRole('checkbox', { name: education.name, exact: true })).toBeDisabled();
  await expect(transportRow.getByLabel('Limit (SAR)', { exact: false })).toBeEnabled();
  await transportRow.getByLabel('Limit (SAR)', { exact: false }).fill('2500');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog.getByRole('status')).toContainText('2 benefit requests sent');
  expect(submitted.map(item => item.benefitPlanId)).toEqual([education.id, transport.id, transport.id]);
  expect(submitted[2].maximumBenefitAmount).toBe(2500);
  expect(errors).toEqual([]);
});

test('unchecking an additional benefit requests its end while grade defaults remain assigned', async ({ page }) => {
  const { endRequests, directWrites, errors } = await boot(page, { extra: true });
  await page.getByRole('button', { name: 'Manage benefits', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Manage benefits', exact: true });
  await dialog.getByLabel('Search employee', { exact: true }).fill('Alex');
  await dialog.getByRole('option', { name: /Alex Morgan/ }).click();
  await expect(dialog.getByRole('checkbox', { name: 'Keep Medical Gold', exact: true })).toBeDisabled();
  await dialog.getByRole('checkbox', { name: 'Keep Education Allowance', exact: true }).uncheck();
  await dialog.getByLabel('Last covered day', { exact: true }).fill('2026-12-31');
  await dialog.getByLabel('Reason for changes', { exact: false }).fill('Education allocation ends at year-end.');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog).toContainText('Removal pending approval. Coverage remains unchanged until approved.');
  await expect(dialog.getByRole('checkbox', { name: 'Keep Education Allowance', exact: true })).toBeChecked();
  await expect(dialog.getByRole('checkbox', { name: 'Keep Education Allowance', exact: true })).toBeDisabled();
  expect(endRequests).toEqual([{ endDate: '2026-12-31', reason: 'Individual benefit allocation.', internalJustification: 'Education allocation ends at year-end.', expectedUpdatedAtUtc: additional.updatedAtUtc }]);
  expect(directWrites()).toBe(0);
  expect(errors).toEqual([]);
});

test('Manage benefits keeps assigned additional edits and grade adjustments in context', async ({ page }) => {
  const { errors } = await boot(page, { extra: true, manage: true });
  await page.getByRole('button', { name: 'Manage benefits', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Manage benefits', exact: true });
  await dialog.getByLabel('Search employee', { exact: true }).fill('Alex');
  await dialog.getByRole('option', { name: /Alex Morgan/ }).click();
  const edit = dialog.getByRole('button', { name: 'Edit terms', exact: true });
  await edit.click();
  await expect(page.getByRole('dialog', { name: 'Amend additional benefit', exact: true }).getByLabel('Individual benefit limit (SAR)', { exact: true })).toHaveValue('15000');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  await expect(edit).toBeFocused();
  const adjust = dialog.getByRole('button', { name: 'Adjust existing benefit', exact: true });
  await adjust.click();
  await expect(page.getByRole('dialog', { name: 'Adjust existing benefit', exact: true }).getByRole('form', { name: 'Apply benefit exception' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  await expect(adjust).toBeFocused();
  expect(errors).toEqual([]);
});


test('benefit request review displays its frozen salary policy instead of the live plan amount', async ({ page }) => {
  await boot(page, { policy: true });
  const dialog = await fillAdditional(page);
  await dialog.getByLabel('Review due', { exact: false }).fill('2027-01-01');
  await dialog.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(dialog.getByRole('status')).toContainText('1 benefit requests sent');
  await dialog.getByRole('button', { name: 'View request', exact: true }).click();
  const request = page.getByRole('dialog', { name: 'Additional benefit request', exact: true });
  await expect(request.getByTestId('benefit-payment-summary')).toContainText('Add to salary');
  await expect(request.getByTestId('benefit-payment-summary')).toContainText('750.00');
  await expect(request.getByTestId('benefit-payment-summary')).not.toContainText('950.00');
  await expect(request).not.toContainText('Payments, contributions and payroll deductions must be recorded separately.');
});

test('benefit catalogue finds custom plans by name, code and category', async ({ page }) => {
  await boot(page);
  const search = page.getByLabel('Find a benefit plan', { exact: false });
  await search.fill('medical'); await expect(page.getByText('Medical Gold', { exact: true })).toBeVisible(); await expect(page.getByText('Education Allowance', { exact: true })).toHaveCount(0);
  await search.fill('TRN'); await expect(page.getByText('Transport Allowance', { exact: true })).toBeVisible(); await expect(page.getByText('Medical Gold', { exact: true })).toHaveCount(0);
  await search.fill('Education'); await expect(page.getByText('Education Allowance', { exact: true })).toBeVisible();
  await search.fill('unmatched'); await expect(page.getByRole('status')).toHaveText('No benefit plans match your search.');
  await search.fill(''); await expect(page.getByText('Medical Gold', { exact: true })).toBeVisible();
});
