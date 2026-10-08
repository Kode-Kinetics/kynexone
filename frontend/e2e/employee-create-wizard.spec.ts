import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import type { EmployeeCreateRequest } from '../src/api/employees';

const company = {
  id: 'company-wizard', legalNameEn: 'Wizard Test Company', legalNameAr: '', tradeName: 'Wizard Test Company',
  countryCode: 'SA', jurisdiction: 'SA', defaultCurrency: 'SAR', emailDomain: 'example.test',
  workEmailPattern: 'first.last', isActive: true, approvalStatus: 'Active',
};
const grade = { id: 'grade-wizard', code: 'G5', name: 'Professional', band: 'P', level: 5, minSalary: 5000, midSalary: 10000, maxSalary: 15000, currency: 'SAR', isActive: true };
const manager = { id: 17, employeeCode: 'TEST-017', fullName: 'Morgan Reviewer', englishName: 'Morgan Reviewer', status: 'Active', companyId: company.id };
const duplicate = { employeeId: 44, employeeCode: 'TEST-044', fullName: 'Alex Morgan', status: 'Draft', matchType: 'probable', signals: ['Name and date of birth match'], canView: true };
const headings = ['Start with the person', 'Place them in the organization', 'Set up payroll', 'Build the salary package', 'Add identity documents', 'Review employee details'];
const stepNames = ['Profile', 'Employment', 'Payroll', 'Salary', 'Identity', 'Review'];

interface MockOptions { missingCountry?: boolean; failFirstCreate?: boolean; duplicate?: boolean }

async function boot(page: Page, options: MockOptions = {}) {
  const creates: EmployeeCreateRequest[] = [];
  const probes: unknown[] = [];
  const errors: string[] = [];
  const testCompany = { ...company, countryCode: options.missingCountry ? '' : company.countryCode };
  let saved: Record<string, unknown> | undefined;
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => localStorage.setItem('zayra_access_token', 'wizard-browser-fixture'));
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const body = request.postData() ? request.postDataJSON() : undefined;
    const reply = (json: unknown, status = 200) => route.fulfill({ status, json });
    const paged = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 100 });
    if (path === '/api/auth/me') return reply({
      id: 'wizard-reviewer', tenantId: 'wizard-tenant', tenantSlug: 'wizard-fixture', fullName: 'HR Reviewer',
      roles: ['HR Manager'], permissions: ['employees.read', 'employees.write', 'employees.approve'],
      companies: [{ id: company.id, name: company.legalNameEn, code: 'TEST', countryCode: testCompany.countryCode, isActive: true }],
    });
    if (path === '/api/tenant-admin/localization') return reply({ currencyCode: 'SAR', countryCode: 'SA', defaultTimezone: 'Asia/Riyadh' });
    if (path === '/api/companies') return reply(paged([testCompany]));
    if (path === '/api/branches') return reply(paged([{ id: 'branch-wizard', companyId: company.id, code: 'RYD', nameEn: 'Riyadh', isActive: true }]));
    if (path === '/api/departments') return reply(paged([{ id: 'department-wizard', branchId: 'branch-wizard', code: 'OPS', nameEn: 'Operations', managerEmployeeId: manager.id, isActive: true }]));
    if (path === '/api/designations') return reply(paged([{ id: 'designation-wizard', departmentId: 'department-wizard', code: 'SPEC', titleEn: 'Specialist', gradeId: grade.id, isActive: true }]));
    if (path === '/api/grades') return reply(paged([grade, { ...grade, id: 'grade-other', code: 'G6', name: 'Senior' }]));
    if (path.endsWith('/pay-scale')) return reply([]);
    if (path === '/api/organization/cost-centers') return reply(paged([{ id: 'cost-wizard', companyId: company.id, code: 'OPS', nameEn: 'Operations cost center', isActive: true }]));
    if (path === '/api/employees/field-catalog') return reply({ countryCode: testCompany.countryCode, fields: [] });
    if (path === '/api/employees/derive-work-email') {
      const localPart = body.localPart ?? body.englishName.trim().toLowerCase().split(/\s+/).join('.');
      return reply({ domain: company.emailDomain, pattern: company.workEmailPattern, localPart, workEmail: `${localPart}@${company.emailDomain}`, unique: true, suggestion: localPart, status: 'derived' });
    }
    if (path === '/api/employees/duplicate-check') {
      probes.push(body);
      return reply({ hasStrong: false, hasProbable: !!options.duplicate, matches: options.duplicate ? [duplicate] : [] });
    }
    if (path === '/api/employees' && request.method() === 'POST') {
      creates.push(body);
      if (options.failFirstCreate && creates.length === 1) return reply({ message: 'Temporary save failure. Please try again.' }, 503);
      saved = { ...body, id: 901, employeeCode: body.employeeCode || 'TEST-901', fullName: body.englishName, status: 'Draft', accessState: body.workEmail ? 'not_started' : 'waiting_for_work_email', profileCompletenessScore: 25, readinessState: 'NeedsAttention', activationBlockersCount: 1, documents: [], history: [], complianceRecords: body.complianceRecords ?? [] };
      return reply(saved, 201);
    }
    if (path === '/api/employees') return reply(paged(url.searchParams.get('status') === 'Active' ? [manager] : saved ? [saved] : []));
    if (path === '/api/employees/901') return reply(saved);
    if (path === '/api/employee-access/summary') return reply(saved ? { [saved.workEmail ? 'not_started' : 'waiting_for_work_email']: 1 } : {});
    if (path === '/api/employee-access/901') return reply({
      employeeId: 901, employeeName: saved?.fullName, employeeCode: 'TEST-901', workEmail: saved?.workEmail ?? null,
      state: saved?.workEmail ? 'not_started' : 'waiting_for_work_email', canIssue: !!saved?.workEmail,
      codeExpiresAtUtc: null, codeIssuedByName: null, lastCodeExpiredAtUtc: null, lastSignInAtUtc: null,
      stoppedReason: null, blockedCode: null, blockedReason: null, reasonCode: null, emailDelivery: false,
    });
    if (path.endsWith('/readiness')) return reply({ employeeId: 901, state: 'NeedsAttention', score: 25, progress: { present: 1, requiredTotal: 4 }, policy: { countryCode: 'SA', tier: 'Standard', sources: [] }, blocking: [], payBlocking: [], recommended: [], present: [], expiringSoon: [], disclaimer: 'Synthetic browser fixture.' });
    if (path.includes('/features/') || path === '/api/notifications' || path.endsWith('/documents') || path.endsWith('/history')) return reply([]);
    // Even unrelated shell requests stay local to this synthetic session.
    return reply(paged([]));
  });
  await page.goto('/people');
  await expect(page.getByRole('button', { name: 'Add Employee', exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Add Employee', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Add Employee', exact: true });
  await expect(dialog.getByRole('heading', { name: headings[0], exact: true })).toBeVisible();
  return { dialog, creates, probes, errors };
}

async function next(dialog: Locator, index: number) {
  await dialog.getByRole('button', { name: `Next: ${stepNames[index]}`, exact: true }).click();
  await expect(dialog.getByRole('heading', { name: headings[index], exact: true })).toBeVisible();
}

async function toReview(dialog: Locator, start = 1) {
  for (let index = start; index < headings.length; index++) await next(dialog, index);
}

async function finishCreated(dialog: Locator) {
  await expect(dialog.getByTestId('employee-added')).toContainText('Alex Morgan has been added.');
  await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Later', exact: true }).click();
  await expect(dialog).toHaveCount(0);
}

async function evidence(page: Page, info: TestInfo, name: string) {
  const path = info.outputPath(`${name}-${info.project.name}.png`);
  await page.screenshot({ path, fullPage: false, animations: 'disabled' });
  await info.attach(name, { path, contentType: 'image/png' });
}

async function expectContained(dialog: Locator, page: Page) {
  const bounds = await dialog.boundingBox();
  const viewport = page.viewportSize()!;
  expect(bounds).not.toBeNull();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width + 1);
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height + 1);
  expect(await dialog.evaluate(node => node.scrollWidth <= node.clientWidth + 1)).toBe(true);
}

test('guides keyboard navigation, requires a name, and keeps creation until review', async ({ page }, info) => {
  const { dialog, creates, probes, errors } = await boot(page);
  await expectContained(dialog, page);
  await evidence(page, info, 'profile');
  const advance = dialog.getByRole('button', { name: 'Next: Employment', exact: true });
  await advance.focus();
  await page.keyboard.press('Enter');
  await expect(dialog.getByText('Enter the employee’s English full name to continue.', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('heading', { name: headings[0], exact: true })).toBeVisible();
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toHaveCount(0);
  await advance.focus();
  await page.keyboard.press('Enter');
  await expect(dialog.getByRole('heading', { name: headings[1], exact: true })).toBeFocused();
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('');
  await expectContained(dialog, page);
  await evidence(page, info, 'employment');
  for (let index = 2; index < headings.length; index++) {
    await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toHaveCount(0);
    await next(dialog, index);
    await expectContained(dialog, page);
    const nextAction = dialog.getByRole('button', { name: index === 5 ? 'Create Employee' : `Next: ${stepNames[index + 1]}`, exact: true });
    await expect(nextAction).toBeInViewport();
  }
  await expect(dialog.getByRole('button', { name: 'Create Employee', exact: true })).toBeEnabled();
  expect(creates).toHaveLength(0);
  expect(probes).toHaveLength(0);
  await evidence(page, info, 'review');
  // Keyboard focus remains trapped in the dialog at its last action.
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Close', exact: true })).toBeFocused();
  if (info.project.name.includes('reduced-motion')) {
    expect(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(true);
    expect(await dialog.evaluate(node => node.getAnimations({ subtree: true }).filter(animation => animation.playState === 'running').length)).toBe(0);
  }
  expect(errors).toEqual([]);
});

test('retains entered values across Back and review edits and submits the existing payload', async ({ page }, info) => {
  const { dialog, creates, errors } = await boot(page);
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await dialog.getByLabel('Preferred name', { exact: true }).fill('Alex');
  await dialog.getByLabel('Personal email', { exact: true }).fill('alex.personal@example.test');
  await next(dialog, 1);
  await dialog.getByRole('textbox', { name: 'Work email', exact: true }).fill('alex.it-issued');
  await dialog.getByRole('button', { name: 'Back', exact: true }).click();
  await expect(dialog.getByLabel('Preferred name', { exact: true })).toHaveValue('Alex');
  await next(dialog, 1);
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('alex.it-issued');
  await dialog.getByRole('combobox', { name: 'Branch', exact: true }).selectOption('branch-wizard');
  await dialog.getByRole('combobox', { name: 'Department', exact: true }).selectOption('department-wizard');
  await expect(dialog.getByRole('combobox', { name: 'Line manager', exact: true })).toHaveValue('17');
  await dialog.getByRole('combobox', { name: 'Designation', exact: true }).selectOption('designation-wizard');
  await expect(dialog.getByRole('combobox', { name: 'Grade', exact: true })).toHaveValue(grade.id);
  await expect(dialog.getByRole('combobox', { name: 'Grade', exact: true }).locator('option')).toHaveCount(2);
  await dialog.getByLabel('Job title', { exact: true }).fill('Operations specialist');
  await dialog.getByLabel('Joining date', { exact: false }).fill('2026-10-15');
  await next(dialog, 2);
  await dialog.getByLabel('Bank name', { exact: true }).fill('Test Bank');
  await dialog.getByRole('textbox', { name: /^IBAN/ }).fill('SA0380000000608010167519');
  await dialog.getByLabel('Account number', { exact: true }).fill('608010167519');
  await next(dialog, 3);
  await dialog.getByLabel('Basic salary', { exact: true }).fill('7000');
  await dialog.getByLabel('Housing allowance', { exact: true }).fill('2000');
  await dialog.getByLabel('Fixed deduction', { exact: true }).fill('100');
  await dialog.getByLabel('Effective date', { exact: true }).fill('2026-10-15');
  await evidence(page, info, 'salary');
  await next(dialog, 4);
  await dialog.getByLabel(/^Passport number/i).fill('TEST-PASSPORT-001');
  await next(dialog, 5);
  await dialog.getByRole('button', { name: 'Edit Profile', exact: true }).click();
  await dialog.getByLabel('Preferred name', { exact: true }).fill('Alex M');
  await next(dialog, 1);
  await expect(dialog.getByRole('textbox', { name: 'Work email', exact: true })).toHaveValue('alex.it-issued');
  await toReview(dialog, 2);
  expect(creates).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await finishCreated(dialog);
  expect(creates).toHaveLength(1);
  expect(creates[0]).toMatchObject({
    englishName: 'Alex Morgan', preferredName: 'Alex M', manualEmployeeCode: false,
    personalEmail: 'alex.personal@example.test', workEmail: 'alex.it-issued@example.test', companyId: company.id,
    branchId: 'branch-wizard', departmentId: 'department-wizard', designationId: 'designation-wizard', gradeId: grade.id,
    reportingManagerEmployeeId: 17, jobTitle: 'Operations specialist', joiningDate: '2026-10-15',
    payrollProfile: { bankName: 'Test Bank', iban: 'SA0380000000608010167519', accountNumber: '608010167519', paymentMethod: 'BankTransfer', salaryCurrency: 'SAR', wpsEligible: true, eosbEligible: true },
    salaryBreakdown: { basicSalary: 7000, housingAllowance: 2000, fixedDeduction: 100, effectiveDate: '2026-10-15', currency: 'SAR' },
  });
  expect(creates[0].complianceRecords).toEqual(expect.arrayContaining([expect.objectContaining({ fieldKey: 'passport_number', fieldValue: 'TEST-PASSPORT-001' })]));
  expect(creates[0]).not.toHaveProperty('costCenterId');
  expect(creates[0]).not.toHaveProperty('acknowledgeDuplicate');
  expect(errors).toEqual([]);
});

test('preserves the reviewed draft after a server failure and retries once', async ({ page }) => {
  const { dialog, creates, errors } = await boot(page, { failFirstCreate: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await next(dialog, 1);
  await dialog.getByRole('textbox', { name: 'Work email', exact: true }).fill('alex.it-issued');
  await toReview(dialog, 2);
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await expect(dialog.getByText(/Temporary save failure/)).toBeVisible();
  await expect(dialog.getByRole('heading', { name: headings[5], exact: true })).toBeVisible();
  expect(creates).toHaveLength(1);
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await finishCreated(dialog);
  expect(creates).toHaveLength(2);
  expect(creates[1]).toEqual(creates[0]);
  expect(errors).toEqual([]);
});

test('requires an explicit duplicate decision before sending the create request', async ({ page }, info) => {
  const { dialog, creates, probes, errors } = await boot(page, { duplicate: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await toReview(dialog);
  await dialog.getByRole('button', { name: 'Create Employee', exact: true }).click();
  await expect(dialog.getByRole('alert').filter({ hasText: 'Possible existing employee' })).toBeVisible();
  expect(creates).toHaveLength(0);
  expect(probes).toHaveLength(1);
  await evidence(page, info, 'duplicate');
  await dialog.getByRole('button', { name: 'Create anyway', exact: true }).click();
  await expect(dialog.getByTestId('employee-added')).toContainText('Add a work email so Alex Morgan can sign in.');
  await finishCreated(dialog);
  expect(creates).toHaveLength(1);
  expect(creates[0].acknowledgeDuplicate).toBe(true);
  expect(creates[0]).not.toHaveProperty('workEmail');
  expect(probes).toHaveLength(1);
  expect(errors).toEqual([]);
});

test('keeps the company-country gate and its actionable setup link', async ({ page }) => {
  const { dialog, creates } = await boot(page, { missingCountry: true });
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await next(dialog, 1);
  await expect(dialog.getByRole('alert')).toContainText('has no country set');
  await expect(dialog.getByRole('link', { name: 'Setup → Companies', exact: true })).toHaveAttribute('href', '/setup?tab=companies');
  const advance = dialog.getByRole('button', { name: 'Next: Payroll', exact: true });
  if (await advance.isEnabled()) await advance.click();
  await expect(dialog.getByRole('heading', { name: headings[1], exact: true })).toBeVisible();
  expect(creates).toHaveLength(0);
});

test('keeps salary-band validation on the salary step and allows correction', async ({ page }) => {
  const { dialog, creates, errors } = await boot(page);
  await dialog.getByLabel('English full name', { exact: false }).fill('Alex Morgan');
  await next(dialog, 1);
  await dialog.getByRole('combobox', { name: 'Grade', exact: true }).selectOption(grade.id);
  await next(dialog, 2);
  await next(dialog, 3);
  await dialog.getByLabel('Basic salary', { exact: true }).fill('20000');
  await dialog.getByRole('button', { name: 'Next: Identity', exact: true }).click();
  await expect(dialog.getByText(/Salary package must be within/)).toBeVisible();
  await expect(dialog.getByRole('heading', { name: headings[3], exact: true })).toBeVisible();
  await dialog.getByLabel('Basic salary', { exact: true }).fill('10000');
  await next(dialog, 4);
  await next(dialog, 5);
  expect(creates).toHaveLength(0);
  expect(errors).toEqual([]);
});
